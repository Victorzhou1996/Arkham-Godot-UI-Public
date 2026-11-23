using System.Text.Json;
using ArkhamUI;
using Gathering;

// The campaign reward of tools/ai/campaign_reward.py in C#, for the search teacher: when a choice ends the chapter (a resign,
// the last investigator defeated) the value network has never seen such a state, so the chapter's real worth is used instead:
// what it added to the campaign log, the trauma it left, an investigator lost — the same target train2.py gives V.
static class CampaignReward
{
    static readonly Dictionary<string, double> Keys = new()
    {
        ["YourHouseIsStillStanding"] = 2, ["YourHouseHasBurnedToTheGround"] = 0, ["GhoulPriestIsStillAlive"] = -2,
        ["LitaWasForcedToFindOthersToHelpHerCause"] = 0, ["ItIsPastMidnight"] = -1,
        ["ArkhamSuccumbedToUmordhothsTerribleVengeance"] = -6, ["TheRitualToSummonUmordhothWasBroken"] = 6,
        ["TheInvestigatorsRepelledUmordhoth"] = 6, ["TheInvestigatorsSacrificedLitaChantlerToUmordhoth"] = 3,
    };
    static readonly Dictionary<string, double> Sets = new() { ["CultistsWeInterrogated"] = 1, ["CultistsWhoGotAway"] = -2 };

    static readonly string[] FinalEndings = { "Succumbed", "Devoured", "Broken", "Repelled", "Sacrificed" };

    public sealed record Rec(HashSet<string> K, Dictionary<string, int> S, Dictionary<string, (int T, bool Lost)> Tr);

    public static Rec Read(TableModel m)
    {
        var log = J.Get(J.Get(J.Get(m.Game, "mode"), "This"), "log");
        var k = J.Items(J.Get(log, "recorded")).Select(x => J.String(x, "contents")).ToHashSet();
        var s = new Dictionary<string, int>();
        foreach (var e in J.Items(J.Get(log, "recordedSets")))
        {
            var parts = J.Items(e).ToArray();
            if (parts.Length == 2) s[J.String(parts[0], "contents")] = J.Items(parts[1]).Count();
        }
        var tr = m.Investigators.ToDictionary(i => CoreCatalog.Normalize(i.Code), i => (J.Int(i.Raw, "physicalTrauma") + J.Int(i.Raw, "mentalTrauma"),
            J.Get(i.Raw, "killed").ValueKind == JsonValueKind.True || J.Get(i.Raw, "drivenInsane").ValueKind == JsonValueKind.True));
        return new Rec(k, s, tr);
    }

    static double Key(string k) => Keys.TryGetValue(k, out var v) ? v : k.Contains("Succumbed") || k.Contains("Devoured") ? -6 : k.Contains("Broken") || k.Contains("Repelled") ? 6 : 0;

    public static double Chapter(Rec prev, Rec cur)
    {
        var added = cur.K.Except(prev.K).ToList();
        double r = added.Sum(Key);
        r += cur.S.Sum(kv => Sets.GetValueOrDefault(kv.Key) * (kv.Value - prev.S.GetValueOrDefault(kv.Key)));
        // the last chapter (地底噬魔's resolution recorded): the campaign ends with it, so its trauma and an investigator lost
        // in it change nothing later — only the ending counts (campaign_reward.py the same)
        if (added.Any(k => FinalEndings.Any(k.Contains))) return r;
        foreach (var (code, (t, lost)) in cur.Tr)
        {
            var p = prev.Tr.GetValueOrDefault(code);
            r -= t - p.T;
            if (lost && !p.Lost) r -= 8 + 6;   // lost for the campaign: the campaign is as good as failed (train2: the cut campaign's −6)
        }
        return r;
    }

    // train2.py value(): the chapter's own progress, 0.25 of it in the target
    public static double Progress(int[] p)
    {
        double act = p[0], clues = p[1], hp = p[2], san = p[3], down = p[4], doom = p[5], n = Math.Max(1, p[6]), enemies = p[7];
        double threshold = p[8], agenda = p[9], maxHp = Math.Max(1, p[10]), maxSan = Math.Max(1, p[11]), victory = p[12];
        double margin = threshold > 0 ? threshold - doom : 9;
        return 12 * act - 6 * Math.Max(0, agenda - 1) + 0.7 * clues / n + 4 * (hp / maxHp + san / maxSan)
            - 10 * down - 1.5 * Math.Max(0, 3 - margin) - 1.0 * enemies + 2 * victory
            - 4 * Math.Max(0, 2.5 - san / n) - 3 * Math.Max(0, 2.5 - hp / n);   // one treachery from insanity / defeat
    }

    public static double Target(Rec start, TableModel m) => Chapter(start, Read(m)) / 4.0 + 0.25 * Progress(AiNet.Progress(m)) / 20.0;
}
