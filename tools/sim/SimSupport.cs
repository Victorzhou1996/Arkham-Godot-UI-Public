using Gathering;
using System.Text.Json;

namespace ArkhamUI;

// The simulator's stand-in for the game's Services: the card catalog, the Chinese locale and the question presenter
// (what TableModel needs). Built the same way as app/Scripts/Services.cs, without the Godot parts.
public sealed class Services
{
    public CoreCatalog Catalog { get; }
    public ClientLocale Locale { get; }
    public QuestionPresenter Presenter { get; }
    public Services(string root)
    {
        Catalog = new CoreCatalog(Path.Combine(root, "app/assets/data/core-cards.json"));
        var merged = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(Path.Combine(root, "app/assets/data/locale-zh.json")))!.AsObject();
        string extra = Path.Combine(root, "app/assets/data/locale-zh-extra.json");
        // merged key by key at every level, as the game does (Services.Merge): keys in locale-zh.json win
        if (File.Exists(extra)) Merge(merged, System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(extra))!.AsObject());
        using var dictionary = JsonDocument.Parse(merged.ToJsonString());
        Locale = new ClientLocale(dictionary.RootElement) { CardName = c => Catalog.Name(c), CardNameFromEnglish = Catalog.NameFromEnglish };
        Presenter = new QuestionPresenter(Catalog, Locale);
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
    public string Name(string code) => Catalog.Name(CoreCatalog.Normalize(code));
    public JsonElement Card(string code) => Catalog.Cards.TryGetValue(CoreCatalog.Normalize(code), out var c) ? c : default;
}

// Test odds for the simulator. During a test the core's own per-face values (valueBreakdown) are used, as the table does;
// outside a test a simple reading of the reference cards (the table's TableScreen.Odds has the full version; this one
// is close enough for choosing actions — the difference is noted in docs/AI-PLAY-PLAN-20261119.md).
public static class SimOdds
{
    public static int? Modifier(TableModel m, string face)
    {
        if (m.InSkillTest)
            foreach (var t in J.Items(J.Get(J.Get(m.SkillTest, "valueBreakdown"), "tokens")))
            {
                if (J.String(t, "face") != face) continue;
                if (J.Get(t, "autoFail").ValueKind == JsonValueKind.True) return null;
                if (J.Get(t, "autoSuccess").ValueKind == JsonValueKind.True) return 100;
                var v = J.Get(t, "value"); int extra = J.Get(t, "revealsAnother").ValueKind == JsonValueKind.True ? -2 : 0;
                return (v.ValueKind == JsonValueKind.Number ? v.GetInt32() : 0) + extra;
            }
        bool hard = m.Difficulty is "Hard" or "Expert";
        switch (face)
        {
            case "PlusOne": return 1; case "Zero": return 0; case "MinusOne": return -1; case "MinusTwo": return -2; case "MinusThree": return -3;
            case "MinusFour": return -4; case "MinusFive": return -5; case "MinusSix": return -6; case "MinusSeven": return -7; case "MinusEight": return -8;
            case "AutoFail": return null; case "ElderSign": return 1;
        }
        if (Scenario(m) == "01104")
        {
            var here = m.MyLocation;
            int ghouls = here is null ? 0 : m.EnemiesHere(here.Id).Count(e => J.String(AiHost.Card(e.Code), "traits").Contains("食尸鬼"));
            return face switch { "Skull" => hard ? -2 : -ghouls, "Cultist" => -1, _ => hard ? -4 : -2 };
        }
        return face switch { "Skull" => -2, "Cultist" => -2, "Tablet" => -3, _ => -4 };
    }

    public static double PassChance(TableModel m, int value, int difficulty)
    {
        var faces = m.ChaosFaces.ToArray(); if (faces.Length == 0) return 0;
        var vb = m.InSkillTest ? J.Get(m.SkillTest, "valueBreakdown") : default;
        bool failTies = vb.ValueKind == JsonValueKind.Object && J.Get(vb, "failTies").ValueKind == JsonValueKind.True;
        int pass = faces.Count(f => Modifier(m, f) is int mod && (failTies ? value + mod > difficulty : value + mod >= difficulty));
        return (double)pass / faces.Length;
    }

    public static string Scenario(TableModel m) { var id = J.String(m.Scenario, "id"); return id.Length > 0 ? CoreCatalog.Normalize(id) : "01104"; }
}
