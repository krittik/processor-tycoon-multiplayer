using System;
using System.Collections.Generic;
using System.Linq;
using ProcessorTycoon;
using ProcessorTycoon.CompanySystem;
using ProcessorTycoon.ProjectSystem;
using ProcessorTycoon.Save;
using ProcessorTycoon.TimeSystem;
using ProcessorTycoonMp.Core.Delta;
using UnityEngine;

namespace ProcessorTycoonMp.Adapter;

// In-progress work (CPU, factory expansion, research, custom hardware and continuous projects) lives in TimerScheduler,
// not in the replicated company entities, and only the owner ticks it (Hooks.PassDay). Without help it existed only on
// the owner's machine and was lost whenever that player got the host's world back (rejoin, resync, resume).
// D52: each machine sends the projects of the human companies it simulates as a carried entity; receivers keep the
// latest copy. Every save written during a session carries those copies for the human companies simulated elsewhere,
// and the host restores them into its world when it takes over a disconnected player's company.
internal static class Projects
{
    [Serializable]
    public sealed class Set
    {
        public List<SaveObject.CpuProject> Cpu = new();
        public List<SaveObject.FactoryExpansionProject> Factory = new();
        public List<SaveObject.ResearchProject> Research = new();
        public List<SaveObject.CustomHardwareProject> Hardware = new();
        public List<SaveObject.ProjectContinuous> Continuous = new();
        public int Count => Cpu.Count + Factory.Count + Research.Count + Hardware.Count + Continuous.Count;
    }

    // Company SaveID → latest projects JSON from the machine that simulates it.
    private static readonly Dictionary<int, string> carried = new();

    public static void Clear() => carried.Clear();

    public static Set Of(int companyId)
    {
        var set = new Set();
        foreach (var p in TimerScheduler.Instance.GetTimedObjectsOfType<Project>())
        {
            if (p.Company?.SaveID != companyId) continue;
            var cpu = p.GetCpuProject(); if (cpu != null) set.Cpu.Add(cpu);
            var factory = p.GetFactoryExpansionProject(); if (factory != null) set.Factory.Add(factory);
            var research = p.GetResearchProject(); if (research != null) set.Research.Add(research);
            var hardware = p.GetCustomHardwareProject(); if (hardware != null) set.Hardware.Add(hardware);
        }
        foreach (var c in TimerScheduler.Instance.GetTimedObjectsOfType<ProjectContinuous>())
            if (c.CompanyID == companyId) set.Continuous.Add(SaveObjectInstantiator.InstantiateToSave(c));
        return set;
    }

    // Owner side: one carried entity per human company simulated here (the local player, and on the host the companies
    // of disconnected players).
    public static void Capture(List<EntityState> result, IEnumerable<ICompany> companies)
    {
        foreach (var c in companies)
            if (Ownership.OwnsLocally(c) && Ownership.SlotOf(c) >= 0)
                result.Add(new EntityState(EntityKind.Projects, c.SaveID, JsonUtility.ToJson(Of(c.SaveID))));
    }

    // Receiver side: carried entities always arrive whole (Core); removals are ignored because the company still
    // exists and its new simulating machine sends its own copy.
    public static void Apply(EntityDelta d)
    {
        if (d.Op == DeltaOp.Upsert) carried[d.Id] = d.Json;
    }

    // Prefix of the save serializer (Hooks): human companies simulated elsewhere get their latest carried projects
    // instead of whatever this world holds for them (nothing, or a stale copy).
    public static void Inject(SaveObject save)
    {
        foreach (var entry in carried)
        {
            var company = DataFinder.FindCompany(entry.Key);
            if (company == null || Ownership.OwnsLocally(company) || Ownership.SlotOf(company) < 0) continue;
            var set = JsonUtility.FromJson<Set>(entry.Value);
            int id = entry.Key;
            save.CpuProjects.RemoveAll(p => p.CompanyID == id);
            save.CpuProjects.AddRange(set.Cpu);
            save.FactoryExpansionProjects.RemoveAll(p => p.CompanyID == id);
            save.FactoryExpansionProjects.AddRange(set.Factory);
            save.ResearchProjects.RemoveAll(p => p.CompanyID == id);
            save.ResearchProjects.AddRange(set.Research);
            save.CustomHardwareProjects.RemoveAll(p => p.CompanyID == id);
            save.CustomHardwareProjects.AddRange(set.Hardware);
            save.ContinuousProjects.RemoveAll(p => p.CompanyID == id);
            save.ContinuousProjects.AddRange(set.Continuous);
        }
    }

    // Host, when a player disconnects and the AI takes over (D25): their latest projects replace whatever this world
    // holds for the company, so the AI continues them and a rejoin gets them back with the AI's progress. The world is
    // then the source, so the carried copy is dropped.
    public static int Restore(ICompany company)
    {
        if (!carried.TryGetValue(company.SaveID, out var json)) return -1;
        carried.Remove(company.SaveID);
        var set = JsonUtility.FromJson<Set>(json);
        var scheduler = TimerScheduler.Instance;
        foreach (var p in scheduler.GetTimedObjectsOfType<Project>().Where(p => p.Company?.SaveID == company.SaveID).ToList()) scheduler.Unschedule(p);
        foreach (var c in scheduler.GetTimedObjectsOfType<ProjectContinuous>().Where(c => c.CompanyID == company.SaveID).ToList()) scheduler.Unschedule(c);
        foreach (var p in set.Cpu) SaveObjectInstantiator.InstantiateFromSave(p);
        foreach (var p in set.Factory) SaveObjectInstantiator.InstantiateFromSave(p);
        foreach (var p in set.Research) SaveObjectInstantiator.InstantiateFromSave(p);
        foreach (var p in set.Hardware) SaveObjectInstantiator.InstantiateFromSave(p);
        foreach (var p in set.Continuous) SaveObjectInstantiator.InstantiateFromSave(p);
        return set.Count;
    }
}
