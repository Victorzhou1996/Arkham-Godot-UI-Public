using Godot;
using Gathering;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ArkhamUI;

// Deck workshop, laid out and animated like Hearthstone's deck builder (Yunke 11-18 r6 #4: "各种元素都完全一致", only the
// style is ours, with soft edges everywhere):
//   left  — the collection "book": a page of 4 × 2 cards, a count plaque under each, page arrows at the sides, the page
//           number at the bottom; a filter bar under it (back, cost gems 0–6+, level, class, type, search, usable only);
//   right — the deck: the investigator's banner and the deck name on top, one tile per card sorted by cost (cost disc,
//           name, art strip, count), the fixed signature / weakness cards at the end, the card count and Save at the foot.
// Clicking a card sends a glowing copy flying into the deck with a sparkle trail and the tile flashes; clicking a tile
// flashes it and sends the card back to its page; hovering a tile shows the whole card beside the list; right-click
// reads a card large. The old screen is kept in backup/WorkshopScreen-20261118-before-hs.cs.txt.
// Validation is still the rules core's.
public partial class WorkshopScreen : Control
{
    Services S => App.I.S!;
    string investigator = "01001";
    string deckKey = "";
    string deckName = "";
    int budget;
    Dictionary<string, int> slots = new();
    JsonElement schema;
    bool dirty;
    string search = "", faction = "", type = ""; int maxLevel = 0; bool usableOnly = true;
    int costFilter = -1;   // -1 every cost; 6 means 6 and more
    int page;
    const int PerPage = 8;
    static readonly Vector2 CardSize = new(232, 324);

    OptionButton invPick = null!, loadPick = null!, levelPick = null!; LineEdit nameEdit = null!, searchEdit = null!; SpinBox budgetSpin = null!;
    GridContainer library = null!; VBoxContainer deckList = null!;
    Label countLabel = null!, xpLabel = null!, status = null!, libCount = null!, editState = null!, pageLabel = null!, bookTitle = null!;
    Label? diffLabel;   // upgrade mode: removed / added against the deck brought in
    Control libraryPanel = null!, deckPanel = null!;
    Button prevPage = null!, nextPage = null!;
    TextureRect bannerArt = null!; TextureRect faceImg = null!;
    readonly List<Button> costGems = new();
    readonly Dictionary<string, Control> tiles = new();       // deck tile per card code (for the flights)
    readonly Dictionary<string, CardView> pageCards = new();  // the cards on the open page
    CardView? preview;                                         // the whole card beside a hovered tile
    int validateVersion;
    string? qaShot;   // QA: capture this file 0.2 s into the next flight

    static readonly string[] Investigators = { "01001", "01002", "01003", "01004", "01005", "02001", "02002", "02003", "02004", "02005" };

    public override void _Ready()
    {
        MouseFilter = MouseFilterEnum.Stop;
        var bg = Ui.Image(Ui.Ref("art/scenario-archive-v1/desk"), Vector2.Zero, true); AddChild(bg); bg.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        var dim = new ColorRect { Color = new Color(0, 0, 0, 0.62f), MouseFilter = MouseFilterEnum.Ignore }; AddChild(dim); dim.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);

        // ───── the book (left)
        var book = new VBoxContainer(); book.AddThemeConstantOverride("separation", 12); AddChild(book);
        book.AnchorLeft = 0; book.AnchorRight = 0.755f; book.AnchorTop = 0; book.AnchorBottom = 1;
        book.OffsetLeft = 36; book.OffsetRight = -10; book.OffsetTop = 24; book.OffsetBottom = -22;

        // top: whose cards, the banner, and the deck file actions
        var top = new HBoxContainer(); top.AddThemeConstantOverride("separation", 12); book.AddChild(top);
        faceImg = Ui.Image(null, new Vector2(44, 62), true); top.AddChild(faceImg);
        invPick = new OptionButton { CustomMinimumSize = new Vector2(250, 54) }; Ui.Soften(invPick); invPick.AddThemeFontSizeOverride("font_size", 23);
        foreach (var c in Investigators) invPick.AddItem(S.Name(c));
        invPick.ItemSelected += i => { if (ConfirmDiscard()) _ = SwitchInvestigator(Investigators[i], null); };
        top.AddChild(invPick);
        bookTitle = Style.Title("", 38); bookTitle.HorizontalAlignment = HorizontalAlignment.Center; bookTitle.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        bookTitle.AddThemeConstantOverride("outline_size", 8); bookTitle.AddThemeColorOverride("font_outline_color", new Color(0, 0, 0, 0.85f));
        top.AddChild(bookTitle);
        loadPick = new OptionButton { CustomMinimumSize = new Vector2(270, 54) }; Ui.Soften(loadPick); loadPick.AddThemeFontSizeOverride("font_size", 21);
        loadPick.ItemSelected += i => { var key = loadPick.GetItemMetadata((int)i).AsString(); if (key.Length > 0 && ConfirmDiscard()) LoadDeck(key); };
        top.AddChild(loadPick);
        if (Upgrade is null)
        {
            top.AddChild(Ui.Button("新建", () => { if (ConfirmDiscard()) _ = NewDeck(); }, 21));
            top.AddChild(Ui.Button("复制", Copy, 21));
            top.AddChild(Ui.Button("删除", DeleteDeck, 21));
        }

        // class, type, level and "usable only" above the page, on the right
        var sub = new HBoxContainer { Alignment = BoxContainer.AlignmentMode.End }; sub.AddThemeConstantOverride("separation", 10); book.AddChild(sub);
        // the page: 4 × 2 cards between two arrows, on a soft dark leaf
        var leaf = new PanelContainer { SizeFlagsVertical = SizeFlags.ExpandFill }; leaf.AddThemeStyleboxOverride("panel", Ui.SoftPanel(0.55f, 20, 16)); book.AddChild(leaf);
        libraryPanel = leaf;
        var leafCol = new VBoxContainer(); leafCol.AddThemeConstantOverride("separation", 6); leaf.AddChild(leafCol);
        var pageRow = new HBoxContainer { SizeFlagsVertical = SizeFlags.ExpandFill }; pageRow.AddThemeConstantOverride("separation", 10); leafCol.AddChild(pageRow);
        prevPage = Arrow(false); pageRow.AddChild(prevPage);
        var center = new CenterContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill, SizeFlagsVertical = SizeFlags.ExpandFill }; pageRow.AddChild(center);
        library = new GridContainer { Columns = 4 }; library.AddThemeConstantOverride("h_separation", 46); library.AddThemeConstantOverride("v_separation", 18); center.AddChild(library);
        nextPage = Arrow(true); pageRow.AddChild(nextPage);
        var pageFoot = new HBoxContainer { Alignment = BoxContainer.AlignmentMode.Center }; pageFoot.AddThemeConstantOverride("separation", 24); leafCol.AddChild(pageFoot);
        pageLabel = Style.Text("", 22, Style.Paper, Style.Kai); pageFoot.AddChild(pageLabel);
        libCount = Style.Text("", 18, Style.PaperDim, Style.Kai); pageFoot.AddChild(libCount);
        var hint = Style.Text("单击卡牌加入 · 单击右侧牌组移出 · 右键查看大图", 18, new Color(Style.PaperDim, 0.8f), Style.Kai); pageFoot.AddChild(hint);

        // the filter bar
        var filters = new HBoxContainer(); filters.AddThemeConstantOverride("separation", 10); book.AddChild(filters);
        var back = Upgrade is null ? Ui.Button("‹ 返回", Back, 22) : Ui.Button("不升级，直接继续", () => _ = FinishUpgrade(true), 22);
        filters.AddChild(back);
        filters.AddChild(Ui.Spacer(8, 0));
        filters.AddChild(Style.Text("费用", 21, Style.PaperDim, Style.Kai));
        for (int c = 0; c <= 6; c++) { int cost = c; var g = CostGem(c == 6 ? "6+" : c.ToString(), () => { costFilter = costFilter == cost ? -1 : cost; page = 0; FillLibrary(true); }); costGems.Add(g); filters.AddChild(g); }
        filters.AddChild(Ui.Spacer(12, 0));
        sub.AddChild(Pick(new[] { ("", "所有职业"), ("guardian", "守护者"), ("seeker", "探求者"), ("rogue", "流浪者"), ("mystic", "神秘学家"), ("survivor", "生存者"), ("neutral", "中立") }, v => { faction = v; page = 0; FillLibrary(true); }));
        sub.AddChild(Pick(new[] { ("", "所有类型"), ("asset", "支援"), ("event", "事件"), ("skill", "技能") }, v => { type = v; page = 0; FillLibrary(true); }));
        levelPick = Pick(new[] { ("0", "仅 0 级"), ("5", "全部等级") }, v => { maxLevel = int.Parse(v); page = 0; FillLibrary(true); }); sub.AddChild(levelPick);
        searchEdit = new LineEdit { PlaceholderText = "查找卡名、特性、规则…", SizeFlagsHorizontal = SizeFlags.ExpandFill, CustomMinimumSize = new Vector2(260, 50) }; searchEdit.AddThemeFontSizeOverride("font_size", 21);
        SoftEdit(searchEdit);
        searchEdit.TextChanged += s => { search = s.Trim(); page = 0; FillLibrary(true); }; filters.AddChild(searchEdit);
        var usable = new CheckBox { Text = "仅可用卡", ButtonPressed = true, FocusMode = FocusModeEnum.None }; usable.AddThemeFontSizeOverride("font_size", 21); Ui.Soften(usable);
        usable.Toggled += on => { usableOnly = on; page = 0; FillLibrary(true); }; sub.AddChild(usable);

        // ───── the deck (right)
        var deck = new PanelContainer(); deck.AddThemeStyleboxOverride("panel", Ui.SoftPanel(0.8f, 14, 12)); AddChild(deck);
        deck.AnchorLeft = 0.755f; deck.AnchorRight = 1; deck.AnchorTop = 0; deck.AnchorBottom = 1;
        deck.OffsetLeft = 10; deck.OffsetRight = -30; deck.OffsetTop = 24; deck.OffsetBottom = -22;
        deckPanel = deck;
        var dcol = new VBoxContainer(); dcol.AddThemeConstantOverride("separation", 8); deck.AddChild(dcol);
        // banner: the investigator's art in a band that fades out at its edges, the name on it
        var banner = new Control { CustomMinimumSize = new Vector2(0, 96), ClipContents = true, MouseFilter = MouseFilterEnum.Ignore }; dcol.AddChild(banner);
        bannerArt = new TextureRect { ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize, StretchMode = TextureRect.StretchModeEnum.KeepAspectCovered, MouseFilter = MouseFilterEnum.Ignore, TextureFilter = TextureFilterEnum.LinearWithMipmapsAnisotropic };
        banner.AddChild(bannerArt); bannerArt.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        banner.Resized += () => bannerArt.Material = Ui.EdgeAlpha(Vector2.Zero, banner.Size, 40, 18, 40, 30);
        nameEdit = new LineEdit { CustomMinimumSize = new Vector2(0, 46), Alignment = HorizontalAlignment.Center }; nameEdit.AddThemeFontSizeOverride("font_size", 23); SoftEdit(nameEdit);
        nameEdit.TextChanged += s => { deckName = s; MarkDirty(); }; dcol.AddChild(nameEdit);
        editState = Style.Text("", 17, Style.PaperDim, Style.Kai); editState.HorizontalAlignment = HorizontalAlignment.Center; dcol.AddChild(editState);
        // the experience: budget (standalone) or what is available (upgrade)
        var bl = new HBoxContainer { Alignment = BoxContainer.AlignmentMode.Center }; bl.AddThemeConstantOverride("separation", 10); dcol.AddChild(bl);
        bl.AddChild(Style.Text(Upgrade is null ? "经验预算" : "可用经验", 20, Style.PaperDim, Style.Kai));
        budgetSpin = new SpinBox { MinValue = 0, MaxValue = 49, Step = 1, CustomMinimumSize = new Vector2(110, 42) }; budgetSpin.GetLineEdit().AddThemeFontSizeOverride("font_size", 20); SoftEdit(budgetSpin.GetLineEdit());
        budgetSpin.ValueChanged += v => { budget = (int)v; MarkDirty(); Refresh(); }; bl.AddChild(budgetSpin);
        xpLabel = Style.Text("", 20, Style.Paper, Style.Sans); bl.AddChild(xpLabel);
        if (Upgrade is not null) { diffLabel = Style.Text("", 18, Style.Amber, Style.Kai); diffLabel.AutowrapMode = TextServer.AutowrapMode.WordSmart; dcol.AddChild(diffLabel); }
        // the list
        deckList = new VBoxContainer(); deckList.AddThemeConstantOverride("separation", 3);
        var scroll = Ui.Scroll(deckList); scroll.SizeFlagsVertical = SizeFlags.ExpandFill; dcol.AddChild(scroll);
        // the foot: status line, then the count and the buttons
        status = Style.Text("", 17, Style.PaperDim, Style.Kai); status.AutowrapMode = TextServer.AutowrapMode.WordSmart; status.HorizontalAlignment = HorizontalAlignment.Center; dcol.AddChild(status);
        var foot = new HBoxContainer(); foot.AddThemeConstantOverride("separation", 8); dcol.AddChild(foot);
        countLabel = Style.Text("", 30, Style.Good, Style.Sans); countLabel.VerticalAlignment = VerticalAlignment.Center; foot.AddChild(countLabel);
        foot.AddChild(Ui.Spacer(0, 0, true));
        var v = Ui.Button("校验", () => _ = Validate(true), 21); v.CustomMinimumSize = new Vector2(96, 54); foot.AddChild(v);
        var save = Upgrade is null ? Ui.Button("保存牌组", () => _ = Save(), 23, true) : Ui.Button("完成升级", () => _ = FinishUpgrade(false), 23, true);
        save.CustomMinimumSize = new Vector2(170, 54); foot.AddChild(save); finishButton = Upgrade is null ? null : save;

        invPick.ItemSelected += i => faceImg.Texture = Ui.Mini(Investigators[i]);
        QaScript.Register("ws-add", code => Add(code));
        QaScript.Register("ws-remove", code => Remove(code));
        QaScript.Register("ws-inv", code => { invPick.Selected = Array.IndexOf(Investigators, code); _ = SwitchInvestigator(code, null); });
        QaScript.Register("ws-save", async _ => await Save());
        QaScript.Register("ws-validate", async _ => await Validate(true));
        QaScript.Register("ws-new", async _ => await NewDeck());
        QaScript.Register("ws-page", a => { if (int.TryParse(a, out int p)) TurnPage(p - page); });
        QaScript.Register("ws-click", a => { if (int.TryParse(a, out int i) && i >= 0 && i < library.GetChildCount() && library.GetChild(i) is Control t && t.GetChild(0) is CardView cv) cv.OnClick?.Invoke(); });
        QaScript.Register("ws-tile", code => { if (tiles.TryGetValue(code, out var t) && t is Button b) b.EmitSignal(BaseButton.SignalName.Pressed); });
        QaScript.Register("ws-hover", code => { if (tiles.TryGetValue(code, out var t)) ShowPreview(code, t); });
        QaScript.Register("ws-shot", a => qaShot = a);
        QaScript.Register("ws-cost", a => { if (int.TryParse(a, out int c)) { costFilter = c; page = 0; FillLibrary(true); } });
        if (Upgrade is not null) { _ = StartUpgrade(); return; }
        _ = SwitchInvestigator(investigator, S.Decks.For(investigator).Select(d => d.Key).FirstOrDefault());
    }

    // ───────── small parts
    OptionButton Pick((string Value, string Name)[] items, Action<string> on)
    {
        var o = new OptionButton { CustomMinimumSize = new Vector2(150, 50) }; Ui.Soften(o); o.AddThemeFontSizeOverride("font_size", 20);
        foreach (var (val, name) in items) { o.AddItem(name); o.SetItemMetadata(o.ItemCount - 1, val); }
        o.ItemSelected += i => on(o.GetItemMetadata((int)i).AsString()); return o;
    }
    static void SoftEdit(LineEdit e)
    {
        var n = Ui.SoftBox(0.45f, false, 14, 6); var f = Ui.SoftBox(0.6f, true, 14, 6);
        e.AddThemeStyleboxOverride("normal", n); e.AddThemeStyleboxOverride("focus", f); e.AddThemeStyleboxOverride("read_only", n);
    }
    // a round cost gem for the filter bar (Hearthstone's mana gems): lit when chosen
    Button CostGem(string text, Action onPress)
    {
        var b = new Button { Text = text, ToggleMode = true, FocusMode = FocusModeEnum.None, CustomMinimumSize = new Vector2(46, 46), MouseDefaultCursorShape = CursorShape.PointingHand };
        b.AddThemeFontSizeOverride("font_size", 21); b.AddThemeFontOverride("font", Style.Symbols ?? Style.Sans);
        StyleBoxFlat Round(bool lit)
        {
            var s = Ui.SoftBox(lit ? 0.75f : 0.4f, lit, 0, 0); s.SetCornerRadiusAll(23);
            if (!lit) s.BgColor = new Color(0.1f, 0.13f, 0.2f, 0.85f);   // a cool gem colour when idle, warm when chosen
            return s;
        }
        b.AddThemeStyleboxOverride("normal", Round(false)); b.AddThemeStyleboxOverride("hover", Round(true)); b.AddThemeStyleboxOverride("pressed", Round(true)); b.AddThemeStyleboxOverride("hover_pressed", Round(true));
        b.AddThemeColorOverride("font_color", Style.HoverText); b.AddThemeColorOverride("font_pressed_color", new Color(1, 0.95f, 0.85f));
        b.Pressed += () => { Sfx.Click(); onPress(); };
        return b;
    }
    // a page arrow at the side of the book
    Button Arrow(bool next)
    {
        var b = new Button { Text = next ? "›" : "‹", FocusMode = FocusModeEnum.None, CustomMinimumSize = new Vector2(64, 120), SizeFlagsVertical = SizeFlags.ShrinkCenter, MouseDefaultCursorShape = CursorShape.PointingHand };
        b.AddThemeFontSizeOverride("font_size", 72); b.AddThemeColorOverride("font_color", Style.Amber); b.AddThemeColorOverride("font_hover_color", Style.HoverText); b.AddThemeColorOverride("font_disabled_color", new Color(Style.PaperDim, 0.18f));
        Ui.Soften(b, 4, 4);
        b.Pressed += () => TurnPage(next ? 1 : -1);
        return b;
    }

    // ───────── data
    HashSet<string> FixedCodes()
    {
        var set = new HashSet<string>();
        foreach (var sig in J.Props(J.Get(J.Get(schema, "requirements"), "card")))
            foreach (var alt in J.Props(sig.Value)) if (S.Catalog.Cards.ContainsKey(alt.Name)) set.Add(alt.Name);
        foreach (var (code, _) in slots) { var c = S.Card(code); if (J.String(c, "subtype_code") is "weakness" or "basicweakness") set.Add(code); }
        return set;
    }
    int Ordinary() { var f = FixedCodes(); return slots.Where(p => !f.Contains(p.Key)).Sum(p => p.Value); }
    int DeckSize() => J.Int(J.Get(S.Card(investigator), "deck_requirements"), "size", 30);
    int Xp() => Upgrade is not null ? UpgradeCost(slots) : S.Catalog.Experience(Build());
    static int Limit(JsonElement card) => J.Get(card, "deck_limit").ValueKind == JsonValueKind.Number ? J.Int(card, "deck_limit") : 2;
    // printed cost; skills (no cost) sort after everything
    static int Cost(JsonElement card) => CoreCosts.Cost(J.String(card, "code")) ?? -1;
    static int SortCost(JsonElement card) { int c = Cost(card); return c < 0 ? 99 : c; }

    async Task SwitchInvestigator(string code, string? key)
    {
        investigator = code; invPick.Selected = Array.IndexOf(Investigators, code);
        faceImg.Texture = Ui.Mini(code);
        var art = Ui.Portrait(code);
        bannerArt.Texture = art is null ? null : new AtlasTexture { Atlas = art, Region = new Rect2(0, art.GetHeight() * 0.12f, art.GetWidth(), art.GetHeight() * 0.42f) };
        status.Text = "读取构筑规则…";
        try { schema = await S.Backend.BuildSchema(code); } catch (Exception e) { status.Text = "读取失败：" + e.Message; return; }
        FillLoad();
        var k = key ?? S.Decks.For(code).Select(d => d.Key).FirstOrDefault();
        page = 0;
        if (k is not null) LoadDeck(k); else await NewDeck();
        status.Text = "";
    }

    void FillLoad()
    {
        loadPick.Clear(); loadPick.AddItem("读取已有牌组…"); loadPick.SetItemMetadata(0, "");
        foreach (var (k, d) in S.Decks.For(investigator)) { loadPick.AddItem(J.String(d, "name", k)); loadPick.SetItemMetadata(loadPick.ItemCount - 1, k); }
        loadPick.Selected = 0;
    }

    void LoadDeck(string key)
    {
        var d = S.Decks.Get(key); deckKey = key; deckName = J.String(d, "name", key);
        var all = CoreCatalog.Slots(d); var extras = StandaloneDecks.Extras(d);
        foreach (var e in extras) { if (all.TryGetValue(e, out int n)) { if (n <= 1) all.Remove(e); else all[e] = n - 1; } }
        slots = all;
        try { budget = StandaloneDecks.Budget(d); } catch { budget = 0; }
        nameEdit.Text = deckName; budgetSpin.SetValueNoSignal(budget); dirty = false; FillLibrary(); Refresh();
    }

    async Task NewDeck()
    {
        var starter = S.Catalog.Starter(investigator, schema);
        deckKey = ""; deckName = S.Name(investigator) + " · 新牌组"; slots = CoreCatalog.Slots(starter); budget = 0;
        nameEdit.Text = deckName; budgetSpin.SetValueNoSignal(0); dirty = true; FillLibrary(); Refresh();
        status.Text = "已按示例构筑建立新牌组，可在此基础上调整。";
        await Task.CompletedTask;
    }

    void Copy() { deckKey = ""; deckName += " · 副本"; nameEdit.Text = deckName; MarkDirty(); status.Text = "已复制为新牌组，保存后生效。"; }

    void DeleteDeck()
    {
        if (deckKey.Length == 0) { status.Text = "这个牌组尚未保存。"; return; }
        var col = new VBoxContainer { CustomMinimumSize = new Vector2(600, 0) }; col.AddThemeConstantOverride("separation", 20);
        var l = Style.Text("删除牌组“" + deckName + "”？此操作不能撤销。", 26, Style.Paper, Style.Kai); l.AutowrapMode = TextServer.AutowrapMode.WordSmart; col.AddChild(l);
        var row = new HBoxContainer { Alignment = BoxContainer.AlignmentMode.End }; col.AddChild(row);
        Control scrim = null!;
        row.AddChild(Ui.Button("取消", () => App.I.CloseModal(scrim), 22));
        row.AddChild(Ui.Button("删除", () => { App.I.CloseModal(scrim); S.Decks.Remove(deckKey); S.Decks.Save(); _ = SwitchInvestigator(investigator, null); App.I.Toast("已删除"); }, 22, true));
        scrim = App.I.Modal(Ui.Panel(col, 34));
    }

    JsonElement Build()
    {
        var node = new JsonObject { ["name"] = deckName, ["investigator_code"] = investigator, ["slots"] = JsonSerializer.SerializeToNode(slots.Where(p => p.Value > 0).ToDictionary(p => p.Key, p => p.Value)), ["sideSlots"] = new JsonObject() };
        if (budget > 0) node["meta"] = new JsonObject { [StandaloneDecks.BudgetKey] = budget }.ToJsonString();
        return JsonSerializer.SerializeToElement(node);
    }

    // ───────── editing
    bool CanAdd(string code, out string why)
    {
        why = ""; var card = S.Card(code);
        if (FixedCodes().Contains(code)) { why = "专属与弱点为固定卡"; return false; }
        if (slots.GetValueOrDefault(code) >= Limit(card)) { why = "同名卡最多 " + Limit(card) + " 张"; return false; }
        if (Ordinary() >= DeckSize()) { why = "牌组已满 " + DeckSize() + " 张"; return false; }
        int xp = Math.Max(0, J.Int(card, "xp"));
        if (Upgrade is not null)
        {
            var trial = new Dictionary<string, int>(slots); trial[code] = trial.GetValueOrDefault(code) + 1;
            int cost = UpgradeCost(trial);
            if (cost > budget) { why = "需要 " + cost + " 经验，可用 " + budget; return false; }
            return true;
        }
        if (xp > 0 && Xp() + xp > budget) { why = "需要 " + (Xp() + xp) + " 经验，预算 " + budget; return false; }
        return true;
    }
    void Add(string code) => AddFrom(code, pageCards.TryGetValue(code, out var cv) && IsInstanceValid(cv) ? cv.GetGlobalRect() : (Rect2?)null);
    void AddFrom(string code, Rect2? from)
    {
        if (!CanAdd(code, out var why)) { status.Text = "不能加入「" + S.Name(code) + "」：" + why; Shake(code); return; }
        bool isNew = !slots.ContainsKey(code);
        slots[code] = slots.GetValueOrDefault(code) + 1; MarkDirty(); status.Text = "已加入「" + S.Name(code) + "」1 张";
        Refresh(); FillLibrary();
        if (from is Rect2 r) _ = FlyIn(code, r, isNew);
    }
    void Remove(string code)
    {
        if (FixedCodes().Contains(code) || !slots.ContainsKey(code)) return;
        Rect2? at = tiles.TryGetValue(code, out var t) && IsInstanceValid(t) ? t.GetGlobalRect() : null;
        if (--slots[code] <= 0) slots.Remove(code);
        MarkDirty(); status.Text = "已移出「" + S.Name(code) + "」1 张"; HidePreview(); Refresh(); FillLibrary();
        if (at is Rect2 r) FlyOut(code, r);
    }
    void MarkDirty() { dirty = true; if (editState is not null) editState.Text = "编辑中 · 未保存"; ScheduleValidate(); }
    bool ConfirmDiscard() { if (!dirty) return true; App.I.Toast("未保存的修改已放弃"); dirty = false; return true; }

    // ───────── the book
    JsonElement[] Pool()
    {
        IEnumerable<JsonElement> pool = usableOnly ? S.Catalog.Pool(schema, maxLevel)
            : S.Catalog.Cards.Values.Where(c => J.String(c, "type_code") is "asset" or "event" or "skill" && CoreCatalog.IsCorePlayerCard(J.String(c, "code")) && J.Int(c, "xp") <= maxLevel && J.String(c, "subtype_code").Length == 0);
        return pool.Where(c => (faction == "" || J.String(c, "faction_code") == faction) && (type == "" || J.String(c, "type_code") == type)
                && (costFilter < 0 || (costFilter >= 6 ? Cost(c) >= 6 : Cost(c) == costFilter))
                && (search == "" || (J.String(c, "name") + J.String(c, "traits") + J.String(c, "text") + J.String(c, "real_name")).Contains(search, StringComparison.OrdinalIgnoreCase)))
            .OrderBy(SortCost).ThenBy(c => J.Int(c, "xp")).ThenBy(c => J.String(c, "name")).ToArray();
    }

    void TurnPage(int delta)
    {
        if (schema.ValueKind != JsonValueKind.Object) return;
        int pages = Math.Max(1, (Pool().Length + PerPage - 1) / PerPage);
        int to = Math.Clamp(page + delta, 0, pages - 1);
        if (to == page) return;
        int dir = Math.Sign(to - page); page = to;
        if (Calm) { FillLibrary(); return; }
        // the page slides out to one side and the next slides in from the other (a page turning)
        var tw = library.CreateTween();
        tw.TweenProperty(library, "modulate:a", 0f, 0.12f);
        tw.Parallel().TweenProperty(library, "position:x", library.Position.X - dir * 60, 0.12f);
        tw.TweenCallback(Callable.From(() => { FillLibrary(); library.Position = new Vector2(library.Position.X + dir * 120, library.Position.Y); }));
        tw.TweenProperty(library, "modulate:a", 1f, 0.18f);
        tw.Parallel().TweenProperty(library, "position:x", library.Position.X, 0.22f).SetTrans(Tween.TransitionType.Cubic).SetEase(Tween.EaseType.Out);
    }
    static bool Calm => App.I.S?.Settings.ReducedMotion == true;

    void FillLibrary(bool fade = false)
    {
        if (library is null || schema.ValueKind != JsonValueKind.Object) return;
        Ui.Clear(library); pageCards.Clear();
        var list = Pool();
        int pages = Math.Max(1, (list.Length + PerPage - 1) / PerPage); page = Math.Clamp(page, 0, pages - 1);
        bookTitle.Text = S.Name(investigator) + " · 可用卡牌";
        libCount.Text = "共 " + list.Length + " 种";
        pageLabel.Text = "第 " + (page + 1) + " 页 / 共 " + pages + " 页";
        prevPage.Disabled = page == 0; nextPage.Disabled = page >= pages - 1;
        for (int i = 0; i < costGems.Count; i++) costGems[i].SetPressedNoSignal(i == costFilter);
        foreach (var c in list.Skip(page * PerPage).Take(PerPage))
        {
            string code = J.String(c, "code"); int have = slots.GetValueOrDefault(code), lim = Limit(c);
            bool can = CanAdd(code, out _);
            var tile = new VBoxContainer { MouseFilter = MouseFilterEnum.Ignore }; tile.AddThemeConstantOverride("separation", 4);
            var cv = new CardView { Code = code, CustomMinimumSize = CardSize, Size = CardSize, Actionable = false, Draggable = true, HoverZoom = false, ZoomHeight = 640, Redrawn = false };   // the workshop keeps printed cards
            cv.OnClick = () => Add(code);
            cv.OnDragRelease = pos => { if (deckPanel.GetGlobalRect().Grow(10).HasPoint(pos)) { Add(code); return true; } return false; };
            cv.GuiInput += e => { if (e is InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Right }) CardReader.Open(code, printed: true); };
            // hover: the card rises a little and glows (Hearthstone lifts the card under the pointer)
            cv.PivotOffset = CardSize / 2;
            cv.MouseEntered += () => { if (!Calm) cv.CreateTween().TweenProperty(cv, "scale", new Vector2(1.05f, 1.05f), 0.12f); };
            cv.MouseExited += () => { if (!Calm) cv.CreateTween().TweenProperty(cv, "scale", Vector2.One, 0.12f); };
            if (!can) cv.Modulate = new Color(0.55f, 0.55f, 0.55f);   // at its limit, or the deck is full: greyed
            tile.AddChild(cv); pageCards[code] = cv;
            // the count plaque under the card ("×2" in Hearthstone): how many are in the deck out of the limit
            var plaque = new PanelContainer { SizeFlagsHorizontal = SizeFlags.ShrinkCenter, MouseFilter = MouseFilterEnum.Ignore };
            plaque.AddThemeStyleboxOverride("panel", Ui.SoftBox(have > 0 ? 0.7f : 0.4f, have > 0, 14, 2));
            var n = Style.Text(have + " / " + lim, 20, have > 0 ? Style.HoverText : Style.PaperDim, Style.Sans); n.HorizontalAlignment = HorizontalAlignment.Center; plaque.AddChild(n);
            tile.AddChild(plaque);
            library.AddChild(tile);
        }
        if (fade && !Calm) { library.Modulate = new Color(1, 1, 1, 0); library.CreateTween().TweenProperty(library, "modulate:a", 1f, 0.18f); }
    }

    // a card that cannot be added shakes on its page
    void Shake(string code)
    {
        if (Calm || !pageCards.TryGetValue(code, out var cv) || !IsInstanceValid(cv)) return;
        float x = cv.Position.X; var tw = cv.CreateTween();
        foreach (var dx in new[] { -8f, 8f, -5f, 5f, 0f }) tw.TweenProperty(cv, "position:x", x + dx, 0.04f);
    }

    // ───────── the deck list
    void Refresh()
    {
        if (deckList is null) return;
        Ui.Clear(deckList); tiles.Clear();
        var f = FixedCodes();
        var ordinary = slots.Where(p => !f.Contains(p.Key)).OrderBy(p => SortCost(S.Card(p.Key))).ThenBy(p => J.String(S.Card(p.Key), "name")).ToList();
        foreach (var (code, n) in ordinary) deckList.AddChild(Tile(code, n, true));
        if (ordinary.Count == 0) { var e = Style.Text("单击左侧的卡牌加入牌组", 20, Style.PaperDim, Style.Kai); e.HorizontalAlignment = HorizontalAlignment.Center; deckList.AddChild(e); }
        var fixedOnes = slots.Where(p => f.Contains(p.Key)).OrderBy(p => p.Key).ToList();
        if (fixedOnes.Count > 0)
        {
            deckList.AddChild(Ui.Spacer(0, 8));
            var h = Style.Text("专属与弱点 · 固定保留，不计入张数", 17, Style.PaperDim, Style.Kai); h.HorizontalAlignment = HorizontalAlignment.Center; deckList.AddChild(h);
            foreach (var (code, n) in fixedOnes) deckList.AddChild(Tile(code, n, false));
        }
        int ord = Ordinary(), size = DeckSize(), xp = Xp();
        countLabel.Text = ord + " / " + size + " 张"; countLabel.AddThemeColorOverride("font_color", ord == size ? Style.Good : Style.Danger);
        xpLabel.Text = (Upgrade is not null ? "花费 " : "已用 ") + xp + " / " + budget; xpLabel.AddThemeColorOverride("font_color", xp <= budget ? Style.Paper : Style.Danger);
        if (finishButton is not null) finishButton.Text = xp > 0 ? "完成升级（" + xp + " 经验）" : "完成升级";
        if (diffLabel is not null && Upgrade is not null)
        {
            string Item(string code, int n) { int lv = J.Int(S.Card(code), "xp"); return S.Name(code) + (lv > 0 ? "（" + lv + "）" : "") + " ×" + n; }
            var outs = upgradeFrom.Where(p => slots.GetValueOrDefault(p.Key) < p.Value).Select(p => Item(p.Key, p.Value - slots.GetValueOrDefault(p.Key))).ToList();
            var ins = slots.Where(p => upgradeFrom.GetValueOrDefault(p.Key) < p.Value).Select(p => Item(p.Key, p.Value - upgradeFrom.GetValueOrDefault(p.Key))).ToList();
            diffLabel.Text = outs.Count + ins.Count == 0 ? Upgrade.InvestigatorName + "：牌组未改动"
                : Upgrade.InvestigatorName + "：" + (outs.Count > 0 ? "移出 " + string.Join("、", outs) + "　" : "") + (ins.Count > 0 ? "加入 " + string.Join("、", ins) + "　" : "") + "花费 " + xp + "，余 " + Math.Max(0, budget - xp);
        }
        if (editState is not null) editState.Text = dirty ? "编辑中 · 未保存" : (deckKey.Length > 0 ? "已保存" : "");
    }

    // One deck tile (Hearthstone's deck list row): the cost disc in the card's class colour, the name, the card's art in a
    // strip that fades in from the left, the count at the end. Click removes one; hover shows the whole card; right-click reads it.
    Control Tile(string code, int n, bool removable)
    {
        var card = S.Card(code);
        var b = new Button { FocusMode = FocusModeEnum.None, CustomMinimumSize = new Vector2(0, 48), MouseDefaultCursorShape = removable ? CursorShape.PointingHand : CursorShape.Arrow, ClipContents = true };
        var normal = new StyleBoxFlat { BgColor = new Color(0.07f, 0.06f, 0.05f, 0.92f), AntiAliasing = true, ShadowColor = new Color(0, 0, 0, 0.5f), ShadowSize = 4 }; normal.SetCornerRadiusAll(8);
        var hover = (StyleBoxFlat)normal.Duplicate(); hover.BgColor = new Color(0.2f, 0.15f, 0.07f, 0.95f); hover.ShadowColor = new Color(Style.Amber, 0.35f); hover.ShadowSize = 8;
        b.AddThemeStyleboxOverride("normal", normal); b.AddThemeStyleboxOverride("hover", removable ? hover : normal); b.AddThemeStyleboxOverride("pressed", hover); b.AddThemeStyleboxOverride("focus", new StyleBoxEmpty());
        // the art strip on the right
        var tex = Ui.Card(code);
        if (tex is not null)
        {
            var crop = new AtlasTexture { Atlas = tex, Region = new Rect2(tex.GetWidth() * 0.1f, tex.GetHeight() * 0.14f, tex.GetWidth() * 0.8f, tex.GetHeight() * 0.26f) };
            var art = new TextureRect { Texture = crop, ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize, StretchMode = TextureRect.StretchModeEnum.KeepAspectCovered, MouseFilter = MouseFilterEnum.Ignore, TextureFilter = TextureFilterEnum.LinearWithMipmapsAnisotropic };
            b.AddChild(art); art.AnchorLeft = 0.42f; art.AnchorRight = 1; art.AnchorTop = 0; art.AnchorBottom = 1; art.OffsetRight = -44;
            art.Resized += () => art.Material = Ui.EdgeAlpha(Vector2.Zero, art.Size, art.Size.X * 0.7f, 0, 10, 0);
        }
        var row = new HBoxContainer { MouseFilter = MouseFilterEnum.Ignore }; row.AddThemeConstantOverride("separation", 10);
        b.AddChild(row); row.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect); row.OffsetLeft = 6; row.OffsetRight = -10;
        // cost disc
        // a plain panel, so the number can stand in its exact middle (Ui.CenterDigits; a container would lay it out itself)
        var disc = new Panel { CustomMinimumSize = new Vector2(36, 36), SizeFlagsVertical = SizeFlags.ShrinkCenter, MouseFilter = MouseFilterEnum.Ignore };
        var ds = new StyleBoxFlat { BgColor = SelectionStage.FactionColor(code).Darkened(0.15f), BorderColor = new Color(0.9f, 0.82f, 0.62f, 0.85f), AntiAliasing = true }; ds.SetBorderWidthAll(2); ds.SetCornerRadiusAll(18);
        disc.AddThemeStyleboxOverride("panel", ds);
        int cost = Cost(card);
        var cl = Style.Text(cost < 0 ? "–" : cost.ToString(), 21, Colors.White, Style.Symbols); cl.HorizontalAlignment = HorizontalAlignment.Center; cl.VerticalAlignment = VerticalAlignment.Center;
        cl.AddThemeConstantOverride("outline_size", 5); cl.AddThemeColorOverride("font_outline_color", Colors.Black); disc.AddChild(cl); Ui.CenterDigits(cl); row.AddChild(disc);
        int xp = J.Int(card, "xp");
        var name = Style.Text(S.Name(code) + (xp > 0 ? " " + new string('•', Math.Min(5, xp)) : ""), 21, removable ? Style.Paper : Style.PaperDim, Style.Kai);
        name.SizeFlagsHorizontal = SizeFlags.ExpandFill; name.VerticalAlignment = VerticalAlignment.Center; name.ClipText = true;
        name.AddThemeConstantOverride("outline_size", 6); name.AddThemeColorOverride("font_outline_color", new Color(0, 0, 0, 0.9f)); row.AddChild(name);
        var cnt = Style.Text(removable ? (n > 1 ? n.ToString() : "") : "固定", removable ? 22 : 16, Style.Amber, removable ? Style.Sans : Style.Kai);
        cnt.CustomMinimumSize = new Vector2(30, 0); cnt.HorizontalAlignment = HorizontalAlignment.Center; cnt.VerticalAlignment = VerticalAlignment.Center; row.AddChild(cnt);
        if (removable) b.Pressed += () => Remove(code);
        b.GuiInput += e => { if (e is InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Right }) CardReader.Open(code, printed: true); };
        b.MouseEntered += () => ShowPreview(code, b);
        b.MouseExited += HidePreview;
        // drag a tile back onto the book to remove one
        Vector2? press = null;
        b.GuiInput += e =>
        {
            if (!removable) return;
            if (e is InputEventMouseButton { ButtonIndex: MouseButton.Left } mb) press = mb.Pressed ? mb.GlobalPosition : null;
            if (e is InputEventMouseMotion mm && press is Vector2 p && mm.GlobalPosition.DistanceTo(p) > 24 && libraryPanel.GetGlobalRect().HasPoint(mm.GlobalPosition)) { press = null; Remove(code); }
        };
        tiles[code] = b;
        return b;
    }

    // the whole card beside the list, level with the hovered tile
    void ShowPreview(string code, Control tile)
    {
        HidePreview();
        var size = new Vector2(286, 400);
        preview = new CardView { Code = code, CustomMinimumSize = size, Size = size, HoverZoom = false, Redrawn = false, MouseFilter = MouseFilterEnum.Ignore, ZIndex = 150 };
        AddChild(preview);
        var r = tile.GetGlobalRect(); float vpH = GetViewportRect().Size.Y;
        preview.GlobalPosition = new Vector2(deckPanel.GetGlobalRect().Position.X - size.X - 18, Math.Clamp(r.GetCenter().Y - size.Y / 2, 20, vpH - size.Y - 20));
        if (!Calm) { preview.Modulate = new Color(1, 1, 1, 0); preview.CreateTween().TweenProperty(preview, "modulate:a", 1f, 0.12f); }
    }
    void HidePreview() { if (preview is not null && IsInstanceValid(preview)) preview.QueueFree(); preview = null; }

    // ───────── flights
    // Add: a bright copy of the card shrinks and flies from the page into its tile, trailing sparks; the tile flashes.
    async Task FlyIn(string code, Rect2 from, bool isNew)
    {
        if (Calm) return;
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);   // the list has laid out the new tile
        if (!tiles.TryGetValue(code, out var tile) || !IsInstanceValid(tile)) { if (App.I.QaRunning) GD.Print("QA_WS_FLY no tile " + code); return; }
        var to = tile.GetGlobalRect();
        if (App.I.QaRunning) GD.Print($"QA_WS_FLY {code} from={from} to={to}");
        if (isNew) tile.Modulate = new Color(1, 1, 1, 0);
        var ghost = new CardView { Code = code, CustomMinimumSize = from.Size, Size = from.Size, HoverZoom = false, Redrawn = false, ShowCost = false, MouseFilter = MouseFilterEnum.Ignore, ZIndex = 160 };
        AddChild(ghost); ghost.GlobalPosition = from.Position; ghost.PivotOffset = from.Size / 2;
        ghost.Modulate = new Color(1.9f, 1.75f, 1.4f, 1f);   // glowing white-gold
        float k = to.Size.Y / from.Size.Y * 1.6f;
        var target = to.GetCenter() - from.Size / 2;
        const float T = 0.45f;
        var tw = ghost.CreateTween().SetParallel();
        tw.TweenProperty(ghost, "global_position", target, T).SetTrans(Tween.TransitionType.Cubic).SetEase(Tween.EaseType.In);
        tw.TweenProperty(ghost, "scale", new Vector2(k, k), T).SetTrans(Tween.TransitionType.Cubic).SetEase(Tween.EaseType.In);
        tw.TweenProperty(ghost, "modulate:a", 0f, 0.14f).SetDelay(T - 0.1f);
        Sparks(from.GetCenter(), to.GetCenter(), T);
        ulong t0 = Time.GetTicksMsec();
        if (qaShot is string shot) { qaShot = null; GetTree().CreateTimer(0.2).Timeout += () => App.I.Capture(System.IO.Path.GetFullPath(System.IO.Path.Combine(App.I.Root, shot))); }
        await ToSignal(GetTree().CreateTimer(T), SceneTreeTimer.SignalName.Timeout);
        if (App.I.QaRunning) GD.Print($"QA_WS_FLY done after {Time.GetTicksMsec() - t0} ms, ghost at {ghost.GlobalPosition} alpha {ghost.Modulate.A}");
        ghost.QueueFree();
        if (IsInstanceValid(tile)) Flash(tile);
    }

    // Remove: the tile flashes, a bright copy rises from it and flies back to its card on the page (or into the book), fading.
    void FlyOut(string code, Rect2 tileRect)
    {
        if (Calm) return;
        var size = CardSize * 0.5f;
        var dest = pageCards.TryGetValue(code, out var cv) && IsInstanceValid(cv) ? cv.GetGlobalRect() : new Rect2(libraryPanel.GetGlobalRect().GetCenter() - CardSize / 2, CardSize);
        var flash = new Panel { MouseFilter = MouseFilterEnum.Ignore, ZIndex = 155, Position = tileRect.Position, Size = tileRect.Size };
        var fs = new StyleBoxFlat { BgColor = new Color(1, 0.92f, 0.75f, 0.85f), ShadowColor = new Color(1, 0.85f, 0.5f, 0.6f), ShadowSize = 14 }; fs.SetCornerRadiusAll(8);
        flash.AddThemeStyleboxOverride("panel", fs); AddChild(flash);
        var ftw = flash.CreateTween(); ftw.TweenProperty(flash, "modulate:a", 0f, 0.35f); ftw.TweenCallback(Callable.From(flash.QueueFree));
        var ghost = new CardView { Code = code, CustomMinimumSize = size, Size = size, HoverZoom = false, Redrawn = false, ShowCost = false, MouseFilter = MouseFilterEnum.Ignore, ZIndex = 160 };
        AddChild(ghost); ghost.GlobalPosition = tileRect.GetCenter() - size / 2; ghost.PivotOffset = size / 2;
        ghost.Modulate = new Color(1.8f, 1.7f, 1.4f, 0.95f);
        var tw = ghost.CreateTween().SetParallel();
        tw.TweenProperty(ghost, "global_position", dest.GetCenter() - size / 2, 0.42f).SetTrans(Tween.TransitionType.Cubic).SetEase(Tween.EaseType.Out);
        tw.TweenProperty(ghost, "scale", new Vector2(dest.Size.X / size.X, dest.Size.Y / size.Y), 0.42f).SetTrans(Tween.TransitionType.Cubic).SetEase(Tween.EaseType.Out);
        tw.TweenProperty(ghost, "modulate", new Color(1, 1, 1, 0), 0.25f).SetDelay(0.3f);
        tw.Chain().TweenCallback(Callable.From(ghost.QueueFree));
        if (IsInstanceValid(cv)) { var c = cv!; c.CreateTween().TweenProperty(c, "modulate", c.Modulate, 0.3f).From(new Color(1.6f, 1.5f, 1.2f)).SetDelay(0.3f); }
    }

    // a tile lights up and settles
    void Flash(Control tile)
    {
        tile.Modulate = new Color(2f, 1.8f, 1.4f, 1f);
        tile.CreateTween().TweenProperty(tile, "modulate", Colors.White, 0.45f).SetTrans(Tween.TransitionType.Quad).SetEase(Tween.EaseType.Out);
    }

    // small glowing motes along the flight path, each drifting a little and fading
    void Sparks(Vector2 a, Vector2 b, float t)
    {
        var rnd = new Random();
        for (int i = 0; i < 16; i++)
        {
            float s = (float)rnd.NextDouble();
            var p = a.Lerp(b, s) + new Vector2((float)rnd.NextDouble() * 60 - 30, (float)rnd.NextDouble() * 60 - 30);
            float r = 3 + (float)rnd.NextDouble() * 4;
            var dot = new Panel { MouseFilter = MouseFilterEnum.Ignore, ZIndex = 158, Size = new Vector2(r * 2, r * 2), Position = p - new Vector2(r, r) };
            var st = new StyleBoxFlat { BgColor = new Color(1f, 0.93f, 0.7f), ShadowColor = new Color(1f, 0.8f, 0.4f, 0.8f), ShadowSize = (int)(r * 2.5f) }; st.SetCornerRadiusAll((int)r + 1);
            dot.AddThemeStyleboxOverride("panel", st); AddChild(dot);
            dot.Modulate = new Color(1, 1, 1, 0);
            float delay = s * t * 0.9f;
            var tw = dot.CreateTween();
            tw.TweenInterval(delay);
            tw.TweenProperty(dot, "modulate:a", 1f, 0.06f);
            tw.Parallel().TweenProperty(dot, "position", dot.Position + new Vector2((float)rnd.NextDouble() * 30 - 15, -12 - (float)rnd.NextDouble() * 20), 0.5f);
            tw.TweenProperty(dot, "modulate:a", 0f, 0.35f);
            tw.TweenCallback(Callable.From(dot.QueueFree));
        }
    }

    // ───────── validation and save
    async void ScheduleValidate() { int v = ++validateVersion; await ToSignal(GetTree().CreateTimer(0.6), SceneTreeTimer.SignalName.Timeout); if (v == validateVersion) await Validate(false); }

    async Task<bool> Validate(bool verbose)
    {
        try
        {
            var prepared = await S.Backend.PrepareStandaloneDeck(Build());
            var result = await S.Backend.ValidateDeck(investigator, J.String(schema, "schemaId"), prepared);
            var st = J.String(result, "status");
            if (st == "valid") { if (verbose) status.Text = "规则核心校验通过，可以带入调查。"; return true; }
            var errors = J.Items(J.Get(result, "errors")).Select(e => ErrorText(J.String(e, "message", J.String(e, "code")))).Where(s => s.Length > 0).Distinct().Take(3);
            status.Text = "尚未通过校验：" + string.Join("；", errors); status.AddThemeColorOverride("font_color", Style.Danger);
            return false;
        }
        catch (Exception e) { status.Text = "尚未通过校验：" + e.Message; status.AddThemeColorOverride("font_color", Style.Danger); return false; }
        finally { if (status.Text.StartsWith("规则核心") || status.Text.StartsWith("已")) status.AddThemeColorOverride("font_color", Style.PaperDim); }
    }

    // the core's error codes in Chinese (all player text is Chinese)
    string ErrorText(string m)
    {
        if (System.Text.RegularExpressions.Regex.IsMatch(m, "[一-鿿]")) return m;
        string k = m.ToLowerInvariant();
        if (k.Contains("size")) return "张数不对：需要 " + DeckSize() + " 张，现在 " + Ordinary() + " 张";
        if (k.Contains("limit") || k.Contains("copies")) return "同名卡超过上限";
        if (k.Contains("xp") || k.Contains("experience")) return "经验超出预算";
        if (k.Contains("signature") || k.Contains("required") || k.Contains("requirement")) return "缺少专属卡或弱点";
        if (k.Contains("faction") || k.Contains("access") || k.Contains("option")) return "有卡牌不在这名调查员的可用范围内";
        return "不符合构筑规则";
    }

    async Task Save()
    {
        deckName = nameEdit.Text.Trim(); if (deckName.Length == 0) { status.Text = "请先填写牌组名称"; return; }
        // Saving over a built-in example keeps the example and stores a copy; give the copy its own name.
        if (deckKey.StartsWith("example-") && S.Decks.Has(deckKey) && J.String(S.Decks.Get(deckKey), "name") == deckName) { deckName += "（我的）"; nameEdit.Text = deckName; }
        if (!await Validate(true)) { App.I.Toast("牌组未通过校验，暂不能保存", true); return; }
        var prepared = await S.Backend.PrepareStandaloneDeck(Build());
        if (deckKey.Length == 0 || deckKey.StartsWith("example-")) deckKey = S.Decks.NewKey();
        S.Decks.Put(deckKey, prepared); S.Decks.Save(); dirty = false; FillLoad(); Refresh();
        status.Text = "已保存「" + deckName + "」"; App.I.Toast("牌组已保存");
    }

    void Back() { var m = new MainMenu(); App.I.Go(m); m.Ready(); }
}
