using System.Text.Json;
using Gathering;

// Between scenarios: what do "保持牌组" (KeepDeck) and an upgrade do to the experience the rules core keeps (xp, spentXp)?
// Usage: probe <root> --keepdeck <data dir (a copy)> <game id> <sequence at a ChooseUpgradeDeck>
static class KeepDeckProbe
{
    public static async Task Run(string root, string dataDir, string game, long seq)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        await using var backend = new PrototypeBackend(root, dataDir); await backend.Ready;
        var id = Guid.Parse(game);
        var st = await backend.Open(id);
        st = await backend.RevertTo(st, seq);
        void Show(string when, TableState s)
        {
            foreach (var p in J.Props(J.Get(s.Game, "investigators")))
                Console.WriteLine(when + " " + p.Name + " xp=" + J.Int(p.Value, "xp") + " spentXp=" + J.Int(p.Value, "spentXp"));
        }
        Show("before", st);
        int k = 0;
        foreach (var seat in st.Seats)
        {
            var s = await backend.Open(id, seat.Player);
            var q = ClientQuestions.Unwrap(s.Question);
            if (J.Tag(q) != "ChooseUpgradeDeck") { Console.WriteLine("seat " + seat.Number + " asks " + J.Tag(q)); continue; }
            var inv = J.Props(J.Get(s.Game, "investigators")).First(p => J.String(p.Value, "playerId") == seat.Player.ToString("D")).Name;
            if (k++ == 0) { Console.WriteLine("seat " + seat.Number + " " + inv + " → KeepDeck"); s = await backend.KeepDeck(s, inv); }
            else
            {
                var slots = backend.CurrentDeck(s, inv);
                var deck = JsonSerializer.SerializeToElement(new { name = "probe", investigator_code = CoreCatalog.Normalize(inv), slots, sideSlots = new Dictionary<string, int>(), meta = "{}" });
                Console.WriteLine("seat " + seat.Number + " " + inv + " → UpgradeDeck (same cards)"); s = await backend.UpgradeDeck(s, inv, deck);
            }
            Show("after", await backend.Open(id, seat.Player));
        }
    }
}
