using Godot;
using Gathering;
using System.Text.Json;

namespace ArkhamUI;

// The deck editor (Yunke 11-18 r6/r7, layout from Yunke's reference r9): Hearthstone's book and motion, arkham.build's
// rules and data, our desk backdrop and soft-edged controls.
//   top   — round back arrow, a wide 搜索卡牌 box with "…" (where to search), 筛选 (level, cost), the 调查员卡 / 弱点卡
//           toggles, 排序; below it the type pills (全部 · 支援 · 事件 · 技能) and the class icons (each a toggle);
//   left  — the book: every card the deck may take (build's access list), four to a row, scrolled up and down (smooth,
//           SmoothScroll; more rows are made as you scroll), the count in the deck under each; cards are masked to the
//           printed card's rounded corners;
//   right — the deck from build's store: portrait, name and an 经验 · 卡组需求 pill; the 编辑主卡组 switch (side / extra
//           deck, bonded cards) and 禁卡表; the list grouped by type (cost disc, name, skill icons, count); build's
//           validation in Chinese; 重置 / 保存并确定. Clicking a card in the list offers its higher-level versions
//           (DeckEditorScreen.CardUpgrade.cs) when there are any, else takes one copy out.
// Every change is build's updateCardQuantity on the deck's pending edits; 保存 runs build's saveDeck and writes the
// deck back to decks.json (BuildDecks).
public partial class DeckEditorScreen : Control
{
    Services S => App.I.S!;
    string deckId;
    // between scenarios of a campaign: the same editor on build's upgrade of the investigator's current deck
    // (DeckEditorScreen.Upgrade.cs)
    readonly UpgradeRequest? upgrade;
    JsonElement view;
    List<JsonElement> pool = new();
    string search = "", type = "", tab = "slots", sort = "cost"; int costFilter = -1;
    readonly HashSet<int> levels = new();   // 筛选's levels (none: all)
    readonly HashSet<string> factions = new();
    bool onlyInvestigator, onlyWeakness, searchName = true, searchTraits = true, searchText = true;
    // the book's cards: four to a row, as wide as the book allows (Yunke 11-19 #24: larger, filling the left side); columns,
    // gaps and rows per mode (大卡 / 小卡 / 文字) in DeckEditorScreen.Modes.cs
    const float CardAspect = 0.716f;   // a printed card, width / height
    static readonly Vector2 PortraitSize = new(76, 118);   // the table's portrait mini card (150×232), smaller
    Vector2 CardSize = new(270, 377);
    JsonElement[] filtered = [];
    int shown;
    ScrollContainer libScroll = null!;
    readonly Dictionary<string, (Label Count, PanelContainer Plaque, JsonElement Card)> plaques = new();

    GridContainer library = null!; VBoxContainer deckList = null!; Label libCount = null!, pill = null!, editState = null!;
    VBoxContainer? notesBox;
    VBoxContainer problems = null!; LineEdit nameEdit = null!;
    Button filterButton = null!, sortButton = null!, tabooButton = null!, moreButton = null!; HBoxContainer tabooFace = null!;
    Control libraryPanel = null!, deckPanel = null!; Control portrait = null!; HFlowContainer deckTabs = null!; readonly List<string> deckTabKeys = new();
    HBoxContainer typeTabs = null!; readonly Dictionary<string, Button> classButtons = new();
    readonly Dictionary<string, Control> tiles = new();
    readonly Dictionary<string, CardView> pageCards = new();
    CardView? preview;
    bool busy;

    public DeckEditorScreen(string deckId) { this.deckId = deckId; }
    public DeckEditorScreen(UpgradeRequest upgrade) { this.upgrade = upgrade; deckId = ""; }

    public override void _Ready()
    {
        MouseFilter = MouseFilterEnum.Stop;
        AddChild(DeckLibraryScreen.WorkshopBackdrop());

        // ───── the book: top bar, type pills + class icons, the page of cards
        var book = new VBoxContainer(); book.AddThemeConstantOverride("separation", 12); AddChild(book);
        book.AnchorRight = 0.755f; book.AnchorBottom = 1; book.OffsetLeft = 30; book.OffsetRight = -10; book.OffsetTop = 22; book.OffsetBottom = -18;
        var top = new HBoxContainer(); top.AddThemeConstantOverride("separation", 12); book.AddChild(top);
        // the way back: the workshop's round icon at the top left, Esc too (Yunke 11-21); Esc first closes an open popover
        if (upgrade is null) { top.AddChild(WorkshopBack.Icon("返回牌组库（Esc）", Leave)); WorkshopBack.Esc(this, Leave, () => { if (popover is null || !IsInstanceValid(popover)) return false; ClosePopover(); return true; }); }
        // a wide search box, its "…" (where to search) at the right end
        var searchBox = new PanelContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill }; searchBox.AddThemeStyleboxOverride("panel", Ui.SoftBox(0.5f, false, 14, 0)); top.AddChild(searchBox);
        var searchRow = new HBoxContainer(); searchRow.AddThemeConstantOverride("separation", 6); searchBox.AddChild(searchRow);
        var searchEdit = new LineEdit { PlaceholderText = "搜索卡牌", Flat = true, SizeFlagsHorizontal = SizeFlags.ExpandFill, CustomMinimumSize = new Vector2(200, 54) }; searchEdit.AddThemeFontSizeOverride("font_size", 22);
        if (Style.Kai is not null) searchEdit.AddThemeFontOverride("font", Style.Kai);
        searchEdit.TextChanged += t => { search = t.Trim(); FillLibrary(true); }; searchRow.AddChild(searchEdit);
        moreButton = Round("…", () => Popover(moreButton, SearchScope()), "搜索范围", 22, 44); moreButton.SizeFlagsVertical = SizeFlags.ShrinkCenter; searchRow.AddChild(moreButton);
        filterButton = Ui.Button("筛选", () => Popover(filterButton, FilterPanel()), 21); filterButton.CustomMinimumSize = new Vector2(96, 54); top.AddChild(filterButton);
        top.AddChild(Toggle("调查员卡", () => onlyInvestigator, v => onlyInvestigator = v));
        top.AddChild(Toggle("弱点卡", () => onlyWeakness, v => onlyWeakness = v));
        signatureButton = Ui.Button("专属与弱点", OpenSignatures, 21); signatureButton.CustomMinimumSize = new Vector2(150, 54); top.AddChild(signatureButton);
        sortButton = Ui.Button("排序", () => Popover(sortButton, SortPanel()), 21); sortButton.CustomMinimumSize = new Vector2(150, 54); top.AddChild(sortButton);
        var second = new HBoxContainer(); second.AddThemeConstantOverride("separation", 8); book.AddChild(second);
        typeTabs = new HBoxContainer(); typeTabs.AddThemeConstantOverride("separation", 6); second.AddChild(typeTabs);
        second.AddChild(ModeSwitch());   // 大卡 · 小卡 · 文字 (DeckEditorScreen.Modes.cs)
        second.AddChild(Ui.Spacer(0, 0, true));
        foreach (var (key, name) in new[] { ("guardian", "守护者"), ("seeker", "探求者"), ("rogue", "流浪者"), ("mystic", "神秘学家"), ("survivor", "生存者"), ("neutral", "中立"), ("multi", "多职业") })
        { var b = ClassIcon(key, name); classButtons[key] = b; second.AddChild(b); }
        RefreshFilterBar();
        var leaf = new PanelContainer { SizeFlagsVertical = SizeFlags.ExpandFill }; leaf.AddThemeStyleboxOverride("panel", Ui.SoftPanel(0.55f, 20, 14)); book.AddChild(leaf); libraryPanel = leaf;
        var leafCol = new VBoxContainer(); leafCol.AddThemeConstantOverride("separation", 6); leaf.AddChild(leafCol);
        libScroll = new ScrollContainer { SizeFlagsVertical = SizeFlags.ExpandFill, HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled, FollowFocus = false };
        leafCol.AddChild(libScroll);
        var centre = new CenterContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill }; libScroll.AddChild(centre);
        var gridPad = new MarginContainer(); gridPad.AddThemeConstantOverride("margin_top", 14); gridPad.AddThemeConstantOverride("margin_bottom", 18); centre.AddChild(gridPad);
        library = new GridContainer { Columns = Columns }; library.AddThemeConstantOverride("h_separation", HGap); library.AddThemeConstantOverride("v_separation", VGap); gridPad.AddChild(library);
        SmoothScroll.Attach(libScroll, 190);
        libScroll.GetVScrollBar().ValueChanged += _ => { MaybeMore(); Cull(); };
        libScroll.Resized += FitCards;
        // how many cards match on the left, the mouse hints on the right
        var libFoot = new HBoxContainer(); libFoot.AddThemeConstantOverride("separation", 10); leafCol.AddChild(libFoot);
        libCount = Style.Text("", 18, Style.PaperDim, Style.Kai); libCount.SizeFlagsHorizontal = SizeFlags.ExpandFill; libCount.VerticalAlignment = VerticalAlignment.Center; libFoot.AddChild(libCount);
        var hint = Style.Text("", 17, new Color(Style.PaperDim, 0.8f), Style.Kai); hint.HorizontalAlignment = HorizontalAlignment.Right; hint.VerticalAlignment = VerticalAlignment.Center; libFoot.AddChild(hint);
        poolHint = hint; ApplyPoolLayout();

        // ───── the deck
        var deck = new PanelContainer(); deck.AddThemeStyleboxOverride("panel", Ui.SoftPanel(0.84f, 16, 14)); AddChild(deck); deckPanel = deck;
        deck.AnchorLeft = 0.755f; deck.AnchorRight = 1; deck.AnchorBottom = 1; deck.OffsetLeft = 10; deck.OffsetRight = -26; deck.OffsetTop = 22; deck.OffsetBottom = -18;
        var dcol = new VBoxContainer(); dcol.AddThemeConstantOverride("separation", 10); deck.AddChild(dcol);
        // the investigator's portrait mini card (whole, its own proportions, as on the table; hover: the text box — Yunke 11-21),
        // the deck's name and the 经验 · 卡组需求 pill
        var head = new HBoxContainer(); head.AddThemeConstantOverride("separation", 12); dcol.AddChild(head);
        portrait = new Control { CustomMinimumSize = PortraitSize, MouseFilter = MouseFilterEnum.Ignore, SizeFlagsVertical = SizeFlags.ShrinkCenter };
        head.AddChild(portrait);
        var headCol = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill }; headCol.AddThemeConstantOverride("separation", 4); head.AddChild(headCol);
        nameEdit = new LineEdit { Flat = true, CustomMinimumSize = new Vector2(0, 40) }; nameEdit.AddThemeFontSizeOverride("font_size", 24); if (Style.Kai is not null) nameEdit.AddThemeFontOverride("font", Style.Kai);
        nameEdit.AddThemeColorOverride("font_color", Style.HoverText);
        nameEdit.TextSubmitted += t => _ = Do(() => BuildDecks.Edit("name", deckId, t));
        // the name as the workshop shows it (a finished campaign's deck without the " 通关" older saves have): saved only when changed
        nameEdit.FocusExited += () => { if (upgrade is null && nameEdit.Text != BuildDecks.ShownName(deckId, J.String(view, "name"))) _ = Do(() => BuildDecks.Edit("name", deckId, nameEdit.Text)); };
        headCol.AddChild(nameEdit);
        var pillBox = new PanelContainer { SizeFlagsHorizontal = SizeFlags.ShrinkBegin, MouseFilter = MouseFilterEnum.Ignore }; pillBox.AddThemeStyleboxOverride("panel", Ui.SoftBox(0.6f, false, 12, 3)); headCol.AddChild(pillBox);
        pill = Style.Text("", 17, Style.Paper, Style.Kai); pillBox.AddChild(pill);
        editState = Style.Text("", 16, Style.PaperDim, Style.Kai); editState.ClipText = true; headCol.AddChild(editState);
        // between scenarios: what happened in play that lowers the price (the table's notes), under the pill
        if (upgrade is not null) { notesBox = new VBoxContainer { Visible = false }; notesBox.AddThemeConstantOverride("separation", 2); dcol.AddChild(notesBox); }
        // which part of the deck is edited (main / side / extra, bonded cards to look at) and the taboo list
        // Yunke 11-22: tab pills like the other tab switches (were a drop-down), wrapping to a second row; the taboo chip last
        deckTabs = new HFlowContainer(); deckTabs.AddThemeConstantOverride("h_separation", 6); deckTabs.AddThemeConstantOverride("v_separation", 6); dcol.AddChild(deckTabs);
        var switchRow = deckTabs;
        // the taboo list: "禁" and its version in a small box (TabooChip; Yunke 11-19: few words)
        tabooButton = Ui.Button("", () => _ = TabooPopover(), 19); tabooButton.CustomMinimumSize = new Vector2(96, 44); tabooButton.TooltipText = "禁卡表"; switchRow.AddChild(tabooButton);
        tabooFace = new HBoxContainer { Alignment = BoxContainer.AlignmentMode.Center, MouseFilter = MouseFilterEnum.Ignore }; tabooFace.AddThemeConstantOverride("separation", 6);
        tabooButton.AddChild(tabooFace); tabooFace.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        deckList = new VBoxContainer(); deckList.AddThemeConstantOverride("separation", 3);
        var scroll = Ui.Scroll(deckList); scroll.SizeFlagsVertical = SizeFlags.ExpandFill; dcol.AddChild(scroll); SmoothScroll.Attach(scroll, 110);
        problems = new VBoxContainer(); problems.AddThemeConstantOverride("separation", 2); dcol.AddChild(problems);
        var foot = new HBoxContainer(); foot.AddThemeConstantOverride("separation", 8); dcol.AddChild(foot);
        var reset = Ui.Button("重置", () => _ = Do(async () => { await BuildDecks.Discard(deckId); App.I.Toast("已回到上次保存的样子"); }, true), 19); reset.CustomMinimumSize = new Vector2(84, 54); foot.AddChild(reset);
        if (upgrade is not null) { var keep = Ui.Button("保持不变", () => _ = FinishUpgrade(false), 19); keep.CustomMinimumSize = new Vector2(110, 54); foot.AddChild(keep); }
        var save = upgrade is null ? Ui.Button("保存并确定", () => _ = SaveAndLeave(), 23, true) : Ui.Button("完成升级", () => _ = FinishUpgrade(true), 23, true);
        save.CustomMinimumSize = new Vector2(0, 54); save.SizeFlagsHorizontal = SizeFlags.ExpandFill; foot.AddChild(save);

        QaScript.Register("ed-pop", a => { switch (a) { case "filter": Popover(filterButton, FilterPanel()); break; case "sort": Popover(sortButton, SortPanel()); break; case "search": Popover(moreButton, SearchScope()); break; case "taboo": _ = TabooPopover(); break; default: ClosePopover(); break; } });
        QaScript.Register("ed-class", a => { if (classButtons.TryGetValue(a, out var b)) b.EmitSignal(BaseButton.SignalName.Pressed); });
        QaScript.Register("ed-type", a => { type = a; RefreshFilterBar(); FillLibrary(true); });
        QaScript.Register("ed-search", a => { search = a; FillLibrary(true); });
        QaScript.Register("ed-scroll", a => { if (double.TryParse(a, out double y)) SmoothScroll.Of(libScroll)?.ScrollTo(y); });
        QaScript.Register("ed-click-deck", a => _ = TileClicked(a));
        // QA (round 10): build's config actions as the 牌组设置 panel sends them, and the card window
        QaScript.Register("ed-taboo", async a => await Do(() => BuildDecks.Edit("taboo", deckId, int.TryParse(a, out int t) ? t : null), true));
        QaScript.Register("ed-side", async a => { var p = a.Split(','); await Do(() => BuildDecks.Edit("side", deckId, p[0], p[1]), true); });
        QaScript.Register("ed-custom", async a => { var p = a.Split(','); await Do(() => BuildDecks.Edit("customization", deckId, p[0], int.Parse(p[1]), "{\"xp_spent\":" + int.Parse(p[2]) + "}"), true); });
        QaScript.Register("ed-budget", async a => await Do(() => BuildDecks.Edit("meta", deckId, StandaloneDecks.BudgetKey, a, null)));
        QaScript.Register("ed-card", a => _ = CardDialog(a));
        // QA: wheel notches, then frames while the scroll eases (…-0.05s, -0.15s, -0.30s, -0.80s.png)
        QaScript.Register("ed-wheel-shots", async a => { var p = a.Split(','); var at = new Vector2(float.Parse(p[0]), float.Parse(p[1])); int n = int.Parse(p[2]);
            for (int i = 0; i < Math.Abs(n); i++) { var b = n > 0 ? MouseButton.WheelDown : MouseButton.WheelUp; GetViewport().PushInput(new InputEventMouseButton { Position = at, GlobalPosition = at, ButtonIndex = b, Pressed = true, Factor = 1 }); GetViewport().PushInput(new InputEventMouseButton { Position = at, GlobalPosition = at, ButtonIndex = b, Pressed = false, Factor = 1 }); }
            double t = 0; foreach (var at2 in new[] { 0.05, 0.15, 0.30, 0.80 }) { await ToSignal(GetTree().CreateTimer(at2 - t), SceneTreeTimer.SignalName.Timeout); t = at2; App.I.Capture(System.IO.Path.GetFullPath(System.IO.Path.Combine(App.I.Root, p[3] + "-" + at2.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture) + "s.png"))); GD.Print("SMOOTH frame " + at2 + " y=" + libScroll.ScrollVertical); } });
        // QA: real wheel notches through the viewport at x,y (down when n > 0), as a mouse sends them
        QaScript.Register("ed-wheel", a => { var p = a.Split(','); var at = new Vector2(float.Parse(p[0]), float.Parse(p[1])); int n = int.Parse(p[2]);
            for (int i = 0; i < Math.Abs(n); i++) { var b = n > 0 ? MouseButton.WheelDown : MouseButton.WheelUp; GetViewport().PushInput(new InputEventMouseButton { Position = at, GlobalPosition = at, ButtonIndex = b, Pressed = true, Factor = 1 }); GetViewport().PushInput(new InputEventMouseButton { Position = at, GlobalPosition = at, ButtonIndex = b, Pressed = false, Factor = 1 }); } });
        QaScript.Register("ed-upgrade", a => { var p = a.Split(','); if (p.Length == 3) _ = ApplyUpgrade(p[0], p[1], int.Parse(p[2]), CardName(p[1]), 0); });
        QaScript.Register("ed-pack", a => { foreach (var k in a.Split(',', StringSplitOptions.RemoveEmptyEntries)) if (!packFilter.Add(k)) packFilter.Remove(k); Refilter(); });
        QaScript.Register("ed-add", code => _ = Add(code, null));
        QaScript.Register("ed-signatures", _ => OpenSignatures());
        QaScript.Register("ed-remove", code => _ = Remove(code));
        QaScript.Register("ed-save", a => { _ = Save(); });   // not awaited: it may wait on the 不符合构筑规则 dialog (ed-dialog)
        QaScript.Register("ed-dialog", t => { if (App.I.GetTree().Root.FindChildren("*", "Button", true, false).OfType<Button>().LastOrDefault(b => b.IsVisibleInTree() && b.Text == t) is { } hit) hit.EmitSignal(BaseButton.SignalName.Pressed); else GD.PrintErr("QA no dialog button " + t); });
        QaScript.Register("ed-click", a => { if (int.TryParse(a, out int i) && i >= 0 && i < library.GetChildCount() && PoolCard(i) is { } cv) cv.OnClick?.Invoke(); });
        QaScript.Register("ed-tab", a => { tab = a; RefreshDeck(); });
        QaScript.Register("ed-upgrade-hover", a => { if (upgradeButtons.TryGetValue(a, out var ub) && IsInstanceValid(ub)) ub.EmitSignal(Control.SignalName.MouseEntered); });
        QaScript.Register("ed-deck-hover", a => { if (tiles.TryGetValue(a, out var dt) && IsInstanceValid(dt)) dt.EmitSignal(Control.SignalName.MouseEntered); });
        QaScript.Register("ed-upgrade-click", a => { if (upgradeButtons.TryGetValue(a, out var ub) && IsInstanceValid(ub)) { ub.EmitSignal(BaseButton.SignalName.Pressed); ub.EmitSignal(Control.SignalName.MouseExited); } });   // as the pointer does: the window opens, the button loses the pointer
        QaScript.Register("ed-deckswitch", a => { if (int.TryParse(a, out int i) && i < deckTabKeys.Count) SwitchTab(deckTabKeys[i]); });
        // QA: which controls in the deck column are wider than it (a too-wide one pushes the column off screen)
        QaScript.Register("ed-widths", _ => { void Walk(Node n, int d) { if (n is Control c && c.GetCombinedMinimumSize().X > 330) GD.Print("QA_W " + new string(' ', d) + c.GetType().Name + " " + c.Name + " min=" + c.GetCombinedMinimumSize().X + (c is Label l ? " '" + l.Text[..Math.Min(20, l.Text.Length)] + "'" : c is Button bb ? " '" + bb.Text + "'" : "")); foreach (var ch in n.GetChildren()) Walk(ch, d + 1); } Walk(deckPanel, 0); });
        QaScript.Register("ed-weakness", async _ => await DrawWeakness());
        // QA (r13 campaign check): a real right button press + release through the viewport at x,y, as a mouse sends it;
        // ed-tiles prints where the deck rows and the pool cards are, to aim it
        QaScript.Register("ed-rclick", async a =>
        {
            var p = a.Split(','); var at = new Vector2(float.Parse(p[0]), float.Parse(p[1])); var vp = GetViewport();
            vp.PushInput(new InputEventMouseMotion { Position = at, GlobalPosition = at });
            await ToSignal(GetTree().CreateTimer(0.1), SceneTreeTimer.SignalName.Timeout);
            vp.PushInput(new InputEventMouseButton { Position = at, GlobalPosition = at, ButtonIndex = MouseButton.Right, Pressed = true, ButtonMask = MouseButtonMask.Right });
            await ToSignal(GetTree().CreateTimer(0.1), SceneTreeTimer.SignalName.Timeout);
            vp.PushInput(new InputEventMouseButton { Position = at, GlobalPosition = at, ButtonIndex = MouseButton.Right, Pressed = false });
            await ToSignal(GetTree().CreateTimer(0.3), SceneTreeTimer.SignalName.Timeout);
            GD.Print("QA_RCLICK " + a + " modals=" + string.Join(",", GetTree().Root.FindChildren("*", "", true, false).OfType<Control>().Where(c => c.ZIndex >= 300 && c.IsVisibleInTree()).Select(c => c.GetType().Name + ":" + c.Name).Take(6)));
        });
        QaScript.Register("ed-tiles", _ =>
        {
            foreach (var (c, t) in tiles) if (IsInstanceValid(t) && t.IsVisibleInTree()) GD.Print("QA_TILE deck " + c + " " + CardName(c) + " " + t.GetGlobalRect());
            for (int i = 0; i < library.GetChildCount() && i < 12; i++) if (library.GetChild(i) is Control lc && PoolCard(i) is { } cv) GD.Print("QA_TILE pool " + i + " " + cv.Code + " " + CardName(cv.Code) + " " + lc.GetGlobalRect());
        });
        // QA: the deck-list strips of the given cards in a window (cards in no sample deck, to check art-crops.json's strips)
        QaScript.Register("ed-strips", a =>
        {
            var want = a.Split(',').Select(s => s.Trim()).Where(s => s.Length > 0).ToList();
            using var doc = JsonDocument.Parse(System.IO.File.ReadAllText(System.IO.Path.Combine(App.I.Assets, "build-data", "cards-zh-cn.json")));
            var byCode = new Dictionary<string, JsonElement>();
            foreach (var c in doc.RootElement.GetProperty("data").GetProperty("all_card").EnumerateArray())
                if (want.Contains(J.String(c, "code"))) byCode.TryAdd(J.String(c, "code"), c.Clone());
            var grid = new GridContainer { Columns = 2 }; grid.AddThemeConstantOverride("h_separation", 18); grid.AddThemeConstantOverride("v_separation", 8);
            var loads = new List<(string, string, Action<Texture2D?>)>();
            foreach (var code in want)
            {
                if (!byCode.TryGetValue(code, out var c)) { GD.Print("QA_STRIP missing " + code); continue; }
                var row = new HBoxContainer(); row.AddThemeConstantOverride("separation", 8);
                row.AddChild(Style.Text(code, 15, Style.PaperDim)); row.AddChild(DeckPreview.Strip(c, 640, loads));
                grid.AddChild(row);
            }
            App.I.Modal(Ui.Panel(grid, 18));
            foreach (var (code, type, set) in loads) set(DeckPreview.Art(code, type));
        });
        // QA (Yunke 11-21): how many pool cards are live, and a real drag through the viewport: ed-drag:x0,y0,x1,y1[,shot]
        QaScript.Register("ed-hover", a => { var p = a.Split(','); var at = new Vector2(float.Parse(p[0]), float.Parse(p[1])); Input.WarpMouse(at); GetViewport().PushInput(new InputEventMouseMotion { Position = at, GlobalPosition = at }); });
        QaScript.Register("ed-scroll-through", async a =>
        {
            int steps = int.Parse(a); var bar = libScroll.GetVScrollBar(); ulong t0 = Time.GetTicksMsec(); int frames = 0; ulong worst = 0;
            for (int i = 0; i < steps; i++)
            {
                libScroll.ScrollVertical = (int)(bar.Value + 260);
                qaCullMs = qaProcMs = 0; int s0 = slots.Count; ulong f0 = Time.GetTicksUsec(); await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame); ulong df = Time.GetTicksUsec() - f0; worst = Math.Max(worst, df); frames++;
                if (df > 30000) GD.Print("QA_POOL slow step=" + i + " ms=" + df / 1000 + " cull=" + qaCullMs.ToString("F1") + " proc=" + qaProcMs.ToString("F1") + " slots " + s0 + "->" + slots.Count + " live=" + slots.Count(x => IsInstanceValid(x.Slot) && x.Slot.GetChildCount() > 0));
                if (i % 40 == 0) GD.Print("QA_POOL step=" + i + " y=" + libScroll.ScrollVertical + " slots=" + slots.Count + " live=" + slots.Count(x => IsInstanceValid(x.Slot) && x.Slot.GetChildCount() > 0) + " nodes=" + GetTree().GetNodeCount());
            }
            GD.Print("QA_POOL through steps=" + steps + " ms=" + (Time.GetTicksMsec() - t0) + " worstFrameMs=" + worst / 1000 + " slots=" + slots.Count + " live=" + slots.Count(x => IsInstanceValid(x.Slot) && x.Slot.GetChildCount() > 0) + " nodes=" + GetTree().GetNodeCount());
        });
        QaScript.Register("ed-pool-stats", _ => GD.Print("QA_POOL slots=" + slots.Count + " live=" + slots.Count(x => IsInstanceValid(x.Slot) && x.Slot.GetChildCount() > 0) + " shown=" + shown + " nodes=" + GetTree().GetNodeCount()));
        QaScript.Register("ed-drag", async a =>
        {
            var p = a.Split(','); var from = new Vector2(float.Parse(p[0]), float.Parse(p[1])); var to = new Vector2(float.Parse(p[2]), float.Parse(p[3])); var vp = GetViewport();
            vp.PushInput(new InputEventMouseMotion { Position = from, GlobalPosition = from }); Input.WarpMouse(from);
            await ToSignal(GetTree().CreateTimer(0.1), SceneTreeTimer.SignalName.Timeout);
            vp.PushInput(new InputEventMouseButton { Position = from, GlobalPosition = from, ButtonIndex = MouseButton.Left, Pressed = true, ButtonMask = MouseButtonMask.Left });
            for (int i = 1; i <= 24; i++)
            {
                var at = from.Lerp(to, i / 24f); Input.WarpMouse(at);
                vp.PushInput(new InputEventMouseMotion { Position = at, GlobalPosition = at, ButtonMask = MouseButtonMask.Left, Relative = (to - from) / 24f });
                await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            }
            await ToSignal(GetTree().CreateTimer(0.2), SceneTreeTimer.SignalName.Timeout);
            if (p.Length > 4) App.I.Capture(System.IO.Path.GetFullPath(System.IO.Path.Combine(App.I.Root, p[4])));
            vp.PushInput(new InputEventMouseButton { Position = to, GlobalPosition = to, ButtonIndex = MouseButton.Left, Pressed = false });
            await ToSignal(GetTree().CreateTimer(0.6), SceneTreeTimer.SignalName.Timeout);
            GD.Print("QA_DRAG done " + a + " deck=" + J.Int(J.Get(view, "stats"), "deckSize"));
        });
        QaScript.Register("ed-finish", async _ => await FinishUpgrade(true));
        QaScript.Register("ed-keep", async _ => await FinishUpgrade(false));
        QaScript.Register("ed-finish-force", async _ => { qaForceSend = true; await FinishUpgrade(true); });
        _ = Load();
    }

    async Task Load()
    {
        // the screen can be replaced while build answers (a campaign upgrade opened again, Yunke r12: "Cannot access a disposed
        // object … TextureRect"): after every wait, stop quietly when this screen is gone
        bool Gone() => !IsInstanceValid(this) || !IsInsideTree();
        Loading(true);
        try
        {
            if (App.I.QaRunning) GD.Print("QA_LOAD start delay=" + QaLoadDelay + " deck=" + deckId);
            if (QaLoadDelay > 0) { await ToSignal(GetTree().CreateTimer(QaLoadDelay), SceneTreeTimer.SignalName.Timeout); if (Gone()) return; }   // QA: a slow load
            if (upgrade is not null && deckId.Length == 0) { deckId = await BeginUpgrade(); if (Gone()) return; }
            view = await BuildDecks.View(deckId); if (Gone()) return;
            if (view.ValueKind != JsonValueKind.Object) { App.I.Toast("找不到这个卡组", true); if (upgrade is null) App.I.Go(new DeckLibraryScreen()); return; }
            var acc = await BuildDecks.Access(deckId); if (Gone()) return;
            pool = acc.EnumerateArray().ToList();
            string inv = J.String(view, "investigator_front", J.String(view, "investigator"));
            Ui.Clear(portrait);
            var face = CardView.PortraitCard(inv, PortraitSize.Y); face.HoverZoom = true;   // the table's text box on hover (Peek, its delay and mode)
            portrait.CustomMinimumSize = face.CustomMinimumSize; portrait.AddChild(face);
            nameEdit.PlaceholderText = J.String(J.Get(view, "investigatorCard"), "name") + "的卡组";
            nameEdit.Text = BuildDecks.ShownName(deckId, J.String(view, "name")); nameEdit.Editable = upgrade is null;
            await LoadPacks(); if (Gone()) return;
            Loading(false);
            if (App.I.QaRunning) GD.Print("QA_LOAD done deck=" + deckId);
            FillLibrary(); RefreshDeck();
        }
        catch (ObjectDisposedException) { }   // the screen went away mid-load: nothing to show
        catch (Exception e) { if (!Gone()) { Loading(false); App.I.Toast("读取失败：" + e.Message, true); } }
    }

    // while build reads the deck: a line in the middle of the card pool and of the deck column instead of two empty panels
    public static double QaLoadDelay;   // QA only (ed-load-delay): the screen as it looks while build is slow to answer
    readonly List<Control> loadingMarks = new();
    void Loading(bool on)
    {
        foreach (var l in loadingMarks) if (IsInstanceValid(l)) l.QueueFree();
        loadingMarks.Clear();
        if (!on) return;
        libCount.Text = "正在读取卡组…";
        foreach (var host in new Control[] { libraryPanel, deckPanel })
        {
            var l = Style.Text(upgrade is not null ? "正在读取卡组与可用经验…" : "正在读取卡组…", 22, Style.PaperDim, Style.Kai);
            l.HorizontalAlignment = HorizontalAlignment.Center; l.VerticalAlignment = VerticalAlignment.Center; l.MouseFilter = MouseFilterEnum.Ignore;
            host.AddChild(l);   // a panel container lays it over the whole panel, the text in its middle
            loadingMarks.Add(l);
        }
    }

    // run an edit, then show build's new view of the deck (and the access list, which can change with the deck)
    async Task Do(Func<Task> edit, bool reaccess = false)
    {
        if (busy) return; busy = true;
        try
        {
            await edit();
            view = await BuildDecks.View(deckId);
            if (reaccess) pool = (await BuildDecks.Access(deckId)).EnumerateArray().ToList();
            RefreshDeck(); RefreshLibrary();
        }
        catch (Exception e) { App.I.Toast("操作失败：" + e.Message, true); }
        finally { busy = false; }
    }

    // a reprint in the deck (新核心 01686 for 06279) counts as the original the library shows (build engine: printingIn)
    int InDeck(string code, string slot = "slots") => J.Items(J.Get(view, slot)).Where(c => J.String(c, "code") == code || J.String(c, "duplicate_of_code") == code).Sum(c => J.Int(c, "quantity"));
    static int Limit(JsonElement c) => J.Get(c, "deck_limit").ValueKind == JsonValueKind.Number ? J.Int(c, "deck_limit") : 2;

    async Task Add(string code, Rect2? from)
    {
        var card = pool.FirstOrDefault(c => J.String(c, "code") == code);
        int lim = card.ValueKind == JsonValueKind.Object ? Limit(card) : 2;
        string slot = tab is "sideSlots" or "extraSlots" ? tab : "slots";
        if (InDeck(code, slot) >= lim) { Shake(code); App.I.Toast("同名卡最多 " + lim + " 张"); return; }
        bool isNew = InDeck(code, slot) == 0;
        from ??= pageCards.TryGetValue(code, out var cv) && IsInstanceValid(cv) ? cv.GetGlobalRect() : null;
        await Do(() => BuildDecks.Edit("quantity", deckId, code, 1, lim, slot, "increment"), true);
        if (from is Rect2 r) _ = FlyIn(code, r, isNew);
    }

    async Task Remove(string code)
    {
        Rect2? at = tiles.TryGetValue(code, out var t) && IsInstanceValid(t) ? t.GetGlobalRect() : null;
        string slot = tab is "sideSlots" or "extraSlots" or "bonded" ? (tab == "bonded" ? "slots" : tab) : "slots";
        if (tab == "bonded") return;   // bonded cards follow their parents
        HidePreview();
        await Do(() => BuildDecks.Edit("quantity", deckId, code, -1, 99, slot, "increment"), true);
        if (at is Rect2 r) FlyOut(code, r);
    }

    const string RandomWeakness = "01000";
    async Task DrawWeakness()
    {
        string name = "";
        await Do(async () =>
        {
            // only weaknesses the rules core can play: the same pool a game start draws from (StandaloneDecks.WeaknessPool —
            // no class-restricted ones like 09124–09128, which make the core refuse the deck: unsupportedCardRestriction)
            var corePool = JsonSerializer.Serialize(StandaloneDecks.WeaknessPool(App.I.S!.Catalog));
            using var doc = JsonDocument.Parse(await BuildEngine.I.Call("decks.randomWeakness", deckId, corePool));
            if (App.I.QaRunning) GD.Print("QA_WEAKNESS " + J.String(doc.RootElement, "code") + " " + J.String(doc.RootElement, "name") + " pool=" + StandaloneDecks.WeaknessPool(App.I.S!.Catalog).Length);
            name = J.String(doc.RootElement, "name");
        }, true);
        if (name.Length > 0) App.I.Toast(BuildDecks.Word("deck_edit.actions.draw_random_basic_weakness_success", "<strong>{{name}}</strong> 是你抽到的随机基础弱点卡。").Replace("<strong>", "").Replace("</strong>", "").Replace("{{name}}", name));
    }

    async Task Save()
    {
        try { if (await SaveChecked() is not null) { view = await BuildDecks.View(deckId); RefreshDeck(); } }
        catch (Exception e) { App.I.Toast("保存失败：" + e.Message, true); }
    }

    async Task SaveAndLeave()
    {
        try { if (await SaveChecked() is true) App.I.Go(new DeckLibraryScreen()); }
        catch (Exception e) { App.I.Toast("保存失败：" + e.Message, true); }
    }

    // Saving tells what is wrong first (bug #37: two copies of a card and two of its upgrade — four of one name — were saved
    // without a word and refused only at a game's start). Before: build's verdict, in its own Chinese words; the player may
    // keep editing or save anyway (a deck can be saved half built). After: the rules core's check of the deck as a campaign's
    // start takes it (PrototypeBackend.ValidateDeck, not standalone: the workshop has no 50 experience limit, Yunke 11-23).
    // null: not saved; true: saved and the core takes it; false: saved, but the core refuses it (the editor stays open).
    async Task<bool?> SaveChecked()
    {
        var val = J.Get(view, "validation");
        var problems = J.Get(val, "valid").ValueKind == JsonValueKind.True ? new List<string>()
            : J.Items(J.Get(val, "errors")).Select(e => BuildDecks.ErrorText(e, CardName, DeckInvestigator)).Where(t => t.Length > 0).Distinct().ToList();
        if (J.Get(val, "valid").ValueKind != JsonValueKind.True && problems.Count == 0) problems.Add("牌组不符合构筑规则");
        if (problems.Count > 0 && !await AskSaveAnyway(problems)) return null;
        await BuildDecks.Save(deckId);
        if (problems.Count > 0) { App.I.Toast("卡组已保存（不符合构筑规则，开局时会被拦下）", true, 5); return true; }
        string? core = null;
        try
        {
            var S = App.I.S!;
            if (S.Backend is { } backend && S.Decks.Has(deckId))
            {
                var prepared = await backend.PrepareDeck(S.Decks.Get(deckId), false);
                string inv = J.String(prepared, "investigator_code");
                var schema = await backend.BuildSchema(inv);
                var result = await backend.ValidateDeck(inv, J.String(schema, "schemaId"), prepared, false);
                if (J.String(result, "status") != "valid") core = PrototypeBackend.Verdict(result);
            }
        }
        catch (Exception e) { core = e.Message; }
        if (App.I.QaRunning) GD.Print("QA_SAVE_CHECK build=" + problems.Count + " core=" + (core ?? "valid"));
        if (core is null) { App.I.Toast("卡组已保存"); return true; }
        App.I.Toast("卡组已保存，但开局时会被拦下：" + core, true, 8);
        return false;
    }

    Task<bool> AskSaveAnyway(List<string> problems)
    {
        var done = new TaskCompletionSource<bool>();
        var col = new VBoxContainer { CustomMinimumSize = new Vector2(620, 0) }; col.AddThemeConstantOverride("separation", 14);
        col.AddChild(Style.Text("这副牌不符合构筑规则", 26, Style.HoverText, Style.Kai));
        foreach (var p in problems.Take(6))
        {
            var l = Style.Text(p, 19, Style.Danger, Style.Kai); l.AutowrapMode = TextServer.AutowrapMode.WordSmart; l.CustomMinimumSize = new Vector2(600, 0); col.AddChild(l);
        }
        var note = Style.Text("可以先保存、之后再改，但这样的牌组开局时会被拦下。", 18, Style.PaperDim, Style.Kai); note.AutowrapMode = TextServer.AutowrapMode.WordSmart; note.CustomMinimumSize = new Vector2(600, 0); col.AddChild(note);
        var row = new HBoxContainer { Alignment = BoxContainer.AlignmentMode.End }; row.AddThemeConstantOverride("separation", 10); col.AddChild(row);
        Control scrim = null!;
        row.AddChild(Ui.Button("仍然保存", () => { done.TrySetResult(true); App.I.CloseModal(scrim); }, 20));
        row.AddChild(Ui.Button("继续编辑", () => { done.TrySetResult(false); App.I.CloseModal(scrim); }, 20, true));
        scrim = App.I.Modal(Ui.Panel(col, 34));
        scrim.TreeExiting += () => done.TrySetResult(false);
        return done.Task;
    }

    void Leave()
    {
        if (J.Get(view, "edited").ValueKind != JsonValueKind.True) { App.I.Go(new DeckLibraryScreen()); return; }
        var col = new VBoxContainer { CustomMinimumSize = new Vector2(560, 0) }; col.AddThemeConstantOverride("separation", 20);
        col.AddChild(Style.Text("这副牌有未保存的修改。", 24, Style.Paper, Style.Kai));
        var row = new HBoxContainer { Alignment = BoxContainer.AlignmentMode.End }; row.AddThemeConstantOverride("separation", 10); col.AddChild(row);
        Control scrim = null!;
        row.AddChild(Ui.Button("取消", () => App.I.CloseModal(scrim), 20));
        row.AddChild(Ui.Button("放弃修改", async () => { App.I.CloseModal(scrim); await BuildDecks.Discard(deckId); App.I.Go(new DeckLibraryScreen()); }, 20));
        row.AddChild(Ui.Button("保存并返回", async () => { App.I.CloseModal(scrim); await SaveAndLeave(); }, 20, true));
        scrim = App.I.Modal(Ui.Panel(col, 34));
    }

    // ───────── the book
    static bool Calm => App.I.S?.Settings.ReducedMotion == true;
    static int Cost(JsonElement c) => J.Get(c, "cost").ValueKind == JsonValueKind.Number ? J.Int(c, "cost") : -1;
    static int SortCost(JsonElement c) { int k = Cost(c); return k < 0 ? 99 : k; }

    // the book from the top (new filters) or as it stands (fade = false keeps the rows already made)
    void FillLibrary(bool fade = false)
    {
        filtered = Filtered();
        libCount.Text = "共 " + filtered.Length + " 种" + (filtered.Length == 0 && pool.Count > 0 ? " · 没有符合筛选的卡牌" : "");
        int keep = fade ? 0 : shown;
        Ui.Clear(library); pageCards.Clear(); plaques.Clear(); slots.Clear(); realizeQueue.Clear(); waited.Clear(); poolRows.Clear(); shown = 0;
        if (fade) SmoothScroll.Of(libScroll)?.Jump(0);
        Append(Math.Max(keep, Columns * FirstRows));
        if (fade && !Calm) { library.Modulate = new Color(1, 1, 1, 0); library.CreateTween().TweenProperty(library, "modulate:a", 1f, 0.18f); }
    }

    // after an edit: the same cards → only the counts under them change; otherwise the book is made again where it was
    void RefreshLibrary()
    {
        var now = Filtered();
        if (now.Length != filtered.Length || now.Where((c, i) => J.String(c, "code") != J.String(filtered[i], "code")).Any()) { FillLibrary(); return; }
        filtered = now;
        foreach (var (code, p) in plaques) Plaque(code, p.Card, p.Count, p.Plaque);
    }

    // more rows as the scroll nears the end
    void MaybeMore()
    {
        if (shown >= filtered.Length) return;
        var bar = libScroll.GetVScrollBar();
        if (bar.Value + bar.Page > bar.MaxValue - ItemHeight * 1.6f) Append(Columns * MoreRows);
    }

    // the card size (or the text rows' width) from the book's size, per mode (DeckEditorScreen.Modes.cs FitPool)
    void FitCards()
    {
        if (!FitPool()) return;
        if (filtered.Length > 0) FillLibrary();
    }

    void Append(int n)
    {
        int end = Math.Min(filtered.Length, shown + n);
        string slot = tab is "sideSlots" or "extraSlots" ? tab : "slots";
        for (; shown < end; shown++)
        {
            var c = filtered[shown];
            string code = J.String(c, "code");
            if (poolMode == PoolMode.Text) { AppendRow(code, c); continue; }   // a row of the text list (DeckEditorScreen.Modes.cs)
            var tile = new VBoxContainer { MouseFilter = MouseFilterEnum.Ignore }; tile.AddThemeConstantOverride("separation", 6);
            // the card's place; the card itself is made only while it is near the view (DeckEditorScreen.Pool.cs)
            var place = new Control { CustomMinimumSize = CardSize, MouseFilter = MouseFilterEnum.Ignore };
            tile.AddChild(place); slots.Add((code, c, place));
            var plaque = new PanelContainer { SizeFlagsHorizontal = SizeFlags.ShrinkCenter, MouseFilter = MouseFilterEnum.Ignore };
            var n2 = Style.Text("", poolMode == PoolMode.Small ? 16 : 20, Style.PaperDim, Style.Sans); n2.HorizontalAlignment = HorizontalAlignment.Center; plaque.AddChild(n2);
            Plaque(code, c, n2, plaque);
            plaques[code] = (n2, plaque, c);
            tile.AddChild(plaque); library.AddChild(tile);
        }
        cullIn = 2;   // once the grid has laid the new rows out
    }

    // "1 / 2 ••" under a card: how many are in the deck part being edited, its limit and level; a full card is dimmed
    void Plaque(string code, JsonElement c, Label n, PanelContainer plaque)
    {
        string slot = tab is "sideSlots" or "extraSlots" ? tab : "slots";
        int have = InDeck(code, slot), lim = Limit(c), lv = Xp(c);
        n.Text = have + " / " + lim + (lv > 0 ? "  " + new string('•', Math.Min(5, lv)) : "");
        n.AddThemeColorOverride("font_color", have > 0 ? Style.HoverText : Style.PaperDim);
        plaque.AddThemeStyleboxOverride("panel", Ui.SoftBox(have > 0 ? 0.7f : 0.4f, have > 0, 14, 2));
        if (pageCards.TryGetValue(code, out var cv) && IsInstanceValid(cv)) cv.Modulate = have >= lim ? new Color(0.55f, 0.55f, 0.55f) : Colors.White;
        if (poolRows.TryGetValue(code, out var pr) && IsInstanceValid(pr)) pr.Modulate = have >= lim ? new Color(0.62f, 0.62f, 0.62f) : Colors.White;
    }

    // a printed card's picture clipped to the card's own rounded corners, the same radius as the frame drawn over it
    // (Yunke 11-19 #24: the corners showed square edges outside the rounded frame)
    static readonly Dictionary<(int, int, int), ShaderMaterial> cornerMats = new();
    static Shader? cornerShader;
    public static void RoundCorners(CardView cv)
    {
        int radius = (int)Math.Clamp(cv.Size.X * 0.075f, 5, 12);   // CardView's own frame radius
        var key = ((int)cv.Size.X, (int)cv.Size.Y, radius);
        if (!cornerMats.TryGetValue(key, out var mat))
        {
            cornerShader ??= new Shader { Code = @"shader_type canvas_item;
uniform vec2 box = vec2(270.0, 377.0);
uniform float radius = 12.0;
void fragment() {
    vec2 p = UV * box;
    vec2 q = abs(p - box * 0.5) - (box * 0.5 - vec2(radius));
    float d = length(max(q, vec2(0.0))) + min(max(q.x, q.y), 0.0) - radius;
    vec4 c = texture(TEXTURE, UV);
    c.a *= 1.0 - smoothstep(-1.2, 0.4, d);
    COLOR = c * COLOR;
}" };
            mat = new ShaderMaterial { Shader = cornerShader };
            mat.SetShaderParameter("box", new Vector2(key.Item1, key.Item2)); mat.SetShaderParameter("radius", (float)radius);
            cornerMats[key] = mat;
        }
        foreach (var child in cv.GetChildren())
            if (child is TextureRect tr) { tr.StretchMode = TextureRect.StretchModeEnum.Scale; tr.Material = mat; }
    }

    void Shake(string code)
    {
        if (Calm || !pageCards.TryGetValue(code, out var cv) || !IsInstanceValid(cv)) return;
        float x = cv.Position.X; var tw = cv.CreateTween();
        foreach (var dx in new[] { -8f, 8f, -5f, 5f, 0f }) tw.TweenProperty(cv, "position:x", x + dx, 0.04f);
    }

    // ───────── the deck
    void SwitchTab(string key)
    {
        if (key == tab) { RefreshDeck(); return; }   // keeps the pill lit (a toggle pressed again would go dark)
        tab = key; RefreshDeck(); FillLibrary(); WorkshopFade.In(deckList, 0.18f);
    }

    void RefreshDeck()
    {
        if (view.ValueKind != JsonValueKind.Object) return;
        // the deck tabs: main and side deck always, the extra deck / bonded cards when there are any, then build's config tab
        // and its deck tools (charts, draw simulator; DeckEditorScreen.Config.cs)
        foreach (var c in deckTabs.GetChildren()) if (c != tabooButton) { deckTabs.RemoveChild(c); c.QueueFree(); }
        deckTabKeys.Clear();
        foreach (var (key, label) in new[] { ("slots", "主卡组"), ("sideSlots", "侧卡组"), ("extraSlots", "额外卡组"), ("bonded", "附属卡"), ("config", "牌组设置"), ("stats", "牌组统计") })
        {
            string text = label;
            if (key is not ("config" or "stats"))
            {
                int n = J.Items(J.Get(view, key)).Sum(c => J.Int(c, "quantity"));
                if (key is "extraSlots" or "bonded" && n == 0 && tab != key) continue;
                text += "（" + n + "）";
            }
            var k = key; var b = Pill(text, key == tab, () => SwitchTab(k), 16);
            foreach (var st in new[] { "normal", "hover", "pressed", "hover_pressed" }) if (b.GetThemeStylebox(st) is StyleBox sb) { sb.ContentMarginLeft = 10; sb.ContentMarginRight = 10; }
            b.CustomMinimumSize = new Vector2(0, 44);
            deckTabs.AddChild(b); deckTabKeys.Add(key);
        }
        deckTabs.MoveChild(tabooButton, deckTabs.GetChildCount() - 1);
        _ = RefreshTabooFace();
        Ui.Clear(deckList); tiles.Clear();
        if (tab is "config" or "stats") { deckList.AddChild(tab == "config" ? ConfigPanel() : StatsPanel()); RefreshVerdict(); return; }
        var cards = J.Items(J.Get(view, tab)).ToList();
        bool fixedTab = tab == "bonded";
        // grouped by type, each group under its name and count with a thin rule; weaknesses last
        foreach (var (title, pick) in new (string, Func<JsonElement, bool>)[] {
            ("支援", c => J.String(c, "type_code") == "asset"), ("事件", c => J.String(c, "type_code") == "event"), ("技能", c => J.String(c, "type_code") == "skill"),
            ("其他", c => J.String(c, "type_code") is not ("asset" or "event" or "skill")) })
        {
            var group = cards.Where(c => J.String(c, "subtype_code").Length == 0 && pick(c)).OrderBy(SortCost).ThenBy(c => J.String(c, "name")).ToList();
            if (group.Count == 0) continue;
            deckList.AddChild(GroupHeader(title, group.Sum(c => J.Int(c, "quantity"))));
            foreach (var c in group) deckList.AddChild(Tile(c, !fixedTab));
        }
        var weak = cards.Where(c => J.String(c, "subtype_code").Length > 0).ToList();
        if (weak.Count > 0)
        {
            deckList.AddChild(GroupHeader("弱点", weak.Sum(c => J.Int(c, "quantity"))));
            foreach (var c in weak) deckList.AddChild(Tile(c, !fixedTab));
        }
        _ = MarkUpgrades();
        if (cards.Count == 0) { var e = Style.Text(tab == "sideSlots" ? "侧卡组是空的（编辑侧卡组时，单击左侧卡牌加入）" : "单击左侧的卡牌加入", 18, Style.PaperDim, Style.Kai); e.AutowrapMode = TextServer.AutowrapMode.WordSmart; deckList.AddChild(e); }
        RefreshVerdict();
    }

    // build's verdict, the weakness draw, size and experience
    void RefreshVerdict()
    {
        Ui.Clear(problems);
        // build's placeholder 01000 (随机基础弱点) stays in the deck as a card of its own (Yunke 11-21: no 抽取 button — the
        // card is simply added); a game start draws a real one for it (StandaloneDecks.Prepare), a campaign upgrade on 完成升级
        var val = J.Get(view, "validation");
        if (J.Get(val, "valid").ValueKind == JsonValueKind.True) problems.AddChild(Style.Text("符合构筑规则", 17, Style.Good, Style.Kai));
        else foreach (var err in J.Items(J.Get(val, "errors")).Take(4)) { var l = Style.Text(BuildDecks.ErrorText(err, CardName, DeckInvestigator), 16, Style.Danger, Style.Kai); l.AutowrapMode = TextServer.AutowrapMode.WordSmart; l.CustomMinimumSize = new Vector2(360, 0); problems.AddChild(l); }
        var st = J.Get(view, "stats");
        int size = J.Int(st, "deckSize"), need = J.Int(J.Get(J.Get(view, "investigatorCard"), "deck_requirements"), "size", 30);
        pill.Text = "经验 " + J.Int(st, "xpRequired") + " · 卡组需求 " + size + "/" + need;
        pill.AddThemeColorOverride("font_color", size == need ? Style.Paper : Style.Danger);
        editState.Text = J.Get(view, "edited").ValueKind == JsonValueKind.True ? "有未保存的修改" : "已保存";
        if (upgrade is not null) RefreshUpgrade();
    }

    string CardName(string code) => pool.FirstOrDefault(c => J.String(c, "code") == code) is { ValueKind: JsonValueKind.Object } c ? J.String(c, "name")
        : J.Items(J.Get(view, "slots")).FirstOrDefault(x => J.String(x, "code") == code) is { ValueKind: JsonValueKind.Object } d ? J.String(d, "name") : S.Name(code) is { Length: > 0 } n ? n : code;

    // ghost: the row as it follows the pointer while a card is dragged over the deck list (no clicks, not registered)
    Control Tile(JsonElement card, bool removable, bool ghost = false)
    {
        string code = J.String(card, "code"); int n = J.Int(card, "quantity");
        var b = new Button { FocusMode = FocusModeEnum.None, CustomMinimumSize = new Vector2(0, 48), MouseDefaultCursorShape = removable ? CursorShape.PointingHand : CursorShape.Arrow, ClipContents = true };
        var normal = new StyleBoxFlat { BgColor = new Color(0.07f, 0.06f, 0.05f, 0.92f), AntiAliasing = true, ShadowColor = new Color(0, 0, 0, 0.5f), ShadowSize = 4 }; normal.SetCornerRadiusAll(8);
        var hover = (StyleBoxFlat)normal.Duplicate(); hover.BgColor = new Color(0.2f, 0.15f, 0.07f, 0.95f); hover.ShadowColor = new Color(Style.Amber, 0.35f); hover.ShadowSize = 8;
        b.AddThemeStyleboxOverride("normal", normal); b.AddThemeStyleboxOverride("hover", removable ? hover : normal); b.AddThemeStyleboxOverride("pressed", hover); b.AddThemeStyleboxOverride("focus", new StyleBoxEmpty());
        var tex = Ui.Card(code);
        if (tex is not null)
        {
            var crop = new AtlasTexture { Atlas = tex, Region = new Rect2(tex.GetWidth() * 0.1f, tex.GetHeight() * 0.14f, tex.GetWidth() * 0.8f, tex.GetHeight() * 0.26f) };
            var art = new TextureRect { Texture = crop, ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize, StretchMode = TextureRect.StretchModeEnum.KeepAspectCovered, MouseFilter = MouseFilterEnum.Ignore };
            b.AddChild(art); art.AnchorLeft = 0.42f; art.AnchorRight = 1; art.AnchorBottom = 1; art.OffsetRight = -44;
            art.Resized += () => art.Material = Ui.EdgeAlpha(Vector2.Zero, art.Size, art.Size.X * 0.7f, 0, 10, 0);
        }
        var row = new HBoxContainer { MouseFilter = MouseFilterEnum.Ignore }; row.AddThemeConstantOverride("separation", 10);
        b.AddChild(row); row.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect); row.OffsetLeft = 6; row.OffsetRight = -10;
        // a plain panel, so the number can stand in its exact middle (Ui.CenterDigits; a container would lay it out itself)
        var disc = new Panel { CustomMinimumSize = new Vector2(36, 36), SizeFlagsVertical = SizeFlags.ShrinkCenter, MouseFilter = MouseFilterEnum.Ignore };
        var ds = new StyleBoxFlat { BgColor = SelectionStage.FactionColor(code).Darkened(0.15f), BorderColor = new Color(0.9f, 0.82f, 0.62f, 0.85f), AntiAliasing = true }; ds.SetBorderWidthAll(2); ds.SetCornerRadiusAll(18);
        disc.AddThemeStyleboxOverride("panel", ds);
        int cost = Cost(card);
        var cl = Style.Text(cost < 0 ? "–" : cost.ToString(), 21, Colors.White, Style.Symbols); cl.HorizontalAlignment = HorizontalAlignment.Center; cl.VerticalAlignment = VerticalAlignment.Center;
        cl.AddThemeConstantOverride("outline_size", 5); cl.AddThemeColorOverride("font_outline_color", Colors.Black); disc.AddChild(cl); Ui.CenterDigits(cl); row.AddChild(disc);
        int xp = J.Int(card, "xp");
        var name = Style.Text(J.String(card, "name", code) + (xp > 0 ? " " + new string('•', Math.Min(5, xp)) : ""), 21, removable ? Style.Paper : Style.PaperDim, Style.Kai);
        name.SizeFlagsHorizontal = SizeFlags.ExpandFill; name.VerticalAlignment = VerticalAlignment.Center; name.ClipText = true;
        name.AddThemeConstantOverride("outline_size", 6); name.AddThemeColorOverride("font_outline_color", new Color(0, 0, 0, 0.9f)); row.AddChild(name);
        if (SkillIcons(card) is { } icons) row.AddChild(icons);
        var cnt = Style.Text(n > 1 ? n.ToString() : "", 22, Style.Amber, Style.Sans); cnt.CustomMinimumSize = new Vector2(30, 0); cnt.HorizontalAlignment = HorizontalAlignment.Center; cnt.VerticalAlignment = VerticalAlignment.Center; row.AddChild(cnt);
        if (ghost) { b.MouseFilter = MouseFilterEnum.Ignore; return b; }
        // a click takes one out; dragging the row out of the deck list does too (Yunke 11-21) — a drag ends without the click
        bool down = false, dragged = false; Vector2 at = default;
        if (removable) b.Pressed += () => { if (dragged) { dragged = false; return; } _ = TileClicked(code); };
        b.GuiInput += e =>
        {
            // right-click: the card's own window (张数, 升级, 注释, 可定制升级项, 移到侧卡组 / 主卡组)
            if (e is InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Right }) { AcceptEvent(); _ = CardDialog(code); return; }
            if (!removable) return;
            if (e is InputEventMouseButton { ButtonIndex: MouseButton.Left } mb)
            {
                if (mb.Pressed) { down = true; dragged = false; at = mb.GlobalPosition; return; }
                down = false;
                if (dragCode == code && dragFromDeck)
                {
                    bool outside = !deckPanel.GetGlobalRect().HasPoint(mb.GlobalPosition);
                    EndDrag();
                    dragged = b.GetGlobalRect().HasPoint(mb.GlobalPosition);   // released on the row itself: no click either
                    if (outside) _ = Remove(code);
                }
            }
            else if (e is InputEventMouseMotion mm && down)
            {
                dragPointer = mm.GlobalPosition;
                if (dragCode is null && mm.GlobalPosition.DistanceTo(at) > 10 && !busy) { HidePreview(); StartDrag(code, card, fromDeck: true); }
            }
        };
        b.MouseEntered += () => { if (dragCode is null) ShowPreview(code, b); }; b.MouseExited += HidePreview;
        tiles[code] = b;
        return b;
    }

    // no hover card while a picker / window is over the screen (Yunke 11-22: it sat in front of the upgrade picker)
    bool Covered => (popover is not null && IsInstanceValid(popover)) || App.I.HasModal;

    static readonly Vector2 PreviewSize = new(286, 400);   // the hover card beside a deck row (and the upgrade's, DeckEditorScreen.CardUpgrade.cs)
    void ShowPreview(string code, Control tile)
    {
        HidePreview(); if (Covered) return;
        var size = PreviewSize;
        preview = new CardView { Code = code, CustomMinimumSize = size, Size = size, HoverZoom = false, Redrawn = false, MouseFilter = MouseFilterEnum.Ignore, ZIndex = 150, Override = SharpCard(code, size) };
        AddChild(preview); RoundCorners(preview); Crisp(preview);
        var r = tile.GetGlobalRect(); float vpH = GetViewportRect().Size.Y;
        preview.GlobalPosition = new Vector2(deckPanel.GetGlobalRect().Position.X - size.X - 18, Math.Clamp(r.GetCenter().Y - size.Y / 2, 20, vpH - size.Y - 20));
        if (!Calm) { preview.Modulate = new Color(1, 1, 1, 0); preview.CreateTween().TweenProperty(preview, "modulate:a", 1f, 0.12f); }
    }
    void HidePreview() { if (preview is not null && IsInstanceValid(preview)) preview.QueueFree(); preview = null; HideUpgradePreview(); }

    // QQ bug #41 ("创建卡组的悬浮有点模糊"): the 750×1050 printed card drawn at ~260×364 screen pixels went through the
    // GPU's trilinear mipmaps, which at a ~2.9× shrink blend in the 187-px mip level and soften the rules text. For the hover
    // cards we instead resample the same original image once (Lanczos) to the exact screen-pixel size and draw it 1:1 —
    // no new picture, no filter, only the scale. Null (the card's normal texture) when it would not be a shrink.
    static readonly Dictionary<(string, int, int), Texture2D> sharpCards = new();
    static readonly LinkedList<(string, int, int)> sharpOrder = new();
    // the resampled card is already at screen size: sample it 1:1 (nearest) so the sub-pixel position of the hover card
    // does not smear it again; only cards whose face carries a SharpCard texture
    static void Crisp(CardView cv)
    {
        if (cv.Override is not ImageTexture t || !sharpCards.ContainsValue(t)) return;
        foreach (var tr in cv.FindChildren("*", "TextureRect", true, false).OfType<TextureRect>())
            if (tr.Texture == t) tr.TextureFilter = TextureFilterEnum.Nearest;
    }
    Texture2D? SharpCard(string code, Vector2 size)
    {
        var src = Ui.Card(code); if (src is null || src.GetWidth() <= 0 || src.GetHeight() <= 0) return null;
        float scale = GetViewport().GetScreenTransform().Scale.Y; if (scale <= 0) scale = 1;
        // fit the picture inside the box as the face does (KeepAspectCentered), in screen pixels
        float k = Math.Min(size.X / src.GetWidth(), size.Y / src.GetHeight()) * scale;
        int w = (int)MathF.Round(src.GetWidth() * k), h = (int)MathF.Round(src.GetHeight() * k);
        if (w < 8 || h < 8 || h >= src.GetHeight() * 0.9f) return null;
        var key = (code, w, h);
        if (sharpCards.TryGetValue(key, out var hit)) { sharpOrder.Remove(key); sharpOrder.AddFirst(key); return hit; }
        var img = src.GetImage(); if (img is null) return null;
        if (img.IsCompressed()) img.Decompress();
        img.ClearMipmaps(); img.Resize(w, h, Godot.Image.Interpolation.Lanczos);
        var tex = ImageTexture.CreateFromImage(img);
        sharpCards[key] = tex; sharpOrder.AddFirst(key);
        while (sharpOrder.Count > 48) { var old = sharpOrder.Last!.Value; sharpOrder.RemoveLast(); sharpCards.Remove(old); }
        if (App.I.QaRunning) GD.Print($"QA_SHARP {code} src={src.GetWidth()}x{src.GetHeight()} -> {w}x{h} scale={scale:F3}");
        return tex;
    }

    // ───────── flights (as in the workshop)
    async Task FlyIn(string code, Rect2 from, bool isNew)
    {
        if (Calm) return;
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        if (!tiles.TryGetValue(code, out var tile) || !IsInstanceValid(tile)) return;
        var to = tile.GetGlobalRect();
        if (isNew) tile.Modulate = new Color(1, 1, 1, 0);
        var ghost = new CardView { Code = code, CustomMinimumSize = from.Size, Size = from.Size, HoverZoom = false, Redrawn = false, ShowCost = false, MouseFilter = MouseFilterEnum.Ignore, ZIndex = 160 };
        AddChild(ghost); RoundCorners(ghost); ghost.GlobalPosition = from.Position; ghost.PivotOffset = from.Size / 2; ghost.Modulate = new Color(1.9f, 1.75f, 1.4f, 1f);
        float k = to.Size.Y / from.Size.Y * 1.6f; const float T = 0.45f;
        var tw = ghost.CreateTween().SetParallel();
        tw.TweenProperty(ghost, "global_position", to.GetCenter() - from.Size / 2, T).SetTrans(Tween.TransitionType.Cubic).SetEase(Tween.EaseType.In);
        tw.TweenProperty(ghost, "scale", new Vector2(k, k), T).SetTrans(Tween.TransitionType.Cubic).SetEase(Tween.EaseType.In);
        tw.TweenProperty(ghost, "modulate:a", 0f, 0.14f).SetDelay(T - 0.1f);
        Sparks(from.GetCenter(), to.GetCenter(), T);
        await ToSignal(GetTree().CreateTimer(T), SceneTreeTimer.SignalName.Timeout);
        ghost.QueueFree();
        if (IsInstanceValid(tile)) { tile.Modulate = new Color(2f, 1.8f, 1.4f, 1f); tile.CreateTween().TweenProperty(tile, "modulate", Colors.White, 0.45f); }
    }

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
        AddChild(ghost); RoundCorners(ghost); ghost.GlobalPosition = tileRect.GetCenter() - size / 2; ghost.PivotOffset = size / 2; ghost.Modulate = new Color(1.8f, 1.7f, 1.4f, 0.95f);
        var tw = ghost.CreateTween().SetParallel();
        tw.TweenProperty(ghost, "global_position", dest.GetCenter() - size / 2, 0.42f).SetTrans(Tween.TransitionType.Cubic).SetEase(Tween.EaseType.Out);
        tw.TweenProperty(ghost, "scale", new Vector2(dest.Size.X / size.X, dest.Size.Y / size.Y), 0.42f).SetTrans(Tween.TransitionType.Cubic).SetEase(Tween.EaseType.Out);
        tw.TweenProperty(ghost, "modulate", new Color(1, 1, 1, 0), 0.25f).SetDelay(0.3f);
        tw.Chain().TweenCallback(Callable.From(ghost.QueueFree));
    }

    void Sparks(Vector2 a, Vector2 b, float t)
    {
        var rnd = new Random();
        for (int i = 0; i < 16; i++)
        {
            float s = (float)rnd.NextDouble(); float r = 3 + (float)rnd.NextDouble() * 4;
            var p = a.Lerp(b, s) + new Vector2((float)rnd.NextDouble() * 60 - 30, (float)rnd.NextDouble() * 60 - 30);
            var dot = new Panel { MouseFilter = MouseFilterEnum.Ignore, ZIndex = 158, Size = new Vector2(r * 2, r * 2), Position = p - new Vector2(r, r) };
            var st = new StyleBoxFlat { BgColor = new Color(1f, 0.93f, 0.7f), ShadowColor = new Color(1f, 0.8f, 0.4f, 0.8f), ShadowSize = (int)(r * 2.5f) }; st.SetCornerRadiusAll((int)r + 1);
            dot.AddThemeStyleboxOverride("panel", st); AddChild(dot); dot.Modulate = new Color(1, 1, 1, 0);
            var tw = dot.CreateTween(); tw.TweenInterval(s * t * 0.9f); tw.TweenProperty(dot, "modulate:a", 1f, 0.06f);
            tw.Parallel().TweenProperty(dot, "position", dot.Position + new Vector2((float)rnd.NextDouble() * 30 - 15, -12 - (float)rnd.NextDouble() * 20), 0.5f);
            tw.TweenProperty(dot, "modulate:a", 0f, 0.35f); tw.TweenCallback(Callable.From(dot.QueueFree));
        }
    }

    // ───────── small parts
    OptionButton Pick((string Value, string Name)[] items, Action<string> on)
    {
        var o = new OptionButton { CustomMinimumSize = new Vector2(150, 50) }; Ui.Soften(o); o.AddThemeFontSizeOverride("font_size", 20);
        foreach (var (val, name) in items) { o.AddItem(name); o.SetItemMetadata(o.ItemCount - 1, val); }
        o.ItemSelected += i => on(o.GetItemMetadata((int)i).AsString()); return o;
    }
    static void SoftEdit(LineEdit e) { e.AddThemeStyleboxOverride("normal", Ui.SoftBox(0.45f, false, 14, 6)); e.AddThemeStyleboxOverride("focus", Ui.SoftBox(0.6f, true, 14, 6)); }
    Button CostGem(string text, Action onPress)
    {
        var b = new Button { Text = text, ToggleMode = true, FocusMode = FocusModeEnum.None, CustomMinimumSize = new Vector2(46, 46), MouseDefaultCursorShape = CursorShape.PointingHand };
        b.AddThemeFontSizeOverride("font_size", 21); b.AddThemeFontOverride("font", Style.Symbols ?? Style.Sans);
        StyleBoxFlat Round(bool lit) { var s = Ui.SoftBox(lit ? 0.75f : 0.4f, lit, 0, 0); s.SetCornerRadiusAll(23); if (!lit) s.BgColor = new Color(0.1f, 0.13f, 0.2f, 0.85f); return s; }
        b.AddThemeStyleboxOverride("normal", Round(false)); b.AddThemeStyleboxOverride("hover", Round(true)); b.AddThemeStyleboxOverride("pressed", Round(true)); b.AddThemeStyleboxOverride("hover_pressed", Round(true));
        b.AddThemeColorOverride("font_color", Style.HoverText);
        b.Pressed += () => onPress();
        return b;
    }

}
