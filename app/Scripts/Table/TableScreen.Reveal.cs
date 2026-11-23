using System.Text.RegularExpressions;
using System.Text.Json;
using Gathering;
using Godot;

namespace ArkhamUI;

// Mythos/encounter draws happen inside the core with no question attached. When the encounter deck
// shrinks, the newly drawn cards are found in the log and shown one by one before play continues,
// so the player always sees what came out of the dark.
public partial class TableScreen
{
    int lastEncounterDeck = -1;
    // While a phase banner, an interlude or a drawn card is on screen, the table shows no prompts, tests or dialogs:
    // the effect of what was drawn appears after the card has been shown.
    bool revealing;
    string? lastActCode, lastAgendaCode;

    // Act/agenda flips have no question of their own; their story (the back of the old card and the
    // opening of the new one) is told as a full-screen interlude before play continues.
    // The flips are worked out first and told last (Yunke 11-19 r9 #16: 远古恶魔 was drawn and the agenda had already
    // advanced): the drawn cards, then the doom flying onto the agenda, then the advance.
    List<(string? Old, string? New, string Kind)> PendingFlips()
    {
        var shows = new List<(string? Old, string? New, string Kind)>();
        if (model is null) return shows;
        static string Key(Entity? e) => e is null ? "" : e.Code + (J.Get(e.Raw, "flipped").ValueKind == System.Text.Json.JsonValueKind.True ? "B" : "A");
        string act = Key(model.Act), agenda = Key(model.Agenda);
        void Diff(string? was, string now, string kind)
        {
            if (was is null || was == now) return;
            // The scenario ended (or the card left play): no act or agenda remains. That is not an advance, and showing the
            // old card's back here played a phantom "agenda advances" interlude after the win (Yunke, playtest 2 #24).
            if (now.Length == 0 || model!.IsOver) return;
            string? wasCode = was.Length > 0 ? was[..^1] : null, nowCode = now.Length > 0 ? now[..^1] : null;
            bool wasFlipped = was.EndsWith("B");
            if (nowCode == wasCode && now.EndsWith("B")) shows.Add((wasCode, null, kind));              // the card turned over
            else if (wasCode is not null && !wasFlipped) shows.Add((wasCode, nowCode, kind));            // replaced without a visible flip
            else if (nowCode is not null) shows.Add((null, nowCode, kind));                              // the next card arrives
        }
        if (App.I.QaRunning && (lastActCode != act || lastAgendaCode != agenda)) GD.Print("QA_FLIPS act " + lastActCode + "->" + act + " agenda " + lastAgendaCode + "->" + agenda + " seq=" + state?.Sequence + " q=" + model.QuestionTag);
        // the agenda before the act (Yunke 11-22: the two were told the other way round): as the html table lays them out (Agenda,
        // then Act) and the game's setup puts the agenda deck left of the act deck — the threat first, then what to do about it
        Diff(lastAgendaCode, agenda, "agenda"); Diff(lastActCode, act, "act");
        lastActCode = act; lastAgendaCode = agenda;
        return shows;
    }

    async Task PlayFlips(List<(string? Old, string? New, string Kind)> shows)
    {
        if (shows.Count == 0 || model is null) return;
        revealing = true;
        try
        {
            Render();
            await bannerDone.Task;   // the phase banner (if one started) goes first; the story flip follows it
            // continue on a fresh frame: resuming inside the banner's tween callback made the modal's add_child fail
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            foreach (var f in shows) { if (App.I.QaRunning) GD.Print("QA_INTERLUDE " + f.Kind + " " + f.Old + "->" + f.New); await Interlude(f.Old, f.New, f.Kind); }
        }
        finally { revealing = false; }
    }

    // The base game's act cards (and its agenda cards) share one panoramic picture per scenario: the house outside for all three
    // acts of 聚集于此, so "地板上的门" showed the house from outside (Yunke 11-18: the picture did not match the scene).
    // These are the cards after the first of each shared picture.
    static readonly HashSet<string> SharedStoryArt = new() { "01106", "01107", "01109", "01110", "01122", "01144", "01145", "01147", "01148" };

    // The picture beside a story page: where the story takes the investigators when the card's back puts them somewhere
    // ("将所有调查员放到走廊"), else — for a card that only repeats an earlier card's picture — where you are now, else the
    // card's own illustration (redrawn, assets/art-story).
    Texture2D? StoryPicture(string? oldCode, string? newCode)
    {
        string card = CoreCatalog.Normalize(oldCode ?? newCode!);
        if (oldCode is not null)
            foreach (System.Text.RegularExpressions.Match mm in System.Text.RegularExpressions.Regex.Matches(J.String(S.Card(card), "back_text"), @"(?:放到|移动到|移至)([^，。,；\s]{1,10})"))
                if (PlaceArt(mm.Groups[1].Value) is { } t) return t;
        if (SharedStoryArt.Contains(card) && model?.MyLocation is { } here && PlaceArt(model.Name(here)) is { } h) return h;
        string picCode = oldCode is not null ? card + "b" : card;
        return Ui.Tex("res://assets/art-story/" + picCode + ".webp") ?? Ui.Tex("res://assets/art-story/" + card + ".webp");
    }

    // a place's illustration by its name: the one on the table, else any location card of that name
    Texture2D? PlaceArt(string name)
    {
        string code = model?.Locations.FirstOrDefault(l => model.Name(l) == name)?.Code ?? "";
        if (code.Length == 0)
            code = S.Catalog.Cards.Values.FirstOrDefault(c => J.String(c, "type_code") == "location" && J.String(c, "name") == name) is { ValueKind: JsonValueKind.Object } c ? J.String(c, "code") : "";
        if (code.Length == 0) return null;
        code = CoreCatalog.Normalize(code);
        // the location card's own printed picture (its art window), as the story pictures are (Yunke 11-18: originals, no redraws)
        return Art.Crop(code, "location") ?? Ui.Art(code);
    }

    Task Interlude(string? oldCode, string? newCode, string kind)
    {
        var done = new TaskCompletionSource();
        var vp = GetViewportRect().Size;
        var old = oldCode is null ? default : S.Card(oldCode);
        string title = oldCode is null ? S.Name(newCode!) : J.String(old, "back_name"); if (title.Length == 0) title = S.Name(oldCode!);
        string story = oldCode is null ? "" : J.String(old, "back_flavor");
        var root = Ui.Backdrop(vp, Ui.Bg("desk-table"), 0.4f);   // on the desk (Yunke 11-19 r9 #8: not the house painting)
        GameAudio.NarrateCards(root, oldCode, newCode);   // noncore audio 11-21: the story part (back_flavor, then flavor) is read; rules are not
        // the card's own picture beside the text (Yunke 11-18 #4): the illustration of the card being told, redrawn from the
        // printed card (tools/make_story_art.py) — the back of the card that turned over, or the front of the one that arrives
        var pic = StoryPicture(oldCode, newCode);
        // an advance told on one page: the old card's back first, then the new card's front comes in by itself (Yunke 11-21:
        // "先出现当前目标的反面…然后再出现下个目标的正面…中间没有我可以操作的地方就不用我再按一次") — its picture with it
        var frontPic = oldCode is not null && newCode is not null ? StoryPicture(null, newCode) : null;
        TextureRect? picRect = null;
        float picW = pic is null ? 0 : Math.Min(vp.X * 0.26f, 470), gap = pic is null ? 0 : 56;
        float colW = Math.Min(pic is null ? 1000 : 860, vp.X - 200 - picW - gap);
        float left = (vp.X - colW - picW - gap) / 2;
        if (pic is not null)
        {
            float picH = Math.Min(vp.Y * 0.72f, picW * pic.GetHeight() / pic.GetWidth());
            var img = new TextureRect { Texture = pic, ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize, StretchMode = TextureRect.StretchModeEnum.KeepAspectCovered, MouseFilter = MouseFilterEnum.Ignore,
                TextureFilter = TextureFilterEnum.LinearWithMipmapsAnisotropic, Position = new Vector2(left, (vp.Y - picH) / 2), Size = new Vector2(picW, picH) };
            img.Material = Ui.EdgeAlpha(Vector2.Zero, img.Size, 40, 40, 40, 40);   // its edges melt into the dark (no frame)
            root.AddChild(img); picRect = img;
            if (S.Settings.ReducedMotion != true) { img.Modulate = new Color(1, 1, 1, 0); img.CreateTween().TweenProperty(img, "modulate:a", 1f, 0.8f); }
        }
        var col = new VBoxContainer { Position = new Vector2(left + picW + gap, vp.Y * 0.10f), Size = new Vector2(colW, vp.Y * 0.84f) }; col.AddThemeConstantOverride("separation", 20);
        root.AddChild(col);
        var kicker = Style.Text(oldCode is null ? (kind == "act" ? "新的目标" : "新的密谋") : kind == "act" ? "场景推进" : "密谋推进 · 黑暗逼近", 24, kind == "act" ? Style.Brass : Style.Danger, Style.Kai); kicker.HorizontalAlignment = HorizontalAlignment.Center; col.AddChild(kicker);
        var t = Style.Title(title, 46); t.HorizontalAlignment = HorizontalAlignment.Center; col.AddChild(t);
        col.AddChild(new ColorRect { Color = new Color(Style.Brass, 0.3f), CustomMinimumSize = new Vector2(0, 1) });
        RichTextLabel Para(string txt, int size, Color color)
        {
            var r = CardReader.Rules(System.Text.RegularExpressions.Regex.Replace(txt, @"\*([^*\n]+)\*", "<b>$1</b>"), size);
            if (Style.Kai is not null) r.AddThemeFontOverride("normal_font", Style.Kai);
            r.AddThemeConstantOverride("line_separation", 12); r.AddThemeColorOverride("default_color", color);
            r.AddThemeConstantOverride("outline_size", 6); r.AddThemeColorOverride("font_outline_color", new Color(0, 0, 0, 0.7f));
            return r;
        }
        if (story.Length > 0)
            foreach (var para in story.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                if (col.GetChildCount() > 2) col.AddChild(Ui.Spacer(0, 12));   // air between paragraphs
                col.AddChild(Para(para, 28, new Color("f1e6cc")));
            }
        // what the back makes happen (an enemy put into play, investigators moved, cards set aside…): its rules text under the
        // story, as printed (Yunke 11-21: those instructions were not shown)
        string backRules = oldCode is null ? "" : J.String(old, "back_text");
        if (backRules.Length > 0)
        {
            col.AddChild(Ui.Spacer(0, 4));
            col.AddChild(new ColorRect { Color = new Color(Style.Brass, 0.22f), CustomMinimumSize = new Vector2(0, 1) });
            foreach (var para in backRules.Split('\n', StringSplitOptions.RemoveEmptyEntries)) col.AddChild(Para(para, 23, new Color("e8dcc0")));
        }
        VBoxContainer? frontPart = null;
        if (newCode is not null)
        {
            var nc = S.Card(newCode); string flavor = J.String(nc, "flavor");
            Container holder = col;
            if (oldCode is not null) { frontPart = new VBoxContainer(); frontPart.AddThemeConstantOverride("separation", 12); col.AddChild(frontPart); holder = frontPart; }
            if (oldCode is not null) { var nt = Style.Text((kind == "act" ? "新的目标 · " : "新的密谋 · ") + S.Name(newCode), 26, Style.HoverText, Style.Kai); nt.HorizontalAlignment = HorizontalAlignment.Center; holder.AddChild(Ui.Spacer(0, 6)); holder.AddChild(nt); }
            if (flavor.Length > 0) holder.AddChild(oldCode is null ? Para(flavor, 28, new Color("f1e6cc")) : Para(flavor, 23, new Color(Style.PaperDim, 0.95f)));
        }
        // the front part waits, then comes in (the picture turning to the new card's), with no click
        if (frontPart is not null && S.Settings.ReducedMotion != true)
        {
            frontPart.Modulate = new Color(1, 1, 1, 0);
            double wait = Math.Clamp(1.2 + (story.Length + backRules.Length) * 0.012, 1.6, 4.0);
            var ft = frontPart.CreateTween(); ft.TweenInterval(wait); ft.TweenProperty(frontPart, "modulate:a", 1f, 0.7f).SetTrans(Tween.TransitionType.Sine);
            if (frontPic is not null && picRect is not null)
            {
                var pr = picRect;
                var pt = pr.CreateTween(); pt.TweenInterval(wait);
                pt.TweenProperty(pr, "modulate:a", 0f, 0.35f);
                pt.TweenCallback(Callable.From(() => { if (IsInstanceValid(pr)) pr.Texture = frontPic; }));
                pt.TweenProperty(pr, "modulate:a", 1f, 0.5f);
            }
            if (App.I.QaRunning) GD.Print("QA_INTERLUDE_STAGED " + oldCode + "->" + newCode + " wait=" + wait.ToString("0.0") + " rules=" + backRules.Length);
        }
        var go = new HBoxContainer { Alignment = BoxContainer.AlignmentMode.Center }; col.AddChild(go);
        Control scrim = null!;
        bool closing = false;
        void CloseNow() { if (IsInstanceValid(scrim)) App.I.CloseModal(scrim); done.TrySetResult(); }
        // leaving the page: to black, then the next thing comes up out of the black (Yunke 11-19 r9 #8)
        void Close()
        {
            if (closing) return; closing = true;
            if (App.I.QaRunning) GD.Print("QA_INTERLUDE_CLOSE t=" + Time.GetTicksMsec() + " " + System.Environment.StackTrace.Split('\n').Skip(2).FirstOrDefault()?.Trim());
            if (S.Settings.ReducedMotion == true || App.I.QaRunning) { CloseNow(); return; }
            var black = new ColorRect { Color = Colors.Black, MouseFilter = MouseFilterEnum.Stop, ZIndex = 200, Modulate = new Color(1, 1, 1, 0) };
            App.I.Overlay.AddChild(black); black.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
            var tw = black.CreateTween();
            tw.TweenProperty(black, "modulate:a", 1f, 0.35f).SetTrans(Tween.TransitionType.Sine);
            tw.TweenCallback(Callable.From(CloseNow));
            tw.TweenInterval(0.25);
            tw.TweenProperty(black, "modulate:a", 0f, 0.5f).SetTrans(Tween.TransitionType.Sine);
            tw.TweenCallback(Callable.From(() => black.QueueFree()));
        }
        var b = Ui.Button("继续", Close, 26, true); b.CustomMinimumSize = new Vector2(260, 60); go.AddChild(b);
        var hint = Style.Text("单击任意处继续", 17, new Color(Style.PaperDim, 0.7f)); hint.HorizontalAlignment = HorizontalAlignment.Center; col.AddChild(hint);
        root.GuiInput += e => { if (e is InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Left }) Close(); };
        scrim = App.I.Modal(root, false, 0f);
        // resume the caller only after the removal has finished: resuming inside it opened the next page while the modal layer
        // was still busy removing this one, so that page failed to appear ("Parent node is busy setting up children")
        scrim.TreeExiting += () => { if (App.I.QaRunning) GD.Print("QA_INTERLUDE_EXIT t=" + Time.GetTicksMsec() + " closing=" + closing); Callable.From(() => done.TrySetResult()).CallDeferred(); };
        if (App.I.QaRunning) GD.Print("QA_INTERLUDE_OPEN t=" + Time.GetTicksMsec() + " inTree=" + scrim.IsInsideTree());
        if (App.I.QaRunning) GetTree().CreateTimer(4).Timeout += Close;
        if (S.Settings.ReducedMotion != true) { col.Modulate = new Color(1, 1, 1, 0); col.CreateTween().TweenProperty(col, "modulate:a", 1f, 0.6f); FromBlack(root); }
        return done.Task;
    }

    // Encounter cards and spawns follow the log (playtest 2 #11, #17, #18): every encounter card the log shows as drawn
    // since the last look is turned over in the log's order, and every enemy that entered play without a draw (spawned
    // from the set-aside cards by an act or agenda) is shown too. Draws already in the log when the table opens are not.
    readonly HashSet<string> seenDraws = new();
    HashSet<string> knownEnemies = new(), knownTreacheries = new();
    int lastEncounterDiscard = -1;
    bool drawsPrimed;

    // the draw prompt's top card, and where it was when the deck was clicked (the reveal glides it from there)
    Control? encDeckTop; Rect2? drawFrom; ulong drawFromAt;
    void NoteDeckDraw()
    {
        if (encDeckTop is not null && IsInstanceValid(encDeckTop) && encDeckTop.IsVisibleInTree()) { drawFrom = encDeckTop.GetGlobalRect(); drawFromAt = Time.GetTicksMsec(); }
    }

    // ───────── the mythos draw, made by the player: the encounter deck in the middle of the table, whose turn it is above it
    // the table seen through the draw prompt: a little darker and softly out of focus (a mip level of the screen)
    static Shader? drawVeil;
    static Shader DrawVeil => drawVeil ??= new Shader { Code = @"shader_type canvas_item;
uniform sampler2D screen : hint_screen_texture, filter_linear_mipmap;
void fragment(){
  vec3 c = textureLod(screen, SCREEN_UV, 2.2).rgb;
  COLOR = vec4(c * 0.55, 1.0);
}" };

    Control? promptVeil;
    void ClearPromptVeil() { if (promptVeil is not null && IsInstanceValid(promptVeil)) promptVeil.QueueFree(); promptVeil = null; }

    void EncounterDrawPrompt()
    {
        var m = model!;
        if (!byAnchor.TryGetValue("encdeck", out var list) || list.Count == 0) return;
        if (revealing) return;   // not while the doom flies onto the agenda or a card is shown (r9 #11: one thing at a time)
        // the table behind dims and softly blurs while the deck waits for the click (Yunke 11-20)
        var veil = new ColorRect { MouseFilter = MouseFilterEnum.Ignore, ZIndex = 60, Material = new ShaderMaterial { Shader = DrawVeil } };
        // over the whole table (the map is not in the render layer); taken away again at the start of every render, so it never
        // outlives the prompt (Yunke 11-20: it stayed on after the draw and blurred the table)
        if (promptVeil is not null && IsInstanceValid(promptVeil)) promptVeil.QueueFree();
        AddChild(veil); veil.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect); promptVeil = veil;
        var box = new VBoxContainer { MouseFilter = MouseFilterEnum.Ignore, Alignment = BoxContainer.AlignmentMode.Center, ZIndex = 61 }; box.AddThemeConstantOverride("separation", 12);
        Place(box, -300, -340, 300, 260, 0.5f, 0.5f, 0.5f, 0.5f);
        string who = m.Me is null ? "" : m.Name(m.Me);
        var t = Style.Text((m.Phase == "MythosPhase" ? "神话阶段 · " : "") + who + " 抽取遭遇牌", 28, Style.HoverText, Style.Kai); t.HorizontalAlignment = HorizontalAlignment.Center;
        t.AddThemeConstantOverride("outline_size", 8); t.AddThemeColorOverride("font_outline_color", Colors.Black); box.AddChild(t);
        // the deck: one encounter back in the frame every large card has (Yunke 11-20: a single card, not three stacked), as big
        // as the revealed card that turns over after the click (300 × 420), breathing like every other thing that can be clicked
        var deck = new Control { CustomMinimumSize = new Vector2(300, 420), SizeFlagsHorizontal = SizeFlags.ShrinkCenter, MouseFilter = MouseFilterEnum.Stop, MouseDefaultCursorShape = CursorShape.PointingHand, TooltipText = "遭遇牌库 " + m.EncounterDeckSize + " 张" };
        // the back keeps its own proportions inside 300 × 420: centred there, and the glow drawn round the card as it really is
        // (Yunke 11-21: the glow, set for 300 × 420, sat askew round the narrower card)
        var top = Peek.FramedCard(Ui.CardBack(true), new Vector2(300, 420)); deck.AddChild(top);
        var cardSize = top.CustomMinimumSize; top.Position = ((new Vector2(300, 420) - cardSize) / 2).Round(); top.Size = cardSize; encDeckTop = top;
        float rad = Math.Clamp(Math.Min(cardSize.X, cardSize.Y) * 0.045f, 8, 22);
        box.AddChild(Clickable(deck, "encdeck", frame: new Rect2(top.Position, cardSize), radius: (int)rad));   // rounded like the card's own frame
        var hint = Style.Text("点击遭遇牌库抽牌（D）", 19, Style.Amber, Style.Kai); hint.HorizontalAlignment = HorizontalAlignment.Center;
        hint.AddThemeConstantOverride("outline_size", 6); hint.AddThemeColorOverride("font_outline_color", Colors.Black); box.AddChild(hint);
        // it comes in once the phase bands have played (they would cover its title)
        box.Modulate = new Color(1, 1, 1, 0); veil.Modulate = new Color(1, 1, 1, 0);
        void Show()
        {
            if (!IsInstanceValid(box)) return;
            if (S.Settings.ReducedMotion == true) { box.Modulate = Colors.White; if (IsInstanceValid(veil)) veil.Modulate = Colors.White; Locate(); return; }
            if (IsInstanceValid(veil)) veil.CreateTween().TweenProperty(veil, "modulate:a", 1f, 0.3f);
            var tw = box.CreateTween(); tw.TweenProperty(box, "modulate:a", 1f, 0.25f); tw.TweenCallback(Callable.From(Locate));   // its ripple now that it is seen
        }
        // look at the banner queue only once this render has finished: the phase banners of this same state (enemy →
        // upkeep → mythos) are queued at the end of the render, after this box is built (Yunke 11-18 r7: the draw box
        // showed up while the bands were still playing)
        Callable.From(() =>
        {
            var wait = bannerDone.Task;
            if (wait.IsCompleted && !bannerRunning) Show(); else wait.ContinueWith(_ => Callable.From(Show).CallDeferred());
        }).CallDeferred();
    }

    // A card the core has drawn but not yet resolved is in no zone of the table: the only trace is the "when drawn" window the
    // question carries (守护结界 asks right then whether to cancel it). That card is turned over first, before the question,
    // so the player sees what was drawn before deciding (Yunke 11-18 #12).
    readonly HashSet<string> windowRevealedIds = new();
    string? lastDrawer;
    readonly List<string> shownFromWindow = new();
    static void WindowDraws(JsonElement e, List<(string Id, string Code)> acc, bool inDraw, int depth)
    {
        if (depth > 40) return;
        if (e.ValueKind == JsonValueKind.Array) { foreach (var x in e.EnumerateArray()) WindowDraws(x, acc, inDraw, depth + 1); return; }
        if (e.ValueKind != JsonValueKind.Object) return;
        bool draw = inDraw || J.String(e, "tag") is "DrawCard" or "DrawCards";
        if (draw && J.String(e, "cardCode") is { Length: > 0 } code) { acc.Add((J.String(e, "id"), CoreCatalog.Normalize(code))); return; }
        foreach (var p in e.EnumerateObject()) WindowDraws(p.Value, acc, draw, depth + 1);
    }

    async Task RevealFromWindow()
    {
        if (model is null || state is null || state.Question.ValueKind != JsonValueKind.Object) return;
        var acc = new List<(string Id, string Code)>();
        WindowDraws(state.Question, acc, false, 0);
        var fresh = acc.Where(d => J.String(S.Card(d.Code), "encounter_code").Length > 0 && windowRevealedIds.Add(d.Id.Length > 0 ? d.Id : d.Code + "@" + state?.Sequence)).Select(d => d.Code).Distinct().ToList();
        if (fresh.Count == 0) return;
        revealing = true;
        try
        {
            Render();
            await bannerDone.Task;
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            foreach (var code in fresh)
            {
                if (App.I.QaRunning) GD.Print("REVEAL_WINDOW " + code + " before " + string.Join(",", model.Question.Choices.Where(c => c.Enabled).Select(c => c.Title)));
                shownFromWindow.Add(code); await RevealCard(code, pending: true);
            }
        }
        finally { revealing = false; }
    }

    async Task CheckEncounterDraws()
    {
        if (model is null) return;
        await RevealFromWindow();
        int deck = model.EncounterDeckSize;
        var enemyIds = model.Enemies.Where(e => !IsWeakness(S, e.Code)).Select(e => e.Id).ToHashSet();   // weaknesses: CheckWeaknessDraws
        bool deckShrank = lastEncounterDeck >= 0 && deck < lastEncounterDeck;
        int prevDeck = lastEncounterDeck, prevDisc = lastEncounterDiscard;
        var arrived = drawsPrimed ? enemyIds.Except(knownEnemies).ToList() : new List<string>();
        // cards from decks other than the encounter deck (the cultist deck, an exhibit deck…) or a treachery that went straight
        // to the discard: a new treachery in play or a longer encounter discard also sends us to the log (Yunke 11-20: every
        // revealed encounter card is turned over)
        var trIds = model.Treacheries.Where(t => !IsWeakness(S, t.Code)).Select(t => t.Id).ToHashSet();
        int disc = model.EncounterDiscard.Count();
        bool other = drawsPrimed && (trIds.Except(knownTreacheries).Any() || (lastEncounterDiscard >= 0 && disc > lastEncounterDiscard));
        knownTreacheries = trIds; lastEncounterDiscard = disc;
        lastEncounterDeck = deck; knownEnemies = enemyIds;
        if (!drawsPrimed) { drawsPrimed = true; await NewDraws(); return; }   // prime: what is already in the log stays unshown
        if (!deckShrank && arrived.Count == 0 && !other) return;
        var codes = await NewDraws();
        // cards that left the deck for its discard pile without being drawn (an act's "discard the top …"): they fly there
        if (deckShrank && codes.Count == 0 && arrived.Count == 0 && model is not null)
        {
            int gone = Math.Min(prevDeck - deck, 5), grew = Math.Max(0, model.EncounterDiscard.Count() - prevDisc);
            if (gone > 0 && grew > 0) { Render(); await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame); await FlyEncounterDiscards(model.EncounterDiscard.Take(Math.Min(gone, grew)).ToList()); }
        }
        // enemies that came into play with no draw of their own (e.g. the Ghoul Priest, ghouls put out by an advance)
        var unmatched = new List<string>(codes);
        foreach (var id in arrived)
        {
            var e = model.Enemies.FirstOrDefault(x => x.Id == id); if (e is null) continue;
            if (unmatched.Contains(e.Code)) unmatched.Remove(e.Code); else codes.Add(e.Code);
        }
        // a card already turned over from its "when drawn" window is not shown a second time
        foreach (var c in codes.ToList()) if (shownFromWindow.Remove(c)) codes.Remove(c);
        if (codes.Count == 0 || codes.Count > 6) { lastDrawer = null; return; }
        // Order (Yunke): the phase banner, then each drawn card, and only then the card's effect (its test or question).
        // A draw in the mythos phase is turned over before the investigation band (11-20 #16: the last drawer's card came
        // after it): that band is held until the cards are shown, the mythos band is waited for.
        revealing = true;
        var hold = revealHold = new TaskCompletionSource();
        try
        {
            Render();
            // wait for the bands before the investigation band (that one waits for us); with no investigation band, for all
            Task gate = bannerDone.Task;
            if (bannerEnds.ContainsKey("InvestigationPhase"))
                gate = bannerEnds.TryGetValue("MythosPhase", out var myth) ? myth.Task : bannerEnds.TryGetValue("UpkeepPhase", out var up) ? up.Task
                    : bannerEnds.TryGetValue("EnemyPhase", out var en) ? en.Task : Task.CompletedTask;
            await gate;
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            foreach (var code in codes) await RevealCard(code);
            effectSource = S.Name(CoreCatalog.Normalize(codes[^1])); effectSourceAge = 0;   // what the next damage or horror came from
        }
        finally { revealing = false; lastDrawer = null; hold.TrySetResult(); if (revealHold == hold) revealHold = null; }
    }

    // Encounter cards drawn in log rows not looked at before, oldest first; each draw is remembered so it shows once.
    async Task<List<string>> NewDraws()
    {
        var found = new List<string>();
        try
        {
            var h = await B.Logs(state!);
            var renderer = new ClientLogText(S.Locale, r => r.CardCode is string code ? CardReader.ShownName(code) : null);
            void Refs(ClientLogPart p, List<string> acc, int depth)
            {
                if (depth > 8) return;
                if (p.Reference is { } r && r.CardCode is string c && !r.FaceDown) acc.Add(CoreCatalog.Normalize(c));
                if (p.Items is not null) foreach (var i in p.Items) Refs(i, acc, depth + 1);
                if (p.Variables is not null) foreach (var v in p.Variables.Values) Refs(v, acc, depth + 1);
            }
            foreach (var row in h.Rows)
            {
                int n = 0;
                void Scan(ClientLogEntry e)
                {
                    string body = renderer.Body(e);
                    if (body.Contains("抽取") || body.Contains("抽到"))
                    {
                        var acc = new List<string>(); foreach (var p in e.Body) Refs(p, acc, 0);
                        foreach (var c in acc)
                        {
                            if (J.String(S.Card(c), "encounter_code").Length == 0 || J.String(S.Card(c), "type_code") is not ("enemy" or "treachery" or "story")) continue;
                            string key = row.Key.HostSequence + ":" + (n++) + ":" + c;
                            if (seenDraws.Add(key)) found.Add(c);
                        }
                    }
                    foreach (var ch in e.Children) Scan(ch);
                }
                if (row.Entry is ClientLogEntry entry) Scan(entry);
            }
        }
        catch { }
        return found;
    }

    Task RevealCard(string code, bool pending = false, bool weakness = false)
    {
        var done = new TaskCompletionSource();
        if (App.I.QaRunning) GD.Print("QA_REVEAL_CARD " + code + (pending ? " pending" : "") + (weakness ? " weakness" : ""));
        GameAudio.Cue("encounter");   // noncore audio 11-19
        var card = S.Card(code); string type = J.String(card, "type_code");
        bool inPlay = model!.Enemies.Any(e => e.Code == code) || model.Treacheries.Any(t => t.Code == code);
        if (!inPlay && type == "enemy")
            // Kept on purpose: if the core ever leaves an enemy out of play, record exactly what the table saw.
            GD.Print("REVEAL_NOT_IN_PLAY code=" + code + " enemies=" + string.Join(",", model.Enemies.Select(e => e.Code)) + " loc=" + (model.MyLocation is null ? "none" : model.Name(model.MyLocation)) + " seq=" + state?.Sequence);
        // where it came out: the location it spawned at, or the investigator it engaged
        string where = "";
        if (type == "enemy" && model.Enemies.LastOrDefault(e => e.Code == code) is { } en)
        {
            var pl = J.Get(en.Raw, "placement"); string pid = J.String(pl, "contents");
            if (J.Tag(pl) == "AtLocation" && model.Location(pid) is { } at) where = "在" + model.Name(at);
            else if (model.Investigators.FirstOrDefault(i => i.Id == pid || J.Items(J.Get(en.Raw, "engagedInvestigators")).Any(x => x.GetString() == i.Id)) is { } who)
                where = "与" + model.Name(who) + "交战，在" + (model.Location(model.LocationOf(who)) is { } wl ? model.Name(wl) : "");
        }
        string line = weakness ? "弱点出现。" : pending ? "它的效果即将结算。" : type == "enemy"
            ? (inPlay ? (where.Length > 0 ? "它" + where + "现身了。" : "它即将按卡上的生成地点现身。") : "它找不到可以现身的地方，被弃掉了。")
            : "诡计降临。";
        var col = new VBoxContainer { Alignment = BoxContainer.AlignmentMode.Center, MouseFilter = MouseFilterEnum.Ignore }; col.AddThemeConstantOverride("separation", 14);
        // who drew it: the investigator whose seat the core is asking (the drawer), as the html table names the drawer
        // (after a draw the core may already be asking the next investigator, so the one who clicked the deck is remembered)
        string drawer = lastDrawer ?? (model.Me is null ? "" : model.Name(model.Me));
        var head = Style.Text(weakness ? (drawer.Length > 0 ? drawer + " 抽到了弱点" : "抽到了弱点") : drawer.Length > 0 && (pending || model.Phase == "MythosPhase") ? drawer + " 抽到了" : model.Phase == "MythosPhase" ? "神话 · 遭遇" : type == "enemy" ? "敌人出现" : "遭遇", 24, Style.Brass, Style.Kai); head.HorizontalAlignment = HorizontalAlignment.Center; col.AddChild(head);
        // As the html encounter reveal (EncounterRevealOverlay + DrawSpotlight): the card shows its encounter back, turns over
        // in 0.3 s, and stands in a pulsing indigo glow; the table stays dimmed until it is clicked away.
        // the printed card itself, as html shows it (Card.vue), not the redrawn small card; its rules are on its face
        var img = new CardView { Code = code, Redrawn = false, CustomMinimumSize = new Vector2(300, 420), Size = new Vector2(300, 420), ShowCost = false, HoverZoom = false, Rim = type == "enemy" ? new Color("c0543e") : new Color("8a5aa8") };
        var stage = new Control { CustomMinimumSize = new Vector2(300, 420), MouseFilter = MouseFilterEnum.Ignore };
        // the glow starts right at the card's edge (Yunke 11-20: a gap showed between them): filled under the card, the card's corners
        var glow = new Panel { MouseFilter = MouseFilterEnum.Ignore, Size = new Vector2(300, 420) };
        var gsb = new StyleBoxFlat { BgColor = new Color(0.48f, 0.36f, 0.86f, 0.9f), ShadowColor = new Color(0.48f, 0.36f, 0.86f, 0.75f), ShadowSize = 46, AntiAliasing = true }; gsb.SetCornerRadiusAll(12);
        glow.AddThemeStyleboxOverride("panel", gsb); stage.AddChild(glow);
        stage.AddChild(img); img.Position = Vector2.Zero;
        // a weakness comes off the player's own deck; the back clipped to the glow's rounded corners (Yunke 11-22: at the moment of
        // the draw the square back stuck out of the rounded frame)
        var backImg = RoundedBack(new Vector2(300, 420), 12, !weakness); stage.AddChild(backImg);
        // surge (涌动): a small tag pops onto the card once it is face up — another card is drawn after this one (Yunke 11-20)
        // only the keyword itself ("涌动。" on a line of its own): a surge that depends on something ("如果你没有线索，获得涌动") is not
        // announced, since the table cannot tell beforehand whether it applies
        bool surge = !weakness && Regex.IsMatch(J.String(card, "text"), @"(^|\n)\s*(<b>)?涌动(</b>)?\s*[。.]");
        Control? surgeTag = null;
        if (surge)
        {
            var st = new PanelContainer { MouseFilter = MouseFilterEnum.Ignore, Visible = false };
            var ssb = new StyleBoxFlat { BgColor = new Color(0.32f, 0.12f, 0.5f, 0.95f), BorderColor = new Color("c9a0e8"), ShadowColor = new Color(0.6f, 0.4f, 0.95f, 0.8f), ShadowSize = 14, AntiAliasing = true,
                ContentMarginLeft = 16, ContentMarginRight = 16, ContentMarginTop = 6, ContentMarginBottom = 6 };
            ssb.SetBorderWidthAll(2); ssb.SetCornerRadiusAll(18); st.AddThemeStyleboxOverride("panel", ssb);
            st.AddChild(Style.Text("涌动 · 再抽一张", 22, Colors.White, Style.Kai));
            stage.AddChild(st); st.Position = new Vector2(150 - 92, -22); surgeTag = st;
        }
        var holder = new CenterContainer { MouseFilter = MouseFilterEnum.Ignore }; holder.AddChild(stage); col.AddChild(holder);
        var sub = Style.Text(line, 24, new Color("f1e6cc"), Style.Kai); sub.HorizontalAlignment = HorizontalAlignment.Center; col.AddChild(sub);
        var hint = Style.Text("单击任意处继续", 17, new Color(Style.PaperDim, 0.75f)); hint.HorizontalAlignment = HorizontalAlignment.Center; col.AddChild(hint);
        var scrim = App.I.Modal(col, true, 0.86f);
        QaMidShot(0.6);
        col.MouseFilter = MouseFilterEnum.Ignore;
        img.OnClick = () => App.I.CloseModal(scrim);
        void ShowSurge()
        {
            if (surgeTag is null || !IsInstanceValid(surgeTag)) return;
            surgeTag.Visible = true; surgeTag.PivotOffset = new Vector2(92, 20); surgeTag.Scale = new Vector2(0.4f, 0.4f);
            surgeTag.CreateTween().TweenProperty(surgeTag, "scale", Vector2.One, 0.3f).SetTrans(Tween.TransitionType.Back).SetEase(Tween.EaseType.Out);
            if (App.I.QaRunning) GD.Print("QA_SURGE " + code);
        }
        // the deck the player just clicked (the draw prompt): its top card travels from there to the middle and turns over there,
        // one movement instead of the prompt vanishing and a second card appearing (Yunke 11-20)
        Rect2? from = !weakness && drawFrom is { } df && Time.GetTicksMsec() - drawFromAt < 8000 ? df : null;
        drawFrom = null;
        if (S.Settings.ReducedMotion == true) { backImg.Visible = false; ShowSurge(); }
        else
        {
            // the turn: the back narrows to an edge, the face widens from it (html rotateY over 0.3 s)
            stage.PivotOffset = new Vector2(150, 210); img.Visible = false;
            void Turn(double wait)
            {
                var tw = stage.CreateTween();
                tw.TweenInterval(wait);
                tw.TweenProperty(stage, "scale:x", 0f, 0.15f).SetTrans(Tween.TransitionType.Sine).SetEase(Tween.EaseType.In);
                tw.TweenCallback(Callable.From(() => { backImg.Visible = false; img.Visible = true; }));
                tw.TweenProperty(stage, "scale:x", 1f, 0.15f).SetTrans(Tween.TransitionType.Sine).SetEase(Tween.EaseType.Out);
                tw.TweenCallback(Callable.From(ShowSurge));
            }
            if (from is { } fr && scrim.GetParent() is Node layer)
            {
                stage.Modulate = new Color(1, 1, 1, 0);
                var ghost = RoundedBack(fr.Size, 6, true);
                layer.AddChild(ghost); ghost.GlobalPosition = fr.Position;
                Callable.From(() =>
                {
                    if (!IsInstanceValid(ghost) || !IsInstanceValid(stage)) return;
                    var gt = ghost.CreateTween();
                    gt.TweenProperty(ghost, "global_position", stage.GlobalPosition, 0.3f).SetTrans(Tween.TransitionType.Cubic).SetEase(Tween.EaseType.Out);
                    gt.TweenCallback(Callable.From(() => { ghost.QueueFree(); stage.Modulate = Colors.White; Turn(0.05); }));
                    if (App.I.QaRunning) GD.Print("QA_DRAW_GLIDE from=" + fr.Position + " to=" + stage.GlobalPosition);
                }).CallDeferred();
            }
            else Turn(0.25);
            var pulse = glow.CreateTween().SetLoops();
            pulse.TweenProperty(glow, "modulate:a", 0.45f, 2f).SetTrans(Tween.TransitionType.Sine); pulse.TweenProperty(glow, "modulate:a", 1f, 2f).SetTrans(Tween.TransitionType.Sine);
        }
        // it stays until clicked away (Yunke 11-18: it sometimes closed by itself after 2 s); only the QA autoplay clicks it
        double t = 0;
        Rect2 seen = default;
        void Tick()
        {
            if (IsInstanceValid(stage) && stage.IsInsideTree()) seen = stage.GetGlobalRect();
            if (!IsInstanceValid(scrim) || !scrim.IsInsideTree()) { if (!weakness) FlyRevealedAway(code, type, seen); done.TrySetResult(); return; }
            t += 0.1; if (autoplaying && t >= 1.2) { App.I.CloseModal(scrim); done.TrySetResult(); return; }
            GetTree().CreateTimer(0.1).Timeout += Tick;
        }
        GetTree().CreateTimer(0.1).Timeout += Tick;
        return done.Task;
    }

    // Once a revealed card is clicked away it goes where it went (Yunke 11-21: "少一个遭遇牌飞到右上的队伍详情区的动画"):
    // into the threat area of the investigator in view, onto a teammate's card in the party column when it went to them,
    // onto the enemy's own card on the table — a copy shrinks on its way there and melts into it.
    void FlyRevealedAway(string code, string type, Rect2 from)
    {
        var m = model; if (m is null || from.Size.X <= 0 || S.Settings.ReducedMotion == true) return;
        Control? to = null;
        var tr = m.Treacheries.LastOrDefault(t => t.Code == code);
        var en = type == "enemy" ? m.Enemies.LastOrDefault(e => e.Code == code) : null;
        string holder = "";
        if (tr is not null)
        {
            var pl = J.Get(tr.Raw, "placement");
            if (J.Tag(pl) == "InThreatArea") holder = J.String(pl, "contents");
            if (holder.Length > 0 && holder != m.Me?.Id && teamPics.TryGetValue(holder, out var pic) && IsInstanceValid(pic)) to = pic;
            else if (anchors.TryGetValue(m.ThreatKey(tr), out var at) && IsInstanceValid(at) && at.IsVisibleInTree()) to = at;
        }
        else if (en is not null)
        {
            holder = J.Items(J.Get(en.Raw, "engagedInvestigators")).Select(x => x.ValueKind == JsonValueKind.String ? x.GetString()! : "").FirstOrDefault(x => x.Length > 0) ?? "";
            if (anchors.TryGetValue("enemy:" + en.Id, out var ea) && IsInstanceValid(ea) && ea.IsVisibleInTree()) to = ea;
            else if (holder.Length > 0 && teamPics.TryGetValue(holder, out var pic) && IsInstanceValid(pic)) to = pic;
        }
        // drawn and still held up (focusedCards, a "when drawn" window open): it waits in the threat area as "抽到的遭遇卡"
        // (Yunke 11-22: no flight into the threat area after the draw was shown)
        if (to is null && drawnCode == code && drawnCard is not null && IsInstanceValid(drawnCard) && drawnCard.IsVisibleInTree()) to = drawnCard;
        if (to is null) return;
        var dest = to.GetGlobalRect();
        if (App.I.QaRunning) GD.Print("QA_REVEAL_AWAY " + code + " -> " + (to == (holder.Length > 0 && teamPics.TryGetValue(holder, out var tp) ? tp : null) ? "team:" + holder : "table") + " " + dest);
        var ghost = new CardView { Code = code, Redrawn = false, CustomMinimumSize = from.Size, Size = from.Size, ShowCost = false, HoverZoom = false, MouseFilter = MouseFilterEnum.Ignore, ZIndex = 120 };
        AddChild(ghost); ghost.GlobalPosition = from.Position; ghost.PivotOffset = Vector2.Zero;
        float k = Math.Min(dest.Size.X / from.Size.X, dest.Size.Y / from.Size.Y);
        var end = dest.GetCenter() - from.Size * k / 2;
        var tw = ghost.CreateTween();
        tw.TweenProperty(ghost, "global_position", end, 0.45f).SetTrans(Tween.TransitionType.Cubic).SetEase(Tween.EaseType.InOut);
        tw.Parallel().TweenProperty(ghost, "scale", new Vector2(k, k), 0.45f).SetTrans(Tween.TransitionType.Cubic).SetEase(Tween.EaseType.InOut);
        tw.TweenProperty(ghost, "modulate:a", 0f, 0.18f);
        tw.TweenCallback(Callable.From(ghost.QueueFree));
        // the card it lands on gives a little nod
        tw.Parallel().TweenCallback(Callable.From(() =>
        {
            if (!IsInstanceValid(to)) return;
            to.PivotOffset = to.Size / 2;
            var nod = to.CreateTween(); nod.TweenProperty(to, "scale", new Vector2(1.08f, 1.08f), 0.1f); nod.TweenProperty(to, "scale", Vector2.One, 0.16f);
        }));
    }
}
