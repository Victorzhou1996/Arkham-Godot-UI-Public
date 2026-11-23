using Godot;
using Gathering;

namespace ArkhamUI;

// A card that leaves the table by being shuffled back into a deck flies there (Yunke 11-20 #18: 随行鬼影, when it would be
// defeated, is shuffled into its bearer's deck — it just vanished). Each render remembers where every enemy, treachery and
// asset was drawn; when one is gone and your deck grew, a copy flies from its last place onto your draw pile; when the
// encounter deck grew instead, it flies up and away (the encounter deck is not on the table between draws).
public partial class TableScreen
{
    Dictionary<string, (string Code, Rect2 Rect)> tableWas = new();
    int deckWas = -1, encWas = -1;

    void FlyShuffles()
    {
        var m = model; if (m?.Me is not { } me) return;
        GetTree().CreateTimer(0.05).Timeout += () =>
        {
            if (!IsInstanceValid(this) || model is not { } mm || mm.Me is not { } me2) return;
            var codes = mm.Enemies.Concat(mm.Treacheries).Concat(mm.Assets).ToDictionary(e => e.Id, e => e.Code);
            var now = new Dictionary<string, (string, Rect2)>();
            foreach (var (key, node) in anchors)
            {
                int colon = key.IndexOf(':'); if (colon < 0) continue;
                string kind = key[..colon], id = key[(colon + 1)..];
                if (kind is not ("enemy" or "tr" or "asset") || !codes.TryGetValue(id, out var code)) continue;
                if (!IsInstanceValid(node) || !node.IsVisibleInTree()) continue;
                now[id] = (code, node.GetGlobalTransform() * new Rect2(Vector2.Zero, node.Size));
            }
            int deckNow = mm.DeckSize(me2), encNow = mm.EncounterDeckSize;
            bool still = S.Settings.ReducedMotion == true;
            if (!still && tableWas.Count > 0)
            {
                foreach (var (id, (code, rect)) in tableWas)
                {
                    if (codes.ContainsKey(id) || rect.Size.X <= 0) continue;   // still in play (moved or not)
                    if (deckWas >= 0 && deckNow > deckWas && deckPileNode is not null && IsInstanceValid(deckPileNode))
                    {
                        if (App.I.QaRunning) GD.Print("QA_SHUFFLE_FLY " + code + " -> deck");
                        Launch(code, rect, deckPileNode); QaShuffleShot();
                    }
                    else if (encWas >= 0 && encNow > encWas)
                    {
                        if (App.I.QaRunning) GD.Print("QA_SHUFFLE_FLY " + code + " -> encounter deck");
                        AwayToEncounter(code, rect); QaShuffleShot();
                    }
                }
            }
            tableWas = now; deckWas = deckNow; encWas = encNow;
        };
    }

    // QA only: shufshot:<path> captures the next shuffle-back flight partway (other flights would take a midshot first)
    internal static string qaShuffleShot = "";
    void QaShuffleShot()
    {
        if (!App.I.QaRunning || qaShuffleShot.Length == 0) return;
        var at = System.IO.Path.GetFullPath(System.IO.Path.Combine(App.I.Root, qaShuffleShot)); qaShuffleShot = "";
        GetTree().CreateTimer(0.22).Timeout += () => App.I.Capture(at);
    }

    // up towards the act and agenda, shrinking and fading: back into the encounter deck
    void AwayToEncounter(string code, Rect2 from)
    {
        var cv = new CardView { Code = code, CustomMinimumSize = from.Size, Size = from.Size, ShowCost = false, ShowName = false, HoverZoom = false, MouseFilter = MouseFilterEnum.Ignore, ZIndex = 140 };
        AddChild(cv); cv.GlobalPosition = from.Position; cv.PivotOffset = from.Size / 2;
        var to = new Vector2(GetViewportRect().Size.X / 2 - from.Size.X / 2, 20);
        var tw = cv.CreateTween().SetParallel();
        tw.TweenProperty(cv, "global_position", to, 0.6f).SetTrans(Tween.TransitionType.Sine).SetEase(Tween.EaseType.In);
        tw.TweenProperty(cv, "scale", new Vector2(0.4f, 0.4f), 0.6f);
        tw.TweenProperty(cv, "rotation_degrees", 180f, 0.6f);
        tw.TweenProperty(cv, "modulate:a", 0f, 0.3f).SetDelay(0.3f);
        tw.Chain().TweenCallback(Callable.From(cv.QueueFree));
    }

    // The core shuffled the viewed investigator's deck in this step (TableState.Shuffled: the backend compares the deck order
    // with the step before; the core logs no shuffle). Yunke 11-22: after 研究馆员's search nothing showed the deck was shuffled,
    // and when tomes happened to come up on top it looked as if it had not been. The pile riffles, its shuffle sound plays, and a
    // line says so.
    long shuffleShownFor = -1;
    void DeckShuffledFx()
    {
        var st = state; var m = model; if (st is null || m?.Me is not { } me || st.Shuffled is not { Length: > 0 } sh) return;
        if (st.Sequence == shuffleShownFor || !sh.Contains(me.Id)) return;
        shuffleShownFor = st.Sequence;
        if (App.I.QaRunning) GD.Print("QA_DECK_SHUFFLED " + me.Id + " seq=" + st.Sequence);
        GameAudio.Cue("shuffle");
        Float(m.Name(me) + "：牌库已洗混", Style.Paper, 0);
        if (S.Settings.ReducedMotion == true || deckPileNode is not { } pile || !IsInstanceValid(pile)) return;
        // the backs in the pile split left and right twice and settle back
        int k = 0;
        foreach (var back in pile.GetChildren().OfType<Control>().Where(c => c is not Label).Take(4))
        {
            var home = back.Position; float dx = (k++ % 2 == 0 ? -1 : 1) * 14;
            var tw = back.CreateTween();
            for (int r = 0; r < 2; r++)
            {
                tw.TweenProperty(back, "position", home + new Vector2(dx, -4), 0.09f).SetTrans(Tween.TransitionType.Sine);
                tw.TweenProperty(back, "position", home, 0.11f).SetTrans(Tween.TransitionType.Sine);
            }
        }
    }
}
