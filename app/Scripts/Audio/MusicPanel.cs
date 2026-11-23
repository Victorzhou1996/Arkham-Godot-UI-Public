using Godot;

namespace ArkhamUI;

// Background-music controls (0712 html MusicControls.vue: track picker for the current place with "跟随剧本", the track playing,
// play / pause / stop, autoplay, loop, music volume) plus previous / next. Every change is heard at once.
// 11-22: the old 循环 switch (on = the track repeated, off = it played once) is now one button that walks the play modes
// 顺序 → 单曲循环 → 剧本循环 → 全部循环, with a 随机 switch beside it (no effect in 单曲循环). The list is in campaign / chapter order.
// Used in two places: MusicPanel.Open() (a dialog; the table's top bar can open it) and inside the settings dialog
// (withVolume: false — the settings dialog keeps its own volume slider, previewed live there).
public partial class MusicControls : VBoxContainer
{
    readonly bool withVolume;
    Label place = null!, now = null!;
    OptionButton pick = null!;
    Button play = null!, autoplay = null!, mode = null!, shuffle = null!;
    Label shuffleLabel = null!;
    bool refreshing;

    public MusicControls(bool withVolume = true) { this.withVolume = withVolume; }

    public override void _Ready()
    {
        AddThemeConstantOverride("separation", 12);
        SizeFlagsHorizontal = SizeFlags.ExpandFill;
        if (!GameAudio.Available) { AddChild(Style.Text("本次启动未开启声音。", 20, Style.PaperDim)); return; }

        place = Style.Text("", 20, Style.PaperDim); AddChild(place);
        pick = new OptionButton { SizeFlagsHorizontal = SizeFlags.ExpandFill, FitToLongestItem = false, CustomMinimumSize = new Vector2(420, 0) };
        Ui.Soften(pick);
        // bigger rows in the track list, so a finger can pick one on a phone (bug #21: the small rows could not be tapped)
        var popup = pick.GetPopup(); popup.AddThemeFontSizeOverride("font_size", 22); popup.AddThemeConstantOverride("v_separation", 16); popup.AddThemeConstantOverride("h_separation", 18);
        pick.ItemSelected += i => { if (!refreshing) GameAudio.Select(i <= 0 ? "" : pick.GetItemMetadata((int)i).AsString()); };
        AddChild(pick);
        now = Style.Text("", 20, Style.Paper); now.AutowrapMode = TextServer.AutowrapMode.WordSmart; AddChild(now);

        var transport = new HBoxContainer(); transport.AddThemeConstantOverride("separation", 10); AddChild(transport);
        transport.AddChild(Ui.Button("上一首", GameAudio.Previous, 20));
        play = Ui.Button("播放", GameAudio.TogglePlay, 20, true); play.CustomMinimumSize = new Vector2(110, 0); transport.AddChild(play);
        transport.AddChild(Ui.Button("下一首", GameAudio.Next, 20));
        transport.AddChild(Ui.Button("停止", GameAudio.Stop, 20));

        var flags = new HBoxContainer(); flags.AddThemeConstantOverride("separation", 14); AddChild(flags);
        flags.AddChild(Style.Text("自动播放", 20, Style.Paper));
        autoplay = Ui.Toggle(GameAudio.Autoplay); autoplay.Toggled += v => { if (!refreshing) GameAudio.Autoplay = v; }; flags.AddChild(autoplay);

        // play mode: one button, each press the next mode; the mode is the button's text, what it does its tooltip.
        // In the settings dialog (no volume row) it shares the 自动播放 row, unlabelled, so that dialog keeps its height.
        bool compact = !withVolume;
        var modes = flags;
        if (!compact) { modes = new HBoxContainer(); modes.AddThemeConstantOverride("separation", 14); AddChild(modes); modes.AddChild(Style.Text("播放方式", 20, Style.Paper)); }
        else { flags.AddThemeConstantOverride("separation", 12); autoplay.CustomMinimumSize = new Vector2(72, 44); modes.AddChild(Ui.Spacer(6)); }
        mode = Ui.Button("", GameAudio.CycleMode, 20); mode.Name = "PlayMode"; mode.CustomMinimumSize = new Vector2(compact ? 0 : 150, 44);
        mode.AddThemeColorOverride("font_color", Style.Amber); mode.AddThemeColorOverride("font_hover_color", Style.Amber);
        modes.AddChild(mode);
        modes.AddChild(Ui.Spacer(compact ? 4 : 10));
        shuffleLabel = Style.Text("随机", 20, Style.Paper); modes.AddChild(shuffleLabel);
        shuffle = Ui.Toggle(GameAudio.Shuffle); shuffle.Name = "Shuffle";
        if (compact) shuffle.CustomMinimumSize = new Vector2(72, 44);
        shuffle.AddThemeStyleboxOverride("disabled", Ui.SoftBox(0.15f, false)); shuffle.AddThemeColorOverride("font_disabled_color", new Color(Style.PaperDim, 0.35f));
        shuffle.Toggled += v => { if (!refreshing) GameAudio.Shuffle = v; };
        modes.AddChild(shuffle);

        if (withVolume && App.I?.S?.Settings is { } s)
        {
            var row = new HBoxContainer(); row.AddThemeConstantOverride("separation", 14); AddChild(row);
            row.AddChild(Style.Text("音乐音量", 20, Style.Paper));
            var vol = new HSlider { MinValue = 0, MaxValue = 1, Step = 0.05, Value = s.MusicVolume, CustomMinimumSize = new Vector2(300, 30), SizeFlagsHorizontal = SizeFlags.ExpandFill };
            vol.ValueChanged += v => s.MusicVolume = v;          // heard at once (the audio node follows the setting every frame)
            vol.DragEnded += _ => s.Save();
            row.AddChild(vol);
        }

        GameAudio.Changed += Refresh;
        TreeExiting += () => GameAudio.Changed -= Refresh;
        Refresh();
    }

    void Refresh()
    {
        if (!IsInstanceValid(this) || pick is null) return;
        refreshing = true;
        try
        {
            place.Text = "当前场合：" + (GameAudio.PlaceName.Length > 0 ? GameAudio.PlaceName : "—");
            pick.Clear();
            var def = GameAudio.DefaultTitle;
            pick.AddItem(def.Length > 0 ? "跟随剧本（" + def + "）" : "跟随剧本（无默认曲目）", 0);
            string chosen = GameAudio.ChosenFile; int sel = 0, id = 1;
            foreach (var group in GameAudio.Tracks.GroupBy(t => t.Group))
            {
                pick.AddSeparator(group.Key);
                foreach (var t in group)
                {
                    pick.AddItem(t.Title, id++);
                    int idx = pick.ItemCount - 1; pick.SetItemMetadata(idx, t.File);
                    if (t.File == chosen) sel = idx;
                }
            }
            pick.Select(sel);
            pick.Disabled = GameAudio.PlaceKey.Length == 0;
            now.Text = GameAudio.HasTrack
                ? "正在播放：" + GameAudio.CurrentTitle + (GameAudio.CurrentGroup.Length > 0 ? "（" + GameAudio.CurrentGroup + "）" : "") + (GameAudio.Playing ? "" : " · 已暂停")
                : "此场合没有曲目";
            play.Text = GameAudio.Playing ? "暂停" : "播放";
            play.Disabled = !GameAudio.HasTrack;
            autoplay.ButtonPressed = GameAudio.Autoplay;
            var m = GameAudio.Mode;
            mode.Text = GameAudio.ModeName(m);
            string scope = m == GameAudio.PlayMode.Campaign && GameAudio.ScopeGroup.Length > 0 ? "（" + GameAudio.ScopeGroup + "）" : "";
            mode.TooltipText = "播放方式：" + GameAudio.ModeName(m) + scope + "\n" + GameAudio.ModeTip(m) + "\n点击切换：顺序 → 单曲循环 → 剧本循环 → 全部循环";
            bool one = m == GameAudio.PlayMode.One;
            shuffle.ButtonPressed = GameAudio.Shuffle; shuffle.Disabled = one;
            shuffleLabel.Modulate = new Color(1, 1, 1, one ? 0.4f : 1f);
            string tip = one ? "随机：单曲循环时不起作用" : "随机：下一首在" + (m == GameAudio.PlayMode.Campaign ? "当前战役" + scope : "全部曲目") + "中随机挑选";
            shuffle.TooltipText = tip; shuffleLabel.TooltipText = tip; shuffleLabel.MouseFilter = MouseFilterEnum.Pass;
        }
        finally { refreshing = false; }
    }
}

public static class MusicPanel
{
    public static void Open()
    {
        var col = new VBoxContainer { CustomMinimumSize = new Vector2(620, 0) };
        col.AddThemeConstantOverride("separation", 18);
        col.AddChild(Style.Title("背景音乐", 34));
        col.AddChild(new MusicControls(withVolume: true));
        col.AddChild(Style.Text("剧情朗读", 24, Style.Paper)); col.AddChild(new NarrationControls());   // 11-21 story narration
        var buttons = new HBoxContainer { Alignment = BoxContainer.AlignmentMode.End }; col.AddChild(buttons);
        Control scrim = null!;
        buttons.AddChild(Ui.Button("关闭", () => App.I.CloseModal(scrim), 22));
        scrim = App.I.Modal(Ui.Panel(col, 36));
        scrim.TreeExiting += () => App.I?.S?.Settings.Save();   // the volume moved here is kept
    }
}
