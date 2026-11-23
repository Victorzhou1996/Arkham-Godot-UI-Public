using Godot;

namespace ArkhamUI;

// Smooth scrolling for any ScrollContainer (Yunke 11-19 #6: "滚动时像在瞬移"). The wheel, a drag of the bar's handle, a
// click on the bar's track and the container's own jumps (keeping a focused control in view) all set a target; the
// position then eases to it every frame (exponential approach), so the content and the handle glide instead of jumping.
//   SmoothScroll.Attach(scroll);              // vertical, the default step
//   SmoothScroll.Attach(scroll, step: 160);   // a longer step per wheel notch
// Reduced motion (settings) keeps the plain jump. Scroll by code with ScrollTo (eased) or Jump (at once).
public partial class SmoothScroll : Node
{
    ScrollContainer scroll = null!;
    VScrollBar bar = null!;
    double target, shown;
    bool driving;
    public float Step = 130f;      // pixels per wheel notch
    public float Sharpness = 10f;  // larger = quicker settle (1/s)

    public static SmoothScroll Attach(ScrollContainer scroll, float step = 130f)
    {
        foreach (var child in scroll.GetChildren(true)) if (child is SmoothScroll existing) return existing;
        var s = new SmoothScroll { scroll = scroll, Step = step, Name = "SmoothScroll" };
        scroll.AddChild(s, false, InternalMode.Front);
        return s;
    }

    // the SmoothScroll a container has, if any
    public static SmoothScroll? Of(ScrollContainer scroll)
    {
        foreach (var child in scroll.GetChildren(true)) if (child is SmoothScroll s) return s;
        return null;
    }

    static bool Calm => App.I.S?.Settings.ReducedMotion == true;

    public override void _Ready()
    {
        bar = scroll.GetVScrollBar();
        target = shown = bar.Value;
        scroll.GuiInput += OnInput;
        bar.ValueChanged += OnBar;
    }

    double Max => Math.Max(0, bar.MaxValue - bar.Page);

    void OnInput(InputEvent e)
    {
        if (e is not InputEventMouseButton { Pressed: true } mb) return;
        if (mb.ButtonIndex is not (MouseButton.WheelUp or MouseButton.WheelDown)) return;
        if (Calm || Max <= 0) return;   // the container's own handling
        float notch = mb.Factor > 0 ? mb.Factor : 1f;
        target = Math.Clamp(target + (mb.ButtonIndex == MouseButton.WheelDown ? 1 : -1) * Step * notch, 0, Max);
        scroll.AcceptEvent();
        if (App.I.QaRunning) GD.Print($"SMOOTH wheel target={target:0} shown={shown:0} max={Max:0}");
        SetProcess(true);
    }

    // the bar moved by itself (a drag of the handle, a click on the track, the container following focus): take that as
    // the new target and put the bar back where it was shown, so the move is eased like the wheel's
    void OnBar(double value)
    {
        if (App.I.QaRunning && !driving) GD.Print($"SMOOTH bar {value:0} shown={shown:0}");
        if (driving || Calm) { if (!driving) target = shown = value; return; }
        target = Math.Clamp(value, 0, Max);
        driving = true; bar.Value = shown; driving = false;
        SetProcess(true);
    }

    public void ScrollTo(double y) { target = Math.Clamp(y, 0, Max); if (Calm) Jump(target); else SetProcess(true); }
    public void Jump(double y) { target = shown = Math.Clamp(y, 0, Max); driving = true; bar.Value = shown; driving = false; }
    public double Target => target;

    public override void _Process(double delta)
    {
        if (!IsInstanceValid(scroll)) { SetProcess(false); return; }
        target = Math.Clamp(target, 0, Max);
        double k = 1 - Math.Exp(-Sharpness * Math.Min(delta, 1.0 / 30));
        shown += (target - shown) * k;
        if (Math.Abs(target - shown) < 0.5) { shown = target; SetProcess(false); if (App.I.QaRunning) GD.Print($"SMOOTH settled {shown:0}"); }
        driving = true; bar.Value = shown; driving = false;
    }
}
