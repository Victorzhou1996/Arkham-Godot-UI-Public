using Gathering;
using System.Text.Json;

namespace ArkhamUI;

// The learned part of the AI (trained on the local RTX 4080 by tools/ai/train.py, see docs/AI-PLAY-PLAN-20261119.md §4.2):
//  - features of the situation and of each offered choice (fixed-length vectors),
//  - a small MLP Q(state, choice) read from evidence/ai/model/policy.json and evaluated here in plain C# (no extra packages),
//  - recording of every turn decision (features, the choice taken, progress so far) for training.
public static class AiNet
{
    public const int StateDim = 26, ChoiceDim = 22;
    public static readonly string[] Kinds = { "end", "act", "investigate", "fight", "evade", "move", "draw", "resource", "play", "ability", "other" };

    // ── features ──
    public static float[] State(TableModel m, AiContext x)
    {
        var g = x.Goal;
        float C(double v, double scale) => (float)Math.Clamp(v / scale, -2, 2);
        int actIdx = J.Items(J.Get(m.Act?.Raw ?? default, "sequence")).FirstOrDefault() is { ValueKind: JsonValueKind.Number } sq ? sq.GetInt32() : 1;
        return new[]
        {
            C(x.Players, 4), C(actIdx, 3), C(x.Hp, 10), C(x.San, 10), C(x.Actions, 3), C(x.Resources, 10), C(x.HandSize, 8),
            C(x.CluesHere, 8), C(x.MyClues, 8), C(g.CluesNeeded == 0 ? 1 : (double)x.TeamClues / g.CluesNeeded, 1), C(g.CluesNeeded, 12),
            C(x.Engaged, 3), C(x.EngagedThreat, 6), C(x.EnemiesOnMap, 6), C(Math.Min(x.DoomMargin, 12), 10),
            C(x.Intellect, 6), C(x.Combat, 6), C(x.Agility, 6), C(x.Willpower, 6),
            x.MyAssetCodes.Any(AiKnow.IsWeapon) ? 1 : 0, g.CluesMatter ? 1 : 0, g.TargetEnemy is not null ? 1 : 0, g.SpendAtRoundEnd ? 1 : 0,
            m.Phase == "InvestigationPhase" ? 1 : 0, m.Difficulty switch { "Easy" => 0, "Standard" => 0.5f, _ => 1 }, g.AllyAt is not null ? 1 : 0
        };
    }

    public static string Kind(TableModel m, ChoicePresentation c, string why)
    {
        string verb = m.ActionVerb(c);
        if (c.Tag == "EndTurnButton") return "end";
        if (m.Anchor(c) == "act") return "act";
        if (verb == "Investigate") return "investigate";
        if (verb == "Fight" || c.Tag is "FightLabel" or "FightLabelWithSkill") return "fight";
        if (verb == "Evade" || c.Tag is "EvadeLabel" or "EvadeLabelWithSkill") return "evade";
        if (verb == "Move") return "move";
        if (why == "抽牌") return "draw";
        if (why == "资源") return "resource";
        if (c.Title.StartsWith("打出")) return "play";
        if (c.Tag == "AbilityLabel") return "ability";
        return "other";
    }

    public static float[] Choice(TableModel m, AiContext x, ChoicePresentation c, string kind, double heuristic, string? dest, bool provokes)
    {
        var f = new float[ChoiceDim];
        int k = Array.IndexOf(Kinds, kind); if (k >= 0) f[k] = 1;
        int i = Kinds.Length;
        f[i++] = (float)Math.Clamp(heuristic / 20, -3, 3);
        f[i++] = provokes ? 1 : 0;
        string code = c.CardCode;
        f[i++] = code.Length > 0 ? AiKnow.Cost(code) / 5f : 0;
        f[i++] = code.Length > 0 && AiKnow.IsWeapon(code) ? 1 : 0;
        f[i++] = code.Length > 0 && AiKnow.Fast(code) ? 1 : 0;
        string anchor = m.Anchor(c);
        f[i++] = x.Goal.TargetEnemy is not null && anchor == "enemy:" + x.Goal.TargetEnemy ? 1 : 0;
        var to = kind == "move" ? m.Location(c.TargetId.Length > 0 ? c.TargetId : anchor.Replace("loc:", "")) : null;
        if (to is not null)
        {
            int now = dest is null ? 0 : x.Dist.GetValueOrDefault(dest, 9), after = dest is null ? 0 : AiContext.Distances(m, to.Id).GetValueOrDefault(dest, 9);
            f[i++] = Math.Sign(now - after);
            f[i++] = m.Revealed(to) ? Math.Min(m.Clues(to), 8) / 4f : 0.5f;
            f[i++] = m.EnemiesAt(to.Id).Count() / 2f;
            f[i++] = to.Code is "01113" or "01114" ? 1 : 0;
        }
        else i += 4;
        f[i++] = kind == "investigate" && x.Here is not null ? Math.Min(m.Shroud(x.Here), 6) / 6f : 0;
        return f;
    }

    // ── team features (for the value network V: how well this game will end from here) ──
    public const int TeamDim = 50;
    public static float[] Team(TableModel m)
    {
        float C(double v, double scale) => (float)Math.Clamp(v / scale, -3, 3);
        var f = new float[TeamDim]; int k = 0;
        var g = AiGoal.Read(m);
        var inv = m.Investigators.ToArray();
        int actIdx = J.Items(J.Get(m.Act?.Raw ?? default, "sequence")).FirstOrDefault() is { ValueKind: JsonValueKind.Number } sq ? sq.GetInt32() : 0;
        int agendaIdx = J.Items(J.Get(m.Agenda?.Raw ?? default, "sequence")).FirstOrDefault() is { ValueKind: JsonValueKind.Number } aq ? aq.GetInt32() : 0;
        int team = inv.Sum(i => m.Clues(i));
        var enemies = m.Enemies.Where(e => J.Tag(J.Get(e.Raw, "placement")) is "AtLocation" or "InThreatArea").ToArray();
        f[k++] = C(actIdx, 3); f[k++] = C(agendaIdx, 3); f[k++] = C(inv.Length, 4);
        f[k++] = C(m.DoomThreshold > 0 ? Math.Min(12, m.DoomThreshold - m.TotalDoom) : 12, 10);
        f[k++] = C(g.CluesNeeded == 0 ? 1 : (double)team / g.CluesNeeded, 1); f[k++] = C(g.CluesNeeded, 12);
        f[k++] = C(enemies.Length, 6); f[k++] = C(enemies.Sum(e => Math.Max(0, m.EnemyHealth(e) - m.Damage(e))), 20);
        f[k++] = g.TargetEnemy is not null && m.Enemies.FirstOrDefault(e => e.Id == g.TargetEnemy) is { } t ? C(Math.Max(0, m.EnemyHealth(t) - m.Damage(t)), 20) : 0;
        f[k++] = m.Difficulty switch { "Easy" => 0, "Standard" => 0.5f, _ => 1 };
        // a test under way: its kind and its chance (the value of a state right after choosing an action that tests)
        bool test = m.InSkillTest;
        f[k++] = test ? 1 : 0;
        f[k++] = test ? (float)AiHost.PassChance(m, J.Int(m.SkillTest, "modifiedSkillValue"), J.Int(m.SkillTest, "modifiedDifficulty")) : 0;
        string action = test ? J.String(m.SkillTest, "action") : "";
        f[k++] = action == "Investigate" ? 1 : 0; f[k++] = action == "Fight" ? 1 : 0;
        for (int s = 0; s < 4; s++, k += 9)
        {
            if (s >= inv.Length) continue;
            var i = inv[s]; var here = m.Location(m.LocationOf(i));
            f[k] = 1; f[k + 1] = C(m.Health(i) - m.Damage(i), 10); f[k + 2] = C(m.Sanity(i) - m.Horror(i), 10); f[k + 3] = C(m.Clues(i), 6);
            f[k + 4] = C(m.Resources(i), 10); f[k + 5] = C(m.Hand(i).Count(), 8);
            f[k + 6] = C(m.EngagedWith(i).Where(e => !AiContext.Exhausted(e)).Sum(AiContext.Threat), 6);
            f[k + 7] = J.Get(i.Raw, "defeated").ValueKind == JsonValueKind.True ? 1 : 0;
            f[k + 8] = here is null ? 0 : C(m.Clues(here), 6);
        }
        return f;
    }

    // ── networks: plain MLPs from JSON (trained by tools/ai/train2.py on the 4080), evaluated here in C# ──
    public sealed class Mlp
    {
        sealed class Layer { public float[,] W = null!; public float[] B = null!; }
        List<Layer>? layers; DateTime stamp; readonly string name; readonly int inputDim;
        public string Info = "";
        public Mlp(string name, int inputDim) { this.name = name; this.inputDim = inputDim; }
        public string File => Path.Combine(AiHost.ModelDir, name);
        public bool Loaded => layers is not null;
        public bool Load()
        {
            try
            {
                if (!System.IO.File.Exists(File)) { AiHost.Log("AI_NET_NONE " + File); return false; }
                var st = System.IO.File.GetLastWriteTimeUtc(File); if (layers is not null && st == stamp) return true;
                using var doc = JsonDocument.Parse(System.IO.File.ReadAllText(File));
                var root = doc.RootElement;
                if (J.Int(root, "inputDim") != inputDim) { AiHost.Log("AI_NET_DIM_MISMATCH " + name); return false; }
                var ls = new List<Layer>();
                foreach (var l in J.Items(J.Get(root, "layers")))
                {
                    var w = J.Items(J.Get(l, "w")).Select(r => J.Items(r).Select(v => (float)v.GetDouble()).ToArray()).ToArray();
                    var W = new float[w.Length, w[0].Length];
                    for (int a = 0; a < w.Length; a++) for (int b = 0; b < w[0].Length; b++) W[a, b] = w[a][b];
                    ls.Add(new Layer { W = W, B = J.Items(J.Get(l, "b")).Select(v => (float)v.GetDouble()).ToArray() });
                }
                layers = ls; stamp = st; Info = J.String(root, "info");
                AiHost.Log("AI_NET_LOADED " + name + " " + Info);
                return true;
            }
            catch (Exception e) { AiHost.Log("AI_NET_LOAD_FAIL " + name + " " + e.Message); layers = null; return false; }
        }
        public double Forward(float[] v)
        {
            for (int li = 0; li < layers!.Count; li++)
            {
                var L = layers[li]; int outN = L.W.GetLength(0), inN = L.W.GetLength(1);
                var o = new float[outN];
                for (int a = 0; a < outN; a++) { float sum = L.B[a]; for (int b = 0; b < inN; b++) sum += L.W[a, b] * v[b]; o[a] = li < layers.Count - 1 ? Math.Max(0, sum) : sum; }
                v = o;
            }
            return v[0];
        }
    }
    public static readonly Mlp Policy = new("policy.json", StateDim + ChoiceDim);   // π(state, choice) score
    public static readonly Mlp ValueNet = new("value.json", TeamDim);                // V(team state)
    public static bool Loaded => Policy.Loaded;
    public static bool Load() => Policy.Load();
    public static double Score(float[] s, float[] c) { var v = new float[s.Length + c.Length]; s.CopyTo(v, 0); c.CopyTo(v, s.Length); return Policy.Forward(v); }
    public static double Value(TableModel m) => ValueNet.Forward(Team(m));

    // ── recording ──
    public static string? RecordFile;   // set by the flag "rec"
    public static bool DeferRecord;     // the simulator's teacher picks first, then writes the row (RecordPending)
    public sealed record Pending(TableModel M, string Seat, float[] S, List<float[]> C, int Chosen, int[] Order, ChoicePresentation[] Choices);
    public static Pending? Last;        // the last turn decision, for the teacher
    static int recIndex;
    public static void Record(TableModel m, AiContext x, float[] s, List<float[]> cs, int chosen, int[] order, ChoicePresentation[] choices)
    {
        Last = new Pending(m, CoreCatalog.Normalize(x.Me.Code), s, cs, chosen, order, choices);
        if (!DeferRecord) RecordPending(chosen, -1);
    }
    // a: the choice played; k: the teacher's choice (-1: no teacher)
    public static void RecordPending(int played, int teacher)
    {
        if (RecordFile is null || Last is null) return;
        var row = new { i = recIndex++, seat = Last.Seat, s = Last.S, c = Last.C, a = played, k = teacher, t = Team(Last.M), p = Progress(Last.M) };
        Append(JsonSerializer.Serialize(row));
    }
    public static void RecordEnd(TableModel m, string result)
    {
        if (RecordFile is null) return;
        Append(JsonSerializer.Serialize(new { end = true, result, p = Progress(m) }));
    }
    static void Append(string line)
    {
        try { Directory.CreateDirectory(Path.GetDirectoryName(RecordFile!)!); File.AppendAllText(RecordFile!, line + System.Environment.NewLine); }
        catch (Exception e) { AiHost.Log("AI_REC_FAIL " + e.Message); RecordFile = null; }
    }

    // progress snapshot for the training target: act step, team clues, health / sanity left, defeated, doom
    public static int[] Progress(TableModel m)
    {
        int actIdx = J.Items(J.Get(m.Act?.Raw ?? default, "sequence")).FirstOrDefault() is { ValueKind: JsonValueKind.Number } sq ? sq.GetInt32() : 0;
        var inv = m.Investigators.ToArray();
        return new[]
        {
            actIdx, inv.Sum(i => m.Clues(i)), inv.Sum(i => Math.Max(0, m.Health(i) - m.Damage(i))), inv.Sum(i => Math.Max(0, m.Sanity(i) - m.Horror(i))),
            inv.Count(i => J.Get(i.Raw, "defeated").ValueKind == JsonValueKind.True), m.TotalDoom, inv.Length,
            m.Enemies.Count(e => J.Tag(J.Get(e.Raw, "placement")) is "AtLocation" or "InThreatArea"),
            // 8.. (11-20 stage 3, Yunke: score by the game's own rules): the agenda's doom threshold and place in its deck,
            // the investigators' printed health / sanity (damage as a share of what defeats them), the victory display
            m.DoomThreshold,
            J.Items(J.Get(m.Agenda?.Raw ?? default, "sequence")).FirstOrDefault() is { ValueKind: JsonValueKind.Number } ag ? ag.GetInt32() : 0,
            inv.Sum(i => m.Health(i)), inv.Sum(i => m.Sanity(i)), m.VictoryDisplay.Count()
        };
    }
}
