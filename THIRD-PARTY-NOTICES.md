# Third-party notices and credits

Empyrion Server Manager is MIT-licensed (see `LICENSE`). It builds on, ships with, talks to or was informed by the following.

## Shipped with the application

| Component | Author | Licence | Use |
|---|---|---|---|
| [.NET runtime and ASP.NET Core](https://github.com/dotnet) | Microsoft / .NET Foundation | MIT | Runtime and web server, bundled in the self-contained `EmpyrionManager.exe` |
| [Microsoft.Data.Sqlite](https://github.com/dotnet/efcore) | Microsoft / .NET Foundation | MIT | Reads in-game chat from a copy of the save's `global.db` |
| [SQLite](https://www.sqlite.org) (via SQLitePCLRaw / e_sqlite3) | SQLite authors, Eric Sink | Public domain / Apache-2.0 | Database engine used by Microsoft.Data.Sqlite |

## Loaded by the dashboard

| Component | Author | Licence | Use |
|---|---|---|---|
| [Chakra Petch](https://fonts.google.com/specimen/Chakra+Petch) | Cadson Demak | SIL Open Font License 1.1 | Headings (served by Google Fonts) |
| [IBM Plex Sans / IBM Plex Mono](https://github.com/IBM/plex) | IBM | SIL Open Font License 1.1 | Body text and data (served by Google Fonts) |

## Build tools

| Tool | Author | Licence | Use |
|---|---|---|---|
| [Inno Setup](https://jrsoftware.org/isinfo.php) | Jordan Russell and Martijn Laan | Inno Setup License (free, including commercial use) | Builds the Windows installer |
| [.NET SDK](https://dotnet.microsoft.com) | Microsoft | MIT | Compiles the application |

## Server mod

The optional on-screen alert mod (`mod\EmpyrionManagerAlerts`, MIT like the rest of this project) is built against Eleon's modding interface `Mif.dll`. That file is part of the Empyrion dedicated server, is **not** included in this repository or its releases, and is loaded from the server at runtime.

## Services and software it works with (not included)

- **[Empyrion – Galactic Survival](https://empyriongame.com)** and its dedicated server, © Eleon Game Studios. This is an unofficial, fan-made tool and isn't affiliated with or endorsed by Eleon. "Empyrion" is used only to describe compatibility.
- **[Reforged Eden](https://steamcommunity.com/sharedfiles/filedetails/?id=3143225812)**, the scenario by Vermillion and contributors that the manager was first built and tested with.
- **[SteamCMD](https://developer.valvesoftware.com/wiki/SteamCMD)** and **Steam Community** profiles, © Valve. Used to update the server, and to show public profile names next to SteamIDs.
- **[Tailscale](https://tailscale.com)**: optional private remote access through Tailscale Serve.
- **[GitHub Releases](https://docs.github.com/en/repositories/releasing-projects-on-github)**: update checks and downloads.

## Knowledge sources

- [Empyrion Wiki](https://empyrion.fandom.com): console, Telnet and dedicated-server settings, and the `wipe` command.
- The [Empyrion forums](https://empyriononline.com) and [Steam discussions](https://steamcommunity.com/app/383120/discussions/): behaviour of POI and asteroid regeneration, starter blocks, and dedicated-server port and setup tips.
- The game's own `gameoptions_example.yaml`, `dedicated.yaml` comments and the server's `help` command output.

## Development

Designed and built with the help of [Claude Code](https://claude.com/claude-code) (Anthropic).
