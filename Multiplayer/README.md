# Processor Tycoon Multiplayer

Standalone BepInEx 5 plugin adding 2–8 player competitive multiplayer to Processor Tycoon (target game version `0.2.16a5`). Every player runs the full simulation of their own company; the host owns the shared world (AI companies, market, contracts) and the clock. State is exchanged as per-entity JSON deltas produced and applied with the game's own serializer.

**Status:** 0.3.0 "Lockstep" — playable over Steam (friends, relay) or direct TCP with 2–8 players; verified with 8 players on one PC and across two machines. Player guide: [package/README-Multiplayer.md](package/README-Multiplayer.md); changes: [CHANGELOG.md](CHANGELOG.md).

## How it works (short)

- **World and players:** the host's game is the shared world. Every player simulates the full game, but only their own company. Other players' companies are ghost AI companies with their simulation muted; AI companies are simulated on the host only.
- **Joining:** a new player founds their company on the game's own New Game screen, with the host's difficulty and date locked.
- **Deltas:** every day each owner sends the changes of what it owns, as per-entity JSON produced by the game's own save converters and applied in place with `JsonUtility.FromJsonOverwrite`.
- **Clock:** the host runs the clock with a lag window of 2 days; native popups and forms do not pause a session.
- **Checkpoints:** on the 1st of each month every machine repairs drift from the owners' last known state, hashes the world and compares with the host. A mismatch resyncs the player with a snapshot of the world (the game's own save).
- **Shared objects:** contracts, business deals and licences are owned by the host; players send commands for them. Deals between players become proposals the other player accepts or declines.
- **Disconnects and bankruptcy:** a disconnected player's company is kept as is for half a game year, then the AI plays it; a bankrupt player stays as a spectator.
- Details: [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md) ("As implemented"), decisions D1–D60 in [docs/DECISIONS.md](docs/DECISIONS.md), wire format in [docs/PROTOCOL.md](docs/PROTOCOL.md).

## Layout

```
Multiplayer/
  AGENTS.md, README.md, CHANGELOG.md, LICENSE
  docs/            ARCHITECTURE, DECISIONS, PROTOCOL, ROADMAP, GAME_INTERNALS, TESTING, STEAM
  src/ProcessorTycoon.Mp.Core/   Unity-free core: JSON delta engine, hashing, wire protocol, TCP/loopback transports, host/peer sessions
  src/ProcessorTycoon.Mp/        BepInEx plugin: Adapter/ (the only code that knows game internals), UI/, Diagnostics/, Api/
  tests/ProcessorTycoon.Mp.Tests/  offline test runner for Core (fake world, loopback sessions, TCP echo)
  spikes/ProcessorTycoon.Mp.Spikes/  early experiment plugin (never packaged)
  tools/           live test scripts (deploy-testbeds, live-duo, live-multi, soak-duo, soak-multi, start-headless) and jsondiff.js
  package/         files shipped in the release zip (README-Multiplayer.md player guide)
  Multiplayer.slnx, build.ps1, package.ps1
```

## Build and test

```powershell
.\build.ps1                                                  # build all + run offline tests, no deploy
.\build.ps1 -Deploy -DeployRoot ..\artifacts\testbeds\A      # also copy plugin + spikes into a testbed
.\package.ps1                                                # release zip in ..\artifacts\dist\
```

```bash
bash tools/deploy-testbeds.sh                    # quit testbed games, offline tests, deploy to every testbed
bash tools/live-duo.sh mpfx-mid-1995 60 3        # A hosts a fixture save, B joins, run 60 s
bash tools/soak-multi.sh 10 3                    # 4 players from a new game with rejoins, 10 min
```

Testbeds, fixture saves and decompiled game source: see [docs/TESTING.md](docs/TESTING.md) and the [repository README](../README.md). Fixture saves are not part of the public repository; any save of your own works.

The plugin compiles against the game folder resolved per the [repository README](../README.md) (`-GameDir`, `PT_GAME_DIR`, or the repository's parent). `Assembly-CSharp` is publicized at compile time, so private game members are directly accessible. Steamworks.NET is fetched by `tools/fetch-steamworks.ps1` during the build.

## Relationship to the Agent mod

Independent plugin, independent versioning, no shared code. The [Agent mod](https://github.com/krittik/processor-tycoon-agent) talks to this mod through its public API `ProcessorTycoonMp.Api.MpApi` by reflection (`pt-agent mp …`, `status.multiplayer`); its CLI is also the tool for scripted multi-client tests (TESTING.md).
