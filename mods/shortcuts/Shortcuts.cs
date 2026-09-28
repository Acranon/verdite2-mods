using System;
using System.Text;
using System.Runtime.InteropServices;
using ImGuiNET;
using RecompOne.Runtime.Context;
using RecompOne.Runtime.Events;
using RecompOne.Runtime.Host;
using RecompOne.Runtime.Host.Window;
using RecompOne.Runtime.Memory;
using RecompOne.Runtime.Modding;
using Silk.NET.Input;
using Game = Recompiled.KingsField2_game;

namespace Kf2.Mods.Shortcuts;

public sealed class ShortcutsMod : IMod
{
    const uint Inventory = 0x8009B52C, Magic = 0x8019C5EC;
    const string Setting = "kf2.shortcuts.slot";
    // Existing bindings retain their IDs and now require confirmation.
    // 0x400 adds direct use; identity is shared across both modes.
    const int Direct = 0x400;
    // Equipment stores the target in bits 12..15: 7 weapon, 8 magic,
    // 9 Button item, 10 Button magic; 0..6 are native armor/accessory slots.
    const int Equipment = 0x300;
    const int EquipMagic = 0x8300, ButtonItem = 0x9300, ButtonMagic = 0xA300;
    static readonly int[] CategorySlots = { 7, -1, 4, 0, 1, 2, 3, 5, 6 };
    static readonly int[] EquipmentMin = { 21, 28, 41, 47, 34, 53, 53, 0, 0, 70, 0 };
    static readonly int[] EquipmentMax = { 27, 33, 46, 52, 40, 59, 59, 20, 13, 116, 19 };
    static readonly int[] Bindings = new int[9];
    static byte[] _question, _questionQuantity;
    static byte[] _notice;
    const int NoticeDuration = 2500, NoticeFade = 250;
    static long _noticeUntil;
    static int _menuDepth, _pageKind;
    static uint _pageDesc, _pageDrawReturn, _buttonRows, _buttonMagicIds;
    static int _held, _pressed, _kind;
    static uint _ids, _drawDesc;
    static bool _busy;
    static long _pressedAt;
    static uint _textSp, _textArg;
    static ushort[] _originalGlyphs;
    static int _glyphX, _glyphY;
    static readonly Action<VSyncEvent> OnFrame = _ => SampleKeys();
    static readonly Action<OverlayLoadedEvent> OnOverlay = e =>
    {
        if (e.Name is "open" or "game" or "end")
        {
            // A new executable owns its textures; never restore an old atlas
            // into it if the host reset while a menu was open.
            _originalGlyphs = null;
            ResetTransient();
        }
    };

    public void OnLoad()
    {
        for (int i = 0; i < 9; i++)
        {
            int.TryParse(RecompOne.Runtime.Runtime.View.GetString(Setting + (i + 1), "0"), out int value);
            Bindings[i] = ValidBinding(value) ? value : 0;
            if (IsEquipment(Bindings[i])) Bindings[i] |= Direct;
            for (int j = 0; j < i; j++)
                if (Identity(Bindings[j]) == Identity(Bindings[i])) Bindings[j] = 0;
        }
        ResetTransient();
        Event.AddListener(OnFrame);
        Event.AddListener(OnOverlay);
    }

    public void OnUnload()
    {
        Event.RemoveListener(OnFrame);
        Event.RemoveListener(OnOverlay);
        RestoreGlyphs();
        ResetTransient();
    }

    static int Identity(int value) => value & ~Direct;
    static bool IsEquipment(int value) => (Identity(value) & ~0xF0FF) == Equipment;
    static bool ValidBinding(int value) => value == 0 ||
        ((Identity(value) & ~255) == 0x100 && (value & 255) >= 67 && (value & 255) < 120) ||
        ((Identity(value) & ~255) == 0x200 && (value & 255) >= 14 && (value & 255) < 20) ||
        (IsEquipment(value) && (value >> 12) < EquipmentMin.Length && (value & 255) >= EquipmentMin[value >> 12] &&
            (value & 255) <= EquipmentMax[value >> 12]);
    static int FindSlot(int binding) => Array.FindIndex(Bindings, b => Identity(b) == binding);

    // Silk's GLFW backend polls physical keys, not the characters produced by
    // the active keyboard layout. Number1 is the &/1 key on French AZERTY.
    static int ReadKeys()
    {
        int result = 0;
        for (int i = 0; i < 9; i++)
            if (HostWindow.IsKeyDown((Key)((int)Key.Number1 + i)) ||
                HostWindow.IsKeyDown((Key)((int)Key.Keypad1 + i))) result |= 1 << i;
        return result;
    }

    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr window, out uint pid);
    static bool InputAllowed()
    {
        if (OperatingSystem.IsWindows())
        {
            GetWindowThreadProcessId(GetForegroundWindow(), out uint pid);
            if (pid != (uint)Environment.ProcessId) return false;
        }
        return !PopupManager.AnyOpen && ImGui.GetCurrentContext() != IntPtr.Zero &&
            !ImGui.GetIO().WantTextInput;
    }

    static void SampleKeys()
    {
        int down = ReadKeys();
        LatchKeys(down, InputAllowed() && !_busy, Environment.TickCount64);
    }
    static void LatchKeys(int down, bool allowed, long now)
    {
        if (!allowed || now - _pressedAt > 250) _pressed = 0;
        int edge = down & ~_held;
        if (allowed && edge != 0) { _pressed |= edge; _pressedAt = now; }
        _held = down;
    }

    static void ResetTransient()
    {
        _held = ReadKeys();
        _pressed = _kind = _pageKind = 0;
        _ids = _drawDesc = _textSp = 0;
        _pageDesc = _pageDrawReturn = _buttonRows = _buttonMagicIds = 0;
        _busy = false;
        _question = null;
        _questionQuantity = null;
        _notice = null;
        _menuDepth = 0;
    }

    static void ClearPage()
    {
        RestoreGlyphs();
        _kind = _pageKind = 0;
        _ids = _drawDesc = 0;
        _pageDesc = _pageDrawReturn = _buttonRows = _buttonMagicIds = 0;
    }

    static void CapturePage(CpuContext c, int kind, uint drawReturn)
    {
        ClearPage();
        _pageKind = kind;
        _ids = c.A3;
        // All five native pages build their private list descriptor at SP+24.
        _pageDesc = c.SP + 24;
        _pageDrawReturn = drawReturn;
        _pressed = 0;
    }
    [PreHook("game", Address = 0x80018E80)]
    static bool EnterMenu(CpuContext c, IMemory m) { ClearPage(); _menuDepth++; return true; }
    [PostHook("game", Address = 0x80018E80)]
    static void LeaveMenu(CpuContext c, IMemory m)
    { ClearPage(); _pressed = 0; _menuDepth = Math.Max(0, _menuDepth - 1); }

    // Recognise native callers at the two shared builders instead of hooking
    // entry and exit of every page. Keep their live arrays, so other mods can
    // sort the rows after the builder without invalidating our item identities.
    [PreHook("game", Address = 0x80019444)]
    static bool ItemList(CpuContext c, IMemory m)
    {
        if (c.RA == 0x80019260) CapturePage(c, 0x100, 0x800193DC);
        else if (c.RA == 0x8001A824)
        {
            int category = (int)c.S6; // retained by the native equipment page
            int kind = category >= 0 && category < CategorySlots.Length && CategorySlots[category] >= 0
                ? Equipment | (CategorySlots[category] << 12) : 0;
            CapturePage(c, kind, 0x8001A9E0);
        }
        else if (c.RA == 0x8001AE54)
        {
            CapturePage(c, ButtonItem, 0x8001B050);
            _buttonRows = c.A1;
        }
        else ClearPage();
        return true;
    }
    [PreHook("game", Address = 0x8001A1E8)]
    static bool MagicList(CpuContext c, IMemory m)
    {
        if (c.RA == 0x8001A09C) CapturePage(c, 0x200, 0x8001A19C);
        else if (c.RA == 0x8001AB9C) CapturePage(c, EquipMagic, 0x8001AD34);
        else if (c.RA == 0x8001AEA0 && _pageKind == ButtonItem && c.A1 >= _buttonRows)
            // Button appends spell names after its 24-byte item-name records.
            _buttonMagicIds = c.A3 - (c.A1 - _buttonRows) / 24;
        else ClearPage();
        return true;
    }
    static int RowBinding(IMemory m, int row)
    {
        int id = m.ReadU8(_ids + (uint)row);
        if (_kind == ButtonItem && id == 255 && _buttonMagicIds != 0)
            return ButtonMagic | m.ReadU8(_buttonMagicIds + (uint)row);
        return _kind | id;
    }

    // Called at the next draw, after all keyboard/mouse cursor hooks finished.
    static void BindHighlighted(IMemory m)
    {
        int pressed = _pressed;
        _pressed = 0;
        if (_kind == 0 || _ids == 0 || _drawDesc == 0 || !InputAllowed()) return;
        int row = m.ReadU8(_drawDesc + 0x21);
        if (row >= m.ReadU8(_drawDesc + 0x1E)) return;
        int binding = RowBinding(m, row);
        if (!ValidBinding(binding)) return;
        for (int i = 0; i < 9; i++)
        {
            if ((pressed & (1 << i)) == 0) continue;
            Toggle(i, binding);
            Persist();
            break;
        }
    }

    static void Toggle(int slot, int binding)
    {
        binding = Identity(binding);
        int next = IsEquipment(binding)
            ? (Identity(Bindings[slot]) == binding ? 0 : binding | Direct)
            : Identity(Bindings[slot]) != binding ? binding :
                (Bindings[slot] & Direct) == 0 ? binding | Direct : 0;
        for (int i = 0; i < 9; i++) if (Identity(Bindings[i]) == binding) Bindings[i] = 0;
        Bindings[slot] = next;
    }
    static void Persist()
    {
        for (int i = 0; i < 9; i++)
            RecompOne.Runtime.Runtime.View.SetString(Setting + (i + 1), Bindings[i].ToString());
        RecompOne.Runtime.Runtime.SaveView();
    }

    [PreHook("game", Address = 0x800209E0)]
    static bool BeforeListDraw(CpuContext c, IMemory m)
    {
        _kind = 0; _drawDesc = 0;
        if (_question != null) { DrawQuestion(c, m); return false; }
        bool interactive = c.RA == _pageDrawReturn;
        // Prompts reuse the page's list for display only. No key assignment
        // there, nor in unrelated lists that happen to reuse the stack address.
        if (_pageKind != 0 && c.A0 == _pageDesc && (interactive || c.RA == 0x80020880))
        {
            _kind = _pageKind; _drawDesc = c.A0;
            if (interactive) BindHighlighted(m);
        }
        _pressed = 0;
        return true;
    }
    [PostHook("game", Address = 0x800209E0)]
    static void AfterListDraw(CpuContext c, IMemory m) { _kind = 0; _drawDesc = 0; }

    // Only the name call inside the scrolling list. A private stack record
    // avoids changing item tables, save data, row strides or the native buffers.
    [PreHook("game", Address = 0x80021E10)]
    static bool BeforeText(CpuContext c, IMemory m)
    {
        if (_kind == 0 || _drawDesc == 0 || _ids == 0 || c.RA != 0x80020B24 || _textSp != 0) return true;
        int row = (m.ReadU16(c.A1 + 2) - m.ReadU8(_drawDesc + 0x1D) - 5) / 14;
        int index = m.ReadU8(_drawDesc + 0x20) + row;
        if (row < 0 || index >= m.ReadU8(_drawDesc + 0x1E)) return true;
        int binding = RowBinding(m, index);
        int slot = FindSlot(binding);
        if (slot < 0) return true;
        bool direct = (Bindings[slot] & Direct) != 0;
        if (!direct) EnsureGlyphs(m);
        uint source = c.A1;
        _textSp = c.SP;
        _textArg = source;
        c.SP -= 64;
        c.A1 = c.SP + 16;
        m.WriteU16(c.A1, m.ReadU16(source));
        m.WriteU16(c.A1 + 2, m.ReadU16(source + 2));
        uint p = c.A1 + 4;
        for (uint i = 0; i < 24; i++)
        {
            byte ch = m.ReadU8(source + 4 + i);
            if (ch == 0xFF) break;
            m.WriteU8(p++, ch);
        }
        m.WriteU8(p++, 0x7F);
        m.WriteU8(p++, direct ? (byte)0x33 : (byte)0x1A);
        m.WriteU8(p++, (byte)(0x20 + slot + 1));
        m.WriteU8(p++, direct ? (byte)0x33 : (byte)0x1B);
        m.WriteU8(p, 0xFF);
        return true;
    }

    // The menu font has digits at 0x20..0x29, but no parentheses. Its blank
    // slots 0x1A/0x1B receive two 8x15 glyphs for the duration of this menu.
    // Use the native GPU upload path so both VRAM and the backend cache agree.
    static void EnsureGlyphs(IMemory m)
    {
        if (_originalGlyphs != null || RecompOne.Runtime.Runtime.Gpu == null) return;
        int page = m.ReadU16(0x80064BF0);
        _glyphX = (page & 15) * 64 + 20; // column 10, 4 pixels per VRAM word
        _glyphY = ((page >> 4) & 1) * 256 + 15;
        _originalGlyphs = new ushort[60];
        var vram = RecompOne.Runtime.Runtime.Gpu.Vram;
        for (int y = 0; y < 15; y++)
            Array.Copy(vram, (_glyphY + y) * 1024 + _glyphX, _originalGlyphs, y * 4, 4);
        var pixels = new ushort[60];
        for (int y = 2; y <= 12; y++)
        {
            int x = y == 2 || y == 12 ? 4 : y == 3 || y == 11 ? 3 : 2;
            PutPixel(pixels, x, y, 15);
            PutPixel(pixels, x + 1, y, 1);
            PutPixel(pixels, 15 - x, y, 15);
            PutPixel(pixels, 14 - x, y, 1);
        }
        UploadGlyphs(pixels);
    }
    static void PutPixel(ushort[] pixels, int x, int y, int color)
    { pixels[y * 4 + x / 4] |= (ushort)(color << ((x % 4) * 4)); }
    static void UploadGlyphs(ushort[] pixels)
    {
        var gpu = RecompOne.Runtime.Runtime.Gpu;
        if (gpu == null) return;
        gpu.WriteGp0(0x01000000);
        gpu.WriteGp0(0xA0000000);
        gpu.WriteGp0((uint)(_glyphX | (_glyphY << 16)));
        gpu.WriteGp0(4 | (15u << 16));
        for (int i = 0; i < pixels.Length; i += 2)
            gpu.WriteGp0((uint)pixels[i] | ((uint)pixels[i + 1] << 16));
    }
    static void RestoreGlyphs()
    {
        if (_originalGlyphs == null) return;
        UploadGlyphs(_originalGlyphs);
        _originalGlyphs = null;
    }
    [PostHook("game", Address = 0x80021E10)]
    static void AfterText(CpuContext c, IMemory m)
    {
        if (_textSp == 0) return;
        c.SP = _textSp;
        c.A1 = _textArg;
        _textSp = 0;
    }

    // This routine is called only on the native interactive world path. Menus,
    // shops, loading and death loops do not enter it. Never launch from VSync:
    // maps and some item actions run their own VSync/modal loops.
    [PreHook("game", Address = 0x80029CBC)]
    static bool WorldInput(CpuContext c, IMemory m)
    {
        ClearPage(); // also restores temporary glyphs after a direct page call
        int pressed = _pressed;
        _pressed = 0;
        if (_busy || !InputAllowed() || m.ReadU16(0x80199428) == 0 ||
            m.ReadU8(0x801994E1) == 0x11 || (short)m.ReadU16(0x801994A4) != -1) return true;
        for (int i = 0; i < 9; i++)
        {
            int binding = Bindings[i];
            if ((pressed & (1 << i)) == 0 || binding == 0) continue;
            var saved = c.Snapshot();
            _busy = true;
            try { UseShortcut(c, m, binding); }
            finally { c.Restore(saved); _busy = false; _pressed = 0; _held = ReadKeys(); }
            break;
        }
        return true;
    }

    static void UseShortcut(CpuContext c, IMemory m, int binding)
    {
        if (!ValidBinding(binding) || binding == 0) return;
        if (IsEquipment(binding)) { Equip(c, m, binding); return; }
        int id = binding & 255;
        bool magic = (Identity(binding) & ~255) == 0x200;
        if (magic ? m.ReadU8(Magic + (uint)id * 26) != 1 : m.ReadU8(Inventory + (uint)id) == 0) return;
        if ((binding & Direct) != 0 || Confirm(c, m, binding)) Activate(c, m, Identity(binding));
    }

    static void Equip(CpuContext c, IMemory m, int binding)
    {
        int id = binding & 255, slot = binding >> 12;
        bool magic = slot == 8 || slot == 10;
        int count = magic ? (m.ReadU8(Magic + (uint)id * 26) == 1 ? 1 : 0) : m.ReadU8(Inventory + (uint)id);
        if (count == 0) return;
        // Native Item 1/Item 2 lists reserve copies worn in the other slot.
        // A shortcut must not create a second copy of the same accessory.
        if ((slot == 5 || slot == 6) && count < 2 &&
            m.ReadU8(slot == 5 ? 0x801994DAu : 0x801994D9u) == id) return;
        c.A0 = (uint)id;
        if (slot == 8) Game.SsSetAutoKeyOffMode(c, m); // native equipped spell, does not cast
        else if (slot == 9) Game.func_80025FA8(c, m); // assign Button item, does not consume
        else if (slot == 10) Game.func_80025F80(c, m); // assign Button magic, does not cast
        else if (slot == 7)
            Game.func_80026210(c, m); // native weapon model, animation and stats
        else
        {
            c.A1 = (uint)slot;
            Game.func_80025FD0(c, m); // native armor/accessory effects and stats
        }
        QueueNotice(m, id, magic);
    }

    static string EquipmentName(IMemory m, int id, bool magic = false)
    {
        var name = new StringBuilder();
        uint source = (magic ? 0x80066664u : 0x80065B24u) + (uint)id * 24;
        for (uint i = 0; i < 24; i++)
        {
            byte ch = m.ReadU8(source + i);
            if (ch == 255) break;
            name.Append(ch < 26 ? (char)('a' + ch) : ch >= 0x20 && ch <= 0x29 ? (char)('0' + ch - 0x20) :
                ch == 0x32 ? '\'' : ch == 0x33 ? '-' : ch == 0x30 ? '.' : ch == 0x7F ? ' ' : '?');
        }
        if (name.Length > 0) name[0] = char.ToUpperInvariant(name[0]);
        return name.ToString();
    }

    static void QueueNotice(IMemory m, int id, bool magic)
    {
        string text = ("Equipping " + EquipmentName(m, id, magic)).ToUpperInvariant();
        _notice = new byte[text.Length];
        for (int i = 0; i < text.Length; i++)
        {
            char ch = text[i];
            _notice[i] = ch >= 'A' && ch <= 'Z' ? (byte)(ch - 'A') : ch >= '0' && ch <= '9' ? (byte)(0x20 + ch - '0') :
                ch == '\'' ? (byte)0x32 : ch == '-' ? (byte)0x33 : ch == '.' ? (byte)0x30 : ch == ' ' ? (byte)0x7F : (byte)0x3A;
        }
        _noticeUntil = Environment.TickCount64 + NoticeDuration;
    }

    // The native pickup overlay ramps its RGB modulation from 0 to 100,
    // by 20 per 50 ms game tick. Interpolate the same ramp at render rate.
    static byte NoticeBrightness(long remaining)
    {
        long elapsed = NoticeDuration - remaining;
        if (remaining <= 0 || elapsed <= 0) return 0;
        return (byte)(100 * Math.Min(NoticeFade, Math.Min(elapsed, remaining)) / NoticeFade);
    }

    // Add the original game's font quads to the world ordering table immediately
    // before submission. No menu setup, present loop or host UI is involved.
    [PreHook("game", Address = 0x8002E0FC)]
    static bool DrawNotice(CpuContext c, IMemory m)
    {
        if (_notice == null) return true;
        long remaining = _noticeUntil - Environment.TickCount64;
        if (remaining <= 0) { _notice = null; return true; }
        if (c.RA != 0x800346AC || _menuDepth != 0 || _kind != 0 || _busy || _question != null) return true;
        byte brightness = NoticeBrightness(remaining);
        if (brightness == 0) return true;
        uint descriptor = m.ReadU32(0x8017E0A4);
        uint cursor = m.ReadU32(descriptor + 8), end = m.ReadU32(descriptor + 4);
        if (cursor > end || (uint)_notice.Length * 80 > end - cursor) return true;
        var saved = c.Snapshot();
        uint menuCursor = m.ReadU32(0x8006E914);
        try
        {
            m.WriteU32(0x8006E914, cursor);
            c.SP -= 64;
            uint record = c.SP + 16;
            int left = (320 - _notice.Length * 7) / 2;
            for (int offset = 0; offset < _notice.Length; offset += 24)
            {
                m.WriteU16(record, (ushort)(left + offset * 7));
                m.WriteU16(record + 2, 64); // below the native HP/MP panel
                int count = Math.Min(24, _notice.Length - offset);
                for (int i = 0; i < count; i++) m.WriteU8(record + 4 + (uint)i, _notice[offset + i]);
                m.WriteU8(record + 4 + (uint)count, 255);
                c.A0 = 0x80064BF0; c.A1 = record;
                Game.func_80021E10(c, m);
            }
            // Match the pickup renderer (80033E78 / 80033D24): subtract then
            // add the same semi-transparent texture. This reveals the world
            // as the modulation falls, instead of leaving opaque black letters.
            // The native font CLUT already marks its visible colors as STP.
            // Splice each added pass immediately after its subtraction pass in
            // the ordering table, retaining the original link and packet size.
            uint after = m.ReadU32(descriptor + 8);
            uint extra = after;
            for (uint packet = cursor; packet < after; packet += 40)
            {
                m.WriteU8(packet + 4, brightness);
                m.WriteU8(packet + 5, brightness);
                m.WriteU8(packet + 6, brightness);
                m.WriteU8(packet + 7, 0x2E);
                ushort page = (ushort)(m.ReadU16(packet + 22) & ~0x60);
                for (uint word = 0; word < 40; word += 4)
                    m.WriteU32(extra + word, m.ReadU32(packet + word));
                m.WriteU16(packet + 22, (ushort)(page | 0x40));
                m.WriteU16(extra + 22, (ushort)(page | 0x20));
                m.WriteU32(packet, (m.ReadU32(packet) & 0xFF000000) | (extra & 0xFFFFFF));
                extra += 40;
            }
            m.WriteU32(descriptor + 8, extra);
        }
        finally { m.WriteU32(0x8006E914, menuCursor); c.Restore(saved); }
        return true;
    }

    static byte[] MakeQuestion(IMemory m, int binding)
    {
        uint names = (Identity(binding) & ~255) == 0x200 ? 0x80066664u : 0x80065B24u;
        uint name = names + (uint)(binding & 255) * 24;
        int length = 0;
        while (length < 24 && m.ReadU8(name + (uint)length) != 255) length++;
        var result = new byte[length + 6];
        result[0] = 20; result[1] = 18; result[2] = 4; result[3] = 127; // USE
        for (int i = 0; i < length; i++) result[i + 4] = m.ReadU8(name + (uint)i);
        result[length + 4] = 127; result[length + 5] = 0x3A; // ?
        return result;
    }

    static bool Confirm(CpuContext c, IMemory m, int binding)
    {
        var saved = c.Snapshot();
        Game.func_80029C50(c, m);
        c.A0 = 1;
        Game.func_80022754(c, m);
        try
        {
            // Native modal pauses gameplay and handles keyboard, pad and mouse.
            // A private, empty descriptor keeps the host's menu hooks in bounds.
            c.SP -= 96;
            uint desc = c.SP + 16;
            for (uint i = 0; i < 64; i++) m.WriteU8(desc + i, 0);
            _question = MakeQuestion(m, binding);
            _questionQuantity = MakeQuantity(m, binding);
            Game.func_80022EFC(c, m);
            c.A0 = desc; c.A1 = 0; c.A2 = 1; c.A3 = 255;
            Game.func_800206E0(c, m);
            return c.V0 == 0;
        }
        finally
        {
            _question = null;
            _questionQuantity = null;
            c.A0 = 0;
            Game.func_800228C8(c, m);
            Game.func_80025D38(c, m);
            c.Restore(saved);
        }
    }

    static byte[] MakeQuantity(IMemory m, int binding)
    {
        if ((Identity(binding) & ~255) != 0x100) return null;
        int id = binding & 255;
        // Native consumption paths: 800197D4 (restoratives), 800474D0
        // (flask filling, temporary effects and learning elemental spells).
        if (!(id >= 71 && id <= 80 || id == 82 || id == 84 ||
              id == 86 || id == 87 || id >= 90 && id <= 94)) return null;
        string digits = m.ReadU8(Inventory + (uint)id).ToString(System.Globalization.CultureInfo.InvariantCulture);
        byte[] prefix = { 8, 13, 127, 18, 19, 14, 2, 10, 127 }; // IN STOCK
        var result = new byte[prefix.Length + digits.Length];
        Array.Copy(prefix, result, prefix.Length);
        for (int i = 0; i < digits.Length; i++) result[prefix.Length + i] = (byte)(0x20 + digits[i] - '0');
        return result;
    }

    static void DrawQuestion(CpuContext c, IMemory m)
    {
        DrawQuestionLine(c, m, _question, 19);
        if (_questionQuantity != null) DrawQuestionLine(c, m, _questionQuantity, 37);
    }

    static void DrawQuestionLine(CpuContext c, IMemory m, byte[] text, ushort y)
    {
        var saved = c.Snapshot();
        try
        {
            c.SP -= 64;
            uint record = c.SP + 16;
            // The native text routine caps a call at 24 characters. Continue
            // on the same line so even the longest names remain complete.
            for (int offset = 0; offset < text.Length; offset += 24)
            {
                m.WriteU16(record, (ushort)(31 + offset * 7));
                m.WriteU16(record + 2, y);
                int count = Math.Min(24, text.Length - offset);
                for (int i = 0; i < count; i++) m.WriteU8(record + 4 + (uint)i, text[offset + i]);
                m.WriteU8(record + 4 + (uint)count, 255);
                c.A0 = 0x80064BF0; c.A1 = record;
                Game.func_80021E10(c, m);
            }
        }
        finally { c.Restore(saved); }
    }

    [PreHook("game", Address = 0x80021478)]
    static bool ConfirmationButtons(CpuContext c, IMemory m)
    {
        if (_question == null) return true;
        // Leave one text line below the question. Use absolute coordinates so
        // repeated frames never move the choices farther down the screen.
        if (_questionQuantity != null)
        {
            m.WriteU16(c.A0 + 2, (ushort)(m.ReadU16(0x80064E26) + 18));
            m.WriteU16(c.A1 + 2, (ushort)(m.ReadU16(0x80064E42) + 18));
        }
        // These are the modal's private stack records, never shared game text.
        m.WriteU8(c.A0 + 4, 24); m.WriteU8(c.A0 + 5, 4); m.WriteU8(c.A0 + 6, 18); m.WriteU8(c.A0 + 7, 255); // YES
        m.WriteU8(c.A1 + 4, 13); m.WriteU8(c.A1 + 5, 14); m.WriteU8(c.A1 + 6, 255); // NO
        return true;
    }

    static void Activate(CpuContext c, IMemory m, int binding)
    {
        int id = binding & 255;
        if ((binding & ~255) == 0x200)
        {
            if (m.ReadU8(Magic + (uint)id * 26) != 1) return;
            c.A0 = (uint)id;
            Game.func_80027DC0(c, m); // native MP, casting and equipment restrictions
        }
        else
        {
            if (m.ReadU8(Inventory + (uint)id) == 0) return;
            if (id >= 67 && id <= 69)
            {
                Game.func_80029C50(c, m);
                c.A0 = 1;
                Game.func_80022754(c, m);
                try
                {
                    c.A0 = (uint)id;
                    Game.func_80019B54(c, m); // native Pirate's / Miner's / Necron's map
                }
                finally
                {
                    c.A0 = 0;
                    Game.func_800228C8(c, m);
                    Game.func_80025D38(c, m);
                }
            }
            else if (id >= 71 && id <= 80)
            {
                c.A0 = (uint)id;
                Game.func_800197D4(c, m); // apply effect and consume exactly once
            }
            else
            {
                c.A0 = 0;
                c.A1 = 0;
                Game.func_800342D8(c, m);
                c.A0 = 0x801994EC;
                c.A1 = 0x8019950C;
                c.A2 = (uint)id;
                Game.func_800474D0(c, m); // keys / crystals / other world items
                Game.func_80025D38(c, m);
            }
        }
    }

    public void DrawSettings()
    {
        if (ImGui.Button("Clear all shortcuts")) { Array.Clear(Bindings); Persist(); }
    }
}
