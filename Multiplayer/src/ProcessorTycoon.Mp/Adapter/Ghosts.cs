using System;
using System.Collections.Generic;
using System.Linq;
using ProcessorTycoon;
using ProcessorTycoon.CompanySystem;
using ProcessorTycoon.Hardware;
using ProcessorTycoon.InitialData;
using ProcessorTycoon.Production.Math;
using ProcessorTycoonMp.Core.Protocol;
using ProcessorTycoon.ResearchSystem;
using ProcessorTycoon.Save;
using ProcessorTycoon.TimeSystem;
using UnityEngine;

namespace ProcessorTycoonMp.Adapter;

// Remote human companies as AICompany instances (D4): cloned from a historical template prefab, AI and simulation muted.
internal static class Ghosts
{
    private static readonly string[] SlotColors = { "#3FA7FF", "#FF5A5A", "#4CD964", "#FFB400", "#B06CFF", "#00D1C1", "#FF7AD9", "#C8C8C8" };

    public static void Spawn(CompanySpawner spawner, string uniqueId, int saveId)
    {
        // Template: the last historical company (only its prefab structure and logo list are used; all state is loaded).
        var template = spawner.historicalCompanies.Last();
        AICompany ghost = UnityEngine.Object.Instantiate(template.companyPrefab);
        ghost.transform.SetParent(spawner.aiCompanies.transform, worldPositionStays: false);
        ghost.SaveID = saveId;
        ghost.UniqueID = uniqueId;
        ghost.Initialize();
    }

    // Host (D58, replaces the D36 clone): the company a new player set up on the native game setup screen, founded now
    // under the host's difficulty with the funds, factory size and technology level of a new game started this year
    // (Company.InitializeAsNewGame). The host's company only lends the save structure; slot-range SaveID (D20).
    public static int CreateForPlayer(int slot, CompanySetup setup)
    {
        // A world saved after an earlier session still has that session's slot-N company (now an AI rival): the new
        // slot-N player continues it (D51).
        var leftover = EntityIO.Companies().FirstOrDefault(c => !c.IsPlayer && c.UniqueID == Ownership.GhostId(slot) && Ownership.SlotOf(c) < 0);
        if (leftover != null)
        {
            Debug.Log($"MP: slot {slot} continues the company {leftover.Name} ({leftover.SaveID}) from an earlier session of this world");
            return leftover.SaveID;
        }
        int id = slot * Ownership.SlotRange;
        if (DataFinder.FindCompany(id) != null) id = IdRanges.Highest(slot) + 1;
        var type = (CompanyType)Mathf.Clamp(setup.CompanyType, 0, 2);
        var now = DateController.Instance.CurrentDate;
        var dto = SaveObjectInstantiator.InstantiateToSave(Player.Instance.Company);
        // A new Divisions object: InstantiateToSave hands out the host company's own and UnpackFromSave adopts it, so every
        // player company created here shared it with the host's company (a player selling their factory made the host
        // "fabless", D54).
        dto.Divisions = new Divisions { Cpu = type != CompanyType.Foundry, Manufacturing = type != CompanyType.CpuFabless, FoundryServices = type == CompanyType.Foundry };
        dto.SaveID = id;
        dto.IsPlayer = false;
        dto.UniqueID = Ownership.GhostId(slot);
        dto.Name = Clean(setup.Name, 22, $"Player {slot + 1}");
        dto.Founder = Clean(setup.Founder, 24, dto.Name);
        // The setup screen starts at white; a player who kept it gets their slot's colour so companies stay distinguishable.
        bool chosen = ColorUtility.TryParseHtmlString(setup.Color, out var picked) && picked != Color.white;
        dto.Color = chosen ? setup.Color : SlotColors[slot % SlotColors.Length];
        dto.FoundationDate = new Date(now.Year, now.Month);
        dto.Money = setup.StartingFunds > 0 ? setup.StartingFunds : 500_000f;
        dto.IsBankrupt = false;
        dto.DaysInDefault = 0;
        dto.Teams.Clear();
        // Same months as the host's history (the finance window expects them), every figure zero.
        var months = dto.FinancialReports.Select(r => JsonUtility.FromJson<SaveObject.FinancialReport>($"{{\"<Year>k__BackingField\":{r.Year},\"<Month>k__BackingField\":{r.Month}}}")).ToList();
        if (months.Count == 0) months.Add(JsonUtility.FromJson<SaveObject.FinancialReport>($"{{\"<Year>k__BackingField\":{now.Year},\"<Month>k__BackingField\":{now.Month}}}"));
        dto.FinancialReports.Clear();
        dto.FinancialReports.AddRange(months);
        dto.NodeOptimizations.Clear();
        foreach (var r in dto.ResearchSector.researches)
        {
            r.Progress = 0f; r.TotalTime = 0; r.TotalInvestment = 0f;
            r.ResearchedYear = 0; r.ResearchedMonth = 0; r.ResearchedDay = 0;
        }
        if (dto.ResearchSector.teamOne != null) dto.ResearchSector.teamOne.CurrentTechnologyID = "";
        dto.ResearchSector.Funding = 1f;
        dto.ResearchSector.InnovationEffort = false;
        dto.Factory.ProductionCapacity = Math.Max(0, setup.FactorySize);
        dto.Factory.DailyProductionCapacity = 0f;
        dto.Factory.MonthlyReports.Clear();
        foreach (var group in dto.Factory.LineGroups) group.Lines = 0;
        SaveObjectInstantiator.UnpackFromSave(dto);
        var ai = (AICompany)DataFinder.FindCompany(id);
        var (year, nodeOffset) = TechnologyYear(now.Year, (StartingTechnology)setup.StartingTechnology);
        ai.ResearchSector.UnlockTechnologies(year, nodeOffset);
        AfterSpawn(ai);   // D53: the derived technology lists keep what the company has researched
        ai.Factory.InitializeLineGroups();
        ai.Factory.OperatingCost = ProductionMath.OperatingCost(ai.Factory.ProductionCapacity, ai);
        foreach (var optimization in ai.NodeOptimizations) optimization.Progress = 1f;
        EntityIO.Log?.Invoke($"MP: slot {slot} founded {dto.Name} ({type}, {dto.Money:0} funds, {ai.Factory.ProductionCapacity} lines, technology of {year})");
        return id;
    }

    // Company.InitializeAsNewGame, from the current year instead of the game's start year.
    private static (int year, int nodeOffset) TechnologyYear(int year, StartingTechnology technology)
    {
        int offset = 0;
        switch (technology)
        {
            case StartingTechnology.AheadOfTime: year += 5; break;
            case StartingTechnology.Competitive: year -= 1; offset = 1; break;
            case StartingTechnology.SlightlyBehind: year -= 3; offset = 3; break;
            case StartingTechnology.Behind: year -= 4; offset = 4; break;
            case StartingTechnology.VeryBehind: year -= 10; break;
        }
        return (Mathf.Clamp(year, 1971, 2030), offset);
    }

    private static string Clean(string? value, int max, string fallback)
    {
        string s = (value ?? "").Trim();
        if (s.Length == 0) return fallback;
        return s.Length > max ? s.Substring(0, max) : s;
    }

    public static void AfterSpawn(ICompany? company)
    {
        if (company is not AICompany ai) return;
        if (Ownership.GhostSlot(ai.UniqueID) >= 0)
        {
            ai.FullName = ai.Name;
            ResetTechnologies(ai);
        }
        ai.Factory?.HandleMissingIndexes();
        try { (typeof(CompanySpawner).GetField("OnCompanySpawned", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public)?.GetValue(CompanySpawner.Instance) as Action)?.Invoke(); }
        catch (Exception e) { Debug.LogWarning("MP: OnCompanySpawned handler failed: " + e.Message); }
    }

    // D53: every ghost after a load (snapshot, checkpoint, or a save made after a session).
    public static void ResetAllTechnologies()
    {
        foreach (var ai in UnityEngine.Object.FindObjectsByType<AICompany>(FindObjectsSortMode.None))
            if (Ownership.GhostSlot(ai.UniqueID) >= 0) ResetTechnologies(ai);
    }

    // D53: AICompany.Initialize unlocks the template's technologies up to its (late) technology year, and the lists
    // derived from them (wafers, nodes, packages, ...) are never rebuilt when the player's research is loaded over it.
    // The daily factory upgrade then rebuilt a ghost's lines toward 300 mm wafers (~$50M/month during an AI takeover), and
    // the AI could design with hardware the player never researched. Remove what the company has not researched; its
    // real research keeps adding technologies through the normal unlock path.
    public static int ResetTechnologies(AICompany ai)
    {
        var researched = new HashSet<Technology>(ai.ResearchSector.researches.Where(r => r.Progress >= 1f && r.Technology != null).Select(r => r.Technology));
        if (researched.Count == 0) return 0;   // research not loaded yet: never leave the factory without a wafer
        var stale = ai.technologies.Where(t => !researched.Contains(t)).ToList();
        foreach (var t in stale)
        {
            ai.technologies.Remove(t);
            switch (t.Hardware)
            {
                case Package: ai.Packages.Remove(DataFinder.FindPackage(t.Name)); break;
                case Memory: ai.Memories.Remove(DataFinder.FindMemory(t.Name)); break;
                case ProcessNode:
                    ai.ProcessNodes.Remove(DataFinder.FindLithography(t.Name));
                    ai.NodeOptimizations.RemoveAll(o => o.NodeID == t.ID);
                    break;
                case Frequency: ai.Frequencies.Remove(DataFinder.FindHardwareOfType<Frequency>(t.Name)); break;
                case CacheSize: ai.CacheSizes.Remove(DataFinder.FindHardwareOfType<CacheSize>(t.Name)); break;
                case WaferSize: ai.WaferSizes.Remove(DataFinder.FindHardwareOfType<WaferSize>(t.Name)); break;
                case Multicore: ai.Multicores.Remove(DataFinder.FindHardwareOfType<Multicore>(t.Name)); break;
            }
        }
        if (stale.Count > 0) Debug.Log($"MP: ghost {ai.UniqueID} dropped {stale.Count} template technologies it has not researched");
        return stale.Count;
    }
}
