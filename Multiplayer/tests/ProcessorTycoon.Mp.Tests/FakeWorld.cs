using System.Text;
using ProcessorTycoonMp.Core.Delta;
using ProcessorTycoonMp.Core.Protocol;
using ProcessorTycoonMp.Core.Session;

namespace ProcessorTycoonMp.Tests;

// Deterministic stand-in for the game: entities are JSON objects owned by slots. Every day each machine changes the
// entities it owns, and its "market" writes into every entity (drift on non-owned ones, repaired at checkpoints).
internal sealed class FakeWorld : IGameWorld
{
    private sealed class Entity
    {
        public EntityKind Kind;
        public int Id;
        public int Owner;
        public string Json = "";
    }

    private readonly Dictionary<long, Entity> entities = new();
    private int localSlot;
    public readonly List<string> Logs = new();
    public readonly List<int> Checkpoints = new();
    public int BugEntity = -1;       // Apply "forgets" the Bug field of this entity (simulated applier bug)
    public bool Verbose;

    public FakeWorld(bool isHost)
    {
        if (!isHost) return;
        Add(EntityKind.Company, 0, 0, Company(0, 0));        // host company
        Add(EntityKind.Company, 1, 0, Company(1, 0));        // AI company
        Add(EntityKind.Cpu, 10, 0, "{\"Price\":10,\"Market\":{\"Sold\":[0],\"Stock\":0},\"Drift\":0}");
        Add(EntityKind.Market, 0, 0, "{\"Potential\":[1,2,3]}");
    }

    public VersionInfo Version { get; set; } = new() { ModVersion = "test", GameVersion = "g", AssemblyHash = "h" };
    public int Day { get; private set; }
    public int Speed { get; set; } = 1;
    public int Steps { get; private set; }

    public bool IsCheckpointDay(int day) => day % 30 == 1;
    public void ShowSpeed(int speed) { }

    public void StepDay()
    {
        Day++;
        Steps++;
        foreach (var e in entities.Values)
        {
            if (e.Owner == localSlot)
            {
                if (e.Kind == EntityKind.Company) e.Json = Json.Merge(e.Json, $"{{\"Money\":{1000 + Day * (e.Id + 1)},\"Research\":{{\"Progress\":{Day % 97}}}}}");
                if (e.Kind == EntityKind.Cpu) e.Json = Json.Merge(e.Json, $"{{\"Market\":{{\"Sold\":[{Day},{Day * 2}],\"Stock\":{Day % 13}}}}}");
                if (e.Kind == EntityKind.Market && Day % 30 == 0) e.Json = Json.Merge(e.Json, $"{{\"Potential\":[{Day},2,3]}}");
            }
            else if (e.Kind == EntityKind.Cpu) e.Json = Json.Merge(e.Json, $"{{\"Drift\":{Day}}}");   // local market writes into a remote CPU
        }
        // Peers create a CPU every 100 days (new entity ids from the slot range, D20).
        if (localSlot > 0 && Day % 100 == 0) Add(EntityKind.Cpu, localSlot * 10_000_000 + Day, localSlot, "{\"Price\":5,\"Market\":{\"Sold\":[],\"Stock\":0},\"Drift\":0}");
    }

    public List<EntityState> CaptureOwned() => entities.Values.Where(e => e.Owner == localSlot).Select(State).ToList();
    public List<EntityState> CaptureReplicated(ISet<long>? owned = null)
    {
        if (owned != null) foreach (var e in entities.Values.Where(x => x.Owner == localSlot)) owned.Add(new EntityState(e.Kind, e.Id, "").Key);
        return entities.Values.Select(State).ToList();
    }

    public List<EntityState> Recapture(IReadOnlyCollection<long> keys) => keys.Where(entities.ContainsKey).Select(k => State(entities[k])).ToList();

    public void Apply(int fromSlot, IReadOnlyList<EntityDelta> deltas)
    {
        foreach (var d in deltas)
        {
            long key = new EntityState(d.Kind, d.Id, "").Key;
            entities.TryGetValue(key, out var e);
            switch (d.Op)
            {
                case DeltaOp.Upsert:
                    if (e == null) { Add(d.Kind, d.Id, fromSlot >= 0 ? fromSlot : OwnerFromId(d.Id), d.Json); e = entities[key]; }
                    else e.Json = d.Json;
                    break;
                case DeltaOp.Patch:
                    if (e != null) e.Json = Json.Merge(e.Json, d.Json);
                    break;
                case DeltaOp.Remove:
                    entities.Remove(key);
                    break;
            }
            if (e != null && d.Id == BugEntity && Json.Get(e.Json, "Bug") != null) e.Json = Json.Merge(e.Json, "{\"Bug\":0}");
        }
    }

    public void SetPlayers(IReadOnlyList<PlayerInfo> players, int localSlot) => this.localSlot = localSlot;

    public int Restored { get; private set; }
    public void RestoreOwned(IReadOnlyList<EntityState> before)
    {
        foreach (var s in before)
            if (entities.TryGetValue(s.Key, out var e) && e.Json != s.Json) { e.Json = s.Json; Restored++; }
    }

    public SessionRules Rules { get; } = new() { Difficulty = 3, Year = 1980 };
    public readonly List<string> Founded = new();

    public int AddPlayer(int slot, CompanySetup setup)
    {
        Founded.Add($"{slot}:{setup.Name}");
        int id = slot * 10_000_000;
        Add(EntityKind.Company, id, slot, Company(id, slot));
        return id;
    }

    public string BuildSnapshot(int slot)
    {
        var sb = new StringBuilder().Append(Day).Append('\n');
        foreach (var e in entities.Values) sb.Append($"{(byte)e.Kind}|{e.Id}|{e.Owner}|{e.Json}\n");
        return sb.ToString();
    }

    public void LoadSnapshot(string json, int slot, Action loaded)
    {
        entities.Clear();
        var lines = json.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Day = int.Parse(lines[0]);
        foreach (var line in lines.Skip(1))
        {
            var parts = line.Split('|', 4);
            Add((EntityKind)byte.Parse(parts[0]), int.Parse(parts[1]), int.Parse(parts[2]), parts[3]);
        }
        loaded();
    }

    public void Checkpoint(int day, string sessionId, string record) => Checkpoints.Add(day);
    public void DesyncReport(int day, int slot, IReadOnlyList<EntityState> entities) => Logs.Add($"desync report {day} slot {slot}: {entities.Count}");
    public readonly List<(string kind, string args)> Outgoing = new();
    public readonly List<string> Executed = new();
    public IReadOnlyList<(string kind, string args)> TakeCommands() { var list = Outgoing.ToList(); Outgoing.Clear(); return list; }
    public void ExecuteCommand(int slot, string kind, string args) => Executed.Add($"{slot}:{kind}:{args}");

    public void Log(string message)
    {
        Logs.Add(message);
        if (Verbose) Console.WriteLine(message);
    }

    public void Corrupt(int id, string patch)
    {
        foreach (var e in entities.Values.Where(x => x.Id == id)) e.Json = Json.Merge(e.Json, patch);
    }

    public string JsonOf(EntityKind kind, int id) => entities[new EntityState(kind, id, "").Key].Json;

    private static int OwnerFromId(int id) => id / 10_000_000;
    private static string Company(int id, int slot) => $"{{\"Name\":\"C{id}\",\"Money\":1000,\"Research\":{{\"Progress\":0,\"List\":[1,2]}},\"Bug\":0}}";
    private static EntityState State(Entity e) => new(e.Kind, e.Id, e.Json);
    private void Add(EntityKind kind, int id, int owner, string json) => entities[new EntityState(kind, id, "").Key] = new Entity { Kind = kind, Id = id, Owner = owner, Json = json };
}
