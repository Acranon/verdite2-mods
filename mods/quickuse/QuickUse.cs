// ModCompiler compiles mods with no implicit usings, so every namespace the
// file needs must be named here -- including System.
using System;
using System.Collections.Generic;
using System.Text;
using ImGuiNET;
using RecompOne.Runtime.Context;
using RecompOne.Runtime.Events;
using RecompOne.Runtime.Host;
using RecompOne.Runtime.Host.Window;
using RecompOne.Runtime.Memory;
using RecompOne.Runtime.Modding;
using Silk.NET.Input;
using KingsField2 = Recompiled.KingsField2_game;

namespace Kf2.Mods.QuickUse;

/// <summary>
/// A hotkey that opens the game's own Items page, with the items you pin at the top.
///
/// ---- the game already uses quest items from the Items page ----
///
/// The in-game menu is opened from `func_80029CBC`, and what it does with the
/// menu's answer is the whole feature:
///
///     r = func_80018E80();                  // the menu
///     if (r >= 0 && func_80029560(r) == 0)  // an item, not one used on yourself
///         func_800474D0(pos, facing, r);    // use it on what is in front of you
///
/// `func_80018E80` returns whatever a page returns other than -1, and the Items
/// page (`func_80019204`, page 0 of the jump table at 0x80011098) returns the id
/// you said Yes to. So a key picked there goes into the lock in front of you, a
/// crystal onto the pedestal, a vial into the fountain -- the game's own path, and
/// the long way round is only the navigating. Items 71-80 are used on yourself
/// inside the page (`func_800197D4`), and those close the menu too.
///
/// ---- what this mod adds ----
///
/// Opening: the menu opens on Circle just pressed (mask word 0x8006E56C against
/// the pad words at 0x80199554 / last frame's 0x80199556), so the hotkey sets
/// that press for one call of `func_80029CBC` -- every other condition the game
/// puts on opening the menu still applies. The top menu then asks
/// `func_8001EA14` (the fixed-list cursor) which page to open, through the
/// pointer in A2; the first time it asks after the hotkey, the answer is 0, the
/// Items page, without it running. Backing out of Items after that closes the
/// whole menu rather than dropping to the top list: the page's -1 ("back") is
/// turned into 0x80000000, which the top menu exits on (it is not -1, -2, -3, and
/// has no 0x1000 spell bit) and `func_80029CBC` ignores.
///
/// Sorting: `func_80019444` builds every item list from the counts at 0x8009B52C,
/// in id order, into three parallel arrays -- 0x18-byte names, u8 counts, u8 ids.
/// Called from the Items page (return address 0x80019260) the three are
/// reordered after it returns: pinned ids first, in pin order, then the rest as
/// the game had them.
///
/// Pinning: the page steps its cursor with `func_8001EB70(desc, ids, ...)` once a
/// frame (from 0x80019348). The pin key flips the highlighted id, re-sorts the
/// arrays (names at desc+0x24, counts at desc+0x2C) and moves the cursor to where
/// that item went: +0x21 the entry, +0x20 the first row shown, +0x22 their
/// difference, which must hold or the highlight lands on the wrong row.
/// </summary>
public sealed class QuickUseMod : IMod
{
    const uint PadNow = 0x80199554, PadLast = 0x80199556;
    const uint MenuMask = 0x8006E56C;          // u32, the button that opens the menu

    const uint AfterTopCursor = 0x80018FFC;    // func_80018E80 -> func_8001EA14
    const uint AfterItemsBuild = 0x80019260;   // func_80019204 -> func_80019444
    const uint AfterItemsStep = 0x80019348;    // func_80019204 -> func_8001EB70
    const uint CloseMenu = 0x80000000;

    const uint NameTable = 0x80065B24, NameStride = 0x18;
    const int ItemCount = 120;

    const string OpenKeyKey = "kf2.quickuse.open";
    const string PinKeyKey = "kf2.quickuse.pin";
    const string PinsKey = "kf2.quickuse.pins";
    const string SortAlwaysKey = "kf2.quickuse.sortalways";

    static readonly (string Name, Key Key)[] Keys =
    [
        ("R", Key.R), ("F", Key.F), ("T", Key.T), ("Z", Key.Z), ("X", Key.X), ("C", Key.C),
        ("V", Key.V), ("B", Key.B), ("I", Key.I), ("P", Key.P),
        ("1", Key.Number1), ("2", Key.Number2), ("3", Key.Number3), ("4", Key.Number4),
        ("F5", Key.F5), ("F6", Key.F6), ("F7", Key.F7), ("F8", Key.F8), ("F9", Key.F9),
        ("None", Key.Unknown),
    ];

    static int _openKey = 0;   // R
    static int _pinKey = 1;    // F
    static bool _sortAlways = true;
    static readonly List<int> _pins = [];

    // The hotkey, latched once a frame and good for a few frames: the routine
    // that opens the menu only runs on world ticks.
    static bool _openHeld;
    static int _openLatch;
    const int LatchFrames = 6;

    static bool _injected;      // Circle is pressed on the game's behalf this call
    static ushort _savedNow, _savedLast;
    static bool _jumpToItems;   // the next top-menu cursor call answers "Items"
    static bool _session;       // this menu was opened by the hotkey
    static bool _pinHeld;

    // The arrays func_80019444 is filling, from the call's arguments.
    static bool _building;
    static uint _rows, _counts, _ids;

    public void OnLoad()
    {
        var view = RecompOne.Runtime.Runtime.View;
        _openKey = IndexOf(view.GetString(OpenKeyKey, "R"), 0);
        _pinKey = IndexOf(view.GetString(PinKeyKey, "F"), 1);
        _sortAlways = view.GetBool(SortAlwaysKey, true);
        _pins.Clear();
        foreach (var s in view.GetString(PinsKey, "").Split(',', StringSplitOptions.RemoveEmptyEntries))
            if (int.TryParse(s, out int id) && id >= 0 && id < ItemCount && !_pins.Contains(id)) _pins.Add(id);
        Event.AddListener(_onVSync);
    }

    public void OnUnload() => Event.RemoveListener(_onVSync);

    static int IndexOf(string name, int fallback)
    {
        for (int i = 0; i < Keys.Length; i++)
            if (Keys[i].Name == name) return i;
        return fallback;
    }

    static readonly Action<VSyncEvent> _onVSync = _ =>
    {
        var key = Keys[_openKey].Key;
        bool down = key != Key.Unknown && HostWindow.IsKeyDown(key);
        if (down && !_openHeld) _openLatch = LatchFrames;
        else if (_openLatch > 0) _openLatch--;
        _openHeld = down;
    };

    // ---- opening ----

    [PreHook("game", Address = 0x80029CBC)]
    static bool BeforeMenuCheck(CpuContext c, IMemory m)
    {
        if (_openLatch == 0) return true;
        _openLatch = 0;

        ushort mask = (ushort)m.ReadU32(MenuMask);
        _savedNow = m.ReadU16(PadNow);
        _savedLast = m.ReadU16(PadLast);
        m.WriteU16(PadNow, (ushort)(_savedNow | mask));
        m.WriteU16(PadLast, (ushort)(_savedLast & ~mask));
        _injected = true;
        _jumpToItems = true;
        return true;
    }

    [PostHook("game", Address = 0x80029CBC)]
    static void AfterMenuCheck(CpuContext c, IMemory m)
    {
        if (_injected)
        {
            ushort mask = (ushort)m.ReadU32(MenuMask);
            m.WriteU16(PadNow, (ushort)((m.ReadU16(PadNow) & ~mask) | (_savedNow & mask)));
            m.WriteU16(PadLast, (ushort)((m.ReadU16(PadLast) & ~mask) | (_savedLast & mask)));
            _injected = false;
        }
        // The menu has come and gone (or the game would not open it just now).
        _jumpToItems = false;
        _session = false;
    }

    [PreHook("game", Address = 0x8001EA14)]
    static bool TopCursor(CpuContext c, IMemory m)
    {
        if (!_jumpToItems || c.RA != AfterTopCursor) return true;
        _jumpToItems = false;
        _session = true;
        m.WriteU32(c.A2, 0);   // the page to open: Items
        c.V0 = 0;              // and the top list's cursor, on it
        return false;
    }

    [PostHook("game", Address = 0x80019204)]
    static void AfterItemsPage(CpuContext c, IMemory m)
    {
        if (!_session || c.V0 != 0xFFFFFFFFu) return;
        // Backed out of a menu the hotkey opened: close it, with the menu's own
        // closing sound, rather than leaving you on the top list.
        c.V0 = CloseMenu;
        var snap = c.Snapshot();
        try
        {
            c.A0 = 0;
            KingsField2.func_80022DC4(c, m);
        }
        finally { c.Restore(snap); }
        c.V0 = CloseMenu;
    }

    // ---- sorting ----

    [PreHook("game", Address = 0x80019444)]
    static bool BeforeBuild(CpuContext c, IMemory m)
    {
        _building = c.RA == AfterItemsBuild && (_sortAlways || _session || _jumpToItems);
        if (_building) { _rows = c.A1; _counts = c.A2; _ids = c.A3; }
        return true;
    }

    [PostHook("game", Address = 0x80019444)]
    static void AfterBuild(CpuContext c, IMemory m)
    {
        if (!_building) return;
        _building = false;
        Sort(m, _rows, _counts, _ids, (int)c.V0);
    }

    static int Rank(int id)
    {
        int p = _pins.IndexOf(id);
        return p >= 0 ? p : 1000 + id;
    }

    static void Sort(IMemory m, uint rows, uint counts, uint ids, int n)
    {
        if (n <= 1 || n > ItemCount) return;
        var entries = new List<(int Id, byte Count, byte[] Row)>(n);
        for (int i = 0; i < n; i++)
        {
            var row = new byte[NameStride];
            for (uint b = 0; b < NameStride; b++) row[b] = m.ReadU8(rows + (uint)i * NameStride + b);
            entries.Add((m.ReadU8(ids + (uint)i), m.ReadU8(counts + (uint)i), row));
        }
        entries.Sort((a, b) => Rank(a.Id).CompareTo(Rank(b.Id)));
        for (int i = 0; i < n; i++)
        {
            m.WriteU8(ids + (uint)i, (byte)entries[i].Id);
            m.WriteU8(counts + (uint)i, entries[i].Count);
            for (uint b = 0; b < NameStride; b++) m.WriteU8(rows + (uint)i * NameStride + b, entries[i].Row[b]);
        }
    }

    // ---- pinning, in the page ----

    [PreHook("game", Address = 0x8001EB70)]
    static bool ItemsStep(CpuContext c, IMemory m)
    {
        if (c.RA != AfterItemsStep) return true;
        var key = Keys[_pinKey].Key;
        bool down = key != Key.Unknown && HostWindow.IsKeyDown(key);
        bool pressed = down && !_pinHeld;
        _pinHeld = down;
        if (!pressed) return true;

        uint desc = c.A0, ids = c.A1;
        int n = m.ReadU8(desc + 0x1E);
        int cur = m.ReadU8(desc + 0x21);
        if (n == 0 || cur >= n) return true;
        int id = m.ReadU8(ids + (uint)cur);

        bool pinned = !_pins.Remove(id);
        if (pinned) _pins.Add(id);
        SavePins();

        Sort(m, m.ReadU32(desc + 0x24), m.ReadU32(desc + 0x2C), ids, n);

        int at = 0;
        for (int i = 0; i < n; i++)
            if (m.ReadU8(ids + (uint)i) == id) { at = i; break; }
        int visible = Math.Max(1, (int)m.ReadU8(desc + 0x1F));
        int scroll = m.ReadU8(desc + 0x20);
        if (at < scroll) scroll = at;
        if (at >= scroll + visible) scroll = at - visible + 1;
        scroll = Math.Clamp(scroll, 0, Math.Max(0, n - visible));
        m.WriteU8(desc + 0x21, (byte)at);
        m.WriteU8(desc + 0x20, (byte)scroll);
        m.WriteU8(desc + 0x22, (byte)(at - scroll));

        ToastNotifications.ShowText("Quick Use", (pinned ? "Pinned " : "Unpinned ") + Name(m, id));
        return true;
    }

    static void SavePins()
    {
        RecompOne.Runtime.Runtime.View.SetString(PinsKey, string.Join(",", _pins));
        RecompOne.Runtime.Runtime.SaveView();
    }

    static string Name(IMemory m, int id)
    {
        if ((uint)id >= ItemCount) return "";
        uint rec = NameTable + (uint)id * NameStride;
        var sb = new StringBuilder();
        for (uint i = 0; i < NameStride; i++)
        {
            byte ch = m.ReadU8(rec + i);
            if (ch == 0xFF) break;
            sb.Append(ch < 26 ? (char)('A' + ch) : ch switch
            {
                0x31 => ",", 0x32 => "'", 0x38 => "!", 0x3A => "?", 0x7F => " ",
                _ => "",
            });
        }
        return sb.ToString().Trim();
    }

    // ---- settings ----

    public void DrawSettings()
    {
        ImGui.TextWrapped($"Press {Keys[_openKey].Name} to open the Items page directly. Pick an item and "
                        + "say Yes: keys, crystals and vials are used on whatever is in front of you, "
                        + "exactly as from the normal menu. Backing out closes the menu.");
        ImGui.TextWrapped($"In the Items page, {Keys[_pinKey].Name} pins or unpins the highlighted item. "
                        + "Pinned items stay at the top, in the order below.");
        ImGui.Separator();

        if (Combo("Open key", ref _openKey)) Persist(OpenKeyKey, Keys[_openKey].Name);
        if (Combo("Pin key", ref _pinKey)) Persist(PinKeyKey, Keys[_pinKey].Name);
        if (ImGui.Checkbox("Sort the Items page in the normal menu too", ref _sortAlways))
        {
            RecompOne.Runtime.Runtime.View.SetBool(SortAlwaysKey, _sortAlways);
            RecompOne.Runtime.Runtime.SaveView();
        }

        ImGui.Separator();
        ImGui.Text("Pinned items");
        if (_pins.Count == 0) ImGui.TextDisabled("None yet.");
        var mem = RecompOne.Runtime.Runtime.Mem;
        for (int i = 0; i < _pins.Count; i++)
        {
            ImGui.PushID(i);
            if (ImGui.ArrowButton("up", ImGuiDir.Up) && i > 0)
            {
                (_pins[i - 1], _pins[i]) = (_pins[i], _pins[i - 1]);
                SavePins();
            }
            ImGui.SameLine();
            if (ImGui.ArrowButton("down", ImGuiDir.Down) && i < _pins.Count - 1)
            {
                (_pins[i + 1], _pins[i]) = (_pins[i], _pins[i + 1]);
                SavePins();
            }
            ImGui.SameLine();
            bool remove = ImGui.SmallButton("Unpin");
            ImGui.SameLine();
            string name = mem != null ? Name(mem, _pins[i]) : "";
            ImGui.Text(name.Length > 0 ? name : $"item {_pins[i]}");
            ImGui.PopID();
            if (remove) { _pins.RemoveAt(i); SavePins(); break; }
        }
    }

    static bool Combo(string label, ref int index)
    {
        bool changed = false;
        if (ImGui.BeginCombo(label, Keys[index].Name))
        {
            for (int i = 0; i < Keys.Length; i++)
                if (ImGui.Selectable(Keys[i].Name, i == index))
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
