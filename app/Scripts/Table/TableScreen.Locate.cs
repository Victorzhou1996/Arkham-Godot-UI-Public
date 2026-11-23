using Godot;
using Gathering;

namespace ArkhamUI;

// "Where do I click?" answered with light, not with a list (Yunke 11-18, after the Codex review: the waiting player finds the
// card by its glow; as little text to read as possible). Every element that can be acted on right now sends out a ripple —
// an amber ring that swells and fades — one after another. It plays:
//   · on a click on your own seat portrait (top right) while you are asked, or on a waiting seat (after the view switches),
//   · when the table switches to another seat on its own (that seat has to answer now),
//   · on the F key.
// Used sparingly (Yunke 11-18: "只有在比较重要的地方再用"): no idle trigger, and hand cards are left out — their breathing
// edge already says they can be played; the ripple is for what is easy to miss (an asset, an enemy, a place, the goal bar).
public partial class TableScreen
{
    bool pendingLocate;
    bool flashMap;            // ripple after the next render (the view just changed seat)
    double idleFor;                // seconds since the last mouse button or key
    string locateSig = "";         // the question the idle ripple already played for
    const double IdleLocate = 8.0;

    public override void _Process(double delta)
    {
        FollowWindowSize();   // the log sidebar keeps to the window while it is dragged (TableScreen.History)
        if (model is null || busy || App.I.HasModal) { idleFor = 0; return; }
        idleFor += delta;   // (kept for diagnostics; the idle ripple was taken out on Yunke's word)
    }

    // a mouse button or a key: the player is doing something, the idle ripple waits again
    void NoteActivity() => idleFor = 0;

    // After a render: the ripple asked for by a seat change, once the new table has its final positions.
    void LocateAfterRender()
    {
        if (!pendingLocate) return;
        pendingLocate = false;
        GetTree().CreateTimer(0.25).Timeout += () => { if (IsInstanceValid(this)) Locate(); };
    }

    // Ripple every on-screen element that has a choice now. Choices with nothing on the table are the buttons above the hand.
    void Locate()
    {
        var m = model; if (m is null) return;
        idleFor = 0; locateSig = m.State.Sequence + "/" + m.State.Player;
        var vp = GetViewportRect();
        var inv = GetGlobalTransform().AffineInverse();
        var clip = boardClip is not null && IsInstanceValid(boardClip) ? boardClip.GetGlobalTransform() * new Rect2(Vector2.Zero, boardClip.Size) : (Rect2?)null;
        var seen = new HashSet<ulong>(); int n = 0;
        foreach (var key in byAnchor.Keys.Where(k => !k.StartsWith("hand:")).OrderBy(k => k.StartsWith("loc:") ? 0 : 1))
        {
            if (!anchors.TryGetValue(key, out var node) || !IsInstanceValid(node) || !node.IsVisibleInTree() || ShownAlpha(node) < 0.3f || !seen.Add(node.GetInstanceId())) continue;
            // a place on a painted map: it flares along its own outline (the shader), no box drawn round its hotspot
            if (key.StartsWith("loc:") && scenario?.WholePicture == true) { flashMap = true; n++; continue; }
            var r = node.GetGlobalTransform() * (anchorFrames.TryGetValue(key, out var fr) ? fr : new Rect2(Vector2.Zero, node.Size));   // a pile: its top card, not the label under it
            // a place on the map: only the part inside the map frame
            if (key.StartsWith("loc:") && clip is Rect2 c) { if (!c.Intersects(r)) continue; r = c.Intersection(r); }
            if (!vp.Intersects(r) || r.Size.X < 4 || r.Size.Y < 4) continue;
            if (n == 0) GD.Print("LOCATE_FIRST " + key + " " + (inv * r));
            // the ripple rides on the element itself (Yunke 11-20 #9, #17): it follows a map that is zoomed or moved while it
            // plays, and stays in the element's own layer, under anything shown over it (the choice of lead investigator)
            Ripple(node, anchorFrames.TryGetValue(key, out var lf) ? lf : new Rect2(Vector2.Zero, node.Size), Math.Min(n, 10) * 0.07f, key.StartsWith("loc:") ? 18 : key == "encdeck" ? 18 : 10);
            n++;
        }
        if (flashMap && groundMat is not null)
        {
            flashMap = false;
            var mat = groundMat; var tw = CreateTween();
            for (int k = 0; k < 2; k++)
            {
                tw.TweenMethod(Callable.From<float>(v => mat.SetShaderParameter("flash", v)), 0f, 1f, 0.18f);
                tw.TweenMethod(Callable.From<float>(v => mat.SetShaderParameter("flash", v)), 1f, 0f, 0.5f);
            }
        }
        if (n == 0) GD.Print("LOCATE none");
        else GD.Print("LOCATE " + n + " turn=" + J.String(m.Game, "turnPlayerInvestigatorId") + " active=" + J.String(m.Game, "activeInvestigatorId") + " me=" + m.Me?.Id);
    }

    // how see-through an element is, all the way up (a box still waiting to fade in is not rippled yet)
    static float ShownAlpha(Control c)
    {
        float a = 1f; for (Node? n = c; n is CanvasItem ci; n = n.GetParent()) a *= ci.Modulate.A * ci.SelfModulate.A;
        return a;
    }

    // Two amber rings over the element: each one swells a little and fades.
    // host: the element; r: the frame to ring, in the host's own coordinates. The rings hang on a Node2D under the host (a
    // container does not lay out a Node2D), so they move and scale with it and sit just above it in its own layer.
    // ring: the rings' colour — amber by default (something to do here); red for a card that stops you (TableScreen.Blockers)
    void Ripple(Control host, Rect2 r, float delay, int radius, Color? ring = null)
    {
        var rc = ring ?? new Color("ffd58a"); var glow = ring is Color g ? new Color(g, 0.8f) : new Color(Style.Amber, 0.75f);
        bool still = S.Settings.ReducedMotion == true;
        var rig = new Node2D { ZIndex = 6 }; host.AddChild(rig);
        float sc = Math.Max(0.2f, host.GetGlobalTransform().Scale.X);   // keep the ring's width the same on screen
        for (int k = 0; k < (still ? 1 : 2); k++)
        {
            var p = new Panel { MouseFilter = MouseFilterEnum.Ignore, Position = r.Position - new Vector2(4, 4) / sc, Size = r.Size + new Vector2(8, 8) / sc };
            var sb = new StyleBoxFlat { DrawCenter = false, BgColor = new Color(0, 0, 0, 0), BorderColor = rc, ShadowColor = glow, ShadowSize = (int)Math.Round(16 / sc), AntiAliasing = true };
            sb.SetBorderWidthAll(Math.Max(1, (int)Math.Round(4 / sc))); sb.SetCornerRadiusAll((int)Math.Round(radius / sc)); p.AddThemeStyleboxOverride("panel", sb);
            rig.AddChild(p); p.PivotOffset = p.Size / 2; p.Modulate = new Color(1, 1, 1, 0);
            if (k == (still ? 0 : 1)) p.TreeExited += () => { if (IsInstanceValid(rig)) rig.QueueFree(); };
            var tw = p.CreateTween();
            tw.TweenInterval(delay + k * 0.5f);
            if (still)
            {
                tw.TweenProperty(p, "modulate:a", 1f, 0.05f); tw.TweenInterval(1.1); tw.TweenProperty(p, "modulate:a", 0f, 0.2f);
            }
            else
            {
                tw.TweenProperty(p, "modulate:a", 1f, 0.08f);
                // swell for a second; stay bright for the first part of it, then fade
                tw.TweenProperty(p, "scale", new Vector2(1f + 40f / Math.Max(60f, p.Size.X), 1f + 40f / Math.Max(60f, p.Size.Y)), 1.0f).SetTrans(Tween.TransitionType.Quad).SetEase(Tween.EaseType.Out);
                tw.Parallel().TweenProperty(p, "modulate:a", 0f, 0.6f).SetDelay(0.4f).SetTrans(Tween.TransitionType.Quad).SetEase(Tween.EaseType.In);
            }
            tw.TweenCallback(Callable.From(() => { if (IsInstanceValid(p)) p.QueueFree(); }));
        }
    }
}
