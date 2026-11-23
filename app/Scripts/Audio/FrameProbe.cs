using Godot;

namespace ArkhamUI;

// Frame-time probe for QA (noncore audio line, 2026-11-19): only exists when the game starts with --frame-probe.
// Every frame's time is recorded; a frame over 33 ms prints FRAME_SPIKE with the audio events that happened just before it
// (GameAudio.Mark), so a hitch can be pinned on music loading, a cross-fade, a first effect … or on something else.
// QA: frames-reset, frames-report:<label>, audio-burst:<cue>,<count>,<intervalMs>.
public partial class FrameProbe : Node
{
    static FrameProbe? I;
    readonly List<float> frames = new();
    readonly List<(ulong Us, string What)> events = new();
    readonly Dictionary<string, int> spikeBy = new();
    ulong lastFrameUs;
    bool recording;

    public static void AttachIfAsked(Node root)
    {
        if (I is not null || !OS.GetCmdlineUserArgs().Contains("--frame-probe")) return;
        root.AddChild(new FrameProbe { Name = "FrameProbe" });
    }
    // Something audio-related happened now (called by GameAudio).
    public static void Mark(string what) { if (I is not null) lock (I.events) I.events.Add((Time.GetTicksUsec(), what)); }

    public override void _Ready()
    {
        I = this; ProcessMode = ProcessModeEnum.Always; ProcessPriority = int.MinValue;   // first in the frame
        QaScript.Register("frames-reset", _ => { frames.Clear(); spikeBy.Clear(); recording = true; GD.Print("FRAMES_RESET"); });
        QaScript.Register("frames-report", label => Report(label));
        QaScript.Register("audio-burst", async a =>
        {
            var p = a.Split(','); string cue = p[0]; int n = int.Parse(p[1]); int gap = int.Parse(p[2]);
            for (int i = 0; i < n; i++) { GameAudio.Cue(cue); await ToSignal(GetTree().CreateTimer(gap / 1000.0), SceneTreeTimer.SignalName.Timeout); }
        });
        lastFrameUs = Time.GetTicksUsec();
    }

    public override void _Process(double delta)
    {
        ulong now = Time.GetTicksUsec();
        float ms = (now - lastFrameUs) / 1000f;
        ulong from = lastFrameUs; lastFrameUs = now;
        List<string> recent;
        lock (events) { recent = events.Where(e => e.Us >= from - 50_000).Select(e => e.What).Distinct().ToList(); events.RemoveAll(e => e.Us < from - 50_000); }
        if (!recording) return;
        frames.Add(ms);
        if (ms > 33)
        {
            string why = recent.Count > 0 ? string.Join(",", recent) : "-";
            spikeBy[why] = spikeBy.GetValueOrDefault(why) + 1;
            GD.Print($"FRAME_SPIKE {ms:0.0}ms audio={why}");
        }
    }

    void Report(string label)
    {
        if (frames.Count == 0) { GD.Print("FRAMES_REPORT " + label + " none"); return; }
        var s = frames.OrderBy(x => x).ToList();
        float P(double q) => s[Math.Min(s.Count - 1, (int)(q * s.Count))];
        GD.Print($"FRAMES_REPORT {label} frames={s.Count} avg={s.Average():0.0}ms p50={P(0.5):0.0} p99={P(0.99):0.0} max={s[^1]:0.0} " +
                 $">33ms={s.Count(x => x > 33)} >50ms={s.Count(x => x > 50)} >100ms={s.Count(x => x > 100)}");
        foreach (var (why, n) in spikeBy.OrderByDescending(k => k.Value)) GD.Print($"FRAMES_SPIKES_BY {label} {n}x audio={why}");
    }
}
