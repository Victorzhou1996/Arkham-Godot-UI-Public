using System.Text.Json;

namespace ArkhamUI;

// What the rules core has changed about a card, read from its own state (game.gameModifiers: [target, [modifier …]] pairs),
// so the table shows the numbers the core plays with instead of the printed ones (Yunke 11-19: a customized Hunter's Armor
// with 轻量 costs 3, not its printed 4; with 耐用 it has 4 health, not 2). The core stays a black box: only its output is read.
public static class CoreModifiers
{
    static IEnumerable<(JsonElement Target, JsonElement Type)> All(JsonElement game)
    {
        // the save calls it gameModifiers, the table's view modifiers; same shape
        var list = J.Get(game, "modifiers").ValueKind == JsonValueKind.Array ? J.Get(game, "modifiers") : J.Get(game, "gameModifiers");
        foreach (var pair in J.Items(list))
        {
            if (pair.ValueKind != JsonValueKind.Array || pair.GetArrayLength() != 2) continue;
            foreach (var m in J.Items(pair[1])) yield return (pair[0], J.Get(m, "type"));
        }
    }

    static int Amount(JsonElement type) => J.Get(type, "contents") is { ValueKind: JsonValueKind.Number } n ? n.GetInt32() : 0;

    // a cost matcher that names this very card (CardWithId); other matchers (by trait, type …) are left to the core's own
    // check when the card is played
    static bool Names(JsonElement matcher, string cardId) => J.String(matcher, "tag") == "CardWithId" && J.String(matcher, "contents") == cardId;

    // A card in hand: its cost after the core's cost changes on it, or null when nothing changes it.
    public static int? Cost(JsonElement game, string cardId, int? printed)
    {
        if (printed is not int p || cardId.Length == 0) return null;
        int delta = 0;
        foreach (var (_, type) in All(game))
        {
            string tag = J.String(type, "tag");
            if (tag is not ("ReduceCostOf" or "IncreaseCostOf")) continue;
            var c = J.Get(type, "contents");
            if (c.ValueKind != JsonValueKind.Array || c.GetArrayLength() != 2 || !Names(c[0], cardId) || !c[1].TryGetInt32(out int n)) continue;
            delta += tag == "ReduceCostOf" ? -n : n;
        }
        if (App.I.QaRunning && delta != 0) Godot.GD.Print("QA_COST " + cardId[..8] + " " + p + "→" + (p + delta));
        return delta == 0 ? null : Math.Max(0, p + delta);
    }

    // An asset in play: extra health and sanity the core gives it (HealthModifier / SanityModifier on the asset).
    public static (int Health, int Sanity) Unit(JsonElement game, string assetId)
    {
        int hp = 0, san = 0;
        foreach (var (target, type) in All(game))
        {
            if (J.String(target, "contents") != assetId) continue;
            switch (J.String(type, "tag"))
            {
                case "HealthModifier": hp += Amount(type); break;
                case "SanityModifier": san += Amount(type); break;
            }
        }
        return (hp, san);
    }
}
