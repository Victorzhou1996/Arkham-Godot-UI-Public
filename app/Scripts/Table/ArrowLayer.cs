using Godot;

namespace ArkhamUI;

// Guidance arrows. Two kinds:
// - the hover arrow: from a choice button to the table element it refers to (with a ring around the element);
// - links: from the card whose ability is resolving to each target it may pick (Yunke: a weapon's attack should point
//   at the enemies it can hit; Medical Texts at the investigators it can heal). Links stay while the choice is open.
public partial class ArrowLayer : Control
{
    Control? from, to;
    readonly List<(Control From, Control To)> links = new();
    public override void _Ready() { MouseFilter = MouseFilterEnum.Ignore; ZIndex = 40; }
    public void Point(Control? source, Control? target) { from = source; to = target; QueueRedraw(); }
    public void Link(Control source, Control target) { if (source != target) { links.Add((source, target)); QueueRedraw(); } }
    public void Clear() { from = to = null; links.Clear(); QueueRedraw(); }
    public override void _Process(double delta) { if (to is not null || links.Count > 0) QueueRedraw(); }

    static readonly Color Brass = new("e0b56a"), Tip = new("fff1d5");

    public override void _Draw()
    {
        foreach (var (f, t) in links)
        {
            if (!IsInstanceValid(f) || !IsInstanceValid(t) || !f.IsVisibleInTree() || !t.IsVisibleInTree()) continue;
            var fr = f.GetGlobalRect(); fr.Position -= GlobalPosition;
            var tr = t.GetGlobalRect(); tr.Position -= GlobalPosition;
            DrawLink(fr.GetCenter(), tr);
        }
        if (to is null || !IsInstanceValid(to) || !to.IsVisibleInTree()) return;
        var r = to.GetGlobalRect(); r.Position -= GlobalPosition;
        DrawRect(r.Grow(6), Brass, false, 4);
        DrawRect(r.Grow(12), new Color(0.88f, 0.71f, 0.42f, 0.35f), false, 3);
        if (from is null || !IsInstanceValid(from)) return;
        var fr2 = from.GetGlobalRect(); fr2.Position -= GlobalPosition;
        DrawLink(new Vector2(fr2.Position.X - 6, fr2.Position.Y + fr2.Size.Y / 2), r);
    }

    // A curved arrow from a point to the nearest edge of a rect, bowing upward so it reads over the table.
    void DrawLink(Vector2 a, Rect2 tr)
    {
        var c = tr.GetCenter();
        var b = new Vector2(Math.Clamp(a.X, tr.Position.X + 10, tr.End.X - 10), a.Y < tr.Position.Y ? tr.Position.Y - 4 : a.Y > tr.End.Y ? tr.End.Y + 4 : c.Y);
        if (b.Y == c.Y) b.X = a.X < tr.Position.X ? tr.Position.X - 4 : tr.End.X + 4;
        float lift = Math.Clamp(a.DistanceTo(b) * 0.35f, 40, 220);
        var mid = (a + b) / 2 + new Vector2(0, -lift);
        var pts = new List<Vector2>();
        for (int i = 0; i <= 32; i++) { float t = i / 32f, u = 1 - t; pts.Add(u * u * a + 2 * u * t * mid + t * t * b); }
        DrawPolyline(pts.ToArray(), new Color(0, 0, 0, 0.55f), 9, true);
        DrawPolyline(pts.ToArray(), Brass, 5, true);
        DrawCircle(a, 7, Brass);
        var dir = (pts[^1] - pts[^3]).Normalized(); var n = new Vector2(-dir.Y, dir.X);
        DrawColoredPolygon(new[] { b + dir * 6, b - dir * 22 + n * 13, b - dir * 22 - n * 13 }, Tip);
    }
}
