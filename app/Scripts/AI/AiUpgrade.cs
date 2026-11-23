using Gathering;
using System.Text.Json;

namespace ArkhamUI;

// Spending experience between chapters (docs/AI-PLAY-PLAN-20261119.md §13): the base game (core set) cards the AI can buy,
// what each is worth to it, and which card of the deck makes room. The core prices the change itself (a card's level, or the
// difference when a lower level of the same card is replaced) and checks the deck; this only proposes the list.
public static class AiUpgrade
{
    // deck-building access of the core set investigators (printed on the investigator cards): class, highest level
    static readonly Dictionary<string, (string Cls, int Max)[]> Access = new()
    {
        ["01001"] = new[] { ("guardian", 5), ("seeker", 2), ("neutral", 5) },
        ["01002"] = new[] { ("seeker", 5), ("mystic", 2), ("neutral", 5) },
        ["01003"] = new[] { ("rogue", 5), ("guardian", 2), ("neutral", 5) },
        ["01004"] = new[] { ("mystic", 5), ("survivor", 2), ("neutral", 5) },
        ["01005"] = new[] { ("survivor", 5), ("rogue", 2), ("neutral", 5) },
    };

    // how much a card is worth in the AI's deck (one scale for buying and for cutting). Cards the AI plays well score high.
    static readonly Dictionary<string, double> Worth = new()
    {
        // experience cards of the core set
        ["01026"] = 2, ["01027"] = 5, ["01028"] = 8, ["01029"] = 9,                        // extra ammo, police badge, beat cop (2), shotgun
        ["01040"] = 7, ["01041"] = 4, ["01042"] = 4, ["01043"] = 4,                        // magnifying glass (1), disc, encyclopedia, cryptic research
        ["01054"] = 10, ["01055"] = 6, ["01056"] = 2, ["01057"] = 3,                       // Leo De Luca (1), cat burglar, sure gamble, hot streak
        ["01068"] = 2, ["01069"] = 5, ["01070"] = 4, ["01071"] = 5,                        // mind wipe, blinding light, book of shadows, grotesque statue
        ["01082"] = 8, ["01083"] = 4, ["01084"] = 7, ["01085"] = 4,                        // Aquinnah (1), close call, lucky! (2), will to survive
        ["01094"] = 8, ["01095"] = 10,                                                     // bulletproof vest, elder sign amulet
        // level 0 cards of the starting decks (what is cut first)
        ["01016"] = 7, ["01020"] = 7, ["01030"] = 6, ["01033"] = 8, ["01034"] = 3, ["01037"] = 5, ["01039"] = 2.5,
        ["01022"] = 4, ["01023"] = 5, ["01025"] = 3, ["01088"] = 3, ["01089"] = 3.5, ["01090"] = 2.5, ["01091"] = 2, ["01093"] = 4,
        ["01018"] = 7, ["01048"] = 9, ["01080"] = 6, ["01031"] = 5, ["01032"] = 5, ["01035"] = 3, ["01036"] = 3,
    };

    // slots a card takes (only the ones a second card would fight over)
    static readonly Dictionary<string, string> Slot = new()
    {
        ["01027"] = "accessory", ["01095"] = "accessory", ["01094"] = "body", ["01059"] = "accessory",
        ["01018"] = "ally", ["01028"] = "ally", ["01033"] = "ally", ["01032"] = "ally", ["01048"] = "ally", ["01054"] = "ally", ["01063"] = "ally", ["01082"] = "ally",
    };

    public sealed record Buy(string Code, string? Removed, int Cost, double Gain);

    static JsonElement Card(string code) => AiHost.Card(code);
    static int Level(string code) => J.Int(Card(code), "xp");
    static string Name(string code) => J.String(Card(code), "real_name") is { Length: > 0 } n ? n : J.String(Card(code), "name");

    // the deck after spending up to xp experience; mentalTrauma / physicalTrauma make soak worth more (the campaign so far)
    public static (Dictionary<string, int> Slots, List<Buy> Bought) Plan(string investigator, Dictionary<string, int> slots, int xp, int mentalTrauma, int physicalTrauma)
    {
        investigator = CoreCatalog.Normalize(investigator);
        var deck = new Dictionary<string, int>(slots);
        var bought = new List<Buy>();
        if (!Access.TryGetValue(investigator, out var access) || xp <= 0) return (deck, bought);
        var fixedCards = J.Props(J.Get(J.Get(Card(investigator), "deck_requirements"), "card")).Select(p => p.Name).ToHashSet();
        bool Removable(string c) => !fixedCards.Contains(c) && !J.String(Card(c), "subtype_code").Contains("weakness") && Level(c) == 0 && Card(c).ValueKind == JsonValueKind.Object;
        var learned = Learned(investigator);
        double Value(string c, bool asBuy)
        {
            double v = Worth.TryGetValue(c, out var w) ? w : J.String(Card(c), "type_code") switch { "asset" => 4, "event" => 3, _ => 2.5 };
            if (c is "01095" or "01082") v += 3 * mentalTrauma;      // sanity soak: the campaign's mental trauma is what ends chapter 3
            if (c is "01094") v += 2 * physicalTrauma;
            // what training measured (the card memory, tools/ai/card_worth.py): campaign score gained by buying it / lost by cutting it
            if (learned.TryGetValue(c, out var m)) v += asBuy ? m.Buy : -m.Cut;
            return v;
        }
        var candidates = Worth.Keys.Where(c => Level(c) > 0 && access.Any(a => a.Cls == J.String(Card(c), "faction_code") && Level(c) <= a.Max)).ToList();
        // training (sim flag explore): now and then the first buy is any affordable card and the cut any of the weakest few, so the
        // card memory sees what the other choices are worth
        bool explore = Explore > 0 && Rng.NextDouble() < Explore;
        string? first = First ?? (explore ? candidates.Where(c => Level(c) <= xp).OrderBy(_ => Rng.Next()).FirstOrDefault() : null);
        int left = xp;
        for (int guard = 0; guard < 12; guard++)
        {
            // the first buy: First (the sim's A/B runs, tools/ai/tune_upgrade.py) or the explored card, when it can be bought
            var best = Pick(guard == 0 ? first : null, explore && guard == 0) ?? (guard == 0 && first is not null ? Pick(null, false) : null);
            if (best is null) break;
            deck[best.Code] = deck.GetValueOrDefault(best.Code) + 1;
            if (best.Removed is { } r) { if (--deck[r] <= 0) deck.Remove(r); }
            left -= best.Cost;
            bought.Add(best);
        }
        return (deck, bought);

        Buy? Pick(string? only, bool randomCut)
        {
            Buy? best = null; double bestRate = 0;
            foreach (var c in candidates)
            {
                if (only is not null && c != only) continue;
                string name = Name(c);
                int copies = deck.Where(kv => Name(kv.Key) == name).Sum(kv => kv.Value);
                // the same card at a lower level is replaced (the core charges the difference); otherwise the weakest card goes
                string? same = deck.Keys.Where(k => k != c && Name(k) == name && Level(k) < Level(c) && Removable(k)).OrderBy(Level).FirstOrDefault();
                if (same is null && copies >= 2) continue;
                int cost = same is not null ? Math.Max(1, Level(c) - Level(same)) : Level(c);
                if (cost > left) continue;
                double v = Value(c, true);
                if (deck.GetValueOrDefault(c) > 0) v *= 0.6;                                   // a second copy: drawn sooner, never both in play
                if (Slot.TryGetValue(c, out var slot) && deck.Keys.Any(k => k != c && k != same && Slot.GetValueOrDefault(k) == slot)) v *= 0.6;   // the slot is taken
                var cuts = deck.Keys.Where(Removable).Where(k => !(Worth.ContainsKey(k) && Level(k) > 0)).OrderBy(k => Value(k, false)).ToList();
                string? removed = same ?? (randomCut ? cuts.Take(4).OrderBy(_ => Rng.Next()).FirstOrDefault() : cuts.FirstOrDefault());
                if (removed is null) continue;
                double gain = v - Value(removed, false);
                if (gain <= 0.5 && only is null) continue;
                double rate = only is not null ? double.MaxValue : gain / cost;
                if (rate > bestRate) { bestRate = rate; best = new Buy(c, removed, cost, gain); }
            }
            return best;
        }
    }

    // sim only: the card bought first (tools/sim --upgrade-first), worth values to try (--worth), how often to explore (flag explore)
    public static string? First;
    public static double Explore;
    static readonly Random Rng = new();
    public static void SetWorth(string code, double value) => Worth[code] = value;
    public static IReadOnlyDictionary<string, double> Worths => Worth;

    // the card memory (evidence/ai/model/card-worth.json, written by tools/ai/card_worth.py after each training iteration): per
    // investigator and card, the campaign score buying it added and cutting it cost, already shrunk by how often it was seen
    // and scaled to the worth table (Buy / Cut are added to / taken from the card's worth). Read again when the file changes.
    public sealed record Memory(double Buy, double Cut);
    static Dictionary<string, Dictionary<string, Memory>> memory = new();
    static DateTime memoryTime;
    public static Dictionary<string, Memory> Learned(string investigator)
    {
        try
        {
            var file = Path.Combine(AiHost.ModelDir, "card-worth.json");
            var t = File.Exists(file) ? File.GetLastWriteTimeUtc(file) : default;
            if (t != memoryTime)
            {
                memoryTime = t; memory = new();
                if (t != default)
                {
                    using var doc = JsonDocument.Parse(File.ReadAllText(file));
                    foreach (var inv in J.Props(J.Get(doc.RootElement, "investigators")))
                        memory[inv.Name] = J.Props(inv.Value).ToDictionary(c => c.Name, c => new Memory(Num(c.Value, "buy"), Num(c.Value, "cut")));
                }
            }
        }
        catch (Exception e) { AiHost.Log("AI_UPGRADE_MEMORY " + e.Message); memory = new(); }
        return memory.TryGetValue(investigator, out var m) ? m : new();
    }
    static double Num(JsonElement e, string name) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : 0;

    // the soak a card puts into play (allies' and items' printed health / sanity; the card data has neither), from the rules
    public static (int Health, int Sanity) Soak(string code) => CoreCatalog.Normalize(code) switch
    {
        "01033" => (1, 2), "01018" => (2, 2), "01028" => (3, 2), "01032" => (1, 1), "01048" => (2, 2), "01054" => (2, 2),
        "01063" => (1, 2), "01021" => (3, 1), "01082" => (1, 4), "01095" => (0, 4), "01094" => (4, 0),
        _ => (0, 0)
    };
}
