using Godot;
using Gathering;

namespace ArkhamUI;

// A card that moved on the map — an investigator's portrait or an enemy at a location — travels from the place it left to the
// place it reached (Yunke 11-20 #22: moves had no animation). It goes along the map's own threads between the places, hop by
// hop, not in a straight line across the map (Yunke 11-20: on a map whose layout was adjusted the straight slide looked odd).
// Each render notes every such card on the map; when one now stands somewhere else, a copy travels the path and the card
// itself shows once it lands.
public partial class TableScreen
{
    readonly Dictionary<string, (CardView Tok, string Loc, bool Portrait)> mapTokens = new();   // this render's cards on the map
    Dictionary<string, (string Loc, Rect2 Rect)> tokenWas = new();                                // where each stood last time

    void NoteInvToken(Entity inv, CardView tok) => mapTokens["inv:" + inv.Id] = (tok, model?.LocationOf(inv) ?? "", true);
    void NoteEnemyToken(Entity en, string locId, CardView tok) => mapTokens["enemy:" + en.Id] = (tok, locId, false);

    void FlyMoves()
    {
        var m = model; if (m is null) return;
        var tokens = mapTokens.Where(t => IsInstanceValid(t.Value.Tok)).ToDictionary(t => t.Key, t => t.Value);
        mapTokens.Clear();
        bool still = S.Settings.ReducedMotion == true;
        var moved = new List<(CardView Tok, bool Portrait, string FromLoc, string ToLoc, Rect2 From)>();
        foreach (var (key, t) in tokens)
            if (!still && tokenWas.TryGetValue(key, out var was) && was.Loc.Length > 0 && t.Loc.Length > 0 && was.Loc != t.Loc && was.Rect.Size.X > 0)
            { t.Tok.Modulate = new Color(1, 1, 1, 0); moved.Add((t.Tok, t.Portrait, was.Loc, t.Loc, was.Rect)); }   // hidden until the copy lands
        // positions are final after the containers have laid out
        GetTree().CreateTimer(0.05).Timeout += () =>
        {
            if (!IsInstanceValid(this) || model is null) return;
            var next = new Dictionary<string, (string, Rect2)>();
            foreach (var (key, t) in tokens)
            {
                if (!IsInstanceValid(t.Tok) || !t.Tok.IsVisibleInTree()) continue;
                next[key] = (t.Loc, t.Tok.GetGlobalTransform() * new Rect2(Vector2.Zero, t.Tok.Size));
            }
            foreach (var (tok, portrait, fromLoc, toLoc, from) in moved)
            {
                if (!IsInstanceValid(tok)) continue;
                var to = tok.GetGlobalTransform() * new Rect2(Vector2.Zero, tok.Size);
                if (!tok.IsVisibleInTree() || to.Size.X <= 0) { tok.Modulate = Colors.White; continue; }
                Slide(tok, portrait, from, to, MapPath(fromLoc, toLoc));
            }
            tokenWas = next;
        };
    }

    // The threads from one place to another, as screen points in travel order: the shortest chain of connections drawn on the
    // map (a move of more than one step follows each thread in turn). Empty when the places are not linked on the map.
    List<Vector2> MapPath(string fromLoc, string toLoc)
    {
        var res = new List<Vector2>();
        if (fromLoc == toLoc) return res;
        var adj = new Dictionary<string, List<(string To, LinkDraw L, bool Fwd)>>();
        foreach (var k in links)
        {
            if (k.IdA.Length == 0 || k.IdB.Length == 0) continue;
            if (!adj.TryGetValue(k.IdA, out var la)) adj[k.IdA] = la = new(); la.Add((k.IdB, k, true));
            if (!adj.TryGetValue(k.IdB, out var lb)) adj[k.IdB] = lb = new(); lb.Add((k.IdA, k, false));
        }
        var prev = new Dictionary<string, (string From, LinkDraw L, bool Fwd)>();
        var q = new Queue<string>(); q.Enqueue(fromLoc); var seen = new HashSet<string> { fromLoc };
        while (q.Count > 0 && !seen.Contains(toLoc))
        {
            var cur = q.Dequeue();
            if (!adj.TryGetValue(cur, out var nb)) continue;
            foreach (var (to, l, fwd) in nb) if (seen.Add(to)) { prev[to] = (cur, l, fwd); q.Enqueue(to); }
        }
        if (!seen.Contains(toLoc)) return res;
        var hops = new List<(LinkDraw L, bool Fwd)>();
        for (var at = toLoc; at != fromLoc; at = prev[at].From) hops.Add((prev[at].L, prev[at].Fwd));
        hops.Reverse();
        foreach (var (l, fwd) in hops)
        {
            var line = l.Lines.LastOrDefault(x => IsInstanceValid(x));
            if (line is null || line.Points.Length < 2) return new List<Vector2>();
            var xf = line.GetGlobalTransform();
            var pts = line.Points.Select(p => xf * p).ToList();
            if (!fwd) pts.Reverse();
            // leave out the ends that lie under the cards themselves: the card goes round the place, not through its middle
            int cut = Math.Max(1, pts.Count / 6);
            res.AddRange(pts.Skip(cut).Take(Math.Max(1, pts.Count - 2 * cut)));
        }
        return res;
    }

    void Slide(CardView tok, bool portrait, Rect2 from, Rect2 to, List<Vector2> via)
    {
        if (App.I.QaRunning) { GD.Print("QA_MOVE_FLY " + tok.Code + " " + from.Position + " -> " + to.Position + " via " + via.Count); QaMidShot(0.3); }
        CardView ghost;
        if (portrait) { ghost = CardView.PortraitCard(tok.Code, from.Size.Y); ghost.ShowName = false; }
        else ghost = new CardView { Code = tok.Code, CustomMinimumSize = from.Size, Size = from.Size, ShowCost = false, ShowName = false };
        ghost.HoverZoom = false; ghost.MouseFilter = MouseFilterEnum.Ignore; ghost.Rim = tok.Rim; ghost.ZIndex = 95;
        AddChild(ghost); ghost.Size = from.Size; ghost.PivotOffset = from.Size / 2;
        float k = to.Size.Y / Math.Max(1f, from.Size.Y);
        // the route of the card's centre: off its old spot, along the threads, onto its new spot
        var route = new List<Vector2> { from.GetCenter() }; route.AddRange(via); route.Add(to.GetCenter());
        var cum = new List<float> { 0 };
        for (int i = 1; i < route.Count; i++) cum.Add(cum[^1] + route[i - 1].DistanceTo(route[i]));
        float total = Math.Max(1f, cum[^1]);
        Vector2 At(float t)
        {
            float d = t * total; int i = 1;
            while (i < cum.Count - 1 && cum[i] < d) i++;
            float seg = Math.Max(0.001f, cum[i] - cum[i - 1]);
            return route[i - 1].Lerp(route[i], Math.Clamp((d - cum[i - 1]) / seg, 0, 1));
        }
        ghost.GlobalPosition = from.GetCenter() - from.Size / 2;
        float T = Math.Clamp(total / 700f, 0.45f, 1.3f);
        var tw = ghost.CreateTween();
        tw.TweenMethod(Callable.From<float>(t =>
        {
            if (!IsInstanceValid(ghost)) return;
            ghost.GlobalPosition = At(t) - from.Size / 2;
            float sc = 1 + (k - 1) * t; ghost.Scale = new Vector2(sc, sc);
        }), 0f, 1f, T).SetTrans(Tween.TransitionType.Sine).SetEase(Tween.EaseType.InOut);
        tw.TweenCallback(Callable.From(() => { ghost.QueueFree(); if (IsInstanceValid(tok)) tok.Modulate = Colors.White; }));
    }
}
