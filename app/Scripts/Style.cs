using Godot;
using Path = System.IO.Path;

namespace ArkhamUI;

// Global visual language (reference-style: charcoal + brass + ivory paper).
public static class Style
{
    // Values decoded from the reference program's board_theme.
    public static readonly Color Ink = new("131512");
    public static readonly Color Charcoal = new("171713f5");
    public static readonly Color Brass = new("c3a366");
    public static readonly Color BrassDim = new("a58a54");
    public static readonly Color PanelBorder = new("b3975c");
    public static readonly Color Paper = new("eadbbd");
    public static readonly Color PaperDim = new("b6aa91");
    public static readonly Color Amber = new("e0b56a");
    public static readonly Color HoverText = new("fff1d5");
    public static readonly Color Danger = new("d58b72");
    public static readonly Color Good = new("a6b99b");
    public static readonly Color Willpower = new("8fb6cb"), Intellect = new("ba9ec9"), Combat = new("d78e79"), Agility = new("93b8a0");
    // the neutral class symbol (arkham.build icon-class_neutral, U+E917 in its icon font, Style.ClassIcons; Yunke 11-22: neutral has a mark too)
    public const string NeutralGlyph = "";
    public static readonly Dictionary<string, string> Glyph = new() { ["willpower"] = "A", ["intellect"] = "F", ["combat"] = "D", ["agility"] = "S", ["wild"] = "G", ["action"] = "I", ["fast"] = "u", ["reaction"] = "y", ["guardian"] = "Q", ["seeker"] = "E", ["rogue"] = "T", ["mystic"] = "W", ["survivor"] = "R", ["elder_sign"] = "X", ["auto_fail"] = "Z" };

    public static FontFile? Serif, Kai, Sans, Brush, Icons, Symbols, ClassIcons, Display, Slim;
    static string FontDir => Path.Combine(App.I.Assets, "fonts");

    static FontFile? Load(string name)
    {
        var path = Path.Combine(FontDir, name);
        if (!System.IO.File.Exists(path)) return null;
        var f = new FontFile { Data = System.IO.File.ReadAllBytes(path), Antialiasing = TextServer.FontAntialiasing.Gray, Hinting = TextServer.Hinting.Light, SubpixelPositioning = TextServer.SubpixelPositioning.Auto, MultichannelSignedDistanceField = false };
        return f;
    }

    public static Theme Build()
    {
        Serif = Load("SourceHanSerifCN-Regular.otf");
        Kai = Load("LXGWWenKai-Medium.ttf");
        Sans = Load("SourceHanSansCN-Medium.otf");
        Brush = Load("MaShanZheng-Regular.ttf");
        Icons = Load("arkhamicons.ttf");
        Symbols = Load("arkham.ttf");
        ClassIcons = Load("arkhambuild-icon.woff2");   // arkham.build's icon font (its class_* marks; the neutral one is only here)
        // titles: ZCOOL XiaoWei (OFL, Yunke 11-20: "标题中文字体你来下载"); the few signs it lacks (·, ：…) come from WenKai
        Display = Load("ZCOOLXiaoWei-Regular.ttf");
        if (Display is not null && Kai is not null) Display.Fallbacks = new Godot.Collections.Array<Font> { Kai };
        // the html version's ArkhamSlim (0712 fonts/arkhamslim.ttf): The Drowned City's rune glyphs (U+E900…, CoreCatalog.Runes) in card
        // names, drawn by whichever text font a label uses — none of them has those code points, so they fall back to it
        Slim = Load("arkhamslim.ttf");
        if (Slim is not null)
            foreach (var f in new[] { Serif, Kai, Sans, Brush, Display })
                if (f is not null) { var fb = f.Fallbacks; fb.Add(Slim); f.Fallbacks = fb; }
        var t = new Theme();
        if (Sans is not null) t.DefaultFont = Sans;
        t.DefaultFontSize = 22;
        t.SetColor("font_color", "Label", Paper);
        t.SetColor("font_color", "Button", Paper);
        t.SetColor("font_hover_color", "Button", HoverText);
        t.SetColor("font_pressed_color", "Button", Amber);
        t.SetColor("font_disabled_color", "Button", new Color("8b8472"));
        // Plain buttons (REVAMP-SUMMARY §0): translucent black, amber hairline underneath on hover.
        StyleBoxFlat Plain(float bg, Color line, int lineW) { var b = new StyleBoxFlat { BgColor = new Color(0, 0, 0, bg), BorderColor = line, BorderWidthBottom = lineW, ContentMarginLeft = 18, ContentMarginRight = 18, ContentMarginTop = 8, ContentMarginBottom = 8 }; b.SetCornerRadiusAll(3); return b; }
        t.SetStylebox("normal", "Button", Plain(0.4f, new Color(0, 0, 0, 0), 0));
        t.SetStylebox("hover", "Button", Plain(0.7f, Amber, 2));
        t.SetStylebox("pressed", "Button", Plain(0.8f, Amber, 2));
        t.SetStylebox("disabled", "Button", Plain(0.22f, new Color(0, 0, 0, 0), 0));
        t.SetStylebox("focus", "Button", new StyleBoxEmpty());
        var panel = new StyleBoxFlat { BgColor = new Color(0.03f, 0.028f, 0.025f, 0.88f), BorderColor = new Color(Brass, 0.16f) }; panel.SetBorderWidthAll(1); panel.SetCornerRadiusAll(8); panel.SetContentMarginAll(14);
        t.SetStylebox("panel", "PanelContainer", panel);
        t.SetColor("font_color", "LineEdit", Paper);
        t.SetStylebox("normal", "LineEdit", Box(new Color(0.06f, 0.055f, 0.05f, 0.95f), BrassDim, 1, 5, 12, 8));
        t.SetStylebox("focus", "LineEdit", Box(new Color(0, 0, 0, 0), Brass, 2, 5));
        t.SetColor("font_color", "OptionButton", Paper);
        t.SetColor("font_color", "PopupMenu", Paper);
        t.SetStylebox("panel", "PopupMenu", Box(new Color(0.04f, 0.037f, 0.033f, 0.98f), new Color(Brass, 0.25f), 1, 6, 8, 8));
        // drop-down lists light up like the title screen's menu (Yunke 11-19): a glowing bar, dark text on it
        t.SetColor("font_hover_color", "PopupMenu", new Color(0.12f, 0.08f, 0.04f));
        t.SetStylebox("hover", "PopupMenu", Ui.GlowBar(10, 24, 4));
        t.SetStylebox("panel", "TooltipPanel", Box(new Color(0.04f, 0.037f, 0.033f, 0.96f), new Color(Brass, 0.25f), 1, 6, 12, 8));
        t.SetColor("font_color", "TooltipLabel", Paper);
        t.SetColor("font_color", "CheckBox", Paper);
        t.SetColor("font_color", "RichTextLabel", Paper);
        t.SetColor("default_color", "RichTextLabel", Paper);
        t.SetConstant("separation", "VBoxContainer", 8);
        t.SetConstant("separation", "HBoxContainer", 10);
        var grabber = Box(BrassDim, new Color(0, 0, 0, 0), 0, 4);
        t.SetStylebox("grabber", "VScrollBar", grabber);
        t.SetStylebox("grabber_highlight", "VScrollBar", Box(Brass, new Color(0, 0, 0, 0), 0, 4));
        t.SetStylebox("scroll", "VScrollBar", Box(new Color(0, 0, 0, 0.25f), new Color(0, 0, 0, 0), 0, 4));
        return t;
    }

    public static StyleBoxFlat Box(Color bg, Color border, int width = 1, int radius = 6, int padX = 14, int padY = 8)
    {
        var b = new StyleBoxFlat { BgColor = bg, BorderColor = border, CornerDetail = 6, AntiAliasing = true };
        b.SetBorderWidthAll(width); b.SetCornerRadiusAll(radius);
        b.ContentMarginLeft = b.ContentMarginRight = padX; b.ContentMarginTop = b.ContentMarginBottom = padY;
        b.ShadowColor = new Color(0, 0, 0, 0.35f); b.ShadowSize = 6;
        return b;
    }

    public static Label Text(string text, int size = 22, Color? color = null, Font? font = null)
    {
        var l = new Label { Text = text, MouseFilter = Control.MouseFilterEnum.Ignore };
        l.AddThemeFontSizeOverride("font_size", size);
        if (color is Color c) l.AddThemeColorOverride("font_color", c);
        if (font is not null) l.AddThemeFontOverride("font", font);
        return l;
    }
    // titles in ZCOOL XiaoWei, falling back to LXGW WenKai (Yunke 11-20 #26: the Song-style serif looked plain), a little spaced, with the soft shadow
    // ZCOOL XiaoWei's own 回 glyph is broken (its inner square has the outer contour's winding, so it fills solid — "往事■声",
    // Carcosa chapter three, 11-20; checked by rendering every character of the locale and card data: only 回 and 徊).
    // A title with one of them is set in the fallback face instead. 11-22 (淹灭之城 drawn as "淹火之城"): a scan of every
    // character in the card, locale and scenario data against Source Han Sans (glyph height / width / ink) found more —
    // 圄 fills solid too, and 亘 旦 灭 豆 lose a horizontal stroke (亘 and 旦 come out as 日, 灭 as 火).
    static readonly char[] DisplayBroken = { '回', '徊', '圄', '亘', '旦', '灭', '豆' };
    public static Label Title(string text, int size = 40) { var l = Text(text, size, Paper, (text.IndexOfAny(DisplayBroken) >= 0 ? null : Display) ?? Kai ?? Serif); l.AddThemeConstantOverride("outline_size", Math.Max(2, size / 14)); l.AddThemeColorOverride("font_outline_color", new Color(0, 0, 0, 0.35f)); l.AddThemeColorOverride("font_shadow_color", new Color(0, 0, 0, 0.8f)); l.AddThemeConstantOverride("shadow_offset_y", 2); l.AddThemeConstantOverride("shadow_outline_size", 6); return l; }
}
