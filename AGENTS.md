# Repository instructions

This repository holds the **Processor Tycoon Multiplayer mod** in [Multiplayer/](Multiplayer/). Read [Multiplayer/AGENTS.md](Multiplayer/AGENTS.md) before working on it.

- Never hard-code the game path. Scripts dot-source `scripts/common.ps1` (`Resolve-GameDir`); projects use `$(GameDir)` / `$(GameManagedDir)` from `Directory.Build.props`.
- Generated output goes to `bin/`, `obj/` or `artifacts/` only. Decompiled game code (`reference/decomp/<version>/`) and game assemblies are never committed or shipped.
- Release zips mirror the game folder and are produced only by `Multiplayer/package.ps1`.
- Live tests run in disposable game copies (`artifacts/testbeds/`, `scripts/new-testbed.ps1`), never in someone's main game folder.
