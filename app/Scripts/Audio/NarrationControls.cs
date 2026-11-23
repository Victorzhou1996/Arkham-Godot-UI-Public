using Godot;

namespace ArkhamUI;

// 剧情朗读 controls (noncore audio, 11-21): on / off, voice volume (heard at once), and in the full panel 重播 / 停止朗读.
// compact (settings dialog row): the switch and the volume only.
public partial class NarrationControls : HBoxContainer
{
    readonly bool compact;
    Button toggle = null!, replay = null!, stop = null!;
    bool refreshing;

    public NarrationControls(bool compact = false) { this.compact = compact; }

    public override void _Ready()
    {
        AddThemeConstantOverride("separation", 12);
        SizeFlagsHorizontal = SizeFlags.ExpandFill;
        if (!GameAudio.Available) { AddChild(Style.Text("本次启动未开启声音。", 20, Style.PaperDim)); return; }
        toggle = Ui.Toggle(GameAudio.NarrationOn); toggle.TooltipText = "剧情页出现时朗读正文（讯飞 江阅尘）";
        if (compact) toggle.CustomMinimumSize = new Vector2(72, 44);
        toggle.Toggled += v => { if (!refreshing) GameAudio.NarrationOn = v; };
        AddChild(toggle);
        AddChild(Style.Text("解说音量", 20, Style.Paper));
        var vol = new HSlider { MinValue = 0, MaxValue = 1, Step = 0.05, Value = GameAudio.NarrationVolume, CustomMinimumSize = new Vector2(compact ? 200 : 240, 30), SizeFlagsHorizontal = SizeFlags.ExpandFill, SizeFlagsVertical = SizeFlags.ShrinkCenter };
        vol.ValueChanged += v => GameAudio.NarrationVolume = v;      // heard at once
        vol.DragEnded += _ => GameAudio.SaveNarrationVolume();
        AddChild(vol);
        if (!compact)
        {
            replay = Ui.Button("重播", GameAudio.ReplayNarration, 20); AddChild(replay);
            stop = Ui.Button("停止朗读", GameAudio.StopNarration, 20); AddChild(stop);
        }
        GameAudio.Changed += Refresh;
        TreeExiting += () => GameAudio.Changed -= Refresh;
        Refresh();
    }

    void Refresh()
    {
        if (!IsInstanceValid(this) || toggle is null) return;
        refreshing = true;
        try
        {
            toggle.ButtonPressed = GameAudio.NarrationOn;
            if (!compact) { stop.Disabled = !GameAudio.Narrating; replay.Disabled = !GameAudio.NarrationOn || !(App.I?.Current is TableScreen); }
        }
        finally { refreshing = false; }
    }
}
