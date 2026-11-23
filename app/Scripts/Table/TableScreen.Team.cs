using System.Text.Json;
using Godot;
using Gathering;

namespace ArkhamUI;

// 队伍 (Yunke 11-19 r11, revised from her sketch): one row per investigator in the top-right corner, under the buttons.
// Folded, a row is that investigator's threats (the enemies engaged with them, the cards in their threat area) to the left
// of their portrait, with health and sanity left in two roundels on the portrait's lower corners. Pointing at a row unfolds
// it: their 装备区 (assets in play) and 手牌区 (hand size, resources, clues, actions, where they are) slide out to the right of
// the portrait. T unfolds every row until pressed again. A click on a portrait switches the view to that investigator; the
// threats and assets are small cards that can be acted on (fight an enemy engaged with a teammate, pick their asset).
public partial class TableScreen
{
    const float TeamH = 121, TeamTop = 92;   // 25% larger than the first version (Yunke 11-19 r11), then 15% more (Yunke 11-20)
    readonly HashSet<string> openRows = new();   // rows unfolded by a click, by investigator id (kept across renders)
    bool teamOpen;            // T: every row unfolded
    readonly Dictionary<string, Control> teamDetails = new();   // each unfolded row's panel (for the fold animation)
    string? teamUnfolding;    // the row just unfolded: its panel grows in once
    Vector2 pointer;          // the last pointer position seen (_Input)
    float engagedTop = 206;   // where the 与你交战 column starts (below the team rows)

    void TeamPanel()
    {
        var m = model!;
        var rows = m.State.Seats.Select(s => (Seat: s, Inv: m.Investigators.FirstOrDefault(i => J.String(i.Raw, "playerId") == s.Player.ToString("D"))))
            .Where(x => x.Inv is not null).ToList();
        engagedTop = 206;
        TeamClues(m, rows.Select(r => r.Inv!).ToList());
        if (rows.Count < 2) return;   // alone: nothing to switch to
        var col = new VBoxContainer { MouseFilter = MouseFilterEnum.Ignore, Alignment = BoxContainer.AlignmentMode.Begin }; col.AddThemeConstantOverride("separation", 8);
        Place(col, -1400, TeamTop, -SideMargin, TeamTop + rows.Count * (TeamH + 8), 1, 0, 1, 0);
        teamDetails.Clear();
        foreach (var (seat, inv) in rows) col.AddChild(TeamRow(m, inv!, seat));
        // the unfolded panels take the widest one's width, so the investigators' cards stand in one column (Yunke 11-22)
        Callable.From(() =>
        {
            var shown = teamDetails.Values.Where(d => IsInstanceValid(d) && d.Visible).ToList();
            if (shown.Count < 2) return;
            float w = shown.Max(d => d.GetCombinedMinimumSize().X);
            foreach (var d in shown) d.CustomMinimumSize = new Vector2(w, d.CustomMinimumSize.Y);
        }).CallDeferred();
        engagedTop = TeamTop + rows.Count * (TeamH + 8) + 14;
    }

    // The team's clues, summed, on one clue token in the bottom-right corner, above the end-turn button (Yunke 11-20: it sat
    // badly at the head of the team column) — 15% smaller than it was there (54 → 46), its number the same size (33)
    void TeamClues(TableModel m, List<Entity> invs)
    {
        const float H = 46; const int Digits = 33;
        int teamClues = invs.Sum(i => m.Clues(i));
        var tok = Ui.TokenNumber("clue", teamClues, H); tok.MouseFilter = MouseFilterEnum.Pass;
        foreach (var l in tok.GetChildren().OfType<Label>()) { l.AddThemeFontSizeOverride("font_size", Digits); Ui.CenterDigits(l); }
        tok.TooltipText = "全队线索 " + teamClues + (invs.Count > 1 ? "\n" + string.Join("　", invs.Select(i => m.Name(i) + " " + m.Clues(i))) : "");
        float w = tok.CustomMinimumSize.X;
        // the very corner (Yunke 11-20); the end-turn button stands to its left
        Place(tok, -SideMargin - w, -SideMargin - H - 6, -SideMargin, -SideMargin - 6, 1, 1, 1, 1);
        // the bless and curse tokens in the chaos bag, beside it (Yunke 11-22): stacked right above the clue token, when there are any
        var faces = m.ChaosFaces.ToList();
        // each is its own token picture as big as the clue token, its count over it in the middle at the clue's size (Yunke 11-22)
        float y = -SideMargin - H - 10;
        foreach (var (face, word, img) in new[] { ("CurseToken", "诅咒", "curse"), ("BlessToken", "祝福", "bless") })
        {
            int n = faces.Count(f => f == face); if (n == 0) continue;
            var chip = new Control { MouseFilter = MouseFilterEnum.Pass, CustomMinimumSize = new Vector2(H, H), TooltipText = "混沌袋中的" + word + "标记：" + n };
            var pic = Ui.Image(Ui.Token(img), new Vector2(H, H)); pic.MouseFilter = MouseFilterEnum.Ignore; chip.AddChild(pic); pic.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
            var num = Style.Text(n.ToString(), Digits, Colors.White, Style.Symbols); num.MouseFilter = MouseFilterEnum.Ignore;
            num.AddThemeConstantOverride("outline_size", 7); num.AddThemeColorOverride("font_outline_color", new Color(0.05f, 0.04f, 0.03f));
            chip.AddChild(num); Ui.CenterDigits(num);
            float x0 = -SideMargin - w + (w - H) / 2;
            Place(chip, x0, y - H, x0 + H, y, 1, 1, 1, 1);
            y -= H + 6;
        }
    }

    Control TeamRow(TableModel m, Entity inv, ClientSeat seat)
    {
        bool viewing = seat.Player == m.State.Player, waiting = seat.HasQuestion;
        // the row hugs the right edge; what unfolds is added at its right end, so the row grows to the left
        var wrap = new HBoxContainer { MouseFilter = MouseFilterEnum.Ignore, Alignment = BoxContainer.AlignmentMode.End, SizeFlagsHorizontal = SizeFlags.ExpandFill };
        var row = new PanelContainer { MouseFilter = MouseFilterEnum.Pass };
        var rsb = new StyleBoxFlat { BgColor = new Color(0.02f, 0.018f, 0.015f, 0), ContentMarginLeft = 6, ContentMarginRight = 0, ContentMarginTop = 0, ContentMarginBottom = 0, AntiAliasing = true }; rsb.SetCornerRadiusAll(12);
        row.AddThemeStyleboxOverride("panel", rsb);
        wrap.AddChild(row);
        var line = new HBoxContainer { MouseFilter = MouseFilterEnum.Ignore }; line.AddThemeConstantOverride("separation", 6); row.AddChild(line);

        // threats: their engaged enemies (one's own are in the 与你交战 column) and their threat area
        // every investigator's engaged enemies and threat cards, the viewer's too (Yunke 11-19; 11-22: "即使切换到A的视角，A的
        // 威胁区卡牌也要显示在右上的详情区里" — 黛西's 帷幕彼端 went missing from her row while looking through her eyes)
        foreach (var e in m.EngagedWith(inv)) line.AddChild(TeamEnemy(m, e));
        foreach (var t in m.ThreatsOf(inv)) line.AddChild(TeamThreat(t));

        // the portrait, health and sanity left on its two lower corners
        var pickSeat = byAnchor.TryGetValue("inv:" + inv.Id, out var sp) ? sp.FirstOrDefault(c => c.Tag is "PortraitLabel" or "TargetLabel") : null;
        var player = seat.Player;
        var holder = new Control { MouseFilter = MouseFilterEnum.Ignore };
        var pic = CardView.PortraitCard(inv.Code, TeamH, pickSeat is not null);
        holder.CustomMinimumSize = pic.CustomMinimumSize + new Vector2(0, 8);
        pic.ShowName = false; pic.HoverZoom = false; pic.Rim = viewing ? Style.Amber : waiting ? Style.Good : new Color("d9c9a3");
        pic.PeekExtra = () => InvestigatorExtra(inv);
        pic.OnClick = pickSeat is not null ? () => Choose(pickSeat)
            : viewing ? () => { if (seat.HasQuestion) Locate(); } : () => { if (seat.HasQuestion) pendingLocate = true; ViewSeat(player); };
        pic.TooltipText = m.Name(inv) + (pickSeat is not null ? " · 点击选择" : viewing ? "（当前视角）" : waiting ? " · 等待操作，点击切换视角" : " · 点击切换视角") + "";
        if (!viewing) pic.Modulate = new Color(1, 1, 1, waiting ? 1 : 0.85f);
        // You are looking at a seat with nothing to do while this one is the one being asked: its card shakes now and then, so the
        // table does not just look stuck (Yunke 11-20)
        // "idle" also when the seat in view only has the window to play fast cards in someone else's turn: what moves the game
        // on is the investigator whose turn it is
        string activeId = J.String(m.Game, "activeInvestigatorId");
        bool viewerIdle = m.State.Seats.FirstOrDefault(x => x.Player == m.State.Player) is { HasQuestion: false }
            || (activeId.Length > 0 && m.Me is { } viewed && viewed.Id != activeId && inv.Id == activeId);
        if (!viewing && waiting && viewerIdle && S.Settings.ReducedMotion != true)
        {
            pic.PivotOffset = pic.CustomMinimumSize / 2;
            var sh = pic.CreateTween().SetLoops();
            sh.TweenInterval(1.6);
            foreach (var deg in new[] { -7f, 6f, -5f, 4f, -2f, 0f })
                sh.TweenProperty(pic, "rotation_degrees", deg, 0.06f).SetTrans(Tween.TransitionType.Sine);
            if (App.I.QaRunning) GD.Print("QA_SEAT_NUDGE " + m.Name(inv));
        }
        holder.AddChild(pic); teamPics[inv.Id] = pic;   // a teammate's card for damage / horror assignment (TableScreen.Assign)
        // online: who controls this investigator (Yunke 11-23: "联机和一人多控结合")
        if (OnlineGame.Active)
        {
            string who = OnlineGame.Mine(player) && !OnlineGame.Shared(player) ? "你" : OnlineGame.SeatName(player);
            var tag = Style.Text(who, 17, OnlineGame.Shared(player) ? Style.Good : Style.Paper, Style.Kai); tag.MouseFilter = MouseFilterEnum.Ignore;
            tag.AddThemeColorOverride("font_outline_color", new Color(0, 0, 0, 0.9f)); tag.AddThemeConstantOverride("outline_size", 6);
            holder.AddChild(tag); tag.Position = new Vector2(6, 2);
            pic.TooltipText = "由 " + (who == "你" ? "你" : who) + " 控制 · " + pic.TooltipText;
        }
        float pw = pic.CustomMinimumSize.X;
        void Roundel(string kind, int left, int max, float x)
        {
            var tok = Ui.TokenNumber(kind, left, 37); tok.MouseFilter = MouseFilterEnum.Ignore;
            if (left < max) foreach (var l in tok.GetChildren().OfType<Label>()) l.AddThemeColorOverride("font_color", new Color("ff9a88"));
            holder.AddChild(tok); tok.Position = new Vector2(x, TeamH - 25);
        }
        Roundel("health", m.Health(inv) - m.Damage(inv), m.Health(inv), -12);
        Roundel("horror", m.Sanity(inv) - m.Horror(inv), m.Sanity(inv), pw - 25);
        line.AddChild(holder);
        // the keys a teammate holds, beside their portrait (QQ #29)
        if (KeyRow(inv.Raw, 30, vertical: true, m.Name(inv)) is { } invKeys) { invKeys.SizeFlagsVertical = SizeFlags.ShrinkCenter; line.AddChild(invKeys); }
        // a resource that can go to this investigator (Yunke 11-22: "点击对应调查员面板上的资源token就是为对应调查员分配资源"): their
        // resource token beside the portrait, glowing; a click gives it to them
        if (byAnchor.ContainsKey("res:" + inv.Id) && !viewing)
        {
            var rt = Ui.TokenNumber("resource", m.Resources(inv), 40); rt.TooltipText = m.Name(inv) + " 的资源 · 点击给 " + m.Name(inv);
            rt.SizeFlagsVertical = SizeFlags.ShrinkCenter; line.AddChild(Clickable(rt, "res:" + inv.Id, hex: true));
        }

        // a click on the slim tab beside the portrait unfolds 装备区 and 手牌区 to its right (Yunke 11-19 r11: by click, not by
        // pointing); a second click folds them. T unfolds every row.
        bool open = teamOpen || openRows.Contains(inv.Id);
        var detail = new VBoxContainer { MouseFilter = MouseFilterEnum.Ignore, Visible = open, Alignment = BoxContainer.AlignmentMode.Center }; detail.AddThemeConstantOverride("separation", 5);
        // Yunke 11-20 #4: a small tab with melting edges (no hard rim, also when pointed at)
        var tab = LineIcon.Button(open ? "chev-r" : "chev-l", open ? "收起" : "展开装备和手牌（T：全部展开）", () => { }, 21);
        // on a phone or tablet the tab is a finger's size (Yunke 11-23: "右侧显示队友详情的两个按钮比较小，要保证手机能点到"):
        // about 7 mm tall at the phone layout's 130 % (84 px) and twice as wide; the same soft tab, only larger
        tab.CustomMinimumSize = SaveTransfer.OnPhone ? new Vector2(44, 84) : new Vector2(21, 53); tab.SizeFlagsVertical = SizeFlags.ShrinkCenter;
        tab.AddThemeStyleboxOverride("normal", Ui.SoftBox(0.30f, false, 0, 0)); tab.AddThemeStyleboxOverride("hover", Ui.SoftBox(0.55f, false, 0, 0)); tab.AddThemeStyleboxOverride("pressed", Ui.SoftBox(0.65f, false, 0, 0));
        // Yunke 11-22: unfolding and folding are animated — the panel grows out sideways from the tab and fades in; folding plays
        // the same backwards before the table is drawn again
        string rowId = inv.Id;
        tab.Pressed += () =>
        {
            if (openRows.Contains(rowId) && teamDetails.TryGetValue(rowId, out var shown) && IsInstanceValid(shown) && S.Settings.ReducedMotion != true)
            {
                shown.PivotOffset = new Vector2(0, shown.Size.Y / 2);
                var tw = shown.CreateTween().SetParallel();
                tw.TweenProperty(shown, "modulate:a", 0f, 0.16f); tw.TweenProperty(shown, "scale:x", 0.6f, 0.16f).SetTrans(Tween.TransitionType.Cubic).SetEase(Tween.EaseType.In);
                tw.Chain().TweenCallback(Callable.From(() => { openRows.Remove(rowId); Render(); }));
                return;
            }
            if (!openRows.Add(rowId)) openRows.Remove(rowId); else teamUnfolding = rowId;
            Render();
        };
        line.AddChild(tab);
        line.AddChild(detail);
        if (open)
        {
            BuildTeamDetail(m, inv, viewing, detail); rsb.BgColor = new Color(0.02f, 0.018f, 0.015f, 0.8f);
            teamDetails[rowId] = detail;
            if (teamUnfolding == rowId && S.Settings.ReducedMotion != true)
            {
                teamUnfolding = null;
                detail.Modulate = new Color(1, 1, 1, 0); detail.Scale = new Vector2(0.6f, 1);
                Callable.From(() =>
                {
                    if (!IsInstanceValid(detail)) return;
                    detail.PivotOffset = new Vector2(0, detail.Size.Y / 2);
                    var tw = detail.CreateTween().SetParallel();
                    tw.TweenProperty(detail, "modulate:a", 1f, 0.2f); tw.TweenProperty(detail, "scale:x", 1f, 0.22f).SetTrans(Tween.TransitionType.Cubic).SetEase(Tween.EaseType.Out);
                }).CallDeferred();
            }
        }
        return wrap;
    }

    // 装备区 and 手牌区 without words: a slot icon before the assets, a card icon before the hand's numbers (names in tooltips)
    void BuildTeamDetail(TableModel m, Entity inv, bool viewing, VBoxContainer detail)
    {
        Control Bar(Control icon, string tip, Control body)
        {
            var bar = new PanelContainer { MouseFilter = MouseFilterEnum.Pass, TooltipText = tip };
            var sb = new StyleBoxFlat { BgColor = new Color(0.06f, 0.05f, 0.045f, 0.92f), BorderColor = new Color(Style.Brass, 0.35f), ContentMarginLeft = 10, ContentMarginRight = 14, ContentMarginTop = 3, ContentMarginBottom = 3, AntiAliasing = true };
            sb.SetBorderWidthAll(1); sb.SetCornerRadiusAll(10); bar.AddThemeStyleboxOverride("panel", sb);
            var h = new HBoxContainer { MouseFilter = MouseFilterEnum.Ignore }; h.AddThemeConstantOverride("separation", 10); bar.AddChild(h);
            icon.SizeFlagsVertical = SizeFlags.ShrinkCenter; h.AddChild(icon);
            h.AddChild(body);
            return bar;
        }
        // 装备区: the assets in play, as small cards
        var gear = new HBoxContainer { MouseFilter = MouseFilterEnum.Ignore }; gear.AddThemeConstantOverride("separation", 5);
        // permanent cards with nothing to use (伟大之作, 危险处境, 掉进兔子洞, 卡戎的银币 …) are left out (Yunke 11-22)
        foreach (var a in m.AssetsOf(inv))
        {
            string text = J.String(S.Card(CoreCatalog.Normalize(a.Code)), "text");
            bool idle = text.Contains("永久") && !System.Text.RegularExpressions.Regex.IsMatch(text, @"\[(action|fast|free|reaction)\]") && !byAnchor.ContainsKey("asset:" + a.Id);
            if (!idle) gear.AddChild(TeamAsset(m, a));
        }
        if (gear.GetChildCount() == 0) gear.AddChild(Style.Text("—", 22, Style.PaperDim));
        Control gearIcon = Ui.SlotIcon("hand") is { } st ? Ui.Image(st, new Vector2(28, 28)) : new Control { CustomMinimumSize = new Vector2(28, 28) };
        gearIcon.MouseFilter = MouseFilterEnum.Ignore; gearIcon.Material = Tint(new Color(Style.Brass, 0.9f));
        detail.AddChild(Bar(gearIcon, "装备区", gear));
        // 手牌区: hand size, resources, clues, actions left, where they are
        var info = new HBoxContainer { MouseFilter = MouseFilterEnum.Ignore }; info.AddThemeConstantOverride("separation", 14);
        void Num(Control icon, string text, string tip)
        {
            var h = new HBoxContainer { MouseFilter = MouseFilterEnum.Pass, TooltipText = tip }; h.AddThemeConstantOverride("separation", 3);
            icon.MouseFilter = MouseFilterEnum.Ignore; icon.SizeFlagsVertical = SizeFlags.ShrinkCenter; h.AddChild(icon);
            var t = Style.Text(text, 23, Style.Paper, Style.Symbols); t.VerticalAlignment = VerticalAlignment.Center; h.AddChild(t);
            info.AddChild(h);
        }
        Num(new LineIcon { Kind = "cards", CustomMinimumSize = new Vector2(25, 25), Ink = Style.HoverText }, (m.Hand(inv).Count() + m.HiddenInHand(inv).Count()).ToString(), "手牌");
        Num(Ui.Image(Ui.Stat("resource"), new Vector2(25, 25)), m.Resources(inv).ToString(), "资源");
        Num(Ui.Image(Ui.Stat("clue"), new Vector2(25, 25)), m.Clues(inv).ToString(), "线索");
        var act = Ui.Glyph("action", 23, Style.Amber); act.VerticalAlignment = VerticalAlignment.Center;
        Num(act, m.Actions(inv).ToString(), "剩余行动");
        if (m.Location(m.LocationOf(inv)) is { } l)
        {
            var where = Style.Text(m.Name(l), 20, Style.PaperDim, Style.Kai); where.VerticalAlignment = VerticalAlignment.Center; where.MouseFilter = MouseFilterEnum.Pass; where.TooltipText = "所在地点";
            info.AddChild(where);
        }
        detail.AddChild(Bar(new LineIcon { Kind = "cards", CustomMinimumSize = new Vector2(28, 28), Ink = new Color(Style.Brass, 0.9f) }, "手牌区", info));
    }

    const float TeamCardH = 121, TeamGearH = 64;   // +15% (Yunke 11-20)

    Control TeamAsset(TableModel m, Entity a)
    {
        string key = "asset:" + a.Id; var code = a.Code;
        var size = new Vector2(MathF.Round(TeamGearH * 0.72f), TeamGearH);
        var cv = new CardView { Code = code, Key = key, CustomMinimumSize = size, Size = size, ShowName = false, ShowCost = false, HoverZoom = true, Actionable = byAnchor.ContainsKey(key), Glow = AssignGlow(key), PeekExtra = () => UnitExtra(a) };
        if (J.Get(a.Raw, "exhausted").ValueKind == JsonValueKind.True) cv.Modulate = new Color(0.55f, 0.55f, 0.55f);
        cv.OnClick = () => ActOn(key, cv, code);
        if (cv.Actionable && !anchors.ContainsKey(key)) Anchor(key, cv);
        return cv;
    }

    Control TeamEnemy(TableModel m, Entity e)
    {
        string key = "enemy:" + e.Id; var code = e.Code;
        var size = new Vector2(MathF.Round(TeamCardH * 0.68f), TeamCardH);
        bool act = byAnchor.ContainsKey(key);
        // what lies on it — damage, doom, clues — as on its card everywhere else (Yunke 11-20)
        var cv = new CardView { Code = code, Key = key, CustomMinimumSize = size, Size = size, ShowName = false, ShowCost = false, HoverZoom = true, Actionable = act, Rim = new Color("c0543e"), PeekExtra = () => EnemyExtra(e), SizeFlagsVertical = SizeFlags.ShrinkBegin,
            Badges = new[] { ("health", m.Damage(e)), ("doom", m.Doom(e)), ("clue", m.Clues(e)) } };
        if (J.Get(e.Raw, "exhausted").ValueKind == JsonValueKind.True) cv.Modulate = new Color(0.6f, 0.6f, 0.6f);
        AddVictoryCorner(cv, Victory(code), 18);
        // fight / evade / any other choice on it, from here as from the map
        cv.OnClick = () => ActOn(key, cv, code);
        if (act && !anchors.ContainsKey(key)) Anchor(key, cv);
        return cv;
    }

    Control TeamThreat(Entity t)
    {
        string key = model!.ThreatKey(t); var code = t.Code;
        var size = new Vector2(MathF.Round(TeamCardH * 0.68f), TeamCardH);
        var cv = new CardView { Code = code, Key = key, CustomMinimumSize = size, Size = size, ShowName = false, ShowCost = false, HoverZoom = true, Actionable = byAnchor.ContainsKey(key), Rim = new Color("8a5aa8"), SizeFlagsVertical = SizeFlags.ShrinkBegin };
        cv.OnClick = () => ActOn(key, cv, code);
        if (cv.Actionable && !anchors.ContainsKey(key)) Anchor(key, cv);
        return cv;
    }
}
