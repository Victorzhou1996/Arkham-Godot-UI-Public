using System.Text.Json;
using Gathering;

// Cards that give experience or trauma when bought (危险处境 08125, 奥术研究 04109 …): does a starting deck with them get past
// deck selection, and what does the rules core ask then? Usage: probe <root> --xpcards <deck.json> [campaign] [physical]
static class XpCardsProbe
{
    public static async Task Run(string root, string deckFile, string campaign, int physical)
    {
        var data = Path.Combine(root, "evidence", "probe-xpcards-" + DateTime.UtcNow.ToString("HHmmss"));
        await using var backend = new PrototypeBackend(root, data); await backend.Ready;
        using var doc = JsonDocument.Parse(File.ReadAllText(deckFile)); var deck = doc.RootElement.Clone();
        var catalog = new CoreCatalog(Path.Combine(root, "app/assets/data/core-cards.json"));
        Console.WriteLine("XP spent=" + StandaloneDecks.Experience(catalog, deck) + " granted=" + StandaloneDecks.GrantedXp(deck));
        var state = await backend.Create(1, "Standard", campaign == "none" ? null : campaign, campaign == "none" ? "01104" : null);
        var seat = state.Seats[0];
        state = await backend.Open(state.Id, seat.Player);
        Console.WriteLine("Q0 " + J.Tag(ClientQuestions.Unwrap(state.Question)));
        try { state = await backend.SelectDeck(state, deck); }
        catch (Exception e) { Console.WriteLine("SELECT_FAILED " + e.Message); return; }
        for (int step = 0; step < 6; step++)
        {
            state = await backend.Open(state.Id, seat.Player);
            var q = state.Question; string tag = q.ValueKind == JsonValueKind.Object ? J.Tag(ClientQuestions.Unwrap(q)) : "none";
            var inv = J.Props(J.Get(J.Get(state.Game, "gameEntities"), "investigators")).FirstOrDefault();
            string trauma = inv.Value.ValueKind == JsonValueKind.Object ? "phys=" + J.Int(inv.Value, "physicalTrauma") + " mental=" + J.Int(inv.Value, "mentalTrauma") + " xp=" + J.Int(inv.Value, "xp") : "";
            Console.WriteLine("seq " + state.Sequence + " Q " + tag + " " + trauma);
            if (q.ValueKind == JsonValueKind.Object) File.WriteAllText(Path.Combine(data, "q" + step + ".json"), q.GetRawText());
            if (tag == "ChooseAmounts")
            {
                Console.WriteLine("  " + q.GetRawText()[..Math.Min(900, q.GetRawText().Length)]);
                var rows = ClientQuestions.AmountRows(q);
                var values = rows.ToDictionary(r => r.Id, r => r.Label.Contains("physical") || r.Label.Contains("肉体") || r == rows[0] ? physical : 2 - physical);
                Console.WriteLine("  answer " + string.Join(",", values.Select(v => v.Key + "=" + v.Value)));
                state = await backend.Amounts(state, values);
                continue;
            }
            if (tag is "PickScenarioSettings") { state = await backend.DefaultSettings(state); continue; }
            break;
        }
        File.WriteAllText(Path.Combine(data, "last-view.json"), state.View.GetRawText());
        Console.WriteLine("data " + data);
    }
}
