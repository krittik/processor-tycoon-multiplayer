using System;
using System.Collections.Generic;
using System.Linq;
using ProcessorTycoon;
using ProcessorTycoon.CompanySystem;
using ProcessorTycoon.ProjectSystem;
using ProcessorTycoon.TimeSystem;
using UnityEngine;

namespace ProcessorTycoonMp.Adapter;

// D57: a bankrupt human company is frozen like a bankrupt AI company (AICompany.TriggerBankruptcy: projects cancelled,
// products retired; here also research stopped) and starts nothing new. Its player stays in the session and watches
// instead of the native Game Over, whose only button would end the session.
internal static class Bankruptcy
{
    private static readonly HashSet<int> refusedFor = new();

    // Raised on the bankrupt player's own machine (UI: spectator notice).
    public static event Action? LocalBankrupt;

    public static bool LocalSpectator => Ownership.Active && Player.Instance?.Company?.IsBankrupt == true;

    public static void Freeze(ICompany c)
    {
        if (!Ownership.Active || !c.IsBankrupt || !Ownership.OwnsLocally(c)) return;
        var scheduler = TimerScheduler.Instance;
        int cancelled = 0, retired = 0;
        foreach (var timer in scheduler.timers.ToList())
            if (CompanyOf(timer) == c) { scheduler.Unschedule(timer); cancelled++; }
        foreach (var cpu in c.Owner.GetCpus().Where(x => x.IsReleased && !x.IsRetired).ToList()) { cpu.TriggerRetirement(); retired++; }
        c.ResearchSector.SetCurrentResearch("");
        EntityIO.Log?.Invoke($"MP: {c.Name} is bankrupt: {cancelled} project(s) cancelled, {retired} product(s) retired, research stopped");
    }

    public static void OnLocalBankrupt()
    {
        EntityIO.Log?.Invoke("MP: this player's company is bankrupt; staying in the session as a spectator");
        LocalBankrupt?.Invoke();
    }

    public static bool Refuses(ITimer timer)
    {
        if (!Ownership.Active) return false;
        var company = CompanyOf(timer);
        if (company == null || !company.IsBankrupt) return false;
        if (refusedFor.Add(company.SaveID)) EntityIO.Log?.Invoke($"MP: {company.Name} is bankrupt and cannot start new projects");
        return true;
    }

    public static void Clear() => refusedFor.Clear();

    private static ICompany? CompanyOf(ITimer timer) => timer switch { Project p => p.Company, ProjectContinuous c => c.Company, _ => null };
}
