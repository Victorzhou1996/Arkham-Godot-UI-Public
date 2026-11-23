using Gathering;
using System.Text.Json;

namespace ArkhamUI;

// Card knowledge for the AI: printed cost and skill icons (from the *-costs.json files the table already uses), and a
// short rule per player card for when it is worth playing / committing. Unknown cards fall back to type-based defaults.
public static class AiKnow
{
    public sealed record Info(string Type, int? Cost, string[] Skills);
    static Dictionary<string, Info>? infos;

    static Dictionary<string, Info> Infos
    {
        get
        {
            if (infos is not null) return infos;
            infos = new();
            try
            {
                foreach (var file in Directory.GetFiles(Path.Combine(AiHost.AssetsDir, "data"), "*-costs.json"))
                {
                    using var doc = JsonDocument.Parse(File.ReadAllText(file));
                    foreach (var p in doc.RootElement.EnumerateObject())
                    {
                        if (p.Value.ValueKind != JsonValueKind.Object) continue;
                        var cost = J.Get(p.Value, "cost");
                        infos.TryAdd(p.Name, new Info(J.String(p.Value, "type"), cost.ValueKind == JsonValueKind.Number ? cost.GetInt32() : null,
                            J.Items(J.Get(p.Value, "skills")).Select(s => s.GetString() ?? "").ToArray()));
                    }
                }
            }
            catch (Exception e) { AiHost.Log("AI_KNOW_LOAD_FAIL " + e.Message); }
            return infos;
        }
    }

    public static Info? Of(string code) => Infos.TryGetValue(CoreCatalog.Normalize(code), out var i) ? i : null;
    public static int Cost(string code) => Of(code)?.Cost ?? 0;
    public static string Type(string code) => Of(code)?.Type ?? J.String(AiHost.Card(CoreCatalog.Normalize(code)), "type_code");
    public static string Text(string code) => J.String(AiHost.Card(CoreCatalog.Normalize(code)), "text");
    public static string Traits(string code) => J.String(AiHost.Card(CoreCatalog.Normalize(code)), "traits");
    public static bool Fast(string code) => Text(code).StartsWith("快速");
    public static bool Unique(string code) => J.Get(AiHost.Card(CoreCatalog.Normalize(code)), "is_unique").ValueKind == JsonValueKind.True;
    // 午夜假面: only the unique cultists (the cultist deck's five and the Masked Hunter) go to the victory display — interrogated;
    // 祭司 / 密会巫师 from the encounter deck are cultists too but not unique: killing them adds nothing to the campaign log
    public static bool WantedCultist(string code) => Traits(code).Contains("异教徒") && Unique(code);

    // Icons a card adds to a test of these skills (core skill names "SkillIntellect" or plain "intellect"); wild always counts.
    public static int Icons(string code, IEnumerable<string> testSkills)
    {
        var info = Of(code); if (info is null) return 0;
        var want = testSkills.Select(s => s.Replace("Skill", "").ToLowerInvariant()).ToHashSet();
        return info.Skills.Count(s => s == "wild" || want.Contains(s));
    }

    // Weapons: extra combat, extra damage, and which skill the attack uses (Shrivelling: willpower).
    public static (int Combat, int Damage, string Skill)? Weapon(string code, TableModel m, Entity me) => CoreCatalog.Normalize(code) switch
    {
        "01016" => (1, 1, "combat"),
        "01006" => (m.MyLocation is { } l && m.Clues(l) > 0 ? 3 : 1, 1, "combat"),
        "01020" => (1, m.EngagedWith(me).Count() <= 1 ? 1 : 0, "combat"),
        "01047" => (2, 0, "combat"),
        "01060" => (0, 1, "willpower"),
        "01029" => (3, 2, "combat"),     // shotgun (4): +3, damage = the margin (1–5): about 2 more
        _ => null
    };

    public static bool IsWeapon(string code) => CoreCatalog.Normalize(code) is "01016" or "01006" or "01020" or "01047" or "01060" or "01029";

    // How much the AI wants to play this card from hand right now (0 = never). The context carries the situation.
    public static double PlayValue(string code, AiContext x)
    {
        code = CoreCatalog.Normalize(code);
        double v = Raw(code, x);
        // assets are best put into play while nothing threatens (the first act): a small push to set up early
        if (v >= 3 && Type(code) == "asset" && x.Engaged == 0 && x.EnemiesOnMap == 0) v += 1.5;
        // sanity management: an ally with sanity soaks horror (assignment sends it there while sanity is low)
        int soak = AiUpgrade.Soak(code).Sanity;   // (the card data has no health / sanity: AiUpgrade keeps them)
        if (Type(code) == "asset" && soak > 0 && x.San <= 3) v += 2 + 2 * soak;
        return v;
    }

    static double Raw(string code, AiContext x)
    {
        string text = Text(code);
        if (text.Contains("撤退")) return 0;                         // never a card that resigns (goal layer decides that)
        string type = Type(code);
        bool clueGame = x.Goal.CluesMatter;
        bool fights = x.EnemiesOnMap > 0 || x.Goal.TargetEnemy is not null;
        bool haveWeapon = x.MyAssetCodes.Any(IsWeapon);
        switch (code)
        {
            // weapons
            case "01016": case "01006": case "01020": case "01047": case "01029":
                return haveWeapon ? 1.5 : x.Combat >= 3 || fights ? 9 : 4;
            case "01060": return haveWeapon ? 1.5 : x.Willpower >= 4 ? 9 : 4;
            // constant boosts and allies
            // magnifying glass (0 / 1); a second one takes the other hand — 11-22: Roland played a second glass beside his
            // weapon and his first glass, and the hand-slot overflow made him discard the weapon before the Ghoul Priest
            case "01030": case "01040": return x.MyAssetCodes.Any(c => c is "01030" or "01040") ? 1.5 : clueGame ? (x.Intellect >= 3 ? 8 : 6) : 3;
            // experience cards (AiUpgrade buys them between chapters)
            case "01095": return x.San <= 4 ? 11 : 8;                          // elder sign amulet: 4 sanity soak
            case "01094": return x.Hp <= 5 ? 9 : 7;                            // bulletproof vest: 4 health soak
            case "01027": return 6;                                            // police badge (2)
            case "01028": return 8;                                            // beat cop (2)
            case "01054": return 10;                                           // Leo De Luca (1)
            case "01082": return 8;                                            // Aquinnah (1)
            case "01055": return 6;                                            // cat burglar (1)
            case "01026": return haveWeapon ? 4 : 0;                           // extra ammunition (1)
            case "01033": return 9;                                            // Dr. Milan Christopher
            case "01018": return 8;                                            // beat cop
            case "01048": return 10;                                           // Leo De Luca
            case "01059": return 5;                                            // holy rosary
            case "01008": return 8;                                            // Daisy's tote bag
            case "01012": return 7;                                            // Heirloom of Hyperborea
            case "01032": return 6;                                            // research librarian
            case "01031": return 6;                                            // old book of lore
            case "01035": return 4;                                            // medical texts
            case "01034": return 5;                                            // hyperawareness
            case "01058": return 4;                                            // forbidden knowledge
            case "01075": return 4;                                            // lucky rabbit's foot
            case "01046": return 3;                                            // pickpocketing
            case "01063": return x.DoomMargin >= 4 ? 2 : 0;                    // arcane initiate puts doom: rarely
            // events
            case "01037": return x.CluesHere > 0 && clueGame ? 11 : 0;          // working a hunch
            case "01088": return x.Resources <= 2 && x.AffordWant ? 6 : x.Resources <= 1 ? 3 : 0.5;   // emergency cache
            case "01022": return x.CluesHere > 0 ? 10 : 0;                     // evidence! (after a defeat)
            case "01079": return x.CluesHere > 0 ? 10 : 0;                     // look what I found!
            case "01064": return x.CluesHere >= 2 && clueGame && x.Hp > 3 && x.San > 3 ? 7 : 0;   // drawn to the flame
            case "01050": return x.Engaged > 0 && (x.Hp <= 3 || x.EngagedThreat >= 3) ? 9 : 0;  // elusive
            case "01052": return x.ExhaustedEnemiesHere > 0 ? 8 : 0;           // sneak attack
            case "01051": return 0;                                            // backstab: chosen as a fight in Decide
            case "01010": return x.EnemiesOnMap > 0 ? 3 : 0;                   // on the lam
            case "01036": return x.Engaged > 0 && x.Intellect > Math.Max(x.Combat, x.Agility) ? 6 : 0;   // mind over matter
            case "01013": return x.DoomMargin >= 4 || x.San <= 2 ? 5 : 0;      // dark memory: play (1 doom) rather than take 2 horror each turn, unless doom is tight
            case "01023": return x.Hp <= 3 || x.San <= 3 ? 9 : 5;              // dodge (only offered when an attack comes)
            case "01065": return x.San >= 3 ? 7 : 0;                           // ward of protection
            case "01080": return 8;                                            // lucky! (only offered when a test is failing)
        }
        if (Traits(code).Contains("弱点")) return 0;
        return Default(code, type, text, x);
    }

    static double Default(string code, string type, string text, AiContext x)
    {
        return type switch { "asset" => 4, "event" => text.Contains("发现") && x.CluesHere > 0 ? 6 : 2, _ => 0 };
    }

    // How costly it is to give the card up as a commit (skills are made for it; good assets are worth keeping).
    public static double CommitCost(string code, AiContext x)
    {
        code = CoreCatalog.Normalize(code);
        string type = Type(code);
        if (type == "skill") return code == "01093" ? 2.5 : 1.0;   // unexpected courage: keep for the important tests
        return 1.5 + 0.6 * PlayValue(code, x);
    }

    // Skill cards whose success effect adds value in this kind of test.
    public static double CommitBonus(string code, string action)
    {
        return CoreCatalog.Normalize(code) switch
        {
            "01039" when action == "Investigate" => 6,   // deduction: +1 clue
            "01025" when action == "Fight" => 5,         // vicious blow: +1 damage
            "01089" or "01090" or "01091" => 1.5,        // draw a card on success
            "01067" => 1.5,                              // fearless: heal 1 horror
            _ => 0
        };
    }
}
