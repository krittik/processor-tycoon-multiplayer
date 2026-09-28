using System;
using System.Collections.Generic;
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

// The Multiplayer window (D59): lobby (host a game / join a game / continue a saved session) or the running session
// (players, chat). Rebuilt when the mode changes; its texts refresh twice a second.
internal sealed class MainWindow
{
    private readonly MpRuntime runtime;
    private readonly Plugin plugin;
    private readonly Action openCredits;
    private readonly Action<string> showDiagnostics;
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

    public MainWindow(MpRuntime runtime, Plugin plugin, Action openCredits, Action<string> showDiagnostics)
    {
        this.runtime = runtime;
        this.plugin = plugin;
        this.openCredits = openCredits;
        this.showDiagnostics = showDiagnostics;
        Window = new Window("Multiplayer", 500);
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
        switch (m)
        {
            case "lobby": BuildLobby(Window.Body); break;
            case "session": BuildSession(Window.Body); break;
            default: BuildWaiting(Window.Body, m); break;
        }
        var error = Kit.Label(Window.Body, "", 15, Paint.Negative, wrap: true);
        refreshers.Add(() => { error.text = runtime.LastError; error.gameObject.SetActive(runtime.LastError.Length > 0); });
        BuildFooter(m == "session");
        foreach (var r in refreshers) r();
    }

    // ---------------- lobby ----------------

    private void BuildLobby(Transform b)
    {
        SteamGate.TryInit();
        Kit.Input(b, "Your name", plugin.PlayerName.Value, 32, v => plugin.PlayerName.Value = v.Trim(), DefaultName);

        // Host: the transport is an explicit choice, and each one says what the other players need.
        Section(b, "Host a game", "employees");
        Kit.Label(b, "Your game becomes the shared world: the same market, AI rivals and calendar for everyone. Up to 7 more players join, each with a company of their own.", 15, Paint.TextLow, wrap: true);
        var viaRow = Kit.Row(b, 6, 28);
        Kit.Size(Kit.Label(viaRow.transform, "Connect through", 15), width: 130);
        if (SteamGate.Enabled) Kit.Button(viaRow.transform, "Steam", () => SetVia("Steam"), cta: ViaSteam, height: 26, size: 15);
        Kit.Button(viaRow.transform, "IP address", () => SetVia("Address"), cta: !ViaSteam, height: 26, size: 15);
        var how = Kit.Label(b, "", 15, wrap: true);
        HorizontalLayoutGroup? steamRetryRow = null;
        if (ViaSteam)
        {
            steamRetryRow = Kit.Row(b, 6, 28);
            Kit.Button(steamRetryRow.transform, "Retry Steam", () => SteamGate.TryInit(force: true), height: 26, size: 15);
        }
        else
        {
            var portRow = Kit.Row(b, 8, 28);
            Kit.Size(Kit.Label(portRow.transform, "Port", 15), width: 130);
            Kit.Input(portRow.transform, "", plugin.HostPort.Value, 6, v => plugin.HostPort.Value = v.Trim(), width: 90);
            var addresses = Kit.Label(b, "", 15, Paint.TextLow, wrap: true);
            refreshers.Add(() => addresses.text = "Your addresses on this PC: " + string.Join(",  ", LocalAddresses()));
        }
        var hostRow = Kit.Row(b, 8, 30);
        Button? hostNow = null, hostNew = null, hostLoad = null;
        if (GameWorld.CampaignLoaded) hostNow = Kit.Button(hostRow.transform, "Host this game", () => HostNow(), cta: true);
        else
        {
            hostNew = Kit.Button(hostRow.transform, "New game and host", () => HostAfter("New Game"), cta: true);
            hostLoad = Kit.Button(hostRow.transform, "Load a save and host", () => HostAfter("Load Game"));
        }
        var pending = Kit.Row(b, 8, 26);
        Kit.Size(Kit.Label(pending.transform, "Hosting starts as soon as your game is running.", 15, Paint.Positive), flexWidth: 1);
        Kit.Button(pending.transform, "Cancel", () => hostWhenLoaded = false, height: 24, size: 14);
        refreshers.Add(() =>
        {
            how.text = ViaSteam
                ? (!SteamGate.Ready ? (SteamGate.Error.Length > 0 ? SteamGate.Error : "Starting Steam…") + " Steam must be running and signed in; or choose IP address."
                    : $"Signed in to Steam as <b>{Escape(SteamGate.LocalName)}</b>. Your Steam friends see your game under Friends hosting in their Multiplayer window; nobody needs your IP address or an open port.")
                : $"Players on your network, or on a shared virtual network (Tailscale, ZeroTier, Radmin VPN, Hamachi), join with your address and the port below. Over the internet without one, forward TCP port {plugin.HostPort.Value} on your router to this PC and give out your public IP address.";
            steamRetryRow?.gameObject.SetActive(SteamGate.Enabled && !SteamGate.Ready);
            if (hostNow != null) hostNow.interactable = HostReady && runtime.CanHost;
            if (hostNew != null) hostNew.interactable = HostReady && !hostWhenLoaded;
            if (hostLoad != null) hostLoad.interactable = HostReady && !hostWhenLoaded;
            pending.gameObject.SetActive(hostWhenLoaded);
        });

        // Join: friends on Steam, or any address.
        Section(b, "Join a game", "browser");
        if (SteamGate.Enabled)
        {
            Kit.Label(b, "Friends hosting on Steam", 15);
            friendsList = Kit.Scroll(b, 76);
            refreshers.Add(RefreshFriends);
        }
        var joinRow = Kit.Row(b, 8, 53);
        joinRow.childAlignment = TextAnchor.LowerLeft;
        var address = Kit.Input(joinRow.transform, "Or join by address (ip:port, or steam:<id>)", plugin.JoinAddress.Value, 64, v => plugin.JoinAddress.Value = v.Trim(), "192.168.1.10:27960");
        Kit.Size(address.transform.parent.GetComponent<LayoutElement>() ?? address.transform.parent.gameObject.AddComponent<LayoutElement>(), flexWidth: 1);
        Kit.Button(joinRow.transform, "Join", () => runtime.Join(plugin.JoinAddress.Value, Name), cta: true, width: 80);
        Kit.Label(b, "The first time you join a session you found your company on the game's New Game screen, under the host's difficulty.", 14, Paint.TextLow, wrap: true);

        // Continue: host a saved session again through the connection chosen above.
        var saved = runtime.Resumable();
        if (saved.Count > 0)
        {
            Section(b, "Continue a saved session", "load");
            Kit.Label(b, "Host a session you played again; its players rejoin their own companies. Uses the connection chosen above.", 14, Paint.TextLow, wrap: true);
            foreach (var entry in saved.Take(3))
            {
                var row = Kit.Row(b, 6, 28);
                var text = Kit.Label(row.transform, $"{entry.sessionId}  <color=#{ColorUtility.ToHtmlStringRGB(Look.Of(Paint.TextLow))}>{entry.written.ToString("d MMM HH:mm", System.Globalization.CultureInfo.InvariantCulture)}</color>", 15);
                Kit.Size(text, flexWidth: 1);
                string id = entry.sessionId;
                var resume = Kit.Button(row.transform, "Continue", () => runtime.Resume(id, HostAddress, Name), height: 26, size: 15);
                refreshers.Add(() => resume.interactable = HostReady && runtime.Session == null && !runtime.Busy);
            }
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
        string shown = string.Join("|", friends.Select(f => f.SteamId + f.Version));
        if (shown == friendsShown && friendsList.childCount > 0) return;
        friendsShown = shown;
        Kit.Clear(friendsList);
        if (friends.Count == 0)
        {
            Kit.Label(friendsList, SteamGate.Ready ? "No friends are hosting right now." : "Start Steam to see friends' games.", 15, Paint.TextLow);
            return;
        }
        foreach (var f in friends)
        {
            var row = Kit.Row(friendsList, 6, 28);
            Kit.Icon(row.transform, "person", 18, Paint.TextLow);
            var name = Kit.Label(row.transform, $"{f.Name}  <size=13><color=#{ColorUtility.ToHtmlStringRGB(Look.Of(Paint.TextLow))}>mod {f.Version}</color></size>", 15);
            Kit.Size(name, flexWidth: 1);
            ulong id = f.SteamId;
            Kit.Button(row.transform, "Join", () => runtime.Join(SteamGate.AddressOf(id), Name), cta: true, width: 64, height: 26, size: 15);
        }
    }

    // ---------------- waiting ----------------

    private void BuildWaiting(Transform b, string m)
    {
        var text = Kit.Label(b, "", 16, Paint.Text, wrap: true);
        Kit.Space(b, 4);
        refreshers.Add(() => text.text = m switch
        {
            "resuming" => "Loading the saved session…",
            "setup" => "Found your company on the game setup screen. The host's difficulty and date apply to everyone; the company is created when you press Join there.",
            "loading" => "Loading the shared world…",
            _ => runtime.Session?.State == SessionState.Handshake ? "Connected. Waiting for the host…" : "Connecting…",
        });
        var row = Kit.Row(b);
        row.childAlignment = TextAnchor.MiddleRight;
        Kit.Button(row.transform, "Cancel", runtime.Leave);
    }

    // ---------------- session ----------------

    private void BuildSession(Transform b)
    {
        var s = runtime.Session!;
        var headline = Kit.Label(b, "", 18, Paint.Header);
        var invite = InfoRow(b, "Invite", out var inviteValue);
        var copy = Kit.Button(invite.transform, "Copy", () => GUIUtility.systemCopyBuffer = InviteAddress(), height: 24, size: 14);
        var clockRow = InfoRow(b, "Clock", out var clock);
        var syncRow = InfoRow(b, "Sync", out var sync);
        refreshers.Add(() =>
        {
            string host = s.Players.FirstOrDefault(p => p.Slot == 0)?.Name ?? "the host";
            headline.text = (s.IsHost ? "Hosting" : $"In {host}'s session") + (runtime.OnSteam ? "  ·  Steam" : "  ·  Direct connection");
            inviteValue.text = s.IsHost ? InviteText() : $"Session {s.SessionId}";
            copy.gameObject.SetActive(s.IsHost);
            clock.text = ClockText(s);
            sync.text = $"checkpoint {DateOf(s.LastCheckpointDay)}, {s.Resyncs} resync{(s.Resyncs == 1 ? "" : "s")}" + (s is PeerSession pp && pp.LastCheckpointResult.Contains("mismatch") ? " (repairing)" : "");
        });

        var spectator = Kit.Label(b, "", 15, Paint.Negative, wrap: true);
        refreshers.Add(() =>
        {
            spectator.gameObject.SetActive(Bankruptcy.LocalSpectator);
            spectator.text = "Your company is bankrupt. You stay in the session and watch the others; leaving is always possible.";
        });

        var playersHeader = Section(b, "Players", "employees");
        playersList = Kit.Scroll(b, 184);
        refreshers.Add(() =>
        {
            playersHeader.text = $"Players  <size=15><color=#{ColorUtility.ToHtmlStringRGB(Look.Of(Paint.TextLow))}>{s.Players.Count(p => p.Connected)} online, {s.Players.Count}/{HostSession.MaxPlayers}</color></size>";
            RefreshPlayers(s);
        });

        Section(b, "Chat", "marketing_icon");
        chatList = Kit.Scroll(b, 104);
        var chatRow = Kit.Row(b, height: 30);
        chatRow.childAlignment = TextAnchor.LowerLeft;
        chatInput = Kit.Input(chatRow.transform, "", chatDraft, 200, v => chatDraft = v, "Message everyone (Enter to send)");
        Kit.Size(chatInput.transform.parent.GetComponent<LayoutElement>() ?? chatInput.transform.parent.gameObject.AddComponent<LayoutElement>(), flexWidth: 1);
        Kit.Button(chatRow.transform, "Send", SendChat, width: 70);
        refreshers.Add(RefreshChat);
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
        if (lines.Count == 0 && chatList.childCount == 0) Kit.Label(chatList, "No messages yet.", 15, Paint.TextLow);
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
            var names = Kit.Label(back.transform, $"<b>{Escape(p.Name)}</b><color=#{low}>{tags}</color>\n<size=14><color=#{low}>{companyName}</color></size>", 16);
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
            if (h.WaitingForPeers) return "waiting for " + h.WaitingFor;
            int speed = runtime.World.Speed;
            return speed <= 0 ? "paused by you" : $"running at speed {speed}";
        }
        var p = (PeerSession)s;
        return p.HostSpeed <= 0 ? "paused by the host" : $"the host runs speed {p.HostSpeed}";
    }

    private string DateOf(int day) => day < 0 ? "not yet" : new DateTime(1970, 1, 1).AddDays(day).ToString("d MMM yyyy", System.Globalization.CultureInfo.InvariantCulture);

    private string InviteText() => runtime.OnSteam ? (SteamGate.LobbyOpen ? "shown to your Steam friends" : "opening the Steam lobby…") : InviteAddress();

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

    // ---------------- shared pieces ----------------

    private TextMeshProUGUI Section(Transform b, string title, string icon)
    {
        Kit.Space(b, 2);
        var row = Kit.Row(b, 6, 24);
        Kit.Icon(row.transform, icon, 20, Paint.Header);
        var header = Kit.Header(row.transform, title);
        Kit.Size(header, flexWidth: 1);
        return header;
    }

    private static HorizontalLayoutGroup InfoRow(Transform b, string title, out TextMeshProUGUI value)
    {
        var row = Kit.Row(b, 8, 24);
        var t = Kit.Label(row.transform, title + ":", 16);
        Kit.Size(t, width: 70);
        value = Kit.Label(row.transform, "", 16, Paint.Text, TextAlignmentOptions.MidlineLeft);
        Kit.Size(value, flexWidth: 1);
        return row;
    }

    // Same pattern as the Agent mod's window: version link (About) on the left, actions and Close on the right.
    private void BuildFooter(bool inSession)
    {
        footer = Window.Footer();
        Kit.VersionLink(footer.transform, ModInfo.Short, openCredits);
        var spacer = Kit.Rect("Spacer", footer.transform);
        Kit.Size(spacer, flexWidth: 1);
        Kit.Button(footer.transform, "Diagnostics", () =>
        {
            try { showDiagnostics(Diagnostics.DiagnosticsBundle.Create()); }
            catch (Exception e) { runtime.Log("MP: could not save diagnostics: " + e.Message); }
        }, height: 28, size: 15);
        if (inSession) Kit.Button(footer.transform, "Leave session", runtime.Leave, height: 28, size: 15);
        Kit.Button(footer.transform, "Close", Window.Close, height: 28, size: 15);
    }

    private static string Escape(string s) => s.Replace("<", "‹").Replace(">", "›");
}
