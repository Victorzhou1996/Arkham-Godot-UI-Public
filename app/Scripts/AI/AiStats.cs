using Gathering;
using System.Text.Json;

namespace ArkhamUI;

// Per-chapter statistics of an autoplay run: one AI_SUMMARY line in the log and one JSON line in evidence/ai/results.jsonl.
public sealed class AiStats
{
    public string Run = "", Ai = "", Flags = "";
    string scenario = ""; DateTime started = DateTime.Now; int steps, undos, maxRound, turnRetries, testUndos, lossUndos;
    readonly HashSet<string> down = new(); int defeats;
    string lastPhase = "";
    public int Rounds;   // rounds seen in this whole run (the runaway guard counts answers per round with it)
    readonly HashSet<string> written = new();

    public void Step(TableModel m)
    {
        string sc = AiHost.ScenarioCode(m);
        if (sc != scenario && m.Phase is "InvestigationPhase" or "MythosPhase" or "EnemyPhase" or "UpkeepPhase")
        {
            if (scenario.Length > 0 && !written.Contains(scenario)) Write(m, "未记录结局", scenario);
            scenario = sc; started = DateTime.Now; steps = undos = maxRound = turnRetries = testUndos = lossUndos = defeats = 0; down.Clear();
        }
        steps++;
        // rounds counted from the phases seen (the view's round counter reads 0): a new investigation phase is a new round
        if (m.Phase == "InvestigationPhase" && lastPhase != "InvestigationPhase") { maxRound++; Rounds++; }
        lastPhase = m.Phase;
        foreach (var i in m.Investigators)
        {
            bool d = J.Get(i.Raw, "defeated").ValueKind == JsonValueKind.True;
            if (d && down.Add(i.Id)) defeats++;
            if (!d) down.Remove(i.Id);
        }
    }
    public void Undo(string kind) { undos++; if (kind == "turn") turnRetries++; else if (kind == "test") testUndos++; else lossUndos++; }

    // the chapter ended (a standalone game is over, or the campaign moved on): write its line once
    public void End(TableModel m, string result)
    {
        if (scenario.Length == 0) scenario = AiHost.ScenarioCode(m);
        if (written.Contains(scenario)) return;
        Write(m, result, scenario);
    }

    void Write(TableModel m, string result, string sc)
    {
        written.Add(sc);
        var row = new Dictionary<string, object>
        {
            ["time"] = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"), ["run"] = Run, ["ai"] = Ai, ["flags"] = Flags,
            ["scenario"] = sc, ["players"] = m.Investigators.Count(), ["investigators"] = string.Join("/", m.Investigators.Select(i => CoreCatalog.Normalize(i.Code))),
            ["difficulty"] = m.Difficulty, ["result"] = result, ["rounds"] = maxRound, ["steps"] = steps, ["defeats"] = defeats,
            ["undos"] = undos, ["turnRetries"] = turnRetries, ["testUndos"] = testUndos, ["lossUndos"] = lossUndos,
            ["damage"] = m.Investigators.Sum(i => m.Damage(i)), ["horror"] = m.Investigators.Sum(i => m.Horror(i)),
            ["act"] = m.Act?.Code ?? "", ["seconds"] = (int)(DateTime.Now - started).TotalSeconds,
            ["seed"] = System.Environment.GetEnvironmentVariable("ARKHAM_QA_SEED") ?? "",
            // behaviour checks: rule → [violations, times checked]
            ["checks"] = AiBrain.CheckAll.ToDictionary(kv => kv.Key, kv => new[] { AiBrain.CheckBad.GetValueOrDefault(kv.Key), kv.Value })
        };
        AiNet.RecordEnd(m, result);
        string json = JsonSerializer.Serialize(row, new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
        AiHost.Log("AI_SUMMARY " + json);
        try
        {
            var file = Path.Combine(AiHost.ResultsDir, "results.jsonl");
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.AppendAllText(file, json + System.Environment.NewLine, System.Text.Encoding.UTF8);
        }
        catch (Exception e) { AiHost.Log("AI_SUMMARY_WRITE_FAIL " + e.Message); }
    }

    // the result of a chapter as the core records it: the outcome when the game is over, else the campaign log's last resolution
    public static string Result(TableModel m)
    {
        var outcome = J.Get(m.Game, "outcome");
        string r = J.String(outcome, "result");
        if (r.Length > 0) return r + (J.Get(outcome, "resolution").ValueKind != JsonValueKind.Undefined ? " " + J.Get(outcome, "resolution").GetRawText() : "");
        return outcome.ValueKind == JsonValueKind.Undefined ? "?" : outcome.GetRawText() is var raw && raw.Length > 160 ? raw[..160] : outcome.GetRawText();
    }
}
