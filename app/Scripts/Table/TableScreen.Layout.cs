using Godot;
using Gathering;
using System.Text.Json;

namespace ArkhamUI;

// Screen zones of the revamp (docs/REVAMP-SUMMARY-20261117.md §1–§4): top bar, left player area with engaged
// enemies above it, right exclusive / public zones, and the fanned hand with deck and discard piles at its sides.
// Everything sits on dark fades; no ornate frames.
public partial class TableScreen
{
    const float SideW = 426, SideMargin = 14, PlayerH = 486;
    static readonly Color Fade = new(0.01f, 0.01f, 0.01f, 0.9f);

    // ───────────────────────── top bar
    Button? topUndo;   // the top bar's 撤回 (FocusLayering puts its twin over the full-screen map at the same place)
    void TopBar2()
    {
        var m = model!;
        // no dark shade behind the top bar (Yunke 11-23: "顶上这个后面的黑色阴影不要，去掉")
        // left: scenario + phase, mansion icon (opens the overview)
        var left = new VBoxContainer(); left.AddThemeConstantOverride("separation", 0); Place(left, 28, 12, 520, 80);
        // the chapter's official name: its scenario data, else the campaign catalog's chapter (later cycles), else the card's name
        string scode = ScenarioCode(m);
        var title = Style.Text(scenario?.Name is { Length: > 0 } sn ? sn : Campaigns.ForScenario(scode).Find(scode)?.Name is { Length: > 0 } cn ? cn : S.Name(scode), 26, Style.Paper, Style.Kai);
        left.AddChild(title);
        // the scenario reference card (what each chaos symbol does at this difficulty) on hover, the card itself on click —
        // as the log's scenario name does (Yunke 11-19 r11 #6)
        string refCode = ScenarioCode(m);
        if (refCode.Length > 0 && J.String(S.Card(refCode), "type_code") == "scenario")
        {
            left.MouseFilter = MouseFilterEnum.Stop; left.MouseDefaultCursorShape = CursorShape.Help;
            left.MouseEntered += () => ShowReference(left, m, refCode);
            left.MouseExited += HideGoal;
            left.GuiInput += e => { if (e is InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Left }) { HideGoal(); CardReader.Open(refCode, printed: true); } };
        }
        left.AddChild(Style.Text(PhaseName(m.Phase) + (m.State.Round > 0 ? " · 第 " + m.State.Round + " 轮" : "") + " · " + DiffName(m.Difficulty), 18, Style.PaperDim));
        // centre: the act and the agenda as two plates (TableScreen.Plates.cs, Yunke 11-19 r11 #4)
        ActAgendaPlates();
        // right: party + plain buttons
        var right = new HBoxContainer { Alignment = BoxContainer.AlignmentMode.End }; right.AddThemeConstantOverride("separation", 6);   // 6 (was 10): two more icons since 11-23, clear of the agenda card
        Place(right, -720, 16, -24, 76, 1, 0, 1, 0);   // room for 撤回 and 提示可点 too (11-23: at -600 the last icon ran off the right edge)
        // the seats moved to the team column on the left (TableScreen.Team.cs, Yunke 11-19 r11 #5)
        right.AddChild(Ui.Spacer(8));
        // 撤回 (Yunke 11-23: "就在这个按钮左边加一个撤回按钮，这个撤回按钮只撤回上一步，类似于快捷读档…并且要永远保持在最前"): one
        // step back, as U does; always the first button of the row (with or without a map); over the full-screen map a twin
        // stands at the same place (FocusLayering). Earlier steps: the side log's ↶ marks.
        topUndo = LineIcon.Button("undo", "撤回一步（U）\n只撤回上一步，像快速读档；更早的步骤在记录里撤回", Undo, 46);
        right.AddChild(topUndo);
        // 记录 opens the side log (its ↶ marks are the way back; U undoes one step — the 撤回 menu is gone, Yunke 11-19 r11)
        // Yunke 11-20 #3: every button up here is an icon (its name in the tooltip); the map's own two (full screen, lock) moved
        // here from the top-left, first in the row
        if (scenario is not null)
        {
            right.AddChild(LineIcon.Button(mapLarge ? "shrink" : "expand", (mapLarge ? "地图回左上" : "地图全屏") + "（Tab）\n" + scenario.MapName + "：指针停在地点上会推近并显示信息；滚轮缩放，拖动平移，M 看全图", ToggleMap, 46));
            if (mapShown)
            {
                string what = scenario.WholePicture ? "地点上的标记" : "地点小卡";   // painted map: the markers on each place are what moves
                right.AddChild(LineIcon.Button(mapUnlocked ? "unlock" : "lock", mapUnlocked ? "锁定地点\n已解锁：拖动" + what + "微调位置，完成后点这里锁定" : "解锁地点\n解锁后可以拖动" + what + "，微调它们在地图上的位置", ToggleMapLock, 46, mapUnlocked));
                if (mapUnlocked && model is not null && Offsets(ScenarioCode(model)).Count > 0) right.AddChild(LineIcon.Button("reset", "复位\n地点回到原来的位置", ResetOffsets, 46));
            }
            right.AddChild(Ui.Spacer(6));
        }
        // 提示可点 (F) as an icon of its own (Yunke 11-23: the phone's "⋯" menu is gone; its keys are buttons in the game's style)
        foreach (var (kind, tip, act) in new (string, string, Action)[] { ("hint", "提示可点（F）\n让现在能点的东西闪一下", Locate), ("bag", "混沌袋", OpenChaosBag), ("cards", "牌组", () => OpenDeckViewer()), ("music", "音乐：选歌、切歌", MusicPanel.Open), ("menu", "菜单（Esc）", OpenMenu), ("history", "记录", ToggleLog), ("help", "帮助", OpenHelp) })
            right.AddChild(LineIcon.Button(kind, tip, act, 46));
    }

    // A seat's portrait in a square frame (not a medallion): amber when it is the view, green when it waits, dim otherwise.
    Control SeatTile(Texture2D? tex, bool viewing, bool waiting, Action onClick)
    {
        var frame = new PanelContainer { CustomMinimumSize = new Vector2(54, 54), MouseFilter = MouseFilterEnum.Stop, MouseDefaultCursorShape = CursorShape.PointingHand };
        var sb = new StyleBoxFlat { BgColor = new Color(0, 0, 0, 0.5f), BorderColor = viewing ? Style.Amber : waiting ? Style.Good : Style.BrassDim, ContentMarginLeft = 2, ContentMarginRight = 2, ContentMarginTop = 2, ContentMarginBottom = 2 };
        sb.SetBorderWidthAll(2); sb.SetCornerRadiusAll(0);
        frame.AddThemeStyleboxOverride("panel", sb);
        // the mini art is a tall card picture: show its upper part (the face) in the square
        var clip = new Control { CustomMinimumSize = new Vector2(50, 50), ClipContents = true, MouseFilter = MouseFilterEnum.Ignore }; frame.AddChild(clip);
        var img = Ui.Image(tex, new Vector2(50, 78), true); img.MouseFilter = MouseFilterEnum.Ignore; img.Size = new Vector2(50, 78); img.Position = new Vector2(0, -6); clip.AddChild(img);
        frame.Modulate = viewing ? Colors.White : new Color(1, 1, 1, 0.6f);
        frame.GuiInput += e => { if (e is InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Left }) onClick(); };
        return frame;
    }

    // The team clues the current goal costs (the core's group clue cost times the seats), or 0 when the goal has none.
    int ActClueCost(TableModel m)
    {
        if (m.Act is null) return 0;
        var cost = J.Get(m.Act.Raw, "advanceCost");
        if (J.Tag(cost) != "GroupClueCost")
        {
            if (scenario?.Objective(m.Act.Code) is { PerPlayer: > 0 } o) return o.PerPlayer * m.State.Seats.Length;
            // a goal the core advances by itself once the team holds the clues (歪打正着: "必须立即花费这些线索并推进") has no
            // advance cost: its printed clue value from the card data, per investigator unless fixed (Yunke 11-22: show its progress)
            var card = BuildCard(m.Act.Code);
            string text = J.String(S.Card(CoreCatalog.Normalize(m.Act.Code)), "text");
            if (card.ValueKind == JsonValueKind.Object && J.Get(card, "clues").ValueKind == JsonValueKind.Number && J.Int(card, "clues") > 0 && text.Contains("线索"))
                return J.Get(card, "clues_fixed").ValueKind == JsonValueKind.True ? J.Int(card, "clues") : J.Int(card, "clues") * m.State.Seats.Length;
            return 0;
        }
        var c = J.Get(cost, "contents"); int total = c.ValueKind == JsonValueKind.Array ? TableModel.Calc(c[0]) : -1;
        return total > 0 ? total : 0;   // already the team total (PerPlayer × investigators)
    }

    // the arkham.build card record (it has the printed clue values the game catalog leaves out), read once
    static Dictionary<string, JsonElement>? buildCards;
    static JsonElement BuildCard(string code)
    {
        if (buildCards is null)
        {
            buildCards = new();
            try
            {
                using var doc = JsonDocument.Parse(System.IO.File.ReadAllText(System.IO.Path.Combine(App.I.Assets, "build-data", "cards-zh-cn.json")));
                foreach (var c in J.Items(J.Get(J.Get(doc.RootElement, "data"), "all_card")))
                    if (J.String(c, "type_code") == "act" && !buildCards.ContainsKey(J.String(c, "code"))) buildCards[J.String(c, "code")] = c.Clone();
            }
            catch (Exception e) { GD.PushWarning("act clues: " + e.Message); }
        }
        return buildCards.TryGetValue(CoreCatalog.Normalize(code), out var v) ? v : default;
    }

    Control? goalTip;
    void ShowGoal(Control anchor, TableModel m)
    {
        HideGoal();
        var col = new VBoxContainer { CustomMinimumSize = new Vector2(460, 0), MouseFilter = MouseFilterEnum.Ignore }; col.AddThemeConstantOverride("separation", 8);
        col.AddChild(Style.Title(m.Name(m.Act), 26));
        var actText = J.String(S.Card(m.Act.Code), "text");
        if (actText.Length > 0) col.AddChild(TipRules(actText));
        // the goal in words, unless the card's own text already states it (it was shown twice)
        if (!actText.Contains("目标")) col.AddChild(Style.Text(Objective(m), 19, Style.Amber, Style.Kai));
        col.AddChild(new ColorRect { Color = new Color(Style.Brass, 0.35f), CustomMinimumSize = new Vector2(0, 1), MouseFilter = MouseFilterEnum.Ignore });
        var sc = scenario;
        col.AddChild(Style.Text("场景", 20, Style.Brass, Style.Kai));
        // the intro the rules core read in this game (it depends on the campaign log), not a fixed text
        string intro = ChapterIntro(ScenarioCode(m));
        // which parts of the intro this game read, the others struck through (Yunke 11-19 r11: the intro's parts looked
        // like alternatives shown side by side)
        if (StoryLine(ScenarioCode(m)) is { } line) col.AddChild(line);
        if (intro.Length == 0) { PlaceTip(anchor, col); return; }
        // the story can run longer than the screen (Yunke 11-20): it scrolls inside the popup, which then stays open while the
        // pointer is over it so the wheel can reach it
        var text = TipRules(intro);
        var scroll = new ScrollContainer { HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled, MouseFilter = MouseFilterEnum.Pass };
        scroll.AddChild(text); col.AddChild(scroll);
        var box = PlaceTip(anchor, col, stay: true);
        void Clamp()
        {
            if (!IsInstanceValid(box) || !IsInstanceValid(text)) return;
            float h = text.GetContentHeight(), others = box.Size.Y - scroll.Size.Y;
            float room = GetViewportRect().Size.Y - box.Position.Y - others - 16;
            scroll.CustomMinimumSize = new Vector2(0, Math.Max(120, Math.Min(h, room)));
            box.Size = Vector2.Zero; box.ResetSize();
            GD.Print("QA_STORY_TIP text=" + h + " shown=" + scroll.CustomMinimumSize.Y + " scroll=" + (h > scroll.CustomMinimumSize.Y + 1));
        }
        scroll.CustomMinimumSize = new Vector2(0, 120);
        Callable.From(() => Callable.From(Clamp).CallDeferred()).CallDeferred();
    }

    // The scenario reference card: the chaos symbols' effects for the difficulty being played (easy / standard on its front,
    // hard / expert on its back).
    void ShowReference(Control anchor, TableModel m, string code)
    {
        HideGoal();
        var card = S.Card(code);
        bool hard = m.Difficulty is "Hard" or "Expert";
        string text = J.String(card, hard ? "back_text" : "text");
        if (text.Length == 0) text = J.String(card, "text");
        var col = new VBoxContainer { CustomMinimumSize = new Vector2(460, 0), MouseFilter = MouseFilterEnum.Ignore }; col.AddThemeConstantOverride("separation", 8);
        col.AddChild(Style.Title(S.Name(code), 26));
        col.AddChild(Style.Text("冒险参考卡 · " + DiffName(m.Difficulty), 18, Style.Brass, Style.Kai));
        col.AddChild(TipRules(text));
        col.AddChild(Style.Text("点击查看原卡", 16, Style.PaperDim, Style.Kai));
        PlaceTip(anchor, col);
    }

    // The agenda: its name, its text, and how much doom is on it against its limit.
    void ShowAgenda(Control anchor, TableModel m)
    {
        HideGoal();
        if (m.Agenda is null) return;
        var col = new VBoxContainer { CustomMinimumSize = new Vector2(460, 0), MouseFilter = MouseFilterEnum.Ignore }; col.AddThemeConstantOverride("separation", 8);
        col.AddChild(Style.Title(m.Name(m.Agenda), 26));
        var text = J.String(S.Card(m.Agenda.Code), "text");
        if (text.Length > 0) col.AddChild(TipRules(text));
        col.AddChild(Style.Text("毁灭 " + m.AgendaDoomCount(m.Agenda) + " / " + Math.Max(1, m.DoomThreshold) + "：达到上限时密谋推进", 19, Style.Danger, Style.Kai));
        if (m.OtherDoomSubtracts(m.Agenda)) col.AddChild(Style.Text("场上其他毁灭从本密谋的毁灭中减去（场上共 " + m.SubtractDoomCount + " 个毁灭）", 17, Style.PaperDim, Style.Kai));
        PlaceTip(anchor, col);
    }

    // Rules text sized to the popup's width before layout, so the popup is only as tall as its text.
    static RichTextLabel TipRules(string text)
    {
        var r = CardReader.Rules(text, 19); r.CustomMinimumSize = new Vector2(460, 0); r.SizeFlagsVertical = SizeFlags.ShrinkBegin;
        return r;
    }

    // stay: the popup takes the pointer and outlives the anchor's mouse-exit while the pointer is on it (a scrolling story)
    Control PlaceTip(Control anchor, Control col, bool stay = false)
    {
        var box = Ui.Panel(col, 16, 0.97f); box.ZIndex = 160; box.MouseFilter = stay ? MouseFilterEnum.Stop : MouseFilterEnum.Ignore; AddChild(box);
        if (stay) { goalTipSticky = true; box.MouseExited += () => { if (!box.GetGlobalRect().HasPoint(pointer)) HideGoal(); }; }
        var r = anchor.GetGlobalRect(); var vp = GetViewportRect().Size;
        void Fit()
        {
            if (!IsInstanceValid(box)) return;
            box.Size = Vector2.Zero; box.ResetSize();
            box.Position = new Vector2(Math.Clamp(r.Position.X, 12, Math.Max(12, vp.X - box.Size.X - 12)), r.End.Y + 10);
        }
        Fit(); Callable.From(Fit).CallDeferred();
        goalTip = box;
        // a watch: once the pointer is off the plate (and off a box it may step onto) the box goes, whatever happened meanwhile —
        // the table rebuilt under the pointer takes the plate's mouse-exit with it (Yunke 11-21: the box stayed stuck)
        void Watch()
        {
            if (!IsInstanceValid(box) || goalTip != box) return;
            bool over = r.Grow(4).HasPoint(pointer) || (box.MouseFilter == MouseFilterEnum.Stop && box.GetGlobalRect().Grow(12).HasPoint(pointer));
            if (!over) { HideGoal(); return; }
            GetTree().CreateTimer(0.15).Timeout += Watch;
        }
        GetTree().CreateTimer(0.3).Timeout += Watch;
        return box;
    }
    bool goalTipSticky;
    // the anchor's mouse-exit: a sticky popup waits a moment for the pointer to cross the gap onto it
    void HideGoalSoon()
    {
        if (!goalTipSticky) { HideGoal(); return; }
        var tip = goalTip;
        GetTree().CreateTimer(0.12).Timeout += () => { if (tip == goalTip && tip is not null && IsInstanceValid(tip) && !tip.GetGlobalRect().HasPoint(pointer)) HideGoal(); };
    }
    void HideGoal()
    {
        goalTipSticky = false;
        if (goalTip is not null && IsInstanceValid(goalTip)) goalTip.QueueFree();
        goalTip = null;
    }

    // ───────────────────────── left: player area (slots and assets on top, investigator below)
    // the encounter card the core is holding up for view (focusedCards: a drawn card before its revelation), if any
    static string? FocusedEncounter(TableModel m)
    {
        var f = J.Get(m.Game, "focusedCards"); if (f.ValueKind != System.Text.Json.JsonValueKind.Array) f = J.Get(m.Game, "gameFocusedCards");
        foreach (var group in J.Items(f))
            foreach (var c in group.ValueKind == System.Text.Json.JsonValueKind.Array ? J.Items(group) : new[] { group })
                if (J.Tag(c) == "EncounterCard" && J.String(J.Get(c, "contents"), "cardCode") is { Length: > 0 } code) return CoreCatalog.Normalize(code);
        return null;
    }

    // the drawn card's identity (its id, or its code), so it flies in once
    static string FocusedEncounterKey(TableModel m)
    {
        var f = J.Get(m.Game, "focusedCards"); if (f.ValueKind != System.Text.Json.JsonValueKind.Array) f = J.Get(m.Game, "gameFocusedCards");
        foreach (var group in J.Items(f))
            foreach (var c in group.ValueKind == System.Text.Json.JsonValueKind.Array ? J.Items(group) : new[] { group })
                if (J.Tag(c) == "EncounterCard") { var k = J.Get(c, "contents"); return J.String(k, "id") is { Length: > 0 } id ? id : J.String(k, "cardCode"); }
        return "";
    }

    string drawnFlown = "";   // the drawn encounter card last flown into the threat area (cleared by an undo)
    CardView? drawnCard; string drawnCode = "";   // the drawn card's place in the threat area (FlyRevealedAway lands there)
    async void FlyDrawnIn(CardView cv, string code)
    {
        if (S.Settings.ReducedMotion == true) return;
        cv.Modulate = new Color(1, 1, 1, 0);
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);   // laid out: the card has its place, the deck its pile
        if (!IsInstanceValid(cv)) return;
        // the draw is being shown large (Reveal): the card goes from there to here when that is clicked away (FlyRevealedAway)
        if (revealing) { cv.Modulate = Colors.White; return; }
        if (encDeckNode is null || !IsInstanceValid(encDeckNode) || !encDeckNode.IsVisibleInTree()) { cv.Modulate = Colors.White; return; }
        var from = encDeckNode.GetGlobalRect(); var to = cv.GetGlobalRect();
        if (App.I.QaRunning) GD.Print("QA_DRAWN_FLY " + code);
        var back = RoundedBack(from.Size, 5, true); back.ZIndex = 140; back.MouseFilter = MouseFilterEnum.Ignore; AddChild(back);
        back.GlobalPosition = from.Position; back.Size = from.Size;
        Sfx.Click();
        var tw = back.CreateTween().SetParallel();
        tw.TweenProperty(back, "global_position", to.Position, 0.42f).SetTrans(Tween.TransitionType.Cubic).SetEase(Tween.EaseType.Out);
        tw.TweenProperty(back, "size", to.Size, 0.42f).SetTrans(Tween.TransitionType.Cubic).SetEase(Tween.EaseType.Out);
        tw.Chain().TweenCallback(Callable.From(() => back.PivotOffset = to.Size / 2));
        tw.Chain().TweenProperty(back, "scale:x", 0f, 0.12f);
        tw.Chain().TweenCallback(Callable.From(() =>
        {
            back.QueueFree();
            if (!IsInstanceValid(cv)) return;
            cv.PivotOffset = cv.Size / 2; cv.Scale = new Vector2(0f, 1f); cv.Modulate = Colors.White;
            cv.CreateTween().TweenProperty(cv, "scale:x", 1f, 0.14f);
        }));
    }

    // Top to bottom: slot counts → assets in play → investigator portrait with skills and name → tokens and action arrows.
    // No dark block behind it (Yunke): the portrait itself fades out to transparent at its edges, and everything sits low,
    // close to the bottom of the screen.
    float assetTopY = float.NaN;   // the top of the player's asset boxes, in screen pixels (PlayerArea, read by RightZone)
    void PlayerArea()
    {
        var m = model!; var me = m.Me; if (me is null) return;
        float W = SideW;
        var area = new Control { MouseFilter = MouseFilterEnum.Ignore }; Place(area, SideMargin, -PlayerH - SideMargin, SideMargin + W, -SideMargin, 0, 1, 0, 1);
        // the asset boxes sit right above the portrait, the portrait above the tokens, all close to the bottom
        const float tokensY = PlayerH - 54, headH = 196, headY = tokensY - 8 - headH;

        // 1. asset boxes, as on the 0712 web table: one rounded-rect box per slot position (hand, body, arcane, ally, other).
        //    A taken position shows its asset; a free one shows its kind's light icon. Assets without a slot follow.
        var positions = new List<Control>();
        var assets = m.AssetsOf(me).ToArray();
        var placed = new HashSet<string>();
        var slotInfo = m.Slots(me).ToDictionary(x => x.Kind, x => (x.Free, x.Total));
        foreach (var kind in new[] { "hand", "arcane", "body", "ally", "accessory", "tarot" })
        {
            // the tarot place is always shown (one box), as on the web table, even when the investigator has no tarot slot
            if (!slotInfo.TryGetValue(kind, out var v)) { if (kind != "tarot") continue; v = (1, 1); }
            var inKind = assets.Where(a => !placed.Contains(a.Id) && m.SlotOf(me, a.Id) == kind).ToList();
            // an asset that fills two slots of this kind (长柄大锤: both hands) shows in both (Yunke 11-22): the second box a dim
            // copy of it, so the free-slot count reads right
            var entries = m.SlotEntries(me, kind);
            int twice = inKind.Sum(a => Math.Max(0, entries.Count(e => e.Contains(a.Id)) - 1));
            var doubled = inKind.Where(a => entries.Count(e => e.Contains(a.Id)) > 1).ToList();
            int extraShown = 0;
            for (int i = 0; i < v.Total; i++)
            {
                if (i < inKind.Count) { placed.Add(inKind[i].Id); positions.Add(AssetTile(m, me, inKind[i], SlotBox)); continue; }
                if (extraShown < twice && doubled.Count > 0)
                {
                    var a2 = doubled[Math.Min(extraShown, doubled.Count - 1)]; extraShown++;
                    var copy = new CardView { Code = a2.Code, CustomMinimumSize = SlotBox, Size = SlotBox, ShowCost = false, ShowName = false, Modulate = new Color(1, 1, 1, 0.5f), TooltipText = m.Name(a2) + "（占用两个" + SlotName(kind) + "槽位）", MouseFilter = MouseFilterEnum.Pass };
                    positions.Add(copy); continue;
                }
                var empty = new Panel { CustomMinimumSize = SlotBox, MouseFilter = MouseFilterEnum.Pass, TooltipText = SlotName(kind) + "：空位" };
                var esb = new StyleBoxFlat { BgColor = new Color(1, 1, 1, 0.03f), BorderColor = new Color(Style.Brass, 0.3f), AntiAliasing = true }; esb.SetBorderWidthAll(1); esb.SetCornerRadiusAll(6);
                empty.AddThemeStyleboxOverride("panel", esb);
                if (Ui.SlotIcon(kind) is { } tex)
                {
                    var ic = Ui.Image(tex, new Vector2(36, 36)); ic.Material = Tint(new Color("f3e7cf")); ic.Modulate = new Color(1, 1, 1, 0.7f); ic.MouseFilter = MouseFilterEnum.Ignore;
                    empty.AddChild(ic); ic.Position = (SlotBox - new Vector2(36, 36)) / 2;
                }
                positions.Add(empty);
            }
        }
        foreach (var a in assets.Where(a => !placed.Contains(a.Id))) positions.Add(AssetTile(m, me, a, SlotBox));
        const int perRow = 4; const float gap = 8;
        int rows = Math.Max(1, (positions.Count + perRow - 1) / perRow);
        float gridH = rows * SlotBox.Y + (rows - 1) * gap;
        // the threat area sits right above these boxes (Yunke 11-20), so it is pushed up when the assets take more rows
        assetTopY = GetViewportRect().Size.Y - SideMargin - PlayerH + headY - 10 - gridH;
        for (int i = 0; i < positions.Count; i++)
        {
            var c = positions[i]; area.AddChild(c);
            c.Position = new Vector2(4 + (i % perRow) * (SlotBox.X + gap), headY - 10 - gridH + (i / perRow) * (SlotBox.Y + gap));
        }

        // 3. investigator portrait: painted art whose edges fade to transparent, skills across its top, name at its foot
        string ikey = "inv:" + me.Id;
        var head = new Control { MouseFilter = MouseFilterEnum.Stop, ClipContents = true, MouseDefaultCursorShape = CursorShape.PointingHand }; area.AddChild(head);
        head.Position = new Vector2(0, headY); head.Size = new Vector2(W, headH);
        // a parallel or later-cycle investigator has no painted art here: the html table's portrait of that version, else a crop
        // the AI-widened portrait (tools/dev/portrait_outpaint.py: the face fits the strip's height, the sides painted by SDXL)
        // when there is one, already at the strip's proportions; else the picture, fitted to the face below
        // (the AI-widened portraits are not used: Yunke 11-19 found them at odds with the paintings — the picture is cut as it is)
        Texture2D? wide = null;
        var portrait = wide ?? Ui.Art(me.Code) ?? ArtStore.Portrait(CoreCatalog.Normalize(me.Code)) ?? Ui.Portrait(me.Code);
        var art = new TextureRect { Texture = portrait, ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize, StretchMode = TextureRect.StretchModeEnum.Scale, MouseFilter = MouseFilterEnum.Ignore, TextureFilter = TextureFilterEnum.LinearWithMipmapsAnisotropic };
        if (portrait is not null)
        {
            float pw = portrait.GetWidth(), ph = portrait.GetHeight();
            float cover = Math.Max(W / pw, headH / ph);
            float sc = cover;
            var pf = wide is null ? PortraitFocus(CoreCatalog.Normalize(me.Code)) : null;
            // The whole head in the strip's height (Yunke 11-19: most faces were cut — the strip is wide and low, so a picture
            // scaled to its width shows only a band of the face): scale so the head (about 1.7 face boxes) takes ~88% of the
            // height, never smaller than the height itself; the picture may then be narrower than the strip.
            // (no shrinking to the face's height any more: the strip shows the picture cut at the strip's width, centred on the face)
            art.Size = new Vector2(pw * sc, ph * sc);
            // centred on the face (tools/dev/portrait_focus.py, found on the GPU), the face a little above the middle
            float ax = pf is { } f1 ? W / 2 - f1.X * art.Size.X : (W - art.Size.X) / 2;
            float ay = pf is { } f2 ? Math.Clamp(headH * 0.45f - f2.Y * art.Size.Y, headH - art.Size.Y, 0) : 0;
            if (art.Size.X >= W) ax = Math.Clamp(ax, W - art.Size.X, 0);
            else ax = Math.Clamp(ax, 0, W - art.Size.X);
            art.Position = new Vector2(ax, ay);
            if (art.Size.X < W - 1)
            {
                // narrower than the strip: behind it, the same picture at the strip's width, blurred and darker, fills the sides,
                // and the picture's own left and right edges fade into it
                var back = new TextureRect { Texture = portrait, ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize, StretchMode = TextureRect.StretchModeEnum.Scale, MouseFilter = MouseFilterEnum.Ignore };
                back.Size = new Vector2(pw * cover, ph * cover);
                back.Position = new Vector2((W - back.Size.X) / 2, pf is { } f3 ? Math.Clamp(headH * 0.45f - f3.Y * back.Size.Y, headH - back.Size.Y, 0) : 0);
                back.Material = BlurredBack(-back.Position, new Vector2(W, headH));
                head.AddChild(back);
                float side = Math.Min(70, art.Size.X * 0.18f);
                var origin = new Vector2(Math.Max(0, -art.Position.X), -art.Position.Y);
                art.Material = Ui.EdgeAlpha(origin, new Vector2(Math.Min(art.Size.X, W - art.Position.X) - origin.X, headH),
                    art.Position.X > 1 ? side : 46, 34, art.Position.X + art.Size.X < W - 1 ? side : 46, 58);
            }
            // the visible window is the head rect; its four edges fade the art to nothing
            else art.Material = Ui.EdgeAlpha(-art.Position, new Vector2(W, headH), 46, 34, 46, 58);
        }
        head.AddChild(art);
        // One meaning per moment: while the question asks to pick an investigator, a click on the portrait picks yourself (as the
        // centre cards and the seat portraits do); only otherwise does it use your investigator's own ability.
        var pickMe = byAnchor.TryGetValue(ikey, out var mine0) ? mine0.Where(c => c.Tag is "PortraitLabel" or "TargetLabel").ToList() : new();
        head.GuiInput += e =>
        {
            if (e is not InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Left }) return;
            if (pickMe.Count == 1) Choose(pickMe[0]); else if (pickMe.Count == 0) ActOn(ikey, head, me.Code); else ActOn(ikey, head, me.Code);
        };
        Peek.Attach(head, me.Code, () => InvestigatorExtra(me));
        Anchor(ikey, head);
        // the investigator's own ability can be used now (Roland after defeating an enemy…): the portrait glows and breathes, and a
        // click on it uses the ability (Yunke 11-18 #15, as on the html table where the investigator card itself is clicked)
        if (byAnchor.TryGetValue(ikey, out var own) && (pickMe.Count > 0 || own.Any(c => c.Tag == "AbilityLabel")))
        {
            var glow = new Panel { MouseFilter = MouseFilterEnum.Ignore, Position = new Vector2(2, headY + 2), Size = new Vector2(W - 4, headH - 4), ZIndex = 5 };
            var gsb = new StyleBoxFlat { DrawCenter = false, BgColor = new Color(0, 0, 0, 0), BorderColor = Style.Amber, ShadowColor = new Color(Style.Amber, 0.55f), ShadowSize = 16, AntiAliasing = true };
            gsb.SetBorderWidthAll(3); gsb.SetCornerRadiusAll(12); glow.AddThemeStyleboxOverride("panel", gsb); area.AddChild(glow);
            var tag = Style.Text(pickMe.Count > 0 ? "点击头像选择自己" : "点击头像发动能力", 17, Style.Amber, Style.Kai); tag.MouseFilter = MouseFilterEnum.Ignore; tag.ZIndex = 5;
            tag.AddThemeConstantOverride("outline_size", 6); tag.AddThemeColorOverride("font_outline_color", Colors.Black); area.AddChild(tag); tag.Position = new Vector2(18, headY + headH - 74);
            if (S.Settings.ReducedMotion != true) { var tw = glow.CreateTween().SetLoops(); tw.TweenProperty(glow, "modulate:a", 0.35f, 0.8f); tw.TweenProperty(glow, "modulate:a", 1f, 0.8f); }
        }
        // the four skills in one compact row across the portrait's top-right corner, right-aligned, no backdrop — clear of the
        // face (Yunke 11-19 r9); each glyph and digit carries a dark outline so it reads on the art
        var skills = new HBoxContainer { MouseFilter = MouseFilterEnum.Ignore, Alignment = BoxContainer.AlignmentMode.End }; skills.AddThemeConstantOverride("separation", 8);
        area.AddChild(skills);
        skills.Resized += () => skills.Position = new Vector2(W - 12 - skills.Size.X, headY + 6);
        foreach (var (k, color) in new[] { ("willpower", Style.Willpower), ("intellect", Style.Intellect), ("combat", Style.Combat), ("agility", Style.Agility) })
        {
            var h = new HBoxContainer { MouseFilter = MouseFilterEnum.Ignore }; h.AddThemeConstantOverride("separation", 1);
            var g = Ui.Glyph(k, 22, color); g.VerticalAlignment = VerticalAlignment.Center;
            g.AddThemeConstantOverride("outline_size", 6); g.AddThemeColorOverride("font_outline_color", new Color(0, 0, 0, 0.85f)); h.AddChild(g);
            int sv = m.SkillValue(me, k), sb0 = m.Stat(me, k);   // with the core's modifiers; changed values are tinted
            var n = Style.Text(sv.ToString(), 26, sv > sb0 ? Style.Good : sv < sb0 ? Style.Danger : Style.HoverText, Style.Symbols);
            n.VerticalAlignment = VerticalAlignment.Center;
            if (sv != sb0) { n.MouseFilter = MouseFilterEnum.Pass; n.TooltipText = "基础 " + sb0 + "，现在 " + sv; } n.AddThemeConstantOverride("outline_size", 7); n.AddThemeColorOverride("font_outline_color", new Color(0, 0, 0, 0.85f)); h.AddChild(n);
            skills.AddChild(h);
        }
        var name = Style.Text(m.Name(me), 30, Style.HoverText, Style.Kai); name.AddThemeConstantOverride("outline_size", 8); name.AddThemeColorOverride("font_outline_color", new Color(0, 0, 0, 0.85f));
        name.MouseFilter = MouseFilterEnum.Ignore; area.AddChild(name); name.Position = new Vector2(18, headY + headH - 50);

        // 4. tokens (0712 web style, packed) and action arrows on the bottom row
        float rowY = tokensY;
        var tokens = new HBoxContainer { MouseFilter = MouseFilterEnum.Ignore }; tokens.AddThemeConstantOverride("separation", 12);
        area.AddChild(tokens); tokens.Position = new Vector2(16, rowY);
        foreach (var (kind, v, tip) in new[] { ("resource", m.Resources(me), "资源"), ("health", m.Damage(me), "伤害（生命 " + (m.Health(me) - m.Damage(me)) + "/" + m.Health(me) + "）"), ("horror", m.Horror(me), "恐惧（理智 " + (m.Sanity(me) - m.Horror(me)) + "/" + m.Sanity(me) + "）"), ("clue", m.Clues(me), "线索") })
        { var t = Ui.TokenNumber(kind, v, 46); t.TooltipText = tip; t.MouseFilter = MouseFilterEnum.Pass; tokens.AddChild(kind == "resource" ? Clickable(t, "res:" + me.Id, hex: true) : t); tokenNodes[kind] = t; }
        // the keys this investigator holds (html Resources.vue; QQ #29: they were not shown at all): a row on the right of the
        // name line, above the action arrows, so they never push into the arrows
        if (KeyRow(me.Raw, 38, owner: m.Name(me)) is { } myKeys)
        { int nk = myKeys.GetChildCount(); area.AddChild(myKeys); myKeys.Position = new Vector2(W - 14 - nk * 38 - (nk - 1) * 4, rowY - 50); }
        int acts = m.Actions(me), shown = Math.Max(3, acts);
        var arrows = new HBoxContainer { MouseFilter = MouseFilterEnum.Ignore, Alignment = BoxContainer.AlignmentMode.End }; arrows.AddThemeConstantOverride("separation", 6);
        area.AddChild(arrows); arrows.Position = new Vector2(W - 170, rowY); arrows.Size = new Vector2(160, 48);
        if (m.Phase == "InvestigationPhase" || acts > 0)
            for (int i = 0; i < shown; i++) { var g = Ui.Glyph("action", 36, i < acts ? Style.Amber : new Color(0.37f, 0.34f, 0.26f)); g.VerticalAlignment = VerticalAlignment.Center; g.AddThemeConstantOverride("outline_size", 5); g.AddThemeColorOverride("font_outline_color", new Color(0, 0, 0, 0.7f)); arrows.AddChild(g); }
        // Extra actions (the core's additionalActions: still to use this turn; usedAdditionalActions: spent), drawn after the
        // ordinary ones in the investigator's class colour, as the html version does (Investigator.vue: `${class}Action`) —
        // Yunke 11-22: 黛西's Tome-only action, 里奥·德卢卡's extra one. Lit while unused; its tooltip says what it is for.
        if (m.Phase == "InvestigationPhase")
        {
            var extra = J.Items(J.Get(me.Raw, "additionalActions")).Select(a => (a, Used: false)).Concat(J.Items(J.Get(me.Raw, "usedAdditionalActions")).Select(a => (a, Used: true))).ToList();
            // the html version's colours for them (Investigator.vue .seekerAction …: the class's "extra-dark"), a touch lighter so
            // they read on the dark table — unlike the ordinary gold arrows on purpose
            string faction = J.String(S.Card(CoreCatalog.Normalize(me.Code)), "faction_code");
            var cls = new Color(faction switch { "guardian" => "0c5693", "seeker" => "aa6005", "rogue" => "186a1d", "mystic" => "5e4389", "survivor" => "a3262d", _ => "d8dee9" });
            if (cls.Luminance < 0.32f) cls = cls.Lightened(0.3f);   // the dark blue / green / purple would vanish on the wood
            foreach (var (a, used) in extra)
            {
                var g = Ui.Glyph("action", 36, used ? new Color(cls.Darkened(0.5f), 0.7f) : cls); g.VerticalAlignment = VerticalAlignment.Center;
                g.AddThemeConstantOverride("outline_size", 5); g.AddThemeColorOverride("font_outline_color", new Color(0, 0, 0, 0.7f));
                g.MouseFilter = MouseFilterEnum.Pass; g.TooltipText = ExtraActionTip(a) + (used ? "：本回合已用" : "");
                arrows.AddChild(g);
            }
            if (extra.Count > 0) { float arrowsW = 160 + extra.Count * 46; arrows.Position = new Vector2(W - arrowsW - 10, rowY); arrows.Size = new Vector2(arrowsW, 48); }
        }
    }

    // The encounter deck and its discard pile, bottom right above the end-turn button (Yunke 11-22: there was no way to see
    // how many cards were left, and cards an act discarded from it vanished without a trace). The deck shows its back and its
    // count; the discard its top card and count, and a click opens it.
    Control? encDeckNode, encDiscardNode;
    void EncounterPiles(TableModel m)
    {
        if (m.Scenario.ValueKind != JsonValueKind.Object || m.State.Round == 0 && m.EncounterDeckSize == 0) return;   // not before the scenario is set up
        const float pw = 64, ph = 92;
        int left = m.EncounterDeckSize; var disc = m.EncounterDiscard.ToArray();
        // three piles left to right: the victory display, the encounter discard, the encounter deck (Yunke 11-22: the victory
        // display joins them; the deck is one card, not a stack)
        var box = new Control { MouseFilter = MouseFilterEnum.Ignore, CustomMinimumSize = new Vector2(3 * pw + 52, ph + 30) };
        Place(box, -SideMargin - 60 - 160 - pw - 26, -SideMargin - 58 - ph - 44, -SideMargin - 60 - 160 + 2 * pw + 26, -SideMargin - 58 - 14, 1, 1, 1, 1);
        var deck = new Control { MouseFilter = MouseFilterEnum.Pass, CustomMinimumSize = new Vector2(pw, ph), Size = new Vector2(pw, ph), TooltipText = "遭遇牌库 " + left + " 张" };
        if (left > 0) deck.AddChild(RoundedBack(new Vector2(pw, ph), 5, true)); else deck.AddChild(PileEdge(new Rect2(0, 0, pw, ph)));
        box.AddChild(deck); deck.Position = new Vector2(2 * (pw + 26), 0);
        var dl = PileLabel("遭遇 " + left, ph + 4); deck.AddChild(dl);
        var dz = new Control { MouseFilter = MouseFilterEnum.Stop, MouseDefaultCursorShape = CursorShape.PointingHand, CustomMinimumSize = new Vector2(pw, ph), Size = new Vector2(pw, ph), TooltipText = "遭遇弃牌堆 " + disc.Length + " 张，点击查看" };
        if (disc.Length > 0)
        {
            var top = new CardView { Code = disc[0], CustomMinimumSize = new Vector2(pw, ph), Size = new Vector2(pw, ph), ShowName = false, ShowCost = false, Redrawn = false, RotationDegrees = 3 };
            top.OnClick = () => OpenEncounterDiscard(); dz.AddChild(top);
        }
        else dz.AddChild(PileEdge(new Rect2(0, 0, pw, ph)));
        dz.AddChild(PileLabel("弃牌 " + disc.Length, ph + 4));
        dz.GuiInput += e => { if (e is InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Left }) OpenEncounterDiscard(); };
        box.AddChild(dz); dz.Position = new Vector2(pw + 26, 0);
        // the victory display: its top card, how many cards and the victory points they are worth
        var vic = m.VictoryDisplay.ToArray(); int points = vic.Sum(Victory);
        var vz = new Control { MouseFilter = MouseFilterEnum.Stop, MouseDefaultCursorShape = CursorShape.PointingHand, CustomMinimumSize = new Vector2(pw, ph), Size = new Vector2(pw, ph), TooltipText = "胜利区 " + vic.Length + " 张 · 胜利 " + points + "，点击查看" };
        if (vic.Length > 0)
        {
            var top = new CardView { Code = vic[^1], CustomMinimumSize = new Vector2(pw, ph), Size = new Vector2(pw, ph), ShowName = false, ShowCost = false, Redrawn = false, RotationDegrees = -3 };
            top.OnClick = () => OpenVictoryDisplay(); vz.AddChild(top);
        }
        else vz.AddChild(PileEdge(new Rect2(0, 0, pw, ph)));
        if (points > 0) { var vb = VictoryBadge(points, 20); vz.AddChild(vb); vb.Position = new Vector2(pw - vb.Size.X + 6, -8); }
        vz.AddChild(PileLabel("胜利区 " + vic.Length, ph + 4));
        vz.GuiInput += e => { if (e is InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Left }) OpenVictoryDisplay(); };
        box.AddChild(vz); vz.Position = Vector2.Zero;
        encDeckNode = deck; encDiscardNode = dz;
    }

    void OpenVictoryDisplay()
    {
        if (model is null) return;
        var cards = model.VictoryDisplay.ToArray();
        var col = new VBoxContainer(); col.AddThemeConstantOverride("separation", 16);
        col.AddChild(Style.Title("胜利区 · " + cards.Length + " 张 · 胜利 " + cards.Sum(Victory), 26));
        if (cards.Length == 0) col.AddChild(Style.Text("胜利区还没有卡牌。", 20, Style.PaperDim));
        var grid = new GridContainer { Columns = 6 }; grid.AddThemeConstantOverride("h_separation", 14); grid.AddThemeConstantOverride("v_separation", 14);
        foreach (var code in cards) { var c = code; var cv = new CardView { Code = c, CustomMinimumSize = new Vector2(110, 154), Size = new Vector2(110, 154), Redrawn = false }; cv.OnClick = () => CardReader.Open(c, printed: true); AddVictoryCorner(cv, Victory(c), 20); grid.AddChild(cv); }
        Control scrim = null!;
        if (cards.Length > 0) { var scroll = Ui.Scroll(grid); scroll.CustomMinimumSize = new Vector2(0, 520); col.AddChild(scroll); }
        col.AddChild(Ui.MinimalButton("关闭", () => App.I.CloseModal(scrim), 22));
        scrim = App.I.Modal(Ui.Panel(col, 30));
    }

    void OpenEncounterDiscard()
    {
        if (model is null) return;
        var cards = model.EncounterDiscard.ToArray();
        var col = new VBoxContainer(); col.AddThemeConstantOverride("separation", 16);
        col.AddChild(Style.Title("遭遇弃牌堆 · " + cards.Length + " 张（最近弃置的在前）", 26));
        if (cards.Length == 0) col.AddChild(Style.Text("遭遇弃牌堆是空的。", 20, Style.PaperDim));
        var grid = new GridContainer { Columns = 6 }; grid.AddThemeConstantOverride("h_separation", 14); grid.AddThemeConstantOverride("v_separation", 14);
        foreach (var code in cards) { var c = code; var cv = new CardView { Code = c, CustomMinimumSize = new Vector2(110, 154), Size = new Vector2(110, 154), Redrawn = false }; cv.OnClick = () => CardReader.Open(c, printed: true); grid.AddChild(cv); }
        Control scrim = null!;
        if (cards.Length > 0) { var scroll = Ui.Scroll(grid); scroll.CustomMinimumSize = new Vector2(0, 520); col.AddChild(scroll); }
        col.AddChild(Ui.MinimalButton("关闭", () => App.I.CloseModal(scrim), 22));
        scrim = App.I.Modal(Ui.Panel(col, 30));
    }

    // Encounter cards discarded straight from the deck (no draw: an act's or a card's "discard the top N"): backs leave the
    // deck one after another and turn face up onto the discard pile.
    async Task FlyEncounterDiscards(IReadOnlyList<string> codes)
    {
        if (S.Settings.ReducedMotion == true || encDeckNode is null || encDiscardNode is null || !IsInstanceValid(encDeckNode) || !IsInstanceValid(encDiscardNode)) return;
        if (App.I.QaRunning) GD.Print("QA_ENC_DISCARD_FLY " + string.Join(",", codes));
        var from = encDeckNode.GetGlobalRect(); var to = encDiscardNode.GetGlobalRect();
        for (int i = codes.Count - 1; i >= 0; i--)
        {
            var back = RoundedBack(from.Size, 5, true); back.ZIndex = 140; AddChild(back); back.GlobalPosition = from.Position; back.PivotOffset = from.Size / 2;
            var tw = back.CreateTween().SetParallel();
            tw.TweenProperty(back, "global_position", to.Position, 0.32f).SetTrans(Tween.TransitionType.Cubic).SetEase(Tween.EaseType.Out);
            tw.TweenProperty(back, "scale:x", 0f, 0.16f).SetDelay(0.16f);
            string code = codes[i];
            tw.Chain().TweenCallback(Callable.From(() =>
            {
                back.QueueFree();
                var face = new CardView { Code = code, CustomMinimumSize = to.Size, Size = to.Size, ShowName = false, ShowCost = false, Redrawn = false, MouseFilter = MouseFilterEnum.Ignore, ZIndex = 140 };
                AddChild(face); face.GlobalPosition = to.Position; face.PivotOffset = to.Size / 2; face.Scale = new Vector2(0f, 1f);
                var t2 = face.CreateTween(); t2.TweenProperty(face, "scale:x", 1f, 0.14f); t2.TweenInterval(0.2); t2.TweenCallback(Callable.From(face.QueueFree));
            }));
            await ToSignal(GetTree().CreateTimer(0.22), SceneTreeTimer.SignalName.Timeout);
        }
        await ToSignal(GetTree().CreateTimer(0.4), SceneTreeTimer.SignalName.Timeout);
    }

    // over the hand size: (cards in hand, the limit), or null
    static (int Have, int Limit)? HandOver(TableModel m, Entity inv)
    {
        int have = m.Hand(inv).Count(), limit = J.Int(inv.Raw, "handSize", 8);
        return have > limit ? (have, limit) : null;
    }

    // what an extra action may be spent on (the core's AdditionalActionType)
    static readonly Dictionary<string, string> TraitWords = new() { ["Tome"] = "典籍", ["Spell"] = "法术", ["Weapon"] = "武器", ["Ally"] = "盟友", ["Item"] = "物品" };
    string ExtraActionTip(JsonElement a)
    {
        var kind = J.Get(a, "kind"); string tag = J.Tag(kind);
        var src = J.Get(a, "source"); string from = "";
        try { var e = ClientDisplay.Reference(model!.Game, src); var c = ClientDisplay.CardCode(e); if (c.Length > 0) from = S.Name(c); } catch { }
        string what = tag switch
        {
            "TraitRestrictedAdditionalAction" => J.Get(kind, "contents") is { ValueKind: JsonValueKind.Array } arr && arr.GetArrayLength() > 0 && arr[0].ValueKind == JsonValueKind.String
                ? "只能用于" + (TraitWords.TryGetValue(arr[0].GetString()!, out var w) ? w : arr[0].GetString()) + "能力" : "有使用限制",
            "ActionRestrictedAdditionalAction" => "只能用于指定的行动",
            "AbilityRestrictedAdditionalAction" or "AbilityMatchingAdditionalAction" => "只能用于指定的能力",
            "PlayCardRestrictedAdditionalAction" => "只能用于打出指定的卡牌",
            "EffectAction" => "效果给予的行动",
            _ => "可用于任何行动",
        };
        return "额外行动" + (from.Length > 0 ? "（" + from + "）" : "") + "：" + what;
    }

    static readonly Dictionary<string, Texture2D?> widePortraits = new();
    static Texture2D? WidePortrait(string code)
    {
        if (widePortraits.TryGetValue(code, out var hit)) return hit;
        Texture2D? tex = null;
        try
        {
            var file = System.IO.Path.Combine(App.I.Assets, "art-portrait-wide", code + ".png");
            if (System.IO.File.Exists(file) && Image.LoadFromFile(file) is { } img) { img.GenerateMipmaps(); tex = ImageTexture.CreateFromImage(img); }
        }
        catch (Exception e) { GD.PushWarning("wide portrait: " + e.Message); }
        return widePortraits[code] = tex;
    }

    // the blurred, darkened copy behind a portrait narrower than its strip; its own window edges fade like the portrait's
    static Shader? blurBackShader;
    static ShaderMaterial BlurredBack(Vector2 origin, Vector2 window)
    {
        blurBackShader ??= new Shader { Code = @"shader_type canvas_item;
uniform vec2 origin; uniform vec2 window; uniform vec4 fade = vec4(46.0, 34.0, 46.0, 58.0);
varying vec2 p;
void vertex(){ p = VERTEX; }
void fragment(){
    vec2 px = TEXTURE_PIXEL_SIZE * 14.0;
    vec4 c = vec4(0.0); float n = 0.0;
    for (int i = -3; i <= 3; i++) for (int j = -3; j <= 3; j++) { c += texture(TEXTURE, UV + vec2(float(i), float(j)) * px); n += 1.0; }
    c /= n;
    vec2 q = p - origin;
    float a = smoothstep(0.0, fade.x, q.x) * smoothstep(0.0, fade.y, q.y) * smoothstep(0.0, fade.z, window.x - q.x) * smoothstep(0.0, fade.w, window.y - q.y);
    COLOR = vec4(c.rgb * 0.62, c.a * a);
}" };
        var m = new ShaderMaterial { Shader = blurBackShader };
        m.SetShaderParameter("origin", origin); m.SetShaderParameter("window", window);
        return m;
    }

    static Dictionary<string, Vector3>? portraitFocus;
    // [fx, fy, face height] as fractions of the picture (tools/dev/portrait_focus.py); face height 0 when unknown
    static Vector3? PortraitFocus(string code)
    {
        if (portraitFocus is null)
        {
            portraitFocus = new();
            try
            {
                var file = System.IO.Path.Combine(App.I.Assets, "build-data", "portrait-focus.json");
                if (System.IO.File.Exists(file))
                    foreach (var p in J.Props(JsonDocument.Parse(System.IO.File.ReadAllText(file)).RootElement))
                        if (p.Value.ValueKind == JsonValueKind.Array && p.Value.GetArrayLength() >= 2)
                            portraitFocus[p.Name] = new Vector3((float)p.Value[0].GetDouble(), (float)p.Value[1].GetDouble(), p.Value.GetArrayLength() > 2 ? (float)p.Value[2].GetDouble() : 0f);
            }
            catch (Exception e) { GD.PushWarning("portrait focus: " + e.Message); }
        }
        return portraitFocus.TryGetValue(code, out var f) ? f : null;
    }

    static string SlotName(string k) => k switch { "hand" => "手部", "arcane" => "魔法", "accessory" => "其他", "body" => "背部", "ally" => "盟友", "tarot" => "塔罗", "head" => "头部", _ => k };

    static readonly Vector2 SlotBox = new(98, 64);

    Control AssetTile(TableModel m, Entity me, Entity a, Vector2 size)
    {
        string akey = "asset:" + a.Id; var code = a.Code;
        var box = new Control { CustomMinimumSize = size, Size = size, MouseFilter = MouseFilterEnum.Ignore };
        bool exhausted = J.Get(a.Raw, "exhausted").ValueKind == JsonValueKind.True;
        // its fast abilities, while usable, are a lightning button on the card; the card itself lights for the rest (Yunke 11-19)
        // QQ #31 ("装备区的闪电能力太难点了", worst on a phone): the button is bigger (26 → 32), and while the fast abilities are
        // all the card offers, a click anywhere on the card uses them too — the button stays as the sign of what it does
        const float fastD = 32;
        var fastBtn = FastButton(akey, code, fastD, false);
        var cv = new CardView { Code = code, Key = akey, CustomMinimumSize = size, Size = size, ShowName = false, ShowCost = false, Actionable = CardActionable(akey, fastBtn is not null), Glow = AssignGlow(akey) };
        if (cv.Actionable) cv.OnClick = () => ActOn(akey, cv, code);
        else if (fastBtn is not null && FastChoices(akey) is { Count: > 0 } fastOnly)
            cv.OnClick = () => { if (busy) return; Sfx.Click(); if (fastOnly.Count == 1) Choose(fastOnly[0]); else InPlaceMenu(cv, S.Name(code), fastOnly, code); };
        if (exhausted) cv.Modulate = new Color(0.55f, 0.55f, 0.55f);
        Anchor(akey, cv); box.AddChild(cv);
        int uses = TableModel.Uses(a);
        if (uses > 0)
        {
            // the uses sit on the card as a token (as on the 0712 web table), its kind in small type under it
            var tok = Ui.TokenNumber("resource", uses, 34); tok.TooltipText = TableModel.UsesName(a) + " " + uses; tok.MouseFilter = MouseFilterEnum.Pass;
            box.AddChild(tok); tok.Position = new Vector2(size.X - 30, -12);
            var kind = Style.Text(TableModel.UsesName(a), 14, Style.HoverText, Style.Kai); kind.HorizontalAlignment = HorizontalAlignment.Center; kind.MouseFilter = MouseFilterEnum.Ignore;
            kind.AddThemeConstantOverride("outline_size", 5); kind.AddThemeColorOverride("font_outline_color", new Color(0, 0, 0, 0.9f));
            // its kind beside the token, on the left (under it, it ran into the lightning button: QQ #31)
            kind.HorizontalAlignment = HorizontalAlignment.Right;
            box.AddChild(kind); kind.Position = new Vector2(size.X - 78, -4); kind.Size = new Vector2(46, 18);
        }
        // its trigger switch (总是触发 / 只在自己回合 / 停止触发) once its fast or reaction ability is known (TriggerModes)
        string rawCard = J.String(a.Raw, "cardCode");
        if (rawCard.Length > 0 && TriggerModes.Known(m, me.Id, rawCard) is { Count: > 0 } idx)
        {
            // a thin strip across the bottom of the card (the slot rows sit close together)
            // several (饥饿霉菌): one square opens a window with a switch per ability (Yunke 11-22)
            var sw = idx.Count > 1 ? MultiModeSwitch(m, me.Id, rawCard, idx) : ModeSwitch(m, me.Id, rawCard, idx[0], false); box.AddChild(sw); sw.ZIndex = 6;
            sw.Size = new Vector2(22, 22); sw.Position = new Vector2(3, size.Y - 25);   // a small square in the lower-left corner (Yunke 11-20: clear of the ability buttons)
        }
        // damage and horror on it (allies, Yunke 11-19 r9 #17): tokens on its top-left corner
        // r10: always its health and sanity left out of its printed limit, not only once it is hurt ("没受伤时也要知道上限")
        // only the tokens placed on it; its limits are in the hover box (Yunke 11-19 r11)
        var hurt = new HBoxContainer { MouseFilter = MouseFilterEnum.Ignore }; hurt.AddThemeConstantOverride("separation", 2);
        if (m.Damage(a) > 0) { var t = Ui.TokenNumber("health", m.Damage(a), 30); t.MouseFilter = MouseFilterEnum.Pass; t.TooltipText = "伤害 " + m.Damage(a); hurt.AddChild(t); }
        if (m.Horror(a) > 0) { var t = Ui.TokenNumber("horror", m.Horror(a), 30); t.MouseFilter = MouseFilterEnum.Pass; t.TooltipText = "恐惧 " + m.Horror(a); hurt.AddChild(t); }
        if (hurt.GetChildCount() > 0) { box.AddChild(hurt); hurt.ZIndex = 5; hurt.Position = new Vector2(-8, -10); }
        cv.PeekExtra = () => UnitExtra(a);
        if (fastBtn is not null) { box.AddChild(fastBtn); fastBtn.ZIndex = 7; fastBtn.Position = new Vector2(size.X - fastD - 2, size.Y - fastD - 2); }   // bottom-right, clear of the uses token
        var slot = m.SlotOf(me, a.Id);
        if (slot.Length > 0 && Ui.SlotIcon(slot) is { } st)
        { var ic = Ui.Image(st, new Vector2(18, 18)); ic.Material = Tint(Style.HoverText); box.AddChild(ic); ic.Position = new Vector2(5, size.Y - 23); }
        return box;
    }

    Control InvestigatorExtra(Entity inv)
    {
        var m = model!;
        var col = new VBoxContainer { MouseFilter = MouseFilterEnum.Ignore }; col.AddThemeConstantOverride("separation", 4);
        col.AddChild(Style.Text("生命 " + (m.Health(inv) - m.Damage(inv)) + " / " + m.Health(inv) + "　　理智 " + (m.Sanity(inv) - m.Horror(inv)) + " / " + m.Sanity(inv), 19, Style.Paper));
        col.AddChild(Style.Text("牌库 " + m.DeckSize(inv) + " · 弃牌 " + m.Discard(inv).Count() + " · 手牌 " + m.Hand(inv).Count(), 17, Style.PaperDim));
        return col;
    }

    static readonly Dictionary<Color, ShaderMaterial> tints = new();
    // Recolour a silhouette icon (keeps its alpha).
    public static ShaderMaterial Tint(Color c)
    {
        if (tints.TryGetValue(c, out var m)) return m;
        m = new ShaderMaterial { Shader = tintShader ??= new Shader { Code = @"shader_type canvas_item;
uniform vec4 tint : source_color = vec4(1.0);
void fragment(){ COLOR = vec4(tint.rgb, texture(TEXTURE, UV).a * tint.a); }" } };
        m.SetShaderParameter("tint", c); tints[c] = m; return m;
    }
    static Shader? tintShader;

    // ───────────────────────── enemies: tall redrawn cards; engaged ones sit above the player area
    CardView EnemyCard(Entity e, bool engaged, Vector2 size)
    {
        var m = model!; string key = "enemy:" + e.Id; var code = e.Code;
        int hp = m.EnemyHealth(e), dmg = m.Damage(e);
        bool exhausted = J.Get(e.Raw, "exhausted").ValueKind == JsonValueKind.True;
        var cv = new CardView
        {
            Code = code, Key = key, CustomMinimumSize = size, Size = size, ShowCost = false,
            Actionable = byAnchor.ContainsKey(key), Rim = engaged ? new Color("c0543e") : null,
            EnemyStats = (m.EnemyFight(e), hp < 0 ? -1 : Math.Max(0, hp - dmg), m.EnemyEvade(e)),
            HealthHurt = dmg > 0, PeekExtra = () => EnemyExtra(e),
            // doom on an enemy (the cultists of The Midnight Masks gather it) and clues it holds sit on its card as tokens
            Badges = new[] { ("doom", m.Doom(e)), ("clue", m.Clues(e)) },
        };
        cv.OnClick = () => ActOn(key, cv, code);
        AddVictoryCorner(cv, Victory(code), 22);
        if (exhausted) cv.Modulate = new Color(0.6f, 0.6f, 0.6f);
        AddKeyCorner(cv, e.Raw, 26, m.Name(e));   // a key the enemy holds (QQ #29)
        if (attacking.Contains(e.Id)) { var target = cv; cv.Ready += () => MarkAttacker(target); }
        Anchor(key, cv);
        TuckAttached(m, cv, e, "AttachedToEnemy");
        return cv;
    }

    // a row of icon + number pairs for a hover box (Yunke 11-19 r9 #14: the basic numbers as icons and digits)
    static HBoxContainer IconRow(IEnumerable<(Control Icon, string Value, Color Tint)> cells)
    {
        var row = new HBoxContainer { MouseFilter = MouseFilterEnum.Ignore }; row.AddThemeConstantOverride("separation", 18);
        foreach (var (icon, value, tint) in cells)
        {
            var c = new HBoxContainer { MouseFilter = MouseFilterEnum.Ignore }; c.AddThemeConstantOverride("separation", 5);
            icon.SizeFlagsVertical = SizeFlags.ShrinkCenter; c.AddChild(icon);
            var n = Style.Text(value, 24, tint, Style.Symbols); n.VerticalAlignment = VerticalAlignment.Center; c.AddChild(n);
            row.AddChild(c);
        }
        return row;
    }
    static Control TokIcon(string kind) { var i = Ui.Image(Ui.Stat(kind), new Vector2(26, 26)); i.MouseFilter = MouseFilterEnum.Ignore; return i; }
    static Control GlyphIcon(string key, Color c) { var g = Ui.Glyph(key, 22, c); g.VerticalAlignment = VerticalAlignment.Center; return g; }

    // Victory points printed on a card (locations: on their revealed side), 0 when none
    // (app/assets/build-data/victory.json, from the html version's official card data via tools/dev/extract_victory.py — the
    // rules core's catalog has no victory field)
    static Dictionary<string, int>? victoryPoints;
    static int Victory(string code)
    {
        if (victoryPoints is null)
        {
            victoryPoints = new();
            try
            {
                var file = System.IO.Path.Combine(App.I.Assets, "build-data", "victory.json");
                if (System.IO.File.Exists(file))
                    foreach (var p in JsonDocument.Parse(System.IO.File.ReadAllText(file)).RootElement.EnumerateObject())
                        if (p.Value.TryGetInt32(out int v)) victoryPoints[p.Name] = v;
            }
            catch (Exception e) { GD.PushWarning("victory points: " + e.Message); }
        }
        return victoryPoints.TryGetValue(CoreCatalog.Normalize(code), out int n) ? n : 0;
    }

    // The victory points on a card's corner (Yunke 11-19: locations and enemies did not show them; then "符号不太好看，换一个，
    // 并且文字直接叠在符号上"): a gold laurel wreath around a dark disc, the number on it in the middle.
    static Control VictoryBadge(int n, float h)
    {
        float d = h * 1.2f;
        var box = new Control { MouseFilter = MouseFilterEnum.Pass, TooltipText = "胜利" + n, CustomMinimumSize = new Vector2(d, d), Size = new Vector2(d, d) };
        // Yunke 11-22: a medal on two ribbon tails (the icon he sent) instead of the laurel; the number sits in the disc
        var medal = new LineIcon { Kind = "medal", Ink = new Color("e8bf5c"), Size = new Vector2(d, d) };
        box.AddChild(medal);
        var t = Style.Text(n.ToString(), (int)Math.Max(10, d * 0.42f), new Color("fff1c8"), Style.Symbols);
        t.AddThemeConstantOverride("outline_size", 4); t.AddThemeColorOverride("font_outline_color", new Color(0.1f, 0.06f, 0.02f)); t.MouseFilter = MouseFilterEnum.Ignore;
        box.AddChild(t); t.Size = new Vector2(d, d * 0.76f); Ui.CenterDigits(t);
        return box;
    }

    // a victory badge in a card view's top-right corner, once it has its size
    static void AddVictoryCorner(CardView cv, int n, float h)
    {
        if (n <= 0) return;
        cv.Ready += () =>
        {
            var b = VictoryBadge(n, h); cv.AddChild(b);
            // under the stats plaque when the card has one (at the bottom it covered the name)
            b.Position = new Vector2(cv.Size.X - b.Size.X - 3, cv.EnemyStats is null ? 3 : 30);
        };
    }

    Control EnemyExtra(Entity e)
    {
        var m = model!;
        int hp = m.EnemyHealth(e), dmg = m.Damage(e);
        var col = new VBoxContainer { MouseFilter = MouseFilterEnum.Ignore }; col.AddThemeConstantOverride("separation", 8);
        // the three numbers along the top of an enemy card: fight · health · evade
        col.AddChild(IconRow(new (Control, string, Color)[]
        {
            (GlyphIcon("combat", Style.Combat), Fmt(m.EnemyFight(e)), Style.HoverText),
            (TokIcon("health"), hp < 0 ? "-" : (hp - dmg) + "/" + hp, dmg > 0 ? Style.Danger : Style.HoverText),
            (GlyphIcon("agility", Style.Agility), Fmt(m.EnemyEvade(e)), Style.HoverText),
        }));
        var attack = new HBoxContainer { MouseFilter = MouseFilterEnum.Ignore }; attack.AddThemeConstantOverride("separation", 10);
        attack.AddChild(Style.Text("攻击", 18, Style.PaperDim, Style.Kai));
        attack.AddChild(IconRow(new (Control, string, Color)[] { (TokIcon("health"), J.Int(e.Raw, "healthDamage").ToString(), Style.Paper), (TokIcon("horror"), J.Int(e.Raw, "sanityDamage").ToString(), Style.Paper) }));
        if (J.Get(e.Raw, "exhausted").ValueKind == JsonValueKind.True) attack.AddChild(Style.Text("（横置）", 18, Style.PaperDim));
        col.AddChild(attack);
        if (Victory(e.Code) is int ev and > 0) col.AddChild(Style.Text("胜利" + ev, 19, new Color("f0c860"), Style.Kai));
        return col;
    }

    // an asset's health and sanity limits: what the core reports for it, else what is printed on its card
    (int Hp, int San) UnitMax(Entity a)
    {
        int hp = J.Int(a.Raw, "health"), san = J.Int(a.Raw, "sanity");
        var card = S.Card(CoreCatalog.Normalize(a.Code));
        if (hp <= 0) hp = J.Int(card, "health");
        if (san <= 0) san = J.Int(card, "sanity");
        // plus what the core adds to it (e.g. Hunter's Armor 耐用 +2 health) — CoreModifiers
        var (dh, ds) = CoreModifiers.Unit(model!.Game, a.Id);
        return (hp + dh, san + ds);
    }

    // health / sanity left on an ally as tokens with its limit beside them ("2/3"); red once hurt (Yunke 11-19 r10)
    Control? HealthChips(Entity a, float h, bool vertical = false)
    {
        var m = model!; var (hp, san) = UnitMax(a);
        BoxContainer row = vertical ? new VBoxContainer { MouseFilter = MouseFilterEnum.Ignore } : new HBoxContainer { MouseFilter = MouseFilterEnum.Ignore }; row.AddThemeConstantOverride("separation", vertical ? 0 : 4);
        foreach (var (kind, max, hurt, name) in new[] { ("health", hp, m.Damage(a), "生命"), ("horror", san, m.Horror(a), "理智") })
        {
            if (max <= 0 && hurt <= 0) continue;
            var chip = new HBoxContainer { MouseFilter = MouseFilterEnum.Pass, TooltipText = max > 0 ? name + " " + Math.Max(0, max - hurt) + " / " + max + (hurt > 0 ? "（" + (kind == "health" ? "伤害 " : "恐惧 ") + hurt + "）" : "") : (kind == "health" ? "伤害 " : "恐惧 ") + hurt };
            chip.AddThemeConstantOverride("separation", 0);
            var tok = Ui.TokenNumber(kind, max > 0 ? Math.Max(0, max - hurt) : hurt, h); tok.MouseFilter = MouseFilterEnum.Ignore;
            if (hurt > 0) foreach (var l in tok.GetChildren().OfType<Label>()) l.AddThemeColorOverride("font_color", new Color("ff9a88"));
            chip.AddChild(tok);
            if (max > 0)
            {
                var lim = Style.Text("/" + max, (int)Math.Max(15, h * 0.55f), Style.Paper, Style.Symbols); lim.VerticalAlignment = VerticalAlignment.Bottom; lim.SizeFlagsVertical = SizeFlags.ShrinkEnd;
                lim.AddThemeConstantOverride("outline_size", 5); lim.AddThemeColorOverride("font_outline_color", new Color(0, 0, 0, 0.9f)); chip.AddChild(lim);
            }
            row.AddChild(chip);
        }
        return row.GetChildCount() == 0 ? null : row;
    }

    // a story ally or an asset: its damage and horror (and uses) as icons, when it has any
    Control? UnitExtra(Entity a)
    {
        var m = model!;
        var cells = new List<(Control, string, Color)>();
        var (hp, san) = UnitMax(a);
        if (hp > 0 || m.Damage(a) > 0) cells.Add((TokIcon("health"), hp > 0 ? (hp - m.Damage(a)) + "/" + hp : m.Damage(a).ToString(), m.Damage(a) > 0 ? Style.Danger : Style.HoverText));
        if (san > 0 || m.Horror(a) > 0) cells.Add((TokIcon("horror"), san > 0 ? (san - m.Horror(a)) + "/" + san : m.Horror(a).ToString(), m.Horror(a) > 0 ? Style.Danger : Style.HoverText));
        if (TableModel.Uses(a) is int u and > 0) cells.Add((TokIcon("resource"), TableModel.UsesName(a) + " " + u, Style.HoverText));
        var row = cells.Count == 0 ? null : IconRow(cells);
        // a customizable card: the upgrades ticked on it (CustomTicks; Yunke 11-19)
        if (CustomTicks.FromCore(J.Get(a.Raw, "customizations"), a.Code) is not { } ticks) return row;
        var col = new VBoxContainer { MouseFilter = MouseFilterEnum.Ignore }; col.AddThemeConstantOverride("separation", 8);
        if (row is not null) col.AddChild(row);
        col.AddChild(ticks); return col;
    }

    // a hand card's hover extra: a customizable card's ticked upgrades
    Control? HandExtra(Entity inv, string id, string code)
    {
        var raw = J.Items(J.Get(inv.Raw, "hand")).Select(c => J.Get(c, "contents")).FirstOrDefault(c => J.String(c, "id") == id);
        return raw.ValueKind == JsonValueKind.Object ? CustomTicks.FromCore(J.Get(raw, "customizations"), code) : null;
    }

    // a place: clues left, shroud, doom — what is on it now (Yunke 11-19 r9 #13)
    Control LocationExtra(Entity loc)
    {
        var m = model!;
        var cells = new List<(Control, string, Color)>();
        if (m.Revealed(loc))
        {
            cells.Add((TokIcon("clue"), "线索 " + m.Clues(loc), m.Clues(loc) > 0 ? new Color("e9d9a6") : Style.PaperDim));
            var ring = new Panel { MouseFilter = MouseFilterEnum.Ignore, CustomMinimumSize = new Vector2(24, 24) };
            var rsb = new StyleBoxFlat { BgColor = new Color(0.14f, 0.2f, 0.36f), BorderColor = new Color("e0b56a"), AntiAliasing = true }; rsb.SetBorderWidthAll(2); rsb.SetCornerRadiusAll(12); ring.AddThemeStyleboxOverride("panel", rsb);
            cells.Add((ring, "隐藏值 " + m.Shroud(loc), Style.HoverText));
        }
        else cells.Add((new Control { CustomMinimumSize = new Vector2(0, 24) }, "未揭示", Style.PaperDim));
        if (m.Doom(loc) > 0) cells.Add((TokIcon("doom"), "毁灭 " + m.Doom(loc), Style.Danger));
        if (m.Revealed(loc) && Victory(loc.Code) is int lv and > 0) cells.Add((new Control { CustomMinimumSize = new Vector2(0, 24) }, "胜利" + lv, new Color("f0c860")));
        var row = IconRow(cells);
        foreach (var l in row.GetChildren().OfType<HBoxContainer>().SelectMany(h => h.GetChildren().OfType<Label>())) { if (Style.Kai is not null) l.AddThemeFontOverride("font", Style.Kai); l.AddThemeFontSizeOverride("font_size", 20); }
        return row;
    }

    // The enemies engaged with you sit in your threat area, top left (RightZone), as the rules place them (Yunke 11-19: not in a
    // column of their own on the right); each investigator's engaged enemies also show beside their portrait in the team rows.
    void EngagedEnemies() { }

    // the treachery ids a question refers to ({"tag":"TreacherySource"|"TreacheryTarget","contents":id}, at any depth)
    static void TreacheryRefs(JsonElement e, HashSet<string> acc, int depth)
    {
        if (depth > 14) return;
        if (e.ValueKind == JsonValueKind.Array) { foreach (var x in e.EnumerateArray()) TreacheryRefs(x, acc, depth + 1); return; }
        if (e.ValueKind != JsonValueKind.Object) return;
        if (J.String(e, "tag") is "TreacherySource" or "TreacheryTarget" && J.Get(e, "contents").ValueKind == JsonValueKind.String) acc.Add(J.String(e, "contents"));
        foreach (var p in e.EnumerateObject()) TreacheryRefs(p.Value, acc, depth + 1);
    }

    // ───────────────────────── right: one open area (Yunke: no exclusive/public grids) — threats when there are any, end turn below
    void RightZone()
    {
        var m = model!; var me = m.Me; if (me is null) return;
        var threats = m.ThreatsOf(me).ToArray();
        var engaged = m.EngagedWith(me).ToArray();
        // a treachery whose test is being made (its revelation is resolving, so it lies in no zone): it stays in sight in the
        // threat area until the test is over, so the player can read it while choosing what to commit (Yunke 11-20 #14)
        // Any effect still resolving keeps its card in sight the same way (Yunke 11-20: "花费1个线索 / 受到2点伤害" with no card
        // to say why — as 攫取之手 before): a treachery the current question or skill test names as its source or target.
        Entity? resolving = null;
        var named = new HashSet<string>();
        if (m.InSkillTest && J.Tag(J.Get(m.SkillTest, "source")) == "TreacherySource") named.Add(J.String(J.Get(m.SkillTest, "source"), "contents"));
        TreacheryRefs(m.State.Question, named, 0);
        // a treachery whose revelation is under way lies in no zone ("Limbo") until it is done: that is the one being resolved,
        // whether or not the question names it (the agenda advance 远古恶魔 causes, a "花费1个线索 / 受到2点伤害" choice…)
        foreach (var t in m.Treacheries) if (J.Tag(J.Get(t.Raw, "placement")) == "Limbo") named.Add(t.Id);
        foreach (var sid in named)
        {
            var tr = m.Treacheries.FirstOrDefault(t => t.Id == sid);
            if (tr is not null && !threats.Any(t => t.Id == sid) && !IsWeakness(S, tr.Code) && !J.Tag(J.Get(tr.Raw, "placement")).StartsWith("Attached")
                && J.Tag(J.Get(tr.Raw, "placement")) is not ("AtLocation" or "InThreatArea")) { resolving = tr; break; }
        }
        // an encounter card drawn and not yet revealed (the core holds it among the focused cards while a "when drawn" window
        // is open — 守护结界, 否决存在 …): shown here too, so the player sees what the cancel is for (Yunke 11-22: 提供力量)
        string? drawing = resolving is null && m.Me is not null && m.State.Question.ValueKind == System.Text.Json.JsonValueKind.Object && LookCards(m).Count == 0 ? FocusedEncounter(m) : null;
        if (threats.Length > 0 || engaged.Length > 0 || resolving is not null || drawing is not null)
        {
            // just above the player's asset boxes, close to the investigator (Yunke 11-20; it was in the top-left corner): the
            // column is bottom-aligned, so more threats, or more rows of assets below, push it up — never above the map buttons
            var area = new VBoxContainer { MouseFilter = MouseFilterEnum.Ignore, Alignment = BoxContainer.AlignmentMode.End }; area.AddThemeConstantOverride("separation", 10);
            float bottom = float.IsNaN(assetTopY) ? 350 : Math.Max(350, assetTopY - 14);
            Place(area, SideMargin + 10, 150, SideMargin + 10 + 620, bottom, 0, 0, 0, 0);
            var lab = Style.Text("威胁区", 20, Style.HoverText, Style.Kai); lab.AddThemeConstantOverride("outline_size", 6); lab.AddThemeColorOverride("font_outline_color", Colors.Black); area.AddChild(lab);
            var row = new HFlowContainer { MouseFilter = MouseFilterEnum.Ignore }; row.AddThemeConstantOverride("h_separation", 12); row.AddThemeConstantOverride("v_separation", 12); area.AddChild(row);
            // the enemies engaged with you first (their fight / health / evade along the top), then the cards in your threat area
            // every card keeps its own size: the row is as tall as its tallest child (the drawn card with its caption line), and a
            // child left to fill that height grew with it (Yunke 11-22: 厄运连连 came out taller than the drawn 紧追不舍 beside it)
            foreach (var e in engaged) { var ec = EnemyCard(e, true, new Vector2(112, 156)); ec.SizeFlagsVertical = SizeFlags.ShrinkBegin; row.AddChild(ec); }
            foreach (var t in threats)
            {
                string tkey = m.ThreatKey(t); var code = t.Code;
                var cv = new CardView { Code = code, Key = tkey, CustomMinimumSize = new Vector2(112, 156), Size = new Vector2(112, 156), ShowCost = false, Rim = new Color("8a5aa8"), Actionable = byAnchor.ContainsKey(tkey),
                    Badges = new[] { ("horror", m.Horror(t)), ("health", m.Damage(t)), ("doom", m.Doom(t)), ("clue", m.Clues(t)) } };   // what lies on it (the Necronomicon's 3 horror)
                cv.SizeFlagsVertical = SizeFlags.ShrinkBegin;
                cv.OnClick = () => ActOn(tkey, cv, code); Anchor(tkey, cv); row.AddChild(cv);
            }
            if (resolving is not null || drawing is not null)
            {
                var rc = resolving?.Code ?? drawing!;
                var col = new VBoxContainer { MouseFilter = MouseFilterEnum.Ignore, SizeFlagsVertical = SizeFlags.ShrinkBegin }; col.AddThemeConstantOverride("separation", 4);   // the caption on a line of its own below
                var cv = new CardView { Code = rc, CustomMinimumSize = new Vector2(112, 156), Size = new Vector2(112, 156), ShowCost = false, Rim = new Color("8a5aa8"), Glow = new Color("c9a0e8"), SizeFlagsVertical = SizeFlags.ShrinkBegin };
                cv.OnClick = () => CardReader.Open(rc, printed: true); col.AddChild(cv);
                if (resolving is null) { drawnCard = cv; drawnCode = rc; }
                // a card just drawn flies from the encounter deck into the threat area, face down, and turns over (Yunke 11-22)
                if (resolving is null) { string key = FocusedEncounterKey(m); if (key != drawnFlown) { drawnFlown = key; FlyDrawnIn(cv, rc); } }
                var tag = Style.Text(resolving is null ? "抽到的遭遇卡" : "结算中", 16, new Color("c9a0e8"), Style.Kai); tag.HorizontalAlignment = HorizontalAlignment.Center; tag.AddThemeConstantOverride("outline_size", 6); tag.AddThemeColorOverride("font_outline_color", Colors.Black); col.AddChild(tag);
                row.AddChild(col);
            }
        }
        EncounterPiles(m);
        // chaos tokens revealed outside a test, shown before anything is decided about them (TableScreen.Tokens)
        FocusedTokenBanner(m, m.Question.Choices.Any(c => c.Enabled && c.Tag == "TargetLabel" && J.Tag(J.Get(c.Raw, "target")) == "ChaosTokenTarget"));
        // end turn, bottom right
        var end = m.Question.Choices.FirstOrDefault(c => c.Enabled && c.Tag == "EndTurnButton");
        if (end is not null)
        {
            var b = Ui.MinimalButton("结束回合", () => Choose(end), 24, true); b.CustomMinimumSize = new Vector2(170, 54);
            Place(b, -SideMargin - 170 - 60, -SideMargin - 58, -SideMargin - 60, -SideMargin, 1, 1, 1, 1);   // left of the team's clue token in the corner
            // nothing else can be done: the button shakes now and then and breathes, so the turn is not left hanging (Yunke 11-20 #10)
            if (m.Question.Choices.All(c => !c.Enabled || c == end) && S.Settings.ReducedMotion != true)
            {
                b.PivotOffset = new Vector2(85, 27); b.TooltipText = "没有其他可做的事了";
                // a soft warm glow around it, breathing (brightening the dark button alone hardly showed — QA 11-20)
                var halo = new Panel { MouseFilter = MouseFilterEnum.Ignore, ShowBehindParent = true };
                var hs = new StyleBoxFlat { BgColor = new Color(Style.Brass, 0.22f), ShadowColor = new Color(1f, 0.76f, 0.38f, 0.6f), ShadowSize = 22 };
                hs.SetCornerRadiusAll(12); halo.AddThemeStyleboxOverride("panel", hs);
                b.AddChild(halo); halo.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect); halo.Modulate = new Color(1, 1, 1, 0.25f);
                var glow = halo.CreateTween().SetLoops();
                glow.TweenProperty(halo, "modulate:a", 1f, 0.9f).SetTrans(Tween.TransitionType.Sine);
                glow.TweenProperty(halo, "modulate:a", 0.25f, 0.9f).SetTrans(Tween.TransitionType.Sine);
                if (App.I.QaRunning) GD.Print("QA_ENDTURN_NUDGE");
                var shake = b.CreateTween().SetLoops();
                shake.TweenInterval(1.2);
                foreach (var deg in new[] { -4f, 4f, -3f, 3f, -1.5f, 0f }) shake.TweenProperty(b, "rotation_degrees", deg, 0.06f);
                shake.TweenInterval(0.8);
            }
        }
    }

    // ───────────────────────── hand: fanned small cards, deck and discard piles at its sides
    readonly List<(CardView Card, float X)> fan = new();
    int qaHandPad;   // QA only; 0 in normal play

    // ───────────────────────── opening hand (mulligan): mark cards, then replace them in one go
    readonly HashSet<string> mulliganPick = new();
    static bool InMulligan(TableModel m) => m.Question.Choices.Any(c => c.Enabled && c.Title.Contains("结束选牌"));

    async void ConfirmMulligan()
    {
        if (busy || state is null) return;
        var ids = mulliganPick.ToList(); mulliganPick.Clear();
        mulliganRedraw = ids.Count > 0;   // the replacements are dealt from the deck after the swap (TableScreen.Fly)
        await FlyToDiscard(ids);   // the marked cards fly from the hand onto the discard pile (Yunke 11-18) before the swap
        await Busy(ids.Count > 0 ? "换牌…" : "确认起手牌…", async () =>
        {
            // one answer per marked card (the core takes them one at a time), then the end of the mulligan
            foreach (var id in ids)
            {
                var m = new TableModel(state!, S);
                var c = m.Question.Choices.FirstOrDefault(x => x.Enabled && x.Title.StartsWith("换掉") && m.Anchor(x) == "hand:" + id);
                if (c is not null) state = await B.Choice(state!, c.Index);
            }
            var after = new TableModel(state!, S);
            var end = after.Question.Choices.FirstOrDefault(x => x.Enabled && x.Title.Contains("结束选牌"));
            if (end is not null) state = await B.Choice(state!, end.Index);
        });
        // the new cards land in this hand first, and only then does the next investigator choose (Yunke 11-21: the view moved on
        // to the next player while this one's replacement cards were still flying in)
        if (ids.Count > 0 && state is not null && IsInstanceValid(this))
        {
            model = new TableModel(state, S);
            Render();
            if (App.I.QaRunning) GD.Print("QA_MULLIGAN_HOLD " + ids.Count);
            // busy while the new cards land: the page the render opened (the setup sheet) is answered by Advance below, and a
            // click on it in between sent a second answer for the same step (Yunke 11-22: "Local state changed …")
            busy = true;
            try { await ToSignal(GetTree().CreateTimer(0.12 * ids.Count + 1.0), SceneTreeTimer.SignalName.Timeout); }
            finally { busy = false; }
        }
        await Advance();
    }

    // Marked hand cards fly to the discard pile, one after another, tilting and shrinking onto it; the pile flashes.
    async Task FlyToDiscard(List<string> ids)
    {
        if (App.I.QaRunning) GD.Print($"QA_DISCARD_FLY {ids.Count} pile={(discardPileNode is not null && IsInstanceValid(discardPileNode))} fan={fan.Count}");
        if (ids.Count == 0 || discardPileNode is null || !IsInstanceValid(discardPileNode) || S.Settings.ReducedMotion == true) return;
        var to = discardPileNode.GetGlobalRect();
        float last = 0;
        int k = 0;
        foreach (var id in ids)
        {
            var src = fan.FirstOrDefault(f => f.Card.Key == "hand:" + id).Card;
            if (src is null || !IsInstanceValid(src)) continue;
            var from = src.GetGlobalRect();
            var ghost = new CardView { Code = src.Code, CustomMinimumSize = from.Size, Size = from.Size, ShowCost = false, HoverZoom = false, MouseFilter = MouseFilterEnum.Ignore, ZIndex = 140 };
            AddChild(ghost); ghost.GlobalPosition = from.Position; ghost.PivotOffset = from.Size / 2;
            src.Modulate = new Color(1, 1, 1, 0);   // the card leaves the hand
            float delay = 0.09f * k++, dur = 0.5f; last = delay + dur;
            var target = to.Position + (to.Size - from.Size) / 2;   // centred on the pile
            float sc = Math.Min(to.Size.X / from.Size.X, to.Size.Y / from.Size.Y);
            var tw = ghost.CreateTween().SetParallel();
            tw.TweenProperty(ghost, "global_position", target, dur).SetDelay(delay).SetTrans(Tween.TransitionType.Cubic).SetEase(Tween.EaseType.InOut);
            tw.TweenProperty(ghost, "scale", new Vector2(sc, sc), dur).SetDelay(delay).SetTrans(Tween.TransitionType.Cubic).SetEase(Tween.EaseType.InOut);
            tw.TweenProperty(ghost, "rotation", (float)(new Random().NextDouble() * 0.5 - 0.25), dur).SetDelay(delay);
            tw.Chain().TweenCallback(Callable.From(() => { if (IsInstanceValid(discardPileNode)) Punch(discardPileNode!, Style.Amber); }));
            tw.Chain().TweenProperty(ghost, "modulate:a", 0f, 0.25f);
            tw.Chain().TweenCallback(Callable.From(ghost.QueueFree));
        }
        if (App.I.QaRunning) QaMidShot(0.3);
        await ToSignal(GetTree().CreateTimer(last + 0.1f), SceneTreeTimer.SignalName.Timeout);
    }

    // a card played as a fast card (its own line "快速。"): an asset or an event, as the html hand toggle's FAST_PLAY_LINE
    bool IsFastPlay(string code)
    {
        var c = S.Card(code);
        if (J.String(c, "type_code") is not ("asset" or "event")) return false;
        return System.Text.RegularExpressions.Regex.IsMatch(J.String(c, "text"), @"(^|\n)\s*(<b>)?快速(</b>)?。");
    }

    void HandFan()
    {
        var m = model!; var me = m.Me; if (me is null) return;
        var committedIds = m.InSkillTest ? J.Props(J.Get(m.SkillTest, "committedCards")).SelectMany(p => J.Items(p.Value)).Select(c => J.String(J.Get(c, "contents"), "id")).ToHashSet() : new HashSet<string>();
        var hand = m.Hand(me).Where(h => !committedIds.Contains(h.Item1)).ToArray();
        // Hidden treacheries secretly in this hand (Carcosa 11-20): at the end of the fan, keyed by their own abilities ("tr:<id>")
        var hiddenIds = m.HiddenInHand(me).Select(t => t.Id).ToHashSet();
        hand = hand.Concat(m.HiddenInHand(me).Select(t => (t.Id, t.Code))).ToArray();
        if (qaHandPad > 0 && hand.Length > 0)   // QA only: repeat the hand's cards to check a crowded layout
            hand = Enumerable.Range(0, qaHandPad).Select(k => (hand[k % hand.Length].Item1 + "#" + k, hand[k % hand.Length].Item2)).ToArray();
        bool raised = m.InSkillTest || m.Question.Title.Contains("起手牌") || m.Question.Choices.Any(c => c.Title.StartsWith("换掉"));
        bool mull = InMulligan(m);
        if (!mull) mulliganPick.Clear();
        if (mull && InTutorial) TutorialMulligan(m);   // the cards to swap are known before the hand is drawn
        var zone = new Control { MouseFilter = MouseFilterEnum.Ignore };
        Place(zone, SideMargin + SideW + 30, -250, -SideMargin - SideW - 30, 0, 0, 1, 1, 1);
        handZone = zone; fan.Clear();
        // the dark fade under the hand: from clear at the top of the zone to dark at its bottom edge, behind the cards
        var handShade = new TextureRect { Texture = VGrad(0.9f, true), ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize, StretchMode = TextureRect.StretchModeEnum.Scale, MouseFilter = MouseFilterEnum.Ignore };
        zone.AddChild(handShade); handShade.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        // its sides fade out too, so the shade has no visible left and right edge
        float zoneW = Math.Max(300, GetViewportRect().Size.X - 2 * (SideMargin + SideW + 30));
        handShade.Material = Ui.EdgeAlpha(Vector2.Zero, new Vector2(zoneW, 250), 180, 0, 180, 0);
        const float w = 136, h = 190;
        float avail = Math.Max(300, GetViewportRect().Size.X - 2 * (SideMargin + SideW + 30));
        float step = hand.Length <= 1 ? w : Math.Min(130, (avail - w - 200) / (hand.Length - 1));
        float mid = (hand.Length - 1) / 2f, cx = avail / 2;
        bool crowded = hand.Length > 8;
        float restY = raised ? 14 : 26;   // outer cards sag along the arc; keep their names above the screen edge
        for (int i = 0; i < hand.Length; i++)
        {
            var (id, code) = hand[i];
            if (hiddenIds.Contains(id))
            {
                string tkey = "tr:" + id;
                var hcv = Card(code, tkey, new Vector2(w, h)); hcv.ShowCost = false; hcv.Rim = new Color("8a5aa8");
                var hcode = code; hcv.OnClick = () => ActOn(tkey, hcv, hcode); Anchor(tkey, hcv);
                float hoff = i - mid, hx = cx + hoff * step - w / 2;
                var (hsag, hrot) = FanArc(hoff, mid);   // the same arc as the other hand cards
                hcv.Position = new Vector2(hx, restY + hsag); hcv.RotationDegrees = hrot;
                int hidx = i; hcv.HoverChanged = on => SpreadAround(hidx, on);
                zone.AddChild(hcv); fan.Add((hcv, hx));
                continue;
            }
            var cv = Card(code, "hand:" + id, new Vector2(w, h));
            if (CustomTicks.IsCustomizable(code)) { var hid = id; var hcode = code; cv.PeekExtra = () => HandExtra(me, hid, hcode); }
            cv.CostShown = CoreModifiers.Cost(m.Game, id, CoreCosts.Cost(code));   // the cost the core charges (e.g. 轻量 -1)
            cv.Draggable = cv.Actionable || (PreCommitWindow(m) && CommitLater(m, code)); cv.Lift = raised ? 14 : 40; cv.ShowSkills = true;   // always shown (Yunke 11-19 r9 #4), not only while committing
            if (!cv.Actionable) { var denied = cv; cv.OnClick = () => Deny(denied, code); }   // a click on a card that cannot be played says why
            // a fast card in hand (守护结界, 闪躲…: played in a window) has its play switch in its lower-left corner — 总是询问 / 只在自己
            //回合 / 停止询问, the core's "play" trigger mode (ability index -1, as the html hand card's toggle) (Yunke 11-21)
            if (!mull && IsFastPlay(code))
            {
                var sw = ModeSwitch(m, me.Id, "c" + code, -1, false); cv.AddChild(sw); sw.ZIndex = 6;
                sw.Size = new Vector2(24, 24); sw.Position = new Vector2(5, h - 30);
            }
            var key = "hand:" + id; cv.OnDragRelease = pos => DropFromHand(key, pos);
            // Opening hand: a click marks or unmarks the card; one confirm replaces all marked cards together.
            bool picked = false;
            if (mull && cv.Actionable)
            {
                string hid = id; cv.Draggable = false;
                cv.OnClick = () => { if (!TutorialMullAllowed(hid)) { App.I.Toast("新手教程中请换掉发光的牌"); return; } if (!mulliganPick.Add(hid)) mulliganPick.Remove(hid); Render(); };
                // 新手教程: only the cards to swap glow (Yunke 11-23: the fixed swap)
                if (InTutorial && tutMullSwap.Count > 0)
                {
                    if (tutMullSwap.Contains(hid)) { cv.Glow = Style.Amber; tutMullNodes[hid] = cv; }
                    else { cv.Actionable = false; cv.Modulate = new Color(0.72f, 0.72f, 0.72f); }
                }
                if (mulliganPick.Contains(id))
                {
                    picked = true; cv.Actionable = false; cv.Rim = new Color("d0573f");
                    var tag = Style.Text("换掉", 18, Colors.White, Style.Kai); tag.HorizontalAlignment = HorizontalAlignment.Center; tag.ZIndex = 5; tag.MouseFilter = MouseFilterEnum.Ignore;
                    var pill = new PanelContainer { MouseFilter = MouseFilterEnum.Ignore, ZIndex = 5 };
                    var psb = new StyleBoxFlat { BgColor = new Color("a8402e"), ContentMarginLeft = 12, ContentMarginRight = 12, ContentMarginTop = 2, ContentMarginBottom = 3 }; psb.SetCornerRadiusAll(10);
                    pill.AddThemeStyleboxOverride("panel", psb); pill.AddChild(tag); cv.AddChild(pill); pill.Position = new Vector2(w / 2 - 34, -14);
                }
            }
            float off = i - mid, x = cx + off * step - w / 2;
            // the hand stays a fan in every state (skill test included); only its height changes when raised.
            // A crowded hand keeps the arc of an eight-card hand — the same heights and tilt from end to end — and only overlaps
            // more (Yunke 11-22: with many cards the heights went uneven)
            var (sag, rot) = FanArc(off, mid);
            cv.Position = new Vector2(x, restY + sag - (picked ? 34 : 0)); cv.RotationDegrees = rot;
            int idx = i; cv.HoverChanged = on => SpreadAround(idx, on); cv.DragChanged = DragFeedback;
            zone.AddChild(cv); fan.Add((cv, x));
        }
        // piles: deck to the left of the fan, discard to the right
        float half = hand.Length == 0 ? 60 : mid * step + w / 2;
        // Both piles use the 0712 card back and show their top card; the deck is clicked to draw, the discard to open its cards.
        const float pileW = 64, pileH = 92;
        var deck = new Control { MouseFilter = MouseFilterEnum.Stop, TooltipText = "牌库 " + m.DeckSize(me) + " 张", CustomMinimumSize = new Vector2(pileW, 120) };
        for (int k = Math.Min(3, Math.Max(1, m.DeckSize(me) / 8)); k >= 0; k--)
        { var back = RoundedBack(new Vector2(pileW, pileH)); back.Position = new Vector2(k * 3, 14 - k * 3); deck.AddChild(back); }
        if (m.DeckSize(me) > 0) deck.AddChild(PileEdge(new Rect2(0, 14, pileW, pileH)));
        deck.AddChild(PileLabel("牌库 " + m.DeckSize(me), 112));
        zone.AddChild(Clickable(deck, "deck:" + me.Id, frame: new Rect2(0, 14, pileW, pileH))); deck.Position = new Vector2(Math.Max(0, cx - half - 110), 100);
        deckPileNode = deck; deckTopFrame = new Rect2(0, 14, pileW, pileH);
        // 乔·戴蒙德's hunch deck: its own pile left of the deck, "直觉牌库 N" (html Player.vue .hunch-deck): card backs, and once
        // its top card is revealed (the investigation phase), that card face up on it — playable from there as if in hand,
        // lit when the core offers it, a click plays it (local-joe-hunch: the pile was missing; the card only popped up alone
        // in the middle of the table)
        if (m.HunchDeck(me) is { } hunch)
        {
            var hp = new Control { MouseFilter = MouseFilterEnum.Pass, TooltipText = "直觉牌库 " + hunch.Count + " 张", CustomMinimumSize = new Vector2(pileW, 120) };
            string revealed = m.RevealedHunchCard(me);
            bool faceUp = hunch.Count > 0 && revealed.Length > 0 && hunch[0].Id == revealed;
            for (int k = Math.Min(2, Math.Max(0, hunch.Count / 4)); k >= (faceUp ? 1 : 0); k--)
            { var back = RoundedBack(new Vector2(pileW, pileH)); back.Position = new Vector2(k * 3, 14 - k * 3); hp.AddChild(back); }
            if (faceUp)
            {
                string hk = "hand:" + hunch[0].Id, hcode = hunch[0].Code;
                var top = new CardView { Code = hcode, Key = hk, CustomMinimumSize = new Vector2(pileW, pileH), Size = new Vector2(pileW, pileH), ShowName = false, HoverZoom = true,
                    Actionable = byAnchor.ContainsKey(hk), TooltipText = "直觉牌库顶的牌（已揭示）：" + S.Name(hcode) + "，本阶段可以如同在手牌中一样打出，费用-2" };
                top.OnClick = () => ActOn(hk, top, hcode);
                hp.AddChild(top); top.Position = new Vector2(0, 14);
                Anchor(hk, top);
            }
            else if (hunch.Count > 0) hp.AddChild(PileEdge(new Rect2(0, 14, pileW, pileH)));
            hp.AddChild(PileLabel("直觉牌库 " + hunch.Count, 112));
            zone.AddChild(hp); hp.Position = deck.Position - new Vector2(pileW + 26, 0);
            if (App.I.QaRunning) GD.Print("QA_HUNCH count=" + hunch.Count + " top=" + (faceUp ? hunch[0].Code : "hidden") + " lit=" + (faceUp && byAnchor.ContainsKey("hand:" + hunch[0].Id)));
        }
        var discard = m.Discard(me).ToArray();
        var dz = new Control { MouseFilter = MouseFilterEnum.Stop, MouseDefaultCursorShape = CursorShape.PointingHand, TooltipText = "弃牌堆 " + discard.Length + " 张，点击查看", CustomMinimumSize = new Vector2(pileW, 120) };
        if (discard.Length > 0)
        {
            var top = new CardView { Code = discard[0], CustomMinimumSize = new Vector2(pileW, pileH), Size = new Vector2(pileW, pileH), ShowName = false, ShowCost = false, RotationDegrees = -4, MouseFilter = MouseFilterEnum.Ignore };
            // the top card takes the mouse itself (CardView), so it opens the pile too (Yunke 11-22: clicking the pile did nothing)
            top.OnClick = () => { if (App.I.QaRunning) GD.Print("QA_DISCARD_CLICK"); OpenDiscard(); };
            dz.AddChild(top); top.Position = new Vector2(0, 14);
        }
        else
        {
            var empty = new Panel { Size = new Vector2(pileW, pileH), Position = new Vector2(0, 14), MouseFilter = MouseFilterEnum.Ignore };
            var sb = new StyleBoxFlat { BgColor = new Color(0, 0, 0, 0.25f), BorderColor = new Color(0.75f, 0.68f, 0.52f, 0.45f), AntiAliasing = true }; sb.SetBorderWidthAll(2); sb.SetCornerRadiusAll(5); empty.AddThemeStyleboxOverride("panel", sb); dz.AddChild(empty);
        }
        dz.AddChild(PileLabel("弃牌 " + discard.Length, 112));
        dz.GuiInput += e => { if (e is InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Left }) { if (App.I.QaRunning) GD.Print("QA_DISCARD_CLICK"); OpenDiscard(); } };
        // a discard card that can act lights the pile
        if (m.DiscardCards(me).Any(d => byAnchor.ContainsKey("hand:" + d.Id) || byAnchor.ContainsKey("asset:" + d.Id)))
        {
            var lit = new Panel { MouseFilter = MouseFilterEnum.Ignore, ZIndex = 40, Position = new Vector2(-2, 12), Size = new Vector2(pileW + 4, pileH + 4) };
            var sb = new StyleBoxFlat { BgColor = new Color(0, 0, 0, 0), DrawCenter = false, BorderColor = Style.Amber, ShadowColor = new Color(Style.Amber, 0.45f), ShadowSize = 10 };
            sb.SetBorderWidthAll(3); sb.SetCornerRadiusAll(4); lit.AddThemeStyleboxOverride("panel", sb); dz.AddChild(lit);
        }
        discardPileNode = dz;
        zone.AddChild(dz); dz.Position = new Vector2(Math.Min(avail - pileW, cx + half + 44), 100);
        // the hand and its limit, always beside the hand (the limit as the core counts it now: an agenda or a card can change it)
        int handLimit = J.Int(me.Raw, "handSize", 8), handHave = m.Hand(me).Count();
        var hl = Style.Text("手牌 " + handHave + " / " + handLimit, 15, handHave > handLimit ? new Color("ff8a70") : Style.PaperDim);
        hl.AddThemeConstantOverride("outline_size", 6); hl.AddThemeColorOverride("font_outline_color", Colors.Black); hl.MouseFilter = MouseFilterEnum.Pass;
        hl.TooltipText = "当前手牌 / 手牌上限（整备阶段结束时超过上限要弃牌）";
        zone.AddChild(hl); hl.Position = deck.Position + new Vector2(-14, -14);
    }

    // A card back clipped to the pile edge's rounded corners (Yunke 11-22: the stacked backs showed square corners)
    static Control RoundedBack(Vector2 size, int radius = 5, bool encounter = false)
    {
        var clip = new Panel { MouseFilter = MouseFilterEnum.Ignore, Size = size, CustomMinimumSize = size, ClipChildren = ClipChildrenMode.Only };
        var mask = new StyleBoxFlat { BgColor = Colors.White, AntiAliasing = true }; mask.SetCornerRadiusAll(radius); clip.AddThemeStyleboxOverride("panel", mask);
        var img = Ui.Image(Ui.CardBack(encounter), size); img.MouseFilter = MouseFilterEnum.Ignore; clip.AddChild(img);
        return clip;
    }

    // The edge a card has on the table (CardView's resting frame), laid over a pile's top card.
    static Control PileEdge(Rect2 r)
    {
        var p = new Panel { MouseFilter = MouseFilterEnum.Ignore, Position = r.Position, Size = r.Size };
        var sb = new StyleBoxFlat { DrawCenter = false, BgColor = new Color(0, 0, 0, 0), BorderColor = new Color(0.75f, 0.68f, 0.52f, 0.7f), AntiAliasing = true, ShadowColor = new Color(0, 0, 0, 0.5f), ShadowSize = 6 };
        sb.SetBorderWidthAll(2); sb.SetCornerRadiusAll(5); p.AddThemeStyleboxOverride("panel", sb);
        return p;
    }

    static Control PileLabel(string text, float y)
    {
        var l = Style.Text(text, 15, Style.PaperDim); l.HorizontalAlignment = HorizontalAlignment.Center; l.Position = new Vector2(-17, y); l.Size = new Vector2(100, 22);
        l.AddThemeConstantOverride("outline_size", 6); l.AddThemeColorOverride("font_outline_color", Colors.Black); l.MouseFilter = MouseFilterEnum.Ignore;
        return l;
    }

    // One smooth fan for any hand (Yunke 11-22: with many cards the outer ones hit the old caps and stuck out of the arc): a parabola
    // whose deepest drop and widest tilt grow with the hand (as before for small hands) up to a gentler cap, every card on it.
    static (float Sag, float Rot) FanArc(float off, float mid)
    {
        if (mid <= 0) return (0, 0);
        float t = off / mid;   // -1 … 1 across the hand
        float maxSag = Math.Min(30f, 6f * mid * mid), maxRot = Math.Min(14f, 5.5f * mid);
        return (maxSag * t * t, maxRot * t);
    }

    // Neighbours slide aside while a hand card is hovered, so its cost roundel and name stay clear.
    void SpreadAround(int hovered, bool on)
    {
        for (int j = 0; j < fan.Count; j++)
        {
            var (cv, x) = fan[j]; if (!IsInstanceValid(cv) || cv.IsDragging || j == hovered) continue;
            float d = j - hovered, push = on ? (Math.Abs(d) == 1 ? 18 : 9) * Math.Sign(d) : 0;
            cv.CreateTween().TweenProperty(cv, "position:x", x + push, 0.14f).SetTrans(Tween.TransitionType.Cubic).SetEase(Tween.EaseType.Out);
        }
    }
}
