using Godot;

namespace ArkhamUI;

// 新手教程 before the table (Yunke 11-23: "教程是不是应该从选剧本和选人开始……整体是线性的，选剧本和选人都是固定的，不能选
// 班克斯和黛西之外的其他人，一步步引导玩家"). While On, the campaign list lets only 狂热之夜 through, the prepare screen fixes
// the difficulty and decks, and the group picture lets only Roland Banks, then Daisy Walker, be picked (CycleScreen,
// PrepareScreen.Guide, SelectionStage.Guide). One tip at a time, at the top of the screen, with a soft glow over what it
// talks about. At the table the tips go on (TableScreen.Tutorial).
public static class Guide
{
    public static bool On { get; private set; }
    static PanelContainer? tip; static Panel? glow; static Func<Rect2?>? target; static Tween? pulse; static TutorialRipple? ripple;

    // from the main menu: the decks first, then the campaign list
    public static async void Begin()
    {
        try { await Tutorial.EnsureDecks(); }
        catch (Exception e) { App.I.Toast("新手教程准备失败：" + e.Message, true, 5); return; }
        On = true;
        GD.Print("GUIDE begin");
        App.I.Go(new CycleScreen());
    }

    public static void End()
    {
        On = false; Clear();
        SelectionStage.Guide = null; SelectionStage.GuideDeck = null;
        GD.Print("GUIDE end");
    }

    // a tip; ok: the 知道了 button (none when the tip waits for the player to do what it says)
    public static void Show(string text, Func<Control?>? where = null, Action? ok = null) =>
        ShowAt(text, where is null ? null : () => where() is { } c && GodotObject.IsInstanceValid(c) && c.IsVisibleInTree() ? c.GetGlobalRect() : null, ok);

    // the same, pointing at a place on screen (a person in the group picture); the ring ripples out from it when the tip waits
    // for a click there (no 知道了)
    public static void ShowAt(string text, Func<Rect2?>? where, Action? ok = null)
    {
        Clear();
        GD.Print("GUIDE_TIP " + (text.Length > 24 ? text[..24] : text));
        var box = new PanelContainer { ZIndex = 260, MouseFilter = Control.MouseFilterEnum.Stop };
        var sb = new StyleBoxFlat { BgColor = new Color(0.035f, 0.03f, 0.025f, 0.95f), BorderColor = new Color(Style.Amber, 0.8f), ContentMarginLeft = 22, ContentMarginRight = 22, ContentMarginTop = 14, ContentMarginBottom = 14 };
        sb.SetBorderWidthAll(1); sb.BorderWidthLeft = 4; sb.SetCornerRadiusAll(10); box.AddThemeStyleboxOverride("panel", sb);
        var col = new VBoxContainer(); col.AddThemeConstantOverride("separation", 10); box.AddChild(col);
        col.AddChild(Style.Text("新手教程", 17, Style.Amber, Style.Kai));
        col.AddChild(TutorialText.Label(text, 21, 620));
        var row = new HBoxContainer { Alignment = BoxContainer.AlignmentMode.End }; row.AddThemeConstantOverride("separation", 12); col.AddChild(row);
        var quit = Ui.Button("退出教程", () => { End(); App.I.Toast("已退出新手教程，可以在主菜单的“新手教程”重新开始"); }, 18); quit.CustomMinimumSize = new Vector2(120, 44); row.AddChild(quit);
        if (ok is not null) { var b = Ui.Button("知道了", () => { Clear(); ok(); }, 20, true); b.CustomMinimumSize = new Vector2(130, 44); row.AddChild(b); }
        App.I.AddChild(box);
        box.AnchorLeft = box.AnchorRight = 0.5f; box.GrowHorizontal = Control.GrowDirection.Both; box.OffsetTop = 20;
        tip = box;
        target = where;
        if (where is not null)
        {
            var g = new Panel { MouseFilter = Control.MouseFilterEnum.Ignore, ZIndex = 250 };
            var gs = new StyleBoxFlat { BgColor = new Color(Style.Amber, 0.015f), ShadowColor = new Color(Style.Amber, 0.42f), ShadowSize = 34, DrawCenter = true };   // soft all round, no hard edge
            gs.SetCornerRadiusAll(14); g.AddThemeStyleboxOverride("panel", gs);
            App.I.AddChild(g); glow = g;
            if (App.I.S?.Settings.ReducedMotion != true) { pulse = g.CreateTween().SetLoops(); pulse.TweenProperty(g, "modulate:a", 0.45f, 0.9f).SetTrans(Tween.TransitionType.Sine); pulse.TweenProperty(g, "modulate:a", 1f, 0.9f).SetTrans(Tween.TransitionType.Sine); }
            App.I.GetTree().ProcessFrame += Follow; following = true;
            Follow();
            if (ok is null) ripple = TutorialRipple.Start(App.I, where, 251);
        }
    }

    // the glow keeps to its target (screens animate in, scroll, rebuild)
    static void Follow()
    {
        if (glow is null || !GodotObject.IsInstanceValid(glow)) { if (following) { App.I.GetTree().ProcessFrame -= Follow; following = false; } return; }
        if (target?.Invoke() is not { } r) { glow.Visible = false; return; }
        var pad = new Vector2(10, 10);
        glow.Visible = true; glow.GlobalPosition = r.Position - pad; glow.Size = r.Size + pad * 2;
    }

    static bool following;
    public static void Clear()
    {
        if (following) { App.I.GetTree().ProcessFrame -= Follow; following = false; }
        if (tip is not null && GodotObject.IsInstanceValid(tip)) tip.QueueFree();
        if (glow is not null && GodotObject.IsInstanceValid(glow)) glow.QueueFree();
        if (ripple is not null && GodotObject.IsInstanceValid(ripple)) ripple.QueueFree();
        tip = null; glow = null; target = null; pulse = null; ripple = null;
    }
}
