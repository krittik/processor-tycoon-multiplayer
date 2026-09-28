using System;
using ProcessorTycoon;
using ProcessorTycoon.InitialData;
using ProcessorTycoon.Menu;
using ProcessorTycoonMp.Adapter;
using ProcessorTycoonMp.Core.Protocol;
using ProcessorTycoonMp.Core.Session;
using UnityEngine;

namespace ProcessorTycoonMp.UI;

// D58: a new player founds their company on the game's own New Game screen (main menu), with the host's difficulty,
// date and cheats locked. Its Start button (relabelled Join) sends the setup to the host instead of starting a game;
// closing the screen cancels the join. Opened from the game by returning to the main menu first.
internal sealed class JoinSetup
{
    private readonly MpRuntime runtime;
    private SessionRules? rules;
    private MenuGameSetup? screen;
    private bool opened, leftGame;

    public JoinSetup(MpRuntime runtime)
    {
        this.runtime = runtime;
        Hooks.NewGame = TryJoin;
    }

    public void Begin(SessionRules r)
    {
        rules = r;
        opened = false;
        leftGame = false;
    }

    public void Tick()
    {
        if (rules == null) return;
        if (runtime.Session is not PeerSession peer || peer.PendingSetup == null) { Restore(); return; }
        if (!opened)
        {
            if (GameWorld.CampaignLoaded)
            {
                // The shared world replaces the open game anyway: found the company from the main menu.
                if (!leftGame && GameManager.Instance != null) { leftGame = true; GameManager.Instance.QuitToMenu(); }
                return;
            }
            screen = UnityEngine.Object.FindFirstObjectByType<MenuGameSetup>(FindObjectsInactive.Include);
            if (screen != null && Surface.InMenu && screen.difficultyDropdown != null) Open(screen, rules);
            return;
        }
        if (screen == null || !screen.window.activeInHierarchy)
        {
            runtime.Log("MP: the company setup was closed; leaving the session");
            Restore();
            runtime.Leave();
        }
    }

    private void Open(MenuGameSetup s, SessionRules r)
    {
        opened = true;
        s.window.SetActive(true);
        s.difficultyDropdown.SetSelected(Mathf.Clamp(r.Difficulty, 0, 5));
        s.difficultyDropdown.Interactable = false;
        int date = Mathf.Clamp((r.Year - 1975) / 5, 0, s.startDateDropdown.dropdown.options.Count - 1);
        s.startDateDropdown.SetSelected(date);
        s.startDateDropdown.Interactable = false;
        s.enableCheatsToggle.IsOn = r.Cheats;
        s.enableCheatsToggle.Interactable = false;
        if (string.IsNullOrWhiteSpace(s.founderNameInput.Text) && runtime.Session != null)
            s.founderNameInput.Text = runtime.Session.Players.Count > 0 ? PlayerName() : "";
        SetTitle(s, "Join Session", "Join");
        s.UpdateInitialData();
    }

    private string PlayerName()
    {
        var s = runtime.Session!;
        foreach (var p in s.Players) if (p.Slot == s.LocalSlot) return p.Name;
        return "";
    }

    // GameManager.StartNewGame during a pending setup: send it to the host instead of starting a local game.
    private bool TryJoin(PlayerInitialData data)
    {
        if (rules == null || !opened || screen == null) return false;
        runtime.SubmitCompany(From(data));
        var s = screen;
        Restore();
        s.Close();
        return true;
    }

    public static CompanySetup From(PlayerInitialData d) => new()
    {
        Name = d.Name ?? "",
        Founder = d.Founder ?? "",
        Color = "#" + ColorUtility.ToHtmlStringRGB(d.Color),
        CompanyType = (int)d.CompanyType,
        StartingFunds = (long)Math.Round(d.StartingFunds),
        FactorySize = d.FactorySize,
        StartingTechnology = (int)d.StartingTechnology,
    };

    // Scripted joins (headless peers, tests): the same numbers the setup screen would show, from its difficulty table.
    public static CompanySetup Auto(SessionRules r, string company, string founder, int type)
    {
        var s = UnityEngine.Object.FindFirstObjectByType<MenuGameSetup>(FindObjectsInactive.Include);
        if (s != null && s.playerInitialData != null && s.difficultyDropdown != null)
        {
            s.difficultyDropdown.SetSelected(Mathf.Clamp(r.Difficulty, 0, 5));
            s.startDateDropdown.SetSelected(Mathf.Clamp((r.Year - 1975) / 5, 0, s.startDateDropdown.dropdown.options.Count - 1));
            s.companyTypeDropdown.SetSelected(Mathf.Clamp(type, 0, 2));
            s.UpdateInitialData();
            var d = s.playerInitialData;
            d.Name = company; d.Founder = founder;
            d.Color = Color.white;
            return From(d);
        }
        // No setup screen here (in a game): the Normal 1975 new-game values.
        float funds = 500_000f * (type == 0 ? 1 : 5);
        return new CompanySetup { Name = company, Founder = founder, Color = "#FFFFFF", CompanyType = type, StartingFunds = (long)funds, FactorySize = type == 1 ? 0 : type == 2 ? 25 : 5, StartingTechnology = (int)StartingTechnology.Competitive };
    }

    private void Restore()
    {
        if (screen != null)
        {
            try
            {
                screen.difficultyDropdown.Interactable = true;
                screen.startDateDropdown.Interactable = true;
                screen.enableCheatsToggle.Interactable = true;
                SetTitle(screen, "Game Setup", "Start");
            }
            catch (Exception e) { Debug.LogWarning("MP: restoring the game setup screen failed: " + e.Message); }
        }
        screen = null;
        rules = null;
        opened = false;
    }

    private static void SetTitle(MenuGameSetup s, string title, string start)
    {
        var bar = s.window.transform.Find("TopBar/TopBarText")?.GetComponent<TMPro.TMP_Text>();
        if (bar != null) bar.text = title;
        s.startButton.Text = start;
    }
}
