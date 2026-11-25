# Better Bags (Server Edition)

A server edition of [Better Bags by yayo1](https://mods.vintagestory.at/betterbags). All credit for Better Bags goes to yayo1, so please follow and support the original.

**What's different in this edition**

- It installs on the server, so every player joining gets it automatically. (The original is client-only, so servers don't send it to players.)
- The bag rows only show while your inventory is open. Use `.bagsmode` to show them all the time, like the original.

**Features (from yayo1's Better Bags)**

- Tab opens only the crafting grid. Shift+Tab (or `.bagsinv`) opens the full inventory.
- Every bag you wear gets its own floating row of slots. Drag the thin bar on top to move it anywhere.
- `.bagsmenu` opens the settings: width, height, slot size, transparency, borders and colours, and horizontal or vertical layout for each bag.
- `.bags` hides or shows the rows, and `.bagsreset` puts them back in place.

Don't install this alongside the original Better Bags. Use one or the other.

## Download

ModDB (1-click install): https://mods.vintagestory.at/betterbagsse

## Report a problem

Please report bugs on the [Issues](../../issues) tab and include your game version, the mod version and your log. Security problems: see [SECURITY.md](SECURITY.md).

**Where to find your log**

The logs are in the Vintage Story data folder, in a folder called `Logs`:

| System | Data folder |
|---|---|
| Windows | `%appdata%\VintagestoryData` (paste that into the File Explorer address bar) |
| Linux | `~/.config/VintagestoryData` |
| Mac | `~/Library/Application Support/VintagestoryData` |

- **Playing on a server:** send `client-main.log` from your own game. If you run the server, also send `server-main.log` from the server's data folder.
- **Singleplayer:** `client-main.log` and `server-main.log` are both in the data folder above.
- Attach the file to the issue (drag it into the box), or paste just the lines with errors. Reproduce the problem first, then grab the log straight afterwards, so the error is near the end.
