using System.Text.Json;
using Gathering;

// Does the rules core run The Dunwich Legacy (campaign "02")? Creates one and prints the first questions.
static class DunwichProbe
{
    public static async Task Run(string root, string campaign, string? scenario)
    {
        var data = Path.Combine(root, "evidence", "probe-dunwich-" + DateTime.UtcNow.ToString("HHmmss"));
        await using var backend = new PrototypeBackend(root, data); await backend.Ready;
        if (campaign == "schema")
        {
            foreach (var code in new[] { "02001", "02002", "02003", "02004", "02005" })
            {
                var schema = await backend.BuildSchema(code);
                var catalog = new CoreCatalog(Path.Combine(root, "app/assets/data/core-cards.json"));
                var deck = catalog.Starter(code, schema);
                var check = await backend.ValidateDeck(code, J.String(schema, "schemaId"), deck);
                Console.WriteLine("STARTER " + code + " " + J.String(check, "status") + " " + J.Get(check, "errors").GetRawText() + " " + J.Get(check, "unsupportedReasons").GetRawText());
                File.WriteAllText(Path.Combine(data, "starter-" + code + ".json"), deck.GetRawText());
            }
            return;
        }
        var state = await backend.Create(2, "Standard", campaign == "none" ? null : campaign, scenario);
        Console.WriteLine("created " + state.Id + " seq " + state.Sequence);
        File.WriteAllText(Path.Combine(data, "first-view.json"), state.View.GetRawText());
        // answer each seat's deck question with the example deck, then print the settings form the core sends
        var cat2 = new CoreCatalog(Path.Combine(root, "app/assets/data/core-cards.json"));
        var codes = new[] { "02003", "02005" }; int k = 0;
        foreach (var seat in state.Seats)
        {
            var s0 = await backend.Open(state.Id, seat.Player);
            if (J.Tag(ClientQuestions.Unwrap(s0.Question)) == "ChooseDeck") { var code = codes[k++ % 2]; var schema = await backend.BuildSchema(code); state = await backend.SelectDeck(s0, cat2.Starter(code, schema)); }
        }
        foreach (var seat in state.Seats)
        {
            var sv = await backend.Open(state.Id, seat.Player);
            var ss = J.Get(sv.View, "settingsSchema");
            if (ss.ValueKind == JsonValueKind.Object) { File.WriteAllText(Path.Combine(data, "settings-schema.json"), ss.GetRawText()); Console.WriteLine("SETTINGS " + ss.GetRawText()[..Math.Min(3000, ss.GetRawText().Length)]); break; }
        }
        foreach (var seat in state.Seats)
        {
            var s = await backend.Open(state.Id, seat.Player);
            var q = s.Question;
            Console.WriteLine("seat " + seat.Player + " question " + (q.ValueKind == JsonValueKind.Object ? J.Tag(ClientQuestions.Unwrap(q)) : "none") + " :: " + (q.ValueKind == JsonValueKind.Object ? q.GetRawText()[..Math.Min(600, q.GetRawText().Length)] : ""));
        }
    }
}
