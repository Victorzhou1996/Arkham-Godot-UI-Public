using Godot;

namespace ArkhamUI;

// The connections between places (Yunke 11-19: "地点的连线有点看不清楚还有锯齿，改成更精细的线，并且颜色清晰，可以有轻微光效").
// Each line is a Line2D whose texture shapes it across its width — a solid middle with soft edges, or a soft glow — so the edges
// are smooth at any zoom without relying on Line2D's own antialiasing; its width is kept in screen pixels (the map is scaled,
// the line is not), so it stays a fine, even thread however far the map is zoomed.
public partial class TableScreen
{
    static ImageTexture? lineCoreTex, lineGlowTex;

    // across the width (V): 1 in the middle, fading to 0 at both edges; glow: a gaussian
    static ImageTexture LineProfile(bool glow)
    {
        const int H = 64; var img = Godot.Image.CreateEmpty(4, H, false, Godot.Image.Format.Rgba8);
        for (int y = 0; y < H; y++)
        {
            float d = Math.Abs((y + 0.5f) / H - 0.5f) * 2f;   // 0 in the middle, 1 at the edge
            float a = glow ? Mathf.Exp(-d * d * 3f) * (1f - Mathf.SmoothStep(0.8f, 1f, d)) : 1f - Mathf.SmoothStep(0.45f, 1f, d);
            for (int x = 0; x < 4; x++) img.SetPixel(x, y, new Color(1, 1, 1, a));
        }
        return ImageTexture.CreateFromImage(img);
    }

    // a line of the given on-screen width; the board sets its world width from the zoom (ScaleLinkWidths)
    Line2D LinkLine(float px, Color color, bool glow)
    {
        lineCoreTex ??= LineProfile(false); lineGlowTex ??= LineProfile(true);
        var l = new Line2D
        {
            Width = px / Math.Max(0.05f, boardZoom), DefaultColor = color, Antialiased = false,
            Texture = glow ? lineGlowTex : lineCoreTex, TextureMode = Line2D.LineTextureMode.Stretch, TextureFilter = TextureFilterEnum.Linear,
            BeginCapMode = Line2D.LineCapMode.Round, EndCapMode = Line2D.LineCapMode.Round, JointMode = Line2D.LineJointMode.Round, RoundPrecision = 12,
        };
        l.SetMeta("px", px);
        return l;
    }

    // keep every connection line its on-screen width after a zoom
    void ScaleLinkWidths()
    {
        if (boardWorld is null) return;
        float z = Math.Max(0.05f, boardZoom);
        void Walk(Node n)
        {
            foreach (var c in n.GetChildren())
            {
                if (c is Line2D l && l.HasMeta("px")) l.Width = (float)l.GetMeta("px") / z;
                else if (c is Node2D) Walk(c);
            }
        }
        Walk(boardWorld);
    }

    // a one-way connection's arrow (map units); its edge is a closed stroke of the thread's bed round it (TableScreen.Board)
    static readonly Vector2[] ArrowShape = { new(15, 0), new(-10, -12), new(-5, 0), new(-10, 12) };

    // The colours of a connection: a clear parchment gold (dimmer while one end is still unrevealed), brighter where a move
    // can be made now.
    static readonly Color LinkGold = new("f8e6bc"), LinkLive = new("fff0c8"), LinkGlow = new("ffe6a8");

    // shadow, faint glow, fine core — and for a move you can make now a wider breathing glow
    List<Line2D> LinkLines(Node parent, bool live, bool seen)
    {
        var lines = new List<Line2D>();
        // the glow underneath, then a thin dark edge, then the bright core: halo, outline, thread
        var glow = LinkLine(live ? 34f : 24f, new Color(LinkGlow, live ? 0.8f : seen ? 0.55f : 0.25f), true); parent.AddChild(glow); lines.Add(glow);
        var bed = LinkLine(6.5f, new Color(0.08f, 0.04f, 0.01f, 0.7f), false); parent.AddChild(bed); lines.Add(bed);
        if (live && S.Settings.ReducedMotion != true)
        {
            var tw = glow.CreateTween().SetLoops();
            tw.TweenProperty(glow, "modulate:a", 0.45f, 0.9f).SetTrans(Tween.TransitionType.Sine); tw.TweenProperty(glow, "modulate:a", 1f, 0.9f).SetTrans(Tween.TransitionType.Sine);
        }
        var core = LinkLine(live ? 3.6f : 2.8f, live ? LinkLive : new Color(LinkGold, seen ? 1f : 0.6f), false); parent.AddChild(core); lines.Add(core);
        return lines;
    }
}
