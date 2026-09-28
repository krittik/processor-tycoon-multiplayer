using System;
using System.Collections.Generic;
using ProcessorTycoon;
using ProcessorTycoon.Hardware;

namespace ProcessorTycoonMp.Adapter;

// Architecture ownership (licences) lives in host-owned market data. The game grants the local player ownership
// ("PLAYER", e.g. the x86-64 cross-licensing agreement) on each machine; on a peer that grant is sent to the host,
// which records the player's ghost id so it replicates to everyone (and back to the peer as "PLAYER").
internal static class Licences
{
    private static readonly HashSet<string> Reported = new();

    public static void Reset() => Reported.Clear();

    // Peer, after the native ownership check.
    public static void AfterOwnershipCheck()
    {
        if (!Ownership.Active || Ownership.IsHost || ArchitectureManager.Instance == null) return;
        foreach (var arch in ArchitectureManager.Instance.Architectures)
            if (arch.OwnerIDs.Contains("PLAYER") && Reported.Add(arch.UniqueID))
                Commands.Enqueue("architecture-owner", arch.UniqueID);
    }

    // Host: grant the architecture to the slot's company.
    public static void Execute(int slot, string architectureId, Action<string> log)
    {
        var arch = ArchitectureManager.Instance.GetArchitecture(architectureId);
        string owner = Ownership.GhostId(slot);
        if (arch == null || arch.OwnerIDs.Contains(owner)) return;
        arch.OwnerIDs.Add(owner);
        log($"MP: slot {slot} now owns architecture {architectureId}");
    }
}

// Outgoing commands of this peer for host-owned shared objects (sent by the session each frame).
internal static class Commands
{
    private static readonly Queue<(string kind, string args)> Outgoing = new();

    public static void Enqueue(string kind, string args) => Outgoing.Enqueue((kind, args));

    public static IReadOnlyList<(string kind, string args)> Take()
    {
        var list = new List<(string, string)>(Outgoing);
        Outgoing.Clear();
        return list;
    }

    public static void Clear() => Outgoing.Clear();
}
