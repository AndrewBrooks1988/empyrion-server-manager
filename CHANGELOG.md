# Changelog

## 2.1.0

First public release.

**New**
- Windows installer (per-user, so no admin prompt). It adds Start-menu and optional desktop shortcuts, an option to start with Windows, and an uninstall entry.
- **Automatic updates** from GitHub Releases. The dashboard shows when a new version is out and installs it in one click. Updates are only installed if their signature matches the project's update key.
- **Twice-daily maintenance:** optionally run the daily maintenance again 12 hours after the restart time.
- **Set a player's server role** (Player / GameMaster / Moderator / Admin) straight from the Known players list. It applies live with `setrole` and is saved to `adminconfig.yaml`.
- **Admin names:** the Admins page shows each SteamID's in-game name and Steam profile name.
- **About page:** version, update settings, licence and credits.
- The dashboard can open in your browser automatically when the manager starts.

## 2.0.0

- Everything configurable from the dashboard: Setup, Server config, Game rules, Admins, schedule and wipes. Nothing server-specific is built in.
- The scheduled tasks are installed, updated and removed from the dashboard.
- Optional dashboard password for devices other than the host PC.
- One-click server update through SteamCMD.

## 1.x

- Initial private version: status, players, chat, console, log, maintenance, backups and Tailscale sharing.
