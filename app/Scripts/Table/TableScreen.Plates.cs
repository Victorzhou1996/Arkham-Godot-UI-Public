using System.Text.Json;
using Godot;
using Gathering;

namespace ArkhamUI;

// The act and the agenda in the top bar as two plates side by side (Yunke 11-19 r11 #4). Each plate: the card's picture on
// the left (its illustration only, cut from the printed card), then three lines — what it is and its progress track
// ("目标 · 第 3 幕 … 线索 ●●○", "密谋 · 第 2 幕 … 毁灭 ●●○○"), the card's name on a line of its own (long names shrink to fit
// instead of being cut: many scenarios have them), and the goal or what the doom does. The act carries a lit 推进 button when
// the team can advance it; the agenda carries its own actions (resign…). Hover: the full card text; click: the printed card.
public partial class TableScreen
{
    void ActAgendaPlates()
    {
        var m = model!;
        if (m.Act is null && m.Agenda is null) return;   // during setup there is no goal yet
        var plates = new HBoxContainer { MouseFilter = MouseFilterEnum.Ignore }; plates.AddThemeConstantOverride("separation", 12);
        Place(plates, 300, 8, -620, 92, 0, 0, 1, 0);
        if (m.Act is not null) { plates.AddChild(ActPlate(m)); AttachedBeside(plates, m, m.Attached("AttachedToAct", m.Act.Id).Concat(m.Attached("NextToAct", null))); }
        // every agenda in play (黑星升起 runs two agenda decks at once, each with its own doom — Carcosa 11-20); usually one
        var agendas = m.Agendas.ToArray(); bool several = agendas.Length > 1;
        for (int i = 0; i < agendas.Length; i++)
        {
            var ag = agendas[i];
            plates.AddChild(AgendaPlate(m, ag, several, first: i == 0));
            AttachedBeside(plates, m, m.Attached("AttachedToAgenda", ag.Id).Concat(i == 0 ? m.Attached("NextToAgenda", null) : Enumerable.Empty<Entity>()));
        }
        TarotBeside(plates);   // 使用塔罗牌解牌: the reading's cards (TableScreen.Tarot)
    }

    // cards attached to (or set next to) the act or the agenda: small cards right after its plate, to be read and acted on
    // (Yunke 11-20 #20)
    void AttachedBeside(HBoxContainer row, TableModel m, IEnumerable<Entity> cards)
    {
        foreach (var at in cards.DistinctBy(c => c.Id))
        {
            string akey = m.ThreatKey(at); var acode = at.Code;
            var cv = new CardView { Code = acode, Key = akey, CustomMinimumSize = new Vector2(56, 78), Size = new Vector2(56, 78), ShowCost = false, ShowName = false, Rim = new Color("8a5aa8"),
                Actionable = byAnchor.ContainsKey(akey), SizeFlagsVertical = SizeFlags.ShrinkCenter, TooltipText = S.Name(acode),
                Badges = new[] { ("doom", m.Doom(at)), ("clue", m.Clues(at)), ("horror", m.Horror(at)), ("health", m.Damage(at)) } };
            cv.OnClick = () => ActOn(akey, cv, acode); Anchor(akey, cv); row.AddChild(cv);
        }
    }

    static StyleBoxFlat PlateBox(Color edge, float alpha = 0.6f)
    {
        var sb = new StyleBoxFlat { BgColor = new Color(0.02f, 0.018f, 0.015f, alpha), BorderColor = edge, ContentMarginLeft = 6, ContentMarginRight = 14, ContentMarginTop = 5, ContentMarginBottom = 5, AntiAliasing = true };
        sb.SetBorderWidthAll(1); sb.BorderWidthLeft = 3; sb.SetCornerRadiusAll(14);
        return sb;
    }

    // The card's illustration at the plate's left. Acts and agendas are printed sideways as an open book: the agenda's picture
    // is the left part of the card, the act's the right part (the text page fills the rest).
    static Control PlateArt(string code, bool agenda)
    {
        Texture2D? tex = Ui.Art(CoreCatalog.Normalize(code));
        if (tex is null && Ui.Card(code) is { } full)
        {
            float w = full.GetWidth(), h = full.GetHeight();
            var r = agenda ? new Rect2(0.01f * w, 0.04f * h, 0.36f * w, 0.92f * h) : new Rect2(0.64f * w, 0.04f * h, 0.35f * w, 0.92f * h);
            tex = new AtlasTexture { Atlas = full, Region = r };
        }
        var clip = new Panel { CustomMinimumSize = new Vector2(62, 72), MouseFilter = MouseFilterEnum.Ignore, ClipChildren = ClipChildrenMode.Only, SizeFlagsVertical = SizeFlags.ShrinkCenter };
        var mask = new StyleBoxFlat { BgColor = Colors.White, AntiAliasing = true }; mask.SetCornerRadiusAll(9); clip.AddThemeStyleboxOverride("panel", mask);
        var img = new TextureRect { Texture = tex, ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize, StretchMode = TextureRect.StretchModeEnum.KeepAspectCovered, MouseFilter = MouseFilterEnum.Ignore, TextureFilter = TextureFilterEnum.LinearWithMipmaps };
        clip.AddChild(img); img.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        return clip;
    }

    // the stage of an act ([3, "A"]) or an agenda ({agendaSequenceStep: 2})
    static int Stage(JsonElement raw)
    {
        var seq = J.Get(raw, "sequence");
        if (seq.ValueKind == JsonValueKind.Array && seq.GetArrayLength() > 0 && seq[0].ValueKind == JsonValueKind.Number) return seq[0].GetInt32();
        return J.Int(seq, "agendaSequenceStep");
    }

    static Label Clipped(string text, int size, Color color, Font? font = null)
    {
        var l = Style.Text(text, size, color, font); l.ClipText = true; l.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis;
        l.CustomMinimumSize = new Vector2(60, 0); l.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        return l;
    }

    // A name that takes its whole line and shrinks (down to `min`) until it fits; only beyond that is it cut with "…".
    static Label FittedName(string text, int max, int min)
    {
        var l = Style.Text(text, max, Style.HoverText, Style.Kai);
        l.ClipText = true; l.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis; l.CustomMinimumSize = new Vector2(60, 0); l.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        l.AddThemeConstantOverride("outline_size", 4); l.AddThemeColorOverride("font_outline_color", new Color(0, 0, 0, 0.8f));
        void Fit()
        {
            if (!IsInstanceValid(l) || l.Size.X <= 1) return;
            var font = l.GetThemeFont("font");
            int size = max;
            while (size > min && font.GetStringSize(text, HorizontalAlignment.Left, -1, size).X > l.Size.X - 2) size--;
            if (l.GetThemeFontSize("font_size") != size) l.AddThemeFontSizeOverride("font_size", size);
        }
        l.Resized += Fit;
        return l;
    }

    static HBoxContainer PlateHead(string kind, Color kindColor)
    {
        var head = new HBoxContainer { MouseFilter = MouseFilterEnum.Ignore }; head.AddThemeConstantOverride("separation", 8);
        var k = Style.Text(kind, 15, kindColor, Style.Kai); k.VerticalAlignment = VerticalAlignment.Center; head.AddChild(k);
        head.AddChild(new Control { MouseFilter = MouseFilterEnum.Ignore, SizeFlagsHorizontal = SizeFlags.ExpandFill });
        return head;
    }

    // The choices on a plate split in two: the card's printed abilities (an icon button each kind) and the rest (a real advance…)
    (List<ChoicePresentation> Abilities, List<ChoicePresentation> Others) PlateChoices(string key)
    {
        var all = byAnchor.TryGetValue(key, out var l) ? l.Where(c => c.Enabled).ToList() : new();
        // abilities this card gives to another one (收牌认输: Peter Clover gains "➤➤: 谈判…"): the core anchors them on that card,
        // but the plate prints them, so its button offers them too (Yunke 11-22: the act's 谈判 button said it could not be used)
        string origin = key == "act" ? "ActSource" : key.StartsWith("agenda") ? "AgendaSource" : "";
        if (origin.Length > 0)
            foreach (var (k, list) in byAnchor)
                if (k != key) foreach (var c in list) if (c.Enabled && c.Tag == "AbilityLabel" && GrantedBy(c) == origin && AbilityKind(c) is not null && !all.Contains(c)) all.Add(c);
        var ab = all.Where(c => c.Tag == "AbilityLabel" && AbilityKind(c) is not null).ToList();
        return (ab, all.Except(ab).Except(PlateTargets(key)).ToList());
    }

    // The plate itself as the answer: a card that must go onto the current agenda or act (亚弗戈蒙之光 "附属到当前密谋或当前场景")
    List<ChoicePresentation> PlateTargets(string key)
        => byAnchor.TryGetValue(key, out var l) ? l.Where(c => c.Enabled && c.Tag == "TargetLabel" && J.Tag(J.Get(c.Raw, "target")) is "ActTarget" or "AgendaTarget").ToList() : new();

    // Yunke 11-22: such a choice was only a small button on the agenda (the act's spot held its 推进 button). Both plates now get
    // a red border and one ring of red ripple, and a click on the plate picks it.
    bool MarkPlateTarget(Control plate, string key)
    {
        var t = PlateTargets(key); if (t.Count == 0) return false;
        var rim = new Panel { MouseFilter = MouseFilterEnum.Ignore, ZIndex = 5 };
        var sb = new StyleBoxFlat { DrawCenter = false, BgColor = new Color(0, 0, 0, 0), BorderColor = BiteRed, ShadowColor = new Color(BiteRed, 0.55f), ShadowSize = 14, AntiAliasing = true };
        sb.SetBorderWidthAll(3); sb.SetCornerRadiusAll(10); rim.AddThemeStyleboxOverride("panel", sb);
        plate.AddChild(rim); rim.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        if (S.Settings.ReducedMotion != true) { var tw = rim.CreateTween().SetLoops(); tw.TweenProperty(rim, "modulate:a", 0.45f, 0.8f).SetTrans(Tween.TransitionType.Sine); tw.TweenProperty(rim, "modulate:a", 1f, 0.8f).SetTrans(Tween.TransitionType.Sine); }
        Callable.From(() => { if (IsInstanceValid(plate) && plate.IsInsideTree()) Ripple(plate, new Rect2(Vector2.Zero, plate.Size), 0f, 12, BiteRed); }).CallDeferred();
        plate.TooltipText = "点击选择：" + t[0].Title;
        plate.MouseDefaultCursorShape = CursorShape.PointingHand;
        if (App.I.QaRunning) GD.Print("QA_PLATE_TARGET " + key);
        return true;
    }

    // the kind of a printed ability a choice uses: "action" (→), "fast" (lightning / free) or "reaction", from the card's own line
    static string? AbilityKind(ChoicePresentation c)
    {
        // a granted ability is printed inside another card's text, not at the start of a line: its kind is its type
        if (GrantedBy(c).Length > 0)
            return J.Tag(J.Get(J.Get(c.Raw, "ability"), "type")) switch { "ActionAbility" or "ActionAbilityWithSkill" => "action", "FastAbility" or "FastAbility'" => "fast", "ReactionAbility" => "reaction", _ => null };
        if (AbilityText.Of(c.Raw) is not { } ab || AbilityText.Line(ab.Code, ab.Index) is not { } line) return null;
        return line.StartsWith("[action]") ? "action" : line.StartsWith("[fast]") || line.StartsWith("[free]") ? "fast" : line.StartsWith("[reaction]") ? "reaction" : null;
    }

    // the kind of card that granted an ability to another card (ProxySource's originalSource: "ActSource", "AgendaSource" …)
    static string GrantedBy(ChoicePresentation c)
    {
        var src = J.Get(J.Get(c.Raw, "ability"), "source");
        return J.Tag(src) == "ProxySource" ? J.Tag(J.Get(src, "originalSource")) : "";
    }

    // the kinds of abilities printed on the card's side that is up (so a button shows, dim, when it cannot be used now)
    List<string> PrintedAbilityKinds(string code)
    {
        string text = J.String(S.Card(CoreCatalog.Normalize(code)), "text");
        var kinds = new List<string>();
        foreach (var line in text.Split('\n').Select(x => x.Trim()))
        {
            string? k = line.StartsWith("[action]") ? "action" : line.StartsWith("[fast]") || line.StartsWith("[free]") ? "fast" : null;
            if (k is not null && !kinds.Contains(k)) kinds.Add(k);
        }
        return kinds;
    }

    // the plates' popups open after the hover delay set in 设置 (they opened at once and stayed in the way — Yunke 11-20)
    int plateHoverGen;
    void PlateHover(Control plate, Action show)
    {
        int gen = ++plateHoverGen;
        GetTree().CreateTimer(Peek.Delay).Timeout += () =>
        {
            if (gen != plateHoverGen || !IsInstanceValid(plate) || !plate.IsInsideTree()) return;
            if (!plate.GetGlobalRect().HasPoint(pointer)) return;   // the pointer left before the delay ran out
            show();
        };
    }

    void AddPlateAbilityButtons(HBoxContainer row, string code, List<ChoicePresentation> abilities)
    {
        var kinds = PrintedAbilityKinds(code);
        foreach (var c in abilities) if (AbilityKind(c) is { } k && !kinds.Contains(k)) kinds.Add(k);
        foreach (var k in kinds)
        {
            var list = abilities.Where(c => AbilityKind(c) == k).ToList();
            var b = AbilityButton(code, 46, k, list, (k == "action" ? "行动能力" : k == "fast" ? "快速能力" : "反应能力") + "（现在不能使用）");
            b.SizeFlagsVertical = SizeFlags.ShrinkCenter; row.AddChild(b);
        }
    }

    Control ActPlate(TableModel m)
    {
        var act = m.Act!; string code = act.Code;
        bool can = byAnchor.ContainsKey("act");
        var plate = new PanelContainer { MouseFilter = MouseFilterEnum.Stop, MouseDefaultCursorShape = CursorShape.PointingHand, SizeFlagsHorizontal = SizeFlags.ExpandFill };   // as wide as the agenda plate (Yunke 11-20: the two were lopsided)
        var sb = PlateBox(can ? Style.Amber : new Color(Style.Brass, 0.45f));
        if (can) { sb.ShadowColor = new Color(Style.Amber, 0.35f); sb.ShadowSize = 10; }
        plate.AddThemeStyleboxOverride("panel", sb);
        var row = new HBoxContainer { MouseFilter = MouseFilterEnum.Ignore }; row.AddThemeConstantOverride("separation", 10); plate.AddChild(row);
        row.AddChild(PlateArt(code, agenda: false));
        var col = new VBoxContainer { MouseFilter = MouseFilterEnum.Ignore, SizeFlagsHorizontal = SizeFlags.ExpandFill, Alignment = BoxContainer.AlignmentMode.Center }; col.AddThemeConstantOverride("separation", -1); row.AddChild(col);
        int stage = Stage(act.Raw);
        var head = PlateHead("目标" + (stage > 0 ? " · 第 " + stage + " 幕" : ""), Style.Brass); col.AddChild(head);
        // progress, on the right of the first line: the team's clues against the cost, or the Midnight Masks' cultists
        int need = ActClueCost(m);
        if (need > 0)
        {
            int have = m.Investigators.Sum(i => m.Clues(i));
            for (int i = 0; i < need; i++) { var ic = Ui.Image(Ui.Stat("clue"), new Vector2(18, 18)); ic.MouseFilter = MouseFilterEnum.Ignore; ic.SizeFlagsVertical = SizeFlags.ShrinkCenter; ic.Modulate = i < have ? Colors.White : new Color(1, 1, 1, 0.22f); head.AddChild(ic); }
            head.AddChild(Style.Text(have + " / " + need, 15, have >= need ? Style.Good : Style.Amber, Style.Sans));
        }
        // tokens put on the act itself (阻止计划: a clue on the act for each test passed, it advances at twice the investigators):
        // shown on the plate, against the target when its objective names one (Yunke 11-21: the clues on it could not be seen)
        int onAct = m.Clues(act);
        string actText = J.String(S.Card(CoreCatalog.Normalize(code)), "text");
        if (onAct > 0 || actText.Contains("本场景的线索") || actText.Contains("在本场景上放置"))
        {
            int seatsNow = Math.Max(1, m.Investigators.Count(i => J.Get(i.Raw, "eliminated").ValueKind != System.Text.Json.JsonValueKind.True));
            var times = System.Text.RegularExpressions.Regex.Match(actText, @"线索数量等于调查员人数的(\d+)倍");
            int target = times.Success ? int.Parse(times.Groups[1].Value) * seatsNow : 0;
            head.AddChild(Style.Text("场景上", 14, Style.PaperDim, Style.Kai));
            if (target > 0)
                for (int i = 0; i < target; i++) { var ic = Ui.Image(Ui.Stat("clue"), new Vector2(18, 18)); ic.MouseFilter = MouseFilterEnum.Ignore; ic.SizeFlagsVertical = SizeFlags.ShrinkCenter; ic.Modulate = i < onAct ? Colors.White : new Color(1, 1, 1, 0.22f); head.AddChild(ic); }
            else { var ic = Ui.Image(Ui.Stat("clue"), new Vector2(18, 18)); ic.MouseFilter = MouseFilterEnum.Ignore; ic.SizeFlagsVertical = SizeFlags.ShrinkCenter; head.AddChild(ic); }
            head.AddChild(Style.Text(target > 0 ? onAct + " / " + target : "× " + onAct, 15, target > 0 && onAct >= target ? Style.Good : Style.Amber, Style.Sans));
            plate.TooltipText = "场景上有 " + onAct + " 个线索" + (target > 0 ? "（达到 " + target + " 个时推进）" : "");
        }
        if (CoreCatalog.Normalize(code) == "01123")
        {
            int cultists = m.VictoryDisplay.Distinct().Count(c => J.String(S.Card(c), "traits").Contains("异教徒"));
            head.AddChild(Style.Text("胜利区异教徒", 14, Style.PaperDim, Style.Kai));
            for (int i = 0; i < 6; i++) { var p = new Panel { CustomMinimumSize = new Vector2(12, 12), SizeFlagsVertical = SizeFlags.ShrinkCenter, MouseFilter = MouseFilterEnum.Ignore }; p.AddThemeStyleboxOverride("panel", Style.Box(i < cultists ? Style.Amber : new Color(0, 0, 0, 0.4f), Style.Brass, 1, 6, 0, 0)); head.AddChild(p); }
            head.AddChild(Style.Text(cultists + " / 6", 15, cultists >= 6 ? Style.Good : Style.Amber, Style.Sans));
        }
        col.AddChild(FittedName(m.Name(act), 22, 15));
        // 揭露阴谋: what its → ability costs and what the team holds (Yunke 11-20: with clues enough, nothing said one could draw a
        // cultist — the old button read 推进), in place of the objective line, which the hover box still gives in full
        if (CoreCatalog.Normalize(code) == "01123" && !m.IsOver)
        {
            int seats = Math.Max(1, m.Investigators.Count(i => J.Get(i.Raw, "eliminated").ValueKind != System.Text.Json.JsonValueKind.True)), cost = 2 * seats, have = m.Investigators.Sum(i => m.Clues(i));
            var line = new HBoxContainer { MouseFilter = MouseFilterEnum.Pass, TooltipText = "[行动]：调查员共同花费每人 2 个线索（共 " + cost + " 个），抽取异教徒牌库顶的卡牌" };
            line.AddThemeConstantOverride("separation", 5);
            var ic = Ui.Image(Ui.Stat("clue"), new Vector2(17, 17)); ic.MouseFilter = MouseFilterEnum.Ignore; ic.SizeFlagsVertical = SizeFlags.ShrinkCenter; line.AddChild(ic);
            line.AddChild(Style.Text("抽异教徒：全队线索 " + have + " / " + cost + (have >= cost ? "，可以抽了" : ""), 15, have >= cost ? Style.Good : Style.PaperDim, Style.Kai));
            col.AddChild(line);
        }
        else col.AddChild(Clipped(Objective(m), 15, Style.PaperDim));
        // it can advance: its own lit button on the plate's right (Yunke 11-18 #8: where to click must be plain)
        // Yunke 11-20: the card's own abilities (揭露阴谋's "spend clues: draw a cultist") are an icon button of the ability's kind
        // (→ action, lightning fast…), as on a location; only a real advance of the act is the word 推进
        var (abilities, advance) = PlateChoices("act");
        if (abilities.Count > 0 || PrintedAbilityKinds(code).Count > 0) AddPlateAbilityButtons(row, code, abilities);
        if (advance.Count > 0)
        {
            Button adv = null!;
            adv = Ui.MinimalButton("推进 ›", () => { HideGoal(); if (advance.Count == 1) Choose(advance[0]); else InPlaceMenu(adv, S.Name(code), advance, code); }, 21, true); adv.CustomMinimumSize = new Vector2(92, 48); adv.SizeFlagsVertical = SizeFlags.ShrinkCenter;
            adv.TooltipText = "队伍可以推进目标";
            if (S.Settings.ReducedMotion != true) { var gt = adv.CreateTween().SetLoops(); gt.TweenProperty(adv, "modulate", new Color(1.3f, 1.16f, 0.9f), 0.75f).SetTrans(Tween.TransitionType.Sine); gt.TweenProperty(adv, "modulate", Colors.White, 0.75f).SetTrans(Tween.TransitionType.Sine); }
            row.AddChild(adv);
        }
        // a click on the plate itself only shows the card: its abilities are its icon buttons, its advance the 推进 button
        // (Yunke 11-20: a click on 时间飞逝's plate resigned)
        bool actTarget = MarkPlateTarget(plate, "act");
        plate.GuiInput += e => { if (e is InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Left }) { HideGoal(); if (actTarget && PlateTargets("act") is { Count: > 0 } at) Choose(at[0]); else CardReader.Open(code, printed: true); } };
        plate.MouseEntered += () => PlateHover(plate, () => ShowGoal(plate, m));
        plate.MouseExited += HideGoalSoon;
        Anchor("act", plate);
        return plate;
    }

    Control AgendaPlate(TableModel m, Entity ag, bool several = false, bool first = true)
    {
        string code = ag.Code;
        // the doom counted against this agenda's threshold, as the rules core counts it (TableModel.AgendaDoomCount): with
        // OtherDoomSubtracts (真相幻影) it can fall below zero — the pips show none, the number shows the count
        int doom = m.AgendaDoomCount(ag), max = Math.Max(1, m.AgendaThreshold(ag));
        bool close = doom >= max - 1;
        var plate = new PanelContainer { MouseFilter = MouseFilterEnum.Stop, MouseDefaultCursorShape = CursorShape.PointingHand, SizeFlagsHorizontal = SizeFlags.ExpandFill };
        plate.AddThemeStyleboxOverride("panel", PlateBox(close ? new Color("d0503c") : new Color("8a3a30"), 0.62f));
        var row = new HBoxContainer { MouseFilter = MouseFilterEnum.Ignore }; row.AddThemeConstantOverride("separation", 10); plate.AddChild(row);
        row.AddChild(PlateArt(code, agenda: true));
        var col = new VBoxContainer { MouseFilter = MouseFilterEnum.Ignore, SizeFlagsHorizontal = SizeFlags.ExpandFill, Alignment = BoxContainer.AlignmentMode.Center }; col.AddThemeConstantOverride("separation", -1); row.AddChild(col);
        int stage = Stage(ag.Raw);
        var head = PlateHead("密谋" + (stage > 0 ? " · 第 " + stage + " 幕" : ""), new Color("d58b72")); col.AddChild(head);
        // the doom track on the first line's right: one pip per point of the threshold (the doom flights land here: "agenda")
        var dial = new HBoxContainer { MouseFilter = MouseFilterEnum.Ignore }; dial.AddThemeConstantOverride("separation", 5);
        // the doom on the agenda itself fills its pips red; the doom lying on cards in play (an enemy, a place…) counts too but
        // stays on its card: an orange ring (Yunke 11-20)
        int own = Math.Max(0, Math.Min(doom, several ? m.Doom(ag) : DoomOnAgendas(m))), onCards = doom - own;
        for (int i = 0; i < max; i++) { var p = new Panel { CustomMinimumSize = new Vector2(13, 13), SizeFlagsVertical = SizeFlags.ShrinkCenter, MouseFilter = MouseFilterEnum.Ignore }; p.AddThemeStyleboxOverride("panel", DoomPip(i < own ? 1 : i < doom ? 2 : 0)); dial.AddChild(p); }
        if (onCards > 0) { dial.MouseFilter = MouseFilterEnum.Pass; dial.TooltipText = "其中 " + onCards + " 个毁灭在场上的卡牌上（橙圈），随卡牌离场而移除"; }
        var dl = Style.Text(doom + " / " + max, 15, Style.Danger, Style.Sans); dl.VerticalAlignment = VerticalAlignment.Center; dial.AddChild(dl);
        head.AddChild(dial);
        col.AddChild(FittedName(m.Name(ag), 22, 15));
        // 真相幻影 while conviction outweighs doubt: the doom elsewhere counts against this agenda (the core's OtherDoomSubtracts)
        string note = close ? "再放 " + Math.Max(0, max - doom) + " 个毁灭，密谋就会推进" : m.OtherDoomSubtracts(ag) ? "其他卡牌上的毁灭从本密谋中减去" : "毁灭达到 " + max + " 时密谋推进";
        col.AddChild(Clipped(note, 15, close ? new Color("ff9a88") : Style.PaperDim, Style.Kai));
        // one doom short of the advance: the plate breathes red
        if (close && S.Settings.ReducedMotion != true) { var tw = plate.CreateTween().SetLoops(); tw.TweenProperty(plate, "modulate", new Color(1.25f, 0.95f, 0.9f), 0.9f).SetTrans(Tween.TransitionType.Sine); tw.TweenProperty(plate, "modulate", Colors.White, 0.9f).SetTrans(Tween.TransitionType.Sine); }
        // the agenda's own actions (resign in The Midnight Masks…): a button on the plate
        // its printed abilities (猎人还是猎物？'s "→: resign") as icon buttons of their kind, dim when they cannot be used now (Yunke 11-20)
        if (first)
        {
            var (abilities, others) = PlateChoices("agenda");
            if (abilities.Count > 0 || PrintedAbilityKinds(code).Count > 0) AddPlateAbilityButtons(row, code, abilities);
            if (others.Count > 0)
            {
                Button ab = null!;
                ab = Ui.MinimalButton(others[0].Title + " ›", () => { HideGoal(); if (others.Count == 1) Choose(others[0]); else InPlaceMenu(ab, S.Name(code), others, code); }, 18, true); ab.CustomMinimumSize = new Vector2(0, 44); ab.SizeFlagsVertical = SizeFlags.ShrinkCenter;
                ab.TooltipText = "密谋卡上的选择"; row.AddChild(ab);
            }
        }
        bool agendaTarget = first && MarkPlateTarget(plate, "agenda");
        plate.GuiInput += e => { if (e is InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Left }) { HideGoal(); if (agendaTarget && PlateTargets("agenda") is { Count: > 0 } gt) Choose(gt[0]); else CardReader.Open(code, printed: true); } };
        if (first) plate.MouseEntered += () => PlateHover(plate, () => ShowAgenda(plate, m)); else plate.TooltipText = m.Name(ag) + " · 点击查看密谋卡";
        plate.MouseExited += HideGoalSoon;
        if (first) Anchor("agenda", dial);
        return plate;
    }
}
