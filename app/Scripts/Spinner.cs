using Godot;

namespace ArkhamUI;

// A small loading ring (Yunke 11-21: while a screen loads behind black, something turns): a faint full circle and a
// brighter arc going round it.
public partial class Spinner : Control
{
    public Color Ink = new(0.91f, 0.76f, 0.48f);
    public float Radius = 22f, Width = 3f;
    float angle;

    public override void _Ready() { MouseFilter = MouseFilterEnum.Ignore; }

    public override void _Process(double delta) { angle += (float)delta * 5.2f; QueueRedraw(); }

    public override void _Draw()
    {
        var c = Size / 2;
        DrawArc(c, Radius, 0, Mathf.Tau, 64, new Color(Ink, 0.18f), Width, true);
        // the arc breathes a little in length as it turns
        float len = 1.6f + 0.6f * Mathf.Sin(angle * 0.7f);
        DrawArc(c, Radius, angle, angle + len, 40, Ink, Width, true);
    }
}
