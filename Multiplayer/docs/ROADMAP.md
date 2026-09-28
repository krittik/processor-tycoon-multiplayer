# Roadmap

Work on the current milestone only; record status in the table below. A milestone is done when its acceptance criteria are verified, not when code exists.

**Status 2026-09-29 (0.3.0 "Lockstep"; changes in [CHANGELOG.md](../CHANGELOG.md)):**

| Milestone | Status |
|---|---|
| M0 | Done. |
| M1 | Done (offline acceptance run). |
| M2 | Done: 14 years of play on one PC; CPU releases seen by others; resync; resume on either machine; verified on two machines. |
| M3 | Mostly done: 4 players over 17 years with 10 rejoins; 8 Agent-driven players over 10 game years; K=2; caretaker then AI takeover; late join with company setup; resume with a different host; host commands for contracts, licences and deals with AI; proposals for deals between players; bankrupt spectators. Still open: D24 drift measurement, automatic host migration. |
| M4 | Implemented (D50): Steam P2P relay transport, friends-only discovery lobby, panel. Verified on one PC over Steam IP sockets and on two machines with two Steam accounts (STEAM.md). |
| M5 | Partial: interop API, Agent integration, chat, session report and schema check, soak scripts, native-style UI and notifications. |
| M6 | Partial: `package.ps1` zip with player guide and license; GUID fixed; public repository. |

## M0 — Spikes (validate the risky assumptions)

Each spike is a throwaway experiment implemented as an `ISpike` in `spikes/ProcessorTycoon.Mp.Spikes` (D28), run in testbeds A/B (D29) on the fixture saves; record results in GAME_INTERNALS.md.

| Spike | Question | Pass criterion |
|---|---|---|
| S1 Overwrite | Does `FromJsonOverwrite` on live `Cpu`, `Factory`, `Contract` update open windows without reopening? Do nested objects merge or get replaced? | Price/name change visible in open Production/Market windows; nested semantics documented |
| S2 Two instances | Can two game copies run at once on one PC and talk over localhost TCP? Shared `persistentDataPath`/PlayerPrefs side effects? | Both run; echo messages; side effects documented |
| S3 Muting | Can event invocation lists be filtered per target (D19)? Which handler targets exist (full inventory)? | Ghost company's handlers skipped, UI handlers run; inventory table complete |
| S4 Market consistency | Same save, same inputs, two instances, no input: do market sales match day by day? | Identical sales for 1 in-game year, or divergence cause identified |
| S5 Cost | Time to build per-entity JSON and hash for a late save (~14 MB) | Hot set < 20 ms/day, full world < 500 ms |
| S6 Ghost spawn | Spawn an `AICompany` for a non-historical company with custom name/colour/logo, AI off | Appears in company, market share and spreadsheet windows; no errors for 1 year |
| S7 IsPlayer swap | Load a save where another company becomes the local player (SaveID alignment with `Player`) | Player UI shows the swapped company; its CPUs and projects work |

## M1 — Core

Delta engine (JSON scanner, key diff, hashing), protocol codec, session and clock state machines (lag window, barriers, catch-up), Loopback and TCP transports, MP save container. Offline tests for each.
Acceptance: test runner green; two in-process sessions over Loopback run a scripted 2-year day loop with fake entities, including a forced mismatch and resync.

## M2 — First playable slice (2 players, TCP, K=0)

Hooks with startup self-check, clock control (host speed, greyed peer buttons, dialog pause off), muting, ghost companies, slot IDs, StateReader/Applier for companies, CPUs, projects, contracts, join by snapshot, monthly checkpoint + hash + auto resync, MP saves and resume, drift detector, desync reports.
Acceptance: two test copies on one PC play 5 in-game years; both release CPUs and see each other in market share; every checkpoint hash matches or resyncs automatically; saved session resumes on either machine.

## M3 — N players and resilience

Lag window K=2, star forwarding for up to 8 players, host commands for shared objects (pool contracts, business contracts, licences), AI takeover on disconnect, late join, rejoin via cached checkpoint, resume with a different host, market drift measurement (decide D24).
Acceptance: 4 clients (Agent-CLI driven) play 10 in-game years with random disconnects/rejoins; no unresolved desync.

## M4 — Steam

Steam lobby, invites, relay transport (AppID 480), native library loading, lobby UI and roster, pause/speed indicator.
Acceptance: two machines on different networks complete the M2 acceptance run over Steam relay.

## M5 — Integration and polish

Public interop API, Agent-mod soft integration (time commands during MP), chat, schema check, soak-test harness, player-facing docs.
Acceptance: Agent mod works on a peer without breaking sync; soak test runs unattended and reports drift.

## M6 — Release

Packaging (plugin + Steam libs, no game files), install docs, version matrix, compatibility notes, public GUID frozen.
