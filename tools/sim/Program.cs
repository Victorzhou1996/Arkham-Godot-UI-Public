using System.Diagnostics;
using System.Text.Json;
using Arkham.NativeHost;
using Gathering;
using ArkhamUI;

// Headless simulator (P1 of docs/AI-PLAY-PLAN-20261119.md): whole games played by the AI against the rules core kept in
// memory (the core's own sessions: create / view / answer), with no Godot, no rendering and no sqlite history.
//   ArkhamSim --games 10 --players 1 --diff Easy --seed 1 --tag sim1 [--flags rec+explore+net] [--scenario 01104] [--verbose]
// Each game writes AI_SUMMARY to evidence/ai/results.jsonl (run evidence/ai/<tag>-g<n>) and, with "rec",
// evidence/ai/<tag>-g<n>/decisions.jsonl for tools/ai/train.py.
string Arg(string name, string def) { int i = Array.IndexOf(args, "--" + name); return i >= 0 && i + 1 < args.Length ? args[i + 1] : def; }
bool verbose = args.Contains("--verbose");
Console.OutputEncoding = System.Text.Encoding.UTF8;
int games = int.Parse(Arg("games", "1")), players = int.Parse(Arg("players", "1")), seed0 = int.Parse(Arg("seed", "1")), limit = int.Parse(Arg("limit", "6000"));
string diff = Arg("diff", "Easy"), tag = Arg("tag", "sim"), scenario = Arg("scenario", "01104");
string campaign = Arg("campaign", "");   // "01": the whole Night of the Zealot campaign, chapter after chapter (log, trauma, experience kept by the core)
var flags = Arg("flags", "").Split('+', StringSplitOptions.RemoveEmptyEntries).ToHashSet();
string root = Path.GetFullPath(Arg("root", Path.Combine(AppContext.BaseDirectory, "..", "..", "..")));
string[] investigators = { "01001", "01002", "01003", "01004", "01005" };
// rotate: the solo investigator changes with the seed (all five of the core set), so the card memory learns every deck

var S = new Services(root);
AiHost.Root = root; AiHost.Card = S.Card; AiHost.PassChance = SimOdds.PassChance; AiHost.ScenarioCode = SimOdds.Scenario;
AiHost.Log = s => { if (verbose || s.StartsWith("AI_SUMMARY") || s.StartsWith("AI_NET")) Console.WriteLine(s); };
AiBrain.Verbose = verbose;
AiBrain.UseNet = flags.Contains("net"); if (AiBrain.UseNet) AiNet.Load();
bool teach = flags.Contains("teach");   // the search teacher: try the rule AI's best choices in the core, score the results with V
AiBrain.Explore = flags.Contains("explore") && !teach ? 0.2 : 0;
AiUpgrade.Explore = flags.Contains("upexplore") ? 0.6 : flags.Contains("explore") ? 0.3 : 0;   // try other buys / cuts now and then (the card memory, tools/ai/card_worth.py); upexplore: only the upgrades explore
double teachExplore = flags.Contains("explore") ? 0.1 : 0;
int teachK = int.Parse(Arg("k", "3"));
// rollout search (§18): each turn decision tries the rule AI's best searchK choices, searchN reseeded rollouts each to the next round
bool search = flags.Contains("search");
int searchK = int.Parse(Arg("sk", "3")), searchN = int.Parse(Arg("sn", "2"));
double searchMargin = double.Parse(Arg("smargin", "0.3"), System.Globalization.CultureInfo.InvariantCulture);
// deck-upgrade A/B (tools/ai/tune_upgrade.py): buy this card first when it can be bought; worth values to try ({"01029": 9, ...})
AiUpgrade.First = Arg("upgrade-first", "") is { Length: > 0 } upFirst ? upFirst : null;
if (Arg("worth", "") is { Length: > 0 } worthFile)
    foreach (var p in JsonDocument.Parse(File.ReadAllText(worthFile)).RootElement.EnumerateObject()) AiUpgrade.SetWorth(p.Name, p.Value.GetDouble());
double teachMargin = double.Parse(Arg("margin", "0.15"), System.Globalization.CultureInfo.InvariantCulture);
if (teach && !AiNet.ValueNet.Load()) { Console.WriteLine("SIM_NO_VALUE_NET (teach needs evidence/ai/model/value.json)"); teach = false; }
AiNet.DeferRecord = true;
var rng = new Random(seed0 * 7919 + 13);
var textSeen = new HashSet<string>();
string lastAssets = "";
int teachCalls = 0, teachChanged = 0, searchCalls = 0, searchChanged = 0; double searchMs = 0; int rngTests = 0;

var decks = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "qa-data", "r11play2", "decks.json"))).RootElement;
var cfg = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "runtime/core.json"))).RootElement;
await using var core = new NativeCoreWorker(Path.Combine(root, "runtime/arkham_core.dll"), cfg.GetProperty("sourceId").GetString()!);
await core.Ready;

async Task<JsonElement> Call(object request)
{
    for (int attempt = 0; ; attempt++)
    {
        var r = await core.RequestAsync(request);
        if (r.TryGetProperty("ok", out var ok) && ok.ValueKind == JsonValueKind.False)
        {
            // the core gives up on a request that runs too long while the CPU is busy (low priority): the state is unchanged, try again
            if (attempt < 3 && r.GetRawText().Contains("cooperative time limit")) { await Task.Delay(500 * (attempt + 1)); continue; }
            throw new InvalidOperationException(r.GetRawText());
        }
        return r.Clone();
    }
}

object CampRecord(TableModel m)
{
    var log = J.Get(J.Get(J.Get(m.Game, "mode"), "This"), "log");
    var keys = J.Items(J.Get(log, "recorded")).Select(k => J.String(k, "contents")).ToArray();
    var sets = J.Items(J.Get(log, "recordedSets")).Select(e => new { key = J.String(J.Items(e).First(), "contents"), n = J.Items(J.Items(e).Skip(1).First()).Count() }).ToArray();
    var trauma = m.Investigators.Select(i => new { code = CoreCatalog.Normalize(i.Code), phys = J.Int(i.Raw, "physicalTrauma"), ment = J.Int(i.Raw, "mentalTrauma"), health = m.Health(i), sanity = m.Sanity(i),
        lost = J.Get(i.Raw, "killed").ValueKind == JsonValueKind.True || J.Get(i.Raw, "drivenInsane").ValueKind == JsonValueKind.True }).ToArray();
    return new { keys, sets, trauma };
}

for (int g = 0; g < games; g++)
{
    int seed = seed0 + g; string run = tag + "-g" + g;
    string Seat(int i) => flags.Contains("rotate") ? investigators[(i + seed) % investigators.Length] : investigators[i % investigators.Length];
    var watch = Stopwatch.StartNew(); double coreMs = 0; int answers = 0;
    var brain = new AiBrain(); var stats = new AiStats { Run = "evidence/ai/" + run, Ai = AiBrain.UseNet && AiNet.Loaded ? "net" : AiBrain.Version, Flags = "sim+" + string.Join("+", flags.OrderBy(f => f)) };
    AiBrain.CheckAll.Clear(); AiBrain.CheckBad.Clear();
    AiNet.RecordFile = flags.Contains("rec") ? Path.Combine(root, "evidence", "ai", run, "decisions.jsonl") : null;
    var seats = Enumerable.Range(0, players).Select(_ => Guid.NewGuid()).ToArray();
    var gameId = Guid.NewGuid();
    var seatIds = seats.Select(p => p.ToString("D")).ToArray();
    var created = campaign.Length > 0
        ? await Call(new { apiVersion = 1, method = "create", gameId = gameId.ToString("D"), request = new { campaign, seed, players = seatIds, difficulty = diff } })
        : await Call(new { apiVersion = 1, method = "create", gameId = gameId.ToString("D"), request = new { scenario, seed, players = seatIds, difficulty = diff } });
    var chapters = new List<string>(); var upgrades = new List<string>(); var upgradeLog = new List<object>(); var continueSteps = new HashSet<string>(); bool playedSince = false; string lost = ""; CampaignReward.Rec? chapterStart = null;
    long session = created.GetProperty("session").GetInt64();
    if (flags.Contains("probe"))
    {
        // which core methods export a session's save and load one (rollouts with fresh randomness, §18)
        foreach (var meth in new[] { "export", "save", "snapshot", "serialize", "host-save", "exportSave" })
        {
            var r = await core.RequestAsync(new { apiVersion = 1, method = meth, session });
            string t = r.GetRawText();
            Console.WriteLine($"SIM_PROBE {meth} -> {t[..Math.Min(300, t.Length)]} ... len={t.Length} gameSeed={t.Contains("gameSeed")}");
        }
        var ex = await Call(new { apiVersion = 1, method = "export", session });
        string raw = ex.GetProperty("save").GetRawText();
        var mm = System.Text.RegularExpressions.Regex.Match(raw, "\"gameSeed\":(-?[0-9]+)");
        Console.WriteLine($"SIM_PROBE seed={mm.Groups[1].Value} at={mm.Index}");
        var save2 = JsonDocument.Parse(raw.Remove(mm.Groups[1].Index, mm.Groups[1].Length).Insert(mm.Groups[1].Index, "12345")).RootElement;
        foreach (var meth in new[] { "restore", "import", "load", "open" })
        {
            var r = await core.RequestAsync(new { apiVersion = 1, method = meth, save = save2 });
            string t = r.GetRawText();
            Console.WriteLine($"SIM_PROBE load {meth} -> {t[..Math.Min(300, t.Length)]}");
        }
        return;
    }

    // ── rollout search (flag search, docs/AI-PLAY-PLAN-20261119.md §18) ──────────────────────────────────────────────
    // A branch of the game (each answer makes a new session, the old one stays) played on by a fresh rule AI until the next
    // round's first turn decision, the chapter's end or maxSteps; logs, checks and the network are off meanwhile.
    async Task<(TableModel? M, long Session)> Rollout(long from, Guid seat, int maxSteps)
    {
        long s = from; var rb = new AiBrain(); Guid cur = seat; bool passed = false; TableModel? m = null;
        var log = AiHost.Log; bool verb = AiBrain.Verbose, net = AiBrain.UseNet; double ex = AiBrain.Explore; var pending = AiNet.Last; var recFile = AiNet.RecordFile;
        var checkAll = new Dictionary<string, int>(AiBrain.CheckAll); var checkBad = new Dictionary<string, int>(AiBrain.CheckBad);
        AiHost.Log = _ => { }; AiBrain.Verbose = false; AiBrain.UseNet = false; AiBrain.Explore = 0; AiNet.RecordFile = null;
        try
        {
            for (int k = 0; k < maxSteps; k++)
            {
                var v = await Call(new { apiVersion = 1, method = "view", session = s, player = cur.ToString("D"), sections = new[] { "game" } });
                var qs = J.Get(v.GetProperty("game"), "question");
                var asked = seats.Where(p => J.Get(qs, p.ToString("D")).ValueKind == JsonValueKind.Object).ToList();
                if (J.Get(qs, cur.ToString("D")).ValueKind != JsonValueKind.Object || J.Items(J.Get(J.Get(qs, cur.ToString("D")), "choices")).Count() == 0)
                    foreach (var p in seats.Where(p => p != cur))
                    {
                        var pv = await Call(new { apiVersion = 1, method = "view", session = s, player = p.ToString("D"), sections = new[] { "game" } });
                        if (J.Get(J.Get(J.Get(pv, "game"), "question"), p.ToString("D")).ValueKind == JsonValueKind.Object && !asked.Contains(p)) asked.Add(p);
                    }
                var sl = seats.Select((p, i) => new ClientSeat(p, i + 1, asked.Contains(p))).ToArray();
                var st = new TableState(gameId, cur, 0, v.GetProperty("revision").GetInt64(), v, sl);
                m = new TableModel(st, S);
                if (m.QuestionTag is "ContinueCampaign" or "ChooseUpgradeDeck" || m.IsOver && m.Question.Choices.Count(c => c.Enabled) == 0) break;
                if (m.Phase != "InvestigationPhase") passed = true;
                else if (passed && k > 0 && m.QuestionTag == "PlayerWindowChooseOne") break;
                if (!asked.Contains(cur) || m.Question.Choices.Count(c => c.Enabled) == 0 && m.QuestionTag is not ("ChooseAmounts" or "ChoosePaymentAmounts"))
                {
                    var other = asked.FirstOrDefault(p => p != cur);
                    if (other == Guid.Empty) break;
                    cur = other; continue;
                }
                JsonElement ans;
                if (m.QuestionTag is "ChooseAmounts" or "ChoosePaymentAmounts") ans = MinAmounts(st, cur);
                else
                {
                    var d = rb.Decide(m);
                    if (d is not null && d.Choice is null && rb.SwitchTo is { } to && Guid.TryParse(to, out var tp) && tp != cur) { cur = tp; continue; }
                    var c = d?.Choice ?? m.Question.Choices.FirstOrDefault(x => x.Enabled && !x.Title.StartsWith("撤回投入"));
                    if (c is null) break;
                    ans = ClientQuestions.IndexAnswer(c.Index, cur, null);
                }
                var r = await Call(new { apiVersion = 1, method = "answer", session = s, player = cur.ToString("D"), revision = st.Revision, answer = ans });
                long n = r.GetProperty("session").GetInt64();
                if (s != from && n != s) await Call(new { apiVersion = 1, method = "release", session = s });
                s = n;
            }
        }
        catch (InvalidOperationException) { }
        finally
        {
            AiHost.Log = log; AiBrain.Verbose = verb; AiBrain.UseNet = net; AiBrain.Explore = ex; AiNet.Last = pending; AiNet.RecordFile = recFile;
            AiBrain.CheckAll.Clear(); foreach (var kv in checkAll) AiBrain.CheckAll[kv.Key] = kv.Value;
            AiBrain.CheckBad.Clear(); foreach (var kv in checkBad) AiBrain.CheckBad[kv.Key] = kv.Value;
        }
        return (m, s);
    }
    // the same position with fresh randomness (chaos bag draws, shuffles): the session's save with another game seed, imported
    // as a new session (the host's undo does the same: restore with refreshRandom). Without it a branch plays the real future.
    async Task<long> Reseed(long from)
    {
        var ex = await Call(new { apiVersion = 1, method = "export", session = from });
        string raw = ex.GetProperty("save").GetRawText();
        var mm = System.Text.RegularExpressions.Regex.Match(raw, "\"gameSeed\":(-?[0-9]+)");
        if (!mm.Success) throw new InvalidOperationException("save has no gameSeed");
        string seeded = raw.Remove(mm.Groups[1].Index, mm.Groups[1].Length).Insert(mm.Groups[1].Index, rng.NextInt64(long.MinValue, long.MaxValue).ToString());
        var r = await Call(new { apiVersion = 1, method = "import", save = JsonDocument.Parse(seeded).RootElement });
        return r.GetProperty("session").GetInt64();
    }
    // one candidate in a reseeded copy of the position: the session after answering it
    async Task<long> Branch(long from, Guid seat, int choiceIndex)
    {
        long s0 = await Reseed(from);
        var v = await Call(new { apiVersion = 1, method = "view", session = s0, player = seat.ToString("D"), sections = new[] { "game" } });
        var r = await Call(new { apiVersion = 1, method = "answer", session = s0, player = seat.ToString("D"), revision = v.GetProperty("revision").GetInt64(), answer = ClientQuestions.IndexAnswer(choiceIndex, seat, null) });
        long s1 = r.GetProperty("session").GetInt64();
        if (s1 != s0) await Call(new { apiVersion = 1, method = "release", session = s0 });
        return s1;
    }
    JsonElement MinAmounts(TableState st, Guid seat)
    {
        var q = ClientQuestions.Unwrap(st.Question); var rows = ClientQuestions.AmountRows(q);
        var values = rows.ToDictionary(r => r.Id, r => r.Minimum);
        for (int guard = 0; guard < 60 && !ClientQuestions.ValidAmounts(q, values); guard++)
        {
            var r = rows.FirstOrDefault(x => values[x.Id] < x.Maximum); if (r is null) break; values[r.Id]++;
        }
        return ClientQuestions.AmountAnswer(st.Question, values, seat, null);
    }
    Guid current = seats[0]; TableModel? model = null; string result = "步数用完";
    // --verbose: where damage and horror come from (SIM_PAIN: phase, place, engaged enemies, the encounter cards just resolved,
    // treacheries in play, the skill test's source, the last answer) — read by tools/ai/coach_report.py
    var painPrev = new Dictionary<string, (int D, int H)>(); var painDisc = new List<string>(); string lastChoice = "", lastCause = "", lastSc = "", lastVic = "", lastEn = "";
    // what the question being answered was asked on behalf of (an enemy's attack, a treachery, a location …) and what is attacking
    static void PainSources(JsonElement e, List<string> acc, int depth)
    {
        if (depth > 16) return;
        if (e.ValueKind == JsonValueKind.Object)
        {
            if (J.Tag(e) == "QuestionWithSource" && J.Get(J.Get(e, "source"), "contents") is { ValueKind: JsonValueKind.String } src) acc.Add(J.Tag(J.Get(e, "source")) + ":" + src.GetString());
            foreach (var p in e.EnumerateObject())
                if (p.Name == "attackEnemy" && p.Value.ValueKind == JsonValueKind.String) acc.Add("attack:" + p.Value.GetString());
                else PainSources(p.Value, acc, depth + 1);
        }
        else if (e.ValueKind == JsonValueKind.Array) foreach (var x in e.EnumerateArray()) PainSources(x, acc, depth + 1);
    }
    try
    {
        for (int step = 0; step < limit; step++)
        {
            var sw = Stopwatch.StartNew();
            var view = await Call(new { apiVersion = 1, method = "view", session, player = current.ToString("D"), sections = new[] { "game" } });
            coreMs += sw.Elapsed.TotalMilliseconds;
            var game = view.GetProperty("game");
            long revision = view.GetProperty("revision").GetInt64();
            var questions = J.Get(game, "question");
            var asked = seats.Where(p => J.Get(questions, p.ToString("D")).ValueKind == JsonValueKind.Object).ToList();
            // a view shows the viewing seat's question only: when this seat has none (or nothing to choose), look at the others
            bool nothingHere = J.Get(questions, current.ToString("D")).ValueKind != JsonValueKind.Object;
            if (nothingHere || J.Items(J.Get(J.Get(questions, current.ToString("D")), "choices")).Count() == 0)
                foreach (var p in seats.Where(p => p != current))
                {
                    var pv = await Call(new { apiVersion = 1, method = "view", session, player = p.ToString("D"), sections = new[] { "game" } });
                    if (J.Get(J.Get(J.Get(pv, "game"), "question"), p.ToString("D")).ValueKind == JsonValueKind.Object && !asked.Contains(p)) asked.Add(p);
                }
            var seatList = seats.Select((p, i) => new ClientSeat(p, i + 1, asked.Contains(p))).ToArray();
            var state = new TableState(gameId, current, step, revision, view, seatList);
            model = new TableModel(state, S);
            if (step > 0) stats.Step(model);
            // --verbose: text the player would see that is not Chinese (an untranslated key, a format placeholder, English words): once per process
            if (verbose)
                foreach (var t in new[] { model.Question.Title, model.Question.Body }.Concat(model.Question.Choices.SelectMany(c => new[] { c.Title, c.Hint })))
                    if (!string.IsNullOrEmpty(t) && System.Text.RegularExpressions.Regex.IsMatch(t, @"\$|\{|_|[A-Za-z]{4,}") && textSeen.Add(t))
                        Console.WriteLine($"SIM_TEXT sc={AiHost.ScenarioCode(model)} tag={model.QuestionTag} {(t.Length > 240 ? t[..240] : t).ReplaceLineEndings(" ")}");
            if (verbose)
            {
                // each investigator's assets in play, logged when they change (11-22: weapons played in act 1 of 聚集 vanished by act 3)
                string assets = string.Join(" ; ", model.Investigators.Select(i => model.Name(i) + ":" + string.Join(",", model.AssetsOf(i).Select(a => a.Code).OrderBy(c => c))));
                if (assets != lastAssets) { Console.WriteLine($"SIM_ASSETS sc={AiHost.ScenarioCode(model)} phase={model.Phase} q={model.QuestionTag} {assets}"); lastAssets = assets; }
            }
            if (verbose && model.Question.Choices.Any(c => c.Title.EndsWith(" Slot")) && textSeen.Add("raw:" + model.State.Question.GetRawText()))
                Console.WriteLine("SIM_TEXT_RAW " + model.State.Question.GetRawText()[..Math.Min(3000, model.State.Question.GetRawText().Length)]);
            if (verbose)
            {
                var disc = model.EncounterDiscard.ToList(); var fresh = new List<string>(disc);
                foreach (var d0 in painDisc) fresh.Remove(d0);
                foreach (var inv in model.Investigators)
                {
                    int dd = model.Damage(inv), hh = model.Horror(inv);
                    if (painPrev.TryGetValue(inv.Id, out var p0) && (dd > p0.D || hh > p0.H))
                    {
                        string Code(Entity? e) => e is null ? "" : CoreCatalog.Normalize(e.Code);
                        var src = J.Get(model.SkillTest, "source"); string srcId = J.Get(src, "contents").ValueKind == JsonValueKind.String ? J.String(src, "contents") : "";
                        var srcE = model.Treacheries.Concat(model.Enemies).Concat(model.Locations).Concat(model.Assets).FirstOrDefault(e => e.Id == srcId);
                        Console.WriteLine($"SIM_PAIN sc={AiHost.ScenarioCode(model)} phase={model.Phase} act={Code(model.Act)} who={Code(inv)} dhp={dd - p0.D} dsan={hh - p0.H} hp={model.Health(inv) - dd} san={model.Sanity(inv) - hh} loc={Code(model.Location(model.LocationOf(inv)))}"
                            + $" eng={string.Join(",", model.EngagedWith(inv).Select(Code))} new={string.Join(",", fresh)} tre={string.Join(",", model.Treacheries.Select(Code))} test={(model.InSkillTest ? J.Tag(src) + ":" + (srcE is null ? srcId : Code(srcE)) : "")} last={lastChoice} why={lastCause}");
                    }
                    painPrev[inv.Id] = (dd, hh);
                }
                painDisc = disc;
                if (J.String(model.Scenario, "id").Length > 0)
                {
                    lastSc = CoreCatalog.Normalize(J.String(model.Scenario, "id")); lastVic = string.Join(",", model.VictoryDisplay);
                    lastEn = string.Join(",", model.Enemies.Select(e => CoreCatalog.Normalize(e.Code)));
                }
                var srcs = new List<string>(); PainSources(model.State.Question, srcs, 0);
                var all = model.Treacheries.Concat(model.Enemies).Concat(model.Locations).Concat(model.Assets).ToList();
                lastCause = string.Join("+", srcs.Distinct().Select(x => { int k = x.IndexOf(':'); string id = x[(k + 1)..]; var e = all.FirstOrDefault(y => y.Id == id); return x[..k] + ":" + (e is null ? id[..Math.Min(8, id.Length)] : CoreCatalog.Normalize(e.Code)); }));
            }
            bool between = model.QuestionTag is "ContinueCampaign" or "ChooseUpgradeDeck";
            if (!between && model.IsOver && (asked.Count == 0 || model.Question.Choices.Count(c => c.Enabled) == 0)) { result = AiStats.Result(model); break; }
            // the seat that must answer: this one if it has something to choose, else another asked seat
            if (!asked.Contains(current) || model.Question.Choices.Count(c => c.Enabled) == 0 && model.QuestionTag is not ("PickScenarioSettings" or "ChooseDeck" or "ChooseAmounts" or "ChoosePaymentAmounts" or "ContinueCampaign" or "ChooseUpgradeDeck"))
            {
                var other = asked.FirstOrDefault(p => p != current);
                if (other == Guid.Empty) { if (asked.Count == 0) { result = "无人可答"; break; } }
                else { current = other; continue; }
            }
            JsonElement answer;
            string tagQ = model.QuestionTag;
            lastChoice = tagQ;
            if (tagQ == "PickScenarioSettings")
            {
                var sv = await Call(new { apiVersion = 1, method = "view", session, player = current.ToString("D"), sections = new[] { "settingsSchema" } });
                var schema = J.Get(sv, "settingsSchema");
                answer = ClientSettings.Answer(schema, ClientSettings.Defaults(schema));
            }
            else if (tagQ == "ContinueCampaign")
            {
                // between chapters: take the core's next step (deck upgrades come at ChooseUpgradeDeck: AiUpgrade)
                var next = J.Get(ClientDisplay.Continuation(model.Game), "nextStep");
                if (next.ValueKind != JsonValueKind.Object) { result = AiStats.Result(model); break; }
                string stepKey = next.GetRawText();
                bool firstVisit = continueSteps.Add(stepKey);   // after the upgrades the core asks this again: record the chapter once
                if (firstVisit)
                {
                    chapters.Add(AiHost.ScenarioCode(model));
                    // --verbose: who ended in the victory display and which enemies were still in play (午夜假面: caught vs drawn)
                    if (verbose) { Console.WriteLine($"SIM_CHAPTER_END sc={lastSc} victory={lastVic} enemies={lastEn}"); lastSc = lastVic = lastEn = ""; }
                    if (AiNet.RecordFile is not null) { Directory.CreateDirectory(Path.GetDirectoryName(AiNet.RecordFile)!); File.AppendAllText(AiNet.RecordFile, JsonSerializer.Serialize(new { chapterEnd = true, chapter = chapters[^1], rec = CampRecord(model) }) + "\n"); }
                }
                chapterStart = CampaignReward.Read(model);
                if (verbose) Console.WriteLine("SIM_CAMPAIGN " + next.GetRawText());
                // before the next chapter (not the first): spend experience — the core then asks ChooseUpgradeDeck per seat
                bool upgradeNow = firstVisit && playedSince && J.Tag(next) == "ScenarioStep" && !flags.Contains("noupgrade");
                if (upgradeNow) playedSince = false;
                answer = upgradeNow ? JsonSerializer.SerializeToElement(new { tag = "BeginUpgradeAnswer" })
                    : JsonSerializer.SerializeToElement(new { tag = "CampaignStepAnswer", contents = next });
            }
            else if (tagQ == "ChooseUpgradeDeck")
            {
                // an investigator killed or driven insane is lost for the campaign: the run ends there (the sim has no replacements)
                var me = model.Me ?? model.Investigators.First();
                if (model.Investigators.FirstOrDefault(i => J.Get(i.Raw, "killed").ValueKind == JsonValueKind.True || J.Get(i.Raw, "drivenInsane").ValueKind == JsonValueKind.True) is { } gone)
                { lost = CoreCatalog.Normalize(gone.Code); result = "失去调查员 " + lost; break; }
                // spend the experience (AiUpgrade); the core prices the new list and checks it — refused: keep the deck
                int xpLeft = Math.Max(0, J.Int(me.Raw, "xp") - J.Int(me.Raw, "spentXp"));
                var have = SimDeck(model, me.Id);
                var (newSlots, bought) = flags.Contains("noupgrade") || have.Count == 0 ? (have, new List<AiUpgrade.Buy>())
                    : AiUpgrade.Plan(me.Code, have, xpLeft, J.Int(me.Raw, "mentalTrauma"), J.Int(me.Raw, "physicalTrauma"));
                Console.WriteLine($"SIM_UPGRADE {CoreCatalog.Normalize(me.Code)} xp={xpLeft} cards={have.Values.Sum()} owned={string.Join(",", have.Keys.Where(k => J.Int(S.Card(k), "xp") > 0))} buy=" + string.Join(",", bought.Select(b => b.Code + "/" + b.Removed + ":" + b.Cost)));
                if (bought.Count > 0)
                {
                    var deckJson = JsonSerializer.SerializeToElement(new { name = model.Name(me), investigator_code = CoreCatalog.Normalize(me.Code), slots = newSlots, sideSlots = new Dictionary<string, int>() });
                    var up = JsonSerializer.SerializeToElement(new { tag = "UpgradeDeckAnswer", contents = new { investigatorId = me.Id, deck = StandaloneDecks.ForCore(deckJson) } });
                    try
                    {
                        var r = await Call(new { apiVersion = 1, method = "answer", session, player = current.ToString("D"), revision, answer = up });
                        long old = session; session = r.GetProperty("session").GetInt64(); answers++;
                        if (old != session) await Call(new { apiVersion = 1, method = "release", session = old });
                        upgrades.Add(chapters.LastOrDefault() + ":" + string.Join(",", bought.Select(b => b.Code)));
                        upgradeLog.Add(new { at = upgradeLog.Count, inv = CoreCatalog.Normalize(me.Code), xp = xpLeft, buy = bought.Select(b => b.Code).ToArray(), cut = bought.Select(b => b.Removed).ToArray(), before = CampRecord(model) });
                        continue;
                    }
                    catch (InvalidOperationException e) { Console.WriteLine("SIM_UPGRADE_REFUSED " + e.Message[..Math.Min(300, e.Message.Length)]); }
                }
                answer = JsonSerializer.SerializeToElement(new { tag = "UpgradeDeckAnswer", contents = new { investigatorId = me.Id, deck = (object?)null } });
            }
            else if (tagQ == "ChooseDeck")
            {
                int seat = Array.IndexOf(seats, current);
                var deck = SimDeckList(decks, root, Seat(seat));
                answer = JsonSerializer.SerializeToElement(new { tag = "DeckListAnswer", deckList = StandaloneDecks.Prepare(S.Catalog, deck), playerId = current.ToString("D") });
            }
            else if (tagQ is "ChooseAmounts" or "ChoosePaymentAmounts")
            {
                var q = ClientQuestions.Unwrap(state.Question); var rows = ClientQuestions.AmountRows(q);
                var values = rows.ToDictionary(r => r.Id, r => r.Minimum);
                for (int guard = 0; guard < 60 && !ClientQuestions.ValidAmounts(q, values); guard++)
                {
                    var r = rows.FirstOrDefault(x => values[x.Id] < x.Maximum); if (r is null) break; values[r.Id]++;
                }
                answer = ClientQuestions.AmountAnswer(state.Question, values, current, null);
            }
            else
            {
                if (model.Phase == "InvestigationPhase") playedSince = true;   // a chapter has been played (the first upgrade comes after it)
                var d = brain.Decide(model);
                if (d is not null && d.Choice is null && brain.SwitchTo is { } to && Guid.TryParse(to, out var tp) && tp != current) { current = tp; continue; }
                var c = d?.Choice ?? model.Question.Choices.FirstOrDefault(x => x.Enabled && !x.Title.StartsWith("撤回投入"));
                if (c is null) { result = "卡住 " + tagQ; break; }
                lastChoice = c.Title.Replace(" ", "_");
                // a turn decision: the teacher looks one step ahead in the core for the best few choices (each answer makes a new
                // session; the current one is left as it was), the value network scores where each leads
                if (AiNet.Last is { } pend && ReferenceEquals(pend.M, model))
                {
                    int played = Array.IndexOf(pend.Choices, c), teacher = -1;
                    if (flags.Contains("rngtest") && rngTests < 6)
                    {
                        // is the core's randomness part of the state? the same branch played on twice: identical ends = yes
                        rngTests++;
                        var t1 = await Call(new { apiVersion = 1, method = "answer", session, player = current.ToString("D"), revision, answer = ClientQuestions.IndexAnswer(c.Index, current, null) });
                        long b = t1.GetProperty("session").GetInt64();
                        var (ra, sa) = await Rollout(b, current, 150); var (rb2, sb) = await Rollout(b, current, 150);
                        string ga = ra is null ? "" : ra.Game.GetRawText(), gb = rb2 is null ? "" : rb2.Game.GetRawText();
                        long r1 = await Reseed(b), r2 = await Reseed(b);
                        var (rc, sc) = await Rollout(r1, current, 150); var (rd, sd) = await Rollout(r2, current, 150);
                        string gc = rc is null ? "" : rc.Game.GetRawText(), gd = rd is null ? "" : rd.Game.GetRawText();
                        Console.WriteLine($"SIM_RNGTEST step={step} sameBranch={ga == gb} reseededSame={gc == gd} hp={rc?.Health(rc.Investigators.First()) - rc?.Damage(rc.Investigators.First())}/{rd?.Health(rd.Investigators.First()) - rd?.Damage(rd.Investigators.First())} clues={rc?.Clues(rc.Investigators.First())}/{rd?.Clues(rd.Investigators.First())}");
                        foreach (var x2 in new[] { sa, sb, b, r1, r2, sc, sd }.Distinct()) if (x2 != session) await Call(new { apiVersion = 1, method = "release", session = x2 });
                    }
                    if (teach && pend.Order.Length > 1)
                    {
                        double best = double.MinValue; int rank = 0;
                        // a resign is always among the candidates (the rule AI ranks it low): whether leaving now is worth it for the
                        // campaign is exactly what the teacher should judge
                        var cands = pend.Order.Take(teachK).ToList();
                        int resignIdx = Array.FindIndex(pend.Choices, q => model.ActionVerb(q) == "Resign");
                        if (campaign.Length > 0 && resignIdx >= 0 && !cands.Contains(resignIdx)) cands.Add(resignIdx);
                        foreach (int idx in cands)
                        {
                            var trial = await Call(new { apiVersion = 1, method = "answer", session, player = current.ToString("D"), revision, answer = ClientQuestions.IndexAnswer(pend.Choices[idx].Index, current, null) });
                            long s2 = trial.GetProperty("session").GetInt64();
                            var v2 = await Call(new { apiVersion = 1, method = "view", session = s2, player = current.ToString("D"), sections = new[] { "game" } });
                            var m2 = new TableModel(new TableState(gameId, current, step, v2.GetProperty("revision").GetInt64(), v2, seatList), S);
                            // a choice that ends the chapter (a resign) is followed by the resolution's story: read on, so the
                            // campaign log the chapter leaves is what gets judged
                            if (campaign.Length > 0 && m2.QuestionTag == "Read")
                            {
                                long s3 = s2;
                                for (int rd = 0; rd < 8 && m2.QuestionTag == "Read" && m2.Question.Choices.FirstOrDefault(q => q.Enabled) is { } rc; rd++)
                                {
                                    var a3 = await Call(new { apiVersion = 1, method = "answer", session = s3, player = current.ToString("D"), revision = m2.State.Revision, answer = ClientQuestions.IndexAnswer(rc.Index, current, null) });
                                    long n3 = a3.GetProperty("session").GetInt64(); if (s3 != s2) await Call(new { apiVersion = 1, method = "release", session = s3 }); s3 = n3;
                                    var v3 = await Call(new { apiVersion = 1, method = "view", session = s3, player = current.ToString("D"), sections = new[] { "game" } });
                                    m2 = new TableModel(new TableState(gameId, current, step, v3.GetProperty("revision").GetInt64(), v3, seatList), S);
                                }
                                if (s3 != s2) await Call(new { apiVersion = 1, method = "release", session = s3 });
                            }
                            await Call(new { apiVersion = 1, method = "release", session = s2 });
                            double val = campaign.Length == 0 ? (m2.IsOver ? (AiStats.Result(m2).StartsWith("Won") ? 6 : -3) : AiNet.Value(m2))
                                : (m2.IsOver || m2.QuestionTag == "ContinueCampaign") && chapterStart is not null ? CampaignReward.Target(chapterStart, m2) : AiNet.Value(m2);
                            if (verbose && idx == resignIdx) Console.WriteLine($"SIM_RESIGN_TRIAL over={m2.IsOver} q={m2.QuestionTag} val={val:F2}");
                            // the rule AI's own choice wins unless another is clearly better by V (a margin above V's noise)
                            if (rank++ > 0) val -= teachMargin;
                            if (val > best) { best = val; teacher = idx; }
                        }
                        teachCalls++; if (teacher != played) teachChanged++;
                        played = teacher;
                        if (teachExplore > 0 && rng.NextDouble() < teachExplore) played = pend.Order[rng.Next(Math.Min(4, pend.Order.Length))];
                        c = pend.Choices[played];
                    }
                    if (search && !teach && pend.Order.Length > 1 && model.Phase == "InvestigationPhase")
                    {
                        // try the rule AI's best few in the core with fresh randomness, play each on to the next round, judge the
                        // position (SearchScore); the rule AI's own choice keeps a small margin (rollouts are noisy)
                        var swS = Stopwatch.StartNew();
                        double best = double.MinValue; int pick = played;
                        foreach (int idx in pend.Order.Take(searchK))
                        {
                            double sum = 0;
                            for (int n = 0; n < searchN; n++)
                            {
                                long b = await Branch(session, current, pend.Choices[idx].Index);
                                var (mEnd, sEnd) = await Rollout(b, current, 200);
                                sum += mEnd is null ? -99 : SearchScore.Score(mEnd, gameId);
                                foreach (var x2 in new[] { b, sEnd }.Distinct()) await Call(new { apiVersion = 1, method = "release", session = x2 });
                            }
                            double val = sum / searchN + (idx == played ? searchMargin : 0);
                            if (verbose) Console.WriteLine($"SIM_SEARCH step={step} {pend.Choices[idx].Title} v={val:F2}");
                            if (val > best) { best = val; pick = idx; }
                        }
                        searchCalls++; if (pick != played) searchChanged++;
                        searchMs += swS.Elapsed.TotalMilliseconds;
                        played = teacher = pick; c = pend.Choices[pick];
                    }
                    AiNet.RecordPending(played, teacher);
                }
                answer = ClientQuestions.IndexAnswer(c.Index, current, null);
            }
            sw.Restart();
            var answered = await Call(new { apiVersion = 1, method = "answer", session, player = current.ToString("D"), revision, answer });
            // an answer gives a new session (the old state stays until released): move on to it and free the old one
            if (answered.TryGetProperty("session", out var ns) && ns.ValueKind == JsonValueKind.Number && ns.GetInt64() != session)
            {
                long old = session; session = ns.GetInt64();
                await Call(new { apiVersion = 1, method = "release", session = old });
            }
            if (step == 0 && verbose) Console.WriteLine("ANSWER_KEYS " + string.Join(",", answered.EnumerateObject().Select(x => x.Name)));
            coreMs += sw.Elapsed.TotalMilliseconds; answers++;
        }
    }
    catch (Exception e) { result = "错误 " + e.Message[..Math.Min(200, e.Message.Length)]; Console.WriteLine("SIM_ERROR " + run + " " + e.Message[..Math.Min(600, e.Message.Length)]); }
    if (model is not null) stats.End(model, result);
    // the campaign's own record for training: the log keys and sets, each investigator's trauma, who was lost
    if (campaign.Length > 0 && model is not null)
    {
        var rec = CampRecord(model);
        if (AiNet.RecordFile is not null) Directory.CreateDirectory(Path.GetDirectoryName(AiNet.RecordFile)!);
        if (AiNet.RecordFile is not null) File.AppendAllText(AiNet.RecordFile, JsonSerializer.Serialize(new { campaignEnd = true, result, lost, chapters, upgrades, upgradeLog, investigators = Enumerable.Range(0, players).Select(Seat).ToArray(), diff, rec }) + "\n");
        Console.WriteLine("SIM_CAMPAIGN_END " + run + " " + JsonSerializer.Serialize(new { result, lost, chapters, upgrades, rec }));
    }
    try { await Call(new { apiVersion = 1, method = "release", session }); } catch { }
    if (teach) { Console.WriteLine($"SIM_TEACH {run} decisions={teachCalls} changed={teachChanged}"); teachCalls = teachChanged = 0; }
    if (search) { Console.WriteLine($"SIM_SEARCH_SUMMARY {run} decisions={searchCalls} changed={searchChanged} msPerDecision={(searchCalls == 0 ? 0 : searchMs / searchCalls):F0}"); searchCalls = searchChanged = 0; searchMs = 0; }
    Console.WriteLine($"SIM_GAME {run} seed={seed} {players}p {diff} result={result} answers={answers} seconds={watch.Elapsed.TotalSeconds:F1} coreMsPerAnswer={(answers == 0 ? 0 : coreMs / answers):F1}");
}

// the investigator's deck between chapters as the core holds it (card code → copies): the campaign's own record first
// (mode.This.decks), else the investigator's deck (PrototypeBackend.CurrentDeck does the same from the save)
// the starting deck of an investigator: the QA deck (qa-data/r11play2, read only), else the example deck of the core set with the
// random basic weakness (01000) in place of the weakness it drew
static JsonElement SimDeckList(JsonElement decks, string root, string code)
{
    var qa = J.Get(decks, "qa4-" + code);
    if (qa.ValueKind == JsonValueKind.Object) return qa;
    var ex = J.Get(decks, "example-" + code);
    var slots = J.Props(J.Get(ex, "slots")).ToDictionary(p => p.Name, p => p.Value.GetInt32());
    foreach (var w in slots.Keys.Where(k => k is "01096" or "01097" or "01098" or "01099" or "01100" or "01101" or "01102" or "01103").ToList()) slots.Remove(w);
    slots["01000"] = 1;
    return JsonSerializer.SerializeToElement(new { name = J.String(ex, "name"), investigator_code = code, slots, sideSlots = new Dictionary<string, int>() });
}

static Dictionary<string, int> SimDeck(TableModel m, string investigatorId)
{
    var campaignDeck = J.Get(J.Get(J.Get(J.Get(m.Game, "mode"), "This"), "decks"), investigatorId);
    var inv = m.Investigators.FirstOrDefault(i => i.Id == investigatorId);
    var cards = campaignDeck.ValueKind == JsonValueKind.Array ? campaignDeck : inv is null ? default : J.Get(inv.Raw, "deck");
    var slots = new Dictionary<string, int>();
    foreach (var card in J.Items(cards))
    {
        string code = J.String(card, "cardCode"); if (code.Length == 0) code = J.String(J.Get(card, "contents"), "cardCode");
        code = CoreCatalog.Normalize(code); if (code.Length == 0) continue;
        slots[code] = slots.GetValueOrDefault(code) + 1;
    }
    return slots;
}
