using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Jint;
using Jint.Native;

namespace ArkhamUI;

// Host for arkham.build's rules (Yunke 11-18: the deck workshop is based on build). app/assets/build-engine/engine.js is
// build's own code (vendor/arkham.build, bundled by tools/build-engine/build.mjs); it runs here in Jint on one worker
// thread, fed with the local card cache in app/assets/build-data (the same JSON build's API serves). Every rule question
// the workshop asks — which cards a deck may take, is it valid, what does an upgrade cost — goes through it.
public sealed class BuildEngine
{
    static BuildEngine? instance;
    public static BuildEngine I => instance ??= new BuildEngine();

    readonly Thread worker;
    readonly BlockingQueue jobs = new();
    Engine? js;
    readonly TaskCompletionSource<int> ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public Task<int> Ready => ready.Task;
    public string? Error { get; private set; }
    public long InitMs { get; private set; }

    BuildEngine()
    {
        worker = new Thread(Run, 16 * 1024 * 1024) { IsBackground = true, Name = "build-engine" };   // a deep stack: the rules recurse
        worker.Start();
        Post(Init);
    }

    // start loading as early as possible (the main menu), so the workshop opens without waiting
    public static void Warm() => _ = I;

    void Run() { while (true) { var job = jobs.Take(); try { job(); } catch (Exception e) { Godot.GD.PushError("build-engine: " + e); } } }
    void Post(Action a) => jobs.Add(a);

    void Init()
    {
        var sw = Stopwatch.StartNew();
        try
        {
            string dir = Path.Combine(App.I.Assets, "build-data"), engineFile = Path.Combine(App.I.Assets, "build-engine", "engine.js");
            var cmp = CultureInfo.GetCultureInfo("zh-CN").CompareInfo;
            js = new Engine(o => o.LimitRecursion(4000).Strict(false));
            js.SetValue("__hostCompare", new Func<string, string, string, int>((a, b, _) => cmp.Compare(a, b, CompareOptions.None)));
            js.Execute(File.ReadAllText(engineFile));
            var e = js.GetValue("BuildEngine");
            var result = js.Invoke(e.Get("init"), e, new object[] {
                File.ReadAllText(Path.Combine(dir, "version-zh-cn.json")),
                File.ReadAllText(Path.Combine(dir, "metadata-zh-cn.json")),
                File.ReadAllText(Path.Combine(dir, "cards-zh-cn.json")),
                "zh-cn" }).UnwrapIfPromise();
            InitMs = sw.ElapsedMilliseconds;
            Godot.GD.Print($"BUILD_ENGINE ready {result} cards in {InitMs} ms");
            ready.TrySetResult((int)result.AsNumber());
        }
        catch (Exception ex)
        {
            Error = ex.Message; Godot.GD.PushError("build-engine init: " + ex);
            ready.TrySetException(ex);
        }
    }

    // Call one of BuildEngine's functions ("validate", or "decks.save" for the deck API); arguments are strings, numbers,
    // booleans or null; the answer comes back as a string (JSON for objects).
    public Task<string> Call(string fn, params object?[] args)
    {
        var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        Post(() =>
        {
            try
            {
                if (js is null) throw new InvalidOperationException(Error ?? "组牌规则引擎尚未就绪");
                JsValue owner = js.GetValue("BuildEngine");
                var parts = fn.Split('.');
                for (int i = 0; i < parts.Length - 1; i++) owner = owner.Get(parts[i]);
                var f = owner.Get(parts[^1]);
                var jsArgs = args.Select(a => a is null ? JsValue.Null : JsValue.FromObject(js, a)).ToArray();
                var r = js.Invoke(f, owner, jsArgs).UnwrapIfPromise();
                tcs.TrySetResult(r.IsString() ? r.AsString() : r.IsBoolean() ? (r.AsBoolean() ? "true" : "false") : r.IsNull() || r.IsUndefined() ? "null" : r.ToString());
            }
            catch (Exception ex) { tcs.TrySetException(ex); }
        });
        return tcs.Task;
    }

    public async Task<JsonElement> CallJson(string fn, params object?[] args)
    {
        await Ready;
        var text = await Call(fn, args);
        using var doc = JsonDocument.Parse(text);
        return doc.RootElement.Clone();
    }

    sealed class BlockingQueue
    {
        readonly Queue<Action> q = new(); readonly object gate = new();
        public void Add(Action a) { lock (gate) { q.Enqueue(a); Monitor.Pulse(gate); } }
        public Action Take() { lock (gate) { while (q.Count == 0) Monitor.Wait(gate); return q.Dequeue(); } }
    }
}
