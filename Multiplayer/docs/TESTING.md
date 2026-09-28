# Testing

## Levels

1. **Offline Core tests** — `tests/ProcessorTycoon.Mp.Tests`, a dependency-free runner (`dotnet run`; exit code 0 = pass), same style as the Agent mod's tests. Cover the delta engine, codec, clock/lag window, session state machine, save container, and in-process multi-session runs over `LoopbackTransport`. Run by `build.ps1`.
2. **In-game spikes** — M0 experiments in a test copy; results go to GAME_INTERNALS.md.
3. **Local multi-instance** — two or more game copies on one PC over TCP localhost.
4. **Soak** — scripted long runs driven by the Agent CLI against each instance; checks checkpoint hashes, drift log, desync reports.
5. **Two machines** — Steam relay (M4) and final acceptance runs by the user.

## Testbeds (D29)

Disposable game copies under the repository's ignored `artifacts/testbeds/`, never the main game folder (it may be in use by Agent-mod live tests):

```powershell
# from the repository root; PT_GAME_ZIP = official game zip (source of truth)
.\scripts\new-testbed.ps1 -Name A -AgentPort 17617 [-Force]   # host
.\scripts\new-testbed.ps1 -Name B -AgentPort 17618 [-Force]   # peer
.\Multiplayer\build.ps1 -Deploy -DeployRoot .\artifacts\testbeds\A   # MP plugin + spikes into a testbed
.\artifacts\testbeds\A\pt-agent.cmd status                          # each testbed has its own Agent CLI
```

A testbed = clean game from the zip + BepInEx 5 (copied from the main install) + optionally the newest Agent package with its own API port. `TESTBED.md` in each records its origin; `-Force` recreates it (only folders carrying that marker are deleted).

Shared between all copies (same Windows user): the save folder (`%USERPROFILE%/AppData/LocalLow/CoffeeHeaven/Processor Tycoon Beta/Saves`) and Unity PlayerPrefs. MP saves are namespaced by session id; test tooling must additionally include an instance tag in MP save paths when two copies run on one machine. Whether the game allows two simultaneous instances is spike S2.

## Fixture saves

`fixtures/saves/` (repository root): `mpfx-early-1975`, `mpfx-mid-1995`, `mpfx-late-2031` (Normal, cheats off; details in `fixtures/README.md`). `scripts/install-fixtures.ps1` copies them into the shared save folder so every copy can load them.

## Rules

- Never act on a game process you did not start in this session (lesson from the Agent mod's incident where a test quit a live game).
- Tests must not call commands that affect a real game process unless they started it.
- Existing vanilla saves are the user's; copy fixtures into test locations instead of modifying them.
- Use the fixture saves; add new fixtures instead of changing existing ones.

## Steam

One PC cannot run two Steam clients with the same account, and Steam refuses a P2P connection to your own account, so relay tests need two machines/accounts. On one PC, `steamip:` exercises the Steam transport itself (Steam sockets, fragments, status callbacks, linger) over IP: dev commands `steam` (start Steam), `host steamip:27961 Hosty` on A, `join steamip:127.0.0.1:27961 Peery` on B. `host steam` on one PC checks init, the listen socket and the lobby (`status.json` → `steam`).

## Live two-instance runs (scripts)

From the repository root, with testbeds A and B created and the fixtures installed:

```bash
bash Multiplayer/tools/deploy-testbeds.sh                 # quits testbed games, runs offline tests, deploys to A and B
bash Multiplayer/tools/live-duo.sh mpfx-late-2031 60 3    # A loads the fixture and hosts, B joins, host clock at speed 3 for 60 s
node Multiplayer/tools/jsondiff.js <host report>.json <peer report>.json   # compare desync reports
```

`live-duo.sh` prints both `mp-dev/status.json` files (date, session state, checkpoint result, resyncs, drift repairs, timings, per-kind profile) and new log problems. Drive further steps by appending lines to `<testbed>/mp-dev/cmd.txt` (`host`, `join`, `resume`, `leave`, `chat`, `speed`, `load`, `quit`; see `Diagnostics/DevControl.cs`) and gameplay through each testbed's Agent CLI (`<testbed>/tools/pt-agent.exe game ...`).

Verified this way (2026-09-27): join by snapshot; months of agreeing checkpoints on the 1975, 1995 and 2031 fixtures; a peer developing and releasing a CPU that the host sees (competitor popup, market catalog); disconnect → AI takeover → rejoin; resume from checkpoints; host change.

## Headless peers

`tools/start-headless.ps1 -GameDir <game copy> -Join <address> -Name <name>` starts the game with `-batchmode -nographics` (no window, GPU or input) at below-normal priority and joins through `mp-dev/cmd.txt`; the plugin caps the loop at `Headless.FrameRate` (30) and mutes audio. The whole session (snapshot load, daily deltas, checkpoints) works headless; the IMGUI panel does not exist, so drive it with dev commands and read `mp-dev/status.json`. Stop with `quit` in `cmd.txt`. Useful for a second machine that someone else is using.

On a second Windows machine reached over SSH, a game started from the SSH shell runs as another user in session 0 and cannot reach the desktop user's Steam client. `tools/remote-headless-task.ps1 -GameDir <copy> -User <MACHINE\desktop user>` (admin) starts it through a scheduled task in the desktop user's session instead, still headless; remove the task with `-Action remove`.

## Dev commands added in 0.3.0

In `<testbed>/mp-dev/cmd.txt`: `join <address> <name> [type 0|1|2] [company name…]` (founds the company automatically, D58), `join-ui <address> <name>` (founds it on the native setup screen; drive it with the Agent CLI `game session-new-preview`/`session-new-start`), `panel on|off`, `credits`, `close-ui`, `preview-deal`, `preview-bankrupt`, `debug-money <cash>` (tests only: forces a default/bankruptcy), `native-pause` (what closing the Default warning does; must not pause a session), `uidump <name filter>` (hierarchy, sprites, colours and fonts of native UI into `mp-dev/uidump-<filter>.txt`). Host config `Session.CaretakerDays` shortens the caretaker for tests. `Debug.Log` output goes to the game's `Player.log`, the mod's own log lines to `BepInEx/LogOutput.log`.
