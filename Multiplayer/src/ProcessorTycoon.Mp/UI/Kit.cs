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
// disappear in dark themes like the game's, the hand cursor and native tooltips. Layout groups size everything.
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
    public static Button Button(Transform parent, string label, Action onClick, bool cta = false, float width = -1, float height = 30, float size = 16)
    {
        var image = Box(parent, "Button " + label, cta ? Paint.Cta : Paint.Button);
        var outline = image.gameObject.AddComponent<Outline>();
        outline.effectColor = new Color(0, 0, 0, .28f);
        outline.effectDistance = new Vector2(1, -1);
        if (!cta) image.gameObject.AddComponent<LightOnly>().Target = outline;
        else outline.enabled = false;
        var button = image.gameObject.AddComponent<Button>();
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

    // A text whose <link="url"> parts open their web page when clicked (hand cursor over the text).
    public static void Links(TextMeshProUGUI label)
    {
        label.raycastTarget = true;
        label.gameObject.AddComponent<TextLinks>();
        label.gameObject.AddComponent<HandCursor>();
    }

    public static void SetLabel(Button button, string label) => button.GetComponentInChildren<TextMeshProUGUI>().text = label;

    // Native input: title above, white field, grey bottom line that turns blue while editing.
    public static TMP_InputField Input(Transform parent, string title, string value, int limit, Action<string> changed, string placeholder = "", float width = -1)
    {
        var column = Column(parent, 3, name: "Input " + title);
        if (width >= 0) Size(column, width, flexWidth: 0);
        if (title.Length > 0) Label(column.transform, title, 16);
        var field = Box(column.transform, "Field", Paint.Field, corners: 10);
        Size(field, height: 30);
        var outline = field.gameObject.AddComponent<Outline>();
        outline.effectColor = new Color(0, 0, 0, .22f);
        outline.effectDistance = new Vector2(1, -1);
        field.gameObject.AddComponent<LightOnly>().Target = outline;
        var area = Rect("Text Area", field.transform);
        Stretch(area, 8, 8);
        area.gameObject.AddComponent<RectMask2D>();
        var text = Label(area, "", 16);
        Stretch(text.rectTransform, 0, 0);
        var hint = Label(area, placeholder, 16, Paint.ButtonTextOff);
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

    // Native tooltip (the game's TooltipHandler) with this text; returns the data so the text can change later.
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

internal sealed class TextLinks : MonoBehaviour, IPointerClickHandler
{
    public void OnPointerClick(PointerEventData data)
    {
        var text = GetComponent<TMP_Text>();
        var index = TMP_TextUtilities.FindIntersectingLink(text, data.position, null);
        if (index >= 0) Application.OpenURL(text.textInfo.linkInfo[index].GetLinkID());
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
