// ModCompiler compiles mods with no implicit usings, so every namespace the
// file needs must be named here -- including System.
using System;
using System.Collections.Generic;
using ImGuiNET;
using Recompiled;
using RecompOne.Runtime.Context;
using RecompOne.Runtime.Memory;
using RecompOne.Runtime.Modding;
using KingsField2 = Recompiled.KingsField2_game;

namespace Kf2.Mods.GearCompare;

/// <summary>
/// The status screen's numbers, before and after, on the equip prompt.
///
/// ---- what is being compared ----
///
/// `func_800244CC` rebuilds STR POWER, MAG POWER and the seventeen offense and
/// defense ratings (0x8019943C..0x80199466) from the equipment, and reads
/// nothing else that a menu can change: the weapon's item id at 0x801994AF,
/// looked up at 0x801C7FBC + id*0x44, and seven armour ids at
/// 0x801994D4..0x801994DA, each at 0x801D9A2C + id*0x20 through func_800243B0.
/// So the "after" column is: put the candidate's id in its slot byte, run
/// func_800244CC, read the nineteen words, then put the slot byte and the
/// nineteen words back. The real equip calls (func_80026210, func_80025FD0)
/// are never made -- the weapon one also loads a model and plays a sound.
///
/// ---- where the candidate comes from ----
///
/// `func_8001A6E8(kind)` is the equipment page. `kind` picks the id range and,
/// through the jump table at 0x800110E0, the slot the "Yes" arm writes:
///
///     kind 0  ids 0x00..0x14  weapon     0x801994AF
///     kind 3  ids 0x15..0x1B             0x801994D4
///     kind 4  ids 0x1C..0x21             0x801994D5
///     kind 2  ids 0x22..0x28             0x801994D6
///     kind 5  ids 0x29..0x2E             0x801994D7
///     kind 6  ids 0x2F..0x34             0x801994D8
///     kind 7  ids 0x35..0x3B  accessory  0x801994D9
///     kind 8  ids 0x35..0x3B  accessory  0x801994DA
///
/// and it opens the prompt as `func_800206E0(desc, 5, 5, id)` -- the fourth
/// argument is the highlighted item's id, 0xFF on the list's last row, which is
/// "take it off".
///
/// ---- drawing ----
///
/// The prompt is a modal loop that draws each frame as func_80022530 (frame
/// head), func_8002156C, func_800209E0, func_80021478 (the prompt's own boxes),
/// func_800226A8 (present). Drawing after func_80021478 puts the panel into the
/// same frame. The primitives are the status screen's own (func_8001FB4C):
/// func_80021E10(0x80064BF0, rec) for a label, func_80022B20(value, width, 0, 0,
/// digits) + func_80021FCC(0x80064BE4, rec) for a number, where rec is s16 x,
/// s16 y and then the text in font indices (A = 0, space = 0x7F, 0xFF ends it);
/// and func_800222B8(x, y, w, h, 1, 2) for the window, drawn *after* the text
/// as the status screen does, so the ordering table puts it underneath.
/// </summary>
public sealed class GearCompareMod : IMod
{
    const uint TextFont   = 0x80064BF0;
    const uint NumberFont = 0x80064BE4;

    const uint StatsStart = 0x8019943C;   // STR POWER .. the last defense word
    const uint StatsEnd   = 0x80199468;

    static readonly (string Label, uint Addr, int Group)[] Stats =
    [
        ("STR POWER", 0x8019943C, 0),
        ("MAG POWER", 0x8019943E, 0),
        ("SLASH", 0x80199444, 1), ("CHOP", 0x80199446, 1), ("STAB", 0x80199448, 1),
        ("HOLY", 0x8019944A, 1), ("FIRE", 0x8019944C, 1), ("EARTH", 0x8019944E, 1),
        ("WIND", 0x80199450, 1), ("WATER", 0x80199452, 1),
        ("SLASH", 0x80199456, 2), ("CHOP", 0x80199458, 2), ("STAB", 0x8019945A, 2),
        ("POISON", 0x8019945C, 2), ("DARK", 0x8019945E, 2), ("FIRE", 0x80199460, 2),
        ("EARTH", 0x80199462, 2), ("WIND", 0x80199464, 2), ("WATER", 0x80199466, 2),
    ];

    static readonly string[] GroupHeads = ["", "OFFENSE", "DEFENSE"];

    static int _kind = -1;        // the equipment page's kind while it is open
    static bool _active;          // an equip prompt is on screen
    static readonly List<(string Text, int Now, int New, bool Head)> _rows = [];

    static int _panelX = 99, _panelY = 40;
    static int _shopX = 92, _shopY = 78;
    static bool _fromShop;   // the open prompt is a shop's
    static int _rowHeight = 13;
    const string XKey = "kf2.gearcompare.x", YKey = "kf2.gearcompare.y", RowKey = "kf2.gearcompare.row";

    public void OnLoad()
    {
        var view = RecompOne.Runtime.Runtime.View;
        _panelX = (int)view.GetFloat(XKey, 99f);
        _shopX = (int)view.GetFloat("kf2.gearcompare.shopx", 92f);
        _shopY = (int)view.GetFloat("kf2.gearcompare.shopy", 78f);
        _panelY = (int)view.GetFloat(YKey, 40f);
        _rowHeight = Math.Clamp((int)view.GetFloat(RowKey, 13f), 11, 16);
    }

    static uint? SlotFor(int kind) => kind switch
    {
        0 => 0x801994AF,
        3 => 0x801994D4,
        4 => 0x801994D5,
        2 => 0x801994D6,
        5 => 0x801994D7,
        6 => 0x801994D8,
        7 => 0x801994D9,
        8 => 0x801994DA,
        _ => null,
    };

    [PreHook("game", Address = 0x8001A6E8)]
    static bool EnterEquipPage(CpuContext c, IMemory m)
    {
        _kind = (int)c.A0;
        return true;
    }

    [PostHook("game", Address = 0x8001A6E8)]
    static void LeaveEquipPage(CpuContext c, IMemory m)
    {
        _kind = -1;
        _active = false;
    }

    // ---- the shops ----
    //
    // The three buy pages -- func_8001D6BC, func_8001DF5C, func_8001E45C, each
    // listing a static stock table (0x80066844, 0x80066A24, 0x80066A9C) --
    // open the same prompt with the item id in A3, as the equipment page does.
    // Which slot it would take follows from the id's range; a ring goes in an
    // empty ring slot if there is one. (func_8001DD34, the sell page, lists your
    // own inventory and is left alone.)
    static int _shop;

    [PreHook("game", Address = 0x8001D6BC)] static bool Shop1In(CpuContext c, IMemory m) { _shop++; return true; }
    [PostHook("game", Address = 0x8001D6BC)] static void Shop1Out(CpuContext c, IMemory m) { Leave(); }
    [PreHook("game", Address = 0x8001DF5C)] static bool Shop2In(CpuContext c, IMemory m) { _shop++; return true; }
    [PostHook("game", Address = 0x8001DF5C)] static void Shop2Out(CpuContext c, IMemory m) { Leave(); }
    [PreHook("game", Address = 0x8001E45C)] static bool Shop3In(CpuContext c, IMemory m) { _shop++; return true; }
    [PostHook("game", Address = 0x8001E45C)] static void Shop3Out(CpuContext c, IMemory m) { Leave(); }

    static void Leave()
    {
        if (_shop > 0) _shop--;
        _active = false;
    }

    static uint? SlotForItem(IMemory m, int id) => id switch
    {
        <= 0x14 => 0x801994AF,
        <= 0x1B => 0x801994D4,
        <= 0x21 => 0x801994D5,
        <= 0x28 => 0x801994D6,
        <= 0x2E => 0x801994D7,
        <= 0x34 => 0x801994D8,
        <= 0x3B => m.ReadU8(0x801994D9) == 0xFF ? 0x801994D9
                 : m.ReadU8(0x801994DA) == 0xFF ? 0x801994DA : 0x801994D9,
        _ => null,
    };

    [PreHook("game", Address = 0x800206E0)]
    static bool PromptOpens(CpuContext c, IMemory m)
    {
        _active = false;
        uint slot;
        if (_kind >= 0 && SlotFor(_kind) is uint s) slot = s;
        else if (_shop > 0 && SlotForItem(m, (int)(c.A3 & 0xFF)) is uint t) slot = t;
        else return true;

        byte candidate = (byte)(c.A3 & 0xFF);
        var saved = c.Snapshot();
        try
        {
            Compute(c, m, slot, candidate);
            _active = true;
            _fromShop = _kind < 0;
        }
        catch (Exception e)
        {
            Console.WriteLine($"[KF2] gearcompare: {e.Message}");
        }
        finally
        {
            c.Restore(saved);
        }
        return true;
    }

    [PostHook("game", Address = 0x800206E0)]
    static void PromptCloses(CpuContext c, IMemory m) => _active = false;

    static void Compute(CpuContext c, IMemory m, uint slot, byte candidate)
    {
        var now = new int[Stats.Length];
        for (int i = 0; i < Stats.Length; i++) now[i] = m.ReadU16(Stats[i].Addr);

        byte oldSlot = m.ReadU8(slot);
        var block = new byte[StatsEnd - StatsStart];
        for (uint i = 0; i < block.Length; i++) block[i] = m.ReadU8(StatsStart + i);

        var after = new int[Stats.Length];
        try
        {
            m.WriteU8(slot, candidate);
            KingsField2.func_800244CC(c, m);
            for (int i = 0; i < Stats.Length; i++) after[i] = m.ReadU16(Stats[i].Addr);
        }
        finally
        {
            m.WriteU8(slot, oldSlot);
            for (uint i = 0; i < block.Length; i++) m.WriteU8(StatsStart + i, block[i]);
        }

        _rows.Clear();
        int group = -1;
        for (int i = 0; i < Stats.Length; i++)
        {
            if (now[i] == after[i]) continue;
            if (Stats[i].Group != group)
            {
                if (group > 0) AddTotal(group, now, after);
                group = Stats[i].Group;
                if (GroupHeads[group].Length > 0) _rows.Add((GroupHeads[group], 0, 0, true));
            }
            _rows.Add((Stats[i].Label, now[i], after[i], false));
        }
        if (group > 0) AddTotal(group, now, after);
    }

    /// <summary>
    /// The sum of the whole group -- all eight offense or nine defense words,
    /// not only the rows shown. Only a rough guide: a hit is scored per damage
    /// type against the target's defense in that same type, each type that has
    /// any attack gets STR POWER added, and the per-type result grows with the
    /// square of (attack + STR POWER) over the target's defense (func_8003A94C,
    /// summed eight times in func_8003A9CC).
    /// </summary>
    static void AddTotal(int group, int[] now, int[] after)
    {
        int n = 0, a = 0;
        for (int i = 0; i < Stats.Length; i++)
            if (Stats[i].Group == group) { n += now[i]; a += after[i]; }
        _rows.Add(("TOTAL", n, a, false));
    }

    // ---- drawing, in the game's own primitives ----

    const int LabelX = 8, NowX = 92, NewX = 128, Width = 170, Pad = 6;

    // The screen the panel shares, in the PS1's 320x240: the EQUIPMENT title
    // ends at about y 36 and the item list's window starts at about y 160
    // (measured off screenshots). A panel that would reach the list slides up
    // toward the title first, and only overlaps the list if it is taller than
    // the whole gap.
    const int TopLimit = 38, BottomLimit = 158;

    [PostHook("game", Address = 0x80021478)]
    static void AfterPromptDrawn(CpuContext c, IMemory m)
    {
        if (!_active) return;
        if (_fromShop) { DrawCompact(c, m); return; }
        var saved = c.Snapshot();
        try
        {
            c.SP -= 0x80u;
            int x = _panelX;

            // The column heads share the first line with the first group's
            // heading when there is one, which saves a row.
            bool headFirst = _rows.Count > 0 && _rows[0].Head;
            int lines = 1 + (_rows.Count == 0 ? 1 : _rows.Count - (headFirst ? 1 : 0));
            int height = Pad + lines * _rowHeight + Pad;

            int top = _panelY;
            if (top + height > BottomLimit) top = Math.Max(TopLimit, BottomLimit - height);
            int y = top + Pad;

            if (headFirst) Text(c, m, x + LabelX, y, _rows[0].Text);
            Text(c, m, x + NowX - 4, y, "NOW");
            Text(c, m, x + NewX - 4, y, "NEW");
            y += _rowHeight;

            if (_rows.Count == 0)
            {
                Text(c, m, x + LabelX, y, "NO CHANGE");
                y += _rowHeight;
            }
            for (int i = headFirst ? 1 : 0; i < _rows.Count; i++)
            {
                var r = _rows[i];
                if (r.Head)
                {
                    Text(c, m, x + LabelX, y, r.Text);
                }
                else
                {
                    Text(c, m, x + LabelX + 8, y, r.Text);
                    Number(c, m, x + NowX, y, r.Now);
                    Number(c, m, x + NewX, y, r.New);
                }
                y += _rowHeight;
            }

            // The window last, so it lands underneath (see the summary).
            Box(c, m, x, top, Width, y - top + Pad);
        }
        catch (Exception e)
        {
            Console.WriteLine($"[KF2] gearcompare draw: {e.Message}");
            _active = false;
        }
        finally
        {
            c.Restore(saved);
        }
    }

    // ---- the shops' compact layout ----
    //
    // A shop screen has GOLD and NUMBER top right, the stock list across the
    // whole bottom and the prompt on the left: about 80 lines free in between,
    // measured off a screenshot, where an armour's nine defense rows need twice
    // that. So in a shop the rows flow into two columns with three-letter names,
    // and a group heading is dropped when only one group changed.

    static string Short(string label) => label switch
    {
        "STR POWER" => "STR", "MAG POWER" => "MAG", "SLASH" => "SLA", "CHOP" => "CHP",
        "STAB" => "STB", "HOLY" => "HLY", "FIRE" => "FIR", "EARTH" => "ERT",
        "WIND" => "WND", "WATER" => "WTR", "POISON" => "PSN", "DARK" => "DRK",
        "TOTAL" => "TOT", "OFFENSE" => "OFF", "DEFENSE" => "DEF",
        _ => label.Length > 3 ? label[..3] : label,
    };

    const int CellW = 84, CNow = 30, CNew = 56, CRow = 12;

    static void DrawCompact(CpuContext c, IMemory m)
    {
        var saved = c.Snapshot();
        try
        {
            c.SP -= 0x80u;
            var cells = new List<(string Text, int Now, int New, bool Head)>();
            int heads = 0;
            foreach (var r in _rows) if (r.Head) heads++;
            foreach (var r in _rows)
                if (!(r.Head && heads == 1)) cells.Add(r);

            int perCol = Math.Max(1, (cells.Count + 1) / 2);
            int cols = cells.Count > perCol ? 2 : 1;
            int x = _shopX, top = _shopY;
            int y0 = top + Pad;

            for (int k = 0; k < cols; k++)
            {
                Text(c, m, x + LabelX + k * CellW + CNow - 4, y0, "NOW");
                Text(c, m, x + LabelX + k * CellW + CNew - 4, y0, "NEW");
            }

            if (cells.Count == 0)
                Text(c, m, x + LabelX, y0 + CRow, "NO CHANGE");

            for (int i = 0; i < cells.Count; i++)
            {
                var r = cells[i];
                int cx = x + LabelX + (i / perCol) * CellW;
                int cy = y0 + CRow * (1 + i % perCol);
                Text(c, m, cx, cy, Short(r.Text));
                if (!r.Head)
                {
                    Number(c, m, cx + CNow, cy, r.Now);
                    Number(c, m, cx + CNew, cy, r.New);
                }
            }

            int lines = 1 + Math.Max(1, cells.Count == 0 ? 1 : perCol);
            Box(c, m, x, top, LabelX * 2 + cols * CellW - 6, Pad * 2 + lines * CRow);
        }
        catch (Exception e)
        {
            Console.WriteLine($"[KF2] gearcompare shop draw: {e.Message}");
            _active = false;
        }
        finally
        {
            c.Restore(saved);
        }
    }

    // A record at sp+0x20: s16 x, s16 y, text. func_80022B20's digits go at
    // sp+0x24, which is that record's text, exactly as the status screen does.
    const uint Rec = 0x20;

    static void Text(CpuContext c, IMemory m, int x, int y, string s)
    {
        uint rec = c.SP + Rec;
        m.WriteU16(rec, (ushort)x);
        m.WriteU16(rec + 2, (ushort)y);
        uint p = rec + 4;
        foreach (char ch in s)
            m.WriteU8(p++, ch == ' ' ? (byte)0x7F : (byte)(char.ToUpperInvariant(ch) - 'A'));
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

    public void DrawSettings()
    {
        DrawShopSettings();
        ImGui.TextWrapped("On the equipment screen, the Yes/No prompt shows every stat that would "
                        + "change -- what it is now and what it would be.");
        ImGui.Separator();
        if (ImGui.SliderInt("Panel X", ref _panelX, 0, 150))
            Persist(XKey, _panelX);
        if (ImGui.SliderInt("Panel Y", ref _panelY, 0, 120))
            Persist(YKey, _panelY);
        if (ImGui.SliderInt("Row spacing", ref _rowHeight, 11, 16))
            Persist(RowKey, _rowHeight);
        ImGui.TextDisabled("A tall panel slides up toward the title before it covers the item list.");
    }

    static void DrawShopSettings()
    {
        if (ImGui.SliderInt("Shop panel X", ref _shopX, 0, 200)) Persist("kf2.gearcompare.shopx", _shopX);
        if (ImGui.SliderInt("Shop panel Y", ref _shopY, 0, 160)) Persist("kf2.gearcompare.shopy", _shopY);
        ImGui.Separator();
    }

    static void Persist(string key, int value)
    {
        RecompOne.Runtime.Runtime.View.SetFloat(key, value);
        RecompOne.Runtime.Runtime.SaveView();
    }
}
