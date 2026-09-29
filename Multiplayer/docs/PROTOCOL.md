# Protocol

Wire protocol between host and peers. Version: `MpProtocol.Version` in Core; any change here bumps it. Peers only ever talk to the host (star, D2).

## Transport contract (`ITransport`)

- Connection-oriented, reliable, ordered delivery per connection (TCP; Steam `SteamNetworkingSockets` reliable messages).
- Message boundaries preserved (TCP uses a 4-byte little-endian length prefix).
- Messages are whole (TCP frames up to 64 MiB; a snapshot is one message, D30). A transport with a smaller limit chunks internally: Steam sends fragments of at most 256 KiB, each with a one-byte header (1 = more follow, 0 = last), reassembled per connection (D50, Core `Transport/Fragments.cs`).
- Events (`Connected`, `Disconnected`, `Data`) are queued by the transport and consumed with `Poll` on the main thread.

## Envelope

```
u8   type
u32  sessionSeq      per-sender, monotonically increasing
...  body            type-specific, BinaryWriter encoding, strings UTF-8 length-prefixed
```

Payloads that carry state (deltas, snapshots) are Deflate-compressed.

## Messages

Implemented in protocol version 7 (7: `Chat` recipient for private messages, D62; 6: roster caretaker fields, company setup handshake D55/D58; 5: entity kind 7 `Projects`, carried: always sent as Upsert, stored by receivers, excluded from drift repair and checkpoint hashes, D52) (`Core/Protocol/Messages.cs`). Envelope: `u8 type`, `i32 seq`, body; strings are .NET `BinaryWriter` strings (7-bit length + UTF-8); delta lists are deflated. TCP frames are prefixed with a 4-byte little-endian length (max 64 MiB); the default port is 27960.

| Type | Dir | Body |
|---|---|---|
| `Hello` (1) | peer→host | version info (protocol, MP mod version, game version, Assembly-CSharp hash prefix, mod list — D38), player name, client id |
| `Welcome` (2) | host→peer | session id, slot, roster, needs-company flag, session rules (difficulty, current year, cheats, caretaker days) |
| `Reject` (3) | host→peer | reason text (version mismatch, full, already connected) |
| `Snapshot` (4) | host→peer | day, deflated save JSON with the receiver's company as the player (D30) |
| `Ready` (5) | peer→host | local day after loading |
| `DayBundle` (6) | host→all | day N, host speed, checkpoint flag, ResendAll flag (D35), per-slot delta lists: slot 0 = host world, announcements of new players' companies, forwarded peer deltas of day N−1 |
| `PeerDelta` (7) | peer→host | day N (acknowledges it), own-company delta |
| `CheckpointHash` (8) | peer→host | day, world hash, per-entity hashes |
| `CheckpointResult` (9) | host→peer | day, ok, detail text, differing entity keys (for the desync report) |
| `ResyncRequest` (10) | peer→host | reason (bundle out of order) |
| `Roster` (11) | host→all | players: slot, name, company id (−1 while founding), connected, client id, offline-since day (−1 when connected), AI control |
| `Chat` (12) | any | sender slot, text, recipient slot (−1: everyone). The host rebroadcasts public messages; a private one goes only to its recipient (the host relays it and never shows it unless it is the recipient) |
| `Ping`/`Pong` (13/14) | any | keepalive every 5 s; a connection silent for 45 s is dropped |
| `Leave` (15) | any | reason |
| `CompanySetup` (18) | peer→host | company name, founder, colour, company type (0 CPU, 1 fabless, 2 foundry), starting funds (i64), factory lines, starting technology — answers `Welcome.NeedsCompany` (D58) |
| `Channel` (17) | any | channel name, sender slot, bytes — other mods' data (MpApi.RegisterChannel/Send); the host rebroadcasts. This mod's own channels: `mp.deal` (a proposal: the contract's save JSON), `mp.deal-declined`, `mp.deal-expired` (the same JSON back to the proposer) |
| `Command` (16) | peer→host | kind + args for host-owned shared objects: `contract-offer` / `contract-withdraw` with `contractKey|cpuId` (D43), `architecture-owner` with the architecture id (D44), `business-add` with the deal's save JSON and `business-break` with `dealKey|breakerId` (D45), `business-add-approved` with the deal JSON after the other player accepted (D46); the result replicates through deltas |

Planned, not implemented: `CommandResult`, `PlayerLocal`.

## Delta format

```
EntityDelta { u8 kind; i32 saveId; u8 op (Upsert | Patch | Remove); string json }
```
- `Upsert`: full DTO JSON; create if missing.
- `Patch`: partial JSON object containing only changed top-level keys; applied with `FromJsonOverwrite`.
- `Remove`: no JSON.
Deltas within one message are applied in order; kinds are applied in dependency order (companies → custom hardware → CPUs → projects → contracts → market).

## Flows

As implemented (protocol 4).

**Join:**
1. `Hello` → version check (D21/D38) → `Welcome` (slot, roster).
2. The host creates a new player's company (D36) and announces it to the other peers in the next bundle.
3. `Snapshot`: the host's save JSON with the joiner's company as the player (D30). It is always sent; there is no cached-checkpoint shortcut yet.
4. The peer loads it and replies `Ready`.
5. The next bundle carries `ResendAll`. Every owner sends its full state once, still with removals (D35/D48). That bundle is never a checkpoint.

**Day loop (lag window K = 2, D27):**
- The host starts day N when every running peer has acked N−1−K.
- `DayBundle(N)` carries the host's own changes plus the peer deltas received so far, which the host applies first. The host then simulates N.
- A peer applies the bundle, simulates N, and sends `PeerDelta(N)` (acknowledging N). Pending commands (contracts, deals, licences) go out as `Command` messages.

**Checkpoint (day 1 of a month):**
1. K = 0 for that day. Every machine repairs drift from its mirrors (D32).
2. Each machine hashes its own entities as last sent and remote entities as held (D49), then sends `CheckpointHash`.
3. The host compares and answers with `CheckpointResult`, which lists the differing entity keys.
4. If all hashes agree, everyone writes a checkpoint save and the session record (D31/D41).
5. On a mismatch, both sides write desync reports and the peer receives a new snapshot.

**Catch-up:** a peer processes up to 8 queued bundles per frame. A bundle out of order → `ResyncRequest` → snapshot.

**Keepalive:** `Ping`/`Pong` every 5 s; 45 s of silence drops the connection.

**Disconnect / kick:** the host marks the slot offline in `Roster`, and the AI plays that company on the host (D25). A reconnect with the same client id reclaims the slot and gets a snapshot.

**Host loss / resume:** peers stop (the session closes; the game pauses). Any player can **Resume** from their own latest checkpoint as host. A former peer that becomes host swaps slots with the old host; other players rejoin with **Join**.

## Hashing

FNV-1a 64 over canonical per-entity JSON (the same string that is diffed), fed with each UTF-16 code unit as low byte then high byte. World hash = FNV-1a 64 over the `(kind, id, entityHash)` list sorted by kind then id. Player-local state (emails, preferences, balance view) is not replicated and not hashed.
