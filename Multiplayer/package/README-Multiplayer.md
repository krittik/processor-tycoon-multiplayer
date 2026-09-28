# Processor Tycoon Multiplayer (preview)

Up to 8 players, each with their own company, compete in one shared world: the same AI rivals, the same market, the same calendar. The host's game is the world; everyone plays their own company with the normal game UI.

## Requirements

- Processor Tycoon **0.2.16a5**, identical for every player.
- **BepInEx 5** (Mono, x64) installed in the game folder.
- The same version of this mod for every player. The host refuses players whose game, mod version or gameplay mods differ.

## Install

Extract the zip into the game folder (the one with `Processor Tycoon Beta.exe`). You should then have `BepInEx/plugins/ProcessorTycoon.Mp/`. Start the game: **Multiplayer** appears in the main menu, and during a game in the bottom bar (F9 opens it too).

## Host a game

Open **Multiplayer** from the main menu and choose how the others connect under **Connect through**:

- **Steam** (recommended): Steam must be running and signed in. Your Steam friends see your game under **Friends hosting** in their Multiplayer window; nobody needs your IP address or an open port. The mod uses Valve's public test app (AppID 480), so Steam shows you as playing **Spacewar**; nothing needs to be installed.
- **IP address**: players on your network, or on a shared virtual network (Tailscale, ZeroTier, Radmin VPN, Hamachi), join with your address and the port (default `27960`); the window lists your addresses. Over the internet without a virtual network, forward that TCP port on your router and give out your public IP address. The first time you host, Windows asks whether the game may use the network: allow it.

Then click **New game and host** (recommended: every player starts from the same fresh company) or **Load a save and host**. The game's own New Game or Load screen opens; hosting starts as soon as your game is running. During a game, open Multiplayer from the bottom bar and click **Host this game** instead.

Players who are not your Steam friends join a Steam session with the address in your Multiplayer window (`steam:<your Steam id>`, **Copy** button).

The session is created with these rules:

- **Time:** only the host controls the speed. Other players' speed buttons are greyed out and follow the host.
- **Dialogs:** they do not pause the game for anyone.
- **Waiting:** the game waits for the slowest player at most a few days, and completely at the start of each month.

## Join

Open **Multiplayer**, enter your name, then click your friend under **Friends hosting**, or enter the host's address (`ip:port` or `steam:<Steam id>`) and click **Join**.

The first time you join a session, the game's **New Game** screen opens as **Join Session**: the host's difficulty and date are fixed, you choose the company name, founder, colour and type (CPU, CPU fabless or foundry). Press **Join**: your company is founded now with a new game's funds and technology, and your game loads the shared world. When you rejoin later you get your company back directly.

Other players' companies carry a small person icon after their name; hover it to see who leads them.

## Saving and continuing

- **Checkpoints:** every machine saves one automatically on the 1st of each month once all players agree on the world state. They go to `Saves/Multiplayer/<session>/` and do not appear in the normal Load list. Manual saves made during a session go to the same folder.
- **Continuing a session:** any player can open **Multiplayer** → **Continue a saved session** → **Continue** to host it again from their own latest checkpoint, through the connection chosen under Connect through. The others then **Join** as usual and get their own companies back.
- **Disconnects:** a disconnected player's company carries on exactly as they left it for half a game year, then the AI plays it until they rejoin from the same installation. Everyone sees the countdown in the players list.
- **Bankruptcy:** a bankrupt company is frozen and its player can keep watching or leave; the session continues.

## Known limitations (0.3.0)

- **Steam:** invites through the Steam overlay are not available (the game is not a Steam app); use Friends hosting or the `steam:` address. Steam's own friends list "Join game" works while your game is running and the panel has been opened once.
- **Contracts:** all players and the AI compete for the same contracts; the host decides who wins.
- **Business deals:** deals with AI companies (for example foundry services) work for everyone. A deal with another player is a proposal: they get an Accept / Decline prompt. Architecture licences (such as the x86-64 agreement) work for everyone.
- **Speed:** the game runs slower than single player, about 2–10 in-game days per second depending on era and number of players.

## Troubleshooting

- **Log:** `BepInEx/LogOutput.log`; lines starting with `MP:` come from this mod.
- **Automatic repair:** if players' worlds drift apart, the host resynchronizes the affected player automatically (a short load) and writes a report to `BepInEx/mp-reports/`.
- **Reporting a problem:** open **Multiplayer** → **Diagnostics** on each affected machine. It saves a zip with the log, the settings and the reports and shows where (**Open folder**); attach those zips to a GitHub issue or share them on Discord.
