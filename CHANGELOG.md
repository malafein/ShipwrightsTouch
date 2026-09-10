# Changelog

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
