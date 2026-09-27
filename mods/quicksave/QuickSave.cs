// ModCompiler compiles mods with no implicit usings, so every namespace the
// file needs must be named here -- including System.
using System;
using System.IO;
using ImGuiNET;
using Recompiled;
using RecompOne.Runtime.Context;
using RecompOne.Runtime.Events;
using RecompOne.Runtime.Hardware;
using RecompOne.Runtime.Host;
using RecompOne.Runtime.Host.Window;
using RecompOne.Runtime.Memory;
using RecompOne.Runtime.Modding;
using Silk.NET.Input;
using KingsField2 = Recompiled.KingsField2_game;

namespace Kf2.Mods.QuickSave;

/// <summary>
/// Save anywhere, load it back: a save state built out of the game's own save
/// format rather than a snapshot of the machine.
///
/// A save point (`func_800489FC`'s kind-0x0E arm) runs `func_800492B8(area)` to
/// pack the entity table, and the card write `func_80023764` then fills the
/// 0x4000-byte buffer at `*(u32*)0x8006E98C`: zeroed, `func_80049A88(buf+0x400)`
/// packs the game state into it, `func_80023DD0` checksums it. The load
/// `func_80023638` reads the card into the same buffer and unpacks it with
/// `func_8004A040(buf+0x400)`. Pack and unpack cover the same globals,
/// including the position at `0x801994EC`, so the save puts you where you stood.
///
/// So a quick save is the packer without the card, and the block goes to a file:
///
///     save   func_800492B8(area); zero buf; func_80049A88(buf+0x400); copy out
///     load   copy in; func_8004A040(buf+0x400); then the -3 arm of
///            func_80029CBC ("the menu loaded a save"), as patches/AutoReload.cs
///            transcribes it -- func_800240B8, func_80024154(area x5, 0xFF),
///            func_80025D38 -- which re-enters the area, restarts the music and
///            re-equips, exactly as a load from the menu does.
///
/// The buffer is borrowed, so its old contents are put back afterwards. Both
/// run from the end of stage 3 (`func_8002A550`), where AutoReload loads from,
/// because the area entry waits on the CD through VSync.
/// </summary>
public sealed class QuickSaveMod : IMod
{
    const string CodeVersion = "2.1";

    const uint BufPtr     = 0x8006E98C;   // u32 -> the 0x4000-byte save buffer
    const uint BufSize    = 0x4000;
    const uint DataOffset = 0x400;        // what pack/unpack read and write
    const uint Area       = 0x8017E060;   // u8, the current area (buf0)
    const uint State      = 0x801994E1;   // u8, player action state
    const byte StateDead  = 0x11;
    const uint MaxHp      = 0x80199426;   // u16, zero until a character exists
    const uint Hp         = 0x80199428;   // u16

    // Version 1 is the packed block alone; version 2 appends the five resource
    // slots that were loaded (see Slots).
    static readonly byte[] MagicV1 = "KF2QS\u0001"u8.ToArray();
    static readonly byte[] Magic = "KF2QS\u0002"u8.ToArray();

    // The loader's current resource slots, one byte each: slot 0 is the area,
    // 1..4 the rest of what 0x800162DC was last asked for. Re-entering with the
    // area in all five (what the menu's load does) loads an area's defaults,
    // and the Ant Nest came back textured like the Barracks -- so a quick save
    // keeps the five that were really loaded and asks for those.
    const uint Slots = 0x8017E060;
    const int SlotCount = 5;

    // Restoring the slots by asking for them all at once fixed the Ant Nest's
    // textures, but after a restart soldiers and archers loaded as nothing --
    // confirmed in play, twice each way. A region is not entered that way: an
    // object in the area (stepped by func_80037C0C) holds five slot values at
    // rec+0x3A..0x3E and three more arguments at rec+0x40..0x42, and calls
    // 0x800162DC with exactly those when you stand on it. So a load now asks
    // for the area's defaults, as the game's own menu load does, and then
    // replays the trigger whose slots match the save's (ReplayRegion).
    static bool _restoreSlots = false;
    const string RestoreSlotsKey = "kf2.quicksave.restoreslots";

    const string SaveKeyKey = "kf2.quicksave.savekey";
    const string LoadKeyKey = "kf2.quicksave.loadkey";
    const string SlotKey    = "kf2.quicksave.slot";

    static readonly (string Name, Key Key)[] Keys =
    [
        ("F2", Key.F2), ("F3", Key.F3), ("F4", Key.F4), ("F5", Key.F5),
        ("F6", Key.F6), ("F7", Key.F7), ("F8", Key.F8), ("F9", Key.F9),
        ("F10", Key.F10), ("F12", Key.F12),
    ];

    static int _saveKey = 0;   // F2
    static int _loadKey = 2;   // F4
    static int _slot = 1;      // 1..3

    static bool _saveHeld, _loadHeld;
    static bool _wantSave, _wantLoad;

    static string SlotPath(int slot) => Path.GetFullPath($"quicksave{slot}.kfq");

    public void OnLoad()
    {
        var view = RecompOne.Runtime.Runtime.View;
        _saveKey = IndexOf(view.GetString(SaveKeyKey, "F2"), 0);
        _loadKey = IndexOf(view.GetString(LoadKeyKey, "F4"), 2);
        _slot = Math.Clamp((int)view.GetFloat(SlotKey, 1f), 1, 3);
        _restoreSlots = view.GetBool(RestoreSlotsKey, false);

        Event.AddListener(_onOverlay);
        Event.AddListener(_onVSync);
        Event.AddListener(_onPad);
    }

    public void OnUnload()
    {
        Event.RemoveListener(_onOverlay);
        Event.RemoveListener(_onVSync);
        Event.RemoveListener(_onPad);
        _bootLoad = false;
    }

    // ---- loading from the title screen ----
    //
    // Stage 3 only runs in an area, so the load key does nothing on the title
    // or the start menu. The first version drove a scratch New Game and loaded
    // over it from stage 3 (patches/AutoStart.cs's route); after a restart that
    // left the soldiers' and archers' textures out, found in play. A card load
    // from the title gets them right, because GAME.EXE's main (func_8001369C)
    // does the whole area set-up after its start menu:
    //
    //     if (func_8001BE60() != -1) func_800240B8();   // start menu; -1 = New Game
    //     func_80016050(); func_80025DA8(); ...          // load the area, floor, display
    //
    // So this takes the start menu's place: pulse Start through OPEN.EXE's
    // title (through PAD_dr, the path that reaches the boot menus), and when
    // main calls func_8001BE60, skip it, unpack the quick save as the card load
    // would, and return "loaded". Main's own code does the rest.
    static string _overlay = "";
    static bool _bootLoad;      // the load key pressed at the title; waiting for the start menu
    static bool _postBoot;      // the start menu was replaced; tidy up in the area
    static bool _bootKeyHeld;

    static bool AtTitle => _overlay is "open" or "game";

    static readonly Action<OverlayLoadedEvent> _onOverlay = e =>
    {
        _overlay = e.Name;
    };

    static readonly Action<VSyncEvent> _onVSync = _ =>
    {
        bool down = HostWindow.IsKeyDown(Keys[_loadKey].Key);
        bool pressed = down && !_bootKeyHeld;
        _bootKeyHeld = down;
        if (!pressed || !AtTitle || _bootLoad) return;

        if (!File.Exists(SlotPath(_slot))) { Toast($"Quick slot {_slot} is empty"); return; }
        _bootLoad = true;
        Toast($"Starting the game and loading quick slot {_slot}...");
    };

    static readonly Action<PadReadEvent> _onPad = e =>
    {
        if (!_bootLoad || e.Port != 0) return;
        double t = Environment.TickCount64 / 1000.0;
        ushort press = 0;
        if (_overlay == "open" && (long)(t * 60) % 30 < 14) press = Controller.Start;
        if (press != 0)
            e.Buttons &= (ushort)~(ushort)((press >> 8) | (press << 8));
    };

    static int IndexOf(string name, int fallback)
    {
        for (int i = 0; i < Keys.Length; i++)
            if (Keys[i].Name == name) return i;
        return fallback;
    }

    [PostHook("game", Address = 0x8002A550)]
    static void AfterPlayerStage(CpuContext c, IMemory m)
    {
        // Stage 3 runs only in an area; a mod loaded mid-game has not seen the
        // area module arrive, so learn it here.
        if (_overlay.Length == 0) _overlay = "fdat";

        if (_postBoot)
        {
            // First tick in the area after a title-screen quick load: the same
            // doorway check a quick load does, then the watch.
            _postBoot = false;
            var snap = c.Snapshot();
            bool moved;
            try
            {
                if (_pendingSlots != null) ReplayRegion(c, m, _pendingSlots);
                _pendingSlots = null;
                moved = Unstick(c, m, "at load");
            }
            finally { c.Restore(snap); }
            if (!moved)
            {
                _watchFrames = WatchFrames;
                _watchX = (int)m.ReadU32(PosX);
                _watchZ = (int)m.ReadU32(PosZ);
            }
            Toast(moved ? $"Loaded quick slot {_slot} (moved you out of something solid)"
                        : $"Loaded quick slot {_slot}");
        }

        // Edge-detect the keys here, once a frame.
        bool s = HostWindow.IsKeyDown(Keys[_saveKey].Key);
        bool l = HostWindow.IsKeyDown(Keys[_loadKey].Key);
        if (s && !_saveHeld) _wantSave = true;
        if (l && !_loadHeld) _wantLoad = true;
        _saveHeld = s;
        _loadHeld = l;

        if (_wantSave) { _wantSave = false; Save(c, m); }
        if (_wantLoad) { _wantLoad = false; Load(c, m); }
    }

    static string BackupPath(int slot) => Path.GetFullPath($"quicksave{slot}.bak");

    static void Save(CpuContext c, IMemory m)
    {
        // The title-screen load plays a scratch New Game for a moment before it
        // loads; a save then would put that New Game over the quick save.
        if (_bootLoad) { Toast("Not now -- a quick load is starting"); return; }

        if (m.ReadU16(MaxHp) == 0) { Toast("Nothing to save yet"); return; }
        if (m.ReadU8(State) == StateDead || m.ReadU16(Hp) == 0) { Toast("Can't save while dead"); return; }

        uint buf = m.ReadU32(BufPtr);
        byte[] borrowed = ReadBlock(m, buf, BufSize);
        var saved = c.Snapshot();
        byte[] data;
        try
        {
            c.A0 = m.ReadU8(Area);
            KingsField2.func_800492B8(c, m);

            for (uint i = 0; i < BufSize; i += 4) m.WriteU32(buf + i, 0);
            c.A0 = buf + DataOffset;
            KingsField2.func_80049A88(c, m);

            data = ReadBlock(m, buf + DataOffset, BufSize - DataOffset);
        }
        finally
        {
            c.Restore(saved);
            WriteBlock(m, buf, borrowed);
        }

        try
        {
            // The previous save survives one overwrite, as a .bak beside it.
            if (File.Exists(SlotPath(_slot))) File.Copy(SlotPath(_slot), BackupPath(_slot), true);
            using var f = File.Create(SlotPath(_slot));
            f.Write(Magic);
            f.Write(data);
            for (uint i = 0; i < SlotCount; i++) f.WriteByte(m.ReadU8(Slots + i));
        }
        catch (Exception e)
        {
            Toast($"Save failed: {e.Message}");
            return;
        }
        Toast($"Saved to quick slot {_slot}");
        Console.WriteLine($"[KF2] quicksave: slot {_slot}, area {m.ReadU8(Area)}");
    }

    /// <summary>GAME.EXE's start menu, replaced while a title-screen quick load
    /// is pending: unpack the save the way the card load does and report a
    /// loaded game, so main runs its own area set-up.</summary>
    [PreHook("game", Address = 0x8001BE60)]
    static bool StartMenu(CpuContext c, IMemory m)
    {
        if (!_bootLoad) return true;
        _bootLoad = false;
        if (!ReadSave(out byte[] file, out byte[]? slots)) return true;   // show the menu

        var snap = c.Snapshot();
        try
        {
            Unpack(c, m, file);
            _pendingSlots = _restoreSlots ? slots : null;
            _savedY = BitConverter.ToInt32(file, Magic.Length + (int)SavedPos + 4);
        }
        finally
        {
            c.Restore(snap);
        }
        _postBoot = true;
        c.V0 = 0;   // anything but -1: a save was loaded
        Console.WriteLine($"[KF2] quickload: slot {_slot} from the title, area {m.ReadU8(Area)}");
        return false;
    }

    static byte[]? _pendingSlots;

    /// <summary>
    /// Enter the save's region the way walking in does: find the trigger object
    /// whose five slot values are the save's, and make its call to 0x800162DC
    /// with all eight of its arguments. Nothing to do if the defaults already
    /// are the save's region; nothing found means the defaults stay.
    /// </summary>
    static void ReplayRegion(CpuContext c, IMemory m, byte[] want)
    {
        bool same = true;
        for (uint i = 0; i < SlotCount; i++)
            if (want[i] != 0xFF && m.ReadU8(Slots + i) != want[i]) same = false;
        if (same) return;

        const uint ObjBase = 0x80177714, ObjStride = 0x44, ObjCount = 396;
        for (uint k = 0; k < ObjCount; k++)
        {
            uint rec = ObjBase + k * ObjStride;
            if (m.ReadU8(rec + 0x4) == 0xFF) continue;
            bool match = true;
            for (uint i = 0; i < SlotCount; i++)
                if (m.ReadU8(rec + 0x3A + i) != want[i]) { match = false; break; }
            if (!match) continue;

            c.SP -= 0x20u;
            m.WriteU32(c.SP + 0x10u, m.ReadU8(rec + 0x3E));
            m.WriteU32(c.SP + 0x14u, (uint)(sbyte)m.ReadU8(rec + 0x40));
            m.WriteU32(c.SP + 0x18u, (uint)(sbyte)m.ReadU8(rec + 0x41));
            m.WriteU32(c.SP + 0x1Cu, (uint)(sbyte)m.ReadU8(rec + 0x42));
            c.A0 = m.ReadU8(rec + 0x3A);
            c.A1 = m.ReadU8(rec + 0x3B);
            c.A2 = m.ReadU8(rec + 0x3C);
            c.A3 = m.ReadU8(rec + 0x3D);
            KingsField2.func_800162DC(c, m);
            c.SP += 0x20u;
            Console.WriteLine($"[KF2] quickload: region replayed from object slot {k} " +
                              $"({want[0]},{want[1]},{want[2]},{want[3]},{want[4]})");
            return;
        }
        Console.WriteLine($"[KF2] quickload: no trigger for region " +
                          $"({want[0]},{want[1]},{want[2]},{want[3]},{want[4]}); area defaults kept");
    }

    static bool ReadSave(out byte[] file, out byte[]? slots)
    {
        file = [];
        slots = null;
        try
        {
            string path = SlotPath(_slot);
            if (!File.Exists(path)) { Toast($"Quick slot {_slot} is empty"); return false; }
            file = File.ReadAllBytes(path);
        }
        catch (Exception e)
        {
            Toast($"Load failed: {e.Message}");
            return false;
        }
        int block = (int)(BufSize - DataOffset);
        bool v2 = file.Length == Magic.Length + block + SlotCount
                  && file.AsSpan(0, Magic.Length).SequenceEqual(Magic);
        bool v1 = file.Length == MagicV1.Length + block
                  && file.AsSpan(0, MagicV1.Length).SequenceEqual(MagicV1);
        if (!v1 && !v2) { Toast($"Quick slot {_slot} is not a quick save"); return false; }
        if (v2) slots = file.AsSpan(Magic.Length + block, SlotCount).ToArray();
        return true;
    }

    /// <summary>The save block into the game's buffer and through the game's own
    /// unpack, with the buffer's previous contents put back.</summary>
    static void Unpack(CpuContext c, IMemory m, byte[] file)
    {
        int block = (int)(BufSize - DataOffset);
        uint buf = m.ReadU32(BufPtr);
        byte[] borrowed = ReadBlock(m, buf, BufSize);
        for (uint i = 0; i < DataOffset; i += 4) m.WriteU32(buf + i, 0);
        WriteBlock(m, buf + DataOffset, file.AsSpan(Magic.Length, block));
        c.A0 = buf + DataOffset;
        KingsField2.func_8004A040(c, m);
        WriteBlock(m, buf, borrowed);
    }

    static void Load(CpuContext c, IMemory m)
    {
        if (!ReadSave(out byte[] file, out byte[]? slots)) return;

        var saved = c.Snapshot();
        bool moved = false;
        try
        {
            Unpack(c, m, file);

            // The -3 arm of func_80029CBC, as AutoReload.LoadSlot runs it.
            KingsField2.func_800240B8(c, m);

            uint area = m.ReadU8(Area);
            // func_80024154(slot0, slot1, slot2, slot3, slot4, 0xFF). A version 1
            // file has no slots and re-enters with the area in all five.
            uint S(int i) => area;   // the defaults; the region is replayed below

            // 0x800162DC loads nothing when all five requested slots equal the
            // loaded ones at 0x8017E060. The unpack above has just written the
            // saved area there, so asking for the saved slots matched and the
            // area was never reloaded: harmless mid-session, where video memory
            // already held it, but after a restart it still held the scratch
            // New Game's, and the soldiers and archers drew as nothing. So mark
            // nothing as loaded first. 0x63 is the loader's own "no area", which
            // also skips packing the old area's objects (func_800492B8) -- right,
            // since the save replaces them; 0xFE matches no real slot.
            m.WriteU8(Slots, 0x63);
            for (uint i = 1; i < SlotCount; i++) m.WriteU8(Slots + i, 0xFE);

            c.SP -= 0x20u;
            m.WriteU32(c.SP + 0x14u, 0xFFu);
            m.WriteU32(c.SP + 0x10u, S(4));
            c.A0 = area;
            c.A1 = S(1);
            c.A2 = S(2);
            c.A3 = S(3);
            KingsField2.func_80024154(c, m);
            c.SP += 0x20u;

            KingsField2.func_80025D38(c, m);

            if (_restoreSlots && slots != null) ReplayRegion(c, m, slots);

            // Loading over a death: clear the latch the way AutoReload does.
            if (m.ReadU8(State) == StateDead)
                KingsField2.func_80029E5C(c, m);

            // The height the save was taken at. The area entry puts you on the
            // floor it resolves, which for a doorway whose door is closed again
            // is the top of the door -- measured 4200 above the saved height --
            // so the saved value is the only record of the level you were on.
            _savedY = BitConverter.ToInt32(file, Magic.Length + (int)SavedPos + 4);

            moved = Unstick(c, m, "at load");
            if (!moved)
            {
                // Not stuck yet as far as anything can tell -- the area's
                // objects may still be settling. Keep checking at the top of
                // the vertical code for a while; see BeforeVertical.
                _watchFrames = WatchFrames;
                _watchX = (int)m.ReadU32(PosX);
                _watchZ = (int)m.ReadU32(PosZ);
            }
        }
        finally
        {
            c.Restore(saved);
        }
        Toast(moved ? $"Loaded quick slot {_slot} (moved you out of something solid)"
                    : $"Loaded quick slot {_slot}");
        Console.WriteLine($"[KF2] quickload: slot {_slot}, area {m.ReadU8(Area)}");
    }

    // ---- Getting out of things that were open when you saved ----
    //
    // The save format keeps where you stood but not what state the doors were
    // in: a real save only ever happens at a save point, so the game never needed
    // it. Re-entering the area closes every door, and a quick save taken in a
    // doorway loads you inside one, where the vertical code pushes you up through
    // the geometry. So after a load, test the position the way walking does and,
    // if it is blocked, move to the nearest clear spot on the same level.

    const uint PosX = 0x801994EC, PosY = 0x801994F0, PosZ = 0x801994F4;   // s32
    const uint StrafeVel = 0x8019953E, FwdVel = 0x80199540, WalkMag = 0x80199542;
    const uint FallState = 0x801994E4;   // u8, 0 = on the ground
    const uint FallVel   = 0x8019954E;   // s16
    const uint FloorY    = 0x801D9C94;   // s32, what func_8002C330 resolved

    const uint Radius = 0x320, Height = 0x6A4, CollideFlags = 0x31;

    /// <summary>func_8002C700(x, y, z, radius, height, flags) -- nonzero when a
    /// body there would hit something, map or object. The same call the walk
    /// (func_80028080) and the fall (func_80028560) make.</summary>
    static bool Blocked(CpuContext c, IMemory m, int x, int y, int z)
    {
        c.SP -= 0x20u;
        m.WriteU32(c.SP + 0x10u, Height);
        m.WriteU32(c.SP + 0x14u, CollideFlags);
        c.A0 = (uint)x; c.A1 = (uint)y; c.A2 = (uint)z; c.A3 = Radius;
        KingsField2.func_8002C700(c, m);
        c.SP += 0x20u;
        return c.V0 != 0;
    }

    /// <summary>func_8002C330(x, y, z, radius, height) -- the floor under a
    /// point, choosing the tile's upper or lower half from y the way the
    /// vertical code does. Leaves the answer at 0x801D9C94.</summary>
    static int Floor(CpuContext c, IMemory m, int x, int y, int z)
    {
        c.SP -= 0x20u;
        m.WriteU32(c.SP + 0x10u, Height);
        c.A0 = (uint)x; c.A1 = (uint)y; c.A2 = (uint)z; c.A3 = Radius;
        KingsField2.func_8002C330(c, m);
        c.SP += 0x20u;
        return (int)m.ReadU32(FloorY);
    }

    // After a load, the check runs again at the top of func_80028560 -- before
    // the floor it resolves can turn into a ledge hop -- for this many calls, or
    // until you move off the spot you loaded at, whichever comes first.
    const int WatchFrames = 90;
    static int _watchFrames;
    static int _watchX, _watchZ;
    static int _savedY;

    // Where func_80049A88 packs the position triple, relative to the data it
    // packs (buf+0x400): X, Y, Z as s32.
    const uint SavedPos = 0x39D4;

    [PreHook("game", Address = 0x80028560)]
    static bool BeforeVertical(CpuContext c, IMemory m)
    {
        if (_watchFrames <= 0) return true;
        int n = WatchFrames - _watchFrames;
        _watchFrames--;

        int x = (int)m.ReadU32(PosX), z = (int)m.ReadU32(PosZ);
        if (x != _watchX || z != _watchZ)
        {
            _watchFrames = 0;
            return true;
        }

        var saved = c.Snapshot();
        try
        {
            if (Unstick(c, m, $"frame {n} after load"))
            {
                _watchFrames = 0;
                Toast($"Moved you out of something solid");
            }
        }
        finally
        {
            c.Restore(saved);
        }
        return true;
    }

    static bool Unstick(CpuContext c, IMemory m, string when)
    {
        int x = (int)m.ReadU32(PosX), y = (int)m.ReadU32(PosY), z = (int)m.ReadU32(PosZ);

        // Three ways to be stuck. Lifted: the area entry put you well above the
        // height the save was taken at -- the doorway case, where the closed
        // door's top is now the floor there. Blocked: inside something the walk
        // would not enter. Buried: the floor here is well above your feet, which
        // func_80028560 answers with the 0x20 ledge hop. Y grows downward, so
        // "above" is smaller.
        bool lifted = y < _savedY - 0x200;
        bool blocked = Blocked(c, m, x, y, z);
        int floorHere = Floor(c, m, x, y, z);
        bool buried = floorHere < y - 0x200;

        // Diagnostics: what the check saw, for the load itself and the first few
        // frames after it, and whenever the answer is "stuck".
        if (when == "at load" || when.StartsWith("frame 0 ") || when.StartsWith("frame 1 ")
            || when.StartsWith("frame 2 ") || lifted || blocked || buried)
            Console.WriteLine($"[KF2] quickload check {when}: pos ({x},{y},{z}) saved Y {_savedY} " +
                              $"floor {floorHere} lifted {lifted} blocked {blocked} fall 0x{m.ReadU8(FallState):X2}");

        if (!lifted && !blocked && !buried) return false;

        // The level to put you back on: the saved one if you were lifted off it.
        int level = lifted ? _savedY : y;

        // Rings of 16 points, a quarter tile apart, out to two tiles. The floor
        // is asked for at the level's height, so it resolves the same half of a
        // two-storey tile you were on; one more than a step away from it is
        // another level -- or the top of the door -- not the other side of it.
        for (int r = 0x200; r <= 0x1000; r += 0x200)
            for (int i = 0; i < 16; i++)
            {
                double a = i * Math.PI / 8;
                int nx = x + (int)(Math.Cos(a) * r);
                int nz = z + (int)(Math.Sin(a) * r);
                int ny = Floor(c, m, nx, level, nz);
                if (Math.Abs(ny - level) > 0x200) continue;
                if (Blocked(c, m, nx, ny, nz)) continue;

                m.WriteU32(PosX, (uint)nx);
                m.WriteU32(PosY, (uint)ny);
                m.WriteU32(PosZ, (uint)nz);
                Floor(c, m, nx, ny, nz);   // leave the game's floor globals for the new spot
                m.WriteU8(FallState, 0);
                m.WriteU16(FallVel, 0);
                m.WriteU16(StrafeVel, 0);
                m.WriteU16(FwdVel, 0);
                m.WriteU16(WalkMag, 0);
                string why = lifted ? "lifted" : blocked ? "blocked" : $"buried (floor {floorHere})";
                Console.WriteLine($"[KF2] quickload: {why} at ({x},{y},{z}), moved to ({nx},{ny},{nz})");
                return true;
            }

        Console.WriteLine($"[KF2] quickload: stuck at ({x},{y},{z}), floor {floorHere}, and found nowhere clear nearby");
        return false;
    }

    static byte[] ReadBlock(IMemory m, uint addr, uint len)
    {
        var b = new byte[len];
        for (uint i = 0; i < len; i++) b[i] = m.ReadU8(addr + i);
        return b;
    }

    static void WriteBlock(IMemory m, uint addr, ReadOnlySpan<byte> data)
    {
        for (int i = 0; i < data.Length; i++) m.WriteU8(addr + (uint)i, data[i]);
    }

    static void Toast(string msg) => ToastNotifications.ShowText("Quick Save", msg);

    public void DrawSettings()
    {
        ImGui.TextWrapped("Save anywhere and load it back. Loading reloads the area like a normal "
                        + "load. Quick saves are separate files in the Verdite2 data folder and "
                        + "never touch your memory card saves.");
        // The panel header reads mod.json once at startup; this line is the
        // running code's own, so it is right after a reload too.
        ImGui.TextDisabled($"Code version {CodeVersion}");
        ImGui.Separator();

        if (Combo("Save key", ref _saveKey)) Persist(SaveKeyKey, Keys[_saveKey].Name);
        if (Combo("Load key", ref _loadKey)) Persist(LoadKeyKey, Keys[_loadKey].Name);

        int slot = _slot;
        if (ImGui.SliderInt("Quick slot", ref slot, 1, 3))
        {
            _slot = slot;
            RecompOne.Runtime.Runtime.View.SetFloat(SlotKey, slot);
            RecompOne.Runtime.Runtime.SaveView();
        }
        ImGui.TextDisabled(File.Exists(SlotPath(_slot))
            ? $"Slot {_slot}: {File.GetLastWriteTime(SlotPath(_slot)):g}"
            : $"Slot {_slot}: empty");

        if (ImGui.Checkbox("Restore the area's region textures", ref _restoreSlots))
        {
            RecompOne.Runtime.Runtime.View.SetBool(RestoreSlotsKey, _restoreSlots);
            RecompOne.Runtime.Runtime.SaveView();
        }
        ImGui.TextDisabled("On: places like the Ant Nest keep their own look. Off: the area's defaults.");
        ImGui.TextDisabled("Experimental: may cause texture problems (e.g. untextured enemies) after a load.");

        // One step of undo for an overwrite: swap the save and its .bak.
        if (File.Exists(BackupPath(_slot)))
        {
            ImGui.TextDisabled($"Previous save: {File.GetLastWriteTime(BackupPath(_slot)):g}");
            if (ImGui.Button("Restore previous save"))
            {
                try
                {
                    string cur = SlotPath(_slot), bak = BackupPath(_slot), tmp = cur + ".swap";
                    if (File.Exists(cur)) File.Move(cur, tmp, true);
                    File.Move(bak, cur, true);
                    if (File.Exists(tmp)) File.Move(tmp, bak, true);
                    Toast($"Quick slot {_slot} restored to the previous save (press again to undo)");
                }
                catch (Exception e)
                {
                    Toast($"Restore failed: {e.Message}");
                }
            }
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
