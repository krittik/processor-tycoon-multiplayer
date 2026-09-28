using System;
using System.Collections.Generic;
using System.Linq;

namespace ProcessorTycoonMp.Core.Delta;

// Entity kinds, applied in this order (PROTOCOL "Delta format").
public enum EntityKind : byte
{
    Company = 1,
    CustomHardware = 2,
    Cpu = 3,
    Contract = 5,   // host-owned tenders (D43); applied after the CPUs they reference
    BusinessContract = 6,   // host-owned deals between companies (D45)
    Projects = 7,   // a human company's in-progress projects: carried, not simulated by receivers (D52)
    Market = 10,
}

public static class EntityKinds
{
    // Carried kinds travel to every machine but receivers only store them: no drift repair, no checkpoint hash, and
    // always sent whole (a receiver may not hold a base to patch).
    public static bool IsCarried(EntityKind kind) => kind == EntityKind.Projects;
}

public enum DeltaOp : byte { Upsert = 1, Patch = 2, Remove = 3 }

// Canonical JSON of one replicated entity as captured on this machine.
public readonly struct EntityState
{
    public EntityState(EntityKind kind, int id, string json) { Kind = kind; Id = id; Json = json; }
    public EntityKind Kind { get; }
    public int Id { get; }
    public string Json { get; }
    public long Key => ((long)Kind << 32) | (uint)Id;
}

public sealed class EntityDelta
{
    public EntityDelta(EntityKind kind, int id, DeltaOp op, string json) { Kind = kind; Id = id; Op = op; Json = json; }
    public EntityKind Kind { get; }
    public int Id { get; }
    public DeltaOp Op { get; }
    public string Json { get; }   // full JSON (Upsert), partial JSON (Patch), empty (Remove)
    public override string ToString() => $"{Kind}#{Id} {Op} {Json.Length}";
}

// Remembers what was last sent per entity and turns a new capture into deltas.
public sealed class DeltaTracker
{
    private readonly Dictionary<long, EntityState> sent = new();

    public int Count => sent.Count;
    public IEnumerable<EntityState> Sent => sent.Values;

    public void Reset() => sent.Clear();

    // Stop tracking entities without reporting them removed (another machine owns them now).
    public void Forget(Func<long, bool> key)
    {
        foreach (var k in sent.Keys.Where(key).ToList()) sent.Remove(k);
    }

    // Accept the current state as already known by every receiver (after a snapshot).
    public void Baseline(IEnumerable<EntityState> current)
    {
        sent.Clear();
        foreach (var e in current) sent[e.Key] = e;
    }

    // full: every current entity as Upsert (after a receiver reloaded), still with Removes for vanished ones.
    public List<EntityDelta> Produce(IEnumerable<EntityState> current, bool full = false)
    {
        var deltas = new List<EntityDelta>();
        var seen = new HashSet<long>();
        foreach (var e in current.OrderBy(x => x.Kind).ThenBy(x => x.Id))
        {
            seen.Add(e.Key);
            if (full || !sent.TryGetValue(e.Key, out var before))
                deltas.Add(new EntityDelta(e.Kind, e.Id, DeltaOp.Upsert, e.Json));
            else if (EntityKinds.IsCarried(e.Kind))
            {
                if (before.Json != e.Json) deltas.Add(new EntityDelta(e.Kind, e.Id, DeltaOp.Upsert, e.Json));
            }
            else
            {
                var patch = Json.Diff(before.Json, e.Json);
                if (patch != null) deltas.Add(new EntityDelta(e.Kind, e.Id, DeltaOp.Patch, patch));
            }
            sent[e.Key] = e;
        }
        foreach (var key in sent.Keys.Where(k => !seen.Contains(k)).ToList())
        {
            var gone = sent[key];
            sent.Remove(key);
            deltas.Add(new EntityDelta(gone.Kind, gone.Id, DeltaOp.Remove, ""));
        }
        return deltas;
    }
}

// FNV-1a 64 over UTF-16 code units (low byte, then high byte) — same result on every platform.
public static class Hashing
{
    public const ulong Offset = 14695981039346656037UL;
    private const ulong Prime = 1099511628211UL;

    public static ulong Fnv(string s, ulong h = Offset)
    {
        foreach (char c in s)
        {
            h ^= (byte)c; h *= Prime;
            h ^= (byte)(c >> 8); h *= Prime;
        }
        return h;
    }

    public static ulong Fnv(ulong value, ulong h)
    {
        for (int i = 0; i < 8; i++) { h ^= (byte)(value >> (8 * i)); h *= Prime; }
        return h;
    }

    // World hash = FNV over the (kind, id, entity hash) list sorted by kind then id.
    public static ulong World(IEnumerable<EntityState> entities, IDictionary<long, ulong>? perEntity = null)
    {
        ulong h = Offset;
        foreach (var e in entities.OrderBy(x => x.Kind).ThenBy(x => x.Id))
        {
            ulong eh = Fnv(e.Json);
            if (perEntity != null) perEntity[e.Key] = eh;
            h = Fnv((ulong)e.Key, h);
            h = Fnv(eh, h);
        }
        return h;
    }
}
