using System;
using System.IO;
using System.Linq;
using ProcessorTycoonMp.Adapter;
using UnityEngine;
using Paint = ProcessorTycoonMp.UI.Look.Paint;

namespace ProcessorTycoonMp.UI;

// Small native-style windows: About (version, credits, links, diagnostics for bug reports), incoming business proposals
// from other players, and the bankruptcy notice that replaces the native Game Over during a session (D57).
internal sealed class Dialogs
{
    private readonly MpRuntime runtime;
    private Window? about, deal, bankrupt, diagnostics;
    private BusinessDeals.Proposal? shownDeal;
    private bool bankruptPending;
    private int aboutRevision;

    public Dialogs(MpRuntime runtime) => this.runtime = runtime;

    public void ShowCredits()
    {
        // Links carry theme colours inline: rebuild after a theme change.
        if (about != null && aboutRevision != Look.Revision) { about.Destroy(); about = null; }
        about ??= BuildAbout();
        aboutRevision = Look.Revision;
        about.Show(Vector2.zero);
    }

    public void ShowBankrupt() => bankruptPending = true;

    public void CloseAll()
    {
        about?.Close();
        diagnostics?.Close();
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

    // Laid out like the Agent mod's About window.
    private Window BuildAbout()
    {
        var w = new Window("About Multiplayer", 440);
        var b = w.Body;
        var head = Kit.Row(b, 12, 46);
        Kit.Icon(head.transform, "employees", 40, Paint.Cta);
        var names = Kit.Column(head.transform, 0);
        Kit.Size(names, flexWidth: 1);
        Kit.Label(names.transform, "<b>" + ModInfo.Name + "</b>", 18);
        Kit.Links(Kit.Label(names.transform, $"{ModInfo.Version} “{ModInfo.Release}”  ·  {ModInfo.License} License  ·  {Kit.Link("GitHub", ModInfo.Repository)}  ·  {Kit.Link("Discord", ModInfo.Discord)}", 14, Paint.TextLow));
        Kit.Label(b, ModInfo.Description, 15, wrap: true);
        var byline = Kit.Label(b, ModInfo.Byline((name, url) => (url == ModInfo.ClaudeUrl ? Kit.MarkSpace : "") + Kit.Link(name, url)), 15, wrap: true);
        Kit.Links(byline);
        Kit.Mark(byline, ModInfo.ClaudeUrl, ClaudeMark.Sprite);
        Kit.Label(b, ModInfo.BuiltWith, 14, Paint.TextLow, wrap: true);
        Kit.Label(b, ModInfo.Disclaimer, 13, Paint.TextLow, wrap: true);
        var footer = w.Footer();
        Kit.Button(footer.transform, "Save diagnostics", SaveDiagnostics);
        Kit.Button(footer.transform, "Report an issue", () => Application.OpenURL(ModInfo.Issues));
        return w;
    }

    // A zip for bug reports, then where it went and what to do with it.
    private void SaveDiagnostics()
    {
        string zip = "", error = "";
        try { zip = Diagnostics.DiagnosticsBundle.Create(); }
        catch (Exception e) { error = e.Message; runtime.Log("MP: could not save diagnostics: " + e.Message); }
        diagnostics?.Destroy();
        var w = new Window("Diagnostics", 420);
        var row = Kit.Row(w.Body, 6, 24);
        Kit.Label(row.transform, error.Length > 0 ? "Could not save diagnostics" : "Saved " + Path.GetFileName(zip), 15, error.Length > 0 ? Paint.Negative : Paint.Text);
        if (error.Length == 0) Kit.Info(row.transform, "What is in it", "The game's log (BepInEx/LogOutput.log), the multiplayer settings and any desync reports. Check it for anything private before you share it.");
        Kit.Size(Kit.Rect("Spacer", row.transform), flexWidth: 1);
        Kit.Label(w.Body, error.Length > 0 ? error : "Attach it to your bug report on GitHub or Discord.", 14, Paint.TextLow, wrap: true);
        var footer = w.Footer();
        if (error.Length == 0)
        {
            string folder = Path.GetDirectoryName(zip) ?? "";
            Kit.Button(footer.transform, "Open folder", () => Application.OpenURL("file:///" + folder.Replace('\\', '/')));
        }
        Kit.Button(footer.transform, "Report an issue", () => Application.OpenURL(ModInfo.Issues), cta: true);
        diagnostics = w;
        w.Show(new Vector2(0, -40));
    }

    private Window BuildDeal(BusinessDeals.Proposal proposal)
    {
        string from = runtime.Session?.Players.FirstOrDefault(p => p.Slot == proposal.FromSlot)?.Name ?? "A player";
        var w = new Window("Business Proposal", 420, closable: false);
        Kit.Label(w.Body, $"<b>{from}</b> proposes a business deal:", 15, wrap: true);
        Kit.Label(w.Body, proposal.Text, 15, wrap: true);
        Kit.Label(w.Body, "Declining changes nothing. The deal starts for both companies when you accept.", 14, Paint.TextLow, wrap: true);
        var footer = w.Footer();
        Kit.Button(footer.transform, "Decline", () => BusinessDeals.Answer(proposal, accept: false));
        Kit.Button(footer.transform, "Accept", () => BusinessDeals.Answer(proposal, accept: true), cta: true);
        return w;
    }

    private Window BuildBankrupt()
    {
        var w = new Window("Bankrupt", 420);
        Kit.Header(w.Body, "Your company is out of business");
        Kit.Label(w.Body, "It could not pay its debt: its projects are cancelled and its products retired.", 15, wrap: true);
        Kit.Label(w.Body, "You stay in the session and can watch the others compete; leave any time from the Multiplayer window.", 14, Paint.TextLow, wrap: true);
        var footer = w.Footer();
        Kit.Button(footer.transform, "Leave session", () => { w.Close(); runtime.Leave(); });
        Kit.Button(footer.transform, "Keep watching", w.Close, cta: true);
        return w;
    }
}
