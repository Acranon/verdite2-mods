// ModCompiler compiles mods with no implicit usings, so every namespace the
// file needs must be named here -- including System.
using System;
using System.Collections.Generic;
using System.Text;
using ImGuiNET;
using RecompOne.Runtime.Context;
using RecompOne.Runtime.Host.Window;
using RecompOne.Runtime.Memory;
using RecompOne.Runtime.Modding;

namespace Kf2.Mods.KeyHint;

/// <summary>
/// Names the key a lock wants, the moment you are told it is locked.
///
/// ---- where a lock is ----
///
/// Keys are items used from the item menu, and using one runs
/// `func_800474D0(pos, facing, itemId)`, which asks `func_800375EC(obj, itemId)`
/// about each object in front of you:
///
///     lock = obj[0x38];
///     if (lock >= 0xFE) return 2;                       // no lock (0xFF = open)
///     if (lock == item) { obj[0x38] = 0xFF; return 1; } // the right key
///     return 3;                                          // the wrong one
///
/// So **the byte at +0x38 is the item id of the key**, and 0xFF once unlocked.
/// That test is only applied to some object types -- the jump table at
/// 0x800118C4, indexed by the type byte at +0x4:
///
///     types 2, 3, 4   doors: a lock while closed (u16 +0x8 == 0)
///     types 5, 8      a lock always
///     type 0x16       outside that table, but locked the same way as far as
///                     the use handler goes; opened by a per-item arm instead
///     everything else no lock (+0x38 means something else there)
///
/// ---- when to say so ----
///
/// Pressing the action button runs the use handler `func_800489FC`, which finds
/// what is in front of you with `func_80036EC8` -- one object per call, returning
/// its index or -1. Every object it finds while the handler runs is checked;
/// any that is still locked gets its key named once the handler returns. The
/// game's own "locked" text is left alone and this appears beside it.
///
/// Item names are the font-index records at 0x80065B24 + id * 0x18 (A = 0,
/// space = 0x7F, 0xFF ends it), and the inventory is a count per item id at
/// 0x8009B52C -- see mods/kf2debug/Items.cs.
/// </summary>
public sealed class KeyHintMod : IMod
{
    const uint ObjBase = 0x80177714, ObjStride = 0x44, ObjCount = 396;
    const uint NameTable = 0x80065B24, NameStride = 0x18;
    const uint Inventory = 0x8009B52C;
    const int ItemCount = 120;

    static bool _inUse;
    static readonly List<uint> _locked = [];
    static bool _showOwned = true;

    public void OnLoad()
    {
        _showOwned = RecompOne.Runtime.Runtime.View.GetBool("kf2.keyhint.owned", true);
    }

    [PreHook("game", Address = 0x800489FC)]
    static bool UseBegins(CpuContext c, IMemory m)
    {
        _inUse = true;
        _locked.Clear();
        return true;
    }

    [PostHook("game", Address = 0x80036EC8)]
    static void Found(CpuContext c, IMemory m)
    {
        if (!_inUse) return;
        int i = (int)c.V0;
        if (i < 0 || i >= ObjCount) return;
        uint rec = ObjBase + (uint)i * ObjStride;
        if (IsLocked(m, rec) && !_locked.Contains(rec)) _locked.Add(rec);
    }

    [PostHook("game", Address = 0x800489FC)]
    static void UseEnds(CpuContext c, IMemory m)
    {
        _inUse = false;
        foreach (uint rec in _locked)
        {
            if (!IsLocked(m, rec)) continue;   // it opened after all
            int key = m.ReadU8(rec + 0x38);
            byte type = m.ReadU8(rec + 4);
            string title, msg;

            if (key >= ItemCount)
            {
                // Not an item id -- the game has 120. Found in play as 0xC8 on a
                // door the game calls SEALED: something else in the area opens it.
                title = "Sealed";
                msg = "Not opened by a key -- a switch, lever or event elsewhere opens it";
            }
            else
            {
                string name = Name(m, key);
                if (name.Length == 0) name = $"item 0x{key:X2}";
                bool have = m.ReadU8(Inventory + (uint)key) > 0;
                title = "Locked";

                // The use handler's door arm (kinds 3/4) opens a lock of 0x0F on
                // the spot when that item is in the inventory -- no item menu.
                // Found in play: 0x0F is DARK SLAYER, and the game says NOT
                // ACCEPTED without it.
                if (type is 3 or 4 && key == 0x0F)
                {
                    msg = $"Opens when you carry {name}";
                    if (_showOwned) msg += have ? "  (you have it -- press the action button again)" : "  (not carried)";
                }
                else
                {
                    msg = $"Needs: {name}";
                    if (_showOwned) msg += have ? "  (you have it -- use it from the item menu)" : "  (not carried)";
                }
                Console.WriteLine($"[KF2] keyhint: slot {(rec - ObjBase) / ObjStride} type 0x{type:X2} " +
                                  $"needs item 0x{key:X2} {name}, carried {have}");
            }
            ToastNotifications.ShowText(title, msg);
        }
        _locked.Clear();
    }

    static bool IsLocked(IMemory m, uint rec)
    {
        byte type = m.ReadU8(rec + 0x4);
        byte lk = m.ReadU8(rec + 0x38);
        if (lk >= 0xFE) return false;
        return type switch
        {
            2 or 3 or 4 => m.ReadU16(rec + 0x8) == 0,
            5 or 8 => true,
            // Not in func_800375EC's table, but the use handler treats it the
            // same way (0xFF opens, 0xFE is open, anything else is "locked"),
            // and its item is used through a dedicated arm of func_800474D0.
            // Found in play: a stone chest locked with item 0x68.
            0x16 => true,
            _ => false,
        };
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

    public void DrawSettings()
    {
        ImGui.TextWrapped("When you press the action button on something locked, a note names the "
                        + "key it needs.");
        if (ImGui.Checkbox("Say whether you are carrying it", ref _showOwned))
        {
            RecompOne.Runtime.Runtime.View.SetBool("kf2.keyhint.owned", _showOwned);
            RecompOne.Runtime.Runtime.SaveView();
        }
    }
}
