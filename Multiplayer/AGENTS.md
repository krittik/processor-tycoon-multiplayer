# Multiplayer mod — agent instructions

This folder is the **Processor Tycoon Multiplayer mod** (`ProcessorTycoon.Mp`): a standalone BepInEx plugin for 2–8 players over Steam relay or TCP. Repository-wide conventions (game path resolution, artifacts, packaging): [../AGENTS.md](../AGENTS.md). The separate [Agent mod](https://github.com/krittik/processor-tycoon-agent) talks to it only through the public `MpApi`.

## Read first, in order

1. [CHANGELOG.md](CHANGELOG.md) — what the current version does.
2. [docs/DECISIONS.md](docs/DECISIONS.md) — binding decisions with rationale. Change a decision only by adding a superseding entry, never by silently diverging.
3. [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md) — the design. Code must match it or the doc must be updated in the same change.
4. As needed: [docs/PROTOCOL.md](docs/PROTOCOL.md), [docs/GAME_INTERNALS.md](docs/GAME_INTERNALS.md), [docs/TESTING.md](docs/TESTING.md), [docs/ROADMAP.md](docs/ROADMAP.md).

## Game access

The mod must read and write internal simulation state. That access is allowed, but only inside `Adapter/` (see ARCHITECTURE "Layers"). `Core/` never references Unity or `Assembly-CSharp`.

## Invariants

- Host owns the world and the clock; each player owns only their own company. No player action is intercepted except actions on shared objects (ARCHITECTURE "Ownership").
- "If it's in the save, it's in MP": replicated state is derived from the game's own `SaveObject`, not from hand-written lists.
- Harmony patches are applied only while an MP session runs and removed afterwards. Prefix/postfix only; `return false` only for muting/clock; transpilers need a DECISIONS entry. Never fork or bundle Harmony.
- Every hook target is resolved at startup; a missing target disables MP with a clear message instead of running half-patched.
- Silent drift is the main failure mode: new code that touches replication or muting must feed the drift detector / hash checks.
- Game-version-specific knowledge lives only in `Adapter/`. Record every new finding in GAME_INTERNALS.md with its decompiled file.
- Protocol changes bump `MpProtocol.Version` and update PROTOCOL.md.

## Mod API

`../ModApi/` is the vendored [Processor Tycoon Mod API](https://github.com/krittik/processor-tycoon-mod-api) (git subtree, compiled into the plugin). Change it in that repository, then `git subtree pull --prefix ModApi https://github.com/krittik/processor-tycoon-mod-api main --squash` here; never edit `ModApi/` in place.

## Working rules

- Never act on a game process you did not start. Live tests run in testbeds (`../artifacts/testbeds/A|B…`, TESTING.md).
- `build.ps1` does not deploy by default (`-Deploy -DeployRoot <testbed>`). Release zips come only from `package.ps1`. Experiments go into `spikes/`, never into `src/`.
- Decompiled game source: `../reference/decomp/<game version>/` (regenerate with `../scripts/decompile.ps1`; ignored by git); never commit or ship it.
