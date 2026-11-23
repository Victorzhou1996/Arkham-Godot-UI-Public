namespace ArkhamUI;

// A campaign's starting chaos bag, by difficulty. Source: the rules core's Campaigns/<Campaign>/ChaosBag.hs.
// Each entry is one token; a face's chance of being drawn is its count divided by the bag size (the core shuffles the
// bag and takes the first token). In play the table reads the bag the core actually holds; this is for the prepare screen.
public static class ChaosBagRules
{
    // Night of the Zealot (Campaigns/NightOfTheZealot/ChaosBag.hs)
    static readonly Dictionary<string, string[]> bags = new()
    {
        ["Easy"] = new[] { "PlusOne", "PlusOne", "Zero", "Zero", "Zero", "MinusOne", "MinusOne", "MinusOne", "MinusTwo", "MinusTwo", "Skull", "Skull", "Cultist", "Tablet", "AutoFail", "ElderSign" },
        ["Standard"] = new[] { "PlusOne", "Zero", "Zero", "MinusOne", "MinusOne", "MinusOne", "MinusTwo", "MinusTwo", "MinusThree", "MinusFour", "Skull", "Skull", "Cultist", "Tablet", "AutoFail", "ElderSign" },
        ["Hard"] = new[] { "Zero", "Zero", "Zero", "MinusOne", "MinusOne", "MinusTwo", "MinusTwo", "MinusThree", "MinusThree", "MinusFour", "MinusFive", "Skull", "Skull", "Cultist", "Tablet", "AutoFail", "ElderSign" },
        ["Expert"] = new[] { "Zero", "MinusOne", "MinusOne", "MinusTwo", "MinusTwo", "MinusThree", "MinusThree", "MinusFour", "MinusFour", "MinusFive", "MinusSix", "MinusEight", "Skull", "Skull", "Cultist", "Tablet", "AutoFail", "ElderSign" },
    };

    // The Dunwich Legacy (Campaigns/TheDunwichLegacy/ChaosBag.hs): the same numbers, no tablet token
    public static string[] Bag(string difficulty, string campaign = "01")
    {
        // every other campaign, return-to and side story: its chaos bags as the html version lists them (campaigns.json,
        // side-stories.json → campaigns-catalog.json); the table reads the bag the core actually holds once the game runs
        if (campaign is not ("01" or "02" or "03") && Campaigns.All.FirstOrDefault(c => c.Id == campaign) is { } other && other.Bags.Count > 0)
            return other.Bags.TryGetValue(difficulty, out var ob) ? ob : other.Bags.TryGetValue("Standard", out var sb) ? sb : other.Bags.Values.First();
        var b = bags.TryGetValue(difficulty, out var x) ? x : bags["Standard"];
        if (campaign == "02") return b.Where(f => f != "Tablet").ToArray();
        // The Path to Carcosa (Campaigns/ThePathToCarcosa/ChaosBag.hs): the same numbers, three skulls, no cultist, no tablet
        if (campaign == "03") { var l = b.Where(f => f is not ("Tablet" or "Cultist")).ToList(); l.Insert(l.IndexOf("Skull"), "Skull"); return l.ToArray(); }
        return b;
    }

    // Penalty text for the scenario's own tokens, from the scenario card (locale <campaign>.<scenario>.tokens).
    // (the elder sign is never a scenario token: the html locale's theDunwichLegacy.lostInTimeAndSpace.tokens.*.elderSign is a stray copy of
    // the elder thing text — the rules source changes only skull, cultist, tablet and elder thing there)
    public static readonly string[] ScenarioFaces = { "Skull", "Cultist", "Tablet", "ElderThing" };

    public static string PenaltyKey(string difficulty, string face, string scenario = "01104")
    {
        string group = difficulty is "Hard" or "Expert" ? "hardExpert" : "easyStandard";
        string token = face.Length == 0 ? face : char.ToLowerInvariant(face[0]) + face[1..];   // elderThing
        return Campaigns.ScenarioSection(scenario) + ".tokens." + group + "." + token;
    }
    // locale scope of a scenario inside its campaign section
    public static string ScenarioKey(string scenario) => Campaigns.ForScenario(scenario).Find(Gathering.CoreCatalog.Normalize(scenario))?.Key ?? "theGathering";
}
