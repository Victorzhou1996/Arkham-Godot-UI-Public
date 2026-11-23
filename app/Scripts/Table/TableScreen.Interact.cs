using Godot;
using Gathering;
using System.Text.Json;

namespace ArkhamUI;

// Interaction layer of the revamp (REVAMP-SUMMARY §5): drag a hand card to the centre to play it (or onto the
// skill-test box to commit it); act on table elements in place through a small popup; the "下一步" bar tells the
// player what to do and holds only what has no element on screen; forced effects resolve with a corner notice.
public partial class TableScreen
{
    // ───────────────────────── resource and draw: act on the token / the deck itself (as on the 0712 web table)
    // Returns the element key a choice should be attached to, or null when it has no dedicated element.
    string? DedicatedKey(ChoicePresentation c)
    {
        var me = model?.Me; if (me is null) return null;
        // a resource for a named investigator (an event's "any investigator gains 1 resource": one choice per investigator) goes
        // on that investigator's own resource token (Yunke 11-22: both choices sat on 黛西's token as two identical "获取1资源")
        var comp = J.Get(c.Raw, "component");
        if (c.Tag is "ComponentLabel" && J.Tag(comp) == "InvestigatorComponent" && J.String(comp, "tokenType") == "ResourceToken" && J.String(comp, "investigatorId") is { Length: > 0 } who)
            return "res:" + who;
        if (c.Title.StartsWith("获取") && c.Title.Contains("资源")) return "res:" + me.Id;
        if (c.Title == "抽牌") return "deck:" + me.Id;
        if (c.Tag == "TargetLabel" && J.Tag(J.Get(c.Raw, "target")) == "EncounterDeckTarget") return "encdeck";
        return null;
    }

    // Makes a control clickable through `key` and, when that key has choices, draws a pulsing amber frame around it.
    // The frame follows the element's own outline: a hexagon for the resource token, the card-back rectangle for the deck.
    Control Clickable(Control c, string key, bool hex = false, Rect2? frame = null, int radius = 4)
    {
        if (!byAnchor.ContainsKey(key)) return c;
        c.MouseFilter = MouseFilterEnum.Stop; c.MouseDefaultCursorShape = CursorShape.PointingHand;
        Control ring;
        if (hex)
        {
            var line = new Line2D { Width = 3, DefaultColor = Style.Amber, Antialiased = true, ZIndex = 40 };
            var glow = new Line2D { Width = 9, DefaultColor = new Color(Style.Amber, 0.3f), Antialiased = true, ZIndex = 39 };
            var outer = new Control { MouseFilter = MouseFilterEnum.Ignore, ZIndex = 40 };
            void Shape() { var s = c.Size; if (s.X <= 0 || s.Y <= 0) s = c.CustomMinimumSize; var pts = HexOutline(s); line.Points = pts; glow.Points = pts; }
            outer.AddChild(glow); outer.AddChild(line); c.AddChild(outer); outer.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
            c.Resized += Shape; Shape();
            ring = outer;
        }
        else
        {
            var panel = new Panel { MouseFilter = MouseFilterEnum.Ignore, ZIndex = 40 };
            var sb = new StyleBoxFlat { BgColor = new Color(0, 0, 0, 0), DrawCenter = false, BorderColor = Style.Amber, ShadowColor = new Color(Style.Amber, 0.45f), ShadowSize = 10 };
            sb.SetBorderWidthAll(3); sb.SetCornerRadiusAll(radius); sb.AntiAliasing = true;
            panel.AddThemeStyleboxOverride("panel", sb);
            c.AddChild(panel);
            if (frame is Rect2 f) { panel.Position = f.Position - new Vector2(2, 2); panel.Size = f.Size + new Vector2(4, 4); }
            else panel.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
            ring = panel;
        }
        if (S.Settings.ReducedMotion != true) { var tw = ring.CreateTween().SetLoops(); tw.TweenProperty(ring, "modulate:a", 0.45f, 0.9f); tw.TweenProperty(ring, "modulate:a", 1f, 0.9f); }
        c.GuiInput += e => { if (e is InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Left }) ActOn(key, c, ""); };
        Anchor(key, c);
        if (frame is Rect2 fr) anchorFrames[key] = fr;   // the part that looks like the thing (a pile's top card, not its label): the ripple centres on it
        return c;
    }

    // Outline of the pointy-top hexagon the resource token is drawn as (vertices at the middle of the top and bottom edges).
    static Vector2[] HexOutline(Vector2 s)
    {
        var p = new[] { new Vector2(0.5f, 0f), new Vector2(1f, 0.25f), new Vector2(1f, 0.75f), new Vector2(0.5f, 1f), new Vector2(0f, 0.75f), new Vector2(0f, 0.25f), new Vector2(0.5f, 0f) };
        for (int i = 0; i < p.Length; i++) p[i] = new Vector2(p[i].X * s.X, p[i].Y * s.Y);
        return p;
    }

    // ───────────────────────── in-place popup menu (replaces the old modal "choose an action" dialog)
    Control? popup;
    void ClosePopup() { if (popup is not null && IsInstanceValid(popup)) popup.QueueFree(); popup = null; }

    void InPlaceMenu(Control anchor, string title, IReadOnlyList<ChoicePresentation> list, string code)
    {
        ClosePopup(); Peek.Hide();
        var root = new Control { MouseFilter = MouseFilterEnum.Stop };   // click outside closes
        AddChild(root); root.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect); root.ZIndex = 150;
        root.GuiInput += e => { if (e is InputEventMouseButton { Pressed: true }) ClosePopup(); };
        var col = new VBoxContainer { MouseFilter = MouseFilterEnum.Ignore }; col.AddThemeConstantOverride("separation", 6);
        if (title.Length > 0) col.AddChild(Style.Text(title, 20, Style.HoverText, Style.Kai));
        int short_ = 0;   // the card's own abilities shown by their verb only
        foreach (var c in list)
        {
            var b = ChoiceButton(c); b.CustomMinimumSize = new Vector2(300, 50); col.AddChild(b);
            // (the skip button can come back as a column with its trigger switches: close the menu from its button inside)
            var btn = b as Button ?? b.FindChildren("*", "Button", true, false).OfType<Button>().FirstOrDefault();
            if (btn is not null) btn.Pressed += ClosePopup;
            // a card's own menu: "攻击 · 刀子" → "攻击" — the card is the one clicked, its hover box says which ability (Yunke 11-19)
            if (btn is not null && c.Tag == "AbilityLabel" && c.Title.Contains(" · ") && ChoiceAbility(c) is { } ca)
            {
                string verb = c.Title[..c.Title.IndexOf(" · ")];
                btn.Text = verb; b.CustomMinimumSize = new Vector2(150, 50); short_++;   // no "›" either, and a narrow menu
                // "发动能力", or one verb twice (two 攻击): the ability's icon and first words tell them apart (Yunke 11-22: 饥饿霉菌)
                if (verb == "发动能力" || list.Count(o => o.Title.StartsWith(verb + " · ")) > 1)
                    AbilityFace(btn, ca.Line);
            }
            // the title screen's menu lines: no box, a glowing bar under the pointer (Yunke 11-19); the trigger switches keep theirs
            // (the left margin it had stays: a glyph — action / reaction / fast — sits there)
            if (btn is not null) Ui.MenuStyle(btn, (int)Math.Max(18, btn.GetThemeStylebox("normal").ContentMarginLeft), 30, 6);
        }
        var box = Ui.Panel(col, 14, 0.98f); root.AddChild(box); box.ResetSize();
        Ui.StaggerIn(box, col.GetChildren().OfType<Control>(), 0.04);
        var r = anchor.GetGlobalRect(); var vp = GetViewportRect().Size; var s = box.Size;
        float x = r.End.X + 12, y = r.Position.Y;
        if (x + s.X > vp.X - 12) x = r.Position.X - s.X - 12;
        if (x < 12) { x = r.Position.X + r.Size.X / 2 - s.X / 2; y = r.Position.Y - s.Y - 12; }
        box.Position = new Vector2(Math.Clamp(x, 12, Math.Max(12, vp.X - s.X - 12)), Math.Clamp(y, 12, Math.Max(12, vp.Y - s.Y - 12)));
        popup = root;
        if (S.Settings.ReducedMotion != true) { box.Modulate = new Color(1, 1, 1, 0); box.CreateTween().TweenProperty(box, "modulate:a", 1f, 0.1f); }
    }

    // ───────────────────────── exits: every location I can act on (move there) gets a tag in the scene
    void Exits()
    {
        var m = model!; var here = m.MyLocation?.Id;
        var spots = byAnchor.Keys.Where(k => k.StartsWith("loc:") && k[4..] != here).ToList();
        if (spots.Count == 0) return;
        // several exits wrap onto a second line instead of running off the screen (chapter three has four woods around the path)
        var row = new HFlowContainer { Alignment = FlowContainer.AlignmentMode.Center, MouseFilter = MouseFilterEnum.Ignore };
        row.AddThemeConstantOverride("h_separation", 16); row.AddThemeConstantOverride("v_separation", 10);
        Place(row, -560, 520, 560, 640, 0.5f, 0, 0.5f, 0);
        foreach (var key in spots)
        {
            var loc = m.Location(key[4..]); if (loc is null) continue;
            var list = byAnchor[key];
            string verb = list.Count == 1 ? list[0].Title.Split(new[] { " · ", "·" }, StringSplitOptions.None)[0].Trim() : "前往";
            string place = scenario?.Place(m.Label(loc)) ?? "";
            var b = Ui.MinimalButton("→ " + verb + " · " + m.Name(loc) + (place.Length > 0 ? " · " + place : "") + (m.Revealed(loc) ? "" : "（未揭示）"), () => { }, 22, true);
            b.Pressed += () => ActOn(key, b, loc.Code);
            b.CustomMinimumSize = new Vector2(0, 48);
            var pulse = b.CreateTween().SetLoops(); if (S.Settings.ReducedMotion != true) { pulse.TweenProperty(b, "modulate", new Color(1.15f, 1.08f, 0.9f), 0.9f); pulse.TweenProperty(b, "modulate", Colors.White, 0.9f); } else pulse.Kill();
            Peek.Attach(b, loc.Code);
            // The forced cost of entering is shown before the move, not after: attic horror and cellar damage are what sank the autoplay.
            string entering = loc.Code switch { "01113" => "进入阁楼：强制，受到 1 点恐惧", "01114" => "进入地窖：强制，受到 1 点伤害", _ => "" };
            if (entering.Length > 0) b.TooltipText = entering;
            Anchor(key, b); row.AddChild(b);
        }
    }

    // ───────────────────────── "下一步" bar: one line of guidance + choices with no element on screen
    void NextStepBar()
    {
        var m = model!; if (m.QuestionTag is "" or "Read" or "ChooseAmounts" or "ChoosePaymentAmounts" or "ChooseOneWizard" or "DropDown" or "PickDestiny" or "PickCampaignSpecific") return;
        var me = m.Me;
        var loose = m.Question.Choices.Where(c => c.Enabled && c.Tag != "EndTurnButton" && DedicatedKey(c) is null && !(m.InSkillTest && c.Tag is "StartSkillTestButton" or "SkillTestApplyResultsButton" or "SkipTriggersButton")
                && !(CommitFromElsewhere && m.InSkillTest && c.Title.StartsWith("投入")))   // committed from the test panel (its 其他可投入的卡 row), not a loose button
            .Where(c =>
            {
                var a = m.Anchor(c);
                // while the map is unlocked for arranging, what stands on it is not offered elsewhere (it filled the middle)
                if (mapUnlocked && (a.StartsWith("loc:") || a.StartsWith("enemy:") || a.StartsWith("inv:") || a.StartsWith("asset:"))) return false;
                return a.Length == 0 || !anchors.ContainsKey(a) || (me is not null && a == "inv:" + me.Id && c.Tag != "AbilityLabel");
            }).ToList();
        // damage / horror that can go onto a card on the table is assigned there (TableScreen.Assign), not in the middle
        var onCards = AssignOnCards(m);
        if (onCards.Count > 0) loose = loose.Where(c => !onCards.Contains(c)).ToList();
        // (the investigator's own abilities light the portrait at the bottom left and are used by clicking it, as on the html table)
        // one row: the guidance pill, then the loose choices; wraps onto a second line if needed
        var row = new HFlowContainer { Alignment = FlowContainer.AlignmentMode.Center, MouseFilter = MouseFilterEnum.Ignore };
        row.AddThemeConstantOverride("h_separation", 10); row.AddThemeConstantOverride("v_separation", 8);
        Place(row, SideMargin + SideW + 30, -330, -SideMargin - SideW - 30, -266, 0, 1, 1, 1);
        row.GrowVertical = GrowDirection.Begin; stepRow = row;
        if (onCards.Count > 0) row.AddChild(AssignPill(m));
        // someone answers during another investigator's turn (a reaction, a commit to a friend's test): one short line says so,
        // with both portraits (Codex review / Yunke 11-18 — only when the two differ)
        // (not while the players pick who goes first: no turn has begun, the id is the last round's — survey 11-19)
        bool pickingFirst = m.Investigators.Count() > 1 && m.Investigators.All(i => m.Question.Choices.Any(c => c.Enabled && m.Anchor(c) == "inv:" + i.Id));
        if (me is not null && !pickingFirst && m.Phase == "InvestigationPhase" && TurnInvestigator(m) is { } turn && turn.Id != me.Id)
        {
            var pill = new HBoxContainer { MouseFilter = MouseFilterEnum.Ignore }; pill.AddThemeConstantOverride("separation", 6);
            foreach (var (who, text) in new[] { (turn, m.Name(turn) + "的回合"), (me, m.Name(me) + "响应") })
            {
                var face = Ui.SquarePortrait(who.Code, 34); face.MouseFilter = MouseFilterEnum.Ignore; face.SizeFlagsVertical = SizeFlags.ShrinkCenter; pill.AddChild(face);
                var t = Style.Text(text, 19, who.Id == me.Id ? Style.Amber : Style.Paper, Style.Kai); t.SizeFlagsVertical = SizeFlags.ShrinkCenter;
                t.AddThemeConstantOverride("outline_size", 6); t.AddThemeColorOverride("font_outline_color", Colors.Black); pill.AddChild(t);
                if (who.Id == turn.Id) pill.AddChild(Style.Text("·", 19, Style.PaperDim));
            }
            row.AddChild(pill);
        }
        if (InMulligan(m))
        {
            // the opening hand is confirmed once, for all marked cards together
            var tip = Style.Text("点击想换掉的牌（可多选，再点一次取消），然后确认", 19, Style.Paper, Style.Kai); tip.SizeFlagsVertical = SizeFlags.ShrinkCenter;
            tip.AddThemeConstantOverride("outline_size", 6); tip.AddThemeColorOverride("font_outline_color", Colors.Black); row.AddChild(tip);
            var ok = Ui.MinimalButton(mulliganPick.Count == 0 ? "不换牌，确认" : "确认换掉 " + mulliganPick.Count + " 张", ConfirmMulligan, 21, true);
            ok.CustomMinimumSize = new Vector2(0, 46); row.AddChild(ok); mullConfirm = ok;
            if (!TutorialMullReady) { ok.Disabled = true; ok.TooltipText = "新手教程：先点选发光的牌"; }
            loose = loose.Where(c => !c.Title.Contains("结束选牌") && !c.Title.StartsWith("换掉")).ToList();
        }
        // Over the hand size at the upkeep check (the core asks for one card at a time): say so, with the numbers (Yunke 11-22)
        if (me is not null && HandOver(m, me) is { } over && m.Phase == "UpkeepPhase" && m.Question.Choices.Any(c => c.Enabled && m.Anchor(c).StartsWith("hand:")))
        {
            var tip = Style.Text("手牌超过上限：现有 " + over.Have + " 张，上限 " + over.Limit + " 张，点一张牌弃掉（还需弃 " + (over.Have - over.Limit) + " 张）", 19, new Color("ff8a70"), Style.Kai);
            tip.SizeFlagsVertical = SizeFlags.ShrinkCenter; tip.AddThemeConstantOverride("outline_size", 6); tip.AddThemeColorOverride("font_outline_color", Colors.Black); row.AddChild(tip);
        }
        // Picking an investigator (every one offered, whether or not their portrait is somewhere on screen) or a card with
        // nothing on screen to click (another investigator's asset, a card looked at from the deck): small cards in the
        // middle of the table, with the hand cards' edge, instead of a row of name buttons (playtest 2 #4, #12, #19, #20, #23).
        var anyInv = m.Question.Choices.Where(c => c.Enabled && c.Tag is "PortraitLabel" or "TargetLabel" && m.Anchor(c).StartsWith("inv:")).ToList();
        // When several investigators are offered (add 丽塔·钱德勒 to whose deck?), every one of them is in the middle, the one
        // being asked too: leaving that one to a click on their own portrait read as "only the other one can take her"
        // (Yunke 11-19 r9 #22). Offered alone, it stays on the portrait.
        bool several = anyInv.Select(c => m.Anchor(c)).Distinct().Count() > 1;
        var people = anyInv.Where(c => several || !(me is not null && m.Anchor(c) == "inv:" + me.Id && DedicatedKey(c) is not null)).ToList();
        var cardPicks = loose.Where(c => !people.Contains(c) && c.CardCode.Length > 0 && c.Tag is "TargetLabel" or "ComponentLabel" or "AbilityLabel" or "Label" or "CardLabel"   // CardLabel: 莫莉根的恩赐's three weaknesses, cards to pick by their face
            && !c.Title.StartsWith("投入") && !c.Title.StartsWith("换掉")).ToList();
        // "treat the revealed token as another token in the bag" (歪打正着): the bag's tokens as pictures to click
        var tokenPicks = loose.Where(c => c.Tag == "TargetLabel" && J.Tag(J.Get(c.Raw, "target")) == "ChaosTokenTarget").ToList();
        // a card held up to look at (保安室: the exhibit deck's top card, its revealed side): the card itself, large, in the middle,
        // and the core's "继续" under it (Yunke 11-22: "保安室的动作能力没有动画效果" — the look was skipped by itself, unseen)
        var lookGo = loose.FirstOrDefault(c => c.Tag == "Label" && J.String(c.Raw, "label") == "$label.continue");
        var lookChoices = LookCards(m).Count > 0 ? loose.Where(c => c.Tag == "Label").ToList() : new();
        bool plainLook = lookChoices.Count == 1 && lookChoices[0] == lookGo;
        if (LookCards(m) is { Count: > 0 } looked && lookChoices.Count > 0)
        {
            var box = new VBoxContainer { MouseFilter = MouseFilterEnum.Ignore, Alignment = BoxContainer.AlignmentMode.Center }; box.AddThemeConstantOverride("separation", 16);
            var patch = Patch(box, 0.88f); patch.ZIndex = 71; patch.MouseFilter = MouseFilterEnum.Stop;
            Place(patch, -600, -330, 600, 250, 0.5f, 0.5f, 0.5f, 0.5f);
            var t = Style.Text((!plainLook && m.Question.Title.Length > 0 ? m.Question.Title + "：" : "查看：") + string.Join("、", looked.Select(S.Name).Distinct()), 26, Style.HoverText, Style.Kai); t.HorizontalAlignment = HorizontalAlignment.Center;
            t.AddThemeConstantOverride("outline_size", 8); t.AddThemeColorOverride("font_outline_color", Colors.Black); box.AddChild(t);
            Draggable("look", t, patch);
            var cardsRow = new HBoxContainer { MouseFilter = MouseFilterEnum.Ignore, Alignment = BoxContainer.AlignmentMode.Center }; cardsRow.AddThemeConstantOverride("separation", 20); box.AddChild(cardsRow);
            var size = looked.Count > 2 ? new Vector2(180, 252) : new Vector2(250, 350);
            foreach (var code in looked)
            {
                var cv = new CardView { Code = code, CustomMinimumSize = size, Size = size, HoverZoom = true, Redrawn = false, Actionable = false, SizeFlagsVertical = SizeFlags.ShrinkCenter };
                cardsRow.AddChild(cv);
                if (S.Settings.ReducedMotion != true) { cv.PivotOffset = size / 2; cv.Scale = new Vector2(0.6f, 0.6f); cv.Modulate = new Color(1, 1, 1, 0); var tw = cv.CreateTween().SetParallel(); tw.TweenProperty(cv, "scale", Vector2.One, 0.35f).SetTrans(Tween.TransitionType.Back).SetEase(Tween.EaseType.Out); tw.TweenProperty(cv, "modulate:a", 1f, 0.25f); }
            }
            // what to do with it: the core's own choices, as buttons under the card (a lone "继续" for a plain look)
            var goRow = new HFlowContainer { MouseFilter = MouseFilterEnum.Ignore, Alignment = FlowContainer.AlignmentMode.Center }; goRow.AddThemeConstantOverride("h_separation", 14); goRow.AddThemeConstantOverride("v_separation", 10); box.AddChild(goRow);
            foreach (var lc in lookChoices)
            {
                var pick = lc;
                var go = Ui.MinimalButton(lc == lookGo ? "继续" : lc.Title, () => Choose(pick), 22, true); go.CustomMinimumSize = new Vector2(plainLook ? 200 : 150, 50);
                goRow.AddChild(go);
            }
            loose = loose.Except(lookChoices).ToList();
            if (App.I.QaRunning) GD.Print("QA_LOOK " + string.Join(",", looked));
        }
        if (tokenPicks.Count > 0)
        {
            // on its own dark patch (Yunke 11-22: it floated over the map with nothing behind it), the revealed token on top
            var box = new VBoxContainer { MouseFilter = MouseFilterEnum.Ignore, Alignment = BoxContainer.AlignmentMode.Center }; box.AddThemeConstantOverride("separation", 14);
            var patch = Patch(box, 0.88f); patch.ZIndex = 71; patch.MouseFilter = MouseFilterEnum.Stop;
            Place(patch, -600, -280, 600, 200, 0.5f, 0.5f, 0.5f, 0.5f);
            var shown = FocusedTokens(m); if (shown.Count > 0) box.AddChild(RevealedTokenRow(m, shown, 84));
            var t = Style.Text("把揭示的标记当作哪一枚结算？", 26, Style.HoverText, Style.Kai); t.HorizontalAlignment = HorizontalAlignment.Center;
            t.AddThemeConstantOverride("outline_size", 8); t.AddThemeColorOverride("font_outline_color", Colors.Black); box.AddChild(t);
            Draggable("tokens", t, patch);
            var flow = new HFlowContainer { Alignment = FlowContainer.AlignmentMode.Center, MouseFilter = MouseFilterEnum.Ignore }; flow.AddThemeConstantOverride("h_separation", 10); flow.AddThemeConstantOverride("v_separation", 10); box.AddChild(flow);
            foreach (var c in tokenPicks)
            {
                string face = J.String(J.Get(J.Get(c.Raw, "target"), "contents"), "chaosTokenFace");
                var b = new TextureButton { TextureNormal = Ui.Token(face), IgnoreTextureSize = true, StretchMode = TextureButton.StretchModeEnum.KeepAspectCentered, CustomMinimumSize = new Vector2(78, 78), TooltipText = c.Title, MouseDefaultCursorShape = CursorShape.PointingHand };
                var pick = c; b.Pressed += () => { tokenSubstitute = face; Choose(pick); };
                b.MouseEntered += () => b.Modulate = new Color(1.25f, 1.15f, 0.9f); b.MouseExited += () => b.Modulate = Colors.White;
                flow.AddChild(b);
            }
            loose = loose.Except(tokenPicks).ToList();
        }
        if (people.Count > 0 || cardPicks.Count > 0)
        {
            // the map now fills the middle of the table (Yunke 11-18 r8): the picks sit on a soft dark patch over it
            // assigning damage / horror (Yunke 11-19 r10): the patch and the cards that can take it turn red
            bool assigning = m.Question.Choices.Any(c => c.Enabled && AssignRe.IsMatch(c.Title));
            // above the map's own glows (ripples, quick-ability buttons, breathing rims sit at z 6–7 inside the board; Yunke 11-20:
            // a location's glow showed through the picker), like the sentence column below
            var back = new ColorRect { MouseFilter = MouseFilterEnum.Ignore, Color = assigning ? new Color(0.2f, 0.02f, 0.015f, 0.8f) : new Color(0.02f, 0.015f, 0.01f, 0.78f), ZIndex = 70 };
            // a little lower than the middle (Yunke 11-19 r9 #9: it sat over the map's upper cards at the start)
            // only investigators to pick (who goes first, who takes it…): on the right, the middle stays clear for the map
            // (Yunke 11-20); cards to pick stay in the middle
            bool onRight = people.Count > 0 && cardPicks.Count == 0;
            // no edge: it fades out all round — the fade is set for the patch's own size (Yunke 11-21: the narrower patch on the
            // right was cut off hard, with the fade of the wide one)
            back.Material = onRight ? Ui.EdgeAlpha(Vector2.Zero, new Vector2(760, 440), 200, 120, 200, 120) : Ui.EdgeAlpha(Vector2.Zero, new Vector2(1200, 440), 220, 120, 220, 120);
            if (onRight) Place(back, -980, -190, -220, 250, 1f, 0.5f, 1f, 0.5f);
            else Place(back, -600, -190, 600, 250, 0.5f, 0.5f, 0.5f, 0.5f);
            var box = new VBoxContainer { MouseFilter = MouseFilterEnum.Ignore, Alignment = BoxContainer.AlignmentMode.Center, ZIndex = 71 }; box.AddThemeConstantOverride("separation", 14);
            if (onRight) Place(box, -940, -170, -260, 230, 1f, 0.5f, 1f, 0.5f);
            else Place(box, -560, -170, 560, 230, 0.5f, 0.5f, 0.5f, 0.5f);
            if (m.Question.Title.Length > 0 || EnemyAskTitle(m) is not null)
            {
                var t = Style.Text(EnemyAskTitle(m) ?? m.Question.Title, 26, Style.HoverText, Style.Kai); t.HorizontalAlignment = HorizontalAlignment.Center;
                t.AddThemeConstantOverride("outline_size", 8); t.AddThemeColorOverride("font_outline_color", Colors.Black); box.AddChild(t);
                t.TooltipText = "按住标题可拖动";
                Draggable("picks", t, back, box);   // a search / pick box can be dragged aside by its title (Yunke 11-20)
            }
            // assigning damage / horror: what is still to place, as the two tokens with their numbers (Codex review: "给谁、给什么")
            int Left(string kind) => m.Question.Choices.Where(c => c.Enabled && c.Title.Contains("分配" + kind)).Select(c => System.Text.RegularExpressions.Regex.Match(c.Title, @"待分配(\d+)点")).Where(x => x.Success).Select(x => int.Parse(x.Groups[1].Value)).DefaultIfEmpty(0).Max();
            int dmgLeft = Left("伤害"), horLeft = Left("恐惧");
            if (dmgLeft + horLeft > 0)
            {
                var left = new HBoxContainer { MouseFilter = MouseFilterEnum.Ignore, Alignment = BoxContainer.AlignmentMode.Center }; left.AddThemeConstantOverride("separation", 10);
                var lt = Style.Text("还要分配", 21, Style.Paper, Style.Kai); lt.VerticalAlignment = VerticalAlignment.Center; lt.AddThemeConstantOverride("outline_size", 6); lt.AddThemeColorOverride("font_outline_color", Colors.Black); left.AddChild(lt);
                foreach (var (kind, n, color) in new[] { ("health", dmgLeft, Style.Danger), ("horror", horLeft, Style.Willpower) })
                {
                    var ic = Ui.Image(Ui.Stat(kind), new Vector2(34, 34)); ic.MouseFilter = MouseFilterEnum.Ignore; ic.Modulate = n > 0 ? Colors.White : new Color(1, 1, 1, 0.35f); left.AddChild(ic);
                    var nt = Style.Text(n.ToString(), 26, n > 0 ? color : Style.PaperDim, Style.Symbols); nt.VerticalAlignment = VerticalAlignment.Center; nt.AddThemeConstantOverride("outline_size", 6); nt.AddThemeColorOverride("font_outline_color", Colors.Black); left.AddChild(nt);
                }
                box.AddChild(left);
            }
            // many cards (a search shows everything it looked at) wrap onto more rows instead of running off the screen
            var notOffered = SearchedButNotOffered(m, cardPicks);
            var pickSize = cardPicks.Count + notOffered.Count > 7 ? new Vector2(112, 157) : new Vector2(140, 196);   // smaller when many
            var cards = new HFlowContainer { MouseFilter = MouseFilterEnum.Ignore, Alignment = FlowContainer.AlignmentMode.Center }; cards.AddThemeConstantOverride("h_separation", 18); cards.AddThemeConstantOverride("v_separation", 10);
            // more than two rows (a whole deck searched): they scroll inside a two-row window, the hand stays clear
            if (cardPicks.Count + notOffered.Count > 14)
            {
                var sc = new ScrollContainer { HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled, CustomMinimumSize = new Vector2(1100, (pickSize.Y + 34) * 2 + 10), MouseFilter = MouseFilterEnum.Pass };
                cards.SizeFlagsHorizontal = SizeFlags.ExpandFill; sc.AddChild(cards); box.AddChild(sc);
            }
            else box.AddChild(cards);
            // the card this choice is about (Yunke 11-22: "选择加入死灵之书的时候，应该让我看到死灵之书的卡牌详情" — whenever a
            // choice comes from a card, that card is shown with it): first, before the people / cards to pick, set apart
            if (QuestionSubject(m) is { } subject && !cardPicks.Any(c => CoreCatalog.Normalize(c.CardCode) == subject))
            {
                var sv = new CardView { Code = subject, CustomMinimumSize = new Vector2(150, 210), Size = new Vector2(150, 210), HoverZoom = true, Redrawn = false, Actionable = false, SizeFlagsVertical = SizeFlags.ShrinkCenter };
                sv.TooltipText = S.Name(subject); cards.AddChild(sv);
                cards.AddChild(new Control { CustomMinimumSize = new Vector2(24, 0), MouseFilter = MouseFilterEnum.Ignore });
                if (onRight) { back.OffsetLeft -= 190; box.OffsetLeft -= 190; back.Material = Ui.EdgeAlpha(Vector2.Zero, new Vector2(950, 440), 200, 120, 200, 120); }
                if (App.I.QaRunning) GD.Print("QA_SUBJECT " + subject);
            }
            pickNodes.Clear();
            foreach (var c in people)
            {
                var inv = m.Investigators.FirstOrDefault(i => "inv:" + i.Id == m.Anchor(c)); if (inv is null) continue;
                var cv = CardView.PortraitCard(inv.Code, 210, true); cv.HoverZoom = true;
                pickNodes[CoreCatalog.Normalize(inv.Code)] = cv;   // 新手教程 points at the one to pick
                if (assigning) cv.Glow = AssignRed;
                var pick = c; cv.OnClick = () => { assignFrom = cv; Choose(pick); };
                cards.AddChild(cv);
            }
            // the same card offered for both damage and horror is one card with two buttons under it, not two identical
            // portraits that look like two people to choose between
            var shown = new HashSet<string>();
            foreach (var c in cardPicks)
            {
                string code = CoreCatalog.Normalize(c.CardCode);
                var both = cardPicks.Where(x => CoreCatalog.Normalize(x.CardCode) == code && (x.Title.Contains("分配伤害") || x.Title.Contains("分配恐惧"))).ToList();
                if (both.Count == 2 && both.Any(x => x.Title.Contains("分配伤害")) && both.Any(x => x.Title.Contains("分配恐惧")))
                {
                    if (!shown.Add(code)) continue;
                    bool inv2 = J.String(S.Card(code), "type_code") == "investigator";
                    var col2 = new VBoxContainer { MouseFilter = MouseFilterEnum.Ignore }; col2.AddThemeConstantOverride("separation", 6);
                    var top2 = inv2 ? CardView.PortraitCard(code, 196) : new CardView { Code = code, CustomMinimumSize = new Vector2(140, 196), Size = new Vector2(140, 196), SizeFlagsHorizontal = SizeFlags.ShrinkCenter };
                    top2.MouseFilter = MouseFilterEnum.Ignore; top2.Rim = AssignRed; col2.AddChild(top2);
                    foreach (var x in both.OrderBy(x => x.Title.Contains("分配恐惧")))
                    {
                        bool d = x.Title.Contains("分配伤害"); var pick2 = x;
                        var b2 = Ui.MinimalButton(d ? "承受 1 伤害" : "承受 1 恐惧", () => { assignFrom = top2; Choose(pick2); }, 18, true); b2.CustomMinimumSize = new Vector2(140, 40);
                        b2.AddThemeColorOverride("font_color", d ? Style.Danger : Style.Willpower); col2.AddChild(b2);
                    }
                    cards.AddChild(col2);
                    continue;
                }
                bool twin = cardPicks.Count(x => CoreCatalog.Normalize(x.CardCode) == code) > 1;
                var col = new VBoxContainer { MouseFilter = MouseFilterEnum.Ignore }; col.AddThemeConstantOverride("separation", 6);
                // assigning damage or horror: the card that takes it carries the token, and the caption says which
                bool dmg = c.Title.Contains("分配伤害"), hor = c.Title.Contains("分配恐惧");
                bool isInv = J.String(S.Card(code), "type_code") == "investigator";
                var cv = isInv ? CardView.PortraitCard(code, pickSize.Y, true) : new CardView { Code = code, CustomMinimumSize = pickSize, Size = pickSize, Actionable = true, SizeFlagsHorizontal = SizeFlags.ShrinkCenter };
                cv.Badges = dmg ? new[] { ("health", 1) } : hor ? new[] { ("horror", 1) } : Array.Empty<(string, int)>();
                if (dmg || hor) cv.Glow = AssignRed;
                var pick = c; cv.OnClick = () => { assignFrom = cv; Choose(pick); };
                if (!isInv) AddVictoryCorner(cv, Victory(code), 20);   // a pick shows its victory points too (Yunke 11-22: 犹格·索托斯的法师)
                col.AddChild(cv);
                string capText = dmg ? "承受伤害" : hor ? "承受恐惧" : c.Title;
                if (twin || dmg || hor) { var cap = Style.Text(capText, 16, dmg ? Style.Danger : hor ? Style.Willpower : Style.PaperDim, Style.Kai); cap.HorizontalAlignment = HorizontalAlignment.Center; cap.CustomMinimumSize = new Vector2(140, 0); cap.AutowrapMode = TextServer.AutowrapMode.WordSmart; col.AddChild(cap); }
                cards.AddChild(col);
            }
            // a search: every card looked at is shown, the ones that cannot be taken dimmed beside the ones that can (Yunke 11-20:
            // seeing them is part of the search)
            foreach (var code in notOffered)
            {
                var col = new VBoxContainer { MouseFilter = MouseFilterEnum.Ignore }; col.AddThemeConstantOverride("separation", 6);
                var cv = new CardView { Code = code, CustomMinimumSize = pickSize, Size = pickSize, Actionable = false, SizeFlagsHorizontal = SizeFlags.ShrinkCenter, Modulate = new Color(0.55f, 0.55f, 0.55f), HoverZoom = true };
                var cc = code; cv.OnClick = () => CardReader.Open(cc);
                AddVictoryCorner(cv, Victory(code), 20);
                col.AddChild(cv);
                var cap = Style.Text("不能选", 16, Style.PaperDim, Style.Kai); cap.HorizontalAlignment = HorizontalAlignment.Center; cap.CustomMinimumSize = new Vector2(pickSize.X, 0); col.AddChild(cap);
                cards.AddChild(col);
            }
            loose = loose.Except(people).Except(cardPicks).ToList();
        }
        // Two or more whole-sentence choices (a resolution: "烧了它！(→ 结局1)" / "…(→ 结局2)"): a column in the middle of the table
        // on a soft dark patch, each a wide button that may wrap, instead of long pills over the map (Yunke 11-20 #24)
        var sentences = loose.Where(c => c.Title.Length > 16 && c.Tag is not ("SkipTriggersButton" or "EndTurnButton")).ToList();
        if (sentences.Count >= 2 && sentences.Count <= 6)
        {
            var back = new ColorRect { MouseFilter = MouseFilterEnum.Ignore, Color = new Color(0.02f, 0.015f, 0.01f, 0.82f), ZIndex = 70 };   // above a test panel still on the table
            back.Material = Ui.EdgeAlpha(Vector2.Zero, new Vector2(1100, 420), 200, 110, 200, 110);
            Place(back, -550, -230, 550, 190, 0.5f, 0.5f, 0.5f, 0.5f);
            var box = new VBoxContainer { MouseFilter = MouseFilterEnum.Ignore, Alignment = BoxContainer.AlignmentMode.Center, ZIndex = 71 }; box.AddThemeConstantOverride("separation", 12);
            Place(box, -430, -200, 430, 160, 0.5f, 0.5f, 0.5f, 0.5f);
            if (m.Question.Title.Length > 0)
            {
                var qt = Style.Text(m.Question.Title, 24, Style.HoverText, Style.Kai); qt.HorizontalAlignment = HorizontalAlignment.Center; qt.AutowrapMode = TextServer.AutowrapMode.WordSmart;
                qt.AddThemeConstantOverride("outline_size", 8); qt.AddThemeColorOverride("font_outline_color", Colors.Black); box.AddChild(qt);
            }
            foreach (var c in sentences)
            {
                var pick = c;
                var sb2 = Ui.Button(c.Title, () => Choose(pick), 22, true); sb2.CustomMinimumSize = new Vector2(860, 58);
                sb2.AutowrapMode = TextServer.AutowrapMode.WordSmart; sb2.TooltipText = c.Hint;
                SkillIconFace(sb2, 22);
                box.AddChild(sb2);
            }
            Ui.StaggerIn(box, box.GetChildren().OfType<Control>());
            loose = loose.Except(sentences).ToList();
        }
        // a choice of which skill to test with shows only the skills' icons (Yunke 11-18 #18); say what is being chosen and for
        // what in front of them (QA 11-20, 阻止计划: two icon buttons and no word of what they were for)
        if (loose.Count > 0 && loose.Where(c => c.Enabled).All(c => c.Tag is "SkillLabel"))
        {
            string src = m.Question.Title is { Length: > 0 } qt0 && qt0 is not ("选择下一步" or "按顺序选择") ? qt0 : "";   // not the generic fallback
            var cap = Style.Text("选择用哪项属性检定" + (src.Length > 0 ? " · " + src : ""), 21, Style.HoverText, Style.Kai);
            cap.VerticalAlignment = VerticalAlignment.Center; cap.SizeFlagsVertical = SizeFlags.ShrinkCenter;
            cap.AddThemeConstantOverride("outline_size", 6); cap.AddThemeColorOverride("font_outline_color", Colors.Black); row.AddChild(cap);
        }
        // tokens of the bag picked by face (不屈不挠: seal up to 3 tokens; QQ #42 — seven identical "选择 · 当前对象" buttons): as the
        // html version (Token.vue: a ChaosTokenFaceTarget choice is clicked on the token itself), each face once as its own token
        // picture with its name under it; the core's own buttons ("结束封印标记") stay after them
        var facePicks = loose.Where(c => c.Tag == "TargetLabel" && J.Tag(J.Get(c.Raw, "target")) == "ChaosTokenFaceTarget").ToList();
        if (facePicks.Count > 0)
        {
            string qt = m.Question.Title is { Length: > 0 } qt1 && qt1 is not ("选择下一步" or "按顺序选择") ? qt1 : "";
            if (qt.Length > 0)
            {
                var cap = Style.Text(qt, 21, Style.HoverText, Style.Kai); cap.VerticalAlignment = VerticalAlignment.Center; cap.SizeFlagsVertical = SizeFlags.ShrinkCenter;
                cap.AddThemeConstantOverride("outline_size", 6); cap.AddThemeColorOverride("font_outline_color", Colors.Black); row.AddChild(cap);
            }
            string[] order = { "PlusOne", "Zero", "MinusOne", "MinusTwo", "MinusThree", "MinusFour", "MinusFive", "MinusSix", "MinusSeven", "MinusEight", "Skull", "Cultist", "Tablet", "ElderThing", "ElderSign", "BlessToken", "CurseToken", "FrostToken" };
            foreach (var c in facePicks.GroupBy(c => J.String(J.Get(c.Raw, "target"), "contents")).Select(g => g.First())
                         .OrderBy(c => Array.IndexOf(order, J.String(J.Get(c.Raw, "target"), "contents")) is var i && i < 0 ? 99 : i))
            {
                string face = J.String(J.Get(c.Raw, "target"), "contents");
                var cell = new VBoxContainer { MouseFilter = MouseFilterEnum.Ignore, SizeFlagsVertical = SizeFlags.ShrinkCenter }; cell.AddThemeConstantOverride("separation", 0);
                var b = new TextureButton { TextureNormal = Ui.Token(face), IgnoreTextureSize = true, StretchMode = TextureButton.StretchModeEnum.KeepAspectCentered, CustomMinimumSize = new Vector2(56, 56), TooltipText = c.Title, MouseDefaultCursorShape = CursorShape.PointingHand, SizeFlagsHorizontal = SizeFlags.ShrinkCenter };
                var pick = c; b.Pressed += () => Choose(pick);
                b.MouseEntered += () => b.Modulate = new Color(1.25f, 1.15f, 0.9f); b.MouseExited += () => b.Modulate = Colors.White;
                cell.AddChild(b);
                var name = Style.Text(TokenName(face), 17, Style.Amber, Style.Kai); name.HorizontalAlignment = HorizontalAlignment.Center;
                name.AddThemeConstantOverride("outline_size", 5); name.AddThemeColorOverride("font_outline_color", Colors.Black); cell.AddChild(name);
                row.AddChild(cell);
            }
            loose = loose.Except(facePicks).ToList();
            if (App.I.QaRunning) GD.Print("QA_FACE_PICKS " + string.Join(",", facePicks.Select(c => J.String(J.Get(c.Raw, "target"), "contents")).Distinct()));
        }
        foreach (var c in loose.Take(8))
        {
            // single-line buttons: wrapping text inside a flow row squeezes them into tall slivers
            // the skip button comes back as a column (button + trigger switches) when trigger modes are known: no cast
            // (4-player run r11play2 crashed here: VBoxContainer is not a Button — the whole render stopped)
            var ctrl = ChoiceButton(c);
            if (ctrl is Button b)
            {
                b.AutowrapMode = TextServer.AutowrapMode.Off; b.SizeFlagsVertical = SizeFlags.ShrinkCenter;
                b.CustomMinimumSize = new Vector2(b.Text.Length == 0 ? 110 : 0, b.Text.Length == 0 ? 60 : 44);   // icon-only buttons (skill choice) keep their size
            }
            else ctrl.SizeFlagsVertical = SizeFlags.ShrinkCenter;
            row.AddChild(ctrl);
        }
    }

    // whose turn it is (the core's turn player; the active investigator as a fallback)
    static Entity? TurnInvestigator(TableModel m)
    {
        string id = J.String(m.Game, "turnPlayerInvestigatorId"); if (id.Length == 0) id = J.String(m.Game, "activeInvestigatorId");
        return id.Length == 0 ? null : m.Investigators.FirstOrDefault(i => i.Id == id);
    }

    string HintText(TableModel m)
    {
        var me = m.Me; int acts = me is null ? 0 : m.Actions(me);
        bool targets = m.Question.Choices.Any(c => c.Enabled && c.Tag == "TargetLabel" && !m.Anchor(c).StartsWith("hand:"));
        if (m.InSkillTest)
        {
            if (m.Question.Choices.Any(c => c.Enabled && c.Tag == "StartSkillTestButton")) return "检定：把手牌拖到检定框投入，然后点下方按钮开始";
            if (J.Tag(J.Get(m.SkillTest, "result")) != "Unrun") return "检定已揭晓，即将自动结算";
            return "检定即将进行：可以打出快速牌，或点「继续」";
        }
        if (AutoPolicy.NeedsOrdering(m)) return "多个效果同时触发：点击选择先结算哪一个";
        if (m.QuestionTag == "PlayerWindowChooseOne" && me is not null && m.Phase == "InvestigationPhase")
            return "你的回合 · 剩余 " + acts + " 次行动：拖动手牌到中央打出，或点击高亮的地点、敌人、出口";
        if (EnemyAskTitle(m) is { } askTitle) return askTitle + "：点击" + (EnemyAsk(m)?.Ask == "engage" ? "调查员" : "高亮的地点");
        if (targets) return m.Question.Title.Length > 0 ? m.Question.Title + "：点击高亮的目标" : "点击高亮的目标";
        return m.Question.Title.Length > 0 ? m.Question.Title : "请选择";
    }

    // The cards a running search has looked at (the investigator's search state, or the view's found cards) that are not among
    // the choices offered: the rest of what was searched
    List<string> SearchedButNotOffered(TableModel m, List<ChoicePresentation> picks)
    {
        var found = new List<string>();
        void Walk(System.Text.Json.JsonElement e, int depth)
        {
            if (depth > 8) return;
            if (e.ValueKind == System.Text.Json.JsonValueKind.Array) { foreach (var x in e.EnumerateArray()) Walk(x, depth + 1); return; }
            if (e.ValueKind != System.Text.Json.JsonValueKind.Object) return;
            var c = J.Get(e, "contents");
            if (c.ValueKind == System.Text.Json.JsonValueKind.Object && J.String(c, "cardCode").Length > 0) { found.Add(CoreCatalog.Normalize(J.String(c, "cardCode"))); return; }
            if (J.String(e, "cardCode").Length > 0 && J.String(e, "id").Length > 0) { found.Add(CoreCatalog.Normalize(J.String(e, "cardCode"))); return; }
            foreach (var p in e.EnumerateObject()) Walk(p.Value, depth + 1);
        }
        if (m.Me is { } me) Walk(J.Get(J.Get(me.Raw, "search"), "searchFoundCards"), 0);
        if (found.Count == 0) Walk(J.Get(m.Game, "foundCards"), 0);
        if (found.Count == 0 || picks.Count == 0) return new();
        var offered = picks.Select(c => CoreCatalog.Normalize(c.CardCode)).ToList();
        var rest = new List<string>();
        foreach (var f in found) { int k = offered.IndexOf(f); if (k >= 0) offered.RemoveAt(k); else rest.Add(f); }
        return rest;
    }

    // ───────────────────────── drag to play: drop zones
    Control? dropZone;
    Control? commitBox;     // skill-test commit area (drop target while committing)
    // The drop area is intentionally invisible (REVAMP §5): PlayRect() below decides where a release counts.
    void DragFeedback(bool on)
    {
        if (dropZone is not null && IsInstanceValid(dropZone)) dropZone.QueueFree(); dropZone = null;
    }

    // The middle of the table: between the side areas, below the top bar, above the hand.
    Rect2 PlayRect()
    {
        var vp = GetViewportRect().Size; float l = SideMargin + SideW + 60, r = vp.X - SideMargin - SideW - 60;
        return new Rect2(l, 190, Math.Max(200, r - l), Math.Max(160, vp.Y - 250 - 60 - 190));
    }

    // The window just before the commit step of a test (SkillTestFastWindow1: "现在可以使用…"): the rules core does not take
    // committed cards yet, but a card dropped on the test panel is meant for it (Yunke 11-20: "在墓地调查还是不能投牌" — the
    // player was in this window, where dragging did nothing). The drop passes the window and commits the card at the next step.
    string? pendingCommit;   // the hand card id to commit once the commit step comes
    bool PreCommitWindow(TableModel m) => m.InSkillTest && J.String(m.SkillTest, "step") == "SkillTestFastWindow1"
        && m.Question.Choices.Any(c => c.Enabled && c.Tag == "SkipTriggersButton");
    // whether a hand card could go into this test: it shows the tested skill or a wild icon (the core decides at the commit step)
    bool CommitLater(TableModel m, string code)
    {
        var tested = J.Items(J.Get(m.SkillTest, "skills")).Select(s => (s.GetString() ?? "").Replace("Skill", "").ToLowerInvariant()).ToHashSet();
        if (tested.Count == 0 && J.String(J.Get(m.SkillTest, "skills"), "contents") is { Length: > 0 } one) tested.Add(one.Replace("Skill", "").ToLowerInvariant());
        return CoreCosts.Skills(code).Any(s => s == "wild" || tested.Contains(s));
    }

    // Drop a hand card: on the commit box → commit; in the middle → play / use; anywhere else → back to hand.
    bool DropFromHand(string key, Vector2 globalPos)
    {
        DragFeedback(false);
        bool onTest = commitBox is not null && IsInstanceValid(commitBox) && commitBox.GetGlobalRect().Grow(40).HasPoint(globalPos);
        if (onTest && model is { } pm && PreCommitWindow(pm) && key.StartsWith("hand:") && pm.Question.Choices.FirstOrDefault(c => c.Enabled && c.Tag == "SkipTriggersButton") is { } skip
            && (!byAnchor.TryGetValue(key, out var l0) || !l0.Any(x => x.Title.StartsWith("投入"))))
        {
            pendingCommit = key[5..];
            if (App.I.QaRunning) GD.Print("QA_PRECOMMIT " + pendingCommit);
            GetTree().CreateTimer(S.Settings.ReducedMotion == true ? 0.01 : 0.16).Timeout += () => Choose(skip);
            return true;
        }
        if (!byAnchor.TryGetValue(key, out var list) || list.Count == 0) return false;
        ChoicePresentation? c = null;
        if (commitBox is not null && IsInstanceValid(commitBox) && commitBox.GetGlobalRect().Grow(40).HasPoint(globalPos))
            // a card that cannot be committed but can be played now (a fast event like 心胜于物 in the window before the test):
            // dropping it on the test panel plays it, as a click does (Yunke 11-18 #18: the drag did nothing)
            c = list.FirstOrDefault(x => x.Title.StartsWith("投入")) ?? list.FirstOrDefault(x => !x.Title.StartsWith("投入"));
        else if (PlayRect().HasPoint(globalPos))
            c = list.FirstOrDefault(x => !x.Title.StartsWith("投入")) ?? (model!.InSkillTest ? list.FirstOrDefault(x => x.Title.StartsWith("投入")) : null);
        if (c is null) return false;
        var pick = c;
        GetTree().CreateTimer(S.Settings.ReducedMotion == true ? 0.01 : 0.16).Timeout += () => Choose(pick);
        return true;
    }

    // ───────────────────────── forced effects resolved automatically: a short corner notice
    // the notices on screen, top to bottom: a new one goes under the last one still showing (survey 11-19: counted slots let a
    // new notice land on one still fading out)
    readonly List<Control> liveNotices = new();
    void ForcedNotice(string why)
    {
        recentNotice = why; recentNoticeAt = Time.GetTicksMsec();
        var box = new PanelContainer { MouseFilter = MouseFilterEnum.Ignore, ZIndex = 120 };
        var sb = new StyleBoxFlat { BgColor = new Color(0.035f, 0.03f, 0.028f, 0.94f), BorderColor = new Color("c0543e"), ContentMarginLeft = 18, ContentMarginRight = 18, ContentMarginTop = 10, ContentMarginBottom = 12 };
        sb.BorderWidthLeft = 3; sb.SetCornerRadiusAll(6); box.AddThemeStyleboxOverride("panel", sb);
        var col = new VBoxContainer { MouseFilter = MouseFilterEnum.Ignore }; col.AddThemeConstantOverride("separation", 2); box.AddChild(col);
        col.AddChild(Style.Text("已自动结算", 16, new Color("d58b72")));
        var l = Style.Text(why, 20, Style.Paper); l.AutowrapMode = TextServer.AutowrapMode.WordSmart; l.CustomMinimumSize = new Vector2(360, 0); col.AddChild(l);
        AddChild(box); box.ResetSize();
        liveNotices.RemoveAll(n => !IsInstanceValid(n) || n.IsQueuedForDeletion());
        while (liveNotices.Count >= 3) { liveNotices[0].QueueFree(); liveNotices.RemoveAt(0); }
        // below the act / agenda buttons on the right (y 92-196): the notice covered the agenda's 撤退 button (playtest 11-18)
        float y = 100; foreach (var n in liveNotices) y = Math.Max(y, n.Position.Y + n.Size.Y + 10);
        box.Position = new Vector2((GetViewportRect().Size.X - box.Size.X) / 2, y);
        liveNotices.Add(box);
        var tw = box.CreateTween(); tw.TweenInterval(3.2); tw.TweenProperty(box, "modulate:a", 0f, 0.5f); tw.TweenCallback(Callable.From(() => { liveNotices.Remove(box); box.QueueFree(); }));
    }

    // ───────────────────────── phase banner
    // As on the html table (Game.vue .phase-notification, Yunke 11-18 #17): a band across the screen in the phase's colour with
    // the phase name glowing on it, about 1.3 s per phase. The band's edges fade to nothing on all four sides (no hard rim). When
    // the core passes several phases at once (enemy → upkeep → mythos → investigation), each phase gets its band in turn, the way
    // the html queue plays them. It shows when the phase has actually changed, before any card drawn in it is turned over.
    string? lastPhase;
    // Completed while no banner is showing; a new banner replaces it. Reveals wait on it so the phase name comes first.
    TaskCompletionSource bannerDone = CompletedTask();
    static TaskCompletionSource CompletedTask() { var t = new TaskCompletionSource(); t.SetResult(); return t; }
    static readonly string[] PhaseCycle = { "MythosPhase", "InvestigationPhase", "EnemyPhase", "UpkeepPhase" };
    static Color PhaseColor(string p) => new(p switch { "MythosPhase" => "7b4b91", "InvestigationPhase" => "a87532", "EnemyPhase" => "9f2929", "UpkeepPhase" => "315b70", _ => "5b5b5b" });

    void PhaseBanner()
    {
        var m = model!; if (lastPhase == m.Phase) return;
        string? was = lastPhase; lastPhase = m.Phase;
        if (was is null || m.Phase is "" or "CampaignPhase") return;
        // the phases passed on the way, in the round's order (the html queue does the same)
        var seq = new List<string>();
        int from = Array.IndexOf(PhaseCycle, was), to = Array.IndexOf(PhaseCycle, m.Phase);
        if (from >= 0 && to >= 0) { for (int i = 1; i <= 4; i++) { var ph = PhaseCycle[(from + i) % 4]; seq.Add(ph); if (ph == m.Phase) break; } }
        else seq.Add(m.Phase);
        // One queue for the whole table (Yunke 11-18: end turn → upkeep → mythos came all at once, and an act advancing at
        // the same time made it a jumble): a new phase waits behind the one playing; the story pages and the drawn cards
        // wait for the whole queue (bannerDone).
        foreach (var ph in seq) { bannerQueue.Enqueue(ph); bannerEnds[ph] = new TaskCompletionSource(); }
        if (bannerRunning) return;
        bannerRunning = true;
        var done = bannerDone = new TaskCompletionSource();
        _ = PlayBanners(done);
    }

    readonly Queue<string> bannerQueue = new();
    bool bannerRunning;

    // the phases between the one on screen and the core's: does the round pass through the upkeep?
    bool PassesUpkeep(TableModel m)
    {
        if (lastPhase is null || lastPhase == m.Phase) return false;
        int from = Array.IndexOf(PhaseCycle, lastPhase), to = Array.IndexOf(PhaseCycle, m.Phase);
        if (from < 0 || to < 0) return false;
        for (int i = 1; i <= 4; i++) { var ph = PhaseCycle[(from + i) % 4]; if (ph == "UpkeepPhase") return true; if (ph == m.Phase) break; }
        return false;
    }

    // while the old table stays under a band, nothing on it may be clicked (it no longer matches the core's state)
    Control? clickHold;
    void HoldClicks(bool on)
    {
        if (clickHold is not null && IsInstanceValid(clickHold)) clickHold.QueueFree(); clickHold = null;
        if (!on) return;
        clickHold = new Control { MouseFilter = MouseFilterEnum.Stop, ZIndex = 105 }; AddChild(clickHold); clickHold.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
    }

    // Yunke 11-20 #16: the investigation band waits until the cards drawn in the mythos phase have been turned over
    TaskCompletionSource? revealHold;

    async Task PlayBanners(TaskCompletionSource done)
    {
        try
        {
            while (bannerQueue.Count > 0 && IsInstanceValid(this))
            {
                var ph = bannerQueue.Dequeue();
                if (ph == "InvestigationPhase" && revealHold is { } hold) await hold.Task;
                await OneBanner(ph);
                if (bannerEnds.Remove(ph, out var end)) end.TrySetResult();
            }
        }
        finally { bannerRunning = false; foreach (var e in bannerEnds.Values) e.TrySetResult(); bannerEnds.Clear(); done.TrySetResult(); }
    }

    // Each phase does its own things in its own time (Yunke 11-19 r9 #11: the upkeep draw flew in while the enemy phase's band
    // was still showing). The state is already worked out; only the showing waits: `a` runs once the given phase's band has
    // gone (or every queued band, when that phase is not queued), at once when none is playing.
    readonly Dictionary<string, TaskCompletionSource> bannerEnds = new();
    void AfterBanner(string? phase, Action a)
    {
        Task gate = phase is not null && bannerEnds.TryGetValue(phase, out var t) ? t.Task : bannerRunning ? bannerDone.Task : Task.CompletedTask;
        if (gate.IsCompleted) { a(); return; }
        _ = Run();
        async Task Run() { await gate; if (IsInstanceValid(this)) a(); }
    }

    Task OneBanner(string phase)
    {
        var finished = new TaskCompletionSource();
        GameAudio.Cue("phase");   // noncore audio 11-19
        var vp = GetViewportRect().Size; float h = Math.Clamp(vp.Y * 0.13f, 96, 150), y = vp.Y * 0.5f - h / 2;   // vertically centred
        var root = new Control { MouseFilter = MouseFilterEnum.Ignore, ZIndex = 110, Position = new Vector2(0, y), Size = new Vector2(vp.X, h) };
        // the band: the phase colour mixed into near-black (html: 54% phase colour over rgba(8,11,13,.88)), every edge faded
        var c = PhaseColor(phase); var dark = new Color(0.03f, 0.043f, 0.05f);
        var band = new ColorRect { Color = new Color(dark.Lerp(c, 0.54f), 0.92f), MouseFilter = MouseFilterEnum.Ignore, Size = root.Size, Material = Ui.EdgeAlpha(Vector2.Zero, root.Size, vp.X * 0.22f, h * 0.38f, vp.X * 0.22f, h * 0.38f) };
        root.AddChild(band);
        var t = Style.Text(PhaseName(phase), (int)Math.Clamp(h * 0.46f, 44, 66), new Color(0.95f, 0.91f, 0.81f, 0.95f), Style.Kai);
        t.HorizontalAlignment = HorizontalAlignment.Center; t.VerticalAlignment = VerticalAlignment.Center; t.Size = root.Size;
        // the title glows in the phase colour (html text-shadow 0 0 35px phase colour)
        t.AddThemeColorOverride("font_shadow_color", new Color(c, 0.85f)); t.AddThemeConstantOverride("shadow_outline_size", 18); t.AddThemeConstantOverride("shadow_offset_y", 0);
        t.AddThemeConstantOverride("outline_size", 4); t.AddThemeColorOverride("font_outline_color", new Color(0, 0, 0, 0.55f));
        root.AddChild(t);
        AddChild(root);
        if (S.Settings.ReducedMotion == true) { GetTree().CreateTimer(1.0).Timeout += () => { if (IsInstanceValid(root)) root.QueueFree(); finished.TrySetResult(); }; return finished.Task; }
        root.Modulate = new Color(1, 1, 1, 0); t.Scale = new Vector2(0.96f, 0.96f); t.PivotOffset = root.Size / 2;
        var tw = root.CreateTween();
        tw.TweenProperty(root, "modulate:a", 1f, 0.22f).SetTrans(Tween.TransitionType.Sine).SetEase(Tween.EaseType.Out);
        tw.Parallel().TweenProperty(t, "scale", Vector2.One, 1.0f).SetTrans(Tween.TransitionType.Sine).SetEase(Tween.EaseType.Out);
        tw.TweenInterval(0.95);
        tw.TweenProperty(root, "modulate:a", 0f, 0.33f).SetTrans(Tween.TransitionType.Sine).SetEase(Tween.EaseType.In);
        tw.TweenCallback(Callable.From(() => { if (IsInstanceValid(root)) root.QueueFree(); finished.TrySetResult(); }));
        return finished.Task;
    }

    // ───────────────────────── help page ("?")
    void OpenHelp()
    {
        var col = new VBoxContainer { CustomMinimumSize = new Vector2(760, 0) }; col.AddThemeConstantOverride("separation", 12);
        col.AddChild(Style.Title("操作说明", 36));
        foreach (var (k, v) in new[] {
            ("打出手牌", "把手牌拖到牌桌中央松开；拖到检定框是投入检定。拖到别处会弹回。"),
            ("场景操作", "点击高亮的地点（调查）、出口（移动）、敌人（攻击、躲避）。有多个选择时会就地弹出小菜单。"),
            ("看卡", "鼠标在卡牌、敌人、地点上停留一秒，会显示文字说明；右键打开大图阅读。"),
            ("下一步", "手牌上方的提示条告诉你现在该做什么；不在场上的操作（拿资源、抽牌等）也在那里。"),
            ("自动结算", "只有一个、无需你决定的强制效果会自动结算，并在右上角提示；可以撤回。"),
            ("快捷键", "Esc 菜单 · Tab 打开/收起边栏（调查记录） · M 地图全屏/回到牌桌 · Home 看全图 · F 指出现在能点的地方 · D 抽遭遇牌 · U / Ctrl+Z 撤回一步"),
        })
        {
            var h = new HBoxContainer(); h.AddThemeConstantOverride("separation", 18);
            var kl = Style.Text(k, 22, Style.Amber, Style.Kai); kl.CustomMinimumSize = new Vector2(120, 0); h.AddChild(kl);
            var vl = Style.Text(v, 20, Style.Paper); vl.AutowrapMode = TextServer.AutowrapMode.WordSmart; vl.CustomMinimumSize = new Vector2(600, 0); h.AddChild(vl);
            col.AddChild(h);
        }
        Control scrim = null!; col.AddChild(Ui.Button("知道了", () => App.I.CloseModal(scrim), 22, true));
        scrim = App.I.Modal(Ui.Panel(col, 34));
    }
    // The card a question comes from or is about: a label naming a card ($label.addCardToDeck __name=s:"02140": 死灵之书), or
    // the question's own source (QuestionWithSource: the card whose ability or effect asks). Null when it is none of these.
    string? QuestionSubject(TableModel m)
    {
        for (var w = m.State.Question; J.Tag(w) is "QuestionLabel" or "PayCostQuestion" or "QuestionWithSource"; w = J.Get(w, "question"))
        {
            if (J.Tag(w) == "QuestionLabel" && System.Text.RegularExpressions.Regex.Match(J.String(w, "label"), @"__name=s:""(\d{5}[ab]?)""") is { Success: true } nm)
                return CoreCatalog.Normalize(nm.Groups[1].Value);
            if (J.Tag(w) == "QuestionWithSource" && ModSourceCode(m, J.Get(w, "source")) is { Length: > 0 } sc
                && J.String(S.Card(sc), "type_code") is not ("investigator" or "location" or "scenario" or "act" or "agenda" or ""))
                return sc;
        }
        // the choices' own labels ("不将“死灵之书”加入牌组")
        foreach (var c in m.Question.Choices)
            if (System.Text.RegularExpressions.Regex.Match(J.String(c.Raw, "label"), @"__name=s:""(\d{5}[ab]?)""") is { Success: true } nm2) return CoreCatalog.Normalize(nm2.Groups[1].Value);
        return null;
    }

    // the cards the core holds up while it waits on a lone "继续" (a "look at" effect), or on plain labelled choices about them
    // (秘密卷轴: 弃牌 / 加入手牌 / 放置在牌组底部 / 放置在牌组顶部 — QQ #35: the card itself was not shown, only the log named
    // it; the html version shows the focused cards with the choices, ChoiceModal.vue), by code; empty otherwise
    static List<string> LookCards(TableModel m)
    {
        var on = m.Question.Choices.Where(c => c.Enabled).ToList();
        bool lone = on.Count == 1 && on[0].Tag == "Label" && J.String(on[0].Raw, "label") == "$label.continue";
        bool labels = !lone && on.Count >= 2 && !m.InSkillTest && on.All(c => c.Tag == "Label" && c.CardCode.Length == 0);
        if (!lone && !labels) return new();
        var codes = new List<string>();
        foreach (var group in AutoPolicy.LookedAt(m))
            foreach (var c in group.ValueKind == System.Text.Json.JsonValueKind.Array ? J.Items(group) : new[] { group })
                if (J.String(J.Get(c, "contents"), "cardCode") is { Length: > 0 } code) codes.Add(CoreCatalog.Normalize(code));
        return codes;
    }


    // "检定敏捷(3)" → the agility icon, as the cards print it (Yunke 11-22: "这里的 敏捷 战力 要用符号表示"); null when the text
    // names no skill test
    static readonly System.Text.RegularExpressions.Regex SkillWord = new(@"(意志|智力|战力|敏捷)\s*([（(]\s*(?:\d+|X)\s*[)）])");
    static string? SkillIconText(string t)
    {
        if (!SkillWord.IsMatch(t)) return null;
        return SkillWord.Replace(t, mm => "[" + (mm.Groups[1].Value switch { "意志" => "willpower", "智力" => "intellect", "战力" => "combat", _ => "agility" }) + "]" + mm.Groups[2].Value.Replace("（", "(").Replace("）", ")").Replace(" ", ""));
    }
    // a button whose words name a skill test: its text drawn again with the skill's icon (the button keeps its size and click)
    void SkillIconFace(Button b, int size)
    {
        if (SkillIconText(b.Text) is not { } marked) return;
        var r = CardReader.Rules(marked, size); r.HorizontalAlignment = HorizontalAlignment.Center; r.VerticalAlignment = VerticalAlignment.Center;
        if (Style.Kai is not null) r.AddThemeFontOverride("normal_font", Style.Kai);
        r.AddThemeColorOverride("default_color", b.GetThemeColor("font_color"));
        string plain = b.Text; b.Text = "";
        b.AddChild(r); r.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect); r.OffsetLeft = 22; r.OffsetRight = -22; r.OffsetTop = 8; r.OffsetBottom = -8;
        // tall enough for the wrapped words
        var font = Style.Kai ?? b.GetThemeFont("font"); float w = Math.Max(200, b.CustomMinimumSize.X - 44);
        float h = font.GetMultilineStringSize(plain, HorizontalAlignment.Left, w, size).Y;
        b.CustomMinimumSize = new Vector2(b.CustomMinimumSize.X, Math.Max(b.CustomMinimumSize.Y, h + 30));
    }
}
