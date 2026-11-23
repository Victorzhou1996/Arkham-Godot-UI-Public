using Godot;
using Gathering;
using System.Text.RegularExpressions;

namespace ArkhamUI;

// Large, readable card view: the redrawn card on the left (the printed scan only in the deck workshop,
// printed: true), crisp typeset Chinese rules on the right.
public static class CardReader
{
    // The places on the table not yet revealed, by card code (the table keeps it current each render). Every look at one of
    // them — right-click, hover, a log link — shows only its unrevealed side: its back picture, back_name and back_text, and
    // no "看正面" (local-rclick-location: right-clicking a "潮水通道" opened 群鱼坟场's revealed side with its full text). As the
    // html version, which only ever shows an unrevealed location's back image.
    public static readonly HashSet<string> Unrevealed = new();
    public static bool IsUnrevealed(string code) => Unrevealed.Contains(CoreCatalog.Normalize(code));
    // the name a card is called by on the table now: an unrevealed place by its unrevealed side
    public static string ShownName(string code)
    {
        var S = App.I.S!; code = CoreCatalog.Normalize(code);
        return IsUnrevealed(code) && J.String(S.Card(code), "back_name") is { Length: > 0 } back ? back : S.Name(code);
    }
    // the first printing that shares this place's name (versions of one place share an unrevealed side, and only the first may
    // carry its scan: the second 南区 01127)
    public static string FirstPrinting(string code)
    {
        var S = App.I.S!; code = CoreCatalog.Normalize(code);
        string name = J.String(S.Card(code), "name"), first = code;
        if (name.Length > 0 && code.Length == 5 && int.TryParse(code, out int n))
            for (int k = n - 1; k > 0 && J.String(S.Card(k.ToString("D5")), "name") == name && J.String(S.Card(k.ToString("D5")), "type_code") == "location"; k--) first = k.ToString("D5");
        return first;
    }
    // The picture of a place's unrevealed side for a redrawn card: its own unrevealed art; for versions of one place that share
    // a name (the second 南区), the first printing's art (Yunke 11-20); then the printed back. Never this card's own revealed art
    // when its two sides have different names (百赌百胜's 走廊后门 hides 画廊 / VIP区 / 后巷 — QQ #47).
    public static Texture2D? UnrevealedArt(string code)
    {
        var S = App.I.S!; code = CoreCatalog.Normalize(code); var card = S.Card(code);
        string first = FirstPrinting(code), back = J.String(card, "back_name");
        bool sameName = back.Length == 0 || back == J.String(card, "name");
        return Ui.Art(code + "b") ?? (sameName ? Ui.Art(first) : null) ?? Ui.Art(first + "b") ?? UnrevealedPicture(code);
    }
    // the printed unrevealed side, never the revealed one; null when there is no scan of it
    public static Texture2D? UnrevealedPicture(string code)
    {
        code = CoreCatalog.Normalize(code);
        return Ui.CardExact(code + "b") ?? Ui.CardExact(FirstPrinting(code) + "b");
    }

    // unrevealed: what the caller knows of this very card (a place's own tile: two copies of one place can lie on the table,
    // one revealed); left out, the table's set decides
    public static void Open(string code, string? extraTitle = null, IEnumerable<(string Label, Action Act)>? actions = null, bool printed = false, bool? unrevealed = null)
    {
        var S = App.I.S!; code = CoreCatalog.Normalize(code);
        var card = S.Card(code);
        if ((unrevealed ?? IsUnrevealed(code)) && J.String(card, "type_code") == "location") { OpenUnrevealed(code, card, extraTitle, actions); return; }
        bool landscape = J.String(card, "type_code") is "investigator" or "act" or "agenda";
        var row = new HBoxContainer(); row.AddThemeConstantOverride("separation", 34);
        Control scrim = null!;
        var faces = new VBoxContainer(); row.AddChild(faces);
        void Face(string c)
        {
            Ui.Clear(faces);
            // the printed card, framed like every large card (Peek.FramedCard)
            if (printed) { faces.AddChild(Peek.FramedCard(Ui.Card(c), landscape ? new Vector2(860, 616) : new Vector2(600, 860))); return; }
            // an agenda or act is redrawn from its own picture (the left / right part of the printed card), not the whole printed card
            string type = J.String(App.I.S!.Card(c), "type_code");
            var pic = Ui.Art(c) is null && type is "agenda" or "act" ? Art.Crop(c, type) : null;
            var cv = new CardView { Code = Ui.Art(c) is not null ? c : code, Picture = pic, CustomMinimumSize = new Vector2(440, 616), Size = new Vector2(440, 616), HoverZoom = false };
            faces.AddChild(cv);
        }
        Face(code);
        bool hasBack = J.Get(card, "double_sided").ValueKind == System.Text.Json.JsonValueKind.True && Ui.Card(code + "b") is not null;
        bool showingBack = false;
        var text = new VBoxContainer { CustomMinimumSize = new Vector2(560, 0) }; text.AddThemeConstantOverride("separation", 14);
        row.AddChild(text);
        void Fill()
        {
            Ui.Clear(text);
            string name = CoreCatalog.Runes(showingBack && J.String(card, "back_name").Length > 0 ? J.String(card, "back_name") : J.String(card, "name", code));
            text.AddChild(Style.Title(name, 40));
            var meta = new List<string>();
            if (J.String(card, "subname").Length > 0) meta.Add(J.String(card, "subname"));
            if (J.String(card, "type_name").Length > 0) meta.Add(J.String(card, "type_name"));
            if (J.String(card, "faction_name").Length > 0) meta.Add(J.String(card, "faction_name"));
            if (J.Get(card, "xp").ValueKind == System.Text.Json.JsonValueKind.Number && J.Int(card, "xp") > 0) meta.Add("等级 " + J.Int(card, "xp"));
            text.AddChild(Style.Text(string.Join(" · ", meta), 22, Style.Brass, Style.Kai));
            if (J.String(card, "traits").Length > 0) text.AddChild(Style.Text(J.String(card, "traits"), 22, Style.PaperDim, Style.Serif));
            text.AddChild(Rules(showingBack ? J.String(card, "back_text") : J.String(card, "text"), 25));
            string flavor = showingBack ? J.String(card, "back_flavor") : J.String(card, "flavor");
            if (flavor.Length > 0) { var f = Rules(flavor, 21); f.AddThemeColorOverride("default_color", Style.PaperDim); text.AddChild(f); }
            if (extraTitle is not null) text.AddChild(Style.Text(extraTitle, 21, Style.Amber));
            text.AddChild(Ui.Spacer(0, 10, true));
            var buttons = new HFlowContainer(); buttons.AddThemeConstantOverride("h_separation", 12); text.AddChild(buttons);
            if (actions is not null) foreach (var (label, act) in actions) buttons.AddChild(Ui.Button(label, () => { App.I.CloseModal(scrim); act(); }, 24, true));
            if (hasBack) buttons.AddChild(Ui.Button(showingBack ? "看正面" : "看背面", () => { showingBack = !showingBack; Face(showingBack ? code + "b" : code); Fill(); }, 22));
            buttons.AddChild(Ui.Button("关闭", () => App.I.CloseModal(scrim), 22));
        }
        Fill();
        scrim = App.I.Modal(Ui.Panel(row, 34), true, 0.72f);
    }

    // An unrevealed place: its unrevealed side only — the back's picture, name, text and flavour; no switch to the other side
    static void OpenUnrevealed(string code, System.Text.Json.JsonElement card, string? extraTitle, IEnumerable<(string Label, Action Act)>? actions)
    {
        var row = new HBoxContainer(); row.AddThemeConstantOverride("separation", 34);
        Control scrim = null!;
        var tex = UnrevealedPicture(code) ?? UnrevealedArt(code);
        if (tex is not null) row.AddChild(Peek.FramedCard(tex, new Vector2(600, 860)));
        var text = new VBoxContainer { CustomMinimumSize = new Vector2(560, 0) }; text.AddThemeConstantOverride("separation", 14); row.AddChild(text);
        text.AddChild(Style.Title(ShownName(code), 40));
        var meta = new List<string> { "未揭示" }; if (J.String(card, "type_name").Length > 0) meta.Add(J.String(card, "type_name"));
        text.AddChild(Style.Text(string.Join(" · ", meta), 22, Style.Brass, Style.Kai));
        if (J.String(card, "back_text") is { Length: > 0 } bt) text.AddChild(Rules(bt, 25));
        if (J.String(card, "back_flavor") is { Length: > 0 } bf) { var f = Rules(bf, 21); f.AddThemeColorOverride("default_color", Style.PaperDim); text.AddChild(f); }
        if (extraTitle is not null) text.AddChild(Style.Text(extraTitle, 21, Style.Amber));
        text.AddChild(Ui.Spacer(0, 10, true));
        var buttons = new HFlowContainer(); buttons.AddThemeConstantOverride("h_separation", 12); text.AddChild(buttons);
        if (actions is not null) foreach (var (label, act) in actions) buttons.AddChild(Ui.Button(label, () => { App.I.CloseModal(scrim); act(); }, 24, true));
        buttons.AddChild(Ui.Button("关闭", () => App.I.CloseModal(scrim), 22));
        if (App.I.QaRunning) GD.Print("QA_READER_UNREVEALED " + code + " " + ShownName(code) + " pic=" + (tex is not null));
        scrim = App.I.Modal(Ui.Panel(row, 34), true, 0.72f);
    }

    static readonly Dictionary<string, (string Glyph, bool Chaos)> Icons = new()
    {
        ["action"] = ("I", false), ["fast"] = ("u", false), ["free"] = ("u", false), ["reaction"] = ("y", false),
        ["willpower"] = ("A", false), ["intellect"] = ("F", false), ["combat"] = ("D", false), ["agility"] = ("S", false), ["wild"] = ("G", false),
        ["elder_sign"] = ("X", false), ["auto_fail"] = ("Z", false), ["guardian"] = ("Q", false), ["seeker"] = ("E", false), ["rogue"] = ("T", false), ["mystic"] = ("W", false), ["survivor"] = ("R", false),
        ["skull"] = ("", true), ["cultist"] = ("", true), ["tablet"] = ("", true), ["elder_thing"] = ("", true),
        // bless and curse tokens (Yunke 11-22: "[curse]" showed as text): ArkhamIcons U+E92A / U+E929, as html icons.css .bless-icon / .curse-icon
        ["bless"] = ("", true), ["curse"] = ("", true),
        // the "per investigator" icon, as the html version draws it (ArkhamIcons U+E915, icons.css .per-player)
        ["per_investigator"] = ("", true),
    };

    // Renders card text with [icon] glyphs and <b>/<i> emphasis, without BBCode injection.
    static FontVariation? Slanted, BoldSerif;
    // A font clamped to the body font's line height at this size: an inline icon or a bold word no longer makes its line
    // taller than the others (Yunke 11-19 r9 #15: uneven line spacing in the hover boxes).
    static readonly Dictionary<(Font, int), FontVariation> evenFonts = new();
    static Font Even(Font f, int size)
    {
        var body = Style.Serif; if (body is null || f == body) return f;
        if (evenFonts.TryGetValue((f, size), out var v)) return v;
        v = new FontVariation { BaseFont = f };
        float da = f.GetAscent(size) - body.GetAscent(size), dd = f.GetDescent(size) - body.GetDescent(size);
        if (da > 0) v.SpacingTop = -(int)Math.Ceiling(da);
        if (dd > 0) v.SpacingBottom = -(int)Math.Ceiling(dd);
        evenFonts[(f, size)] = v; return v;
    }
    public static RichTextLabel Rules(string raw, int size)
    {
        // card markup: [[Trait]] is a trait name (bold); [per_investigator] is drawn as its icon (see Icons)
        // a Drowned City rune ([tdc_rune_n]) becomes its glyph first: the _x_ emphasis below took "_rune_" out of the token
        // ("古代秘库[tdcrunen]", local-rune-icon); the text font falls back to ArkhamSlim for it (Style)
        raw = CoreCatalog.Runes(raw ?? "");
        raw = Regex.Replace(raw, @"\[\[([^\]]+)\]\]", "<b>$1</b>");
        // story text emphasis, as the html version does it (helpers.ts formatContent): _x_ is bold, *x* is italic
        // (brackets excluded so icon tokens such as [elder_sign] … [auto_fail] are never paired up)
        raw = Regex.Replace(raw, @"_([^_\n\[\]]*)_", "<b>$1</b>");
        raw = Regex.Replace(raw, @"\*([^*\n\[\]]*)\*", "<i>$1</i>");
        var r = new RichTextLabel { FitContent = true, ScrollActive = false, AutowrapMode = TextServer.AutowrapMode.WordSmart, MouseFilter = Control.MouseFilterEnum.Ignore, SelectionEnabled = false };
        r.AddThemeFontSizeOverride("normal_font_size", size); r.AddThemeFontSizeOverride("bold_font_size", size); r.AddThemeFontSizeOverride("italics_font_size", size);
        if (Style.Serif is not null) r.AddThemeFontOverride("normal_font", Style.Serif);
        // bold: the body font made heavier, so a bold word keeps the line's height (a second font made its line taller)
        if (Style.Serif is not null) r.AddThemeFontOverride("bold_font", BoldSerif ??= new FontVariation { BaseFont = Style.Serif, VariationEmbolden = 0.9f });
        else if (Style.Sans is not null) r.AddThemeFontOverride("bold_font", Style.Sans);
        if (Style.Serif is not null) r.AddThemeFontOverride("italics_font", Slanted ??= new FontVariation { BaseFont = Style.Serif, VariationTransform = new Transform2D(new Vector2(1, 0), new Vector2(0.2f, 1), Vector2.Zero) });
        r.AddThemeConstantOverride("line_separation", 8);
        r.AddThemeColorOverride("default_color", Style.Paper);
        foreach (Match m in Regex.Matches(raw ?? "", @"\[(\w+)\]|<b>|</b>|<i>|</i>|<[^>]+>|[^\[<]+|[\[<]"))
        {
            var v = m.Value;
            if (m.Groups[1].Success && Icons.TryGetValue(m.Groups[1].Value, out var ic))
            {
                var font = ic.Chaos ? Style.Icons : Style.Symbols;
                if (font is not null) { r.PushFont(Even(font, size), size); r.PushColor(Style.Brass); r.AddText(ic.Glyph); r.Pop(); r.Pop(); }
                else r.AddText(v);
            }
            else if (v == "<b>") { r.PushBold(); }
            else if (v == "</b>" || v == "</i>") { r.Pop(); }
            else if (v == "<i>") { r.PushItalics(); }
            // a story or setup line the rules core marks as not applying (html greys it)
            else if (v == "<dim>") { r.PushColor(new Color(Style.PaperDim, 0.45f)); }
            else if (v == "</dim>") { r.Pop(); }
            else if (v.StartsWith("<") && v.Length > 1) { }
            else r.AddText(v);
        }
        return r;
    }
}
