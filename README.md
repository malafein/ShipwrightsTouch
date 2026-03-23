# Shipwright's Touch

A Valheim mod that lets you name your ships and customize sail colors.

## Features

- **Ship Naming:** Rename your ships using `Shift + E` while looking at the rudder, seats, mast, or hull.
- **Dynamic Hover Text:** The ship's custom name is displayed in yellow at the top of the hover text for all ship parts, including storage containers.
- **Sail Coloring:** Customize your ship's sail style during construction or modification. Press `G` while placing a ship with your Hammer, or `Shift + G` on an existing ship.
- **Builder Identity & Restrictions:** When a ship is constructed, the builder's identity is recorded. Only the builder (owner) can rename, recolor, or deconstruct the ship. The owner's name is displayed in the hover text.
- **Mod Compatibility:** Specifically designed to work alongside popular mods like `QuickStackStore`. Interaction prompts are disabled on containers to ensure no conflict with storage-specific features.
- **Server Sync:** Names, colors, and owners are stored in the ship's ZDO and synchronized across the server.

## Installation

### Thunderstore / r2modman (Recommended)
- Install via Thunderstore Mod Manager or r2modman.  
-or-  
- Download the mod from [Thunderstore](https://thunderstore.io/c/valheim/p/malafein/ShipwrightsTouch/), and follow the Manual Installation instructions below.

### Nexus Mods / Vortex
- Install via Vortex Mod Manager.  
-or-  
- Download the mod from [Nexus Mods](https://www.nexusmods.com/valheim/mods/3200), and follow the Manual Installation instructions below.

### Manual Installation
1. Install [BepInExPack Valheim](https://valheim.thunderstore.io/package/denikson/BepInExPack_Valheim/).
2. Download the latest release of Shipwright's Touch.
3. Extract the `ShipwrightsTouch.dll` file into your `<Valheim Install Folder>\BepInEx\plugins` directory.

## Configuration

The mod generates a configuration file at `BepInEx/config/com.malafein.shipwrightstouch.cfg` after the first run.

- **AssignBuilderIdentity:** Set to `true` (default) to assign your character as the owner when constructing a ship, restricting modifications to yourself.
- **AllowShipDeconstruction:** Set to `true` to allow removing ships with the hammer tool.

## Technical Details

- Uses Harmony for patching `Player`, `Ship`, `Container`, and `HoverText`.
- Custom data is stored in ZDO keys: `custom_ship_name`, `custom_sail_style`, `shipwrightstouch.builder_id`, and `shipwrightstouch.builder_name`.
- Interaction logic is precisely focused to avoid conflict with container-specific mods.

## License & Development

This project is licensed under the **GNU General Public License v3.0 (GPLv3)**. You are free to modify and redistribute this mod under the same license terms.

**Development Note:** This mod was primarily developed with the assistance of **Google Antigravity**, **Gemini**, and **Claude**. The source code is open to the community to learn from, modify, and improve.
