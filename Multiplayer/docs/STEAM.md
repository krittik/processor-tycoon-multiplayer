# Steam transport plan (M4)

Status: **implemented (D50), 2026-09-27.** Verified on one PC: Steam init, friends-only lobby, P2P listen socket, and full sessions (1995 save, 4 MiB snapshot fragmented, 14 months, checkpoints ok, leave/rejoin, host leave/quit noticed in 1–2 s) over the same transport on Steam IP sockets (`steamip:`). Verified on two machines with two Steam accounts (friends) on one LAN, peer headless: friends-hosting discovery, P2P join, 1975–1981 and 2031 sessions, leave/rejoin, checkpoints ok, 0 resyncs. Not yet: friends-list "Join game", a real internet path. Everything above the transport is shared with TCP.

As built (differences from the plan below): no overlay invites (the overlay is not injected when the game is not launched by Steam as app 480). Friends find the host in the panel's "Friends hosting" list (lobby found through `GetFriendGamePlayed`), through Steam's friends list "Join game" while their game runs with Steam started, or by typing `steam:<host Steam id>` (the host's panel shows it with a Copy button). Joining does not enter the lobby; the peer connects straight to the host's Steam id.

## Binding choice

| | Steamworks.NET | Facepunch.Steamworks |
|---|---|---|
| License | MIT | MIT |
| Shape | thin 1:1 wrapper of the C API (`SteamMatchmaking`, `SteamNetworkingSockets`, callbacks) | idiomatic C# (async, events) |
| Unity/Mono | made for Unity; netstandard2.0 assembly + `steam_api64.dll` / `libsteam_api.so` / `libsteam_api.bundle` | also works in Unity; bundles its own native loading |
| Risk | low; widely used in mods | larger surface, more allocations |

Recommendation: **Steamworks.NET** (NuGet `Steamworks.NET`, or the GitHub release zip). Ship `Steamworks.NET.dll` + native libraries in `BepInEx/plugins/ProcessorTycoon.Mp/` (D16: only these files are platform-specific).

## Initialization (D13)

1. Before `SteamAPI.Init()`: `Environment.SetEnvironmentVariable("SteamAppId", "480")` and `"SteamGameId"` (no `steam_appid.txt` in the game root).
2. Load the native library explicitly from the plugin folder (`LoadLibrary` / `dlopen` on the absolute path) so the game folder stays untouched.
3. If `SteamAPI.Init()` fails (Steam not running, no account), the Steam option is disabled with a message; TCP remains available.
4. Pump `SteamAPI.RunCallbacks()` from the plugin's `Update`.

## Lobby flow

- Host: `SteamMatchmaking.CreateLobby(k_ELobbyTypeFriendsOnly, 8)` → set lobby data `pt_mp_version` (protocol + mod version + game hash, D21/D38) and `pt_mp_session`.
- Invites: `SteamFriends.ActivateGameOverlayInviteDialog(lobby)`; joins arrive through `GameLobbyJoinRequested_t` / the `+connect_lobby` command-line argument.
- Peer: `JoinLobby` → read the lobby owner's SteamID → connect to it (below). Version mismatch is visible in lobby data before connecting.

## Transport mapping (`SteamTransport : ITransport`)

- Host: `SteamNetworkingSockets.CreateListenSocketP2P(0, …)`; peers: `ConnectP2P(ownerIdentity, 0, …)`. Both use the Steam relay (SDR), so no port forwarding is needed.
- Connection ids: `HSteamNetConnection` → the int peer ids of `ITransport` (host is peer 0 on the client side, as in TCP).
- Send: `SendMessageToConnection(conn, bytes, k_nSteamNetworkingSend_Reliable)`. Messages above Steam's reliable limit (512 KiB) — snapshots (D30) — are chunked in the transport (length-prefixed chunks reassembled on receive), keeping the session layer unchanged.
- Receive: `ReceiveMessagesOnConnection` in `Poll`, from the main thread (Steam callbacks run in `RunCallbacks`).
- Disconnects: `SteamNetConnectionStatusChangedCallback_t` → `NetEventKind.Disconnected` with the end reason.

## Testing

One PC cannot run two Steam clients with one account (TESTING.md). A Loopback test covers chunking. The acceptance run (ROADMAP M4) needs two machines with two Steam accounts: host, invite, join, play a few years, disconnect and rejoin.

## Routes (2026-09-27)

Steam chooses the route itself: direct (ICE, including LAN) when both ends can reach each other, otherwise through Valve's relays. The plugin logs every route change (`MP: Steam route: …`), shows current routes in `status.json` → `steam.routes`, and the dev command `route` logs Steam's detailed connection text. `Steam.ForceRelay = true` (config) or dev command `relay on` allows relays only for new connections: an internet path between machines on one LAN, and it hides players' IP addresses from each other.

Measured with the laptop (both machines on one LAN, both running a VPN): by default the connection went **direct over the LAN** (192.168.0.x, 2 ms), bypassing the VPNs. With relays forced: **Steam relay in Frankfurt (`fra`)**, 166–248 ms round trip (the VPNs add distance). At that latency a 1975 session still ran ~8.4 days/s at full speed with all checkpoints ok.
