using System.Text.Json;
using Gathering;

// Legal decks for functional tests (Yunke 11-22): the rules core's own verdict on each deck of a { decks: { key: deck } }
// file, as a game start would see it (prepared like a starting deck, then validated). Usage: probe <root> --validate <file>
static class MakeDeckProbe
{
    public static async Task Run(string root, string file)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        var data = Path.Combine(root, "evidence", "probe-validate-" + DateTime.UtcNow.ToString("HHmmss"));
        await using var backend = new PrototypeBackend(root, data); await backend.Ready;
        using var doc = JsonDocument.Parse(File.ReadAllText(file));
        foreach (var p in J.Props(J.Get(doc.RootElement, "decks")))
        {
            var deck = p.Value.Clone(); string inv = J.String(deck, "investigator_code");
            try
            {
                var prepared = await backend.PrepareStandaloneDeck(deck);
                var schema = await backend.BuildSchema(inv);
                var r = await backend.ValidateDeck(inv, J.String(schema, "schemaId"), prepared);
                Console.WriteLine(p.Name + " " + J.String(r, "status") + " " + (J.String(r, "status") == "valid" ? "" : PrototypeBackend.Verdict(r) + " " + J.Get(r, "errors").GetRawText() + " " + J.Get(r, "unsupportedReasons").GetRawText()));
            }
            catch (Exception e) { Console.WriteLine(p.Name + " ERROR " + e.Message); }
        }
    }
}
