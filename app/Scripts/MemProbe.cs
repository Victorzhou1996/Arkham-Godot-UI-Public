using Godot;

namespace ArkhamUI;

// Memory and hitch probe for QA (Yunke 11-23: "有些时候会卡死甚至闪退，尤其是换初始牌还有剧本结束的时候，检查一下是不是爆内存
// 或者占用cpu资源的问题"). Only exists when the game starts with --mem-probe. Once a second it prints the process's memory
// (working set, private bytes), the .NET heap, Godot's own static memory, video memory for textures, object and node counts,
// and the CPU time used in that second; every frame that takes longer than 250 ms prints MEM_HITCH with the step being run.
public partial class MemProbe : Node
{
    double acc; ulong lastUs; TimeSpan lastCpu; ulong startMs;
    readonly System.Diagnostics.Process self = System.Diagnostics.Process.GetCurrentProcess();
    public static string Step = "";   // the QA step being run (QaScript sets it)

    public static void AttachIfAsked(Node root)
    {
        if (!OS.GetCmdlineUserArgs().Contains("--mem-probe")) return;
        root.AddChild(new MemProbe { Name = "MemProbe" });
    }

    public override void _Ready()
    {
        ProcessMode = ProcessModeEnum.Always;
        lastUs = Time.GetTicksUsec(); startMs = Time.GetTicksMsec(); self.Refresh(); lastCpu = self.TotalProcessorTime;
        QaScript.Register("mem", label => Print("mark " + label));
    }

    public override void _Process(double delta)
    {
        ulong now = Time.GetTicksUsec();
        double frameMs = (now - lastUs) / 1000.0; lastUs = now;
        if (frameMs > 250) GD.Print($"MEM_HITCH {frameMs:0} ms t={(Time.GetTicksMsec() - startMs) / 1000.0:0.0} step={Step}");
        acc += delta;
        if (acc < 1.0) return;
        acc = 0;
        Print("tick");
    }

    void Print(string what)
    {
        self.Refresh();
        var cpu = self.TotalProcessorTime; double cpuMs = (cpu - lastCpu).TotalMilliseconds; lastCpu = cpu;
        GD.Print($"MEM {what} t={(Time.GetTicksMsec() - startMs) / 1000.0:0.0} ws={self.WorkingSet64 >> 20}MB priv={self.PrivateMemorySize64 >> 20}MB gc={System.GC.GetTotalMemory(false) >> 20}MB "
            + $"static={(long)Performance.GetMonitor(Performance.Monitor.MemoryStatic) >> 20}MB tex={(long)Performance.GetMonitor(Performance.Monitor.RenderTextureMemUsed) >> 20}MB "
            + $"vmem={(long)Performance.GetMonitor(Performance.Monitor.RenderVideoMemUsed) >> 20}MB objs={(long)Performance.GetMonitor(Performance.Monitor.ObjectCount)} nodes={(long)Performance.GetMonitor(Performance.Monitor.ObjectNodeCount)} "
            + $"orphans={(long)Performance.GetMonitor(Performance.Monitor.ObjectOrphanNodeCount)} cpu={cpuMs:0}ms threads={self.Threads.Count} step={Step}");
    }
}
