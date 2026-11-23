using Godot;

namespace ArkhamUI;

// 新手教程's "click here" ring (Yunke 11-23: "按钮也要有光圈涟漪动效，其他的教程部分也是如此，要用各种方法提示点击哪里"):
// every 1.5 s a soft amber ring leaves the target's outline and widens as it fades. Only in the tutorial, only on what the
// tip asks the player to click; none with reduced motion (the breathing glow stays).
public partial class TutorialRipple : Node
{
    Func<Rect2?> rect = null!; Control host = null!; int z; double wait;

    public static TutorialRipple? Start(Control host, Func<Rect2?> rect, int z)
    {
        if (App.I.S?.Settings.ReducedMotion == true) return null;
        var r = new TutorialRipple { rect = rect, host = host, z = z, Name = "TutorialRipple" };
        host.AddChild(r);
        return r;
    }

    public override void _Process(double delta)
    {
        wait -= delta;
        if (wait > 0) return;
        wait = 1.5;
        if (!GodotObject.IsInstanceValid(host) || rect() is not { } r || r.Size.X < 4) return;
        var ring = new Panel { MouseFilter = Control.MouseFilterEnum.Ignore, ZIndex = z };
        var sb = new StyleBoxFlat { DrawCenter = false, BorderColor = new Color(Style.Amber, 0.95f), ShadowColor = new Color(Style.Amber, 0.35f), ShadowSize = 10 };
        sb.SetBorderWidthAll(3); sb.SetCornerRadiusAll(16); ring.AddThemeStyleboxOverride("panel", sb);
        host.AddChild(ring);
        var pad = new Vector2(8, 8);
        ring.GlobalPosition = r.Position - pad; ring.Size = r.Size + pad * 2; ring.PivotOffset = ring.Size / 2;
        var tw = ring.CreateTween().SetParallel();
        tw.TweenProperty(ring, "scale", new Vector2(1.18f, 1.18f), 1.1f).SetTrans(Tween.TransitionType.Sine).SetEase(Tween.EaseType.Out);
        tw.TweenProperty(ring, "modulate:a", 0f, 1.1f).From(0.95f);
        tw.Chain().TweenCallback(Callable.From(ring.QueueFree));
    }
}

// The tutorial's words, coloured (Yunke 11-23: "新手介绍的文字里，要用颜色标注出调查员和卡牌等，方便阅读"): investigators in
// amber, 「card and place names」 in blue (the brackets dropped), “buttons and things on screen” in green; line breaks kept.
public static class TutorialText
{
    static readonly string[] Investigators = { "罗兰·班克斯", "黛西·沃克", "罗兰", "黛西" };
    public static string Bb(string text)
    {
        string inv = Style.Amber.ToHtml(false), card = "9fd0ff", ui = "a6e3a1";
        var s = text.Replace("[", "［").Replace("]", "］").Replace("｜", "\n");   // ｜ in a tip's text: a new line
        s = System.Text.RegularExpressions.Regex.Replace(s, "「([^」]+)」", m => "[color=#" + card + "]" + m.Groups[1].Value + "[/color]");
        s = System.Text.RegularExpressions.Regex.Replace(s, "“([^”]+)”|\"([^\"]+)\"", m => "[color=#" + ui + "]“" + (m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value) + "”[/color]");
        // the longest names first; a name inside a colour tag already set is left alone
        s = System.Text.RegularExpressions.Regex.Replace(s, "(?<!=#[0-9a-f]{6}\\][^\\[]*)(罗兰·班克斯|黛西·沃克|罗兰|黛西)", m => "[color=#" + inv + "]" + m.Value + "[/color]");
        return s;
    }

    public static RichTextLabel Label(string text, int size, float width)
    {
        var l = new RichTextLabel { BbcodeEnabled = true, FitContent = true, ScrollActive = false, AutowrapMode = TextServer.AutowrapMode.WordSmart, MouseFilter = Control.MouseFilterEnum.Ignore, CustomMinimumSize = new Vector2(width, 0) };
        l.AddThemeFontSizeOverride("normal_font_size", size);
        l.AddThemeColorOverride("default_color", Style.Paper);
        l.AddThemeConstantOverride("line_separation", 6);
        l.Text = Bb(text);
        return l;
    }
}
