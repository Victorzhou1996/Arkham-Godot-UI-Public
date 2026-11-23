using Godot;
using System.Net.Http;
using HttpClient = System.Net.Http.HttpClient;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Path = System.IO.Path;
using File = System.IO.File;
using Directory = System.IO.Directory;

namespace ArkhamUI;

// In-game updates (docs/HOT-UPDATE-DESIGN.md, Yunke 11-23). The flow:
//   check     the chosen line's manifest.json, signed with the release key (UpdatePolicy); compared file by file (SHA-256)
//             with what is installed (game/version.json caches the hashes)
//   download  only the files that differ, from the server's object store (objects/ab/abcdef…), each checked against the
//             manifest's hash, into game/updates/staging/<release>/files/; then ready.json
//   switch    never while the files are in use. Data files (pictures, texts, scenarios, patch .pck) are switched by the
//             game itself at its next start, before it opens any of them; the engine, the program code and the rules core
//             by the update helper (game/updater/apply-update.ps1) after the game has exited. Every switch is a list of
//             moves; the replaced files go to game/updates/recovery/<release>/, and a failed switch moves everything back.
//   roll back a new version that fails to reach the main menu twice in a row is moved back out at the third start.
// Saves (DataRoot) are never touched. Not in a development run unless --update-root names a test install.
public static class Updater
{
    public enum State { Off, Idle, Checking, UpToDate, Available, Downloading, Ready, Failed, TooOld }

    // NeedsApk (Android): the program or the rules core changed — the new APK is downloaded by the browser and installed by
    // the system (Yunke 11-23: "把安卓的热更新也做出来"); ApkUrl is where
    public sealed record Offer(string Release, string[] Notes, long Bytes, int Files, bool CoreChanged, string SaveCompatibility, bool NeedsHelper, bool NeedsApk = false, string ApkUrl = "");
    // Android: the release the installed APK was built as (res://ahm-bundle/bundle.json "release"; QA --qa-apk-release=)
    public static string ApkRelease { get; private set; } = "";
    public static bool Android => platform == "android";

    public static bool Enabled { get; private set; }
    public static State Now { get; private set; } = State.Off;
    public static Offer? Pending { get; private set; }
    public static string Error { get; private set; } = "";
    public static long DoneBytes { get; private set; }
    public static string LocalRelease { get; private set; } = "";
    public static string LocalCore { get; private set; } = "";
    public static string BootNote { get; private set; } = "";      // said once on the menu (updated / rolled back / failed)
    public static event Action? Changed;

    // online play needs the newest version (Yunke 11-23: "不更新就不能联机，可以单机")
    public static bool OnlineBlocked => Now is State.Available or State.Downloading or State.Ready or State.TooOld;
    // what the online room compares: the release and the rules core
    public static string VersionTag => (LocalRelease.Length > 0 ? LocalRelease : "dev") + "|" + LocalCore;

    static string install = "", meta = "", platform = "win64", dataRoot = "";
    static string? baseOverride;
    static JsonObject? payload;                 // the offered release's manifest payload
    static List<JsonNode> needed = new();       // its files that differ from what is installed
    static List<string> removed = new();
    static CancellationTokenSource? cts;

    static string Updates => Path.Combine(meta, "updates");
    static string Staging(string r) => Path.Combine(Updates, "staging", r);
    static string Recovery(string r) => Path.Combine(Updates, "recovery", r);
    static string VersionFile => Path.Combine(meta, "version.json");
    static string Rel(string full) => Path.GetRelativePath(install, full).Replace('\\', '/');
    static string Abs(string rel) => Path.GetFullPath(Path.Combine(install, rel));

    static void Notify() => Callable.From(() => Changed?.Invoke()).CallDeferred();
    static void Log(string s) => GD.Print("UPDATE " + s);

    // ─────────────────────────── start-up ───────────────────────────

    // Called first thing in App._Ready (after Root/DataRoot are known, before anything opens a game file). Applies a staged
    // data update, rolls back a version that keeps failing, loads the patch packs. Returns true when the game must quit at
    // once (the update helper takes over and starts it again).
    public static bool Boot(string appRoot, string data)
    {
        dataRoot = data;
        var args = OS.GetCmdlineUserArgs();
        string? root = null;
        foreach (var a in args)
        {
            if (a.StartsWith("--update-root=")) root = Path.GetFullPath(Path.Combine(appRoot, a[14..]));
            else if (a.StartsWith("--update-base=")) baseOverride = a[14..].TrimEnd('/') + "/";
        }
        if (root is null && App.ExportedWindows) root = Path.GetDirectoryName(Path.GetFullPath(OS.GetExecutablePath()))!;
        // Android (and its desktop test, --update-platform=android with --update-root=): the unpacked game files under the
        // app's files folder are the install; the APK itself is replaced only by the system installer
        if (OS.GetName() == "Android" || args.Contains("--update-platform=android"))
        {
            platform = "android";
            if (root is null && OS.GetName() == "Android") root = appRoot;
            ApkRelease = args.FirstOrDefault(a => a.StartsWith("--qa-apk-release="))?[17..] ?? "";
            try
            {
                if (ApkRelease.Length == 0 && Godot.FileAccess.FileExists("res://ahm-bundle/bundle.json"))
                    ApkRelease = (string?)JsonNode.Parse(Godot.FileAccess.GetFileAsString("res://ahm-bundle/bundle.json"))?["release"] ?? "";
            }
            catch { }
        }
        LocalCore = ReadCore(appRoot);
        if (root is null) { Log("off (development run)"); return false; }
        install = root; meta = Android ? install : Path.Combine(install, "game"); Enabled = true; Now = State.Idle;   // Android: version.json, updates/, patches/ in the files folder itself
        foreach (var a in args) if (a.StartsWith("--after-pid=") && int.TryParse(a[12..], out int pid)) WaitExit(pid);
        try
        {
            if (HelperResult()) return false;
            if (BootPending(out bool quit)) { if (quit) return true; }
            else if (ApplyStaged(out bool handOff)) { if (handOff) return true; }
        }
        catch (Exception e) { Log("boot error " + e); BootNote = "更新切换出错：" + e.Message; }
        LoadPatches();
        ReadLocal();
        Log($"ready release={LocalRelease} root={install}");
        return false;
    }

    // the main menu is up: this version starts fine
    public static void BootOk()
    {
        if (!Enabled) return;
        var f = Path.Combine(Updates, "boot-pending.json");
        if (!File.Exists(f)) return;
        try
        {
            var p = JsonNode.Parse(File.ReadAllText(f))!;
            string r = (string?)p["release"] ?? "";
            File.Delete(f);
            BootNote = "已更新到 " + r;
            Log("boot ok " + r);
            // tidy: the staging copy is used up; keep the two newest recovery copies (to roll back), drop older ones
            try { if (Directory.Exists(Staging(r))) Directory.Delete(Staging(r), true); } catch { }
            var rec = Path.Combine(Updates, "recovery");
            if (Directory.Exists(rec))
                foreach (var d in new DirectoryInfo(rec).GetDirectories().OrderByDescending(d => d.CreationTimeUtc).Skip(2))
                    try { d.Delete(true); } catch { }
        }
        catch (Exception e) { Log("boot-ok error " + e.Message); }
    }

    static void WaitExit(int pid)
    {
        try { using var p = System.Diagnostics.Process.GetProcessById(pid); p.WaitForExit(15000); } catch { }
    }

    static string ReadCore(string appRoot)
    {
        try
        {
            using var cj = JsonDocument.Parse(File.ReadAllText(Path.Combine(appRoot, "runtime", "core.json")));
            return cj.RootElement.TryGetProperty("sourceId", out var s) ? s.GetString() ?? "" : "";
        }
        catch { return ""; }
    }

    static void ReadLocal()
    {
        try
        {
            if (!File.Exists(VersionFile)) { LocalRelease = ""; return; }
            var v = JsonNode.Parse(File.ReadAllText(VersionFile))!;
            LocalRelease = (string?)v["release"] ?? "";
        }
        catch { LocalRelease = ""; }
    }

    // the patch packs over the base pack, in name order (0001.pck, 0002.pck …): what changed in res:// since the base
    static void LoadPatches()
    {
        var dir = Path.Combine(meta, "patches");
        if (!Directory.Exists(dir)) return;
        foreach (var p in Directory.GetFiles(dir, "*.pck").OrderBy(p => Path.GetFileName(p), StringComparer.Ordinal))
        {
            bool ok = ProjectSettings.LoadResourcePack(p, true);
            Log("patch " + Path.GetFileName(p) + (ok ? " loaded" : " FAILED"));
        }
    }

    // the helper finished (switch or roll back) before this start: what came of it
    static bool HelperResult()
    {
        var f = Path.Combine(Updates, "helper-result.json");
        if (!File.Exists(f)) return false;
        try
        {
            var r = JsonNode.Parse(File.ReadAllText(f))!;
            bool ok = (bool?)r["ok"] ?? false; string mode = (string?)r["mode"] ?? ""; string rel = (string?)r["release"] ?? "";
            File.Delete(f);
            Log($"helper {mode} {rel} ok={ok} {(string?)r["error"]}");
            if (mode == "apply" && !ok)
            {
                File.Delete(Path.Combine(Updates, "boot-pending.json"));
                BootNote = "更新没能装上（" + ((string?)r["error"] ?? "") + "），游戏保持原版本";
                return false;
            }
            if (mode == "rollback") { File.Delete(Path.Combine(Updates, "boot-pending.json")); BootNote = ok ? "新版本 " + rel + " 启动失败，已退回原版本" : "退回原版本失败：" + (string?)r["error"]; MarkBad(rel); }
        }
        catch (Exception e) { Log("helper result unreadable " + e.Message); }
        return false;
    }

    // a release switched in but not yet confirmed by a start that reached the menu: count this start; the third start of
    // one that never got there moves it back out
    static bool BootPending(out bool quit)
    {
        quit = false;
        var f = Path.Combine(Updates, "boot-pending.json");
        if (!File.Exists(f)) return false;
        var p = JsonNode.Parse(File.ReadAllText(f))!.AsObject();
        int tries = ((int?)p["attempts"] ?? 0) + 1;
        string r = (string?)p["release"] ?? "";
        if (tries <= 2 && !OS.GetCmdlineUserArgs().Contains("--qa-force-rollback"))
        {
            p["attempts"] = tries; File.WriteAllText(f, p.ToJsonString());
            Log($"boot of {r}, attempt {tries}");
            return true;
        }
        Log("rolling back " + r);
        var ops = ReadPlan(Path.Combine(Recovery(r), "plan.json"));
        var back = ops.AsEnumerable().Reverse().Select(o => (o.To, o.From)).ToList();
        if (back.Any(o => NeedsHelperPath(o.Item1)))
        {
            quit = LaunchHelper("rollback", r, back, relaunch: true);
            if (quit) return true;
        }
        if (Execute(back, out string err)) { BootNote = "新版本 " + r + " 启动失败，已退回原版本"; MarkBad(r); }
        else BootNote = "退回原版本失败：" + err;
        File.Delete(f);
        return true;
    }

    static void MarkBad(string release)
    {
        try { File.WriteAllText(Path.Combine(Updates, "bad-release.json"), new JsonObject { ["release"] = release }.ToJsonString()); } catch { }
        try { var ready = Path.Combine(Staging(release), "ready.json"); if (File.Exists(ready)) File.Move(ready, ready + ".failed", true); } catch { }
    }

    static string BadRelease()
    {
        try { return (string?)JsonNode.Parse(File.ReadAllText(Path.Combine(Updates, "bad-release.json")))?["release"] ?? ""; } catch { return ""; }
    }

    // ─────────────────────────── switching ───────────────────────────

    record Op(string From, string To);   // install-relative paths

    static bool NeedsHelperPath(string rel) => LayerOf(rel) != "data";
    static string LayerOf(string rel)
    {
        if (platform == "android") return "data";
        if (!rel.Contains('/') && (rel.EndsWith(".exe") || rel.EndsWith(".pck"))) return "engine";
        if (rel.StartsWith("data_")) return "code";
        if (rel.StartsWith("game/runtime/")) return "core";
        return "data";
    }

    static string? ReadyRelease()
    {
        var dir = Path.Combine(Updates, "staging");
        if (!Directory.Exists(dir)) return null;
        foreach (var d in Directory.GetDirectories(dir))
            if (File.Exists(Path.Combine(d, "ready.json"))) return Path.GetFileName(d);
        return null;
    }

    // the moves that put a staged release in place: each replaced (or dropped) file out to recovery first, then the new one in
    static List<Op> PlanFor(string r)
    {
        var ready = JsonNode.Parse(File.ReadAllText(Path.Combine(Staging(r), "ready.json")))!;
        var ops = new List<Op>();
        string stage = Rel(Path.Combine(Staging(r), "files")), rec = Rel(Path.Combine(Recovery(r), "files"));
        foreach (var f in ready["files"]!.AsArray())
        {
            string p = (string)f!;
            if (File.Exists(Abs(p))) ops.Add(new Op(p, rec + "/" + p));
            ops.Add(new Op(stage + "/" + p, p));
        }
        foreach (var f in ready["removed"]!.AsArray())
        {
            string p = (string)f!;
            if (File.Exists(Abs(p))) ops.Add(new Op(p, rec + "/" + p));
        }
        return ops;
    }

    static void WritePlan(string path, string mode, string release, IEnumerable<(string From, string To)> ops)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var arr = new JsonArray(); foreach (var (a, b) in ops) arr.Add(new JsonObject { ["from"] = a, ["to"] = b });
        File.WriteAllText(path, new JsonObject { ["root"] = install, ["mode"] = mode, ["release"] = release, ["ops"] = arr }.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }

    static List<Op> ReadPlan(string path)
    {
        var p = JsonNode.Parse(File.ReadAllText(path))!;
        return p["ops"]!.AsArray().Select(o => new Op((string)o!["from"]!, (string)o["to"]!)).ToList();
    }

    // moves in order; on any failure the moves already made go back, newest first
    static bool Execute(List<(string From, string To)> ops, out string error)
    {
        error = "";
        int i = 0;
        try
        {
            for (; i < ops.Count; i++)
            {
                string from = Abs(ops[i].From), to = Abs(ops[i].To);
                if (File.Exists(to)) throw new IOException("已存在：" + ops[i].To);
                Directory.CreateDirectory(Path.GetDirectoryName(to)!);
                File.Move(from, to);
            }
            return true;
        }
        catch (Exception e)
        {
            error = e.Message; Log("switch failed at " + i + ": " + e.Message);
            for (int j = i - 1; j >= 0; j--) try { File.Move(Abs(ops[j].To), Abs(ops[j].From)); } catch (Exception u) { Log("undo failed " + ops[j].To + " " + u.Message); }
            return false;
        }
    }

    // a staged release at start-up: data only → switched here, now; anything else → the helper (returns handOff)
    static bool ApplyStaged(out bool handOff)
    {
        handOff = false;
        var r = ReadyRelease();
        if (r is null) return false;
        var ops = PlanFor(r);
        var pairs = ops.Select(o => (o.From, o.To)).ToList();
        WritePlan(Path.Combine(Recovery(r), "plan.json"), "apply", r, pairs);
        if (ops.Any(o => NeedsHelperPath(o.To) || NeedsHelperPath(o.From)))
        {
            handOff = LaunchHelper("apply", r, pairs, relaunch: true);
            if (handOff) return true;
            BootNote = "更新需要的更新助手不可用，游戏保持原版本"; return true;
        }
        if (Execute(pairs, out string err))
        {
            File.Move(Path.Combine(Staging(r), "ready.json"), Path.Combine(Staging(r), "applied.json"), true);
            WritePending(r, 1);   // this start is the new version's first
            Log("switched to " + r + " (" + ops.Count + " moves)");
        }
        else BootNote = "更新没能装上（" + err + "），游戏保持原版本";
        return true;
    }

    static void WritePending(string r, int attempts) =>
        File.WriteAllText(Path.Combine(Updates, "boot-pending.json"), new JsonObject { ["release"] = r, ["previous"] = LocalReleaseOnDisk(), ["attempts"] = attempts }.ToJsonString());

    static string LocalReleaseOnDisk() { ReadLocal(); return LocalRelease; }

    // the helper: a PowerShell script beside the game (Windows has it everywhere). Copied out first, as the update may
    // replace the script itself. It waits for this process to end, makes the moves (undoing them on failure), writes
    // helper-result.json and starts the game again.
    static bool LaunchHelper(string mode, string release, List<(string From, string To)> ops, bool relaunch)
    {
        var script = Path.Combine(meta, "updater", "apply-update.ps1");
        if (!File.Exists(script)) { Log("no helper at " + script); return false; }
        var run = Path.Combine(Updates, "helper-run.ps1");
        File.Copy(script, run, true);
        var plan = Path.Combine(Updates, "helper-plan.json");
        WritePlan(plan, mode, release, ops);
        if (mode == "apply") WritePending(release, 0);   // the helper's new start is the first
        var (exe, argLine) = relaunch && !OS.GetCmdlineUserArgs().Contains("--qa-no-relaunch") ? Relaunch() : ("", "");
        var psi = new System.Diagnostics.ProcessStartInfo("powershell.exe") { UseShellExecute = false, CreateNoWindow = true };
        foreach (var a in new[] { "-NoProfile", "-ExecutionPolicy", "Bypass", "-WindowStyle", "Hidden", "-File", run, "-Plan", plan,
                     "-Result", Path.Combine(Updates, "helper-result.json"), "-WaitPid", OS.GetProcessId().ToString(), "-Exe", exe, "-ArgsLine", argLine })
            psi.ArgumentList.Add(a);
        try { System.Diagnostics.Process.Start(psi); Log($"helper started ({mode} {release}, {ops.Count} moves)"); return true; }
        catch (Exception e) { Log("helper failed to start " + e.Message); if (mode == "apply") File.Delete(Path.Combine(Updates, "boot-pending.json")); return false; }
    }

    // how this game is started again: the same program and engine arguments; the player's own arguments minus the one-run
    // ones; and --after-pid so the new start waits for this one to be gone
    static (string Exe, string Args) Relaunch()
    {
        var user = OS.GetCmdlineUserArgs().Where(a => !a.StartsWith("--after-pid=") && !a.StartsWith("--script=") && !a.StartsWith("--capture") && a != "--quit-after-capture" && !a.StartsWith("--qa-next-script=") && a != "--qa-force-rollback").ToList();
        var next = OS.GetCmdlineUserArgs().FirstOrDefault(a => a.StartsWith("--qa-next-script="));
        if (next is not null) user.Add("--script=" + next[17..]);
        user.Add("--after-pid=" + OS.GetProcessId());
        var all = OS.GetCmdlineArgs().ToList(); all.Add("--"); all.AddRange(user);
        return (OS.GetExecutablePath(), string.Join(" ", all.Select(Quote)));
    }

    static string Quote(string a) => a.Length > 0 && !a.Any(c => c == ' ' || c == '"') ? a : "\"" + a.Replace("\"", "\\\"") + "\"";

    // 现在重启: the helper for engine/code/core, otherwise a plain new start that switches the data files itself
    public static void RestartNow()
    {
        var r = ReadyRelease();
        if (r is null) return;
        // Android cannot start itself again: the game closes, and the files switch when it is opened next (Boot)
        if (Android) { Log("quit for " + r + " (Android: switched at the next start)"); App.I.GetTree().Quit(); return; }
        var ops = PlanFor(r).Select(o => (o.From, o.To)).ToList();
        if (ops.Any(o => NeedsHelperPath(o.From) || NeedsHelperPath(o.To)))
        {
            WritePlan(Path.Combine(Recovery(r), "plan.json"), "apply", r, ops);
            if (!LaunchHelper("apply", r, ops, relaunch: true)) { App.I.Toast("更新助手启动失败，请关闭游戏后重新打开", true); return; }
        }
        else if (!OS.GetCmdlineUserArgs().Contains("--qa-no-relaunch"))
        {
            var (exe, line) = Relaunch();
            var psi = new System.Diagnostics.ProcessStartInfo(exe, line) { UseShellExecute = false };
            try { System.Diagnostics.Process.Start(psi); } catch (Exception e) { App.I.Toast("重新启动失败：" + e.Message, true); return; }
        }
        Log("restart for " + r);
        App.I.GetTree().Quit();
    }

    // ─────────────────────────── check ───────────────────────────

    public static UpdateLine? Line
    {
        get
        {
            try
            {
                var f = Path.Combine(dataRoot, "update-line.json");
                if (!File.Exists(f)) return null;
                string id = (string?)JsonNode.Parse(File.ReadAllText(f))?["line"] ?? "";
                return UpdatePolicy.Lines.FirstOrDefault(l => l.Id == id);
            }
            catch { return null; }
        }
        set
        {
            if (value is null) return;
            File.WriteAllText(Path.Combine(dataRoot, "update-line.json"), new JsonObject { ["line"] = value.Id }.ToJsonString());
            Log("line " + value.Id);
        }
    }

    static string BaseOf(UpdateLine l) => baseOverride ?? l.Base;

    static readonly HttpClient http = MakeClient();
    static HttpClient MakeClient()
    {
        var h = new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = (msg, cert, chain, errors) =>
                errors == System.Net.Security.SslPolicyErrors.None || msg.RequestUri?.Host == UpdatePolicy.DomesticHost,
        };
        var c = new HttpClient(h) { Timeout = Timeout.InfiniteTimeSpan };
        c.DefaultRequestHeaders.UserAgent.ParseAdd("ArkhamUI-Updater/1");
        return c;
    }

    public static async Task Check()
    {
        if (!Enabled || Now is State.Checking or State.Downloading) return;
        var line = Line;
        if (line is null) return;
        Now = State.Checking; Error = ""; Notify();
        try
        {
            using var t = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var text = await http.GetStringAsync(BaseOf(line) + platform + "/manifest.json", t.Token);
            // a server with nothing published answers with its web page instead of a 404 (the domestic line, 11-23: the
            // player saw "'<' is an invalid start of a value")
            if (text.TrimStart().StartsWith('<')) { Now = State.Failed; Error = line.Label + "线路还没有发布更新，可在设置里换另一条线路"; Log("no manifest (web page) for " + platform + " on " + line.Id); Notify(); return; }
            var p = Verify(text);
            if ((string?)p["product"] != UpdatePolicy.Product || (string?)p["platform"] != platform) throw new InvalidDataException("清单不是这个游戏的");
            string rel = (string?)p["release"] ?? "";
            ReadLocal();
            Log($"manifest {rel} (local {LocalRelease})");
            if (LocalRelease.Length > 0 && VKey(rel).CompareTo(VKey(LocalRelease)) <= 0) { Now = State.UpToDate; Notify(); return; }
            if (rel == BadRelease()) { Now = State.Failed; Error = "新版本 " + rel + " 在这台电脑上启动失败过，已退回；请等下一个版本"; Notify(); return; }
            string min = (string?)p["minimumRelease"] ?? "";
            if (min.Length > 0 && LocalRelease.Length > 0 && VKey(LocalRelease).CompareTo(VKey(min)) < 0) { Now = State.TooOld; Error = "当前版本太旧，请下载完整安装包"; Notify(); return; }
            // Android: a newer APK (the program or the rules core changed) is installed whole; it brings its own data
            if (Android && p["apk"] is JsonObject apk && (string?)apk["release"] is { Length: > 0 } apkRel
                && (ApkRelease.Length == 0 || VKey(apkRel).CompareTo(VKey(ApkRelease)) > 0))
            {
                payload = p; needed = new(); removed = new();
                var apkNotes = p["releaseNotes"]?.AsArray().Select(n => (string?)n ?? "").ToArray() ?? Array.Empty<string>();
                Pending = new Offer(rel, apkNotes, (long?)apk["bytes"] ?? 0, 1, (string?)p["coreSourceId"] != LocalCore && LocalCore.Length > 0, (string?)p["saveCompatibility"] ?? "", false,
                    NeedsApk: true, ApkUrl: BaseOf(line) + (string?)apk["path"]);
                Now = State.Available;
                Log($"offer {rel}: new APK {apkRel} (installed {ApkRelease}) {Pending.Bytes} bytes");
                Notify(); return;
            }
            await Task.Run(() => Diff(p));
            payload = p;
            bool core = (string?)p["coreSourceId"] != LocalCore && LocalCore.Length > 0;
            var notes = p["releaseNotes"]?.AsArray().Select(n => (string?)n ?? "").ToArray() ?? Array.Empty<string>();
            Pending = new Offer(rel, notes, needed.Sum(f => (long)f!["bytes"]!), needed.Count, core, (string?)p["saveCompatibility"] ?? "", needed.Any(f => (string?)f!["layer"] != "data") || removed.Any(NeedsHelperPath));
            Now = ReadyRelease() == rel ? State.Ready : State.Available;
            Log($"offer {rel}: {needed.Count} files, {Pending.Bytes} bytes, removed {removed.Count}, helper {Pending.NeedsHelper}");
        }
        catch (HttpRequestException e) when (e.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            Now = State.UpToDate; Log("no manifest for " + platform + " on " + line.Id);   // nothing published for this platform yet
        }
        catch (Exception e)
        {
            Now = State.Failed; Error = e is TaskCanceledException or HttpRequestException ? "无法连接" + line.Label + "线路" : e.Message;
            Log("check failed: " + e.Message);
        }
        Notify();
    }

    static IComparable VKey(string r) => string.Join(".", r.Split('.').Select(x => int.TryParse(x, out int n) ? n.ToString("D6") : x));

    static JsonObject Verify(string text)
    {
        var doc = JsonNode.Parse(text)!.AsObject();
        if ((string?)doc["keyId"] != UpdatePolicy.KeyId) throw new InvalidDataException("清单签名钥匙不对");
        var data = Convert.FromBase64String((string)doc["payload"]!);
        var sig = Convert.FromBase64String((string)doc["signature"]!);
        using var rsa = RSA.Create(); rsa.ImportFromPem(UpdatePolicy.PublicKey);
        if (!rsa.VerifyData(data, sig, HashAlgorithmName.SHA256, RSASignaturePadding.Pss)) throw new InvalidDataException("清单签名校验失败");
        return JsonNode.Parse(Encoding.UTF8.GetString(data))!.AsObject();
    }

    // what differs: the installed files' hashes, from version.json's cache when size and time still match, else computed
    // (and the cache refreshed)
    static void Diff(JsonObject p)
    {
        JsonObject local;
        try { local = File.Exists(VersionFile) ? JsonNode.Parse(File.ReadAllText(VersionFile))!.AsObject() : new JsonObject(); } catch { local = new JsonObject(); }
        var cache = local["files"] as JsonObject ?? new JsonObject();
        bool dirty = false;
        needed = new(); removed = new();
        var wanted = new HashSet<string>();
        foreach (var f in p["files"]!.AsArray())
        {
            string path = (string)f!["path"]!; wanted.Add(path);
            string sha = LocalHash(path, cache, ref dirty);
            if (sha != (string?)f["sha256"]) needed.Add(f.DeepClone());
        }
        foreach (var kv in cache) if (!wanted.Contains(kv.Key) && File.Exists(Abs(kv.Key))) removed.Add(kv.Key);
        if (dirty) { local["files"] = cache; if (local["release"] is null) local["release"] = ""; SafeWrite(VersionFile, local.ToJsonString()); }
    }

    static string LocalHash(string path, JsonObject cache, ref bool dirty)
    {
        var full = Abs(path);
        if (!File.Exists(full)) return "";
        var info = new FileInfo(full);
        long mtime = new DateTimeOffset(info.LastWriteTimeUtc).ToUnixTimeMilliseconds();
        if (cache[path] is JsonObject c && (long?)c["bytes"] == info.Length && ((long?)c["mtime"] == mtime || Android) && (string?)c["sha256"] is { Length: 64 } known) return known;
        // (Android: the files are unpacked from the APK with new times; their size and the hash recorded at build are trusted,
        // so the first check does not read a gigabyte on the phone)
        string sha = HashFile(full);
        cache[path] = new JsonObject { ["bytes"] = info.Length, ["sha256"] = sha, ["mtime"] = mtime };
        dirty = true;
        return sha;
    }

    static string HashFile(string full)
    {
        using var s = File.OpenRead(full);
        return Convert.ToHexString(SHA256.HashData(s)).ToLowerInvariant();
    }

    static void SafeWrite(string path, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path + ".part", text);
        File.Move(path + ".part", path, true);
    }

    // ─────────────────────────── download ───────────────────────────

    public static async Task Download()
    {
        if (Now != State.Available || payload is null || Line is not { } line) return;
        // a new APK: the browser downloads it and the system installs it over this one (same signing key); the game keeps
        // nothing of it
        if (Pending is { NeedsApk: true } apkOffer)
        {
            Log("open APK " + apkOffer.ApkUrl);
            var err = App.I.QaRunning ? Godot.Error.Ok : OS.ShellOpen(apkOffer.ApkUrl);   // QA: logged, no browser
            if (App.I.QaRunning) GD.Print("QA_UPDATE_APK " + apkOffer.ApkUrl);
            if (err != Godot.Error.Ok) Error = "无法打开浏览器下载安装包（" + err + "），请到 " + apkOffer.ApkUrl + " 下载";
            Notify(); return;
        }
        string r = (string)payload["release"]!;
        Now = State.Downloading; DoneBytes = 0; Error = ""; Notify();
        cts = new CancellationTokenSource();
        try
        {
            string files = Path.Combine(Staging(r), "files");
            foreach (var f in needed)
            {
                string path = (string)f!["path"]!, sha = (string)f["sha256"]!; long bytes = (long)f["bytes"]!;
                string dst = Path.GetFullPath(Path.Combine(files, path));
                if (!dst.StartsWith(Path.GetFullPath(files) + Path.DirectorySeparatorChar) || path.Contains("..") || Path.IsPathRooted(path)) throw new InvalidDataException("清单里有不安全的路径：" + path);
                if (File.Exists(dst) && new FileInfo(dst).Length == bytes && await Task.Run(() => HashFile(dst)) == sha) { DoneBytes += bytes; Notify(); continue; }
                for (int attempt = 1; ; attempt++)
                {
                    long before = DoneBytes;
                    try { await Fetch(BaseOf(line) + "objects/" + sha[..2] + "/" + sha, dst, sha, cts.Token); break; }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception e) when (attempt < 2) { Log("retry " + path + ": " + e.Message); DoneBytes = before; }
                }
            }
            // the new version.json (installed hashes and times, so the next check need not hash everything again) and the
            // list of what to switch
            var local = File.Exists(VersionFile) ? JsonNode.Parse(File.ReadAllText(VersionFile))!.AsObject() : new JsonObject();
            var cache = local["files"] as JsonObject ?? new JsonObject();
            var fileMap = new JsonObject();
            var neededPaths = needed.Select(f => (string)f!["path"]!).ToHashSet();
            foreach (var f in payload["files"]!.AsArray())
            {
                string path = (string)f!["path"]!;
                long mtime = neededPaths.Contains(path) ? new DateTimeOffset(File.GetLastWriteTimeUtc(Path.Combine(files, path))).ToUnixTimeMilliseconds() : (long?)cache[path]?["mtime"] ?? 0;
                fileMap[path] = new JsonObject { ["bytes"] = (long)f["bytes"]!, ["sha256"] = (string)f["sha256"]!, ["mtime"] = mtime };
            }
            string vrel = Rel(VersionFile);
            SafeWrite(Path.Combine(files, vrel), new JsonObject { ["schemaVersion"] = 1, ["release"] = r, ["platform"] = platform, ["coreSourceId"] = (string?)payload["coreSourceId"], ["files"] = fileMap }.ToJsonString());
            var list = new JsonArray(); foreach (var p in neededPaths) list.Add(p); list.Add(vrel);
            var rem = new JsonArray(); foreach (var p in removed) rem.Add(p);
            SafeWrite(Path.Combine(Staging(r), "ready.json"), new JsonObject { ["release"] = r, ["files"] = list, ["removed"] = rem }.ToJsonString());
            Now = State.Ready;
            Log("staged " + r);
        }
        catch (OperationCanceledException) { Now = State.Available; Log("download cancelled"); }
        catch (Exception e) { Now = State.Available; Error = "下载失败：" + (e is HttpRequestException ? "无法连接" + line.Label + "线路" : e.Message); Log("download failed " + e); }
        Notify();
    }

    public static void CancelDownload() => cts?.Cancel();

    static long lastNote;
    static async Task Fetch(string url, string dst, string sha, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
        string part = dst + ".part";
        using (var headT = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            headT.CancelAfter(TimeSpan.FromSeconds(30));
            using var resp = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, headT.Token);
            resp.EnsureSuccessStatusCode();
            await using var src = await resp.Content.ReadAsStreamAsync(ct);
            await using var o = File.Create(part);
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buf = new byte[1 << 16]; int n;
            while ((n = await src.ReadAsync(buf, ct)) > 0)
            {
                await o.WriteAsync(buf.AsMemory(0, n), ct); hash.AppendData(buf, 0, n);
                DoneBytes += n;
                if (System.Environment.TickCount64 - lastNote > 150) { lastNote = System.Environment.TickCount64; Notify(); }
            }
            if (Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant() != sha) throw new InvalidDataException("文件校验失败");
        }
        File.Move(part, dst, true);
    }
}
