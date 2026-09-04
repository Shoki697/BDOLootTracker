# BDO Loot Tracker — v0.12.1 Dev Notes

## 2026-09-03 BDO packet hotfix

The post-maintenance capture showed that the BDO server connection is still TCP source port `8889`, but the loot packet changed.

Observed post-patch loot packet:

- application packet length: 3-byte little-endian value at offset `0`
- loot signature: `1E 16 01` at offset `3`
- item ID: uint32 little-endian at offset `28`
- quantity: uint64 little-endian at offset `32`
- observed packet length: `254` bytes

Validated against the supplied capture using these visible in-game events:

- Silver x490 -> item 1 / qty 490
- Elaborate Metal Part x2 -> item 44187 / qty 2
- Silver x461 -> item 1 / qty 461
- Elaborate Metal Part x1 -> item 44187 / qty 1
- Silver x1038 -> item 1 / qty 1038
- Silver x1021 -> item 1 / qty 1021
- Grunil Defense Gear Box x1 -> item 757470 / qty 1

## Parser Recovery System

Parser constants are no longer hard-coded as the only source of truth.

### Built-in fallback

`Resources/parser-default.json` is embedded in the executable and is loaded without network access.

### GitHub parser files

- `parser/manifest.json`
- `parser/eu-current.json`
- `parser/samples/`

The manifest includes the current profile URL and SHA-256. Remote JSON is treated only as data; no downloaded code is executed.

### When remote parser checks happen

Parser checks intentionally **do not run on normal application startup**.

They run only when:

1. the user presses `START` to begin a session, or
2. the user explicitly uses `Settings -> Network -> Diagnostics / Check for parser update / Auto Repair`.

If GitHub is unavailable when START is pressed, tracking continues with the current local/built-in profile.

### Auto Repair

Auto Repair:

1. downloads the manifest,
2. downloads the current parser JSON,
3. verifies SHA-256,
4. validates parser schema/offsets,
5. writes `active-profile.json`,
6. stores `last-known-good.json`,
7. optionally downloads a newer pcapng diagnostic sample when configured in the manifest.

If repair fails, the service rolls back to `last-known-good.json` when available.

Local parser state is stored under:

`%LOCALAPPDATA%\BDOLootTracker\parser\`

## Parser health popup

During an active session only, the tracker performs a conservative local health heuristic.

A warning can appear when:

- the session has been active for at least 6 minutes,
- at least 5 MB of BDO server payload has been captured,
- zero valid loot events have been decoded.

The popup offers:

- Later
- Diagnostics
- Auto Repair

This warning is shown at most once per session.

## Network Settings

The Network tab now contains two diagnostic areas:

- Npcap status
- Loot Parser status

Parser controls:

- Diagnostics — checks remote metadata without changing the active parser
- Check for parser update — installs a newer JSON profile if available
- Auto Repair — force-downloads and validates the current remote profile and performs rollback on failure

Opening Settings alone does not contact GitHub for parser diagnostics.

## pcapng fallback

For a larger BDO protocol change that cannot be described by the current JSON schema, capture a new `.pcapng` and publish it separately. Update `sampleVersion`, `sampleUrl`, and `sampleSha256` in `parser/manifest.json`.

The application can detect a newer sample and Auto Repair can cache it for diagnostics. Diagnostics then performs a conservative raw-capture validation and reports how many valid loot candidates the active profile finds in the cached sample. A packet sample is not executable and does not magically guarantee decoding of an arbitrary new protocol; a compatible JSON profile/parser schema may still need to be published.

## Release checklist

- Build solution in Visual Studio / `dotnet build`.
- Confirm Silver and normal item loot are detected.
- Confirm Garmoth-only filter still works both enabled/disabled.
- Confirm storage/maid transfer suppression has not regressed; the new packet format should be re-tested specifically for transfers.
- Confirm START performs the parser check but app launch alone does not.
- Confirm Network Diagnostics performs the remote parser check.
- Confirm Auto Repair creates `%LOCALAPPDATA%\BDOLootTracker\parser\active-profile.json` and `last-known-good.json`.
- Push `parser/manifest.json` and `parser/eu-current.json` to GitHub `main` before release so installed clients can retrieve the profile.

## v0.12.1 release-candidate test focus

> Versioning reset: the temporary v0.13.x / v0.14.x / v0.15.x development labels were never intended as public releases. They are consolidated here into the real **v0.12.1** line. Future work continues from v0.12.1.

### Update UX / changelog
- Installed builds check for new releases every two minutes while running.
- If an update already exists at startup, verify the custom `UpdateAvailableWindow` appears with `Later` and `Update now`.
- A release discovered after startup must only show the bottom-right `Update available • Click here` notification.
- After updating to v0.12.1, verify the full **v0.12.1 UI / Garmoth / Sessions Redesign** changelog appears once after restart.
- The durable pending-update marker must remain until the What's New window was actually displayed.

### Dialog consistency
- Confirm info, warning, error and confirmation prompts use the themed `AppDialogWindow`, not the native Windows MessageBox UI.

### Garmoth upload
- Verify the themed upload preview stays dark/readable in normal, hover, selection and quantity-edit states.
- Test first upload and duplicate upload warning; confirm upload time/count persists after restart.
- Editing upload quantities must not modify the original packet-tracked session loot.
- Drop Rate is local session metadata; Garmoth currently does not apply the external field.

### Session History
- Verify `All Spots` is selected by default and spot filters recalculate Total Silver, Silver/hr, Trash/hr and Total Time.
- Confirm compact session cards show main-loot icons and have Upload to Garmoth, Generate Screenshot and Delete Session actions before expansion.
- Expand sessions and verify the full loot list, Ignore action, character data, Garmoth status and editable Drop Rate.

### Session screenshots
- Verify preview supports Copy, Save As and Close.
- Pasting to Paint / Discord should preserve the complete image.
- Share card should have rounded transparent outer corners, transparent main-window branding, 2x sharp output and compact two-row stats.
- Quantity badges must remain fully inside the item tile and normal values such as `x12,641` must not clip.
- Saved Drop Rate must appear on the screenshot; unset Drop Rate must display `—`.

### Platform / parser
- `MainWindow` is intentionally Windows-only; CA1416 tray / Win32 warnings should stay resolved.
- Parser Recovery remains local at normal app startup and only performs remote checks on session START or explicit Network Diagnostics / Auto Repair actions.
- Push `parser/manifest.json` and `parser/eu-current.json` to GitHub `main` so installed clients can retrieve the remote parser profile.
