using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using ProcessorTycoon.Save;
using ProcessorTycoon.TimeSystem;
using ProcessorTycoonMp.Adapter;
using ProcessorTycoonMp.Core.Delta;
using UnityEngine;

namespace ProcessorTycoonMp.Diagnostics;

// Session-start diagnostics for game updates (ARCHITECTURE "Diagnostics"): which tick handlers exist and how muting
// classifies them, and whether the save DTOs still have the members this adapter was built against (0.2.16a5).
internal static class SessionReport
{
    // World systems seen in 0.2.16a5 (GAME_INTERNALS "M0 spike results", S3). A new name after a game update means a
    // new system that may need muting (D37) — check it before trusting a session.
    private static readonly HashSet<string> KnownWorldSystems = new()
    {
        "FactoryUpgradeHandler", "ContractManager", "InflationHandler", "CpuUnitCostUpdater", "TeamHiringHandler", "TechnologyTimeUpdater",
        "CentralBank", "WaferCostUpdater", "TimerScheduler", "ContractSeller", "Market", "BankruptcyHandler", "CompanyEventHandler",
        "CreatorFactoryExpansion", "BusinessContractManager", "CompanyTypeUpdater", "ContractAutomation", "CompanySpawner", "CpuDataProvider",
        "MarketDataHandler", "MarketDataProvider", "TaxationHandler", "ArchitectureManager", "CompanyDataProvider", "Cheats", "DateController",
    };

    // Member names of the replicated save DTOs in 0.2.16a5 (JsonUtility keys).
    private static readonly Dictionary<string, string[]> KnownSchema = new()
    {
        ["Company"] = new[] { "Name", "Founder", "Color", "IsPlayer", "FoundationDate", "UniqueID", "Money", "IsBankrupt", "DaysInDefault", "Divisions", "Teams", "NodeOptimizations", "Factory", "ResearchSector", "ResearchModifiers", "FinancialReports", "<SaveID>k__BackingField" },
        ["Market"] = new[] { "HasSeenPgaEvent", "PgaDate", "PotentialSales", "ArchitectureMarketDatas" },
        ["SaveObject"] = new[] { "GameVersion", "IdCount", "GameDate", "PlayerPreferences", "Companies", "Cpus", "CustomHardwares", "CpuProjects", "FactoryExpansionProjects", "ResearchProjects", "CustomHardwareProjects", "ContinuousProjects", "AvailableContracts", "ActiveContracts", "BusinessContracts", "Emails", "MarketData", "PlayerBalanceData" },
    };

    public static void Write(Action<string> log)
    {
        try { Handlers(log); } catch (Exception e) { log("MP: hook report failed: " + e.Message); }
        try { Schema(log); } catch (Exception e) { log("MP: schema check failed: " + e.Message); }
    }

    private static void Handlers(Action<string> log)
    {
        var dc = DateController.Instance;
        if (dc == null) return;
        int owned = 0, remote = 0, world = 0, ui = 0;
        var unknown = new SortedSet<string>();
        foreach (var field in typeof(DateController).GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
        {
            if (field.FieldType != typeof(Action) || field.GetValue(dc) is not Delegate d) continue;
            foreach (var handler in d.GetInvocationList())
            {
                var company = Muting.CompanyOf(handler.Target);
                if (company != null) { if (Ownership.OwnsLocally(company)) owned++; else remote++; continue; }
                string type = Muting.TargetType(handler)?.Name ?? "?";
                if (IsUi(type)) ui++;
                else { world++; if (!KnownWorldSystems.Contains(type) && !type.StartsWith("<")) unknown.Add(type); }
            }
        }
        log($"MP: tick handlers: {owned} of companies simulated here, {remote} muted (simulated elsewhere), {world} world systems, {ui} UI");
        if (unknown.Count > 0) log("MP: WARNING unclassified world systems (new in this game version? check muting, D37): " + string.Join(", ", unknown));
    }

    private static bool IsUi(string type) =>
        type.EndsWith("Window") || type.EndsWith("UI") || type.EndsWith("Button") || type.EndsWith("Bar") || type == "Tooltip" || type.Contains("DisplayClass");

    private static void Schema(Action<string> log)
    {
        var samples = new Dictionary<string, string>
        {
            ["Company"] = JsonUtility.ToJson(new SaveObject.Company()),
            ["Market"] = JsonUtility.ToJson(new SaveObject.Market()),
            ["SaveObject"] = JsonUtility.ToJson(new SaveObject()),
        };
        var problems = new List<string>();
        foreach (var kv in samples)
        {
            var actual = Json.Members(kv.Value).Select(m => m.Key).ToList();
            var expected = KnownSchema[kv.Key];
            var added = actual.Except(expected).ToList();
            var removed = expected.Except(actual).ToList();
            if (added.Count > 0) problems.Add($"{kv.Key} has new members {string.Join(", ", added)}");
            if (removed.Count > 0) problems.Add($"{kv.Key} lost members {string.Join(", ", removed)}");
        }
        if (problems.Count == 0) log("MP: save schema matches the adapter (0.2.16a5)");
        else log("MP: WARNING save schema differs from the adapter (possible drift source): " + string.Join("; ", problems));
    }
}
