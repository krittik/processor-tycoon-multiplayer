using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Text;
using BepInEx.Logging;
using ProcessorTycoon;
using ProcessorTycoon.CompanySystem;
using ProcessorTycoon.Hardware;
using ProcessorTycoon.Save;
using ProcessorTycoon.TimeSystem;
using UnityEngine;

namespace ProcessorTycoonMp.Spikes;

internal static class Owners
{
    // Resolves the company a delegate target belongs to (closure display classes, components, company sub-objects).
    public static string Describe(object? target, Dictionary<object, ICompany> subObjects)
    {
        if (target == null) return "static";
        var t = target.GetType();
        if (t.Name.Contains("DisplayClass"))
        {
            var self = t.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic).FirstOrDefault(f => f.Name.Contains("4__this"));
            if (self != null) return Describe(self.GetValue(target), subObjects);
            return "closure:" + t.FullName;
        }
        if (target is ICompany c) return Label(c);
        if (target is Component comp)
        {
            var owner = comp.GetComponentInParent<ICompany>();
            return owner != null ? Label(owner) + "/" + t.Name : "world";
        }
        if (subObjects.TryGetValue(target, out var sc)) return Label(sc) + "/" + t.Name;
        return "plain";
    }

    public static string Label(ICompany c) => c.IsPlayer ? "PLAYER" : "AI";

    public static Dictionary<object, ICompany> SubObjects()
    {
        var map = new Dictionary<object, ICompany>(ReferenceEqualityComparer.Instance);
        foreach (var c in DataFinder.FindAllCompanies())
        {
            if (c.Factory != null) map[c.Factory] = c;
            if (c.ResearchSector != null) map[c.ResearchSector] = c;
            if (c.ResearchModifiers != null) map[c.ResearchModifiers] = c;
        }
        return map;
    }
}

internal sealed class ReferenceEqualityComparer : IEqualityComparer<object>
{
    public static readonly ReferenceEqualityComparer Instance = new();
    public new bool Equals(object x, object y) => ReferenceEquals(x, y);
    public int GetHashCode(object obj) => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(obj);
}

// S3: full inventory of DateController/SaveHandler event handlers, grouped by target type + method + owner class.
internal sealed class S3Inventory : ISpike
{
    public string Name => "S3-inventory";
    public KeyCode Key => KeyCode.F3;

    public void Run(ManualLogSource log)
    {
        var dc = DateController.Instance;
        if (dc == null) { log.LogWarning("no DateController (load a campaign first)"); return; }
        var sub = Owners.SubObjects();
        var sb = new StringBuilder();
        Dump(sb, dc, sub);
        if (SaveHandler.Instance != null) Dump(sb, SaveHandler.Instance, sub);
        var timers = TimerScheduler.Instance.timers;
        sb.AppendLine($"TimerScheduler.timers: {timers.Count}");
        foreach (var g in timers.GroupBy(t => t.GetType().Name + " tickTarget=" + (t.Tick?.Target?.GetType().Name ?? "null")))
            sb.AppendLine($"  {g.Key} x{g.Count()}");
        var player = Player.Instance.Company;
        sb.AppendLine($"Player company SaveID={player.SaveID} UniqueID={player.UniqueID} type={player.GetType().Name}");
        foreach (var go in SaveIDHandler.Instance.staticInstances)
            sb.AppendLine($"  static instance {go.name} id={go.GetComponent<ISaveID>()?.SaveID}");
        var ai = UnityEngine.Object.FindObjectsByType<AICompany>(FindObjectsSortMode.None).FirstOrDefault();
        if (ai != null) sb.AppendLine($"AICompany components: {string.Join(", ", ai.GetComponents<Component>().Select(x => x.GetType().Name))}");
        sb.AppendLine($"Companies: {string.Join("; ", DataFinder.FindAllCompanies().Select(x => $"{x.Name}#{x.SaveID}[{x.UniqueID}]"))}");
        log.LogInfo("\n" + sb);
    }

    private static void Dump(StringBuilder sb, object source, Dictionary<object, ICompany> sub)
    {
        foreach (var f in source.GetType().GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
        {
            if (!typeof(Delegate).IsAssignableFrom(f.FieldType)) continue;
            if (f.GetValue(source) is not Delegate d) continue;
            var list = d.GetInvocationList();
            sb.AppendLine($"{source.GetType().Name}.{f.Name}: {list.Length}");
            foreach (var g in list.GroupBy(h => $"{h.Target?.GetType().Name ?? h.Method.DeclaringType?.Name}.{h.Method.Name} owner={Owners.Describe(h.Target, sub)}"))
                sb.AppendLine($"  {g.Key} x{g.Count()}");
        }
    }
}

// S1: JsonUtility.FromJsonOverwrite semantics on live model objects.
internal sealed class S1Overwrite : ISpike
{
    public string Name => "S1-overwrite";
    public KeyCode Key => KeyCode.F1;

    public void Run(ManualLogSource log)
    {
        var cpus = DataFinder.FindAllCpus();
        var cpu = cpus.FirstOrDefault(c => c.IsReleased && !c.IsRetired && !c.Company.IsPlayer) ?? cpus.First();
        string json = JsonUtility.ToJson(cpu);
        log.LogInfo($"Cpu '{cpu.Name}' json {json.Length} chars, instanceID refs: {json.Contains("instanceID")}; head: {json.Substring(0, Math.Min(500, json.Length))}");

        var copy = DataConverter.Convert<Cpu>(cpu);
        var market = copy.Market;
        int sold = market.Sold.Count;
        JsonUtility.FromJsonOverwrite("{\"<Market>k__BackingField\":{\"<Stock>k__BackingField\":12345}}", copy);
        log.LogInfo($"nested object: sameRef={ReferenceEquals(market, copy.Market)} stock={copy.Market.Stock} soldCount {sold}->{copy.Market.Sold.Count} (kept = merge, 0 = replace)");

        var copy2 = DataConverter.Convert<Cpu>(cpu);
        var pops = copy2.Popularities;
        JsonUtility.FromJsonOverwrite("{\"<Popularities>k__BackingField\":[0.5]}", copy2);
        log.LogInfo($"list: sameRef={ReferenceEquals(pops, copy2.Popularities)} count={copy2.Popularities.Count} first={copy2.Popularities[0]}");

        var copy3 = DataConverter.Convert<Cpu>(cpu);
        JsonUtility.FromJsonOverwrite(json, copy3);
        log.LogInfo($"full round trip equal: {JsonUtility.ToJson(copy3) == json}");

        string beforeName = cpu.Name; float beforePrice = cpu.Price;
        var company = cpu.Company; var arch = cpu.Architecture;
        JsonUtility.FromJsonOverwrite($"{{\"<Name>k__BackingField\":\"S1 {beforeName}\",\"<Price>k__BackingField\":{(beforePrice + 1).ToString(System.Globalization.CultureInfo.InvariantCulture)}}}", cpu);
        log.LogInfo($"live AI cpu: name '{beforeName}'->'{cpu.Name}', price {beforePrice}->{cpu.Price}, refs kept: company={ReferenceEquals(company, cpu.Company)} arch={ReferenceEquals(arch, cpu.Architecture)} package={cpu.Package != null}");

        var dtoCpu = DataConverter.CpuToSaveObject(cpu);
        dtoCpu.Price += 1; dtoCpu.Name = beforeName;
        string dtoJson = JsonUtility.ToJson(dtoCpu);
        JsonUtility.FromJsonOverwrite(dtoJson, cpu);
        log.LogInfo($"DTO json {dtoJson.Length} chars (instanceID: {dtoJson.Contains("instanceID")}) onto live cpu: name='{cpu.Name}' price={cpu.Price} arch kept={ReferenceEquals(arch, cpu.Architecture)} company kept={ReferenceEquals(company, cpu.Company)} liveEqualsDto={JsonUtility.ToJson(DataConverter.CpuToSaveObject(cpu)) == dtoJson}");
        var cdto = SaveObjectInstantiator.InstantiateToSave(cpu.Company);
        var cf = cdto.Factory; int fjLen = JsonUtility.ToJson(cf).Length;
        JsonUtility.FromJsonOverwrite("{\"Money\":5.0,\"Factory\":{\"<ProductionCapacity>k__BackingField\":777}}", cdto);
        log.LogInfo($"company DTO partial: money={cdto.Money} factorySameRef={ReferenceEquals(cf, cdto.Factory)} capacity={cdto.Factory.ProductionCapacity} factoryJson {fjLen}->{JsonUtility.ToJson(cdto.Factory).Length}");

        var f = Player.Instance.Company.Factory;
        var fc = f.Company;
        string fj = JsonUtility.ToJson(f);
        JsonUtility.FromJsonOverwrite(fj, f);
        log.LogInfo($"factory: json {fj.Length} chars, company ref kept={ReferenceEquals(fc, f.Company)}, round trip equal={JsonUtility.ToJson(f) == fj}");

        var rs = Player.Instance.Company.ResearchSector;
        log.LogInfo($"research sector json {JsonUtility.ToJson(rs).Length} chars: {Head(JsonUtility.ToJson(rs), 300)}");
        var dto = SaveObjectInstantiator.InstantiateToSave(Player.Instance.Company);
        log.LogInfo($"company DTO json {JsonUtility.ToJson(dto).Length} chars: {Head(JsonUtility.ToJson(dto), 400)}");
    }

    private static string Head(string s, int n) => s.Length <= n ? s : s.Substring(0, n) + "…";
}

// S5: cost of building per-entity JSON and hashing for the whole world.
internal sealed class S5Cost : ISpike
{
    public string Name => "S5-cost";
    public KeyCode Key => KeyCode.F5;

    public void Run(ManualLogSource log)
    {
        var sw = Stopwatch.StartNew();
        long bytes = 0; int n = 0; ulong h = 1469598103934665603UL;
        var companies = DataFinder.FindAllCompanies();
        foreach (var c in companies)
        {
            string j = JsonUtility.ToJson(SaveObjectInstantiator.InstantiateToSave(c));
            bytes += j.Length; n++; h = Fnv(h, j);
        }
        long tCompanies = sw.ElapsedMilliseconds;
        var cpus = DataFinder.FindAllCpus();
        int onSale = 0; long saleBytes = 0; var sw2 = new Stopwatch();
        foreach (var cpu in cpus)
        {
            bool hot = cpu.IsReleased && !cpu.IsRetired;
            if (hot) sw2.Start();
            string j = JsonUtility.ToJson(DataConverter.CpuToSaveObject(cpu));
            bytes += j.Length; n++; h = Fnv(h, j);
            if (hot) { sw2.Stop(); onSale++; saleBytes += j.Length; }
        }
        log.LogInfo($"companies {companies.Count} in {tCompanies} ms; cpus {cpus.Count} ({onSale} on sale: {sw2.ElapsedMilliseconds} ms, {saleBytes / 1024} KiB); total {n} entities, {bytes / 1024} KiB, {sw.ElapsedMilliseconds} ms incl. FNV, hash {h:x16}");
    }

    private static ulong Fnv(ulong h, string s)
    {
        foreach (char ch in s) { h ^= ch; h *= 1099511628211UL; }
        return h;
    }
}
