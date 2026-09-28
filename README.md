# Processor Tycoon Multiplayer

Competitive multiplayer for Processor Tycoon: up to 8 players, each leading their own company, in one shared world with the same AI rivals, market and calendar. Play over Steam (friends list, relayed, no port forwarding) or by IP address.

An unofficial fan mod (BepInEx 5 plugin) for game version **0.2.16a5**. Not affiliated with or endorsed by the game's developers; it contains none of the game's files.

## Features

- **Host on Steam or by address.** Friends see your game under *Friends hosting*; others join with `steam:<id>` or `ip:port`.
- **Found your own company** on the game's New Game screen (CPU, fabless or foundry) under the host's difficulty.
- **Everyone plays with the normal game UI.** Only the host controls the speed; dialogs never pause the session.
- **Shared world:** the same contracts, AI companies and market for everyone; deals between players are proposals the other player accepts or declines.
- **Drop in, drop out:** a disconnected player's company is kept for half a game year before the AI takes over; any player can resume a saved session from their monthly checkpoint.
- **Bankrupt players stay as spectators**; the session continues.
- Native-looking multiplayer window, players list, chat and notifications, in light and dark themes.

## Install

1. Install [BepInEx 5](https://github.com/BepInEx/BepInEx/releases) (Windows x64, Mono) into the game folder (the one with `Processor Tycoon Beta.exe`) and start the game once.
2. Download the latest zip from [Releases](https://github.com/krittik/processor-tycoon-multiplayer/releases/latest) and extract it into the game folder. You should have `BepInEx/plugins/ProcessorTycoon.Mp/`.
3. Start the game: **Multiplayer** appears in the main menu and in the bottom bar (F9).

Every player needs the same game version and the same mod version. How to host, join, save and continue: [player guide](Multiplayer/package/README-Multiplayer.md).

## Building from source

Prerequisites: Windows x64, .NET SDK 9, Processor Tycoon 0.2.16a5 with BepInEx 5 installed.

Clone into a short folder, for example `<game>\Dev\<repository>`: Windows limits paths to 260 characters, and a deeply nested clone or build fails with "Filename too long".

The projects compile against the game's own assemblies, which are never part of this repository. The game folder is resolved in this order: `-GameDir <path>` (scripts) or `-p:GameDir=<path>` (`dotnet build`); the environment variable `PT_GAME_DIR`; the folder containing the repository or the one above it (so `<game>\Dev\<repository>` needs no setting).

```powershell
cd Multiplayer
.\build.ps1                                         # build + offline tests (no deploy)
.\build.ps1 -Deploy -DeployRoot 'D:\Games\PT-test'  # also copy the plugin into a (test) game folder
.\package.ps1                                       # release zip in artifacts\dist\
```

Architecture, protocol, design decisions and test tooling: [Multiplayer/README.md](Multiplayer/README.md) and [Multiplayer/docs/](Multiplayer/docs/). `scripts/decompile.ps1` decompiles your local game into the git-ignored `reference/decomp/` for reading; never commit or share it.

## Related

[Processor Tycoon Agent](https://github.com/krittik/processor-tycoon-agent) lets AI agents play through the visible game; it can also drive multiplayer sessions (`pt-agent mp …`).

## Community, contributing and license

Find players, ask questions and share ideas on [Discord](https://discord.gg/YKdTjge7J2). Issues and pull requests are welcome; see [CONTRIBUTING.md](CONTRIBUTING.md). Released under the [MIT license](LICENSE).

By [Critique (Sevastyanoff)](https://discord.gg/YKdTjge7J2), in collaboration with [Claude Code](https://claude.com/claude-code) (Anthropic). Uses BepInEx (LGPL-2.1), HarmonyX (MIT) and Steamworks.NET (MIT).
