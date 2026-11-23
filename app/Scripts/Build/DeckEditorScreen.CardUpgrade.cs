using Godot;
using Gathering;
using System.Text.Json;

namespace ArkhamUI;

// Clicking a card in the deck list (Yunke 11-19 #23): when the card has higher-level versions this deck may take, a
// window shows them side by side (there may be several: Lightning Gun (5), .45 Automatic (2) …) and asks which one to
// upgrade to, one copy or all of them; otherwise the click takes one copy out as before. The versions are build's
// (selectAvailableUpgrades: same card, more xp, allowed by the deck — decks.upgrades), the swap is build's upgradeCard,
// and the experience it costs is build's own count (the 经验 pill; in a campaign upgrade, getChangeStats).
public partial class DeckEditorScreen
{
    // a left click on a deck row takes one copy out, whatever the card (Yunke 11-21: an upgradable card used to open the
    // upgrade window on the same click — two meanings for one click); upgrading is the row's 升级 mark or the card window
    async Task TileClicked(string code)
    {
        if (busy || popover is not null && IsInstanceValid(popover)) return;
        await Remove(code);
    }

    // the 升级 mark on the rows of cards that have higher-level versions this deck may take (build's decks.upgrades)
    int upgradeMarks;
    async Task MarkUpgrades()
    {
        if (tab is not ("slots" or "extraSlots")) return;
        int gen = ++upgradeMarks; string part = tab;
        JsonElement ups;
        try { ups = await BuildEngine.I.CallJson("decks.upgrades", deckId, part); } catch (Exception e) { GD.PushWarning("upgrades: " + e.Message); return; }
        if (gen != upgradeMarks || !IsInstanceValid(this) || ups.ValueKind != JsonValueKind.Object) return;
        int n = 0;
        foreach (var p in ups.EnumerateObject())
        {
            if (p.Value.ValueKind != JsonValueKind.Array || p.Value.GetArrayLength() == 0 || !tiles.TryGetValue(p.Name, out var t) || !IsInstanceValid(t)) continue;
            var row = t.GetChildren().OfType<HBoxContainer>().FirstOrDefault(); if (row is null) continue;
            string code = p.Name; var list = p.Value.EnumerateArray().Select(x => x.Clone()).ToList();
            var b = new Button { Text = "升级", FocusMode = FocusModeEnum.None, MouseDefaultCursorShape = CursorShape.PointingHand, SizeFlagsVertical = SizeFlags.ShrinkCenter, TooltipText = "换成高级版（" + string.Join("、", list.Select(v => Xp(v) + " 级").Distinct()) + "）" };
            b.AddThemeFontSizeOverride("font_size", 16); if (Style.Kai is not null) b.AddThemeFontOverride("font", Style.Kai);
            StyleBoxFlat Box(float a, Color line) { var s = new StyleBoxFlat { BgColor = new Color(0.25f, 0.17f, 0.05f, a), BorderColor = line, AntiAliasing = true, ContentMarginLeft = 9, ContentMarginRight = 9, ContentMarginTop = 1, ContentMarginBottom = 2 }; s.SetBorderWidthAll(1); s.SetCornerRadiusAll(11); return s; }
            b.AddThemeStyleboxOverride("normal", Box(0.75f, new Color(Style.Amber, 0.7f))); b.AddThemeStyleboxOverride("hover", Box(0.95f, Style.Amber)); b.AddThemeStyleboxOverride("pressed", Box(1f, Style.Amber)); b.AddThemeStyleboxOverride("focus", new StyleBoxEmpty());
            b.AddThemeColorOverride("font_color", new Color("ffe2a6")); b.AddThemeColorOverride("font_hover_color", Colors.White);
            b.Pressed += () => { Sfx.Click(); HidePreview(); if (!busy) UpgradeChoice(code, list); };
            // hover: the higher versions as big cards (Yunke 11-22: "悬停在升级按钮上时，显示升级后的卡牌（大卡）"); back on the
            // row, its own card again
            var tile = t;
            b.MouseEntered += () => { if (dragCode is null) ShowUpgradePreview(list, b); };
            b.MouseExited += () => { HidePreview(); if (dragCode is null && IsInstanceValid(tile) && tile.GetGlobalRect().HasPoint(tile.GetGlobalMousePosition())) ShowPreview(code, tile); };
            upgradeButtons[code] = b;
            row.AddChild(b); row.MoveChild(b, row.GetChildCount() - 2);   // before the count
            n++;
        }
        if (App.I.QaRunning) GD.Print("QA_UPGRADE_MARKS " + n);
    }

    readonly Dictionary<string, Button> upgradeButtons = new();
    Control? upPreview;

    // the higher versions side by side left of the deck panel, at the row's hover card size
    void ShowUpgradePreview(List<JsonElement> versions, Control from)
    {
        HidePreview(); if (Covered) return;
        var codes = versions.Select(v => J.String(v, "code")).Where(c => c.Length > 0).Distinct().Take(4).ToList();
        if (codes.Count == 0) return;
        float right = deckPanel.GetGlobalRect().Position.X - 18, vpH = GetViewportRect().Size.Y; const float gap = 18;
        // the size of the row's own hover card (ShowPreview: 286 × 400), so the two look alike (Yunke 11-22: "升级预览的卡牌太大了 要保持一致")
        float w = MathF.Floor(Math.Min(PreviewSize.X, (right - 40 - gap * (codes.Count - 1)) / codes.Count));
        var size = new Vector2(w, MathF.Round(w / CardAspect));
        var box = new Control { MouseFilter = MouseFilterEnum.Ignore, ZIndex = 150 }; AddChild(box);
        for (int i = 0; i < codes.Count; i++)
        {
            var cv = new CardView { Code = codes[i], CustomMinimumSize = size, Size = size, HoverZoom = false, Redrawn = false, MouseFilter = MouseFilterEnum.Ignore, Override = SharpCard(codes[i], size) };
            box.AddChild(cv); RoundCorners(cv); Crisp(cv); cv.Position = new Vector2(i * (w + gap), 0);
        }
        float total = codes.Count * w + gap * (codes.Count - 1);
        var r = from.GetGlobalRect();
        box.GlobalPosition = new Vector2(right - total, Math.Clamp(r.GetCenter().Y - size.Y / 2, 20, vpH - size.Y - 20));
        if (!Calm) { box.Modulate = new Color(1, 1, 1, 0); box.CreateTween().TweenProperty(box, "modulate:a", 1f, 0.12f); }
        upPreview = box;
        if (App.I.QaRunning) GD.Print("QA_UPGRADE_HOVER " + string.Join(",", codes) + " size=" + size);
    }
    void HideUpgradePreview() { if (upPreview is not null && IsInstanceValid(upPreview)) upPreview.QueueFree(); upPreview = null; }

    void UpgradeChoice(string code, List<JsonElement> versions)
    {
        var card = J.Items(J.Get(view, tab)).FirstOrDefault(c => J.String(c, "code") == code);
        string name = J.String(card, "name", CardName(code)); int have = InDeck(code, tab), level = Xp(card);
        var col = new VBoxContainer(); col.AddThemeConstantOverride("separation", 12);
        // one header row: title and what the deck holds on the left, 只移出一张 / 取消 on the right — so the cards get the
        // height (Yunke 11-20 #29: the versions as big as the big card view; r13 campaign check: they were ~645 high, it ~800)
        var head = new HBoxContainer(); head.AddThemeConstantOverride("separation", 16); col.AddChild(head);
        var titles = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill }; titles.AddThemeConstantOverride("separation", 2); head.AddChild(titles);
        titles.AddChild(Style.Title("升级 " + name, 34));
        titles.AddChild(Style.Text("牌组里有 " + have + " 张（" + (level > 0 ? level + " 级" : "0 级") + "）。选择要换成的高级版：", 19, Style.PaperDim, Style.Kai));
        var remove = Ui.Button("只移出一张", () => { ClosePopover(); _ = Remove(code); }, 19); remove.SizeFlagsVertical = SizeFlags.ShrinkCenter; head.AddChild(remove);
        var cancel = Ui.Button("取消", ClosePopover, 19); cancel.SizeFlagsVertical = SizeFlags.ShrinkCenter; head.AddChild(cancel);
        // centred, the cards as big as the big card view when the screen allows: what is not card here is the panel's padding
        // (52), the header (~76 + 12) and below each card its name and the upgrade buttons (~92), plus a margin
        var vp = GetViewportRect().Size; int n = Math.Min(5, versions.Count) + 1;
        float cw = MathF.Floor(Math.Min(Math.Min(BigSize().X, (vp.Y - 256) * CardAspect), (vp.X * 0.94f - 68 - 72 - 28 * n) / n));
        var row = new HBoxContainer { Alignment = BoxContainer.AlignmentMode.Center, SizeFlagsHorizontal = SizeFlags.ShrinkCenter }; row.AddThemeConstantOverride("separation", 28);
        // the current card on the left, an arrow, then each higher version
        var current = VersionCard(code, name, level, null, cw); row.AddChild(current);
        // the experience rule sits under the current card, where the versions have their buttons
        string note = upgrade is not null ? "所需经验按 arkham.build 的规则计算（同名升级只算等级差），确认后右上角会显示本次花费和剩余经验。"
                                          : "牌组需要的经验会按 arkham.build 的规则重新计算，显示在右上角。";
        var nl = Style.Text(note, 15, Style.PaperDim, Style.Kai); nl.AutowrapMode = TextServer.AutowrapMode.WordSmart; nl.HorizontalAlignment = HorizontalAlignment.Center; nl.CustomMinimumSize = new Vector2(cw, 0); current.AddChild(nl);
        row.AddChild(new Label { Text = "›", VerticalAlignment = VerticalAlignment.Center, SizeFlagsVertical = SizeFlags.ShrinkCenter });
        ((Label)row.GetChild(row.GetChildCount() - 1)).AddThemeFontSizeOverride("font_size", 64);
        ((Label)row.GetChild(row.GetChildCount() - 1)).AddThemeColorOverride("font_color", Style.Amber);
        foreach (var v in versions.OrderBy(Xp).Take(5))
        {
            string up = J.String(v, "code"); int lv = Xp(v);
            var box = VersionCard(up, J.String(v, "name"), lv, J.String(v, "subname"), cw);
            // the two buttons side by side (they wrap when the cards are narrow)
            var buttons = new HFlowContainer(); buttons.AddThemeConstantOverride("h_separation", 8); buttons.AddThemeConstantOverride("v_separation", 6); box.AddChild(buttons);
            var b1 = Ui.Button("升级 1 张", () => _ = ApplyUpgrade(code, up, 1, J.String(v, "name"), lv), 19, true); b1.CustomMinimumSize = new Vector2(0, 46); b1.SizeFlagsHorizontal = SizeFlags.ExpandFill; buttons.AddChild(b1);
            if (have > 1) { var b2 = Ui.Button("全部升级（" + have + " 张）", () => _ = ApplyUpgrade(code, up, have, J.String(v, "name"), lv), 19); b2.CustomMinimumSize = new Vector2(0, 46); b2.SizeFlagsHorizontal = SizeFlags.ExpandFill; buttons.AddChild(b2); }
            row.AddChild(box);
        }
        col.AddChild(row);
        Dialog(col);
        if (App.I.QaRunning) GD.Print("QA_UPGRADE_CHOICE n=" + n + " card=" + cw + "x" + MathF.Round(cw / CardAspect) + " big=" + BigSize());
    }

    // one version: the printed card (rounded corners, right-click for the big view), its name and level
    Control VersionCard(string code, string name, int level, string? subname, float width)
    {
        var box = new VBoxContainer { CustomMinimumSize = new Vector2(width, 0) }; box.AddThemeConstantOverride("separation", 8);
        var size = new Vector2(width, MathF.Round(width / CardAspect));
        var cv = BigCard(code, size);   // the same rounded class frame as the big card view
        box.AddChild(cv); RoundCorners(cv);
        var l = Style.Text(name + (subname is { Length: > 0 } ? "（" + subname + "）" : "") + "  " + (level > 0 ? new string('•', Math.Min(5, level)) + " " + level + " 级" : "0 级"), 19, Style.HoverText, Style.Kai);
        l.HorizontalAlignment = HorizontalAlignment.Center; l.AutowrapMode = TextServer.AutowrapMode.WordSmart; l.CustomMinimumSize = new Vector2(width, 0); box.AddChild(l);
        return box;
    }

    async Task ApplyUpgrade(string code, string up, int copies, string upName, int level)
    {
        ClosePopover();
        await Do(() => BuildEngine.I.Call("decks.upgradeCard", deckId, code, up, copies, tab), true);
        App.I.Toast("已升级为 " + upName + "（" + level + " 级）" + (copies > 1 ? " × " + copies : ""));
        if (tiles.TryGetValue(up, out var t) && IsInstanceValid(t) && !Calm) { t.Modulate = new Color(2f, 1.8f, 1.4f); t.CreateTween().TweenProperty(t, "modulate", Colors.White, 0.6f); }
    }

    // a window in the middle of the screen over a dimmed editor (inside this screen, so it also shows over the table)
    void Dialog(Control content)
    {
        ClosePopover();
        // the deck row's hover preview (z 150) would stay over the window while the pointer has not moved (r13 campaign check)
        HidePreview();
        var dim = new ColorRect { Color = new Color(0, 0, 0, 0.6f), MouseFilter = MouseFilterEnum.Stop, ZIndex = 120 };
        AddChild(dim); dim.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        dim.GuiInput += e => { if (e is InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Left }) ClosePopover(); };
        var centre = new CenterContainer { MouseFilter = MouseFilterEnum.Ignore }; dim.AddChild(centre); centre.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        var panel = new PanelContainer(); panel.AddThemeStyleboxOverride("panel", Ui.SoftPanel(0.97f, 34, 26)); panel.AddChild(content); centre.AddChild(panel);
        if (!Calm) { dim.Modulate = new Color(1, 1, 1, 0); dim.CreateTween().TweenProperty(dim, "modulate:a", 1f, 0.16f); }
        popover = dim;
    }
}
