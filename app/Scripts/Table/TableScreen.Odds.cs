using Godot;
using Gathering;

namespace ArkhamUI;

// The chaos bag in the skill test box, and the chance to pass the test as it stands (playtest 2 #9; the 0712 web table
// shows the bag there too). The chance is a display estimate from the bag's tokens; the core still resolves the test.
public partial class TableScreen
{
    // The scenario being played (its card code), from the core's scenario record.
    internal static string ScenarioCode(TableModel m) { var id = J.String(m.Scenario, "id"); return id.Length > 0 ? CoreCatalog.Normalize(id) : "01104"; }

    // The face the revealed token resolves as when the player chose another one for it (歪打正着), for this test only.
    string? tokenSubstitute;

    // Modifier of one token face for this investigator at this moment, or null when the token fails the test outright.
    static int? TokenModifier(TableModel m, string face)
    {
        // During a skill test the core sends each face's value for this test (valueBreakdown): that is the rule, use it.
        // The tables below are only for when no test is running (and as a fallback for a face the core did not list).
        // 吉姆·卡尔弗: in his own tests every revealed skull's modifier becomes 0 (rules source JimCulver.hs,
        // ChangeChaosTokenModifier (PositiveModifier 0)); the core's per-face list still shows the scenario's skull value, but it
        // resolves the skull as 0 (review 11-18 "吉姆骷髅检定显示与核心结算不符": core result succeeded by 0 at 2 vs 2)
        if (face == "Skull" && m.InSkillTest && m.Investigators.FirstOrDefault(i => i.Id == J.String(m.SkillTest, "investigator")) is { } ti && CoreCatalog.Normalize(ti.Code) == "02004") return 0;
        // A card in play that turns a face into an automatic failure for this investigator (The Necronomicon: "将你揭示的每个混乱
        // 标记上的[elder_sign]视为[auto_fail]"). The core resolves it so, but its per-face preview did not show it (4-player run,
        // Daisy: the preview promised a pass, the test failed automatically).
        var tester = (m.InSkillTest ? m.Investigators.FirstOrDefault(i => i.Id == J.String(m.SkillTest, "investigator")) : null) ?? m.Me;
        if (tester is not null && AutoFailSource(m, tester, face) is not null) return null;
        // (The elder sign is the core's: an earlier "mismatch" was the audit comparing the viewer's own elder sign with the core's
        // value for the investigator taking the test — Agnes's +1 per horror while Daisy was committing to her test.)
        if (CoreTokenValue(m, face) is var (found, v) && found) return v;
        return TableTokenModifier(m, face);
    }

    static readonly System.Text.RegularExpressions.Regex AutoFailText = new(@"\[(elder_sign|skull|cultist|tablet|elder_thing)\][^。]{0,6}视为\[auto_fail\]");
    static string FaceSymbol(string face) => face switch { "ElderSign" => "elder_sign", "Skull" => "skull", "Cultist" => "cultist", "Tablet" => "tablet", "ElderThing" => "elder_thing", _ => "" };

    // The card in this investigator's threat area or play area whose constant effect makes `face` an automatic failure, if any.
    internal static Entity? AutoFailSource(TableModel m, Entity inv, string face)
    {
        string sym = FaceSymbol(face); if (sym.Length == 0) return null;
        foreach (var e in m.ThreatsOf(inv).Concat(m.AssetsOf(inv)))
            foreach (System.Text.RegularExpressions.Match mt in AutoFailText.Matches(J.String(App.I.S!.Card(CoreCatalog.Normalize(e.Code)), "text")))
                if (mt.Groups[1].Value == sym) return e;
        return null;
    }

    // The front end's own reading of the reference cards (no test running). Audited against the core's values.
    internal static int? TableTokenModifier(TableModel m, string face)
    {
        bool hard = m.Difficulty is "Hard" or "Expert";
        var me = m.Me; var here = m.MyLocation;
        if (ScenarioCode(m) == "01120" && face is "Skull" or "Cultist" or "Tablet")
        {
            // The Midnight Masks' reference card: Easy/Standard skull −X (most doom on one Cultist enemy), cultist −2, tablet −3;
            // Hard/Expert skull −X (all doom in play), cultist −2, tablet −4.
            bool Cultist(Entity e) => J.String(App.I.S!.Card(e.Code), "traits").Contains("异教徒");
            int x = hard ? m.TotalDoom : m.Enemies.Where(Cultist).Select(e => m.Doom(e)).DefaultIfEmpty(0).Max();
            return face switch { "Skull" => -x, "Cultist" => -2, _ => hard ? -4 : -3 };
        }
        if (ScenarioCode(m) == "01142" && face is "Skull" or "Cultist" or "Tablet" or "ElderThing")
        {
            // The Devourer Below's reference card: Easy/Standard skull −X (Monster enemies in play), cultist −2, tablet −3,
            // elder thing −5; Hard/Expert skull −3, cultist −4, tablet −5, elder thing −7.
            int monsters = m.Enemies.Count(e => J.String(App.I.S!.Card(e.Code), "traits").Contains("怪物"));
            return face switch { "Skull" => hard ? -3 : -monsters, "Cultist" => hard ? -4 : -2, "Tablet" => hard ? -5 : -3, _ => hard ? -7 : -5 };
        }
        // Jim Culver treats the skull's modifier as 0 (his card)
        if (face == "Skull" && me is not null && CoreCatalog.Normalize(me.Code) == "02004") return 0;
        if (ScenarioCode(m).StartsWith("02") && DunwichModifier(m, face, hard) is var (known, mod) && known) return mod;
        if (ScenarioCode(m).StartsWith("03") && CarcosaModifier(m, face, hard) is var (cknown, cmod) && cknown) return cmod;
        int ghoulsHere = here is null ? 0 : m.EnemiesHere(here.Id).Count(e => J.String(App.I.S!.Card(e.Code), "traits").Contains("食尸鬼"));
        switch (face)
        {
            case "PlusOne": return 1;
            case "Zero": return 0;
            case "MinusOne": return -1; case "MinusTwo": return -2; case "MinusThree": return -3; case "MinusFour": return -4;
            case "MinusFive": return -5; case "MinusSix": return -6; case "MinusSeven": return -7; case "MinusEight": return -8;
            case "AutoFail": return null;
            // The Gathering's reference card: Easy/Standard skull −X (X = Ghoul enemies at your location), cultist −1,
            // tablet −2; Hard/Expert skull −2, cultist reveals another token (counted as −1 here), tablet −4.
            case "Skull": return hard ? -2 : -ghoulsHere;
            case "Cultist": return -1;
            case "Tablet": return hard ? -4 : -2;
            case "ElderThing": return -2;
            case "ElderSign":
                // each investigator's own elder sign (core set): Roland +1 per clue at his location, Skids +2, Agnes +1 per horror
                return me is null ? 0 : CoreCatalog.Normalize(me.Code) switch
                {
                    "01001" => here is null ? 0 : m.Clues(here),
                    "01003" => 2,
                    "01004" => m.Horror(me),
                    // The Dunwich Legacy investigators (their cards' elder sign effects)
                    "02001" => 1, "02002" => 2, "02003" => m.Resources(me), "02004" => 1, "02005" => 2,
                    // The Path to Carcosa investigators (their cards: Mark +1 per damage on him, Minh +1, Sefina +3, Akachi +1, Yorick +2, Lola +2)
                    "03001" => m.Damage(me), "03002" => 1, "03003" => 3, "03004" => 1, "03005" => 2, "03006" => 2,
                    _ => 0,
                };
            default: return 0;
        }
    }

    // The Dunwich Legacy: each scenario's reference card (official text: theDunwichLegacy.<scenario>.tokens). A token that
    // reveals another counts as −2 here; −X from discarded cards' printed costs is estimated at 2 per card. Display only.
    static (bool Known, int? Mod) DunwichModifier(TableModel m, string face, bool hard)
    {
        var me = m.Me; var here = m.MyLocation;
        bool Has(Entity e, string trait) => J.String(App.I.S!.Card(e.Code), "traits").Contains(trait);
        bool Named(Entity e, string code) => CoreCatalog.Normalize(e.Code) == code;
        int agenda = m.Agenda is null ? 1 : int.TryParse(CoreCatalog.Normalize(m.Agenda.Code), out int a) ? a : 1;
        const int Another = -2;
        int? r = (ScenarioCode(m), face) switch
        {
            // 课外活动
            ("02041", "Skull") => hard ? -2 : -1,
            ("02041", "Cultist") => me is not null && m.DiscardCount(me) >= 10 ? (hard ? -5 : -3) : -1,
            ("02041", "ElderThing") => hard ? -6 : -4,
            // 百赌百胜
            ("02062", "Skull") => hard ? -3 : -2,
            ("02062", "Cultist") => -3,
            ("02062", "Tablet") => -2,
            // 博物馆之夜: the Hunting Horror (02141) at your location makes the skull worse
            ("02118", "Skull") => here is not null && m.EnemiesHere(here.Id).Any(e => Named(e, "02141")) ? (hard ? -4 : -3) : (hard ? -2 : -1),
            ("02118", "Cultist") => hard ? -3 : -1,
            ("02118", "Tablet") => hard ? -4 : -2,
            ("02118", "ElderThing") => hard ? -5 : -3,
            // 疯狂列车: skull −(agenda number) (+1 on Hard/Expert); agendas 02160–02165 are numbered 1–6
            ("02159", "Skull") => -(Math.Max(1, agenda - 2159) + (hard ? 1 : 0)),
            ("02159", "Cultist") => hard ? Another : -1,
            ("02159", "Tablet") => hard ? -4 : -2,
            ("02159", "ElderThing") => -3,
            // 血色祭坛: skull −1 per location with no encounter card beneath it (at most −4 on Easy/Standard); estimated by locations in play
            ("02195", "Skull") => -(hard ? m.Locations.Count() : Math.Min(4, m.Locations.Count())),
            ("02195", "Cultist") => hard ? -4 : -2,
            ("02195", "Tablet") => hard ? -3 : -2,
            ("02195", "ElderThing") => -3,
            // 无人知晓: −1 (−2) per Brood of Yog-Sothoth in play
            ("02236", "Skull") => -(hard ? 2 : 1) * m.Enemies.Count(e => App.I.S!.Name(CoreCatalog.Normalize(e.Code)).Contains("犹格·索托斯之子")),
            ("02236", "Cultist") => Another,
            ("02236", "Tablet") => 0,
            ("02236", "ElderThing") => hard ? -5 : -3,
            // 毁灭之地: at an Altered location the skull is worse; on agenda 2 the tablet is worse (Hard/Expert: fails)
            ("02274", "Skull") => here is not null && Has(here, "幻境") ? (hard ? -5 : -3) : (hard ? -2 : -1),
            ("02274", "Cultist") => Another,
            ("02274", "Tablet") => m.Agenda is not null && CoreCatalog.Normalize(m.Agenda.Code) == "02276" ? (hard ? null : -4) : (hard ? -3 : -2),
            ("02274", "ElderThing") => hard ? -6 : -4,
            // 迷失时空: −1 per Extradimensional location (at most −5 on Easy/Standard); elder thing −(shroud here) (×2) (rules source LostInTimeAndSpace.hs)
            ("02311", "Skull") => -(hard ? m.Locations.Count(l => Has(l, "异次元")) : Math.Min(5, m.Locations.Count(l => Has(l, "异次元")))),
            ("02311", "Cultist") => Another,
            ("02311", "Tablet") => hard ? -5 : -3,
            ("02311", "ElderThing") => here is null ? 0 : -(m.Shroud(here) * (hard ? 2 : 1)),
            _ => 0,
        };
        bool known = face is "Skull" or "Cultist" or "Tablet" or "ElderThing";
        return (known, r);
    }

    // The Path to Carcosa: each scenario's reference card as the rules source has it (Scenario/Scenarios/ThePathToCarcosa/
    // <Scenario>.hs getChaosTokenValue, checked one by one 11-20; official wording: thePathToCarcosa.<scenario>.tokens).
    // A token that reveals another counts as −2 here (as for the Dunwich Legacy). Display only: in a test the core's
    // valueBreakdown is used.
    static (bool Known, int? Mod) CarcosaModifier(TableModel m, string face, bool hard)
    {
        var me = m.Me; var here = m.MyLocation;
        const int Another = -2;
        int horror = me is null ? 0 : m.Horror(me);
        int? r = (ScenarioCode(m), face) switch
        {
            // 谢幕: skull −1 (−3 with 3+ horror on you); Hard/Expert −(your horror, at least 1). Cultist/tablet/elder thing −4 (−5)
            ("03043", "Skull") => hard ? -Math.Max(1, horror) : (horror >= 3 ? -3 : -1),
            ("03043", "Cultist" or "Tablet" or "ElderThing") => hard ? -5 : -4,
            // 最后的王者: skull reveals another (no modifier of its own); cultist −2 (−3); tablet −4; elder thing −(shroud here)
            ("03061", "Skull") => Another,
            ("03061", "Cultist") => hard ? -3 : -2,
            ("03061", "Tablet") => -4,
            ("03061", "ElderThing") => here is null ? 0 : -m.Shroud(here),
            // 往事回声: skull −(most doom on one enemy); Hard/Expert −(all doom on enemies) (the source's order; the zh card text
            // has the two the other way round). Cultist/tablet/elder thing −2 (−4)
            ("03120", "Skull") => hard ? -m.Enemies.Sum(e => m.Doom(e)) : -m.Enemies.Select(e => m.Doom(e)).DefaultIfEmpty(0).Max(),
            ("03120", "Cultist" or "Tablet" or "ElderThing") => hard ? -4 : -2,
            // 邪秽誓约: skull −1 (Hard/Expert: reveals another); cultist −(your horror); tablet −(base shroud here); elder thing 0
            ("03159", "Skull") => hard ? Another : -1,
            ("03159", "Cultist") => -horror,
            ("03159", "Tablet") => here is null ? 0 : -m.PrintedShroud(here),
            ("03159", "ElderThing") => 0,
            // 真相幻影: skull −(doom in play, at most 5; Hard/Expert no cap); cultist −2; tablet −3 (−4); elder thing −2 (−3)
            ("03200", "Skull") => -(hard ? m.TotalDoom : Math.Min(5, m.TotalDoom)),
            ("03200", "Cultist") => -2,
            ("03200", "Tablet") => hard ? -4 : -3,
            ("03200", "ElderThing") => hard ? -3 : -2,
            // 苍白面具: skull −(locations between you and the starting location, at most 5; Hard/Expert no cap)
            ("03240", "Skull") => -(hard ? StepsFromStart(m) : Math.Min(5, StepsFromStart(m))),
            ("03240", "Cultist" or "Tablet") => hard ? -3 : -2,
            ("03240", "ElderThing") => hard ? -4 : -3,
            // 黑星升起: skull −(most doom on one agenda); Hard/Expert −(doom on both agendas). Cultist reveals another (fails
            // instead against an enemy with doom: Hard/Expert any such enemy at your location); tablet reveals another
            ("03274", "Skull") => hard ? -AgendaDoom(m).Sum() : -AgendaDoom(m).DefaultIfEmpty(0).Max(),
            ("03274", "Cultist") => hard && here is not null && m.EnemiesHere(here.Id).Any(e => m.Doom(e) > 0) ? null : Another,
            ("03274", "Tablet") => Another,
            ("03274", "ElderThing") => hard ? -3 : -2,
            // 卡城幽影: skull −2 (−4 with no sanity left); Hard/Expert −(your horror). Cultist reveals another; tablet and elder thing −3 (−5)
            ("03316", "Skull") => hard ? -horror : (me is not null && m.Sanity(me) - horror <= 0 ? -4 : -2),
            ("03316", "Cultist") => Another,
            ("03316", "Tablet" or "ElderThing") => hard ? -5 : -3,
            _ => 0,
        };
        return (face is "Skull" or "Cultist" or "Tablet" or "ElderThing", r);
    }
    static IEnumerable<int> AgendaDoom(TableModel m) => m.Agendas.Select(a => m.Doom(a));
    // 苍白面具: how many locations away from the starting location (the one placed at position 0,0) you are
    static int StepsFromStart(TableModel m)
    {
        var here = m.MyLocation; if (here is null) return 0;
        var start = m.Locations.FirstOrDefault(l => J.Get(l.Raw, "position") is var p && p.ValueKind == System.Text.Json.JsonValueKind.Object && J.Int(p, "x") == 0 && J.Int(p, "y") == 0);
        if (start is null) return 0;
        var dist = new Dictionary<string, int> { [start.Id] = 0 }; var queue = new Queue<string>(); queue.Enqueue(start.Id);
        while (queue.Count > 0)
        {
            var id = queue.Dequeue(); if (id == here.Id) return dist[id];
            if (m.Location(id) is not { } l) continue;
            foreach (var n in J.Items(J.Get(l.Raw, "connectedLocations")).Select(x => x.GetString() ?? "")) if (n.Length > 0 && dist.TryAdd(n, dist[id] + 1)) queue.Enqueue(n);
        }
        return 0;
    }

    // The core's value of one face in the running skill test: (true, value), (true, null) for an automatic failure,
    // (true, +100) for an automatic success; a face that reveals another token and has no value of its own counts −2.
    internal static (bool Found, int? Value) CoreTokenValue(TableModel m, string face)
    {
        if (!m.InSkillTest) return (false, null);
        foreach (var t in J.Items(J.Get(J.Get(m.SkillTest, "valueBreakdown"), "tokens")))
        {
            if (J.String(t, "face") != face) continue;
            if (J.Get(t, "autoFail").ValueKind == System.Text.Json.JsonValueKind.True) return (true, null);
            if (J.Get(t, "autoSuccess").ValueKind == System.Text.Json.JsonValueKind.True) return (true, 100);
            var v = J.Get(t, "value");
            if (v.ValueKind == System.Text.Json.JsonValueKind.Number) return (true, v.GetInt32() + (J.Get(t, "revealsAnother").ValueKind == System.Text.Json.JsonValueKind.True ? -2 : 0));
            return (true, J.Get(t, "revealsAnother").ValueKind == System.Text.Json.JsonValueKind.True ? -2 : 0);
        }
        return (false, null);
    }

    // Share of the bag's tokens with which `value` reaches `difficulty`. In a test the core's own rules for ties apply
    // (failTies: a tie fails) and its "automatically fails if it succeeds by N or more" (autoFailIfSucceedByAtLeast).
    internal static double PassChance(TableModel m, int value, int difficulty)
    {
        var faces = m.ChaosFaces.ToArray();
        if (faces.Length == 0) return 0;
        var vb = m.InSkillTest ? J.Get(m.SkillTest, "valueBreakdown") : default;
        bool failTies = vb.ValueKind == System.Text.Json.JsonValueKind.Object && J.Get(vb, "failTies").ValueKind == System.Text.Json.JsonValueKind.True;
        int? capAt = vb.ValueKind == System.Text.Json.JsonValueKind.Object ? J.Items(J.Get(vb, "autoFailIfSucceedByAtLeast")).Where(x => x.ValueKind == System.Text.Json.JsonValueKind.Number).Select(x => (int?)x.GetInt32()).Min() : null;
        bool Passes(int total) => (failTies ? value + total > difficulty : value + total >= difficulty) && !(capAt is int c && value + total - difficulty >= c);
        // A bag with tokens that reveal another (bless, curse, frost): a light simulation of the draws instead of counting each
        // such token as −2 (Yunke 11-19: "多个标记的估算可以使用模拟抽取来算个大概"). 100 draws, cached per state, so the panel
        // never stutters.
        if (faces.Any(f => RevealsAnother(m, f)))
        {
            var key = (m.State.Sequence, value, difficulty);
            if (simCache.TryGetValue(key, out var hit)) return hit;
            var rng = new Random(HashCode.Combine(m.State.Sequence, value, difficulty));
            var vals = faces.Select(f => (Mod: RawModifier(m, f), Again: RevealsAnother(m, f))).ToArray();
            int ok = 0; const int N = 100; var idx = new int[vals.Length];   // 100 draws (Yunke 11-20: 600 made the panel stutter)
            for (int n = 0; n < N; n++)
            {
                for (int i = 0; i < idx.Length; i++) idx[i] = i;
                int left = idx.Length, total = 0; bool fail = false, auto = false;
                while (left > 0)
                {
                    int k = rng.Next(left); var (mod, again) = vals[idx[k]]; idx[k] = idx[--left];
                    if (mod is null) { fail = true; break; }
                    if (mod >= 100) { auto = true; break; }
                    total += mod.Value;
                    if (!again) break;
                }
                if (auto || (!fail && Passes(total))) ok++;
            }
            if (simCache.Count > 64) simCache.Clear();
            return simCache[key] = (double)ok / N;
        }
        int pass = faces.Count(f => TokenModifier(m, f) is int mod && Passes(mod));
        return (double)pass / faces.Length;
    }

    static readonly Dictionary<(long, int, int), double> simCache = new();

    // a face that makes the test reveal another token (the core says so in its breakdown; bless / curse / frost always do)
    static bool RevealsAnother(TableModel m, string face)
    {
        if (face is "BlessToken" or "CurseToken" or "FrostToken") return true;
        if (!m.InSkillTest) return false;
        return J.Items(J.Get(J.Get(m.SkillTest, "valueBreakdown"), "tokens")).Any(t => J.String(t, "face") == face && J.Get(t, "revealsAnother").ValueKind == System.Text.Json.JsonValueKind.True);
    }

    // a face's own modifier, without the −2 stand-in for "reveals another" (the simulation draws that other token itself)
    static int? RawModifier(TableModel m, string face)
    {
        if (RevealsAnother(m, face) && m.InSkillTest)
            foreach (var t in J.Items(J.Get(J.Get(m.SkillTest, "valueBreakdown"), "tokens")))
                if (J.String(t, "face") == face && J.Get(t, "value").ValueKind == System.Text.Json.JsonValueKind.Number) return J.Get(t, "value").GetInt32();
        if (face == "BlessToken") return 2;
        if (face == "CurseToken") return -2;
        if (face == "FrostToken") return -1;
        return TokenModifier(m, face);
    }

    // One row: every face in the bag as a small token with its count, then the chance to pass.
    Control BagRow(TableModel m, int value, int difficulty, bool showChance)
    {
        var row = new HBoxContainer { MouseFilter = MouseFilterEnum.Ignore, Alignment = BoxContainer.AlignmentMode.Center }; row.AddThemeConstantOverride("separation", 3);
        var order = new[] { "PlusOne", "Zero", "MinusOne", "MinusTwo", "MinusThree", "MinusFour", "MinusFive", "MinusSix", "MinusSeven", "MinusEight", "Skull", "Cultist", "Tablet", "ElderThing", "AutoFail", "ElderSign" };
        var counts = m.ChaosFaces.GroupBy(f => f).ToDictionary(g => g.Key, g => g.Count());
        foreach (var f in order.Concat(counts.Keys.Except(order)))
        {
            if (!counts.TryGetValue(f, out int n)) continue;
            // every token in the bag on its own, no "×2" (Yunke 11-18 #7): the row is the bag as it is
            for (int k = 0; k < n; k++)
            {
                var ic = Ui.Image(Ui.Token(f), new Vector2(28, 28)); ic.MouseFilter = MouseFilterEnum.Pass;
                ic.TooltipText = TokenName(f) + (TokenModifier(m, f) is int mod ? "：" + (mod >= 0 ? "+" : "") + mod : "：自动失败");
                row.AddChild(ic);
            }
        }
        if (showChance)
        {
            double p = PassChance(m, value, difficulty);
            row.AddChild(Ui.Spacer(12));
            var t = Style.Text("通过概率 " + Math.Round(p * 100) + "%", 20, p >= 0.7 ? Style.Good : p >= 0.4 ? Style.Amber : Style.Danger, Style.Kai);
            t.VerticalAlignment = VerticalAlignment.Center; row.AddChild(t);
        }
        return row;
    }
}
