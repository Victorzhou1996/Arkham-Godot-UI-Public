using Gathering;
using System.Text.Json;

namespace ArkhamUI;

// The AI player (version "v2"): answers one question for the seat that is asked (TableModel.Me), from that seat's view.
// It never touches Godot nodes, so the same brain can later answer for a robot seat or in a headless simulator.
// Layers: special questions (tests, assignment, targets) → act goals (AiGoal) → action utility (expected value of each
// offered choice: success chance × gain − cost − risk). See docs/AI-PLAY-PLAN-20261119.md.
public sealed class AiBrain
{
    public const string Version = "v2";
    public static bool Verbose = true;
    public static bool UseNet;            // autoplay flag "net": choose by the trained model (evidence/ai/model/policy.json)
    public static int NetTopK = 3;        // how many of the rule AI's best choices the network may choose among
    public static double Explore;         // autoplay flag "explore": share of turn decisions taken at random among the top 4
    static readonly Random Rng = new();
    // behaviour checks (the main measure now, Yunke 11-19: sensible play at a given point, not the win rate)
    public static readonly Dictionary<string, int> CheckBad = new(), CheckAll = new();
    static void Check(string rule, bool ok) { CheckAll[rule] = CheckAll.GetValueOrDefault(rule) + 1; if (!ok) { CheckBad[rule] = CheckBad.GetValueOrDefault(rule) + 1; AiHost.Log("AI_CHECK_BAD " + rule); } }
    static void Checks(TableModel m, AiContext x, ChoicePresentation c, string kind, List<(ChoicePresentation C, double V, string Why)> all)
    {
        if (m.Phase != "InvestigationPhase" || x.Actions == 0) return;
        bool canHandle = all.Any(a => AiNet.Kind(m, a.C, a.Why) is "fight" or "evade");
        // 1 engaged with a ready enemy: deal with it first (fight / evade / a fast card / resign), not another action
        if (x.EngagedThreat > 0 && canHandle) Check("交战先处理敌人", kind is "fight" or "evade" or "end" or "act" || kind == "play" && AiKnow.Fast(c.CardCode) || kind == "other");
        // 2 the act wants clues, there are clues here and nothing holds me: investigate (or advance / set up), not idle
        if (x.Goal.CluesMatter && x.CluesHere > 0 && x.Engaged == 0 && all.Any(a => AiNet.Kind(m, a.C, a.Why) == "investigate"))
            Check("按目标收集线索", kind is "investigate" or "act" or "play" or "ability");
        // 3 the act can be advanced: advance it
        if (all.Any(a => AiNet.Kind(m, a.C, a.Why) == "act")) Check("能推进就推进", kind == "act");
        // 4 a usable weapon / asset ability while the target enemy is engaged with me: use something, not draw / resource
        if (x.Goal.TargetEnemy is not null && m.EngagedWith(x.Me).Any(e => e.Id == x.Goal.TargetEnemy) && canHandle) Check("目标敌人交战时出手", kind is "fight" or "evade" or "play" or "ability");
    }   // AI_CHOICES lines: every scored choice of a turn decision
    public sealed record Decision(ChoicePresentation? Choice, string Why);   // Choice null: answer nothing here, switch to SwitchTo

    // an evade that failed (still engaged with that enemy, ready, at the next turn decision): trying the same again is worth
    // less each time (狩猎中的夜魇 doubles the bad tokens on evades; a solo Roland spent whole turns failing it)
    readonly Dictionary<string, int> evadeFails = new(); string? lastEvade;
    string? intendedEnemy;      // the enemy a chosen weapon/attack is meant for (the core asks the target next)
    string? intendedPlace;      // where a chosen move is meant to go
    // places that threw me back (墓地 in 午夜假面: a failed willpower test sends you back to 河岸城) — a sim game repeated the
    // move seven times; each bounce makes the place less attractive as a destination
    readonly Dictionary<string, int> bounced = new();
    readonly HashSet<string> mulled = new();   // opening-hand cards already marked for replacement (never toggle back)
    public string? SwitchTo;    // set when this seat has nothing more to add and another seat must answer (helper commits)

    public Decision? Decide(TableModel m)
    {
        SwitchTo = null;
        var ch = m.Question.Choices.Where(c => c.Enabled).ToArray();
        if (ch.Length == 0) return null;
        Decision D(ChoicePresentation c, string why) => new(c, why);
        ChoicePresentation? F(Func<ChoicePresentation, bool> p) => ch.FirstOrDefault(p);
        if (m.QuestionTag == "Read") return D(ch[0], "阅读");
        // the Gathering's last choice (WhatHaveYouDone): both are wins, but resolution 1 (burn the house) gives the lead
        // 1 mental trauma for the whole campaign — resolution 2 keeps the house standing and gives the lead 1 experience
        // (11-20: Roland entered 地底噬魔 with ~2 mental trauma and was driven insane there by one treachery)
        if (F(c => c.Title.Contains("烧了它")) is { } burn)
            return ch.FirstOrDefault(c => c != burn && !c.Title.StartsWith("撤回")) is { } keep ? D(keep, "结局2 保留房子") : D(burn, "结局1");
        if (F(c => c.Tag == "SkillTestApplyResultsButton") is { } apply) return D(apply, "应用结果");
        if (F(c => c.Title.Contains("结束选牌")) is { } mull) return Mulligan(m, ch) ?? D(mull, "保留起手");
        if (m.Me is null) return D(ch[0], "无座位");
        var goal = AiGoal.Read(m);
        var x = AiContext.Build(m, m.Me, goal);
        // skill test commit window (own test: has the start button; helping another: only commit choices)
        if (m.InSkillTest && (F(c => c.Tag == "StartSkillTestButton") is not null || ch.Any(c => c.Title.StartsWith("投入") || c.Title.StartsWith("撤回投入"))))
            return Commit(m, x, ch);
        // damage / horror assignment
        if (ch.Any(c => c.Title.StartsWith("给") && c.Title.Contains("分配"))) return Assign(m, x, ch);
        // a chaos token to treat the revealed one as: the best face
        if (ch.All(c => c.Tag == "TargetLabel" && J.Tag(J.Get(c.Raw, "target")) == "ChaosTokenTarget"))
            return D(ch.OrderByDescending(c => TokenValue(m, J.String(J.Get(J.Get(c.Raw, "target"), "contents"), "chaosTokenFace"))).First(), "最好的标记");
        // which skill to test with: the highest
        if (ch.All(c => c.Tag is "SkillLabel" or "SkillLabelWithLabel"))
            return D(ch.OrderByDescending(c => m.SkillValue(m.Me, (J.String(c.Raw, "skillType") is var s && s.StartsWith("Skill") ? s[5..] : s).ToLowerInvariant() is var k && k.Length > 0 ? k : "intellect")).First(), "最高技能");
        // fight / evade target picks
        if (ch.Any(c => c.Tag is "FightLabel" or "FightLabelWithSkill" or "EvadeLabel" or "EvadeLabelWithSkill") && m.QuestionTag != "PlayerWindowChooseOne")
            return TargetEnemy(m, x, ch);
        if (m.QuestionTag == "PlayerWindowChooseOne") return Turn(m, x, ch);
        // a location to pick (move effects, spawn choices)
        if (ch.All(c => c.Tag == "TargetLabel" && J.Tag(J.Get(c.Raw, "target")) == "LocationTarget"))
        {
            string? dest = intendedPlace ?? Destination(x, null);
            var best = ch.OrderBy(c => dest is null ? 0 : AiContext.Distances(m, J.String(J.Get(c.Raw, "target"), "contents")).GetValueOrDefault(dest, 50)).First();
            intendedPlace = null; return D(best, "地点");
        }
        // an investigator to pick (healing, old book of lore…): the one this helps most — the most hurt, else myself
        if (ch.All(c => c.Tag == "PortraitLabel" || c.Tag == "TargetLabel" && J.Tag(J.Get(c.Raw, "target")) == "InvestigatorTarget"))
            return D(ch.OrderBy(c => m.Investigators.FirstOrDefault(i => c.Title.Contains(m.Name(i))) is { } i ? m.Health(i) - m.Damage(i) + m.Sanity(i) - m.Horror(i) : 99).First(), "调查员");
        // a card to pick: from my hand or my play area it is a card to give up (a treachery's discard) → the one worth least;
        // from elsewhere (a search of the deck: old book of lore, research librarian) it is a card to take → the one worth most
        if (ch.All(c => c.Tag == "TargetLabel" && J.Tag(J.Get(c.Raw, "target")) is "CardIdTarget" or "AssetTarget") && ch.All(c => c.CardCode.Length > 0))
        {
            var mineIds = m.Hand(m.Me).Select(h => h.Id).Concat(m.AssetsOf(m.Me).Select(a => a.Id)).ToHashSet();
            // a weapon already in play is worth keeping: its play value is low only because a weapon is already there (itself)
            var inPlay = m.AssetsOf(m.Me).Select(a => a.Id).ToHashSet();
            double Worth(ChoicePresentation c) => AiKnow.PlayValue(c.CardCode, x) + (AiKnow.Type(c.CardCode) == "skill" ? 1 : 0) + (m.Anchor(c).StartsWith("asset:") ? 3 : 0)
                + (inPlay.Contains(c.TargetId) && AiKnow.IsWeapon(c.CardCode) ? 8 : 0);
            return ch.All(c => mineIds.Contains(c.TargetId)) ? D(ch.OrderBy(Worth).First(), "弃最不值钱的牌") : D(ch.OrderByDescending(Worth).First(), "拿最有用的牌");
        }
        // story / effect options written out as text: avoid the one that hurts whoever is closest to defeat
        if (ch.Length >= 2 && ch.All(c => c.Tag is "Label" or "TooltipLabel"))
        {
            var opt = ch.OrderByDescending(c => LabelValue(m, x, c.Title)).First();
            if (opt.Title.Contains("移动到") && x.Here is not null) bounced[x.Here.Code] = bounced.GetValueOrDefault(x.Here.Code) + 1;
            return D(opt, "选项");
        }
        // windows outside the turn (reactions, fast plays, act buttons)
        if (m.QuestionTag is "WindowChooseOne" || F(c => c.Tag == "SkipTriggersButton") is not null) return Window(m, x, ch);
        return D(ch.FirstOrDefault(c => !c.Title.StartsWith("撤回")) ?? ch[0], "默认");
    }

    // ── the investigator's turn ──────────────────────────────────────────────
    Decision Turn(TableModel m, AiContext x, ChoicePresentation[] ch)
    {
        if (lastEvade is not null)
        {
            if (m.EngagedWith(x.Me).Any(e => e.Id == lastEvade && !AiContext.Exhausted(e))) evadeFails[lastEvade] = evadeFails.GetValueOrDefault(lastEvade) + 1;
            else evadeFails.Remove(lastEvade);
            lastEvade = null;
        }
        var scored = new List<(ChoicePresentation C, double V, string Why)>();
        var feats = new List<float[]>();
        var coachNotes = new List<string>();   // the coach rules that changed each option (logged with the choice)
        var movable = ch.Where(c => m.ActionVerb(c) == "Move").Select(c => c.TargetId.Length > 0 ? c.TargetId : m.Anchor(c).Replace("loc:", "")).ToHashSet();
        string? dest = Destination(x, movable);
        double aoo = x.Engaged == 0 ? 0 : m.EngagedWith(x.Me).Where(e => !AiContext.Exhausted(e)).Sum(e => J.Int(e.Raw, "healthDamage") * x.PainHp + J.Int(e.Raw, "sanityDamage") * x.PainSan);
        if (AiCoach.Lethal(m, x)) aoo += 100;   // the attacks of opportunity would defeat me mid-turn
        bool holdHere = HoldHere(m, x);
        bool flee = PriestHopeless(m, x, ch), aooLethal = AiCoach.Lethal(m, x);
        bool weaponAttack = ch.Any(c => (m.ActionVerb(c) == "Fight" || c.Tag is "FightLabel" or "FightLabelWithSkill") && m.Anchor(c).StartsWith("asset:"));
        bool canResign = ch.Any(c => m.ActionVerb(c) == "Resign");
        foreach (var c in ch)
        {
            string verb = m.ActionVerb(c); string anchor = m.Anchor(c);
            double v; string why;
            if (c.Tag == "EndTurnButton") { v = holdHere ? 0.5 : 0; why = "结束回合"; }
            else if (anchor == "act")
            {
                v = 60; why = "推进场景";
                // 顺藤摸瓜 (01146): advancing spawns every cultist who got away at the Main Path (InvestigatingTheTrail) —
                // never with an investigator standing there (they would all engage at once): step off first
                if (x.Goal.Act == "01146" && m.Investigators.Any(i => m.Location(m.LocationOf(i))?.Code == "01149")) v = -5;
            }
            else if (verb == "Investigate") (v, why) = Investigate(m, x, c);
            else if (verb == "Fight" || c.Tag is "FightLabel" or "FightLabelWithSkill") (v, why) = Fight(m, x, c);
            else if (verb == "Evade" || c.Tag is "EvadeLabel" or "EvadeLabelWithSkill") (v, why) = Evade(m, x, c);
            else if (verb == "Move") (v, why) = Move(m, x, c, dest, holdHere);
            else if (verb == "Draw" || c.Title == "抽牌")
            {
                v = 2.5 + (x.HandSize <= 2 ? 2.5 : 0) - (x.HandSize >= 7 ? 2 : 0);
                // a fight to win and no weapon yet: dig for one (the Ghoul Priest needs every hit)
                if (x.Goal.TargetEnemy is not null && !x.MyAssetCodes.Any(AiKnow.IsWeapon) && !m.Hand(x.Me).Any(h => AiKnow.IsWeapon(h.Code)) && (x.Combat >= 3 || x.Willpower >= 4)) v += 2;
                why = "抽牌";
            }
            else if (verb == "Resource" || c.Title == "获取1资源") { v = 2 + (x.Resources < 2 ? 1.5 : 0) + (x.AffordWant ? 3 : 0); why = "资源"; }
            else if (verb == "Resign") { v = Resign(m, x) ? 80 : -50; why = "撤退"; }
            else if (verb == "Parley")
            {
                // a cultist won over by its own cost (彼得·沃伦 2 clues, 维多利亚 5 resources, 赫尔曼 4 cards) goes to the victory
                // display: interrogated in 午夜假面, one enemy fewer on the Main Path in 地底噬魔
                var pe = anchor.StartsWith("enemy:") ? m.Enemies.FirstOrDefault(e => "enemy:" + e.Id == anchor) : null;
                if (pe is not null && AiKnow.Traits(pe.Code).Contains("异教徒"))
                    // (above 揭露阴谋 at 60: the same 2 clues catch 彼得 for sure instead of drawing one still to catch)
                    v = x.Goal.Act == "01123" ? 65 : x.Here is not null && m.EnemiesHere(x.Here.Id).Any(e => e.Id == pe.Id) ? 18 : 2;
                else v = x.Goal.AllyAt is not null ? 16 * Boosted(m, x, x.Intellect, 4, new[] { "intellect" }) : 0.5;
                why = "谈判";
            }
            else if (verb == "Engage") { v = 0.3; why = "交战"; }
            else if (c.Title.StartsWith("打出") && c.CardCode.Length > 0)
            {
                v = AiKnow.PlayValue(c.CardCode, x); why = "打出 " + m.Name(new Entity("", c.CardCode, default));
                if (AiKnow.Cost(c.CardCode) > 0) v -= 0.4 * AiKnow.Cost(c.CardCode);
                if (AiKnow.Fast(c.CardCode)) v += 0; else v -= aoo;   // playing a non-fast card is an action: attacks of opportunity
            }
            else if (c.Tag == "AbilityLabel") (v, why) = AssetAbility(m, x, c);
            else { v = 0.1; why = c.Title; }
            // any action except fight / evade / resign / parley provokes the engaged enemies
            bool provokes = x.Engaged > 0 && (verb is "Investigate" or "Move" or "Draw" or "Resource" or "Engage" || why is "抽牌" or "资源" || why.StartsWith("打出") && !AiKnow.Fast(c.CardCode));
            if (x.Engaged > 0 && (verb is "Investigate" or "Move" or "Draw" or "Resource" or "Engage" || why is "抽牌" or "资源")) v -= aoo;
            // an action ability (a place's, an asset's, the act's) provokes too — 11-21: engaged with a 狩猎中的夜魇, Roland used
            // 米斯卡塔尼克大学 three times (1.0, above ending the turn) and took three attacks of opportunity
            if (x.Engaged > 0 && c.Tag == "AbilityLabel" && J.Tag(J.Get(J.Get(c.Raw, "ability"), "type")) is "ActionAbility" or "ActionAbilityWithSkill"
                && verb is not ("Fight" or "Evade" or "Parley" or "Resign" or "Investigate" or "Move" or "Draw" or "Resource" or "Engage"))
            { v -= aoo; provokes = true; }
            // the coach's guidance (AiCoach): written from the analysis of many played campaigns
            string kind = why == "结束回合" ? "结束回合" : verb == "Investigate" ? "调查" : verb == "Fight" || c.Tag is "FightLabel" or "FightLabelWithSkill" ? "攻击"
                : verb == "Evade" || c.Tag is "EvadeLabel" or "EvadeLabelWithSkill" ? "躲避" : verb == "Move" ? "移动" : verb == "Resign" ? "撤退" : why == "抽牌" ? "抽牌"
                : why == "资源" ? "资源" : why.StartsWith("打出") ? "打出" : verb == "Parley" ? "谈判" : "其他";
            var foe = anchor.StartsWith("enemy:") ? m.Enemies.FirstOrDefault(e => "enemy:" + e.Id == anchor) : c.TargetId.Length > 0 ? m.Enemies.FirstOrDefault(e => e.Id == c.TargetId) : null;
            var coached = new List<string>();
            // the Ghoul Priest cannot be beaten bare-handed: flee to the Parlor and resign (no resolution, no trauma) rather than
            // idle until he defeats me (11-22: 21 turns in 60 games ended with 1–3 actions unused, engaged with him and no weapon)
            if (flee)
            {
                string locId = c.TargetId.Length > 0 ? c.TargetId : anchor.Replace("loc:", "");
                if (verb == "Move" && m.Location(locId)?.Code == "01115" && !aooLethal && x.Actions >= 2) { v = 70; coached.Add("逃往客厅"); }
                else if (verb == "Resign") { v = 80; coached.Add("逃往客厅"); }
                else if (kind == "躲避" && foe is not null && CoreCatalog.Normalize(foe.Code).StartsWith("01116") && aooLethal && x.Actions >= 3) { v = 40; coached.Add("逃往客厅"); }
            }
            // engaged with the enemy I must defeat, no weapon in play, one in hand: take the attack of opportunity and arm —
            // ending the turn instead only gives him a free attack (11-22: Roland ended whole turns with 大砍刀 / .38 in hand
            // beside the Ghoul Priest, the play valued −13 for its attack of opportunity)
            if (kind == "打出" && AiKnow.IsWeapon(c.CardCode) && !weaponAttack && !aooLethal && x.Actions >= 2
                && x.Goal.TargetEnemy is { } want && m.EngagedWith(x.Me).Any(e => e.Id == want) && v < 20) { v = 20; coached.Add("交战中备武器"); }
            // the ready enemies engaged with me will defeat me in the enemy phase and there is no resigning here: ending the turn
            // is the one sure defeat — an evade (exhausts him) or an attack at least has a chance (11-22: 聚集 act 3, sanity 1–2)
            if (kind == "结束回合" && aooLethal && x.Actions > 0 && !canResign) { v = -40; coached.Add("致命不空过"); }
            v = AiCoach.Adjust(m, x, kind, foe is null ? null : CoreCatalog.Normalize(foe.Code), kind == "打出" ? c.CardCode : null, v, coached);
            coachNotes.Add(coached.Count > 0 ? " 教练:" + string.Join(",", coached) : "");
            scored.Add((c, v, why));
            feats.Add(AiNet.Choice(m, x, c, AiNet.Kind(m, c, why), v, dest, provokes));
        }
        var state = AiNet.State(m, x);
        int pick = 0; double bestV = double.MinValue;
        for (int i = 0; i < scored.Count; i++) if (scored[i].V > bestV) { bestV = scored[i].V; pick = i; }
        string mode = "规则";
        // the learned Q(state, choice) when a model is loaded ("net"), exploration while collecting training data ("explore")
        // advancing the act is a hard rule the network may not overturn (it rarely sees it in the teacher's labels)
        if (UseNet && AiNet.Loaded && AiNet.Kind(m, scored[pick].C, scored[pick].Why) != "act")
        {
            // the network only re-ranks the rule AI's three best choices: it can refine, never pick something absurd
            double bq = double.MinValue;
            foreach (int i in Enumerable.Range(0, scored.Count).OrderByDescending(i => scored[i].V).Take(NetTopK))
            { double q = AiNet.Score(state, feats[i]); if (q > bq) { bq = q; pick = i; } }
            mode = "网络";
        }
        if (Explore > 0 && Rng.NextDouble() < Explore)
        {
            var top = Enumerable.Range(0, scored.Count).OrderByDescending(i => scored[i].V).Take(4).ToArray();
            pick = top[Rng.Next(top.Length)]; mode = "探索";
        }
        AiNet.Record(m, x, state, feats, pick, Enumerable.Range(0, scored.Count).OrderByDescending(i => scored[i].V).ToArray(), scored.Select(q => q.C).ToArray());
        Checks(m, x, scored[pick].C, AiNet.Kind(m, scored[pick].C, scored[pick].Why), scored);
        var best = scored[pick];
        if (Verbose) AiHost.Log("AI_CHOICES " + string.Join(" | ", scored.Select(s => s.C.Tag + "/" + m.ActionVerb(s.C) + "/" + m.Anchor(s.C) + "/" + s.C.TargetId + ":" + s.C.Title + "=" + s.V.ToString("F1"))) + " dest=" + (dest is null ? "-" : m.Location(dest) is { } dl ? m.Name(dl) : dest) + " dist=" + string.Join(",", x.Dist.Select(kv => (m.Location(kv.Key) is { } l ? m.Name(l) : "?") + kv.Value)));
        AiHost.Log("AI_TURN " + m.Name(x.Me) + " " + x.Goal.Note + " doom=" + m.TotalDoom + "/" + m.DoomThreshold + " ag=" + m.Agenda?.Code + " hp=" + x.Hp + " san=" + x.San + " act=" + x.Actions + " res=" + x.Resources
            + " → " + best.C.Title + " (" + best.V.ToString("F1") + (mode == "规则" ? "" : " " + mode) + ")" + coachNotes[pick] + " | " + string.Join("; ", scored.OrderByDescending(s => s.V).Skip(1).Take(3).Select(s => s.C.Title + "=" + s.V.ToString("F1"))));
        // remember what the chosen action is meant for (the core asks for its target next)
        if (best.Why.StartsWith("攻击") || best.Why.StartsWith("躲避")) intendedEnemy = best.Why.Split('|').ElementAtOrDefault(1);
        if (best.Why.StartsWith("移动")) intendedPlace = dest;
        if (best.Why.StartsWith("躲避|")) lastEvade = best.Why.Split('|')[1];
        return new(best.C, best.Why);
    }

    // 午夜假面's once-a-game healing places: walk there when hurt and no enemy holds me (a solo Roland resigned or fell
    // in 120 of 120 chapters with 圣玛丽医院 never used: its ability was only valued when he happened to stand there)
    public static string? HealPlace(AiContext x)
    {
        var m = x.M;
        if (x.Engaged > 0 || x.Goal.Act != "01123") return null;
        int dmg = m.Damage(x.Me), hor = m.Horror(x.Me);
        string? best = null; int bestD = 99;
        foreach (var l in m.Locations)
        {
            string code = CoreCatalog.Normalize(l.Code);
            int heal = code == "01128" ? Math.Min(3, dmg) : code == "01131" ? Math.Min(3, hor) : 0;
            if (heal < 2 || !x.Dist.TryGetValue(l.Id, out int d)) continue;
            // worth the walk: 3 healed for up to two moves, 2 for one (or when the next hits would be my last)
            bool low = code == "01128" ? x.Hp <= 3 : x.San <= 2;
            if (d > (heal >= 3 || low ? 2 : 1)) continue;
            if (J.Items(J.Get(m.Game, "usedAbilities")).Concat(J.Items(J.Get(x.Me.Raw, "usedAbilities"))).Any(u => u.GetRawText().Contains(l.Id))) continue;
            if (d < bestD) { bestD = d; best = l.Id; }
        }
        return best;
    }

    // Should this investigator stay where the clues are spent (the hallway at the round's end)?
    static bool HoldHere(TableModel m, AiContext x)
    {
        var g = x.Goal;
        return g.SpendAtRoundEnd && g.SpendAt is not null && x.Here?.Id == g.SpendAt && x.MyClues > 0 && x.TeamClues >= g.CluesNeeded;
    }

    // Where this investigator should head: the spend place once the team holds enough clues, the act's goal place,
    // the target enemy, else the best place to gather clues (or the nearest unrevealed place).
    string? Destination(AiContext x, HashSet<string>? movable)
    {
        var m = x.M; var g = x.Goal;
        // (only once the team holds them all: waiting there early left the last clue ungathered with everyone in the hallway, cmp1)
        if (g.SpendAtRoundEnd && g.SpendAt is not null && x.TeamClues >= g.CluesNeeded && x.MyClues > 0) return g.SpendAt;
        if (g.GoTo is not null) return g.GoTo;
        if (HealPlace(x) is { } heal) return heal;
        // the team's best talker goes to win the ally over (not while an enemy holds them)
        if (g.AllyAt is not null && x.Engaged == 0 && x.Intellect >= m.Investigators.Where(i => J.Get(i.Raw, "defeated").ValueKind != JsonValueKind.True).Max(i => m.SkillValue(i, "intellect")))
            return g.AllyAt;
        if (g.TargetEnemy is not null && m.Enemies.FirstOrDefault(e => e.Id == g.TargetEnemy) is { } t)
        {
            var at = J.Tag(J.Get(t.Raw, "placement")) == "AtLocation" ? J.String(J.Get(t.Raw, "placement"), "contents")
                : m.Investigators.FirstOrDefault(i => m.EngagedWith(i).Any(e => e.Id == t.Id)) is { } held ? m.LocationOf(held) : null;
            if (at is not null) return at;
        }
        if (!g.CluesMatter && g.SpendAt is not null && x.MyClues > 0) return g.SpendAt;
        // clue places: value per clue, minus the distance, the enemies there and the investigators already working it
        string? best = null; double bestV = double.MinValue;
        foreach (var l in m.Locations)
        {
            if (!x.Dist.TryGetValue(l.Id, out int d)) continue;
            // a neighbouring place no move is offered to is closed for now (灼热屏障: the parlor behind the barrier)
            if (d == 1 && movable is { Count: > 0 } && !movable.Contains(l.Id)) continue;
            int clues = m.Revealed(l) ? m.Clues(l) : 2;
            if (clues == 0) continue;
            int others = m.InvestigatorsAt(l.Id).Count(i => i.Id != x.Me.Id);
            double v = Math.Min(clues, 4) * 3 - d * 2.5 - m.EnemiesAt(l.Id).Count() * 4 - others * 2.5 - (m.Revealed(l) ? m.Shroud(l) - x.Intellect : 0) * 1.5;
            if (l.Code == "01113") v -= x.San <= 3 ? 30 : 3;   // the attic costs a horror on entry
            if (l.Code == "01114") v -= x.Hp <= 3 ? 30 : 3;    // the cellar a damage
            // 墓地: willpower (3) on entry, a failure costs 2 horror or the trip back
            if (l.Code == "01133") v -= (1 - AiHost.PassChance(m, x.Willpower, 3)) * 6 + bounced.GetValueOrDefault(l.Code) * 15;
            if (v > bestV) { bestV = v; best = l.Id; }
        }
        return best;
    }

    (double, string) Investigate(TableModel m, AiContext x, ChoicePresentation c)
    {
        var loc = m.Location(c.TargetId.Length > 0 ? c.TargetId : m.Anchor(c).Replace("loc:", "")) ?? x.Here;
        if (loc is null || m.Clues(loc) == 0) return (x.Goal.CluesMatter ? 0.2 : 0, "调查");
        // 假面猎手: no clue can be found while it is engaged
        if (m.EngagedWith(x.Me).Any(e => CoreCatalog.Normalize(e.Code).StartsWith("01121"))) return (0.1, "调查");
        int skill = x.Intellect + (x.MyAssetCodes.Contains("01030") || x.MyAssetCodes.Contains("01040") ? 1 : 0);
        double p = Boosted(m, x, skill, m.Shroud(loc), new[] { "intellect" });
        double clue = x.Goal.CluesMatter ? 10 : (x.Goal.SpendAt is not null || x.Goal.Act is "01108" or "01109") ? 3 : 4;
        if (x.Goal.CluesMatter && x.TeamClues + 1 >= x.Goal.CluesNeeded) clue += 3;   // the last clue the act needs
        // Roland: his .38 hits harder where clues lie; Roland's reaction finds clues after a kill — no change here
        return (p * clue, "调查");
    }

    // chance with what the hand could still add (a test can be boosted with committed cards)
    static double Boosted(TableModel m, AiContext x, int skill, int difficulty, string[] skills)
    {
        int extra = m.Hand(x.Me).Where(h => AiKnow.Type(h.Code) == "skill").Sum(h => AiKnow.Icons(h.Code, skills));
        double p0 = AiHost.PassChance(m, skill, difficulty), p1 = AiHost.PassChance(m, skill + Math.Min(extra, 3), difficulty);
        return p0 + 0.6 * (p1 - p0);
    }

    (double, string) Fight(TableModel m, AiContext x, ChoicePresentation c)
    {
        // which enemy: the choice's own target (basic fight on an enemy), else the best one here (a weapon asks next)
        string anchor = m.Anchor(c);
        var here = x.Here is null ? Array.Empty<Entity>() : m.EnemiesHere(x.Here.Id).ToArray();
        var targets = anchor.StartsWith("enemy:") ? here.Where(e => "enemy:" + e.Id == anchor).ToArray() : here;
        if (targets.Length == 0) return (0, "攻击");
        var weapon = anchor.StartsWith("asset:") ? AiKnow.Weapon(c.CardCode, m, x.Me) : null;
        // a non-weapon ability that fights (e.g. backstab is an event): plain values
        int bonus = weapon?.Combat ?? 0, dmg = 1 + (weapon?.Damage ?? 0);
        string skillName = weapon?.Skill ?? "combat";
        if (c.Title.StartsWith("打出") && CoreCatalog.Normalize(c.CardCode) == "01051") { skillName = "agility"; dmg = 3; }
        int skill = m.SkillValue(x.Me, skillName) + bonus;
        double best = double.MinValue; Entity? pick = null;
        foreach (var e in targets)
        {
            double p = Boosted(m, x, skill, m.EnemyFight(e), new[] { skillName });
            int left = x.EnemyLeft(e);
            bool engagedMe = m.EngagedWith(x.Me).Any(q => q.Id == e.Id);
            int health = Math.Max(1, m.EnemyHealth(e));
            // 午夜假面: every unique cultist killed is one interrogated (and one fewer in 地底噬魔), not only the one hunted
            // (露丝·特纳 too: evading her sends her to the victory display, but so does defeating her — victory 1, fight 2,
            // evade 5 (rules core); 11-21 the AI never fought her and Roland, agility 2, could hardly evade her)
            bool target = e.Id == x.Goal.TargetEnemy || x.Goal.Act == "01123" && AiKnow.WantedCultist(e.Code);
            double kill = AiContext.Threat(e) * 4 + (target ? 40 + 5 * health : 0) + (engagedMe ? 8 : 0) + (m.EngagedWith(x.Me).Any() && !engagedMe ? -4 : 0);
            // 他们爬出来了！ (01107): each ghoul in the hallway or the parlor puts a doom on the agenda at the round's end
            if (m.Agenda?.Code == "01107" && AiKnow.Traits(e.Code).Contains("食尸鬼") && x.EnemyLocation(e) is { } el && m.Location(el)?.Code is "01112" or "01115") kill += 12;
            // every point of damage is worth its share of the kill (a 20-health priest in a 4-player game is still the goal)
            double v;
            if (dmg >= left) v = p * kill;
            else
            {
                // a share of the kill only when the kill is within reach: at p≈0.3 a 6-health 假面猎手 needs ~20 actions (a sim
                // game spent whole turns swinging at it, missing and being hit) — then dodging, moving off or arming first is better
                double need = left / Math.Max(0.05, p * dmg);
                double reach = need <= x.Actions ? 1 : need <= x.Actions + 3 ? 0.6 : need <= x.Actions + 6 ? 0.3 : 0.1;
                v = p * dmg * kill / health * 0.9 * reach;
            }
            // killing the only ready enemy on me: its attack this round is gone and the actions left are free
            if (dmg >= left && engagedMe && ReadyEngaged(m, x) == 1) v += p * (AttackPain(e, x) + Freedom(x));
            // retaliate (the Ghoul Priest): a miss means an attack back
            // (weighed lightly: ending the turn engaged with it means its attack comes anyway, cmp2 Roland ended turns unused)
            if (e.Code == "01116") v -= (1 - p) * (2 * x.PainHp + 2 * x.PainSan) * 0.25;
            if (Verbose) AiHost.Log($"AI_FIGHT {e.Code} skill={skill}({skillName}) vs {m.EnemyFight(e)} p={p:F2} dmg={dmg} left={left} kill={kill:F1} v={v:F1}");
            if (v > best) { best = v; pick = e; }
        }
        // an enemy not engaged with anyone and not the act's target: leave it be unless it is easy
        if (pick is not null && x.Engaged == 0 && pick.Id != x.Goal.TargetEnemy && !J.Items(J.Get(pick.Raw, "engagedInvestigators")).Any()) best *= 0.5;
        return (best, "攻击|" + pick?.Id);
    }

    // what an engaged enemy's attack costs me (its damage and horror at my current pain), how many ready enemies hold me, and
    // what the turn's other actions are worth once nothing holds me (each one roughly an investigation, or a lesser action)
    static double AttackPain(Entity e, AiContext x) => J.Int(e.Raw, "healthDamage") * x.PainHp + J.Int(e.Raw, "sanityDamage") * x.PainSan;
    static int ReadyEngaged(TableModel m, AiContext x) => m.EngagedWith(x.Me).Count(q => !AiContext.Exhausted(q));
    static double Freedom(AiContext x) => Math.Max(0, x.Actions - 1) * (x.Goal.CluesMatter ? 4 : 2.5);

    (double, string) Evade(TableModel m, AiContext x, ChoicePresentation c)
    {
        string anchor = m.Anchor(c);
        var mine = m.EngagedWith(x.Me).Where(e => anchor.Length == 0 || anchor == "enemy:" + e.Id).ToArray();
        if (mine.Length == 0) return (0, "躲避");
        var e = mine.OrderByDescending(AiContext.Threat).First();
        double p = Boosted(m, x, x.Agility, m.EnemyEvade(e), new[] { "agility" });
        double v = p * (AttackPain(e, x) + 4);
        // the act wants it dead, not dodged (露丝·特纳 is the exception: evading her is what sends her to the victory display)
        if ((e.Id == x.Goal.TargetEnemy || x.Goal.Act == "01123" && AiKnow.WantedCultist(e.Code)) && CoreCatalog.Normalize(e.Code) != "01141") v *= 0.5;
        // dodging the only ready enemy on me frees the rest of the turn (investigate / move without attacks of opportunity)
        if (ReadyEngaged(m, x) == 1) v += p * Freedom(x);
        // 假面猎手 (午夜假面): engaged with it no clue can be found or spent — dodge it when the clues in hand would then buy a
        // cultist this turn (揭露阴谋 costs 2), else it comes back at once: better hit it
        if (CoreCatalog.Normalize(e.Code).StartsWith("01121") && x.Goal.Act == "01123" && x.TeamClues >= 2 * m.Investigators.Count() && x.Actions >= 2) v += 12 * p;
        // 露丝·特纳: once evaded she goes to the victory display (interrogated in 午夜假面)
        if (CoreCatalog.Normalize(e.Code) == "01141") v += p * (x.Goal.Act == "01123" ? 50 : 15);
        v *= Math.Pow(0.4, evadeFails.GetValueOrDefault(e.Id));
        return (v, "躲避|" + e.Id);
    }

    (double, string) Move(TableModel m, AiContext x, ChoicePresentation c, string? dest, bool holdHere)
    {
        var to = m.Location(c.TargetId.Length > 0 ? c.TargetId : m.Anchor(c).Replace("loc:", ""));
        if (to is null) return (dest is null ? 0.2 : 3, "移动");   // a move whose place is asked next
        if (holdHere) return (-20, "移动");
        double v;
        if (dest is null) v = 0.2;
        else
        {
            int now = x.Dist.GetValueOrDefault(dest, 50);
            int after = AiContext.Distances(m, to.Id).GetValueOrDefault(dest, 50);
            v = after < now ? 7 + (after == 0 ? 2 : 0) : -3;
            // clues left here while the act still needs them: finish here first when the test is easy
            if (x.CluesHere > 0 && x.Goal.CluesMatter && dest != x.Goal.SpendAt) v -= 3;
        }
        // forced horror on entering the attic / damage on entering the cellar. When it is the clue place the act still needs
        // (dest, already chosen with the cost in mind) and the point does not defeat, the trip is the only way on: cap the
        // cost, or the AI waits in the hallway one clue short until the agenda runs out (§18c: 9 rounds, then defeated)
        bool onlyWay = dest is not null && to.Id == dest && x.Goal.CluesMatter;
        if (to.Code == "01113") v -= onlyWay && x.San >= 2 ? Math.Min(x.PainSan, 6) : x.PainSan;
        if (to.Code == "01114") v -= onlyWay && x.Hp >= 2 ? Math.Min(x.PainHp, 6) : x.PainHp;
        // the act's own place (the Ritual Site, reached only through the Main Path where the cultists who got away spawn):
        // the enemies on the way cost less, and arriving advances the act
        bool must = dest is not null && dest == x.Goal.GoTo;
        v -= m.EnemiesAt(to.Id).Count(e => !AiContext.Exhausted(e)) * (to.Id == dest ? 1 : must ? 1.5 : 4);
        if (must && to.Id == dest) v += 15;
        return (v, "移动");
    }

    (double, string) AssetAbility(TableModel m, AiContext x, ChoicePresentation c)
    {
        string code = CoreCatalog.Normalize(c.CardCode); string anchor = m.Anchor(c);
        var type = J.Tag(J.Get(J.Get(c.Raw, "ability"), "type"));
        bool mine = anchor == "asset:" + c.TargetId || anchor.StartsWith("asset:") && m.AssetsOf(x.Me).Any(a => "asset:" + a.Id == anchor) || anchor == "inv:" + x.Me.Id;
        double v = code switch
        {
            "01031" => x.HandSize <= 5 ? 3 : 1,                                         // old book of lore
            "01035" => m.Investigators.Any(i => m.LocationOf(i) == x.Here?.Id && m.Damage(i) >= 2) ? 3.5 : -1,   // medical texts
            "01009" => x.San >= 4 ? 4 : -1,                                             // the Necronomicon: move its horror to Daisy
            "01058" => x.San >= 5 && x.AffordWant ? 1.5 : -1,                           // forbidden knowledge
            "01011" => CoreCatalog.Normalize(x.Me.Code) == "01003" && x.Resources >= 5 ? 0.3 : -1,   // hospital debts (Skids' own, spare money only)
            "01063" => 1,                                                               // arcane initiate
            "01017" => -1,                                                              // physical training (only during a test)
            "01134" => x.Goal.Act == "01123" && x.Resources >= 5 ? 30 : -1,             // 北区: 5 resources → 2 clues = one more cultist drawn
            // 午夜假面's once-a-game places (a solo Roland lost ~1 health a round and fell in 5–7 rounds; none was ever used)
            "01128" => m.Damage(x.Me) >= 2 ? (HealPlace(x) == x.Here?.Id ? 12 : 0) + Math.Min(3, m.Damage(x.Me)) * x.PainHp * 0.8 : -1,   // 圣玛丽医院: heal 3 damage
            "01131" => m.Horror(x.Me) >= 2 ? (HealPlace(x) == x.Here?.Id ? 12 : 0) + Math.Min(3, m.Horror(x.Me)) * x.PainSan * 0.8 : -1,  // 商业区: heal 3 horror
            "01130" => 4.5,                                                             // 商业区: gain 3 resources
            "01126" => x.HandSize <= 6 ? 4 : 1,                                         // 南区: draw 3 cards
            "01127" => 3,                                                               // 南区: search the deck for an ally
            "01124" => 3.5,                                                             // 你的房子: draw 1 and gain 1 (once a round)
            "01034" => -1,                                                              // hyperawareness (only during a test)
            _ => type is "ActionAbility" or "ActionAbilityWithSkill" ? 1 : -0.5
        };
        if (!mine && anchor.StartsWith("asset:")) v = Math.Min(v, 0.5);
        return (v, "能力 " + c.Title);
    }

    // act 3 of the Gathering with the Ghoul Priest beside me and no weapon to fight him with (none to attack with, none in hand):
    // five bare-handed hits are out of reach before his attacks defeat me
    static bool PriestHopeless(TableModel m, AiContext x, ChoicePresentation[] ch)
    {
        if (x.Goal.Act != "01110" || x.Here is null) return false;
        // 腿伤 (08131): after a move, resign or evade no more of those this round — the flight cannot be done in one turn
        if (m.ThreatsOf(x.Me).Any(t => CoreCatalog.Normalize(t.Code).StartsWith("08131"))) return false;
        if (!m.Enemies.Any(e => CoreCatalog.Normalize(e.Code).StartsWith("01116") && (m.EngagedWith(x.Me).Any(g => g.Id == e.Id) || m.EnemiesHere(x.Here.Id).Any(g => g.Id == e.Id)))) return false;
        bool weaponAttack = ch.Any(c => (m.ActionVerb(c) == "Fight" || c.Tag is "FightLabel" or "FightLabelWithSkill") && m.Anchor(c).StartsWith("asset:"));
        return !weaponAttack && !m.Hand(x.Me).Any(h => AiKnow.IsWeapon(h.Code));
    }

    // Resign: only when the act's work is done or staying would mean defeat for little gain
    static bool Resign(TableModel m, AiContext x)
    {
        if (x.Goal.Act is "01108" or "01109" or "01110") return x.Hp <= 1 && x.San <= 1;   // the Gathering is won by the priest
        // a location's 撤退 is not an ordinary action: low health or sanity alone (campaign trauma starts some
        // investigators at 1–2) is no reason; only the scenario's work being done, or an engaged enemy whose next
        // attack would defeat me for good, or an agenda about to end the scenario with no hope left
        if (x.Goal.ResignWhenDone && x.Goal.CluesNeeded == 0 && x.Goal.TargetEnemy is null && x.Goal.GoTo is null) return true;
        // the campaign is the goal: a defeat only costs one trauma, so stay and play on unless that trauma would kill
        // the investigator or drive them insane (lost for the rest of the campaign)
        var ready = m.EngagedWith(x.Me).Where(e => !AiContext.Exhausted(e)).ToArray();
        bool lethalHp = ready.Sum(e => J.Int(e.Raw, "healthDamage")) >= x.Hp, lethalSan = ready.Sum(e => J.Int(e.Raw, "sanityDamage")) >= x.San;
        bool lethal = ready.Length > 0 && (lethalHp && x.FatalHp || lethalSan && x.FatalSan);
        // the agenda ends the scenario at the next mythos phase: resign with the last action, not the first (49 of 105
        // second-chapter resigns came at a turn's start with 3 actions left that could still catch a cultist)
        bool doomed = x.DoomMargin <= 1 && m.Agenda?.Code is "01122" or "01121" && x.Actions <= 1;
        return lethal || doomed;
    }

    // ── skill tests ─────────────────────────────────────────────────────────
    Decision Commit(TableModel m, AiContext x, ChoicePresentation[] ch)
    {
        var st = m.SkillTest; string action = J.String(st, "action");
        int value = J.Int(st, "modifiedSkillValue"), diff = J.Int(st, "modifiedDifficulty");
        var skills = J.Items(J.Get(st, "skills")).Select(s => s.GetString() ?? "").ToArray();
        if (skills.Length == 0) skills = new[] { J.String(st, "skillType") };
        bool own = ch.Any(c => c.Tag == "StartSkillTestButton");
        string tester = J.String(st, "investigator");
        double p0 = AiHost.PassChance(m, value, diff);
        // what is at stake: a fight that kills, an investigation for a needed clue, a treachery that hurts
        double stakes = action switch { "Investigate" => x.Goal.CluesMatter ? 10 : 4, "Fight" => 9, "Evade" => 8, "Parley" => 6, _ => 8 };
        // a test no action asked for (a treachery's): failing usually costs horror (willpower) or damage — the closer to
        // insanity / defeat, the more a commit is worth (Yunke 11-20: 第三章两局都是低理智时被诡计的恐惧打死)
        if (action is "" or null)
            stakes = Math.Max(stakes, skills.Contains("willpower") ? 4 + 1.5 * x.PainSan : 4 + 1.0 * x.PainHp);
        if (!own) stakes *= 0.7;
        ChoicePresentation? best = null; double bestGain = 0.6;
        foreach (var c in ch.Where(c => c.Title.StartsWith("投入") && c.CardCode.Length > 0))
        {
            int icons = AiKnow.Icons(c.CardCode, skills);
            double p1 = AiHost.PassChance(m, value + icons, diff);
            double gain = (p1 - p0) * stakes + p1 * AiKnow.CommitBonus(c.CardCode, action) * (icons > 0 ? 1 : 0) - AiKnow.CommitCost(c.CardCode, x);
            if (p1 < 0.2) gain -= 3;            // still hopeless: keep the card
            if (p0 >= 0.95) gain -= 5;          // already safe
            if (gain > bestGain) { bestGain = gain; best = c; }
        }
        if (best is not null)
        {
            AiHost.Log("AI_COMMIT " + m.Name(x.Me) + " " + action + " " + value + "/" + diff + " p=" + p0.ToString("F2") + " → " + best.Title + " gain=" + bestGain.ToString("F1"));
            return new(best, "投入");
        }
        if (own && ch.FirstOrDefault(c => c.Tag == "StartSkillTestButton") is { } start) return new(start, "开始检定 p=" + p0.ToString("F2"));
        // helping another investigator's test: nothing (more) to add — hand the question to the tester's seat
        var testerInv = m.Investigators.FirstOrDefault(i => i.Id == tester);
        SwitchTo = testerInv is null ? null : J.String(testerInv.Raw, "playerId");
        // never toggle a committed card back (r11play2d: 投入/撤回投入 forever): answer nothing, the tester's seat goes on
        var other = ch.FirstOrDefault(c => !c.Title.StartsWith("撤回投入") && !c.Title.StartsWith("投入"));
        return new(SwitchTo is null ? other : null, "交给检定者");
    }

    // ── damage / horror ─────────────────────────────────────────────────────
    static Decision Assign(TableModel m, AiContext x, ChoicePresentation[] ch)
    {
        bool horror = ch.Any(c => c.Title.Contains("分配恐惧"));
        int left = horror ? x.San : x.Hp;
        ChoicePresentation? self = ch.FirstOrDefault(c => m.Anchor(c) == "inv:" + x.Me.Id);
        // an ally / asset that can soak: prefer it while it lives, unless it is precious and I am healthy
        var soak = ch.Where(c => m.Anchor(c).StartsWith("asset:")).Select(c =>
        {
            var a = m.Assets.FirstOrDefault(q => "asset:" + q.Id == m.Anchor(c));
            int aLeft = a is null ? 1 : horror ? Math.Max(0, TableModel.Calc(J.Get(a.Raw, "sanity")) - m.Horror(a)) : Math.Max(0, TableModel.Calc(J.Get(a.Raw, "health")) - m.Damage(a));
            double precious = a is null ? 3 : CoreCatalog.Normalize(a.Code) switch { "01048" => 9, "01054" => 9, "01033" => 7, "01032" => 2, "01018" => 3, "01028" => 3, "01095" => 1, "01094" => 1, _ => 4 };
            return (c, aLeft, precious);
        }).OrderBy(s => s.precious).ToArray();
        foreach (var s in soak)
            if (left <= 4 || s.precious <= 4) return new(s.c, "支援承受");
        if (self is not null) return new(self, "自己承受");
        return new(ch[0], "分配");
    }

    Decision TargetEnemy(TableModel m, AiContext x, ChoicePresentation[] ch)
    {
        var pick = intendedEnemy is not null ? ch.FirstOrDefault(c => m.Anchor(c) == "enemy:" + intendedEnemy) : null;
        intendedEnemy = null;
        pick ??= ch.OrderByDescending(c =>
        {
            var e = m.Enemies.FirstOrDefault(q => "enemy:" + q.Id == m.Anchor(c)); if (e is null) return 0.0;
            return (e.Id == x.Goal.TargetEnemy ? 50 : 0) + (m.EngagedWith(x.Me).Any(q => q.Id == e.Id) ? 10 : 0) + AiContext.Threat(e) * 3 - x.EnemyLeft(e);
        }).First();
        return new(pick, "目标");
    }

    // ── windows outside the action (reactions, fast cards, the act at the round's end) ─────────────────────────
    static Decision Window(TableModel m, AiContext x, ChoicePresentation[] ch)
    {
        if (ch.FirstOrDefault(c => m.Anchor(c) == "act") is { } act) return new(act, "推进场景");
        ChoicePresentation? best = null; double bestV = 2.5;
        foreach (var c in ch)
        {
            double v;
            if (c.Title.StartsWith("打出") && c.CardCode.Length > 0) v = AiKnow.PlayValue(c.CardCode, x) - 0.3 * AiKnow.Cost(c.CardCode);
            else if (c.Tag == "AbilityLabel")
            {
                string anchor = m.Anchor(c); string code = CoreCatalog.Normalize(c.CardCode);
                bool mine = anchor == "inv:" + x.Me.Id || m.AssetsOf(x.Me).Any(a => "asset:" + a.Id == anchor);
                var type = J.Tag(J.Get(J.Get(c.Raw, "ability"), "type"));
                // own reactions that only help (Roland's clue after a kill, Milan's resource, the rabbit's foot draw…)
                v = mine && type is "ReactionAbility" ? (code is "01009" or "01058" or "01011" ? 0 : 6) : 0;
                if (mine && type is "FastAbility" && code is "01017" or "01034" && m.InSkillTest && x.Resources >= 2)
                {
                    var st = m.SkillTest; double p0 = AiHost.PassChance(m, J.Int(st, "modifiedSkillValue"), J.Int(st, "modifiedDifficulty"));
                    double p1 = AiHost.PassChance(m, J.Int(st, "modifiedSkillValue") + 1, J.Int(st, "modifiedDifficulty"));
                    v = (p1 - p0) * 20;
                }
            }
            else continue;
            if (v > bestV) { bestV = v; best = c; }
        }
        if (best is not null) return new(best, "响应 " + best.Title);
        return new(ch.FirstOrDefault(c => c.Tag == "SkipTriggersButton") ?? ch.FirstOrDefault(c => !c.Title.StartsWith("撤回")) ?? ch[0], "跳过");
    }

    // Opening hand: replace cards the AI would not play soon (weaknesses cannot be replaced; the core does not offer them)
    Decision? Mulligan(TableModel m, ChoicePresentation[] ch)
    {
        if (m.Me is null) return null;
        var x = AiContext.Build(m, m.Me, AiGoal.Read(m));
        bool hasWeapon = m.Hand(m.Me).Any(h => AiKnow.IsWeapon(h.Code));
        var swap = ch.Where(c => c.Title.StartsWith("换掉") && c.CardCode.Length > 0 && !mulled.Contains(c.TargetId)).FirstOrDefault(c =>
            AiKnow.Type(c.CardCode) == "event" && AiKnow.PlayValue(c.CardCode, x) < 3 && CoreCatalog.Normalize(c.CardCode) is not ("01023" or "01065" or "01080" or "01079" or "01022"));
        _ = hasWeapon;
        if (swap is not null) mulled.Add(swap.TargetId);
        return swap is null ? null : new(swap, "换掉起手牌");
    }

    // a written option: horror / damage cost by how close the one who takes it is to defeat, discards a little
    static double LabelValue(TableModel m, AiContext x, string t)
    {
        double v = 0;
        int n = System.Text.RegularExpressions.Regex.Match(t, @"(\d+)点") is { Success: true } mt ? int.Parse(mt.Groups[1].Value) : 1;
        if (t.Contains("恐惧")) v -= n * x.PainSan;
        if (t.Contains("伤害")) v -= n * x.PainHp;
        if (t.Contains("弃")) v -= t.Contains("每位") ? 2.5 * x.Players : 2.5;
        if (t.Contains("毁灭")) v -= 6;
        if (t.Contains("撤退")) v -= 20;
        return v;
    }

    static double TokenValue(TableModel m, string face) => face switch
    {
        "ElderSign" => 5, "PlusOne" => 1, "Zero" => 0, "MinusOne" => -1, "MinusTwo" => -2, "MinusThree" => -3, "MinusFour" => -4,
        "AutoFail" => -20, _ => -3
    };
}
