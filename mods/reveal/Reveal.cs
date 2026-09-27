// ModCompiler compiles mods with no implicit usings, so every namespace the
// file needs must be named here -- including System.
using System;
using System.Collections.Generic;
using ImGuiNET;
using RecompOne.Runtime.Context;
using RecompOne.Runtime.Host;
using RecompOne.Runtime.Host.Window;
using RecompOne.Runtime.Memory;
using RecompOne.Runtime.Modding;
using Silk.NET.Input;
using KingsField2 = Recompiled.KingsField2_game;

namespace Kf2.Mods.Reveal;

/// <summary>
/// Makes the world's interactive objects glow, by recolouring their polygons.
///
/// ---- which objects ----
///
/// The object table is 396 records of 0x44 at 0x80177714. A record's
/// definition is `0x80175914 + u16[rec+0x6] * 0x18`, and the byte at def+0 is
/// the object's *kind* -- the vocabulary the use handler `func_800489FC`
/// dispatches on when you press the action button in front of something:
///
///     0x02 0x03 0x04   set "activated" (rec+0x8) if unlocked; 3/4 check the
///                      side you stand on and a key item            -> doors
///     0x09 0x15        a container (chest, barrel): activate the object linked at rec+0x3A and pick it up;
///                      0xFFFF there once emptied                   -> chests
///     0x40             func_8004831C, the pickup                   -> items
///     0x05 0x08 0x16   flip a two-state byte, facing checked; with contents
///                      linked at +0x3A these are chests, locked while +0x38
///                      (the key's item id) is below 0xFE          -> levers/chests
///     0x53             set "activated" with no lock, key or facing -> a wall
///                      switch (found in play)
///     0x51             a spear trap in a wall panel (found in play) -- not
///                      drawn while closed
///
/// A secret door is not a kind of its own: the one found in play is kind 0x02,
/// state 0 -> 20 as it opened, definition 0x082 -- a door whose model is a wall.
///     0x0D 0x14 signs, 0x0E save point, 0x12 fountain, 0x20 lift
///     0x54             a trap floor that falls away (found in play; not a use
///                      handler kind -- it triggers when walked on)
///
/// ---- how it glows ----
///
/// `func_800331B4` hands each object to the submitter `func_80032588` with
/// `A2 = rec + 0x14`, so in a hook on that call `A2 - 0x14` is the record. The packets
/// it builds go into this frame's primitive buffer, whose `{start, end, current}`
/// descriptor `*(u32*)0x8017E0A4` points at (patches/PrimBuffer.cs); `current`
/// before and after the call brackets exactly that object's packets. Each is a
/// GPU packet -- a tag whose top byte is its length in words, then a command
/// word whose top byte says flat/gouraud, triangle/quad, textured -- so every
/// colour word is found from the command, and tinted. The port's per-pixel
/// lighting keys each packet by its command word and falls back to the packet's
/// own colours when it no longer matches, which is what makes the tint show.
/// </summary>
public sealed class RevealMod : IMod
{
    const uint ObjBase = 0x80177714, ObjStride = 0x44, ObjCount = 396;
    const uint DefBase = 0x80175914, DefStride = 0x18, DefCount = 320;
    const uint FrameDesc = 0x8017E0A4;   // -> {start, end, current}
    const uint PlayerPos = 0x801994EC;   // s32 x, y, z

    enum Cat { None, Secret, Chest, Item, Door, Lever, Other, LockedChest, Trap }

    static readonly (string Name, float R, float G, float B)[] Colours =
    [
        ("", 0, 0, 0),
        ("Secret doors and wall panels", 1.00f, 0.15f, 0.15f),
        ("Containers with loot (chests, barrels)", 1.00f, 0.80f, 0.10f),
        ("Loose items",          0.20f, 1.00f, 0.25f),
        ("Doors",                0.25f, 0.45f, 1.00f),
        ("Levers and switches",  0.10f, 0.95f, 0.95f),
        ("Everything else",      0.90f, 0.90f, 0.90f),
        ("Locked containers",    0.85f, 0.20f, 1.00f),
        ("Traps",                1.00f, 0.45f, 0.00f),
    ];

    static readonly bool[] _show = [false, true, true, true, true, true, true, true, true];

    static readonly (string Name, Key Key)[] Keys =
    [
        ("G", Key.G), ("H", Key.H), ("J", Key.J), ("K", Key.K), ("L", Key.L),
        ("B", Key.B), ("N", Key.N), ("M", Key.M), ("F6", Key.F6), ("F7", Key.F7),
        ("F8", Key.F8), ("F9", Key.F9),
    ];

    static int _toggleKey = 0;   // G
    static int _probeKey = 1;    // H
    static bool _on;
    static bool _toggleHeld, _probeHeld;
    static bool _pulse = true;
    static float _strength = 0.7f;

    // Set in the Pre hook, used by the Post hook of the same call.
    static Cat _cat;
    static uint _before;

    public void OnLoad()
    {
        var view = RecompOne.Runtime.Runtime.View;
        _toggleKey = IndexOf(view.GetString("kf2.reveal.toggle", "G"), 0);
        _probeKey = IndexOf(view.GetString("kf2.reveal.probe", "H"), 1);
        _pulse = view.GetBool("kf2.reveal.pulse", true);
        _phantom = view.GetBool("kf2.reveal.phantom", true);
        _warnings = view.GetBool("kf2.reveal.warnings", true);
        _warnTiles = Math.Clamp(view.GetFloat("kf2.reveal.warntiles", 2.5f), 1f, 5f);
        _strength = view.GetFloat("kf2.reveal.strength", 0.7f);
        for (int i = 1; i < _show.Length; i++)
            _show[i] = view.GetBool($"kf2.reveal.show{i}", _show[i]);
    }

    static int IndexOf(string name, int fallback)
    {
        for (int i = 0; i < Keys.Length; i++)
            if (Keys[i].Name == name) return i;
        return fallback;
    }

    // ---- keys, polled once a frame at the end of stage 3 ----

    [PostHook("game", Address = 0x8002A550)]
    static void AfterPlayerStage(CpuContext c, IMemory m)
    {
        bool t = HostWindow.IsKeyDown(Keys[_toggleKey].Key);
        if (t && !_toggleHeld)
        {
            _on = !_on;
            ToastNotifications.ShowText("Reveal", _on ? "On" : "Off");
        }
        _toggleHeld = t;

        bool p = HostWindow.IsKeyDown(Keys[_probeKey].Key);
        if (p && !_probeHeld) Probe(m);
        _probeHeld = p;

        if (_on)
        {
            RebuildPartners(m);
            if (_warnings) ProximityWarning(m);
        }
        PhantomSight(c, m);
    }

    // ---- phantom sight: the Phantom Rod's own effect ----
    //
    // Using the PHANTOM ROD (item 0x54) in func_800474D0 only sets a timer --
    // u16 0x8019947E = 1200 ticks, a minute -- and uses one up. Stage 3
    // (func_8002A550) counts it down and every 8 ticks calls func_800364D8(1);
    // on the last tick, func_800364D8(0). That routine walks all 396 objects and
    // hands two kinds to func_800363E4(obj, show, tile):
    //
    //     kind 0x05 with +0x38 != 0xFE   a hidden compartment, not yet opened
    //     kind 0x51 with +0x3B != 0xFF   a spear trap in a wall
    //
    // which, showing, sets the object's scale (+0x2C/+0x2E/+0x30) to 0x1000 and
    // its map tile to 0xFE (open); hidden, the scale to 0 and the tile back to
    // the definition's wall (def+0xC or +0x17). So those two are not drawn at all
    // until opened -- they are scaled to nothing, which is why forcing their
    // draw mask on showed nothing -- and the rod is how the game shows them.
    // While the glow is on this does what the rod does, on the rod's schedule,
    // without the rod, its timer or its HUD icon; turned off, it puts them back
    // as the rod does when it runs out (unless a real rod is running).
    const uint PhantomTimer = 0x8019947E;
    static bool _phantom = true;
    static bool _phantomActive;
    static int _phantomTick;

    static void PhantomSight(CpuContext c, IMemory m)
    {
        if (_on && _phantom)
        {
            if (!_phantomActive || ++_phantomTick >= 8)
            {
                _phantomTick = 0;
                CallPhantom(c, m, 1);
            }
            _phantomActive = true;
        }
        else if (_phantomActive)
        {
            _phantomActive = false;
            if (m.ReadU16(PhantomTimer) == 0) CallPhantom(c, m, 0);
        }
    }

    static void CallPhantom(CpuContext c, IMemory m, uint show)
    {
        var snap = c.Snapshot();
        try
        {
            c.A0 = show;
            KingsField2.func_800364D8(c, m);
        }
        finally { c.Restore(snap); }
    }

    // A trap or a hidden panel is scaled to nothing while closed, so without
    // phantom sight (above) it cannot glow; while Reveal is on, one coming
    // within range is announced -- once, until you have walked away from it.
    // In tiles (2048 units each); 1.5 was found too close to react in play.
    static float _warnTiles = 2.5f;
    static long WarnRange => (long)(_warnTiles * 2048);
    static long ForgetRange => WarnRange + 2048;
    static readonly HashSet<uint> _warned = [];

    static bool _warnings = true;

    // Measured in play: the floor below sat exactly 0x1000 under this one, so
    // the limit has to be well short of that and still clear a trap mounted
    // high in a wall on your own floor.
    const long MaxHeight = 0xC00;

    // On your floor and in range is enough -- behind a wall or a door
    // included, by request: something just the other side is worth knowing.
    static void ProximityWarning(IMemory m)
    {
        int px = (int)m.ReadU32(PlayerPos), pz = (int)m.ReadU32(PlayerPos + 8);
        int py = (int)m.ReadU32(PlayerPos + 4);
        for (uint i = 0; i < ObjCount; i++)
        {
            uint rec = ObjBase + i * ObjStride;
            uint def = m.ReadU16(rec + 0x6);
            if (def >= DefCount) { _warned.Remove(i); continue; }
            var cat = Classify(m, rec);
            if (cat is not (Cat.Trap or Cat.Secret) || !_show[(int)cat]) continue;
            // Secret doors (kind 0x02) never warn, by request: their glow reads
            // plainly on its own, and the toast was taken for a hidden wall
            // panel nearby. Only the compartments (kind 0x05, any definition), which
            // are easy to walk past, warn as "something hidden".
            if (cat == Cat.Secret && m.ReadU8(DefBase + m.ReadU16(rec + 0x6) * DefStride) == 0x02)
                continue;
            long dx = (int)m.ReadU32(rec + 0x14) - px, dz = (int)m.ReadU32(rec + 0x1C) - pz;
            long d2 = dx * dx + dz * dz;
            // Height too: found in play, the same spear panel warned from the
            // floor above (or below) it, 185 away across the map.
            long dy = (int)m.ReadU32(rec + 0x18) - py;
            // A falling floor (kind 0x54) is placed at the bottom of its drop,
            // found in play exactly 0x1000 below the floor it takes out from
            // under you (0x14D4 mid-jump) -- so below counts further for it.
            // Y grows downward: positive dy is below.
            bool trapFloor = m.ReadU8(DefBase + m.ReadU16(rec + 0x6) * DefStride) == 0x54;
            long below = trapFloor ? 0x1800 : MaxHeight;
            if (dy > below || -dy > MaxHeight) d2 = long.MaxValue;
            if (d2 < WarnRange * WarnRange)
            {
                if (_warned.Add(i))
                    ToastNotifications.ShowText("Reveal",
                        cat == Cat.Trap ? "Trap nearby!" : "Something hidden nearby");
            }
            else if (d2 > ForgetRange * ForgetRange)
                _warned.Remove(i);
        }
    }

    // ---- classification ----

    static Cat Classify(IMemory m, uint rec)
    {
        uint def = m.ReadU16(rec + 0x6);
        if (def >= DefCount) return Cat.None;
        byte kind = m.ReadU8(DefBase + def * DefStride);
        uint link = m.ReadU16(rec + 0x3A);
        return kind switch
        {
            // Confirmed in play: the secret door in area 0 is kind 0x02 with
            // definition 0x082 (model 130) -- an ordinary door wearing a wall.
            0x02 when def == 0x082 => Cat.Secret,
            0x09 or 0x15 => HasLoot(m, link) ? Cat.Chest : Cat.None,
            0x40 => Cat.Item,
            // Confirmed in play: a floor that falls away under you in area 0 is
            // kind 0x54 -- never reached by the use handler, it is stepped on.
            0x54 => Cat.Trap,
            // Confirmed in play: a wall panel that opens and strikes with a
            // spear is kind 0x51 (definition 0x0CA). Closed, it is not drawn at
            // all, so it can only glow while it strikes -- see TrapWarning.
            0x51 => Cat.Trap,
            0x02 or 0x03 or 0x04 => Cat.Door,
            // Confirmed in play: a chest in area 0 is kind 0x08 with its lock in
            // +0x38 (the PIRATE'S KEY; 0xFE once opened) and its contents
            // linked at +0x3A. The link outlives the contents -- an opened,
            // emptied chest still has it -- so loot means the linked object is
            // still there. Without a link, the same kinds are the levers.
            //
            // Unopened (+0x38 == 0xFF) counts as loot whether or not the linked
            // item exists yet: found in play, a hidden wall panel (kind 0x05,
            // definition 0x083 -- next to the secret door's 0x082, and as
            // wall-like) whose item only appeared once it was opened. That
            // model is a secret, so it is lit as one.
            // Kind 0x05 is always a compartment hidden in a wall -- the Phantom
            // Rod's own test (func_800364D8) is only "+0x38 != 0xFE", with or
            // without a linked item. Found in play: one with no link (0xFFFF)
            // was lit as a lever by the rule below it.
            0x05 => m.ReadU8(rec + 0x38) != 0xFE ? Cat.Secret
                  : HasLoot(m, link) ? Cat.Chest : Cat.None,
            0x08 or 0x16 when link < ObjCount => m.ReadU8(rec + 0x38) switch
            {
                < 0xFE => Cat.LockedChest,
                0xFF => Cat.Chest,
                _ => HasLoot(m, link) ? Cat.Chest : Cat.None,
            },
            0x08 or 0x16 => Cat.Lever,
            // Confirmed in play: a wall switch is kind 0x53.
            0x53 => Cat.Lever,
            0x0D or 0x0E or 0x0F or 0x12 or 0x14 or 0x20 => Cat.Other,
            _ => Cat.None,
        };
    }

    /// <summary>A container's linked contents still exist: a live slot (u16
    /// +0x6 not 0xFF, the renderer's own test).</summary>
    static bool HasLoot(IMemory m, uint link) =>
        link < ObjCount && m.ReadU16(ObjBase + link * ObjStride + 0x6) != 0xFF;

    // A door's +0x3A links its other leaf, which is not a door kind itself --
    // a double door lit only half way is what found it. Rebuilt every tick:
    // partner slot -> the door's category.
    static readonly Cat[] _partner = new Cat[ObjCount];

    static void RebuildPartners(IMemory m)
    {
        Array.Clear(_partner);
        for (uint i = 0; i < ObjCount; i++)
        {
            uint rec = ObjBase + i * ObjStride;
            var cat = Classify(m, rec);
            if (cat is not (Cat.Door or Cat.Secret)) continue;
            uint link = m.ReadU16(rec + 0x3A);
            if (link >= ObjCount || link == i) continue;

            // A door's link is not always its other leaf -- one lit a grave in
            // the pirates' cave -- so the partner has to look like one: no kind
            // of its own (0xFF) or a door kind, and within about a tile.
            uint other = ObjBase + link * ObjStride;
            uint odef = m.ReadU16(other + 0x6);
            if (odef >= DefCount) continue;
            byte okind = m.ReadU8(DefBase + odef * DefStride);
            if (okind is not (0xFF or 0x02 or 0x03 or 0x04)) continue;
            long dx = (int)m.ReadU32(other + 0x14) - (int)m.ReadU32(rec + 0x14);
            long dz = (int)m.ReadU32(other + 0x1C) - (int)m.ReadU32(rec + 0x1C);
            if (dx * dx + dz * dz > 0x1800L * 0x1800L) continue;
            _partner[link] = cat;
        }
    }

    // ---- the glow ----

    // func_80032588 is the model submitter. patches/ModelWalk.cs replaces both it
    // and the walk that calls it (func_800331B4) with C#, and the object calls
    // func_80032AC4 made in the original never happen there -- which is why the
    // first version of this hook drew nothing. Both walks hand the submitter an
    // object as A2 = rec + 0x14, its position, so that is how one is recognised.
    [PreHook("game", Address = 0x80032588)]
    static bool BeforeModel(CpuContext c, IMemory m)
    {
        _cat = Cat.None;
        if (!_on) return true;

        uint rec = c.A2 - 0x14u;
        if (rec < ObjBase || rec >= ObjBase + ObjStride * ObjCount || (rec - ObjBase) % ObjStride != 0)
            return true;

        var cat = Classify(m, rec);
        if (cat == Cat.None) cat = _partner[(rec - ObjBase) / ObjStride];
        if (cat == Cat.None || !_show[(int)cat]) return true;

        uint desc = m.ReadU32(FrameDesc);
        if (desc == 0) return true;
        _before = m.ReadU32(desc + 8);
        _cat = cat;
        return true;
    }

    [PostHook("game", Address = 0x80032588)]
    static void AfterModel(CpuContext c, IMemory m)
    {
        if (_cat == Cat.None) return;
        var cat = _cat;
        _cat = Cat.None;

        uint desc = m.ReadU32(FrameDesc);
        uint after = m.ReadU32(desc + 8);
        uint end = m.ReadU32(desc + 4);
        if (after <= _before || after > end || after - _before > 0x10000) return;

        var col = Colours[(int)cat];
        float k = _strength;
        if (_pulse) k *= 0.55f + 0.45f * MathF.Sin(Environment.TickCount64 / 180f);

        uint p = _before;
        while (p < after)
        {
            uint tag = m.ReadU32(p);
            uint len = tag >> 24;
            if (len == 0) break;
            TintPacket(m, p, len, col.R, col.G, col.B, k);
            p += 4 + len * 4;
        }
    }

    static void TintPacket(IMemory m, uint p, uint len, float tr, float tg, float tb, float k)
    {
        byte code = m.ReadU8(p + 7);
        if ((code & 0xE0) != 0x20) return;   // polygons only
        bool gouraud = (code & 0x10) != 0;
        bool quad = (code & 0x08) != 0;
        bool tex = (code & 0x04) != 0;
        int verts = quad ? 4 : 3;

        // Words after the tag: colour0|code, v0, [uv0]; then per further vertex
        // [colour], v, [uv].
        uint expected = (uint)(verts * (1 + (tex ? 1 : 0)) + (gouraud ? verts : 1));
        if (expected != len) return;

        uint w = 1;
        Tint(m, p + w * 4, tr, tg, tb, k);
        w += 2 + (tex ? 1u : 0u);
        for (int i = 1; i < verts; i++)
        {
            if (gouraud) { Tint(m, p + w * 4, tr, tg, tb, k); w++; }
            w += 1 + (tex ? 1u : 0u);
        }
    }

    static void Tint(IMemory m, uint addr, float tr, float tg, float tb, float k)
    {
        uint v = m.ReadU32(addr);
        float r = v & 0xFF, g = (v >> 8) & 0xFF, b = (v >> 16) & 0xFF;
        // Toward the tint at full brightness: 255 is twice as bright as a
        // texture's own colour, so it reads as a glow even in the dark.
        r += (tr * 255f - r) * k;
        g += (tg * 255f - g) * k;
        b += (tb * 255f - b) * k;
        uint rgb = (uint)Math.Clamp((int)r, 0, 255)
                 | ((uint)Math.Clamp((int)g, 0, 255) << 8)
                 | ((uint)Math.Clamp((int)b, 0, 255) << 16);
        m.WriteU32(addr, (v & 0xFF000000u) | rgb);
    }

    // ---- identify: what is around you ----

    static void Probe(IMemory m)
    {
        int px = (int)m.ReadU32(PlayerPos), pz = (int)m.ReadU32(PlayerPos + 8);
        var near = new List<(long D, string Line)>();
        for (uint i = 0; i < ObjCount; i++)
        {
            uint rec = ObjBase + i * ObjStride;
            uint def = m.ReadU16(rec + 0x6);
            if (def == 0xFF || def >= DefCount) continue;
            int x = (int)m.ReadU32(rec + 0x14), z = (int)m.ReadU32(rec + 0x1C);
            long dx = x - px, dz = z - pz, d2 = dx * dx + dz * dz;
            if (d2 > 6000L * 6000L) continue;
            byte kind = m.ReadU8(DefBase + def * DefStride);
            int dy = (int)m.ReadU32(rec + 0x18) - (int)m.ReadU32(PlayerPos + 4);
            near.Add((d2, $"slot {i,3} dist {(int)Math.Sqrt(d2),5} dy {dy,6} kind 0x{kind:X2} def 0x{def:X3} " +
                          $"type 0x{m.ReadU8(rec + 0x4):X2} state {m.ReadU16(rec + 0x8)} " +
                          $"b38 0x{m.ReadU8(rec + 0x38):X2} link 0x{m.ReadU16(rec + 0x3A):X4} " +
                          $"-> {Colours[(int)Classify(m, rec)].Name}"));
        }
        near.Sort((a, b) => a.D.CompareTo(b.D));

        // The player's condition timers (status screen page one) and the timer
        // at 0x8019947E whose countdown swaps palettes at VRAM (576, 279..282)
        // every four ticks -- a candidate for the screen blinking after a
        // ghost's darkness.
        short T(uint a) => (short)m.ReadU16(a);
        Console.WriteLine($"[KF2] reveal: conditions poison {T(0x80199468)} curse {T(0x8019946A)} " +
                          $"dark {T(0x8019946E)} slow {T(0x80199472)} paralyze {T(0x80199474)}; " +
                          $"timers 6C {T(0x8019946C)} 70 {T(0x80199470)} 76 {T(0x80199476)} 78 {T(0x80199478)} " +
                          $"7A {T(0x8019947A)} 7C {T(0x8019947C)} flash(7E) {T(0x8019947E)} 80 {T(0x80199480)}");
        Console.WriteLine($"[KF2] reveal: {near.Count} object(s) within 6000 of ({px},{pz}), nearest first");
        for (int i = 0; i < near.Count && i < 12; i++) Console.WriteLine("[KF2]   " + near[i].Line);
        ToastNotifications.ShowText("Reveal", $"{near.Count} objects nearby -- see the console");
    }

    // ---- settings ----

    public void DrawSettings()
    {
        ImGui.TextWrapped($"Press {Keys[_toggleKey].Name} in game to toggle the glow. "
                        + $"{Keys[_probeKey].Name} lists the objects around you in the console "
                        + "(Debug > Console), nearest first, with their kind.");
        ImGui.Separator();
        var view = RecompOne.Runtime.Runtime.View;
        if (Combo("Toggle key", ref _toggleKey)) { view.SetString("kf2.reveal.toggle", Keys[_toggleKey].Name); RecompOne.Runtime.Runtime.SaveView(); }
        if (Combo("Identify key", ref _probeKey)) { view.SetString("kf2.reveal.probe", Keys[_probeKey].Name); RecompOne.Runtime.Runtime.SaveView(); }

        for (int i = 1; i < _show.Length; i++)
        {
            var col = Colours[i];
            ImGui.ColorButton($"##c{i}", new System.Numerics.Vector4(col.R, col.G, col.B, 1f));
            ImGui.SameLine();
            if (ImGui.Checkbox(col.Name, ref _show[i])) { view.SetBool($"kf2.reveal.show{i}", _show[i]); RecompOne.Runtime.Runtime.SaveView(); }
        }

        if (ImGui.SliderFloat("Glow strength", ref _strength, 0.2f, 1f, "%.2f")) { view.SetFloat("kf2.reveal.strength", _strength); RecompOne.Runtime.Runtime.SaveView(); }
        if (ImGui.Checkbox("Pulse", ref _pulse)) { view.SetBool("kf2.reveal.pulse", _pulse); RecompOne.Runtime.Runtime.SaveView(); }
        if (ImGui.Checkbox("Phantom sight", ref _phantom)) { view.SetBool("kf2.reveal.phantom", _phantom); RecompOne.Runtime.Runtime.SaveView(); }
        ImGui.TextDisabled("While the glow is on, do what the Phantom Rod does: hidden compartments");
        ImGui.TextDisabled("open up and spear traps show in the walls, so they glow too. No rod is used.");
        if (ImGui.Checkbox("Warn when a trap or hidden wall panel is near", ref _warnings)) { view.SetBool("kf2.reveal.warnings", _warnings); RecompOne.Runtime.Runtime.SaveView(); }
        if (ImGui.SliderFloat("Warning distance", ref _warnTiles, 1f, 5f, "%.1f tiles")) { view.SetFloat("kf2.reveal.warntiles", _warnTiles); RecompOne.Runtime.Runtime.SaveView(); }
    }

    static bool Combo(string label, ref int index)
    {
        bool changed = false;
        if (ImGui.BeginCombo(label, Keys[index].Name))
        {
            for (int i = 0; i < Keys.Length; i++)
                if (ImGui.Selectable(Keys[i].Name, i == index)) { index = i; changed = true; }
            ImGui.EndCombo();
        }
        return changed;
    }
}
