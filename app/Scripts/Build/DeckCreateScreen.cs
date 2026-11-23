using Godot;
using Gathering;
using System.Text.Json;

namespace ArkhamUI;

// 创建卡组 (build's deck-create flow): first every investigator, grouped by cycle, as small upright pictures; then the
// deck settings build asks for — name, taboo list, front / back (parallel), the investigator's deck options (a class to
// pick, a deck size…), the card sets (advanced / replacement signatures…) — and 创建, which build's own createDeck runs.
public partial class DeckCreateScreen : Control
{
    Services S => App.I.S!;
    VBoxContainer body = null!;
    Label status = null!;
    bool inOptions;   // an investigator picked: back goes to the list first

    public override void _Ready()
    {
        MouseFilter = MouseFilterEnum.Stop;
        AddChild(DeckLibraryScreen.WorkshopBackdrop());
        var head = Style.Title("创建卡组 · 选择调查员", 42); AddChild(head); head.Position = new Vector2(102, 30);   // right of the back icon (WorkshopBack)
        status = Style.Text("", 20, Style.PaperDim, Style.Kai); AddChild(status); status.Position = new Vector2(106, 92);
        body = new VBoxContainer(); body.AddThemeConstantOverride("separation", 14);
        var scroll = Ui.Scroll(body); AddChild(scroll); scroll.Position = new Vector2(48, 130); scroll.Size = new Vector2(1824, 930); SmoothScroll.Attach(scroll);
        // back one level: from an investigator's options to the list, from the list to the library (Yunke 11-21)
        WorkshopBack.Attach(this, "返回上一级（Esc）", async () => { if (inOptions) { inOptions = false; await WorkshopFade.Out(body); Ui.Clear(body); WorkshopFade.In(body, 0.22f); await ShowInvestigators(); } else App.I.Go(new DeckLibraryScreen()); });
        QaScript.Register("create-pick", a => _ = Options(a));
        QaScript.Register("create-go", async a => { await BuildDecks.CreateSet("createTitle", a.Length > 0 ? a : "测试牌组"); await Create(); });
        _ = ShowInvestigators();
    }

    // An investigator's picture: the portrait small card where we have one, else the left (picture) part of the printed card.
    public static Control InvPicture(string code, float height)
    {
        var mini = Ui.Mini(code);
        float w = MathF.Round(height * 0.645f);
        Texture2D? tex = mini;
        if (tex is null && Ui.Card(code) is { } card)
            tex = new AtlasTexture { Atlas = card, Region = new Rect2(card.GetWidth() * 0.03f, card.GetHeight() * 0.13f, card.GetWidth() * 0.4f, card.GetHeight() * 0.62f) };
        var img = Ui.Image(tex, new Vector2(w, height), true);
        img.Material = Ui.EdgeAlpha(Vector2.Zero, new Vector2(w, height), 6, 6, 6, 6);
        return img;
    }

    // The investigators, filtered by class and by expansion (Yunke 11-23: "创建卡组的界面太简陋了，改成可以筛选职阶和扩展包，并且尽量
    // 利用到整个屏幕"): a filter bar on top, then each expansion's investigators as a block of larger pictures, the blocks
    // flowing side by side across the whole width.
    readonly HashSet<string> factions = new(), cycleFilter = new();
    string search = "";
    JsonElement invCache; Dictionary<string, (string Name, int Pos)> cycleNames = new();
    VBoxContainer? filterBar; Label countLabel = null!;
    static readonly string[] Classes = { "guardian", "seeker", "rogue", "mystic", "survivor", "neutral" };

    async Task ShowInvestigators()
    {
        inOptions = false;
        status.Text = "正在读取调查员…";
        JsonElement invs, packs;
        try { invs = await BuildDecks.Investigators(); packs = await BuildDecks.Packs(); }
        catch (Exception e) { status.Text = "读取失败：" + e.Message; return; }
        var packCycle = J.Items(J.Get(packs, "packs")).ToDictionary(p => J.String(p, "code"), p => J.String(p, "cycle_code"));
        cycleNames = J.Items(J.Get(packs, "cycles")).ToDictionary(c => J.String(c, "code"), c => (J.String(c, "name"), J.Int(c, "position")));
        // each investigator with its expansion (cycle) code, for grouping and the expansion filter
        var list = new System.Text.Json.Nodes.JsonArray();
        foreach (var i in invs.EnumerateArray())
        {
            var o = System.Text.Json.Nodes.JsonNode.Parse(i.GetRawText())!.AsObject();
            o["cycle"] = packCycle.TryGetValue(J.String(i, "pack_code"), out var cy) && cycleNames.TryGetValue(cy, out var cn) && cn.Name.Length > 0 ? cy : "other";   // nameless cycles together as 其他
            list.Add(o);
        }
        invCache = JsonSerializer.SerializeToElement(list);
        status.Text = "点选一位调查员。牌组规则来自 arkham.build。";
        filterBar = null;
        BuildFilters();
        Fill();
    }

    void BuildFilters()
    {
        if (filterBar is not null && IsInstanceValid(filterBar)) filterBar.QueueFree();
        filterBar = new VBoxContainer(); filterBar.AddThemeConstantOverride("separation", 10);
        body.AddChild(filterBar);
        // classes: the icons as in the deck editor, any number at once
        var row1 = new HBoxContainer(); row1.AddThemeConstantOverride("separation", 10); filterBar.AddChild(row1);
        row1.AddChild(Heading("职阶"));
        foreach (var f in Classes) row1.AddChild(ClassToggle(f));
        row1.AddChild(Ui.Spacer(24, 0));
        var find = new LineEdit { PlaceholderText = "搜索调查员名字", CustomMinimumSize = new Vector2(360, 50), Text = search, ClearButtonEnabled = true };
        find.AddThemeFontSizeOverride("font_size", 22); if (Style.Kai is not null) find.AddThemeFontOverride("font", Style.Kai);
        find.AddThemeStyleboxOverride("normal", Ui.SoftBox(0.45f, false, 14, 6)); find.AddThemeStyleboxOverride("focus", Ui.SoftBox(0.6f, true, 14, 6));
        find.TextChanged += t => { search = t.Trim(); Fill(); };
        row1.AddChild(find);
        row1.AddChild(Ui.Spacer(0, 0, true));
        countLabel = Style.Text("", 22, Style.PaperDim, Style.Kai); countLabel.SizeFlagsVertical = SizeFlags.ShrinkCenter; row1.AddChild(countLabel);
        row1.AddChild(Ui.Button("清除筛选", () => { factions.Clear(); cycleFilter.Clear(); search = ""; BuildFilters(); Fill(); }, 20));
        // expansions present among the investigators, in release order
        var row2 = new HFlowContainer(); row2.AddThemeConstantOverride("h_separation", 8); row2.AddThemeConstantOverride("v_separation", 8); filterBar.AddChild(row2);
        row2.AddChild(Heading("扩展"));
        var present = invCache.EnumerateArray().Select(i => J.String(i, "cycle")).Distinct().OrderBy(c => cycleNames.TryGetValue(c, out var n) ? n.Pos : 999);
        foreach (var cy in present) row2.AddChild(CycleToggle(cy));
        body.MoveChild(filterBar, 0);
    }

    static Label Heading(string text) { var l = Style.Text(text, 24, Style.Brass, Style.Kai); l.SizeFlagsVertical = SizeFlags.ShrinkCenter; l.CustomMinimumSize = new Vector2(64, 0); return l; }

    Button ClassToggle(string key)
    {
        var colour = key switch { "guardian" => new Color("5a8fd6"), "seeker" => new Color("d99a52"), "rogue" => new Color("54b47a"), "mystic" => new Color("9a78d0"), "survivor" => new Color("d86a6a"), _ => Style.Paper };
        bool glyph = Style.Glyph.TryGetValue(key, out var g) && Style.Symbols is not null;
        bool neutralMark = !glyph && key == "neutral" && Style.ClassIcons is not null;
        var b = new Button { Text = glyph ? g : neutralMark ? Style.NeutralGlyph : BuildDecks.FactionName(key), ToggleMode = true, ButtonPressed = factions.Contains(key), TooltipText = BuildDecks.FactionName(key), FocusMode = FocusModeEnum.None, CustomMinimumSize = new Vector2(54, 54), MouseDefaultCursorShape = CursorShape.PointingHand };
        b.AddThemeFontSizeOverride("font_size", glyph ? 30 : 26);
        if (glyph) b.AddThemeFontOverride("font", Style.Symbols); else if (neutralMark) b.AddThemeFontOverride("font", Style.ClassIcons); else if (Style.Kai is not null) b.AddThemeFontOverride("font", Style.Kai);
        foreach (var (state, lit, bg) in new[] { ("normal", false, 0.3f), ("hover", true, 0.5f), ("pressed", true, 0.85f), ("hover_pressed", true, 0.9f) })
        { var sb = Ui.SoftBox(bg, lit, 0, 0); sb.SetCornerRadiusAll(27); b.AddThemeStyleboxOverride(state, sb); }
        b.AddThemeStyleboxOverride("focus", new StyleBoxEmpty());
        b.AddThemeColorOverride("font_color", new Color(colour, 0.75f)); b.AddThemeColorOverride("font_hover_color", colour);
        b.AddThemeColorOverride("font_pressed_color", colour.Lightened(0.25f)); b.AddThemeColorOverride("font_hover_pressed_color", colour.Lightened(0.25f));
        b.Toggled += on => { Sfx.Click(); if (on) factions.Add(key); else factions.Remove(key); Fill(); };
        return b;
    }

    Button CycleToggle(string cycle)
    {
        string name = cycleNames.TryGetValue(cycle, out var n) && n.Name.Length > 0 ? n.Name : "其他";
        var b = new Button { Text = name, ToggleMode = true, ButtonPressed = cycleFilter.Contains(cycle), FocusMode = FocusModeEnum.None, CustomMinimumSize = new Vector2(0, 46), MouseDefaultCursorShape = CursorShape.PointingHand };
        b.AddThemeFontSizeOverride("font_size", 20); if (Style.Kai is not null) b.AddThemeFontOverride("font", Style.Kai);
        foreach (var (state, lit, bg) in new[] { ("normal", false, 0.3f), ("hover", true, 0.5f), ("pressed", true, 0.85f), ("hover_pressed", true, 0.9f) })
            b.AddThemeStyleboxOverride(state, Ui.SoftBox(bg, lit, 14, 6));
        b.AddThemeStyleboxOverride("focus", new StyleBoxEmpty());
        b.AddThemeColorOverride("font_pressed_color", Style.Amber); b.AddThemeColorOverride("font_hover_pressed_color", Style.Amber);
        b.Toggled += on => { Sfx.Click(); if (on) cycleFilter.Add(cycle); else cycleFilter.Remove(cycle); Fill(); };
        return b;
    }

    // the investigators that pass the filters, a block per expansion
    HFlowContainer? grid;
    void Fill()
    {
        if (grid is not null && IsInstanceValid(grid)) grid.QueueFree();
        grid = new HFlowContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill }; grid.AddThemeConstantOverride("h_separation", 46); grid.AddThemeConstantOverride("v_separation", 22);
        body.AddChild(grid);
        bool Keep(JsonElement i)
        {
            if (cycleFilter.Count > 0 && !cycleFilter.Contains(J.String(i, "cycle"))) return false;
            if (factions.Count > 0 && !new[] { J.String(i, "faction_code"), J.String(i, "faction2_code") }.Any(f => f.Length > 0 && factions.Contains(f))) return false;
            if (search.Length > 0 && !(J.String(i, "name").Contains(search, StringComparison.OrdinalIgnoreCase) || J.String(i, "real_name").Contains(search, StringComparison.OrdinalIgnoreCase) || J.String(i, "subname").Contains(search))) return false;
            return true;
        }
        var shown = invCache.EnumerateArray().Where(Keep).ToList();
        countLabel.Text = shown.Count + " 位调查员";
        foreach (var g in shown.GroupBy(i => J.String(i, "cycle")).OrderBy(g => cycleNames.TryGetValue(g.Key, out var c) ? c.Pos : 999))
        {
            var block = new VBoxContainer(); block.AddThemeConstantOverride("separation", 10); grid.AddChild(block);
            block.AddChild(Style.Text((cycleNames.TryGetValue(g.Key, out var c) && c.Name.Length > 0 ? c.Name : "其他") + "  " + g.Count(), 26, Style.Brass, Style.Kai));
            var row = new HFlowContainer { CustomMinimumSize = new Vector2(Math.Min(g.Count(), 6) * 158, 0) }; row.AddThemeConstantOverride("h_separation", 14); row.AddThemeConstantOverride("v_separation", 14); block.AddChild(row);
            foreach (var inv in g.OrderBy(i => J.String(i, "code"))) row.AddChild(Tile(inv));
        }
        if (shown.Count == 0) grid.AddChild(Style.Text("没有符合筛选的调查员", 26, Style.PaperDim, Style.Kai));
    }

    Control Tile(JsonElement inv)
    {
        string code = J.String(inv, "code");
        var tile = new PanelContainer { MouseFilter = MouseFilterEnum.Stop, MouseDefaultCursorShape = CursorShape.PointingHand, TooltipText = J.String(inv, "name") + (J.String(inv, "subname") is { Length: > 0 } sn ? " · " + sn : "") };
        tile.AddThemeStyleboxOverride("panel", Ui.SoftBox(0.4f, false, 8, 6));
        var col = new VBoxContainer { MouseFilter = MouseFilterEnum.Ignore }; col.AddThemeConstantOverride("separation", 4); tile.AddChild(col);
        var pic = InvPicture(code, 196); pic.MouseFilter = MouseFilterEnum.Ignore; col.AddChild(pic);
        var n = Style.Text(J.String(inv, "name"), 19, SelectionStage.FactionColor(code).Lightened(0.4f), Style.Kai); n.HorizontalAlignment = HorizontalAlignment.Center;
        n.CustomMinimumSize = new Vector2(126, 0); n.ClipText = true; col.AddChild(n);
        tile.MouseEntered += () => tile.AddThemeStyleboxOverride("panel", Ui.SoftBox(0.7f, true, 8, 6));
        tile.MouseExited += () => tile.AddThemeStyleboxOverride("panel", Ui.SoftBox(0.4f, false, 8, 6));
        tile.GuiInput += e => { if (e is InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Left }) _ = Options(code); };
        return tile;
    }

    // step 2: build's deck-create settings for this investigator
    async Task Options(string code)
    {
        JsonElement info;
        try { info = await BuildDecks.CreateInit(code); } catch (Exception e) { App.I.Toast("无法创建：" + e.Message, true); return; }
        await WorkshopFade.Out(body);   // the list fades out, the options fade in (Yunke 11-22)
        RenderOptions(info);
    }

    void RenderOptions(JsonElement info)
    {
        Ui.Clear(body); inOptions = true; WorkshopFade.In(body, 0.22f);
        var row = new HBoxContainer(); row.AddThemeConstantOverride("separation", 40); body.AddChild(row);
        var front = J.String(J.Get(info, "front"), "code");
        var big = Peek.FramedCard(Ui.Card(front), new Vector2(560, 400)); row.AddChild(big);   // framed as every big card (Yunke 11-20)
        var form = new VBoxContainer { CustomMinimumSize = new Vector2(900, 0) }; form.AddThemeConstantOverride("separation", 16); row.AddChild(form);
        form.AddChild(Style.Title(J.String(J.Get(info, "investigator"), "name"), 40));
        // name
        var nameRow = new HBoxContainer(); nameRow.AddThemeConstantOverride("separation", 12); form.AddChild(nameRow);
        nameRow.AddChild(Style.Text("牌组名称", 22, Style.Paper, Style.Kai));
        // build's default title is English when its translations are not loaded: name it in Chinese
        string deckTitle = J.String(J.Get(info, "state"), "title");
        if (deckTitle.Length == 0 || System.Text.RegularExpressions.Regex.IsMatch(deckTitle, "[A-Za-z]{3,}")) { deckTitle = J.String(J.Get(info, "investigator"), "name") + " · 新牌组"; _ = BuildDecks.CreateSet("createTitle", deckTitle); }
        var name = new LineEdit { Text = deckTitle, CustomMinimumSize = new Vector2(560, 50) }; name.AddThemeFontSizeOverride("font_size", 22);
        name.AddThemeStyleboxOverride("normal", Ui.SoftBox(0.45f, false, 14, 6)); name.AddThemeStyleboxOverride("focus", Ui.SoftBox(0.6f, true, 14, 6));
        name.TextChanged += t => _ = BuildDecks.CreateSet("createTitle", t); nameRow.AddChild(name);
        // taboo
        var tabooRow = new HBoxContainer(); tabooRow.AddThemeConstantOverride("separation", 12); form.AddChild(tabooRow);
        tabooRow.AddChild(Style.Text("禁卡表", 22, Style.Paper, Style.Kai));
        // the versions as small numbered boxes, the date in each tooltip (TabooChip)
        var taboo = new HFlowContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill }; taboo.AddThemeConstantOverride("h_separation", 8); taboo.AddThemeConstantOverride("v_separation", 8);
        var ts = J.Get(J.Get(info, "state"), "tabooSetId");
        _ = TabooChip.Fill(taboo, ts.ValueKind == JsonValueKind.Number ? ts.GetInt32() : null, id => _ = BuildDecks.CreateSet("createTaboo", id));
        tabooRow.AddChild(taboo);
        // parallel front / back: the four combinations, picked in one go (Yunke 11-23: "无法直接选择正反面的原版平行调查员卡背组合");
        // the back decides the deck-building options and the signature cards, so the form is drawn again for the new pair
        if (J.Get(info, "parallel").ValueKind == JsonValueKind.Object)
        {
            string orig = J.String(J.Get(info, "investigator"), "code"), par = J.String(J.Get(info, "parallel"), "code");
            string nowF = J.String(J.Get(info, "front"), "code"), nowB = J.String(J.Get(info, "back"), "code");
            var r = new HBoxContainer(); r.AddThemeConstantOverride("separation", 12); form.AddChild(r);
            r.AddChild(Style.Text("正反面", 22, Style.Paper, Style.Kai));
            var combos = new HFlowContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill }; combos.AddThemeConstantOverride("h_separation", 8); combos.AddThemeConstantOverride("v_separation", 8); r.AddChild(combos);
            foreach (var (f, b) in new[] { (orig, orig), (orig, par), (par, orig), (par, par) })
            {
                string label = (f == orig ? "原版" : "平行") + "正面 · " + (b == orig ? "原版" : "平行") + "背面";
                bool on = nowF == f && nowB == b;
                var ff = f; var bb = b;
                var pill = Ui.Button(label, async () =>
                {
                    await BuildDecks.CreateSet("createSide", ff, "front"); await BuildDecks.CreateSet("createSide", bb, "back");
                    RenderOptions(await BuildDecks.CreateInfo());
                }, 20, on);
                pill.CustomMinimumSize = new Vector2(0, 46); combos.AddChild(pill);
            }
        }
        // the investigator's own deck options (a class to choose, a deck size…)
        foreach (var opt in J.Items(J.Get(info, "options")))
        {
            var r = new HBoxContainer(); r.AddThemeConstantOverride("separation", 12); form.AddChild(r);
            string key = J.String(opt, "id"); if (key.Length == 0) key = J.Get(opt, "faction_select").ValueKind == JsonValueKind.Array ? "faction_selected" : J.Get(opt, "deck_size_select").ValueKind == JsonValueKind.Array ? "deck_size_selected" : "option_selected";
            r.AddChild(Style.Text(OptionName(opt), 22, Style.Paper, Style.Kai));
            var o = new OptionButton { CustomMinimumSize = new Vector2(300, 46) }; Ui.Soften(o); o.AddThemeFontSizeOverride("font_size", 20);
            var values = new List<string>();
            if (J.Get(opt, "faction_select").ValueKind == JsonValueKind.Array) foreach (var f in J.Items(J.Get(opt, "faction_select"))) { values.Add(f.GetString()!); o.AddItem(BuildDecks.FactionName(f.GetString()!)); }
            else if (J.Get(opt, "deck_size_select").ValueKind == JsonValueKind.Array) foreach (var f in J.Items(J.Get(opt, "deck_size_select"))) { values.Add(f.ToString()); o.AddItem(f.ToString() + " 张"); }
            else foreach (var f in J.Items(J.Get(opt, "option_select"))) { values.Add(J.String(f, "id")); o.AddItem(J.String(f, "name", J.String(f, "id"))); }
            var k = key; o.ItemSelected += i => _ = BuildDecks.CreateSet("createSelection", k, values[(int)i]);
            if (values.Count > 0) _ = BuildDecks.CreateSet("createSelection", k, values[0]);
            r.AddChild(o);
        }
        // card sets (required signatures are always in; advanced / replacement / parallel can be chosen)
        foreach (var set in J.Items(J.Get(info, "sets")))
        {
            string sid = J.String(set, "id"); bool can = J.Get(set, "canSelect").ValueKind == JsonValueKind.True, on = J.Get(set, "selected").ValueKind == JsonValueKind.True;
            var r = new HBoxContainer(); r.AddThemeConstantOverride("separation", 10); form.AddChild(r);
            string title = sid switch { "requiredCards" => "专属卡", "advanced" => "进阶专属卡", "replacement" => "替换专属卡", "random_basic_weakness" => "随机基础弱点", "extra" => "平行调查员附加卡", _ => J.String(set, "title", sid) };
            if (can) { var cb = new CheckBox { Text = title, ButtonPressed = on, FocusMode = FocusModeEnum.None }; cb.AddThemeFontSizeOverride("font_size", 20); Ui.Soften(cb); var s2 = sid; cb.Toggled += on2 => _ = BuildDecks.CreateSet("createToggleSet", s2); r.AddChild(cb); }
            else r.AddChild(Style.Text(title, 20, Style.Brass, Style.Kai));
            var names = string.Join("、", J.Items(J.Get(set, "cards")).Select(c => J.String(c, "name") + (J.Int(c, "quantity") > 1 ? "×" + J.Int(c, "quantity") : "")));
            var nl = Style.Text(names, 18, Style.PaperDim, Style.Kai); nl.AutowrapMode = TextServer.AutowrapMode.WordSmart; nl.CustomMinimumSize = new Vector2(640, 0); r.AddChild(nl);
        }
        form.AddChild(Ui.Spacer(0, 10));
        var go = Ui.Button("创建卡组", () => _ = Create(), 26, true); go.CustomMinimumSize = new Vector2(280, 62); form.AddChild(go);
    }

    // an investigator deck option's label in Chinese (the card data names them in English)
    static string OptionName(JsonElement opt)
    {
        string n = J.String(opt, "name");
        if (System.Text.RegularExpressions.Regex.IsMatch(n, "[一-鿿]")) return n;
        if (J.Get(opt, "deck_size_select").ValueKind == JsonValueKind.Array || n.Contains("Deck Size", StringComparison.OrdinalIgnoreCase)) return "牌组张数";
        if (n.Contains("Secondary", StringComparison.OrdinalIgnoreCase) || n.Contains("Class", StringComparison.OrdinalIgnoreCase)) return "副职业";
        if (J.Get(opt, "faction_select").ValueKind == JsonValueKind.Array) return "可选职业";
        return "组牌选项";
    }

    async Task Create()
    {
        try
        {
            var id = await BuildDecks.Create();
            App.I.Toast("已创建卡组");
            App.I.Go(new DeckEditorScreen(id));
        }
        catch (Exception e) { App.I.Toast("创建失败：" + e.Message, true); }
    }
}
