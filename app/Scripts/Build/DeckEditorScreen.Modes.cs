using Godot;
using Gathering;
using System.Text.Json;

namespace ArkhamUI;

// How the card pool on the left is shown (Yunke 11-21: eight cards at a time is slow to look through; arkham.build's search
// list — words, icons and colour — as another way, switched with the card view): 大卡 (four to a row, two rows), 小卡 (eight
// to a row, three rows) and 文字 (two columns of rows in arkham.build's list-card form: the count, the cost, a round thumbnail
// of the art, the class icon, the name in the class colour over its type · slot · traits, the skill icons). The cards drag
// as before; the rows do not — a click on a row puts one copy in (a click on the deck list takes one out, as ever), a right
// click opens the card window, and resting on a row shows the printed card beside it. The choice is kept in settings.
public partial class DeckEditorScreen
{
    enum PoolMode { Large, Small, Text }
    PoolMode poolMode = (App.I.S?.Settings.DeckEditorPoolMode) switch { "small" => PoolMode.Small, "text" => PoolMode.Text, _ => PoolMode.Large };

    int Columns => poolMode switch { PoolMode.Small => 8, PoolMode.Text => 2, _ => 4 };
    int HGap => poolMode switch { PoolMode.Small => 20, PoolMode.Text => 16, _ => 34 };
    int VGap => poolMode switch { PoolMode.Small => 12, PoolMode.Text => 8, _ => 22 };
    int FirstRows => poolMode switch { PoolMode.Small => 4, PoolMode.Text => 16, _ => 4 };
    int MoreRows => poolMode switch { PoolMode.Text => 8, _ => 2 };
    const float RowHeight = 60;
    float rowWidth = 640;
    float ItemHeight => poolMode == PoolMode.Text ? RowHeight : CardSize.Y;

    HBoxContainer? modeBar;
    Label poolHint = null!;
    readonly Dictionary<string, Control> poolRows = new();

    // 大卡 · 小卡 · 文字, in the second row after the type pills
    Control ModeSwitch()
    {
        modeBar = new HBoxContainer(); modeBar.AddThemeConstantOverride("separation", 4);
        var box = new PanelContainer(); box.AddThemeStyleboxOverride("panel", Ui.SoftBox(0.3f, false, 4, 0)); box.AddChild(modeBar);
        var outer = new MarginContainer(); outer.AddThemeConstantOverride("margin_left", 18); outer.AddChild(box);
        RefreshModeBar();
        // QA: switch the mode (large / small / text); click the i-th text row; print where the rows are
        QaScript.Register("ed-mode", a => SetPoolMode(a switch { "small" => PoolMode.Small, "text" => PoolMode.Text, _ => PoolMode.Large }));
        QaScript.Register("ed-row-click", a => { if (int.TryParse(a, out int i) && i >= 0 && i < library.GetChildCount() && library.GetChild(i) is Button rb) rb.EmitSignal(BaseButton.SignalName.Pressed); });
        QaScript.Register("ed-row-hover", a => { if (int.TryParse(a, out int i) && i >= 0 && i < library.GetChildCount() && library.GetChild(i) is Button rb) rb.EmitSignal(Control.SignalName.MouseEntered); });
        return outer;
    }

    void RefreshModeBar()
    {
        if (modeBar is null) return;
        Ui.Clear(modeBar);
        foreach (var (m, name, tip) in new[] { (PoolMode.Large, "大卡", "大卡：一行四张"), (PoolMode.Small, "小卡", "小卡：我们重绘的小卡，一行八张"), (PoolMode.Text, "文字", "文字列表：名称、图标和颜色") })
        {
            var mode = m; var b = Pill(name, poolMode == m, () => SetPoolMode(mode), 19);
            b.TooltipText = tip; b.CustomMinimumSize = new Vector2(64, 42); modeBar.AddChild(b);
        }
    }

    void SetPoolMode(PoolMode m)
    {
        if (m == poolMode) { RefreshModeBar(); return; }
        poolMode = m; HidePreview(); warm.Clear();   // another mode shows other pictures (DeckEditorScreen.Pool.cs)
        if (App.I.S is { } s) { s.Settings.DeckEditorPoolMode = m switch { PoolMode.Small => "small", PoolMode.Text => "text", _ => "large" }; s.Settings.Save(); }
        RefreshModeBar(); ApplyPoolLayout();
        FitPool(); FillLibrary(true);
        if (App.I.QaRunning) GD.Print("QA_POOL_MODE " + m + " card=" + CardSize + " row=" + rowWidth);
    }

    // the grid's columns and gaps and the hint under it, for the mode
    void ApplyPoolLayout()
    {
        library.Columns = Columns;
        library.AddThemeConstantOverride("h_separation", HGap); library.AddThemeConstantOverride("v_separation", VGap);
        if (poolHint is not null) poolHint.Text = poolMode == PoolMode.Text
            ? "滚轮翻看 · 单击加入一张 · 右侧单击移出一张 · 悬停看原卡 · 右键：张数、升级、注释"
            : "滚轮翻看 · 单击或拖入加入 · 右侧单击或拖出移出一张 · 「升级」换高级版 · 右键：张数、升级、注释";
    }

    // the card size (or row width) from the book's size; true when it changed
    bool FitPool()
    {
        float w = libScroll.Size.X - 26, h = libScroll.Size.Y;   // the bar
        if (poolMode == PoolMode.Text)
        {
            float rw = MathF.Floor(Math.Max(420, (w - HGap) / 2));
            if (Math.Abs(rw - rowWidth) < 2) return false;
            rowWidth = rw; return true;
        }
        float cw;
        if (poolMode == PoolMode.Small)
        {
            // eight to a row, three whole rows (a card, the gap above its plaque and the plaque, about 30 px)
            cw = MathF.Floor(Math.Clamp((w - (Columns - 1) * HGap) / Columns, 110, 210));
            if (h > 0) cw = MathF.Floor(Math.Max(100, Math.Min(cw, (h - 14 - 18 - 2 * VGap - 3 * (6 + 30)) / 3 * CardAspect)));
        }
        else
        {
            cw = MathF.Floor(Math.Clamp((w - (Columns - 1) * HGap) / Columns, 200, 330));
            // two whole rows must fit the height (Yunke 11-19: the second row was cut off): each row is a card, the gap above its
            // count plaque and the plaque (about 34 px), the rows apart by VGap, the grid padded 14 above and 18 below
            if (h > 0) cw = MathF.Floor(Math.Max(150, Math.Min(cw, (h - 14 - 18 - VGap - 2 * (6 + 34)) / 2 * CardAspect)));
        }
        var size = new Vector2(cw, MathF.Round(cw / CardAspect));
        if (size.DistanceTo(CardSize) < 2) return false;
        CardSize = size; return true;
    }

    static Color ClassColour(JsonElement c) => J.String(c, "faction2_code").Length > 0 ? new Color("d8b45a")   // several classes: gold, as build's multiclass
        : NameColour(J.String(c, "faction_code"));
    static Color NameColour(string f)
    {
        return f switch
        {
            "guardian" => new Color("6f9fe0"), "seeker" => new Color("e0a560"), "rogue" => new Color("5fc187"), "mystic" => new Color("a888dc"),
            "survivor" => new Color("e07878"), _ => Style.Paper,
        };
    }

    static string TypeName(string t) => t switch { "asset" => "支援", "event" => "事件", "skill" => "技能", "treachery" => "诡计", "enemy" => "敌人", "investigator" => "调查员", _ => "" };

    // one row of the text list; its thumbnail is made only near the view, like the cards (DeckEditorScreen.Pool.cs)
    void AppendRow(string code, JsonElement c)
    {
        var b = new Button { FocusMode = FocusModeEnum.None, CustomMinimumSize = new Vector2(rowWidth, RowHeight), MouseDefaultCursorShape = CursorShape.PointingHand, ClipContents = true };
        var colour = ClassColour(c);
        StyleBoxFlat Box(bool lit)
        {
            var s = new StyleBoxFlat { BgColor = lit ? new Color(0.2f, 0.15f, 0.07f, 0.95f) : new Color(0.07f, 0.06f, 0.05f, 0.88f), AntiAliasing = true, ShadowColor = lit ? new Color(Style.Amber, 0.3f) : new Color(0, 0, 0, 0.45f), ShadowSize = lit ? 8 : 3 };
            s.BorderColor = new Color(colour, 0.9f); s.BorderWidthLeft = 4; s.SetCornerRadiusAll(8); return s;
        }
        b.AddThemeStyleboxOverride("normal", Box(false)); b.AddThemeStyleboxOverride("hover", Box(true)); b.AddThemeStyleboxOverride("pressed", Box(true)); b.AddThemeStyleboxOverride("focus", new StyleBoxEmpty());
        var row = new HBoxContainer { MouseFilter = MouseFilterEnum.Ignore }; row.AddThemeConstantOverride("separation", 10);
        b.AddChild(row); row.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect); row.OffsetLeft = 10; row.OffsetRight = -10;
        // the count in the deck ("1 / 2"), as under the cards
        var plaque = new PanelContainer { CustomMinimumSize = new Vector2(64, 0), SizeFlagsVertical = SizeFlags.ShrinkCenter, MouseFilter = MouseFilterEnum.Ignore };
        var n = Style.Text("", 17, Style.PaperDim, Style.Sans); n.HorizontalAlignment = HorizontalAlignment.Center; plaque.AddChild(n);
        row.AddChild(plaque);
        // the cost disc in the class colour
        var disc = new Panel { CustomMinimumSize = new Vector2(36, 36), SizeFlagsVertical = SizeFlags.ShrinkCenter, MouseFilter = MouseFilterEnum.Ignore };
        var ds = new StyleBoxFlat { BgColor = SelectionStage.FactionColor(code).Darkened(0.15f), BorderColor = new Color(0.9f, 0.82f, 0.62f, 0.85f), AntiAliasing = true }; ds.SetBorderWidthAll(2); ds.SetCornerRadiusAll(18);
        disc.AddThemeStyleboxOverride("panel", ds);
        int cost = Cost(c);
        var cl = Style.Text(cost < 0 ? "–" : cost.ToString(), 20, Colors.White, Style.Symbols); cl.HorizontalAlignment = HorizontalAlignment.Center; cl.VerticalAlignment = VerticalAlignment.Center;
        cl.AddThemeConstantOverride("outline_size", 5); cl.AddThemeColorOverride("font_outline_color", Colors.Black); disc.AddChild(cl); Ui.CenterDigits(cl); row.AddChild(disc);
        // the round thumbnail's place
        // (a panel that draws only its children inside its round box: the thumbnail cut to a circle)
        var thumb = new Panel { CustomMinimumSize = new Vector2(46, 46), SizeFlagsVertical = SizeFlags.ShrinkCenter, MouseFilter = MouseFilterEnum.Ignore, ClipChildren = ClipChildrenMode.Only };
        var round = new StyleBoxFlat { BgColor = Colors.White, AntiAliasing = true }; round.SetCornerRadiusAll(23); thumb.AddThemeStyleboxOverride("panel", round);
        row.AddChild(thumb); slots.Add((code, c, thumb));
        // the class icon(s)
        foreach (var f in new[] { J.String(c, "faction_code"), J.String(c, "faction2_code"), J.String(c, "faction3_code") })
            if (f.Length > 0 && Style.Glyph.TryGetValue(f, out var g) && Style.Symbols is not null)
            { var icon = Style.Text(g, 24, NameColour(f), Style.Symbols); icon.VerticalAlignment = VerticalAlignment.Center; row.AddChild(icon); }
        // the name in the class colour, level dots; under it type · slot · traits
        var words = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill, SizeFlagsVertical = SizeFlags.ShrinkCenter, MouseFilter = MouseFilterEnum.Ignore }; words.AddThemeConstantOverride("separation", -2);
        int xp = Xp(c);
        var name = Style.Text(J.String(c, "name", code) + (xp > 0 ? " " + new string('•', Math.Min(5, xp)) : ""), 20, colour, Style.Kai);
        name.ClipText = true; name.AddThemeConstantOverride("outline_size", 4); name.AddThemeColorOverride("font_outline_color", new Color(0, 0, 0, 0.85f)); words.AddChild(name);
        string detail = string.Join(" · ", new[] { TypeName(J.String(c, "type_code")), SlotName(J.String(c, "slot")), J.String(c, "traits") }.Where(s => s.Length > 0));
        var sub = Style.Text(detail, 15, new Color(Style.PaperDim, 0.85f), Style.Kai); sub.ClipText = true; words.AddChild(sub);
        row.AddChild(words);
        if (RowSkillIcons(c) is { } icons) row.AddChild(icons);
        b.Pressed += () =>
        {
            Sfx.Click();
            // the card flies to the deck from the thumbnail, at card proportions
            var at = thumb.GetGlobalRect().GetCenter(); var size = new Vector2(96, 134);
            _ = Add(code, new Rect2(at - size / 2, size));
        };
        b.GuiInput += e => { if (e is InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Right }) { b.AcceptEvent(); LibraryCardDialog(code); } };
        b.MouseEntered += () => { if (dragCode is null) ShowRowPreview(code, b); };
        b.MouseExited += HidePreview;
        poolRows[code] = b;
        Plaque(code, c, n, plaque);
        plaques[code] = (n, plaque, c);
        library.AddChild(b);
    }

    // 小卡 (Yunke 11-21: "直接显示我们做的小卡版本的卡"): the workshop's small card — the table's redrawn card with its skill
    // icons, the art cut around its subject when there is no redrawn picture (as DeckPreview's 小卡 view), level pips at its
    // top right; it drags like the printed card
    CardView SmallPoolCard(string code, JsonElement card)
    {
        string type = J.String(card, "type_code");
        // 随机基础弱点 (01000) is a card back: shown whole, as the printed card (Yunke 11-21)
        if (code == RandomWeakness) return new CardView { Code = code, CustomMinimumSize = CardSize, Size = CardSize, Actionable = false, Draggable = true, HoverZoom = false, ZoomHeight = 640, Redrawn = false, ShowCost = false };
        var pic = Ui.Art(CoreCatalog.Normalize(code)) is null ? DeckPreview.ArtFor(code, type, CardSize.X / CardSize.Y) : null;
        return new CardView { Code = code, CustomMinimumSize = CardSize, Size = CardSize, Actionable = false, Draggable = true, HoverZoom = false, ZoomHeight = 640, ShowSkills = true, Picture = pic };
    }
    void SmallPips(CardView cv, JsonElement card)
    {
        int xp = Xp(card); if (xp <= 0) return;
        var pips = Style.Text(new string('•', Math.Min(5, xp)), 24, new Color("f0d9a0"), Style.Symbols); pips.MouseFilter = MouseFilterEnum.Ignore;
        pips.AddThemeConstantOverride("outline_size", 5); pips.AddThemeColorOverride("font_outline_color", Colors.Black);
        pips.HorizontalAlignment = HorizontalAlignment.Right; cv.AddChild(pips); pips.Position = new Vector2(CardSize.X - 50, 2); pips.Size = new Vector2(46, 26);
    }

    // the slot in the cards' own Chinese (a few cards carry only the English one: Hand, Arcane …)
    static readonly Dictionary<string, string> SlotWords = new() { ["Hand"] = "手部", ["Ally"] = "盟友", ["Accessory"] = "饰品", ["Arcane"] = "法术", ["Body"] = "身体", ["Tarot"] = "塔罗", ["Head"] = "头部" };
    static string SlotName(string s) => System.Text.RegularExpressions.Regex.Replace(s, "[A-Za-z]+", m => SlotWords.TryGetValue(m.Value, out var w) ? w : m.Value);

    // the skill icons, larger than the deck list's (build's list shows them at the name's size)
    static Control? RowSkillIcons(JsonElement card)
    {
        if (Style.Symbols is null) return null;
        var row = new HBoxContainer { MouseFilter = MouseFilterEnum.Ignore, SizeFlagsVertical = SizeFlags.ShrinkCenter }; row.AddThemeConstantOverride("separation", 1);
        foreach (var (field, key, colour) in new[] { ("skill_willpower", "willpower", Style.Willpower), ("skill_intellect", "intellect", Style.Intellect), ("skill_combat", "combat", Style.Combat), ("skill_agility", "agility", Style.Agility), ("skill_wild", "wild", Style.Paper) })
            for (int i = 0; i < Math.Min(4, J.Int(card, field)); i++)
            {
                var l = Style.Text(Style.Glyph[key], 25, colour, Style.Symbols); l.AddThemeConstantOverride("outline_size", 4); l.AddThemeColorOverride("font_outline_color", Colors.Black); row.AddChild(l);
            }
        return row.GetChildCount() > 0 ? row : null;
    }

    // a row's thumbnail: the art around its subject (the panel it goes in cuts it to a circle)
    void RealizeThumb(string code, JsonElement card, Control slot)
    {
        var tex = DeckPreview.ArtFor(code, J.String(card, "type_code"), 1f);
        if (tex is null) return;
        var tr = new TextureRect { Texture = tex, ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize, StretchMode = TextureRect.StretchModeEnum.KeepAspectCovered, MouseFilter = MouseFilterEnum.Ignore };
        slot.AddChild(tr); tr.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
    }

    // the printed card beside the row (to the right of a left-column row, to the left of a right-column one)
    void ShowRowPreview(string code, Control rowCtl)
    {
        HidePreview(); if (Covered) return;
        var size = new Vector2(286, 400);
        preview = new CardView { Code = code, CustomMinimumSize = size, Size = size, HoverZoom = false, Redrawn = false, MouseFilter = MouseFilterEnum.Ignore, ZIndex = 150, Override = SharpCard(code, size) };
        AddChild(preview); RoundCorners(preview); Crisp(preview);
        var r = rowCtl.GetGlobalRect(); var book = libraryPanel.GetGlobalRect(); float vpH = GetViewportRect().Size.Y;
        bool left = r.GetCenter().X < book.GetCenter().X;
        float x = left ? r.End.X + 14 : r.Position.X - size.X - 14;
        preview.GlobalPosition = new Vector2(x, Math.Clamp(r.GetCenter().Y - size.Y / 2, 20, vpH - size.Y - 20));
        if (!Calm) { preview.Modulate = new Color(1, 1, 1, 0); preview.CreateTween().TweenProperty(preview, "modulate:a", 1f, 0.12f); }
    }
}
