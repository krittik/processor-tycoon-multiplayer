using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using ProcessorTycoon;
using ProcessorTycoon.CompanySystem;
using ProcessorTycoonMp.Adapter;
using ProcessorTycoonMp.Core.Protocol;
using ProcessorTycoonMp.Core.Session;
using ProcessorTycoonMp.Steam;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using Paint = ProcessorTycoonMp.UI.Look.Paint;

namespace ProcessorTycoonMp.UI;

// The Multiplayer window (D59, D61): lobby (host a game / join a game / continue a session) or the running session
// (status, players, chat). Rebuilt when the mode changes; its texts refresh twice a second.
internal sealed class MainWindow
{
    private const float LabelWidth = 90;
    private readonly MpRuntime runtime;
    private readonly Plugin plugin;
    private readonly Action openAbout;
    public readonly Window Window;
    private readonly List<Action> refreshers = new();
    private HorizontalLayoutGroup? footer;
    private string mode = "";
    private float nextRefresh;
    private string friendsShown = "", playersShown = "";
    private int chatShown;
    private RectTransform? friendsList, playersList, chatList;
    private TMP_InputField? chatInput;
    private string chatDraft = "";
    // "New game and host" / "Load a save and host" from the main menu: host once the game is running.
    private bool hostWhenLoaded;

    public MainWindow(MpRuntime runtime, Plugin plugin, Action openAbout)
    {
        this.runtime = runtime;
        this.plugin = plugin;
        this.openAbout = openAbout;
        Window = new Window("Multiplayer", 480);
    }

    // Never the Windows user name: what the player typed, else their Steam name, else "Player".
    private string Name => string.IsNullOrWhiteSpace(plugin.PlayerName.Value) ? DefaultName : plugin.PlayerName.Value.Trim();
    private static string DefaultName => SteamGate.Ready && SteamGate.LocalName.Length > 0 ? SteamGate.LocalName : "Player";
    private bool ViaSteam => SteamGate.Enabled && !string.Equals(plugin.HostVia.Value, "Address", StringComparison.OrdinalIgnoreCase);
    private string HostAddress => ViaSteam ? SteamGate.AddressPrefix : plugin.HostPort.Value;
    private bool HostReady => !ViaSteam || SteamGate.Ready;

    public void TickPendingHost(RectTransform trayItem)
    {
        if (!hostWhenLoaded || !runtime.CanHost) return;
        hostWhenLoaded = false;
        if (ViaSteam && !SteamGate.TryInit(force: true)) { runtime.Log("MP: could not host on Steam: " + SteamGate.Error); return; }
        runtime.Host(HostAddress, Name);
        Window.ShowAbove(trayItem);
    }

    // The main menu's own New Game / Load Game buttons (found by their label).
    private static bool PressMenu(string label)
    {
        var button = UnityEngine.Object.FindObjectsByType<Button>(FindObjectsSortMode.None)
            .FirstOrDefault(b => b.isActiveAndEnabled && b.transform.parent != null && b.transform.parent.name == "Buttons" && b.GetComponentInChildren<TMP_Text>()?.text.Trim() == label);
        button?.onClick.Invoke();
        return button != null;
    }

    public void Refresh()
    {
        if (!Window.Visible) return;
        string m = Mode();
        if (m != mode || Look.Font != null && mode.Length == 0) Build(m);
        if (chatInput != null && chatInput.isFocused && UnityEngine.Input.GetKeyDown(KeyCode.Return)) SendChat();
        if (Time.unscaledTime < nextRefresh) return;
        nextRefresh = Time.unscaledTime + .5f;
        foreach (var r in refreshers) r();
    }

    private string Mode()
    {
        var s = runtime.Session;
        if (s == null) return runtime.Busy ? "resuming" : "lobby";
        if (s.State == SessionState.Running) return "session";
        if (s is PeerSession p && p.PendingSetup != null) return "setup";
        return s.State == SessionState.Loading ? "loading" : "connecting";
    }

    private void Build(string m)
    {
        mode = m;
        refreshers.Clear();
        friendsShown = playersShown = "";
        chatShown = 0;
        friendsList = playersList = chatList = null;
        chatInput = null;
        Kit.Clear(Window.Body);
        if (footer != null) UnityEngine.Object.Destroy(footer.gameObject);
        footer = Window.Footer();
        Kit.VersionLink(footer.transform, ModInfo.Short, openAbout);
        Kit.Size(Kit.Rect("Spacer", footer.transform), flexWidth: 1);
        switch (m)
        {
            case "lobby": BuildLobby(Window.Body); break;
            case "session": BuildSession(Window.Body); break;
            default: BuildWaiting(Window.Body, m); break;
        }
        var error = Kit.Label(Window.Body, "", 14, Paint.Negative, wrap: true);
        refreshers.Add(() => { error.text = runtime.LastError; error.gameObject.SetActive(runtime.LastError.Length > 0); });
        foreach (var r in refreshers) r();
    }

    // ---------------- lobby ----------------

    private void BuildLobby(Transform b)
    {
        SteamGate.TryInit();
        var nameRow = Kit.Row(b, 8, 28);
        Kit.Size(Kit.Label(nameRow.transform, "Your name", 15), width: LabelWidth);
        Kit.Input(nameRow.transform, "", plugin.PlayerName.Value, 32, v => plugin.PlayerName.Value = v.Trim(), DefaultName, flexWidth: 1);

        // Host: the connection is an explicit choice; its details sit in the tooltips.
        Kit.Section(b, "Host a game", "employees", "Your game becomes the shared world: one market, one set of AI rivals, one calendar. Up to 7 more players join, each with a company of their own, and you set the game speed.");
        if (SteamGate.Enabled)
        {
            var viaRow = Kit.Row(b, 8, 28);
            Kit.Size(Kit.Label(viaRow.transform, "Connection", 15), width: LabelWidth);
            Kit.Segments(viaRow.transform, new[] { "Steam", "IP address" }, () => ViaSteam ? 0 : 1, i => SetVia(i == 0 ? "Steam" : "Address"));
        }
        if (ViaSteam)
        {
            var (dot, text, row) = Kit.Status(b, 15);
            var info = Kit.Info(row.transform, "Hosting on Steam", "Your Steam friends see your game under Join a game in their Multiplayer window. Nobody needs your IP address or an open port.");
            var retry = Kit.Button(row.transform, "Retry", () => SteamGate.TryInit(force: true), height: 24, size: 14);
            refreshers.Add(() =>
            {
                Painted.Set(dot, SteamGate.Ready ? Paint.Positive : Paint.Negative);
                text.text = SteamGate.Ready ? $"Signed in to Steam as <b>{Escape(SteamGate.LocalName)}</b>" : SteamGate.Error.Length > 0 ? SteamGate.Error : "Starting Steam…";
                info.Text = SteamGate.Ready ? "Your Steam friends see your game under Join a game in their Multiplayer window. Nobody needs your IP address or an open port." : "Steam must be running and signed in. Or choose IP address.";
                retry.gameObject.SetActive(!SteamGate.Ready);
            });
        }
        else
        {
            var portRow = Kit.Row(b, 8, 28);
            Kit.Size(Kit.Label(portRow.transform, "Port", 15), width: LabelWidth);
            Kit.Input(portRow.transform, "", plugin.HostPort.Value, 6, v => plugin.HostPort.Value = v.Trim(), width: 80);
            var addresses = Kit.Label(portRow.transform, "", 14, Paint.TextLow);
            var info = Kit.Info(portRow.transform, "Your addresses", "");
            refreshers.Add(() =>
            {
                addresses.text = string.Join("  ·  ", LocalAddresses());
                info.Text = $"Players on your network, or on a shared virtual network (Tailscale, ZeroTier, Radmin VPN, Hamachi), join with one of these addresses and port {plugin.HostPort.Value}. Over the internet without one, forward TCP port {plugin.HostPort.Value} on your router to this PC and give out your public IP address.";
            });
        }
        var hostRow = Kit.Row(b, 8, 28);
        Button? hostNow = null, hostNew = null, hostLoad = null;
        if (GameWorld.CampaignLoaded) hostNow = Kit.Button(hostRow.transform, "Host this game", HostNow, cta: true);
        else
        {
            hostNew = Kit.Button(hostRow.transform, "New game and host", () => HostAfter("New Game"), cta: true);
            hostLoad = Kit.Button(hostRow.transform, "Load a save and host", () => HostAfter("Load Game"));
        }
        var pending = Kit.Row(b, 8, 24);
        Kit.Size(Kit.Label(pending.transform, "Hosting starts as soon as your game is running.", 14, Paint.Positive), flexWidth: 1);
        Kit.Button(pending.transform, "Cancel", () => hostWhenLoaded = false, height: 24, size: 14);
        refreshers.Add(() =>
        {
            if (hostNow != null) hostNow.interactable = HostReady && runtime.CanHost;
            if (hostNew != null) hostNew.interactable = HostReady && !hostWhenLoaded;
            if (hostLoad != null) hostLoad.interactable = HostReady && !hostWhenLoaded;
            pending.gameObject.SetActive(hostWhenLoaded);
        });

        // Join: friends hosting on Steam, or any address.
        Kit.Section(b, "Join a game", "browser", "Join a Steam friend's game from the list, or enter the address the host gives you: ip:port, or steam:<id> for a Steam host who is not your friend. The first time you join a session you found your company on the game's New Game screen, under the host's difficulty.");
        if (SteamGate.Enabled)
        {
            friendsList = (RectTransform)Kit.Column(b, 4, name: "Friends").transform;
            refreshers.Add(RefreshFriends);
        }
        var joinRow = Kit.Row(b, 8, 28);
        Kit.Size(Kit.Label(joinRow.transform, "Address", 15), width: LabelWidth);
        Kit.Input(joinRow.transform, "", plugin.JoinAddress.Value, 64, v => plugin.JoinAddress.Value = v.Trim(), "ip:port or steam:<id>", flexWidth: 1);
        Kit.Button(joinRow.transform, "Join", () => runtime.Join(plugin.JoinAddress.Value, Name), width: 70);

        // Continue: host a saved session again through the connection chosen above.
        var saved = runtime.Resumable();
        if (saved.Count == 0) return;
        Kit.Section(b, "Continue a session", "load", "Host a session you played before again: its players rejoin their own companies. It uses the connection chosen under Host a game.");
        foreach (var entry in saved.Take(3))
        {
            var row = Kit.Row(b, 8, 38);
            var texts = Kit.Column(row.transform, 0);
            Kit.Size(texts, flexWidth: 1);
            var others = SessionRecord.Parse(entry.record).Players.Where(p => p.ClientId != runtime.ClientId).Select(p => Escape(p.Name)).ToList();
            Kit.Label(texts.transform, others.Count == 0 ? "Only you" : "With " + string.Join(", ", others), 15);
            string date = DateTime.TryParseExact(entry.checkpoint.Substring(Math.Max(0, entry.checkpoint.Length - 10)), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d.ToString("d MMM yyyy", CultureInfo.InvariantCulture) + "  ·  " : "";
            Kit.Label(texts.transform, $"{date}played {entry.written.ToString("d MMM, HH:mm", CultureInfo.InvariantCulture)}", 13, Paint.TextLow);
            string id = entry.sessionId;
            var resume = Kit.Button(row.transform, "Continue", () => runtime.Resume(id, HostAddress, Name));
            refreshers.Add(() => resume.interactable = HostReady && runtime.Session == null && !runtime.Busy);
        }
    }

    private void SetVia(string via)
    {
        if (string.Equals(plugin.HostVia.Value, via, StringComparison.OrdinalIgnoreCase)) return;
        plugin.HostVia.Value = via;
        mode = "";   // rebuild the lobby for the other connection
    }

    private void HostNow()
    {
        if (ViaSteam && !SteamGate.TryInit(force: true)) return;
        runtime.Host(HostAddress, Name);
    }

    private void HostAfter(string menuButton)
    {
        if (!Surface.InMenu || !PressMenu(menuButton)) { runtime.Log("MP: open the main menu to start or load a game to host"); return; }
        hostWhenLoaded = true;
        Window.Close();
    }

    private void RefreshFriends()
    {
        if (friendsList == null) return;
        var friends = SteamGate.Ready ? SteamGate.FriendsHosting() : Array.Empty<HostingFriend>();
        string shown = SteamGate.Ready + string.Join("|", friends.Select(f => f.SteamId + f.Version));
        if (shown == friendsShown) return;
        friendsShown = shown;
        Kit.Clear(friendsList);
        if (friends.Count == 0)
        {
            Kit.Label(friendsList, SteamGate.Ready ? "No Steam friends are hosting right now." : "Start Steam to see your friends' games.", 14, Paint.TextLow);
            return;
        }
        foreach (var f in friends)
        {
            var row = Kit.Row(friendsList, 8, 28);
            Kit.Icon(row.transform, "person", 18, Paint.TextLow);
            var name = Kit.Label(row.transform, $"{Escape(f.Name)}  <size=13><color=#{ColorUtility.ToHtmlStringRGB(Look.Of(Paint.TextLow))}>mod {f.Version}</color></size>", 15);
            Kit.Size(name, flexWidth: 1);
            ulong id = f.SteamId;
            Kit.Button(row.transform, "Join", () => runtime.Join(SteamGate.AddressOf(id), Name), cta: true, width: 70);
        }
    }

    // ---------------- waiting ----------------

    private void BuildWaiting(Transform b, string m)
    {
        var (_, text, _) = Kit.Status(b);
        var detail = Kit.Label(b, "", 14, Paint.TextLow, wrap: true);
        refreshers.Add(() =>
        {
            text.text = m switch
            {
                "resuming" => "Loading the saved session…",
                "setup" => "Found your company",
                "loading" => "Loading the shared world…",
                _ => runtime.Session?.State == SessionState.Handshake ? "Connected, waiting for the host…" : "Connecting…",
            };
            detail.text = m == "setup" ? "On the game setup screen: the host's difficulty and date apply to everyone; your company is created when you press Join there." : "";
            detail.gameObject.SetActive(detail.text.Length > 0);
        });
        Kit.Button(footer!.transform, "Cancel", runtime.Leave);
    }

    // ---------------- session ----------------

    private void BuildSession(Transform b)
    {
        var s = runtime.Session!;
        var (dot, headline, statusRow) = Kit.Status(b);
        var details = Kit.Info(statusRow.transform, "Session", "");
        Painted.Set(dot, Paint.Positive);
        var clock = Kit.Label(b, "", 14, Paint.TextLow);
        refreshers.Add(() =>
        {
            string host = s.Players.FirstOrDefault(p => p.Slot == 0)?.Name ?? "the host";
            headline.text = s.IsHost ? (runtime.OnSteam ? "Hosting on Steam" : "Hosting, direct connection") : $"In {Escape(host)}'s session" + (runtime.OnSteam ? " on Steam" : "");
            bool repairing = s is PeerSession pp && pp.LastCheckpointResult.Contains("mismatch");
            clock.text = repairing ? "Repairing a difference between the worlds…" : ClockText(s);
            Painted.Set(clock, repairing ? Paint.Negative : Paint.TextLow);
            details.Text = $"Session {s.SessionId}. Last checkpoint: {DateOf(s.LastCheckpointDay)}; resyncs: {s.Resyncs}. Checkpoints keep every player's world the same and let the session continue later.";
        });

        if (s.IsHost)
        {
            var invite = Kit.Row(b, 8, 26);
            Kit.Size(Kit.Label(invite.transform, "Invite", 15), width: LabelWidth);
            var inviteText = Kit.Label(invite.transform, "", 14, Paint.TextLow);
            Kit.Size(inviteText, flexWidth: 1);
            var copy = Kit.Button(invite.transform, runtime.OnSteam ? "Copy Steam ID" : "Copy", () => GUIUtility.systemCopyBuffer = InviteAddress(), height: 24, size: 14);
            if (runtime.OnSteam) Tip.On(copy.gameObject, "Steam ID", "For players who are not your Steam friends: they join with this address.");
            refreshers.Add(() => inviteText.text = runtime.OnSteam ? (SteamGate.LobbyOpen ? "Your Steam friends see this game" : "Opening the Steam lobby…") : InviteAddress());
        }

        var spectator = Kit.Label(b, "Your company is bankrupt. You stay in the session and watch the others.", 14, Paint.Negative, wrap: true);
        refreshers.Add(() => spectator.gameObject.SetActive(Bankruptcy.LocalSpectator));

        var playersHeader = Kit.Section(b, "Players", "employees");
        playersList = (RectTransform)Kit.Column(b, 2, name: "Players").transform;
        refreshers.Add(() =>
        {
            playersHeader.text = $"Players  <size=14><color=#{ColorUtility.ToHtmlStringRGB(Look.Of(Paint.TextLow))}>{s.Players.Count(p => p.Connected)} online · {s.Players.Count} of {HostSession.MaxPlayers}</color></size>";
            RefreshPlayers(s);
        });

        Kit.Section(b, "Chat", "marketing_icon");
        chatList = Kit.Scroll(b, 104);
        var chatRow = Kit.Row(b, 8, 28);
        chatInput = Kit.Input(chatRow.transform, "", chatDraft, 200, v => chatDraft = v, "Message everyone (Enter to send)", flexWidth: 1);
        Kit.Button(chatRow.transform, "Send", SendChat, width: 70);
        refreshers.Add(RefreshChat);

        Kit.Button(footer!.transform, "Leave session", runtime.Leave);
    }

    private void SendChat()
    {
        if (chatDraft.Trim().Length == 0) return;
        runtime.SendChat(chatDraft);
        chatDraft = "";
        if (chatInput != null) { chatInput.SetTextWithoutNotify(""); chatInput.ActivateInputField(); }
        RefreshChat();
    }

    private void RefreshChat()
    {
        if (chatList == null) return;
        var lines = runtime.ChatLines;
        if (chatShown > lines.Count) { Kit.Clear(chatList); chatShown = 0; }
        if (lines.Count == 0 && chatList.childCount == 0) Kit.Label(chatList, "No messages yet.", 14, Paint.TextLow);
        if (chatShown == lines.Count) return;
        if (chatShown == 0) Kit.Clear(chatList);
        for (; chatShown < lines.Count; chatShown++)
        {
            string line = lines[chatShown];
            int colon = line.IndexOf(": ", StringComparison.Ordinal);
            string text = colon > 0 ? $"<b>{Escape(line.Substring(0, colon))}</b>: {Escape(line.Substring(colon + 2))}" : Escape(line);
            Kit.Label(chatList, text, 15, wrap: true);
        }
        Canvas.ForceUpdateCanvases();
        var scroll = chatList.GetComponentInParent<ScrollRect>();
        if (scroll != null) scroll.verticalNormalizedPosition = 0;
    }

    private void RefreshPlayers(SessionBase s)
    {
        if (playersList == null) return;
        int day = GameWorld.CampaignLoaded ? runtime.World.Day : 0;
        string shown = string.Join("|", s.Players.Select(p => $"{p.Slot}{p.Connected}{p.AiControl}{p.CompanyId}{Status(s, p, day).text}{CompanyOf(p)?.Name}"));
        if (shown == playersShown) return;
        playersShown = shown;
        Kit.Clear(playersList);
        int i = 0;
        foreach (var p in s.Players.OrderBy(p => p.Slot))
        {
            var back = Kit.Fill(Kit.Rect("Player " + p.Name, playersList), i++ % 2 == 0 ? Paint.Row : Paint.RowAlt, Kit.Rounded);
            Kit.Size(back, height: 40);
            var row = back.gameObject.AddComponent<HorizontalLayoutGroup>();
            row.padding = new RectOffset(8, 8, 3, 3);
            row.spacing = 8;
            row.childAlignment = TextAnchor.MiddleLeft;
            row.childControlWidth = row.childControlHeight = true;
            row.childForceExpandWidth = row.childForceExpandHeight = false;
            var company = CompanyOf(p);
            var swatch = Kit.Fill(Kit.Rect("Colour", back.transform), Paint.Clear);
            UnityEngine.Object.Destroy(swatch.GetComponent<Painted>());
            swatch.color = company != null ? company.Color : Look.Of(Paint.Line);
            Kit.Size(swatch, 14, 14);
            string low = ColorUtility.ToHtmlStringRGB(Look.Of(Paint.TextLow));
            string tags = (p.Slot == 0 ? "  <size=13>host</size>" : "") + (p.Slot == s.LocalSlot ? "  <size=13>you</size>" : "");
            string companyName = company != null ? Escape(company.Name) : "founding a company…";
            var names = Kit.Label(back.transform, $"<b>{Escape(p.Name)}</b><color=#{low}>{tags}</color>\n<size=14><color=#{low}>{companyName}</color></size>", 15);
            Kit.Size(names, height: 36, flexWidth: 1);
            var (text, paint) = Status(s, p, day);
            var status = Kit.Label(back.transform, text, 14, paint, TextAlignmentOptions.MidlineRight);
            Kit.Size(status, width: Mathf.Min(190, status.GetPreferredValues(text).x + 4));
            if (s is HostSession host && p.Slot != 0 && p.Connected)
            {
                int slot = p.Slot;
                Kit.Button(back.transform, "Kick", () => host.Kick(slot, "removed by the host"), height: 24, size: 14);
            }
        }
        LayoutRebuilder.ForceRebuildLayoutImmediate(playersList);
    }

    private (string text, Paint paint) Status(SessionBase s, PlayerInfo p, int day)
    {
        var company = CompanyOf(p);
        if (p.CompanyId < 0) return ("setting up", Paint.TextLow);
        if (company != null && company.IsBankrupt) return ("bankrupt · watching", Paint.Negative);
        if (p.Connected)
        {
            float ping = s is HostSession h && p.Slot != 0 ? h.PingMs(p.Slot) : -1f;
            return (ping >= 0 ? $"online · {ping:0} ms" : "online", Paint.Positive);
        }
        if (p.AiControl) return ("away · the AI plays", Paint.TextLow);
        int caretaker = s is HostSession hs ? hs.CaretakerDays : s is PeerSession ps ? ps.Rules.CaretakerDays : 182;
        int left = Math.Max(0, caretaker - (day - p.OfflineSince));
        return (left >= 45 ? $"away · AI in {left / 30} mo" : $"away · AI in {left} days", Paint.TextLow);
    }

    private static ICompany? CompanyOf(PlayerInfo p) => p.CompanyId >= 0 && GameWorld.CampaignLoaded ? DataFinder.FindCompany(p.CompanyId) : null;

    private string ClockText(SessionBase s)
    {
        if (s is HostSession h)
        {
            if (h.WaitingForPeers) return "Waiting for " + h.WaitingFor;
            int speed = runtime.World.Speed;
            return speed <= 0 ? "Paused by you" : $"Running at speed {speed}";
        }
        var p = (PeerSession)s;
        return p.HostSpeed <= 0 ? "Paused by the host" : $"The host runs speed {p.HostSpeed}";
    }

    private static string DateOf(int day) => day < 0 ? "not yet" : new DateTime(1970, 1, 1).AddDays(day).ToString("d MMM yyyy", CultureInfo.InvariantCulture);

    private string InviteAddress()
    {
        if (runtime.OnSteam) return SteamGate.AddressOf(SteamGate.LocalId);
        return string.Join(", ", LocalAddresses().Select(a => a + ":" + plugin.HostPort.Value));
    }

    private static IEnumerable<string> LocalAddresses()
    {
        try
        {
            var list = System.Net.Dns.GetHostAddresses(System.Net.Dns.GetHostName())
                .Where(a => a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork && !System.Net.IPAddress.IsLoopback(a) && !a.ToString().StartsWith("169.254.", StringComparison.Ordinal))
                .Select(a => a.ToString()).ToList();
            return list.Count > 0 ? list : new List<string> { "127.0.0.1" };
        }
        catch { return new[] { "127.0.0.1" }; }
    }

    private static string Escape(string s) => s.Replace("<", "‹").Replace(">", "›");
}
