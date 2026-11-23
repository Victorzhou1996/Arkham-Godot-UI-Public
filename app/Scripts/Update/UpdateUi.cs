using Godot;

namespace ArkhamUI;

// The update's screens (docs/HOT-UPDATE-DESIGN.md §10): the line picker on the first check, a small tag at the main
// menu's top right while there is something to say, the update window (notes, size, download, restart), and the
// settings row (version, line, check). Plain text and the usual buttons — no new effects.
public static class UpdateUi
{
    // after the menu is up: the line picker the first time, then the check (Yunke 11-23: "启动时检查")
    public static void Startup()
    {
        Updater.BootOk();
        // QA: drive the update without the windows (docs/HOT-UPDATE-DESIGN.md §11)
        QaScript.Register("update-line", a => { Updater.Line = UpdatePolicy.Lines.First(l => l.Id == a); });
        QaScript.Register("update-check", async _ => { await Updater.Check(); GD.Print("QA_UPDATE_CHECK " + Updater.Now + " " + Updater.Pending + " " + Updater.Error); });
        QaScript.Register("update-download", async _ => { await Updater.Download(); GD.Print("QA_UPDATE_DOWNLOAD " + Updater.Now + " " + Updater.DoneBytes + " " + Updater.Error); });
        QaScript.Register("update-open", _ => Open());
        QaScript.Register("update-restart", _ => Updater.RestartNow());
        QaScript.Register("update-state", _ => GD.Print("QA_UPDATE_STATE " + Updater.Now + " local=" + Updater.LocalRelease + " note=" + Updater.BootNote + " online=" + Online.Available + " blocked=" + Updater.OnlineBlocked));
        QaScript.Register("update-res", a => GD.Print("QA_UPDATE_RES " + a + " = " + (Godot.FileAccess.FileExists(a) ? Godot.FileAccess.GetFileAsString(a).Trim() : "(none)")));
        // QA: a patch pack holding one text file (res path, then its text), as the release tool's patch export would
        QaScript.Register("update-make-patch", a =>
        {
            var p = a.Split(',', 3);   // out file (under the project root), res path, text
            var src = System.IO.Path.Combine(App.I.Root, p[0] + ".src.txt"); System.IO.File.WriteAllText(src, p[2]);
            var pk = new PckPacker(); pk.PckStart(System.IO.Path.Combine(App.I.Root, p[0])); pk.AddFile(p[1], src); pk.Flush();
            GD.Print("QA_UPDATE_PATCH_MADE " + p[0]);
        });
        if (!Updater.Enabled) return;
        if (Updater.BootNote.Length > 0) App.I.Toast(Updater.BootNote, Updater.BootNote.Contains("失败") || Updater.BootNote.Contains("出错"), 5);
        if (Updater.Line is null) ChooseLine(() => _ = Updater.Check());
        else _ = Updater.Check();
    }

    public static void ChooseLine(Action? then = null)
    {
        var col = new VBoxContainer { CustomMinimumSize = new Vector2(620, 0) }; col.AddThemeConstantOverride("separation", 20);
        col.AddChild(Style.Title("选择更新线路", 34));
        var hint = Style.Text("游戏会从这里检查和下载更新。以后可以在设置里更改。", 21, Style.PaperDim);
        hint.AutowrapMode = TextServer.AutowrapMode.WordSmart; col.AddChild(hint);
        var row = new HBoxContainer { Alignment = BoxContainer.AlignmentMode.Center }; row.AddThemeConstantOverride("separation", 24); col.AddChild(row);
        Control scrim = null!;
        foreach (var (line, label) in new[] { (UpdatePolicy.Domestic, "国内（中国大陆推荐）"), (UpdatePolicy.Overseas, "海外") })
        {
            var l = line;
            var b = Ui.Button(label, () => { Updater.Line = l; App.I.CloseModal(scrim); then?.Invoke(); }, 24, l == UpdatePolicy.Domestic);
            b.CustomMinimumSize = new Vector2(260, 64); row.AddChild(b);
        }
        scrim = App.I.Modal(Ui.Panel(col, 40), dismissOnBackdrop: false);
    }

    static string MB(long bytes) => bytes < 1_000_000 ? Math.Max(1, bytes / 1000) + " KB" : (bytes / 1_000_000.0).ToString(bytes < 10_000_000 ? "0.0" : "0") + " MB";

    // the top-right tag on the main menu (left of the wallpaper button); hidden when there is nothing to say
    public static void Tag(Control menu)
    {
        if (!Updater.Enabled) return;
        var b = Ui.Button("", Open, 20);
        menu.AddChild(b); b.ZIndex = 5;
        b.AnchorLeft = b.AnchorRight = 1; b.GrowHorizontal = Control.GrowDirection.Begin; b.OffsetRight = -28 - 46 - 18; b.OffsetTop = 24; b.OffsetBottom = 24 + 46;
        void Paint()
        {
            if (!GodotObject.IsInstanceValid(b)) { Updater.Changed -= Paint; return; }
            string text = Updater.Now switch
            {
                Updater.State.Available => "有新版本 · 约 " + MB(Updater.Pending?.Bytes ?? 0),
                Updater.State.Downloading => "正在下载更新 " + Percent() + "%",
                Updater.State.Ready => "更新已准备好 · 重启后生效",
                Updater.State.TooOld => "版本太旧，请下载完整安装包",
                Updater.State.Failed => Updater.Error,
                _ => "",
            };
            b.Text = text; b.Visible = text.Length > 0;
            b.Modulate = Updater.Now == Updater.State.Failed ? new Color(1, 1, 1, 0.7f) : Colors.White;
        }
        Updater.Changed += Paint; b.TreeExiting += () => Updater.Changed -= Paint;
        Paint();
    }

    static int Percent() => Updater.Pending is { Bytes: > 0 } p ? (int)Math.Min(100, Updater.DoneBytes * 100 / p.Bytes) : 0;

    // the update window: what is new, how big, and the one next step
    public static void Open()
    {
        var col = new VBoxContainer { CustomMinimumSize = new Vector2(720, 0) }; col.AddThemeConstantOverride("separation", 16);
        var title = Style.Title("", 34); col.AddChild(title);
        var body = new VBoxContainer(); body.AddThemeConstantOverride("separation", 8); col.AddChild(body);
        var progress = new ProgressBar { CustomMinimumSize = new Vector2(0, 14), ShowPercentage = false, MaxValue = 100 }; col.AddChild(progress);
        var line = Style.Text("", 20, Style.PaperDim); col.AddChild(line);
        var buttons = new HBoxContainer { Alignment = BoxContainer.AlignmentMode.End }; buttons.AddThemeConstantOverride("separation", 14); col.AddChild(buttons);
        Control scrim = null!;
        Label Para(string t, Color c, int size = 21) { var l = Style.Text(t, size, c); l.AutowrapMode = TextServer.AutowrapMode.WordSmart; l.CustomMinimumSize = new Vector2(680, 0); body.AddChild(l); return l; }
        void Btn(string t, Action a, bool primary = false) { var b = Ui.Button(t, a, 22, primary); b.CustomMinimumSize = new Vector2(150, 54); buttons.AddChild(b); }
        void Paint()
        {
            if (!GodotObject.IsInstanceValid(col)) { Updater.Changed -= Paint; return; }
            Ui.Clear(body); Ui.Clear(buttons);
            var o = Updater.Pending; var now = Updater.Now;
            var lineName = Updater.Line?.Label ?? "";
            progress.Visible = now == Updater.State.Downloading; progress.Value = Percent();
            line.Text = ""; line.Visible = false;
            switch (now)
            {
                case Updater.State.Available or Updater.State.Downloading or Updater.State.Ready when o is not null:
                    title.Text = "新版本 " + o.Release;
                    if (o.NeedsApk)
                    {
                        // Android, the program changed: a whole new installer (Yunke 11-23)
                        Para("当前版本 " + (Updater.LocalRelease.Length > 0 ? Updater.LocalRelease : "未知") + "。这次更新改动了程序本身，需要下载新的安装包（约 " + MB(o.Bytes) + "）覆盖安装，存档会保留。", Style.Paper);
                        foreach (var n in o.Notes) Para("· " + n, Style.Paper, 20);
                        Para("点“下载安装包”后会用浏览器下载，下载完成后点开安装。第一次安装时，系统会询问是否允许安装未知应用，请选择允许。", Style.PaperDim, 19);
                        Para("不更新也可以单机游玩；联机需要先更新。", Style.PaperDim, 19);
                        if (Updater.Error.Length > 0) Para(Updater.Error, Style.Danger, 20);
                        Btn("以后再说", () => App.I.CloseModal(scrim)); Btn("下载安装包", () => _ = Updater.Download(), true);
                        break;
                    }
                    Para("当前版本 " + (Updater.LocalRelease.Length > 0 ? Updater.LocalRelease : "未知") + "，需要下载约 " + MB(o.Bytes) + "（" + o.Files + " 个文件）。", Style.Paper);
                    foreach (var n in o.Notes) Para("· " + n, Style.Paper, 20);
                    if (o.CoreChanged) Para("这次更新包含规则核心变化。更新前会自动备份存档。" + (o.SaveCompatibility == "breaks" ? "旧版本的存档更新后不能继续，可以先在“继续调查”里导出保存。" : ""), Style.Brass, 20);
                    Para("不更新也可以单机游玩；联机需要先更新。", Style.PaperDim, 19);
                    if (Updater.Error.Length > 0) Para(Updater.Error, Style.Danger, 20);
                    if (now == Updater.State.Available) { Btn("以后再说", () => App.I.CloseModal(scrim)); Btn("下载更新", () => _ = Updater.Download(), true); }
                    else if (now == Updater.State.Downloading)
                    {
                        line.Visible = true; line.Text = "正在下载 " + MB(Updater.DoneBytes) + " / " + MB(o.Bytes) + "（" + lineName + "线路）";
                        Btn("取消下载", Updater.CancelDownload); Btn("在后台下载", () => App.I.CloseModal(scrim));
                    }
                    else
                    {
                        Para(Updater.Android ? "更新已准备好，重新打开游戏后生效。" : "更新已准备好，重启后生效。", Style.Good);
                        Btn("下次启动时", () => App.I.CloseModal(scrim)); Btn(Updater.Android ? "退出游戏" : "现在重启", Updater.RestartNow, true);
                    }
                    break;
                case Updater.State.Checking:
                    title.Text = "检查更新"; Para("正在检查（" + lineName + "线路）…", Style.PaperDim);
                    Btn("关闭", () => App.I.CloseModal(scrim));
                    break;
                case Updater.State.UpToDate:
                    title.Text = "检查更新"; Para("已是最新版本 " + Updater.LocalRelease + "。", Style.Good);
                    Btn("关闭", () => App.I.CloseModal(scrim));
                    break;
                case Updater.State.TooOld:
                    title.Text = "版本太旧"; Para(Updater.Error + "。存档不会受影响。", Style.Paper);
                    Btn("关闭", () => App.I.CloseModal(scrim));
                    break;
                default:
                    title.Text = "检查更新"; Para(Updater.Error.Length > 0 ? Updater.Error : "还没有检查过更新。", Updater.Error.Length > 0 ? Style.Danger : Style.PaperDim);
                    var other = UpdatePolicy.Lines.FirstOrDefault(l => l != Updater.Line);
                    Btn("关闭", () => App.I.CloseModal(scrim));
                    if (other is not null) Btn("改用" + other.Label + "线路", () => { Updater.Line = other; _ = Updater.Check(); });
                    Btn("重新检查", () => _ = Updater.Check(), true);
                    break;
            }
        }
        Updater.Changed += Paint;
        Paint();
        scrim = App.I.Modal(Ui.Panel(col, 40));
        scrim.TreeExiting += () => Updater.Changed -= Paint;
    }

    // the settings row: 当前版本 · 更新线路 [国内][海外] · [检查更新]
    public static Control SettingsRow()
    {
        var row = new HBoxContainer(); row.AddThemeConstantOverride("separation", 12);
        if (!Updater.Enabled) { row.AddChild(Style.Text("开发版（不检查更新）", 22, Style.PaperDim)); return row; }
        row.AddChild(Style.Text(Updater.LocalRelease.Length > 0 ? Updater.LocalRelease : "未知版本", 22, Style.Paper));
        row.AddChild(Ui.Spacer(10));
        var lines = new List<(UpdateLine L, Button B)>();
        void PaintLines() { foreach (var (l, b) in lines) { bool on = Updater.Line == l; b.ButtonPressed = on; b.Modulate = on ? Colors.White : new Color(1, 1, 1, 0.45f); } }
        foreach (var l in UpdatePolicy.Lines)
        {
            var line = l; Button b = null!;
            b = Ui.Button(line.Label, () => { Updater.Line = line; PaintLines(); }, 22, true);
            b.ToggleMode = true; b.CustomMinimumSize = new Vector2(96, 48); b.TooltipText = "更新线路";
            row.AddChild(b); lines.Add((line, b));
        }
        PaintLines();
        var check = Ui.Button("检查更新", () => { if (Updater.Line is null) ChooseLine(() => { _ = Updater.Check(); Open(); }); else { _ = Updater.Check(); Open(); } }, 22);
        check.CustomMinimumSize = new Vector2(140, 48); row.AddChild(check);
        return row;
    }
}
