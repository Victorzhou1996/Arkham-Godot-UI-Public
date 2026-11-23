using Godot;
using Gathering;
using System.Text.Json;

namespace ArkhamUI;

// Rewind, the investigation log and the discard pile, modelled on the 0712 web table:
//  - the top-right 撤回 button opens a menu of rewind points (action, turn, round, scenario start);
//  - 记录 opens a side log whose entries can each be undone "to here";
//  - the discard pile opens its cards, and a card with a choice (for example an ability used from the discard) can be played from there.
public partial class TableScreen
{
    Control? logPanel;
    VBoxContainer? logList;
    readonly HashSet<int> logOpenRounds = new(); int logRoundsSeen = -1;   // which rounds of the log are unfolded
    Control? discardPileNode;
    Control? deckPileNode; Rect2 deckTopFrame;   // the draw pile and its top card's frame inside it (draw flights start there)
    Button? undoButton;
    readonly List<long> logSequences = new();   // top-level log entries, newest first, as shown

    void RegisterHistoryQa()
    {
        QaScript.Register("undo-menu", _ => { if (undoButton is not null) UndoMenu(undoButton); });
        QaScript.Register("undo-to", a => UndoTo(a));
        QaScript.Register("undo-scenario", _ => RunUndo("scenario"));   // QA: straight back to the scenario's start (no confirm dialog)
        QaScript.Register("log-open", a => { if (int.TryParse(a, out int r)) { logOpenRounds.Add(r); RefreshLog(); } });   // QA: unfold round r of the log
        QaScript.Register("undo-log", a => { if (a == "last" && logSequences.Count > 0) UndoToLog(logSequences[^1]); else if (int.TryParse(a, out int i) && i >= 0 && i < logSequences.Count) UndoToLog(logSequences[i]); });
        QaScript.Register("discard", _ => OpenDiscard());
        QaScript.Register("handpad", a => { if (int.TryParse(a, out int n)) { qaHandPad = n; Render(); } });
        // QA only: fire the change effects directly, so a capture can show them mid-animation.
        QaScript.Register("fx", a =>
        {
            var me = model?.Me;
            GD.Print("QA_FX " + a + " me=" + (me is null ? "null" : "ok") + " tokens=" + tokenNodes.Count + " res=" + (tokenNodes.TryGetValue("resource", out var rn) ? (IsInstanceValid(rn) ? "valid" : "freed") : "missing") + " fan=" + fan.Count + " reduced=" + S.Settings.ReducedMotion);
            if (me is null) return;
            switch (a)
            {
                case "spend":
                    Punch(tokenNodes.GetValueOrDefault("resource"), Style.Amber); Float(model!.Name(me) + "  -2 资源", Style.Amber, 0);
                    { var rn2 = tokenNodes.GetValueOrDefault("resource"); GD.Print("QA_FX_SPEND scale=" + (rn2 is null ? "null" : rn2.Scale.ToString()) + " children=" + GetChildCount() + " vp=" + GetViewportRect().Size); }
                    break;
                case "damage": FlyChip("health", "-2", Style.Danger); break;
                case "horror": FlyChip("horror", "-1", new Color("8fb6cb")); break;
                case "denied": if (fan.Count > 0) Deny(fan[0].Card, fan[0].Card.Code); break;
            }
        });
        // QA only: print the chaos tokens the current skill test drew, to check repeated draws.
        QaScript.Register("testlog", _ =>
        {
            if (model is null || !model.InSkillTest) { GD.Print("QA_TESTLOG none"); return; }
            var st = model.SkillTest;
            var faces = string.Join(",", J.Items(J.Get(st, "resolvedChaosTokens")).Select(t => J.String(t, "chaosTokenFace")));
            GD.Print("QA_TESTLOG action=" + J.String(st, "action") + " chaos=" + faces + " result=" + J.Tag(J.Get(st, "result")));
        });
    }

    // ───────────────────────── rewind

    // Rewind points the core currently offers: the turn, round and scenario starts are reachable once the game has moved past them.
    bool UndoAvailable(string boundary)
    {
        if (model is null || state is null) return false;
        var g = model.Game;
        if (boundary == "step") return state.PreviousSequence is not null;
        if (boundary == "action") return state.ActionStartSequence is not null;
        int steps = J.Int(g, "scenarioSteps");
        if (boundary == "scenario") return steps > 1;
        string field = boundary == "turn" ? "undoTurnStep" : "undoRoundStep";
        var v = J.Get(g, field);
        return v.ValueKind == JsonValueKind.Number && steps > v.GetInt32();
    }

    // Ctrl+Z / U and the first menu entry: one step back, whatever the step was (a drawn token included).
    public void Undo() => UndoTo("step");

    void UndoMenu(Control anchor)
    {
        if (busy || state is null) return;
        var col = new VBoxContainer { CustomMinimumSize = new Vector2(280, 0) }; col.AddThemeConstantOverride("separation", 4);
        col.AddChild(Style.Text("撤回到", 18, Style.PaperDim));
        foreach (var (label, boundary) in new[] { ("上一步", "step"), ("行动开始", "action"), ("回合开始", "turn"), ("一轮开始", "round"), ("剧本开始", "scenario") })
        {
            var b = Ui.MinimalButton(label, () => { ClosePopup(); UndoTo(boundary); }, 21);
            b.Disabled = !UndoAvailable(boundary); b.Alignment = HorizontalAlignment.Left;
            b.CustomMinimumSize = new Vector2(260, 48);
            col.AddChild(b);
        }
        DropPanel(anchor, col);
    }

    public void UndoTo(string boundary)
    {
        if (busy || state is null) return;
        if (boundary == "scenario") { Confirm("回到剧本开始？之后的所有进度都会被撤销。", () => RunUndo(boundary)); return; }
        RunUndo(boundary);
    }

    async void RunUndo(string boundary)
    {
        skipFeedback = true;   // an undo restores numbers; it must not float them as gains or losses
        AfterUndo();
        if (boundary == "step")
        {
            // Step back past states the table answers by itself (a lone forced effect, the start of a test with nothing
            // to commit, ...): landing on one would replay it at once and the undo would seem to do nothing.
            await Busy("撤回…", async () =>
            {
                for (int i = 0; i < 40; i++)
                {
                    var back = await B.UndoStep(state!);
                    if (back is null) break;
                    state = back;
                    if (!AnswersItself(new TableModel(state, S))) break;
                }
            });
            await Advance();
            return;
        }
        await Busy("撤回…", async () => { state = await B.UndoBoundary(state!, boundary); });
        await Advance();
    }

    // An undo moves the game backwards: enemies already defeated come back, the encounter deck grows, an advanced act turns
    // back. None of that is new, so the table's "what just happened" trackers start over from the restored state instead of
    // reading it as a fresh draw, spawn, flip or phase (playtest 11-18 #14: an undo showed an encounter card being drawn).
    void AfterUndo()
    {
        drawsPrimed = false; lastEncounterDeck = -1; knownEnemies = new(); lastForcedSeq = -1;
        lastActCode = null; lastAgendaCode = null; lastPhase = null; shownFromWindow.Clear();
        // a card drawn again after the undo (the same card, same id, when the deck order comes back) is turned over again
        // (Yunke 11-22: 黛西's encounter card redrawn after an undo showed no draw)
        windowRevealedIds.Clear();
        // nor do cards that leave the table by the undo fly into a deck (QA hw6: 撤回到冒险开始 sent three cards to the deck)
        tableWas.Clear(); deckWas = -1; encWas = -1;
        drawnFlown = "";   // a drawn encounter card shown again after the undo flies in again
    }

    // True when Advance() would answer this state without the player (so an undo should not stop on it).
    bool AnswersItself(TableModel m)
    {
        if (m.QuestionTag is "PickScenarioSettings" or "PickCampaignSettings" or "ChooseDeck") return true;
        if (AutoPolicy.Decide(m, S.Settings.AutoSkip) is not null) return true;
        var on = m.Question.Choices.Where(c => c.Enabled).ToArray();
        return S.Settings.AutoSkip && on.Length == 1 && on[0].Tag == "SkillTestApplyResultsButton";
    }

    // ───────────────────────── log (side panel; entries can be undone to)

    void OpenLog() => ToggleLog();

    void ToggleLog()
    {
        if (logPanel is not null && IsInstanceValid(logPanel)) { logPanel.QueueFree(); logPanel = null; logList = null; return; }
        var vp = GetViewportRect().Size; float w = 520;
        var col = new VBoxContainer { CustomMinimumSize = new Vector2(w - 60, 0) }; col.AddThemeConstantOverride("separation", 10);
        var head = new HBoxContainer(); head.AddThemeConstantOverride("separation", 6); head.AddChild(Style.Title("调查记录", 28)); head.AddChild(Ui.Spacer(0, 0, true));
        // quick undo to the start of the action, the round or the scenario, beside the title (Yunke 11-20)
        foreach (var (label, boundary) in new[] { ("行动", "action"), ("一轮", "round"), ("冒险", "scenario") })
        {
            var ub = Ui.MinimalButton(label, () => UndoTo(boundary), 15);   // words only (Yunke 11-22)
            ub.Disabled = !UndoAvailable(boundary); ub.TooltipText = "撤回到" + label + "开始"; ub.CustomMinimumSize = new Vector2(0, 34);
            head.AddChild(ub);
        }
        head.AddChild(Ui.MinimalButton("收起", ToggleLog, 18));
        col.AddChild(head);
        col.AddChild(Style.Text("↶ 撤回到行动、回合、阶段的开始；只退一小步按 U", 16, Style.PaperDim, Style.Kai));
        logList = new VBoxContainer { MouseFilter = Control.MouseFilterEnum.Ignore }; logList.AddThemeConstantOverride("separation", 8);
        var scroll = Ui.Scroll(logList); logScroll = scroll; logCol = col;
        col.AddChild(scroll);
        // flush with the window's right edge, the contents kept in from it (Yunke 11-22: the undo arrows were cut off at the edge)
        var pad = new MarginContainer { MouseFilter = MouseFilterEnum.Ignore }; pad.AddThemeConstantOverride("margin_right", 34); pad.AddChild(col);
        logPanel = Ui.Panel(pad, 26, 0.96f);
        logPanel.ZIndex = 80;
        AddChild(logPanel);
        LayoutLog();
        RefreshLog();
    }

    // The sidebar's place and size from the window as it is now (Yunke 11-22: dragging the window narrower left it cut off
    // at the right): right-aligned, at most 520 wide and never more than 44% of the width, full height below the top bar.
    ScrollContainer? logScroll; Control? logCol; Vector2 logForSize;
    void LayoutLog()
    {
        if (logPanel is null || !IsInstanceValid(logPanel)) return;
        var vp = GetViewportRect().Size; logForSize = vp;
        float w = Mathf.Clamp(vp.X * 0.44f, 360, 520);
        if (logCol is not null && IsInstanceValid(logCol)) logCol.CustomMinimumSize = new Vector2(w - 94, 0);
        if (logScroll is not null && IsInstanceValid(logScroll)) logScroll.CustomMinimumSize = new Vector2(w - 94, Mathf.Max(200, vp.Y - 260));
        logPanel.ResetSize(); logPanel.Size = new Vector2(w, Mathf.Max(260, vp.Y - 130));
        // placed by the width it really takes, so nothing hangs past the edge
        logPanel.Position = new Vector2(vp.X - logPanel.Size.X, 110);
    }
    void FollowWindowSize()
    {
        if (logPanel is null || !IsInstanceValid(logPanel)) return;
        if (GetViewportRect().Size != logForSize) LayoutLog();
    }

    async void RefreshLog()
    {
        if (logList is null || !IsInstanceValid(logList) || state is null) return;
        try
        {
            var history = await B.Logs(state);
            if (logList is null || !IsInstanceValid(logList)) return;
            Ui.Clear(logList);
            // Names in the log are coloured links (investigators amber, player cards by class, encounter cards red); hovering
            // one shows its card (playtest 2 #6). The renderer gives each name through a placeholder, so the rest of the
            // text can be escaped for BBCode first.
            var names = new List<(string Code, string Name)>();
            var renderer = new ClientLogText(S.Locale, r =>
            {
                if (r.CardCode is not string code) return null;
                string nm = CardReader.ShownName(code); if (nm.Length == 0) return null;   // an unrevealed place by its unrevealed side
                names.Add((CoreCatalog.Normalize(code), nm));
                return "\u0001" + (names.Count - 1) + "\u0002";
            });
            string Rich(string body)
            {
                body = TidyLog(body).Replace("[", "[lb]");
                // ability and token symbols (ClientLogText marks them \u0003name\u0004): the game's symbol font, in the log's amber
                body = System.Text.RegularExpressions.Regex.Replace(body, "\u0003(\\w+)\u0004", mt => LogSymbol(mt.Groups[1].Value));
                return System.Text.RegularExpressions.Regex.Replace(body, "\u0001(\\d+)\u0002", mt =>
                {
                    var (code, nm) = names[int.Parse(mt.Groups[1].Value)];
                    return "[url=" + code + "][color=#" + LogColor(code).ToHtml(false) + "]" + nm + "[/color][/url]";
                });
            }
            static string LogSymbol(string name)
            {
                string key = name switch { "free" => "fast", "elderSign" => "elder_sign", "autoFail" => "auto_fail", _ => name };
                if (Style.Glyph.TryGetValue(key, out var g)) return "[font=res://assets/fonts/arkham.ttf][color=#e8c37a]" + g + "[/color][/font]";
                return name switch { "skull" => "骷髅", "cultist" => "异教徒", "tablet" => "石板", "elder_thing" or "elderThing" => "远古之物", _ => "【" + name + "】" };
            }
            // Only a top-level entry offers "undo to here": the state the entry was written in is the row key's host sequence.
            logSequences.Clear();
            void Entry(ClientLogEntry e, int depth, long seq)
            {
                var l = new RichTextLabel { BbcodeEnabled = true, FitContent = true, ScrollActive = false, AutowrapMode = TextServer.AutowrapMode.WordSmart, MouseFilter = Control.MouseFilterEnum.Pass, MetaUnderlined = false };
                l.AddThemeFontSizeOverride("normal_font_size", depth == 0 ? 20 : 18);
                if (Style.Kai is not null) l.AddThemeFontOverride("normal_font", Style.Kai);
                l.AddThemeColorOverride("default_color", depth == 0 ? Style.Paper : Style.PaperDim);
                l.Text = new string('　', depth) + Rich(renderer.Body(e));
                l.MetaHoverStarted += meta => Peek.ShowCard(l, meta.AsString(), null, 0.25);
                l.MetaHoverEnded += _ => Peek.Hide();
                l.MetaClicked += meta => CardReader.Open(meta.AsString());
                // Undo points only where a player starts something — an action, a played card, a turn, a round or a phase
                // (Yunke 11-19 r11 #7); the small steps in between are one press of U each.
                if (depth == 0 && IsUndoPoint(e))
                {
                    logSequences.Add(seq);
                    var line = new HBoxContainer(); line.AddThemeConstantOverride("separation", 8);
                    l.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill; line.AddChild(l);
                    // the return arrow (Yunke 11-22: his icon, the arrow only) instead of the words "撤回到此处"
                    var back = LineIcon.Button("undo", "撤回到这条记录之前", () => Confirm("撤回到这条记录之前？之后的所有行动都会被撤销。", () => UndoToLog(seq)), 30, false, new Color("e8c37a"));
                    back.SizeFlagsVertical = Control.SizeFlags.ShrinkBegin;
                    line.AddChild(back);
                    // hovering the arrow lights the whole line, so it is clear which entry the undo goes back to (Yunke 11-22)
                    var band = new PanelContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
                    var off = new StyleBoxFlat { BgColor = new Color(0, 0, 0, 0), ContentMarginLeft = 0, ContentMarginRight = 0, ContentMarginTop = 0, ContentMarginBottom = 0, ExpandMarginLeft = 8, ExpandMarginRight = 2, ExpandMarginTop = 2, ExpandMarginBottom = 2, AntiAliasing = true };   // drawn around the line, not pushing it
                    off.SetCornerRadiusAll(8);
                    var on = (StyleBoxFlat)off.Duplicate(); on.BgColor = new Color(0.91f, 0.76f, 0.48f, 0.16f); on.BorderColor = new Color(0.91f, 0.76f, 0.48f, 0.55f); on.BorderWidthLeft = 3;
                    band.AddThemeStyleboxOverride("panel", off); band.AddChild(line);
                    back.MouseEntered += () => band.AddThemeStyleboxOverride("panel", on);
                    back.MouseExited += () => band.AddThemeStyleboxOverride("panel", off);
                    logList!.AddChild(band);
                }
                else logList!.AddChild(l);
                foreach (var c in e.Children) Entry(c, Math.Min(4, depth + 1), seq);
            }
            // The whole scenario, folded by round (Yunke 11-22): every round is one line; only the open ones draw their entries
            // (the current round is open), so a long scenario stays quick to show. A round opens or closes with a click.
            var rounds = new List<List<ClientLogRow>>();
            foreach (var row in history.Rows)
            {
                bool starts = row.Entry is ClientLogEntry re && re.Body.Count > 0 && re.Body[0].Tag == "LogI18n" && re.Body[0].Text is "log.round" or "log.scenarioBegins";
                if (rounds.Count == 0 || starts) rounds.Add(new());
                rounds[^1].Add(row);
            }
            if (logRoundsSeen != rounds.Count) { logOpenRounds.Clear(); logOpenRounds.Add(rounds.Count - 1); logRoundsSeen = rounds.Count; }
            void Rows(List<ClientLogRow> list)
            {
                foreach (var row in Enumerable.Reverse(list))
                {
                    if (row.Entry is ClientLogEntry entry) Entry(entry, 0, row.Key.HostSequence);
                    else if (!string.IsNullOrEmpty(row.Text)) logList!.AddChild(Style.Text(RawLog(row.Text), 19, Style.Paper));
                }
            }
            for (int r = rounds.Count - 1; r >= 0; r--)
            {
                bool first = rounds[r].Count > 0 && rounds[r][0].Entry is ClientLogEntry fe && fe.Body.Count > 0 && fe.Body[0].Text is "log.scenarioBegins";
                int roundNo = first ? 0 : rounds.Take(r + 1).Count(g => !(g.Count > 0 && g[0].Entry is ClientLogEntry ge && ge.Body.Count > 0 && ge.Body[0].Text is "log.scenarioBegins"));
                string label = first ? "开场" : roundNo > 0 ? "第 " + roundNo + " 轮" : "更早";
                bool open = logOpenRounds.Contains(r); int idx = r;
                var hb = Ui.MinimalButton((open ? "▾ " : "▸ ") + label + "  · " + rounds[r].Count + " 条", () =>
                {
                    if (!logOpenRounds.Add(idx)) logOpenRounds.Remove(idx);
                    RefreshLog();
                }, 19);
                hb.Alignment = HorizontalAlignment.Left; hb.Modulate = open ? Colors.White : new Color(1, 1, 1, 0.7f);
                logList!.AddChild(hb);
                if (open) Rows(rounds[r]);
            }
            if (App.I.QaRunning) GD.Print("QA_LOG_ROUNDS " + rounds.Count + " rows=" + history.Rows.Count + " open=" + string.Join(",", logOpenRounds) + " drawn=" + logList!.GetChildCount() + " undo=" + logSequences.Count + " oldest=" + (logSequences.Count > 0 ? logSequences[^1] : -1));
            if (logList!.GetChildCount() == 0) logList.AddChild(Style.Text("尚无调查记录。", 20, Style.PaperDim));
        }
        catch (Exception e) { App.I.Toast("记录读取失败：" + e.Message, true); }
    }

    static readonly HashSet<string> UndoPointKeys = new()
    {
        "log.turn", "log.round", "log.phase.investigation", "log.phase.enemy", "log.phase.upkeep", "log.phase.mythos",
        "log.activatesAbility", "log.triggersAbility", "log.playsCard", "log.movesTo", "log.testOpensAgainst", "log.testOpensVs", "log.testOpens",
        "log.gainsResources", "log.drawsCard", "log.engages", "log.scenarioBegins",
    };
    static bool IsUndoPoint(ClientLogEntry e) => e.Body.Count > 0 && e.Body[0].Tag == "LogI18n" && e.Body[0].Text is string k && UndoPointKeys.Contains(k);

    // Colour of a name in the log: investigators amber, encounter cards red, player cards by their class.
    Color LogColor(string code)
    {
        var card = S.Card(code);
        if (J.String(card, "type_code") == "investigator") return Style.Amber;
        if (J.String(card, "encounter_code").Length > 0) return new Color("e07a62");
        return J.String(card, "faction_code") switch
        {
            "guardian" => new Color("7fa8de"), "seeker" => new Color("e3a64a"), "rogue" => new Color("7fc28c"),
            "mystic" => new Color("b89be0"), "survivor" => new Color("e0807a"), _ => new Color("d8cdb5"),
        };
    }

    async void UndoToLog(long rowSequence)
    {
        if (busy || state is null) return;
        skipFeedback = true; AfterUndo();
        await Busy("撤回…", async () => { state = await B.UndoToLogRow(state!, rowSequence); });
        await Advance();
    }

    // ───────────────────────── discard pile: the cards, and any choice they offer

    void OpenDiscard()
    {
        if (model is null || model.Me is null) return;
        var me = model.Me;
        var cards = model.DiscardCards(me).ToArray();
        var col = new VBoxContainer(); col.AddThemeConstantOverride("separation", 16);
        col.AddChild(Style.Title("弃牌堆 · " + cards.Length + " 张（最近弃置的在前）", 26));
        if (cards.Length == 0) col.AddChild(Style.Text("弃牌堆是空的。", 20, Style.PaperDim));
        var grid = new GridContainer { Columns = 6 };
        grid.AddThemeConstantOverride("h_separation", 14); grid.AddThemeConstantOverride("v_separation", 14);
        Control scrim = null!;
        foreach (var (id, code) in cards)
        {
            string key = byAnchor.ContainsKey("hand:" + id) ? "hand:" + id : byAnchor.ContainsKey("asset:" + id) ? "asset:" + id : "";
            var cv = new CardView { Code = code, CustomMinimumSize = new Vector2(110, 154), Size = new Vector2(110, 154), Actionable = key.Length > 0 };
            if (key.Length > 0)
            {
                cv.OnClick = () => { App.I.CloseModal(scrim); if (discardPileNode is not null) ActOn(key, discardPileNode, code); };
            }
            else cv.Modulate = new Color(1, 1, 1, 0.55f);
            grid.AddChild(cv);
        }
        if (cards.Length > 0) { var scroll = Ui.Scroll(grid); scroll.CustomMinimumSize = new Vector2(0, 520); col.AddChild(scroll); }
        col.AddChild(Ui.MinimalButton("关闭", () => App.I.CloseModal(scrim), 22));
        scrim = App.I.Modal(Ui.Panel(col, 30));
    }

    // ───────────────────────── shared: a small menu hung under a button

    void DropPanel(Control anchor, Control content)
    {
        ClosePopup(); Peek.Hide();
        var root = new Control { MouseFilter = MouseFilterEnum.Stop };   // a click outside closes it
        AddChild(root); root.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect); root.ZIndex = 150;
        root.GuiInput += e => { if (e is InputEventMouseButton { Pressed: true }) ClosePopup(); };
        var box = Ui.Panel(content, 14, 0.98f); root.AddChild(box); box.ResetSize();
        var r = anchor.GetGlobalRect(); var vp = GetViewportRect().Size; var s = box.Size;
        box.Position = new Vector2(Math.Clamp(r.End.X - s.X, 12, Math.Max(12, vp.X - s.X - 12)), Math.Clamp(r.End.Y + 8, 12, Math.Max(12, vp.Y - s.Y - 12)));
        popup = root;
    }
}
