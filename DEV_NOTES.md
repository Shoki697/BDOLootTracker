# BDO Loot Tracker — v0.12.0 Dev Test

This dev package is based on the last tested Overlay v2 Fix1 + AppIcon/HeaderLogo build.

## Included

- Storage / Transaction Maid withdrawal filtering for the packet form captured in TEST(2)
- Overlay position save fix (prevents Settings from overwriting freshly saved placement)
- Overlay position/size reset
- Overlay loot sorting:
  - Quantity
  - Last Looted
  - Total Value
  - Unit Price
  - Ascending / Descending
- Main window close choice: Exit / Cancel / Tray
- System tray mode with Open / Exit menu and double-click restore
- Optional global keybinds:
  - Start / Stop Tracking
  - Toggle Overlay
- Styled What's New window shown once per version

## Suggested test order

1. Start a session and confirm normal loot is counted.
2. Withdraw the same controlled test items through Storage / Transaction Maid and confirm they are not counted.
3. Move and resize Detailed overlay, Save, then press Settings Save and restart the app. Confirm position stays exact.
4. Repeat with Compact overlay.
5. Test Reset Position.
6. Test all four overlay sort modes in both ascending and descending order.
7. Start a session, press X, choose Tray, and confirm the session and overlay continue.
8. Restore by double-clicking the tray icon.
9. Test tray right-click Open / Exit.
10. Set global keybinds and test them while BDO is focused and while the main window is in the tray.
11. Confirm What's New appears once for v0.12.0 and not again on the next start.

## Note

The Storage filter intentionally targets only the Storage / Transaction Maid packet prefix observed in the controlled capture. Central Market and other transfer types are not guessed or filtered yet.
