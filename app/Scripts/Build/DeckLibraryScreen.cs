using Godot;
using Gathering;
using System.Text.Json;

namespace ArkhamUI;

// The deck workshop's first page (Yunke 11-18 r7; build's deck collection): every deck as a card on the left — portrait,
// name, investigator, experience, size "30 (35)", build's verdict — with 编辑 / 复制 / 导出 / 删除 under the pointer;
// 创建卡组, search and sort above; the chosen deck's preview on the right. Decks live in build's store (BuildDecks).
public partial class DeckLibraryScreen : Control
{
    Services S => App.I.S!;
    VBoxContainer list = null!; Control preview = null!; Label status = null!; LineEdit search = null!; HBoxContainer switches = null!, classRow = null!;
    int sortMode, kindMode; readonly HashSet<string> classes = new();
    List<JsonElement> decks = new();
    string? selected;

    public override void _Ready()
    {
        MouseFilter = MouseFilterEnum.Stop;
        AddChild(WorkshopBackdrop());
        var head = Style.Title("牌组工坊", 46); AddChild(head); head.Position = new Vector2(102, 30);   // right of the back icon (WorkshopBack)
        var sub = Style.Text("组牌规则来自 arkham.build · 所有循环的卡牌都可以组", 20, Style.PaperDim, Style.Kai); AddChild(sub); sub.Position = new Vector2(106, 92);

        // left: the collection
        var left = new VBoxContainer(); left.AddThemeConstantOverride("separation", 10); AddChild(left);
        left.Position = new Vector2(48, 140); left.Size = new Vector2(640, 860); left.CustomMinimumSize = new Vector2(640, 860);
        var bar = new HBoxContainer(); bar.AddThemeConstantOverride("separation", 10); left.AddChild(bar);
        var create = Ui.Button("＋ 创建卡组", () => App.I.Go(new DeckCreateScreen()), 23, true); create.CustomMinimumSize = new Vector2(160, 54); bar.AddChild(create);
        // 导入 as a cloud with an arrow going up, 导出 (the chosen deck to a .json file) beside it — Yunke 11-22
        // (导出 is on each deck's row — Yunke 11-22 second round)
        bar.AddChild(LineIcon.Button("cloud", "导入牌组（ArkhamDB / arkham.build 链接或 JSON）", ImportDialog, 50));
        search = new LineEdit { PlaceholderText = "搜索卡组…", SizeFlagsHorizontal = SizeFlags.ExpandFill, CustomMinimumSize = new Vector2(0, 50) }; search.AddThemeFontSizeOverride("font_size", 21);
        search.AddThemeStyleboxOverride("normal", Ui.SoftBox(0.45f, false, 14, 6)); search.AddThemeStyleboxOverride("focus", Ui.SoftBox(0.6f, true, 14, 6));
        search.TextChanged += _ => Fill(); bar.AddChild(search);
        // the order and 初始卡组 / 通关卡组 as two switches (a click goes to the next state; Yunke 11-22: the drop-downs crowded the bar)
        switches = new HBoxContainer(); switches.AddThemeConstantOverride("separation", 6); bar.AddChild(switches);
        Switches();
        // the count, and the classes as their symbols to switch on and off (none on = every class; Yunke 11-22)
        var second = new HBoxContainer(); second.AddThemeConstantOverride("separation", 6); left.AddChild(second);
        status = Style.Text("正在载入组牌规则…", 18, Style.PaperDim, Style.Kai); status.SizeFlagsHorizontal = SizeFlags.ExpandFill; status.VerticalAlignment = VerticalAlignment.Center; second.AddChild(status);
        classRow = new HBoxContainer(); classRow.AddThemeConstantOverride("separation", 4); second.AddChild(classRow);
        ClassToggles();
        list = new VBoxContainer(); list.AddThemeConstantOverride("separation", 8);
        var scroll = Ui.Scroll(list); scroll.CustomMinimumSize = new Vector2(640, 720); scroll.SizeFlagsVertical = SizeFlags.ExpandFill; left.AddChild(scroll); SmoothScroll.Attach(scroll);

        // right: the preview
        var right = new PanelContainer(); right.AddThemeStyleboxOverride("panel", Ui.SoftPanel(0.72f, 26, 22)); AddChild(right);
        right.Position = new Vector2(730, 60); right.Size = new Vector2(1140, 960); right.CustomMinimumSize = new Vector2(1140, 960);
        preview = new VBoxContainer(); ((VBoxContainer)preview).AddThemeConstantOverride("separation", 12); right.AddChild(preview);

        // back: to where the workshop was opened from (the picker's ＋, the prepare screen — App.WorkshopReturn), else the main menu
        WorkshopBack.Attach(this, "返回上一级（Esc）", () => { if (!App.I.TakeWorkshopReturn()) { var m = new MainMenu(); App.I.Go(m); m.Ready(); } });
        QaScript.Register("lib-select", a => { var d = decks.FirstOrDefault(x => J.String(x, "id") == a); if (d.ValueKind == JsonValueKind.Object) Select(a); });
        QaScript.Register("lib-create", _ => App.I.Go(new DeckCreateScreen()));
        QaScript.Register("lib-mode", a => previewMode?.Invoke(a switch { "small" => DeckPreview.Mode.Small, "large" => DeckPreview.Mode.Large, _ => DeckPreview.Mode.Strips }));
        QaScript.Register("lib-kind", a => { if (int.TryParse(a, out int k)) { kindMode = k; Switches(); Fill(); GD.Print("QA_LIB_KIND " + k + " " + list.GetChildCount()); } });
        QaScript.Register("lib-sort", a => { if (int.TryParse(a, out int k)) { sortMode = k; Switches(); Fill(); GD.Print("QA_LIB_SORT " + k + " " + string.Join(",", ShownIds())); } });
        QaScript.Register("lib-class", a => { classes.Clear(); foreach (var c in a.Split(',', StringSplitOptions.RemoveEmptyEntries)) if (Classes.Contains(c)) classes.Add(c); ClassToggles(); Fill(); GD.Print("QA_LIB_CLASS " + a + " " + list.GetChildCount() + " " + string.Join(",", ShownIds())); });
        QaScript.Register("lib-export-file", a => { if (selected is not null) WriteDeck(selected, Path.Combine(App.I.Root, a)); });
        // QA: the title's rename: lib-rename-open, then lib-rename-type:<text> and Enter (submit) or Esc (cancel) through the viewport
        QaScript.Register("lib-search", a => search.Text = a);   // (setting Text does not send TextChanged)
        QaScript.Register("lib-search-fill", _ => Fill());
        QaScript.Register("lib-rename-open", _ => titleEdit?.Invoke());
        QaScript.Register("lib-rename-type", a => { if (renameBox is not null && IsInstanceValid(renameBox)) { renameBox.Text = a; renameBox.CaretColumn = a.Length; } });
        QaScript.Register("lib-rename-key", a => { var k = a == "esc" ? Key.Escape : Key.Enter; var vp = GetViewport(); vp.PushInput(new InputEventKey { Keycode = k, PhysicalKeycode = k, Pressed = true }); vp.PushInput(new InputEventKey { Keycode = k, PhysicalKeycode = k, Pressed = false }); });
        QaScript.Register("lib-row-actions", _ => GD.Print("QA_ROW_ACTIONS " + string.Join(",", list.FindChildren("*", "", true, false).OfType<Control>().Where(c => c.HasMeta("row_action")).Select(c => (string)c.GetMeta("row_action")).Distinct())));
        QaScript.Register("lib-export-name", _ => { if (selected is not null) GD.Print("QA_EXPORT_NAME " + ExportName(selected)); });
        QaScript.Register("lib-peek-badge", a => { var b = list.FindChildren("*", "", true, false).OfType<Control>().FirstOrDefault(c => c.HasMeta("badge_of") && (string)c.GetMeta("badge_of") == a); if (b is not null) Peek.Schedule(b, () => BadgeText(a), 0); else GD.Print("QA_PEEK none " + a); });
        // QA: a raw rules-core call, written to a file: core-call:<method>|<request json, ' for ">|<out path>
        // QA: validate a deck file the way a game start does (prepare + the core's construction check), result to a file
        QaScript.Register("deck-validate", async a => { var p = a.Split('|'); try { using var d = JsonDocument.Parse(File.ReadAllText(Path.Combine(App.I.Root, p[0]))); var prepared = await S.Backend.PrepareStandaloneDeck(d.RootElement.Clone()); var inv = J.String(prepared, "investigator_code"); var schema = await S.Backend.BuildSchema(inv); var r = await S.Backend.ValidateDeck(inv, J.String(schema, "schemaId"), prepared); File.WriteAllText(Path.Combine(App.I.Root, p[1]), r.GetRawText()); GD.Print("QA_VALIDATE " + p[0] + " " + J.String(r, "status") + " " + PrototypeBackend.Verdict(r)); } catch (Exception e) { GD.Print("QA_VALIDATE fail " + p[0] + " " + e.Message); } });
        QaScript.Register("core-call", async a => { var p = a.Split('|'); try { using var req = JsonDocument.Parse(p[1].EndsWith(".json") ? File.ReadAllText(Path.Combine(App.I.Root, p[1])) : p[1].Replace('\'', '"')); var r = await S.Backend.PureRaw(p[0], req.RootElement.Clone()); File.WriteAllText(Path.Combine(App.I.Root, p[2]), r.GetRawText()); GD.Print("QA_CORE ok " + p[0] + " " + r.GetRawText().Length); } catch (Exception e) { GD.Print("QA_CORE fail " + p[0] + " " + e.Message); } });
        QaScript.Register("lib-import", async a => await DoImport(File.ReadAllText(Path.Combine(App.I.Root, a))));
        QaScript.Register("lib-import-dialog", _ => ImportDialog());
        QaScript.Register("lib-import-link", async a => await DoImportOnline(a));
        QaScript.Register("lib-link-targets", a => { var (u, e) = OnlineImport.Targets(a); GD.Print("QA_LINK " + a + " -> " + string.Join(" ", u) + (e is null ? "" : " ERR " + e)); });
        QaScript.Register("lib-close", _ => App.I.CloseAllModals());
        QaScript.Register("ed-load-delay", a => DeckEditorScreen.QaLoadDelay = double.TryParse(a, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double d) ? d : 0);
        // QA: a window of table small cards for the given codes (checks crops of cards no scenario here reaches)
        QaScript.Register("card-grid", a => { var flow = new HFlowContainer { CustomMinimumSize = new Vector2(1500, 0) }; flow.AddThemeConstantOverride("h_separation", 14); flow.AddThemeConstantOverride("v_separation", 14); foreach (var code in a.Split(',')) flow.AddChild(new CardView { Code = code, CustomMinimumSize = new Vector2(136, 190), Size = new Vector2(136, 190), HoverZoom = false }); App.I.Modal(Ui.Panel(flow, 24)); });
        // QA: the same for deck-list strips (DeckPreview.Strip): strip-grid:<code>:<type>,… — the strip's art band and its fade
        QaScript.Register("strip-grid", a =>
        {
            var col = new VBoxContainer(); col.AddThemeConstantOverride("separation", 6);
            var loads = new List<(string, string, Action<Texture2D?>)>();
            foreach (var item in a.Split(','))
            {
                var p = item.Split(':');
                using var doc = JsonDocument.Parse(JsonSerializer.Serialize(new { code = p[0], type_code = p.Length > 1 ? p[1] : "asset", name = p[0], quantity = 1, xp = 0, cost = 1 }));
                col.AddChild(DeckPreview.Strip(doc.RootElement.Clone(), 560, loads));
            }
            foreach (var (code, type, done) in loads) done(DeckPreview.Art(code, type));
            App.I.Modal(Ui.Panel(col, 20));
        });
        // QA (anywhere): the pointer enters the n-th visible button whose text starts with the given words ("攻击 · 刀子|1")
        QaScript.Register("hover-button", a => { var p = a.Split('|'); int n = p.Length > 1 && int.TryParse(p[1], out int k) ? k : 0; var bs = App.I.GetTree().Root.FindChildren("*", "", true, false).OfType<Button>().Where(b => b.IsVisibleInTree() && b.Text.StartsWith(p[0])).ToList(); if (n < bs.Count) bs[n].EmitSignal(Control.SignalName.MouseEntered); else GD.Print("QA_HOVER none " + a + " of " + bs.Count); });
        // QA (anywhere, also on the table): the hover box of the card shown with this key ("asset:<id>", "hand:<id>") or code
        QaScript.Register("peek-card", a => { var cv = App.I.GetTree().Root.FindChildren("*", "", true, false).OfType<CardView>().Where(c => c.IsVisibleInTree()).FirstOrDefault(c => c.Key == a || c.Code == a); if (cv is not null) Peek.ShowCard(cv, cv.Code, cv.PeekExtra, 0); else GD.Print("QA_PEEK none " + a); });
        // QA: the hover box of a card in the preview's small cards (as a pointer resting on it)
        QaScript.Register("lib-peek", a => { var cv = preview.FindChildren("*", "", true, false).OfType<CardView>().FirstOrDefault(c => c.Code == a); if (cv is not null) Peek.ShowCard(cv, a, cv.PeekExtra, 0); else GD.Print("QA_PEEK none " + a); });
        QaScript.Register("lib-peek-strip", a => { var b = preview.FindChildren("*", "", true, false).OfType<Control>().FirstOrDefault(c => c.HasMeta("strip_code") && (string)c.GetMeta("strip_code") == a); if (b is not null) Peek.Schedule(b, () => DeckPreview.HoverCard(a), 0); else GD.Print("QA_PEEK none " + a); });
        QaScript.Register("lib-edit", a => App.I.Go(new DeckEditorScreen(a.Length > 0 ? a : selected ?? "")));
        QaScript.Register("lib-copy", async a => { await BuildDecks.Copy(a); await Reload(); });
        QaScript.Register("lib-delete", async a => { await BuildDecks.Remove(a); await Reload(); });
        _ = Reload();
    }

    // the warm detective's desk Yunke painted for the workshop and the options (11-19 #5: every workshop page uses it):
    // assets/bg/workshop-desk.png (from inbox/art/detective-desk-warm.png), read straight from the file when Godot has not
    // imported it yet; the old desk only if it is missing
    static Texture2D? desk;
    public static Texture2D? WorkshopDesk()
    {
        if (desk is not null) return desk;
        desk = Ui.Bg("workshop-desk");
        if (desk is null)
        {
            var file = Path.Combine(App.I.Assets, "bg", "workshop-desk.png");
            if (File.Exists(file)) { var img = Image.LoadFromFile(file); if (img is not null) { img.GenerateMipmaps(); desk = ImageTexture.CreateFromImage(img); } }
        }
        return desk ??= Ui.Ref("art/scenario-archive-v1/desk");
    }
    public static Control WorkshopBackdrop()
    {
        var root = new Control { MouseFilter = MouseFilterEnum.Ignore }; root.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        var bg = Ui.Image(WorkshopDesk(), Vector2.Zero, true); root.AddChild(bg); bg.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        bg.TextureFilter = TextureFilterEnum.LinearWithMipmaps;
        // a light dimming only, darker towards the edges, so the panels read and the desk stays warm
        var dim = new ColorRect { Color = new Color(0, 0, 0, 0.22f), MouseFilter = MouseFilterEnum.Ignore }; root.AddChild(dim); dim.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        var edges = new Control { MouseFilter = MouseFilterEnum.Ignore }; root.AddChild(edges); edges.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        Ui.EdgeFade(edges, new Color(0, 0, 0, 0.55f), 260, 160, 260, 200);
        return root;
    }

    async Task Reload()
    {
        try
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var arr = await BuildDecks.List(); await TabooChip.Sets();
            decks = arr.EnumerateArray().ToList();
            status.Text = decks.Count + " 个卡组";
            if (App.I.QaRunning) GD.Print($"QA_LIB {decks.Count} decks in {sw.ElapsedMilliseconds} ms");
            Fill();
            if (selected is null || !decks.Any(d => J.String(d, "id") == selected)) selected = decks.Count > 0 ? J.String(decks[0], "id") : null;
            if (selected is not null) Select(selected); else Ui.Clear(preview);
        }
        catch (Exception e) { status.Text = "组牌规则载入失败：" + e.Message; }
    }

    void Fill()
    {
        Ui.Clear(list);
        string q = search.Text.Trim();
        IEnumerable<JsonElement> shown = decks.Where(d => q.Length == 0 || (J.String(d, "name") + J.String(d, "investigator_name")).Contains(q, StringComparison.OrdinalIgnoreCase));
        if (kindMode > 0) shown = shown.Where(d => BuildDecks.IsCompleted(J.String(d, "id")) == (kindMode == 2));
        if (classes.Count > 0) shown = shown.Where(d => classes.Contains(J.String(d, "faction") is { Length: > 0 } f ? f : "neutral"));
        shown = sortMode switch
        {
            1 => shown.OrderBy(d => J.String(d, "name")),
            2 => shown.OrderBy(d => J.String(d, "investigator")).ThenBy(d => J.String(d, "name")),
            _ => shown.OrderByDescending(d => J.String(d, "date_update")),
        };
        int n = 0;
        foreach (var d in shown) { list.AddChild(DeckCard(d)); n++; }
        if (decks.Count > 0) status.Text = n == decks.Count ? decks.Count + " 个卡组" : "显示 " + n + " / " + decks.Count + " 个卡组";
    }

    // One deck in the list: portrait, name, investigator, experience, size; the actions slide in under the pointer.
    Control DeckCard(JsonElement d)
    {
        string id = J.String(d, "id"), inv = J.String(d, "investigator_front", J.String(d, "investigator"));
        bool chosen = id == selected, valid = J.Get(d, "valid").ValueKind == JsonValueKind.True;
        var box = new PanelContainer { CustomMinimumSize = new Vector2(620, 96), MouseFilter = MouseFilterEnum.Stop, MouseDefaultCursorShape = CursorShape.PointingHand };
        box.AddThemeStyleboxOverride("panel", Ui.SoftBox(chosen ? 0.7f : 0.45f, chosen, 12, 8)); box.SetMeta("deck_id", id);
        var row = new HBoxContainer { MouseFilter = MouseFilterEnum.Ignore }; row.AddThemeConstantOverride("separation", 14); box.AddChild(row);
        var pic = CardView.PortraitCard(inv, 78); pic.MouseFilter = MouseFilterEnum.Ignore; pic.ShowName = false; row.AddChild(pic);
        var col = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill, MouseFilter = MouseFilterEnum.Ignore, Alignment = BoxContainer.AlignmentMode.Center }; col.AddThemeConstantOverride("separation", 2); row.AddChild(col);
        // the name; a finished campaign's deck has a small laurel before it instead of "通关" in the name (Yunke 11-22)
        var nameRow = new HBoxContainer { MouseFilter = MouseFilterEnum.Ignore }; nameRow.AddThemeConstantOverride("separation", 6); col.AddChild(nameRow);
        if (BuildDecks.IsCompleted(id)) nameRow.AddChild(Badge(id, 26));
        var name = Style.Text(BuildDecks.ShownName(id, J.String(d, "name", "未命名")), 23, Style.HoverText, Style.Kai); name.ClipText = true; name.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis; name.CustomMinimumSize = new Vector2(10, 0); name.SizeFlagsHorizontal = SizeFlags.ExpandFill; nameRow.AddChild(name);
        var sub = new HBoxContainer { MouseFilter = MouseFilterEnum.Ignore }; sub.AddThemeConstantOverride("separation", 10); col.AddChild(sub);
        // the class as its symbol (Yunke 11-19 r11: fewer words in the workshop), its name in the tooltip
        string fac = J.String(d, "faction");
        if (Style.Glyph.ContainsKey(fac))
        {
            var fg = Ui.Glyph(fac, 18, SelectionStage.FactionColor(inv).Lightened(0.35f)); fg.VerticalAlignment = VerticalAlignment.Center; fg.MouseFilter = MouseFilterEnum.Pass; fg.TooltipText = BuildDecks.FactionName(fac);
            sub.AddChild(fg);
        }
        sub.AddChild(Style.Text(J.String(d, "investigator_name"), 18, SelectionStage.FactionColor(inv).Lightened(0.35f), Style.Kai));
        var actions = new HBoxContainer { MouseFilter = MouseFilterEnum.Ignore, Modulate = new Color(1, 1, 1, chosen ? 1 : 0) }; actions.AddThemeConstantOverride("separation", 4); col.AddChild(actions);
        // under the pointer: 导出 (the deck as a .json file) and 删除 only (Yunke 11-22: 编辑 / 复制 off the row)
        foreach (var (kind, label, act) in new (string, string, Action)[] {
            ("export", "导出为 JSON 文件", () => ExportFile(id)),
            ("delete", "删除", () => ConfirmDelete(id, BuildDecks.ShownName(id, J.String(d, "name")))) })
        {
            var b = LineIcon.Button(kind, label, act, 32); b.MouseFilter = MouseFilterEnum.Pass; b.SetMeta("row_action", kind); actions.AddChild(b);
        }
        var nums = new VBoxContainer { MouseFilter = MouseFilterEnum.Ignore, Alignment = BoxContainer.AlignmentMode.Center }; nums.AddThemeConstantOverride("separation", 2); row.AddChild(nums);
        // the card count after a small cards icon (it was "× 30 (33)", which read like a close button — Yunke 11-19)
        var sizeRow = new HBoxContainer { MouseFilter = MouseFilterEnum.Ignore, Alignment = BoxContainer.AlignmentMode.End }; sizeRow.AddThemeConstantOverride("separation", 5); nums.AddChild(sizeRow);
        sizeRow.AddChild(new LineIcon { Kind = "cards", Ink = new Color(Style.Paper, 0.8f), CustomMinimumSize = new Vector2(20, 20), Stroke = 1.6f, SizeFlagsVertical = SizeFlags.ShrinkCenter, MouseFilter = MouseFilterEnum.Pass, TooltipText = "张数（含专属卡）" });
        var size = Style.Text(J.Int(d, "size") + " (" + J.Int(d, "total") + ")", 20, Style.Paper, Style.Sans); sizeRow.AddChild(size);
        // experience, and the taboo list's version in its small box when the deck has one
        var xpRow = new HBoxContainer { MouseFilter = MouseFilterEnum.Ignore, Alignment = BoxContainer.AlignmentMode.End }; xpRow.AddThemeConstantOverride("separation", 6); nums.AddChild(xpRow);
        if (J.Get(d, "taboo_id").ValueKind == JsonValueKind.Number) xpRow.AddChild(TabooChip.Badge(J.Int(d, "taboo_id"), 14));
        var xp = Style.Text("经验 " + J.Int(d, "xp"), 18, Style.PaperDim, Style.Kai); xpRow.AddChild(xp);
        var okRow = new HBoxContainer { MouseFilter = MouseFilterEnum.Ignore, Alignment = BoxContainer.AlignmentMode.End }; nums.AddChild(okRow);
        var ok = new LineIcon { Kind = valid ? "ok" : "warn", Ink = valid ? Style.Good : Style.Danger, CustomMinimumSize = new Vector2(22, 22), Stroke = 2.2f, MouseFilter = MouseFilterEnum.Pass, TooltipText = valid ? "符合构筑规则" : "不符合构筑规则" };
        okRow.AddChild(ok);
        box.MouseEntered += () => actions.CreateTween().TweenProperty(actions, "modulate:a", 1f, 0.12f);
        box.MouseExited += () => { if (id != selected) actions.CreateTween().TweenProperty(actions, "modulate:a", 0f, 0.2f); };
        box.GuiInput += e =>
        {
            if (e is InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Left } mb)
            {
                if (mb.DoubleClick) App.I.Go(new DeckEditorScreen(id)); else Select(id);
            }
        };
        return box;
    }

    async void Select(string id)
    {
        bool other = id != shownId; shownId = id;
        selected = id; Fill();
        Ui.Clear(preview);
        if (other) WorkshopFade.In(preview, 0.2f);   // another deck: its preview fades in (Yunke 11-22)
        JsonElement v;
        try { v = await BuildDecks.View(id); } catch (Exception e) { preview.AddChild(Style.Text("读取失败：" + e.Message, 20, Style.Danger)); return; }
        if (selected != id) return;
        string inv = J.String(v, "investigator_front", J.String(v, "investigator"));
        var top = new HBoxContainer(); top.AddThemeConstantOverride("separation", 22); preview.AddChild(top);
        var art = Peek.FramedCard(Ui.Card(inv), new Vector2(470, 336)); top.AddChild(art);   // framed as every big card, large enough to read (Yunke 11-20)
        var info = new VBoxContainer(); info.AddThemeConstantOverride("separation", 8); info.SizeFlagsHorizontal = SizeFlags.ExpandFill; top.AddChild(info);
        var titleRow = new HBoxContainer(); titleRow.AddThemeConstantOverride("separation", 10); info.AddChild(titleRow);
        if (BuildDecks.IsCompleted(id)) { var bd = Badge(id, 38); bd.SizeFlagsVertical = SizeFlags.ShrinkBegin; titleRow.AddChild(bd); }
        // the info column beside the 470-wide card: 1096 − 470 − 22, less the laurel when there is one
        titleRow.AddChild(TitleName(id, BuildDecks.ShownName(id, J.String(v, "name", "未命名")), 600 - (BuildDecks.IsCompleted(id) ? 48 : 0)));
        info.AddChild(Style.Text(S.Name(inv) is { Length: > 0 } n ? n : J.String(J.Get(v, "investigatorCard"), "name"), 22, SelectionStage.FactionColor(inv).Lightened(0.35f), Style.Kai));
        var st = J.Get(v, "stats");
        var line = new HBoxContainer(); line.AddThemeConstantOverride("separation", 12); info.AddChild(line);
        line.AddChild(Style.Text("张数 " + J.Int(st, "deckSize") + "（含专属 " + J.Int(st, "deckSizeTotal") + "）　经验 " + J.Int(st, "xpRequired"), 20, Style.Paper, Style.Kai));
        if (J.Get(v, "taboo_id").ValueKind == JsonValueKind.Number) { line.AddChild(Style.Text("禁", 20, Style.Paper, Style.Kai)); line.AddChild(TabooChip.Badge(J.Int(v, "taboo_id"), 16)); }
        var val = J.Get(v, "validation");
        if (J.Get(val, "valid").ValueKind == JsonValueKind.True) info.AddChild(Style.Text("符合构筑规则", 19, Style.Good, Style.Kai));
        else foreach (var err in J.Items(J.Get(val, "errors")).Take(4)) { var l = Style.Text(BuildDecks.ErrorText(err, CardName, inv), 18, Style.Danger, Style.Kai); l.AutowrapMode = TextServer.AutowrapMode.WordSmart; l.CustomMinimumSize = new Vector2(10, 0); l.SizeFlagsHorizontal = SizeFlags.ExpandFill; info.AddChild(l); }
        var btns = new HBoxContainer(); btns.AddThemeConstantOverride("separation", 10); info.AddChild(btns);
        btns.AddChild(LineIcon.Button("edit", "编辑这副牌", () => App.I.Go(new DeckEditorScreen(id)), 50, true));
        btns.AddChild(LineIcon.Button("delete", "删除", () => ConfirmDelete(id, BuildDecks.ShownName(id, J.String(v, "name"))), 50));

        // the cards: strips in two columns, small cards or printed cards (Yunke 11-19), the choice remembered
        var modes = new HBoxContainer(); modes.AddThemeConstantOverride("separation", 6);
        btns.AddChild(Ui.Spacer(0, 0, true)); btns.AddChild(modes); btns.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        var holder = new MarginContainer { SizeFlagsVertical = SizeFlags.ExpandFill }; preview.AddChild(holder);
        void Show(DeckPreview.Mode mode, bool fade)
        {
            DeckPreview.Save(mode);
            Ui.Clear(modes);
            foreach (var (label, md) in new[] { ("strips|横条", DeckPreview.Mode.Strips), ("small|小卡", DeckPreview.Mode.Small), ("large|大卡", DeckPreview.Mode.Large) })
            {
                var m2 = md; var parts = label.Split('|');
                var b = LineIcon.Button(parts[0], parts[1], () => { if (selected == id) Show(m2, true); }, 44, md == mode); modes.AddChild(b);
            }
            Ui.Clear(holder);
            var body = DeckPreview.Build(v, mode, 1080, CardName);
            var scroll = Ui.Scroll(body); scroll.SizeFlagsVertical = SizeFlags.ExpandFill; scroll.CustomMinimumSize = new Vector2(1096, 540);
            holder.AddChild(scroll); SmoothScroll.Attach(scroll);
            if (fade) WorkshopFade.In(holder, 0.16f);   // 横条 / 小卡 / 大卡 switched: the cards fade in
            if (App.I.QaRunning) GD.Print("QA_PREVIEW " + mode);
        }
        previewMode = m => Show(m, true);
        Show(DeckPreview.Saved, false);
    }

    Action<DeckPreview.Mode>? previewMode; string? shownId;

    string CardName(string code) { var n = S.Name(code); return n.Length > 0 ? n : code; }

    // Import: paste the JSON (the clipboard is filled in when it holds some) or pick .json files.
    void ImportDialog()
    {
        // one input line for a link or a deck's JSON (Yunke 11-22: no big text box, no 粘贴剪贴板 button), 选择文件… / 取消 / 导入
        var col = new VBoxContainer { CustomMinimumSize = new Vector2(760, 0) }; col.AddThemeConstantOverride("separation", 16);
        col.AddChild(Style.Title("导入牌组", 34));
        var note = Style.Text("粘贴 ArkhamDB 或 arkham.build 的牌组链接，或导出的牌组 JSON，再按回车或点“导入”；也可以选择 .json 文件（可多选）。导入的牌组会另存一份新的，原有牌组不受影响。", 19, Style.PaperDim, Style.Kai);
        note.AutowrapMode = TextServer.AutowrapMode.WordSmart; col.AddChild(note);
        Control scrim = null!;
        var input = new LineEdit { PlaceholderText = "https://arkhamdb.com/decklist/view/…  ·  https://arkham.build/share/…  ·  { 牌组 JSON }", SizeFlagsHorizontal = SizeFlags.ExpandFill, CustomMinimumSize = new Vector2(760, 56) };
        input.AddThemeFontSizeOverride("font_size", 18);
        input.AddThemeStyleboxOverride("normal", Ui.SoftBox(0.55f, true, 14, 6)); input.AddThemeStyleboxOverride("focus", Ui.SoftBox(0.68f, true, 14, 6));
        input.SetMeta("import_input", true);
        string clip = DisplayServer.ClipboardGet().Trim();
        if (clip.StartsWith("{") || clip.StartsWith("[") || (clip.Length < 300 && OnlineImport.LooksLikeLink(clip))) input.Text = clip.Replace("\r", "").Replace("\n", " ");
        col.AddChild(input);
        async void Go()
        {
            var t = input.Text.Trim(); if (t.Length == 0) return;
            App.I.CloseModal(scrim);
            if (!t.StartsWith("{") && !t.StartsWith("[") && OnlineImport.LooksLikeLink(t)) await DoImportOnline(t); else await DoImport(t);
        }
        input.TextSubmitted += _ => Go();
        var row = new HBoxContainer(); row.AddThemeConstantOverride("separation", 10); col.AddChild(row);
        row.AddChild(Ui.Button("选择文件…", () =>
        {
            var fd = new FileDialog { FileMode = FileDialog.FileModeEnum.OpenFiles, Access = FileDialog.AccessEnum.Filesystem, Filters = new[] { "*.json ; 牌组 JSON" }, Title = "选择牌组 JSON", UseNativeDialog = true };
            AddChild(fd);
            fd.FilesSelected += async files => { App.I.CloseModal(scrim); fd.QueueFree(); await DoImportFiles(files); };
            fd.Canceled += () => fd.QueueFree();
            fd.PopupCentered(new Vector2I(1100, 700));
        }, 20));
        row.AddChild(Ui.Spacer(0, 0, true));
        row.AddChild(Ui.Button("取消", () => App.I.CloseModal(scrim), 20));
        row.AddChild(Ui.Button("导入", Go, 20, true));
        scrim = App.I.Modal(Ui.Panel(col, 34));
        input.CallDeferred(Control.MethodName.GrabFocus);
    }

    async Task DoImportFiles(string[] files)
    {
        var added = new List<string>(); var refused = new List<string>();
        foreach (var f in files)
        {
            string text; try { text = File.ReadAllText(f); } catch (Exception e) { refused.Add(Path.GetFileName(f) + "：读取失败（" + e.Message + "）"); continue; }
            var (a, r) = await BuildDecks.Import(text); added.AddRange(a); refused.AddRange(r.Select(x => Path.GetFileName(f) + "：" + x));
        }
        await AfterImport(added, refused);
    }
    async Task DoImport(string text) { var (a, r) = await BuildDecks.Import(text); await AfterImport(a, r); }
    async Task DoImportOnline(string link)
    {
        App.I.Toast("正在联网读取牌组…");
        var (json, error) = await OnlineImport.Fetch(link);
        if (json is null) { await AfterImport(new(), new() { error ?? "读取失败" }); return; }
        await DoImport(json);
    }
    async Task AfterImport(List<string> added, List<string> refused)
    {
        if (added.Count > 0) { selected = added[0]; await Reload(); }
        if (App.I.QaRunning) GD.Print("QA_IMPORT added=" + string.Join(",", added) + " refused=" + string.Join(" | ", refused));
        if (refused.Count == 0) { App.I.Toast(added.Count > 0 ? "已导入 " + added.Count + " 副牌组" : "没有可导入的牌组", added.Count == 0); return; }
        // what could not come in, in a window (several lines)
        var col = new VBoxContainer { CustomMinimumSize = new Vector2(680, 0) }; col.AddThemeConstantOverride("separation", 14);
        col.AddChild(Style.Title(added.Count > 0 ? "已导入 " + added.Count + " 副，另有 " + refused.Count + " 副未导入" : "未能导入", 30));
        foreach (var line in refused.Take(8)) { var l = Style.Text("· " + line, 20, Style.Paper, Style.Kai); l.AutowrapMode = TextServer.AutowrapMode.WordSmart; col.AddChild(l); }
        var row = new HBoxContainer { Alignment = BoxContainer.AlignmentMode.End }; col.AddChild(row);
        Control scrim = null!; row.AddChild(Ui.Button("知道了", () => App.I.CloseModal(scrim), 21, true));
        scrim = App.I.Modal(Ui.Panel(col, 34));
    }

    void ConfirmDelete(string id, string name)
    {
        var col = new VBoxContainer { CustomMinimumSize = new Vector2(620, 0) }; col.AddThemeConstantOverride("separation", 20);
        var l = Style.Text("删除卡组“" + name + "”？\n删掉的卡组会留在 userdata/decks-trash.json，需要时可以找回。", 24, Style.Paper, Style.Kai); l.AutowrapMode = TextServer.AutowrapMode.WordSmart; col.AddChild(l);
        var row = new HBoxContainer { Alignment = BoxContainer.AlignmentMode.End }; row.AddThemeConstantOverride("separation", 10); col.AddChild(row);
        Control scrim = null!;
        row.AddChild(Ui.Button("取消", () => App.I.CloseModal(scrim), 22));
        row.AddChild(Ui.Button("删除", async () => { App.I.CloseModal(scrim); await BuildDecks.Remove(id); if (selected == id) selected = null; await Reload(); App.I.Toast("已删除"); }, 22, true));
        scrim = App.I.Modal(Ui.Panel(col, 34));
    }

    // 导出 (Yunke 11-22): the deck as a .json file (ArkhamDB / build format, which 导入 reads back), named after the deck,
    // through the system's save dialog
    string ExportName(string id)
    {
        var name = S.Decks.Has(id) ? BuildDecks.ShownName(id, J.String(S.Decks.Get(id), "name")) : "";
        foreach (var c in Path.GetInvalidFileNameChars()) name = name.Replace(c, ' ');
        name = name.Trim();
        return (name.Length > 0 ? name : "牌组") + ".json";
    }
    void ExportFile(string id)
    {
        if (!S.Decks.Has(id)) return;
        var fd = new FileDialog { FileMode = FileDialog.FileModeEnum.SaveFile, Access = FileDialog.AccessEnum.Filesystem, Filters = new[] { "*.json ; 牌组 JSON" }, Title = "导出牌组", UseNativeDialog = true, CurrentFile = ExportName(id) };
        var docs = OS.GetSystemDir(OS.SystemDir.Documents); if (docs.Length > 0) fd.CurrentDir = docs;
        AddChild(fd);
        fd.FileSelected += path => { fd.QueueFree(); WriteDeck(id, path.EndsWith(".json", StringComparison.OrdinalIgnoreCase) ? path : path + ".json"); };
        fd.Canceled += () => fd.QueueFree();
        fd.PopupCentered(new Vector2I(1100, 700));
    }
    void WriteDeck(string id, string path)
    {
        try
        {
            // what build's import needs (its DeckSchema): a deck a campaign saved lacks description_md / tags / version, and
            // 导入 would refuse the file; the name as the workshop shows it (no " 通关")
            var deck = System.Text.Json.Nodes.JsonNode.Parse(S.Decks.Get(id).GetRawText())!.AsObject();
            deck["name"] = BuildDecks.ShownName(id, deck["name"]?.ToString() ?? "");
            foreach (var (key, value) in new[] { ("description_md", ""), ("tags", ""), ("version", "1.0") })
                if (deck[key] is null) deck[key] = value;
            if (deck["meta"] is null) deck["meta"] = "{}";
            var now = DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'");
            if (deck["date_creation"] is null) deck["date_creation"] = now;
            if (deck["date_update"] is null) deck["date_update"] = now;
            var text = deck.ToJsonString(new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
            File.WriteAllText(path, text);
            App.I.Toast("已导出：" + Path.GetFileName(path));
            if (App.I.QaRunning) GD.Print("QA_EXPORT " + path + " " + text.Length);
        }
        catch (Exception e) { App.I.Toast("导出失败：" + e.Message, true); }
    }

    // ───────── the bar's switches and the class toggles (Yunke 11-22)
    static readonly (string Icon, string Name)[] Sorts = { ("sort-time", "最近更新"), ("sort-name", "名称"), ("sort-inv", "调查员") };
    static readonly (string Icon, string Name)[] Kinds = { ("cards", "全部卡组"), ("deck-new", "初始卡组"), ("victory", "通关卡组") };
    void Switches()
    {
        Ui.Clear(switches);
        Button Cycle((string Icon, string Name)[] states, int at, string what, Action<int> set)
        {
            int next = (at + 1) % states.Length;
            return LineIcon.Button(states[at].Icon, what + "：" + states[at].Name + "（点击换成" + states[next].Name + "）", () => { set(next); Switches(); Fill(); }, 50, at != 0);
        }
        switches.AddChild(Cycle(Sorts, sortMode, "排序", k => sortMode = k));
        switches.AddChild(Cycle(Kinds, kindMode, "显示", k => kindMode = k));
    }
    static readonly string[] Classes = { "guardian", "seeker", "rogue", "mystic", "survivor", "neutral" };
    static Color ClassColor(string f) => f switch
    {
        "guardian" => new Color("3f6fb0"), "seeker" => new Color("b0743f"), "rogue" => new Color("3f8f5a"),
        "mystic" => new Color("6e4fa0"), "survivor" => new Color("b04848"), _ => new Color("9a948a"),
    };
    void ClassToggles()
    {
        Ui.Clear(classRow);
        foreach (var f in Classes)
        {
            bool on = classes.Contains(f);
            var b = Ui.MinimalButton("", () => { if (!classes.Remove(f)) classes.Add(f); ClassToggles(); Fill(); }, 18, on);
            b.CustomMinimumSize = new Vector2(40, 40); b.TooltipText = BuildDecks.FactionName(f) + (on ? "（点击取消）" : "（点击筛选，可多选）");
            // on: the class's colour round it and under its symbol; off: dim
            var ink = on ? ClassColor(f).Lightened(0.55f) : new Color(0.93f, 0.88f, 0.78f, 0.38f);
            if (on)
                foreach (var (state, a) in new[] { ("normal", 0.42f), ("hover", 0.55f), ("pressed", 0.6f) })
                {
                    var sb = new StyleBoxFlat { BgColor = new Color(ClassColor(f).Darkened(0.2f), a), BorderColor = ClassColor(f).Lightened(0.35f), AntiAliasing = true };
                    sb.SetBorderWidthAll(2); sb.SetCornerRadiusAll(10); b.AddThemeStyleboxOverride(state, sb);
                }
            // the same size on and off (Yunke 11-22): no padding from any state's box, the button's own size only
            b.CustomMinimumSize = new Vector2(44, 40);
            foreach (var state in new[] { "normal", "hover", "pressed", "disabled", "focus", "hover_pressed" })
                if (b.HasThemeStyleboxOverride(state) && b.GetThemeStylebox(state).Duplicate() is StyleBox box)
                {
                    box.ContentMarginLeft = box.ContentMarginRight = box.ContentMarginTop = box.ContentMarginBottom = 0;
                    b.AddThemeStyleboxOverride(state, box);
                }
            Control mark;
            if (Style.Glyph.ContainsKey(f)) { var g = Ui.Glyph(f, 22, ink); g.HorizontalAlignment = HorizontalAlignment.Center; g.VerticalAlignment = VerticalAlignment.Center; mark = g; }
            else if (Style.ClassIcons is not null) { var g = Style.Text(Style.NeutralGlyph, 21, ink, Style.ClassIcons); g.HorizontalAlignment = HorizontalAlignment.Center; g.VerticalAlignment = VerticalAlignment.Center; mark = g; }   // neutral's own mark (Yunke 11-22)
            else mark = new LineIcon { Kind = "neutral", Ink = ink, Stroke = 1.8f };
            mark.MouseFilter = MouseFilterEnum.Ignore; b.AddChild(mark); mark.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
            if (mark is LineIcon) { mark.OffsetLeft = mark.OffsetTop = 9; mark.OffsetRight = mark.OffsetBottom = -9; }
            classRow.AddChild(b);
        }
    }
    IEnumerable<string> ShownIds() => list.GetChildren().OfType<Control>().Select(c => c.HasMeta("deck_id") ? (string)c.GetMeta("deck_id") : "?");

    // The chosen deck's name over the preview (Yunke 11-22): one line, cut with "…" when long (the full name on hover); a click
    // turns it into a text box in place — Enter or leaving it saves, Esc puts the name back.
    Control TitleName(string id, string name, float width)
    {
        // (a wrapping label needs its width given: in a container it measures itself at its minimum width and wraps every word)
        var holder = new MarginContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill, CustomMinimumSize = new Vector2(width, 52) };
        void Show()
        {
            Ui.Clear(holder);
            // a long name goes on to the next line (Yunke 11-22: not cut, not smaller); a very long one stops at three lines
            var l = Style.Title(name, 34); l.AutowrapMode = TextServer.AutowrapMode.WordSmart;
            if (App.I.QaRunning) Callable.From(() => { if (IsInstanceValid(l)) GD.Print("QA_TITLE size=" + l.Size + " lines=" + l.GetLineCount() + " holder=" + holder.Size); }).CallDeferred();
            l.CustomMinimumSize = new Vector2(width, 0); l.SizeFlagsHorizontal = SizeFlags.Fill; l.VerticalAlignment = VerticalAlignment.Center;
            l.MouseFilter = MouseFilterEnum.Stop; l.MouseDefaultCursorShape = CursorShape.Ibeam; l.TooltipText = name + "\n（点击改名）";
            l.GuiInput += e => { if (e is InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Left }) { l.AcceptEvent(); Edit(); } };
            holder.AddChild(l);
        }
        void Edit()
        {
            Ui.Clear(holder);
            var box = new LineEdit { Text = name, CustomMinimumSize = new Vector2(0, 52), SizeFlagsHorizontal = SizeFlags.ExpandFill, PlaceholderText = "牌组名" };
            box.AddThemeFontSizeOverride("font_size", 30); if ((Style.Display ?? Style.Kai) is { } font) box.AddThemeFontOverride("font", font);
            box.AddThemeColorOverride("font_color", Style.HoverText);
            box.AddThemeStyleboxOverride("normal", Ui.SoftBox(0.55f, true, 12, 4)); box.AddThemeStyleboxOverride("focus", Ui.SoftBox(0.65f, true, 12, 4));
            bool done = false;
            async void Commit()
            {
                if (done) return; done = true;
                var t = box.Text.Trim();
                if (t.Length == 0 || t == name) { Show(); return; }
                try { await BuildDecks.Edit("name", id, t); await BuildDecks.Save(id); name = t; App.I.Toast("已改名"); if (App.I.QaRunning) GD.Print("QA_RENAME " + id + " " + t); await Reload(); }
                catch (Exception e) { App.I.Toast("改名失败：" + e.Message, true); Show(); }
            }
            void Cancel() { if (done) return; done = true; Show(); if (App.I.QaRunning) GD.Print("QA_RENAME cancel " + id); }
            box.TextSubmitted += _ => Commit();
            box.FocusExited += Commit;
            box.GuiInput += e => { if (e is InputEventKey { Pressed: true, Keycode: Key.Escape }) { box.AcceptEvent(); Cancel(); } };
            holder.AddChild(box); box.GrabFocus(); box.SelectAll();
            renameBox = box;
        }
        Show();
        titleEdit = Edit;
        return holder;
    }
    Action? titleEdit; LineEdit? renameBox;

    // the laurel of a finished campaign's deck; the pointer resting on it shows which campaign, how it ended, the trauma
    Control Badge(string id, float size)
    {
        var ic = new LineIcon { Kind = "victory", Ink = new Color("e0b56a"), CustomMinimumSize = new Vector2(size, size), MouseFilter = MouseFilterEnum.Stop, SizeFlagsVertical = SizeFlags.ShrinkCenter };
        ic.SetMeta("badge_of", id);
        ic.MouseEntered += () => Peek.Schedule(ic, () => BadgeText(id));
        ic.MouseExited += Peek.HideSoon;
        return ic;
    }
    Control BadgeText(string id)
    {
        var raw = S.Decks.Get(id);
        string camp = BuildDecks.MetaString(raw, BuildDecks.CampaignKey), res = BuildDecks.MetaString(raw, BuildDecks.ResultKey), trauma = BuildDecks.MetaString(raw, BuildDecks.TraumaKey);
        var col = new VBoxContainer { MouseFilter = MouseFilterEnum.Ignore }; col.AddThemeConstantOverride("separation", 6);
        col.AddChild(Style.Text(string.Join(" · ", new[] { "战役通关卡组", camp, res }.Where(x => x.Length > 0)), 22, Style.HoverText, Style.Kai));
        if (trauma.Length > 0) col.AddChild(Style.Text(trauma, 19, Style.PaperDim, Style.Kai));
        return col;
    }
}
