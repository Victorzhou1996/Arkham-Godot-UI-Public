using Godot;
using Gathering;

namespace ArkhamUI;

// A card played from the hand flies from where it left the hand to where it lands: an asset to its place in the
// player area, an event to the discard pile (Yunke: playing a card had no movement into the asset area).
public partial class TableScreen
{
    (string Code, Rect2 From, int Renders)? pendingFly;
    // QA only: a screenshot taken partway through the next flight or token flip (timer waits are not reliable while the core works)
    internal static string? qaMidShot;   // static: a QA script may arm it on the main menu, before the table opens
    void QaMidShot(double after)
    {
        if (qaMidShot is not string spec) return;
        qaMidShot = null;
        var parts = spec.Split(','); string path = parts[0];
        if (parts.Length > 1 && double.TryParse(parts[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double d)) after = d;
        GetTree().CreateTimer(after).Timeout += () => App.I.Capture(System.IO.Path.GetFullPath(System.IO.Path.Combine(App.I.Root, path)));
    }

    // The card whose ability offered this choice (the first AssetSource in the choice's messages), if it is on screen.
    Control? SourceNode(ChoicePresentation c)
    {
        string? id = FindAssetSource(c.Raw, 0);
        return id is not null && anchors.TryGetValue("asset:" + id, out var n) && IsInstanceValid(n) ? n : null;
    }
    static string? FindAssetSource(System.Text.Json.JsonElement e, int depth)
    {
        if (depth > 12) return null;
        if (e.ValueKind == System.Text.Json.JsonValueKind.Object)
        {
            if (J.Tag(e) == "AssetSource" && J.Get(e, "contents") is { ValueKind: System.Text.Json.JsonValueKind.String } id) return id.GetString();
            foreach (var p in e.EnumerateObject()) if (FindAssetSource(p.Value, depth + 1) is string f) return f;
        }
        else if (e.ValueKind == System.Text.Json.JsonValueKind.Array)
            foreach (var x in e.EnumerateArray()) if (FindAssetSource(x, depth + 1) is string f) return f;
        return null;
    }

    // After a render: every target on the table that a resolving card may pick gets an arrow from that card.
    void LinkSources()
    {
        if (model is null) return;
        foreach (var c in model.Question.Choices.Where(c => c.Enabled))
        {
            string a = model.Anchor(c);
            if (!(a.StartsWith("enemy:") || a.StartsWith("loc:") || a.StartsWith("inv:"))) continue;
            if (!anchors.TryGetValue(a, out var target) || SourceNode(c) is not Control src) continue;
            arrows.Link(src, target);
        }
    }

    // Enemies attacking right now: every "attackEnemy" the current question carries (attacks of the enemy phase, attacks of
    // opportunity, ordering several attacks, assigning the damage of one).
    HashSet<string> attacking = new();
    static void FindAttackers(System.Text.Json.JsonElement e, HashSet<string> acc, int depth)
    {
        if (depth > 16) return;
        if (e.ValueKind == System.Text.Json.JsonValueKind.Object)
        {
            // a question asked on an enemy's behalf (assigning the damage and horror of its attack) names the enemy as its source
            if (J.Tag(e) == "QuestionWithSource" && J.Tag(J.Get(e, "source")) == "EnemySource" && J.Get(J.Get(e, "source"), "contents") is { ValueKind: System.Text.Json.JsonValueKind.String } src)
                acc.Add(src.GetString()!);
            foreach (var p in e.EnumerateObject())
            {
                if (p.Name == "attackEnemy" && p.Value.ValueKind == System.Text.Json.JsonValueKind.String) acc.Add(p.Value.GetString()!);
                else FindAttackers(p.Value, acc, depth + 1);
            }
        }
        else if (e.ValueKind == System.Text.Json.JsonValueKind.Array) foreach (var x in e.EnumerateArray()) FindAttackers(x, acc, depth + 1);
    }
    HashSet<string> Attackers(TableModel m) { var acc = new HashSet<string>(); FindAttackers(m.State.Question, acc, 0); return acc; }

    // The attacking enemy: a red glow around its card and a short wiggle, so the attack is seen before it lands (playtest 2 #8).
    void MarkAttacker(CardView cv)
    {
        var glow = new Panel { MouseFilter = MouseFilterEnum.Ignore, ZIndex = 5 };
        var sb = new StyleBoxFlat { DrawCenter = false, BgColor = new Color(0, 0, 0, 0), BorderColor = new Color("ff6a4d"), ShadowColor = new Color(1f, 0.3f, 0.2f, 0.7f), ShadowSize = 18, AntiAliasing = true };
        sb.SetBorderWidthAll(4); sb.SetCornerRadiusAll(10); glow.AddThemeStyleboxOverride("panel", sb);
        cv.AddChild(glow); glow.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        if (S.Settings.ReducedMotion == true) return;
        var pulse = glow.CreateTween().SetLoops(); pulse.TweenProperty(glow, "modulate:a", 0.45f, 0.35f); pulse.TweenProperty(glow, "modulate:a", 1f, 0.35f);
        var w = cv.CreateTween().SetLoops(3);
        w.TweenProperty(cv, "rotation_degrees", 4f, 0.06f); w.TweenProperty(cv, "rotation_degrees", -4f, 0.12f); w.TweenProperty(cv, "rotation_degrees", 0f, 0.06f);
        w.TweenInterval(0.25);
    }

    // Called before a choice is answered: remember the hand card being played and where it is on screen.
    void NoteHandPlay(ChoicePresentation c)
    {
        if (model is null || !c.Title.StartsWith("打出")) return;
        string a = model.Anchor(c);
        if (!a.StartsWith("hand:") || !anchors.TryGetValue(a, out var node) || node is not CardView cv || !IsInstanceValid(cv)) return;
        string code = CoreCatalog.Normalize(cv.Code);
        // an event goes to the discard pile once played: it flies there at once (Yunke 11-20: 闪躲 played against an attack
        // showed nothing — the core went on with the attack, the view turned to the next seat, and the wait for the card to show
        // on this investigator's pile gave up)
        if (J.String(S.Card(code), "type_code") == "event" && discardPileNode is not null && IsInstanceValid(discardPileNode))
        {
            pendingFly = null;
            if (S.Settings.ReducedMotion != true) { var from = cv.GetGlobalRect(); var pile = discardPileNode; cv.Modulate = new Color(1, 1, 1, 0); Launch(code, from, pile); }
            return;
        }
        pendingFly = (code, cv.GetGlobalRect(), 0);
    }

    // Called at the end of every render: once the played card shows up in play (or on the discard pile), fly it there.
    void FlyPlayed()
    {
        if (pendingFly is not { } f || model?.Me is not { } me) return;
        Control? target = anchors.Where(kv => kv.Key.StartsWith("asset:")).Select(kv => kv.Value)
            .FirstOrDefault(n => n is CardView v && IsInstanceValid(v) && CoreCatalog.Normalize(v.Code) == f.Code);
        if (target is CardView && target.GetParent() is Control tile && tile is not Container) target = tile;   // the asset's whole tile (with its uses) appears on landing
        if (target is null && model.Discard(me).FirstOrDefault() is string top && CoreCatalog.Normalize(top) == f.Code) target = discardPileNode;
        if (target is null)
        {
            // the card may land a step later (a target to choose first); give up after a few renders
            pendingFly = f.Renders >= 3 ? null : (f.Code, f.From, f.Renders + 1);
            return;
        }
        pendingFly = null;
        if (S.Settings.ReducedMotion == true || !IsInstanceValid(target)) return;
        // wait one frame so the target has its final layout position
        Callable.From(() => Launch(f.Code, f.From, target)).CallDeferred();
    }

    // Any card that leaves the hand for the discard pile (a discard chosen by a card effect, the hand-size limit at
    // upkeep, a treachery that discards, …) flies from where it was in the hand onto the pile; cards drawn from the deck
    // fly from the pile into the hand (Yunke 11-18 r7). Each investigator's hand, deck and pile are remembered from the
    // last time they were on screen, so a draw made while the view was on the other investigator still shows when the
    // view comes back.
    sealed record HandMemo(Dictionary<string, (string Code, Rect2 Rect)> Cards, int Deck, int Discard);
    readonly Dictionary<string, HandMemo> handMemo = new();
    HandMemo? memoNow;   // the viewed investigator's memo, read at the start of this render
    string? fanOwner;   // whose hand the fan on screen shows
    // start of a render: the cards on screen are still the last render's; note where each one is
    void SnapshotHand()
    {
        if (fanOwner is null || !handMemo.TryGetValue(fanOwner, out var memo)) return;
        foreach (var (cv, _) in fan)
            if (IsInstanceValid(cv) && cv.IsInsideTree() && cv.Key.StartsWith("hand:") && cv.Modulate.A > 0.05f && memo.Cards.TryGetValue(cv.Key[5..], out var c))
                memo.Cards[cv.Key[5..]] = (c.Code, cv.GetGlobalRect());
    }
    // end of a render: what the viewed investigator's hand, deck and pile are now (compared at their next render)
    void RememberHand()
    {
        if (model?.Me is not { } me) { fanOwner = null; return; }
        var cards = model.Hand(me).ToDictionary(h => h.Id, h => (CoreCatalog.Normalize(h.Code), new Rect2()));
        handMemo[me.Id] = new HandMemo(cards, model.DeckSize(me), model.Discard(me).Count());
        fanOwner = me.Id;
    }
    void FlyDiscards()
    {
        memoNow = model?.Me is { } who && handMemo.TryGetValue(who.Id, out var mm) ? mm : null;
        if (model?.Me is not { } me || memoNow is not { } memo) return;
        var pile = model.Discard(me).ToList();
        int added = pile.Count - memo.Discard;
        if (added <= 0 || discardPileNode is null || !IsInstanceValid(discardPileNode) || S.Settings.ReducedMotion == true) return;
        var newTop = pile.Take(added).ToList();   // the pile's newest cards are on top
        var handNow = model.Hand(me).Select(h => h.Item1).ToHashSet();
        int k = 0;
        foreach (var (id, (code, rect)) in memo.Cards)
        {
            if (handNow.Contains(id) || rect.Size.X <= 0) continue;            // still in hand / never drawn on screen
            if (pendingFly is { } f && f.Code == code) continue;                 // an event being played: FlyPlayed flies it
            int at = newTop.IndexOf(code); if (at < 0) continue;                 // left the hand, but not for the discard pile
            newTop.RemoveAt(at);
            var target = discardPileNode; float delay = 0.08f * k++;
            GetTree().CreateTimer(delay).Timeout += () => Callable.From(() => Launch(code, rect, target!)).CallDeferred();
        }
    }

    // Cards drawn from the deck fly from the draw pile into their places in the hand, turning face up on the way.
    // New hand cards while the deck shrank = drawn cards.
    // The opening hand (and the cards a mulligan brings) are dealt the same way, one after another (Yunke 11-22: they just appeared).
    readonly HashSet<string> openingDealt = new();
    internal bool mulliganRedraw;   // set when the opening hand is confirmed with cards to replace (TableScreen.Layout · ConfirmMulligan)
    void FlyDraws()
    {
        if (model?.Me is not { } me || deckPileNode is null || !IsInstanceValid(deckPileNode) || S.Settings.ReducedMotion == true) return;
        bool redraw = mulliganRedraw; mulliganRedraw = false;
        if ((memoNow is null || memoNow.Cards.Count == 0) && InMulligan(model) && openingDealt.Add(me.Id + "@" + J.String(model.Scenario, "id") + "@" + gameId))
        {
            if (App.I.QaRunning) GD.Print("QA_OPENING_DEAL " + me.Id + " " + fan.Count);
            int n = 0;
            foreach (var (cv, _) in fan)
            {
                if (!IsInstanceValid(cv) || !cv.Key.StartsWith("hand:")) continue;
                var target = cv; float delay = 0.25f + 0.11f * n++;
                target.Modulate = new Color(1, 1, 1, 0);
                GetTree().CreateTimer(delay).Timeout += () => Callable.From(() => LaunchDraw(target.Code, deckPileNode is { } dp && IsInstanceValid(dp) ? dp.GetGlobalTransform() * deckTopFrame : new Rect2(), target)).CallDeferred();
            }
            return;
        }
        if (memoNow is not { } memo) return;
        int drawn = memo.Deck - model.DeckSize(me);
        // a mulligan puts the replaced cards back and draws as many: the deck keeps its size, the new cards are still dealt
        if (redraw) drawn = Math.Max(drawn, fan.Count(f => IsInstanceValid(f.Card) && f.Card.Key.StartsWith("hand:") && !memo.Cards.ContainsKey(f.Card.Key[5..])));
        if (drawn <= 0) return;
        var from = deckPileNode.GetGlobalTransform() * deckTopFrame;
        int k = 0;
        foreach (var (cv, _) in fan)
        {
            if (k >= drawn) break;
            if (!IsInstanceValid(cv) || !cv.Key.StartsWith("hand:") || memo.Cards.ContainsKey(cv.Key[5..])) continue;
            var target = cv; float delay = 0.12f * k++;
            target.Modulate = new Color(1, 1, 1, 0);   // the card is not in the hand until it lands
            // drawn in upkeep: the card leaves the pile once the upkeep band has gone, not under the enemy phase's (r9 #11)
            AfterBanner("UpkeepPhase", () => GetTree().CreateTimer(delay).Timeout += () => Callable.From(() => LaunchDraw(target.Code, deckPileNode is { } dp && IsInstanceValid(dp) ? dp.GetGlobalTransform() * deckTopFrame : from, target)).CallDeferred());
        }
    }

    void LaunchDraw(string code, Rect2 from, CardView target)
    {
        if (!IsInstanceValid(target) || !target.IsInsideTree()) return;
        var to = target.GetGlobalRect(); if (to.Size.X <= 0) { target.Modulate = Colors.White; return; }
        if (App.I.QaRunning) { GD.Print("QA_DRAW_FLY " + code + " from=" + from.Position + " to=" + to.Position); QaMidShot(0.25); }
        const float T = 0.5f;
        var mid = from.Position.Lerp(to.Position, 0.5f) + new Vector2(0, -60);   // a small arc up and over
        // first half: the card back leaves the pile and turns edge-on; second half: the face turns toward us and lands
        var back = new CardView { Code = code, FaceDown = true, CustomMinimumSize = from.Size, Size = from.Size, ShowCost = false, ShowName = false, HoverZoom = false, MouseFilter = MouseFilterEnum.Ignore, ZIndex = 140 };
        AddChild(back); back.GlobalPosition = from.Position; back.PivotOffset = from.Size / 2;
        var midSize = from.Size.Lerp(to.Size, 0.5f);
        var tw = back.CreateTween().SetParallel();
        tw.TweenProperty(back, "global_position", mid, T / 2).SetTrans(Tween.TransitionType.Sine).SetEase(Tween.EaseType.Out);
        tw.TweenProperty(back, "scale", new Vector2(0.02f, midSize.Y / from.Size.Y), T / 2).SetTrans(Tween.TransitionType.Sine).SetEase(Tween.EaseType.In);
        tw.Chain().TweenCallback(Callable.From(() =>
        {
            back.QueueFree();
            if (!IsInstanceValid(target)) return;
            var face = new CardView { Code = code, CustomMinimumSize = to.Size, Size = to.Size, ShowCost = false, HoverZoom = false, MouseFilter = MouseFilterEnum.Ignore, ZIndex = 140 };
            AddChild(face); face.PivotOffset = to.Size / 2;
            face.GlobalPosition = mid + (from.Size - to.Size) / 2 * 0;   // continue from the arc's top
            face.Scale = new Vector2(0.02f, midSize.Y / to.Size.Y);
            var t2 = face.CreateTween().SetParallel();
            t2.TweenProperty(face, "global_position", to.Position, T / 2).SetTrans(Tween.TransitionType.Sine).SetEase(Tween.EaseType.In);
            t2.TweenProperty(face, "scale", Vector2.One, T / 2).SetTrans(Tween.TransitionType.Sine).SetEase(Tween.EaseType.Out);
            t2.Chain().TweenCallback(Callable.From(() => { face.QueueFree(); if (IsInstanceValid(target)) { target.Modulate = Colors.White; Punch(target, Style.Amber); } }));
        }));
    }

    void Launch(string code, Rect2 from, Control target)
    {
        if (!IsInstanceValid(target) || !target.IsInsideTree()) return;
        var to = target.GetGlobalRect();
        if (to.Size.X <= 0 || to.Size.Y <= 0) return;
        if (App.I.QaRunning) { GD.Print("QA_FLY " + code + " from=" + from.Position + " to=" + to.Position); QaMidShot(0.2); }
        var ghost = new CardView { Code = code, CustomMinimumSize = from.Size, Size = from.Size, ShowCost = false, HoverZoom = false, MouseFilter = MouseFilterEnum.Ignore, ZIndex = 140 };
        AddChild(ghost); ghost.GlobalPosition = from.Position; ghost.PivotOffset = Vector2.Zero;
        target.Modulate = new Color(1, 1, 1, 0);
        var tw = ghost.CreateTween().SetParallel();
        tw.TweenProperty(ghost, "global_position", to.Position, 0.42f).SetTrans(Tween.TransitionType.Cubic).SetEase(Tween.EaseType.InOut);
        tw.TweenProperty(ghost, "scale", new Vector2(to.Size.X / from.Size.X, to.Size.Y / from.Size.Y), 0.42f).SetTrans(Tween.TransitionType.Cubic).SetEase(Tween.EaseType.InOut);
        tw.Chain().TweenCallback(Callable.From(() =>
        {
            if (IsInstanceValid(target)) { target.Modulate = Colors.White; Punch(target, Style.Amber); }
            ghost.QueueFree();
        }));
    }
}
