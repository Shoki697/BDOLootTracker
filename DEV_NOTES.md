# BDO Loot Tracker — v0.13.0 Dev Test

This dev package continues from v0.12.0 Settings Tabs.

## Included

- Main-window Garmoth quick upload:
  - New Garmoth panel directly below STOP
  - Uploads the most recent completed session
  - Disabled while tracking / while an upload is running
  - Existing Session History upload remains available
- Live update notification:
  - Silent update check on startup
  - Re-check every 30 minutes while the tracker is running
  - Small `Update available • vX.Y.Z` button in the bottom status bar
  - Clicking it downloads the newest Velopack update and restarts the app
  - An active session is stopped/saved before update installation
- Changelog restart fix:
  - The target update version is saved as `PendingChangelogVersion` before restart
  - After the updated executable starts, What's New is forced once for that version
  - Closing What's New marks it as seen and clears the pending marker
- Optional Garmoth loot filter:
  - Settings → Garmoth → `Show and track only loot items known by Garmoth`
  - Enabled by default for new/migrated settings
  - Uses the local `GrindSpotDrops` cache populated by Fetch / Update Database
  - If the Garmoth drop cache is empty/unavailable, tracking fails open and keeps all loot rather than losing a session
- Shared loot sorting:
  - Quantity / Last Looted / Total Value / Unit Price
  - Ascending / Descending
  - The same setting now controls both the main-screen Loot List and the overlay
- Npcap diagnostics in Settings → Network:
  - Installed / not installed
  - File version when detectable
  - Capture adapter availability / Ready state
  - Recheck button

## Suggested test order

1. Open Settings → Network and confirm Npcap reports `Installed • Ready` on a working PC. Press Recheck.
2. Open Settings → Garmoth and verify the new Garmoth-only loot checkbox saves/restores correctly.
3. With the checkbox ON, run Fetch / Update Database once, start a test session, and verify unrelated packet item IDs do not appear.
4. Turn the checkbox OFF, start a new session, and verify all detected loot behaves like the previous version.
5. Change Overlay → Loot list sort by / Sort order and save. Confirm the main Loot List and overlay use exactly the same ordering.
6. Complete a short session. Confirm `UPLOAD LAST SESSION` becomes enabled under STOP.
7. Upload the latest completed session from the main window and verify the confirmation + success/error handling.
8. Confirm the existing upload button in Session History still works.
9. Test update UI from an installed Velopack build with a newer GitHub release available. The bottom status bar should show the update button without a startup popup.
10. Click the update button. If tracking is active, confirm the session is saved/stopped before installation.
11. After updater restart, confirm What's New opens automatically for the newly installed version exactly once.
12. Restart again and confirm What's New does not appear a second time.

## Notes

- Automatic update UI is intentionally hidden when running directly from Visual Studio/bin/publish because Velopack reports that build as not installed.
- The Garmoth-only filter uses locally cached Garmoth grind-spot drop relationships. Run Fetch / Update Database before evaluating the filter.
