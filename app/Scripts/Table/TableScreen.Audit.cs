using Godot;
using Gathering;
using System.Text.Json;
using Path = System.IO.Path;

namespace ArkhamUI;

// QA only (autoplay): checks what the table and the prepare screen show against the rules core's own numbers.
// One line per check: "AUDIT_RULE <kind> ok|MISMATCH ...". Samples of the core's raw records go to <dir>/samples.
public partial class TableScreen
{
    readonly HashSet<string> auditOnce = new();
    (string Inv, string Card, string Code, int Before, int? Cost, int Hand)? pendingPlay;
    string lastTestKey = "";

    void RulesAudit(TableModel m, string dir)
    {
        string scen = ScenarioCode(m);
        var camp = Camp;
        // 1. the chaos bag the prepare screen promises, against the bag the core holds (first chapter only: later
        //    chapters' bags carry the campaign's additions, which are printed for the record)
        if (m.ChaosFaces.Any() && auditOnce.Add("bag-" + scen))
        {
            var core = m.ChaosFaces.OrderBy(f => f).ToArray();
            var ui = ChaosBagRules.Bag(m.Difficulty, camp.Id).OrderBy(f => f).ToArray();
            bool first = camp.Chapters.Length > 0 && (scen == camp.Chapters[0].Code || (camp.Id == "02" && scen == "02062" && !Completed(m).Contains("02041")));
            bool same = core.SequenceEqual(ui);
            var extra = core.GroupBy(f => f).Select(g => (g.Key, g.Count() - ui.Count(u => u == g.Key))).Where(p => p.Item2 != 0)
                .Concat(ui.Distinct().Where(f => !core.Contains(f)).Select(f => (f, -ui.Count(u => u == f))));
            GD.Print("AUDIT_RULE bag " + (same ? "ok" : first ? "MISMATCH" : "changed") + " scenario=" + scen + " difficulty=" + m.Difficulty
                + " core=" + string.Join(",", core) + (same ? "" : " diff=" + string.Join(",", extra.Select(p => p.Item1 + (p.Item2 > 0 ? "+" : "") + p.Item2))));
        }
        // 2. investigator stats on the selection screens against the core's investigator
        foreach (var inv in m.Investigators)
        {
            string code = CoreCatalog.Normalize(inv.Code);
            if (!auditOnce.Add("stats-" + scen + "-" + code)) continue;
            var core = new[] { m.Stat(inv, "willpower"), m.Stat(inv, "intellect"), m.Stat(inv, "combat"), m.Stat(inv, "agility"), m.Health(inv), m.Sanity(inv) };
            var ui = SelectionStage.Stats.GetValueOrDefault(code);
            bool same = ui is not null && ui.SequenceEqual(core);
            GD.Print("AUDIT_RULE stats " + (same ? "ok" : "MISMATCH") + " " + code + " core=" + string.Join("/", core) + " ui=" + (ui is null ? "none" : string.Join("/", ui)));
            Sample(dir, "investigator-" + code, inv.Raw);
        }
        // 2b. the skill the table shows (core modifiers applied) against the value the core starts a test with
        foreach (var inv in m.Investigators)
            foreach (var mod in J.Items(J.Get(inv.Raw, "modifiers")))
            {
                string t = J.Tag(J.Get(mod, "type"));
                if (t is "SkillModifier" or "BaseSkillOf" or "BaseSkill" or "AnySkillValue" or "ActionSkillModifier" or "SetSkillValue" && auditOnce.Add("mod-" + t + J.Get(mod, "type").GetRawText()))
                    GD.Print("AUDIT_RULE skillmod seen " + CoreCatalog.Normalize(inv.Code) + " " + J.Get(mod, "type").GetRawText());
            }
        if (m.InSkillTest && m.Me is not null && J.Tag(J.Get(m.SkillTest, "result")) == "Unrun")
        {
            var st = m.SkillTest;
            var skills = J.Items(J.Get(st, "skills")).Select(x => x.GetString() ?? "").ToArray();
            bool committed = J.Props(J.Get(st, "committedCards")).Any(p => J.Items(p.Value).Any());
            string inv = J.String(st, "investigator");
            var who = m.Investigators.FirstOrDefault(i => i.Id == inv);
            string key = "skill-" + m.State.Round + "-" + J.String(st, "id") + "-" + inv;
            if (!committed && skills.Length == 1 && who is not null && J.String(st, "action").Length > 0 && auditOnce.Add(key))
            {
                string k = skills[0].Replace("Skill", "").ToLowerInvariant();
                int ui = m.SkillValue(who, k), core = J.Int(st, "modifiedSkillValue");
                GD.Print("AUDIT_RULE skill " + (ui == core ? "ok" : "MISMATCH") + " " + CoreCatalog.Normalize(who.Code) + " " + k + " action=" + J.String(st, "action") + " shown=" + ui + " base=" + m.Stat(who, k) + " core=" + core);
                if (ui != core) Sample(dir, "skill-" + CoreCatalog.Normalize(who.Code) + "-" + k + "-" + core, st);
                // the difficulty: the enemy's fight / evade or the location's shroud as the table shows them
                var target = J.Get(st, "target"); string tid = J.String(target, "contents"); string action = J.String(st, "action");
                int shown = -1; string what = "";
                if (J.Tag(target) == "EnemyTarget" && m.Enemies.FirstOrDefault(e => e.Id == tid) is { } foe)
                { if (action == "Fight") { shown = m.EnemyFight(foe); what = "fight " + CoreCatalog.Normalize(foe.Code); } else if (action == "Evade") { shown = m.EnemyEvade(foe); what = "evade " + CoreCatalog.Normalize(foe.Code); } }
                else if (J.Tag(target) == "LocationTarget" && action == "Investigate" && m.Location(tid) is { } loc) { shown = m.Shroud(loc); what = "shroud " + CoreCatalog.Normalize(loc.Code); }
                if (what.Length > 0)
                {
                    int cd = J.Int(st, "modifiedDifficulty");
                    GD.Print("AUDIT_RULE difficulty " + (shown == cd ? "ok" : "MISMATCH") + " " + what + " shown=" + shown + " core=" + cd);
                    if (shown != cd) Sample(dir, "difficulty-" + what.Replace(" ", "-") + "-" + cd, st);
                }
            }
        }
        // 2c. the front end's reference-card table against the core's value of each face in this test
        // only for the viewer's own test: the table reads the viewer's elder sign, the core the test investigator's (a commit to
        // someone else's test is seen from the committing seat)
        if (m.InSkillTest && m.Me is not null && J.String(m.SkillTest, "investigator") == m.Me.Id)
            foreach (var t in J.Items(J.Get(J.Get(m.SkillTest, "valueBreakdown"), "tokens")))
            {
                string face = J.String(t, "face");
                var (_, core) = CoreTokenValue(m, face);
                var table = TableTokenModifier(m, face);
                string k = "tok-" + scen + "-" + face + "-" + CoreCatalog.Normalize(m.Me.Code) + "-" + core + "-" + table;
                if (auditOnce.Add(k))
                    GD.Print("AUDIT_RULE token " + (core == table ? "ok" : "MISMATCH") + " scenario=" + scen + " " + m.Difficulty + " face=" + face + " inv=" + CoreCatalog.Normalize(m.Me.Code) + " table=" + (table?.ToString() ?? "fail") + " core=" + (core?.ToString() ?? "fail") + " raw=" + t.GetRawText());
            }
        // 4b. the story the core reads, as locale keys (lines it marks as not applying get a "-" in front): the flow check
        if (m.QuestionTag == "Read")
        {
            var raw = ClientQuestions.Unwrap(m.State.Question).GetRawText();
            var keys = new List<string>(); StoryKeys(JsonDocument.Parse(raw).RootElement, false, keys);
            string line = string.Join(" ", keys);
            if (line.Length > 0 && auditOnce.Add("story-" + line)) GD.Print("AUDIT_STORY scenario=" + scen + " " + line);
        }
        // 5. text the core asks with: question title, body and every choice must come out in Chinese
        foreach (var text in new[] { m.Question.Title, m.Question.Body }.Concat(m.Question.Choices.Select(c => c.Title + " " + c.Hint)))
            if (text is { Length: > 0 } && System.Text.RegularExpressions.Regex.IsMatch(System.Text.RegularExpressions.Regex.Replace(System.Text.RegularExpressions.Regex.Replace(text, @"<[^>]*>", ""), @"(?<![A-Za-z])[IVX]+(?![A-Za-z])", ""), @"[A-Za-z]{3,}") && auditOnce.Add("en-" + text))
                GD.Print("AUDIT_RULE text MISMATCH scenario=" + scen + " question=" + m.QuestionTag + " :: " + Trunc(text.Replace('\n', ' '), 200));
        foreach (var e in m.Enemies) Sample(dir, "enemy-" + CoreCatalog.Normalize(e.Code), e.Raw);
        foreach (var l in m.Locations.Take(1)) Sample(dir, "location-" + scen, l.Raw);
        foreach (var a in m.Assets.Take(1)) Sample(dir, "asset-" + CoreCatalog.Normalize(a.Code), a.Raw);
        // 3. a resolved skill test: the table's token modifiers against the core's result
        if (m.InSkillTest && J.Tag(J.Get(m.SkillTest, "result")) is "SucceededBy" or "FailedBy")
        {
            var st = m.SkillTest;
            var faces = J.Items(J.Get(st, "resolvedChaosTokens")).Select(t => J.String(t, "chaosTokenFace")).ToArray();
            if (tokenSubstitute is { } sub && faces.Length == 1) faces = new[] { sub };   // resolved as the chosen token (歪打正着)
            string key = m.State.Round + "|" + J.String(st, "id") + "|" + string.Join(",", faces) + "|" + J.Get(st, "result").GetRawText();
            if (key != lastTestKey)
            {
                lastTestKey = key;
                int value = J.Int(st, "modifiedSkillValue"), diff = J.Int(st, "modifiedDifficulty");
                var res = J.Get(st, "result"); string tag = J.Tag(res);
                var arr = J.Get(res, "contents");
                int by = arr.ValueKind == JsonValueKind.Array && arr.GetArrayLength() == 2 && arr[1].ValueKind == JsonValueKind.Number ? arr[1].GetInt32() : arr.ValueKind == JsonValueKind.Number ? arr.GetInt32() : 0;
                bool autoFail = arr.ValueKind == JsonValueKind.Array && arr.GetArrayLength() == 2 && (arr[0].ValueKind == JsonValueKind.True || arr[0].ValueKind == JsonValueKind.String && arr[0].GetString() == "Automatic");
                int coreTotal = tag == "SucceededBy" ? by : -by;
                var mods = faces.Select(f => TokenModifier(m, f)).ToArray();
                bool uiFail = mods.Any(x => x is null);
                int uiTotal = Math.Max(0, value + mods.Sum(x => x ?? 0)) - diff;   // the core floors the modified skill value at 0
                // a symbol token's −X depends on board state the table estimates (marked "estimate"); numbers must match
                bool symbol = faces.Any(f => f is "Skull" or "Cultist" or "Tablet" or "ElderThing" or "ElderSign");
                string verdict = autoFail ? "auto" : uiFail ? (tag == "FailedBy" ? "ok" : "MISMATCH") : uiTotal == coreTotal ? "ok" : (uiTotal >= 0) == (coreTotal >= 0) && symbol ? "near" : "MISMATCH";
                GD.Print("AUDIT_RULE test " + verdict + " scenario=" + scen + " action=" + J.String(st, "action") + " inv=" + (m.Me is null ? "" : CoreCatalog.Normalize(m.Me.Code))
                    + " value=" + value + " diff=" + diff + " tokens=" + string.Join(",", faces) + " uiMods=" + string.Join(",", mods.Select(x => x?.ToString() ?? "fail"))
                    + " ui=" + (uiFail ? "fail" : uiTotal.ToString()) + " core=" + tag + ":" + by + (autoFail ? "(auto)" : ""));
                if (verdict != "ok") Sample(dir, "test-" + scen + "-" + string.Join("_", faces) + "-" + verdict, st);
            }
        }
        // 4. a card just played: the resources spent against the cost the card shows
        if (pendingPlay is { } p)
        {
            var inv = m.Investigators.FirstOrDefault(i => i.Id == p.Inv);
            bool gone = inv is null || !m.Hand(inv).Any(h => h.Id == p.Card);
            if (gone && !m.InSkillTest)
            {
                int spent = inv is null ? 0 : p.Before - m.Resources(inv);
                GD.Print("AUDIT_RULE cost " + (p.Cost is null || spent == p.Cost ? "ok" : "check") + " card=" + p.Code + " name=" + S.Name(p.Code) + " shown=" + (p.Cost?.ToString() ?? "-") + " spent=" + spent);
                pendingPlay = null;
            }
            else if (m.QuestionTag == "PlayerWindowChooseOne" && !gone) pendingPlay = null;   // not played after all
        }
    }

    // Every locale key in a story tree, depth first; a key under an InvalidEntry mark gets a "-" in front.
    static void StoryKeys(JsonElement e, bool invalid, List<string> keys)
    {
        if (e.ValueKind == JsonValueKind.Array) { foreach (var x in e.EnumerateArray()) StoryKeys(x, invalid, keys); return; }
        if (e.ValueKind != JsonValueKind.Object) return;
        bool inv = invalid || (J.Tag(e) == "ModifyEntry" && J.Items(J.Get(e, "modifiers")).Any(x => x.ValueKind == JsonValueKind.String && x.GetString() == "InvalidEntry"));
        if (J.String(e, "key") is { Length: > 0 } k) keys.Add((inv ? "-" : "") + k);
        foreach (var p in e.EnumerateObject()) if (p.Name != "key") StoryKeys(p.Value, inv, keys);
    }

    // Called with the choice autoplay is about to take.
    void AuditChoice(TableModel m, ChoicePresentation c)
    {
        if (m.Me is null || c.CardCode.Length == 0 || !c.Title.StartsWith("打出")) return;
        string anchor = m.Anchor(c);
        string card = anchor.StartsWith("hand:") ? anchor[5..] : (m.Hand(m.Me).FirstOrDefault(h => h.Code == CoreCatalog.Normalize(c.CardCode)).Id ?? "");
        if (card.Length == 0) return;
        pendingPlay = (m.Me.Id, card, CoreCatalog.Normalize(c.CardCode), m.Resources(m.Me), CoreCosts.Cost(c.CardCode), m.Hand(m.Me).Count());
    }

    void Sample(string dir, string name, JsonElement raw)
    {
        if (!auditOnce.Add("sample-" + name)) return;
        try
        {
            var folder = Path.Combine(App.I.Root, dir, "samples"); Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, name.Replace(":", "-") + ".json"), raw.GetRawText());
        }
        catch (Exception e) { GD.PushWarning("audit sample " + name + ": " + e.Message); }
    }
}
