# Architecture

Target design of the Multiplayer mod. Decisions referenced as `D#` are in [DECISIONS.md](DECISIONS.md); wire details are in [PROTOCOL.md](PROTOCOL.md); game facts in [GAME_INTERNALS.md](GAME_INTERNALS.md).

## Goals and non-goals

Goals:
- 2–8 players, each running their own company, competing in one shared market (D2, D3).
- Native game UI for every player; no rebuilt screens.
- Pace: slower than single player is acceptable (≈1–2 in-game days/s).
- Survive game updates with small adapter changes; fail loudly, never drift silently.
- Coexist with other BepInEx mods (D18).

Non-goals (for now):
- Shared-company co-op, anti-cheat / untrusted players (D12), cross-version play, loading MP saves in vanilla single player, dedicated servers (possible later without protocol changes).

## Topology and ownership

```
          ┌──────── Host (slot 0) ─────────────────────┐
          │  world owner + clock + arbiter             │
          │  owns: AI companies, market globals,       │
          │  contract pool, events, its own company    │
          └──▲──────────▲──────────▲──────────▲────────┘
             │          │          │          │   ITransport (TCP dev / Steam relay)
          slot 1     slot 2     slot 3     slot N
       (owns its company, CPUs, projects, teams, factory, finances)
```

Star topology (D2): peers talk only to the host; the host forwards peer deltas. Every machine runs the **full simulation**; what differs per machine is which parts are muted and which state is overwritten by incoming deltas.

| State | Owner | On non-owners |
|---|---|---|
| A player's company: money, factory, research, teams, finances, CPUs, projects | that player | ghost `AICompany`, sim muted, overwritten by owner deltas |
| AI companies and their CPUs/projects | host | sim muted on peers, overwritten by host deltas |
| Market globals, market events, inflation, central bank, taxation | host | muted on peers where it mutates world state; see "Market" |
| Contract pool (available contracts), business contracts, architecture licences (`OwnerIDs`) | host | changes only via host commands. **Now:** the contract pool is shared and host-owned, peers send offers as commands (D43); licences and business deals are host commands too (D44, D45) |
| Player-local data: emails/notifications, preferences, wallpaper, `MoneyBalance` view | each player, never replicated | — (stored per slot in MP saves) |

Players act natively on what they own; nothing is intercepted (D3). Actions that touch host-owned shared objects (accepting a pool contract, business contracts with AI, licensing) are blocked locally and sent as **commands** to the host, which applies them and replicates the result.

## Layers

```
src/ProcessorTycoon.Mp.Core      (netstandard2.1, no Unity, no third-party deps — D22)
  Protocol/     message types, binary codec, versioning
  Session/      slots, player roster, session state machine, settings
  Clock/        day index, lag window, barriers, speed/pause state
  Delta/        JSON top-level scanner, key diff, entity hashing, world hash
  Transport/    ITransport, LoopbackTransport, TcpTransport
  Saves/        MP save container (meta + world), compression
src/ProcessorTycoon.Mp           (BepInEx plugin, netstandard2.1, publicized Assembly-CSharp — D23)
  Plugin.cs     entry, config, main-thread dispatcher
  Adapter/      the ONLY code that knows game internals
    Hooks/        Harmony patches, applied per session (D18), startup self-check
    StateReader   builds per-entity JSON from the game's SaveObject builders
    Applier       FromJsonOverwrite, create/remove per entity kind, cache refresh
    GhostCompany  spawn/remove ghost AICompany, AI takeover on disconnect
    Muting        tick-handler filtering and per-item filters (D19)
    ClockControl  drives TriggerTicks, disables dialog pause (D7), greys speed buttons (D6)
    IdRanges      slot-partitioned SaveIDs (D20)
    SaveIO        MP save load/convert (IsPlayer swap)
    Schema        SaveObject schema check against the compiled expectation
  Transport/Steam  Steam lobby + relay transport (D13), native lib loading
  Diagnostics/   drift detector, hash-mismatch reports, hook report
  UI/            lobby, roster, sync indicator, chat (later)
  Api/           public interop surface (D14)
```

Rules: Core is unit-tested offline. Adapter is versioned by game version; a game update should only touch Adapter (and GAME_INTERNALS.md). Network I/O runs on background threads; all game work runs on the Unity main thread through a queue, like the Agent mod's scheduler.

## Simulation muting (D19)

Every machine calls the game's native `DateController.TriggerTicks()`. Simulation that the machine does not own must not run, otherwise it double-applies effects before the owner's delta arrives.

1. **Handler filtering by target.** Game systems subscribe to `DateController` events; company logic subscribes as closures whose delegate `Target` is the company (`Company`, `AICompany`, `AIBehaviourController`, …). A prefix on `TriggerTicks` swaps each event's backing delegate for a filtered copy; a postfix restores it (no skip of the original, so other mods' patches still run). Filtering policy:
   - handler target is a ghost company or its AI → skip everywhere except on its owner;
   - handler target is a host-owned world system (AI controller, `CompanySpawner`, `ContractManager` generation, `MarketEventHandler`, inflation/bank/tax) → skip on peers;
   - UI handlers (`*Window`, `*UI`, graphs, top bar) → always run;
   - **unclassified target type → reported at session start** (hook report) and treated per a conservative default. This is how new systems added by game updates become visible.
2. **Per-item filters for shared iterators.** Some systems tick all companies at once (e.g. `TimerScheduler.PassDay` ticks every project). These get a prefix on the item method (e.g. project `Tick`) that skips items owned by other machines.
3. **Shared computations run everywhere.** The market (`Market` sell tick) must run on every machine because each owner needs its own sales. It writes into ghost CPUs too; those writes are discarded by the owner's next delta. Same for cross-company effects such as foundry services (one company's factory producing another's CPUs). The drift detector watches these.

## State replication (D5)

Source of truth for *what* is replicated is the game's own save builder (`SaveHandler.Save` → `SaveObject`). Anything the developers must persist for save/load is automatically in scope.

- **Entity key:** `(kind, SaveID)`, kinds: company, cpu, customHardware, project kinds, continuousProject, contract (available/active), businessContract, marketGlobals, playerSettings is excluded (player-local).
- **Serialize:** `JsonUtility.ToJson` of the game's save DTO for that entity (same converters the save uses: `SaveObjectInstantiator.InstantiateToSave`, `DataConverter.*ToSaveObject`).
- **Diff:** Core's top-level JSON scanner splits the object into key → raw value; compare with the last sent version; changed keys form a **partial JSON object**. Unchanged entities are skipped by hash.
- **Apply:** existing entity → `JsonUtility.FromJsonOverwrite(partial, target)`, which only touches keys present and preserves object identity, so UI bindings survive and no scene reload is needed. Plain `[Serializable]` model classes (Cpu, Factory, Team, Contract, …) take the partial JSON directly. MonoBehaviours (`Company`, `AICompany`, `Money`, `Market`) are never overwritten wholesale (scene references serialize as `instanceID`); they are applied through their DTO fields and plain sub-objects (`Factory`, `ResearchSector`, `ResearchModifiers`, `Finance.Reports`).
- **Create:** the game's own instantiation path (`SaveObjectInstantiator.InstantiateFromSave`, `company.AddCpu`, …) so native events/UI fire.
- **Remove:** small per-kind handlers (unschedule project, remove contract, retire/remove CPU).
- **Derived caches:** refreshed with the game's own methods after applying (e.g. market `CacheCpus`, unit-cost updates); list maintained in GAME_INTERNALS.
- **Budget:** a hot set (all human companies and their CPUs/projects, CPUs currently on sale, active contracts) is diffed daily; the cold set (history-heavy AI data) round-robins across the month; everything is covered at checkpoints.

The same engine serves joins and resyncs: a full snapshot is "all entities as upserts" or, when the scene must be rebuilt, an MP save loaded through the normal load path.

## Clock (D6, D7, D8)

- The MP clock replaces `DateController.Update` time accumulation during a session and calls `TriggerTicks()` itself. Speed comes from the host's native speed buttons; on peers those buttons are non-interactable and show the host's speed (D6).
- Native dialog pause (`PauseHandler` → `ManualPause`) is disabled during a session (D7); only the host's pause/speed controls time.
- Day loop with lag window `K` (default 2, 0 in the first slice):
  1. Host may start day `N` when every peer has acknowledged day `N−1−K`.
  2. Host sends `DayBundle(N)` = world delta as of the end of day `N−1` + forwarded peer deltas + clock state, then simulates day `N`.
  3. A peer applies `DayBundle(N)`, simulates day `N`, sends `PeerDelta(N)` (its company delta) and acknowledges `N`.
- Both host and peers therefore simulate day `N` from the same world start state; remote companies are 1…K+1 days stale, which is invisible at tycoon pace.
- A peer that falls behind catches up by running several days per frame; beyond `R` days (default 30) or after reconnect it takes a snapshot resync instead (D8).
- Effective speed is throttled so the slowest peer stays within the window.

## Checkpoints, hashes and saves (D10, D15)

- On day 1 of every month the host forces a `K=0` barrier: all peers' deltas for the last day are applied everywhere, then every machine computes the **world hash** (per-entity hashes of the canonical save JSON, sorted by key, excluding player-local state) and sends it.
- Match → every machine writes the same **checkpoint save**; play continues. Mismatch → automatic resync of the mismatching peer(s) and an automatic desync report on both sides. No user action involved.
- **MP save format** (`.ptmp`, compressed): `meta` (format, session id, game/mod/protocol versions, date, day index, world hash, host slot, settings, slots with name, platform id, company SaveID and that player's player-local blob) + `world` (SaveObject JSON with every company stored as non-player). Stored under `<persistentDataPath>/Saves/Multiplayer/<sessionId>/`, which the vanilla loader never lists (it only lists top-level `*.txt`).
- The host's checkpoint is the truth. Every peer caches the same checkpoint, so any player can later resume alone, or host the session for others; a rejoining player whose cached checkpoint hash matches skips the snapshot transfer.
- **Loading an MP save** as slot `s`: the Adapter marks slot `s`'s company as the local player (IsPlayer swap, SaveID alignment with the `Player` company object) and spawns all other human companies as ghosts; then the normal load path runs.
- New games: only the host creates the world, including the history fast-forward; peers join via snapshot (D9).

## Ghost companies (D4)

- Remote human companies are spawned as `AICompany` instances from a cloned historical template with the player's name, colour and logo, flagged as ghosts in a registry (slot, owner).
- Their AI brain and simulation are muted (see Muting). They appear everywhere an AI rival appears: company lists, market share, spreadsheets, notifications.
- On disconnect the host unmutes the ghost's AI so the AI plays the company until the player returns (D25); on return the player receives a snapshot and takes over.

## IDs (D20)

`SaveIDHandler` is a simple counter. During a session each slot allocates from its own range: `slot × 10,000,000 + n`. The host (slot 0) keeps the normal counter. After any load the Adapter re-establishes each machine's range cursor from the highest existing ID in its range.

## Session lifecycle

`Idle → Lobby → (host) World ready → Syncing (snapshot) → Running ⇄ Paused → Ending`. Handshake validates versions and mods (D21). Late join: the host creates the new player's company (starting state from the game's new-company setup), assigns a slot and sends a snapshot. Host loss: session pauses; any player can resume from the cached checkpoint as the new host (automatic migration later).

## Market (D24)

Initial rule: each owner is authoritative for its own sales, computed by the native market on every machine from (almost) identical inputs. Measure drift across 4+ players; if visible, switch to host-authoritative market results (host sends per-CPU sales; owners apply them). The switch is contained in Adapter + one message type.

## Diagnostics

- **Hook report** at session start: every patch target resolved, every tick handler target classified; failures disable MP with a readable message.
- **Schema check:** `SaveObject` fields vs the schema compiled into the Adapter; unknown fields → warning "possible drift source: X".
- **Drift detector:** before overwriting an entity with the owner's delta, record which fields the local sim changed on a non-owned entity; aggregate per field and log. After a game update this points directly at unmuted systems.
- **Desync report:** written automatically on hash mismatch (both sides' per-entity hashes, differing entity JSON, drift log, mod list, versions) to `BepInEx/mp-reports/`.

## Interop API (D14)

`ProcessorTycoonMp.Api` (static, in the plugin assembly): `IsActive`, `IsHost`, `LocalSlot`, `Players`, `CanControlTime`, `RequestPause(bool)`, `RequestSpeed(int)`, `SendChat(string)`, `event OnChat`, `RegisterChannel(name, handler)` / `Send(channel, bytes)` for other mods to replicate their own data. Other mods declare a *soft* dependency on GUID `processortycoon.multiplayer`. The Agent mod uses it to refuse or redirect its time commands during a session.

## Compatibility (D18, D21)

- Uses BepInEx's shared HarmonyX; patches exist only during a session (`UnpatchSelf` at session end), so single-player is untouched.
- Handshake requires identical game version, `Assembly-CSharp` hash, MP mod version, protocol version and identical simulation-affecting mods; known client-side mods (e.g. the Agent mod) are allow-listed.
- Steam native library is loaded explicitly from the plugin folder; the app id comes from the `SteamAppId` environment variable set before `SteamAPI.Init`, so no file is written to the game root. If the game itself ever initializes Steamworks, reuse its instance.

## As implemented (M2, 2026-09-27)

Where this section differs from the target design above, this section describes the code; the decisions are D30–D40.

- **Code map.** Core: `Delta/Json.cs` (scanner, recursive diff, merge), `Delta/Entities.cs` (kinds, tracker, FNV hashing), `Protocol/` (wire codec, messages), `Transport/` (TCP, loopback), `Session/` (`SessionBase` with mirrors + checkpoint repair, `HostSession`, `PeerSession`, `SessionRecord`, `IGameWorld`). Plugin: `Adapter/` (`GameWorld` implements `IGameWorld`; `EntityIO` capture/apply; `Hooks`; `Muting`; `Ownership`; `Ghosts`; `IdRanges`; `SaveIO`), `MpRuntime` (lifecycle), `UI/MpPanel` (IMGUI), `Diagnostics/DevControl` (scripted control), `Api/MpApi`.
- **Entities.** Company (save DTO JSON, canonical: `IsPlayer=false`, human companies `UniqueID=MPGHOST-<slot>`), Cpu (untrimmed save DTO), CustomHardware, Market (potential sales, architecture market data with canonical owner ids, PGA event). Company apply is per member: `Factory`/`ResearchModifiers`/`Divisions`/`FoundationDate` merge in place, `ResearchSector` merges per technology (entries keep their non-serialized references) and is applied first, lists are replaced, `FinancialReports` are assigned directly (never `LoadReports`).
- **Muting.** Handler owner = closure `this`, company component, or the company. Owned locally: the player company; on the host also every AI company and the companies of disconnected players (AI takeover, D25). Host-only world systems on peers: D37. Projects of companies simulated elsewhere are skipped in `TimerScheduler.PassDay`; AI decisions outside ticks (price update, bankruptcy, fabless, project cancel) are skipped for companies simulated elsewhere.
- **Clock.** `DateController.Update` and `PauseHandler.Update` are off during a session; the host runs one day per frame when all peers acked the previous day (K=0) at the host's native speed; peers step on each `DayBundle`, catching up to 8 days per frame.
- **Checkpoints.** Day 1 of each month: repair from mirrors, hash, compare at the host, `CheckpointResult`; agreement → every machine writes its checkpoint + session record (D31); mismatch → snapshot for that peer + desync reports on both sides (`BepInEx/mp-reports/<session>/<day>-slot<N>-<host|peer>/`).
- **Join / resync / resume.** Snapshot = `SaveHandler.Save` into the session folder, IsPlayer swap in the JSON, player-local data stripped (emails, balance view, active contracts), deflated; the peer writes it and loads it through `SaveHandler.Load` (3 s transition), keeping its own contracts and preferences. Resume: `MpRuntime.Resume` loads this machine's latest checkpoint and hosts with the saved roster; a player who was not the host swaps slots with the old host (the slot-ID ranges stay disjoint). Returning players are recognized by client id.
- **Measured (one PC, two instances, K=0).** 1975: ~4 days/s; 1995: ~2.3 days/s; 2031: ~2.4 days/s after D34 (host capture ~130 ms/day, peer apply ~20 ms/day). Snapshot 1975: 674 KiB → 34 KiB; 1995: 4.1 MiB → 326 KiB.

### Additions since the first slice (D41–D49)

- **Shared objects:**
  - the contract pool (D43), architecture ownership (D44) and business deals (D45) are host-owned entities;
  - peers act on them with `Command` messages;
  - deals between players go through a consent prompt (D46) on the internal mod channel `mp.deal`.
- **Muting refinement (D47):** factory handlers run for every company on every machine, so local markets see realistic rival stock. All other company handlers stay muted on non-owners.
- **Checkpoint correctness:**
  - a full resend keeps removals (D48);
  - own entities are hashed as last sent (D49);
  - the first bundle after a snapshot is never a checkpoint;
  - one full capture per checkpoint; repaired entities are re-captured.
- **Robustness:**
  - keepalive (5 s ping, 45 s timeout);
  - a join is rejected if the host cannot build the world;
  - load hooks are always installed (D42);
  - checkpoint saves are adaptive (D41).
- **Diagnostics:** session report (handler classification, unknown world systems), save schema check, D24 sales-drift metric (`salesDriftPercent`, ~4–9%).
- **Interop:** `MpApi` host/join/resume/leave/`Status()` (with `bankrupt`), `LocalBankrupt`, `RegisterChannel`/`Send` (protocol message `Channel`).

### Steam and roster ownership (D50, D51)

- **Steam transport (`Steam/`, plugin only):** `SteamGate` (no Steamworks types; TCP keeps working without Steam) → `SteamServices` (init with AppID 480, callbacks pumped from `Plugin.Update`, friends-only discovery lobby, friends-hosting list) → `SteamTransport : ITransport` (P2P by Steam id over the relay; frames fragmented by Core `Fragments`). Address `steam` hosts, `steam:<id>` joins, `steamip:` is the one-PC test mode.
- **Ghost ownership:** a company is a player's only through the roster's company ids; `MPGHOST-<slot>` companies left by an earlier session are plain AI until a new slot-N player continues them.
- **Projects (D52):** `Adapter/Projects.cs` captures the owner's in-progress projects as a carried entity, injects the latest copies into every save of the session, and restores a disconnected player's projects into the host's world for the AI.

### Players, caretaker, bankruptcy and the native UI (0.3.0, D54–D60)

- **Joining (D58):** `Hello` → the host holds a slot (company id −1) and answers `Welcome` with `NeedsCompany` and the session rules → the player founds the company on the native New Game screen (difficulty, date, cheats locked) → `CompanySetup` → the host creates a new-game company founded now and sends the snapshot. Returning players (known client id) get the snapshot at once. Leaving during setup frees the slot.
- **Away (D55):** the host simulates a disconnected player's company (it owns it, D25); for `CaretakerDays` the AI's decision components are muted for it, then `AiControl` is set and the AI plays until the player returns. `OfflineSince`/`AiControl` travel in the roster.
- **Bankrupt (D57):** the owner freezes the company (projects, products, research); nothing new can be scheduled for it; the player stays as a spectator. Other machines see `IsBankrupt` through the company entity.
- **Resync (D56):** a resynced peer keeps its own newer entities (restored after the snapshot baseline). Ownership moves forget tracker entries (D60).
- **UI (D59):** `UI/` builds everything from the game's resources on one overlay canvas (`Surface`): `Look` (fonts, sprites, theme palette), `Kit` (controls), `Window`, `MainWindow` (lobby / session: players with company, colour and status; chat), `Tray`, `MenuEntry`, `Dialogs` (credits, business proposals with every term, bankruptcy), `Notices` (native popups), `PlayerTags`, `JoinSetup`. `MpUi` ticks them from `Plugin.Update`; headless games build none.
