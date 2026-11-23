using Godot;

namespace ArkhamUI;

// Which campaign or scenario to play (11-22, "后续循环接入": every campaign, return-to, side story and fan-made scenario the
// rules core starts, app/assets/data/campaigns-catalog.json). One scrolling page in three groups — campaigns, side and
// challenge scenarios, fan-made — each a grid of cards: the html version's cover melting into the dark, the official name,
// the chapters, the box's investigators, and small tags (BETA, 可切换重返, 需要某调查员, 核心暂不支持). A return-to version
// has no card of its own: it is a switch on its original's prepare screen. Minimal style, no frames.
public partial class CycleScreen : Control
{
    readonly List<(CampaignDef Camp, Control Panel, TextureRect? Pic)> panels = new();
    ScrollContainer scroll = null!;
    VBoxContainer page = null!;

    public override void _Ready()
    {
        MouseFilter = MouseFilterEnum.Stop;
        var bg = new ColorRect { Color = Style.Ink, MouseFilter = MouseFilterEnum.Ignore }; AddChild(bg); bg.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        scroll = new ScrollContainer { HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled, MouseFilter = MouseFilterEnum.Pass };
        AddChild(scroll); scroll.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect); scroll.OffsetTop = 150; scroll.OffsetLeft = 80; scroll.OffsetRight = -64; scroll.OffsetBottom = -20;
        SmoothScroll.Attach(scroll);
        page = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill, MouseFilter = MouseFilterEnum.Ignore }; page.AddThemeConstantOverride("separation", 18);
        scroll.AddChild(page);
        float width = Math.Max(800, GetViewportRect().Size.X - 160);
        Section("战役", "基础游戏与各循环。战役中各章依次进行，之间可以用经验升级牌组；有重返版的战役，进去后可以在“原版 / 重返”之间切换。",
            Campaigns.Official, width, 4, 360, Big);
        Section("旁支剧本与挑战剧本", "单独一个剧本，打完即结束。挑战剧本需要指定的调查员参加。",
            Campaigns.Side, width, 5, 290, Small);
        Section("玩家自制", "html 版收录的玩家自制战役与剧本（BETA）：规则由规则核心执行，部分卡图与文字可能不全。",
            Campaigns.Fan, width, 5, 290, Small);
        page.AddChild(Ui.Spacer(0, 40));
        // the header stays over the scrolling page, melting into it
        var head = new Control { MouseFilter = MouseFilterEnum.Ignore }; AddChild(head); head.AnchorRight = 1; head.OffsetBottom = 170;
        var headBg = new ColorRect { Color = Style.Ink, MouseFilter = MouseFilterEnum.Ignore }; head.AddChild(headBg); headBg.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect); headBg.OffsetBottom = -40;
        var headFade = new Control { MouseFilter = MouseFilterEnum.Ignore }; head.AddChild(headFade); headFade.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect); headFade.OffsetTop = 130;
        Ui.EdgeFade(headFade, Style.Ink, 0, 40, 0, 0);
        var title = Style.Title("选择战役", 48); AddChild(title); title.Position = new Vector2(104, 40);
        var hint = Style.Text(Campaigns.Official.Count() + " 个战役 · " + Campaigns.Side.Count() + " 个旁支与挑战剧本 · " + Campaigns.Fan.Count() + " 个玩家自制战役与剧本", 21, Style.PaperDim); AddChild(hint); hint.Position = new Vector2(106, 104);
        Ui.BackButton(this, "返回主菜单", () => { var m = new MainMenu(); App.I.Go(m); m.Ready(); });
        QaScript.Register("cycle", id => Open(Campaigns.ById(id.Length > 0 ? id : "01")));
        Callable.From(GuideHere).CallDeferred();
        QaScript.Register("cycle-scroll", y => SmoothScroll.Of(scroll)?.Jump(float.Parse(y, System.Globalization.CultureInfo.InvariantCulture)));
        QaScript.Register("cycle-hover", id => { var p = panels.FirstOrDefault(x => x.Camp.Id == id); if (p.Panel is not null) { scroll.EnsureControlVisible(p.Panel); Light(p, true); } });
    }

    // a group: its heading, a line on what it is, and its cards in rows
    void Section(string name, string note, IEnumerable<CampaignDef> defs, float width, int columns, float height, Func<CampaignDef, float, float, Control> card)
    {
        var list = defs.ToList(); if (list.Count == 0) return;
        var headRow = new HBoxContainer { MouseFilter = MouseFilterEnum.Ignore }; headRow.AddThemeConstantOverride("separation", 18); page.AddChild(headRow);
        headRow.AddChild(Style.Title(name, 34));
        var count = Style.Text(list.Count + " 项", 20, Style.Brass, Style.Kai); count.SizeFlagsVertical = SizeFlags.ShrinkCenter; headRow.AddChild(count);
        var noteLabel = Style.Text(note, 19, Style.PaperDim, Style.Kai); noteLabel.AutowrapMode = TextServer.AutowrapMode.WordSmart; noteLabel.CustomMinimumSize = new Vector2(width - 40, 0); page.AddChild(noteLabel);
        const float gap = 22;
        float w = (width - gap * (columns - 1)) / columns;
        var grid = new GridContainer { Columns = columns, MouseFilter = MouseFilterEnum.Ignore };
        grid.AddThemeConstantOverride("h_separation", (int)gap); grid.AddThemeConstantOverride("v_separation", (int)gap); page.AddChild(grid);
        foreach (var d in list) grid.AddChild(card(d, w, height));
        page.AddChild(Ui.Spacer(0, 18));
    }

    // a campaign: the cover over the upper part, the name, the chapters, the box's investigators
    Control Big(CampaignDef camp, float w, float h)
    {
        var (panel, pic, col) = Shell(camp, w, h, 0.58f);
        col.OffsetLeft = 26; col.OffsetRight = -22; col.OffsetTop = -h * 0.5f; col.OffsetBottom = -18;
        col.AddChild(Style.Text(camp.Cycle, 18, Style.Brass, Style.Kai));
        col.AddChild(NameRow(camp, 34));
        var chapters = Style.Text(camp.Chapters.Length + " 章 · " + string.Join("、", camp.Chapters.Select(c => c.Name)), 16, Style.PaperDim, Style.Kai);
        chapters.AutowrapMode = TextServer.AutowrapMode.WordSmart; chapters.MaxLinesVisible = 2; chapters.CustomMinimumSize = new Vector2(0, 44); chapters.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis; col.AddChild(chapters);
        var foot = new HBoxContainer { MouseFilter = MouseFilterEnum.Ignore }; foot.AddThemeConstantOverride("separation", 6); col.AddChild(foot);
        foreach (var code in camp.Investigators.Take(6)) foot.AddChild(Ui.SquarePortrait(code, 36));
        foot.AddChild(Ui.Spacer(0, 0, true));
        if (camp.Return is not null) foot.AddChild(Tag("可切换重返", Style.Brass, "进入后可在“原版 / 重返”之间切换：重返" + camp.Name.Replace("重返", "")));
        if (IsDev(camp)) DevMark(panel);
        Finish(camp, panel, pic, col);
        return panel;
    }

    static bool IsFan(CampaignDef camp) => camp.Kind is "fanCampaign" or "fanScenario";
    const string Placeholder = "res://assets/ui/backs/encounter-0712.jpg";

    // Yunke 11-22 ("给3-10循环都加上dev标识"): the cycles this page labels 第三循环…第十循环 (campaign ids 04-11; the page
    // calls 01 基础游戏, 02 第一循环, 03 第二循环) are still in development — a "DEV" mark in the card's top-right corner, on a
    // soft black corner shade (no frame, no pill); the first two cycles have none
    static bool IsDev(CampaignDef camp) => camp.Kind == "campaign" && int.TryParse(camp.Id, out int n) && n >= 4 && n <= 11;

    static void DevMark(Control panel)
    {
        var g = new Gradient(); g.SetColor(0, new Color(0, 0, 0, 0.82f)); g.SetColor(1, new Color(0, 0, 0, 0));
        g.AddPoint(0.45f, new Color(0, 0, 0, 0.5f));
        var shade = new TextureRect
        {
            Texture = new GradientTexture2D { Gradient = g, Width = 128, Height = 128, Fill = GradientTexture2D.FillEnum.Radial, FillFrom = new Vector2(1, 0), FillTo = new Vector2(0, 0) },
            ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize, StretchMode = TextureRect.StretchModeEnum.Scale, MouseFilter = MouseFilterEnum.Ignore,
        };
        panel.AddChild(shade); shade.AnchorLeft = shade.AnchorRight = 1; shade.OffsetLeft = -230; shade.OffsetBottom = 130;
        var label = new Label { Text = "DEV", MouseFilter = MouseFilterEnum.Pass, TooltipText = "开发中：这个循环还在接入和测试，可能遇到问题", HorizontalAlignment = HorizontalAlignment.Right,
            LabelSettings = new LabelSettings { Font = Style.Sans, FontSize = 20, FontColor = new Color(Style.Amber, 0.95f), ShadowColor = new Color(0, 0, 0, 0.7f), ShadowSize = 6, ShadowOffset = Vector2.Zero } };
        panel.AddChild(label); label.AnchorLeft = label.AnchorRight = 1; label.OffsetLeft = -90; label.OffsetRight = -22; label.OffsetTop = 14;
        // a thin amber rule under the word, fading out to the left
        var rg = new Gradient(); rg.SetColor(0, new Color(Style.Amber, 0)); rg.SetColor(1, new Color(Style.Amber, 0.8f));
        var rule = new TextureRect { Texture = new GradientTexture2D { Gradient = rg, Width = 64, Height = 2 }, ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize, StretchMode = TextureRect.StretchModeEnum.Scale, MouseFilter = MouseFilterEnum.Ignore };
        panel.AddChild(rule); rule.AnchorLeft = rule.AnchorRight = 1; rule.OffsetLeft = -110; rule.OffsetRight = -22; rule.OffsetTop = 44; rule.OffsetBottom = 46;
    }

    // a side, challenge or fan-made scenario (or a fan-made campaign): smaller, the cover when the html version has one
    Control Small(CampaignDef camp, float w, float h)
    {
        // fan-made entries always keep a picture band (the html cover, or the shared placeholder until one arrives) so the grid lines up
        bool hasCover = IsFan(camp) || camp.Cover.Length > 0 && Ui.Tex("res://assets/art-cover/" + camp.Cover + ".webp") is not null;
        var (panel, pic, col) = Shell(camp, w, h, hasCover ? 0.62f : 0f);
        col.OffsetLeft = 18; col.OffsetRight = -16; col.OffsetTop = hasCover ? -h * 0.46f : -h + 18; col.OffsetBottom = -14;
        string kind = camp.Kind switch { "fanCampaign" => "玩家自制战役 · " + camp.Chapters.Length + " 章", "fanScenario" => "玩家自制剧本", _ => camp.Cycle };
        col.AddChild(Style.Text(kind, 16, Style.Brass, Style.Kai));
        col.AddChild(NameRow(camp, 26));
        if (camp.NeedsInvestigator.Length > 0) col.AddChild(Style.Text("需要" + App.I.S!.Name(camp.NeedsInvestigator) + "参加", 16, Style.PaperDim, Style.Kai));
        if (camp.Kind == "side")
        {
            string diffs = string.Join("/", camp.Difficulties.Select(d => d switch { "Easy" => "简单", "Hard" => "困难", "Expert" => "专家", _ => "标准" }));
            if (camp.Difficulties.Length < 4) col.AddChild(Style.Text("难度：" + diffs, 15, new Color(Style.PaperDim, 0.8f), Style.Kai));
        }
        if (camp.Kind == "fanCampaign" && camp.Chapters.Length > 0)
        {
            var ch = Style.Text(string.Join("、", camp.Chapters.Select(c => c.Name)), 15, Style.PaperDim, Style.Kai);
            ch.AutowrapMode = TextServer.AutowrapMode.WordSmart; ch.MaxLinesVisible = 2; ch.CustomMinimumSize = new Vector2(0, 40); ch.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis; col.AddChild(ch);
        }
        Finish(camp, panel, pic, col);
        return panel;
    }

    // the card: the cover band (if any) melting into the dark, and a text column anchored to the bottom
    (Control Panel, TextureRect? Pic, VBoxContainer Col) Shell(CampaignDef camp, float w, float h, float coverPart)
    {
        var panel = new Control { CustomMinimumSize = new Vector2(w, h), MouseFilter = MouseFilterEnum.Stop, MouseDefaultCursorShape = CursorShape.PointingHand, ClipContents = true };
        var dark = new Color(0.016f, 0.016f, 0.02f, 1f);
        var under = new ColorRect { Color = dark, MouseFilter = MouseFilterEnum.Ignore }; panel.AddChild(under); under.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        TextureRect? pic = null;
        var real = camp.Cover.Length > 0 ? Ui.Tex("res://assets/art-cover/" + camp.Cover + ".webp") : null;
        // no cover yet (a fan-made entry added later without one): the html version's encounter card back, dimmed — one shared placeholder
        var cover = real ?? (coverPart > 0 && IsFan(camp) ? Ui.Tex(Placeholder) : null);
        if (coverPart > 0 && cover is not null)
        {
            var rest = real is null ? new Color(0.34f, 0.33f, 0.34f) : new Color(0.62f, 0.62f, 0.64f);
            pic = new TextureRect { Texture = cover, ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize, StretchMode = TextureRect.StretchModeEnum.KeepAspectCovered, MouseFilter = MouseFilterEnum.Ignore, TextureFilter = TextureFilterEnum.LinearWithMipmapsAnisotropic, Modulate = rest };
            pic.SetMeta("rest", rest); pic.SetMeta("lit", real is null ? new Color(0.5f, 0.48f, 0.46f) : new Color(1, 0.98f, 0.95f));
            panel.AddChild(pic); pic.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect); pic.AnchorBottom = coverPart;
            var melt = new Control { MouseFilter = MouseFilterEnum.Ignore }; panel.AddChild(melt); melt.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
            melt.AnchorTop = coverPart * 0.45f; melt.AnchorBottom = coverPart + 0.005f; Ui.EdgeFade(melt, dark, 0, 0, 0, h * coverPart * 0.55f);
            var edge = new Control { MouseFilter = MouseFilterEnum.Ignore }; panel.AddChild(edge); edge.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect); edge.AnchorBottom = coverPart;
            Ui.EdgeFade(edge, dark, 40, 30, 40, 0);
        }
        else
        {
            // no cover (fan-made): a faint warm glow from the top so the card is not a flat block
            var glow = new Control { MouseFilter = MouseFilterEnum.Ignore }; panel.AddChild(glow); glow.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
            Ui.EdgeFade(glow, new Color(0.16f, 0.11f, 0.06f, 0.55f), 0, h * 0.8f, 0, 0);
        }
        var col = new VBoxContainer { MouseFilter = MouseFilterEnum.Ignore }; col.AddThemeConstantOverride("separation", 6);
        panel.AddChild(col); col.AnchorTop = col.AnchorBottom = 1; col.AnchorRight = 1; col.Alignment = BoxContainer.AlignmentMode.End;
        return (panel, pic, col);
    }

    // the official name with its small tags after it
    static Control NameRow(CampaignDef camp, int size)
    {
        var row = new HFlowContainer { MouseFilter = MouseFilterEnum.Ignore }; row.AddThemeConstantOverride("h_separation", 10); row.AddThemeConstantOverride("v_separation", 4);
        var name = Style.Title(camp.Name, size); name.AutowrapMode = TextServer.AutowrapMode.Off; row.AddChild(name);
        if (camp.Beta && !IsDev(camp)) row.AddChild(Tag("BETA", Style.Amber, "测试中：这部分内容还在完善，可能遇到问题"));
        if (!camp.Starts && camp.NeedsInvestigator.Length == 0) row.AddChild(Tag("核心暂不支持", Style.Danger, "规则核心暂时开不了这个" + (camp.IsScenario ? "剧本" : "战役")));
        else if (camp.UiGap.Length > 0) row.AddChild(Tag("界面未适配", Style.Danger, camp.UiGap));
        return row;
    }

    static Control Tag(string text, Color color, string tip)
    {
        var pill = new PanelContainer { MouseFilter = MouseFilterEnum.Pass, SizeFlagsVertical = SizeFlags.ShrinkCenter, TooltipText = tip };
        var sb = new StyleBoxFlat { BgColor = new Color(0.08f, 0.06f, 0.03f, 0.85f), BorderColor = new Color(color, 0.85f), AntiAliasing = true, ContentMarginLeft = 9, ContentMarginRight = 9, ContentMarginTop = 1, ContentMarginBottom = 2 };
        sb.SetBorderWidthAll(1); sb.SetCornerRadiusAll(7); pill.AddThemeStyleboxOverride("panel", sb);
        var label = Style.Text(text, 15, color, Style.Sans); label.MouseFilter = MouseFilterEnum.Ignore; pill.AddChild(label);
        return pill;
    }

    void Finish(CampaignDef camp, Control panel, TextureRect? pic, VBoxContainer col)
    {
        var line = new ColorRect { Color = new Color(Style.Amber, 0), CustomMinimumSize = new Vector2(0, 2), MouseFilter = MouseFilterEnum.Ignore }; col.AddChild(line);
        line.Name = "line";
        var entry = (camp, panel, pic);
        panel.MouseEntered += () => Light(entry, true);
        panel.MouseExited += () => Light(entry, false);
        panel.GuiInput += e => { if (e is InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Left }) Open(camp); };
        panels.Add(entry);
    }

    static void Light((CampaignDef Camp, Control Panel, TextureRect? Pic) p, bool on)
    {
        var tw = p.Panel.CreateTween().SetParallel();
        if (p.Pic is not null) tw.TweenProperty(p.Pic, "modulate", p.Pic.GetMeta(on ? "lit" : "rest").AsColor(), 0.25f);
        if (p.Panel.FindChild("line", true, false) is ColorRect line) tw.TweenProperty(line, "color", new Color(Style.Amber, on ? 0.9f : 0f), 0.25f);
    }

    static void Open(CampaignDef camp)
    {
        if (Guide.On && camp.Id != "01") { App.I.Toast("新手教程请先选择基础游戏「狂热之夜」"); return; }
        App.I.Go(new PrepareScreen(camp));
    }

    // 新手教程: everything but 狂热之夜 dimmed, and the tip pointing at it
    void GuideHere()
    {
        if (!Guide.On) return;
        Control? zealot = null;
        foreach (var p in panels) { if (p.Camp.Id == "01") zealot = p.Panel; else p.Panel.Modulate = new Color(1, 1, 1, 0.3f); }
        Guide.Show("第一步：选择战役。这里列着所有战役和剧本。新手教程从基础游戏的「狂热之夜」开始——点击它。", () => zealot);
    }
}
