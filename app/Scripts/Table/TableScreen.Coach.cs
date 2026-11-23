using Godot;
using Gathering;

namespace ArkhamUI;

// First-two-rounds coach marks (REVAMP-SUMMARY §5 "交互提示"): short amber bubbles pinned to the real things the player
// should touch first — a hand card (drag it to the middle) and an exit (click to move). They disappear on the first
// action, after round two, or when the pointer is not needed any more. Plus the hover preview of the mansion icon.
public partial class TableScreen
{
    bool coachDone;
    readonly List<Control> coachBubbles = new();
    Control? mapPreview;

    void Coach()
    {
        var m = model!;
        int round = m.State.Round;
        if (coachDone || round > 2 || m.Phase != "InvestigationPhase" || m.QuestionTag != "PlayerWindowChooseOne" || m.Me is null)
        {
            ClearCoach(); return;
        }
        ClearCoach();
        // The hints sit together in the lower-right corner above 结束回合, not on the map: pinned to a card they covered the
        // places beside it (Yunke 11-19 r11). What they point at already glows.
        var lines = new List<string>();
        if (fan.Any(f => f.Card.Actionable && IsInstanceValid(f.Card))) lines.Add("拖动发光的手牌到桌面中央，松开即可打出");
        // only when the core really offers a move (a lit location card on the map)
        if (mapShown && anchors.Any(kv => kv.Key.StartsWith("loc:") && kv.Key != "loc:" + m.MyLocation?.Id && byAnchor.TryGetValue(kv.Key, out var moves) && moves.Any(c => c.Title.StartsWith("移动"))))
            lines.Add("点击发光的地点即可移动");
        if (lines.Count > 0) Bubble(lines);
    }

    void Bubble(List<string> lines)
    {
        var box = new PanelContainer { MouseFilter = MouseFilterEnum.Ignore, ZIndex = 30 };
        var sb = new StyleBoxFlat { BgColor = new Color(0.04f, 0.035f, 0.03f, 0.9f), BorderColor = new Color(Style.Amber, 0.85f), ContentMarginLeft = 14, ContentMarginRight = 14, ContentMarginTop = 8, ContentMarginBottom = 9 };
        sb.SetBorderWidthAll(1); sb.BorderWidthLeft = 3; sb.SetCornerRadiusAll(8); box.AddThemeStyleboxOverride("panel", sb);
        var col = new VBoxContainer { MouseFilter = MouseFilterEnum.Ignore }; col.AddThemeConstantOverride("separation", 4); box.AddChild(col);
        col.AddChild(Style.Text("提示", 15, Style.Amber, Style.Kai));
        foreach (var l in lines) col.AddChild(Style.Text("· " + l, 18, Style.HoverText, Style.Kai));
        AddChild(box); box.ResetSize();
        var vp = GetViewportRect().Size;
        box.Position = new Vector2(vp.X - SideMargin - box.Size.X, vp.Y - SideMargin - 58 - 14 - box.Size.Y);
        if (S.Settings.ReducedMotion != true) { box.Modulate = new Color(1, 1, 1, 0); box.CreateTween().TweenProperty(box, "modulate:a", 1f, 0.3f); }
        coachBubbles.Add(box);
    }

    void ClearCoach()
    {
        foreach (var b in coachBubbles) if (IsInstanceValid(b)) b.QueueFree();
        coachBubbles.Clear();
    }

    void DismissCoach()
    {
        coachDone = true; ClearCoach();
    }

    // Hover over the mansion icon: a small picture of the whole house, so the player knows what the icon opens.
    void ShowMapPreview(Control anchor)
    {
        HideMapPreview();
        if (scenario is null) return;
        var box = new PanelContainer { MouseFilter = MouseFilterEnum.Ignore, ZIndex = 120 };
        var sb = new StyleBoxFlat { BgColor = new Color(0.02f, 0.02f, 0.02f, 0.9f), BorderColor = new Color(Style.Brass, 0.35f), ContentMarginLeft = 6, ContentMarginRight = 6, ContentMarginTop = 6, ContentMarginBottom = 6 };
        sb.SetBorderWidthAll(1); sb.SetCornerRadiusAll(6); sb.ShadowColor = new Color(0, 0, 0, 0.6f); sb.ShadowSize = 12; box.AddThemeStyleboxOverride("panel", sb);
        var pic = Ui.Image(Ui.Bg(scenario.MapThumb), new Vector2(340, 191), true); box.AddChild(pic);
        AddChild(box); box.ResetSize();
        var r = anchor.GetGlobalRect();
        box.Position = new Vector2(r.Position.X, r.End.Y + 10);
        mapPreview = box;
    }

    void HideMapPreview()
    {
        if (mapPreview is not null && IsInstanceValid(mapPreview)) mapPreview.QueueFree();
        mapPreview = null;
    }
}
