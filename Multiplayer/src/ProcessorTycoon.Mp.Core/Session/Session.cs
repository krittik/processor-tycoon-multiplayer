using System;
using System.Collections.Generic;
using System.Linq;
using ProcessorTycoonMp.Core.Delta;
using ProcessorTycoonMp.Core.Protocol;
using ProcessorTycoonMp.Core.Transport;

namespace ProcessorTycoonMp.Core.Session;

public enum SessionState { Connecting, Handshake, Loading, Running, Closed }

// Shared by host and peer: transport pump, own-change tracking, mirrors of remote entities, checkpoint repair + hash.
public abstract class SessionBase : IDisposable
{
    protected readonly ITransport Transport;
    protected readonly IGameWorld World;
    protected readonly DeltaTracker Tracker = new();
    // Last known owner state of every remote entity (Json.Merge of all received deltas).
    protected readonly Dictionary<long, EntityState> Mirrors = new();
    private readonly Dictionary<long, int> mirrorSlot = new();
    protected readonly List<PlayerInfo> PlayerList = new();
    private uint seq;

    protected SessionBase(ITransport transport, IGameWorld world)
    {
        Transport = transport;
        World = world;
    }

    public SessionState State { get; protected set; } = SessionState.Connecting;
    public string SessionId { get; protected set; } = "";
    public int LocalSlot { get; protected set; }
    public string CloseReason { get; protected set; } = "";
    public IReadOnlyList<PlayerInfo> Players => PlayerList;
    public abstract bool IsHost { get; }
    public int LastCheckpointDay { get; protected set; } = -1;
    protected Dictionary<long, EntityState> LastCheckpointStates = new();
    public ulong LastCheckpointHash { get; protected set; }
    public int Resyncs { get; protected set; }
    public int DriftRepairs { get; protected set; }
    // (sender slot, text, recipient slot or -1 for everyone). Private messages reach only their sender and recipient.
    public event Action<int, string, int>? ChatReceived;
    public event Action<string, int, byte[]>? ChannelReceived;

    // Keepalive: idle connections still carry a ping every PingInterval seconds; silence longer than Timeout drops
    // the connection (dead peer, NAT idle timeout), which then follows the normal disconnect path.
    public const float PingInterval = 5f;
    public const float Timeout = 45f;
    protected float Clock { get; private set; }
    protected readonly Dictionary<int, float> LastHeard = new();
    // Round-trip time per connection from the keepalive pings (milliseconds).
    protected readonly Dictionary<int, float> RoundTripMs = new();
    private float pingTimer;

    public void Update(float deltaTime)
    {
        Clock += deltaTime;
        while (Transport.Poll(out var e))
        {
            try
            {
                LastHeard[e.Peer] = Clock;
                switch (e.Kind)
                {
                    case NetEventKind.Connected: OnConnected(e.Peer); break;
                    case NetEventKind.Disconnected: LastHeard.Remove(e.Peer); OnDisconnected(e.Peer, e.Reason); break;
                    case NetEventKind.Data:
                        var message = Message.Decode(e.Data!);
                        if (message is Ping ping) Send(e.Peer, new Pong { Ticks = ping.Ticks });
                        else if (message is Pong pong) RoundTripMs[e.Peer] = (float)TimeSpan.FromTicks(DateTime.UtcNow.Ticks - pong.Ticks).TotalMilliseconds;
                        else OnMessage(e.Peer, message);
                        break;
                }
            }
            catch (Exception ex) { World.Log($"MP: error handling {e.Kind} from {e.Peer}: {ex}"); }
        }
        Tick(deltaTime);
        pingTimer += deltaTime;
        if (pingTimer < PingInterval) return;
        pingTimer = 0;
        foreach (var peer in LastHeard.Keys.ToList())
        {
            if (Clock - LastHeard[peer] > Timeout)
            {
                World.Log($"MP: connection {peer} silent for {Timeout:0} s; dropping it");
                LastHeard.Remove(peer);
                Transport.Disconnect(peer, "timed out");
            }
            else Send(peer, new Ping { Ticks = DateTime.UtcNow.Ticks });
        }
    }

    public virtual void SendChat(string text, int to = -1) { }
    public virtual void SendChannel(string name, byte[] data) { }

    public abstract void Leave(string reason);

    public void Dispose()
    {
        if (State != SessionState.Closed) Leave("closed");
        Transport.Dispose();
    }

    protected abstract void OnConnected(int peer);
    protected abstract void OnDisconnected(int peer, string reason);
    protected abstract void OnMessage(int peer, Message message);
    protected abstract void Tick(float deltaTime);

    protected void Send(int peer, Message m)
    {
        m.Seq = ++seq;
        Transport.Send(peer, m.Encode());
    }

    protected void RaiseChat(int slot, string text, int to) => ChatReceived?.Invoke(slot, text, to);
    protected void RaiseChannel(string name, int slot, byte[] data) => ChannelReceived?.Invoke(name, slot, data);

    protected void ApplyRemote(int slot, List<EntityDelta> deltas)
    {
        if (deltas.Count == 0) return;
        foreach (var d in deltas)
        {
            var key = new EntityState(d.Kind, d.Id, "").Key;
            mirrorSlot[key] = slot;
            switch (d.Op)
            {
                case DeltaOp.Upsert: Mirrors[key] = new EntityState(d.Kind, d.Id, d.Json); break;
                case DeltaOp.Patch:
                    Mirrors[key] = Mirrors.TryGetValue(key, out var m) ? new EntityState(d.Kind, d.Id, Json.Merge(m.Json, d.Json)) : new EntityState(d.Kind, d.Id, d.Json);
                    break;
                case DeltaOp.Remove: Mirrors.Remove(key); break;
            }
        }
        World.Apply(slot, deltas);
    }

    // A slot's entities are no longer remote (e.g. the host's AI takes over a disconnected player's company).
    protected void ForgetMirrors(int slot)
    {
        foreach (var key in mirrorSlot.Where(kv => kv.Value == slot).Select(kv => kv.Key).ToList())
        {
            Mirrors.Remove(key);
            mirrorSlot.Remove(key);
        }
    }

    // Ownership moved to another machine (a player returned): forget those entities without reporting them removed;
    // entities that really disappeared are still reported.
    protected void ForgetUnowned()
    {
        var owned = new HashSet<long>(World.CaptureOwned().Select(e => e.Key));
        var existing = new HashSet<long>(World.CaptureReplicated().Select(e => e.Key));
        Tracker.Forget(k => !owned.Contains(k) && existing.Contains(k));
    }

    // After a snapshot: what this machine has now is what every other machine knows.
    protected void ResetBaselines()
    {
        var owned = World.CaptureOwned();
        Tracker.Baseline(owned);
        var ownedKeys = new HashSet<long>(owned.Select(e => e.Key));
        Mirrors.Clear();
        foreach (var e in World.CaptureReplicated())
            if (!ownedKeys.Contains(e.Key)) Mirrors[e.Key] = e;
    }

    // Checkpoint (state before simulating `day`): overwrite remote entities that local simulation changed with the
    // owner's last known state, report which fields drifted, then hash the replicated world.
    protected CheckpointHash ComputeCheckpoint(int day)
    {
        var owned = new HashSet<long>();
        var local = RepairDrift($"checkpoint {day}", owned);
        // Own entities are hashed as last sent: the player may have changed them since (e.g. released a CPU between
        // frames), and the others only know the sent state. Remote entities are hashed as held here.
        var entities = new Dictionary<long, ulong>();
        var states = local.Values.Where(e => !owned.Contains(e.Key)).Concat(Tracker.Sent).Where(e => !EntityKinds.IsCarried(e.Kind)).ToList();
        ulong hash = Hashing.World(states, entities);
        LastCheckpointStates = states.ToDictionary(e => e.Key);
        LastCheckpointDay = day;
        LastCheckpointHash = hash;
        return new CheckpointHash { Day = day, Hash = hash, Entities = entities };
    }

    // Overwrites remote entities whose local state differs from the owner's last known state; logs the fields.
    // Returns the replicated world after the repair (one full capture; repaired entities are re-captured).
    protected Dictionary<long, EntityState> RepairDrift(string context, ISet<long>? owned = null)
    {
        var local = World.CaptureReplicated(owned).ToDictionary(e => e.Key);
        var repairs = new List<EntityDelta>();
        var drifted = new Dictionary<string, int>();
        foreach (var m in Mirrors.Values.Where(m => !EntityKinds.IsCarried(m.Kind)))
        {
            if (local.TryGetValue(m.Key, out var mine) && mine.Json == m.Json) continue;
            repairs.Add(new EntityDelta(m.Kind, m.Id, DeltaOp.Upsert, m.Json));
            if (mine.Json == null) { Count(drifted, $"{m.Kind}:missing"); continue; }
            var diff = Json.Diff(m.Json, mine.Json);
            if (diff != null) foreach (var member in Json.Members(diff)) Count(drifted, $"{m.Kind}.{member.Key}");
        }
        if (repairs.Count > 0)
        {
            DriftRepairs += repairs.Count;
            World.Log($"MP: {context}: repaired {repairs.Count} drifted entities; fields: " +
                      string.Join(", ", drifted.OrderByDescending(kv => kv.Value).Take(12).Select(kv => $"{kv.Key}×{kv.Value}")));
            World.Apply(-1, repairs);
            foreach (var e in World.Recapture(repairs.Select(r => new EntityState(r.Kind, r.Id, "").Key).ToList())) local[e.Key] = e;
        }
        return local;
    }

    private static void Count(Dictionary<string, int> map, string key) => map[key] = map.TryGetValue(key, out int n) ? n + 1 : 1;
}
