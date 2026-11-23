using Godot;
using Gathering;
using System.Text.Json;

namespace ArkhamUI;

// The editor's search and filter controls (layout from Yunke's reference, r9): round buttons, the 调查员卡 / 弱点卡
// toggles, the type pills, the class icons, and the small panels that 筛选 / 排序 / "…" / 禁卡表 open. Everything here
// only narrows or orders build's access list; the rules stay build's.
public partial class DeckEditorScreen
{
    // ───────── which cards the book shows
    static readonly System.Globalization.CompareInfo Zh = System.Globalization.CultureInfo.GetCultureInfo("zh-CN").CompareInfo;
    static readonly Comparer<string> ZhOrder = Comparer<string>.Create((a, b) => Zh.Compare(a, b));
    static readonly string[] ClassOrder = { "guardian", "seeker", "rogue", "mystic", "survivor", "neutral" };
    static readonly string[] TypeOrder = { "asset", "event", "skill" };
    static readonly (string Key, string Name)[] Sorts = { ("cost", "费用"), ("name", "名称"), ("level", "等级"), ("class", "职业"), ("type", "类型") };
    static bool IsWeakness(JsonElement c) => J.String(c, "access") == "weakness" || J.String(c, "subtype_code").Length > 0;
    static int Xp(JsonElement c) => J.Get(c, "xp").ValueKind == JsonValueKind.Number ? J.Int(c, "xp") : 0;

    bool Matches(JsonElement c)
    {
        if (IsWeakness(c) != onlyWeakness) return false;
        if (onlyInvestigator && J.Get(J.Get(c, "restrictions"), "investigator").ValueKind != JsonValueKind.Object) return false;
        if (factions.Count > 0)
        {
            var own = new[] { J.String(c, "faction_code"), J.String(c, "faction2_code"), J.String(c, "faction3_code") }.Where(f => f.Length > 0).ToArray();
            if (!own.Any(factions.Contains) && !(own.Length > 1 && factions.Contains("multi"))) return false;
        }
        if (type != "" && J.String(c, "type_code") != type) return false;
        if (packFilter.Count > 0 && !packFilter.Contains(J.String(c, "pack_code"))) return false;
        if (levels.Count > 0 && !levels.Contains(Math.Clamp(Xp(c), 0, 5))) return false;
        if (costFilter >= 0 && (costFilter >= 6 ? Cost(c) < 6 : Cost(c) != costFilter)) return false;
        if (search.Length > 0)
        {
            string hay = (searchName ? J.String(c, "name") + "|" + J.String(c, "real_name") + "|" + J.String(c, "subname") : "")
                + (searchTraits ? "|" + J.String(c, "traits") : "") + (searchText ? "|" + J.String(c, "text") : "");
            if (!hay.Contains(search, StringComparison.OrdinalIgnoreCase)) return false;
        }
        return true;
    }

    JsonElement[] Filtered()
    {
        var list = pool.Where(Matches);
        IOrderedEnumerable<JsonElement> ordered = sort switch
        {
            "name" => list.OrderBy(c => J.String(c, "name"), ZhOrder),
            "level" => list.OrderBy(Xp).ThenBy(SortCost),
            "class" => list.OrderBy(c => Array.IndexOf(ClassOrder, J.String(c, "faction_code")) is int i && i >= 0 ? i : 9).ThenBy(SortCost),
            "type" => list.OrderBy(c => Array.IndexOf(TypeOrder, J.String(c, "type_code")) is int i && i >= 0 ? i : 9).ThenBy(SortCost),
            _ => list.OrderBy(SortCost).ThenBy(Xp),
        };
        return ordered.ThenBy(c => J.String(c, "name"), ZhOrder).ToArray();
    }

    void Refilter() { RefreshFilterBar(); FillLibrary(true); }

    // the type pills, the class icons, the 筛选 / 排序 labels as the filters stand now
    void RefreshFilterBar()
    {
        Ui.Clear(typeTabs);
        foreach (var (key, name) in new[] { ("", "全部"), ("asset", "支援"), ("event", "事件"), ("skill", "技能") })
        {
            var k = key; var b = Pill(name, type == key, () => { type = k; Refilter(); });
            typeTabs.AddChild(b);
        }
        foreach (var (key, b) in classButtons) b.SetPressedNoSignal(factions.Contains(key));
        int active = (levels.Count > 0 ? 1 : 0) + (costFilter >= 0 ? 1 : 0) + (packFilter.Count > 0 ? 1 : 0);
        filterButton.Text = active > 0 ? "筛选 · " + active : "筛选";
        sortButton.Text = "排序 · " + Sorts.First(x => x.Key == sort).Name;
    }

    // ───────── small parts
    // a round button (the back arrow, "…", the pager)
    Button Round(string text, Action onPress, string tip, int font = 26, int size = 54)
    {
        var b = new Button { Text = text, TooltipText = tip, FocusMode = FocusModeEnum.None, CustomMinimumSize = new Vector2(size, size), MouseDefaultCursorShape = CursorShape.PointingHand };
        b.AddThemeFontSizeOverride("font_size", font);
        foreach (var (state, lit, bg) in new[] { ("normal", false, 0.45f), ("hover", true, 0.65f), ("pressed", true, 0.75f), ("disabled", false, 0.15f) })
        { var sb = Ui.SoftBox(bg, lit, 0, 0); sb.SetCornerRadiusAll(size / 2); b.AddThemeStyleboxOverride(state, sb); }
        b.AddThemeStyleboxOverride("focus", new StyleBoxEmpty());
        b.AddThemeColorOverride("font_color", Style.HoverText); b.AddThemeColorOverride("font_disabled_color", new Color(Style.PaperDim, 0.25f));
        b.Pressed += () => { Sfx.Click(); onPress(); };
        return b;
    }

    // a pill that is filled (lit) when chosen
    Button Pill(string text, bool on, Action onPress, int font = 20)
    {
        var b = new Button { Text = text, ToggleMode = true, ButtonPressed = on, FocusMode = FocusModeEnum.None, CustomMinimumSize = new Vector2(0, 46), MouseDefaultCursorShape = CursorShape.PointingHand };
        b.AddThemeFontSizeOverride("font_size", font); if (Style.Kai is not null) b.AddThemeFontOverride("font", Style.Kai);
        StyleBoxFlat Box(bool lit, float bg) { var sb = Ui.SoftBox(bg, lit, 20, 4); sb.SetCornerRadiusAll(23); return sb; }
        b.AddThemeStyleboxOverride("normal", Box(false, 0.35f)); b.AddThemeStyleboxOverride("hover", Box(true, 0.5f));
        b.AddThemeStyleboxOverride("pressed", Box(true, 0.85f)); b.AddThemeStyleboxOverride("hover_pressed", Box(true, 0.9f)); b.AddThemeStyleboxOverride("focus", new StyleBoxEmpty());
        b.AddThemeColorOverride("font_color", Style.Paper); b.AddThemeColorOverride("font_pressed_color", Style.Amber); b.AddThemeColorOverride("font_hover_pressed_color", Style.Amber);
        b.Pressed += () => { Sfx.Click(); onPress(); };
        return b;
    }

    // 调查员卡 / 弱点卡: a pill that stays lit while on
    Button Toggle(string text, Func<bool> get, Action<bool> set)
    {
        Button b = null!;
        b = Pill(text, get(), () => { set(b.ButtonPressed); Refilter(); }, 19);
        b.CustomMinimumSize = new Vector2(0, 54);
        b.TooltipText = text == "弱点卡" ? "只看可以加入的弱点卡" : "只看这名调查员的专属卡";
        return b;
    }

    // a class icon (the icon font's glyph in the class colour; 中立 / 多职业 as one character), a toggle
    Button ClassIcon(string key, string name)
    {
        var colour = key switch { "guardian" => new Color("5a8fd6"), "seeker" => new Color("d99a52"), "rogue" => new Color("54b47a"), "mystic" => new Color("9a78d0"), "survivor" => new Color("d86a6a"), _ => Style.Paper };
        bool glyph = Style.Glyph.TryGetValue(key, out var g) && Style.Symbols is not null;
        bool neutralMark = !glyph && key == "neutral" && Style.ClassIcons is not null;   // neutral's own mark (Yunke 11-22), not "中"
        var b = new Button { Text = glyph ? g : neutralMark ? Style.NeutralGlyph : "多", ToggleMode = true, TooltipText = name, FocusMode = FocusModeEnum.None, CustomMinimumSize = new Vector2(50, 50), MouseDefaultCursorShape = CursorShape.PointingHand };
        b.AddThemeFontSizeOverride("font_size", glyph ? 28 : neutralMark ? 26 : 21);
        if (glyph) b.AddThemeFontOverride("font", Style.Symbols); else if (neutralMark) b.AddThemeFontOverride("font", Style.ClassIcons); else if (Style.Kai is not null) b.AddThemeFontOverride("font", Style.Kai);
        foreach (var (state, lit, bg) in new[] { ("normal", false, 0.3f), ("hover", true, 0.5f), ("pressed", true, 0.85f), ("hover_pressed", true, 0.9f) })
        { var sb = Ui.SoftBox(bg, lit, 0, 0); sb.SetCornerRadiusAll(25); b.AddThemeStyleboxOverride(state, sb); }
        b.AddThemeStyleboxOverride("focus", new StyleBoxEmpty());
        b.AddThemeColorOverride("font_color", new Color(colour, 0.75f)); b.AddThemeColorOverride("font_hover_color", colour);
        b.AddThemeColorOverride("font_pressed_color", colour.Lightened(0.25f)); b.AddThemeColorOverride("font_hover_pressed_color", colour.Lightened(0.25f));
        b.Pressed += () => { Sfx.Click(); if (!factions.Add(key)) factions.Remove(key); Refilter(); };
        return b;
    }

    // a group heading in the deck list: "支援 8" over a thin rule
    static Control GroupHeader(string title, int count)
    {
        var col = new VBoxContainer { MouseFilter = MouseFilterEnum.Ignore }; col.AddThemeConstantOverride("separation", 3);
        col.AddChild(Ui.Spacer(0, 6));
        var row = new HBoxContainer { MouseFilter = MouseFilterEnum.Ignore }; row.AddThemeConstantOverride("separation", 8); col.AddChild(row);
        row.AddChild(Style.Text(title, 19, Style.HoverText, Style.Kai));
        row.AddChild(Style.Text(count.ToString(), 18, Style.PaperDim, Style.Sans));
        col.AddChild(new ColorRect { Color = new Color(Style.Brass, 0.28f), CustomMinimumSize = new Vector2(0, 1), MouseFilter = MouseFilterEnum.Ignore });
        return col;
    }

    // a card's skill icons (willpower, intellect, combat, agility, wild), each in its colour
    static Control? SkillIcons(JsonElement card)
    {
        if (Style.Symbols is null) return null;
        var row = new HBoxContainer { MouseFilter = MouseFilterEnum.Ignore, SizeFlagsVertical = SizeFlags.ShrinkCenter }; row.AddThemeConstantOverride("separation", 0);
        foreach (var (field, key, colour) in new[] { ("skill_willpower", "willpower", Style.Willpower), ("skill_intellect", "intellect", Style.Intellect), ("skill_combat", "combat", Style.Combat), ("skill_agility", "agility", Style.Agility), ("skill_wild", "wild", Style.Paper) })
            for (int i = 0; i < Math.Min(4, J.Int(card, field)); i++)
            {
                var l = Style.Text(Style.Glyph[key], 18, colour, Style.Symbols); l.AddThemeConstantOverride("outline_size", 4); l.AddThemeColorOverride("font_outline_color", Colors.Black); row.AddChild(l);
            }
        return row.GetChildCount() > 0 ? row : null;
    }

    // ───────── the small panels (opened under their button, inside this screen so they also work over the table)
    Control? popover;
    void ClosePopover() { if (popover is not null && IsInstanceValid(popover)) popover.QueueFree(); popover = null; }
    void Popover(Control anchor, Control content)
    {
        ClosePopover();
        var catcher = new Control { MouseFilter = MouseFilterEnum.Stop, ZIndex = 120 }; AddChild(catcher); catcher.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        catcher.GuiInput += e => { if (e is InputEventMouseButton { Pressed: true }) ClosePopover(); };
        var panel = new PanelContainer(); panel.AddThemeStyleboxOverride("panel", Ui.SoftPanel(0.96f, 20, 16)); panel.AddChild(content); catcher.AddChild(panel);
        var r = anchor.GetGlobalRect(); var origin = GetGlobalRect().Position;
        panel.Position = r.Position - origin + new Vector2(0, r.Size.Y + 10);
        // keep it on screen once its size is known
        Callable.From(() => { if (!IsInstanceValid(panel)) return; float maxX = Size.X - panel.Size.X - 16; if (panel.Position.X > maxX) panel.Position = new Vector2(Math.Max(16, maxX), panel.Position.Y); }).CallDeferred();
        popover = catcher;
    }

    static Label Caption(string t) => Style.Text(t, 18, Style.PaperDim, Style.Kai);

    // "…": where the search box looks
    Control SearchScope()
    {
        var col = new VBoxContainer(); col.AddThemeConstantOverride("separation", 10);
        col.AddChild(Caption("在这些内容里搜索"));
        var row = new HBoxContainer(); row.AddThemeConstantOverride("separation", 8); col.AddChild(row);
        void Add(string name, Func<bool> get, Action<bool> set) { Button b = null!; b = Pill(name, get(), () => { set(b.ButtonPressed); if (!searchName && !searchTraits && !searchText) { set(true); b.SetPressedNoSignal(true); } Refilter(); }); row.AddChild(b); }
        Add("卡名", () => searchName, v => searchName = v);
        Add("特性", () => searchTraits, v => searchTraits = v);
        Add("规则文字", () => searchText, v => searchText = v);
        return col;
    }

    // 筛选: level and cost
    Control FilterPanel()
    {
        var col = new VBoxContainer(); col.AddThemeConstantOverride("separation", 10);
        col.AddChild(Caption("等级"));
        var lv = new HBoxContainer(); lv.AddThemeConstantOverride("separation", 8); col.AddChild(lv);
        // 全部等级, then each level on its own, several at once (Yunke 11-22: "1-5级拆开 改为5个按钮，可以多选")
        lv.AddChild(Pill("全部等级", levels.Count == 0, () => { levels.Clear(); Refilter(); Popover(filterButton, FilterPanel()); }));
        for (int l = 0; l <= 5; l++)
        { int k = l; lv.AddChild(Pill(k + " 级", levels.Contains(k), () => { if (!levels.Remove(k)) levels.Add(k); Refilter(); Popover(filterButton, FilterPanel()); })); }
        col.AddChild(Caption("费用"));
        var cost = new HBoxContainer(); cost.AddThemeConstantOverride("separation", 6); col.AddChild(cost);
        for (int c = 0; c <= 6; c++)
        {
            int k = c; var g = CostGem(c == 6 ? "6+" : c.ToString(), () => { costFilter = costFilter == k ? -1 : k; Refilter(); Popover(filterButton, FilterPanel()); });
            g.SetPressedNoSignal(costFilter == c); cost.AddChild(g);
        }
        col.AddChild(PackSection());
        var clear = Ui.Button("清除筛选", () => { levels.Clear(); costFilter = -1; factions.Clear(); packFilter.Clear(); type = ""; onlyInvestigator = onlyWeakness = false; Refilter(); ClosePopover(); }, 19);
        clear.SizeFlagsHorizontal = SizeFlags.ShrinkEnd; col.AddChild(clear);
        return col;
    }

    // ───────── 扩展包 (Yunke 11-19 #23): build's packs grouped by cycle, only those with cards this deck may take
    readonly HashSet<string> packFilter = new();
    readonly List<(string Code, string Name, List<(string Code, string Name)> Packs)> cycles = new();
    async Task LoadPacks()
    {
        if (cycles.Count > 0) return;
        JsonElement data;
        try { data = await BuildDecks.Packs(); } catch { return; }
        var used = pool.Select(c => J.String(c, "pack_code")).ToHashSet();
        var cycleNames = J.Items(J.Get(data, "cycles")).ToDictionary(c => J.String(c, "code"), c => (Name: J.String(c, "name"), Pos: J.Int(c, "position")));
        foreach (var g in J.Items(J.Get(data, "packs")).Where(p => used.Contains(J.String(p, "code")))
                     .GroupBy(p => J.String(p, "cycle_code")).OrderBy(g => cycleNames.TryGetValue(g.Key, out var cy) ? cy.Pos : 99))
        {
            string cname = cycleNames.TryGetValue(g.Key, out var cy) && cy.Name.Length > 0 ? cy.Name : "其他";
            // a pack without a Chinese name in build's data is called by its cycle's (重印版 when the cycle has one already)
            var packs = g.OrderBy(p => J.Int(p, "position")).Select(p => (J.String(p, "code"), J.String(p, "name").Length > 0 ? J.String(p, "name") : cname + "（重印）")).ToList();
            cycles.Add((g.Key, cname, packs));
        }
        if (App.I.QaRunning) GD.Print("QA_PACKS " + cycles.Count + " cycles " + cycles.Sum(c => c.Packs.Count) + " packs");
    }

    readonly List<(Button Button, string[] Packs)> packButtons = new();
    Control PackSection()
    {
        packButtons.Clear();
        var col = new VBoxContainer(); col.AddThemeConstantOverride("separation", 8);
        var head = new HBoxContainer(); head.AddThemeConstantOverride("separation", 10); col.AddChild(head);
        head.AddChild(Caption("扩展包（单击循环名选中整个循环）"));
        var list = new VBoxContainer(); list.AddThemeConstantOverride("separation", 10);
        foreach (var (code, name, packs) in cycles)
        {
            var row = new HBoxContainer(); row.AddThemeConstantOverride("separation", 10); list.AddChild(row);
            var all = packs.Select(p => p.Code).ToArray();
            Button cb = null!;
            cb = Pill(name, all.All(packFilter.Contains), () => { bool on = !all.All(packFilter.Contains); foreach (var k in all) { if (on) packFilter.Add(k); else packFilter.Remove(k); } Refilter(); SyncPacks(); }, 19);
            cb.CustomMinimumSize = new Vector2(180, 42); cb.SizeFlagsVertical = SizeFlags.ShrinkBegin; row.AddChild(cb); packButtons.Add((cb, all));
            if (packs.Count > 1 || packs[0].Name != name)
            {
                var flow = new HFlowContainer { CustomMinimumSize = new Vector2(640, 0) }; flow.AddThemeConstantOverride("h_separation", 6); flow.AddThemeConstantOverride("v_separation", 6); row.AddChild(flow);
                foreach (var (pc, pn) in packs)
                {
                    var k = pc; var b = Pill(pn, packFilter.Contains(pc), () => { if (!packFilter.Add(k)) packFilter.Remove(k); Refilter(); SyncPacks(); }, 17);
                    b.CustomMinimumSize = new Vector2(0, 38); flow.AddChild(b); packButtons.Add((b, new[] { pc }));
                }
            }
        }
        var scroll = new ScrollContainer { CustomMinimumSize = new Vector2(860, 330), HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled };
        scroll.AddChild(list); list.SizeFlagsHorizontal = SizeFlags.ExpandFill; col.AddChild(scroll); SmoothScroll.Attach(scroll, 90);
        return col;
    }
    void SyncPacks() { foreach (var (b, packs) in packButtons) if (IsInstanceValid(b)) b.SetPressedNoSignal(packs.All(packFilter.Contains)); }

    // 排序
    Control SortPanel()
    {
        var col = new VBoxContainer(); col.AddThemeConstantOverride("separation", 6);
        col.AddChild(Caption("排列方式"));
        foreach (var (key, name) in Sorts)
        { var k = key; var b = Pill("按" + name, sort == key, () => { sort = k; Refilter(); ClosePopover(); }); b.CustomMinimumSize = new Vector2(180, 44); col.AddChild(b); }
        return col;
    }

    // 禁卡表: build's taboo lists as small numbered boxes (the date in each tooltip); the rules core plays them
    async Task TabooPopover()
    {
        var col = new VBoxContainer { CustomMinimumSize = new Vector2(300, 0) }; col.AddThemeConstantOverride("separation", 8);
        col.AddChild(Caption("禁卡表"));
        var row = new HFlowContainer { CustomMinimumSize = new Vector2(300, 0) }; row.AddThemeConstantOverride("h_separation", 8); row.AddThemeConstantOverride("v_separation", 8); col.AddChild(row);
        await TabooChip.Fill(row, CurrentTaboo(), id => { ClosePopover(); _ = Do(() => BuildDecks.Edit("taboo", deckId, id), true); });
        Popover(tabooButton, col);
    }
    int? CurrentTaboo() => J.Get(view, "taboo_id").ValueKind == JsonValueKind.Number ? J.Get(view, "taboo_id").GetInt32() : null;
    // the 禁 button's face: the word and the version in its box
    async Task RefreshTabooFace()
    {
        await TabooChip.Sets();
        if (!IsInstanceValid(tabooFace)) return;
        Ui.Clear(tabooFace);
        tabooFace.AddChild(Style.Text("禁", 19, Style.Paper, Style.Kai));
        tabooFace.AddChild(TabooChip.Badge(CurrentTaboo(), 16));
        tabooButton.TooltipText = TabooChip.Tip(CurrentTaboo());
    }
}
