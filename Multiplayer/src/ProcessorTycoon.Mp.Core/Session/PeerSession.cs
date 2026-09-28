using System;
using System.Collections.Generic;
using System.Linq;
using ProcessorTycoonMp.Core.Protocol;
using ProcessorTycoonMp.Core.Transport;

namespace ProcessorTycoonMp.Core.Session;

// Peer: joins by snapshot, then simulates each day the host starts, after applying the host's bundle, and reports
// its own company's changes (PeerDelta acknowledges the day).
public sealed class PeerSession : SessionBase
{
    private const int MaxDaysPerFrame = 8;
    private readonly Queue<DayBundle> bundles = new();
    private readonly string playerName;
    private readonly string clientId;

    public PeerSession(ITransport transport, IGameWorld world, string playerName, string clientId) : base(transport, world)
    {
        this.playerName = playerName;
        this.clientId = clientId;
    }

    public override bool IsHost => false;
    public int HostSpeed { get; private set; }
    // D58: the host asks a new player to set up their company; the UI answers with SubmitCompany (or leaves).
    public event Action<SessionRules>? CompanySetupRequested;
    public SessionRules? PendingSetup { get; private set; }
    public SessionRules Rules { get; private set; } = new();
    // Scripted joins (tests, headless peers): answered at once with this setup.
    public Func<SessionRules, CompanySetup>? AutoCompany { get; set; }
    private bool hasRun;
    public string LastCheckpointResult { get; private set; } = "";

    public void Connect(string address)
    {
        State = SessionState.Connecting;
        World.Log($"MP: connecting to {address}");
        Transport.Connect(address);
    }

    public void SubmitCompany(CompanySetup setup)
    {
        if (PendingSetup == null) return;
        PendingSetup = null;
        World.Log($"MP: founding {setup.Name}");
        Send(0, setup);
    }

    public override void SendChat(string text) => Send(0, new Chat { Slot = LocalSlot, Text = text });
    public override void SendChannel(string name, byte[] data) => Send(0, new Channel { Name = name, Slot = LocalSlot, Data = data });

    public override void Leave(string reason)
    {
        if (State != SessionState.Closed) Send(0, new Leave { Reason = reason });
        Transport.Disconnect(0, reason);
        State = SessionState.Closed;
        CloseReason = reason;
    }

    protected override void OnConnected(int peer)
    {
        State = SessionState.Handshake;
        Send(0, new Hello { Version = World.Version, PlayerName = playerName, ClientId = clientId });
    }

    protected override void OnDisconnected(int peer, string reason)
    {
        if (State == SessionState.Closed) return;
        State = SessionState.Closed;
        if (CloseReason.Length == 0) CloseReason = reason;
        World.Log("MP: disconnected: " + CloseReason);
    }

    protected override void OnMessage(int peer, Message message)
    {
        switch (message)
        {
            case Welcome w:
                SessionId = w.SessionId;
                LocalSlot = w.Slot;
                SetPlayers(w.Players);
                Rules = w.Rules;
                World.Log($"MP: joined session {SessionId} as slot {LocalSlot}");
                if (w.NeedsCompany)
                {
                    PendingSetup = w.Rules;
                    if (AutoCompany != null) SubmitCompany(AutoCompany(w.Rules));
                    else CompanySetupRequested?.Invoke(w.Rules);
                }
                break;
            case Reject r:
                CloseReason = r.Reason;
                World.Log("MP: rejected: " + r.Reason);
                break;
            case Snapshot s:
                // A resync (not the first load): this machine's own entities are newer than the host's copy of them.
                var before = hasRun ? World.CaptureOwned() : null;
                State = SessionState.Loading;
                bundles.Clear();
                string json = Compression.InflateText(s.World);
                World.Log($"MP: loading snapshot of day {s.Day} ({json.Length / 1024} KiB)");
                World.LoadSnapshot(json, LocalSlot, () =>
                {
                    World.SetPlayers(PlayerList, LocalSlot);
                    ResetBaselines();
                    // After the baseline, so the next delta carries whatever the restore changes back to the host.
                    if (before != null) World.RestoreOwned(before);
                    hasRun = true;
                    // A loaded world shows no selected speed button; bundles only report speed changes.
                    World.ShowSpeed(HostSpeed);
                    State = SessionState.Running;
                    Send(0, new Ready { Day = World.Day });
                    World.Log($"MP: snapshot loaded, day {World.Day}");
                });
                break;
            case DayBundle b:
                if (State == SessionState.Running) bundles.Enqueue(b);
                break;
            case CheckpointResult cr:
                LastCheckpointResult = cr.Ok ? $"day {cr.Day} ok" : $"day {cr.Day} mismatch: {cr.Detail}";
                if (cr.Ok) World.Checkpoint(cr.Day, SessionId, new SessionRecord { SessionId = SessionId, Players = PlayerList }.Serialize());
                else
                {
                    Resyncs++;
                    World.DesyncReport(cr.Day, LocalSlot, cr.Differing.Where(LastCheckpointStates.ContainsKey).Select(k => LastCheckpointStates[k]).ToList());
                    World.Log("MP: checkpoint mismatch, resync follows: " + cr.Detail);
                }
                break;
            case Roster roster: SetPlayers(roster.Players); break;
            case Chat chat: RaiseChat(chat.Slot, chat.Text); break;
            case Channel channel: RaiseChannel(channel.Name, channel.Slot, channel.Data); break;
            case Leave leave:
                CloseReason = "Host left: " + leave.Reason;
                break;
        }
    }

    protected override void Tick(float deltaTime)
    {
        if (State == SessionState.Running)
            foreach (var (kind, args) in World.TakeCommands()) Send(0, new Command { Kind = kind, Args = args });
        int budget = MaxDaysPerFrame;
        while (State == SessionState.Running && bundles.Count > 0 && budget-- > 0)
        {
            var b = bundles.Dequeue();
            if (b.Day != World.Day + 1)
            {
                World.Log($"MP: day {b.Day} arrived at local day {World.Day}; requesting resync");
                bundles.Clear();
                State = SessionState.Loading;
                Send(0, new ResyncRequest { Reason = $"expected day {World.Day + 1}, got {b.Day}" });
                return;
            }
            foreach (var slot in b.Slots)
                if (slot.Slot != LocalSlot) ApplyRemote(slot.Slot, slot.Deltas);
            if (b.Checkpoint) Send(0, ComputeCheckpoint(b.Day));
            // Every bundle, not only on changes: the display heals if anything changed the local speed buttons.
            HostSpeed = b.Speed;
            World.ShowSpeed(b.Speed);
            World.StepDay();
            Send(0, new PeerDelta { Day = b.Day, Deltas = Tracker.Produce(World.CaptureOwned(), full: b.ResendAll) });
        }
    }

    private void SetPlayers(List<PlayerInfo> players)
    {
        PlayerList.Clear();
        PlayerList.AddRange(players);
        if (State == SessionState.Running) World.SetPlayers(PlayerList, LocalSlot);
    }
}
