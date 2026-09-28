using System;
using System.Linq;
using ProcessorTycoonMp.Adapter;
using TMPro;
using UnityEngine;
using Paint = ProcessorTycoonMp.UI.Look.Paint;

namespace ProcessorTycoonMp.UI;

// Small native-style windows: credits (with version, release name and links), incoming business proposals from other
// players, and the bankruptcy notice that replaces the native Game Over during a session (D57).
internal sealed class Dialogs
{
    private readonly MpRuntime runtime;
    private Window? credits, deal, bankrupt;
    private BusinessDeals.Proposal? shownDeal;
    private bool bankruptPending;

    public Dialogs(MpRuntime runtime) => this.runtime = runtime;

    public void ShowCredits()
    {
        credits ??= BuildCredits();
        credits.Show(Vector2.zero);
    }

    public void ShowBankrupt() => bankruptPending = true;

    public void CloseAll()
    {
        credits?.Close();
        bankrupt?.Close();
        deal?.Close();
    }

    public void Tick()
    {
        var incoming = runtime.Session != null ? BusinessDeals.Incoming.FirstOrDefault() : null;
        if (incoming != shownDeal)
        {
            shownDeal = incoming;
            deal?.Destroy();
            deal = incoming != null ? BuildDeal(incoming) : null;
            deal?.Show(new Vector2(0, 60));
        }
        if (bankruptPending && runtime.Session != null)
        {
            bankruptPending = false;
            bankrupt ??= BuildBankrupt();
            bankrupt.Show(new Vector2(0, 40));
        }
        if (runtime.Session == null && bankrupt != null && bankrupt.Visible) bankrupt.Close();
    }

    private Window BuildCredits()
    {
        var w = new Window("About Multiplayer", 440);
        var b = w.Body;
        var head = Kit.Row(b, 12, 52);
        Kit.Icon(head.transform, "employees", 44, Paint.Cta);
        var names = Kit.Column(head.transform, 0);
        Kit.Size(names, flexWidth: 1);
        Kit.Label(names.transform, "<b>" + ModInfo.Name + "</b>", 18);
        Kit.Label(names.transform, $"Version {ModInfo.Version}  “{ModInfo.Release}”  ·  {ModInfo.License} License", 15, Paint.TextLow);
        Kit.Header(b, "Credits");
        var cta = ColorUtility.ToHtmlStringRGB(Look.Of(Paint.Cta));
        Kit.Links(Kit.Label(b, ModInfo.Byline((name, url) => $"<link=\"{url}\"><u><color=#{cta}>{name}</color></u></link>"), 16, wrap: true));
        Kit.Header(b, "Built with");
        foreach (var (name, note) in ModInfo.ThirdParty) Kit.Label(b, $"{name}  <color=#{ColorUtility.ToHtmlStringRGB(Look.Of(Paint.TextLow))}>{note}</color>", 15);
        Kit.Space(b, 2);
        Kit.Label(b, ModInfo.Disclaimer, 13, Paint.TextLow, wrap: true);
        var footer = w.Footer();
        Kit.Button(footer.transform, "Report an issue", () => Application.OpenURL(ModInfo.Issues), height: 28, size: 15);
        Kit.Button(footer.transform, "Discord", () => Application.OpenURL(ModInfo.Discord), height: 28, size: 15);
        Kit.Button(footer.transform, "GitHub", () => Application.OpenURL(ModInfo.Repository), cta: true, height: 28, size: 15);
        Kit.Button(footer.transform, "Close", w.Close, height: 28, size: 15);
        return w;
    }

    private Window BuildDeal(BusinessDeals.Proposal proposal)
    {
        string from = runtime.Session?.Players.FirstOrDefault(p => p.Slot == proposal.FromSlot)?.Name ?? "A player";
        var w = new Window("Business Proposal", 420, closable: false);
        Kit.Label(w.Body, $"<b>{from}</b> proposes a business deal:", 16, wrap: true);
        Kit.Label(w.Body, proposal.Text, 16, wrap: true);
        Kit.Label(w.Body, "Declining changes nothing. The deal starts for both companies when you accept.", 14, Paint.TextLow, wrap: true);
        var footer = w.Footer();
        Kit.Button(footer.transform, "Decline", () => BusinessDeals.Answer(proposal, accept: false), height: 30);
        Kit.Button(footer.transform, "Accept", () => BusinessDeals.Answer(proposal, accept: true), cta: true, height: 30);
        return w;
    }

    private Window BuildBankrupt()
    {
        var w = new Window("Bankrupt", 420);
        Kit.Header(w.Body, "Your company is out of business");
        Kit.Label(w.Body, "It could not pay its debt. Its projects are cancelled and its products retired.", 16, wrap: true);
        Kit.Label(w.Body, "You stay in the session and can keep watching the others compete. Leaving is always possible from the Multiplayer window.", 15, Paint.TextLow, wrap: true);
        var footer = w.Footer();
        Kit.Button(footer.transform, "Leave session", () => { w.Close(); runtime.Leave(); }, height: 30);
        Kit.Button(footer.transform, "Keep watching", w.Close, cta: true, height: 30);
        return w;
    }
}
