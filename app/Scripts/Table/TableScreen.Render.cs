using Godot;
using Gathering;
using System.Text.Json;

namespace ArkhamUI;

public partial class TableScreen
{
    Dictionary<string, List<ChoicePresentation>> byAnchor = new();
    Control? actionPanelRef;

    static string PhaseName(string p) => p switch { "MythosPhase" => "神话阶段", "InvestigationPhase" => "调查阶段", "EnemyPhase" => "敌人阶段", "UpkeepPhase" => "整备阶段", "CampaignPhase" => "准备", _ => p };

    string renderParts = "";   // perf: time of each part of the last render (PERF render … board= hand= …)
    string? lastScenarioId;   // the scenario on the table last render: a change (chapter → campaign screen → next chapter) dips to black
    public void RefreshView() { if (model is not null && !busy) Render(); }

    void Render()
    {
        var w = System.Diagnostics.Stopwatch.StartNew();
        RenderTable();
        Gathering.PrototypeBackend.Perf("render ms=" + w.Elapsed.TotalMilliseconds.ToString("F0") + " nodes=" + GetTree().GetNodeCount() + renderParts + (Ui.Loads > 0 ? " texLoads=" + Ui.Loads + " texMs=" + Ui.LoadMs.ToString("F0") : "")); renderParts = ""; Ui.Loads = 0; Ui.LoadMs = 0;
        TutorialAfterRender();   // 新手教程 tips (TableScreen.Tutorial)
    }

    void RenderTable()
    {
        // which places are still unrevealed: every look at one shows only its unrevealed side (CardReader.Unrevealed)
        CardReader.Unrevealed.Clear();
        if (model is not null)
        {
            var shown = model.Locations.Where(model.Revealed).Select(l => CoreCatalog.Normalize(l.Code)).ToHashSet();
            foreach (var l in model.Locations) if (!model.Revealed(l) && !shown.Contains(CoreCatalog.Normalize(l.Code))) CardReader.Unrevealed.Add(CoreCatalog.Normalize(l.Code));
        }
        if (model is not null)
        {
            var sid = J.String(model.Scenario, "id");
            if (lastScenarioId is not null && sid != lastScenarioId) App.I.Dip();   // Yunke 11-20 #6
            lastScenarioId = sid;
        }
        anchors.Clear(); anchorFrames.Clear(); arrows.Clear(); ClosePopup(); commitBox = null; fastOnCard.Clear(); actionOnCard.Clear(); teamPics.Clear();
        // the enemy whose attack was asked last render is where damage or horror landing now came from
        if (attacking.Count > 0 && model is not null) { var names = attacking.Select(id => model.Enemies.FirstOrDefault(e => e.Id == id)).Where(e => e is not null).Select(e => model.Name(e!)).Distinct().ToList(); if (names.Count > 0) { effectSource = string.Join("、", names); effectSourceAge = 0; } }
        else effectSourceAge++;
        attacking = model is null ? new() : Attackers(model);
        if (logList is not null && IsInstanceValid(logList)) RefreshLog();   // an open log follows every new state
        // Yunke 11-20 #11: the round passes through the upkeep — its band plays over the table as it was, and only once it has
        // gone is the new table built (the upkeep draw, the resource, a discard down to hand size all come after it)
        if (model is not null && PassesUpkeep(model))
        {
            PhaseBanner(); HoldClicks(true);
            AfterBanner("UpkeepPhase", () => { HoldClicks(false); Render(); });
            return;
        }
        SnapshotHand();   // where each hand card was, before the table is rebuilt (for the discard flights)
        foreach (var c in layer.GetChildren()) { layer.RemoveChild(c); c.QueueFree(); }
        ClearPromptVeil();
        if (model is null) return;
        GameAudio.Observe(model);   // noncore audio 11-19: scenario music + effects for what changed
        TriggerModes.Offered(model);
        RememberStory(model);
        // the table is Yunke's desk painting (11-18 r8: "游戏界面的背景图平时就使用这张图"); scene paintings stay for story pages
        var bg = "desk-table";
        var tex = Ui.Bg(bg);
        if (background.Texture != tex)
        {
            background.Texture = tex;
            if (S.Settings.ReducedMotion != true) { background.Modulate = new Color(1, 1, 1, 0.2f); CreateTween().TweenProperty(background, "modulate:a", 1f, 0.5f); }
        }
        byAnchor = new();
        foreach (var c in model.Question.Choices.Where(c => c.Enabled))
        {
            var a = DedicatedKey(c) ?? model.Anchor(c); if (a.Length == 0) continue;
            if (!byAnchor.TryGetValue(a, out var list)) byAnchor[a] = list = new();
            list.Add(c);
        }
        var sec = System.Diagnostics.Stopwatch.StartNew(); var parts = new System.Text.StringBuilder();
        void Lap(string name) { parts.Append(' ').Append(name).Append('=').Append(sec.Elapsed.TotalMilliseconds.ToString("F0")); sec.Restart(); }
        Lap("clear");
        TopBar2(); Lap("top");
        // the map is the centre of the table: all places, who is where, what can be done there (TableScreen.Board.cs)
        Board(); Lap("board");
        TeamPanel(); Lap("team");   // the party on the left: who has what, their enemies (r11 #5)
        PlayerArea(); Lap("player");
        EngagedEnemies(); Lap("engaged");
        RightZone(); Lap("right");
        HandFan(); Lap("hand");
        EncounterDrawPrompt();
        renderParts = parts.ToString();
        if (revealing) { PhaseBanner(); Feedback(); return; }   // the drawn card first; its effect comes on the next render
        if (!model.InSkillTest) tokenSubstitute = null;   // a substitution lasts for its own test only
        testBox = null; stepRow = null;
        if (model.InSkillTest) { SkillTestPanel(); CheckConstantWeakness(); }
        NextStepBar();          // last: it only holds choices that got no element on screen
        GetTree().CreateTimer(0.02).Timeout += () => { if (IsInstanceValid(this)) AssignOverlay(); };   // damage / horror chips on the cards that can take them (laid out first)
        Coach();                // first two rounds only
        QuestionModal();
        PhaseBanner();   // first, so the changes shown below can wait for their phase's band
        Feedback();
        FlyDiscards();   // before FlyPlayed, which still knows the event being played
        FlyDraws();
        RememberHand();
        FlyPlayed();
        FlyMoves();      // an investigator who moved slides across the map (Yunke 11-20 #22)
        ShakeAskedEnemy();   // the enemy a question is about shakes (who it engages, where it goes)
        FlyEnemyDamage();    // damage put on an enemy outside a fight flies onto it
        FlyShuffles();   // a card shuffled back into a deck flies there (Yunke 11-20 #18)
        LocateAfterRender();
        BitesAfterRender();
        DeckShuffledFx();   // the core shuffled the deck in view (TableScreen.Shuffles)
        WarmLog();   // once per table: the log read in the background (TableScreen)
        if (testBox is not null) GetTree().CreateTimer(0.05).Timeout += FitTestPanel;
        // (no automatic source → target arrows any more: Yunke 11-18 #15 found them puzzling; what can act glows instead)
    }

    // Floating change markers for damage, horror, resources and clues of every investigator.
    void Feedback()
    {
        var m = model!; int k = 0;
        if (skipFeedback) { skipFeedback = false; lastStats.Clear(); foreach (var inv in m.Investigators) lastStats[inv.Id] = (m.Damage(inv), m.Horror(inv), m.Resources(inv), m.Clues(inv)); return; }
        foreach (var inv in m.Investigators)
        {
            var now = (m.Damage(inv), m.Horror(inv), m.Resources(inv), m.Clues(inv));
            if (lastStats.TryGetValue(inv.Id, out var was))
            {
                var parts = new List<(string, Color)>();
                if (now.Item1 > was.Dmg) parts.Add(("-" + (now.Item1 - was.Dmg) + " 生命", Style.Danger));
                if (now.Item2 > was.Hor) parts.Add(("-" + (now.Item2 - was.Hor) + " 理智", new Color("8fb6cb")));
                if (now.Item1 < was.Dmg) parts.Add(("+" + (was.Dmg - now.Item1) + " 生命", Style.Good));
                if (now.Item2 < was.Hor) parts.Add(("+" + (was.Hor - now.Item2) + " 理智", Style.Good));
                if (now.Item4 > was.Clue) parts.Add(("+" + (now.Item4 - was.Clue) + " 线索", Style.Good));
                if (now.Item3 > was.Res) parts.Add(("+" + (now.Item3 - was.Res) + " 资源", Style.Amber));
                if (now.Item3 < was.Res) parts.Add(("-" + (was.Res - now.Item3) + " 资源", Style.Amber));   // spent
                // damage or horror right after an attack or a revealed card says where it came from: "食尸鬼 → 黛西 -1 生命"
                string from = effectSource is { } src && effectSourceAge <= 2 && (now.Item1 > was.Dmg || now.Item2 > was.Hor) ? src + " → " : "";
                // shown in its phase's time: hurt after the enemy phase's band, income after upkeep's (Yunke 11-19 r9 #11)
                string? gate = now.Item1 > was.Dmg || now.Item2 > was.Hor ? "EnemyPhase" : now.Item3 != was.Res ? "UpkeepPhase" : null;
                var wasC = was; var invC = inv; string invName = m.Name(inv); bool mine = inv.Id == m.Me?.Id;
                AfterBanner(gate, () => Show(now, wasC, invName, mine, parts, from));
            }
            lastStats[inv.Id] = now;
        }

        void Show((int, int, int, int) now, (int Dmg, int Hor, int Res, int Clue) was, string invName, bool mine, List<(string, Color)> parts, string from)
        {
                foreach (var (text, color) in parts) Float((text.StartsWith("-") && !text.EndsWith("资源") ? from : "") + invName + "  " + text, color, k++);
                // the matching token answers the change itself, and damage or horror tints the whole table edge
                if (mine)
                {
                    if (now.Item3 < was.Res) Punch(tokenNodes.GetValueOrDefault("resource"), Style.Amber);
                    if (now.Item3 > was.Res) Punch(tokenNodes.GetValueOrDefault("resource"), Style.Good);
                    // damage and horror fly from the table to the investigator's own token, which answers on arrival
                    if (now.Item1 > was.Dmg) FlyChip("health", "-" + (now.Item1 - was.Dmg), Style.Danger);
                    if (now.Item2 > was.Hor) FlyChip("horror", "-" + (now.Item2 - was.Hor), new Color("8fb6cb"));
                    if (now.Item1 < was.Dmg) Punch(tokenNodes.GetValueOrDefault("health"), Style.Good);
                    if (now.Item2 < was.Hor) Punch(tokenNodes.GetValueOrDefault("horror"), Style.Good);
                }
        }
    }

    bool skipFeedback;
    string? effectSource; int effectSourceAge = 99;   // the last attack or revealed card, and how many renders ago
    readonly Dictionary<string, Control> tokenNodes = new();   // the player's token images, by kind, for the change effects

    // Turns or swells a node about a new pivot without moving it (Yunke 11-22: 科学大楼 and other places left their spot after
    // a shake — a map card is scaled, and changing the pivot of a scaled node shifts what is drawn). The position takes up the
    // difference; the old pivot is returned so the effect can hand it back at its end (KeepPivot again).
    // Shakes that overlap on one node (a place's forced-ability shake and a bite at once) share one record of where it rests:
    // taken when the first starts, put back exactly when the last ends (Yunke 11-22: a place still crept right and down)
    static void BeginShake(Control n)
    {
        int k = n.HasMeta("shk") ? (int)n.GetMeta("shk") : 0;
        if (k == 0) { n.SetMeta("shk_pos", n.Position); n.SetMeta("shk_rot", n.Rotation); n.SetMeta("shk_scale", n.Scale); n.SetMeta("shk_piv", n.PivotOffset); }
        n.SetMeta("shk", k + 1);
    }
    static void EndShake(Control n)
    {
        if (!IsInstanceValid(n) || !n.HasMeta("shk")) return;
        int k = (int)n.GetMeta("shk") - 1;
        if (k > 0) { n.SetMeta("shk", k); return; }
        n.PivotOffset = (Vector2)n.GetMeta("shk_piv"); n.Position = (Vector2)n.GetMeta("shk_pos");
        n.Rotation = (float)n.GetMeta("shk_rot"); n.Scale = (Vector2)n.GetMeta("shk_scale");
        n.RemoveMeta("shk");
    }
    static Vector2 KeepPivot(Control n, Vector2 pivot)
    {
        var old = n.PivotOffset; var d = pivot - old;
        n.Position -= d - (d * n.Scale).Rotated(n.Rotation);
        n.PivotOffset = pivot;
        return old;
    }

    // A short pop on a token: it swells, then settles, and tints toward the change's colour.
    void Punch(Control? node, Color color)
    {
        if (node is null || !IsInstanceValid(node) || S.Settings.ReducedMotion == true) return;
        // A token in a row (a container) that was just built has not been placed yet: its spot read now is (0, 0), and the
        // shake's end put it back there — the heart token then lay over the resource token and its own place stood empty
        // (Yunke 11-22, 紧急救援; QA ea4). In a container the row places it: only the pivot is set, a frame later.
        if (node.GetParent() is Container row)
        {
            GetTree().ProcessFrame += Later;
            void Later()
            {
                GetTree().ProcessFrame -= Later;
                if (!IsInstanceValid(node) || !node.IsInsideTree()) return;
                node.PivotOffset = node.Size / 2;
                node.Scale = new Vector2(1.28f, 1.28f); node.Modulate = new Color(color.R * 1.6f, color.G * 1.6f, color.B * 1.6f, 1f);
                var t = node.CreateTween().SetParallel();
                t.TweenProperty(node, "scale", Vector2.One, 0.45f).SetTrans(Tween.TransitionType.Back).SetEase(Tween.EaseType.Out);
                t.TweenProperty(node, "modulate", Colors.White, 0.6f);
                t.Chain().TweenCallback(Callable.From(() => { if (IsInstanceValid(row)) row.QueueSort(); }));
            }
            return;
        }
        BeginShake(node); KeepPivot(node, node.Size / 2);
        node.Scale = new Vector2(1.28f, 1.28f); node.Modulate = new Color(color.R * 1.6f, color.G * 1.6f, color.B * 1.6f, 1f);
        var tw = node.CreateTween().SetParallel();
        tw.TweenProperty(node, "scale", Vector2.One, 0.45f).SetTrans(Tween.TransitionType.Back).SetEase(Tween.EaseType.Out);
        tw.TweenProperty(node, "modulate", Colors.White, 0.6f);
        tw.Chain().TweenCallback(Callable.From(() => EndShake(node)));
    }

    // Damage or horror as a chip with its token icon: it leaves the table where the attack lands and flies to
    // the investigator's matching token; on arrival the token pulses.
    void FlyChip(string kind, string text, Color color)
    {
        var target = tokenNodes.GetValueOrDefault(kind);
        if (target is null || !IsInstanceValid(target)) return;
        if (S.Settings.ReducedMotion == true) { Punch(target, color); return; }
        var chip = new PanelContainer { MouseFilter = MouseFilterEnum.Ignore, ZIndex = 95 };
        var sb = new StyleBoxFlat { BgColor = new Color(0.02f, 0.02f, 0.02f, 0.92f), BorderColor = color, ContentMarginLeft = 8, ContentMarginRight = 12, ContentMarginTop = 3, ContentMarginBottom = 3 };
        sb.SetBorderWidthAll(2); sb.SetCornerRadiusAll(16); chip.AddThemeStyleboxOverride("panel", sb);
        var row = new HBoxContainer { MouseFilter = MouseFilterEnum.Ignore }; row.AddThemeConstantOverride("separation", 6); chip.AddChild(row);
        var icon = Ui.Image(Ui.Stat(kind), new Vector2(34, 34)); icon.MouseFilter = MouseFilterEnum.Ignore; row.AddChild(icon);
        var label = Style.Text(text, 26, color, Style.Symbols); label.VerticalAlignment = VerticalAlignment.Center; row.AddChild(label);
        // unseen until it is aimed: added at the top-left corner, it showed over the scenario title for a moment (survey 11-19)
        chip.Modulate = new Color(1, 1, 1, 0);
        AddChild(chip); chip.ResetSize();
        var vp = GetViewportRect().Size;
        // wait one frame so the token has its final on-screen position before the flight is aimed
        GetTree().CreateTimer(0.08).Timeout += () =>
        {
            if (!IsInstanceValid(chip) || !IsInstanceValid(target)) return;
            var from = new Vector2(vp.X * 0.5f, vp.Y * 0.38f) - chip.Size / 2;
            var to = target.GetGlobalRect().GetCenter() - chip.Size / 2;
            chip.Position = from; chip.Scale = new Vector2(0.6f, 0.6f); chip.Modulate = new Color(1, 1, 1, 0);
            var tw = chip.CreateTween();
            tw.TweenProperty(chip, "modulate:a", 1f, 0.12f);
            tw.Parallel().TweenProperty(chip, "scale", Vector2.One, 0.2f).SetTrans(Tween.TransitionType.Back).SetEase(Tween.EaseType.Out);
            tw.TweenInterval(0.1);
            tw.TweenProperty(chip, "position", to, 0.5f).SetTrans(Tween.TransitionType.Quad).SetEase(Tween.EaseType.In);
            tw.TweenCallback(Callable.From(() => { if (IsInstanceValid(chip)) chip.QueueFree(); if (IsInstanceValid(target)) Punch(target, color); }));
        };
    }

    // A card that cannot be played: it shakes, and says why. Insufficient resources also flash the resource token.
    void Deny(CardView cv, string code)
    {
        var m = model; var me = m?.Me; if (m is null || me is null) return;
        int? cost = CoreCosts.Cost(code); int have = m.Resources(me);
        bool short_ = cost is int c && c > have;
        if (S.Settings.ReducedMotion != true)
        {
            float x0 = cv.Position.X; var tw = cv.CreateTween();
            foreach (var dx in new[] { -9f, 9f, -6f, 6f, 0f }) tw.TweenProperty(cv, "position:x", x0 + dx, 0.04f);
        }
        if (short_)
        {
            Punch(tokenNodes.GetValueOrDefault("resource"), Style.Danger);
            Float(m.Name(me) + "  资源不足：这张牌需要 " + cost + "，现有 " + have, Style.Danger, 0);
        }
        else if (!BitePlay(m, me, code)) Float(m.Name(me) + "  现在无法打出这张牌", Style.PaperDim, 0);
    }
    void Float(string text, Color color, int index)
    {
        var p = new PanelContainer { MouseFilter = MouseFilterEnum.Ignore, ZIndex = 90 };
        var sb = new StyleBoxFlat { BgColor = new Color(0.02f, 0.02f, 0.02f, 0.82f), BorderColor = new Color(color, 0.6f), ContentMarginLeft = 16, ContentMarginRight = 16, ContentMarginTop = 4, ContentMarginBottom = 5 };
        sb.BorderWidthLeft = 3; sb.SetCornerRadiusAll(16); p.AddThemeStyleboxOverride("panel", sb);
        p.AddChild(Style.Text(text, 22, color, Style.Kai));
        AddChild(p); p.ResetSize();
        // queued beside the player area, one line per change, never on top of each other
        p.Position = new Vector2(SideMargin + SideW + 24, GetViewportRect().Size.Y - PlayerH - 10 - index * 44);
        var tw = p.CreateTween(); tw.TweenInterval(0.12 * index); tw.TweenProperty(p, "position:y", p.Position.Y - 40, 1.8f).SetEase(Tween.EaseType.Out);
        tw.Parallel().TweenProperty(p, "modulate:a", 0f, 0.6f).SetDelay(1.4f); tw.TweenCallback(Callable.From(() => p.QueueFree()));
    }

    T Place<T>(T c, float left, float top, float right, float bottom, float aL = 0, float aT = 0, float aR = 0, float aB = 0) where T : Control
    {
        layer.AddChild(c);
        c.AnchorLeft = aL; c.AnchorTop = aT; c.AnchorRight = aR; c.AnchorBottom = aB;
        c.OffsetLeft = left; c.OffsetTop = top; c.OffsetRight = right; c.OffsetBottom = bottom;
        return c;
    }

    void Anchor(string key, Control node) { if (key.Length > 0) anchors[key] = node; }
    readonly Dictionary<string, Rect2> anchorFrames = new();   // per anchor: the visible part inside its node, when smaller than the node

    // Act on a table element: one offered choice → do it; several → small menu; none → read the card.
    void ActOn(string key, Control node, string code)
    {
        if (busy) return;
        var list = byAnchor.TryGetValue(key, out var l) ? l : new();
        // fast abilities with their own button on the card are not offered again by a click on the card (TableScreen.FastButtons)
        if (fastOnCard.Contains(key)) list = list.Where(c => !IsFast(c)).ToList();
        if (actionOnCard.Contains(key)) list = list.Where(c => !IsPrintedAction(c)).ToList();
        // damage and horror for this card are placed with the chips on it (TableScreen.Assign), not from a menu
        if (list.Count > 1 && list.All(IsAssignChoice)) { App.I.Toast("点击卡片上的伤害或恐惧标记来分配"); return; }
        if (list.Count == 1) { Choose(list[0]); return; }
        if (list.Count == 0) { if (BiteClick(key)) return; if (code.Length > 0) CardReader.Open(code); return; }
        // a place not yet revealed is named by its unrevealed side (QQ #34)
        string title = key.StartsWith("loc:") && model?.Location(key[4..]) is { } place ? model.Name(place) : key.StartsWith("conc:") ? "隐匿迷你卡牌" : code.Length > 0 ? S.Name(code) : "选择操作";
        InPlaceMenu(node, title, list, code);
    }

    CardView Card(string code, string key, Vector2 size, bool faceDown = false, (string, int)[]? badges = null, string caption = "", bool exhausted = false)
    {
        var cv = new CardView { Code = code, Key = key, CustomMinimumSize = size, Size = size, FaceDown = faceDown, Badges = badges ?? Array.Empty<(string, int)>(), Caption = caption, Exhausted = exhausted };
        cv.Actionable = key.Length > 0 && byAnchor.ContainsKey(key);
        if (cv.Actionable) cv.OnClick = () => ActOn(key, cv, code);
        Anchor(key, cv);
        return cv;
    }

    // ───────────────────────── top: narrative strip with the current objective
    void TopBar()
    {
        var m = model!;
        var shade = new TextureRect { Texture = VGrad(0.85f), ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize, StretchMode = TextureRect.StretchModeEnum.Scale, MouseFilter = MouseFilterEnum.Ignore };
        Place(shade, 0, 0, 0, 150, 0, 0, 1, 0);
        var left = new VBoxContainer(); left.AddThemeConstantOverride("separation", 0);
        Place(left, 28, 14, 520, 90);
        left.AddChild(Style.Text("聚集于此", 30, Style.Paper, Style.Kai));
        left.AddChild(Style.Text(PhaseName(m.Phase) + (m.State.Round > 0 ? " · 第 " + m.State.Round + " 轮" : "") + " · " + DiffName(m.Difficulty), 19, Style.PaperDim));

        // objective banner (act) + doom dial (agenda): text first, cards on demand
        var banner = new HBoxContainer { Alignment = BoxContainer.AlignmentMode.Center }; banner.AddThemeConstantOverride("separation", 18);
        Place(banner, -560, 12, 560, 84, 0.5f, 0, 0.5f, 0);
        if (m.Act is not null)
        {
            var plate = new Button { FocusMode = FocusModeEnum.None, MouseDefaultCursorShape = CursorShape.PointingHand, TooltipText = "点击查看场景卡" };
            var key = "act"; bool act = byAnchor.ContainsKey(key);
            foreach (var st in new[] { "normal", "hover", "pressed" }) plate.AddThemeStyleboxOverride(st, Ui.PlaqueBox(56, 8, st == "hover" || act ? new Color(1.25f, 1.15f, 0.95f) : Colors.White));
            var col = new VBoxContainer { MouseFilter = MouseFilterEnum.Ignore, Alignment = BoxContainer.AlignmentMode.Center }; col.AddThemeConstantOverride("separation", -2);
            var t1 = Style.Text("目标 · " + m.Name(m.Act), 24, Style.HoverText, Style.Kai); t1.HorizontalAlignment = HorizontalAlignment.Center; col.AddChild(t1);
            var t2 = Style.Text(act ? "可以推进场景 · 点击推进" : Objective(m), 18, act ? Style.Amber : Style.PaperDim); t2.HorizontalAlignment = HorizontalAlignment.Center; col.AddChild(t2);
            plate.AddChild(col); col.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
            plate.CustomMinimumSize = new Vector2(Math.Max(420, t1.GetMinimumSize().X + 130), 70);
            plate.Pressed += () => ActOn(key, plate, m.Act.Code);
            Anchor(key, plate); banner.AddChild(plate);
        }
        if (m.Agenda is not null)
        {
            int doom = m.TotalDoom, max = Math.Max(1, m.DoomThreshold);
            var dial = new VBoxContainer { MouseFilter = MouseFilterEnum.Stop, MouseDefaultCursorShape = CursorShape.PointingHand, TooltipText = "密谋 · " + m.Name(m.Agenda) + "（点击查看）" };
            dial.AddThemeConstantOverride("separation", 2);
            var pips = new HBoxContainer { MouseFilter = MouseFilterEnum.Ignore }; pips.AddThemeConstantOverride("separation", 4);
            for (int i = 0; i < max; i++) { var p = new Panel { CustomMinimumSize = new Vector2(18, 18), MouseFilter = MouseFilterEnum.Ignore }; p.AddThemeStyleboxOverride("panel", Style.Box(i < doom ? new Color("b0402e") : new Color(0, 0, 0, 0.5f), new Color("d58b72"), 1, 9, 0, 0)); pips.AddChild(p); }
            dial.AddChild(Style.Text("毁灭 " + doom + " / " + max, 19, Style.Danger));
            dial.AddChild(pips);
            var ag = m.Agenda; dial.GuiInput += e => { if (e is InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Left }) ActOn("agenda", dial, ag.Code); };
            dial.MouseEntered += () => Zoom.Show(dial, ag.Code, 420); dial.MouseExited += Zoom.Hide;
            Anchor("agenda", dial); banner.AddChild(dial);
        }

        var right = new HBoxContainer(); right.AddThemeConstantOverride("separation", 10);
        Place(right, -720, 16, -24, 80, 1, 0, 1, 0); right.Alignment = BoxContainer.AlignmentMode.End;
        foreach (var seat in m.State.Seats)
        {
            var inv = m.Investigators.FirstOrDefault(i => J.String(i.Raw, "playerId") == seat.Player.ToString("D")); if (inv is null) continue;
            bool viewing = seat.Player == m.State.Player; var player = seat.Player;
            var md = new Medallion { Picture = Ui.Portrait(inv.Code), Code = inv.Code, CustomMinimumSize = new Vector2(60, 60), Size = new Vector2(60, 60), Ring = viewing ? Style.Amber : seat.HasQuestion ? Style.Good : Style.BrassDim, Dim = !viewing, OnClick = () => ViewSeat(player) };
            md.TooltipText = m.Name(inv) + (viewing ? "（当前视角）" : seat.HasQuestion ? " · 等待操作，点击切换" : " · 点击切换视角");
            right.AddChild(md);
        }
        right.AddChild(Ui.Spacer(10));
        foreach (var (label, act) in new (string, Action)[] { ("宅邸", OpenMap), ("混沌袋", OpenChaosBag), ("记录", OpenLog), ("撤回", Undo), ("菜单", OpenMenu) })
        {
            var b = Ui.Button(label, act, 19); b.CustomMinimumSize = new Vector2(76, 50);
            if (label == "撤回") b.Disabled = m.State.ActionStartSequence is null;
            right.AddChild(b);
        }
    }

    static GradientTexture2D VGrad(float a, bool up = false)
    {
        var g = new Gradient(); g.SetColor(0, new Color(0, 0, 0, up ? 0 : a)); g.SetColor(1, new Color(0, 0, 0, up ? a : 0));
        return new GradientTexture2D { Gradient = g, FillFrom = new Vector2(0.5f, 0), FillTo = new Vector2(0.5f, 1), Width = 8, Height = 128 };
    }

    // ───────────────────────── mini map: the mansion cutaway with who is where
    void MiniMap()
    {
        var m = model!; if (scenario is null) return;
        var size = new Vector2(360, 202);
        var frame = new PanelContainer { MouseFilter = MouseFilterEnum.Stop, MouseDefaultCursorShape = CursorShape.PointingHand, TooltipText = "宅邸总览（M）" };
        frame.AddThemeStyleboxOverride("panel", Style.Box(new Color(0, 0, 0, 0.6f), Style.Brass, 2, 8, 3, 3));
        Place(frame, 24, 104, 24 + size.X + 6, 104 + size.Y + 6);
        var holder = new Control { CustomMinimumSize = size, MouseFilter = MouseFilterEnum.Ignore }; frame.AddChild(holder);
        var img = Ui.Image(Ui.Bg(scenario.MapImage), size, true); img.Modulate = new Color(0.85f, 0.82f, 0.8f); holder.AddChild(img);
        var here = m.MyLocation?.Id;
        foreach (var loc in m.Locations)
        {
            var hs = scenario.Hotspot(m, m.Label(loc)); if (hs is null) continue;
            var r = new Rect2(hs[0] * size.X, hs[1] * size.Y, hs[2] * size.X, hs[3] * size.Y);
            var box = new Panel { Position = r.Position, Size = r.Size, MouseFilter = MouseFilterEnum.Ignore };
            bool cur = loc.Id == here, act = byAnchor.ContainsKey("loc:" + loc.Id);
            box.AddThemeStyleboxOverride("panel", Style.Box(new Color(0, 0, 0, m.Revealed(loc) ? 0 : 0.5f), cur ? Style.Good : act ? Style.Amber : new Color(Style.Brass, 0.35f), cur || act ? 2 : 1, 4, 0, 0));
            holder.AddChild(box);
            int k = 0;
            foreach (var inv in m.InvestigatorsAt(loc.Id)) { var p = Ui.Image(Ui.Portrait(inv.Code), new Vector2(26, 26), true); box.AddChild(p); p.Position = new Vector2(4 + 24 * k++, r.Size.Y - 30); }
            int en = m.EnemiesAt(loc.Id).Count();
            if (en > 0) { var d = new Panel { MouseFilter = MouseFilterEnum.Ignore, Size = new Vector2(16, 16), Position = new Vector2(r.Size.X - 20, 4) }; d.AddThemeStyleboxOverride("panel", Style.Box(new Color("b0402e"), Colors.Black, 1, 8, 0, 0)); box.AddChild(d); }
        }
        frame.GuiInput += e => { if (e is InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Left }) OpenMap(); };
    }

    // ───────────────────────── stage: where we are, who is here, what threatens us
    void Stage()
    {
        var m = model!; var loc = m.MyLocation; if (loc is null) return;
        // location title (the card stays behind a click)
        var title = new VBoxContainer { MouseFilter = MouseFilterEnum.Stop, MouseDefaultCursorShape = CursorShape.PointingHand, TooltipText = "点击查看地点卡" };
        title.AddThemeConstantOverride("separation", 0);
        Place(title, -360, 104, 360, 196, 0.5f, 0, 0.5f, 0);
        string place = scenario?.Place(m.Label(loc)) ?? "";
        var name = Style.Text(m.Name(loc) + (place.Length > 0 ? " · " + place : ""), 44, Style.HoverText, Style.Kai); name.HorizontalAlignment = HorizontalAlignment.Center;
        name.AddThemeConstantOverride("outline_size", 12); name.AddThemeColorOverride("font_outline_color", new Color(0, 0, 0, 0.85f)); title.AddChild(name);
        var sub = new HBoxContainer { Alignment = BoxContainer.AlignmentMode.Center, MouseFilter = MouseFilterEnum.Ignore }; sub.AddThemeConstantOverride("separation", 10); title.AddChild(sub);
        var shroud = Style.Text("隐藏值 " + m.Shroud(loc), 21, Style.Paper); shroud.AddThemeConstantOverride("outline_size", 8); shroud.AddThemeColorOverride("font_outline_color", Colors.Black); sub.AddChild(shroud);
        sub.AddChild(CardView.Badge("clue", m.Clues(loc), 30));
        var cl = Style.Text("线索", 21, Style.Paper); cl.AddThemeConstantOverride("outline_size", 8); cl.AddThemeColorOverride("font_outline_color", Colors.Black); sub.AddChild(cl);
        string lkey = "loc:" + loc.Id;
        title.GuiInput += e => { if (e is InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Left }) ActOn(lkey, title, loc.Code); };
        Peek.Attach(title, loc.Code);
        Anchor(lkey, title);
        if (byAnchor.TryGetValue(lkey, out var here) && here.Count > 0)
        {
            var quick = new HBoxContainer { Alignment = BoxContainer.AlignmentMode.Center }; quick.AddThemeConstantOverride("separation", 10);
            Place(quick, -360, 200, 360, 250, 0.5f, 0, 0.5f, 0);
            foreach (var c in here.Take(3)) { var b = Ui.Button(c.Title, () => Choose(c), 21, true); b.CustomMinimumSize = new Vector2(220, 50); quick.AddChild(b); }
        }

        // enemies as medallions in the scene
        var row = new HBoxContainer { Alignment = BoxContainer.AlignmentMode.Center }; row.AddThemeConstantOverride("separation", 36);
        Place(row, -520, 290, 520, 520, 0.5f, 0, 0.5f, 0);
        var engaged = m.Me is null ? new HashSet<string>() : m.EngagedWith(m.Me).Select(e => e.Id).ToHashSet();
        foreach (var e in m.EnemiesAt(loc.Id).Where(e => !engaged.Contains(e.Id))) row.AddChild(EnemyCard(e, false, new Vector2(156, 218)));
        // Story assets with no controller wait at a location (Lita Chantler in the parlor): they are on the table too.
        foreach (var a in m.AssetsAt(loc.Id))
        {
            string akey = "asset:" + a.Id; var acode = a.Code;
            var cv = new CardView { Code = acode, Key = akey, CustomMinimumSize = new Vector2(156, 218), Size = new Vector2(156, 218), ShowCost = false, Rim = new Color("93b8a0"), Actionable = byAnchor.ContainsKey(akey) };
            cv.OnClick = () => ActOn(akey, cv, acode); Anchor(akey, cv); row.AddChild(cv);
        }
        // companions here
        var others = m.InvestigatorsAt(loc.Id).Where(i => i.Id != m.Me?.Id).ToArray();
        if (others.Length > 0)
        {
            var oc = new VBoxContainer(); oc.AddThemeConstantOverride("separation", 4);
            Place(oc, 24, 150, 260, 260);
            var lab = Style.Text("同在此地", 19, Style.PaperDim); lab.AddThemeConstantOverride("outline_size", 6); lab.AddThemeColorOverride("font_outline_color", Colors.Black); oc.AddChild(lab);
            var h = new HBoxContainer(); oc.AddChild(h);
            foreach (var o in others)
            {
                var key = "inv:" + o.Id; var code = o.Code;
                var md = new Medallion { Picture = Ui.Portrait(o.Code), Code = o.Code, CustomMinimumSize = new Vector2(72, 72), Size = new Vector2(72, 72), Actionable = byAnchor.ContainsKey(key) };
                md.OnClick = () => ActOn(key, md, code); md.TooltipText = m.Name(o); Anchor(key, md); h.AddChild(md);
            }
        }
    }

    Control EnemyFigure(Entity e, bool engaged)
    {
        var m = model!; string key = "enemy:" + e.Id;
        var col = new VBoxContainer { Alignment = BoxContainer.AlignmentMode.Center }; col.AddThemeConstantOverride("separation", 6);
        if (engaged) { var t = Style.Text("与你交战", 20, Style.Danger, Style.Kai); t.HorizontalAlignment = HorizontalAlignment.Center; t.AddThemeConstantOverride("outline_size", 8); t.AddThemeColorOverride("font_outline_color", Colors.Black); col.AddChild(t); }
        else col.AddChild(Ui.Spacer(0, 27));
        bool exhausted = J.Get(e.Raw, "exhausted").ValueKind == System.Text.Json.JsonValueKind.True;
        var md = new Medallion { Code = e.Code, Kind = "enemy", Key = key, CustomMinimumSize = new Vector2(190, 190), Size = new Vector2(190, 190), Ring = engaged ? new Color("c0543e") : Style.BrassDim, Actionable = byAnchor.ContainsKey(key), Dim = exhausted };
        var code = e.Code; md.OnClick = () => ActOn(key, md, code);
        md.SizeFlagsHorizontal = SizeFlags.ShrinkCenter; col.AddChild(md); Anchor(key, md);
        var nm = Style.Text(m.Name(e) + (exhausted ? "（横置）" : ""), 24, Style.HoverText, Style.Kai); nm.HorizontalAlignment = HorizontalAlignment.Center; nm.AddThemeConstantOverride("outline_size", 8); nm.AddThemeColorOverride("font_outline_color", Colors.Black); col.AddChild(nm);
        int hp = m.EnemyHealth(e), dmg = m.Damage(e);
        var stat = new HBoxContainer { Alignment = BoxContainer.AlignmentMode.Center }; stat.AddThemeConstantOverride("separation", 12);
        foreach (var (g, v, c) in new[] { ("combat", m.EnemyFight(e), Style.Combat), ("agility", m.EnemyEvade(e), Style.Agility) })
        { var h = new HBoxContainer(); h.AddChild(Ui.Glyph(g, 22, c)); var n = Style.Text(Fmt(v), 22, Style.Paper); n.AddThemeConstantOverride("outline_size", 6); n.AddThemeColorOverride("font_outline_color", Colors.Black); h.AddChild(n); stat.AddChild(h); }
        col.AddChild(stat);
        if (hp > 0)
        {
            var pips = new HBoxContainer { Alignment = BoxContainer.AlignmentMode.Center }; pips.AddThemeConstantOverride("separation", 3);
            for (int i = 0; i < hp; i++) { var p = new Panel { CustomMinimumSize = new Vector2(14, 14), MouseFilter = MouseFilterEnum.Ignore }; p.AddThemeStyleboxOverride("panel", Style.Box(i < hp - dmg ? new Color("b0402e") : new Color(0, 0, 0, 0.6f), Colors.Black, 1, 7, 0, 0)); pips.AddChild(p); }
            col.AddChild(pips);
        }
        return col;
    }

    // ───────────────────────── investigator (bottom-left): a character sheet, not a card
    void InvestigatorPanel()
    {
        var m = model!; var me = m.Me; if (me is null) return;
        var shade = new TextureRect { Texture = VGrad(0.88f, true), ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize, StretchMode = TextureRect.StretchModeEnum.Scale, MouseFilter = MouseFilterEnum.Ignore };
        Place(shade, 0, -260, 0, 0, 0, 1, 1, 1);
        var row = new HBoxContainer(); row.AddThemeConstantOverride("separation", 16);
        Place(row, 24, -214, 560, -18, 0, 1, 0, 1);
        string key = "inv:" + me.Id;
        var md = new Medallion { Picture = Ui.Portrait(me.Code), Code = me.Code, CustomMinimumSize = new Vector2(150, 150), Size = new Vector2(150, 150), Ring = Style.Amber, Actionable = byAnchor.ContainsKey(key) };
        md.OnClick = () => ActOn(key, md, me.Code); md.TooltipText = "点击查看调查员卡"; md.SizeFlagsVertical = SizeFlags.ShrinkCenter;
        Anchor(key, md); row.AddChild(md);
        var col = new VBoxContainer(); col.AddThemeConstantOverride("separation", 6); row.AddChild(col);
        var nm = new HBoxContainer(); nm.AddThemeConstantOverride("separation", 12); col.AddChild(nm);
        nm.AddChild(Style.Text(m.Name(me), 30, Style.HoverText, Style.Kai));
        if (m.Phase == "InvestigationPhase") for (int i = 0; i < Math.Max(3, m.Actions(me)); i++) { var g = Ui.Glyph("action", 24, i < m.Actions(me) ? Style.Amber : new Color(Style.PaperDim, 0.3f)); g.VerticalAlignment = VerticalAlignment.Center; nm.AddChild(g); }
        var stats = new HBoxContainer(); stats.AddThemeConstantOverride("separation", 14); col.AddChild(stats);
        foreach (var (k, color) in new[] { ("willpower", Style.Willpower), ("intellect", Style.Intellect), ("combat", Style.Combat), ("agility", Style.Agility) })
        { var h = new HBoxContainer(); h.AddThemeConstantOverride("separation", 2); h.AddChild(Ui.Glyph(k, 24, color)); int sv = m.SkillValue(me, k), sb0 = m.Stat(me, k); h.AddChild(Style.Text(sv.ToString(), 24, sv > sb0 ? Style.Good : sv < sb0 ? Style.Danger : Style.Paper)); stats.AddChild(h); }
        var tokens = new HBoxContainer(); tokens.AddThemeConstantOverride("separation", 14); col.AddChild(tokens);
        tokens.AddChild(Counter("health", m.Health(me) - m.Damage(me), m.Health(me), "生命"));
        tokens.AddChild(Counter("horror", m.Sanity(me) - m.Horror(me), m.Sanity(me), "理智"));
        tokens.AddChild(Counter("resource", m.Resources(me), -1, "资源"));
        tokens.AddChild(Counter("clue", m.Clues(me), -1, "线索"));
        var gear = new HBoxContainer(); gear.AddThemeConstantOverride("separation", 8); col.AddChild(gear);
        foreach (var a in m.AssetsOf(me))
        {
            string akey = "asset:" + a.Id; var code = a.Code;
            var am = new Medallion { Code = a.Code, Kind = "player", CustomMinimumSize = new Vector2(54, 54), Size = new Vector2(54, 54), Actionable = byAnchor.ContainsKey(akey), Dim = J.Get(a.Raw, "exhausted").ValueKind == System.Text.Json.JsonValueKind.True, TooltipText = m.Name(a) };
            am.OnClick = () => ActOn(akey, am, code); Anchor(akey, am);
            int uses = J.Items(J.Get(a.Raw, "tokens")).Sum(p => p.ValueKind == System.Text.Json.JsonValueKind.Array && p.GetArrayLength() == 2 && p[0].GetString() is "Ammo" or "Supply" or "Charge" or "Secret" or "Resource" ? p[1].GetInt32() : 0);
            if (uses > 0) { var b = CardView.Badge("resource", uses, 24); am.AddChild(b); b.Position = new Vector2(32, 32); }
            gear.AddChild(am);
        }
        foreach (var t in m.ThreatsOf(me))
        {
            string tkey = m.ThreatKey(t); var code = t.Code;
            var tm = new Medallion { Code = t.Code, Kind = "treachery", CustomMinimumSize = new Vector2(54, 54), Size = new Vector2(54, 54), Ring = new Color("c0543e"), Actionable = byAnchor.ContainsKey(tkey), TooltipText = m.Name(t) };
            tm.OnClick = () => ActOn(tkey, tm, code); Anchor(tkey, tm); gear.AddChild(tm);
        }
        var piles = Style.Text("牌库 " + m.DeckSize(me) + " · 弃牌 " + m.Discard(me).Count(), 17, Style.PaperDim); col.AddChild(piles);
    }

    // ───────────────────────── hand: tucked at the bottom edge, rises under the pointer
    Control? handZone;
    void HandRow()
    {
        var m = model!; var me = m.Me; if (me is null) return;
        var committedIds = m.InSkillTest ? J.Props(J.Get(m.SkillTest, "committedCards")).SelectMany(p => J.Items(p.Value)).Select(c => J.String(J.Get(c, "contents"), "id")).ToHashSet() : new HashSet<string>();
        var hand = m.Hand(me).Where(h => !committedIds.Contains(h.Item1)).ToArray();
        bool raised = m.InSkillTest || m.Question.Title.Contains("起手牌") || m.Question.Choices.Any(c => c.Title.StartsWith("换掉"));
        var zone = new Control { MouseFilter = MouseFilterEnum.Ignore };
        Place(zone, 580, -300, -440, 0, 0, 1, 1, 1);
        handZone = zone;
        const float w = 176, h = 252;
        float avail = Math.Max(400, GetViewportRect().Size.X - 580 - 440);
        float step = hand.Length <= 1 ? w : Math.Min(w * 0.82f, (avail - w) / (hand.Length - 1));
        float startX = Math.Max(0, (avail - (step * (hand.Length - 1) + w)) / 2);
        float restY = raised ? 36 : 150;
        for (int i = 0; i < hand.Length; i++)
        {
            var (id, code) = hand[i];
            var cv = Card(code, "hand:" + id, new Vector2(w, h));
            cv.ZoomHeight = 640; cv.Draggable = cv.Actionable; cv.Lift = raised ? 16 : 120;
            var key = "hand:" + id; cv.OnDragRelease = pos => DropFromHand(key, pos);
            float x = startX + i * step, mid = (hand.Length - 1) / 2f;
            cv.Position = new Vector2(x, restY + (raised ? 0 : Math.Abs(i - mid) * 6)); cv.RotationDegrees = raised ? 0 : (i - mid) * 2.2f;
            cv.PivotOffset = new Vector2(w / 2, h);
            zone.AddChild(cv);
        }
    }

    static string DiffName(string d) => d switch { "Easy" => "简单", "Hard" => "困难", "Expert" => "专家", _ => "标准" };

    string Objective(TableModel m)
    {
        if (m.Act is null) return "";
        var cost = J.Get(m.Act.Raw, "advanceCost");
        int have = m.Investigators.Sum(i => m.Clues(i));
        // the cost's value is already the team total (Calc multiplies "per investigator" values by the investigator count)
        if (J.Tag(cost) == "GroupClueCost") { var c = J.Get(cost, "contents"); int total = c.ValueKind == JsonValueKind.Array ? TableModel.Calc(c[0]) : -1; if (total > 0) return "推进：全队共花费 " + total + " 线索（现有 " + have + "）"; }
        // the act card's own official objective sentence ("<b>目标</b> - …"), with the team's clue count when the act needs clues
        var text = J.String(S.Card(m.Act.Code), "text");
        var goal = System.Text.RegularExpressions.Regex.Match(text, @"目标</b>\s*-\s*(.+?)(?:
|$)");
        if (goal.Success)
        {
            string sentence = System.Text.RegularExpressions.Regex.Replace(goal.Groups[1].Value, @"\[\[([^\]]+)\]\]|<[^>]+>", g => g.Groups[1].Value).Trim();
            // "3[per_investigator]" (the act card's per-investigator icon, Carcosa 真相幻影 "风琴手上有3[per_investigator]个线索"):
            // the number for this table, with the printed value beside it
            int people = Math.Max(1, m.Investigators.Count());
            sentence = System.Text.RegularExpressions.Regex.Replace(sentence, @"(\d+)\s*\[per_investigator\]", g => (int.Parse(g.Groups[1].Value) * people) + "（每位调查员 " + g.Groups[1].Value + "）");
            int per = scenario?.Objective(m.Act.Code)?.PerPlayer ?? 0;
            return "目标：" + sentence + (per > 0 ? "（全队需 " + per * m.State.Seats.Length + " 线索，现有 " + have + "）" : "");
        }
        return "查看场景卡了解目标";
    }

    string EnemyLine(Entity e) => "战" + Fmt(model!.EnemyFight(e)) + " 命" + Fmt(model!.EnemyHealth(e)) + " 闪" + Fmt(model!.EnemyEvade(e));

    static string Fmt(int v) => v < 0 ? "-" : v.ToString();

    Control Counter(string kind, int value, int max, string name)
    {
        var h = new HBoxContainer(); h.AddThemeConstantOverride("separation", 4);
        h.AddChild(Ui.Image(Ui.Stat(kind), new Vector2(36, 36)));
        var v = Style.Text(max >= 0 ? value + "/" + max : value.ToString(), 26, Style.Paper, Style.Sans); h.AddChild(v);
        h.TooltipText = name; h.MouseFilter = MouseFilterEnum.Pass;
        return h;
    }

}
