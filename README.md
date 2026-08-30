# BDO Loot Tracker

A Windows desktop loot tracker for Black Desert Online.

## Current test version

`0.9.2`

## Requirements

- Windows 10/11 x64
- Npcap installed on the PC (required for packet capture)
- Administrator privileges are recommended for reliable capture

The GitHub Release installer is self-contained, so testers do **not** need to install the .NET Desktop Runtime separately.

## First run

1. Install Npcap.
2. Install BDO Loot Tracker with the `Setup.exe` from GitHub Releases.
3. Open **Settings** and select the active network adapter.
4. Choose the market region and run **Fetch / Update Database**.
5. Press **Start**.

## Automatic updates

This project uses [Velopack](https://velopack.io/) with public GitHub Releases.

The installed application automatically checks the repository's Releases on startup. If a newer version exists, the user can download/install it and the tracker restarts automatically.

> Auto-update only works for a real Velopack installation (the GitHub Release `Setup.exe`). Running directly from Visual Studio, `bin`, or a plain publish folder intentionally skips the updater.

The release workflow writes `update-source.json` into each installer automatically using the repository that is running the workflow. There is no GitHub token stored in the application because the repository is public.

## Creating a release

The workflow is located at:

`.github/workflows/release.yml`

Create and push a semantic version tag:

```powershell
git tag v0.9.2
git push origin v0.9.2
```

For the next release:

```powershell
git tag v0.9.3
git push origin v0.9.3
```

GitHub Actions will then:

1. restore the project,
2. publish a self-contained Windows x64 build,
3. write the public repository URL into `update-source.json`,
4. package the app with Velopack,
5. create/publish the GitHub Release and installer/update packages.

## Development build

```powershell
dotnet restore
dotnet build
```

The updater stays silent in a normal Visual Studio/dev build.

## User data

Application data is stored outside of the installation directory under the user's Local AppData folder, so an application update does not overwrite the loot database, settings, sessions, or icon cache.

## Security note

Do not commit personal Garmoth API keys, local `settings.json`, SQLite databases, or cached user data. `.gitignore` excludes these common runtime files.
