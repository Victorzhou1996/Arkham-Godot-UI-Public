using Gathering;
using System.Text.Json;
using System.Text.Json.Nodes;
using Path = System.IO.Path;
using File = System.IO.File;

namespace ArkhamUI;

// Everything that talks to the rules DLL or persists data. UI never touches JSON files directly.
public sealed class Services : IAsyncDisposable
{
    public PrototypeBackend Backend { get; }
    public CoreCatalog Catalog { get; }
    public ClientLocale Locale { get; }
    public QuestionPresenter Presenter { get; }
    public DeckLibrary Decks { get; }
    public UserSettings Settings { get; }
    readonly string data;

    Services(string root, string data, bool noCore = false)
    {
        this.data = data;
        string assets = App.I.Assets;   // <root>/app/assets in the editor, game/assets in the exported build (App.cs)
        Catalog = new CoreCatalog(Path.Combine(assets, "data", "core-cards.json"));
        // locale-zh.json, plus official keys our copy lacks (locale-zh-extra.json, copied from the html version's
        // newer locale files); keys already in locale-zh.json always win
        var merged = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(Path.Combine(assets, "data", "locale-zh.json")))!.AsObject();
        string extraPath = Path.Combine(assets, "data", "locale-zh-extra.json");
        if (File.Exists(extraPath)) Merge(merged, System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(extraPath))!.AsObject());
        using var dictionary = JsonDocument.Parse(merged.ToJsonString());
        Locale = new ClientLocale(dictionary.RootElement) { CardName = c => Catalog.Name(c), CardNameFromEnglish = Catalog.NameFromEnglish };
        ArkhamUI.TableModel.TokenName = k => Locale.Plain("$token." + k);
        Presenter = new QuestionPresenter(Catalog, Locale);
        // a phone without the rules core (Android, 11-22) still gets the cards, texts and decks, so it can sit at an online
        // table whose host runs the rules (OnlineGame); CoreError says why the core is missing
        try { Backend = noCore ? throw new InvalidOperationException("QA：模拟没有规则核心的手机") : new PrototypeBackend(root, data, assets); }
        catch (Exception e) { Backend = null!; CoreError = e.Message; }
        if (Backend is null) { Decks = new DeckLibrary(Path.Combine(data, "decks.json")); Settings = UserSettings.Load(Path.Combine(data, "settings.json")); return; }
        // build's construction check for decks the core checks only provisionally (PrototypeBackend.TextOnlyCards: Sophie)
        Backend.BuildCheck = async deck =>
        {
            await BuildEngine.I.Ready;
            var v = await BuildEngine.I.CallJson("validate", deck.GetRawText());
            bool ok = J.Get(v, "valid").ValueKind == JsonValueKind.True;
            return (ok, J.Items(J.Get(v, "errors")).Select(e => BuildDecks.ErrorText(e, c => Catalog.Name(c), J.String(deck, "investigator_code"))).Where(s => s.Length > 0).Distinct().ToArray());
        };
        Decks = new DeckLibrary(Path.Combine(data, "decks.json"));
        Settings = UserSettings.Load(Path.Combine(data, "settings.json"));
    }

    static void Merge(System.Text.Json.Nodes.JsonObject into, System.Text.Json.Nodes.JsonObject extra)
    {
        foreach (var (key, value) in extra.ToList())
        {
            if (key.StartsWith('_') || value is null) continue;
            if (into[key] is System.Text.Json.Nodes.JsonObject a && value is System.Text.Json.Nodes.JsonObject b) Merge(a, b);
            else if (!into.ContainsKey(key)) into[key] = value.DeepClone();
        }
    }

    public string? CoreError { get; private set; }
    public bool HasCore => Backend is not null && CoreError is null;

    public static async Task<Services> Start(string root, string data)
    {
        var s = await Task.Run(() => new Services(root, data, noCore: Godot.OS.GetCmdlineUserArgs().Contains("--qa-no-core")));
        if (s.Backend is null) return s;
        try { await s.Backend.Ready; }
        catch (Exception e) { s.CoreError = e.Message; return s; }
        await s.EnsureExampleDecks();
        return s;
    }

    async Task EnsureExampleDecks()
    {
        bool added = false;
        // the example decks are for each set's own investigators (01001-01005, 02001-02005, 03001-03006; not the reprints 015xx …,
        // which the catalog now accepts as well since 11-22); later investigators get no example deck
        // The Path to Carcosa's six too (Yunke 11-20, for the Carcosa group picture); a Carcosa list the core rejects is skipped
        // with a warning rather than stopping the start, as their cards are newer to the table
        foreach (var investigator in Catalog.Investigators.Where(i => J.String(i, "code")[..2] is "01" or "02" or "03" && J.String(i, "code")[2..4] == "00" && CoreCatalog.IsInvestigator(J.String(i, "code"))))
        {
            var code = J.String(investigator, "code");
            if (Decks.All.Any(d => J.String(d.Value, "investigator_code") == code) || Decks.Dismissed("example-" + code)) continue;
            bool later = code.StartsWith("03");
            try
            {
                var schema = await Backend.BuildSchema(code);
                var draft = Catalog.Starter(code, schema);
                // checked as a game would start it: the random weakness (01000) drawn first (the core rejects the placeholder itself)
                var validation = await Backend.ValidateDeck(code, J.String(schema, "schemaId"), await Backend.PrepareStandaloneDeck(draft));
                if (J.String(validation, "status") != "valid")
                {
                    if (!later) throw new InvalidOperationException("示例牌组校验失败：" + Catalog.Name(code));
                    Godot.GD.PushWarning("示例牌组未通过校验：" + Catalog.Name(code) + " " + validation.GetRawText()); continue;
                }
                Decks.Put("example-" + code, draft); added = true;
            }
            catch (Exception e) when (later) { Godot.GD.PushWarning("示例牌组：" + Catalog.Name(code) + " " + e.Message); }
        }
        // "基础+第一循环": each investigator's deck from both boxes (Yunke 11-18 #2), added once; a list the core rejects is skipped
        // the example decks are for the investigators the table supports (the core set and The Dunwich Legacy); the catalog
        // also lists every later investigator for the arkham.build workshop (zz-build-cards.json), who get no example deck
        foreach (var investigator in Catalog.Investigators.Where(i => J.String(i, "code")[..2] is "01" or "02" && J.String(i, "code")[2..4] == "00" && CoreCatalog.IsInvestigator(J.String(i, "code"))))
        {
            var code = J.String(investigator, "code");
            if (Decks.Has("mixed-" + code) || Decks.Dismissed("mixed-" + code)) continue;
            try
            {
                var schema = await Backend.BuildSchema(code);
                var draft = Catalog.Mixed(code, schema);
                var validation = await Backend.ValidateDeck(code, J.String(schema, "schemaId"), await Backend.PrepareStandaloneDeck(draft));
                if (J.String(validation, "status") == "valid") { Decks.Put("mixed-" + code, draft); added = true; }
                else Godot.GD.PushWarning("复合牌组未通过校验：" + Catalog.Name(code) + " " + validation.GetRawText());
            }
            catch (Exception e) { Godot.GD.PushWarning("复合牌组：" + Catalog.Name(code) + " " + e.Message); }
        }
        if (added) Decks.Save();
    }

    public string Name(string code) => Catalog.Name(CoreCatalog.Normalize(code));
    public JsonElement Card(string code) => Catalog.Cards.TryGetValue(CoreCatalog.Normalize(code), out var c) ? c : default;

    public async ValueTask DisposeAsync() { if (Backend is not null) await Backend.DisposeAsync(); }
}

public sealed class DeckLibrary
{
    readonly string file;
    readonly Dictionary<string, JsonElement> decks = new();
    public DeckLibrary(string file)
    {
        this.file = file;
        if (File.Exists(file))
        {
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(file));
                foreach (var p in J.Props(doc.RootElement)) decks[p.Name] = p.Value.Clone();
            }
            catch (JsonException)
            {
                // a power cut left the file zero-filled or cut short (bug #7): keep it aside for recovery, start with an empty library
                // instead of failing at start-up
                try { File.Move(file, file + ".corrupt-" + DateTime.Now.ToString("yyyyMMdd-HHmmss"), true); } catch (IOException) { }
                Godot.GD.PushWarning("牌组库文件损坏，已另存并以空库启动：" + file);
            }
        }
    }
    public IEnumerable<KeyValuePair<string, JsonElement>> All => decks.OrderBy(d => d.Key.StartsWith("example-") ? 1 : d.Key.StartsWith("mixed-") ? 2 : 0).ThenBy(d => d.Key);   // own decks, then examples, then 基础+第一循环
    public IEnumerable<KeyValuePair<string, JsonElement>> For(string investigator) => All.Where(d => J.String(d.Value, "investigator_code") == investigator);
    public JsonElement Get(string key) => decks[key];
    public bool Has(string key) => decks.ContainsKey(key);
    public void Put(string key, JsonElement deck) => decks[key] = deck.Clone();
    public void Remove(string key) { decks.Remove(key); if (key.StartsWith("example-") || key.StartsWith("mixed-") || key.StartsWith("completed-")) Dismiss(key); }

    // Example decks the player deleted stay deleted (Yunke 11-23: "卡组删除后还会出现" — the start-up added every missing
    // example deck again). Kept next to decks.json.
    string DismissedFile => Path.Combine(Path.GetDirectoryName(file) ?? "", "decks-dismissed.json");
    HashSet<string>? dismissed;
    public bool Dismissed(string key)
    {
        if (dismissed is null)
        {
            try { dismissed = File.Exists(DismissedFile) ? JsonSerializer.Deserialize<HashSet<string>>(File.ReadAllText(DismissedFile)) ?? new() : new(); }
            catch { dismissed = new(); }
        }
        return dismissed.Contains(key);
    }
    void Dismiss(string key)
    {
        Dismissed(key);
        if (dismissed!.Add(key)) SafeFile.WriteAllText(DismissedFile, JsonSerializer.Serialize(dismissed));
    }
    public string NewKey() => "deck-" + DateTime.Now.ToString("yyyyMMdd-HHmmss-fff");
    public void Save() => SafeFile.WriteAllText(file, JsonSerializer.Serialize(decks, new JsonSerializerOptions { WriteIndented = true }));
}

public sealed class UserSettings
{
    public double MusicVolume { get; set; } = 0.6;
    public double EffectsVolume { get; set; } = 0.8;
    public bool Fullscreen { get; set; } = false;
    public bool ReducedMotion { get; set; } = false;
    public int TextScale { get; set; } = 100;      // percent, App.ScaleMin..ScaleMax (a free slider in the settings: 显示比例)
    public int FrameZoom { get; set; } = 100;      // percent, App.ZoomMin..ZoomMax: how much of the screen the whole picture takes (缩放)
    public bool AutoSkip { get; set; } = true;     // only trivially single, decision-free steps
    public bool ConfirmEndTurn { get; set; } = true;
    // fast abilities that have nothing to do with the test, offered in a skill test's player windows too (bug #10: 天文地图集、
    // 卡纳玛戈斯之杖… could not be used in a test). The rules allow it; the core hides them unless the seat says otherwise
    // (globalSettings.ignoreUnrelatedSkillTestTriggers, on by default; the 0712 html version had no switch for it)
    public bool FastInSkillTests { get; set; } = true;
    public string Difficulty { get; set; } = "Standard";
    public string DeckPreviewMode { get; set; } = "strips";   // workshop preview: strips / small / large
    public string DeckEditorPoolMode { get; set; } = "large";  // deck editor's card pool: large / small / text
    // hover (Yunke 11-20): how long the pointer rests before the hover box opens, and what it shows — "text" (the card's words
    // in a dark box) or "image" (the printed card itself, framed, as the html version zooms a card)
    public double HoverDelay { get; set; } = 1.0;
    public string HoverMode { get; set; } = "text";
    [System.Text.Json.Serialization.JsonIgnore] public string File { get; private set; } = "";

    public static UserSettings Load(string file)
    {
        UserSettings s;
        try { s = System.IO.File.Exists(file) ? JsonSerializer.Deserialize<UserSettings>(System.IO.File.ReadAllText(file)) ?? new() : new(); }
        catch { s = new(); }
        s.File = file; return s;
    }
    public void Save() => SafeFile.WriteAllText(File, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
}

// Writes a file so that a power cut cannot leave it half-written (bug #7): the text goes to <file>.tmp, is flushed to disk,
// and then replaces the old file in one move.
public static class SafeFile
{
    public static void WriteAllText(string path, string text)
    {
        System.IO.Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tmp = path + ".tmp";
        using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
        using (var w = new StreamWriter(fs, new System.Text.UTF8Encoding(false)))
        {
            w.Write(text); w.Flush(); fs.Flush(true);
        }
        File.Move(tmp, path, true);
    }
}
