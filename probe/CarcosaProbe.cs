using System.Text.Json;
using Gathering;

// Does the rules core run The Path to Carcosa (campaign "03")? (cycle 2 integration, 11-20)
//   schema: the six Carcosa investigators' build schemas and their example decks (CoreCatalog.Starter) checked by the core
//   flow:   create campaign "03", answer each seat's ChooseDeck with a Carcosa example deck, then walk the first
//           questions (settings → defaults, everything else → first option) and print what the core asks.
// Usage: Gathering.Probe <root> --carcosa [schema|flow] [steps]
static class CarcosaProbe
{
    public static async Task Run(string root, string what, int steps)
    {
        var data = Path.Combine(root, "evidence", "probe-carcosa-" + DateTime.UtcNow.ToString("HHmmss"));
        Directory.CreateDirectory(data);
        await using var backend = new PrototypeBackend(root, data); await backend.Ready;
        var catalog = new CoreCatalog(Path.Combine(root, "app/assets/data/core-cards.json"));
        if (what == "schema")
        {
            foreach (var code in new[] { "03001", "03002", "03003", "03004", "03005", "03006" })
            {
                try
                {
                    var schema = await backend.BuildSchema(code);
                    File.WriteAllText(Path.Combine(data, "schema-" + code + ".json"), schema.GetRawText());
                    var deck = await backend.PrepareStandaloneDeck(catalog.Starter(code, schema));
                    var check = await backend.ValidateDeck(code, J.String(schema, "schemaId"), deck);
                    Console.WriteLine("STARTER " + code + " " + J.String(check, "status") + " errors=" + J.Get(check, "errors").GetRawText() + " unsupported=" + J.Get(check, "unsupportedReasons").GetRawText());
                    File.WriteAllText(Path.Combine(data, "starter-" + code + ".json"), deck.GetRawText());
                }
                catch (Exception e) { Console.WriteLine("STARTER " + code + " FAILED " + e.Message); }
            }
            return;
        }
        var state = await backend.Create(2, "Standard", "03", null);
        Console.WriteLine("created " + state.Id + " seq " + state.Sequence + " title");
        File.WriteAllText(Path.Combine(data, "first-view.json"), state.View.GetRawText());
        var codes = new[] { "03002", "03004" }; int k = 0;
        foreach (var seat in state.Seats)
        {
            var s0 = await backend.Open(state.Id, seat.Player);
            var tag = s0.Question.ValueKind == JsonValueKind.Object ? J.Tag(ClientQuestions.Unwrap(s0.Question)) : "none";
            Console.WriteLine("seat " + seat.Player + " first question " + tag);
            if (tag == "ChooseDeck") { var code = codes[k++ % 2]; var schema = await backend.BuildSchema(code); state = await backend.SelectDeck(s0, catalog.Starter(code, schema)); }
        }
        string lastScenario = "";
        for (int step = 0; step < steps; step++)
        {
            TableState? asked = null;
            foreach (var seat in state.Seats)
            {
                var s = await backend.Open(state.Id, seat.Player);
                var ss = J.Get(s.View, "settingsSchema");
                if (ss.ValueKind == JsonValueKind.Object && J.Get(ss, "supported").ValueKind != JsonValueKind.Undefined)
                {
                    File.WriteAllText(Path.Combine(data, "settings-" + step + ".json"), ss.GetRawText());
                    Console.WriteLine("[" + step + "] SETTINGS supported=" + J.Get(ss, "supported").GetRawText() + " " + ss.GetRawText()[..Math.Min(400, ss.GetRawText().Length)]);
                }
                if (s.Question.ValueKind == JsonValueKind.Object) { asked = s; break; }
            }
            var game = state.Game;
            var scen = J.Get(J.Get(game, "scenario"), "id").ValueKind == JsonValueKind.String ? J.String(J.Get(game, "scenario"), "id") : J.String(J.Get(J.Get(J.Get(game, "mode"), "That"), "id"), "");
            var sid = J.String(J.Get(game, "scenario"), "id");
            if (sid != lastScenario) { Console.WriteLine("[" + step + "] SCENARIO " + sid); lastScenario = sid; }
            if (asked is null)
            {
                var next = J.Get(ClientDisplay.Continuation(game), "nextStep");
                if (next.ValueKind == JsonValueKind.Object) { Console.WriteLine("[" + step + "] CONTINUE " + next.GetRawText()); state = await backend.Continue(state); continue; }
                Console.WriteLine("[" + step + "] no question, stop"); break;
            }
            var q = ClientQuestions.Unwrap(asked.Question);
            var qtag = J.Tag(q);
            var raw = asked.Question.GetRawText();
            Console.WriteLine("[" + step + "] " + qtag + " :: " + raw[..Math.Min(700, raw.Length)]);
            File.WriteAllText(Path.Combine(data, "q-" + step.ToString("000") + ".json"), asked.View.GetRawText());
            try
            {
                if (qtag is "PickScenarioSettings" or "PickCampaignSettings") state = await backend.DefaultSettings(asked);
                else if (qtag == "ContinueCampaign") { Console.WriteLine("    next " + J.Get(ClientDisplay.Continuation(asked.Game), "nextStep").GetRawText()); state = await backend.Continue(asked); }
                else state = await backend.Choice(asked, 0);
            }
            catch (Exception e) { Console.WriteLine("[" + step + "] ANSWER FAILED " + e.Message); break; }
        }
        File.WriteAllText(Path.Combine(data, "last-view.json"), state.View.GetRawText());
        Console.WriteLine("data " + data);
    }
}
