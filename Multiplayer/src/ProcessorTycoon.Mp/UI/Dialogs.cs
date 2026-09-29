using System.Collections.Generic;
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

    // A zip for bug reports, then where it went and what to do with it (Mod API BugReport).
    private void SaveDiagnostics()
    {
        diagnostics?.Destroy();
        diagnostics = BugReport.Show(Surface.Get(), Diagnostics.DiagnosticsBundle.Create, Diagnostics.DiagnosticsBundle.Contents, ModInfo.Issues);
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
