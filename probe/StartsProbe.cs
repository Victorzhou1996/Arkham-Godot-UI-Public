using System.Text.Json;
using Gathering;

// Which campaigns and scenarios the rules core really starts (later cycles, 11-22). For every candidate id the core
// creates a game (campaign from its start, or a standalone scenario), each seat gets Roland's core example deck, and the
// first questions are answered (settings → defaults, campaign steps → continue, anything else → first option) until the
// table is in play (a player window, or locations in play) or something stops it. One line per candidate:
//   START <kind> <id> <ok|fail> steps=<n> scenario=<id> reason=<…>
// Usage: Gathering.Probe <root> --starts <file with one "kind id" per line> [max steps]
static class StartsProbe
{
    public static async Task Run(string root, string listFile, int maxSteps)
    {
        var data = Path.Combine(root, "evidence", "probe-starts-" + DateTime.UtcNow.ToString("HHmmss"));
        Directory.CreateDirectory(data);
        await using var backend = new PrototypeBackend(root, data); await backend.Ready;
        var catalog = new CoreCatalog(Path.Combine(root, "app/assets/data/core-cards.json"));
        var roland = catalog.Starter("01001", await backend.BuildSchema("01001"));
        var results = new List<object>();
        foreach (var line in File.ReadAllLines(listFile).Select(l => l.Trim()).Where(l => l.Length > 0 && !l.StartsWith('#')))
        {
            var parts = line.Split(' ', 2); string kind = parts[0], id = parts[1];
            string verdict = "fail", reason = "", scenario = ""; int steps = 0; string? settings = null;
            try
            {
                var state = kind == "campaign" ? await backend.Create(1, "Standard", id, null) : await backend.Create(1, "Standard", null, id);
                for (; steps < maxSteps; steps++)
                {
                    var s = await backend.Open(state.Id, state.Seats[0].Player);
                    var game = s.Game;
                    scenario = J.String(J.Get(game, "scenario"), "id");
                    var ss = J.Get(s.View, "settingsSchema");
                    if (ss.ValueKind == JsonValueKind.Object && J.Get(ss, "supported").ValueKind == JsonValueKind.False) { reason = "settings unsupported: " + J.String(ss, "reason"); settings = ss.GetRawText(); break; }
                    if (J.Props(J.Get(game, "locations")).Any() && s.Question.ValueKind == JsonValueKind.Object && J.Tag(ClientQuestions.Unwrap(s.Question)) == "PlayerWindowChooseOne") { verdict = "ok"; break; }
                    if (s.Question.ValueKind != JsonValueKind.Object)
                    {
                        var next = J.Get(ClientDisplay.Continuation(game), "nextStep");
                        if (next.ValueKind == JsonValueKind.Object) { state = await backend.Continue(s); continue; }
                        reason = "no question"; break;
                    }
                    string tag = J.Tag(ClientQuestions.Unwrap(s.Question));
                    if (Environment.GetEnvironmentVariable("STARTS_TRACE") == "1") Console.WriteLine("  [" + steps + "] " + tag + " :: " + s.Question.GetRawText()[..Math.Min(500, s.Question.GetRawText().Length)]);
                    if (tag == "ChooseDeck") state = await backend.SelectDeck(s, roland);
                    else if (tag is "PickScenarioSettings" or "PickCampaignSettings") state = await backend.DefaultSettings(s);
                    else if (tag == "ContinueCampaign") state = await backend.Continue(s);
                    else if (tag == "PlayerWindowChooseOne") { verdict = "ok"; break; }
                    else state = await backend.Choice(s, 0);
                }
                if (verdict != "ok" && reason.Length == 0) reason = "not in play after " + steps + " steps";
            }
            catch (Exception e) { reason = e.Message.Replace("\n", " "); if (reason.Length > 300) reason = reason[..300]; }
            Console.WriteLine("START " + kind + " " + id + " " + verdict + " steps=" + steps + " scenario=" + scenario + " reason=" + reason);
            results.Add(new { kind, id, verdict, steps, scenario, reason, settings });
            File.WriteAllText(Path.Combine(data, "starts.json"), JsonSerializer.Serialize(results, new JsonSerializerOptions { WriteIndented = true }));
        }
        Console.WriteLine("data " + data);
    }
}
