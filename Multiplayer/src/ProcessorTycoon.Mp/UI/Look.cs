using System.Collections.Generic;
using System.Linq;
using ProcessorTycoon.UI.Themes;
using TMPro;
using UnityEngine;

namespace ProcessorTycoonMp.UI;

// D59: the mod's UI is drawn with the game's own font, sprites and current theme (ThemeManager), so it looks like a native
// window in light and dark themes. Values fall back to the light theme as measured from the native windows.
internal static class Look
{
    public enum Paint { Window, Window2, TopBar, TopBarText, CloseIcon, CloseHover, Text, TextLow, Header, Button, ButtonText, ButtonTextOff, Cta, CtaText, Field, Line, LineActive, Scroll, ScrollBack, Positive, Negative, Row, RowAlt, Clear, Tray, Icon, Tab, TabBar, TabText, TabTextHover, TabTextOn }

    public static TMP_FontAsset? Font { get; private set; }
    public static TMP_FontAsset? Mono { get; private set; }
    public static bool Dark { get; private set; }
    public static int Revision { get; private set; }
    private static readonly Dictionary<string, Sprite?> sprites = new();
    private static readonly Dictionary<Paint, Color> palette = new()
    {
        [Paint.Window] = Hex("#F5F5F5"), [Paint.Window2] = Hex("#E9EBEE"), [Paint.TopBar] = Hex("#DFE2E6"), [Paint.TopBarText] = Hex("#2E3033"),
        [Paint.CloseIcon] = Hex("#1A1A1A"), [Paint.CloseHover] = Hex("#E63D17"), [Paint.Text] = Hex("#141414"), [Paint.TextLow] = Hex("#4C4C4C"),
        [Paint.Header] = Hex("#141414"), [Paint.Button] = Color.white, [Paint.ButtonText] = Hex("#141414"), [Paint.ButtonTextOff] = Hex("#9297A0"),
        [Paint.Cta] = Hex("#2784D2"), [Paint.CtaText] = Color.white, [Paint.Field] = Color.white, [Paint.Line] = Hex("#878787"),
        [Paint.LineActive] = Hex("#2784D2"), [Paint.Scroll] = Hex("#878787"), [Paint.ScrollBack] = Hex("#E0E0E0"), [Paint.Positive] = Hex("#126605"),
        [Paint.Negative] = Hex("#C62828"), [Paint.Row] = Color.white, [Paint.RowAlt] = Hex("#F0F1F3"), [Paint.Clear] = Color.clear, [Paint.Tray] = Hex("#C7CCD1"),
        [Paint.Icon] = Hex("#858688"), [Paint.Tab] = Hex("#D9DADB"), [Paint.TabBar] = Hex("#2784D2"), [Paint.TabText] = Hex("#8B8B8C"),
        [Paint.TabTextHover] = Hex("#5C5C5D"), [Paint.TabTextOn] = Hex("#111111"),
    };
    private static Theme? shownTheme;

    public static Color Of(Paint paint) => palette[paint];

    public static Sprite? Sprite(string name)
    {
        if (sprites.TryGetValue(name, out var s) && s != null) return s;
        s = Resources.FindObjectsOfTypeAll<Sprite>().FirstOrDefault(x => x.name == name);
        sprites[name] = s;
        return s;
    }

    // Called every frame by the canvas; cheap unless the theme or scene assets changed.
    public static bool Refresh()
    {
        bool changed = false;
        if (Font == null)
        {
            var fonts = Resources.FindObjectsOfTypeAll<TMP_FontAsset>();
            Font = fonts.FirstOrDefault(f => f.name == "Roboto-Regular SDF") ?? fonts.FirstOrDefault();
            Mono = fonts.FirstOrDefault(f => f.name == "RobotoMono-Regular SDF") ?? Font;
            changed = Font != null;
        }
        var theme = ThemeManager.Instance != null ? ThemeManager.Instance.CurrentTheme : null;
        if (theme != null && theme != shownTheme)
        {
            shownTheme = theme;
            Dark = theme.IsDark;
            palette[Paint.Window] = theme.Background;
            palette[Paint.Window2] = theme.Background2;
            palette[Paint.TopBar] = theme.TopBarBackground;
            palette[Paint.TopBarText] = theme.TopBarText;
            palette[Paint.CloseIcon] = theme.TopBarCloseIcon;
            palette[Paint.CloseHover] = theme.TopBarCloseButton;
            palette[Paint.Text] = theme.Text;
            palette[Paint.TextLow] = theme.TextLowHierarchy;
            palette[Paint.Header] = theme.Header;
            palette[Paint.Button] = theme.ButtonBackground;
            palette[Paint.ButtonText] = theme.ButtonText;
            palette[Paint.ButtonTextOff] = theme.ButtonTextDisabled;
            palette[Paint.Cta] = theme.CTAButtonBackground;
            palette[Paint.CtaText] = theme.CTAButtonText;
            palette[Paint.Field] = theme.InputFieldBackground;
            palette[Paint.Line] = theme.InputBottomLineUnselected;
            palette[Paint.LineActive] = theme.InputBottomLineSelected;
            palette[Paint.Scroll] = theme.ScrollBarHandle;
            palette[Paint.ScrollBack] = theme.ScrollBarBackground;
            palette[Paint.Row] = theme.SpreadsheetColor1;
            palette[Paint.RowAlt] = theme.SpreadsheetColor2;
            palette[Paint.Icon] = theme.IsDark ? theme.TextLowHierarchy : Hex("#858688");   // the native info icon (ThemeableIcon)
            palette[Paint.Tab] = theme.TabBackground;
            palette[Paint.TabBar] = theme.TabBar;
            palette[Paint.TabText] = theme.TabGroupText.NormalColor;
            palette[Paint.TabTextHover] = theme.TabGroupText.MouseOverColor;
            palette[Paint.TabTextOn] = theme.TabGroupText.SelectedColor;
            if (ProcessorTycoon.Colors.Instance != null)
            {
                palette[Paint.Positive] = ProcessorTycoon.Colors.Instance.DynamicPositiveColor();
                palette[Paint.Negative] = ProcessorTycoon.Colors.Instance.DynamicNegativeColor();
            }
            changed = true;
        }
        if (changed) Revision++;
        return changed;
    }

    public static void Reset()
    {
        sprites.Clear();
        Font = null;
        shownTheme = null;
    }

    public static Color Hex(string html) => ColorUtility.TryParseHtmlString(html, out var c) ? c : Color.magenta;
}
