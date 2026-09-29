using System;
using ProcessorTycoon;
using ProcessorTycoon.TooltipSystem;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;
using Paint = ProcessorTycoonMp.UI.Look.Paint;

namespace ProcessorTycoonMp.UI;

// Builders for native-looking controls (D59): the game's RoundCorners42 sprite, Roboto font, theme colours, outlines that
// disappear in dark themes like the game's, the hand cursor and tooltips. Layout groups size everything.
//
// UI conventions shared with the Agent mod (D61; its AgentUi follows the same rules):
// - A window is a title bar with a close button, a body and a footer strip. A main window's footer starts with the version
//   link (opens About) and ends with actions for the whole window; the title bar's close button is the only Close.
// - At most one call-to-action (blue) button per section: the most likely next step.
// - State comes first, as a status line (coloured dot, short text); body text shows state and actions only.
// - Explanations go into an info icon's tooltip next to what they explain; a tooltip starts with a short header.
// - A choice between modes or preset values is a segmented strip (the game's tab style), never blue buttons.
// - Sizes: section headers 18 with a 20 px icon, body 15, secondary 14 in the low-hierarchy colour, fine print 13;
//   buttons and fields 28 px high with 15 px text.
// - Credited names link to their closest page.
internal static class Kit
{
    public const string Rounded = "RoundCorners42";

    public static RectTransform Rect(string name, Transform parent)
    {
        var go = new GameObject(name, typeof(RectTransform));
        var rect = (RectTransform)go.transform;
        rect.SetParent(parent, false);
        return rect;
    }

    // Corner scale as on the native controls (pixelsPerUnitMultiplier of RoundCorners42): windows 3, buttons and
    // dropdowns 5, scrollbars 4, input fields 10.
    public static Image Box(Transform parent, string name, Paint paint, string? sprite = Rounded, float corners = 5)
    {
        var rect = Rect(name, parent);
        return Fill(rect, paint, sprite, corners);
    }

    public static Image Fill(RectTransform rect, Paint paint, string? sprite = Rounded, float corners = 5)
    {
        var image = rect.gameObject.AddComponent<Image>();
        var s = sprite != null ? Look.Sprite(sprite) : null;
        if (s != null) { image.sprite = s; image.type = Image.Type.Sliced; image.pixelsPerUnitMultiplier = corners; }
        Painted.Add(image, paint);
        return image;
    }

    public static TextMeshProUGUI Label(Transform parent, string text, float size = 16, Paint paint = Paint.Text, TextAlignmentOptions align = TextAlignmentOptions.MidlineLeft, bool wrap = false)
    {
        var rect = Rect("Text", parent);
        var label = rect.gameObject.AddComponent<TextMeshProUGUI>();
        if (Look.Font != null) label.font = Look.Font;
        label.fontSize = size;
        label.text = text;
        label.alignment = align;
        label.raycastTarget = false;
        label.richText = true;
        label.textWrappingMode = wrap ? TextWrappingModes.Normal : TextWrappingModes.NoWrap;
        label.overflowMode = wrap ? TextOverflowModes.Overflow : TextOverflowModes.Ellipsis;
        Painted.Add(label, paint);
        var layout = rect.gameObject.AddComponent<LayoutElement>();
        if (!wrap) layout.preferredHeight = Mathf.Ceil(size * 1.35f);
        return label;
    }

    public static Image Icon(Transform parent, string sprite, float size, Paint paint)
    {
        var rect = Rect("Icon " + sprite, parent);
        var image = rect.gameObject.AddComponent<Image>();
        image.sprite = Look.Sprite(sprite);
        image.preserveAspect = true;
        image.raycastTarget = false;
        Painted.Add(image, paint);
        var layout = rect.gameObject.AddComponent<LayoutElement>();
        layout.preferredWidth = layout.minWidth = size;
        layout.preferredHeight = layout.minHeight = size;
        return image;
    }

    public static VerticalLayoutGroup Column(Transform parent, float spacing = 6, int padding = 0, string name = "Column")
    {
        var rect = Rect(name, parent);
        var group = rect.gameObject.AddComponent<VerticalLayoutGroup>();
        group.spacing = spacing;
        group.padding = new RectOffset(padding, padding, padding, padding);
        group.childControlWidth = group.childControlHeight = true;
        group.childForceExpandWidth = true;
        group.childForceExpandHeight = false;
        return group;
    }

    public static HorizontalLayoutGroup Row(Transform parent, float spacing = 6, float height = 30, string name = "Row")
    {
        var rect = Rect(name, parent);
        var group = rect.gameObject.AddComponent<HorizontalLayoutGroup>();
        group.spacing = spacing;
        group.childAlignment = TextAnchor.MiddleLeft;
        group.childControlWidth = group.childControlHeight = true;
        group.childForceExpandWidth = false;
        group.childForceExpandHeight = false;
        var layout = rect.gameObject.AddComponent<LayoutElement>();
        layout.minHeight = layout.preferredHeight = height;
        return group;
    }

    public static LayoutElement Size(Component c, float width = -1, float height = -1, float flexWidth = -1)
    {
        var layout = c.GetComponent<LayoutElement>() ?? c.gameObject.AddComponent<LayoutElement>();
        if (width >= 0) layout.preferredWidth = layout.minWidth = width;
        if (height >= 0) layout.preferredHeight = layout.minHeight = height;
        if (flexWidth >= 0) layout.flexibleWidth = flexWidth;
        return layout;
    }

    public static void Space(Transform parent, float height) => Size(Rect("Space", parent), height: height);

    public static TextMeshProUGUI Header(Transform parent, string text) => Label(parent, text, 18, Paint.Header);

    // A native button: white rounded box with an outline, or the blue call-to-action style.
    public static Button Button(Transform parent, string label, Action onClick, bool cta = false, float width = -1, float height = 28, float size = 15)
    {
        var image = Box(parent, "Button " + label, cta ? Paint.Cta : Paint.Button);
        var outline = image.gameObject.AddComponent<Outline>();
        outline.effectColor = new Color(0, 0, 0, .28f);
        outline.effectDistance = new Vector2(1, -1);
        if (!cta) image.gameObject.AddComponent<LightOnly>().Target = outline;
        else outline.enabled = false;
        var button = image.gameObject.AddComponent<Button>();
        button.targetGraphic = image;
        var colors = ColorBlock.defaultColorBlock;
        colors.highlightedColor = new Color(.93f, .93f, .93f);
        colors.pressedColor = new Color(.84f, .84f, .84f);
        colors.selectedColor = Color.white;
        colors.disabledColor = new Color(1, 1, 1, .55f);
        colors.fadeDuration = .08f;
        button.colors = colors;
        button.navigation = new Navigation { mode = Navigation.Mode.None };
        button.onClick.AddListener(() => onClick());
        image.gameObject.AddComponent<HandCursor>();
        image.gameObject.AddComponent<PressFeedback>().Target = image.rectTransform;
        // The label sizes the button (padding 14): no measuring before the text has rendered.
        var fit = image.gameObject.AddComponent<HorizontalLayoutGroup>();
        fit.padding = new RectOffset(14, 14, 0, 0);
        fit.childAlignment = TextAnchor.MiddleCenter;
        fit.childControlWidth = fit.childControlHeight = true;
        fit.childForceExpandWidth = fit.childForceExpandHeight = true;
        var text = Label(image.transform, label, size, cta ? Paint.CtaText : Paint.ButtonText, TextAlignmentOptions.Center);
        var layout = Size(image, height: height);
        // The inner layout group would report flexible size and stretch the button to the row's height.
        layout.flexibleWidth = layout.flexibleHeight = 0;
        layout.minWidth = width >= 0 ? width : 70;
        if (width >= 0) layout.preferredWidth = width;
        return button;
    }

    // The version in a window footer: a quiet text that turns into a link on hover and opens the About window (as in the
    // Agent mod's window).
    public static void VersionLink(Transform parent, string text, Action action)
    {
        var label = Label(parent, text, 14, Paint.TextLow);
        label.raycastTarget = true;
        var button = label.gameObject.AddComponent<Button>();
        button.transition = Selectable.Transition.None;
        button.navigation = new Navigation { mode = Navigation.Mode.None };
        button.onClick.AddListener(() => action());
        label.gameObject.AddComponent<HandCursor>();
        label.gameObject.AddComponent<LinkHover>().Text = label;
    }

    // A link inside a text (see Links), underlined in the call-to-action colour of the current theme.
    public static string Link(string text, string url) => $"<link=\"{url}\"><u><color=#{ColorUtility.ToHtmlStringRGB(Look.Of(Paint.Cta))}>{text}</color></u></link>";

    // A text whose <link="url"> parts open their web page when clicked (hand cursor over the links).
    public static void Links(TextMeshProUGUI label)
    {
        label.raycastTarget = true;
        label.gameObject.AddComponent<TextLinks>();
    }

    public static void SetLabel(Button button, string label) => button.GetComponentInChildren<TextMeshProUGUI>().text = label;

    // Native input: title above, white field, grey bottom line that turns blue while editing.
    public static TMP_InputField Input(Transform parent, string title, string value, int limit, Action<string> changed, string placeholder = "", float width = -1, float flexWidth = -1)
    {
        var column = Column(parent, 3, name: "Input " + title);
        if (width >= 0) Size(column, width, flexWidth: 0);
        if (flexWidth >= 0) Size(column, flexWidth: flexWidth);
        if (title.Length > 0) Label(column.transform, title, 15);
        var field = Box(column.transform, "Field", Paint.Field, corners: 10);
        Size(field, height: 28);
        var outline = field.gameObject.AddComponent<Outline>();
        outline.effectColor = new Color(0, 0, 0, .22f);
        outline.effectDistance = new Vector2(1, -1);
        field.gameObject.AddComponent<LightOnly>().Target = outline;
        var area = Rect("Text Area", field.transform);
        Stretch(area, 8, 8);
        area.gameObject.AddComponent<RectMask2D>();
        var text = Label(area, "", 15);
        Stretch(text.rectTransform, 0, 0);
        var hint = Label(area, placeholder, 15, Paint.ButtonTextOff);
        hint.fontStyle = FontStyles.Italic;
        Stretch(hint.rectTransform, 0, 0);
        var line = Box(field.transform, "BottomLine", Paint.Line, null);
        var lineRect = line.rectTransform;
        lineRect.anchorMin = new Vector2(0, 0); lineRect.anchorMax = new Vector2(1, 0); lineRect.pivot = new Vector2(.5f, 0);
        lineRect.sizeDelta = new Vector2(0, 2); lineRect.anchoredPosition = Vector2.zero;
        var input = field.gameObject.AddComponent<TMP_InputField>();
        input.textViewport = area;
        input.textComponent = text;
        input.placeholder = hint;
        input.characterLimit = limit;
        input.caretColor = Look.Of(Paint.Text);
        input.customCaretColor = true;
        input.text = value;
        input.onValueChanged.AddListener(v => changed(v));
        input.onSelect.AddListener(_ => Painted.Set(line, Paint.LineActive));
        input.onDeselect.AddListener(_ => Painted.Set(line, Paint.Line));
        field.gameObject.AddComponent<TextCursor>();
        return input;
    }

    // Scrolling list: rows are added under the returned content.
    public static RectTransform Scroll(Transform parent, float height, Paint background = Paint.Window2)
    {
        var frame = Box(parent, "Scroll", background);
        Size(frame, height: height);
        var view = Rect("Viewport", frame.transform);
        Stretch(view, 4, 14);
        view.offsetMin = new Vector2(4, 4);
        view.offsetMax = new Vector2(-14, -4);
        view.gameObject.AddComponent<RectMask2D>();
        var content = Column(view, 2, name: "Content");
        var contentRect = (RectTransform)content.transform;
        contentRect.anchorMin = new Vector2(0, 1); contentRect.anchorMax = new Vector2(1, 1); contentRect.pivot = new Vector2(.5f, 1);
        contentRect.sizeDelta = Vector2.zero;
        content.gameObject.AddComponent<ContentSizeFitter>().verticalFit = ContentSizeFitter.FitMode.PreferredSize;
        var bar = Box(frame.transform, "Scrollbar", Paint.ScrollBack, corners: 4);
        var barRect = bar.rectTransform;
        barRect.anchorMin = new Vector2(1, 0); barRect.anchorMax = new Vector2(1, 1); barRect.pivot = new Vector2(1, .5f);
        barRect.sizeDelta = new Vector2(8, -8); barRect.anchoredPosition = new Vector2(-3, 0);
        var handleArea = Rect("Sliding Area", bar.transform);
        Stretch(handleArea, 0, 0);
        var handle = Box(handleArea, "Handle", Paint.Scroll, corners: 4);
        Stretch(handle.rectTransform, 0, 0);
        var scrollbar = bar.gameObject.AddComponent<Scrollbar>();
        scrollbar.handleRect = handle.rectTransform;
        scrollbar.targetGraphic = handle;
        scrollbar.direction = Scrollbar.Direction.BottomToTop;
        var scroll = frame.gameObject.AddComponent<ScrollRect>();
        scroll.viewport = view;
        scroll.content = contentRect;
        scroll.horizontal = false;
        scroll.movementType = ScrollRect.MovementType.Clamped;
        scroll.scrollSensitivity = 25;
        scroll.verticalScrollbar = scrollbar;
        scroll.verticalScrollbarVisibility = ScrollRect.ScrollbarVisibility.AutoHide;
        return contentRect;
    }

    // Section header: icon, title and, when there is more to say, an info icon with that text in its tooltip.
    public static TextMeshProUGUI Section(Transform parent, string title, string icon, string tip = "")
    {
        Space(parent, 2);
        var row = Row(parent, 6, 24);
        Icon(row.transform, icon, 20, Paint.Header);
        var header = Header(row.transform, title);
        if (tip.Length > 0) Info(row.transform, title, tip);
        Size(Rect("Spacer", row.transform), flexWidth: 1);
        return header;
    }

    // The game's grey info icon; hovering it shows the text.
    public static Tip Info(Transform parent, string header, string text)
    {
        var icon = Icon(parent, "info_icon", 18, Paint.Icon);
        icon.raycastTarget = true;
        return Tip.On(icon.gameObject, header, text);
    }

    // Status line: a coloured dot (Painted.Set it) and a short text; returns the row for an info icon or buttons.
    public static (Image dot, TextMeshProUGUI text, HorizontalLayoutGroup row) Status(Transform parent, float size = 16)
    {
        var row = Row(parent, 8, 24);
        var dot = Icon(row.transform, "circle_png_small", 10, Paint.TextLow);
        var text = Label(row.transform, "", size);
        return (dot, text, row);
    }

    // A choice between a few options, drawn like the game's tab strips (Analysis window): grey band, the chosen option in
    // dark text with a blue bar under it. selected() is read every frame, so the strip follows changes made elsewhere.
    public static RectTransform Segments(Transform parent, string[] options, Func<int> selected, Action<int> choose, float height = 28)
    {
        var band = Box(parent, "Segments", Paint.Tab, corners: 5);
        var layout = Size(band, height: height);
        layout.flexibleWidth = 0;
        var row = band.gameObject.AddComponent<HorizontalLayoutGroup>();
        row.padding = new RectOffset(3, 3, 0, 0);
        row.childControlWidth = row.childControlHeight = true;
        row.childForceExpandWidth = row.childForceExpandHeight = true;
        var view = band.gameObject.AddComponent<SegmentsView>();
        view.Selected = selected;
        for (int i = 0; i < options.Length; i++)
        {
            int index = i;
            var hit = Rect("Option " + options[i], band.transform);
            hit.gameObject.AddComponent<Image>().color = Color.clear;
            var button = hit.gameObject.AddComponent<Button>();
            button.transition = Selectable.Transition.None;
            button.navigation = new Navigation { mode = Navigation.Mode.None };
            button.onClick.AddListener(() => choose(index));
            hit.gameObject.AddComponent<HandCursor>();
            var fit = hit.gameObject.AddComponent<HorizontalLayoutGroup>();
            fit.padding = new RectOffset(8, 8, 0, 0);
            fit.childControlWidth = fit.childControlHeight = true;
            fit.childForceExpandWidth = fit.childForceExpandHeight = true;
            var text = Label(hit, options[i], 15, Paint.TabText, TextAlignmentOptions.Center);
            var bar = Box(hit, "Bar", Paint.TabBar, null);
            bar.gameObject.AddComponent<LayoutElement>().ignoreLayout = true;
            var br = bar.rectTransform;
            br.anchorMin = Vector2.zero; br.anchorMax = new Vector2(1, 0); br.pivot = new Vector2(.5f, 0);
            br.sizeDelta = new Vector2(-12, 2); br.anchoredPosition = new Vector2(0, 3);
            var hover = hit.gameObject.AddComponent<SegmentHover>();
            hover.View = view; hover.Index = index;
            view.Options.Add((text, bar));
        }
        return (RectTransform)band.transform;
    }

    // Room for Kit.Mark in front of a linked name; put it right before the <link>.
    public const string MarkSpace = "<space=1.2em>";

    // An icon in front of a linked name inside a text (the Claude mark before "Claude Code"), in the room left by MarkSpace.
    public static void Mark(TextMeshProUGUI text, string linkId, Sprite sprite)
    {
        var rect = Rect("Mark", text.transform);
        rect.gameObject.AddComponent<LayoutElement>().ignoreLayout = true;
        var image = rect.gameObject.AddComponent<Image>();
        image.sprite = sprite;
        image.preserveAspect = true;
        image.raycastTarget = false;
        var mark = text.gameObject.AddComponent<InlineMark>();
        mark.Text = text; mark.Icon = rect; mark.LinkId = linkId;
    }

    // Native tooltip (the game's TooltipHandler) with this text; returns the data so the text can change later. For the
    // game's own texts (PlayerTags); the mod's windows use Tip, which draws on the mod's canvas above them.
    public static TooltipData Tooltip(GameObject target, string header, string content, int width = 0)
    {
        var data = ScriptableObject.CreateInstance<TooltipData>();
        data.Header = header;
        data.Content = content;
        data.Delay = .15f;
        data.PreferedWidth = width;
        if (target.GetComponent<Graphic>() == null) target.AddComponent<Image>().color = Color.clear;
        var trigger = target.AddComponent<TooltipTrigger>();
        trigger.tooltipData = data;
        return data;
    }

    public static void Stretch(RectTransform rect, float left, float right, float top = 0, float bottom = 0)
    {
        rect.anchorMin = Vector2.zero; rect.anchorMax = Vector2.one;
        rect.offsetMin = new Vector2(left, bottom); rect.offsetMax = new Vector2(-right, -top);
    }

    public static void Clear(Transform parent)
    {
        for (int i = parent.childCount - 1; i >= 0; i--) UnityEngine.Object.Destroy(parent.GetChild(i).gameObject);
    }
}

// Keeps a graphic in its theme colour (alpha kept as set).
internal sealed class Painted : MonoBehaviour
{
    public Graphic Target = null!;
    public Paint Paint;
    private int revision = -1;

    public static void Add(Graphic g, Paint paint)
    {
        var p = g.gameObject.AddComponent<Painted>();
        p.Target = g;
        p.Paint = paint;
        p.Apply();
    }

    public static void Set(Graphic g, Paint paint)
    {
        foreach (var p in g.GetComponents<Painted>()) if (p.Target == g) { p.Paint = paint; p.Apply(); }
    }

    private void Apply()
    {
        var c = Look.Of(Paint);
        if (Paint != Paint.Clear) c.a *= Target is TMP_Text && Target.color.a < 1f && Target.color.a > 0f ? Target.color.a : 1f;
        Target.color = c;
        if (Target is TMP_Text t && Look.Font != null && t.font != Look.Font && t.font != Look.Mono) t.font = Look.Font;
        revision = Look.Revision;
    }

    private void LateUpdate() { if (revision != Look.Revision) Apply(); }
}

// Native controls drop their outline in dark themes.
internal sealed class LightOnly : MonoBehaviour
{
    public Behaviour Target = null!;
    private void LateUpdate() { if (Target != null) Target.enabled = !Look.Dark; }
}

// The native button press (the game's UIAnimationButton): a short ease-out shrink while held.
internal sealed class PressFeedback : MonoBehaviour, IPointerDownHandler, IPointerUpHandler, IPointerExitHandler
{
    public RectTransform Target = null!;
    public float Pressed = .9825f;
    private float from = 1, to = 1, started;

    public void OnPointerDown(PointerEventData data) { var s = GetComponent<Selectable>(); if (data.button == PointerEventData.InputButton.Left && (s == null || s.interactable)) Animate(Pressed); }
    public void OnPointerUp(PointerEventData data) { if (data.button == PointerEventData.InputButton.Left) Animate(1); }
    public void OnPointerExit(PointerEventData data) => Animate(1);
    private void OnDisable() { if (Target != null) Target.localScale = Vector3.one; from = to = 1; }
    private void Animate(float scale) { if (Target == null) return; from = Target.localScale.x; to = scale; started = Time.unscaledTime; }

    private void Update()
    {
        if (Target == null) return;
        float progress = Mathf.Clamp01((Time.unscaledTime - started) / .1f);
        float scale = Mathf.Lerp(from, to, 1 - Mathf.Pow(1 - progress, 4));
        Target.localScale = new Vector3(scale, scale, 1);
    }
}

// Version link: underlined in the call-to-action colour while hovered.
internal sealed class LinkHover : MonoBehaviour, IPointerEnterHandler, IPointerExitHandler
{
    public TextMeshProUGUI Text = null!;
    public void OnPointerEnter(PointerEventData data) { Painted.Set(Text, Paint.Cta); Text.fontStyle |= FontStyles.Underline; }
    public void OnPointerExit(PointerEventData data) { Painted.Set(Text, Paint.TextLow); Text.fontStyle &= ~FontStyles.Underline; }
}

internal sealed class TextLinks : MonoBehaviour, IPointerClickHandler, IPointerMoveHandler, IPointerExitHandler
{
    private bool overLink;

    public void OnPointerClick(PointerEventData data)
    {
        var index = LinkAt(data);
        if (index >= 0) Application.OpenURL(GetComponent<TMP_Text>().textInfo.linkInfo[index].GetLinkID());
    }

    public void OnPointerMove(PointerEventData data) => Hand(LinkAt(data) >= 0);
    public void OnPointerExit(PointerEventData data) => Hand(false);
    private void OnDisable() => Hand(false);
    private int LinkAt(PointerEventData data) => TMP_TextUtilities.FindIntersectingLink(GetComponent<TMP_Text>(), data.position, null);

    private void Hand(bool over)
    {
        if (over == overLink || CursorController.Instance == null) return;
        overLink = over;
        CursorController.Instance.SetCursorType(over ? CursorType.Hand : CursorType.Mouse);
    }
}

internal sealed class HandCursor : MonoBehaviour, IPointerEnterHandler, IPointerExitHandler
{
    public void OnPointerEnter(PointerEventData e)
    {
        var s = GetComponent<Selectable>();
        if ((s == null || s.interactable) && CursorController.Instance != null) CursorController.Instance.SetCursorType(CursorType.Hand);
    }

    public void OnPointerExit(PointerEventData e) { if (CursorController.Instance != null) CursorController.Instance.SetCursorType(CursorType.Mouse); }
    private void OnDisable() { if (CursorController.Instance != null) CursorController.Instance.SetCursorType(CursorType.Mouse); }
}

internal sealed class TextCursor : MonoBehaviour, IPointerEnterHandler, IPointerExitHandler
{
    public void OnPointerEnter(PointerEventData e) { if (CursorController.Instance != null) CursorController.Instance.SetCursorType(CursorType.Text); }
    public void OnPointerExit(PointerEventData e) { if (CursorController.Instance != null) CursorController.Instance.SetCursorType(CursorType.Mouse); }
}

// Hover text for the mod's own controls. Header and Text may change while shown.
internal sealed class Tip : MonoBehaviour, IPointerEnterHandler, IPointerExitHandler
{
    public string Header = "", Text = "";

    public static Tip On(GameObject target, string header, string text)
    {
        if (target.GetComponent<Graphic>() == null) target.AddComponent<Image>().color = Color.clear;
        var tip = target.AddComponent<Tip>();
        tip.Header = header;
        tip.Text = text;
        return tip;
    }

    public void OnPointerEnter(PointerEventData e) => TipView.Hover(this);
    public void OnPointerExit(PointerEventData e) => TipView.Leave(this);
    private void OnDisable() => TipView.Leave(this);
}

// The one tooltip box of the mod's canvas, drawn like the game's (dark translucent box, white text) and shown after the
// game's delay (0.15 s) next to the mouse. It lives on the mod's canvas so it shows above the mod's windows.
internal sealed class TipView : MonoBehaviour
{
    private static TipView? instance;
    private Tip? target;
    private float since;
    private RectTransform box = null!;
    private TextMeshProUGUI header = null!, text = null!;

    public static void Hover(Tip tip) { var view = instance != null ? instance : instance = Create(); view.target = tip; view.since = Time.unscaledTime; }
    public static void Leave(Tip tip) { if (instance != null && instance.target == tip) instance.target = null; }

    private static TipView Create()
    {
        var root = Kit.Rect("Tooltip", Surface.Get().Root);
        root.anchorMin = root.anchorMax = Vector2.zero;
        root.sizeDelta = Vector2.zero;
        var view = root.gameObject.AddComponent<TipView>();
        view.box = Kit.Rect("Box", root);
        view.box.anchorMin = view.box.anchorMax = Vector2.zero;
        var back = view.box.gameObject.AddComponent<Image>();
        back.sprite = Look.Sprite(Kit.Rounded);
        back.type = Image.Type.Sliced;
        back.pixelsPerUnitMultiplier = 5;
        back.color = new Color(.137f, .137f, .137f, .93f);
        back.raycastTarget = false;
        var column = view.box.gameObject.AddComponent<VerticalLayoutGroup>();
        column.padding = new RectOffset(10, 10, 7, 9);
        column.spacing = 2;
        column.childControlWidth = column.childControlHeight = true;
        column.childForceExpandWidth = true;
        column.childForceExpandHeight = false;
        view.box.gameObject.AddComponent<ContentSizeFitter>().verticalFit = ContentSizeFitter.FitMode.PreferredSize;
        view.header = Plain(Kit.Label(view.box, "", 17), Color.white);
        view.text = Plain(Kit.Label(view.box, "", 15, wrap: true), new Color(1, 1, 1, .88f));
        view.box.gameObject.SetActive(false);
        return view;
    }

    private static TextMeshProUGUI Plain(TextMeshProUGUI label, Color color)
    {
        DestroyImmediate(label.GetComponent<Painted>());
        label.color = color;
        return label;
    }

    private void LateUpdate()
    {
        bool show = target != null && target.isActiveAndEnabled && Time.unscaledTime - since >= .15f;
        if (box.gameObject.activeSelf != show) box.gameObject.SetActive(show);
        if (!show) return;
        transform.SetAsLastSibling();
        if (header.text != target!.Header || text.text != target.Text)
        {
            if (Look.Font != null) header.font = text.font = Look.Font;
            header.text = target.Header;
            text.text = target.Text;
            header.gameObject.SetActive(target.Header.Length > 0);
            float width = Mathf.Max(header.GetPreferredValues(target.Header).x, text.GetPreferredValues(target.Text).x) + 21;
            box.sizeDelta = new Vector2(Mathf.Min(320, width), box.sizeDelta.y);
        }
        // Below-right of the mouse, flipped to stay on screen.
        var canvas = Surface.Get().Canvas;
        var mouse = (Vector2)Input.mousePosition / Mathf.Max(.01f, canvas.scaleFactor);
        var size = box.sizeDelta;
        bool left = mouse.x + 14 + size.x > Surface.Get().Width, up = mouse.y - 20 - size.y < 0;
        box.pivot = new Vector2(left ? 1 : 0, up ? 0 : 1);
        box.anchoredPosition = mouse + new Vector2(left ? -6 : 14, up ? 12 : -20);
    }
}

internal sealed class SegmentsView : MonoBehaviour
{
    public Func<int> Selected = () => -1;
    public int Hovered = -1;
    public readonly System.Collections.Generic.List<(TextMeshProUGUI text, Image bar)> Options = new();
    private string shown = "";

    private void LateUpdate()
    {
        int selected = Selected();
        string state = $"{selected}|{Hovered}|{Look.Revision}";
        if (state == shown) return;
        shown = state;
        for (int i = 0; i < Options.Count; i++)
        {
            Painted.Set(Options[i].text, i == selected ? Paint.TabTextOn : i == Hovered ? Paint.TabTextHover : Paint.TabText);
            Options[i].bar.enabled = i == selected;
        }
    }
}

internal sealed class SegmentHover : MonoBehaviour, IPointerEnterHandler, IPointerExitHandler
{
    public SegmentsView View = null!;
    public int Index;
    public void OnPointerEnter(PointerEventData e) => View.Hovered = Index;
    public void OnPointerExit(PointerEventData e) { if (View.Hovered == Index) View.Hovered = -1; }
}

// Keeps Kit.Mark's icon in front of the first character of its link, centred on the capital letters.
internal sealed class InlineMark : MonoBehaviour
{
    public TextMeshProUGUI Text = null!;
    public RectTransform Icon = null!;
    public string LinkId = "";

    private void LateUpdate()
    {
        var info = Text.textInfo;
        for (int i = 0; info != null && i < info.linkCount; i++)
        {
            if (info.linkInfo[i].GetLinkID() != LinkId) continue;
            var c = info.characterInfo[info.linkInfo[i].linkTextfirstCharacterIndex];
            float size = Text.fontSize;
            Icon.anchorMin = Icon.anchorMax = Text.rectTransform.pivot;
            Icon.pivot = new Vector2(1, .5f);
            Icon.sizeDelta = new Vector2(size, size);
            Icon.anchoredPosition = new Vector2(c.bottomLeft.x - size * .12f, (c.topLeft.y + c.bottomLeft.y) / 2);
            if (!Icon.gameObject.activeSelf) Icon.gameObject.SetActive(true);
            return;
        }
        if (Icon.gameObject.activeSelf) Icon.gameObject.SetActive(false);
    }
}

// The Claude mark (Anthropic's spark) for the credits, drawn in code rather than shipped as a file: twelve slightly
// uneven rays around a small core, in Claude's orange.
internal static class ClaudeMark
{
    private static readonly (float angle, float length)[] Rays =
        { (7, .89f), (48, .92f), (81, .89f), (116, 1f), (148, .95f), (180, .94f), (214, .89f), (237, .96f), (266, .93f), (299, .92f), (316, .94f), (346, .91f) };
    private static Sprite? sprite;

    public static Sprite Sprite => sprite != null ? sprite : sprite = Draw(64);

    private static Sprite Draw(int size)
    {
        var texture = new Texture2D(size, size, TextureFormat.RGBA32, false) { filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Clamp, name = "Claude mark" };
        var pixels = new Color32[size * size];
        float centre = size / 2f, radius = size / 2f - 1;
        const int samples = 4;
        for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                int hits = 0;
                for (int sy = 0; sy < samples; sy++)
                    for (int sx = 0; sx < samples; sx++)
                        if (Inside((x + (sx + .5f) / samples - centre) / radius, (y + (sy + .5f) / samples - centre) / radius)) hits++;
                pixels[y * size + x] = new Color32(0xD9, 0x77, 0x57, (byte)(255 * hits / (samples * samples)));
            }
        texture.SetPixels32(pixels);
        texture.Apply();
        return Sprite.Create(texture, new Rect(0, 0, size, size), new Vector2(.5f, .5f), 100);
    }

    // Unit coordinates, the longest ray reaching 1.
    private static bool Inside(float x, float y)
    {
        if (x * x + y * y < .27f * .27f) return true;
        foreach (var (angle, length) in Rays)
        {
            float a = angle * Mathf.Deg2Rad, dx = Mathf.Cos(a), dy = Mathf.Sin(a);
            float along = x * dx + y * dy, across = Mathf.Abs(y * dx - x * dy);
            if (along >= 0 && along <= length && across <= Mathf.Lerp(.1f, .065f, along / length)) return true;
            float tx = x - dx * length, ty = y - dy * length;
            if (tx * tx + ty * ty <= .065f * .065f) return true;
        }
        return false;
    }
}
