using Godot;
using Gathering;

namespace ArkhamUI;

// Yunke 11-22 (饥饿霉菌): a card with two abilities offered "发动能力 / 发动能力" in its menu, and its one trigger switch set
// only the first ability. Each ability now shows by its icon (→ action, lightning fast, reaction) and its first words; a card
// with several switchable abilities gets one switch that opens a small window with a switch per ability.
public partial class TableScreen
{
    // the ability's kind from its printed line: "action", "fast" ([free]/[fast]), "reaction", or "" (forced)
    static string LineKind(string line) =>
        line.StartsWith("[action]") ? "action" : line.StartsWith("[free]") || line.StartsWith("[fast]") ? "fast" : line.StartsWith("[reaction]") ? "reaction" : "";

    // An ability choice's words: the card's printed line; for an ability another card grants (ProxySource: 博物馆大门's door
    // test from the act), the choice's own description — the card it sits on has other lines at that index (Yunke 11-22:
    // the gate's door test showed as its "撤退" line).
    (string Title, string Line)? ChoiceAbility(ChoicePresentation c)
    {
        if (AbilityText.Of(c.Raw) is not { } ab) return null;
        string code = c.CardCode.Length > 0 ? CoreCatalog.Normalize(c.CardCode) : ab.Code;
        if (GrantedBy(c).Length > 0)
        {
            var h = System.Text.RegularExpressions.Regex.Replace((c.Hint ?? "").Trim(), @"^(行动|快速|反应|免费)\s*[:：]\s*", "");
            if (h.Length == 0) return null;
            return (code.Length > 0 ? S.Name(code) : "", (AbilityKind(c) is { } k ? "[" + k + "]" : "") + "：" + h);
        }
        return code.Length > 0 && AbilityText.Line(code, ab.Index) is { } line ? (S.Name(code), line) : null;
    }

    // the first words of an ability, in the card's own text: its icon, tags and a leading colon dropped
    static string AbilitySnippet(string line, int n = 14)
    {
        var t = System.Text.RegularExpressions.Regex.Replace(line, @"^(\[(action|free|fast|reaction)\])+", "");
        t = System.Text.RegularExpressions.Regex.Replace(t, @"<[^>]+>", "");
        t = System.Text.RegularExpressions.Regex.Replace(t, @"\[\[([^\]]+)\]\]", "$1");
        t = System.Text.RegularExpressions.Regex.Replace(t, @"\[[a-z_]+\]", "");
        t = t.TrimStart('：', ':', ' ', '　');
        return t.Length > n ? t[..n] + "…" : t;
    }

    // an ability's icon and first words, laid over a menu button (its own text cleared)
    void AbilityFace(Button btn, string line)
    {
        btn.Text = "";
        var row = new HBoxContainer { MouseFilter = MouseFilterEnum.Ignore, Alignment = BoxContainer.AlignmentMode.Begin }; row.AddThemeConstantOverride("separation", 10);
        string kind = LineKind(line);
        // the icon in a box of its own width (Yunke 11-22: the double / triple arrows ran into the words)
        if (kind.Length > 0) { var g = Ui.Glyph(kind, 24, Style.HoverText); g.VerticalAlignment = VerticalAlignment.Center; g.HorizontalAlignment = HorizontalAlignment.Center; g.MouseFilter = MouseFilterEnum.Ignore; g.CustomMinimumSize = new Vector2(GlyphWidth(kind, 24), 0); row.AddChild(g); }
        var t = Style.Text(AbilitySnippet(line), 20, Style.Paper, Style.Kai); t.VerticalAlignment = VerticalAlignment.Center; t.MouseFilter = MouseFilterEnum.Ignore; row.AddChild(t);
        btn.AddChild(row); row.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect); row.OffsetLeft = 22; row.OffsetRight = -12;
        btn.CustomMinimumSize = new Vector2(Math.Max(240, 22 + GlyphWidth(kind, 24) + 10 + AbilitySnippet(line).Length * 20 + 20), 50);
    }

    // QA: the faces of a card's printed abilities in a menu box, to look at them without a game state offering them all
    void QaAbilityFaces(string code)
    {
        ClosePopup();
        var root = new Control { MouseFilter = MouseFilterEnum.Stop }; AddChild(root); root.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect); root.ZIndex = 150;
        var col = new VBoxContainer(); col.AddThemeConstantOverride("separation", 6); col.AddChild(Style.Text(S.Name(code), 20, Style.HoverText, Style.Kai));
        for (int i = 1; AbilityText.Line(code, i) is { } line; i++) { var b = new Button(); Ui.MenuStyle(b, 18, 30, 6); AbilityFace(b, line); col.AddChild(b); }
        var box = Ui.Panel(col, 14, 0.98f); root.AddChild(box); box.ResetSize(); box.Position = new Vector2(260, 600); popup = root;
    }

    // the icon's drawn width (the glyph's advance can be narrower than its ink: arrows overhang)
    static float GlyphWidth(string kind, int size)
    {
        var l = Ui.Glyph(kind, size, Colors.White);
        var font = l.GetThemeFont("font"); float w = font?.GetStringSize(l.Text, HorizontalAlignment.Left, -1, size).X ?? size;
        l.Free();
        return MathF.Ceiling(Math.Max(w, size) * 1.25f) + 4;
    }

    // ---------- trigger modes ----------
    static (string Ch, Color Fill) ModeLook(string mode) => mode switch
    {
        TriggerModes.OwnerOnly => ("己", new Color(0.2f, 0.36f, 0.62f)),
        TriggerModes.Off => ("停", new Color(0.32f, 0.3f, 0.28f)),
        _ => ("常", new Color(0.72f, 0.5f, 0.16f)),   // 总是触发 (Yunke 11-20 #7: "常", not "触")
    };

    static void PaintModeSquare(Button sq, string ch, Color fill, float d)
    {
        sq.Text = ch;
        foreach (var (st, k) in new[] { ("normal", 0f), ("hover", 0.18f), ("pressed", 0.3f) })
        {
            var sb = new StyleBoxFlat { BgColor = fill.Lightened(k), BorderColor = new Color(1, 1, 1, 0.35f), AntiAliasing = true, ContentMarginLeft = 0, ContentMarginRight = 0, ContentMarginTop = 0, ContentMarginBottom = 0 };
            sb.SetBorderWidthAll(1); sb.SetCornerRadiusAll(5); sq.AddThemeStyleboxOverride(st, sb);
        }
        sq.AddThemeStyleboxOverride("focus", new StyleBoxEmpty());
        sq.AddThemeFontSizeOverride("font_size", (int)(d * 0.6f)); if (Style.Kai is not null) sq.AddThemeFontOverride("font", Style.Kai);
        sq.AddThemeColorOverride("font_color", Colors.White); sq.AddThemeColorOverride("font_hover_color", Colors.White);
    }

    // a card with several switchable abilities: one square (their common mode, or 多 when they differ); a click opens the window
    Control MultiModeSwitch(TableModel m, string inv, string card, List<int> indexes)
    {
        string code = CoreCatalog.Normalize(card);
        var modes = indexes.Select(i => TriggerModes.Mode(m, inv, card, i)).ToList();
        var (ch, fill) = modes.Distinct().Count() == 1 ? ModeLook(modes[0]) : ("多", new Color(0.46f, 0.36f, 0.3f));
        var sq = new Button { FocusMode = FocusModeEnum.None, MouseDefaultCursorShape = CursorShape.PointingHand, CustomMinimumSize = new Vector2(22, 22) };
        PaintModeSquare(sq, ch, fill, 22);
        sq.TooltipText = S.Name(code) + "\n" + string.Join("\n", indexes.Select((i, k) => (AbilityText.Line(code, i) is { } l ? AbilitySnippet(l, 10) : "能力 " + i) + "：" + TriggerModes.Name(modes[k])))
            + "\n点击分别设置每项能力的触发方式";
        sq.Pressed += () => { Sfx.Click(); ModeWindow(sq, m, inv, card, indexes); };
        return sq;
    }

    // the window: a row per ability (icon, first words, its switch); switches change here and are applied when it closes
    void ModeWindow(Control anchor, TableModel m, string inv, string card, List<int> indexes)
    {
        ClosePopup(); Peek.Hide();
        string code = CoreCatalog.Normalize(card);
        var start = indexes.ToDictionary(i => i, i => TriggerModes.Mode(m, inv, card, i));
        var now = new Dictionary<int, string>(start);
        async void Apply()
        {
            ClosePopup();
            foreach (var i in indexes) if (now[i] != start[i]) await SetTriggerMode(inv, card, i, now[i]);
        }
        var root = new Control { MouseFilter = MouseFilterEnum.Stop };
        AddChild(root); root.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect); root.ZIndex = 150;
        root.GuiInput += e => { if (e is InputEventMouseButton { Pressed: true }) Apply(); };
        var col = new VBoxContainer { MouseFilter = MouseFilterEnum.Ignore }; col.AddThemeConstantOverride("separation", 10);
        col.AddChild(Style.Text(S.Name(code) + " · 触发方式", 20, Style.HoverText, Style.Kai));
        foreach (var i in indexes)
        {
            var row = new HBoxContainer { MouseFilter = MouseFilterEnum.Ignore }; row.AddThemeConstantOverride("separation", 10); col.AddChild(row);
            string line = AbilityText.Line(code, i) ?? "";
            if (LineKind(line) is { Length: > 0 } kind) { var g = Ui.Glyph(kind, 24, Style.HoverText); g.VerticalAlignment = VerticalAlignment.Center; g.HorizontalAlignment = HorizontalAlignment.Center; g.MouseFilter = MouseFilterEnum.Ignore; g.CustomMinimumSize = new Vector2(GlyphWidth(kind, 24), 0); row.AddChild(g); }
            var t = Style.Text(line.Length > 0 ? AbilitySnippet(line, 14) : "能力 " + i, 19, Style.Paper, Style.Kai); t.VerticalAlignment = VerticalAlignment.Center; t.SizeFlagsHorizontal = SizeFlags.ExpandFill;
            t.MouseFilter = MouseFilterEnum.Pass; t.TooltipText = System.Text.RegularExpressions.Regex.Replace(line, @"<[^>]+>|\[[a-z_]+\]", ""); row.AddChild(t);
            var sq = new Button { FocusMode = FocusModeEnum.None, MouseDefaultCursorShape = CursorShape.PointingHand, CustomMinimumSize = new Vector2(32, 32), SizeFlagsVertical = SizeFlags.ShrinkCenter };
            var name = Style.Text(TriggerModes.Name(now[i]), 16, Style.PaperDim, Style.Kai); name.VerticalAlignment = VerticalAlignment.Center; name.CustomMinimumSize = new Vector2(104, 0);
            void Paint() { var (ch, fill) = ModeLook(now[i]); PaintModeSquare(sq, ch, fill, 32); name.Text = TriggerModes.Name(now[i]); }
            Paint();
            int idx = i;
            sq.Pressed += () => { Sfx.Click(); now[idx] = TriggerModes.Next(now[idx]); Paint(); };
            row.AddChild(sq); row.AddChild(name);
        }
        var hint = Style.Text("点击方块切换：常 总是触发 → 己 只在自己回合 → 停 停止触发", 15, new Color(Style.PaperDim, 0.85f), Style.Kai); col.AddChild(hint);
        var done = Ui.MinimalButton("完成", Apply, 20, true); done.SizeFlagsHorizontal = SizeFlags.ShrinkEnd; col.AddChild(done);
        var box = Ui.Panel(col, 16, 0.98f); root.AddChild(box); box.ResetSize();
        var r = anchor.GetGlobalRect(); var vp = GetViewportRect().Size; var s = box.Size;
        float x = r.End.X + 12, y = r.Position.Y - s.Y / 2;
        if (x + s.X > vp.X - 12) x = r.Position.X - s.X - 12;
        box.Position = new Vector2(Math.Clamp(x, 12, Math.Max(12, vp.X - s.X - 12)), Math.Clamp(y, 12, Math.Max(12, vp.Y - s.Y - 12)));
        popup = root;
        if (App.I.QaRunning) GD.Print("QA_MODE_WINDOW " + code + " " + string.Join(",", indexes));
        if (S.Settings.ReducedMotion != true) { box.Modulate = new Color(1, 1, 1, 0); box.CreateTween().TweenProperty(box, "modulate:a", 1f, 0.1f); }
    }
}
