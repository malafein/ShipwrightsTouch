# Changelog

## Unreleased

Custom sail textures, any sail color, and a new customization panel. Install the mod on your server too if you want to share textures with other players.

### New
- **Customization Panel:** `Left Shift + E` on a ship now opens a panel for its name, sail color, sail texture, and owner. Changes preview live on the ship and are saved when you click Apply.
- **Any Sail Color:** Pick a preset or type any `#RRGGBB` color. Your recent custom colors are kept as swatches.
- **Custom Sail Textures:** Four historically inspired Viking sails are included. Add your own PNG or JPG images in `BepInEx/config/ShipwrightsTouch/sails` and choose them in the panel. The sail color tints the image.
- **Sail Packs:** Any `ShipwrightsTouch-Sails` folder under `BepInEx/plugins` is used, so sail packs can be installed with a mod manager.
- **Sharing:** On a server with this mod, a PNG texture you put on a ship is shared with the server, so everyone sees it. Server owners decide whether players may share, set size and count limits, and can require approval first. A server's own textures go in its `sails` folder.
- **Moderation:** Admins, and moderators listed in the server's `moderators.txt`, approve, deny, or remove shared textures in an in-game moderation panel.
- **Public Ships:** The panel can make your ship public, so anyone can customize it, or claim a public ship as yours.
- **New Settings:** `ShareMyTextures`, `ShowOtherPlayersTextures`, `CompressTextures`, `ScrollSensitivity`, the `OpenModeration` key, and the server's texture settings. See the README.

### Changes
- **RenameShip is now CustomizeShip:** same default key (`Left Shift + E`), and your saved key carries over.
- **ChangeSailColor is unbound by default** for new installs, since the panel covers it. If you had it set, it stays set.
- Left and right Shift, Ctrl, and Alt are now separate keys, as they are in the game. The default shortcuts use the left-hand keys, so if you pressed the right-hand ones, rebind the shortcut or switch hands.
- Log messages now appear in the BepInEx log under the mod's name, and routine messages are no longer logged as warnings.

## [1.1.1] - 2026-09-10

- Corrected documentation.

## [1.1.0] - 2026-09-10

- Updated for the Valheim 1.0 release, which moved the game to Unity 6. Version 1.0.2 of this mod does not work with Valheim 1.0, so updating is required.

### Changes
- **New Sail Color Keys:** Valheim 1.0 uses `G` for its new radial menu, so sail coloring has moved. Use `Alt + E` while looking at an existing ship, or `E` while placing a ship. Renaming is unchanged (`Shift + E`).
- **Configurable Keybindings:** Renaming and both sail color actions can now be rebound in the new `Controls` section of the config. The hover text always shows the keys you have set.
- **Keybinding Conflict Warning:** If one of these keybindings is also bound to a game action, a warning is written to the log.

### Fixes
- Typing a capital `E` in chat while looking at a ship's hull or mast no longer opens the rename box.

## [1.0.2] - 2026-03-22

### Features
- **Sail Coloring on Existing Ships:** You can now change the sail color of an existing ship using `Shift + G`.
- **Builder Identity & Restrictions:** The mod now optionally (enabled by default) tracks who built a ship. If tracking is enabled, only the owner can deconstruct, rename, or recolor the ship. The owner's name is also displayed on the ship's HoverText.

## [1.0.1] - 2025-12-26

- Updated README with instructions for changing sail color.

## [1.0.0] - 2025-12-25

Initial Release of Valheim Boat Customizer.

### Features
- **Ship Naming:** Rename your ships using `Shift + E` while looking at the rudder, seats, mast, or hull.
- **Universal Ship Header:** The ship's name is dynamically displayed in yellow at the top of the hover text for all components (Cargo, Rudder, Mast, Hull).
- **Sail Coloring:** Support for custom sail styles synchronized via ZDO.
- **Mod Compatibility:** Precisely targeted interactions to avoid conflicts with container-specific mods like `QuickStackStore`.
