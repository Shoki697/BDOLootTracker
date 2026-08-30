# Installer notes – v0.10.4

## Shortcuts
Velopack's Windows Setup.exe is a one-click installer. It does not show a checkbox wizard.
It creates shortcuts in **Desktop** and **StartMenuRoot** by default.

## Npcap
The application checks for Npcap after installation / at startup and before starting packet capture.
If Npcap is missing, the user is offered the official Npcap download page.

## Version
The GitHub workflow writes Version, AssemblyVersion and FileVersion during `dotnet publish`.
The UI reads the installed executable FileVersion, so a workflow build with version `0.10.4` displays `v0.10.4`.

## Release
Use GitHub Actions → Build and Release → Run workflow and enter a version greater than the latest published Velopack version.
