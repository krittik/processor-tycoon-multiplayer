using System.Collections.Generic;
using System.Linq;
using ProcessorTycoon;
using ProcessorTycoon.TooltipSystem;
using ProcessorTycoonModApi;
using ProcessorTycoonMp.Adapter;
using ProcessorTycoonMp.Core.Protocol;
using ProcessorTycoonMp.Core.Session;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace ProcessorTycoonMp.UI;

// Marks companies led by players wherever the game shows a company name (Business cards, market tables, Inspector...):
// a small person icon after the name with a native tooltip "Player company, led by Bob". The icon has no text, so the
// displayed name and what the Agent mod reads stay exactly the game's.
internal sealed class PlayerTags
{
    private sealed class Tag { public TMP_Text Text = null!; public RectTransform Rect = null!; public TooltipData Tip = null!; public string Company = ""; }

    private readonly MpRuntime runtime;
    private readonly Dictionary<TMP_Text, Tag> tags = new();
    private float next;

    public PlayerTags(MpRuntime runtime) => this.runtime = runtime;

    public void Tick()
    {
        var s = runtime.Session;
        if (s == null || s.State != SessionState.Running || !GameWorld.CampaignLoaded) { Clear(); return; }
        foreach (var tag in tags.Values) Place(tag);
        if (Time.unscaledTime < next) return;
        next = Time.unscaledTime + 1f;
        var players = new Dictionary<string, PlayerInfo>();
        foreach (var p in s.Players.Where(p => p.CompanyId >= 0))
        {
            var c = DataFinder.FindCompany(p.CompanyId);
            if (c != null && !string.IsNullOrEmpty(c.Name)) players[c.Name.Trim()] = p;
        }
        var alive = new HashSet<TMP_Text>();
        foreach (var text in Object.FindObjectsByType<TMP_Text>(FindObjectsSortMode.None))
        {
            if (!text.isActiveAndEnabled || text.canvas == null || text.canvas.rootCanvas.GetComponent(nameof(ModApiOverlay)) != null || text.GetComponentInParent<TMP_InputField>() != null) continue;
            string value = text.text?.Trim() ?? "";
            if (value.Length == 0 || !players.TryGetValue(value, out var player) || text.transform.root.name == "AgentOverlay") continue;
            // Names only: not the big generated logo text of a player company's card.
            if (text.fontSize > 26 || text.textInfo != null && text.textInfo.lineCount > 1) continue;
            alive.Add(text);
            if (!tags.TryGetValue(text, out var tag)) tags[text] = tag = Create(text);
            tag.Company = value;
            tag.Tip.Header = "Player company";
            tag.Tip.Content = $"{value} is led by {player.Name}" + (player.Slot == s.LocalSlot ? " (you)." : ".") + "\n" + Status(s, player);
        }
        foreach (var dead in tags.Keys.Where(t => t == null || !alive.Contains(t)).ToList())
        {
            if (tags[dead].Rect != null) Object.Destroy(tags[dead].Rect.gameObject);
            tags.Remove(dead);
        }
    }

    private static string Status(SessionBase s, PlayerInfo p) =>
        p.Connected ? "Online now." : p.AiControl ? "Away: the AI plays their company until they return." : "Away: their company carries on as they left it.";

    private static Tag Create(TMP_Text text)
    {
        var rect = Ui.Rect("MpPlayerTag", text.transform);
        rect.gameObject.AddComponent<LayoutElement>().ignoreLayout = true;
        rect.anchorMin = rect.anchorMax = new Vector2(0, .5f);
        rect.pivot = new Vector2(0, .5f);
        rect.sizeDelta = new Vector2(16, 16);
        var image = rect.gameObject.AddComponent<Image>();
        image.sprite = Theme.Sprite("person");
        image.preserveAspect = true;
        Painted.Add(image, Paint.Cta);
        var tip = NativeTooltip(rect.gameObject, "Player company");
        var tag = new Tag { Text = text, Rect = rect, Tip = tip };
        Place(tag);
        return tag;
    }

    // The game's own tooltip (TooltipTrigger with runtime TooltipData): these icons sit on the game's canvases.
    private static TooltipData NativeTooltip(GameObject target, string header)
    {
        var data = ScriptableObject.CreateInstance<TooltipData>();
        data.Header = header;
        data.Content = "";
        data.Delay = .15f;
        target.AddComponent<TooltipTrigger>().tooltipData = data;
        return data;
    }

    // Right after the rendered name, inside the text's own rectangle when it is too narrow.
    private static void Place(Tag tag)
    {
        if (tag.Text == null || tag.Rect == null) return;
        var bounds = tag.Text.textBounds;
        var textRect = tag.Text.rectTransform.rect;
        float size = Mathf.Clamp(tag.Text.fontSize, 12, 18);
        tag.Rect.sizeDelta = new Vector2(size, size);
        float x = bounds.size.x > 0 ? bounds.max.x - textRect.xMin + 4 : 0;
        tag.Rect.anchoredPosition = new Vector2(Mathf.Min(x, textRect.width - size), bounds.size.y > 0 ? bounds.center.y - textRect.center.y : 0);
    }

    private void Clear()
    {
        foreach (var tag in tags.Values) if (tag.Rect != null) Object.Destroy(tag.Rect.gameObject);
        tags.Clear();
    }
}
