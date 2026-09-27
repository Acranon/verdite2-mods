// ModCompiler compiles mods with no implicit usings, so every namespace the
// file needs must be named here -- including System.
using System;
using System.Collections.Generic;
using ImGuiNET;
using RecompOne.Runtime.Context;
using RecompOne.Runtime.Host;
using RecompOne.Runtime.Memory;
using RecompOne.Runtime.Modding;
using Silk.NET.Input;
using KingsField2 = Recompiled.KingsField2_game;

namespace Kf2.Mods.SpellInfo;

/// <summary>
/// What each spell does, on the two magic pages, in the game's own font.
///
/// ---- where spells are ----
///
/// Twenty spells, numbered 0..19 in the order of the name table at
/// 0x80066664 (0x18 a name, WATER FALL first, BREATH last). Each has a
/// 0x1A-byte record at 0x8019C5EC + n * 0x1A: byte 0 is 1 once learned, and
/// the u16 at +0x16 is the MP cost -- what func_80027DC0 compares against the
/// player's MP (0x8019942C) before a cast, so the cost shown here is read live.
///
/// Both magic pages build their rows through func_8001A1E8(records, names,
/// costs, ids, first, last): the Magic page (func_8001A04C) asks for 14..19,
/// the support spells, and the attack-magic page (func_8001AB48) for 0..13.
/// `ids` is a u8 per row, the spell number, so the builder's A3 is noted, and
/// the highlighted row is desc+0x21 at each cursor step (func_8001EB70, from
/// 0x8001A170 and 0x8001AD08).
///
/// ---- drawing ----
///
/// The key toggles a panel that follows the cursor. It is drawn as Gear
/// Compare draws its own: after the page's list (func_800209E0), with the
/// status screen's text and number routines, and the window last so the
/// ordering table puts it underneath. The game has no spell descriptions of
/// its own (no message on the disc describes one), so the lines below are
/// ours, written from the King's Field Wiki's spell pages; the font has only
/// capitals, space and , ' ! ? -- no digits or full stops -- so numbers are
/// spelled out except the MP cost, which uses the number routine.
/// </summary>
public sealed class SpellInfoMod : IMod
{
    const uint Records = 0x8019C5EC, RecordSize = 0x1A;
    const uint NameTable = 0x80066664, NameStride = 0x18;
    const int SpellCount = 20;

    const uint TextFont = 0x80064BF0, NumberFont = 0x80064BE4;

    static readonly uint[] SpellLists = [0x8001A170, 0x8001AD08];   // func_8001EB70 return addresses

    static readonly (string Element, string Text)[] Spells =
    [
        /*  0 WATER FALL     */ ("WATER", "A SPRAY OF WATER THAT LINGERS ON THE TARGET, GOOD AGAINST COPPER KNIGHTS"),
        /*  1 STONE          */ ("EARTH", "HURLS A LARGE BOULDER STRAIGHT AHEAD, IT DOES NOT HOME IN"),
        /*  2 EARTH WAVE     */ ("EARTH", "A SHOCKWAVE AROUND YOU THAT HITS EVERY FOE NEARBY, BUT OFTEN MISSES ONES RIGHT BESIDE YOU"),
        /*  3 METEOR         */ ("EARTH", "DROPS FIERY BOULDERS ON THE TARGET FROM ABOVE, ONE OF THE STRONGEST SPELLS"),
        /*  4 WIND CUTTER    */ ("WIND",  "A FAST BLADE OF WIND THAT CUTS THROUGH FOES, SO IT CAN HIT SEVERAL IN A LINE, WEAK BUT CHEAP"),
        /*  5 ICE STORM      */ ("WIND",  "A HOMING MIST OF ICE THAT HANGS AROUND THE TARGET, DAMAGING IT OVER TIME"),
        /*  6 FREEZE         */ ("WIND",  "AN ICE DRAGON ENCASES THE TARGET, FREEZING IT IN PLACE FOR A MOMENT, CLOSE IN AND STRIKE"),
        /*  7 FIRE BALL      */ ("FIRE",  "A SMALL FIREBALL, WEAK BUT CHEAP, IT CAN ALSO LIGHT UNLIT LAMPS"),
        /*  8 FIRE WALL      */ ("FIRE",  "PILLARS OF FIRE THAT KEEP BURNING ANY FOE INSIDE THEM, VERY GOOD VALUE FOR ITS MP"),
        /*  9 FIRE STORM     */ ("FIRE",  "A VOLLEY OF LARGE FIREBALLS THAT EXPLODE ON CONTACT"),
        /* 10 FLAME          */ ("FIRE",  "A PHOENIX THAT FIRES ON FOES IN ITS PATH, THEN BURSTS ON THE TARGET, STRONG BUT COSTLY"),
        /* 11 LIGHTNING VOLT */ ("LIGHT", "CALLS A LIGHTNING BOLT DOWN ON THE TARGET WITH A WIDE BLAST, BEST IN OPEN ROOMS"),
        /* 12 FLASH          */ ("LIGHT", "FIVE HOMING LIGHTS THAT BURST ON IMPACT, DO NOT CAST IT UP CLOSE, THE BLASTS CAN HURT YOU"),
        /* 13 SEATH          */ ("WATER", "FOUR STREAMS OF HOLY WATER THAT PIERCE THROUGH EVERYTHING IN THEIR PATH"),
        /* 14 DISPOISON      */ ("WATER", "CURES POISON AT ONCE"),
        /* 15 RESIST FIRE    */ ("WATER", "FOR A WHILE, GREATLY REDUCES THE DAMAGE YOU TAKE FROM FIRE"),
        /* 16 EARTH HEAL     */ ("EARTH", "RESTORES A LITTLE HP, ABOUT FIFTY, THE CHEAPEST HEAL"),
        /* 17 MISSILE SHIELD */ ("WIND",  "FOR A WHILE, MEANT TO WARD OFF ARROWS, THOUGH PLAYERS REPORT LITTLE EFFECT"),
        /* 18 LIGHT          */ ("LIGHT", "LIGHTS UP YOUR SURROUNDINGS FOR A WHILE AND PUSHES BACK DARKNESS"),
        /* 19 BREATH         */ ("LIGHT", "RESTORES ABOUT A HUNDRED AND FIFTY HP AND CURES BAD CONDITIONS"),
    ];

    const string KeyKey = "kf2.spellinfo.key", XKey = "kf2.spellinfo.x", YKey = "kf2.spellinfo.y";

    static readonly (string Name, Key Key)[] Keys =
    [
        ("`", Key.GraveAccent), ("I", Key.I), ("Tab", Key.Tab), ("Z", Key.Z), ("X", Key.X),
        ("C", Key.C), ("V", Key.V), ("F5", Key.F5), ("F6", Key.F6), ("F7", Key.F7),
    ];

    static int _key;
    static int _x = 30, _y = 40;
    static bool _shown, _held;

    // The page being stepped: its row ids (from the builder) and descriptor.
    static int _pages;
    static uint _ids, _desc;

    // Measured off a screenshot: 7.2 px a character. The text routine draws at
    // most 24 characters (its records are 0x18 bytes, like a name), so lines
    // wrap at 24 and the window is sized to that.
    const float CharW = 7.2f;
    const int MaxChars = 24, Line = 13, Pad = 6;
    const int Width = Pad * 2 + (int)(MaxChars * CharW) + 4;

    public void OnLoad()
    {
        var view = RecompOne.Runtime.Runtime.View;
        string name = view.GetString(KeyKey, "`");
        _key = 0;
        for (int i = 0; i < Keys.Length; i++)
            if (Keys[i].Name == name) _key = i;
        _x = (int)view.GetFloat(XKey, 30f);
        _y = (int)view.GetFloat(YKey, 40f);
    }

    // ---- which page, which rows ----

    [PreHook("game", Address = 0x8001A04C)] static bool MagicIn(CpuContext c, IMemory m) { Enter(); return true; }
    [PostHook("game", Address = 0x8001A04C)] static void MagicOut(CpuContext c, IMemory m) { Leave(); }
    [PreHook("game", Address = 0x8001AB48)] static bool AttackIn(CpuContext c, IMemory m) { Enter(); return true; }
    [PostHook("game", Address = 0x8001AB48)] static void AttackOut(CpuContext c, IMemory m) { Leave(); }

    static void Enter() { _pages++; _ids = 0; _desc = 0; }
    static void Leave() { if (_pages > 0) _pages--; _ids = 0; _desc = 0; }

    [PreHook("game", Address = 0x8001A1E8)]
    static bool BeforeBuild(CpuContext c, IMemory m)
    {
        if (_pages > 0) _ids = c.A3;
        return true;
    }

    [PreHook("game", Address = 0x8001EB70)]
    static bool BeforeStep(CpuContext c, IMemory m)
    {
        if (_pages == 0 || Array.IndexOf(SpellLists, c.RA) < 0) return true;
        _desc = c.A0;
        bool down = HostWindow.IsKeyDown(Keys[_key].Key);
        if (down && !_held) _shown = !_shown;
        _held = down;
        return true;
    }

    // ---- the panel ----

    // A0 is not preserved across the call, so the list being drawn is noted
    // on the way in.
    static uint _drawing;

    [PreHook("game", Address = 0x800209E0)]
    static bool BeforeList(CpuContext c, IMemory m) { _drawing = c.A0; return true; }

    [PostHook("game", Address = 0x800209E0)]
    static void AfterList(CpuContext c, IMemory m)
    {
        if (!_shown || _pages == 0 || _ids == 0 || _desc == 0 || _drawing != _desc) return;
        int n = m.ReadU8(_desc + 0x1E), cur = m.ReadU8(_desc + 0x21);
        if (n == 0 || cur >= n) return;
        int spell = m.ReadU8(_ids + (uint)cur);
        if ((uint)spell >= SpellCount) return;

        var saved = c.Snapshot();
        try
        {
            c.SP -= 0x80u;
            var (element, text) = Spells[spell];
            var lines = Wrap(text, MaxChars);

            // Never over the list: its row 0 starts at desc+0x1D (plus a 5 px
            // inset, with the frame a little above), so a panel that would
            // reach it slides up instead.
            int height = Pad + 2 * Line + 4 + lines.Count * Line + Pad - 2;
            int listTop = m.ReadU8(_desc + 0x1D) - 4;
            int top = _y;
            if (top + height > listTop) top = Math.Max(4, listTop - height);

            int x = _x, y = top + Pad;
            Text(c, m, x + Pad, y, Name(m, spell));
            y += Line;
            Text(c, m, x + Pad, y, element);
            Text(c, m, x + Width - Pad - (int)(6 * CharW), y, "MP");
            Number(c, m, x + Width - Pad - (int)(3 * CharW), y, m.ReadU16(Records + (uint)spell * RecordSize + 0x16));
            y += Line + 4;
            foreach (var l in lines) { Text(c, m, x + Pad, y, l); y += Line; }

            Box(c, m, x, top, Width, height);
        }
        finally { c.Restore(saved); }
    }

    static List<string> Wrap(string text, int width)
    {
        var lines = new List<string>();
        string cur = "";
        foreach (var w in text.Split(' '))
        {
            if (cur.Length > 0 && cur.Length + 1 + w.Length > width) { lines.Add(cur); cur = w; }
            else cur = cur.Length == 0 ? w : cur + " " + w;
        }
        if (cur.Length > 0) lines.Add(cur);
        return lines;
    }

    static string Name(IMemory m, int spell)
    {
        uint rec = NameTable + (uint)spell * NameStride;
        var chars = new char[NameStride];
        int k = 0;
        for (uint i = 0; i < NameStride; i++)
        {
            byte ch = m.ReadU8(rec + i);
            if (ch == 0xFF) break;
            chars[k++] = ch < 26 ? (char)('A' + ch) : ch == 0x7F ? ' ' : ch switch
            {
                0x31 => ',', 0x32 => '\'', 0x38 => '!', 0x3A => '?', _ => ' ',
            };
        }
        return new string(chars, 0, k).Trim();
    }

    // Gear Compare's primitives: a record at sp+0x20 of s16 x, s16 y and the
    // text in font indices; func_80022B20's digits go in that record's text.
    const uint Rec = 0x20;

    static byte Glyph(char ch) => ch switch
    {
        ' ' => 0x7F, ',' => 0x31, '\'' => 0x32, '!' => 0x38, '?' => 0x3A,
        _ => ch is >= 'A' and <= 'Z' ? (byte)(ch - 'A') : (byte)0x7F,
    };

    static void Text(CpuContext c, IMemory m, int x, int y, string s)
    {
        uint rec = c.SP + Rec;
        m.WriteU16(rec, (ushort)x);
        m.WriteU16(rec + 2, (ushort)y);
        uint p = rec + 4;
        foreach (char ch in s) m.WriteU8(p++, Glyph(char.ToUpperInvariant(ch)));
        m.WriteU8(p, 0xFF);
        c.A0 = TextFont;
        c.A1 = rec;
        KingsField2.func_80021E10(c, m);
    }

    static void Number(CpuContext c, IMemory m, int x, int y, int value)
    {
        uint rec = c.SP + Rec;
        m.WriteU16(rec, (ushort)x);
        m.WriteU16(rec + 2, (ushort)y);
        m.WriteU32(c.SP + 0x10u, rec + 4);
        c.A0 = (uint)value;
        c.A1 = 3;
        c.A2 = 0;
        c.A3 = 0;
        KingsField2.func_80022B20(c, m);
        c.A0 = NumberFont;
        c.A1 = rec;
        KingsField2.func_80021FCC(c, m);
    }

    static void Box(CpuContext c, IMemory m, int x, int y, int w, int h)
    {
        m.WriteU32(c.SP + 0x10u, 1);
        m.WriteU32(c.SP + 0x14u, 2);
        c.A0 = (uint)x;
        c.A1 = (uint)y;
        c.A2 = (uint)w;
        c.A3 = (uint)h;
        KingsField2.func_800222B8(c, m);
    }

    // ---- settings ----

    public void DrawSettings()
    {
        ImGui.TextWrapped($"On the Magic and attack-magic pages, press {Keys[_key].Name} to show or hide a panel "
                        + "describing the highlighted spell: its element, MP cost and what it does.");
        var view = RecompOne.Runtime.Runtime.View;
        if (ImGui.BeginCombo("Key", Keys[_key].Name))
        {
            for (int i = 0; i < Keys.Length; i++)
                if (ImGui.Selectable(Keys[i].Name, i == _key))
                {
                    _key = i;
                    view.SetString(KeyKey, Keys[i].Name);
                    RecompOne.Runtime.Runtime.SaveView();
                }
            ImGui.EndCombo();
        }
        if (ImGui.SliderInt("Panel X", ref _x, 0, 60)) { view.SetFloat(XKey, _x); RecompOne.Runtime.Runtime.SaveView(); }
        if (ImGui.SliderInt("Panel Y", ref _y, 0, 160)) { view.SetFloat(YKey, _y); RecompOne.Runtime.Runtime.SaveView(); }
        ImGui.TextDisabled("Descriptions written from the King's Field Wiki (kingsfield.fandom.com).");
    }
}
