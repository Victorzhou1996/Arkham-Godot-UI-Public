using System.Text.Json;
using System.Text.Json.Nodes;
using Gathering;

// Cards that act before the game starts or when bought (Yunke 11-22): for each one, a starter deck of its class with the card
// in it starts the Night of the Zealot campaign; every question the core asks up to the first action is recorded, with
// where the card is then (in play, in hand, in the deck, set aside) and the investigator's trauma and experience.
// Usage: probe <root> --setupcards <code,code,…>
static class SetupCardsProbe
{
    static readonly Dictionary<string, string> ByFaction = new() { ["guardian"] = "01001", ["seeker"] = "01002", ["rogue"] = "01003", ["mystic"] = "01004", ["survivor"] = "01005", ["neutral"] = "01005" };

    public static Task RunDecks(string root, string file) => Run(root, "", file);

    public static async Task Run(string root, string codes, string deckFile = "")
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        var data = Path.Combine(root, "evidence", "probe-setupcards-" + DateTime.UtcNow.ToString("HHmmss"));
        await using var backend = new PrototypeBackend(root, data); await backend.Ready;
        var catalog = new CoreCatalog(Path.Combine(root, "app/assets/data/core-cards.json"));
        using var dictionary = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "app/assets/data/locale-zh.json")));
        var presenter = new QuestionPresenter(catalog, new ClientLocale(dictionary.RootElement));
        using var cardsDoc = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "app/assets/build-data/cards-zh-cn.json")));
        var all = new Dictionary<string, JsonElement>();
        foreach (var c in cardsDoc.RootElement.GetProperty("data").GetProperty("all_card").EnumerateArray()) all.TryAdd(J.String(c, "code"), c.Clone());
        var report = new List<object>();
        var given = new Dictionary<string, JsonElement>();
        if (deckFile.Length > 0) { using var df = JsonDocument.Parse(File.ReadAllText(deckFile)); foreach (var p in J.Props(J.Get(df.RootElement, "decks"))) given[p.Name] = p.Value.Clone(); }
        foreach (var code in deckFile.Length > 0 ? given.Keys.ToArray() : codes.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            var card = given.ContainsKey(code) ? all[J.String(given[code], "investigator_code")] : all[code]; string faction = J.String(card, "faction_code"); int xp = Math.Max(0, J.Int(card, "xp"));
            string inv = ByFaction.GetValueOrDefault(faction, "01005");
            var steps = new List<string>(); string outcome = "", where = "", trauma = "";
            try
            {
                JsonElement deck;
                if (given.TryGetValue(code, out var g)) { deck = g; inv = J.String(g, "investigator_code"); }
                else {
                var schema = await backend.BuildSchema(inv); var starter = catalog.Starter(inv, schema);
                var slots = CoreCatalog.Slots(starter);
                // one copy out (a plain level-0 card of the starter), the card in
                // a permanent card does not count toward the deck size: nothing goes out for it
                if (!J.String(card, "text").Contains("永久")) { var drop = slots.Where(p => p.Value >= 2).Select(p => p.Key).Last(); slots[drop]--; }
                slots[code] = slots.GetValueOrDefault(code) + 1;
                var node = JsonNode.Parse(catalog.Deck(inv, slots, "probe " + code).GetRawText())!.AsObject();
                int need = Math.Max(0, xp - StandaloneDecks.GrantedXp(JsonSerializer.SerializeToElement(node)));
                var meta = new JsonObject(); if (need > 0) meta["gathering_standalone_xp"] = need;
                // cards the core sets up from the deck list (build's attachments_<code>): 原定计划 takes up to 3 events
                if (code == "03264") meta["attachments_03264"] = "01023,01088";
                if (meta.Count > 0) node["meta"] = meta.ToJsonString();
                deck = JsonSerializer.SerializeToElement(node);
                }
                var state = await backend.Create(1, "Standard", "01", null);
                state = await backend.Open(state.Id, state.Seats[0].Player);
                for (int tick = 0; tick < 90; tick++)
                {
                    if (state.Question.ValueKind != JsonValueKind.Object) { var asked = state.Seats.FirstOrDefault(s => s.HasQuestion); if (asked is null) { outcome = "no question"; break; } state = await backend.Open(state.Id, asked.Player); continue; }
                    var q = ClientQuestions.Unwrap(state.Question); var ui = presenter.Present(state);
                    if (ui.Tag == "PlayerWindowChooseOne") { outcome = "first action reached"; break; }
                    string label = J.String(q, "label"); steps.Add(ui.Tag + (label.Length > 0 ? "「" + label + "」" : "") + (ui.Title is { Length: > 0 } t ? " " + t : ""));
                    if (ui.Tag == "PickScenarioSettings") { state = await backend.DefaultSettings(state); continue; }
                    if (ui.Tag == "ContinueCampaign") { state = await backend.Continue(state); continue; }
                    if (ui.Tag == "ChooseDeck") { state = await backend.SelectDeck(state, deck); continue; }
                    if (ui.Tag is "ChooseAmounts" or "ChoosePaymentAmounts")
                    {
                        var rows = ClientQuestions.AmountRows(q); var amounts = rows.ToDictionary(r => r.Id, r => r.Minimum);
                        var target = J.Get(q, "amountTargetValue"); int wanted = J.Get(target, "contents").ValueKind == JsonValueKind.Number ? J.Get(target, "contents").GetInt32() : amounts.Values.Sum();
                        foreach (var row in rows) { int add = Math.Min(Math.Max(0, wanted - amounts.Values.Sum()), row.Maximum - amounts[row.Id]); amounts[row.Id] += add; }
                        state = await backend.Amounts(state, amounts); continue;
                    }
                    var choices = ui.Choices.Where(c => c.Enabled).ToArray();
                    // a pick of cards (the market's 10 illicit cards, the plan's 3 events …): take cards until the core lets go
                    var pick = choices.FirstOrDefault(c => c.Tag is "TargetLabel" or "CardLabel" or "TargetCard" && c.CardCode.Length > 0);
                    var sel = choices.FirstOrDefault(c => c.Tag == "Done" || c.Title.Contains("结束") || c.Title.Contains("Keep") || c.Title.Contains("保留"));
                    var react = ui.Tag == "WindowChooseOne" ? choices.FirstOrDefault(c => c.Tag == "AbilityLabel") : null;
                    if (react is not null) File.WriteAllText(Path.Combine(data, code + "-reaction.json"), react.Raw.GetRawText());
                    sel = react ?? pick ?? sel ?? choices.FirstOrDefault(c => c.Tag == "SkipTriggersButton") ?? choices.FirstOrDefault();
                    if (sel is null) { outcome = "unanswerable " + ui.Tag; break; }
                    if (ui.Tag == "WindowChooseOne") steps.Add("  选项: " + string.Join(" | ", choices.Select(c => c.Tag + ":" + c.CardCode + ":" + c.Title)) + " → " + sel.Title);
                    state = await backend.Choice(state, sel.Index);
                }
                if (outcome.Length == 0) outcome = "step limit";
                var game = state.Game; string id = "";
                foreach (var zone in new[] { "assets", "events", "skills" })
                    foreach (var p in J.Props(J.Get(game, zone))) if (ClientDisplay.CardCode(p.Value) == code) { where = "在场(" + zone + ")"; id = p.Name; }
                var invEl = J.Props(J.Get(game, "investigators")).Select(p => p.Value).FirstOrDefault();
                if (invEl.ValueKind == JsonValueKind.Object)
                {
                    string raw = invEl.GetRawText();
                    if (where.Length == 0) where = J.Items(J.Get(invEl, "hand")).Any(h => h.GetRawText().Contains(code)) ? "手牌" : J.Get(invEl, "deck").GetRawText().Contains(code) ? "牌库" : J.Get(invEl, "decks").GetRawText().Contains(code) ? "另设牌库" : raw.Contains(code) ? "调查员其他区域" : game.GetRawText().Contains(code) ? "场上其他位置" : "未找到";
                    trauma = "肉体创伤" + J.Int(invEl, "physicalTrauma") + " 精神创伤" + J.Int(invEl, "mentalTrauma") + " 经验" + J.Int(invEl, "xp");
                }
            }
            catch (Exception e) { outcome = "ERROR " + e.Message; }
            Console.WriteLine(code + " " + J.String(card, "name") + " [" + inv + "] " + outcome + " · " + where + " · " + trauma);
            foreach (var s in steps.Where(s => !s.StartsWith("Read") && !s.StartsWith("ChooseDeck") && !s.StartsWith("PickScenarioSettings") && !s.StartsWith("ContinueCampaign"))) Console.WriteLine("    " + s);
            report.Add(new { code, name = J.String(card, "name"), investigator = inv, outcome, where, trauma, steps });
        }
        File.WriteAllText(Path.Combine(data, "report.json"), JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
        Console.WriteLine("data " + data);
    }
}
