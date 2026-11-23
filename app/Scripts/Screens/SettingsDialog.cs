using Godot;

namespace ArkhamUI;

public static class SettingsDialog
{
    public static void Open()
    {
        var s = App.I.S!.Settings;
        var col = new VBoxContainer { CustomMinimumSize = new Vector2(760, 0) };
        col.AddThemeConstantOverride("separation", 18);
        col.AddChild(Style.Title("设置", 38));
        var grid = new GridContainer { Columns = 2 }; grid.AddThemeConstantOverride("h_separation", 30); grid.AddThemeConstantOverride("v_separation", 12);   // noncore audio 11-22: 18 → 12 so the 解说音量 row fits at 1080p
        // the rows scroll and the title and the buttons stay (bug #24: at 115 % on a phone 保存设置 was below the screen's
        // edge, so a scale too large could not be set back)
        var body = new VBoxContainer(); body.AddThemeConstantOverride("separation", 18); body.AddChild(grid);
        var bodyScroll = new ScrollContainer { HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled, VerticalScrollMode = ScrollContainer.ScrollMode.Disabled };
        bodyScroll.AddChild(body); col.AddChild(bodyScroll);
        void Row(string name, Control c) { grid.AddChild(Style.Text(name, 24, Style.Paper)); c.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill; grid.AddChild(c); }
        HSlider Slider(double v) { var sl = new HSlider { MinValue = 0, MaxValue = 1, Step = 0.05, Value = v, CustomMinimumSize = new Vector2(360, 30) }; return sl; }
        var music = Slider(s.MusicVolume); Row("音乐音量", music);
        var fx = Slider(s.EffectsVolume); Row("音效音量", fx);
        // Yunke 11-20: what is changed here is heard at once (noncore audio): the sliders preview live, cancel goes back
        music.ValueChanged += _ => GameAudio.PreviewVolume(music.Value, fx.Value);
        fx.ValueChanged += _ => GameAudio.PreviewVolume(music.Value, fx.Value);
        // Yunke 11-22 "音乐和解说音量要分开设置" (noncore audio): the narration voice has its own level, heard at once, kept on save
        double voiceWas = GameAudio.NarrationVolume; var voice = Slider(voiceWas); Row("解说音量", voice);
        voice.ValueChanged += v => GameAudio.NarrationVolume = v;
        Row("背景音乐", new MusicControls(withVolume: false));
        var full = Ui.Toggle(s.Fullscreen); Row("全屏显示", full);
        var motion = Ui.Toggle(s.ReducedMotion); Row("减少动效", motion);
        // a free slider (Yunke 11-23: "无极变化…调整时随时变化尺寸大小方便预览"), not fixed choices: the whole interface takes
        // the new size as soon as the finger lets go (a size changing under a dragging finger would move the slider itself);
        // the number follows the drag. Saving keeps it (with the 10 s confirm), cancelling goes back.
        int chosenScale = Math.Clamp(s.TextScale, App.ScaleMin, App.ScaleMax);
        var scaleRow = new HBoxContainer(); scaleRow.AddThemeConstantOverride("separation", 14);
        var scale = new HSlider { MinValue = App.ScaleMin, MaxValue = App.ScaleMax, Step = 1, Value = chosenScale, CustomMinimumSize = new Vector2(300, 30), SizeFlagsVertical = Control.SizeFlags.ShrinkCenter, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        var scaleText = Style.Text(chosenScale + "%", 22, Style.Paper); scaleText.CustomMinimumSize = new Vector2(80, 0);
        bool dragging = false;
        void PreviewScale() { chosenScale = (int)scale.Value; App.I.PreviewUiScale(chosenScale / 100f); }
        scale.DragStarted += () => dragging = true;
        scale.DragEnded += _ => { dragging = false; PreviewScale(); };
        scale.ValueChanged += v => { scaleText.Text = (int)v + "%"; chosenScale = (int)v; if (!dragging) PreviewScale(); };   // a tap on the bar or a key: at once
        var scaleReset = Ui.Button("100%", () => scale.Value = 100, 20); scaleReset.CustomMinimumSize = new Vector2(90, 44);
        scaleRow.AddChild(scale); scaleRow.AddChild(scaleText); scaleRow.AddChild(scaleReset);
        Row("显示比例", scaleRow);   // Yunke 11-23: the old 界面缩放, renamed
        QaScript.Register("settings-scale", a => scale.Value = int.Parse(a));
        // 缩放 (Yunke 11-23): how much of the screen the whole picture takes, like pinching a video larger or smaller. Like
        // 显示比例 it changes when the finger lets go, and a new value has to be kept within 10 s.
        int chosenZoom = Math.Clamp(s.FrameZoom, App.ZoomMin, App.ZoomMax);
        var zoomRow = new HBoxContainer(); zoomRow.AddThemeConstantOverride("separation", 14);
        var zoom = new HSlider { MinValue = App.ZoomMin, MaxValue = App.ZoomMax, Step = 1, Value = chosenZoom, CustomMinimumSize = new Vector2(300, 30), SizeFlagsVertical = Control.SizeFlags.ShrinkCenter, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        var zoomText = Style.Text(chosenZoom + "%", 22, Style.Paper); zoomText.CustomMinimumSize = new Vector2(80, 0);
        bool zoomDragging = false;
        void PreviewZoom() { chosenZoom = (int)zoom.Value; App.I.PreviewZoom(chosenZoom / 100f); }
        zoom.DragStarted += () => zoomDragging = true;
        zoom.DragEnded += _ => { zoomDragging = false; PreviewZoom(); };
        zoom.ValueChanged += v => { zoomText.Text = (int)v + "%"; chosenZoom = (int)v; if (!zoomDragging) PreviewZoom(); };
        var zoomReset = Ui.Button("100%", () => zoom.Value = 100, 20); zoomReset.CustomMinimumSize = new Vector2(90, 44);
        zoomRow.AddChild(zoom); zoomRow.AddChild(zoomText); zoomRow.AddChild(zoomReset);
        Row("缩放", zoomRow);
        QaScript.Register("settings-zoom", a => zoom.Value = int.Parse(a));
        var auto = Ui.Toggle(s.AutoSkip); Row("自动略过无需选择的步骤", auto);
        var confirm = Ui.Toggle(s.ConfirmEndTurn); Row("结束回合前确认", confirm);
        var fastTest = Ui.Toggle(s.FastInSkillTests); if (TableScreen.CoreTakesFastSetting) Row("检定中也可使用闪电能力", fastTest);
        fastTest.TooltipText = "打开时，技能检定的空档会询问与这次检定无关的快速能力（如禁忌知识、天文地图集）；关闭则检定中只出现与检定相关的能力。不想被某张牌打断，可在该牌上设为“停止触发”。";
        // hover (Yunke 11-20): the delay in 0.1 s steps, and what the hover box shows
        var delayRow = new HBoxContainer(); delayRow.AddThemeConstantOverride("separation", 14);
        var delay = new HSlider { MinValue = 0.1, MaxValue = 3.0, Step = 0.1, Value = Math.Clamp(s.HoverDelay, 0.1, 3.0), CustomMinimumSize = new Vector2(300, 30), SizeFlagsVertical = Control.SizeFlags.ShrinkCenter };
        var delayText = Style.Text(delay.Value.ToString("0.0") + " 秒", 22, Style.Paper); delayText.CustomMinimumSize = new Vector2(80, 0);
        delay.ValueChanged += v => delayText.Text = v.ToString("0.0") + " 秒";
        delayRow.AddChild(delay); delayRow.AddChild(delayText); Row("悬停触发时间", delayRow);
        // two buttons side by side, the chosen one lit (Yunke 11-20: buttons, not a drop-down)
        string chosenMode = s.HoverMode == "image" ? "image" : "text";
        var modeRow = new HBoxContainer(); modeRow.AddThemeConstantOverride("separation", 12);
        var modeButtons = new List<(string Mode, Button B)>();
        void PaintModes() { foreach (var (md, b) in modeButtons) { b.Modulate = md == chosenMode ? Colors.White : new Color(1, 1, 1, 0.45f); b.ButtonPressed = md == chosenMode; } }
        foreach (var (md, label) in new[] { ("text", "文字模式"), ("image", "原图模式") })
        {
            var mode = md; Button b = null!;
            b = Ui.Button(label, () => { chosenMode = mode; PaintModes(); }, 22, true);
            b.ToggleMode = true; b.CustomMinimumSize = new Vector2(170, 48); b.TooltipText = mode == "image" ? "悬停时显示原卡" : "悬停时显示卡牌文字";
            modeRow.AddChild(b); modeButtons.Add((mode, b));
        }
        PaintModes(); Row("悬停模式", modeRow);
        Row("游戏版本", UpdateUi.SettingsRow());   // version, update line, check (Scripts/Update, Yunke 11-23)
        body.AddChild(Style.Text("自动略过只处理“唯一选项且不需要你决定”的步骤；需要排序、选目标或可选发动的效果始终由你决定。", 19, Style.PaperDim));
        foreach (var l in body.GetChildren()) if (l is Label lab) lab.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        int scaleWas = s.TextScale, zoomWas = s.FrameZoom;   // a changed scale or zoom must be confirmed (ConfirmScale)
        var buttons = new HBoxContainer { Alignment = BoxContainer.AlignmentMode.End }; col.AddChild(buttons);
        Control scrim = null!;
        buttons.AddChild(Ui.Button("取消", () => App.I.CloseModal(scrim)));
        buttons.AddChild(Ui.Button("保存设置", () =>
        {
            s.MusicVolume = music.Value; s.EffectsVolume = fx.Value; s.Fullscreen = full.ButtonPressed; s.ReducedMotion = motion.ButtonPressed;
            s.TextScale = chosenScale; s.FrameZoom = chosenZoom; s.AutoSkip = auto.ButtonPressed; s.ConfirmEndTurn = confirm.ButtonPressed; s.FastInSkillTests = fastTest.ButtonPressed;
            s.HoverDelay = Math.Round(delay.Value, 1); s.HoverMode = chosenMode;
            voiceWas = voice.Value; GameAudio.SaveNarrationVolume();
            App.I.PreviewUiScale(null); App.I.PreviewZoom(null);   // the saved values take over from the previews
            s.Save(); App.I.ApplySettings(); App.I.CloseModal(scrim);
            if (chosenScale != scaleWas || chosenZoom != zoomWas) ConfirmScale(scaleWas, zoomWas); else App.I.Toast("设置已保存");
        }, 22, true));
        var panel = Ui.Panel(col, 40);
        scrim = App.I.Modal(panel);
        // each frame: the rows get what the screen has left after the title, the buttons and the panel's margins
        void FitRows()
        {
            if (!GodotObject.IsInstanceValid(bodyScroll)) return;
            float view = panel.GetViewportRect().Size.Y;
            float overhead = panel.GetCombinedMinimumSize().Y - bodyScroll.GetCombinedMinimumSize().Y;
            float room = view - 24 - overhead, rows = body.GetCombinedMinimumSize().Y;
            bool tall = rows > room;
            bodyScroll.VerticalScrollMode = tall ? ScrollContainer.ScrollMode.Auto : ScrollContainer.ScrollMode.Disabled;
            bodyScroll.CustomMinimumSize = new Vector2(0, tall ? Math.Max(160, room) : 0);
        }
        Action onFrame = FitRows; var tree = App.I.GetTree(); tree.ProcessFrame += onFrame;
        bodyScroll.TreeExiting += () => tree.ProcessFrame -= onFrame;
        scrim.TreeExiting += GameAudio.EndPreview;   // saved → the settings hold the new levels; cancelled → back to the old ones
        scrim.TreeExiting += () => GameAudio.NarrationVolume = voiceWas;   // the same for the narration level
        scrim.TreeExiting += () => { App.I.PreviewUiScale(null); App.I.PreviewZoom(null); };   // cancelled (or saved): the saved scale and zoom again
    }

    // a new interface scale has to be kept on purpose: without 保留 within 10 s it goes back to the old one (Yunke 11-23:
    // a player set 130% and could no longer reach the confirm button to change it back)
    static void ConfirmScale(int old, int oldZoom)
    {
        var s = App.I.S!.Settings;
        var col = new VBoxContainer { CustomMinimumSize = new Vector2(560, 0) }; col.AddThemeConstantOverride("separation", 18);
        bool scaleChanged = s.TextScale != old, zoomChanged = s.FrameZoom != oldZoom;
        string what = scaleChanged && zoomChanged ? "显示比例和缩放" : zoomChanged ? "缩放" : "显示比例";
        col.AddChild(Style.Title("保留新的" + what + "吗？", 32));
        int left = 10;
        var line = Style.Text("", 22, Style.Paper); line.AutowrapMode = TextServer.AutowrapMode.WordSmart; col.AddChild(line);
        string Now() => (scaleChanged ? "显示比例 " + s.TextScale + "%" : "") + (scaleChanged && zoomChanged ? "，" : "") + (zoomChanged ? "缩放 " + s.FrameZoom + "%" : "");
        string Was() => (scaleChanged ? "显示比例 " + old + "%" : "") + (scaleChanged && zoomChanged ? "，" : "") + (zoomChanged ? "缩放 " + oldZoom + "%" : "");
        string nowText = Now(), wasText = Was();
        void Paint() => line.Text = "已改为" + nowText + "。" + left + " 秒后自动恢复为" + wasText + "。";
        Paint();
        var buttons = new HBoxContainer { Alignment = BoxContainer.AlignmentMode.End }; buttons.AddThemeConstantOverride("separation", 14); col.AddChild(buttons);
        Control scrim = null!;
        bool done = false;
        void Revert()
        {
            if (done) return; done = true;
            s.TextScale = old; s.FrameZoom = oldZoom; s.Save(); App.I.ApplySettings(); App.I.CloseModal(scrim);
            App.I.Toast("已恢复为" + wasText);
        }
        void Keep() { if (done) return; done = true; App.I.CloseModal(scrim); App.I.Toast("设置已保存"); }
        var back = Ui.Button("恢复", Revert, 24); back.CustomMinimumSize = new Vector2(170, 58); buttons.AddChild(back);
        var keep = Ui.Button("保留", Keep, 24, true); keep.CustomMinimumSize = new Vector2(150, 58); buttons.AddChild(keep);
        var timer = new Godot.Timer { WaitTime = 1, Autostart = true };
        timer.Timeout += () => { if (done) return; left--; if (left <= 0) Revert(); else Paint(); };
        col.AddChild(timer);
        scrim = App.I.Modal(Ui.Panel(col, 36), dismissOnBackdrop: false);
        QaScript.Register("scale-keep", _ => Keep());
    }
}
