using System;
using System.Collections.Generic;
using System.IO;
using ProcessorTycoonModApi;
using UnityEngine;

namespace ProcessorTycoonMp.UI;

// Small native-style windows: About (Mod API, with Save diagnostics for bug reports) and the bankruptcy notice that
// replaces the native Game Over during a session (D57). Business proposals arrive by email (BusinessDeals, D62).
internal sealed class Dialogs
{
    private readonly MpRuntime runtime;
    private readonly AboutWindow about;
    private Window? bankrupt, diagnostics;
    private bool bankruptPending;

    public Dialogs(MpRuntime runtime)
    {
        this.runtime = runtime;
        about = new AboutWindow(Surface.Get(), ModInfo.Credits, Theme.Sprite("employees"), new Dictionary<string, Sprite> { [Marks.ClaudeUrl] = Marks.Claude });
        about.AddAction("Save diagnostics", SaveDiagnostics);
    }

    public void ShowCredits() => about.Show();
    public void ShowBankrupt() => bankruptPending = true;

    public void CloseAll()
    {
        about.Close();
        diagnostics?.Close();
        bankrupt?.Close();
    }

    public void Tick()
    {
        about.Tick();
        if (bankruptPending && runtime.Session != null)
        {
            bankruptPending = false;
            bankrupt ??= BuildBankrupt();
            bankrupt.Show(new Vector2(0, 40));
        }
        if (runtime.Session == null && bankrupt != null && bankrupt.Visible) bankrupt.Close();
    }

    // A zip for bug reports, then where it went and what to do with it.
    private void SaveDiagnostics()
    {
        string zip = "", error = "";
        try { zip = Diagnostics.DiagnosticsBundle.Create(); }
        catch (Exception e) { error = e.Message; runtime.Log("MP: could not save diagnostics: " + e.Message); }
        diagnostics?.Destroy();
        var w = new Window(Surface.Get(), "Diagnostics", 420);
        var row = Ui.Row(w.Body, 6, 24);
        Ui.Label(row.transform, error.Length > 0 ? "Could not save diagnostics" : "Saved " + Path.GetFileName(zip), 15, error.Length > 0 ? Paint.Negative : Paint.Text);
        if (error.Length == 0) Ui.Info(row.transform, "What is in it", "The game's log (BepInEx/LogOutput.log), the multiplayer settings and any desync reports. Check it for anything private before you share it.");
        Ui.Spacer(row.transform);
        Ui.Label(w.Body, error.Length > 0 ? error : "Attach it to your bug report on GitHub or Discord.", 14, Paint.TextLow, wrap: true);
        var footer = w.Footer();
        if (error.Length == 0)
        {
            string folder = Path.GetDirectoryName(zip) ?? "";
            Ui.Button(footer.transform, "Open folder", () => Application.OpenURL("file:///" + folder.Replace('\\', '/')));
        }
        Ui.Button(footer.transform, "Report an issue", () => Application.OpenURL(ModInfo.Issues), cta: true);
        diagnostics = w;
        w.Show(new Vector2(0, -40));
    }

    private Window BuildBankrupt()
    {
        var w = new Window(Surface.Get(), "Bankrupt", 420);
        Ui.Header(w.Body, "Your company is out of business");
        Ui.Label(w.Body, "It could not pay its debt: its projects are cancelled and its products retired.", 15, wrap: true);
        Ui.Label(w.Body, "You stay in the session and can watch the others compete; leave any time from the Multiplayer window.", 14, Paint.TextLow, wrap: true);
        var footer = w.Footer();
        Ui.Button(footer.transform, "Leave session", () => { w.Close(); runtime.Leave(); });
        Ui.Button(footer.transform, "Keep watching", w.Close, cta: true);
        return w;
    }
}
