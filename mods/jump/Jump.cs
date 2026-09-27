// ModCompiler compiles mods with no implicit usings, so every namespace the
// file needs must be named here -- including System.
using System;
using ImGuiNET;
using RecompOne.Runtime.Context;
using RecompOne.Runtime.Host;
using RecompOne.Runtime.Memory;
using RecompOne.Runtime.Modding;
using Silk.NET.Input;

namespace Kf2.Mods.Jump;

/// <summary>
/// A jump, built out of the game's own fall.
///
/// `func_80028560` is the player's vertical motion, called from stage 3 every
/// frame, and it is a state machine on the byte at `0x801994E4` with the fall
/// velocity at `0x8019954E` (s16; Y grows downward, so negative is up):
///
///     0x00  on the ground: follow the floor, step up/down, or start a fall
///     0x10  short fall     0x20  ledge hop (a rise onto a higher floor)
///     0x40  long fall      0x50  the landing dip
///
/// The 0x40 arm is already a jump that nobody starts upward. Each tick it moves Y
/// by the velocity, adds 40 of gravity, and tests the new position with
/// `func_8002C700`. On a hit while rising it zeroes the velocity -- a bump on the
/// ceiling, not the crush check -- and on a hit while falling it lands: fall
/// damage only when the velocity is 480 or more, then the 0x50 dip back to 0.
///
/// So a jump is two writes before the function runs, on a frame the player is on
/// the ground: state 0x40 and a negative velocity. A launch below 480 lands
/// below 480 on flat ground, so it costs no HP; jumping down off a ledge still
/// hurts as much as walking off it would. Nothing else reads the state byte, so
/// walking, turning and attacking carry on in the air.
///
/// The rise can be slowed ("Rise speed"): launch slower and replace the 40 of
/// gravity with 40 * s^2 until the peak, which keeps the height. The fall stays
/// the game's own, so the landing speed and its damage do not change.
/// </summary>
public sealed class JumpMod : IMod
{
    const uint FallState = 0x801994E4;   // u8
    const uint FallVel   = 0x8019954E;   // s16, negative is up
    const byte OnGround  = 0x00;
    const byte LongFall  = 0x40;
    const uint Action    = 0x801994E1;   // u8, player action state
    const byte Dead      = 0x11;
    const uint Hp        = 0x80199428;   // u16

    const string KeyKey      = "kf2.jump.key";
    const string PadKey      = "kf2.jump.pad";
    const string StrengthKey = "kf2.jump.strength";
    const string RiseKey     = "kf2.jump.rise";

    static readonly (string Name, Key Key)[] Keys =
    [
        ("Space", Key.Space), ("E", Key.E), ("R", Key.R), ("C", Key.C), ("V", Key.V), ("X", Key.X), ("Z", Key.Z),
        ("Left Ctrl", Key.ControlLeft), ("Left Alt", Key.AltLeft), ("Caps Lock", Key.CapsLock),
        ("None", Key.Unknown),
    ];

    // SDL GameControllerButton indices, the encoding HostWindow.IsPadButtonDown takes.
    static readonly (string Name, int Button)[] PadButtons =
    [
        ("None", -1),
        ("R3 (right stick click)", 8),
        ("L3 (left stick click)", 7),
        ("Misc (share / mute)", 15),
    ];

    static int _keyIndex;
    static int _padIndex;
    static int _strength = 373;
    static int _risePercent = 80;
    static bool _held;

    // The rise, while this mod is steering it: the velocity it wants, kept as a
    // float so a gravity of 25.6 a tick does not round away, and the value it
    // last wrote, to tell the game's own +40 apart from anything else.
    static bool _rising;
    static float _riseVel;
    static short _written;

    const int GameGravity = 40;

    // The slider's range. Above SafeStrength a jump on flat ground lands fast
    // enough to take fall damage (found in play; the landing test is >= 480).
    const int MinStrength = 80, MaxStrength = 700, SafeStrength = 450;

    public void OnLoad()
    {
        var view = RecompOne.Runtime.Runtime.View;
        _keyIndex = IndexOf(Keys, view.GetString(KeyKey, "Space"));
        _padIndex = IndexOf(PadButtons, view.GetString(PadKey, "R3 (right stick click)"));
        _strength = Math.Clamp((int)view.GetFloat(StrengthKey, 373f), MinStrength, MaxStrength);
        _risePercent = Math.Clamp((int)view.GetFloat(RiseKey, 80f), 40, 100);
    }

    static int IndexOf<T>((string Name, T)[] list, string name)
    {
        for (int i = 0; i < list.Length; i++)
            if (list[i].Name == name) return i;
        return 0;
    }

    static bool JumpDown()
    {
        var key = Keys[_keyIndex].Key;
        if (key != Key.Unknown && HostWindow.IsKeyDown(key)) return true;
        int pad = PadButtons[_padIndex].Button;
        return pad >= 0 && HostWindow.IsPadButtonDown(pad);
    }

    [PreHook("game", Address = 0x80028560)]
    static bool BeforeVertical(CpuContext c, IMemory m)
    {
        bool down = JumpDown();
        bool pressed = down && !_held;
        _held = down;
        if (!pressed) return true;

        if (m.ReadU8(FallState) != OnGround) return true;
        if (m.ReadU8(Action) == Dead || m.ReadU16(Hp) == 0) return true;

        // Launch at strength * s under gravity 40 * s^2: the same peak as
        // strength under 40, reached 1/s times as slowly. The fall is left at
        // the game's gravity, so the landing -- and its damage -- is unchanged.
        float s = _risePercent / 100f;
        _riseVel = -_strength * s;
        _written = (short)MathF.Round(_riseVel);
        _rising = _risePercent < 100;

        m.WriteU8(FallState, LongFall);
        m.WriteU16(FallVel, unchecked((ushort)_written));
        return true;
    }

    /// <summary>
    /// The 0x40 arm has just moved Y by the velocity and added 40 to it. While
    /// rising, replace that 40 with the slower rise gravity. Anything other than
    /// exactly +40 means the game stepped in -- a ceiling zeroes the velocity, a
    /// landing changes the state -- and the rise is handed back to it.
    /// </summary>
    [PostHook("game", Address = 0x80028560)]
    static void AfterVertical(CpuContext c, IMemory m)
    {
        if (!_rising) return;

        short now = unchecked((short)m.ReadU16(FallVel));
        if (m.ReadU8(FallState) != LongFall || now != _written + GameGravity)
        {
            _rising = false;
            return;
        }

        float s = _risePercent / 100f;
        _riseVel += GameGravity * s * s;
        if (_riseVel >= 0)
        {
            // The peak: from here the game's own gravity brings you down.
            _rising = false;
            m.WriteU16(FallVel, 0);
            return;
        }

        _written = (short)MathF.Round(_riseVel);
        m.WriteU16(FallVel, unchecked((ushort)_written));
    }

    public void DrawSettings()
    {
        ImGui.TextWrapped("Press the jump key while standing on the ground. The game's own gravity "
                        + "and landing take it from there; a ceiling stops you rising.");
        ImGui.Separator();

        if (Combo("Jump key", ref _keyIndex, Keys))
            Persist(KeyKey, Keys[_keyIndex].Name);
        if (Combo("Jump pad button", ref _padIndex, PadButtons))
            Persist(PadKey, PadButtons[_padIndex].Name);

        if (ImGui.SliderInt("Jump strength", ref _strength, MinStrength, MaxStrength))
        {
            RecompOne.Runtime.Runtime.View.SetFloat(StrengthKey, _strength);
            RecompOne.Runtime.Runtime.SaveView();
        }
        ImGui.TextDisabled("Rough height: " + (_strength * _strength / 80) + " units (eye height is 1600). "
                         + SafeStrength + " is the most that lands without fall damage.");
        if (_strength > SafeStrength)
            ImGui.TextColored(new System.Numerics.Vector4(1f, 0.6f, 0.2f, 1f),
                "Above " + SafeStrength + ": every jump on flat ground costs HP when you land.");

        if (ImGui.SliderInt("Rise speed", ref _risePercent, 40, 100, "%d%%"))
        {
            RecompOne.Runtime.Runtime.View.SetFloat(RiseKey, _risePercent);
            RecompOne.Runtime.Runtime.SaveView();
        }
        ImGui.TextDisabled("How fast you go up. Lower is floatier; the height and the landing stay the same.");
    }

    static bool Combo<T>(string label, ref int index, (string Name, T)[] list)
    {
        bool changed = false;
        if (ImGui.BeginCombo(label, list[index].Name))
        {
            for (int i = 0; i < list.Length; i++)
                if (ImGui.Selectable(list[i].Name, i == index))
                {
                    index = i;
                    changed = true;
                }
            ImGui.EndCombo();
        }
        return changed;
    }

    static void Persist(string key, string value)
    {
        RecompOne.Runtime.Runtime.View.SetString(key, value);
        RecompOne.Runtime.Runtime.SaveView();
    }
}
