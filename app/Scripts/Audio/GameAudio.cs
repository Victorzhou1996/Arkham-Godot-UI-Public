using System.Text.Json;
using Godot;
using Gathering;
using FileIO = System.IO.File;
using PathIO = System.IO.Path;

namespace ArkhamUI;

// Music and sound effects (noncore line, 2026-11-19; plan: docs/NONCORE-PLAN-20261119.md).
// One node under App. Everything is optional: a missing file is silent (logged once), a machine without an audio device
// falls back to Godot's dummy driver, and no call here ever throws into the caller.
//  · two buses, "Music" and "Sfx", follow UserSettings.MusicVolume / EffectsVolume every frame (the settings dialog needs no hook)
//  · Cue(name): a short effect from the manifest (app/assets/audio/audio.json); a few variants, slight pitch spread, 60 ms debounce
//  · music: two players cross-fade; the same track carries on without restarting (0712 html rule); story pages duck it
//  · Observe(model): the table hands over each new state; the scenario picks the music and a few changes (doom, damage,
//    horror, cards drawn, act / agenda advancing, the turn passing) play their effect
// Files are looked up in <data root>/audio/<sfx|music>/ first (the player's own, overrides), then res://assets/audio/<sfx|music>/.
public partial class GameAudio : Node
{
    public static GameAudio? I { get; private set; }
    const string MusicBus = "Music", SfxBus = "Sfx";
    const float MusicBaseDb = -6f, SfxBaseDb = -4f, Silent = -60f;
    const double FadeOut = 1.0, FadeIn = 1.5, DuckTime = 0.6;
    const float DuckDb = -8f;

    readonly Dictionary<string, string[]> cues = new(), music = new();
    readonly Dictionary<string, string> titles = new();
    readonly Dictionary<string, AudioStream?> streams = new();
    readonly Dictionary<string, string?> found = new();
    readonly Dictionary<string, ulong> lastCue = new();
    readonly HashSet<string> missing = new();
    readonly List<AudioStreamPlayer> pool = new();
    readonly RandomNumberGenerator rng = new();
    AudioStreamPlayer musicA = null!, musicB = null!;
    AudioStreamPlayer? musicNow;
    readonly Dictionary<AudioStreamPlayer, Tween> fades = new();
    string musicFile = "", musicContext = "";
    float duck; bool ducked; Tween? duckTween;
    double appliedMusic = -1, appliedFx = -1, poll;
    bool verbose; int cuesPlayed; string lastPlayed = "";

    // App boot (via Sfx.Attach). Safe to call twice.
    public static void Attach(Node root)
    {
        if (I is not null && IsInstanceValid(I)) return;
        FrameProbe.AttachIfAsked(root);   // QA only (--frame-probe)
        root.AddChild(new GameAudio { Name = "GameAudio" });
    }
    public static void Cue(string name) { if (Off) return; try { I?.PlayCue(name); } catch (Exception e) { GD.PrintErr("AUDIO_CUE_FAIL " + name + " " + e.Message); } }
    public static void Observe(TableModel m)
    {
        if (Off || I is null) return;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try { I.OnTable(m); } catch (Exception e) { GD.PrintErr("AUDIO_OBSERVE_FAIL " + e.Message); }
        double ms = sw.Elapsed.TotalMilliseconds; if (ms > 5) FrameProbe.Mark($"observe{ms:0}ms"); I.observeCalls++; I.observeMs += ms; I.observeMax = Math.Max(I.observeMax, ms);
    }
    // The first context that has a file plays; none → the music fades out.
    public static void Music(params string[] contexts) { if (Off) return; try { I?.SetMusic(contexts); } catch (Exception e) { GD.PrintErr("AUDIO_MUSIC_FAIL " + e.Message); } }

    // Offscreen QA runs (--background, often several games at once for the AI benches) cannot be heard: no audio at all there,
    // so no MP3 is read or decoded. --audio-qa turns it back on for audio tests; --no-audio / ARKHAM_NO_AUDIO=1 turn it off anywhere.
    public static bool Off { get; private set; }
    int observeCalls; double observeMs, observeMax, lastLoadMs; int musicToken;
    // QA comparison only: --audio-sync behaves as before 11-19 13:00 (music read on the main thread, no effect preload)
    bool syncLoad;

    public override void _Ready()
    {
        I = this; ProcessMode = ProcessModeEnum.Always;
        var args = OS.GetCmdlineUserArgs();
        Off = (args.Contains("--background") && !args.Contains("--audio-qa")) || args.Contains("--no-audio") || System.Environment.GetEnvironmentVariable("ARKHAM_NO_AUDIO") == "1";
        if (Off) { SetProcess(false); GD.Print("AUDIO_READY off (offscreen QA or --no-audio)"); return; }
        syncLoad = args.Contains("--audio-sync");
        Bus(MusicBus); Bus(SfxBus);
        LoadManifest();
        for (int i = 0; i < 8; i++) { var p = new AudioStreamPlayer { Bus = SfxBus, VolumeDb = SfxBaseDb }; AddChild(p); pool.Add(p); }
        musicA = new AudioStreamPlayer { Bus = MusicBus, VolumeDb = Silent }; musicB = new AudioStreamPlayer { Bus = MusicBus, VolumeDb = Silent };
        AddChild(musicA); AddChild(musicB);
        musicA.Finished += () => TrackEnded(musicA); musicB.Finished += () => TrackEnded(musicB);
        LoadPrefs();
        NarrationReady();
        RegisterQa();
        // the short effects are decoded once, off the main thread, so the first click / draw does not wait for its file
        var effects = cues.Values.SelectMany(f => f).Distinct().Select(f => Find("sfx", f)).Where(p => p is not null).Select(p => p!).Distinct().ToArray();
        if (!syncLoad) Task.Run(() => { foreach (var p in effects) Load(p, false); });
        GD.Print($"AUDIO_READY device={AudioServer.OutputDevice} rate={AudioServer.GetMixRate()} cues={cues.Count} music={music.Count}");
    }

    public override void _Process(double delta)
    {
        var s = App.I?.S?.Settings;
        double mv = previewMusic ?? s?.MusicVolume ?? 0.6, fv = previewFx ?? s?.EffectsVolume ?? 0.8;
        if (mv != appliedMusic) { appliedMusic = mv; ApplyMusicBus(); }
        if (fv != appliedFx) { appliedFx = fv; SetBus(SfxBus, fv, 0); }
        poll += delta; if (poll < 0.5) return; poll = 0;
        // Off the table (menu, workshop, preparation, campaign pages) the menu music plays; the table sets its own via Observe.
        if (App.I?.Current is { } screen && screen is not TableScreen) { lastGame = Guid.Empty; Duck(false); if (narrSig.Length > 0) DoStopNarration(0.5); Want("menu", "主菜单", "menu"); }
    }

    // ---------- buses and volume ----------
    static int Bus(string name)
    {
        int i = AudioServer.GetBusIndex(name); if (i >= 0) return i;
        AudioServer.AddBus(); i = AudioServer.BusCount - 1;
        AudioServer.SetBusName(i, name); AudioServer.SetBusSend(i, "Master");
        return i;
    }
    // The slider is a loudness, not an amplitude: squared, so half way sounds about half as loud (≈ -12 dB). 0 mutes.
    static void SetBus(string name, double value, float extraDb)
    {
        int i = Bus(name); value = Math.Clamp(value, 0, 1);
        AudioServer.SetBusMute(i, value <= 0.001);
        AudioServer.SetBusVolumeDb(i, (float)Mathf.LinearToDb(Math.Max(value * value, 0.000001)) + extraDb);
    }
    void ApplyMusicBus() => SetBus(MusicBus, appliedMusic < 0 ? 0.6 : appliedMusic, duck + voiceDuck);   // story page, and further while the voice reads
    void Duck(bool on)
    {
        if (on == ducked) return; ducked = on; FrameProbe.Mark(on ? "duck" : "unduck");
        duckTween?.Kill();
        duckTween = CreateTween();
        duckTween.TweenMethod(Callable.From<float>(v => { duck = v; ApplyMusicBus(); }), duck, on ? DuckDb : 0f, DuckTime);
    }

    // ---------- manifest and files ----------
    void LoadManifest()
    {
        try
        {
            var bytes = Godot.FileAccess.GetFileAsBytes("res://assets/audio/audio.json");
            if (bytes.Length == 0) { GD.PrintErr("AUDIO_MANIFEST_MISSING"); return; }
            using var doc = JsonDocument.Parse(bytes);
            void Read(string key, Dictionary<string, string[]> into)
            {
                if (!doc.RootElement.TryGetProperty(key, out var o) || o.ValueKind != JsonValueKind.Object) return;
                foreach (var p in o.EnumerateObject())
                    into[p.Name] = p.Value.ValueKind == JsonValueKind.Array ? p.Value.EnumerateArray().Select(x => x.GetString() ?? "").Where(x => x.Length > 0).ToArray() : new[] { p.Value.GetString() ?? "" };
            }
            Read("cues", cues); Read("music", music);
            if (doc.RootElement.TryGetProperty("titles", out var t) && t.ValueKind == JsonValueKind.Object)
                foreach (var p in t.EnumerateObject()) titles[p.Name] = p.Value.GetString() ?? "";
        }
        catch (Exception e) { GD.PrintErr("AUDIO_MANIFEST_FAIL " + e.Message); }
    }

    // The player's own folder first, then the shipped assets. Remembered for the session.
    string? Find(string kind, string file)
    {
        string key = kind + "/" + file;
        if (found.TryGetValue(key, out var hit)) return hit;
        string? path = null;
        var data = App.I?.DataRoot ?? "";
        if (data.Length > 0)
        {
            var own = PathIO.Combine(data, "audio", kind, file);
            if (FileIO.Exists(own)) path = own;
        }
        if (path is null)
        {
            var res = "res://assets/audio/" + kind + "/" + file;
            if (ResourceLoader.Exists(res) || Godot.FileAccess.FileExists(res)) path = res;
        }
        found[key] = path; return path;
    }

    // Imported resources load normally; raw files (not imported, or the player's own) are decoded from their bytes.
    AudioStream? Load(string path, bool loop)
    {
        string key = (loop ? "L|" : "S|") + path;
        lock (streams) if (streams.TryGetValue(key, out var cached)) return cached;   // also called from worker threads
        AudioStream? s = null;
        try
        {
            if (path.StartsWith("res://") && ResourceLoader.Exists(path)) s = GD.Load<AudioStream>(path)?.Duplicate() as AudioStream;
            else
            {
                var bytes = Godot.FileAccess.GetFileAsBytes(path);
                if (bytes.Length > 0)
                    s = PathIO.GetExtension(path).ToLowerInvariant() switch
                    {
                        ".ogg" => AudioStreamOggVorbis.LoadFromBuffer(bytes),
                        ".mp3" => new AudioStreamMP3 { Data = bytes },
                        ".wav" => AudioStreamWav.LoadFromBuffer(bytes),
                        _ => null
                    };
            }
            switch (s)
            {
                case AudioStreamOggVorbis o: o.Loop = loop; break;
                case AudioStreamMP3 m: m.Loop = loop; break;
                case AudioStreamWav w: w.LoopMode = loop ? AudioStreamWav.LoopModeEnum.Forward : AudioStreamWav.LoopModeEnum.Disabled; if (loop) w.LoopEnd = (int)(w.GetLength() * w.MixRate); break;
            }
            if (s is null) GD.PrintErr("AUDIO_LOAD_EMPTY " + path);
        }
        catch (Exception e) { GD.PrintErr("AUDIO_LOAD_FAIL " + path + " " + e.Message); s = null; }
        lock (streams) streams[key] = s;
        return s;
    }
    bool Cached(string path, bool loop, out AudioStream? s) { lock (streams) return streams.TryGetValue((loop ? "L|" : "S|") + path, out s); }

    void Missing(string what) { if (missing.Add(what)) GD.Print("AUDIO_MISSING " + what); }

    // ---------- effects ----------
    // a cue that shares a file with another one plays at its own pitch, so the two stay apart: the encounter draw uses the
    // phase bell, lower and darker (Yunke 11-21 #13: "翻遭遇牌" 换成低沉钟鸣，不要开门吱呀声)
    static readonly Dictionary<string, float> CuePitch = new() { ["encounter"] = 0.72f };
    string? PlayCue(string name)
    {
        if (!cues.TryGetValue(name, out var files)) { Missing("cue=" + name + " (not in manifest)"); return null; }
        ulong now = Time.GetTicksMsec();
        if (lastCue.TryGetValue(name, out var t) && now - t < 60) return null;
        lastCue[name] = now;
        var have = files.Select(f => Find("sfx", f)).Where(p => p is not null).Select(p => p!).ToArray();
        if (have.Length == 0) { Missing("cue=" + name); return null; }
        var path = have[have.Length == 1 ? 0 : rng.RandiRange(0, have.Length - 1)];
        bool first = !Cached(path, false, out _);
        var stream = Load(path, false); if (stream is null) return null;
        FrameProbe.Mark("cue:" + name + (first ? "(first-load)" : ""));
        var player = pool.FirstOrDefault(p => !p.Playing) ?? pool.OrderByDescending(p => p.GetPlaybackPosition()).First();   // steal the oldest
        player.Stream = stream; player.PitchScale = (CuePitch.TryGetValue(name, out var pitch) ? pitch : 1f) + rng.RandfRange(-0.03f, 0.03f); player.Play();
        cuesPlayed++; lastPlayed = name;
        if (verbose) GD.Print("AUDIO_CUE " + name + " " + path);
        return path;
    }

    // ---------- music ----------
    // QA / old entry: the contexts are tried in order; the first one is also the place ("场合") a chosen track is kept for.
    void SetMusic(params string[] contexts) => Want(contexts.FirstOrDefault(c => c.Length > 0) ?? "", "", contexts);
    // The default track of a place: the first context that has a file.
    (string? File, string Ctx) Default(string[] contexts)
    {
        foreach (var c in contexts)
        {
            if (!music.TryGetValue(c, out var list)) continue;
            var file = list.Select(f => Find("music", f)).FirstOrDefault(p => p is not null);
            if (file is not null) return (file, c);
        }
        foreach (var c in contexts) if (music.ContainsKey(c)) Missing("music=" + c);
        return (null, "");
    }
    // Plays what is wanted now, unless the player paused / stopped the music (then it waits for Play).
    void Apply()
    {
        if (paused) return;
        string ctx = wantedCtx; string? file = wantedFile.Length > 0 ? wantedFile : null;
        if ((file ?? "") == musicFile) { if (file is not null) musicContext = ctx; return; }   // the same track carries on
        musicFile = file ?? ""; musicContext = ctx; int token = ++musicToken;
        if (file is null) { StartMusic(null, ctx, null); return; }
        if (Cached(file, true, out var ready)) { StartMusic(ready, ctx, file); return; }
        if (syncLoad) { FrameProbe.Mark("music-load-main"); var sw0 = System.Diagnostics.Stopwatch.StartNew(); var s0 = Load(file, true); lastLoadMs = sw0.Elapsed.TotalMilliseconds; StartMusic(s0, ctx, file); return; }
        // A track is a few MB: read and opened on a worker thread, so switching music never stalls a frame; a newer request wins.
        Task.Run(() =>
        {
            FrameProbe.Mark("music-load-worker");
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var s = Load(file, true);
            double ms = sw.Elapsed.TotalMilliseconds;
            Callable.From(() => { lastLoadMs = ms; if (token == musicToken && IsInstanceValid(this)) StartMusic(s, ctx, file); }).CallDeferred();
        });
    }
    void StartMusic(AudioStream? stream, string ctx, string? file)
    {
        FrameProbe.Mark(stream is null ? "music-fadeout" : "music-crossfade");
        var old = musicNow;
        if (old is not null) Fade(old, Silent, FadeOut, stopAfter: true);
        if (stream is null) { musicNow = null; Changed?.Invoke(); GD.Print("AUDIO_MUSIC off"); return; }
        var next = old == musicA ? musicB : musicA;
        if (fades.TryGetValue(next, out var tw)) tw.Kill();
        LoopStream(stream, ModeNow == PlayMode.One && !ctx.StartsWith("chapter:"));   // only 单曲循环 repeats the stream; the other modes (and a chapter's opening track) move on when it ends
        next.Stop(); next.Stream = stream; next.VolumeDb = Silent; next.StreamPaused = false; next.Play();
        Fade(next, MusicBaseDb, FadeIn, stopAfter: false);
        musicNow = next;
        Changed?.Invoke();
        GD.Print("AUDIO_MUSIC " + ctx + " " + PathIO.GetFileName(file) + (titles.TryGetValue(PathIO.GetFileName(file!), out var title) ? " (" + title + ")" : "") + $" load={lastLoadMs:0}ms(worker)");
    }
    void Fade(AudioStreamPlayer p, float to, double time, bool stopAfter)
    {
        if (fades.TryGetValue(p, out var old)) old.Kill();
        var tw = CreateTween();
        tw.TweenProperty(p, "volume_db", to, time).SetTrans(Tween.TransitionType.Sine).SetEase(to > p.VolumeDb ? Tween.EaseType.Out : Tween.EaseType.In);
        if (stopAfter) tw.TweenCallback(Callable.From(p.Stop));
        fades[p] = tw;
    }

    // ---------- the table ----------
    Guid lastGame; string lastMe = "", lastAct = "", lastAgenda = "", lastTurn = "";
    int lastDoom = -1, lastHand = -1;
    readonly Dictionary<string, (int Damage, int Horror)> lastWounds = new();

    void OnTable(TableModel m)
    {
        // music: the scenario's own track → the campaign theme → a general table track; the end of a scenario its ending
        string scen = J.String(m.Scenario, "id"); scen = scen.Length > 0 ? CoreCatalog.Normalize(scen) : "";
        string camp = J.String(J.Get(J.Get(m.Game, "mode"), "This"), "id");
        if (m.IsOver) Want("__ending__", "剧本结束", J.String(J.Get(m.Game, "outcome"), "result") == "Won" ? "ending-won" : "ending-lost");
        else if (scen.Length > 0)
        {
            // Yunke 11-22: a chapter entered (a new scenario of this game, or the game opened in it) starts with its own track
            if (chapterSeen != m.State.Id + "/" + scen) { chapterSeen = m.State.Id + "/" + scen; EnterChapter(scen); }
            Want(scen, ChapterName(scen), "scenario:" + scen, camp.Length > 0 ? "campaign:" + camp : "", "table");
        }
        else Want("__interlude__", "战役幕间", camp.Length > 0 ? "campaign:" + camp : "", "table");
        Duck(m.QuestionTag == "Read");   // story pages: the music steps back while the text is read
        NarrationFollow(m);

        // effects for what changed since the last state of this game (a new game / view only takes the snapshot)
        string me = m.Me?.Id ?? "";
        bool fresh = m.State.Id != lastGame; lastGame = m.State.Id;
        int doom = m.TotalDoom;
        string act = m.Act?.Code ?? "", agenda = m.Agenda?.Code ?? "";
        string turn = J.String(m.Game, "turnPlayerInvestigatorId"); if (turn.Length == 0) turn = J.String(m.Game, "activeInvestigatorId");
        int hand = m.Me is null ? -1 : m.Hand(m.Me).Count();
        var wounds = m.Investigators.ToDictionary(i => i.Id, i => (m.Damage(i), m.Horror(i)));
        if (!fresh && !m.IsOver)
        {
            if (lastDoom >= 0 && doom > lastDoom) Cue("doom");
            bool hurt = false, scared = false;
            foreach (var (id, w) in wounds)
                if (lastWounds.TryGetValue(id, out var was)) { hurt |= w.Item1 > was.Damage; scared |= w.Item2 > was.Horror; }
            if (hurt) Cue("damage");
            if (scared) Cue("horror");
            if (lastAct.Length > 0 && act.Length > 0 && act != lastAct) Cue("act");
            if (lastAgenda.Length > 0 && agenda.Length > 0 && agenda != lastAgenda) Cue("agenda");
            if (me == lastMe && lastHand >= 0 && hand > lastHand) Cue("draw");
            if (wounds.Count > 1 && lastTurn.Length > 0 && turn.Length > 0 && turn != lastTurn && m.Phase == "InvestigationPhase") Cue("turn");
        }
        lastDoom = doom; lastAct = act; lastAgenda = agenda; lastTurn = turn; lastHand = hand; lastMe = me;
        lastWounds.Clear(); foreach (var (id, w) in wounds) lastWounds[id] = w;
    }

    // ---------- QA (offscreen runs) ----------
    void RegisterQa()
    {
        QaScript.Register("audio-status", _ => Status());
        QaScript.Register("audio-cue", a => GD.Print("AUDIO_QA_CUE " + a + " -> " + (PlayCueForced(a) ?? "none")));
        QaScript.Register("audio-music", a => { SetMusic(a.Split(',', StringSplitOptions.RemoveEmptyEntries)); Status(); });
        QaScript.Register("audio-volume", a =>
        {
            var v = a.Split(','); var s = App.I?.S?.Settings; if (s is null || v.Length < 2) return;
            s.MusicVolume = double.Parse(v[0], System.Globalization.CultureInfo.InvariantCulture);
            s.EffectsVolume = double.Parse(v[1], System.Globalization.CultureInfo.InvariantCulture);   // not saved; the next frame applies it
        });
        QaScript.Register("audio-log", a => verbose = a != "off");
    }
    string? PlayCueForced(string name) { lastCue.Remove(name); return PlayCue(name); }
    void Status()
    {
        string BusText(string n) { int i = AudioServer.GetBusIndex(n); return i < 0 ? n + "=none" : $"{n}={AudioServer.GetBusVolumeDb(i):0.0}dB{(AudioServer.IsBusMute(i) ? "(mute)" : "")}"; }
        var p = musicNow;
        GD.Print($"AUDIO_STATUS {BusText(MusicBus)} {BusText(SfxBus)} {BusText("Voice")} music={(musicContext.Length > 0 ? musicContext : "-")} file={(musicFile.Length > 0 ? PathIO.GetFileName(musicFile) : "-")} " +
                 $"playing={(p?.Playing ?? false)} pos={(p?.GetPlaybackPosition() ?? 0):0.00} vol={(p?.VolumeDb ?? Silent):0.0} duck={duck:0.0} cues={cuesPlayed} last={lastPlayed} busy={pool.Count(x => x.Playing)} missing={missing.Count} " +
                 $"observe={observeCalls}x avg={(observeCalls > 0 ? observeMs / observeCalls : 0):0.000}ms max={observeMax:0.000}ms lastLoad={lastLoadMs:0}ms(worker)");
    }
}
