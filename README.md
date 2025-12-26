# Shipwright's Touch

A Valheim mod that lets you name your ships and customize sail colors.

## Features

- **Ship Naming:** Rename your ships using `Shift + E` while looking at the rudder, seats, mast, or hull.
- **Dynamic Hover Text:** The ship's custom name is displayed in yellow at the top of the hover text for all ship parts, including storage containers.
- **Sail Coloring:** Customize your ship's sail style during construction or modification (if supported by other mods).
- **Mod Compatibility:** Specifically designed to work alongside popular mods like `QuickStackStore`. Interaction prompts are disabled on containers to ensure no conflict with storage-specific features.
- **Server Sync:** Names and colors are stored in the ship's ZDO and synchronized across the server.

## Installation

1. Ensure you have [BepInEx](https://valheim.thunderstore.io/package/denikson/BepInExPack_Valheim/) installed.
2. Place `ShipwrightsTouch.dll` into your `Valheim/BepInEx/plugins` folder.
3. Restart the game.

## Configuration

The mod generates a configuration file at `BepInEx/config/com.malafein.shipwrightstouch.cfg` after the first run.

- **AllowShipDeconstruction:** Set to `true` to allow removing ships with the hammer tool.

## Technical Details

- Uses Harmony for patching `Player`, `Ship`, `Container`, and `HoverText`.
- Custom data is stored in ZDO keys: `custom_ship_name` and `custom_sail_style`.
- Interaction logic is precisely focused to avoid conflict with container-specific mods.

## License & Development

This project is licensed under the **GNU General Public License v3.0 (GPLv3)**. You are free to modify and redistribute this mod under the same license terms.

**Development Note:** This mod was primarily developed with the assistance of **Google Antigravity** and **Gemini**. The source code is open to the community to learn from, modify, and improve.
