using System.Text.Json;
using System.Text.Json.Nodes;
using Arkham.NativeHost;
using Gathering;
using ArkhamUI;

// Rules probe: drive the native rules core step by step from the command line (no Godot), to check a card interaction.
//   RulesProbe start <deck.json> <out-save.json> [seed] [scenario]   create a solo game, play setup to the first turn, export the save
//   RulesProbe play <save.json> <out-save.json> <step>...            import a save, answer step by step, export the save
// Steps: "s:<text>" first enabled choice whose title contains text; "i:<n>" choice index; "auto" first enabled choice;
// "amt" smallest valid amounts. Each step prints the question, the choices, and the investigator's hand / deck / discard / resources.
Console.OutputEncoding = System.Text.Encoding.UTF8;
string root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", ".."));
var S = new Services(root);
AiHost.Root = root; AiHost.Card = S.Card; AiHost.PassChance = SimOdds.PassChance; AiHost.ScenarioCode = SimOdds.Scenario; AiHost.Log = _ => { };
var cfg = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "runtime/core.json"))).RootElement;
await using var core = new NativeCoreWorker(Path.Combine(root, "runtime/arkham_core.dll"), cfg.GetProperty("sourceId").GetString()!);
await core.Ready;

async Task<JsonElement> Call(object request)
{
    var r = await core.RequestAsync(request);
    if (r.TryGetProperty("ok", out var ok) && ok.ValueKind == JsonValueKind.False) throw new InvalidOperationException(r.GetRawText());
    return r.Clone();
}

Guid gameId = Guid.NewGuid(), seat = Guid.NewGuid(); long session;
string mode = args[0];
if (mode == "start")
{
    var deck = JsonDocument.Parse(File.ReadAllText(args[1])).RootElement;
    int seed = args.Length > 3 ? int.Parse(args[3]) : 1; string scenario = args.Length > 4 ? args[4] : "01104";
    var created = await Call(new { apiVersion = 1, method = "create", gameId = gameId.ToString("D"), request = new { scenario, seed, players = new[] { seat.ToString("D") }, difficulty = "Easy" } });
    session = created.GetProperty("session").GetInt64();
    for (int step = 0; step < 200; step++)
    {
        var (m, st) = await View();
        if (m.Phase == "InvestigationPhase" && m.QuestionTag == "PlayerWindowChooseOne") break;
        JsonElement answer;
        if (m.QuestionTag == "PickScenarioSettings")
        {
            var sv = await Call(new { apiVersion = 1, method = "view", session, player = seat.ToString("D"), sections = new[] { "settingsSchema" } });
            var schema = J.Get(sv, "settingsSchema"); answer = ClientSettings.Answer(schema, ClientSettings.Defaults(schema));
        }
        else if (m.QuestionTag == "ChooseDeck")
            answer = JsonSerializer.SerializeToElement(new { tag = "DeckListAnswer", deckList = StandaloneDecks.ForCore(deck), playerId = seat.ToString("D") });
        else if (m.QuestionTag is "ChooseAmounts" or "ChoosePaymentAmounts") answer = MinAmounts(st);
        else
        {
            // setup: keep the opening hand, otherwise the first choice
            var c = m.Question.Choices.FirstOrDefault(x => x.Enabled && (x.Title.Contains("完成") || x.Title.Contains("保留") || x.Title.Contains("不调度")))
                    ?? m.Question.Choices.FirstOrDefault(x => x.Enabled);
            if (c is null) { Console.WriteLine("STUCK " + m.QuestionTag); break; }
            Console.WriteLine($"SETUP [{m.QuestionTag}] -> {c.Title}");
            answer = ClientQuestions.IndexAnswer(c.Index, seat, null);
        }
        await Answer(st, answer);
    }
    await Show();
    await Export(args[2]);
}
else if (mode == "viewbench")
{
    // time the core's view: everything vs. single sections (perf 11-21)
    var save = JsonDocument.Parse(File.ReadAllText(args[1])).RootElement;
    session = (await Call(new { apiVersion = 1, method = "import", save })).GetProperty("session").GetInt64();
    seat = Guid.Parse(PlayerOf(save));
    var full = await Call(new { apiVersion = 1, method = "view", session, player = seat.ToString("D") });
    foreach (var p in full.EnumerateObject()) Console.WriteLine($"SECTION {p.Name} {p.Value.GetRawText().Length / 1024}KB");
    async Task Bench(string label, string[]? sections)
    {
        var w = new System.Diagnostics.Stopwatch();
        for (int i = 0; i < 5; i++)
        {
            // a fresh session each time: the core may cache a view per session
            var s2 = (await Call(new { apiVersion = 1, method = "import", save })).GetProperty("session").GetInt64();
            w.Start();
            if (sections is null) await Call(new { apiVersion = 1, method = "view", session = s2, player = seat.ToString("D") });
            else await Call(new { apiVersion = 1, method = "view", session = s2, player = seat.ToString("D"), sections });
            w.Stop();
            await Call(new { apiVersion = 1, method = "release", session = s2 });
        }
        Console.WriteLine($"VIEW {label} avg={w.Elapsed.TotalMilliseconds / 5:F1}ms");
    }
    var w0 = System.Diagnostics.Stopwatch.StartNew(); w0.Stop();
    await Bench("full", null);
    foreach (var p in full.EnumerateObject()) if (p.Value.ValueKind is JsonValueKind.Object or JsonValueKind.Array) await Bench(p.Name, new[] { p.Name });
    await Bench("game+settingsSchema", new[] { "game", "settingsSchema" });
    await Bench("full-minus-debug", full.EnumerateObject().Where(p => p.Name != "debugSchema" && p.Value.ValueKind is JsonValueKind.Object or JsonValueKind.Array).Select(p => p.Name).ToArray());
    await core.RequestAsync(new { apiVersion = 1, method = "release", session });
}
else if (mode == "play")
{
    var save = JsonDocument.Parse(File.ReadAllText(args[1])).RootElement;
    var imp = await Call(new { apiVersion = 1, method = "import", save });
    session = imp.GetProperty("session").GetInt64();
    seat = Guid.Parse(PlayerOf(save));
    await Show();
    foreach (var step in args.Skip(3))
    {
        var (m, st) = await View();
        JsonElement answer;
        if (step == "amt") answer = MinAmounts(st);
        else if (step.StartsWith("raw:")) { answer = JsonDocument.Parse(step[4..]).RootElement.Clone(); Console.WriteLine($"\n>>> {step}"); }
        else
        {
            var c = step.StartsWith("i:") ? m.Question.Choices.FirstOrDefault(x => x.Index == int.Parse(step[2..]))
                  : step.StartsWith("s:") ? m.Question.Choices.FirstOrDefault(x => x.Enabled && x.Title.Contains(step[2..]))
                  : m.Question.Choices.FirstOrDefault(x => x.Enabled);
            if (c is null) { Console.WriteLine($"NO_CHOICE for step '{step}'"); break; }
            Console.WriteLine($"\n>>> {step}  =>  [{c.Index}] {c.Title}");
            answer = ClientQuestions.IndexAnswer(c.Index, seat, null);
        }
        try { await Answer(st, answer); }
        catch (InvalidOperationException e) { Console.WriteLine("CORE_REFUSED " + e.Message[..Math.Min(400, e.Message.Length)]); break; }
        await Show();
    }
    await Export(args[2]);
}

static string PlayerOf(JsonElement save)
{
    var g = save.GetProperty("snapshot").GetProperty("game");
    foreach (var inv in g.GetProperty("gameEntities").GetProperty("investigators").EnumerateObject()) return inv.Value.GetProperty("playerId").GetString()!;
    throw new InvalidOperationException("no investigator");
}

async Task<(TableModel, TableState)> View()
{
    var v = await Call(new { apiVersion = 1, method = "view", session, player = seat.ToString("D"), sections = new[] { "game" } });
    var st = new TableState(gameId, seat, 0, v.GetProperty("revision").GetInt64(), v, new[] { new ClientSeat(seat, 1, true) });
    return (new TableModel(st, S), st);
}

async Task Answer(TableState st, JsonElement answer)
{
    var r = await Call(new { apiVersion = 1, method = "answer", session, player = seat.ToString("D"), revision = st.Revision, answer });
    if (r.TryGetProperty("session", out var ns) && ns.ValueKind == JsonValueKind.Number && ns.GetInt64() != session) { long old = session; session = ns.GetInt64(); await Call(new { apiVersion = 1, method = "release", session = old }); }
}

JsonElement MinAmounts(TableState st)
{
    var q = ClientQuestions.Unwrap(st.Question); var rows = ClientQuestions.AmountRows(q);
    var values = rows.ToDictionary(r => r.Id, r => r.Minimum);
    for (int guard = 0; guard < 60 && !ClientQuestions.ValidAmounts(q, values); guard++)
    { var r = rows.FirstOrDefault(x => values[x.Id] < x.Maximum); if (r is null) break; values[r.Id]++; }
    return ClientQuestions.AmountAnswer(st.Question, values, seat, null);
}

async Task<JsonElement> Save() => (await Call(new { apiVersion = 1, method = "export", session })).GetProperty("save");
async Task Export(string file) { File.WriteAllText(file, (await Save()).GetRawText()); Console.WriteLine("SAVED " + file); await core.RequestAsync(new { apiVersion = 1, method = "release", session }); }

string Cn(string code) { var n = S.Name(code); return n == CoreCatalog.Normalize(code) ? code : n + "(" + CoreCatalog.Normalize(code) + ")"; }
string CardCode(JsonElement c) => c.TryGetProperty("contents", out var x) && x.ValueKind == JsonValueKind.Object ? J.String(x, "cardCode") : J.String(c, "cardCode");

async Task Show()
{
    var (m, _) = await View();
    var g = (await Save()).GetProperty("snapshot").GetProperty("game");
    Console.WriteLine($"-- 第{J.Int(g, "gameRoundCount")}轮 {J.String(g, "gamePhase")}  问题[{m.QuestionTag}] {m.Question.Title}");
    foreach (var c in m.Question.Choices) Console.WriteLine($"   {(c.Enabled ? " " : "x")}[{c.Index}] {c.Title}");
    foreach (var inv in g.GetProperty("gameEntities").GetProperty("investigators").EnumerateObject())
    {
        var i = inv.Value; var tokens = J.Items(J.Get(i, "tokens")).ToDictionary(t => J.Items(t).First().GetString()!, t => J.Items(t).Skip(1).First().GetInt32());
        Console.WriteLine($"   {Cn(inv.Name)} 资源{tokens.GetValueOrDefault("Resource")} 伤害{tokens.GetValueOrDefault("Damage")} 恐惧{tokens.GetValueOrDefault("Horror")} 行动{J.Int(i, "remainingActions")}");
        Console.WriteLine("     手牌: " + string.Join(" ", J.Items(J.Get(i, "hand")).Select(c => Cn(CardCode(c)))));
        Console.WriteLine($"     牌库({J.Items(J.Get(i, "deck")).Count()}): " + string.Join(" ", J.Items(J.Get(i, "deck")).Select(c => Cn(CardCode(c)))));
        Console.WriteLine($"     弃牌({J.Items(J.Get(i, "discard")).Count()}): " + string.Join(" ", J.Items(J.Get(i, "discard")).Select(c => Cn(CardCode(c)))));
    }
    var assets = J.Get(J.Get(g, "gameEntities"), "assets");
    if (assets.ValueKind == JsonValueKind.Object)
        Console.WriteLine("     场上支援: " + string.Join(" ", assets.EnumerateObject().Select(a => Cn(J.String(a.Value, "cardCode")) + (J.Get(a.Value, "exhausted").ValueKind == JsonValueKind.True ? "[横置]" : ""))));
}
