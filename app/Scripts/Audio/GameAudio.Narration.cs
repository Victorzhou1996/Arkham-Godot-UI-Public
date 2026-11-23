using System.Text.Json;
using Godot;
using Gathering;

namespace ArkhamUI;

// Story narration (Yunke 11-21): the 讯飞「江阅尘-解说」reading of the base game and the Dunwich Legacy, delivered as
// "江阅尘独读-基础与敦威治" (524 recordings, commercial use allowed per Yunke). Converted to Ogg in app/assets/audio/voice/,
// index voice/narration.json (key → recording; paragraph links). See voice/NOTICE.md.
// What is read is what the story page shows: the locale keys of the core's Read question (I18nEntry, paragraph by paragraph
// "#p1, #p2 …"), never the lines the core marks as not applying (InvalidEntry, greyed on the page). Only the story text
// (Yunke 11-21: 序章、引言、结局、幕间、尾声) — card flavour and choice labels in the index are not played. The core picks the branch (resolution 1 / 2 …), so a recording is only ever played for
// a key the page really shows. Card lines that need a game condition the index cannot decide ("guarded") are not played.
// The music steps further back while the voice speaks; leaving the page stops the voice.
public partial class GameAudio
{
    const string VoiceBus = "Voice";
    const float VoiceDuckDb = -7f;
    readonly Dictionary<string, string> narrCues = new();          // key → recording id
    readonly Dictionary<string, (string Next, int GapMs)> narrLinks = new();
    readonly Queue<(string Body, int GapMs)> narrQueue = new();
    AudioStreamPlayer voice = null!;
    string narrSig = "", narrLastKeys = "";
    float voiceDuck; Tween? voiceDuckTween;
    bool narrLoaded;
    int narrToken;

    // ---------- what the table and the UI call ----------
    // The story page is on screen (TableScreen.StoryDialog): read it, once per page.
    public static void Narrate(TableModel m) { if (Available) try { I!.DoNarrate(m); } catch (Exception e) { GD.PrintErr("AUDIO_NARRATE_FAIL " + e.Message); } }
    public static void StopNarration() { if (Available) I!.DoStopNarration(0.4); }
    public static void ReplayNarration() { if (Available) I!.DoReplay(); }
    public static bool Narrating => Available && (I!.voice.Playing || I.narrQueue.Count > 0);
    public static bool NarrationOn
    {
        get => I?.prefs.Narration ?? true;
        set { if (I is null) return; I.prefs.Narration = value; I.SavePrefs(); if (!value) I.DoStopNarration(0.3); Changed?.Invoke(); }
    }
    public static double NarrationVolume
    {
        get => I?.prefs.NarrationVolume ?? 0.9;
        set { if (I is null) return; I.prefs.NarrationVolume = Math.Clamp(value, 0, 1); SetBus(VoiceBus, I.prefs.NarrationVolume, 0); }
    }
    public static void SaveNarrationVolume() => I?.SavePrefs();

    // ---------- inside ----------
    void NarrationReady()
    {
        Bus(VoiceBus);
        voice = new AudioStreamPlayer { Bus = VoiceBus, VolumeDb = 0 };
        AddChild(voice);
        voice.Finished += OnVoiceFinished;
        SetBus(VoiceBus, prefs.NarrationVolume, 0);
        try
        {
            var bytes = Godot.FileAccess.GetFileAsBytes("res://assets/audio/voice/narration.json");
            if (bytes.Length == 0) { GD.Print("AUDIO_NARRATION none"); return; }
            using var doc = JsonDocument.Parse(bytes);
            foreach (var c in doc.RootElement.GetProperty("cues").EnumerateObject()) narrCues[c.Name] = c.Value.GetProperty("body").GetString() ?? "";
            if (doc.RootElement.TryGetProperty("links", out var l))
                foreach (var k in l.EnumerateObject()) narrLinks[k.Name] = (k.Value.GetProperty("next").GetString() ?? "", k.Value.GetProperty("gapMs").GetInt32());
            narrLoaded = true;
            GD.Print($"AUDIO_NARRATION cues={narrCues.Count} links={narrLinks.Count}");
        }
        catch (Exception e) { GD.PrintErr("AUDIO_NARRATION_FAIL " + e.Message); }
    }

    // The recordings for one locale key: the key itself, or its paragraphs "#p1 … #pN" in order.
    IEnumerable<string> CueKeysFor(string key)
    {
        if (narrCues.ContainsKey(key)) { yield return key; yield break; }
        // paragraphs "#p1 …", or "#n1 …" (the 48k pack's 淹灭之城 awakening notes, 11-23)
        string mark = narrCues.ContainsKey(key + "#p1") || !narrCues.ContainsKey(key + "#n1") ? "#p" : "#n";
        for (int n = 1; n <= 40; n++) { var k = key + mark + n; if (narrCues.ContainsKey(k)) yield return k; else if (n > 1 && !narrCues.ContainsKey(key + mark + (n + 1))) yield break; }
    }

    // The keys a Read question shows, in page order, skipping what the core marks as not applying.
    static void PageKeys(JsonElement entry, List<string> into)
    {
        switch (J.Tag(entry))
        {
            case "I18nEntry": into.Add(J.String(entry, "key")); break;
            case "ModifyEntry":
                if (J.Items(J.Get(entry, "modifiers")).Any(x => x.ValueKind == JsonValueKind.String && x.GetString() == "InvalidEntry")) break;
                PageKeys(J.Get(entry, "entry"), into); break;
            case "CompositeEntry": case "ColumnEntry": foreach (var c in J.Items(J.Get(entry, "entries"))) PageKeys(c, into); break;
            case "ListEntry": foreach (var it in J.Items(J.Get(entry, "list"))) { PageKeys(J.Get(it, "entry"), into); foreach (var n in J.Items(J.Get(it, "nested"))) PageKeys(n, into); } break;
        }
    }

    void DoNarrate(TableModel m)
    {
        if (!narrLoaded || m.QuestionTag != "Read") return;
        var q = ClientQuestions.Unwrap(m.State.Question);
        var keys = new List<string>();
        foreach (var e in J.Items(J.Get(J.Get(q, "flavorText"), "body"))) PageKeys(e, keys);
        // Yunke 11-21: only the story itself (prologue, intros, resolutions, interludes, epilogues) — no card text, not even
        // the flavour of a card the page shows
        keys.RemoveAll(k => k.StartsWith("card.") || k.Contains(".label."));
        string pageSig = m.State.Id + "|" + string.Join(",", keys);
        if (pageSig == narrSig) return;                       // the same page re-rendered: keep reading, do not restart
        narrSig = pageSig; narrLastKeys = string.Join(",", keys);
        var items = keys.SelectMany(CueKeysFor).Distinct().ToList();
        var missingKeys = keys.Where(k => !CueKeysFor(k).Any()).ToList();
        GD.Print($"AUDIO_NARRATE keys={keys.Count} recordings={items.Count}" + (missingKeys.Count > 0 ? " unvoiced=" + string.Join(",", missingKeys) : ""));
        DoStopNarration(0, keepSig: true);                    // a new page: the old one stops at once
        if (!prefs.Narration || items.Count == 0) return;
        for (int i = 0; i < items.Count; i++)
        {
            int gap = i == items.Count - 1 ? 0
                : narrLinks.TryGetValue(items[i], out var link) && link.Next == items[i + 1] ? link.GapMs   // same body, paragraph to paragraph
                : 420;                                                                                       // a new paragraph / text on the page
            narrQueue.Enqueue((narrCues[items[i]], gap));
        }
        VoiceDuck(true);
        NextInQueue();
    }

    int currentGap;
    Tween? voiceFade;
    // a paragraph ended: wait the pause that follows it (0 = straight on), then the next one
    void OnVoiceFinished()
    {
        int token = narrToken, gap = currentGap;
        if (gap > 0 && narrQueue.Count > 0) GetTree().CreateTimer(gap / 1000.0).Timeout += () => { if (token == narrToken) NextInQueue(); };
        else NextInQueue();
    }
    void NextInQueue()
    {
        voiceFade?.Kill(); voiceFade = null;                 // a fade-out of the page before must not cut this one
        if (voice.Playing) voice.Stop();
        voice.VolumeDb = 0;
        if (narrQueue.Count == 0) { VoiceDuck(false); Changed?.Invoke(); return; }
        var (body, gap) = narrQueue.Dequeue();
        var path = Find("voice", body + ".ogg");
        var stream = path is null ? null : Load(path, false);
        if (stream is null) { Missing("voice=" + body); NextInQueue(); return; }
        currentGap = gap;
        voice.Stream = stream; voice.Play();
        Changed?.Invoke();
        if (verbose) GD.Print("AUDIO_VOICE " + body + " gapAfter=" + gap);
    }

    void DoStopNarration(double fade, bool keepSig = false)
    {
        narrToken++; narrQueue.Clear();
        if (!keepSig) narrSig = "";
        voiceFade?.Kill(); voiceFade = null;
        if (voice is not null && voice.Playing)
        {
            if (fade <= 0) { voice.Stop(); voice.VolumeDb = 0; }
            else
            {
                var p = voice; voiceFade = CreateTween();
                voiceFade.TweenProperty(p, "volume_db", Silent, fade);
                voiceFade.TweenCallback(Callable.From(() => { p.Stop(); p.VolumeDb = 0; voiceFade = null; }));
            }
        }
        VoiceDuck(false);
        Changed?.Invoke();
    }
    void DoReplay() { var keep = narrSig; narrSig = ""; if (App.I?.Current is TableScreen && lastModel is { } m) DoNarrate(m); else narrSig = keep; }

    void VoiceDuck(bool on)
    {
        float to = on ? VoiceDuckDb : 0f; if (Math.Abs(voiceDuck - to) < 0.01f && voiceDuckTween is null) return;
        voiceDuckTween?.Kill();
        voiceDuckTween = CreateTween();
        voiceDuckTween.TweenMethod(Callable.From<float>(v => { voiceDuck = v; ApplyMusicBus(); }), voiceDuck, to, on ? 0.5 : 1.0);
        voiceDuckTween.TweenCallback(Callable.From(() => voiceDuckTween = null));
    }

    // the page left (Observe): a story page that is no longer on screen is not read on
    TableModel? lastModel;
    void NarrationFollow(TableModel m)
    {
        lastModel = m;
        if (m.QuestionTag != "Read" && narrSig.Length > 0) DoStopNarration(0.5);
    }
}
