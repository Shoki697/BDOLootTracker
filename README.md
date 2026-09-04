# BDO Loot Tracker

A Windows desktop loot tracker for Black Desert Online.

BDO Loot Tracker uses **Npcap** to passively capture Black Desert Online network traffic and process the relevant incoming game data in real time.

![Release](https://img.shields.io/github/v/release/Shoki697/BDOLootTracker)
![Platform](https://img.shields.io/badge/platform-Windows%2010%2F11-blue)
![.NET](https://img.shields.io/badge/.NET-8.0-512BD4)

[Download the latest release](https://github.com/Shoki697/BDOLootTracker/releases)

![BDO Loot Tracker](images/main-window.png)

---

## Requirements

- Windows 10/11 x64
- Npcap installed on the PC

Npcap is required for packet capture.  
[Download Npcap](https://npcap.com/)

### Installation

1. Download the latest `Setup.exe` from GitHub Releases.
2. Install Npcap if it is not already installed.
3. Install and start BDO Loot Tracker.

### First Run

Open **Settings**, select your network adapter, market region and item language, then run **Fetch / Update Database**.  
After that, press **Start** to begin tracking.

---

## Features

- Real-time loot tracking
- Automatic grind spot detection
- Silver/hr and Trash/hr statistics
- Detailed and compact in-game overlay
- Session history
- Garmoth Grind Tracker upload
- Market prices and item icons
- Item Ignore List
- Automatic updates with in-app update notification

---

## Main Window

Track your current grinding session with live loot, silver and trash statistics.  
The current class and detected grind spot are shown at the top.

---

## In-Game Overlay

![Overlay](images/overlay-detailed.png)

Use the optional click-through overlay to keep your session statistics visible while playing.  
Detailed and Compact modes are available, and the overlay can be freely positioned and resized.

---

## Session History

![Sessions](images/sessions.png)

Previous grinding sessions are saved automatically and can be reviewed later.  
Sessions can also be uploaded directly to the Garmoth Grind Tracker.

---

## Settings

![Settings](images/settings.png)

Configure your network adapter, market region, character, overlay and Garmoth integration from one place.

---

## Ignore List

![Ignore List](images/ignore-list.png)

Items added to the Ignore List are excluded from future live tracking and calculations.

---

## Garmoth Integration

Add your Garmoth API token under **Settings → Garmoth**.  
Completed sessions can then be uploaded from the main window directly below **STOP**, or from the **Sessions** window.

---

## Disclaimer

BDO Loot Tracker is an unofficial community project and is not affiliated with or endorsed by Pearl Abyss.  
Use of third-party tools with Black Desert Online is subject to Pearl Abyss' Terms of Service and policies.

## Parser recovery (v0.14.0)

BDO packet formats can change after maintenance. v0.14.0 moves the loot packet signature, framing, item offset, and quantity offset into a SHA-256 verified JSON parser profile.

The tracker does **not** contact GitHub for parser checks just because the application was opened. Parser checks happen only when you start a tracking session or explicitly run parser diagnostics from **Settings -> Network**.

Repository parser files:

- `parser/manifest.json`
- `parser/eu-current.json`
- `parser/samples/` for optional diagnostic captures after larger protocol changes

If a parser update cannot be downloaded, the current local/built-in profile remains usable. Auto Repair keeps a last-known-good profile for rollback.

## Live updater reliability (v0.14.1)

While the application is running, installed Velopack builds check for a newer release every two minutes. Once a release is detected, the bottom-right update button remains visible even if a later background check temporarily cannot reach GitHub.

Before an update restarts the tracker, the target version is saved both in settings and in `%LocalAppData%\BDOLootTracker\pending-update.json`. After restart, What's New is opened after the main window has rendered and the marker is only consumed after the dialog was actually displayed.

## Session History and Garmoth review (v0.15.0)

Session History is organized by grind spot. **All Spots** is selected by default; selecting another spot filters its sessions and recalculates Total Silver, Silver/hr, Trash/hr and Total Time. Session cards show a compact main-loot preview and expand in place for the complete loot list and actions.

Garmoth uploads now open a review window before sending. Loot quantities can be corrected for the outgoing upload without changing the original packet-tracked session. The app records whether a session was previously uploaded and warns before a duplicate upload. Drop Rate is stored locally with the session because Garmoth's current external upload endpoint does not apply that value.

Expanded sessions can generate a shareable PNG summary with the spot, class/spec, duration, totals and loot list.


### v0.15.1 preview refinements

The Garmoth upload review window uses a compact dark loot editor and treats Drop Rate as local metadata because the current external Garmoth upload endpoint does not apply the attempted field. Session screenshots now open in a built-in preview with Copy and Save As actions, and the generated card includes the BDO Loot Tracker icon in a narrower share-friendly layout.


### v0.15.2 / v0.15.3 session refinements

The primary Session History actions — Upload to Garmoth, Generate Screenshot, and Delete Session — are available directly on the compact session card before expansion. Expanded cards now include an editable session Drop Rate field; saving it persists the value to SQLite and the same value is then reused by Garmoth review and screenshots.

Session screenshots use a compact two-row statistics layout and icon-only loot tiles with quantity badges. v0.15.3 renders the share card at 2x resolution for sharper text and icons, enlarges the application icon, and keeps normal five-digit quantities such as `x12,641` fully visible. Very large quantities are compacted to K/M/B notation only when needed to keep the card narrow.

### v0.15.4 share-card polish

The generated session share card now uses rounded transparent outer corners and the same transparent header logo as the main window. Loot quantity badges are smaller, softer and kept completely inside each item tile so the icon grid stays clean while normal five-digit quantities remain readable. The complete v0.15.x UI / Garmoth / Sessions redesign remains consolidated into the current What's New entry until the v0.15 line is released.
