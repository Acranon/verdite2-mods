# Verdite2 mods

Mods for [Verdite2](https://github.com/Voicedrew11/verdite2), the native PC port of *King's Field* (US, SLUS-00158).

Every mod can be switched on and off in the game's **Mods** panel. Each one's settings (keys, strength, colours and so on) are under it there, and they are saved between sessions.

Tested with Verdite2 **v0.3.2**. The mods hook the game at fixed addresses, so a future port update that changes those could break one. If that happens, open an issue.

## Installing

1. Download this repo (**Code → Download ZIP**) and unzip it.
2. Copy the folders from `mods/` (all of them or just the ones you want) into Verdite2's data folder:
   - Windows: `%LOCALAPPDATA%\Verdite2\mods\` (paste that into Explorer's address bar)
   - Linux: `~/.local/share/verdite2/mods/`
3. Start the game. The mods are compiled on first launch, which takes a moment. Turn them on or off in the **Mods** panel.

To update, replace the folders with the new ones. Your settings are stored separately and are kept.

## The mods

### Separate Run Key
On the PS1 pad, one button both runs (when held) and uses or picks up (when pressed). This splits them: the Use key only uses, and a separate Run key only runs.
- **Run:** Left Shift, or L3 on a controller.

### Jump
Adds a jump. It uses the game's own falling state, so gravity, ceiling collision, landing and **fall damage** all still apply. Jump off something high and you'll feel it.
- **Jump:** Space.
- **Settings:** jump strength (default 373, up to 700) and rise speed (default 80%). **450 is the most that lands without fall damage.** Anything higher reaches spots you otherwise couldn't, but every jump on flat ground will cost HP when you land.

### Quick Save
Save anywhere and load it back, using the game's own save and load code, so the area, music and everything else reload the way a normal load does. Quick saves are separate files (`quicksaveN.kfq` in the data folder) and never touch your memory-card saves.
- **Save:** F2. **Load:** F4. The load key also works from the title screen.
- **Settings:** three quick-save slots, and **Restore previous save**, which undoes one accidental overwrite. Each save keeps a `.bak` of the one it replaced.
- After a load, if you would be stuck in a wall or doorway, you are moved to the nearest clear spot.
- **Restore the area's region textures** is **off by default and experimental.** When on, places like the Ant Nest keep their own textures after a load. It's still being tested and may cause texture problems, such as untextured enemies after a restart. If you try it and see problems, please open an issue.

### Gear Compare
When the equipment menu asks whether to equip something, shows every combat stat that would change, current and new, plus a **TOTAL** row, beside the prompt in the game's own font. Also works in shops.
- **Settings:** panel position for the equipment menu and for shops, and row spacing.

### Key Hint
When you try to open something locked, tells you which key it needs and whether you're carrying it. It also recognises sealed doors, and doors that need the DARK SLAYER.

### Quick Use
Using a key on a door, a crystal on a pedestal or a vial on a fountain normally takes a trip through the menu. Quick Use opens the game's own Items page directly and keeps the items you care about at the top.
- **Open:** R. Pick an item and answer Yes, and it's used on whatever is in front of you, exactly as from the normal menu. Backing out closes the menu.
- **Pin:** F, on the Items page, pins or unpins the highlighted item. Pinned items stay at the top.
- **Settings:** both keys, whether the normal menu's Items page is sorted too (on by default), and the pinned list, which you can reorder or unpin.

### Item Info
In any item list (the Items page, equipment, shops), press **`** to read the highlighted item's description. It's shown the way the fortuneteller shows it, using the game's own text from your disc. Any button closes it. The key can be changed in settings.

### Spell Info
The game never explains what its spells do. On the Magic and attack-magic pages, press **`** to show a panel for the highlighted spell: its element, its MP cost (read live from the game) and a short description, in the game's own font. The key and the panel's position can be changed in settings.

The descriptions are our own, written from the spell pages of the [King's Field Wiki](https://kingsfield.fandom.com/wiki/Magic).

### Reveal *(work in progress)*
Press **G** to make interactive objects glow, colour-coded by what they are:

| Colour | Objects |
|---|---|
| Red | Secret doors and wall panels |
| Gold | Containers with loot (chests, barrels) |
| Magenta | Locked containers |
| Green | Loose items |
| Blue | Doors |
| Cyan | Levers and switches |
| Orange | Traps |
| White | Everything else |

- **Warnings:** a message appears when a trap or a hidden wall panel is near. Secret doors glow but do not warn, because their glow is easy to spot. The warning distance can be set (default 2.5 tiles).
- **Phantom sight** (on by default): while the glow is on, Reveal does what the in-game PHANTOM ROD does, without using one. Hidden wall compartments open up and spear traps show in the walls, so they glow too.
- **H** prints the objects around you to the console, which helps identify what something is.
- **Settings:** which categories glow, glow strength, pulse, warnings and warning distance.
- Secret doors are the hard case, and a few may not be classified correctly yet. Reports with screenshots are welcome.

### Shortcuts
Bind number keys to items, magic and equipment, then press the same key in game to use or equip them.
- **Assign:** highlight an entry in Use Item, Use Magic or Equipment and press **1–9**. Reusing a key transfers its shortcut.
- **Use:** press the same key on the same entry to cycle through **(1)** (confirm), **-1-** (use immediately), then remove it.
- **Equipment:** first press assigns **-1-**, second removes it. Equips immediately with a brief fading message. Includes **Magic** and **Button**, without casting or consuming anything.
- **Settings:** clear all shortcuts.

## License

MIT. See [LICENSE](LICENSE).
