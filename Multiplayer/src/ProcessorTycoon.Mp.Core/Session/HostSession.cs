using System;
using System.Collections.Generic;
using System.Linq;
using ProcessorTycoonMp.Core.Delta;
using ProcessorTycoonMp.Core.Protocol;
using ProcessorTycoonMp.Core.Transport;

namespace ProcessorTycoonMp.Core.Session;

// Host (slot 0): world owner, clock and arbiter (D2). Day loop with lag window K = 0 (D27): day N starts only when
// every running peer has acknowledged day N-1.
public sealed class HostSession : SessionBase
{
    public const int MaxPlayers = 8;

    private sealed class Peer
    {
        public int Connection;
        public int Slot = -1;
        public string ClientId = "";
        public SessionState State = SessionState.Handshake;
        public int AckDay;
        public List<EntityDelta>? Pending;
        public int LastResyncCheckpoint = int.MinValue;
    }

    private readonly Dictionary<int, Peer> peers = new();
    private readonly Dictionary<string, int> slotByClient = new();
    private float accumulator;
    private int lastDay;
    private CheckpointHash? checkpoint;
    private readonly Dictionary<int, ulong> checkpointReports = new();
    private readonly List<SlotDeltas> announcements = new();
    private bool resendAll;

    // resume: the roster of a saved session (D10). The resuming machine becomes slot 0; if it was another slot before
    // (host change), it swaps slots with the previous host, whose company becomes an ordinary remote company.
    public HostSession(ITransport transport, IGameWorld world, string hostName, string hostClientId = "", SessionRecord? resume = null) : base(transport, world)
    {
        LocalSlot = 0;
        if (resume == null)
        {
            SessionId = Guid.NewGuid().ToString("N").Substring(0, 12);
            PlayerList.Add(new PlayerInfo { Slot = 0, Name = hostName, CompanyId = -1, Connected = true, ClientId = hostClientId });
        }
        else
        {
            SessionId = resume.SessionId;
            var me = resume.Players.FirstOrDefault(p => p.ClientId == hostClientId && hostClientId.Length > 0);
            int mySlot = me?.Slot ?? 0;
            foreach (var p in resume.Players)
            {
                int slot = p.Slot == mySlot ? 0 : p.Slot == 0 ? mySlot : p.Slot;
                PlayerList.Add(new PlayerInfo { Slot = slot, Name = p.Name, CompanyId = p.CompanyId, Connected = slot == 0, ClientId = p.ClientId });
                if (p.ClientId.Length > 0 && slot != 0) slotByClient[p.ClientId] = slot;
            }
            if (PlayerList.All(p => p.Slot != 0)) PlayerList.Add(new PlayerInfo { Slot = 0, Name = hostName, CompanyId = -1, Connected = true, ClientId = hostClientId });
            PlayerList.Sort((a, b) => a.Slot.CompareTo(b.Slot));
        }
    }

    public SessionRecord Record() => new() { SessionId = SessionId, Players = PlayerList.Select(p => p.Copy()).ToList() };

    public override bool IsHost => true;
    public int RunningPeers => peers.Values.Count(p => p.State == SessionState.Running);
    public bool WaitingForPeers { get; private set; }
    // Names of the players the clock is waiting for (empty when running).
    public string WaitingFor { get; private set; } = "";
    // Lag window K (D27): how many days peers may trail the host between checkpoints.
    public int LagWindow { get; set; } = 2;
    // D55: game days a caretaker keeps a disconnected player's company as they left it before the AI takes over.
    public int CaretakerDays { get; set; } = 182;

    public void Start(string address, int hostCompanyId)
    {
        PlayerList.First(p => p.Slot == 0).CompanyId = hostCompanyId;
        // A resumed session starts with everyone else away: their caretaker period starts now.
        foreach (var p in PlayerList.Where(p => !p.Connected && p.OfflineSince < 0)) p.OfflineSince = World.Day;
        World.SetPlayers(PlayerList, 0);
        Transport.Host(address);
        lastDay = World.Day;
        State = SessionState.Running;
        Tracker.Baseline(World.CaptureOwned());
        World.Log($"MP: hosting session {SessionId} on {address}");
    }

    // Round-trip time to the player in a slot, or -1 when unknown/offline.
    public float PingMs(int slot)
    {
        var p = peers.Values.FirstOrDefault(x => x.Slot == slot);
        return p != null && RoundTripMs.TryGetValue(p.Connection, out var ms) ? ms : -1f;
    }

    // Disconnect a player; the AI plays their company until they rejoin (D25).
    public void Kick(int slot, string reason)
    {
        var p = peers.Values.FirstOrDefault(x => x.Slot == slot);
        if (p == null) return;
        Send(p.Connection, new Leave { Reason = reason });
        Transport.Disconnect(p.Connection, reason);
    }

    public override void SendChannel(string name, byte[] data) => Broadcast(new Channel { Name = name, Slot = 0, Data = data });

    public override void SendChat(string text, int to = -1)
    {
        if (to == 0) return;
        RaiseChat(0, text, to);
        if (to < 0) Broadcast(new Chat { Slot = 0, Text = text });
        else Relay(new Chat { Slot = 0, Text = text, To = to });
    }

    private void Relay(Chat chat)
    {
        var target = peers.Values.FirstOrDefault(x => x.Slot == chat.To);
        if (target != null) Send(target.Connection, chat);
    }

    public override void Leave(string reason)
    {
        foreach (var p in peers.Values.ToList())
        {
            Send(p.Connection, new Leave { Reason = reason });
            Transport.Disconnect(p.Connection, reason);
        }
        State = SessionState.Closed;
        CloseReason = reason;
    }

    protected override void OnConnected(int connection) => peers[connection] = new Peer { Connection = connection };

    protected override void OnDisconnected(int connection, string reason)
    {
        if (!peers.TryGetValue(connection, out var p)) return;
        peers.Remove(connection);
        if (p.Slot < 0) return;
        var info = PlayerList.FirstOrDefault(x => x.Slot == p.Slot);
        if (info == null) return;   // rejected during the company setup
        if (info.CompanyId < 0)
        {
            // Left during the company setup: nothing was created, the slot is free again.
            PlayerList.Remove(info);
            World.Log($"MP: {info.Name} left before founding a company");
            BroadcastRoster();
            return;
        }
        info.Connected = false;
        info.OfflineSince = World.Day;
        info.AiControl = false;
        ForgetMirrors(p.Slot);   // the host simulates this company until the player returns (D25, D55)
        World.Log($"MP: {info.Name} (slot {p.Slot}) disconnected: {reason}; a caretaker keeps their company for {CaretakerDays} days, then the AI plays it");
        BroadcastRoster();
    }

    protected override void OnMessage(int connection, Message message)
    {
        if (!peers.TryGetValue(connection, out var p)) return;
        switch (message)
        {
            case Hello hello: OnHello(p, hello); break;
            case Ready ready:
                p.State = SessionState.Running;
                p.AckDay = ready.Day;
                p.Pending = null;
                World.Log($"MP: slot {p.Slot} ready at day {ready.Day}");
                break;
            case PeerDelta delta:
                if (p.State != SessionState.Running) break;
                p.AckDay = delta.Day;
                if (p.Pending == null) p.Pending = delta.Deltas; else p.Pending.AddRange(delta.Deltas);
                break;
            case CheckpointHash hash: OnCheckpointHash(p, hash); break;
            case CompanySetup setup: OnCompanySetup(p, setup); break;
            case ResyncRequest req:
                World.Log($"MP: slot {p.Slot} requests resync: {req.Reason}");
                SendSnapshot(p);
                break;
            case Command command:
                if (p.State == SessionState.Running) World.ExecuteCommand(p.Slot, command.Kind, command.Args);
                break;
            case Channel channel:
                RaiseChannel(channel.Name, p.Slot, channel.Data);
                foreach (var other in peers.Values.Where(x => x != p && x.Slot >= 0)) Send(other.Connection, new Channel { Name = channel.Name, Slot = p.Slot, Data = channel.Data });
                break;
            case Chat chat:
                if (chat.To < 0) { RaiseChat(p.Slot, chat.Text, -1); Broadcast(new Chat { Slot = p.Slot, Text = chat.Text }); }
                else if (chat.To == 0) RaiseChat(p.Slot, chat.Text, 0);
                else if (chat.To != p.Slot) Relay(new Chat { Slot = p.Slot, Text = chat.Text, To = chat.To });
                break;
            case Leave leave:
                Transport.Disconnect(connection, leave.Reason);
                break;
        }
    }

    private void OnHello(Peer p, Hello hello)
    {
        string? mismatch = World.Version.Mismatch(hello.Version);
        if (mismatch != null) { Reject(p, "Version mismatch: " + mismatch); return; }
        int slot;
        if (hello.ClientId.Length > 0 && slotByClient.TryGetValue(hello.ClientId, out int known))
        {
            if (peers.Values.Any(x => x != p && x.Slot == known)) { Reject(p, "This player is already connected."); return; }
            slot = known;
        }
        else
        {
            if (PlayerList.Count >= MaxPlayers) { Reject(p, "Session is full."); return; }
            slot = Enumerable.Range(1, MaxPlayers - 1).First(s => PlayerList.All(x => x.Slot != s));
            string name = string.IsNullOrWhiteSpace(hello.PlayerName) ? $"Player {slot + 1}" : hello.PlayerName.Trim();
            // D58: the slot is held while the player sets up their company; the company exists only once they confirm.
            PlayerList.Add(new PlayerInfo { Slot = slot, Name = name, CompanyId = -1, ClientId = hello.ClientId, Connected = true });
            p.Slot = slot;
            p.ClientId = hello.ClientId;
            World.Log($"MP: {name} joins as slot {slot} and sets up their company");
            Send(p.Connection, new Welcome { SessionId = SessionId, Slot = slot, Players = PlayerList, NeedsCompany = true, Rules = Rules() });
            BroadcastRoster();
            return;
        }
        p.Slot = slot;
        p.ClientId = hello.ClientId;
        var joined = PlayerList.First(x => x.Slot == slot);
        bool returning = !joined.Connected && joined.OfflineSince >= 0;
        joined.Connected = true;
        joined.OfflineSince = -1;
        joined.AiControl = false;
        World.SetPlayers(PlayerList, 0);
        // The returning player owns their company again: stop tracking it here without announcing a removal.
        if (returning) ForgetUnowned();
        World.Log($"MP: {hello.PlayerName} joins as slot {slot}");
        Send(p.Connection, new Welcome { SessionId = SessionId, Slot = slot, Players = PlayerList, Rules = Rules() });
        try { SendSnapshot(p); }
        catch (Exception e)
        {
            World.Log("MP: building the world for a joining player failed: " + e);
            p.State = SessionState.Closed;
            Reject(p, "The host could not send the world: " + e.Message);
            return;
        }
        BroadcastRoster();
    }

    public SessionRules Rules()
    {
        var rules = World.Rules;
        rules.CaretakerDays = CaretakerDays;
        return rules;
    }

    private void OnCompanySetup(Peer p, CompanySetup setup)
    {
        var info = PlayerList.FirstOrDefault(x => x.Slot == p.Slot);
        if (info == null || info.CompanyId >= 0) return;
        try { info.CompanyId = World.AddPlayer(p.Slot, setup); }
        catch (Exception e)
        {
            World.Log("MP: creating the company of a new player failed: " + e);
            PlayerList.Remove(info);
            Reject(p, "The host could not create your company: " + e.Message);
            return;
        }
        if (p.ClientId.Length > 0) slotByClient[p.ClientId] = p.Slot;
        World.SetPlayers(PlayerList, 0);
        World.Log($"MP: {info.Name} founded {setup.Name}");
        try { SendSnapshot(p); }
        catch (Exception e)
        {
            World.Log("MP: building the world for a joining player failed: " + e);
            p.State = SessionState.Closed;
            Reject(p, "The host could not send the world: " + e.Message);
            return;
        }
        BroadcastRoster();
    }

    private void Reject(Peer p, string reason)
    {
        World.Log("MP: rejected a player: " + reason);
        Send(p.Connection, new Reject { Reason = reason });
        Transport.Disconnect(p.Connection, reason);
    }

    // Snapshot = current live state. Later bundles are diffs against the last bundle, which the snapshot already
    // contains, so re-sent changes are harmless (Upsert of an existing entity overwrites it).
    private void SendSnapshot(Peer p)
    {
        p.State = SessionState.Loading;
        p.Pending = null;
        // The snapshot must carry every owner's truth, not this machine's local drift on remote entities.
        var owned = new HashSet<long>();
        var local = RepairDrift($"snapshot for slot {p.Slot}", owned);
        resendAll = true;   // loading normalizes some state; every owner re-sends everything once the peer is back
        string json = World.BuildSnapshot(p.Slot);
        // From now on the receiver starts from exactly this state (for its own company too), and so do the mirrors.
        var announce = new List<EntityDelta>();
        foreach (var e in local.Values)
        {
            if (owned.Contains(e.Key)) continue;
            if (!Mirrors.ContainsKey(e.Key)) announce.Add(new EntityDelta(e.Kind, e.Id, DeltaOp.Upsert, e.Json));
            Mirrors[e.Key] = e;
        }
        // A new player's company must also appear on the other peers.
        if (announce.Count > 0) announcements.Add(new SlotDeltas { Slot = p.Slot, Deltas = announce });
        var snapshot = new Snapshot { Day = World.Day, World = Compression.DeflateText(json) };
        Send(p.Connection, snapshot);
        World.Log($"MP: snapshot for slot {p.Slot}: {json.Length / 1024} KiB JSON, {snapshot.World.Length / 1024} KiB sent");
    }

    protected override void Tick(float deltaTime)
    {
        int speed = World.Speed;
        bool loading = peers.Values.Any(p => p.State == SessionState.Loading);
        var running = peers.Values.Where(p => p.State == SessionState.Running).ToList();
        WaitingForPeers = loading || !CanRun(running);
        WaitingFor = !WaitingForPeers ? "" : string.Join(", ", peers.Values
            .Where(p => p.State == SessionState.Loading || (p.State == SessionState.Running && p.AckDay < World.Day - (World.IsCheckpointDay(World.Day + 1) ? 0 : LagWindow)))
            .Select(p => PlayerList.FirstOrDefault(x => x.Slot == p.Slot)?.Name + (p.State == SessionState.Loading ? " (loading)" : "")));
        if (speed <= 0 || loading) { accumulator = 0; return; }
        accumulator = Math.Min(accumulator + deltaTime * speed, 2f + LagWindow);
        int budget = running.Count == 0 ? 4 : 1 + LagWindow;
        while (accumulator >= 1f && budget-- > 0 && CanRun(running))
        {
            accumulator -= 1f;
            RunDay(speed, running);
        }
    }

    // D27: day N may start when every running peer acknowledged day N-1-K. Checkpoint days use K = 0, so the state
    // of the previous day of every owner is applied everywhere before hashing.
    private bool CanRun(List<Peer> running)
    {
        int next = World.Day + 1;
        int k = World.IsCheckpointDay(next) ? 0 : LagWindow;
        return running.All(p => p.AckDay >= next - 1 - k);
    }

    private void RunDay(int speed, List<Peer> running)
    {
        int day = World.Day + 1;
        // A checkpoint needs every owner's post-load full state (ResendAll) applied first, so the first bundle after a
        // snapshot never checks; the next month does.
        var bundle = new DayBundle { Day = day, Speed = speed, Checkpoint = World.IsCheckpointDay(day) && !resendAll, ResendAll = resendAll };
        bundle.Slots.Add(new SlotDeltas { Slot = 0, Deltas = Tracker.Produce(World.CaptureOwned(), full: resendAll) });
        resendAll = false;
        bundle.Slots.AddRange(announcements);
        announcements.Clear();
        foreach (var p in running.OrderBy(x => x.Slot))
        {
            if (p.Pending == null) continue;
            ApplyRemote(p.Slot, p.Pending);
            bundle.Slots.Add(new SlotDeltas { Slot = p.Slot, Deltas = p.Pending });
            p.Pending = null;
        }
        foreach (var p in running) Send(p.Connection, bundle);
        if (bundle.Checkpoint)
        {
            checkpoint = ComputeCheckpoint(day);
            checkpointReports.Clear();
            if (running.Count == 0) World.Checkpoint(day, SessionId, Record().Serialize());
        }
        World.StepDay();
        lastDay = day;
        CheckCaretakers();
    }

    // D55: after CaretakerDays away, the AI plays a disconnected player's company until they return.
    private void CheckCaretakers()
    {
        bool changed = false;
        foreach (var p in PlayerList.Where(p => !p.Connected && !p.AiControl && p.OfflineSince >= 0 && World.Day - p.OfflineSince >= CaretakerDays))
        {
            p.AiControl = true;
            changed = true;
            World.Log($"MP: {p.Name} has been away for {CaretakerDays} days; the AI now plays their company");
        }
        if (changed) BroadcastRoster();
    }

    private void OnCheckpointHash(Peer p, CheckpointHash hash)
    {
        if (checkpoint == null || hash.Day != checkpoint.Day) return;
        checkpointReports[p.Slot] = hash.Hash;
        bool ok = hash.Hash == checkpoint.Hash;
        var differing = ok ? new List<long>() : Differing(checkpoint, hash);
        string detail = ok ? "" : $"{differing.Count} entities: " + string.Join(", ", differing.Take(10).Select(k => $"{(EntityKind)(k >> 32)}#{(int)k}"));
        Send(p.Connection, new CheckpointResult { Day = hash.Day, Ok = ok, Detail = detail, Differing = differing });
        if (!ok)
        {
            World.DesyncReport(hash.Day, p.Slot, differing.Where(LastCheckpointStates.ContainsKey).Select(k => LastCheckpointStates[k]).ToList());
            World.Log($"MP: checkpoint {hash.Day}: slot {p.Slot} differs ({detail}); resync");
            Resyncs++;
            SendSnapshot(p);
        }
        var running = peers.Values.Where(x => x.State == SessionState.Running || x.Slot == p.Slot).ToList();
        if (running.All(x => checkpointReports.ContainsKey(x.Slot)) && checkpointReports.Values.All(h => h == checkpoint.Hash))
            World.Checkpoint(checkpoint.Day, SessionId, Record().Serialize());
    }

    private static List<long> Differing(CheckpointHash mine, CheckpointHash theirs) =>
        mine.Entities.Where(kv => !theirs.Entities.TryGetValue(kv.Key, out var h) || h != kv.Value).Select(kv => kv.Key)
            .Concat(theirs.Entities.Keys.Where(k => !mine.Entities.ContainsKey(k))).Distinct().ToList();

    private void Broadcast(Message m)
    {
        foreach (var p in peers.Values.Where(x => x.Slot >= 0)) Send(p.Connection, m);
    }

    private void BroadcastRoster()
    {
        World.SetPlayers(PlayerList, 0);
        Broadcast(new Roster { Players = PlayerList });
    }
}
