using System;
using System.Linq;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.SceneManagement;
using UnityEngine.UI;
using Object = UnityEngine.Object;
using Paint = ProcessorTycoonMp.UI.Look.Paint;

namespace ProcessorTycoonMp.UI;

internal sealed class MpUiMarker : MonoBehaviour { }

// The mod's overlay canvas: above the game's windows, scaled like the game's main canvas (as the Agent mod's overlay).
internal sealed class Surface
{
    private static Surface? instance;
    public readonly Canvas Canvas;
    public readonly RectTransform Root;
    private Canvas? native;

    private Surface()
    {
        var go = new GameObject("MultiplayerUI", typeof(RectTransform), typeof(Canvas), typeof(GraphicRaycaster), typeof(MpUiMarker));
        Object.DontDestroyOnLoad(go);
        Canvas = go.GetComponent<Canvas>();
        Canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        Canvas.sortingOrder = 32600;   // above the game, below the Agent mod's overlay (32760)
        Root = (RectTransform)go.transform;
    }

    public static Surface Get() => instance ??= new Surface();

    public float Width => Screen.width / Mathf.Max(.01f, Canvas.scaleFactor);
    public float Height => Screen.height / Mathf.Max(.01f, Canvas.scaleFactor);
    public static bool InMenu => SceneManager.GetActiveScene().name == "Main Menu Scene";

    public void Tick()
    {
        Look.Refresh();
        var scene = SceneManager.GetActiveScene();
        if (native == null || !native.isActiveAndEnabled || native.gameObject.scene != scene)
            native = Object.FindObjectsByType<Canvas>(FindObjectsSortMode.None)
                .Where(c => c.isRootCanvas && c.renderMode != RenderMode.WorldSpace && c.gameObject.scene == scene && c.GetComponent<MpUiMarker>() == null)
                .OrderByDescending(c => c.GetComponentsInChildren<Selectable>(true).Length).FirstOrDefault();
        if (native != null) Canvas.scaleFactor = native.scaleFactor;
    }
}

// A native-looking window (the game's GameSetupWindow as reference): shadow, 1 px dark outline, rounded frame, 30 px top
// bar with the title and the close button; content goes into Body. Dragged by its top bar.
internal sealed class Window
{
    public readonly RectTransform Holder;
    public readonly RectTransform Frame;
    public readonly RectTransform Body;
    public readonly TextMeshProUGUI Title;
    public event Action? Closed;

    public Window(string title, float width, bool closable = true)
    {
        var surface = Surface.Get();
        Holder = Kit.Rect("Window " + title, surface.Root);
        Holder.sizeDelta = Vector2.zero;
        var shadow = Kit.Fill(Kit.Rect("Shadow", Holder), Paint.Clear, "shadow", corners: 1);
        shadow.color = Color.white;
        Object.Destroy(shadow.GetComponent<Painted>());
        shadow.raycastTarget = false;
        var outline = Kit.Fill(Kit.Rect("Outline", Holder), Paint.Clear, corners: 3);
        Object.Destroy(outline.GetComponent<Painted>());
        outline.color = new Color(0, 0, 0, .6f);
        outline.raycastTarget = false;
        var frameImage = Kit.Fill(Kit.Rect("Frame", Holder), Paint.Window, corners: 3);
        Frame = frameImage.rectTransform;
        Frame.anchorMin = Frame.anchorMax = Frame.pivot = new Vector2(.5f, .5f);
        Frame.gameObject.AddComponent<Mask>().showMaskGraphic = true;
        var layout = Frame.gameObject.AddComponent<VerticalLayoutGroup>();
        layout.childControlWidth = layout.childControlHeight = true;
        layout.childForceExpandWidth = true;
        layout.childForceExpandHeight = false;
        Frame.gameObject.AddComponent<ContentSizeFitter>().verticalFit = ContentSizeFitter.FitMode.PreferredSize;
        Frame.sizeDelta = new Vector2(width, 100);
        var follow = Holder.gameObject.AddComponent<FollowFrame>();
        follow.Frame = Frame; follow.Shadow = shadow.rectTransform; follow.Outline = outline.rectTransform;

        var bar = Kit.Fill(Kit.Rect("TopBar", Frame), Paint.TopBar, null);
        Kit.Size(bar, height: 30);
        var barShadow = bar.gameObject.AddComponent<Shadow>();
        barShadow.effectColor = new Color(0, 0, 0, .12f);
        barShadow.effectDistance = new Vector2(0, -1);
        bar.gameObject.AddComponent<Drag>().Target = Holder;
        Title = Kit.Label(bar.transform, title, 18, Paint.TopBarText);
        Kit.Stretch(Title.rectTransform, 10, closable ? 44 : 10);
        if (closable)
        {
            var close = Kit.Fill(Kit.Rect("CloseButton", bar.transform), Paint.Clear, null);
            var cr = close.rectTransform;
            cr.anchorMin = new Vector2(1, 0); cr.anchorMax = new Vector2(1, 1); cr.pivot = new Vector2(1, .5f);
            cr.sizeDelta = new Vector2(40, 0); cr.anchoredPosition = Vector2.zero;
            var icon = Kit.Icon(close.transform, "close", 22, Paint.CloseIcon);
            var ir = icon.rectTransform;
            ir.anchorMin = ir.anchorMax = ir.pivot = new Vector2(.5f, .5f); ir.sizeDelta = new Vector2(22, 22); ir.anchoredPosition = Vector2.zero;
            var button = close.gameObject.AddComponent<Button>();
            button.transition = Selectable.Transition.None;
            button.onClick.AddListener(Close);
            var hover = close.gameObject.AddComponent<CloseHover>();
            hover.Back = close; hover.Icon = icon;
            close.gameObject.AddComponent<HandCursor>();
        }
        var body = Kit.Column(Frame, 8, 12, "Body");
        Body = (RectTransform)body.transform;
        Holder.gameObject.SetActive(false);
    }

    public bool Visible => Holder.gameObject.activeSelf;

    public void Show(Vector2? position = null)
    {
        if (position.HasValue) Holder.anchoredPosition = position.Value;
        Holder.gameObject.SetActive(true);
        Holder.SetAsLastSibling();
    }

    public void Close()
    {
        if (!Visible) return;
        Holder.gameObject.SetActive(false);
        Closed?.Invoke();
    }

    public void Toggle() { if (Visible) Close(); else Show(); }
    public void Destroy() => Object.Destroy(Holder.gameObject);

    // Footer strip like the Business window's: a lighter band with buttons at the bottom of the window.
    public HorizontalLayoutGroup Footer()
    {
        var strip = Kit.Fill(Kit.Rect("Footer", Frame), Paint.Window2, null);
        var row = strip.gameObject.AddComponent<HorizontalLayoutGroup>();
        row.padding = new RectOffset(12, 12, 8, 8);
        row.spacing = 8;
        row.childAlignment = TextAnchor.MiddleRight;
        row.childControlWidth = row.childControlHeight = true;
        row.childForceExpandWidth = row.childForceExpandHeight = false;
        return row;
    }
}

internal sealed class FollowFrame : MonoBehaviour
{
    public RectTransform Frame = null!, Shadow = null!, Outline = null!;
    private void LateUpdate()
    {
        var size = Frame.rect.size;
        Place(Outline, size + new Vector2(2, 2));
        Place(Shadow, size + new Vector2(29, 29));
        Frame.anchoredPosition = Vector2.zero;
    }

    private void Place(RectTransform r, Vector2 size)
    {
        r.anchorMin = r.anchorMax = r.pivot = new Vector2(.5f, .5f);
        r.sizeDelta = size;
        r.anchoredPosition = Vector2.zero;
        r.SetSiblingIndex(r == Shadow ? 0 : 1);
    }
}

internal sealed class Drag : MonoBehaviour, IDragHandler, IBeginDragHandler
{
    public RectTransform Target = null!;
    public void OnBeginDrag(PointerEventData e) => Target.SetAsLastSibling();
    public void OnDrag(PointerEventData e)
    {
        var canvas = Target.GetComponentInParent<Canvas>();
        Target.anchoredPosition += e.delta / (canvas != null ? canvas.scaleFactor : 1f);
        var s = Surface.Get();
        var p = Target.anchoredPosition;
        Target.anchoredPosition = new Vector2(Mathf.Clamp(p.x, -s.Width / 2 + 60, s.Width / 2 - 60), Mathf.Clamp(p.y, -s.Height / 2 + 30, s.Height / 2 - 15));
    }
}

internal sealed class CloseHover : MonoBehaviour, IPointerEnterHandler, IPointerExitHandler
{
    public Image Back = null!, Icon = null!;
    public void OnPointerEnter(PointerEventData e) { Painted.Set(Back, Paint.CloseHover); Icon.color = Color.white; Painted.Set(Icon, Paint.CtaText); }
    public void OnPointerExit(PointerEventData e) { Painted.Set(Back, Paint.Clear); Painted.Set(Icon, Paint.CloseIcon); }
}
