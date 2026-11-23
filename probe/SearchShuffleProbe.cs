using System.Text.Json;
using Gathering;

// Does a deck search shuffle the deck afterwards (研究馆员 01032: search the deck for a Tome; 智慧古书 01031: search the top 3)?
// A Daisy Walker starter deck starts Night of the Zealot; at the first action the investigator's deck order (card ids) is
// recorded, the card is played / used, the first offered card is taken, and the deck order is compared once the search is over.
// Usage: probe <root> --searchshuffle [01032|01031] [games]
static class SearchShuffleProbe
{
    static readonly List<string> shuffledSeen = new();
    record DeckCard(string Id, string Code);

    static List<DeckCard> Deck(TableState s)
    {
        var inv = J.Props(J.Get(s.Game, "investigators")).Select(p => p.Value).First();
        return J.Items(J.Get(inv, "deck")).Select(c => new DeckCard(J.String(c, "id"), CoreCatalog.Normalize(J.String(c, "cardCode")))).ToList();
    }
    static List<string> Hand(TableState s)
    {
        var inv = J.Props(J.Get(s.Game, "investigators")).Select(p => p.Value).First();
        return J.Items(J.Get(inv, "hand")).Select(c => CoreCatalog.Normalize(J.String(J.Get(c, "contents"), "cardCode").Length > 0 ? J.String(J.Get(c, "contents"), "cardCode") : J.String(c, "cardCode"))).ToList();
    }
    static string Codes(IEnumerable<DeckCard> d) => string.Join(" ", d.Select(c => c.Code));

    public static async Task Run(string root, string card, int games)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        var data = Path.Combine(Environment.GetEnvironmentVariable("SEARCH_PROBE_DATA") is { Length: > 0 } d ? d : Path.Combine(root, "evidence"), "probe-searchshuffle-" + DateTime.UtcNow.ToString("HHmmss"));
        await using var backend = new PrototypeBackend(root, data); await backend.Ready;
        PrototypeBackend.Trace = _ => { };
        var catalog = new CoreCatalog(Path.Combine(root, "app/assets/data/core-cards.json"));
        using var dictionary = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "app/assets/data/locale-zh.json")));
        var presenter = new QuestionPresenter(catalog, new ClientLocale(dictionary.RootElement));
        var schema = await backend.BuildSchema("01002"); var deck = catalog.Starter("01002", schema);
        var slots = CoreCatalog.Slots(deck);
        Console.WriteLine("Daisy starter: " + string.Join(",", slots.Select(p => p.Key + "x" + p.Value)));
        int done = 0;
        for (int g = 0; g < 40 && done < games; g++)
        {
            var state = await backend.Create(1, "Standard", "01", null);
            state = await backend.Open(state.Id, state.Seats[0].Player);
            QuestionPresentation ui = presenter.Present(state);
            for (int tick = 0; tick < 60; tick++)
            {
                if (state.Question.ValueKind != JsonValueKind.Object) { var asked = state.Seats.FirstOrDefault(s => s.HasQuestion); if (asked is null) break; state = await backend.Open(state.Id, asked.Player); continue; }
                var q = ClientQuestions.Unwrap(state.Question); ui = presenter.Present(state);
                if (ui.Tag == "PlayerWindowChooseOne") break;
                if (ui.Tag == "PickScenarioSettings") { state = await backend.DefaultSettings(state); continue; }
                if (ui.Tag == "ContinueCampaign") { state = await backend.Continue(state); continue; }
                if (ui.Tag == "ChooseDeck") { state = await backend.SelectDeck(state, deck); continue; }
                if (ui.Tag is "ChooseAmounts" or "ChoosePaymentAmounts") { var rows = ClientQuestions.AmountRows(q); state = await backend.Amounts(state, rows.ToDictionary(r => r.Id, r => r.Minimum)); continue; }
                var ch = ui.Choices.Where(c => c.Enabled).ToArray();
                var sel = ch.FirstOrDefault(c => c.Tag == "Done" || c.Title.Contains("结束选牌") || c.Title.Contains("保留") || c.Title.Contains("Keep")) ?? ch.FirstOrDefault(c => c.Tag == "SkipTriggersButton") ?? ch.FirstOrDefault();
                if (sel is null) break;
                state = await backend.Choice(state, sel.Index);
                if (state.Shuffled is { Length: > 0 } fl) shuffledSeen.AddRange(fl);
            }
            if (ui.Tag != "PlayerWindowChooseOne") { Console.WriteLine("game " + g + ": first action not reached (" + ui.Tag + ")"); continue; }
            var hand = Hand(state);
            var play = ui.Choices.FirstOrDefault(c => c.Enabled && CoreCatalog.Normalize(c.CardCode) == card && hand.Contains(card));
            if (play is null) { Console.WriteLine("game " + g + ": " + card + " not playable from opening hand [" + string.Join(" ", hand) + "]"); continue; }
            done++;
            Console.WriteLine("\n=== game " + g + " (" + state.Id + ") hand [" + string.Join(" ", hand) + "]");
            Console.WriteLine("play: " + play.Index + " " + play.Tag + " " + play.Title);
            List<DeckCard> before = Deck(state);
            state = await backend.Choice(state, play.Index);
            bool used = card == "01032";   // the librarian searches as it enters play; the book needs its action after it is played
            string? taken = null; bool searchSeen = false;
            for (int tick = 0; tick < 40; tick++)
            {
                if (state.Question.ValueKind != JsonValueKind.Object) { var asked = state.Seats.FirstOrDefault(s => s.HasQuestion); if (asked is null) break; state = await backend.Open(state.Id, asked.Player); continue; }
                ui = presenter.Present(state);
                var ch = ui.Choices.Where(c => c.Enabled).ToArray();
                var inv = J.Props(J.Get(state.Game, "investigators")).Select(p => p.Value).First();
                var found = J.Get(J.Get(inv, "search"), "searchFoundCards");
                if (ui.Tag == "PlayerWindowChooseOne")
                {
                    if (!used)
                    {
                        var ab = ch.FirstOrDefault(c => c.Tag == "AbilityLabel" && CoreCatalog.Normalize(c.CardCode) == card);
                        if (ab is null) { Console.WriteLine("  no ability of " + card + " offered"); break; }
                        before = Deck(state); used = true;
                        Console.WriteLine("  deck before ability (" + before.Count + "): " + Codes(before));
                        Console.WriteLine("  use: " + ab.Index + " " + ab.Title);
                        state = await backend.Choice(state, ab.Index); continue;
                    }
                    break;
                }
                if (found.ValueKind != JsonValueKind.Undefined && found.ValueKind != JsonValueKind.Null && !searchSeen)
                {
                    searchSeen = true;
                    var foundCodes = new List<string>();
                    foreach (var z in J.Items(found)) foreach (var x in J.Items(z)) if (x.ValueKind == JsonValueKind.Array) foreach (var c in J.Items(x)) { var cc = J.Get(c, "contents"); foundCodes.Add(CoreCatalog.Normalize(J.String(cc, "cardCode"))); }
                    Console.WriteLine("  core searchFoundCards (" + foundCodes.Count + "): " + string.Join(" ", foundCodes) + " raw=" + found.GetRawText()[..Math.Min(300, found.GetRawText().Length)]);
                    Console.WriteLine("  deck while searching (" + Deck(state).Count + "): " + (Codes(Deck(state)) == Codes(before) ? "unchanged" : "CHANGED " + Codes(Deck(state))));
                }
                Console.WriteLine("  Q " + ui.Tag + " \"" + ui.Title + "\": " + string.Join(" | ", ui.Choices.Select(c => c.Index + ":" + c.Tag + ":" + c.CardCode + ":" + c.Title)));
                ChoicePresentation? sel;
                if (ui.Tag == "WindowChooseOne" && ch.FirstOrDefault(c => c.Tag == "AbilityLabel" && CoreCatalog.Normalize(c.CardCode) == card) is { } react) sel = react;
                else if (ch.FirstOrDefault(c => c.Tag == "PortraitLabel" || (c.Tag == "TargetLabel" && c.Title.Contains("黛西"))) is { } who && taken is null && !ch.Any(c => c.Tag == "TargetLabel" && c.CardCode.Length > 0 && !c.Title.Contains("黛西"))) sel = who;
                else if (taken is null && ch.FirstOrDefault(c => c.Tag == "TargetLabel" && c.CardCode.Length > 0 && CoreCatalog.Normalize(c.CardCode) != "01002") is { } pick) { sel = pick; taken = J.String(J.Get(pick.Raw, "target"), "contents"); if (taken.Length == 0) taken = pick.TargetId; Console.WriteLine("  take: " + pick.CardCode + " (" + taken + ")"); }
                else sel = ch.FirstOrDefault(c => c.Tag == "Done") ?? ch.FirstOrDefault(c => c.Tag == "SkipTriggersButton") ?? ch.FirstOrDefault();
                if (sel is null) break;
                state = await backend.Choice(state, sel.Index);
            }
            var after = Deck(state);
            Console.WriteLine("  deck before (" + before.Count + "): " + Codes(before));
            Console.WriteLine("  deck after  (" + after.Count + "): " + Codes(after));
            var beforeIds = before.Select(c => c.Id).ToList(); var afterIds = after.Select(c => c.Id).ToList();
            var removed = beforeIds.Except(afterIds).ToList();
            var keptInOldOrder = beforeIds.Where(afterIds.Contains).ToList();
            bool sameOrder = keptInOldOrder.SequenceEqual(afterIds);
            int samePos = afterIds.Where((id, i) => i < keptInOldOrder.Count && keptInOldOrder[i] == id).Count();
            Console.WriteLine("  removed from deck: " + string.Join(" ", removed.Select(id => before.First(c => c.Id == id).Code)) + " · in hand now [" + string.Join(" ", Hand(state)) + "]");
            Console.WriteLine("  RESULT " + card + ": " + (sameOrder ? "NOT SHUFFLED (remaining cards keep their order)" : "SHUFFLED") + " · cards still in their old position " + samePos + "/" + afterIds.Count
                + " · top 3 before " + Codes(before.Take(3)) + " → after " + Codes(after.Take(3)) + " · flagged=" + string.Join(",", shuffledSeen));
        }
        Console.WriteLine("data " + data);
    }
}
