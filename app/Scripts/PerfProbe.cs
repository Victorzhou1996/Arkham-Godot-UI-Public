using Godot;

namespace ArkhamUI;

// Performance probe for QA (Yunke 11-21: "现在各种操作的延迟卡顿如何？"). Only exists when the game starts with --perf-probe.
// The QA window runs off-screen and Windows caps its frame rate (~32 fps even on the idle main menu), so the frame interval
// says little; this records what each frame actually costs: the main-thread process time and the viewport's render time
// (CPU side and GPU side, measured by the rendering server).
// QA: perf-reset, perf-report:<label>. Actions on the table print ACT_MS separately (TableScreen.Choose2Async).
public partial class PerfProbe : Node
{
    readonly List<(float Process, float Cpu, float Gpu)> frames = new();
    bool recording;
    Rid vp;
    ulong frameStartUs; float logicMs;

    public static void AttachIfAsked(Node root)
    {
        if (!OS.GetCmdlineUserArgs().Contains("--perf-probe")) return;
        root.AddChild(new PerfProbe { Name = "PerfProbe" });
    }

    public override void _Ready()
    {
        ProcessMode = ProcessModeEnum.Always; ProcessPriority = int.MinValue;   // first node processed in the frame
        // the frame's own work before drawing (scripts, tweens, layout): from this node's process to the start of the draw.
        // TimeProcess includes the off-screen window's frame wait, so it is not used.
        RenderingServer.FramePreDraw += () => { if (frameStartUs > 0) logicMs = (Time.GetTicksUsec() - frameStartUs) / 1000f; };
        vp = GetViewport().GetViewportRid();
        RenderingServer.ViewportSetMeasureRenderTime(vp, true);
        QaScript.Register("perf-reset", _ => { frames.Clear(); recording = true; GD.Print("PERF_RESET"); });
        QaScript.Register("perf-report", label => Report(label));
    }

    public override void _Process(double delta)
    {
        float process = logicMs;   // the previous frame's logic (measured at its draw)
        frameStartUs = Time.GetTicksUsec();
        if (!recording) return;
        float cpu = (float)RenderingServer.ViewportGetMeasuredRenderTimeCpu(vp) + (float)RenderingServer.GetFrameSetupTimeCpu();
        float gpu = (float)RenderingServer.ViewportGetMeasuredRenderTimeGpu(vp);
        frames.Add((process, cpu, gpu));
        float work = Math.Max(process + cpu, gpu);
        if (work > 33) GD.Print($"PERF_SPIKE work={work:0.0}ms logic={process:0.0} renderCpu={cpu:0.0} gpu={gpu:0.0}");
    }

    void Report(string label)
    {
        if (frames.Count == 0) { GD.Print("PERF_REPORT " + label + " none"); return; }
        string Line(string name, IEnumerable<float> values)
        {
            var s = values.OrderBy(x => x).ToList();
            float P(double q) => s[Math.Min(s.Count - 1, (int)(q * s.Count))];
            return $"{name} avg={s.Average():0.0} p50={P(0.5):0.0} p99={P(0.99):0.0} max={s[^1]:0.0} >16ms={s.Count(x => x > 16.7)} >33ms={s.Count(x => x > 33)}";
        }
        GD.Print($"PERF_REPORT {label} frames={frames.Count} | " + Line("logic", frames.Select(f => f.Process)) + " | " + Line("renderCpu", frames.Select(f => f.Cpu))
            + " | " + Line("gpu", frames.Select(f => f.Gpu)) + " | " + Line("work", frames.Select(f => Math.Max(f.Process + f.Cpu, f.Gpu))));
    }
}
