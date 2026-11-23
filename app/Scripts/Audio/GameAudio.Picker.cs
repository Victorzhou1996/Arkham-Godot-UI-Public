using System.Text.Json;
using Godot;
using PathIO = System.IO.Path;

namespace ArkhamUI;

// Choosing music (Yunke 11-20: "可以切歌或者选歌吗，我记得html版里有这个功能"; "设置里修改音乐时要随修改即时预览变化").
// As in the 0712 html version (MusicControls.vue / bgm.ts): every place ("场合") has its default track and the player may pick
// another one for it — the main menu, each scenario (by its code), the campaign's between-chapter pages, the end of a scenario.
// The choice is remembered (<data root>/music.json: per-place overrides, autoplay, loop). Play / pause / stop, previous / next
// (next = the following track in the html list, kept as this place's choice), and a live volume preview for the settings dialog.
// Yunke 11-22: the list runs campaign by campaign, chapter by chapter (each campaign's theme first, then its scenarios in
// chapter order, then any other track by name), and "循环" became a play mode — 顺序 / 单曲循环 / 剧本循环 / 全部循环 — with a
// separate 随机 switch; both are kept in music.json. (Before: "循环" on = the one track repeated, off = it played once and the
// music stopped; nothing ever moved on to another track by itself.)
// Yunke 11-22 "每次进入新的章节时，应该默认切到这一章对应的音乐（如有），播放结束后继续之前的播放设置": entering a chapter
// plays that chapter's own track once from the start (above any choice kept for it, also under 单曲循环, also after 顺序 ran
// out — but not when the player paused / stopped or turned 自动播放 off); when it ends the play mode carries on as before:
// 单曲循环 goes back to the track that was looping before the chapter, 顺序 / 剧本循环 / 全部循环 (with or without 随机) move
// on from the chapter's track. A chapter with no track of its own keeps the place rules below. 上一首 / 下一首 now only
// move the music for this session (they used to be kept as the place's choice, which then hid the chapter's own track).
// UI: MusicControls / MusicPanel (MusicPanel.cs). A table entry only needs MusicPanel.Open() (or GameAudio.Next()).
public partial class GameAudio
{
    public sealed class MusicPrefs
    {
        public Dictionary<string, string> Overrides { get; set; } = new();
        public bool Autoplay { get; set; } = true;
        // the old switch, still written (= Mode is single-track) so an older build reading this file keeps its meaning
        public bool Loop { get; set; } = true;
        // "order" / "one" / "campaign" / "all"; missing in files from before 11-22 → taken from Loop (on = one, off = order)
        public string? Mode { get; set; }
        public bool Shuffle { get; set; }
        public bool Narration { get; set; } = true;          // story pages are read aloud (GameAudio.Narration.cs, 11-21)
        public double NarrationVolume { get; set; } = 0.9;
    }
    public sealed record Track(string File, string Title, string Group);
    // 顺序: the list once, then silence · 单曲循环: this track again · 剧本循环: the current campaign's tracks · 全部循环: the whole list
    public enum PlayMode { Order, One, Campaign, All }
    static readonly (PlayMode Mode, string Key, string Name, string Tip)[] Modes =
    {
        (PlayMode.Order, "order", "顺序", "按列表顺序播放，最后一首放完后停止"),
        (PlayMode.One, "one", "单曲循环", "重复播放当前曲目"),
        (PlayMode.Campaign, "campaign", "剧本循环", "循环播放当前战役的曲目"),
        (PlayMode.All, "all", "全部循环", "循环播放整个曲目列表"),
    };

    // UI refresh: the place, the track or the playing state changed.
    public static event Action? Changed;

    MusicPrefs prefs = new();
    PlayMode ModeNow => Array.FindIndex(Modes, m => m.Key == prefs.Mode) is var mi && mi >= 0 ? Modes[mi].Mode : prefs.Loop ? PlayMode.One : PlayMode.Order;
    // session only: the track the list moved on to by itself at this place (a manual choice is kept in prefs.Overrides instead)
    string autoKey = "", autoFile = "";
    // 随机: the tracks already heard in this round, so a shuffle goes through its whole scope before repeating
    readonly HashSet<string> heard = new(); string heardScope = "";
    readonly List<Track> order = new();
    bool paused;                               // the player paused or stopped; nothing starts until Play
    bool quietEnd;                             // …or 顺序 ran out by itself (a new chapter may start its music again)
    // the chapter's opening track (Yunke 11-22): the place it plays at, the file, and what 单曲循环 returns to after it
    string chapterKey = "", chapterFile = "", chapterResume = "", chapterSeen = "";
    string ctxKey = "", ctxLabel = "", wantedFile = "", wantedCtx = "";
    string[] ctxDefaults = Array.Empty<string>();
    double? previewMusic, previewFx;
    ulong lastPreviewCue;

    // ---------- what the UI reads ----------
    public static bool Available => !Off && I is not null;
    public static IReadOnlyList<Track> Tracks => I?.order ?? (IReadOnlyList<Track>)Array.Empty<Track>();
    public static string PlaceKey => I?.ctxKey ?? "";
    public static string PlaceName => I is null ? "" : I.ctxLabel.Length > 0 ? I.ctxLabel : I.ctxKey;
    // the chosen track for this place ("" = follow the default)
    public static string ChosenFile => I is not null && I.prefs.Overrides.TryGetValue(I.ctxKey, out var f) ? f : "";
    public static string DefaultTitle => I is null ? "" : I.TitleOf(I.Default(I.ctxDefaults).File);
    public static string CurrentTitle => I is null ? "" : I.TitleOf(I.wantedFile);
    public static string CurrentGroup => I is not null && I.groups.TryGetValue(PathIO.GetFileName(I.wantedFile), out var g) ? g : "";
    public static bool HasTrack => I is not null && I.wantedFile.Length > 0;
    public static bool Playing => I is { paused: false, musicNow: { } p } && p.Playing && !p.StreamPaused;
    public static bool Autoplay { get => I?.prefs.Autoplay ?? true; set { if (I is null) return; I.prefs.Autoplay = value; I.SavePrefs(); Changed?.Invoke(); } }
    public static PlayMode Mode
    {
        get => I?.ModeNow ?? PlayMode.One;
        set
        {
            if (I is null) return;
            I.prefs.Mode = Modes.First(m => m.Mode == value).Key; I.prefs.Loop = value == PlayMode.One; I.heard.Clear(); I.SavePrefs();
            if (I.musicNow?.Stream is { } s) LoopStream(s, value == PlayMode.One && I.chapterKey.Length == 0);   // the track playing now repeats or runs to its end
            GD.Print("AUDIO_MUSIC mode " + ModeName(value));
            Changed?.Invoke();
        }
    }
    public static bool Shuffle { get => I?.prefs.Shuffle ?? false; set { if (I is null) return; I.prefs.Shuffle = value; I.heard.Clear(); I.SavePrefs(); Changed?.Invoke(); } }
    public static string ModeName(PlayMode m) => Modes.First(x => x.Mode == m).Name;
    public static string ModeTip(PlayMode m) => Modes.First(x => x.Mode == m).Tip;
    // the button walks 顺序 → 单曲循环 → 剧本循环 → 全部循环 → 顺序
    public static void CycleMode() => Mode = (PlayMode)(((int)Mode + 1) % Modes.Length);
    // the campaign 剧本循环 keeps to: the one the current place's own music belongs to (else the track playing)
    public static string ScopeGroup => I?.CampaignGroup() ?? "";

    // ---------- what the UI does ----------
    public static void Play() { if (Available) I!.DoPlay(); }
    public static void Pause() { if (Available) I!.DoPause(); }
    public static void Stop() { if (Available) I!.DoStop(); }
    public static void TogglePlay() { if (Playing) Pause(); else Play(); }
    // file "" = back to this place's default
    public static void Select(string file) { if (Available) I!.DoSelect(file); }
    public static void Next() { if (Available) I!.Step(+1); }
    public static void Previous() { if (Available) I!.Step(-1); }
    // Settings dialog: the sliders are heard while they move; EndPreview returns to the saved settings.
    public static void PreviewVolume(double music, double effects)
    {
        if (!Available) return;
        var g = I!; bool fxMoved = g.previewFx is null || Math.Abs(g.previewFx.Value - effects) > 0.001;
        g.previewMusic = music; g.previewFx = effects;
        ulong now = Time.GetTicksMsec();
        if (fxMoved && now - g.lastPreviewCue > 180) { g.lastPreviewCue = now; Callable.From(() => Cue("click")).CallDeferred(); }   // after the bus took the new level
    }
    public static void EndPreview() { if (I is null) return; I.previewMusic = null; I.previewFx = null; }

    // ---------- inside ----------
    void Want(string key, string label, params string[] contexts)
    {
        bool moved = key != ctxKey;
        ctxKey = key; ctxLabel = label; ctxDefaults = contexts;
        if (moved) autoKey = "";
        if (moved && !prefs.Autoplay)   // html: autoplay off → the music stops at a new place and waits for Play
        {
            paused = true;
            if (musicNow is { } p0) Fade(p0, Silent, FadeOut, stopAfter: true);
            musicNow = null; musicFile = ""; musicToken++;
        }
        if (moved && chapterKey.Length > 0 && key != chapterKey) chapterKey = "";   // left the chapter before its track ended
        string? file = null; string ctx = "";
        if (key.Length > 0 && chapterKey == key && Find("music", chapterFile) is { } pc) { file = pc; ctx = "chapter:" + key; }
        else if (key.Length > 0 && autoKey == key && Find("music", autoFile) is { } pa) { file = pa; ctx = "list:" + key; }
        else if (key.Length > 0 && prefs.Overrides.TryGetValue(key, out var chosen) && Find("music", chosen) is { } p) { file = p; ctx = "chosen:" + key; }
        else (file, ctx) = Default(contexts);
        bool fileMoved = (file ?? "") != wantedFile;
        wantedFile = file ?? ""; wantedCtx = ctx;
        if (moved || fileMoved) Changed?.Invoke();
        Apply();
    }

    void DoPlay()
    {
        paused = false; quietEnd = false;
        if (musicNow is { } p && p.StreamPaused && PathIO.GetFileName(musicFile) == PathIO.GetFileName(wantedFile))
        {
            p.StreamPaused = false; Fade(p, MusicBaseDb, 0.5, stopAfter: false);
            GD.Print("AUDIO_MUSIC resume " + TitleOf(musicFile));
        }
        else if (musicFile == wantedFile && musicNow is { Playing: false } q && wantedFile.Length > 0) { musicFile = ""; Apply(); }   // ended (loop off): from the start
        else Apply();
        Changed?.Invoke();
    }
    void DoPause()
    {
        paused = true; quietEnd = false;
        if (musicNow is { } p && p.Playing && !p.StreamPaused)
        {
            if (fades.TryGetValue(p, out var old)) old.Kill();
            var tw = CreateTween();
            tw.TweenProperty(p, "volume_db", Silent, 0.35);
            tw.TweenCallback(Callable.From(() => { if (paused && musicNow == p) p.StreamPaused = true; }));
            fades[p] = tw;
        }
        GD.Print("AUDIO_MUSIC pause");
        Changed?.Invoke();
    }
    void DoStop()
    {
        paused = true; quietEnd = false;
        if (musicNow is { } p) Fade(p, Silent, 0.6, stopAfter: true);
        musicNow = null; musicFile = ""; musicToken++;
        GD.Print("AUDIO_MUSIC stop");
        Changed?.Invoke();
    }
    void DoSelect(string file)
    {
        if (ctxKey.Length == 0) return;
        if (file.Length == 0) prefs.Overrides.Remove(ctxKey);
        else if (order.Any(t => t.File == file)) prefs.Overrides[ctxKey] = file;
        else return;
        SavePrefs();
        autoKey = ""; chapterKey = "";
        paused = false; quietEnd = false;     // choosing a track is wanting to hear it
        GD.Print("AUDIO_MUSIC select place=" + ctxKey + " -> " + (file.Length > 0 ? TitleOf(file) : "default"));
        Want(ctxKey, ctxLabel, ctxDefaults);
        Changed?.Invoke();
    }
    // 上一首 / 下一首: within the mode's scope (the campaign for 剧本循环, else the whole list), always wrapping; with 随机 on,
    // 下一首 is a random track of that scope. The track reached plays for this session only (11-22; before, it was kept as this
    // place's choice and came back every time, even over a chapter's own track).
    void Step(int delta)
    {
        if (ctxKey.Length == 0) return;
        var next = NextTrack(delta, manual: true);
        if (next is null) return;
        chapterKey = ""; autoKey = ctxKey; autoFile = next;
        paused = false; quietEnd = false; musicFile = "";
        GD.Print("AUDIO_MUSIC step place=" + ctxKey + " -> " + TitleOf(next));
        Want(ctxKey, ctxLabel, ctxDefaults);
        Changed?.Invoke();
    }
    // A chapter entered: its own track (if it has one) plays first, from the start.
    void EnterChapter(string scen)
    {
        var own = Default(new[] { "scenario:" + scen }).File;
        if (own is null) { chapterKey = ""; return; }
        chapterResume = musicNow is { Playing: true } && !paused && musicFile.Length > 0 ? musicFile : "";
        chapterKey = scen; chapterFile = PathIO.GetFileName(own);
        if (paused && quietEnd) { paused = false; quietEnd = false; }   // 顺序 had run out: a new chapter has music again
        if (ctxKey == scen) autoKey = "";
        if (musicFile == own) musicFile = "";   // the same file starts again from the top
        GD.Print("AUDIO_MUSIC chapter " + scen + " -> " + TitleOf(own) + " (then " + ModeName(ModeNow) + (ModeNow == PlayMode.One ? " " + (chapterResume.Length > 0 ? TitleOf(chapterResume) : "-") : "") + ")");
    }
    void TrackEnded(AudioStreamPlayer p)
    {
        if (p != musicNow) return;
        if (chapterKey.Length > 0 && chapterKey == ctxKey) { ChapterTrackEnded(); return; }
        if (ModeNow == PlayMode.One) return;   // 单曲循环: the stream loops by itself and never ends
        Advance();
    }
    // the chapter's track ran to its end: the play mode takes over again
    void ChapterTrackEnded()
    {
        string played = chapterFile; chapterKey = "";
        if (ModeNow != PlayMode.One) { GD.Print("AUDIO_MUSIC chapter-end -> " + ModeName(ModeNow)); Advance(); return; }
        // 单曲循环: back to the track that looped before the chapter (none → this place's usual track)
        string back = chapterResume.Length > 0 && Find("music", PathIO.GetFileName(chapterResume)) is not null ? PathIO.GetFileName(chapterResume) : "";
        if (back.Length > 0) { autoKey = ctxKey; autoFile = back; } else autoKey = "";
        musicFile = "";
        GD.Print("AUDIO_MUSIC chapter-end -> 单曲循环 " + (back.Length > 0 ? TitleOf(back) : "place default") + " (after " + TitleOf(played) + ")");
        Want(ctxKey, ctxLabel, ctxDefaults);
        Changed?.Invoke();
    }
    // a track ran to its end: the next one of the mode (顺序 stops after the last)
    void Advance()
    {
        var mode = ModeNow;
        var next = mode == PlayMode.One || ctxKey.Length == 0 ? null : NextTrack(+1, manual: false);
        if (next is null)
        {
            paused = true; quietEnd = true;    // stays silent until Play (not restarted by the next state; a new chapter does)
            GD.Print("AUDIO_MUSIC ended mode=" + ModeName(mode));
            Changed?.Invoke();
            return;
        }
        autoKey = ctxKey; autoFile = next;
        musicFile = "";                        // even the same file starts again
        GD.Print("AUDIO_MUSIC advance mode=" + ModeName(mode) + (prefs.Shuffle ? "+随机" : "") + " -> " + TitleOf(next));
        Want(ctxKey, ctxLabel, ctxDefaults);
        Changed?.Invoke();
    }
    // The tracks a mode moves through, in list order, only those that exist.
    List<Track> Scope(out string scopeName)
    {
        var all = order.Where(t => Find("music", t.File) is not null).ToList();
        scopeName = "all";
        if (ModeNow != PlayMode.Campaign) return all;
        string g = CampaignGroup();
        var mine = all.Where(t => g.Length > 0 && t.Group == g).ToList();
        if (mine.Count == 0) return all;
        scopeName = g; return mine;
    }
    string CampaignGroup()
    {
        string GroupOf(string? f) => string.IsNullOrEmpty(f) ? "" : groups.GetValueOrDefault(PathIO.GetFileName(f), "");
        var g = GroupOf(Default(ctxDefaults).File);
        return g.Length > 0 ? g : GroupOf(wantedFile);
    }
    // The next track (delta +1) or the previous one (-1). manual: the 上一首 / 下一首 buttons, which always wrap round;
    // otherwise only the looping modes wrap and 顺序 returns null after the last track.
    string? NextTrack(int delta, bool manual)
    {
        var list = Scope(out var scopeName);
        if (list.Count == 0) return null;
        string cur = PathIO.GetFileName(wantedFile);
        bool wrap = manual || ModeNow != PlayMode.Order;
        if (prefs.Shuffle && ModeNow != PlayMode.One && delta > 0)
        {
            if (heardScope != scopeName) { heard.Clear(); heardScope = scopeName; }
            if (cur.Length > 0) heard.Add(cur);
            var fresh = list.Where(t => !heard.Contains(t.File)).ToList();
            if (fresh.Count == 0)
            {
                heard.Clear();
                if (!wrap) return null;        // 顺序 + 随机: every track once, then silence
                if (cur.Length > 0) heard.Add(cur);
                fresh = list.Where(t => t.File != cur).ToList();
                if (fresh.Count == 0) fresh = list;
            }
            return fresh[rng.RandiRange(0, fresh.Count - 1)].File;
        }
        int i = list.FindIndex(t => t.File == cur);
        if (i < 0) return list[delta > 0 ? 0 : list.Count - 1].File;
        int n = i + delta;
        if (n < 0 || n >= list.Count) { if (!wrap) return null; n = (n % list.Count + list.Count) % list.Count; }
        return list[n].File;
    }
    static void LoopStream(AudioStream s, bool loop)
    {
        switch (s)
        {
            case AudioStreamMP3 m: m.Loop = loop; break;
            case AudioStreamOggVorbis o: o.Loop = loop; break;
            case AudioStreamWav w: w.LoopMode = loop ? AudioStreamWav.LoopModeEnum.Forward : AudioStreamWav.LoopModeEnum.Disabled; break;
        }
    }
    string TitleOf(string? file)
    {
        if (string.IsNullOrEmpty(file)) return "";
        var name = PathIO.GetFileName(file);
        return titles.TryGetValue(name, out var t) && t.Length > 0 ? t : PathIO.GetFileNameWithoutExtension(name);
    }
    // the official chapter name for a scenario code (Campaigns registry), else the code
    static string ChapterName(string code) => Campaigns.All.SelectMany(c => c.Chapters).FirstOrDefault(ch => ch.Code == code)?.Name ?? code;

    readonly Dictionary<string, string> groups = new();

    // Yunke 11-22 "音乐的排列按照关卡来": the groups (campaigns) in campaign order — the ones in Campaigns.All first, in its
    // order, then the later cycles by their number — and inside each group the campaign's own theme (its "campaign:<id>" track,
    // which plays between chapters and is "01-" in the composer's numbering), then the scenarios' tracks in chapter order
    // (Campaigns.cs; cycles not there yet by their scenario code, which runs in chapter order), then the tracks tied to no
    // scenario, by name. The link is the music map in audio.json ("scenario:<code>" / "campaign:<id>" → file).
    void SortByScenario()
    {
        var camp = new Dictionary<string, string>(); var code = new Dictionary<string, string>();
        foreach (var (key, files) in music)
            foreach (var f in files)
            {
                if (key.StartsWith("scenario:")) { if (!code.ContainsKey(f)) { code[f] = key[9..]; camp[f] = CampaignOfCode(key[9..]); } }
                else if (key.StartsWith("campaign:") && !camp.ContainsKey(f)) camp[f] = key[9..];
            }
        int CampRank(string id)
        {
            int i = Array.FindIndex(Campaigns.All, c => c.Id == id);
            return i >= 0 ? i : int.TryParse(id, out var n) ? Campaigns.All.Length + n : int.MaxValue;
        }
        int ChapterRank(string id, string c) => Campaigns.All.FirstOrDefault(x => x.Id == id)?.Index(Gathering.CoreCatalog.Normalize(c)) is int i && i >= 0 ? i : 1000;
        var groupRank = order.GroupBy(t => t.Group).ToDictionary(g => g.Key, g => g.Min(t => camp.TryGetValue(t.File, out var id) ? CampRank(id) : int.MaxValue));
        var sorted = order
            .OrderBy(t => groupRank[t.Group]).ThenBy(t => t.Group, StringComparer.Ordinal)
            .ThenBy(t => code.ContainsKey(t.File) ? 1 : camp.ContainsKey(t.File) ? 0 : 2)
            .ThenBy(t => code.TryGetValue(t.File, out var c) && camp.TryGetValue(t.File, out var id) ? ChapterRank(id, c) : 0)
            .ThenBy(t => code.GetValueOrDefault(t.File, ""), StringComparer.Ordinal)
            .ThenBy(t => t.Title, StringComparer.OrdinalIgnoreCase)
            .ToList();
        order.Clear(); order.AddRange(sorted);
    }
    // a scenario code → its campaign: the one in Campaigns.cs that has it, else the code's cycle number ("04043" → "04")
    static string CampaignOfCode(string c) => Campaigns.All.FirstOrDefault(x => x.Find(Gathering.CoreCatalog.Normalize(c)) is not null)?.Id ?? (c.Length >= 2 ? c[..2] : c);
    string PrefsFile => PathIO.Combine(App.I?.DataRoot ?? "", "music.json");
    void LoadPrefs()
    {
        try
        {
            var bytes = Godot.FileAccess.GetFileAsBytes("res://assets/audio/audio.json");
            using var doc = JsonDocument.Parse(bytes);
            if (doc.RootElement.TryGetProperty("groups", out var g) && g.ValueKind == JsonValueKind.Object)
                foreach (var p in g.EnumerateObject()) groups[p.Name] = p.Value.GetString() ?? "";
            if (doc.RootElement.TryGetProperty("order", out var o) && o.ValueKind == JsonValueKind.Array)
                foreach (var f in o.EnumerateArray().Select(x => x.GetString() ?? "").Where(x => x.Length > 0))
                    order.Add(new Track(f, TitleOf(f), groups.GetValueOrDefault(f, "")));
            SortByScenario();
        }
        catch (Exception e) { GD.PrintErr("AUDIO_ORDER_FAIL " + e.Message); }
        try { if (System.IO.File.Exists(PrefsFile)) prefs = JsonSerializer.Deserialize<MusicPrefs>(System.IO.File.ReadAllText(PrefsFile)) ?? new(); }
        catch (Exception e) { GD.PrintErr("AUDIO_PREFS_FAIL " + e.Message); prefs = new(); }
        RegisterPickerQa();
    }
    void SavePrefs()
    {
        try { System.IO.File.WriteAllText(PrefsFile, JsonSerializer.Serialize(prefs, new JsonSerializerOptions { WriteIndented = true })); }
        catch (Exception e) { GD.PrintErr("AUDIO_PREFS_SAVE_FAIL " + e.Message); }
    }

    void RegisterPickerQa()
    {
        QaScript.Register("music-status", _ => PickerStatus());
        QaScript.Register("music-next", _ => { Next(); PickerStatus(); });
        QaScript.Register("music-prev", _ => { Previous(); PickerStatus(); });
        QaScript.Register("music-select", a => { Select(a == "-" ? "" : a); PickerStatus(); });
        QaScript.Register("music-pause", _ => { Pause(); PickerStatus(); });
        QaScript.Register("music-play", _ => { Play(); PickerStatus(); });
        QaScript.Register("music-stop", _ => { Stop(); PickerStatus(); });
        QaScript.Register("music-loop", a => Mode = a != "off" ? PlayMode.One : PlayMode.Order);   // old step name
        QaScript.Register("music-mode", a => { int mi = Array.FindIndex(Modes, m => m.Key == a || m.Name == a); Mode = mi >= 0 ? Modes[mi].Mode : PlayMode.One; PickerStatus(); });
        QaScript.Register("music-shuffle", a => { Shuffle = a != "off"; PickerStatus(); });
        // as if the track playing ran to its end
        QaScript.Register("music-end", _ => { if (chapterKey.Length > 0 && chapterKey == ctxKey) ChapterTrackEnded(); else if (ModeNow == PlayMode.One) GD.Print("AUDIO_MUSIC one: repeats"); else Advance(); PickerStatus(); });
        QaScript.Register("music-order", _ => { foreach (var g in order.GroupBy(t => t.Group)) GD.Print("MUSIC_ORDER [" + g.Key + "] " + string.Join(" | ", g.Select(t => t.Title))); });
        // presses the play-mode button of the music controls on screen
        QaScript.Register("music-mode-click", _ => { var b = FindShown<Button>(x => x.Name == "PlayMode"); b?.EmitSignal(BaseButton.SignalName.Pressed); GD.Print("QA_MODE_CLICK " + (b?.Text ?? "none")); });
        QaScript.Register("music-autoplay", a => Autoplay = a != "off");
        QaScript.Register("music-panel", _ => MusicPanel.Open());
        QaScript.Register("settings-open", _ => SettingsDialog.Open());
        // the act / agenda reading without the page (the QA run closes that page after 4 s): narr-cards:old,new opens a stand-in
        // page, narr-cards-close closes it
        Node? qaPage = null;
        QaScript.Register("narr-cards", a => { var p = a.Split(','); qaPage?.QueueFree(); qaPage = new Node(); AddChild(qaPage); NarrateCards(qaPage, p[0].Length > 0 ? p[0] : null, p.Length > 1 && p[1].Length > 0 ? p[1] : null); });
        QaScript.Register("narr-cards-close", _ => { qaPage?.QueueFree(); qaPage = null; });
        QaScript.Register("narr-status", _ => GD.Print($"NARR_STATUS speaking={Narrating} voicePlaying={voice?.Playing} queued={narrQueue.Count} on={prefs.Narration} sig={(narrSig.Length > 0 ? "page" : "-")}"));
        // opens the track list of the music controls on screen (to capture it)
        QaScript.Register("music-dropdown", _ =>
        {
            OptionButton? hit = null; void Walk(Node x) { if (hit is null && x is OptionButton o && o.GetParent() is MusicControls && o.IsVisibleInTree()) hit = o; foreach (var c in x.GetChildren()) Walk(c); }
            if (App.I is not null) Walk(App.I);
            hit?.ShowPopup(); GD.Print("QA_DROPDOWN " + (hit is null ? "none" : hit.ItemCount + " items"));
        });
        // moves the n-th slider on screen (0 = music volume in the settings dialog, 1 = effects) the way a drag would
        QaScript.Register("ui-slide", a =>
        {
            var p = a.Split(','); int n = int.Parse(p[0]); double v = double.Parse(p[1], System.Globalization.CultureInfo.InvariantCulture);
            var sliders = new List<HSlider>(); void Walk(Node x) { if (x is HSlider h && h.IsVisibleInTree()) sliders.Add(h); foreach (var c in x.GetChildren()) Walk(c); }
            if (App.I is not null) Walk(App.I);
            if (n < sliders.Count) { sliders[n].Value = v; GD.Print($"QA_SLIDE {n} -> {v}"); } else GD.Print("QA_SLIDE none " + sliders.Count);
        });
    }
    static T? FindShown<T>(Func<T, bool> ok) where T : CanvasItem
    {
        T? hit = null; void Walk(Node x) { if (hit is null && x is T t && t.IsVisibleInTree() && ok(t)) hit = t; foreach (var c in x.GetChildren()) Walk(c); }
        if (App.I is not null) Walk(App.I);
        return hit;
    }
    void PickerStatus()
    {
        int i = AudioServer.GetBusIndex(MusicBus);
        GD.Print($"MUSIC_STATUS place={ctxKey}({ctxLabel}) chosen={(ChosenFile.Length > 0 ? TitleOf(ChosenFile) : "-")} default={DefaultTitle} now={CurrentTitle} [{CurrentGroup}] " +
                 $"playing={Playing} paused={paused} streamPaused={(musicNow?.StreamPaused ?? false)} quietEnd={quietEnd} chapter={(chapterKey.Length > 0 ? chapterKey + ":" + TitleOf(chapterFile) : "-")} autoplay={prefs.Autoplay} mode={ModeName(ModeNow)} shuffle={prefs.Shuffle} scope={(ModeNow == PlayMode.Campaign ? CampaignGroup() : "all")} " +
                 $"musicBus={(i >= 0 ? AudioServer.GetBusVolumeDb(i) : 0):0.0}dB{(i >= 0 && AudioServer.IsBusMute(i) ? "(mute)" : "")} preview={(previewMusic?.ToString("0.00") ?? "-")}");
    }
}
