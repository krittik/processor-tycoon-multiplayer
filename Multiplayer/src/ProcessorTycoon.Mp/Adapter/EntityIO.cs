using System;
using System.Collections.Generic;
using System.Linq;
using ProcessorTycoon;
using ProcessorTycoon.CompanySystem;
using ProcessorTycoon.Hardware;
using ProcessorTycoon.MarketSystem;
using ProcessorTycoon.MarketSystem.Events;
using ProcessorTycoon.ResearchSystem;
using ProcessorTycoon.Save;
using ProcessorTycoonMp.Core.Delta;
using UnityEngine;

namespace ProcessorTycoonMp.Adapter;

// Replicated entities (D5): canonical JSON from the game's own save converters, applied with FromJsonOverwrite.
// Canonical = identical on every machine: human companies are never "PLAYER" (IsPlayer false, UniqueID MPGHOST-<slot>),
// architecture owners likewise.
internal static class EntityIO
{
    public static Action<string>? Log;
    // Accumulated milliseconds / bytes per "capture.Kind" and "apply.Kind" (status file), exponentially smoothed per day.
    public static readonly Dictionary<string, double> Profile = new();
    private static readonly System.Diagnostics.Stopwatch Clock = System.Diagnostics.Stopwatch.StartNew();
    private static void Add(string key, double since) => Profile[key] = (Profile.TryGetValue(key, out var v) ? v : 0) + (Clock.Elapsed.TotalMilliseconds - since);
    private static double Now => Clock.Elapsed.TotalMilliseconds;

    public static List<ICompany> Companies()
    {
        var list = new List<ICompany>();
        if (Player.Instance?.Company != null) list.Add(Player.Instance.Company);
        list.AddRange(UnityEngine.Object.FindObjectsByType<AICompany>(FindObjectsSortMode.None));
        return list;
    }

    // ---------------- capture ----------------

    // Budget (ARCHITECTURE "State replication"): a full capture on checkpoint eves; otherwise AI companies round-robin
    // (every RoundRobin days), retired CPUs reuse their last capture, and month-scale history lists (financial and factory
    // monthly reports) keep the value of the last full capture, so they travel once a month instead of daily.
    private const int RoundRobin = 3;
    // AI CPUs change slowly between owner updates since every machine runs their production and sales (D47).
    private const int CpuRoundRobin = 5;
    private static readonly Dictionary<long, string> Cache = new();
    private static readonly Dictionary<long, (string? reports, string? factoryReports)> SlowCache = new();
    private const string ReportsKey = "FinancialReports";
    private const string FactoryKey = "Factory";
    private const string FactoryReportsKey = "<MonthlyReports>k__BackingField";

    public static void ClearCaches()
    {
        Cache.Clear();
        SlowCache.Clear();
    }

    public static List<EntityState> Capture(bool ownedOnly, bool full = true, int day = 0, ISet<long>? ownedKeys = null)
    {
        var result = new List<EntityState>();
        var companies = Companies();
        var owned = new HashSet<int>();
        foreach (var c in companies)
        {
            if (ownedOnly && !Ownership.OwnsLocally(c)) continue;
            owned.Add(c.SaveID);
            double t0 = Now;
            long key = new EntityState(EntityKind.Company, c.SaveID, "").Key;
            bool human = Ownership.SlotOf(c) >= 0;
            bool due = full || human || (c.SaveID + day) % RoundRobin == 0 || !Cache.ContainsKey(key);
            string json = due ? SlowKeys(key, full || !SlowCache.ContainsKey(key) ? CompanyJson(c) : CompanyJsonWithoutHistory(c), full) : Cache[key];
            Cache[key] = json;
            result.Add(new EntityState(EntityKind.Company, c.SaveID, json));
            bool mine = ownedKeys != null && Ownership.OwnsLocally(c);
            if (mine) ownedKeys!.Add(key);
            Add("capture.Company", t0);
            t0 = Now;
            foreach (var cpu in c.Owner.GetCpus())
            {
                long cpuKey = new EntityState(EntityKind.Cpu, cpu.SaveID, "").Key;
                // AI CPUs round-robin (their daily sales are recomputed by every machine's market); retired CPUs only on full days.
                bool cpuDue = full || (!cpu.IsRetired && (human || (cpu.SaveID + day) % CpuRoundRobin == 0));
                string cpuJson = cpuDue || !Cache.TryGetValue(cpuKey, out var cached) ? CpuJson(cpu) : cached;
                Cache[cpuKey] = cpuJson;
                result.Add(new EntityState(EntityKind.Cpu, cpu.SaveID, cpuJson));
                if (mine) ownedKeys!.Add(cpuKey);
            }
            Add("capture.Cpu", t0);
        }
        if (ownedOnly) Projects.Capture(result, companies);
        foreach (var h in CustomHardwareHelper.Instance.GetAllCustomHardwares())
        {
            if (!owned.Contains(h.CompanyID)) continue;
            var state = new EntityState(EntityKind.CustomHardware, h.SaveID, JsonUtility.ToJson(h));
            result.Add(state);
            if (ownedKeys != null && Ownership.OwnsLocally(DataFinder.FindCompany(h.CompanyID))) ownedKeys.Add(state.Key);
        }
        if (!ownedOnly || Ownership.IsHost)
        {
            int before = result.Count;
            result.Add(new EntityState(EntityKind.Market, 0, MarketJson()));
            Contracts.Capture(result);
            BusinessDeals.Capture(result);
            if (ownedKeys != null && Ownership.IsHost) for (int i = before; i < result.Count; i++) ownedKeys.Add(result[i].Key);
        }
        return result;
    }

    // Fresh canonical JSON of specific entities (after a drift repair).
    public static List<EntityState> Recapture(IReadOnlyCollection<long> keys)
    {
        var result = new List<EntityState>();
        Dictionary<int, Cpu>? cpus = null;
        List<EntityState>? shared = null;
        foreach (long key in keys)
        {
            var kind = (EntityKind)(key >> 32);
            int id = (int)key;
            switch (kind)
            {
                case EntityKind.Company:
                    var c = DataFinder.FindCompany(id);
                    if (c != null) result.Add(new EntityState(kind, id, CompanyJson(c)));
                    break;
                case EntityKind.Cpu:
                    cpus ??= DataFinder.FindAllCpus().GroupBy(x => x.SaveID).ToDictionary(g => g.Key, g => g.First());
                    if (cpus.TryGetValue(id, out var cpu)) result.Add(new EntityState(kind, id, CpuJson(cpu)));
                    break;
                case EntityKind.CustomHardware:
                    var h = CustomHardwareHelper.Instance.GetCustomHardware(id);
                    if (h != null) result.Add(new EntityState(kind, id, JsonUtility.ToJson(h)));
                    break;
                default:
                    if (shared == null)
                    {
                        shared = new List<EntityState> { new EntityState(EntityKind.Market, 0, MarketJson()) };
                        Contracts.Capture(shared);
                        BusinessDeals.Capture(shared);
                    }
                    result.AddRange(shared.Where(e => e.Key == key));
                    break;
            }
        }
        return result;
    }

    // Full capture: remember the history lists. Lite capture: substitute the remembered values.
    private static string SlowKeys(long key, string json, bool full)
    {
        var members = Json.Members(json);
        int reports = members.FindIndex(m => m.Key == ReportsKey);
        int factory = members.FindIndex(m => m.Key == FactoryKey);
        if (reports < 0 || factory < 0 || !Json.IsObject(members[factory].Value)) return json;
        var factoryMembers = Json.Members(members[factory].Value);
        int factoryReports = factoryMembers.FindIndex(m => m.Key == FactoryReportsKey);
        if (full || !SlowCache.TryGetValue(key, out var slow))
        {
            SlowCache[key] = (members[reports].Value, factoryReports >= 0 ? factoryMembers[factoryReports].Value : null);
            return json;
        }
        if (slow.reports != null) members[reports] = new Json.Member(members[reports].RawKey, slow.reports);
        if (factoryReports >= 0 && slow.factoryReports != null)
        {
            factoryMembers[factoryReports] = new Json.Member(factoryMembers[factoryReports].RawKey, slow.factoryReports);
            members[factory] = new Json.Member(members[factory].RawKey, Json.Build(factoryMembers));
        }
        return Json.Build(members);
    }

    // Lite capture: the history lists are replaced by their cached values anyway, so skip converting them (the save
    // converters round-trip every report through JSON). Main thread only; the lists are restored immediately.
    private static string CompanyJsonWithoutHistory(ICompany c)
    {
        var reports = c.Finance.Reports;
        var monthly = c.Factory.MonthlyReports;
        try
        {
            c.Finance.Reports = new List<CompanyFinance.Report>();
            c.Factory.MonthlyReports = new List<ProcessorTycoon.Production.FactoryBase.MonthlyReport>();
            return CompanyJson(c);
        }
        finally
        {
            c.Finance.Reports = reports;
            c.Factory.MonthlyReports = monthly;
        }
    }

    public static string CompanyJson(ICompany c)
    {
        var dto = SaveObjectInstantiator.InstantiateToSave(c);
        dto.IsPlayer = false;
        int slot = Ownership.SlotOf(c);
        if (slot >= 0) dto.UniqueID = Ownership.GhostId(slot);
        return JsonUtility.ToJson(dto);
    }

    // Untrimmed (unlike the save), so the live object re-serializes to exactly what was applied (S1).
    public static string CpuJson(Cpu cpu) => JsonUtility.ToJson(DataConverter.CpuToSaveObject(cpu));

    public static string MarketJson()
    {
        var m = new SaveObject.Market
        {
            HasSeenPgaEvent = MarketEventHandler.Instance.HasSeenPgaEvent,
            PgaDate = (SaveObject.Date)MarketEventHandler.Instance.PgaDate,
        };
        m.PotentialSales.AddRange(Market.Instance.PotentialSales);
        foreach (var arch in ArchitectureManager.Instance.GetAvailableArchitectures())
        {
            var data = DataConverter.Convert<ArchitectureMarketData>(arch.MarketData);
            data.OwnerIDs = arch.OwnerIDs.Select(ToCanonicalOwner).ToList();
            m.ArchitectureMarketDatas.Add(data);
        }
        return JsonUtility.ToJson(m);
    }

    private static string ToCanonicalOwner(string id) => id == "PLAYER" ? Ownership.GhostId(Ownership.LocalSlot) : id;
    private static string FromCanonicalOwner(string id) => id == Ownership.GhostId(Ownership.LocalSlot) ? "PLAYER" : id;

    // ---------------- apply ----------------

    private static bool restoring;

    // D56, peer after a resync snapshot: the snapshot holds the host's copy of this machine's own entities, which can be a
    // day behind (a change made just before the resync). Write back the members that differ from the pre-load capture.
    public static int RestoreOwned(IReadOnlyList<EntityState> before)
    {
        var wanted = before.Where(e => e.Kind is EntityKind.Company or EntityKind.Cpu or EntityKind.CustomHardware).ToList();
        var now = Recapture(wanted.Select(e => e.Key).ToList()).ToDictionary(e => e.Key);
        var restore = new List<EntityDelta>();
        foreach (var e in wanted)
        {
            if (!now.TryGetValue(e.Key, out var current)) { restore.Add(new EntityDelta(e.Kind, e.Id, DeltaOp.Upsert, e.Json)); continue; }
            string? diff = Json.Diff(current.Json, e.Json);
            if (diff != null) restore.Add(new EntityDelta(e.Kind, e.Id, DeltaOp.Patch, diff));
        }
        restoring = true;
        try { Apply(restore); }
        finally { restoring = false; }
        return restore.Count;
    }

    public static void Apply(IReadOnlyList<EntityDelta> deltas)
    {
        Dictionary<int, Cpu>? cpus = null;
        foreach (var d in deltas)
        {
            if (d.Kind is EntityKind.Contract or EntityKind.BusinessContract) continue;
            double t0 = Now;
            try
            {
                switch (d.Kind)
                {
                    case EntityKind.Company: ApplyCompany(d); break;
                    case EntityKind.CustomHardware: ApplyCustomHardware(d); break;
                    case EntityKind.Cpu:
                        cpus ??= DataFinder.FindAllCpus().GroupBy(c => c.SaveID).ToDictionary(g => g.Key, g => g.First());
                        ApplyCpu(d, cpus);
                        break;
                    case EntityKind.Market: ApplyMarket(d); break;
                    case EntityKind.Projects: Projects.Apply(d); break;
                }
            }
            catch (Exception e) { Log?.Invoke($"MP: applying {d} failed: {e}"); }
            Add("apply." + d.Kind, t0);
            Profile["bytes." + d.Kind] = (Profile.TryGetValue("bytes." + d.Kind, out var b) ? b : 0) + d.Json.Length;
        }
        // Contracts reference CPUs (offers, chosen CPU): after everything else in the batch.
        Contracts.Apply(deltas.Where(d => d.Kind == EntityKind.Contract), m => Log?.Invoke(m));
        BusinessDeals.Apply(deltas.Where(d => d.Kind == EntityKind.BusinessContract), m => Log?.Invoke(m));
    }

    private static void ApplyCompany(EntityDelta d)
    {
        var c = DataFinder.FindCompany(d.Id);
        if (d.Op == DeltaOp.Remove) { Log?.Invoke($"MP: ignoring removal of company {d.Id}"); return; }
        if (c == null)
        {
            if (d.Op != DeltaOp.Upsert) { Log?.Invoke($"MP: patch for unknown company {d.Id}"); return; }
            var created = JsonUtility.FromJson<SaveObject.Company>(d.Json);
            created.IsPlayer = false;
            SaveObjectInstantiator.UnpackFromSave(created);
            Ghosts.AfterSpawn(DataFinder.FindCompany(d.Id));
            Log?.Invoke($"MP: company {created.Name} ({created.UniqueID}#{d.Id}) created");
            return;
        }
        if (Ownership.OwnsLocally(c) && !restoring) { Log?.Invoke($"MP: refusing to overwrite locally owned company {c.Name}"); return; }

        // Only the members present in the delta are meaningful in this DTO; nested objects are merged in place.
        var dto = JsonUtility.FromJson<SaveObject.Company>(d.Json);
        // Research first: refreshing it may unlock technologies locally (appending node optimizations); the owner's
        // lists applied afterwards are authoritative.
        foreach (var member in Json.Members(d.Json).OrderBy(m => m.Key == "ResearchSector" ? 0 : 1))
        {
            switch (member.Key)
            {
                case "Name":
                    c.Name = dto.Name;
                    if (c is AICompany ai && Ownership.IsGhost(c)) ai.FullName = dto.Name;
                    break;
                case "Founder": c.Founder = dto.Founder; break;
                case "Color": if (ColorUtility.TryParseHtmlString(dto.Color, out var color)) c.Color = color; break;
                case "FoundationDate": if (c.FoundationDate != null) JsonUtility.FromJsonOverwrite(member.Value, c.FoundationDate); else c.FoundationDate = dto.FoundationDate; break;
                case "Money": c.MoneyAmount = dto.Money; break;
                case "IsBankrupt": c.LoadBankruptcyState(dto.IsBankrupt); break;
                case "DaysInDefault": c.DaysInDefault = dto.DaysInDefault; break;
                case "Divisions": if (c.Divisions != null) JsonUtility.FromJsonOverwrite(member.Value, c.Divisions); else c.Divisions = dto.Divisions; break;
                case "Factory": JsonUtility.FromJsonOverwrite(member.Value, c.Factory); break;
                case "ResearchModifiers": JsonUtility.FromJsonOverwrite(member.Value, c.ResearchModifiers); break;
                case "ResearchSector": ApplyResearch(c, JsonUtility.FromJson<ResearchSector>(Json.Merge(JsonUtility.ToJson(c.ResearchSector), member.Value))); break;
                case "Teams":
                    c.Teams.Clear();
                    foreach (var t in dto.Teams) c.Teams.Add(DataConverter.SaveObjectToTeam(t));
                    break;
                case "NodeOptimizations":
                    c.LoadNodeOptimizations(dto.NodeOptimizations.Select(n => DataConverter.SaveObjectToNodeOptimization(n)).ToList());
                    if (c.NodeOptimizations.Count > dto.NodeOptimizations.Count) c.NodeOptimizations.RemoveRange(dto.NodeOptimizations.Count, c.NodeOptimizations.Count - dto.NodeOptimizations.Count);
                    break;
                case "FinancialReports":
                    // Not LoadReports: it inserts a placeholder report that only the load path removes again.
                    if (dto.FinancialReports.Count == 0) break;
                    var fin = c.Finance;
                    fin.Reports = dto.FinancialReports.Select(r => DataConverter.SaveObjectToFinancialReport(r)).ToList();
                    fin.currentMonth = fin.Reports[fin.Reports.Count - 1].Month;
                    fin.currentYear = fin.Reports[fin.Reports.Count - 1].Year;
                    break;
            }
        }
    }

    // Research entries hold non-serialized references (company, dependencies, state flags): merge per technology
    // into the live entries, keep the owner's order, then let the sector unlock newly completed technologies.
    private static void ApplyResearch(ICompany c, ResearchSector desired)
    {
        var live = c.ResearchSector;
        live.InnovationEffort = desired.InnovationEffort;
        live.Funding = desired.Funding;
        if (desired.teamOne != null && live.teamOne != null) JsonUtility.FromJsonOverwrite(JsonUtility.ToJson(desired.teamOne), live.teamOne);
        var byId = new Dictionary<string, ResearchSector.Research>();
        foreach (var r in live.researches) byId[r.TechnologyID] = r;
        var ordered = new List<ResearchSector.Research>(live.researches.Count);
        foreach (var r in desired.researches)
        {
            if (!byId.TryGetValue(r.TechnologyID, out var target)) continue;
            JsonUtility.FromJsonOverwrite(JsonUtility.ToJson(r), target);
            ordered.Add(target);
            byId.Remove(r.TechnologyID);
        }
        ordered.AddRange(byId.Values);
        live.researches.Clear();
        live.researches.AddRange(ordered);
        live.Update(statesOnly: true);
    }

    private static void ApplyCpu(EntityDelta d, Dictionary<int, Cpu> cpus)
    {
        if (d.Op == DeltaOp.Remove) { Log?.Invoke($"MP: ignoring removal of cpu {d.Id}"); return; }
        if (cpus.TryGetValue(d.Id, out var cpu))
        {
            if (Ownership.OwnsLocally(cpu.Company) && !restoring) { Log?.Invoke($"MP: refusing to overwrite own cpu {cpu.Name}"); return; }
            MeasureSalesDrift(cpu, d.Json);
            JsonUtility.FromJsonOverwrite(d.Json, cpu);
            return;
        }
        if (d.Op != DeltaOp.Upsert) { Log?.Invoke($"MP: patch for unknown cpu {d.Id}"); return; }
        var dto = JsonUtility.FromJson<SaveObject.Cpu>(d.Json);
        var company = DataFinder.FindCompany(dto.CompanyID);
        if (company == null) { Log?.Invoke($"MP: cpu {dto.Name}#{d.Id} of unknown company {dto.CompanyID}"); return; }
        cpu = DataConverter.SaveObjectToCpu(dto);   // not untrimmed: replicated CPUs are never trimmed
        company.AddCpu(cpu);
        cpus[d.Id] = cpu;
    }

    // D24 measurement: this machine's market computed sales for a CPU it does not own; the owner's figure arrives here.
    // Accumulated as sum |local - owner| / sum owner of SoldLastDay (status "salesDriftPercent").
    public static double SalesOwnerTotal, SalesAbsDiff;

    private static void MeasureSalesDrift(Cpu cpu, string patch)
    {
        string? sold = Json.Get(patch, "<SoldLastDay>k__BackingField");
        if (sold == null || !int.TryParse(sold, out int owner)) return;
        SalesOwnerTotal += owner;
        SalesAbsDiff += Math.Abs(cpu.SoldLastDay - owner);
    }

    private static void ApplyCustomHardware(EntityDelta d)
    {
        if (d.Op == DeltaOp.Remove) return;
        var existing = CustomHardwareHelper.Instance.GetCustomHardware(d.Id);
        if (existing != null) { JsonUtility.FromJsonOverwrite(d.Json, existing); return; }
        if (d.Op != DeltaOp.Upsert) return;
        var created = JsonUtility.FromJson<CustomHardware>(d.Json);
        DataFinder.FindCompany(created.CompanyID)?.AddCustomHardware(created);
    }

    private static void ApplyMarket(EntityDelta d)
    {
        if (Ownership.IsHost || d.Op == DeltaOp.Remove) return;
        string full = d.Op == DeltaOp.Patch ? Json.Merge(MarketJson(), d.Json) : d.Json;
        var m = JsonUtility.FromJson<SaveObject.Market>(full);
        for (int i = 0; i < m.PotentialSales.Count && i < Market.Instance.PotentialSales.Length; i++) Market.Instance.PotentialSales[i] = m.PotentialSales[i];
        foreach (var data in m.ArchitectureMarketDatas)
        {
            var arch = ArchitectureManager.Instance.GetArchitecture(data.ArchitectureID);
            if (arch == null) continue;
            var owners = data.OwnerIDs.Select(FromCanonicalOwner).ToList();
            data.OwnerIDs = owners;
            JsonUtility.FromJsonOverwrite(JsonUtility.ToJson(data), arch.MarketData);
            arch.OwnerIDs = owners;
        }
        MarketEventHandler.Instance.HasSeenPgaEvent = m.HasSeenPgaEvent;
        MarketEventHandler.Instance.PgaDate = (DateTime)m.PgaDate;
    }
}
