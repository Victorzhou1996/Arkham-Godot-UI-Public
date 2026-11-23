using Godot;
using Gathering;
using System.Text.Json;

namespace ArkhamUI;

public partial class TableScreen
{
    // ───────────────────────── right action window
    void ActionPanel()
    {
        var m = model!;
        var col = new VBoxContainer(); col.AddThemeConstantOverride("separation", 10);
        var box = Ui.Panel(col, 22, 0.86f);
        Place(box, -420, 100, -18, 100, 1, 0, 1, 0);
        actionPanelRef = box;
        var title = Style.Text(m.QuestionTag == "" ? (m.IsOver ? "调查结束" : "等待其他调查员…") : m.Question.Title, 26, Style.HoverText, Style.Kai);
        title.AutowrapMode = TextServer.AutowrapMode.WordSmart; col.AddChild(title);
        if (m.Me is not null && m.Phase == "InvestigationPhase")
        {
            var who = Style.Text(m.Name(m.Me) + " · 剩余行动 " + m.Actions(m.Me), 20, Style.Brass); col.AddChild(who);
        }
        if (m.Question.Body.Length > 0 && m.QuestionTag != "Read")
        {
            var body = CardReader.Rules(m.Question.Body, 20); col.AddChild(body);
        }
        if (AutoPolicy.NeedsOrdering(m)) col.AddChild(Hint("有多个效果同时触发：请选择先结算哪一个。"));
        var list = new VBoxContainer(); list.AddThemeConstantOverride("separation", 8);
        if (m.Question.Choices.Count > 6) { var scroll = Ui.Scroll(list); scroll.CustomMinimumSize = new Vector2(0, 380); col.AddChild(scroll); } else col.AddChild(list);
        ChoicePresentation? endTurn = null;
        foreach (var c in m.Question.Choices)
        {
            if (c.Tag == "EndTurnButton") { endTurn = c; continue; }   // shown bottom-right in the side zone
            list.AddChild(ChoiceButton(c));
        }
        if (m.Question.Choices.Count == 0 && m.QuestionTag != "") list.AddChild(Hint("这个问题需要在弹出的窗口中作答。"));

        if (autoLog.Count > 0)
        {
            var a = Style.Text("已自动处理：" + System.Text.RegularExpressions.Regex.Replace(autoLog.Last(), @"（[^）]*）", ""), 16, new Color(Style.PaperDim, 0.8f));
            a.AutowrapMode = TextServer.AutowrapMode.WordSmart; col.AddChild(a);
        }
        if (m.QuestionTag is "Read" or "ChooseAmounts" or "ChoosePaymentAmounts" or "ChooseOneWizard" or "DropDown" or "PickDestiny" or "PickCampaignSpecific")
            col.AddChild(Ui.Button("打开当前选择", () => { if (m.QuestionTag == "PickCampaignSpecific") { CloseWorldMap(); worldMapSeq = -1; } QuestionModal(); }, 22, true));
    }

    Label Hint(string text) { var l = Style.Text(text, 19, Style.Amber); l.AutowrapMode = TextServer.AutowrapMode.WordSmart; return l; }

    Control ChoiceButton(ChoicePresentation c)
    {
        if (c.Tag == "SkipTriggersButton" && model is not null) return SkipButton(c);
        return PlainChoiceButton(c);
    }

    // "继续 · 跳过可选响应" names the cards that are asking instead of "可选" (Yunke 11-18 r7), each name in its class
    // colour as the side panel shows it; under it, a switch per asking ability: 总是触发 / 只在自己回合 / 停止触发.
    Control SkipButton(ChoicePresentation c)
    {
        var m = model!;
        var askers = m.Question.Choices.Where(x => x.Enabled && x.Tag != "SkipTriggersButton" && x.CardCode.Length > 0)
            .Select(x => CoreCatalog.Normalize(x.CardCode)).Distinct().ToList();
        var b = (Button)PlainChoiceButton(c);
        if (askers.Count > 0)
        {
            string plain = "继续 · 跳过" + string.Join("、", askers.Select(S.Name)) + "的响应";
            // one line, as wide as its words: the hidden plain copy must not wrap (in a narrow column it wrapped a character per
            // line and stood as a tall empty bar over the map, the coloured line above it unwrapped)
            b.AutowrapMode = TextServer.AutowrapMode.Off;
            b.Text = plain; b.AddThemeColorOverride("font_color", new Color(0, 0, 0, 0)); b.AddThemeColorOverride("font_hover_color", new Color(0, 0, 0, 0)); b.AddThemeColorOverride("font_pressed_color", new Color(0, 0, 0, 0));
            string Esc(string t) => t.Replace("[", "[lb]");
            var rich = new RichTextLabel { BbcodeEnabled = true, FitContent = true, ScrollActive = false, AutowrapMode = TextServer.AutowrapMode.Off, MouseFilter = MouseFilterEnum.Ignore };
            rich.AddThemeFontSizeOverride("normal_font_size", 21); if (b.GetThemeFont("font") is Font f) rich.AddThemeFontOverride("normal_font", f);
            rich.AddThemeColorOverride("default_color", Style.Paper);
            rich.Text = "继续 · 跳过" + string.Join("、", askers.Select(code => "[color=#" + SelectionStage.FactionColor(code).Lightened(0.4f).ToHtml(false) + "]" + Esc(S.Name(code)) + "[/color]")) + "的响应";
            b.AddChild(rich); rich.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect); rich.OffsetLeft = 48; rich.OffsetTop = 14;
        }
        var offered = TriggerModes.Offered(m);
        if (offered.Count == 0) return b;
        var col = new VBoxContainer { MouseFilter = MouseFilterEnum.Ignore }; col.AddThemeConstantOverride("separation", 6); col.AddChild(b);
        var row = new HBoxContainer { MouseFilter = MouseFilterEnum.Ignore, Alignment = BoxContainer.AlignmentMode.Center }; row.AddThemeConstantOverride("separation", 8); col.AddChild(row);
        foreach (var (inv, card, index) in offered) row.AddChild(ModeSwitch(m, inv, card, index, true));
        return col;
    }

    // One ability's trigger switch: "<card>：<mode>"; a click steps to the next mode and tells the rules core.
    // The trigger mode as one small coloured square with one character (Yunke 11-19 r9 #10: the words were too much); the
    // hover box says the mode in full. Click: 总是触发 → 只在自己回合 → 停止触发. withName: the card's name beside it.
    Control ModeSwitch(TableModel m, string inv, string card, int index, bool withName)
    {
        string mode = TriggerModes.Mode(m, inv, card, index), code = CoreCatalog.Normalize(card);
        var (ch, fill) = mode switch
        {
            TriggerModes.OwnerOnly => ("己", new Color(0.2f, 0.36f, 0.62f)),
            TriggerModes.Off => ("停", new Color(0.32f, 0.3f, 0.28f)),
            _ => ("常", new Color(0.72f, 0.5f, 0.16f)),   // 总是触发 (Yunke 11-20 #7: "常", not "触")
        };
        float d = withName ? 28 : 22;
        var sq = new Button { Text = ch, FocusMode = FocusModeEnum.None, MouseDefaultCursorShape = CursorShape.PointingHand, CustomMinimumSize = new Vector2(d, d),
            TooltipText = S.Name(code) + "：" + TriggerModes.Name(mode) + "\n点击切换（总是触发 → 只在自己回合 → 停止触发）" };
        foreach (var (st, k) in new[] { ("normal", 0f), ("hover", 0.18f), ("pressed", 0.3f) })
        {
            var sb = new StyleBoxFlat { BgColor = fill.Lightened(k), BorderColor = new Color(1, 1, 1, 0.35f), AntiAliasing = true, ContentMarginLeft = 0, ContentMarginRight = 0, ContentMarginTop = 0, ContentMarginBottom = 0 };
            sb.SetBorderWidthAll(1); sb.SetCornerRadiusAll(5); sq.AddThemeStyleboxOverride(st, sb);
        }
        sq.AddThemeStyleboxOverride("focus", new StyleBoxEmpty());
        sq.AddThemeFontSizeOverride("font_size", (int)(d * 0.6f)); if (Style.Kai is not null) sq.AddThemeFontOverride("font", Style.Kai);
        sq.AddThemeColorOverride("font_color", Colors.White); sq.AddThemeColorOverride("font_hover_color", Colors.White);
        sq.Pressed += () => { Sfx.Click(); _ = SetTriggerMode(inv, card, index, TriggerModes.Next(mode)); };
        if (!withName) return sq;
        var row = new HBoxContainer { MouseFilter = MouseFilterEnum.Ignore }; row.AddThemeConstantOverride("separation", 6);
        var nm = Style.Text(S.Name(code), 17, Style.PaperDim, Style.Kai); nm.VerticalAlignment = VerticalAlignment.Center; row.AddChild(nm); row.AddChild(sq);
        return row;
    }

    async Task SetTriggerMode(string inv, string card, int index, string mode)
    {
        if (busy || state is null || model is null) return;
        // kept by the table (TriggerModes); the core is only told when an older save still holds a mode there
        TriggerModes.Set(gameId, inv, card, index, mode);
        if (TriggerModes.HeldByCore(model, inv, card, index))
            await Busy("切换触发模式…", () => ResetCoreTriggerMode(inv, card, index));
        App.I.Toast(S.Name(CoreCatalog.Normalize(card)) + "：" + TriggerModes.Name(mode));
        await Advance();
    }

    // Put one ability back to 总是触发 in the rules core. The core takes a trigger preference only from the seat of the
    // investigator it belongs to (QA tm3: "belongs to another seat"), so that seat answers it and the table returns to its seat.
    async Task ResetCoreTriggerMode(string inv, string card, int index)
    {
        var seatOf = model?.Investigators.FirstOrDefault(i => i.Id == inv) is { } e && Guid.TryParse(J.String(e.Raw, "playerId"), out var g) ? g : state!.Player;
        var back = state!.Player;
        var st = seatOf == back ? state : await B.Open(gameId, seatOf);
        st = await B.Answer(st, TriggerModes.Answer(inv, card, index, TriggerModes.Always));
        state = seatOf == back ? st : await B.Open(gameId, back);
    }

    // bug #10: the investigators of this player's seats whose core setting is not what 检定中也可使用闪电能力 says
    int fastTries; bool fastWas = true;
    // the current rules core (b25aded6) refuses the setting ("Raw messages are not a public operation"): the switch and this
    // step wait for a core that takes it (rules-work/SYNC-b25aded6.md, next core build)
    public static bool CoreTakesFastSetting = false;
    List<Entity> FastSettingDiffers()
    {
        if (!CoreTakesFastSetting) return new();
        if (fastWas != S.Settings.FastInSkillTests) { fastWas = S.Settings.FastInSkillTests; fastTries = 0; }
        bool ignore = !S.Settings.FastInSkillTests;
        return model!.Investigators.Where(i => Guid.TryParse(J.String(i.Raw, "playerId"), out var p) && OnlineGame.Mine(p)
            && J.Get(J.Get(J.Get(i.Raw, "settings"), "globalSettings"), "ignoreUnrelatedSkillTestTriggers") is { ValueKind: JsonValueKind.True or JsonValueKind.False } v
            && (v.ValueKind == JsonValueKind.True) != ignore).ToList();
    }
    async Task SetCoreFastInTests(Entity inv)
    {
        var seatOf = Guid.TryParse(J.String(inv.Raw, "playerId"), out var g) ? g : state!.Player;
        var back = state!.Player;
        var st = seatOf == back ? state : await B.Open(gameId, seatOf);
        var msg = JsonSerializer.SerializeToElement(new { tag = "Raw", contents = new { tag = "UpdateGlobalSetting", contents = new object[] { inv.Id, new { tag = "SetIgnoreUnrelatedSkillTestTriggers", contents = !S.Settings.FastInSkillTests } } } });
        st = await B.Answer(st, msg);
        state = seatOf == back ? st : await B.Open(gameId, back);
    }

    Control PlainChoiceButton(ChoicePresentation c)
    {
        var m = model!; var anchor = m.Anchor(c);
        string glyph = m.ActionVerb(c) is string v && v.Length > 0 ? "action" : c.Tag switch { "TargetLabel" when anchor.StartsWith("hand:") => "action", "SkipTriggersButton" => "reaction", _ => "" };   // skipping responses: the reaction symbol (Yunke 11-20 #7)
        var abilityType = c.Tag == "AbilityLabel" ? J.Tag(J.Get(J.Get(c.Raw, "ability"), "type")) : "";
        if (abilityType == "ReactionAbility") glyph = "reaction"; else if (abilityType == "FastAbility") glyph = "fast";
        var b = new Button { Text = c.Title + (anchor.Length > 0 ? "  ›" : ""), FocusMode = FocusModeEnum.None, Disabled = !c.Enabled, MouseDefaultCursorShape = CursorShape.PointingHand, TooltipText = c.Hint, Alignment = HorizontalAlignment.Left, AutowrapMode = TextServer.AutowrapMode.WordSmart, CustomMinimumSize = new Vector2(0, 56) };
        b.AddThemeFontSizeOverride("font_size", 21);
        foreach (var (state, bg, line) in new[] { ("normal", 0.45f, 0), ("hover", 0.72f, 2), ("pressed", 0.82f, 2), ("disabled", 0.22f, 0) })
        {
            // the same soft-edged backdrop as every other button (Ui.SoftBox)
            var sb = Ui.SoftBox(bg, Style.Amber, line, 16, 10); sb.ContentMarginLeft = glyph.Length > 0 ? 48 : 16; sb.ContentMarginRight = 14;
            b.AddThemeStyleboxOverride(state, sb);
        }
        // which skill to test with (心胜于物: 敏捷 or 智力): the skill's own icon in its colour instead of the word (Yunke 11-18 #18)
        string skill = c.Tag is "SkillLabel" or "SkillLabelWithLabel" ? J.String(c.Raw, "skillType").Replace("Skill", "").ToLowerInvariant() : "";
        if (skill is "willpower" or "intellect" or "combat" or "agility")
        {
            var color = skill switch { "willpower" => Style.Willpower, "intellect" => Style.Intellect, "combat" => Style.Combat, _ => Style.Agility };
            b.Text = c.Tag == "SkillLabelWithLabel" ? "     " + c.Title : ""; b.TooltipText = c.Title; b.CustomMinimumSize = new Vector2(c.Tag == "SkillLabelWithLabel" ? 0 : 96, 56);
            var g = Ui.Glyph(skill, 34, color); g.AddThemeConstantOverride("outline_size", 5); g.AddThemeColorOverride("font_outline_color", new Color(0, 0, 0, 0.8f)); g.MouseFilter = MouseFilterEnum.Ignore;
            b.AddChild(g); g.SetAnchorsAndOffsetsPreset(c.Tag == "SkillLabelWithLabel" ? LayoutPreset.LeftWide : LayoutPreset.FullRect); if (c.Tag == "SkillLabelWithLabel") g.OffsetLeft = 14;
            g.HorizontalAlignment = c.Tag == "SkillLabelWithLabel" ? HorizontalAlignment.Left : HorizontalAlignment.Center; g.VerticalAlignment = VerticalAlignment.Center;
        }
        else if (glyph.Length > 0) { var g = Ui.Glyph(glyph, 24, Style.Amber); b.AddChild(g); g.AnchorTop = g.AnchorBottom = 0.5f; g.OffsetLeft = 14; g.OffsetTop = -16; g.OffsetBottom = 16; g.VerticalAlignment = VerticalAlignment.Center; }
        b.Pressed += () => Choose(c);
        // the hover arrow only when the element is somewhere else on the table; next to the button it just looks odd
        b.MouseEntered += () => { if (anchors.TryGetValue(anchor, out var n) && IsInstanceValid(n) && n.GetGlobalRect().GetCenter().DistanceTo(b.GetGlobalRect().GetCenter()) > 260) arrows.Point(b, n); };
        b.MouseExited += () => arrows.Clear();
        // an ability: hovering the button shows that very ability in the card's words (two "攻击 · 刀子" are told apart by it)
        if (c.Tag == "AbilityLabel" && ChoiceAbility(c) is { } ca)
        { b.MouseEntered += () => Peek.Schedule(b.GetParent() as Control ?? b, () => AbilityText.BoxOf(ca.Title, ca.Line), 0.25); b.MouseExited += Peek.Hide; }   // the box goes above the whole list, so it covers no other choice
        return b;
    }

    // ───────────────────────── skill test
    // The test panel never covers the choices under it (Yunke 11-19 r9 #21: an act advancing or the scenario ending during a
    // test put its choices under the panel): once laid out, a panel reaching down to the choices row moves up, and if it still
    // would not fit, it is drawn smaller.
    Control? testBox, stepRow;
    void FitTestPanel()
    {
        if (testBox is null || stepRow is null || !IsInstanceValid(testBox) || !IsInstanceValid(stepRow)) return;
        if (stepRow.FindChildren("*", "Button", true, false).Count == 0) return;
        var r = testBox.GetGlobalRect(); float rowTop = stepRow.GetGlobalRect().Position.Y;
        float over = r.End.Y - (rowTop - 14); if (over <= 0) return;
        float up = Math.Min(over, Math.Max(0, r.Position.Y - 92));
        testBox.Position -= new Vector2(0, up); over -= up;
        if (over > 0) { float k = Math.Max(0.55f, (r.Size.Y - over) / r.Size.Y); testBox.PivotOffset = new Vector2(r.Size.X / 2, 0); testBox.Scale = new Vector2(k, k); }
    }

    // Panels the player may drag aside (Yunke 11-20: the test panel sometimes covers what one needs to see). The shift is kept
    // per kind of panel, so the panel rebuilt by the next render stays where it was put.
    static readonly Dictionary<string, Vector2> panelShift = new();
    void Draggable(string kind, Control handle, params Control[] moved)
    {
        if (panelShift.TryGetValue(kind, out var off)) foreach (var c in moved) c.Position += off;
        if (handle.MouseFilter == MouseFilterEnum.Ignore) handle.MouseFilter = MouseFilterEnum.Stop;
        handle.MouseDefaultCursorShape = CursorShape.Move;
        bool drag = false; Vector2 last = Vector2.Zero;
        handle.GuiInput += e =>
        {
            if (e is InputEventMouseButton { ButtonIndex: MouseButton.Left } mb) { drag = mb.Pressed; last = mb.GlobalPosition; handle.AcceptEvent(); }
            else if (e is InputEventMouseMotion mm && drag)
            {
                var d = mm.GlobalPosition - last; last = mm.GlobalPosition;
                foreach (var c in moved) if (IsInstanceValid(c)) c.Position += d;
                panelShift[kind] = panelShift.GetValueOrDefault(kind) + d; handle.AcceptEvent();
            }
        };
    }

    HBoxContainer? testCards;   // the cards the test is about, built before the title line and set into it
    void SkillTestPanel()
    {
        var m = model!; var st = m.SkillTest;
        var col = new VBoxContainer(); col.AddThemeConstantOverride("separation", 10);
        var box = Ui.Panel(col, 24, 0.96f);
        // wider than before, and the cards the test is about stand at its top, in the middle (Yunke 11-20: "所有的检定时，鉴定框里都要
        // 有正在进行检定的相关卡牌。放在顶部中央")
        var about = TestCards(m, st);
        Place(box, -460, about.Count > 0 ? 150 : 262, 460, about.Count > 0 ? 150 : 262, 0.5f, 0, 0.5f, 0);   // as tall as its content
        if (about.Count > 0)
        {
            var cardsRow = new HBoxContainer { Alignment = BoxContainer.AlignmentMode.Center, MouseFilter = MouseFilterEnum.Ignore }; cardsRow.AddThemeConstantOverride("separation", 16);
            foreach (var code in about)
            {
                var sz = new Vector2(150, 210);
                bool landscape = J.String(S.Card(code), "type_code") is "act" or "agenda";
                if (landscape) sz = new Vector2(210, 150);
                // the printed card or the small redrawn card, as the hover mode is set (原图模式 / 文字模式, Yunke 11-20)
                bool printed = Peek.ImageMode;
                var cv = new CardView { Code = code, Redrawn = !printed, CustomMinimumSize = sz, Size = sz, ShowCost = false, HoverZoom = true };
                var cc = code; cv.OnClick = () => CardReader.Open(cc, printed: true);
                // it hangs 36 px below the title line (Yunke 11-20: "中间的卡牌再下移一点"): the holder is that much shorter
                const float drop = 36;
                var hold = new Control { CustomMinimumSize = new Vector2(sz.X, sz.Y - drop), MouseFilter = MouseFilterEnum.Ignore, SizeFlagsVertical = SizeFlags.ShrinkEnd };
                hold.AddChild(cv); cv.Position = new Vector2(0, 0);
                cardsRow.AddChild(hold);
            }
            testCards = cardsRow;
        }
        box.ZIndex = 35;   // above the map frame's dark edge and push-in caption (they covered "检定")
        testBox = box;
        Draggable("test", box, box);   // it can be moved off what it covers (Yunke 11-20)
        var skills = J.Items(J.Get(st, "skills")).Select(s => (s.GetString() ?? "").Replace("Skill", "").ToLowerInvariant()).ToArray();
        var head = new HBoxContainer(); head.AddThemeConstantOverride("separation", 10); col.AddChild(head);
        var title = Style.Text("技能检定", 28, Style.HoverText, Style.Kai); title.SizeFlagsVertical = SizeFlags.ShrinkEnd; head.AddChild(title);
        head.AddChild(Ui.Spacer(0, 0, true));
        // the test's cards in the middle of the title line, their foot level with the foot of "技能检定" (Yunke 11-20)
        if (testCards is not null) { head.AddChild(testCards); head.AddChild(Ui.Spacer(0, 0, true)); }
        int value = J.Int(st, "modifiedSkillValue"), diff = J.Int(st, "modifiedDifficulty");
        var res0 = J.Get(st, "result"); string rtag = J.Tag(res0);
        if (rtag is "SucceededBy" or "FailedBy")
        {
            var arr = J.Get(res0, "contents"); int by = arr.ValueKind == System.Text.Json.JsonValueKind.Array && arr.GetArrayLength() == 2 && arr[1].ValueKind == System.Text.Json.JsonValueKind.Number ? arr[1].GetInt32() : 0;
            head.AddChild(Compare(diff, Math.Max(0, rtag == "SucceededBy" ? diff + by : diff - by), 0, skills));
        }
        else
        {
            // the value before any card was committed and what the committed cards add (Codex review: compare, pick cards,
            // confirm); the core's own total decides, the split is what it was before the first commit
            int nCommitted = J.Props(J.Get(st, "committedCards")).SelectMany(p => J.Items(p.Value)).Count();
            string tkey = J.String(st, "investigator") + "|" + J.String(st, "action") + "|" + J.Get(st, "source").GetRawText() + "|" + diff;
            if (nCommitted == 0) testBase[tkey] = value;
            int plus = nCommitted > 0 && testBase.TryGetValue(tkey, out int b0) ? value - b0 : 0;
            head.AddChild(Compare(diff, value, plus, skills));
        }
        if (testCards is not null && head.GetChildCount() > 0 && head.GetChild(head.GetChildCount() - 1) is Control cmp)
        {
            cmp.SizeFlagsVertical = SizeFlags.ShrinkEnd;
            // the two sides as wide as each other, so the cards stand in the true middle
            void Even() { if (!IsInstanceValid(title) || !IsInstanceValid(cmp)) return; float w = Math.Max(title.Size.X, cmp.Size.X); title.CustomMinimumSize = new Vector2(w, 0); cmp.CustomMinimumSize = new Vector2(w, cmp.CustomMinimumSize.Y); }
            Callable.From(Even).CallDeferred();
        }
        testCards = null;
        var action = J.String(st, "action");
        var stage = StageHint(m, st);
        col.AddChild(Style.Text((action switch { "Investigate" => "调查", "Fight" => "攻击", "Evade" => "躲避", "Parley" => "谈判", _ => SourceName(m, st) }) + (stage.Length > 0 ? "　·　" + stage : ""), 19, Style.PaperDim));
        // the chaos bag as it stands, and the chance to pass with what is committed now (before the draw only)
        col.AddChild(BagRow(m, value, diff, J.Tag(J.Get(st, "result")) == "Unrun"));
        if (action == "Fight") col.AddChild(DamageRow(m, st));
        // committed cards fan out like the hand (same arc, tilt and spacing), centred in the box
        // tall enough for the committed cards with room around them (Yunke 11-18: the box was a little low)
        var commit = new Control { CustomMinimumSize = new Vector2(720, 180), MouseFilter = MouseFilterEnum.Ignore };
        var commitArea = new PanelContainer(); var csb = new StyleBoxFlat { BgColor = new Color(0, 0, 0, 0.35f), BorderColor = new Color(Style.Amber, 0.45f), ContentMarginLeft = 10, ContentMarginRight = 10, ContentMarginTop = 8, ContentMarginBottom = 8 }; csb.SetBorderWidthAll(1); csb.SetCornerRadiusAll(8);
        commitArea.AddThemeStyleboxOverride("panel", csb);
        commitArea.AddChild(commit); col.AddChild(commitArea); commitBox = commitArea;
        var committed = J.Props(J.Get(st, "committedCards")).SelectMany(p => J.Items(p.Value)).Select(c => J.Get(c, "contents")).ToArray();
        // upright, side by side, centred in the box as wide as it really is (Yunke 11-21: they sat left of the middle — the
        // box grows to the panel's width, the cards were placed for a narrower one)
        var crow = new HBoxContainer { Alignment = BoxContainer.AlignmentMode.Center, MouseFilter = MouseFilterEnum.Ignore };
        crow.AddThemeConstantOverride("separation", 12);
        commit.AddChild(crow); crow.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect); crow.OffsetTop = 14;
        for (int i = 0; i < committed.Length; i++)
        {
            var c = committed[i];
            string id = J.String(c, "id"), code = CoreCatalog.Normalize(J.String(c, "cardCode"));
            var cv = Card(code, "hand:" + id, new Vector2(104, 150));
            cv.SizeFlagsVertical = SizeFlags.ShrinkBegin; cv.SizeFlagsHorizontal = SizeFlags.ShrinkCenter;
            crow.AddChild(cv);
        }
        if (committed.Length == 0 && J.Tag(J.Get(st, "result")) != "Unrun") commitArea.Visible = false;
        else if (committed.Length == 0)
        {
            // nothing committed yet: a slim strip instead of an empty frame; gone when no hand card can be committed at all
            bool canCommit = m.Question.Choices.Any(c => c.Enabled && c.Title.StartsWith("投入"));
            bool later = !canCommit && PreCommitWindow(m) && m.Me is { } me0 && m.Hand(me0).Any(h => CommitLater(m, h.Code));
            // the box is there even with nothing to commit (Yunke 11-20: the moment of the test, and its odds, are seen), saying so
            {
                commit.CustomMinimumSize = new Vector2(720, 110);   // an open space to drop cards into, not a thin strip
                string say = canCommit ? "拖入手牌投入" : later ? "拖入手牌投入（会先跳过上面的能力，再投入）" : "没有可以投入这次检定的牌";
                var hint = Style.Text(say, 18, new Color(Style.PaperDim, 0.8f), Style.Kai); hint.HorizontalAlignment = HorizontalAlignment.Center; hint.VerticalAlignment = VerticalAlignment.Center;
                hint.MouseFilter = MouseFilterEnum.Ignore; commit.AddChild(hint); hint.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
            }
        }
        // cards that may be committed from somewhere other than the hand (梦境结晶器's attached events "as if they were in your
        // hand", QQ 11-23: "梦境结晶器下压的牌不能投入到技能检定中"): the core offers them, but only hand cards could be dragged in —
        // they are shown here as small cards, a click commits
        var handIds = m.Me is { } meH ? m.Hand(meH).Select(h => "hand:" + h.Id).ToHashSet() : new HashSet<string>();
        var elsewhere = m.Question.Choices.Where(c => c.Enabled && c.Title.StartsWith("投入") && c.CardCode.Length > 0 && !handIds.Contains(m.Anchor(c))).ToList();
        if (CommitFromElsewhere && elsewhere.Count > 0)
        {
            var erow = new HBoxContainer { Alignment = BoxContainer.AlignmentMode.Center }; erow.AddThemeConstantOverride("separation", 10); col.AddChild(erow);
            var el = Style.Text("其他可投入的卡（点击投入）", 17, Style.PaperDim, Style.Kai); el.SizeFlagsVertical = SizeFlags.ShrinkCenter; erow.AddChild(el);
            foreach (var ec in elsewhere)
            {
                var pick = ec; var size = new Vector2(84, 118);
                var ev = new CardView { Code = CoreCatalog.Normalize(ec.CardCode), CustomMinimumSize = size, Size = size, HoverZoom = true, Actionable = true, TooltipText = ec.Title };
                ev.OnClick = () => Choose(pick);
                erow.AddChild(ev);
            }
            if (App.I.QaRunning) GD.Print("QA_COMMIT_ELSEWHERE " + string.Join(",", elsewhere.Select(x => x.CardCode)));
            QaScript.Register("commit-elsewhere", a => { var hit = elsewhere.FirstOrDefault(x => CoreCatalog.Normalize(x.CardCode) == a) ?? elsewhere.FirstOrDefault(); if (hit is not null) Choose(hit); });
        }
        var tokens = m.RevealedTokens.ToArray();
        var tokRow = new HBoxContainer { Alignment = BoxContainer.AlignmentMode.Center }; tokRow.AddThemeConstantOverride("separation", 14); col.AddChild(tokRow);
        string sig = string.Join(",", tokens);
        // A token put back into the bag (雷克斯的诅咒 returns the revealed token and reveals a new one; the core keeps both in the
        // test's revealed list, the one still in play in setAsideChaosTokens): shown faded with "已放回", not as a second token
        // drawn (Yunke 11-20: it looked like "reveal one more"). Only the tokens still in play count.
        var inPlay = J.Items(J.Get(st, "setAsideChaosTokens")).Select(t => J.String(t, "chaosTokenFace")).Where(x => x.Length > 0).ToList();
        var returned = new List<bool>();
        if (inPlay.Count > 0 && inPlay.Count < tokens.Length)
        {
            var left = new List<string>(inPlay);
            foreach (var f in tokens) { int k = left.IndexOf(f); if (k >= 0) { left.RemoveAt(k); returned.Add(false); } else returned.Add(true); }
        }
        else returned.AddRange(tokens.Select(_ => false));
        for (int ti = 0; ti < tokens.Length; ti++)
        {
            var f = tokens[ti];
            var tcol = new VBoxContainer { Alignment = BoxContainer.AlignmentMode.Center }; tcol.AddThemeConstantOverride("separation", 2); tokRow.AddChild(tcol);
            var img = Ui.Image(Ui.Token(f), new Vector2(96, 96)); img.PivotOffset = new Vector2(48, 48); tcol.AddChild(img);
            if (returned[ti])
            {
                img.Modulate = new Color(1, 1, 1, 0.35f); img.CustomMinimumSize = new Vector2(72, 72); img.PivotOffset = new Vector2(36, 36);
                var back = Style.Text("已放回", 16, Style.PaperDim, Style.Kai); back.HorizontalAlignment = HorizontalAlignment.Center; tcol.AddChild(back);
                tcol.TooltipText = QuestionPresenter.ChaosTokenName(f) + "：已放回混沌袋，不计入结果"; tcol.MouseFilter = MouseFilterEnum.Pass;
            }
            else if (sig != lastTokens && S.Settings.ReducedMotion != true) FlipToken(img, Ui.Token(f), null);
        }
        tokens = tokens.Where((_, i) => !returned[i]).ToArray();   // the effects below: only for the tokens that count
        // As on the 0712 web table: no name under the token; a symbol token's effect (from the scenario card) is shown
        // beside a small copy of it. A number token needs nothing: its number is on the token.
        var effects = new VBoxContainer { MouseFilter = MouseFilterEnum.Ignore }; effects.AddThemeConstantOverride("separation", 6);
        foreach (var f in tokens.Where(f => ChaosBagRules.ScenarioFaces.Contains(f) || f == "ElderThing").Distinct())
        {
            string text = S.Locale.TryTemplate(ChaosBagRules.PenaltyKey(m.Difficulty, f, ScenarioCode(m)), out var tpl) ? S.Locale.Text(tpl).Replace("_", "") : "";
            if (text.Length == 0) continue;
            var line = new HBoxContainer { MouseFilter = MouseFilterEnum.Ignore }; line.AddThemeConstantOverride("separation", 10);
            var small = Ui.Image(Ui.Token(f), new Vector2(34, 34)); small.SizeFlagsVertical = SizeFlags.ShrinkCenter; line.AddChild(small);
            var lt = Style.Text(text, 19, Style.Paper, Style.Kai); lt.AutowrapMode = TextServer.AutowrapMode.WordSmart; lt.CustomMinimumSize = new Vector2(660, 0); lt.SizeFlagsVertical = SizeFlags.ShrinkCenter; line.AddChild(lt);
            effects.AddChild(line);
        }
        if (effects.GetChildCount() > 0) col.AddChild(effects);
        // the revealed token resolved as another one (歪打正着): say so, with the token it counts as
        if (tokenSubstitute is { } sub && tokens.Length > 0)
        {
            var line = new HBoxContainer { MouseFilter = MouseFilterEnum.Ignore, Alignment = BoxContainer.AlignmentMode.Center }; line.AddThemeConstantOverride("separation", 10);
            line.AddChild(Style.Text("当作", 21, Style.Amber, Style.Kai));
            var small = Ui.Image(Ui.Token(sub), new Vector2(40, 40)); small.SizeFlagsVertical = SizeFlags.ShrinkCenter; line.AddChild(small);
            line.AddChild(Style.Text(QuestionPresenter.ChaosTokenName(sub) + " 结算", 21, Style.Amber, Style.Kai));
            col.AddChild(line);
        }
        lastTokens = sig;
        var result = J.Get(st, "result");
        if (J.Tag(result) != "Unrun") col.AddChild(Style.Text(ClientDisplay.SkillOutcome(result), 30, J.Tag(result) == "SucceededBy" ? Style.Good : Style.Danger, Style.Kai));
        var primary = m.Question.Choices.FirstOrDefault(c => c.Enabled && c.Tag is "StartSkillTestButton" or "SkillTestApplyResultsButton")
            ?? m.Question.Choices.FirstOrDefault(c => c.Enabled && c.Tag == "SkipTriggersButton");
        string go = primary?.Tag == "SkipTriggersButton" ? "继续" : primary?.Title ?? "";
        // the button says what pressing it does with the committed cards
        if (primary?.Tag == "StartSkillTestButton") go = committed.Length > 0 ? "投入 " + committed.Length + " 张，开始检定" : "不投牌，开始检定";
        if (primary?.Tag == "SkillTestApplyResultsButton")
        {
            var cont = Style.Text("看清结果后，点击任意处继续", 18, Style.PaperDim, Style.Kai); cont.HorizontalAlignment = HorizontalAlignment.Center; col.AddChild(cont);
        }
        if (primary?.Tag == "SkipTriggersButton")
        {
            // A window before (or during) the test: fast cards and abilities may be used here. Name them, since the
            // only other sign is their glow (Yunke: Roland's investigation stopped here with no visible reason).
            var usable = m.Question.Choices.Where(c => c.Enabled && c.Tag != "SkipTriggersButton")
                .Select(c => c.CardCode.Length > 0 ? S.Name(CoreCatalog.Normalize(c.CardCode)) : c.Title).Where(n => n.Length > 0).Distinct().ToList();
            if (usable.Count > 0)
            {
                // 心胜于物 lets a later test use 智力 in place of 战斗/敏捷 (the choice is made when a test begins: rules source
                // MindOverMatter.hs, UseSkillInPlaceOf); played inside a test already under way it does not change that test
                bool mom = m.Question.Choices.Any(c => c.Enabled && CoreCatalog.Normalize(c.CardCode) == "01036");
                // a token just drawn and waiting on its "when revealed" window (温蒂·亚当斯): name it, so the reaction is read as
                // answering that token (QQ #28)
                var fresh = m.FreshTestTokens;
                string drawn = fresh.Count > 0 ? "刚揭示了" + string.Join("、", fresh.Select(t => QuestionPresenter.ChaosTokenName(t.Face))) + "标记，还没有结算。" : "";
                var tip = Style.Text(drawn + "现在可以使用：" + string.Join("、", usable) + "。不用的话点下方按钮。"
                    + (mom ? "\n心胜于物：本轮之后开始的检定可改用智力；本次检定已经开始，不会改变。" : ""), 20, Style.Amber, Style.Kai);
                tip.AutowrapMode = TextServer.AutowrapMode.WordSmart; tip.CustomMinimumSize = new Vector2(720, 0); col.AddChild(tip);
                go = "不使用，继续检定";
                // each asking ability's trigger switch (总是触发 / 只在自己回合 / 停止触发)
                var offered = TriggerModes.Offered(m);
                if (offered.Count > 0)
                {
                    var swRow = new HBoxContainer { MouseFilter = MouseFilterEnum.Ignore, Alignment = BoxContainer.AlignmentMode.Center }; swRow.AddThemeConstantOverride("separation", 8);
                    foreach (var (inv, card, index) in offered) swRow.AddChild(ModeSwitch(m, inv, card, index, true));
                    col.AddChild(swRow);
                }
            }
        }
        if (primary is not null) { var b = Ui.Button(go, () => Choose(primary), 26, true); b.CustomMinimumSize = new Vector2(0, 64); col.AddChild(b); }
    }

    readonly Dictionary<string, int> testBase = new();   // a test's value before its first committed card

    // The comparison as the html SkillTest draws it (.test-status): the difficulty in a dark red disc, the tested skill's icon in
    // between, the investigator's value in a dark green disc (Yunke 11-18: not the word "对抗"; html has "VS" there, we keep
    // the icon only — no English on screen). What committed cards add sits on the green disc as a small amber "+N".
    Control Compare(int difficulty, int value, int plus, string[] skills)
    {
        var row = new HBoxContainer { MouseFilter = MouseFilterEnum.Pass, Alignment = BoxContainer.AlignmentMode.Center, TooltipText = "难度 " + difficulty + " · 检定值 " + value + (plus != 0 ? "（投入 " + (plus > 0 ? "+" : "") + plus + "）" : "") };
        row.AddThemeConstantOverride("separation", 10);
        Control Disc(int n, Color bg)
        {
            var p = new PanelContainer { MouseFilter = MouseFilterEnum.Ignore, CustomMinimumSize = new Vector2(54, 54), SizeFlagsVertical = SizeFlags.ShrinkCenter };
            var sb = new StyleBoxFlat { BgColor = bg, BorderColor = new Color(1, 1, 1, 0.25f), AntiAliasing = true, ShadowColor = new Color(0, 0, 0, 0.5f), ShadowSize = 4 }; sb.SetBorderWidthAll(2); sb.SetCornerRadiusAll(27);
            p.AddThemeStyleboxOverride("panel", sb);
            var t = Style.Text(n.ToString(), 30, Colors.White, Style.Symbols); t.MouseFilter = MouseFilterEnum.Ignore;
            var hold = new Control { MouseFilter = MouseFilterEnum.Ignore }; p.AddChild(hold); hold.AddChild(t); Ui.CenterDigits(t); return p;
        }
        row.AddChild(Disc(difficulty, new Color("8b0000")));
        var mid = new HBoxContainer { MouseFilter = MouseFilterEnum.Ignore, SizeFlagsVertical = SizeFlags.ShrinkCenter }; mid.AddThemeConstantOverride("separation", 2);
        foreach (var k in skills) mid.AddChild(Ui.Glyph(k, 30, k switch { "willpower" => Style.Willpower, "intellect" => Style.Intellect, "combat" => Style.Combat, "agility" => Style.Agility, _ => Style.Paper }));
        row.AddChild(mid);
        var green = new Control { MouseFilter = MouseFilterEnum.Ignore, CustomMinimumSize = new Vector2(54, 54), SizeFlagsVertical = SizeFlags.ShrinkCenter };
        var gd = Disc(value, new Color("006400")); green.AddChild(gd);
        if (plus != 0)
        {
            var tag = Style.Text((plus > 0 ? "+" : "−") + Math.Abs(plus), 20, Style.Amber, Style.Symbols); tag.MouseFilter = MouseFilterEnum.Ignore;
            tag.AddThemeConstantOverride("outline_size", 6); tag.AddThemeColorOverride("font_outline_color", Colors.Black);
            green.AddChild(tag); tag.Position = new Vector2(42, 30);   // bottom right of the disc (top right ran into the panel edge)
        }
        row.AddChild(green);
        return row;
    }

    // An attack's damage if it succeeds (Yunke 11-18 #13, the html SkillTest's DamageDealt modifiers): 1, plus every "+N damage"
    // the core has put on the investigator or the test (a weapon's ability, 背刺…), plus committed cards that add damage on success.
    // Committed skills (from their printed text, every printing): Plus always on success; Better instead when the test succeeds
    // by At or more (Yunke 11-22: 长柄大锤 + 致命打击(2) read "2 点伤害（基础 1，效果 +1）" — the upgraded 致命打击 was not
    // counted and the hammer was not named; it is 3, or 4 when succeeding by 2 or more).
    static readonly Dictionary<string, (int Plus, int Better, int At)> DamageOnSuccess = new()
    {
        ["01025"] = (1, 0, 0), ["01525"] = (1, 0, 0), ["12025"] = (1, 0, 0), ["60119"] = (1, 0, 0), ["60169"] = (1, 0, 0),   // 致命打击
        ["60176"] = (1, 2, 2), ["02299"] = (1, 2, 2),    // 致命打击 (2): +1, +2 instead when succeeding by 2 or more (Yunke 11-22: 02299 was missing)
        ["06166"] = (0, 2, 2),    // 蛮力 (basic fight): +2 when succeeding by 2 or more
        ["10031"] = (1, 0, 0),    // 膂力过人 (with a melee or ranged asset)
    };
    Control DamageRow(TableModel m, System.Text.Json.JsonElement st)
    {
        var parts = new List<string>(); int total = 1, extraAt = 0, extra = 0;
        var fromMods = new HashSet<string>();
        var inv = m.Investigators.FirstOrDefault(i => i.Id == J.String(st, "investigator"));
        var mods = (inv is null ? Enumerable.Empty<System.Text.Json.JsonElement>() : J.Items(J.Get(inv.Raw, "modifiers"))).Concat(J.Items(J.Get(st, "modifiers")));
        foreach (var md in mods)
        {
            var type = J.Get(md, "type"); if (J.Tag(type) != "DamageDealt") continue;
            var v = J.Get(type, "contents"); int n = v.ValueKind == System.Text.Json.JsonValueKind.Number ? v.GetInt32() : 0; if (n == 0) continue;
            string code = ModSourceCode(m, J.Get(md, "source")); if (code.Length > 0) fromMods.Add(code);
            total += n; parts.Add(ModSourceName(m, J.Get(md, "source")) + " +" + n);
        }
        foreach (var c in J.Props(J.Get(st, "committedCards")).SelectMany(p => J.Items(p.Value)).Select(c => CoreCatalog.Normalize(J.String(J.Get(c, "contents"), "cardCode"))))
        {
            if (fromMods.Contains(c) || !DamageOnSuccess.TryGetValue(c, out var d)) continue;   // already counted by the core's own modifier
            total += d.Plus;
            // "致命打击 +1/2" (Yunke 11-22: "成功时造成3/4点伤害 / 基础1 大锤+1 致命打击+1/2")
            if (d.At > 0) { extra += d.Better - d.Plus; extraAt = Math.Max(extraAt, d.At); parts.Add(S.Name(c) + " +" + d.Plus + "/" + d.Better); }
            else parts.Add(S.Name(c) + " +" + d.Plus);
        }
        var box = new VBoxContainer { MouseFilter = MouseFilterEnum.Pass, Alignment = BoxContainer.AlignmentMode.Center, TooltipText = "基础 1" + string.Concat(parts.Select(x => " · " + x)) + (extra > 0 ? "\n超过难度至少 " + extraAt + " 点时为 " + (total + extra) + " 点" : "") };
        box.AddThemeConstantOverride("separation", 2);
        var row = new HBoxContainer { MouseFilter = MouseFilterEnum.Ignore, Alignment = BoxContainer.AlignmentMode.Center }; row.AddThemeConstantOverride("separation", 8); box.AddChild(row);
        var ic = Ui.Image(Ui.Stat("health"), new Vector2(28, 28)); ic.MouseFilter = MouseFilterEnum.Ignore; row.AddChild(ic);
        string head = "成功时造成 " + total + (extra > 0 ? "/" + (total + extra) : "") + " 点伤害";
        var t = Style.Text(head, 20, total + extra > 1 ? Style.Amber : Style.Paper, Style.Kai); t.VerticalAlignment = VerticalAlignment.Center; row.AddChild(t);
        if (parts.Count > 0)
        {
            var why = Style.Text("基础 1，" + string.Join("，", parts), 16, Style.PaperDim, Style.Kai); why.HorizontalAlignment = HorizontalAlignment.Center; why.MouseFilter = MouseFilterEnum.Ignore;
            box.AddChild(why);
        }
        return box;
    }

    // the card behind a modifier's source: a card in play, or the card whose ability it is (AbilitySource [source, n], …)
    string ModSourceCode(TableModel m, System.Text.Json.JsonElement src)
    {
        string group = J.Tag(src) switch { "AssetSource" => "assets", "EventSource" => "events", "SkillSource" => "skills", "InvestigatorSource" => "investigators", "TreacherySource" => "treacheries", "EnemySource" => "enemies", "LocationSource" => "locations", _ => "" };
        var id = J.Get(src, "contents"); string sid = id.ValueKind == System.Text.Json.JsonValueKind.String ? id.GetString()! : "";
        if (group.Length > 0 && sid.Length > 0 && ClientDisplay.CardCode(J.Get(J.Get(m.Game, group), sid)) is { Length: > 0 } code) return CoreCatalog.Normalize(code);
        if (id.ValueKind == System.Text.Json.JsonValueKind.Array)
            foreach (var inner in J.Items(id)) if (inner.ValueKind == System.Text.Json.JsonValueKind.Object && ModSourceCode(m, inner) is { Length: > 0 } c) return c;
        if (id.ValueKind == System.Text.Json.JsonValueKind.Object) return ModSourceCode(m, id);
        return "";
    }

    string ModSourceName(TableModel m, System.Text.Json.JsonElement src)
    {
        string group = J.Tag(src) switch { "AssetSource" => "assets", "EventSource" => "events", "SkillSource" => "skills", "InvestigatorSource" => "investigators", "TreacherySource" => "treacheries", "EnemySource" => "enemies", "LocationSource" => "locations", _ => "" };
        var id = J.Get(src, "contents"); string sid = id.ValueKind == System.Text.Json.JsonValueKind.String ? id.GetString()! : "";
        if (group.Length > 0 && sid.Length > 0 && ClientDisplay.CardCode(J.Get(J.Get(m.Game, group), sid)) is { Length: > 0 } code) return S.Name(CoreCatalog.Normalize(code));
        if (ModSourceCode(m, src) is { Length: > 0 } inner) return S.Name(inner);   // an ability's card (长柄大锤's fight)
        if (J.Tag(src) == "CardIdSource" && sid.Length > 0) return "卡牌效果";
        return "效果";
    }

    // The chaos token is drawn face down and turns over like a coin (0712 web table: blank back, two quick turns, then the face).
    void FlipToken(TextureRect img, Texture2D? face, Control? label)
    {
        img.Texture = Ui.Token("Blank"); if (label is not null) label.Modulate = new Color(1, 1, 1, 0);
        GameAudio.Cue("chaos");   // noncore audio 11-19
        QaMidShot(0.15);
        img.Position += new Vector2(0, -18);
        var tw = img.CreateTween();
        tw.TweenProperty(img, "position:y", img.Position.Y + 18, 0.5f).SetTrans(Tween.TransitionType.Bounce).SetEase(Tween.EaseType.Out);
        var flip = img.CreateTween();
        for (int i = 0; i < 2; i++)
        {
            flip.TweenProperty(img, "scale:x", 0f, 0.1f).SetTrans(Tween.TransitionType.Sine).SetEase(Tween.EaseType.In);
            if (i == 1) flip.TweenCallback(Callable.From(() => img.Texture = face));
            flip.TweenProperty(img, "scale:x", 1f, 0.1f).SetTrans(Tween.TransitionType.Sine).SetEase(Tween.EaseType.Out);
        }
        flip.TweenProperty(img, "scale", new Vector2(1.12f, 1.12f), 0.08f);
        flip.TweenProperty(img, "scale", Vector2.One, 0.12f);
        if (label is not null) flip.TweenProperty(label, "modulate:a", 1f, 0.15f);
    }

    static string SourceName(TableModel m, System.Text.Json.JsonElement st)
    {
        var cards = TestCards(m, st);
        return cards.Count > 0 ? App.I.S!.Name(cards[0]) + " · 检定" : "效果检定";
    }

    // The cards a test is about: what makes it (a treachery, a location's forced effect, an asset or event used, an enemy's
    // attack) and what it is made against (the enemy fought or evaded, the location investigated) — searched for in the test's
    // source and target at any depth (an ability source wraps the card: {"tag":"AbilitySource","contents":[{LocationSource…}, 1]}).
    // Investigators are left out. At most two, the source first.
    static List<string> TestCards(TableModel m, System.Text.Json.JsonElement st)
    {
        var res = new List<string>();
        // entity ids and card instance ids → card codes: every entity on the table, and the game's card registry (gameCards),
        // which also knows an event being played or a card in a hand or discard pile (Yunke 11-20: 寻找答案 + 河岸城)
        var ents = new Dictionary<string, string>();
        // (the client view keeps the groups at its top level; a raw snapshot under gameEntities — both are read)
        foreach (var grp in new[] { "treacheries", "enemies", "locations", "assets", "agendas", "acts", "events", "skills", "stories" })
            foreach (var root in new[] { m.Game, J.Get(m.Game, "gameEntities") })
                foreach (var p in J.Props(J.Get(root, grp)))
                {
                    string code = ClientDisplay.CardCode(p.Value); if (code.Length == 0) code = J.String(p.Value, "cardCode"); if (code.Length == 0) continue;
                    ents[p.Name] = code; if (J.String(p.Value, "cardId") is { Length: > 0 } cid) ents[cid] = code;
                }
        foreach (var reg in new[] { "gameCards", "cards" })
            foreach (var p in J.Props(J.Get(m.Game, reg)))
            {
                string cc = J.String(J.Get(p.Value, "contents"), "cardCode"); if (cc.Length == 0) cc = J.String(p.Value, "cardCode");
                if (cc.Length > 0) ents.TryAdd(p.Name, cc);
            }
        if (App.I.QaRunning) GD.Print("QA_TESTCARDS ents=" + ents.Count + " sourceCard=" + J.String(st, "sourceCard") + " keys=" + string.Join(",", J.Props(m.Game).Select(p => p.Name).Where(n => n.Contains("ard")).Take(8)));
        void Walk(System.Text.Json.JsonElement e, int depth)
        {
            if (depth > 10 || res.Count >= 2) return;
            switch (e.ValueKind)
            {
                case System.Text.Json.JsonValueKind.String:
                    string v = e.GetString() ?? "";
                    string? code = ents.TryGetValue(v, out var c) ? c : v.Length >= 6 && v[0] == 'c' && char.IsDigit(v[1]) && J.String(App.I.S!.Card(CoreCatalog.Normalize(v)), "type_code") is not ("" or "investigator") ? v : null;
                    if (code is not null) { code = CoreCatalog.Normalize(code); if (!res.Contains(code)) res.Add(code); }
                    break;
                case System.Text.Json.JsonValueKind.Array: foreach (var x in e.EnumerateArray()) Walk(x, depth + 1); break;
                case System.Text.Json.JsonValueKind.Object:
                    if (J.String(e, "tag") is "InvestigatorSource" or "InvestigatorTarget") return;
                    foreach (var p in e.EnumerateObject()) Walk(p.Value, depth + 1); break;
            }
        }
        // what makes the test first (the event played, the asset used, the treachery…), then what it is against
        Walk(J.Get(st, "sourceCard"), 0); Walk(J.Get(st, "source"), 0); Walk(J.Get(st, "targetCard"), 0); Walk(J.Get(st, "target"), 0);
        return res;
    }

    static string StageHint(TableModel m, System.Text.Json.JsonElement st)
    {
        // State only, no instructions: the table's highlights say what can be acted on.
        return J.Tag(J.Get(st, "result")) != "Unrun" ? "检定已揭晓" : "";
    }

    internal static string TokenName(string face) => face switch
    {
        "PlusOne" => "+1", "Zero" => "0", "MinusOne" => "−1", "MinusTwo" => "−2", "MinusThree" => "−3", "MinusFour" => "−4",
        "MinusFive" => "−5", "MinusSix" => "−6", "MinusSeven" => "−7", "MinusEight" => "−8",
        // official token names (html version locale, log.json)
        "ElderSign" => "远古印记", "AutoFail" => "自动失败", "Skull" => "骷髅", "Cultist" => "邪教徒", "Tablet" => "石板", "ElderThing" => "远古之物",
        "CurseToken" => "诅咒", "BlessToken" => "祝福", "FrostToken" => "霜冻", _ => face
    };

    // Raw "$key name=s:\"…\" count=i:1.0" rows the core logs without a structured entry.
    static readonly Dictionary<string, string> RawLogFallback = new() { ["drewCard"] = "{iname}抽了{count}张牌" };
    string RawLog(string text)
    {
        var drew = System.Text.RegularExpressions.Regex.Match(text, @"^(.+) drew (Enemy|Treachery|Asset|Event|Skill|Location)$");
        if (drew.Success) return EnglishName(drew.Groups[1].Value) + "抽到了" + (drew.Groups[2].Value switch { "Enemy" => "敌人", "Treachery" => "诡计", "Asset" => "支援", "Event" => "事件", "Skill" => "技能", _ => "地点" });
        var mm = System.Text.RegularExpressions.Regex.Match(text, @"^\$(\w+)\s*(.*)$");
        if (!mm.Success) return S.Locale.Plain(EnglishLog(text));
        string key = mm.Groups[1].Value;
        var vars = new Dictionary<string, string>();
        foreach (System.Text.RegularExpressions.Match v in System.Text.RegularExpressions.Regex.Matches(mm.Groups[2].Value, @"(\w+)=(\w):(""[^""]*""|\S+)"))
        {
            string val = v.Groups[3].Value.Trim('"');
            if (v.Groups[2].Value == "i" && double.TryParse(val, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var d)) val = ((long)d).ToString();
            vars[v.Groups[1].Value] = val;
        }
        foreach (var k in vars.Keys.ToArray()) if (vars.TryGetValue("__" + k, out var code) && code.Length > 0) { var n = S.Name(CoreCatalog.Normalize(code)); if (n.Length > 0) vars[k] = n; }
        string? tpl = S.Locale.TryTemplate("log." + key, out var t1) ? t1 : S.Locale.TryTemplate(key, out var t2) ? t2 : RawLogFallback.GetValueOrDefault(key);
        if (tpl is null) return "（" + key + "）";
        // fill the variables before Plain, which would read {damage} / {horror} as icon words
        return S.Locale.Plain(System.Text.RegularExpressions.Regex.Replace(tpl, @"\{(\w+)\}", g => vars.GetValueOrDefault(g.Groups[1].Value, "")));
    }

    // English rows the core logs as plain text, translated the way the html version does it
    // (gameLogLocalization.ts translateGameLogText) with its official wording (gameLog.*)
    string EnglishLog(string text)
    {

        // inline card references {investigator:"Daisy Walker: The Librarian":"01002"} → the card's Chinese name
        text = System.Text.RegularExpressions.Regex.Replace(text, @"\{\w+:"".*?"":""(\w+)""\}", g => S.Name(CoreCatalog.Normalize(g.Groups[1].Value)) is { Length: > 0 } n ? n : g.Value);
        string T(string key, string name, object value) => S.Locale.TryTemplate("gameLog." + key, out var t) ? t.Replace("{" + name + "}", value.ToString()) : "";
        // Record "…" / Remember "…" / Forgot "…" (campaign log and scenario memory)
        var directive = System.Text.RegularExpressions.Regex.Match(text, @"^(Remember|Forgot|Record) ""(.+)""(?: \((\d+)\))?$");
        if (directive.Success)
        {
            string action = directive.Groups[1].Value == "Remember" ? "remember" : directive.Groups[1].Value == "Forgot" ? "forgot" : "record";
            string done = T(action, "value", RecordValue(directive.Groups[2].Value));
            if (done.Length > 0) return done + (directive.Groups[3].Success ? "（" + directive.Groups[3].Value + "）" : "");
        }
        text = System.Text.RegularExpressions.Regex.Replace(text, @"\s*\bmust\s+discard\s+down\s+to\s+(\d+)\s+cards?\b", g => T("discardDownTo", "count", g.Groups[1].Value) is { Length: > 0 } s ? s : g.Value, System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        text = System.Text.RegularExpressions.Regex.Replace(text, @"\s*\bdiscovered\s+(\d+)\s+clues?\b", g => T("discoveredClues", "count", g.Groups[1].Value) is { Length: > 0 } s ? s : g.Value, System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        return text;
    }

    // a campaign-log phrase, optionally after a card name ("Victoria Devereux: The Collector cultists who got away"):
    // the phrase is the official campaign-log key (nightOfTheZealot.key.cultistsWhoGotAway = 逃跑的异教徒), the name the card's name
    string RecordValue(string value)
    {
        var words = value.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        for (int i = 0; i < words.Length; i++)
        {
            string camel = string.Concat(words.Skip(i).Select((w, j) =>
            {
                string clean = new string(w.Normalize(System.Text.NormalizationForm.FormD).Where(char.IsLetterOrDigit).ToArray());
                return clean.Length == 0 ? "" : j == 0 ? char.ToLowerInvariant(clean[0]) + clean[1..].ToLowerInvariant() : char.ToUpperInvariant(clean[0]) + clean[1..].ToLowerInvariant();
            }));
            if (camel.Length == 0) continue;
            foreach (var scope in new[] { Camp.Key + ".key.", "nightOfTheZealot.key.", "base.key." })
            {
                if (!S.Locale.TryTemplate(scope + camel, out var phrase)) continue;
                string who = string.Join(' ', words.Take(i)).Trim();
                if (who.Length == 0) return S.Locale.Plain(phrase);
                string bare = who.Contains(": ") ? who[..who.IndexOf(": ", StringComparison.Ordinal)] : who;
                string zh = EnglishName(who) is var full && full != who ? full : EnglishName(bare);
                return S.Locale.Plain(phrase) + "：" + zh;
            }
        }
        return value;
    }

    static Dictionary<string, string>? englishNames;
    string EnglishName(string n)
    {
        englishNames ??= S.Catalog.Cards.Values.Where(c => J.String(c, "real_name").Length > 0).GroupBy(c => J.String(c, "real_name")).ToDictionary(g => g.Key, g => J.String(g.First(), "name"));
        return englishNames.TryGetValue(n, out var z) && z.Length > 0 ? z : n;
    }

    static string TidyLog(string t) => System.Text.RegularExpressions.Regex.Replace(t, @"点(\d+\s*对\s*难度\s*\d+)", "点（$1）");

    // ───────────────────────── modal questions
    Control? questionScrim;
    void QuestionModal()
    {
        var m = model!;
        if (questionScrim is not null && IsInstanceValid(questionScrim)) App.I.CloseModal(questionScrim);
        questionScrim = null;
        if (m.IsOver && m.QuestionTag == "") { if (CampaignFinished(m)) CampaignEndDialog(); else ResultDialog(); return; }
        switch (m.QuestionTag)
        {
            case "ChooseOne" when IsTarotReadingChoice(m): TarotReadingDialog(); break;   // 使用塔罗牌解牌 (TableScreen.Tarot)
            case "ChooseN" when HasTarotLabels(m): TarotPickDialog(); break;
            case "Read": StoryDialog(); break;
            case "ChooseAmounts": case "ChoosePaymentAmounts": AmountsDialog(); break;
            case "ChooseOneWizard": case "DropDown": GenericDialog(); break;
            case "PickDestiny": DestinyDialog(); break;   // The Circle Undone's tarot reading (later cycles, 11-22)
            case "PickCampaignSpecific": WorldMapDialog(); break;   // The Scarlet Keys: embarking on the world map (QQ #45)
        }
        if (m.QuestionTag != "PickCampaignSpecific") CloseWorldMap();
    }

    static FontVariation? boldKai;
    void StoryDialog()
    {
        var m = model!;
        GameAudio.Narrate(m);   // noncore audio 11-21: the page is read aloud (讯飞 江阅尘); once per page
        var vp = GetViewportRect().Size;
        // the story pages lie on the table too (Yunke 11-19 r9 #8: the house painting is replaced by the desk)
        var root = Ui.Backdrop(vp, Ui.Bg("desk-table"));
        float colW = Math.Min(980, vp.X - 200);
        var col = new VBoxContainer { Position = new Vector2((vp.X - colW) / 2, vp.Y * 0.12f), Size = new Vector2(colW, vp.Y * 0.8f) }; col.AddThemeConstantOverride("separation", 22);
        root.AddChild(col);
        string title = m.Question.Title.Length > 0 && m.Question.Title != "阅读剧情" ? m.Question.Title : "";
        var cards = m.Question.ReadingCards.ToArray();
        if (title.Length == 0 && cards.Length > 0) title = S.Name(cards[0]);
        var texts = new List<string>();
        if (m.Question.Body.Length > 0)
        {
            var lines = m.Question.Body.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
            for (int i = lines.Count - 1; i > 0; i--) if (lines[i] == lines[i - 1]) lines.RemoveAt(i);
            if (lines.Count > 1 && lines[0].Length <= 24) { if (title.Length == 0 || title == lines[0]) title = lines[0]; lines.RemoveAt(0); }
            texts.Add(string.Join("\n", lines));
        }
        foreach (var code in cards.Take(2)) { var card = S.Card(code); var txt = J.String(card, "back_flavor").Length > 0 ? J.String(card, "back_flavor") : J.String(card, "flavor"); if (txt.Length > 0 && !texts.Any(x => x.Contains(txt))) texts.Add(txt); }
        if (title.Length > 0) { var t = Style.Title(title, 46); t.HorizontalAlignment = HorizontalAlignment.Center; col.AddChild(t); }
        var rule = new ColorRect { Color = new Color(Style.Brass, 0.3f), CustomMinimumSize = new Vector2(0, 1) }; col.AddChild(rule);
        // one paragraph per line, with air between paragraphs. A long page (a resolution with its log lines) scrolls inside
        // a box that leaves room for the button: the button ran off the bottom of the screen (playtest 11-18)
        var textCol = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill }; textCol.AddThemeConstantOverride("separation", 22);
        var textScroll = new ScrollContainer { HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled };
        textScroll.AddChild(textCol); col.AddChild(textScroll); SmoothScroll.Attach(textScroll);   // eased, not jumping (r9 #6)
        // grows with the text and scrolls past about half the screen; one text width with or without the scroll bar (FitScroll:
        // QQ #32/#33, 聚集于此's resolution 2 froze the game)
        var fitText = FitScroll(textScroll, textCol, colW, vp.Y * 0.52f);
        // the experience gained is a paragraph of its own, in bold (Yunke 11-20 #25): the sentences of a line that speak of
        // experience are taken out of it and set after it (with the tally "经验: 6")
        var paras = new List<string>();
        foreach (var line0 in texts.SelectMany(t => t.Split('\n', StringSplitOptions.RemoveEmptyEntries)))
        {
            // the locale's emphasis marks before a colon ("结局 1_:", "经验_: 10") are not shown
            // the locale's emphasis pairs ("_结局1：_你们…", "_经验：_6") read as bold, as the html page shows them
            var line = System.Text.RegularExpressions.Regex.Replace(line0, @"_([^_\n]{1,30})_", "<b>$1</b>");
            line = System.Text.RegularExpressions.Regex.Replace(line, @"_+(?=\s*[:：])", "");
            if (!line.Contains("经验")) { paras.Add(line); continue; }
            var parts = System.Text.RegularExpressions.Regex.Split(line, @"(?<=[。！？])").Where(x => x.Trim().Length > 0).ToList();
            var xp = parts.Where(x => x.Contains("经验")).ToList(); var rest = parts.Except(xp).ToList();
            if (rest.Count > 0) paras.Add(string.Concat(rest));
            // gold, and only the tally "经验: X" in bold (Yunke 11-20); \u0007 marks the paragraph for its colour below
            string plain = string.Concat(xp).Replace("<b>", "").Replace("</b>", "").Replace("*", "");
            paras.Add("\u0007" + System.Text.RegularExpressions.Regex.Replace(plain, @"(经验[_\s]*[:：][_\s]*\d+)", "<b>$1</b>"));
        }
        NoteTrauma(paras, title);   // a resolution's trauma, with its sentence, for the campaign screen (Yunke 11-20 #27)
        foreach (var txt in paras)
        {
            // ordinal: the culture-aware StartsWith(string) ignores the control mark and matched every paragraph (Yunke 11-21: each
            // paragraph lost its first letter and the whole page came out gold)
            bool xpLine = txt.Length > 0 && txt[0] == '\u0007';
            var r = CardReader.Rules(System.Text.RegularExpressions.Regex.Replace(xpLine ? txt[1..] : txt, @"\*([^*\n]+)\*", "<b>$1</b>"), 28); r.AddThemeColorOverride("default_color", new Color("f1e6cc"));
            // the page's own face for bold words too (QA 11-20: "没有结局" — the bold rule line came out in an emboldened serif)
            if (Style.Kai is not null) { r.AddThemeFontOverride("normal_font", Style.Kai); r.AddThemeFontOverride("bold_font", boldKai ??= new FontVariation { BaseFont = Style.Kai, VariationEmbolden = 0.8f }); }
            r.AddThemeConstantOverride("line_separation", 14); r.AddThemeColorOverride("default_color", xpLine ? new Color("e8c37a") : new Color("f1e6cc"));
            r.AddThemeConstantOverride("outline_size", 6); r.AddThemeColorOverride("font_outline_color", new Color(0, 0, 0, 0.7f));
            r.CustomMinimumSize = new Vector2(colW - 16, 0);
            textCol.AddChild(r);
        }
        fitText();
        col.AddChild(Ui.Spacer(0, 6));
        var enabled = m.Question.Choices.Where(c => c.Enabled).ToArray();
        // story choices are often whole sentences (无人知晓: calm or warn the villagers): when they would not fit side by
        // side they stack, one per row, so the page never grows past the screen (playtest 11-18: the text ran off the right)
        bool stack = enabled.Length > 1 && enabled.Sum(c => c.Title.Length) > 28;
        BoxContainer go = stack ? new VBoxContainer { Alignment = BoxContainer.AlignmentMode.Center } : new HBoxContainer { Alignment = BoxContainer.AlignmentMode.Center };
        go.AddThemeConstantOverride("separation", stack ? 10 : 16); col.AddChild(go);
        foreach (var c in enabled)
        {
            var b = Ui.Button(c.Title, () => BlackFade(() => Choose(c)), stack ? 23 : 26, true);
            b.CustomMinimumSize = new Vector2(stack ? colW : 260, stack ? 54 : 60);
            if (stack) { b.ClipText = true; b.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis; b.TooltipText = c.Title; }
            go.AddChild(b);
        }
        if (enabled.Length == 1)
        {
            var hint = Style.Text("单击任意处继续", 17, new Color(Style.PaperDim, 0.7f)); hint.HorizontalAlignment = HorizontalAlignment.Center; col.AddChild(hint);
            var only = enabled[0]; bool gone = false;
            root.GuiInput += e => { if (e is InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Left } && !gone) { gone = true; BlackFade(() => Choose(only)); } };
        }
        // (no "先看看牌桌" button any more — Yunke 11-19 r9 #8)
        questionScrim = App.I.Modal(root, false, 0f);
        if (S.Settings.ReducedMotion != true) { col.Modulate = new Color(1, 1, 1, 0); col.CreateTween().TweenProperty(col, "modulate:a", 1f, 0.6f); FromBlack(root); }
    }

    // The amount target the core sends: a tagged value (total, minimum, maximum or one of a few), or none.
    static (string Tag, int Value, int[] Options) AmountTargetOf(JsonElement q, bool payment)
    {
        var target = J.Get(q, payment ? "paymentAmountTargetValue" : "amountTargetValue");
        if (target.ValueKind is System.Text.Json.JsonValueKind.Null or System.Text.Json.JsonValueKind.Undefined) return ("None", 0, Array.Empty<int>());
        string tag = J.Tag(target); var c = J.Get(target, "contents");
        if (tag == "AmountOneOf") return (tag, 0, J.Items(c).Where(n => n.ValueKind == System.Text.Json.JsonValueKind.Number).Select(n => n.GetInt32()).ToArray());
        return (tag, c.ValueKind == System.Text.Json.JsonValueKind.Number ? c.GetInt32() : 0, Array.Empty<int>());
    }

    static string AmountRequirement((string Tag, int Value, int[] Options) t, string unit = "个线索") => t.Tag switch
    {
        "TotalAmountTarget" => "需要 " + t.Value + " " + unit,
        "MinAmountTarget" => "至少 " + t.Value + " " + unit,
        "MaxAmountTarget" => "至多 " + t.Value + " " + unit,
        "AmountOneOf" => "需要 " + string.Join(" 或 ", t.Options) + " " + unit,
        _ => "可以自由分配",
    };

    // What an amount row counts, from the core's row label ("Damage", "Horror", …): the token drawn and its name.
    // A row for an investigator with no such label is the team's clue payment (the dialog's first use).
    static (string Token, string Name) AmountKind(ClientAmountRow r)
    {
        string l = r.Label.ToLowerInvariant();
        // the trauma a card bought at deck creation asks to split (危险处境: "$physical" / "$mental")
        if (l == "$physical") return ("health", "肉体创伤");
        if (l == "$mental") return ("horror", "精神创伤");
        if (l.Contains("damage")) return ("health", "伤害");
        if (l.Contains("horror")) return ("horror", "恐惧");
        if (l.Contains("resource")) return ("resource", "资源");
        if (l.Contains("doom")) return ("doom", "毁灭");
        return ("clue", "线索");
    }

    // the question's own wording, in Chinese, for the amount questions met so far (the core sends it in English)
    static readonly Dictionary<string, string> AmountTitles = new() { ["Amount of Damage/Horror to cancel"] = "要取消多少伤害和恐惧" };

    // The distribution a player would pick: start from the minimums, then add clues until the target is met (within bounds).
    internal static Dictionary<string, int> SuggestAmounts(JsonElement q, IReadOnlyList<ClientAmountRow> rows, bool payment)
    {
        var values = rows.ToDictionary(r => r.Id, r => r.Minimum);
        var t = AmountTargetOf(q, payment);
        int want = t.Tag switch
        {
            "TotalAmountTarget" or "MinAmountTarget" => t.Value,
            "AmountOneOf" => t.Options.Where(o => o >= values.Values.Sum()).DefaultIfEmpty(-1).Min(),
            _ => values.Values.Sum(),
        };
        foreach (var r in rows)
            while (values.Values.Sum() < want && values[r.Id] < r.Maximum) values[r.Id]++;
        return ClientQuestions.ValidAmounts(q, values) ? values : rows.ToDictionary(r => r.Id, r => r.Minimum);
    }

    // The team payment: each investigator is a small card. Clue tokens over the portrait show what they pay;
    // left click adds a clue, right click takes one back. The total is shown above, and confirm appears when it is valid.
    void AmountsDialog()
    {
        var m = model!; var q = ClientQuestions.Unwrap(m.State.Question);
        if (IsTraumaAsk(q, out int traumaTotal)) { TraumaDialog(traumaTotal); return; }   // 危险处境's trauma (TableScreen.Trauma)
        IReadOnlyList<ClientAmountRow> rows;
        try { rows = ClientQuestions.AmountRows(q); } catch { return; }
        bool payment = m.QuestionTag == "ChoosePaymentAmounts";
        var target = AmountTargetOf(q, payment);
        var values = rows.ToDictionary(r => r.Id, r => r.Minimum);
        var col = new VBoxContainer { CustomMinimumSize = new Vector2(Math.Max(640, rows.Count * 190), 0) }; col.AddThemeConstantOverride("separation", 18);
        var kinds = rows.Select(AmountKind).ToList();
        string unit = kinds.Select(k => k.Name).Distinct().Count() == 1 ? (kinds.Count > 0 && kinds[0].Name == "线索" ? "个线索" : "点" + kinds.FirstOrDefault().Name) : "点";
        col.AddChild(Style.Title(AmountTitles.TryGetValue(J.String(q, "label"), out var zt) ? zt : m.Question.Title, 30));
        var head = Style.Text("", 24, Style.HoverText, Style.Kai); head.HorizontalAlignment = HorizontalAlignment.Center; col.AddChild(head);
        var cards = new HBoxContainer { Alignment = BoxContainer.AlignmentMode.Center }; cards.AddThemeConstantOverride("separation", 22); col.AddChild(cards);
        var tokensOf = new List<(Control Holder, ClientAmountRow Row)>();
        Button ok = null!;
        void Refresh()
        {
            int sum = values.Values.Sum();
            bool valid = ClientQuestions.ValidAmounts(q, values);
            head.Text = "共 " + sum + " " + unit + "  ·  " + AmountRequirement(target, unit);
            head.AddThemeColorOverride("font_color", valid ? Style.Good : Style.Amber);
            // the clue token drawn as in the player area (the token with its number on it)
            foreach (var (holder, row) in tokensOf)
            {
                foreach (var ch in holder.GetChildren()) ch.QueueFree();
                var tok = Ui.TokenNumber(AmountKind(row).Token, values[row.Id], 64); tok.MouseFilter = MouseFilterEnum.Ignore; holder.AddChild(tok);
                tok.Position = new Vector2((holder.CustomMinimumSize.X - tok.CustomMinimumSize.X) / 2, 0);
            }
            ok.Visible = valid;
        }
        foreach (var r in rows)
        {
            var who = m.Investigators.FirstOrDefault(i => i.Id == r.Investigator) ?? m.Investigators.FirstOrDefault(i => i.Id == r.Id);
            var card = new VBoxContainer { Alignment = BoxContainer.AlignmentMode.Center }; card.AddThemeConstantOverride("separation", 8); cards.AddChild(card);
            var holder = new Control { CustomMinimumSize = new Vector2(150, 66), MouseFilter = MouseFilterEnum.Ignore }; card.AddChild(holder);
            tokensOf.Add((holder, r));
            // the investigator as a redrawn small card (the 0712 web mini art), with a clear layer on top that takes the clicks:
            // left click adds a clue, right click takes one back
            var face = new Control { CustomMinimumSize = new Vector2(150, 232), MouseFilter = MouseFilterEnum.Ignore }; card.AddChild(face);
            var kind = AmountKind(r);
            if (who is not null)
            {
                var cv = new CardView { Code = who.Code, Picture = Ui.Mini(who.Code), CustomMinimumSize = new Vector2(150, 232), Size = new Vector2(150, 232), ShowCost = false, HoverZoom = false, Actionable = true };
                face.AddChild(cv);
            }
            else
            {
                // a row that is not a person (damage / horror to cancel…): a framed tile with the kind's name
                var tile = new PanelContainer { MouseFilter = MouseFilterEnum.Ignore, Size = new Vector2(150, 232) };
                var tsb = new StyleBoxFlat { BgColor = new Color(0, 0, 0, 0.45f), BorderColor = new Color("ffcf6e"), AntiAliasing = true }; tsb.SetBorderWidthAll(2); tsb.SetCornerRadiusAll(10); tile.AddThemeStyleboxOverride("panel", tsb);
                var tcol = new VBoxContainer { Alignment = BoxContainer.AlignmentMode.Center, MouseFilter = MouseFilterEnum.Ignore }; tile.AddChild(tcol);
                var big = Ui.Image(Ui.Stat(kind.Token), new Vector2(96, 96)); big.MouseFilter = MouseFilterEnum.Ignore; big.SizeFlagsHorizontal = SizeFlags.ShrinkCenter; tcol.AddChild(big);
                var nm = Style.Text(kind.Name, 26, Style.HoverText, Style.Kai); nm.HorizontalAlignment = HorizontalAlignment.Center; tcol.AddChild(nm);
                face.AddChild(tile);
            }
            var hit = new Control { CustomMinimumSize = new Vector2(150, 232), Size = new Vector2(150, 232), MouseFilter = MouseFilterEnum.Stop, MouseDefaultCursorShape = CursorShape.PointingHand, TooltipText = "左键 +1 " + kind.Name + "，右键 −1" };
            face.AddChild(hit);
            var row = r;
            hit.GuiInput += e =>
            {
                if (e is not InputEventMouseButton { Pressed: true } mb) return;
                if (mb.ButtonIndex == MouseButton.Left && values[row.Id] < row.Maximum) values[row.Id]++;
                else if (mb.ButtonIndex == MouseButton.Right && values[row.Id] > row.Minimum) values[row.Id]--;
                else return;
                hit.AcceptEvent(); Refresh();
            };
            var note = Style.Text("最多 " + r.Maximum, 16, Style.PaperDim, Style.Kai); note.HorizontalAlignment = HorizontalAlignment.Center; card.AddChild(note);
        }
        ok = Ui.Button("确认", async () => { if (questionScrim is not null) App.I.CloseModal(questionScrim); await Do("提交数量", () => B.Amounts(state!, values)); await Advance(); }, 26, true);
        ok.CustomMinimumSize = new Vector2(320, 64); ok.SizeFlagsHorizontal = SizeFlags.ShrinkCenter;
        col.AddChild(ok);
        Refresh();
        questionScrim = App.I.Modal(Ui.Panel(col, 36), false);
    }

    void GenericDialog()
    {
        var m = model!;
        var col = new VBoxContainer { CustomMinimumSize = new Vector2(640, 0) }; col.AddThemeConstantOverride("separation", 12);
        col.AddChild(Style.Title(m.Question.Title, 32));
        foreach (var c in m.Question.Choices.Where(c => c.Enabled)) { var b = Ui.Button(c.Title, () => Choose(c), 22); b.CustomMinimumSize = new Vector2(0, 58); col.AddChild(b); }
        questionScrim = App.I.Modal(Ui.Panel(col, 36), false);
    }

    void ResultDialog()
    {
        var m = model!; var outcome = J.Get(m.Game, "outcome");
        bool won = J.String(outcome, "result") == "Won";
        var res = J.Get(J.Get(outcome, "ending"), "resolution");
        string resTag = J.Tag(res); string resText = resTag switch { "NoResolution" => "未达成结局", "Resolution" => "结局 " + J.Get(res, "contents").GetRawText(), _ => resTag };
        var row = new HBoxContainer(); row.AddThemeConstantOverride("separation", 30);
        // The picture melts into the dark panel: black fades on all four edges, so no hard image border shows.
        var pic = new Control { CustomMinimumSize = new Vector2(720, 405), MouseFilter = MouseFilterEnum.Ignore };
        string resKey = resTag == "Resolution" ? "R" + J.Get(res, "contents").GetRawText() : "";
        // an official "no resolution" that sends you to resolution N (迷失时空 → 结局 4): say so and show that resolution's picture
        if (resTag == "NoResolution")
        {
            string official = ResolutionText(ScenarioCode(m), res, "title");
            var to = System.Text.RegularExpressions.Regex.Match(official, @"结局 (\d)$");
            if (to.Success) { resText = official; resKey = "R" + to.Groups[1].Value; }
        }
        var picImg = Ui.Image(Ui.Bg(scenario is null ? "exterior" : resKey.Length > 0 ? scenario.Ending(resKey) : scenario.ExteriorFor(model!.Game)), new Vector2(720, 405), true);
        pic.AddChild(picImg); picImg.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        Ui.EdgeFade(pic, new Color(0.03f, 0.025f, 0.02f, 1f), 140, 110, 140, 110);
        row.AddChild(pic);
        var col = new VBoxContainer { CustomMinimumSize = new Vector2(520, 0) }; col.AddThemeConstantOverride("separation", 16); row.AddChild(col);
        col.AddChild(Style.Title(won ? "调查完成" : "调查结束", 44));
        col.AddChild(Style.Text(resText, 26, won ? Style.Good : Style.Danger, Style.Kai));
        col.AddChild(Style.Text("本局已自动保存。可以在“继续调查”中回顾牌桌。", 21, Style.PaperDim));
        col.AddChild(Ui.Spacer(0, 0, true));
        col.AddChild(Ui.Button("返回主菜单", () => { var mm = new MainMenu(); App.I.Go(mm); mm.Ready(); }, 24, true));
        Control scrim = null!; col.AddChild(Ui.Button("查看牌桌", () => App.I.CloseModal(scrim), 22));
        scrim = App.I.Modal(Ui.Panel(row, 40), false, 0.78f);
    }

    void ChooseDeckDialog()
    {
        var col = new VBoxContainer { CustomMinimumSize = new Vector2(760, 0) }; col.AddThemeConstantOverride("separation", 10);
        col.AddChild(Style.Title("选择这名调查员的牌组", 32));
        // the scroll gets the height (on the list alone the scroll collapsed to nothing and no deck showed — 11-22)
        var list = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill }; var deckScroll = Ui.Scroll(list); deckScroll.CustomMinimumSize = new Vector2(740, 520); col.AddChild(deckScroll);
        foreach (var d in S.Decks.All)
        {
            var deck = d.Value;
            list.AddChild(Ui.Button(S.Name(J.String(deck, "investigator_code")) + " · " + J.String(deck, "name", d.Key), async () => { App.I.CloseAllModals(); var before = state!.Sequence; await Do("带入牌组", () => B.SelectDeck(state!, deck)); if (state!.Sequence != before) StartCarry(gameId, Array.FindIndex(state.Seats, s => s.Player == state.Player), deck); await Advance(); }, 22));
        }
        App.I.Modal(Ui.Panel(col, 36), false);
    }

    // ───────────────────────── overlays
    void OpenMap()
    {
        if (model is null || scenario is null) return;
        var m = model;
        var size = new Vector2(1500, 844);
        var holder = new Control { CustomMinimumSize = size };
        bool painted = scenario.PaintedMap;
        if (painted) { var img = Ui.Image(Ui.Bg(scenario.MapImage), size); holder.AddChild(img); }
        else
        {
            // no painted map (the Dunwich Legacy): the locations as pictures on the core's layout grid, joined by their connections
            var ground = new Panel { Size = size, MouseFilter = MouseFilterEnum.Ignore };
            ground.AddThemeStyleboxOverride("panel", Style.Box(new Color(0.03f, 0.03f, 0.035f, 0.92f), new Color(0, 0, 0, 0), 0, 10)); holder.AddChild(ground);
            Vector2 Centre(Entity l) => scenario.Hotspot(m, m.Label(l)) is float[] h ? new Vector2((h[0] + h[2] / 2) * size.X, (h[1] + h[3] / 2) * size.Y) : new Vector2(-1, -1);
            var drawn = new HashSet<string>();
            foreach (var a in m.Locations)
                foreach (var bid in J.Items(J.Get(a.Raw, "connectedLocations")).Select(x => x.GetString() ?? ""))
                {
                    var b = m.Location(bid); if (b is null || !drawn.Add(string.CompareOrdinal(a.Id, bid) < 0 ? a.Id + bid : bid + a.Id)) continue;
                    Vector2 p = Centre(a), q = Centre(b); if (p.X < 0 || q.X < 0) continue;
                    holder.AddChild(new Line2D { Points = new[] { p, q }, Width = 3, DefaultColor = new Color(Style.Brass, 0.45f), Antialiased = true });
                }
        }
        Control scrim = null!;
        foreach (var loc in m.Locations)
        {
            var hs = scenario.Hotspot(m, m.Label(loc)); if (hs is null) continue;
            var r = new Rect2(hs[0] * size.X, hs[1] * size.Y, hs[2] * size.X, hs[3] * size.Y);
            bool rev = m.Revealed(loc); string key = "loc:" + loc.Id; bool act = byAnchor.ContainsKey(key);
            var spot = new Button { Position = r.Position, Size = r.Size, Flat = true, FocusMode = FocusModeEnum.None, MouseDefaultCursorShape = CursorShape.PointingHand };
            spot.AddThemeStyleboxOverride("normal", Style.Box(new Color(0, 0, 0, rev ? 0.05f : 0.55f), act ? Style.Amber : new Color(Style.Brass, 0.45f), act ? 3 : 1, 8));
            spot.AddThemeStyleboxOverride("hover", Style.Box(new Color(1, 0.9f, 0.7f, 0.12f), Style.HoverText, 3, 8));
            holder.AddChild(spot);
            if (!painted)
            {
                // the location's picture inside its box (its unrevealed side while unrevealed), darkened under the text
                string code = CoreCatalog.Normalize(loc.Code);
                var art = new TextureRect { Texture = rev ? Ui.Art(code) ?? Ui.Card(code) : UnrevealedArt(code), ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize, StretchMode = TextureRect.StretchModeEnum.KeepAspectCovered, MouseFilter = MouseFilterEnum.Ignore, Modulate = rev ? new Color(0.75f, 0.73f, 0.7f) : new Color(0.45f, 0.45f, 0.48f) };
                spot.ClipContents = true; spot.AddChild(art); art.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
                var fade = new Control { MouseFilter = MouseFilterEnum.Ignore }; spot.AddChild(fade); fade.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
                Ui.EdgeFade(fade, new Color(0, 0, 0, 0.7f), 0, r.Size.Y * 0.55f, 0, 0);
            }
            var info = new VBoxContainer { MouseFilter = MouseFilterEnum.Ignore }; spot.AddChild(info); info.Position = new Vector2(10, 8);
            string place = scenario.Place(m.Label(loc));
            var name = Style.Text(m.Name(loc) + (place.Length > 0 ? " · " + place : "") + (rev ? "" : "（未揭示）"), 24, Style.HoverText, Style.Kai); name.AddThemeConstantOverride("outline_size", 8); name.AddThemeColorOverride("font_outline_color", Colors.Black); info.AddChild(name);
            var line = new HBoxContainer(); info.AddChild(line);
            if (m.Clues(loc) > 0) line.AddChild(CardView.Badge("clue", m.Clues(loc), 34));
            // investigators stand on the map as small upright portrait cards (Yunke 11-18: no round investigator tokens)
            foreach (var inv in m.InvestigatorsAt(loc.Id))
            {
                var tok = InvToken(m, inv, 70, false, null);
                tok.MouseFilter = MouseFilterEnum.Ignore;
                var shade = new PanelContainer { MouseFilter = MouseFilterEnum.Ignore };
                var hsb = new StyleBoxFlat { BgColor = new Color(0, 0, 0, 0), ShadowColor = new Color(0, 0, 0, 0.65f), ShadowSize = 7 }; hsb.SetCornerRadiusAll(6);
                shade.AddThemeStyleboxOverride("panel", hsb); shade.AddChild(tok); line.AddChild(shade);
            }
            int en = m.EnemiesAt(loc.Id).Count(); if (en > 0) { var e = Style.Text("敌人 ×" + en, 22, Style.Danger); e.AddThemeConstantOverride("outline_size", 6); e.AddThemeColorOverride("font_outline_color", Colors.Black); line.AddChild(e); }
            if (act) { var hint = Style.Text(string.Join(" / ", byAnchor[key].Select(c => c.Title)), 20, Style.Amber); hint.AddThemeConstantOverride("outline_size", 6); hint.AddThemeColorOverride("font_outline_color", Colors.Black); info.AddChild(hint); }
            spot.Pressed += () => { App.I.CloseModal(scrim); ActOn(key, spot, loc.Code); };
        }
        var col = new VBoxContainer(); col.AddChild(Style.Title(scenario.MapName, 34)); col.AddChild(holder);
        col.AddChild(Ui.Button("关闭", () => App.I.CloseModal(scrim), 22));
        scrim = App.I.Modal(Ui.Panel(col, 24), true, 0.7f);
    }

    void OpenChaosBag()
    {
        if (model is null) return;
        var col = new VBoxContainer { CustomMinimumSize = new Vector2(760, 0) }; col.AddThemeConstantOverride("separation", 14);
        col.AddChild(Style.Title("混沌袋 · " + DiffName(model.Difficulty), 34));
        // every token on its own, no "×2" (Yunke 11-18 #7)
        var grid = new GridContainer { Columns = 8 }; grid.AddThemeConstantOverride("h_separation", 12); grid.AddThemeConstantOverride("v_separation", 12); col.AddChild(grid);
        foreach (var f in model.ChaosFaces.OrderBy(f => f))
        { var ic = Ui.Image(Ui.Token(f), new Vector2(80, 80)); ic.TooltipText = TokenName(f); ic.MouseFilter = MouseFilterEnum.Pass; grid.AddChild(ic); }
        Control scrim = null!; col.AddChild(Ui.Button("关闭", () => App.I.CloseModal(scrim), 22));
        scrim = App.I.Modal(Ui.Panel(col, 34));
    }

    // the table's keys, listed (Yunke 11-20: a way to see them from the in-game menu)
    void ShowShortcuts()
    {
        var col = new VBoxContainer { CustomMinimumSize = new Vector2(620, 0) }; col.AddThemeConstantOverride("separation", 10);
        col.AddChild(Style.Title("快捷键", 34));
        var grid = new GridContainer { Columns = 2 }; grid.AddThemeConstantOverride("h_separation", 28); grid.AddThemeConstantOverride("v_separation", 10); col.AddChild(grid);
        foreach (var (key, what) in new[] {
            ("Esc", "打开菜单 / 关闭窗口"), ("Tab", "打开或收起边栏（调查记录）"), ("M", "地图全屏 / 回到牌桌"), ("Home", "地图缩放到看全图"),
            ("F", "指出现在能点的地方"), ("D", "抽遭遇牌"), ("T", "展开或收起所有队友的装备和手牌"), ("U / Ctrl+Z", "撤回一步") })
        {
            var k = Style.Text(key, 22, Style.Amber, Style.Sans); grid.AddChild(k);
            grid.AddChild(Style.Text(what, 22, Style.Paper, Style.Kai));
        }
        Control scrim = null!; col.AddChild(Ui.Button("关闭", () => App.I.CloseModal(scrim), 22));
        scrim = App.I.Modal(Ui.Panel(col, 34));
    }

    // the save's heading as 继续调查 shows it (an online guest has no save list of its own)
    string DebugTitle()
    {
        try { if (!OnlineGame.IsGuest && S.Backend.SavedGames().FirstOrDefault(g => g.Id == state!.Id.ToString("D")) is { } g) return ContinueDialog.Heading(g); } catch { }
        return "存档";
    }

    // QQ 11-23 梦境结晶器: commit choices from outside the hand are drawn in the test panel (false: the old loose button only;
    // used once to export a base release for the hot update test)
    const bool CommitFromElsewhere = true;

    // 调整控制权: each investigator and who plays it
    void ShowOwners()
    {
        if (state is null || model is null) return;
        var col = new VBoxContainer { CustomMinimumSize = new Vector2(760, 0) }; col.AddThemeConstantOverride("separation", 14);
        col.AddChild(Style.Title("调整控制权", 36));
        var hint = Style.Text("把调查员交给房间里的任何一位玩家，或者大家一起控制。玩家掉线时，他的调查员暂由你控制，回来后自动还给他。", 20, Style.PaperDim, Style.Kai);
        hint.AutowrapMode = TextServer.AutowrapMode.WordSmart; hint.CustomMinimumSize = new Vector2(720, 0); col.AddChild(hint);
        var options = new List<(string Id, string Name)> { ("", "你（建房者）") };
        foreach (var (id, name) in OnlineGame.Peers) options.Add((id, name));
        options.Add((OnlineGame.Everyone, "大家一起"));
        options.Add((OnlineGame.Ai, "AI 控制（开发中）"));
        foreach (var seat in state.Seats.OrderBy(x => x.Number))
        {
            var inv = model.Investigators.FirstOrDefault(i => J.String(i.Raw, "playerId") == seat.Player.ToString("D"));
            var row = new HBoxContainer(); row.AddThemeConstantOverride("separation", 16); col.AddChild(row);
            var name = Style.Text("调查员 " + seat.Number + (inv is not null ? " · " + model.Name(inv) : ""), 24, Style.Paper, Style.Kai);
            name.SizeFlagsHorizontal = SizeFlags.ExpandFill; name.SizeFlagsVertical = SizeFlags.ShrinkCenter; row.AddChild(name);
            var pick = new OptionButton { FitToLongestItem = false, ClipText = true, CustomMinimumSize = new Vector2(260, 52) }; Ui.Soften(pick); pick.AddThemeFontSizeOverride("font_size", 21);
            for (int k = 0; k < options.Count; k++) { pick.AddItem(options[k].Name); if (options[k].Id == OnlineGame.Ai) pick.SetItemDisabled(k, true); }
            pick.Selected = Math.Max(0, options.FindIndex(o => o.Id == OnlineGame.Owner(seat.Player)));
            var p = seat.Player;
            pick.ItemSelected += idx => OnlineGame.SetOwner(p, options[(int)idx].Id);
            row.AddChild(pick);
        }
        Control scrim = null!; col.AddChild(Ui.Button("完成", () => App.I.CloseModal(scrim), 22, true));
        scrim = App.I.Modal(Ui.Panel(col, 34));
    }

    void OpenMenu()
    {
        // the title screen's menu over the table (Yunke 11-19): a black fade from the left, plain lines that light up
        Control scrim = null!;
        scrim = Ui.SideMenu("菜单", "每个操作都已自动保存。", new (string, Action)[] {
            ("继续游戏", () => App.I.CloseModal(scrim)),
            ("设置", () => { App.I.CloseModal(scrim); SettingsDialog.Open(); }),
            ("快捷键", () => { App.I.CloseModal(scrim); ShowShortcuts(); }),
            // the host of an online game: hand any seat to another player, to everyone, or back (Yunke 11-23)
            ("调整控制权", () => { App.I.CloseModal(scrim); ShowOwners(); }),
            // 导出存档 (Yunke 11-23: "把导出调试信息换成导出存档，现在没有调试模式了"): this game's save, the same file as 继续调查's
            // 导出 (importable there). An online guest has no save of its own (the host keeps it): not offered to a guest.
            ("导出存档", () => { App.I.CloseModal(scrim); ExportThisSave(); }),
            ("返回主菜单", () => { var mm = new MainMenu(); App.I.Go(mm); mm.Ready(); }),
            ("退出游戏", () => GetTree().Quit()) }.Where(x => (x.Item1 != "调整控制权" || OnlineGame.IsHost) && (x.Item1 != "导出存档" || !OnlineGame.IsGuest)));
    }

    void ExportThisSave()
    {
        try
        {
            if (state is { } st && S.Backend.SavedGames().FirstOrDefault(g => g.Id == st.Id.ToString("D")) is { } g) { SaveTransfer.Export(g, ContinueDialog.Heading(g)); return; }
        }
        catch (Exception e) { App.I.Toast("导出失败：" + e.Message, true, 5); return; }
        App.I.Toast("这一局没有本机存档，不能导出", true, 4);
    }

    // a skill test's result waits for a click anywhere (the test panel says so)
    (long Seq, ChoicePresentation C)? applyWait;

    public override void _Input(InputEvent e)
    {
        if (applyWait is { } aw && (e is InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Left } || e is InputEventScreenTouch { Pressed: true } || e is InputEventKey { Pressed: true, Keycode: Key.Space or Key.Enter or Key.KpEnter }))
        {
            if (!busy && state is not null && state.Sequence == aw.Seq && !App.I.HasModal) { applyWait = null; GetViewport().SetInputAsHandled(); _ = Choose2Async(aw.C); return; }
        }
        if (e is InputEventMouseButton { Pressed: true } || e is InputEventKey { Pressed: true }) NoteActivity();
        if (e is InputEventMouse mm) pointer = mm.GlobalPosition;   // where the pointer is (the team rows fold when it leaves)
        if (e is not InputEventKey { Pressed: true, Echo: false } k || !IsVisibleInTree()) return;
        bool handled = true;
        if (k.Keycode == Key.Escape && !App.I.HasModal) { if (mapLarge) ToggleMap(); else OpenMenu(); }   // Esc leaves the map focus mode first
        else if (k.Keycode == Key.M && !App.I.HasModal) ToggleMap();   // M: the map full screen / back (Yunke 11-20)
        else if (k.Keycode == Key.Home && !App.I.HasModal) BoardFit();   // Home: the whole map in its frame
        else if (k.Keycode == Key.Tab && !App.I.HasModal) ToggleLog();   // Tab: open / close the side panel (调查记录) — Yunke 11-20 (it was the map)
        else if (k.Keycode == Key.Z && k.CtrlPressed && !App.I.HasModal) Undo();
        else if (k.Keycode == Key.D && !App.I.HasModal && byAnchor.TryGetValue("encdeck", out var draw) && draw.Count > 0) Choose(draw[0]);   // D: draw the encounter card (html keybinding)
        else if (k.Keycode == Key.F && !App.I.HasModal) Locate();   // F: show what can be clicked now
        else if (k.Keycode == Key.U && !k.CtrlPressed && !k.AltPressed && !App.I.HasModal) Undo();   // U: undo the last action
        else if (k.Keycode == Key.T && !App.I.HasModal) { teamOpen = !teamOpen; Render(); }   // T: unfold every team row (Yunke 11-19 r11)
        else handled = false;
        if (handled) GetViewport().SetInputAsHandled();
    }
}
