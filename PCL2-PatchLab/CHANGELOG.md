# Changelog

## Unreleased

- Replaced the previous multi-panel interface with a single-window, three-step wizard.
- Switched the UI to a charcoal palette with Windows blue accents.
- Added a milestone progress indicator and a dedicated completion page.
- Removed the in-window log panel; patch-engine log callbacks are intentionally not displayed in the UI.
- Kept `PclPatcher.cs` byte-for-byte unchanged.

- Refined the window chrome with a background-matched custom title bar and only minimize/close controls.
- Removed text labels from the three-stage progress indicator and improved spacing for a cleaner, sharper UI.
- Kept `PclPatcher.cs` unchanged.

## 2026-10-28 — UI refinement
- Equalized the minimize and close caption-button hit areas and simplified the minimize glyph.
- Added left spacing and the supplied PNG brand mark to the custom title bar.
- Embedded the supplied ICO as the executable/window icon.
- Switched the UI font to Microsoft YaHei UI and enlarged small text for improved readability.
- Requested native Windows 11 rounded corners via DWM.
