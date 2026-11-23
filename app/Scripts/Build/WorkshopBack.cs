using Godot;

namespace ArkhamUI;

// The way back on the workshop's screens (Yunke 11-21: "把所有界面的返回上一级都搞到左上，只要一个图标按钮就行 … 返回上一级
// 的默认快捷键是esc"): a round outlined icon button in the top-left corner, and Esc does the same. Esc first closes what is
// open over the screen (App's windows close themselves; `first` closes the screen's own popovers and says it did).
public static class WorkshopBack
{
    public const float Size = 54, Margin = 28;

    // the button alone (for a screen that puts it in its own top row)
    public static Button Icon(string tip, Action back)
    {
        var b = new Button { TooltipText = tip, FocusMode = Control.FocusModeEnum.None, CustomMinimumSize = new Vector2(Size, Size), MouseDefaultCursorShape = Control.CursorShape.PointingHand };
        StyleBoxFlat Ring(float bg, Color line) { var s = new StyleBoxFlat { BgColor = new Color(0.04f, 0.035f, 0.03f, bg), BorderColor = line, AntiAliasing = true, ShadowColor = new Color(0, 0, 0, 0.45f), ShadowSize = 8 }; s.SetBorderWidthAll(2); s.SetCornerRadiusAll((int)(Size / 2)); return s; }
        b.AddThemeStyleboxOverride("normal", Ring(0.5f, new Color(Style.Brass, 0.75f)));
        b.AddThemeStyleboxOverride("hover", Ring(0.7f, Style.Amber));
        b.AddThemeStyleboxOverride("pressed", Ring(0.82f, Style.Amber));
        b.AddThemeStyleboxOverride("focus", new StyleBoxEmpty());
        var ic = new LineIcon { Kind = "chev-l", Ink = new Color(0.93f, 0.88f, 0.78f), Stroke = 2.6f, MouseFilter = Control.MouseFilterEnum.Ignore };
        b.AddChild(ic); ic.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        ic.OffsetLeft = ic.OffsetTop = Size * 0.24f; ic.OffsetRight = ic.OffsetBottom = -Size * 0.24f;
        b.Pressed += () => { Sfx.Click(); back(); };
        return b;
    }

    // the button in the screen's top-left corner, and Esc
    public static Button Attach(Control screen, string tip, Action back, Func<bool>? first = null)
    {
        var b = Icon(tip, back); screen.AddChild(b);
        b.Position = new Vector2(Margin, Margin); b.Size = new Vector2(Size, Size); b.ZIndex = 20;
        Esc(screen, back, first);
        return b;
    }

    public static void Esc(Control screen, Action back, Func<bool>? first = null)
    {
        screen.AddChild(new EscBack { Back = back, First = first });
        // QA: an Esc press through the viewport, as the keyboard sends it
        QaScript.Register("key-esc", _ => { var vp = screen.GetViewport(); vp.PushInput(new InputEventKey { Keycode = Key.Escape, PhysicalKeycode = Key.Escape, Pressed = true }); vp.PushInput(new InputEventKey { Keycode = Key.Escape, PhysicalKeycode = Key.Escape, Pressed = false }); });
    }
}

// The short fades of the workshop's switches inside a screen (Yunke 11-22: page switches need a fade in and out; between
// screens App.Go already dips to black). None with 减少动效 on.
public static class WorkshopFade
{
    static bool Calm => App.I.S?.Settings.ReducedMotion == true;
    public static void In(CanvasItem c, float time = 0.18f)
    {
        if (Calm || !GodotObject.IsInstanceValid(c)) return;
        c.Modulate = new Color(c.Modulate, 0);
        c.CreateTween().TweenProperty(c, "modulate:a", 1f, time).SetEase(Tween.EaseType.Out);
        if (App.I.QaRunning) { GD.Print("QA_FADE in " + c.Name + " " + time); c.GetTree().CreateTimer(0.06).Timeout += () => { if (GodotObject.IsInstanceValid(c)) GD.Print("QA_FADE mid " + c.Name + " a=" + c.Modulate.A.ToString("0.00")); }; }
    }
    public static async Task Out(CanvasItem c, float time = 0.12f)
    {
        if (Calm || !GodotObject.IsInstanceValid(c)) return;
        var tw = c.CreateTween(); tw.TweenProperty(c, "modulate:a", 0f, time).SetEase(Tween.EaseType.In);
        if (App.I.QaRunning) GD.Print("QA_FADE out " + c.Name + " " + time);
        await c.ToSignal(tw, Tween.SignalName.Finished);
    }
}

// Esc → back, when nothing takes the key before it (a text box being typed in, an App window: App closes those itself)
public partial class EscBack : Node
{
    public Action? Back;
    public Func<bool>? First;
    public override void _UnhandledKeyInput(InputEvent e)
    {
        if (e is not InputEventKey { Pressed: true, Echo: false, Keycode: Key.Escape }) return;
        if (App.I.HasModal) return;
        GetViewport().SetInputAsHandled();
        if (First?.Invoke() == true) return;
        Back?.Invoke();
    }
}
