using ProcessorTycoonMp.Adapter;
using UnityEngine;

namespace ProcessorTycoonMp.UI;

// All of the mod's UI (D59). Nothing is built in headless games (-batchmode): they are driven through mp-dev/cmd.txt.
internal sealed class MpUi
{
    private readonly MainWindow main;
    private readonly Tray tray;
    private readonly MenuEntry menu;
    private readonly Dialogs dialogs;
    private readonly Notices notices;
    private readonly PlayerTags tags;
    private readonly JoinSetup setup;

    public MpUi(MpRuntime runtime, Plugin plugin)
    {
        Look.Refresh();
        dialogs = new Dialogs(runtime);
        main = new MainWindow(runtime, plugin, dialogs.ShowCredits, dialogs.ShowDiagnostics);
        tray = new Tray(runtime, Toggle);
        menu = new MenuEntry(() => Show(true));
        notices = new Notices(runtime);
        tags = new PlayerTags(runtime);
        setup = new JoinSetup(runtime);
        runtime.SetupRequested += setup.Begin;   // the setup screen itself; the tray shows the state
        Bankruptcy.LocalBankrupt += dialogs.ShowBankrupt;
    }

    // One window, two entries: the bottom-bar item during a game (the window opens above it) and the main menu's
    // Multiplayer entry (centred). F9 does the same as the item.
    public void Toggle()
    {
        if (main.Window.Visible) main.Window.Close();
        else if (Surface.InMenu) main.Window.Show(Vector2.zero);
        else main.Window.ShowAbove(tray.Item);
    }

    public void Show(bool visible) { if (!visible) main.Window.Close(); else if (!main.Window.Visible) Toggle(); }
    public void ShowCredits() => dialogs.ShowCredits();
    public void CloseAll() { main.Window.Close(); dialogs.CloseAll(); }

    public void Update()
    {
        Surface.Get().Tick();
        tray.Refresh();
        menu.Refresh();
        main.Refresh();
        main.TickPendingHost(tray.Item);
        setup.Tick();
        dialogs.Tick();
        notices.Tick();
        tags.Tick();
        // The native default countdown stops at "Bankruptcy in 0 days" once the company is bankrupt.
        if (Bankruptcy.LocalSpectator && ProcessorTycoon.Bank.BankruptcyHandler.Instance?.bankruptcyText is { } label && label.text != "Bankrupt · watching")
            label.text = "Bankrupt · watching";
    }
}
