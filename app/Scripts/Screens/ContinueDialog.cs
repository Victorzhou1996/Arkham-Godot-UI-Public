using Godot;
using Gathering;

namespace ArkhamUI;

// 继续调查: every save, newest first, each a row with its investigators' portrait cards, where it stands, 继续 and 删除.
// Sized for four-player saves (Yunke 11-20: the old 1100-wide box would not hold four portraits plus the names), and a
// save can be deleted there (taken off the list only; the backend keeps its rows, see PrototypeBackend.DeleteGame).
public static class ContinueDialog
{
    const float PortraitH = 128, PortraitGap = 10, Seats = 4;

    public static void Open()
    {
        var S = App.I.S!;
        var col = new VBoxContainer { CustomMinimumSize = new Vector2(1560, 860) };
        col.AddThemeConstantOverride("separation", 18);
        // the title in 思源宋体, bold-ish and spaced (Yunke 11-20: "继续调查四个字换个字体")
        var head = Style.Text("继续调查", 40, Style.HoverText, Style.Serif); head.AddThemeConstantOverride("outline_size", 1); head.AddThemeColorOverride("font_outline_color", Style.HoverText);
        if (Style.Serif is not null) { var sp = new FontVariation { BaseFont = Style.Serif, SpacingGlyph = 6 }; head.AddThemeFontOverride("font", sp); }
        // two shelves (Yunke 11-22): saves still in play, and finished ones filed under 已完成的调查
        var top = new HBoxContainer(); top.AddThemeConstantOverride("separation", 28); col.AddChild(top);
        head.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill; top.AddChild(head);
        bool showDone = false;
        var tabs = new HBoxContainer { SizeFlagsVertical = Control.SizeFlags.ShrinkCenter }; tabs.AddThemeConstantOverride("separation", 8); top.AddChild(tabs);
        var list = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill }; list.AddThemeConstantOverride("separation", 12);
        var scroll = Ui.Scroll(list); scroll.SizeFlagsVertical = Control.SizeFlags.ExpandFill; col.AddChild(scroll);
        Control scrim = null!;
        SavedGameSummary[] games = Array.Empty<SavedGameSummary>();

        void Fill()
        {
            foreach (var c in list.GetChildren()) { list.RemoveChild(c); c.QueueFree(); }
            var all = S.Backend.SavedGames().Reverse().ToArray();
            games = all.Where(g => g.Over == showDone).ToArray();
            foreach (var c in tabs.GetChildren()) { tabs.RemoveChild(c); c.QueueFree(); }
            foreach (var (label, done) in new[] { ("进行中", false), ("已完成的调查", true) })
            {
                int n = all.Count(g => g.Over == done); bool on = done == showDone;
                var t = Ui.MinimalButton(label + "  " + n, () => { showDone = done; Fill(); }, 24);
                t.Modulate = on ? Colors.White : new Color(1, 1, 1, 0.55f);
                if (on) { var u = new StyleBoxFlat { BgColor = new Color(0, 0, 0, 0), BorderColor = Style.Amber, BorderWidthBottom = 2, ContentMarginLeft = 14, ContentMarginRight = 14, ContentMarginTop = 6, ContentMarginBottom = 6 }; t.AddThemeStyleboxOverride("normal", u); t.AddThemeStyleboxOverride("hover", u); }
                tabs.AddChild(t);
            }
            if (games.Length == 0) list.AddChild(Style.Text(showDone ? "还没有完成的调查。" : "还没有进行中的调查。", 24, Style.PaperDim));
            foreach (var g in games) list.AddChild(Row(g));
        }

        Control Row(SavedGameSummary g)
        {
            var box = new PanelContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
            var sb = new StyleBoxFlat { BgColor = new Color(0, 0, 0, 0.4f), ContentMarginLeft = 20, ContentMarginRight = 20, ContentMarginTop = 12, ContentMarginBottom = 12 };
            sb.SetCornerRadiusAll(8); box.AddThemeStyleboxOverride("panel", sb);
            var row = new HBoxContainer(); row.AddThemeConstantOverride("separation", 22); box.AddChild(row);
            // a fixed strip wide enough for four portraits, so the text column lines up whatever the seat count
            var faces = new HBoxContainer { CustomMinimumSize = new Vector2(Seats * MathF.Round(PortraitH * 0.645f) + (Seats - 1) * PortraitGap, PortraitH) };
            faces.AddThemeConstantOverride("separation", (int)PortraitGap);
            foreach (var code in g.InvestigatorCodes)
                // the portrait small card, whole (Yunke 11-18: no round or square crops of the figure)
                if (code.Length > 0) { var pc = CardView.PortraitCard(code, PortraitH); pc.ShowName = false; faces.AddChild(pc); }
            row.AddChild(faces);
            var info = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, SizeFlagsVertical = Control.SizeFlags.ShrinkCenter };
            info.AddThemeConstantOverride("separation", 6);
            info.AddChild(Style.Text(Heading(g), 30, Style.Paper, Style.Kai));
            info.AddChild(Style.Text(StopPoint(g), 22, Style.PaperDim));
            var names = Style.Text(string.Join("、", g.InvestigatorCodes.Where(c => c.Length > 0).Select(S.Name)), 22, Style.PaperDim);
            names.AutowrapMode = TextServer.AutowrapMode.WordSmart; info.AddChild(names);
            info.AddChild(Style.Text("第 " + g.Sequence + " 步", 16, new Color(Style.PaperDim, 0.6f)));   // only to tell two saves at the same spot apart
            row.AddChild(info);
            var id = Guid.Parse(g.Id);
            // the save out: to a .json file, or to a phone over EOS (SaveTransfer)
            var export = Ui.Button("导出", () => SaveTransfer.Export(g, Heading(g)), 20); export.CustomMinimumSize = new Vector2(96, 56); export.SizeFlagsVertical = Control.SizeFlags.ShrinkCenter; row.AddChild(export);
            var send = Ui.Button("发送到" + SaveTransfer.Other, () => { App.I.CloseModal(scrim); SaveTransfer.Send(g, Heading(g)); }, 20); send.CustomMinimumSize = new Vector2(140, 56); send.SizeFlagsVertical = Control.SizeFlags.ShrinkCenter; Online.Gate(send); row.AddChild(send);
            var del = LineIcon.Button("delete", "删除存档", () => ConfirmDelete(g), 52);
            del.SizeFlagsVertical = Control.SizeFlags.ShrinkCenter; row.AddChild(del);
            // the save over EOS (Yunke 11-22): the others join its room and take their seats; disabled until EOS is connected
            if (!g.Over)
            {
                var online = Ui.Button("联机继续", () => { if (g.InvestigatorCodes.Length < 2) { App.I.Toast("这局只有一名调查员，不能联机", true); return; } App.I.CloseModal(scrim); OnlineDialogs.Host(Heading(g), id, g.InvestigatorCodes.Length, g.InvestigatorCodes); }, 22);
                online.CustomMinimumSize = new Vector2(150, 66); online.SizeFlagsVertical = Control.SizeFlags.ShrinkCenter; Online.Gate(online); row.AddChild(online);
            }
            var go = Ui.Button(g.Over ? "查看" : "继续", () => { App.I.CloseModal(scrim); App.I.Go(new TableScreen(id)); }, 26, true);
            go.CustomMinimumSize = new Vector2(200, 66); go.SizeFlagsVertical = Control.SizeFlags.ShrinkCenter; row.AddChild(go);
            return box;
        }

        void ConfirmDelete(SavedGameSummary g)
        {
            var c = new VBoxContainer { CustomMinimumSize = new Vector2(680, 0) }; c.AddThemeConstantOverride("separation", 20);
            var l = Style.Text("删除存档“" + Heading(g) + "”？\n存档数据和这局的本地文件都会一起删除，不能恢复。", 24, Style.Paper, Style.Kai);
            l.AutowrapMode = TextServer.AutowrapMode.WordSmart; c.AddChild(l);
            var r = new HBoxContainer { Alignment = BoxContainer.AlignmentMode.End }; r.AddThemeConstantOverride("separation", 10); c.AddChild(r);
            Control ask = null!;
            r.AddChild(Ui.Button("取消", () => App.I.CloseModal(ask), 22));
            r.AddChild(Ui.Button("删除", () => { App.I.CloseModal(ask); Delete(g); }, 22, true));
            ask = App.I.Modal(Ui.Panel(c, 34));
        }

        async void Delete(SavedGameSummary g)
        {
            App.I.Toast("正在删除存档…", false, 1.5);
            try { await S.Backend.DeleteGameAsync(g.Id); GD.Print("QA_SAVE_DELETED " + g.Id); Fill(); App.I.Toast("已删除存档"); }
            catch (Exception e) { GD.PushWarning("delete save: " + e.Message); App.I.Toast(e.Message, true, 4); }
        }

        Fill();
        // saves in from outside (Yunke 11-23): a .json file, or one sent from a computer over EOS (SaveTransfer)
        var importB = Ui.Button("导入存档", () => SaveTransfer.Import(() => Fill()), 22); importB.SizeFlagsVertical = Control.SizeFlags.ShrinkCenter; top.AddChild(importB);
        var receiveB = Ui.Button("从" + SaveTransfer.Other + "接收", () => SaveTransfer.Receive(() => Fill()), 22); receiveB.SizeFlagsVertical = Control.SizeFlags.ShrinkCenter; Online.Gate(receiveB); top.AddChild(receiveB);
        var bottom = new HBoxContainer { Alignment = BoxContainer.AlignmentMode.End }; col.AddChild(bottom);
        bottom.AddChild(Ui.Button("关闭", () => App.I.CloseModal(scrim), 24));
        scrim = App.I.Modal(Ui.Panel(col, 40));
        QaScript.Register("continue", a => { if (games.Length > 0) { App.I.CloseModal(scrim); App.I.Go(new TableScreen(Guid.Parse(games[int.TryParse(a, out int n) ? n : 0].Id))); } });
        // QA: open the delete confirmation for row n ("save-delete-ask"), or delete row n outright ("save-delete")
        QaScript.Register("save-delete-ask", a => { int n = int.TryParse(a, out int k) ? k : 0; if (n < games.Length) ConfirmDelete(games[n]); });
        QaScript.Register("continue-done", a => { showDone = a != "off"; Fill(); });
        // QA: export row n to a file (path under the project root), import a file, without the file windows
        QaScript.Register("save-export", async a => { var p = a.Split(','); int n = int.Parse(p[0]); if (n < games.Length) await SaveTransfer.ExportTo(games[n], System.IO.Path.Combine(App.I.Root, p[1])); });
        QaScript.Register("save-import", async a => { await SaveTransfer.ImportFrom(System.IO.Path.Combine(App.I.Root, a), () => Fill()); });
        QaScript.Register("save-send", a => { int n = int.TryParse(a, out int k) ? k : 0; if (n < games.Length) { App.I.CloseModal(scrim); SaveTransfer.Send(games[n], Heading(games[n])); } });
        QaScript.Register("save-delete", a => { int n = int.TryParse(a, out int k) ? k : 0; if (n < games.Length) Delete(games[n]); });
    }

    // Where the save is now, not where it began (a campaign keeps the title it was created with):
    // "狂热之夜 · 第二章 · 午夜假面", or the scenario's name alone when it is played on its own.
    internal static string Heading(SavedGameSummary g)
    {
        if (Guid.TryParse(g.Id, out var tid) && Tutorial.Is(tid)) return "新手教程 · " + HeadingOf(g);
        return HeadingOf(g);
    }
    static string HeadingOf(SavedGameSummary g)
    {
        string seats = g.InvestigatorCodes.Length + "人";
        if (g.CampaignId.Length == 0)
            return Campaigns.ForScenario(g.ScenarioCode).Find(g.ScenarioCode) is { } solo ? solo.Name + " · " + seats : g.Title;
        var camp = Campaigns.ById(g.CampaignId);
        if (g.ScenarioCode.Length > 0 && camp.Find(g.ScenarioCode) is not null) return camp.Name + " · " + camp.Label(g.ScenarioCode) + " · " + seats;
        return camp.Name + " · " + (g.Step == "EpilogueStep" && camp.EpilogueName.Length > 0 ? camp.EpilogueName : camp.PrologueName) + " · " + seats;
    }

    // The point play stopped at: the phase and round inside a scenario, or the campaign step between scenarios.
    static string StopPoint(SavedGameSummary g)
    {
        if (g.Over) return g.CampaignId.Length > 0 ? "战役结束" : "调查结束";
        if (g.Phase is "" or "CampaignPhase")
            return g.Step switch
            {
                "PrologueStep" or "EpilogueStep" => "阅读剧情",
                "ScenarioStep" => "准备开始",
                "InterludeStep" => "幕间故事",
                "UpgradeDeckStep" => "幕间 · 升级牌组",
                _ => "幕间",
            };
        string phase = g.Phase switch { "MythosPhase" => "神话阶段", "InvestigationPhase" => "调查阶段", "EnemyPhase" => "敌人阶段", "UpkeepPhase" => "整备阶段", _ => g.Phase };
        return g.Round > 0 ? phase + " · 第 " + g.Round + " 轮" : phase;
    }
}
