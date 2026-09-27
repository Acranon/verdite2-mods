// ModCompiler compiles mods with no implicit usings, so every namespace the
// file needs must be named here -- including System.
using System;
using ImGuiNET;
using RecompOne.Runtime.Context;
using RecompOne.Runtime.Host;
using RecompOne.Runtime.Memory;
using RecompOne.Runtime.Modding;
using Silk.NET.Input;
using KingsField2 = Recompiled.KingsField2_game;

namespace Kf2.Mods.ItemInfo;

/// <summary>
/// A key that shows the highlighted item's description, the way the
/// fortuneteller does.
///
/// Every message in the game is a picture: a 4-bit 256x256 TIM in an archive on
/// the disc, shown from load to close by `func_80035B48(file, entry)` -- dim the
/// screen, fade the text in, wait for a button, fade out, put VRAM back. The
/// fortuneteller (`func_80047000`) picks an item from a list and calls it with
/// file 6 (`COM\ITEM.T`) and entry `0x168 + id`: one description per item id,
/// the same text as picking the item up. The 22 ids with no picture are exactly
/// the unused ones, whose name record is the placeholder `00 FF`.
///
/// Every scrolling item list steps its cursor through `func_8001EB70(desc, ids,
/// ...)`, where `ids` is the u8 item id of each row (it loads the preview model
/// from them) and desc+0x21 is the highlighted row. On the key, that id's
/// description is shown from inside the step, exactly as the fortuneteller
/// shows hers; the list carries on underneath when it closes.
///
/// The message waits for a press to close but not for its release, so after it
/// the step is skipped until the pad is let go -- otherwise the button that
/// closed the description would also pick the item.
/// </summary>
public sealed class ItemInfoMod : IMod
{
    const uint ItemFile = 6;           // COM\ITEM.T
    const uint DescriptionBase = 0x168;
    const uint NameTable = 0x80065B24, NameStride = 0x18;
    const int ItemCount = 120;

    // func_8001EB70's return addresses in the item lists (not magic): the Items
    // page, equipment, the item slot, and the shop and fortuneteller lists.
    static readonly uint[] ItemLists =
    [
        0x80019348, 0x8001A994, 0x8001B004,
        0x8001D858, 0x8001DEB8, 0x8001E114, 0x8001E5C0,
    ];

    const string KeyKey = "kf2.iteminfo.key";

    static readonly (string Name, Key Key)[] Keys =
    [
        ("`", Key.GraveAccent), ("I", Key.I), ("Tab", Key.Tab), ("Z", Key.Z), ("X", Key.X),
        ("C", Key.C), ("V", Key.V), ("F5", Key.F5), ("F6", Key.F6), ("F7", Key.F7),
    ];

    static int _key;
    static bool _held;
    static bool _waitRelease;

    public void OnLoad()
    {
        string name = RecompOne.Runtime.Runtime.View.GetString(KeyKey, "`");
        _key = 0;
        for (int i = 0; i < Keys.Length; i++)
            if (Keys[i].Name == name) _key = i;
    }

    [PreHook("game", Address = 0x8001EB70)]
    static bool BeforeStep(CpuContext c, IMemory m)
    {
        if (Array.IndexOf(ItemLists, c.RA) < 0) return true;

        if (_waitRelease)
        {
            if (PadDown(c, m)) { c.V0 = 0; return false; }
            _waitRelease = false;
        }

        bool down = HostWindow.IsKeyDown(Keys[_key].Key);
        bool pressed = down && !_held;
        _held = down;
        if (!pressed) return true;

        uint desc = c.A0, ids = c.A1;
        int n = m.ReadU8(desc + 0x1E);
        int cur = m.ReadU8(desc + 0x21);
        if (n == 0 || cur >= n) return true;
        int id = m.ReadU8(ids + (uint)cur);
        if (!HasDescription(m, id)) return true;

        var snap = c.Snapshot();
        try
        {
            c.A0 = ItemFile;
            c.A1 = DescriptionBase + (uint)id;
            KingsField2.func_80035B48(c, m);
        }
        finally { c.Restore(snap); }

        _waitRelease = true;
        c.V0 = 0;
        return false;
    }

    static bool PadDown(CpuContext c, IMemory m)
    {
        var snap = c.Snapshot();
        try
        {
            c.A0 = 1;
            KingsField2.PadRead_game(c, m);
            return c.V0 != 0;
        }
        finally { c.Restore(snap); }
    }

    /// <summary>An unused id's name is the placeholder "A" (00 FF), and exactly
    /// those have no picture on the disc.</summary>
    static bool HasDescription(IMemory m, int id)
    {
        if ((uint)id >= ItemCount) return false;
        uint rec = NameTable + (uint)id * NameStride;
        return !(m.ReadU8(rec) == 0x00 && m.ReadU8(rec + 1) == 0xFF);
    }

    public void DrawSettings()
    {
        ImGui.TextWrapped($"In any item list (items, equipment, shops), press {Keys[_key].Name} to read the "
                        + "highlighted item's description, as the fortuneteller shows it. Any button closes it.");
        if (ImGui.BeginCombo("Key", Keys[_key].Name))
        {
            for (int i = 0; i < Keys.Length; i++)
                if (ImGui.Selectable(Keys[i].Name, i == _key))
                {
                    _key = i;
                    RecompOne.Runtime.Runtime.View.SetString(KeyKey, Keys[i].Name);
                    RecompOne.Runtime.Runtime.SaveView();
                }
            ImGui.EndCombo();
        }
    }
}
