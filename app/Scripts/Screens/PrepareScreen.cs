using Godot;
using Gathering;
using System.Text.Json;
using Path = System.IO.Path;
using File = System.IO.File;

namespace ArkhamUI;

// Seat setup: 1-4 investigators, one deck each, difficulty. Starts a new rules game.
public partial class PrepareScreen : Control
{
    readonly List<(string Code, string DeckKey)> seats = new();
    Container slots = null!;   // investigator seats: a grid of up to four
    Button start = null!;
    string difficulty = "Standard";
    Label status = null!;
    // the campaign being set up (CycleScreen): its chapters, investigators, chaos bag and locale section
    readonly CampaignDef camp;
    public PrepareScreen() : this(Campaigns.Zealot) { }
    public PrepareScreen(CampaignDef camp) { this.camp = camp; }
    // back from the deck workshop: the seats as they were, and the picker reopened on the investigator who was being looked at
    (string Code, string DeckKey)[]? restoreSeats; (SelectionStage.Scene Scene, string Code)? reopen;
    void OpenWorkshopHere(SelectionStage.Scene? scene, string code)
    {
        var keep = seats.ToArray(); var c = camp;
        App.I.WorkshopReturn = () => { var p = new PrepareScreen(c) { restoreSeats = keep, reopen = scene is null ? null : (scene, code) }; App.I.Go(p); };
        var ready = System.Threading.Tasks.Task.WhenAll(BuildDecks.List(), TabooChip.Sets());
        App.I.HoldNextDip(ready, "正在整理牌组…"); App.I.Go(new DeckLibraryScreen());
    }

    public override void _Ready()
    {
        var S = App.I.S!; difficulty = S.Settings.Difficulty;
        if (Guide.On) difficulty = "Easy";   // 新手教程: 简单
        if (!camp.Difficulties.Contains(difficulty)) difficulty = camp.Difficulties.Contains("Standard") ? "Standard" : camp.Difficulties[0];
        MouseFilter = MouseFilterEnum.Stop;
        // blurred, darkened scene with dark fades — same backdrop as the story screens
        // a plain tablecloth (Yunke 11-18, instead of the blurred scene): the html table's felt, tiled; a patterned cloth
        // (Cthulhu motifs) replaces it when the picture arrives (docs/ART-PROMPTS-20261118.md)
        // the game table's own desk painting (Yunke 11-20: "这个界面的背景也改成游戏桌面的那个背景"), as the table shows it; the
        // dark fades below keep the text readable on it
        var bg = new TextureRect { Texture = Ui.Bg("desk-table") ?? Ui.Tex("res://assets/ui/tablecloth-felt.png"), StretchMode = TextureRect.StretchModeEnum.KeepAspectCovered, ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize, MouseFilter = MouseFilterEnum.Ignore, Modulate = new Color(0.86f, 0.84f, 0.82f) };
        AddChild(bg); bg.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        var fade = new Control { MouseFilter = MouseFilterEnum.Ignore }; AddChild(fade); fade.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        Ui.EdgeFade(fade, new Color(0.01f, 0.01f, 0.01f, 0.8f), 520, 260, 0, 420);
        var margin = new MarginContainer(); AddChild(margin); margin.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        margin.AddThemeConstantOverride("margin_left", 104); margin.AddThemeConstantOverride("margin_right", 70);   // the back icon sits left of the title
        margin.AddThemeConstantOverride("margin_top", 46); margin.AddThemeConstantOverride("margin_bottom", 40);
        var col = new VBoxContainer(); col.AddThemeConstantOverride("separation", 22); margin.AddChild(col);
        var head = new HBoxContainer(); col.AddChild(head);
        var titles = new VBoxContainer(); head.AddChild(titles);
        titles.AddChild(Style.Title("准备调查", 48));
        titles.AddChild(Style.Text(camp.Cycle + " · " + camp.Name + (camp.IsScenario ? " · 单独剧本" : " · 战役（关卡之间可升级牌组）") + (camp.Beta ? "  · BETA" : ""), 24, Style.Brass, Style.Kai));
        // a campaign with a return-to version (11-22, Yunke: "重返和原版的同剧本用同一个入口，但要有切换选项"): 原版 / 重返
        var original = camp.ReturnOf.Length > 0 ? Campaigns.ById(camp.ReturnOf) : camp;
        if (original.Return is { } ret)
        {
            head.AddChild(Ui.Spacer(40, 0));
            var vg = new ButtonGroup(); var vbox = new HBoxContainer(); vbox.AddThemeConstantOverride("separation", 8); head.AddChild(vbox);
            foreach (var (def, name) in new[] { (original, "原版"), (ret, "重返") })
            {
                var vb = new Button { Text = name, ToggleMode = true, ButtonGroup = vg, ButtonPressed = def.Id == camp.Id, CustomMinimumSize = new Vector2(96, 48), FocusMode = FocusModeEnum.None, MouseDefaultCursorShape = CursorShape.PointingHand, SizeFlagsVertical = SizeFlags.ShrinkCenter };
                vb.AddThemeFontSizeOverride("font_size", 22); if (Style.Kai is not null) vb.AddThemeFontOverride("font", Style.Kai);
                var von = Ui.SoftBox(0.6f, true); var voff = Ui.SoftBox(0.25f, false);
                vb.AddThemeStyleboxOverride("pressed", von); vb.AddThemeStyleboxOverride("hover_pressed", von); vb.AddThemeStyleboxOverride("normal", voff);
                vb.AddThemeColorOverride("font_pressed_color", Style.Amber);
                if (!def.Starts) { vb.Disabled = true; vb.TooltipText = "规则核心暂不支持：" + def.Reason; }
                var target = def;
                vb.Toggled += on => { if (on && target.Id != camp.Id) { var keep = seats.ToArray(); App.I.Go(new PrepareScreen(target) { restoreSeats = keep }); } };
                vbox.AddChild(vb);
            }
            QaScript.Register("prepare-variant", v => { var target = v == "return" ? ret : original; if (target.Id != camp.Id) { var keep = seats.ToArray(); App.I.Go(new PrepareScreen(target) { restoreSeats = keep }); } });
        }
        head.AddChild(Ui.Spacer(0, 0, true));
        var diffBox = new HBoxContainer(); diffBox.AddThemeConstantOverride("separation", 10); head.AddChild(diffBox);
        diffBox.AddChild(Style.Text("难度", 24, Style.PaperDim));
        var group = new ButtonGroup();
        foreach (var (key, name) in new[] { ("Easy", "简单"), ("Standard", "标准"), ("Hard", "困难"), ("Expert", "专家") }.Where(d => camp.Difficulties.Contains(d.Item1)))
        {
            var b = new Button { Text = name, ToggleMode = true, ButtonGroup = group, ButtonPressed = key == difficulty, CustomMinimumSize = new Vector2(96, 48), FocusMode = FocusModeEnum.None, MouseDefaultCursorShape = CursorShape.PointingHand };
            b.AddThemeFontSizeOverride("font_size", 22); if (Style.Kai is not null) b.AddThemeFontOverride("font", Style.Kai);
            var on = Ui.SoftBox(0.6f, true); var off = Ui.SoftBox(0.25f, false);   // soft-edged (Yunke 11-18: every button)
            b.AddThemeStyleboxOverride("pressed", on); b.AddThemeStyleboxOverride("hover_pressed", on); b.AddThemeStyleboxOverride("normal", off);
            b.AddThemeColorOverride("font_pressed_color", Style.Amber);
            b.Toggled += on => { if (on) { difficulty = key; S.Settings.Difficulty = key; S.Settings.Save(); RebuildChaos(); } };
            if (Guide.On && key != "Easy") { b.Disabled = true; b.TooltipText = "新手教程固定为简单难度"; }
            if (Guide.On) diffButtons.Add(b);
            diffBox.AddChild(b);
        }
        // a campaign is played from its start, chapter after chapter (Yunke 11-18; html version: FullCampaign): no chapter
        // picker and no chapter intro here — each intro and ending comes from the rules core in play, chosen by the log
        // 剧本模式 / 单场冒险模式（使用剧本关卡） (PrepareScreen.Options): the route, or the chapters to pick one from
        var modeChoice = ModeChoice(); if (modeChoice.GetChildCount() > 0) col.AddChild(modeChoice);
        routeHost = new VBoxContainer(); col.AddChild(routeHost); RebuildRoute();
        // how the seats are played (Yunke 11-22): on this computer in turn, or online over EOS — each player picks their own
        // investigator in the room; the online choice is shown but disabled until EOS is connected (Online.cs)
        var modeRow = new HBoxContainer(); modeRow.AddThemeConstantOverride("separation", 10); col.AddChild(modeRow);
        modeRow.AddChild(Style.Text("调查员（1–4 名）", 26, Style.Paper, Style.Kai));
        modeRow.AddChild(Ui.Spacer(16, 0));
        var local = Ui.MinimalButton("本机轮流操作", () => { }, 20, true); local.CustomMinimumSize = new Vector2(0, 42); modeRow.AddChild(local);
        // the room's seats are this party's investigators (pick them first); the host starts the game from the room
        // every player brings their own deck in the room (Yunke 11-22), so the room has four seats whatever is chosen here
        var host = Ui.MinimalButton("EOS 联机主持", () =>
        {
            OnlineGame.CreateNewGame = CreateOnlineGame;
            OnlineDialogs.Host(camp.Name, null, 4);
        }, 20); host.CustomMinimumSize = new Vector2(0, 42);
        Online.Gate(host); modeRow.AddChild(host);
        if (Guide.On) host.Visible = false;
        // investigators on the left, the chosen difficulty's chaos bag and its penalties on the right
        var middle = new HBoxContainer { SizeFlagsVertical = SizeFlags.ExpandFill }; middle.AddThemeConstantOverride("separation", 36); col.AddChild(middle);
        // investigators as a 2 x 2 grid on the left; the chaos bag keeps its own column on the right
        slots = new GridContainer { Columns = 2, SizeFlagsVertical = SizeFlags.ExpandFill, SizeFlagsHorizontal = SizeFlags.ExpandFill };
        slots.AddThemeConstantOverride("h_separation", 22); slots.AddThemeConstantOverride("v_separation", 8); middle.AddChild(slots);
        // each seat scrolls its own text (SeatCard), so four seats never push 开始调查 off the screen (Yunke 11-23)
        var right = new VBoxContainer { CustomMinimumSize = new Vector2(600, 0) }; right.AddThemeConstantOverride("separation", 12); middle.AddChild(right);
        chaosBox = new VBoxContainer(); chaosBox.AddThemeConstantOverride("separation", 12); right.AddChild(chaosBox);
        RebuildChaos();
        right.AddChild(OptionsBox());   // 使用塔罗牌解牌 · 恩赐与最后通牒 (PrepareScreen.Options)
        var bottom = new HBoxContainer(); bottom.AddThemeConstantOverride("separation", 18); col.AddChild(bottom);
        // back to the main menu: the round icon in the top-left corner, and Esc (Yunke 11-21)
        Ui.BackButton(this, "返回选择战役", () => App.I.Go(new CycleScreen()));   // back one level: the campaign choice (Yunke 11-22)
        var workshop = Ui.Button("牌组工坊", () => OpenWorkshopHere(null, ""), 24); bottom.AddChild(workshop);   // its back returns here
        if (Guide.On) workshop.Visible = false;
        status = Style.Text("", 22, Style.PaperDim); status.SizeFlagsHorizontal = SizeFlags.ExpandFill; status.HorizontalAlignment = HorizontalAlignment.Center; bottom.AddChild(status);
        status.AutowrapMode = TextServer.AutowrapMode.WordSmart; status.VerticalAlignment = VerticalAlignment.Center;   // a deck's refusal is a sentence or two (bug #36)
        start = Ui.Button("开始调查", () => _ = Start(), 30, true);
        start.CustomMinimumSize = new Vector2(360, 74); bottom.AddChild(start);
        if (restoreSeats is not null) seats.AddRange(restoreSeats);
        Rebuild();
        if (reopen is { } ro) Callable.From(() => { var st = OpenPicker(ro.Scene); st.Focus(ro.Code); }).CallDeferred();
        QaScript.Register("prepare-add", a => { var p = a.Split(','); AddSeat(p[0], p.Length > 1 ? p[1] : ""); });   // QA: prepare-add:<code>[,<deck key>]
        QaScript.Register("prepare-start", async _ => await Start());
        OnlineGame.CreateNewGame = CreateOnlineGame;   // QA online-host-room: the room's way of making the game
        // QA: a new online game over the local link (OnlineGame.QaBoot), seat 2 played by the loop's guest
        QaScript.Register("online-host-new", async _ =>
        {
            if (OnlineGame.Link is null || await CreateGame() is not { } id) { GD.PrintErr("QA online-host-new failed"); return; }
            var st = await App.I.S!.Backend.Open(id);
            var assign = st.Seats.ToDictionary(x => x.Player, x => x.Number == 2 ? TcpLink.GuestId : "");
            var names = st.Seats.ToDictionary(x => x.Player, x => x.Number == 2 ? "玩家二" : "主持");
            for (int i = 0; i < 200 && OnlineGame.Link is TcpLink { Connected: false }; i++) await Task.Delay(100);
            OnlineGame.BeginHost(id, OnlineGame.Link, "", assign, names);
            App.I.Go(new TableScreen(id));
        });
        // QA only (never on screen: players always start a campaign from its beginning): start the campaign at a later chapter,
        // so the rules audit can reach chapters the autoplay cannot win its way to (迷失时空 after 毁灭之地)
        QaScript.Register("prepare-start-at", async code => await Start(code));
        // QA only: one chapter as a standalone scenario (the core does not start the Dunwich campaign at 迷失时空)
        QaScript.Register("prepare-standalone", async code => await Start(code, standalone: true));
        QaScript.Register("prepare-pick", _ => OpenPicker());
    }

    // The campaign's route: its chapters in order, not clickable. Where the story branches (the Dunwich Legacy's first two
    // chapters, its second interlude) the rules core decides in play.
    Control Route()
    {
        var col = new VBoxContainer(); col.AddThemeConstantOverride("separation", 10);
        col.AddChild(Style.Text(camp.IsScenario ? "剧本" : "战役路线", 26, Style.HoverText, Style.Kai));
        // fixed rows of at most five steps (a wrapping HFlowContainer without a fixed width hung the layout for the
        // Dunwich Legacy's ten steps)
        HBoxContainer flow = null!;
        var steps = new List<string>();
        if (camp.PrologueName.Length > 0) steps.Add(camp.PrologueName);
        steps.AddRange(camp.Chapters.Select(c => camp.IsScenario ? c.Name : camp.Label(c.Code)));
        if (camp.EpilogueName.Length > 0) steps.Add(camp.EpilogueName);
        for (int i = 0; i < steps.Count; i++)
        {
            if (i % 5 == 0) { flow = new HBoxContainer(); flow.AddThemeConstantOverride("separation", 8); col.AddChild(flow); }
            if (i % 5 != 0) { var arrow = Style.Text("›", 22, new Color(Style.Brass, 0.7f), Style.Kai); flow.AddChild(arrow); }
            else if (i > 0) { var arrow = Style.Text("›", 22, new Color(Style.Brass, 0.7f), Style.Kai); flow.AddChild(arrow); }
            flow.AddChild(Style.Text(steps[i], 21, i == 0 ? Style.Amber : Style.Paper, Style.Kai));
        }
        // no route note under the steps (Yunke 11-23: "三章依次进行……" removed, so four seats fit above 开始调查)
        return col;
    }

    void Rebuild()
    {
        Ui.Clear(slots);
        var S = App.I.S!;
        for (int i = 0; i < 4; i++)
        {
            if (i < seats.Count) slots.AddChild(SeatCard(i));
            else if (i == seats.Count && !(Guide.On && seats.Count >= 2)) slots.AddChild(AddCard());   // 新手教程: two seats only
            else slots.AddChild(Empty());
        }
        start.Disabled = seats.Count == 0 || seats.Any(s => s.DeckKey.Length == 0);
        status.Text = seats.Count == 0 ? "先在画面中选择一名调查员" : seats.Count + " 名调查员 · " + DifficultyName(difficulty) + (standaloneMode ? " · " + camp.Label(standaloneChapter) : "");
        if (camp.NeedsInvestigator.Length > 0 && !seats.Any(x => x.Code == camp.NeedsInvestigator))
        { start.Disabled = true; status.Text = "这个挑战剧本需要" + S.Name(camp.NeedsInvestigator) + "参加"; }
        if (camp.UiGap.Length > 0) { start.Disabled = true; status.Text = "暂不能开局：" + camp.UiGap; }
        if (!camp.Starts) { start.Disabled = true; status.Text = "规则核心暂不支持这个" + (camp.IsScenario ? "剧本" : "战役") + "：" + StartsText(camp.Reason); }
        if (Guide.On) { start.Disabled = seats.Count < 2; Callable.From(GuideStep).CallDeferred(); }
    }
    VBoxContainer chaosBox = null!;

    // The chaos bag for the chosen difficulty, and what the scenario's own tokens do on it.
    void RebuildChaos()
    {
        Ui.Clear(chaosBox);
        var faces = BagWithUltimatums(ChaosBagRules.Bag(difficulty, camp.Id));   // 失败 / 破碎承诺的最后通牒 (PrepareScreen.Options)
        chaosBox.AddChild(Style.Text("混沌袋 · " + DifficultyName(difficulty) + "（" + faces.Length + " 枚）", 24, Style.Paper, Style.Kai));
        var row = new GridContainer { Columns = 6 }; row.AddThemeConstantOverride("h_separation", 14); row.AddThemeConstantOverride("v_separation", 8); chaosBox.AddChild(row);
        row.Columns = 8;
        foreach (var f in faces) row.AddChild(Ui.Image(Ui.Token(f), new Vector2(60, 60)));   // every token on its own, no "×2"
        // each chapter's own symbol tokens (skull, cultist…) are on its scenario reference card: the table shows them in each
        // skill test, with the values the rules core gives for that test
        var hint = Style.Text("骷髅、异教徒等符号标记的效果每章不同，游戏中检定时显示本章的效果。", 18, Style.PaperDim, Style.Kai);
        hint.AutowrapMode = TextServer.AutowrapMode.WordSmart; hint.CustomMinimumSize = new Vector2(600, 0); chaosBox.AddChild(hint);
    }

    // the core's reason in words (the start probe's message)
    static string StartsText(string reason) => reason.Contains("Advanced settings") ? "这个剧本的高级设置尚未实现" : reason.Length > 0 ? reason : "开局未通过检查";

    static string DifficultyName(string k) => k switch { "Easy" => "简单", "Hard" => "困难", "Expert" => "专家", _ => "标准" };

    Control Frame(Control content, bool bright)
    {
        var p = new PanelContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill, SizeFlagsVertical = SizeFlags.ExpandFill };
        // a solid dark patch whose edges glow softly away, no hard corner (Yunke 11-18 r6 #3)
        p.AddThemeStyleboxOverride("panel", Ui.SoftPanel(bright ? 0.62f : 0.3f, 18, 16));
        p.AddChild(content); return p;
    }
    Control Empty() => Frame(new CenterContainer { Name = "e" }, false);

    Control AddCard()
    {
        var c = new VBoxContainer { Alignment = BoxContainer.AlignmentMode.Center };
        var b = Ui.Button("＋ 选择调查员", OpenPicker, 26, true); b.CustomMinimumSize = new Vector2(300, 70); b.SizeFlagsHorizontal = SizeFlags.ShrinkCenter;
        addButton = b;
        c.AddChild(b);
        var hint = Style.Text(seats.Count == 0 ? "进入群像，点选人物" : "可再添加一名同伴", 20, Style.PaperDim); hint.HorizontalAlignment = HorizontalAlignment.Center; c.AddChild(hint);
        return Frame(c, true);
    }

    void OpenPicker()
    {
        string box = camp.ReturnOf.Length > 0 ? camp.ReturnOf : camp.Id;
        if (box is "01" or "02" or "03" || camp.Investigators.Length == 0) { OpenPicker(box == "02" ? SelectionStage.Dunwich : box == "03" ? SelectionStage.Carcosa : SelectionStage.Core); return; }
        // a later box: straight into its own group picture (Yunke 11-23: "直接用群像选择就行了（即使是手机版）")
        if (SelectionStage.All.FirstOrDefault(s => s.Box == box) is { } own) { OpenPicker(own); return; }
        // a box with no group picture yet: its investigators side by side, and the painted scenes for everyone else
        if (App.I.QaRunning) GD.Print("QA_PICKER camp=" + camp.Id + " lineup=" + string.Join(",", camp.Investigators));
        var line = new LineupStage(camp.Investigators, seats.Select(s => s.Code), AddSeat) { Label = camp.Cycle + " · " + camp.Name, Others = () => OpenPicker(SelectionStage.Core) };
        App.I.Overlay.AddChild(line); line.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
    }

    // The group picture of one box. Investigators of both boxes can play either campaign, so the picker can switch to the
    // other box's group (Yunke 11-18 #2); it opens on the campaign's own box.
    SelectionStage OpenPicker(SelectionStage.Scene scene)
    {
        if (App.I.QaRunning) GD.Print("QA_PICKER camp=" + camp.Id + " scene=" + scene.Box);
        var stage = new SelectionStage(seats.Select(s => s.Code), AddSeat, scene);
        if (Guide.On) GuidePicker(stage);
        stage.SwitchTo = other => { stage.QueueFree(); OpenPicker(other); };
        stage.NewDeck = code => OpenWorkshopHere(scene, code);   // the notebook's +: the workshop, and back to this picker
        App.I.Overlay.AddChild(stage); stage.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        return stage;
    }

    // deckKey: the deck chosen in the selection notebook; empty means the first deck of this investigator.
    void AddSeat(string code, string deckKey)
    {
        if (seats.Count >= 4 || seats.Any(s => s.Code == code)) return;
        var deck = Guide.On ? Tutorial.DeckKey(code) : deckKey.Length > 0 ? deckKey : App.I.S!.Decks.For(code).Select(d => d.Key).FirstOrDefault() ?? "";
        seats.Add((code, deck)); Rebuild();
        // the new seat arrives (Yunke 11-18: a little effect on the way back from the picker): it rises in from slightly
        // smaller and transparent, with a brief brightening, after the picker has faded out
        if (slots.GetChildCount() >= seats.Count && slots.GetChild(seats.Count - 1) is Control card && App.I.S?.Settings.ReducedMotion != true)
        {
            card.Modulate = new Color(1, 1, 1, 0);
            GetTree().CreateTimer(0.05).Timeout += () =>
            {
                if (!IsInstanceValid(card)) return;
                card.PivotOffset = card.Size / 2; card.Scale = new Vector2(0.9f, 0.9f);
                var tw = card.CreateTween(); tw.TweenInterval(0.2);
                tw.TweenProperty(card, "modulate", new Color(1.35f, 1.25f, 1.05f, 1f), 0.3f).SetTrans(Tween.TransitionType.Sine).SetEase(Tween.EaseType.Out);
                tw.Parallel().TweenProperty(card, "scale", Vector2.One, 0.45f).SetTrans(Tween.TransitionType.Back).SetEase(Tween.EaseType.Out);
                tw.TweenProperty(card, "modulate", Colors.White, 0.5f);
            };
        }
    }

    // A seat is a small horizontal card: the investigator's face on the left, the name, deck and remove on the right.
    // Horizontal keeps the face whole even when there are two rows of seats.
    Control SeatCard(int i)
    {
        var S = App.I.S!; var (code, deckKey) = seats[i];
        // the face this deck plays: a parallel front (deck meta alternate_front, chosen in the workshop) brings its own
        // picture, skills, health, sanity and ability
        string face = Face(S, deckKey.Length > 0 ? deckKey : S.Decks.For(code).Select(d => d.Key).FirstOrDefault() ?? "", code);
        var root = new HBoxContainer(); root.AddThemeConstantOverride("separation", 16);
        var pic = CardView.PortraitCard(face, 150); pic.MouseFilter = MouseFilterEnum.Stop;   // the portrait small card, whole (Yunke 11-18)
        pic.MouseDefaultCursorShape = CursorShape.PointingHand; pic.SizeFlagsVertical = SizeFlags.ShrinkBegin; root.AddChild(pic);
        Peek.Attach(pic, face);
        // the seat keeps the size of the empty slot it replaced (Yunke 11-23): a long ability scrolls inside the seat
        // instead of making the seat, and the page, taller
        var col = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill }; col.AddThemeConstantOverride("separation", 8);
        var colScroll = Ui.Scroll(col); colScroll.MouseFilter = MouseFilterEnum.Pass; root.AddChild(colScroll);
        var top = new HBoxContainer(); col.AddChild(top);
        top.AddChild(Style.Text("调查员 " + (i + 1) + (i == 0 ? "（领队）" : ""), 18, Style.Brass));
        top.AddChild(Ui.Spacer(0, 0, true));
        var remove = Ui.Button("移出", () => { seats.RemoveAt(i); Rebuild(); }, 17); top.AddChild(remove);
        if (Guide.On) remove.Visible = false;
        col.AddChild(Style.Text(S.Name(code), 28, Style.HoverText, Style.Kai));
        var pick = new OptionButton { FitToLongestItem = false, ClipText = true }; Ui.Soften(pick); pick.AddThemeFontSizeOverride("font_size", 19);
        var keys = S.Decks.For(code).Select(d => d.Key).ToList();
        foreach (var k in keys) pick.AddItem(J.String(S.Decks.Get(k), "name", k));
        pick.Selected = Math.Max(0, keys.IndexOf(deckKey));
        pick.ItemSelected += idx => { seats[i] = (code, keys[(int)idx]); Rebuild(); };
        if (Guide.On) { pick.Disabled = true; pick.TooltipText = "新手教程使用固定牌组"; }
        col.AddChild(pick);
        if (keys.Count == 0) col.AddChild(Style.Text("没有可用牌组，请先到牌组工坊建立。", 18, Style.Danger));
        // the seat's lower half was empty (Yunke 11-18: "有点空"): what this investigator brings — skills, health and sanity,
        // the deck's size, and the printed ability in the official wording
        if ((face == code ? SelectionStage.Stats.GetValueOrDefault(CoreCatalog.Normalize(code)) : PrintedStats(S, face)) is { } st)
        {
            var stats = new HBoxContainer(); stats.AddThemeConstantOverride("separation", 14); col.AddChild(stats);
            foreach (var (k, v, c) in new[] { ("willpower", st[0], Style.Willpower), ("intellect", st[1], Style.Intellect), ("combat", st[2], Style.Combat), ("agility", st[3], Style.Agility) })
            {
                var h = new HBoxContainer(); h.AddThemeConstantOverride("separation", 3);
                h.AddChild(Ui.Glyph(k, 22, c)); var n = Style.Text(v.ToString(), 24, Style.HoverText, Style.Symbols); n.VerticalAlignment = VerticalAlignment.Center; h.AddChild(n); stats.AddChild(h);
            }
            stats.AddChild(Ui.Spacer(6));
            foreach (var (k, v) in new[] { ("health", st[4]), ("sanity", st[5]) })
            {
                var h = new HBoxContainer(); h.AddThemeConstantOverride("separation", 3);
                var ic = Ui.Image(Ui.Stat(k == "sanity" ? "horror" : "health"), new Vector2(24, 24)); ic.SizeFlagsVertical = SizeFlags.ShrinkCenter; h.AddChild(ic);
                var n = Style.Text(v.ToString(), 24, Style.HoverText, Style.Symbols); n.VerticalAlignment = VerticalAlignment.Center; h.AddChild(n); stats.AddChild(h);
            }
        }
        if (keys.Count > 0 && S.Decks.Has(keys[pick.Selected]))
        {
            int cards = CoreCatalog.Slots(S.Decks.Get(keys[pick.Selected])).Values.Sum();
            col.AddChild(Style.Text("牌组 " + cards + " 张", 17, Style.PaperDim));
        }
        var ability = J.String(S.Card(CoreCatalog.Normalize(face)), "text");
        if (ability.Length > 0)
        {
            var r = CardReader.Rules(ability, 17); r.CustomMinimumSize = new Vector2(320, 0); r.SizeFlagsHorizontal = SizeFlags.ExpandFill; r.Modulate = new Color(1, 1, 1, 0.78f);
            col.AddChild(r);
        }
        return Frame(root, true);
    }

    // the investigator card a seat's deck plays: its parallel front when the workshop chose one, else the investigator
    static string Face(Services S, string deckKey, string code)
    {
        if (deckKey.Length == 0 || !S.Decks.Has(deckKey)) return code;
        try
        {
            var meta = StandaloneDecks.Metadata(S.Decks.Get(deckKey));
            return meta["alternate_front"] is System.Text.Json.Nodes.JsonValue v && v.TryGetValue<string>(out var f) && f.Length > 0 && S.Card(f).ValueKind == JsonValueKind.Object ? f : code;
        }
        catch { return code; }
    }
    // skills, health and sanity as printed on a card (willpower, intellect, combat, agility, health, sanity)
    static int[]? PrintedStats(Services S, string code)
    {
        var c = S.Card(code); if (c.ValueKind != JsonValueKind.Object) return null;
        return new[] { "skill_willpower", "skill_intellect", "skill_combat", "skill_agility", "health", "sanity" }.Select(k => J.Int(c, k)).ToArray();
    }

    // which scenario of the campaign to start at (the first chapter by default)

    async Task Start(string? qaChapter = null, bool standalone = false)
    {
        if (Guide.On)
        {
            start.Disabled = true; status.Text = "正在布置场景…";
            var tid = await Tutorial.StartGame();
            if (tid is { } t) { Guide.End(); App.I.Go(new TableScreen(t)); } else start.Disabled = false;
            return;
        }
        if (await CreateGame(qaChapter, standalone) is { } id) App.I.Go(new TableScreen(id));
    }

    // the game for this party (online: the host's room starts it, OnlineGame.HostStart)
    async Task<Guid?> CreateGame() => await CreateGame(null, false);

    // an online game: one seat per seated player; the host's own deck by key, the others' sent at ChooseDeck (OnlineGame.SeatDeck)
    async Task<Guid?> CreateOnlineGame(int count, string[] deckKeys)
    {
        try
        {
            var S = App.I.S!;
            var state = camp.IsScenario ? await S.Backend.Create(count, difficulty, null, camp.Standalone, Variants)
                : standaloneMode ? await S.Backend.Create(count, difficulty, null, standaloneChapter, Variants)
                : await S.Backend.Create(count, difficulty, camp.Id, null, Variants);
            SetupStore.Save(state.Id, deckKeys);
            return state.Id;
        }
        catch (Exception e) { App.I.Toast("开局失败：" + e.Message, true, 5); GD.PushError(e.ToString()); return null; }
    }
    async Task<Guid?> CreateGame(string? qaChapter, bool standalone)
    {
        if (seats.Count == 0) return null;
        start.Disabled = true; status.Text = "正在检查牌组…";
        try
        {
            var S = App.I.S!;
            // each deck is checked as the table will take it, before the game is made (bug #36: a deck the start refuses
            // made a game whose table stood empty, repeating the refusal, and the reason was nowhere on screen)
            foreach (var (code, key) in seats)
            {
                if (key.Length == 0 || !S.Decks.Has(key)) continue;
                if (await DeckProblem(S, S.Decks.Get(key), PlaysStandalone || standalone, Variants.Boons) is { } why)
                {
                    status.Text = S.Name(code) + "的牌组不能开局：" + why; start.Disabled = false;
                    App.I.Toast(S.Name(code) + "的牌组不能开局：" + why, true, 8);
                    if (App.I.QaRunning) GD.Print("QA_DECK_REFUSED " + code + " " + why);
                    return null;
                }
            }
            status.Text = "正在布置场景…";
            // a campaign: the scenarios follow one another with the campaign log, experience and upgrades
            // (starting at the first chapter lets the core run the prologue; a later chapter starts there with the log's defaults)
            var variants = Variants;   // 使用塔罗牌解牌, 恩赐与最后通牒 (PrepareScreen.Options)
            if (App.I.QaRunning) GD.Print("QA_CREATE standalone=" + (PlaysStandalone || standalone) + " chapter=" + (standaloneMode ? standaloneChapter : qaChapter ?? "") + " tarot=" + variants.Tarot + " boons=" + string.Join(",", variants.Boons));
            var state = camp.IsScenario
                ? await S.Backend.Create(seats.Count, difficulty, null, camp.Standalone, variants)   // a side story / fan-made scenario on its own
                : standaloneMode
                ? await S.Backend.Create(seats.Count, difficulty, null, standaloneChapter, variants)   // 单场冒险模式: one chapter on its own
                : await S.Backend.Create(seats.Count, difficulty, standalone ? null : camp.Id, qaChapter, variants);   // always from the campaign's start (qaChapter: QA audit only)
            SetupStore.Save(state.Id, seats.Select(s => s.DeckKey).ToArray());
            return state.Id;
        }
        catch (Exception e) { status.Text = "开局失败：" + e.Message; start.Disabled = false; GD.PushError(e.ToString()); return null; }
    }

    // Why a deck cannot start this game, or null: the same preparation and check the table makes (PrototypeBackend.SelectDeck).
    // standalone: a scenario played on its own, where the standalone rules hold (FFG Rules Reference, Standalone Mode: "A
    // player cannot include 50 or more experience worth of cards in a standalone deck", and a random basic weakness more for
    // each 10 experience). A campaign's start has no experience limit (Yunke 11-23, bug #36).
    static async Task<string?> DeckProblem(Services S, JsonElement deck, bool standalone, string[]? boons = null)
    {
        try
        {
            if (UltimatumDeckProblem(S, deck, boons ?? [], standalone) is { } ultimatum) return ultimatum;
            int spent = StandaloneDecks.Experience(S.Catalog, deck);
            if (standalone && spent > 49) return StandaloneDecks.StandaloneLimitText(spent) + "请在牌组工坊里换下一些升级卡。";
            var prepared = await S.Backend.PrepareDeck(deck, standalone);
            string inv = J.String(prepared, "investigator_code");
            var schema = await S.Backend.BuildSchema(inv);
            var validation = await S.Backend.ValidateDeck(inv, J.String(schema, "schemaId"), prepared, standalone);
            return J.String(validation, "status") == "valid" ? null : PrototypeBackend.Verdict(validation);
        }
        catch (Exception e) { return e.Message; }
    }
}

// Which saved deck each seat chose, so setup questions (ChooseDeck) are answered from the player's own choice.
public static class SetupStore
{
    static string FileFor(Guid id) => Path.Combine(App.I.DataRoot, "setups", id.ToString("D") + ".json");
    public static void Save(Guid id, string[] deckKeys) { System.IO.Directory.CreateDirectory(Path.GetDirectoryName(FileFor(id))!); File.WriteAllText(FileFor(id), JsonSerializer.Serialize(deckKeys)); }
    // a setup file cut short by a power cut reads as no setup (bug #7), not as an error on every operation
    public static string[] Load(Guid id)
    {
        try { return File.Exists(FileFor(id)) ? JsonSerializer.Deserialize<string[]>(File.ReadAllText(FileFor(id))) ?? [] : []; }
        catch (JsonException) { return []; }
    }
}
