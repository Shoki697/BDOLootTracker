# Installer / prerequisite changes (0.10.2)

## Npcap prerequisite check

Npcap is intentionally **not bundled** with BDO Loot Tracker.

On the first launch after Velopack installation, the app checks the Npcap installation using the registry location documented by Npcap:

`HKLM\\SYSTEM\\CurrentControlSet\\Services\\npcap\\Parameters`

It also checks the standard `%SystemRoot%\\System32\\Npcap` DLL directory as a fallback.

If Npcap is missing, the user is offered a button to open the official Npcap download page. The Start button checks again and will not begin packet capture until Npcap is available.

## Start Menu / Desktop shortcuts

Velopack's Windows `Setup.exe` is a one-click installer and creates **both a Start Menu shortcut and a Desktop shortcut by default**. There is no checkbox wizard in the standard Velopack Setup.exe.

If optional shortcut checkboxes are required later, use a custom WiX/MSI installer or another installer frontend instead of the normal one-click Setup.exe.

## Version number

The main window now displays the executing assembly version beside `BDO Loot Tracker`, e.g. `v0.10.2`.

GitHub Actions sets the assembly/file/package version from the release version, so the number shown in the UI automatically follows the version entered in `Run workflow` or the pushed `vX.Y.Z` tag.
