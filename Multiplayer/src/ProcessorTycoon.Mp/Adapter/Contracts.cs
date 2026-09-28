using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using ProcessorTycoon.ContractSystem;
using ProcessorTycoon.Hardware;
using ProcessorTycoon.Save;
using ProcessorTycoonMp.Core.Delta;
using UnityEngine;

namespace ProcessorTycoonMp.Adapter;

// D43: one shared, host-owned contract pool. Contracts are tenders: companies offer CPUs and the client picks a winner
// on the acceptance date. Only the host generates and resolves them (ContractManager is host-only, D37); peers see the
// replicated pool and send their offers to the host as commands.
internal static class Contracts
{
    private static bool applying;

    // ContractID is only a template index: identify a tender by everything fixed when it is created.
    // Culture-invariant: machines may format numbers differently.
    public static int Key(Contract c) => (int)(Hashing.Fnv(FormattableString.Invariant(
        $"{c.ContractID}|{c.Company}|{c.AcceptanceDate?.Year}-{c.AcceptanceDate?.Month}|{c.TerminationDate?.Year}-{c.TerminationDate?.Month}|{c.Units}|{c.PaymentPerUnit}")) & 0x7fffffff);

    private static IEnumerable<Contract> All() =>
        ContractManager.Instance == null ? Enumerable.Empty<Contract>() : ContractManager.Instance.GetAvailableContracts().Concat(ContractManager.Instance.GetActiveContracts());

    public static void Capture(List<EntityState> into)
    {
        var seen = new HashSet<int>();
        foreach (var c in All())
            if (seen.Add(Key(c))) into.Add(new EntityState(EntityKind.Contract, Key(c), JsonUtility.ToJson(DataConverter.ContractToSaveObject(c))));
    }

    // Peers: offers made through the native UI or contract automation go to the host.
    public static void ClearWaiting() => Waiting.Clear();

    public static void OnOffer(Contract contract, int cpuId, bool add)
    {
        if (!Ownership.Active || Ownership.IsHost || applying) return;
        Commands.Enqueue(add ? "contract-offer" : "contract-withdraw", $"{Key(contract)}|{cpuId}");
        EntityIO.Log?.Invoke($"MP: {(add ? "offer" : "withdrawal")} of cpu {cpuId} for {contract.Company} sent to the host");
    }

    // Host: a peer's offer or withdrawal (the game re-validates the CPU against the contract's requirements).
    // A peer's new CPU reaches the host with its next daily delta, which can come after the offer: such commands wait
    // (retried every day, up to 60 days).
    private static readonly List<(string kind, string args, int attempts)> Waiting = new();

    public static void RetryWaiting(Action<string> log)
    {
        if (Waiting.Count == 0) return;
        var retry = Waiting.ToList();
        Waiting.Clear();
        foreach (var (kind, args, attempts) in retry) Execute(kind, args, log, attempts + 1);
    }

    public static void Execute(string kind, string args, Action<string> log, int attempts = 0)
    {
        var parts = args.Split('|');
        if (parts.Length != 2 || !int.TryParse(parts[0], out int key) || !int.TryParse(parts[1], out int cpuId)) return;
        var contract = All().FirstOrDefault(c => Key(c) == key);
        if (contract == null) { log($"MP: {kind} for a contract that no longer exists"); return; }
        if (CpuDataProvider.Instance.GetCpu(cpuId) == null)
        {
            if (attempts < 60) Waiting.Add((kind, args, attempts));
            else log($"MP: {kind} dropped: cpu {cpuId} never arrived");
            return;
        }
        bool before = contract.OffersIDs.Contains(cpuId);
        if (kind == "contract-offer") contract.AddOffer(cpuId);
        else if (kind == "contract-withdraw") contract.RemoveOffer(cpuId);
        bool after = contract.OffersIDs.Contains(cpuId);
        log($"MP: {kind} cpu {cpuId} for {contract.Company}: {(before == after ? "no change (rejected or already so)" : after ? "offer added" : "offer withdrawn")}");
    }

    // Peers: host deltas. New tenders load like saved ones; promotion and termination use the native paths (popups).
    public static void Apply(IEnumerable<EntityDelta> deltas, Action<string> log)
    {
        if (Ownership.IsHost || ContractManager.Instance == null) return;
        var manager = ContractManager.Instance;
        bool changed = false;
        applying = true;
        try
        {
            foreach (var d in deltas)
            {
                var existing = All().FirstOrDefault(c => Key(c) == d.Id);
                if (d.Op == DeltaOp.Remove)
                {
                    if (existing != null) { manager.RemoveContract(existing); changed = true; }
                    continue;
                }
                if (existing == null)
                {
                    if (d.Op != DeltaOp.Upsert) continue;
                    var loaded = JsonUtility.FromJson<SaveObject.Contract>(d.Json);
                    manager.LoadContract(loaded);
                    changed = true;
                    continue;
                }
                bool wasActive = existing.IsActive;
                JsonUtility.FromJsonOverwrite(d.Json, existing);
                if (!wasActive && existing.IsActive)
                {
                    if (CpuDataProvider.Instance.GetCpu(existing.ChosenCpuID) != null) manager.PromoteContractToActive(existing);
                    else { manager.availableContracts.Remove(existing); manager.activeContracts.Add(existing); }
                }
                if (existing.Terminated) manager.RemoveContract(existing);
                changed = true;
            }
        }
        catch (Exception e) { log("MP: applying contracts failed: " + e); }
        finally { applying = false; }
        if (changed) NotifyNewContracts(manager, log);
    }

    private static void NotifyNewContracts(ContractManager manager, Action<string> log)
    {
        try { (typeof(ContractManager).GetField("OnNewContracts", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)?.GetValue(manager) as Action)?.Invoke(); }
        catch (Exception e) { log("MP: contract UI refresh failed: " + e.Message); }
    }
}
