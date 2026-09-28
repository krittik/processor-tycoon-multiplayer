using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using ProcessorTycoon.CompanySystem;
using ProcessorTycoon.TimeSystem;
using UnityEngine;

namespace ProcessorTycoonMp.Adapter;

// D19: during TriggerTicks each DateController event holds a filtered copy of its invocation list: handlers of companies
// this machine does not simulate are removed; host-only world systems are removed on peers. Restored afterwards.
internal static class Muting
{
    // World systems whose effects create/destroy world entities; they run on the host and replicate (S3 inventory).
    private static readonly HashSet<string> HostOnlySystems = new() { "CompanySpawner", "CompanyEventHandler", "ContractManager", "BusinessContractManager" };

    // Production events whose handlers of a provider company also run on its human clients' machines (D45).
    private static readonly HashSet<string> ProductionEvents = new() { "OnClientProductionTick", "OnContractProductionTick", "OnProductionTick" };

    private static readonly FieldInfo[] EventFields = typeof(DateController)
        .GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
        .Where(f => f.FieldType == typeof(Action) && (f.Name.StartsWith("On") || f.Name == "FirstEverTick"))
        .ToArray();

    private static readonly Dictionary<Delegate, Delegate?> Cache = new();
    private static readonly Dictionary<Type, FieldInfo?> ClosureOwner = new();
    private static readonly Delegate?[] Saved = new Delegate?[EventFields.Length];
    private static readonly Delegate?[] Filtered = new Delegate?[EventFields.Length];
    private static int cacheVersion = -1;
    private static bool swapped;

    public static IReadOnlyList<string> EventNames => EventFields.Select(f => f.Name).ToList();

    public static void Before(DateController dc)
    {
        if (!Ownership.Active) return;
        if (cacheVersion != Ownership.Version || Cache.Count > 512) { Cache.Clear(); cacheVersion = Ownership.Version; }
        for (int i = 0; i < EventFields.Length; i++)
        {
            var original = (Delegate?)EventFields[i].GetValue(dc);
            Saved[i] = original;
            Filtered[i] = original == null ? null : Filter(original, EventFields[i].Name);
            if (!ReferenceEquals(original, Filtered[i])) EventFields[i].SetValue(dc, Filtered[i]);
        }
        swapped = true;
    }

    public static void After(DateController dc)
    {
        if (!swapped) return;
        swapped = false;
        for (int i = 0; i < EventFields.Length; i++)
        {
            var now = (Delegate?)EventFields[i].GetValue(dc);
            if (ReferenceEquals(now, Filtered[i])) { EventFields[i].SetValue(dc, Saved[i]); continue; }
            // Subscriptions made during the tick (e.g. a spawned company) are kept.
            var added = Filtered[i] == null ? now : Delegate.Remove(now, Filtered[i]);
            EventFields[i].SetValue(dc, Delegate.Combine(Saved[i], added));
        }
    }

    public static void Clear() => Cache.Clear();

    private static Delegate? Filter(Delegate d, string eventName)
    {
        if (Cache.TryGetValue(d, out var f)) return f;
        var list = d.GetInvocationList();
        var kept = list.Where(h => Allowed(h, eventName)).ToArray();
        f = kept.Length == list.Length ? d : Delegate.Combine(kept);
        Cache[d] = f;
        return f;
    }

    private static bool Allowed(Delegate handler, string eventName)
    {
        var target = handler.Target;
        var company = CompanyOf(target);
        // Factory handlers (production, operating cost, daily reset, monthly report) run for every company everywhere
        // (D47): production is deterministic, keeps stock realistic for the local market between owner updates, and
        // produces a human client's outsourced CPUs on the client's machine; remote effects are overwritten by owners.
        if (company != null)
        {
            // D55: the caretaker runs the company (production, sales, research progress, costs) but makes no AI decisions.
            if (Ownership.OwnsLocally(company)) return !Ownership.IsCaretaker(company) || !IsAiDecision(target);
            return handler.Method.Name.Contains("InitializeFactory") || (ProductionEvents.Contains(eventName) && BusinessDeals.ProvidesForLocalPlayer(company));
        }
        if (!Ownership.IsHost)
        {
            var type = RealTarget(target)?.GetType() ?? handler.Method.DeclaringType;
            if (type != null && HostOnlySystems.Contains(type.Name)) return false;
        }
        return true;
    }

    // AI behaviour components (pricing, lines, research choices, new CPUs, expansion, contracts): ProcessorTycoon.AISystem.
    private static bool IsAiDecision(object? target) => RealTarget(target)?.GetType().Namespace == "ProcessorTycoon.AISystem";

    // Company a handler belongs to: closures (display classes capture `this`), company components, the company itself.
    public static ICompany? CompanyOf(object? target)
    {
        target = RealTarget(target);
        return target switch
        {
            ICompany c => c,
            Component comp => comp.GetComponentInParent<ICompany>(),
            _ => null,
        };
    }

    public static Type? TargetType(Delegate handler) => RealTarget(handler.Target)?.GetType() ?? handler.Method.DeclaringType;

    private static object? RealTarget(object? target)
    {
        for (int depth = 0; target != null && depth < 3; depth++)
        {
            var type = target.GetType();
            if (!type.Name.Contains("DisplayClass")) return target;
            if (!ClosureOwner.TryGetValue(type, out var field))
            {
                field = type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic).FirstOrDefault(x => x.Name.Contains("4__this"));
                ClosureOwner[type] = field;
            }
            if (field == null) return target;
            target = field.GetValue(target);
        }
        return target;
    }
}
