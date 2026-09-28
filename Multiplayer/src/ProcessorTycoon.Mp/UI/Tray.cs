using System;
using System.Linq;
using ProcessorTycoon.TooltipSystem;
using ProcessorTycoonMp.Core.Session;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.SceneManagement;
using UnityEngine.UI;
using Paint = ProcessorTycoonMp.UI.Look.Paint;

namespace ProcessorTycoonMp.UI;

// Status entry in the game's bottom bar during a game, built like the Agent mod's item and placed left of it when that mod
// runs: "Multiplayer · hosting · 3 online". Click opens the Multiplayer window above it. In the main menu the menu's own
// Multiplayer entry takes its place.
internal sealed class Tray
{
    private readonly MpRuntime runtime;
    private readonly RectTransform root;
    private readonly TextMeshProUGUI label;
    private readonly Image icon;
    private string shown = "";

    public RectTransform Item => root;

    public Tray(MpRuntime runtime, Action toggle)
    {
        this.runtime = runtime;
        root = Kit.Rect("Multiplayer tray", Surface.Get().Root);
        root.anchorMin = root.anchorMax = root.pivot = new Vector2(1, 0);
        root.sizeDelta = new Vector2(200, 40);
        var hit = root.gameObject.AddComponent<Image>();
        hit.color = Color.white;
        var button = root.gameObject.AddComponent<Button>();
        button.targetGraphic = hit;
        var colors = ColorBlock.defaultColorBlock;
        colors.normalColor = Color.clear;
        colors.highlightedColor = new Color(1, 1, 1, .12f);
        colors.pressedColor = new Color(1, 1, 1, .21f);
        colors.selectedColor = Color.clear;
        colors.fadeDuration = .1f;
        button.colors = colors;
        button.navigation = new Navigation { mode = Navigation.Mode.None };
        button.onClick.AddListener(() => toggle());
        root.gameObject.AddComponent<HandCursor>();
        // The contents shrink on press like the Agent mod's item (and the game's buttons).
        var content = Kit.Rect("Tray content", root);
        Kit.Stretch(content, 0, 0);
        content.pivot = new Vector2(.5f, .5f);
        root.gameObject.AddComponent<PressFeedback>().Target = content;
        var row = content.gameObject.AddComponent<HorizontalLayoutGroup>();
        row.padding = new RectOffset(10, 10, 0, 0);
        row.spacing = 8;
        row.childAlignment = TextAnchor.MiddleLeft;
        row.childControlWidth = row.childControlHeight = true;
        row.childForceExpandWidth = row.childForceExpandHeight = false;
        icon = Kit.Icon(content, "employees", 18, Paint.Tray);
        label = Kit.Label(content, "Multiplayer", 15, Paint.Tray);
    }

    public void Refresh()
    {
        bool gameplay = SceneManager.GetActiveScene().name != "Main Menu Scene";
        if (root.gameObject.activeSelf != gameplay) root.gameObject.SetActive(gameplay);
        if (!gameplay) return;
        var s = runtime.Session;
        string text = s == null ? (runtime.Busy ? "Multiplayer · resuming…" : "Multiplayer")
            : s.State != SessionState.Running ? (s is PeerSession p && p.PendingSetup != null ? "Multiplayer · founding your company" : "Multiplayer · connecting…")
            : $"Multiplayer · {(s.IsHost ? "hosting" : "joined")} · {s.Players.Count(x => x.Connected)} online";
        if (text != shown)
        {
            shown = text;
            label.text = text;
            root.sizeDelta = new Vector2(label.GetPreferredValues(text).x + 46, 40);
        }
        // Left of the Agent mod's tray when it is there; otherwise where that tray would sit (clock/version space).
        var agent = GameObject.Find("AgentOverlay")?.transform.Find("Agent tray") as RectTransform;
        float right = agent != null && agent.gameObject.activeInHierarchy ? -agent.anchoredPosition.x + agent.sizeDelta.x + 4 : gameplay ? 90 : 140;
        root.anchoredPosition = new Vector2(-right, 0);
        root.SetAsFirstSibling();
    }
}

// "Multiplayer" in the main menu, between Load Game and Settings: a copy of the native menu button with its own action.
internal sealed class MenuEntry
{
    private readonly Action open;
    private GameObject? item;

    public MenuEntry(Action open) => this.open = open;

    public void Refresh()
    {
        if (item != null || !Surface.InMenu) return;
        var buttons = UnityEngine.Object.FindObjectsByType<RectTransform>(FindObjectsSortMode.None).FirstOrDefault(r => r.name == "Buttons" && r.parent != null && r.parent.name == "Menu");
        var template = buttons?.Find("SettingsButton");
        var load = buttons?.Find("LoadGameButton");
        if (buttons == null || template == null) return;
        item = UnityEngine.Object.Instantiate(template.gameObject, buttons);
        item.name = "MultiplayerButton";
        item.transform.SetSiblingIndex(load != null ? load.GetSiblingIndex() + 1 : template.GetSiblingIndex());
        var button = item.GetComponent<Button>();
        button.onClick = new Button.ButtonClickedEvent();   // drop anything copied from Settings
        button.onClick.AddListener(() => open());
        var text = item.GetComponentInChildren<TMP_Text>();
        if (text != null) text.text = "Multiplayer";
        var rect = (RectTransform)item.transform;
        if (text != null) rect.sizeDelta = new Vector2(text.GetPreferredValues("Multiplayer").x + 10, rect.sizeDelta.y);
    }
}
