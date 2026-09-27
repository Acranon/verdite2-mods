// ModCompiler compiles mods with no implicit usings, so every namespace the
// file needs must be named here -- including System.
using System;
using ImGuiNET;
using RecompOne.Runtime.Context;
using RecompOne.Runtime.Host;
using RecompOne.Runtime.Memory;
using RecompOne.Runtime.Modding;
using Silk.NET.Input;

namespace Kf2.Mods.RunKey;

/// <summary>
/// A Run key of its own, so Cross stops meaning two things.
///
/// The game reads the Cross mask (`0x8006E568`) against the pad word two ways in
/// stage 3 (`func_8002A550`), and the two tests are what make one button do two
/// jobs:
///
///     func_8002957C   Cross down this frame AND last frame  -> run
///     func_80029CBC   Cross down this frame, NOT last frame -> use / pick up
///
/// with this frame's pad at `0x80199554` and last frame's at `0x80199556`, which
/// stage 3 copies across at its end. func_8002957C is called before
/// func_80029CBC, so a Pre/Post pair around func_8002957C alone can show it a
/// different pad without the use test ever seeing the difference:
///
/// * **Run key held** -- set Cross in both words, so the held test passes and the
///   just-pressed test inside func_8002957C does not. func_80029CBC then sees the
///   real pad, in which Cross is not down, so nothing is picked up.
/// * **Cross held without the Run key** -- clear Cross from this frame's word
///   once it was also down last frame, so the held test fails. The first frame
///   of a press is left alone, so every just-pressed test still fires.
///
/// The Post hook puts back only the Cross bit it changed, in case the function
/// wrote anything else into those words. Menus poll the pad in their own loops
/// and never pass through here, so Cross still confirms there.
/// </summary>
public sealed class RunKeyMod : IMod
{
    const uint PadNow     = 0x80199554;
    const uint PadLast    = 0x80199556;
    const uint CrossMask  = 0x8006E568;

    const string KeyKey = "kf2.runkey.key";
    const string PadKey = "kf2.runkey.pad";

    static readonly (string Name, Key Key)[] Keys =
    [
        ("Left Shift", Key.ShiftLeft),
        ("Left Ctrl", Key.ControlLeft),
        ("Left Alt", Key.AltLeft),
        ("Caps Lock", Key.CapsLock),
        ("C", Key.C),
        ("V", Key.V),
        ("X", Key.X),
        ("Z", Key.Z),
        ("None", Key.Unknown),
    ];

    // SDL GameControllerButton indices, the encoding HostWindow.IsPadButtonDown takes.
    static readonly (string Name, int Button)[] PadButtons =
    [
        ("None", -1),
        ("L3 (left stick click)", 7),
        ("R3 (right stick click)", 8),
        ("Misc (share / mute)", 15),
    ];

    static int _keyIndex;
    static int _padIndex;

    static bool _touched;
    static ushort _savedNow, _savedLast;

    public void OnLoad()
    {
        var view = RecompOne.Runtime.Runtime.View;
        _keyIndex = IndexOf(Keys, view.GetString(KeyKey, "Left Shift"));
        _padIndex = IndexOf(PadButtons, view.GetString(PadKey, "L3 (left stick click)"));
    }

    static int IndexOf<T>((string Name, T)[] list, string name)
    {
        for (int i = 0; i < list.Length; i++)
            if (list[i].Name == name) return i;
        return 0;
    }

    static bool RunHeld()
    {
        var key = Keys[_keyIndex].Key;
        if (key != Key.Unknown && HostWindow.IsKeyDown(key)) return true;
        int pad = PadButtons[_padIndex].Button;
        return pad >= 0 && HostWindow.IsPadButtonDown(pad);
    }

    [PreHook("game", Address = 0x8002957C)]
    static bool BeforeRunCheck(CpuContext c, IMemory m)
    {
        _touched = false;
        ushort cross = (ushort)m.ReadU32(CrossMask);
        if (cross == 0) return true;

        ushort now = m.ReadU16(PadNow);
        ushort last = m.ReadU16(PadLast);
        ushort newNow = now, newLast = last;

        if (RunHeld())
        {
            newNow |= cross;
            newLast |= cross;
        }
        else if ((now & cross) != 0 && (last & cross) != 0)
        {
            newNow &= (ushort)~cross;
        }

        if (newNow == now && newLast == last) return true;

        _savedNow = now;
        _savedLast = last;
        _touched = true;
        m.WriteU16(PadNow, newNow);
        m.WriteU16(PadLast, newLast);
        return true;
    }

    [PostHook("game", Address = 0x8002957C)]
    static void AfterRunCheck(CpuContext c, IMemory m)
    {
        if (!_touched) return;
        _touched = false;
        ushort cross = (ushort)m.ReadU32(CrossMask);
        m.WriteU16(PadNow, (ushort)((m.ReadU16(PadNow) & ~cross) | (_savedNow & cross)));
        m.WriteU16(PadLast, (ushort)((m.ReadU16(PadLast) & ~cross) | (_savedLast & cross)));
    }

    public void DrawSettings()
    {
        ImGui.TextWrapped("Cross (F by default) now only uses and picks up. Hold the Run key "
                        + "to run; it never picks anything up.");
        ImGui.Separator();

        if (Combo("Run key", ref _keyIndex, Keys))
            Persist(KeyKey, Keys[_keyIndex].Name);
        if (Combo("Run pad button", ref _padIndex, PadButtons))
            Persist(PadKey, PadButtons[_padIndex].Name);
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
