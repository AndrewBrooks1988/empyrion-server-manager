# Empyrion Server Manager

[![Latest release](https://img.shields.io/github/v/release/AndrewBrooks1988/empyrion-server-manager)](https://github.com/AndrewBrooks1988/empyrion-server-manager/releases/latest) [![License: MIT](https://img.shields.io/badge/license-MIT-blue.svg)](LICENSE)

A web dashboard for running an **Empyrion – Galactic Survival** dedicated server on Windows. It replaces the server's own console window: start, stop and restart with in-game warnings, see who's online, chat, run console commands, watch the log, schedule daily/weekly maintenance and resets, manage backups, and edit the server's settings, all from a browser on the PC or (optionally) your phone.

Nothing about a particular server is built in. Everything is set up from the dashboard and stored in `manager-settings.json` next to the program.

## Features

- **Overview:** status, uptime, players online, server tick rate, playfields, CPU/RAM, next maintenance. Per player you can Message, Kick, Mute or Ban. The overview also has in-game chat (colour-coded by channel), an activity feed, known players with last-seen times, players who joined but never spawned, and a ban list with Unban.
- **Console & log:** run any server console command, and see a live, filterable server log.
- **Maintenance:** daily and weekly scheduled restarts with in-game countdown warnings, backups, and playfield wipes:
  - daily: chosen wipe types in your starter systems, plus every visited **space** sector (respawns asteroids in scenarios where they're POIs, such as Reforged Eden)
  - weekly: chosen wipe types on everything visited
  - player structures are never wiped
  - the Windows scheduled tasks are installed, updated, paused and removed from the dashboard
- **Backups:** list, back up now, and restore. A restore keeps a copy of the current world first.
- **Settings:**
  - *Setup:* server folder (auto-detect), pick or create the server config `.yaml`, turn on Telnet, headless or windowed start, dashboard title and address, SteamCMD path and **one-click server update**, optional dashboard password.
  - *Server config:* edit the dedicated server's `.yaml` (name, description, password, ports, players, blueprints, save name, scenario…). Comments and layout are preserved, and a `.bak` copy is made before every save.
  - *Game rules:* edit the active save's `gameoptions.yaml` (limits, anti-grief, difficulty…).
  - *Admins:* admin/moderator/game-master SteamIDs and login priority. Changes apply to a running server immediately.
- **Twice-daily maintenance:** optional, for busy servers (every 12 hours).
- **Roles from the player list:** make someone Player, GameMaster, Moderator or Admin straight from *Known players*. The Admins page shows in-game and Steam names next to each SteamID.
- **Auto-updates** from GitHub Releases, signature-checked.
- **Play on this PC:** restarts the server around launching the game through Steam, because Steam won't start the game while the dedicated server is running.
- **Remote access with [Tailscale](https://tailscale.com):** one click shares the dashboard to your own tailnet only, never the open internet.

Changes to server settings take effect **when the server next starts**: immediately if it's stopped, or on the next restart if it's running. The dashboard offers *Save & restart*.

## Requirements

- Windows 10/11 with the Empyrion dedicated server installed (SteamCMD app `530870`).
- Nothing else. The manager is a single self-contained `.exe`, and .NET does not need to be installed.
- Optional: SteamCMD (to update the server from the dashboard) and Tailscale (for phone/tablet access).

## Install

**Installer (recommended):** download `EmpyrionServerManager-Setup-<version>.exe` from the [latest release](https://github.com/AndrewBrooks1988/empyrion-server-manager/releases/latest) and run it.
- It installs for your Windows user only (no admin prompt) into `%LOCALAPPDATA%\Programs\Empyrion Server Manager`, with a Start-menu shortcut, an optional desktop shortcut and an optional **start when I sign in** shortcut.
- The installer isn't code-signed yet, so Windows SmartScreen may warn you: click **More info → Run anyway**.

**Portable:** download `EmpyrionServerManager-<version>-win-x64.zip`, unzip it anywhere and run `EmpyrionManager.exe`. Portable copies don't auto-update.

Then:

1. Start **Empyrion Server Manager**. A small minimised console window keeps it running, and the dashboard opens at **http://127.0.0.1:8090**.
2. Go to **Settings → Setup**:
   - **Find** (or paste) your dedicated server folder, the one containing `EmpyrionLauncher.exe`.
   - Pick the server config `.yaml` you start the server with, or **Create** a new one from the template.
   - **Turn on Telnet** if it isn't already. The manager talks to the server through its Telnet console.
   - **Save setup.**
3. Go to **Maintenance**: set the restart time, days, wipes and starter systems under *Schedule & resets*, save, then **Install task** on the daily and weekly cards.
4. Start the server from the top bar.

## Updates

Installed copies check GitHub Releases at startup and every 6 hours. When a new version is out, a banner offers **Install update**. The manager downloads the installer, **verifies its signature** against the update key built into the app, installs it silently and restarts itself. Your settings are kept. You can turn checking off, or check manually, in **Settings → About**.
## Security

- The dashboard listens on **127.0.0.1** (this PC only) by default. For other devices, use **Remote access** (Tailscale Serve). It needs no firewall changes and isn't reachable from the internet.
- If you change the dashboard address to listen on your network, **set a dashboard password** first (Settings → Setup). Requests from this PC never need it.
- The server's Telnet password stays in the server's `.yaml`. It's never shown in the dashboard or sent to the browser. **Never port-forward the Telnet port.**
- `manager-settings.json` holds your configuration and, if set, the dashboard password **hash** (PBKDF2). Don't share it.

## Files

| Path | What it is |
|---|---|
| `EmpyrionManager.exe` | The manager (web server + dashboard). |
| `wwwroot\` | Dashboard pages. |
| `scripts\Empyrion-Maintenance.ps1` | Maintenance script used by the scheduled tasks and the dashboard (`-Mode Daily|Weekly|Play|Restart|Stop`, `-Warn 5,1`, `-DryRun`). |
| `appsettings.json` | Default dashboard address and logging. |
| `manager-settings.json` | **Your** settings. Created by Setup, never included in releases, and kept by updates and uninstall. |

Logs written to the server folder: `Logs\Maintenance\YYYY-MM.log` (maintenance runs), `Logs\Manager\players.json` (player history) and `Logs\Manager\sent-messages.log` (messages sent from the dashboard). Backups go in `Backups\`.

## How it works

- Player, activity and performance data come from the server's own log (`Logs\<build>\Dedicated_*.log`). Chat comes from the save's `global.db`, read from a lock-free copy so the game is never blocked.
- Commands go through one persistent Telnet session.
- Stop, restart, play and maintenance run `scripts\Empyrion-Maintenance.ps1`, the same script the scheduled tasks use.
- Wipes are queued with the server's `wipe` command right after a restart, so they apply the next time each playfield loads.

## Building from source

Requires the .NET 10 SDK.

```
cd src
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=none -o ..\app
```

- `build.ps1` publishes into `dist\stage` and makes the portable zip. It refuses to package personal settings, backups, player data or anything that looks like a SteamID or password.
- `release.ps1` also builds the installer (Inno Setup 6) and signs it with the update key. `release.ps1 -Publish` tags the version and creates the GitHub release.

### Update signing (for forks)

Updates are signed with an ECDSA P-256 key, the same idea as Tauri's updater key. To publish your own builds:

1. `dotnet run --project tools\UpdateSigner -- genkey %USERPROFILE%\.empyrion-server-manager\update-signing-key.pem` creates a key pair and prints the public key. **Never commit the private key.**
2. Paste the public key into `PublicKeyPem` in `src\Updates.cs` (and `tools\update-public-key.pem`), and set `Updates:Repo` in `appsettings.json` or `Repo` in `Updates.cs` to your repository.
3. `release.ps1 -Publish`.

## Licence and credits

[MIT](LICENSE) © 2026 AndrewBrooks1988. Free to use, change and share.

This is an unofficial fan-made tool, not affiliated with or endorsed by Eleon Game Studios. It builds on .NET, ASP.NET Core, Microsoft.Data.Sqlite and SQLite, IBM Plex and Chakra Petch fonts, and Inno Setup, and works with SteamCMD and Tailscale. See [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md) for the full list of components, licences and knowledge sources. The dashboard's **Settings → About** page shows the same credits.