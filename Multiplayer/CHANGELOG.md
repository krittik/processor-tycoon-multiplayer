# Changelog — Processor Tycoon Multiplayer

## 0.3.0 "Lockstep" — 2026-09-29 (preview)

Protocol 7: every player needs this version.

- **New look:** the multiplayer window, dialogs and notices are drawn like the game's own windows (its fonts, icons and theme, light and dark). Open it from the **Multiplayer** entry in the bottom bar, the new **Multiplayer** item in the main menu, or F9. The players list shows every player with their company, colour and status.
- **Found your own company:** joining a session for the first time opens the game's New Game screen with the host's difficulty and date locked. Choose the name, founder, colour and type (CPU, CPU fabless or foundry); the company is created when you press Join, with a new game's funds and technology for the current year. Returning players get their company back as before.
- **Player companies are marked** with a person icon (and a tooltip naming the player) wherever the game shows company names.
- **Disconnects:** a disconnected player's company carries on exactly as they left it for half a game year (host setting `Session.CaretakerDays`); only then does the AI start playing it. Everyone sees a countdown and gets the game's notifications when players join, leave, return, go bankrupt or are taken over by the AI.
- **Bankruptcy:** a bankrupt player's company is frozen (projects cancelled, products retired, research stopped) and the player stays to watch, or leaves; a bankrupt host no longer ends the session.
- **Chat on screen:** messages appear at the bottom left like the Agent mod's action feed and fade out, above an input line that stays on screen. Press Enter or click it to write; hover the chat (or type) to read the history, and scroll back with the mouse wheel or Page Up / Page Down. Turn it off under **Show chat on screen** in the Multiplayer window (or `Chat.Overlay` in the config): chat then lives in the window and messages arrive as the game's notifications.
- **Private messages:** start a message with @ and a player's name (or click the envelope next to them in the players list). Only that player gets it, as a conversation in their game **Email** (Chat with …); its **Reply** button opens the chat for an answer.
- **Business proposals by email:** a deal another player proposes arrives as an email with every term (capacity, price, term, renewal, fines) and the Email window's **Accept** / **Decline** buttons, plus a notification. Nothing interrupts your game; unanswered for 30 game days, the proposal is declined automatically and the proposer is told.
- **About:** the version and release name sit in the window's footer; click them for credits, license, links to GitHub and Discord, and **Save diagnostics** / **Report an issue** for bug reports (the diagnostics zip holds the log, the settings and any desync reports; **Open folder** shows it).
- **Clearer hosting:** the Multiplayer window is organised as Host a game / Join a game / Continue a session. Choose **Steam** or **IP address** under Connection; the details (what the other players need, port forwarding, virtual networks) are in the info icons' tooltips, and the IP option shows your addresses next to the port. Saved sessions list their players, the in-game date and when you last played. From the main menu, **New game and host** or **Load a save and host** opens the game's own screen and hosts as soon as the game runs.
- **One pattern with the Agent mod:** the bottom-bar item (shown during a game; the main menu has its own entry) opens the window above it; a status line comes first, explanations sit in tooltips, a choice is a strip like the game's tabs, at most one blue button per section; the title bar's × closes a window; buttons press like the game's; the footer's version link opens About; no tooltips on the bottom bar.
- Your multiplayer name defaults to your Steam name (or "Player"), never your Windows user name.
- Built on the new [Processor Tycoon Mod API](https://github.com/krittik/processor-tycoon-mod-api) (compiled in; nothing extra to install), shared with the Agent mod.
- **For other mods:** `MpApi.LocalBankrupt` and `Status()["bankrupt"]` report a bankrupt local company; `MpApi.ChatSince(n)` and `ChatLast` (also `Status()["chatLast"]`) read the chat, public and private lines, numbered. The Agent mod 0.5.0 uses both.
- Released under the MIT license (LICENSE.txt in the plugin folder). The lobby layout was polished (compact port field, short Resume buttons).
- **Fix (D54):** closing the game's Default warning or "Select New Research" on the host paused the session for everyone.
- **Fix (D54):** on the host, every joined player's company shared the host company's "divisions"; a player selling their factory division changed the host's company and forced a resync of all players.
- **Fix (D56):** a resync no longer undoes a player's own last changes (for example production lines set just before it).
- **Fix (D60):** a player taking their company back sent stray removal messages (ignored, but wrong).

## 0.2.2 — 2026-09-27 (preview)

- **Fix (D53):** other players' companies (ghosts) no longer inherit the technologies of the AI template they are spawned from. The game upgraded their factory lines toward 300 mm wafers every day; while the AI played a disconnected player's company this cost about $50M a month and bankrupted it. Also removes the constant factory drift repair at checkpoints.
- Peers re-apply the host's speed with every day.

## 0.2.1 — 2026-09-27 (preview)

Protocol 5. Fixes found in the two-machine Steam test (see below: D52 projects, peer speed buttons).

## 0.2.0 — 2026-09-27 (preview)

- **Steam:** host and join over Steam (Valve's relay, no port forwarding, AppID 480 "Spacewar"). Friends who host appear in the panel's "Friends hosting" list; anyone can join with `steam:<Steam id>` (shown with a Copy button on the host). Steam starts when the panel opens; TCP still works without Steam. Resume offers "on Steam" and "on port".
- **Fix (D52, protocol 5):** a player's unfinished projects (CPU in development, factory expansion, custom hardware) were lost when they rejoined, were resynchronized or the session was resumed. They now travel with the player's company; while a player is disconnected the AI continues them.
- **Fix:** a joined player's speed buttons showed no selection after loading the world (the Agent mod could not read the clock), and could drift from the host's speed; peers now re-apply the host's speed with every day.
- **Fix:** hosting from a world saved after an earlier session no longer treats that session's player companies as players (they were frozen) and no longer crashes the next join with a duplicate company id; a new slot-N player continues the slot-N company instead (D51).
- Steam routes are logged and shown in `status.json`; config `Steam.ForceRelay` (dev command `relay on`) sends traffic only through Valve's relays.
- Ships `Steamworks.NET.dll` (MIT) and Valve's `steam_api64.dll` in the plugin folder.

## 0.1.0 — 2026-09-27 (preview)

First playable version for Processor Tycoon 0.2.16a5, protocol 4.

- **Play:** 2–8 players, each running their own company in one shared world (host's AI rivals, market, calendar). Direct TCP connections (default port 27960); Steam lobby/relay is planned (docs/STEAM.md).
- **Start:** host a new or loaded game (F9 → Host this game); others join by address and load the shared world. Each joining player gets a company cloned from the host's.
- **Time:** the host controls speed for everyone; native dialogs do not pause; players can trail the host by up to 2 days.
- **Consistency:** daily per-entity deltas through the game's own save format. Every month all machines repair drift, compare world hashes, and resynchronize a differing player automatically, with desync reports.
- **Saves:** monthly checkpoints on every machine, every third month in late eras. Any player can resume a saved session as host; returning players reclaim their companies. A save made after leaving a session loads as single player, with the other players' companies becoming AI rivals.
- **Disconnects:** the AI plays a disconnected player's company until they rejoin; the host can kick; keepalive detects dead connections.
- **Shared world objects:**
  - one contract pool for all players and the AI (the host decides tenders);
  - business deals with AI companies for every player;
  - deals between players need the other player's Accept;
  - architecture licences.
- **Tools and integration:**
  - IMGUI window: players with ping, chat, host addresses, deal prompts, resume list;
  - public `MpApi` for other mods (state, session control, chat, data channels);
  - optional Agent mod integration (`pt-agent mp …`).
- **Verified** on one PC with testbeds:
  - 4 players over 20 in-game years with 12 disconnect/rejoin cycles and 0 resyncs;
  - fixtures from 1975, 1995 and 2031;
  - resume and host change.
  - A two-machine test is pending.
