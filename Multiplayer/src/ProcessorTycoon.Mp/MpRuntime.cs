using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using BepInEx.Bootstrap;
using BepInEx.Logging;
using ProcessorTycoon;
using ProcessorTycoon.TimeSystem;
using ProcessorTycoonMp.Adapter;
using ProcessorTycoonMp.Core;
using ProcessorTycoonMp.Core.Protocol;
using ProcessorTycoonMp.Core.Session;
using ProcessorTycoonMp.Core.Transport;
using ProcessorTycoonMp.Steam;

namespace ProcessorTycoonMp;

// Session lifecycle: Harmony patches exist only while a session runs (D18).
internal sealed class MpRuntime
{
    // Client-side mods that do not affect the simulation (D21 allow-list).
    private static readonly HashSet<string> ClientSideMods = new() { MpProtocol.PluginGuid, "processortycoon.multiplayer.spikes", "local.processortycoon.mod" };

    private readonly ManualLogSource logger;
    private readonly string clientId;
    private readonly List<string> recent = new();
    private readonly List<ChatLine> chat = new();
    private int chatSeq;
    private SessionBase? reportedSession;

    public MpRuntime(ManualLogSource logger, string modVersion, string clientId)
    {
        this.logger = logger;
        this.clientId = clientId;
        World = new GameWorld(BuildVersion(modVersion), Log);
    }

    public GameWorld World { get; }
    public SessionBase? Session { get; private set; }
    public string LastError { get; private set; } = "";
    public IReadOnlyList<string> Recent => recent;
    // Chat of the running session, public and private lines (at most 100).
    public IReadOnlyList<ChatLine> ChatLines => chat;
    // The number of the latest chat line; numbers keep growing across sessions (MpApi.ChatSince).
    public int ChatLast => chatSeq;
    // Public messages (sender slot, text), for other mods (MpApi.Chat).
    public event Action<int, string>? Chat;
    // Every line, public, private or from the mod itself (unknown @name).
    public event Action<ChatLine>? ChatLine;
    // D58: the host asks this new player to set up their company (the UI opens the native game setup).
    public event Action<SessionRules>? SetupRequested;
    public int CaretakerDays { get; set; } = 182;

    // The running session travels over Steam (D50) rather than TCP.
    public bool OnSteam { get; private set; }
    public ITransport? Transport { get; private set; }
    private float nextRouteCheck;
    private ITransport? hosted;
    private string hostedAddress = "";

    // Where a game copy on this PC (an agent's companion) joins the session this game hosts (D64); "" when not hosting.
    public string LocalAddress => Session is not HostSession || hosted == null ? ""
        : hosted is DualTransport dual ? dual.LocalAddress
        : "127.0.0.1:" + TcpTransport.Parse(hostedAddress, "0.0.0.0").port;
    private string lastRoutes = "";

    public bool CanHost => Session == null && !Busy && GameWorld.CampaignLoaded;
    // Between Resume and the end of its load there is no session object yet.
    public bool Busy => Session == null && Ownership.Active;

    public void Host(string address, string playerName)
    {
        if (Session != null) { Fail("Already in a session."); return; }
        if (!GameWorld.CampaignLoaded) { Fail("Start or load a game first; the host's game becomes the shared world."); return; }
        try
        {
            var transport = CreateTransport(address, host: true);
            Begin(isHost: true);
            var host = new HostSession(transport, World, playerName, clientId) { CaretakerDays = CaretakerDays };
            SaveIO.SessionId = host.SessionId;
            Attach(host);
            host.Start(string.IsNullOrWhiteSpace(address) ? TcpTransport.DefaultPort.ToString() : address, Player.Instance.Company.SaveID);
            OpenLobby(host);
            LastError = "";
        }
        catch (Exception e) { Fail("Could not host: " + e.Message); End(); }
    }

    public string ClientId => clientId;
    public IReadOnlyList<(string sessionId, string record, string checkpoint, DateTime written)> Resumable() => SaveIO.Resumable(clientId);

    // D10: continue a saved session from this machine's latest checkpoint, as its host. Other players rejoin with Join.
    public void Resume(string sessionId, string address, string playerName)
    {
        if (Session != null || Busy) { Fail("Already in a session."); return; }
        var saved = Resumable().FirstOrDefault(r => sessionId.Length == 0 || r.sessionId == sessionId);
        if (saved.sessionId == null) { Fail("No saved session to resume" + (sessionId.Length > 0 ? " with id " + sessionId : "") + "."); return; }
        try
        {
            if (SteamGate.IsSteamAddress(address) && !SteamGate.TryInit(force: true)) { Fail("Could not resume on Steam: " + SteamGate.Error); return; }
            Begin(isHost: true);
            SaveIO.SessionId = saved.sessionId;
            LastError = "";
            Log($"MP: resuming session {saved.sessionId} from {saved.checkpoint}");
            SaveIO.Load(saved.checkpoint, () =>
            {
                try
                {
                    IdRanges.RestoreHostCounter();
                    EntityIO.ClearCaches();
                    Muting.Clear();
                    var host = new HostSession(CreateTransport(address, host: true), World, playerName, clientId, SessionRecord.Parse(saved.record)) { CaretakerDays = CaretakerDays };
                    Attach(host);
                    host.Start(string.IsNullOrWhiteSpace(address) ? TcpTransport.DefaultPort.ToString() : address, Player.Instance.Company.SaveID);
                    OpenLobby(host);
                    DateController.Instance.ManualPause();
                }
                catch (Exception e) { Fail("Could not resume: " + e.Message); End(); }
            });
        }
        catch (Exception e) { Fail("Could not resume: " + e.Message); End(); }
    }

    // auto: answers the company setup without the UI (scripted and headless joins).
    public void Join(string address, string playerName, Func<SessionRules, CompanySetup>? auto = null)
    {
        if (Session != null) { Fail("Already in a session."); return; }
        try
        {
            var transport = CreateTransport(address);
            Begin(isHost: false);
            var peer = new PeerSession(transport, World, playerName, clientId) { AutoCompany = auto };
            peer.CompanySetupRequested += rules => SetupRequested?.Invoke(rules);
            Attach(peer);
            peer.Connect(address);
            LastError = "";
        }
        catch (Exception e) { Fail("Could not join: " + e.Message); End(); }
    }

    public void Leave()
    {
        if (Session == null) return;
        Session.Leave("left the session");
        Session.Dispose();
        End();
        Log("MP: left the session");
    }

    public void SubmitCompany(CompanySetup setup) => (Session as PeerSession)?.SubmitCompany(setup);

    // "@Name text" sends a private message to that player (the longest matching name, any case); anything else goes to
    // everyone.
    public void SendChat(string text)
    {
        if (Session == null || string.IsNullOrWhiteSpace(text)) return;
        text = text.Trim();
        if (text.StartsWith("@", StringComparison.Ordinal))
        {
            var target = Session.Players.Where(p => p.Slot != Session.LocalSlot && text.Length > p.Name.Length + 1 && text.Substring(1).StartsWith(p.Name, StringComparison.OrdinalIgnoreCase) && char.IsWhiteSpace(text[p.Name.Length + 1]))
                .OrderByDescending(p => p.Name.Length).FirstOrDefault();
            if (target == null) { AddChat(new ChatLine { From = -1, To = Session.LocalSlot, Name = "", Text = "No player by that name. Write @ and a player's name, then your message." }); return; }
            Session.SendChat(text.Substring(target.Name.Length + 1).Trim(), target.Slot);
            return;
        }
        Session.SendChat(text);
    }

    private void AddChat(ChatLine line)
    {
        line.Seq = ++chatSeq;
        chat.Add(line);
        if (chat.Count > 100) chat.RemoveAt(0);
        ChatLine?.Invoke(line);
    }

    public void Update(float deltaTime)
    {
        if (Session == null) return;
        if (Session is PeerSession peer && peer.SessionId.Length > 0) SaveIO.SessionId = peer.SessionId;
        // Leaving the shared world (main menu, quitting) ends the session instead of ticking a missing world.
        if (!GameWorld.CampaignLoaded && (Session.IsHost || Session.State == SessionState.Running))
        {
            Log("MP: the game left the shared world; leaving the session");
            Leave();
            return;
        }
        try { Session.Update(deltaTime); }
        catch (Exception e) { Log("MP: session error: " + e); }
        // Steam picks and changes routes (relay or direct) on its own: log every change (D50).
        if (OnSteam && UnityEngine.Time.unscaledTime >= nextRouteCheck)
        {
            nextRouteCheck = UnityEngine.Time.unscaledTime + 10f;
            string routes = string.Join("; ", SteamGate.Routes(Transport).Select(r => System.Text.RegularExpressions.Regex.Replace(r, @" \(remote end near [^)]*\)|, ping \d+ ms", "")));
            if (routes != lastRoutes && routes.Length > 0) Log("MP: Steam route: " + string.Join("; ", SteamGate.Routes(Transport)));
            lastRoutes = routes;
        }
        if (Session.State == SessionState.Running) BusinessDeals.Tick();
        if (Session.State == SessionState.Running && reportedSession != Session)
        {
            reportedSession = Session;
            Diagnostics.SessionReport.Write(Log);
        }
        if (Session.State == SessionState.Closed)
        {
            Fail("Session ended: " + Session.CloseReason);
            Session.Dispose();
            End();
            // Host loss / disconnect: the world stops; the player decides how to continue (D25).
            if (GameWorld.CampaignLoaded) DateController.Instance.ManualPause();
        }
    }

    public void Log(string message)
    {
        logger.LogInfo(message);
        recent.Add($"{DateTime.Now:HH:mm:ss} {message}");
        if (recent.Count > 40) recent.RemoveAt(0);
    }

    private void Begin(bool isHost)
    {
        Ownership.Reset();
        Ownership.IsHost = isHost;
        Ownership.Active = true;
        IdRanges.Reset();
        Muting.Clear();
        EntityIO.ClearCaches();
        Commands.Clear();
        Licences.Reset();
        Contracts.ClearWaiting();
        Projects.Clear();
        Bankruptcy.Clear();
        Hooks.Apply();
        if (!isHost && GameWorld.CampaignLoaded) World.SetSpeedButtonsInteractable(false);
    }

    private void Attach(SessionBase session)
    {
        Session = session;
        session.ChannelReceived += Api.MpApi.Deliver;
        session.ChatReceived += (slot, text, to) =>
        {
            AddChat(new ChatLine { From = slot, To = to, Name = session.Players.FirstOrDefault(p => p.Slot == slot)?.Name ?? $"slot {slot}", Text = text });
            if (to < 0) Chat?.Invoke(slot, text);
        };
    }

    private void End()
    {
        Session = null;
        chat.Clear();
        ProcessorTycoonModApi.Game.Mail.Clear();
        BusinessDeals.Pending.Clear();
        Transport = null;
        if (OnSteam) SteamGate.CloseLobby();
        OnSteam = false;
        Ownership.Reset();
        Hooks.Remove();
        Muting.Clear();
        if (GameWorld.CampaignLoaded) World.SetSpeedButtonsInteractable(true);
    }

    // "steam" / "steam:<id>" addresses use Steam (D50), anything else TCP.
    // A Steam host also listens on loopback TCP, so game copies on this PC can join (D64); Transport stays the Steam one
    // for its route diagnostics.
    private ITransport CreateTransport(string address, bool host = false)
    {
        OnSteam = SteamGate.IsSteamAddress(address);
        Transport = OnSteam ? SteamGate.CreateTransport() : new TcpTransport();
        lastRoutes = "";
        hostedAddress = address;
        hosted = !host ? null : OnSteam ? new DualTransport(Transport, () => new TcpTransport(), Enumerable.Range(TcpTransport.DefaultPort, 10).Select(p => "127.0.0.1:" + p).ToList()) : Transport;
        return hosted ?? Transport;
    }

    private void OpenLobby(HostSession host)
    {
        if (OnSteam) SteamGate.OpenLobby(host.SessionId, World.Version.ModVersion);
    }

    private void Fail(string message)
    {
        LastError = message;
        Log("MP: " + message);
    }

    private static VersionInfo BuildVersion(string modVersion)
    {
        string assemblyPath = typeof(DateController).Assembly.Location;
        string hash = "";
        try
        {
            using var sha = SHA256.Create();
            using var stream = File.OpenRead(assemblyPath);
            hash = BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "").Substring(0, 16);
        }
        catch { }
        var mods = Chainloader.PluginInfos.Values
            .Where(p => !ClientSideMods.Contains(p.Metadata.GUID))
            .Select(p => $"{p.Metadata.GUID}@{p.Metadata.Version}")
            .OrderBy(s => s, StringComparer.Ordinal);
        return new VersionInfo
        {
            ModVersion = modVersion,
            GameVersion = MpProtocol.TargetGameVersion + "/" + UnityEngine.Application.version,
            AssemblyHash = hash,
            Mods = string.Join(",", mods),
        };
    }
}

// One chat line: From is the sender slot (−1: a note from the mod itself), To the recipient slot of a private message or
// −1 for everyone.
internal sealed class ChatLine
{
    public int Seq, From, To = -1;
    public string Name = "", Text = "";
    public bool Private => To >= 0;
}
