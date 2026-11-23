using Godot;
using Gathering;
using System.Text.Json;
using Path = System.IO.Path;

namespace ArkhamUI;

// QA autoplay: plays the scenario through the real UI (same Choose path as a mouse click), capturing new situations.
// The decisions come from the AI (app/Scripts/AI/AiBrain.cs); Pick / BestMove below are the old rule list (flag "legacy").
public partial class TableScreen
{
    readonly HashSet<string> captured = new();
    int shot;

    // Undo has no count limit (the player's undo has none either). A runaway guard only, so a bad loop cannot run forever.
    const int RunawayUndos = 1000;
    int undosUsed, commitAll, undosThisTest; string lastUndoTest = "";
    int defeatTries;   // lossundo: investigators defeated mid-chapter and rewound
    bool rush;
    readonly HashSet<string> upgradedAt = new();
    TableState? turnStart; int turnRound = -1, turnRetries, retriesTotal;
    bool probeBarrier;
    bool momDropTested;
    bool autoplaying;   // QA autoplay running: the drawn-card display closes by itself (a player always clicks it away)
    readonly HashSet<string> auditSeen = new();

    readonly AiBrain brain = new();
    readonly AiStats aiStats = new();
    double decideMs, chooseMs; int timedSteps;   // where an autoplay step's time goes (AI_TIMING every 50 steps)

    // autoplay:<steps>,<dir>[,<flags>[,<stopAt>]]   flags joined with "+" (each old single word still works on its own):
    //   lossundo  rewind a lost chapter / a defeated investigator (QA: reach later chapters)
    //   rush      end every turn at once        force / probe / momplay   old QA checks (unchanged)
    //   legacy    the old rule list (Pick / BestMove) instead of the AI   retry   turn and failed-test retries (old default)
    //   fair      no rewinds of any kind (measuring the AI)                fast    no screenshots, no rules audit, short waits
    //   momdrop   the 心胜于物 drag-drop check (on by default in legacy)
    //   rec / explore / net   learning: record decisions / explore / play by the trained model (tools/ai/loop.py)
    async Task Autoplay(string arg)
    {
        autoplaying = true;
        AiHostGodot.Install();
        var parts = arg.Split(','); int limit = int.TryParse(parts[0], out int n) ? n : 300; string dir = parts.Length > 1 ? parts[1] : "evidence/m4/auto";
        var flags = (parts.Length > 2 ? parts[2] : "").Split('+', StringSplitOptions.RemoveEmptyEntries).ToHashSet();
        bool legacy = flags.Contains("legacy"), fair = flags.Contains("fair"), fast = flags.Contains("fast");
        // "force" (QA only): undo every investigation right after its result, to check the draws on repeated attempts.
        bool forceUndo = flags.Contains("force");
        // "probe" (QA only): go to the hallway and end the turn there whatever the clue count, to see what the core offers.
        probeBarrier = flags.Contains("probe");
        // "rush" (QA only): end every turn at once, no retries — lets a scenario run out quickly to test what follows it
        rush = flags.Contains("rush");
        // turn retries and failed-test retries: the old autoplay's default; the AI plays without them unless asked ("retry")
        bool retries = !rush && !fair && (legacy || flags.Contains("retry"));
        // "lossundo" (QA only): a lost scenario is rewound and replayed (a round back first, the scenario's start after many
        // tries), so the audit can reach chapters the autoplay would not win its way to (迷失时空 after 毁灭之地)
        bool lossUndo = flags.Contains("lossundo") && !fair; int lossTries = 0;
        // "momplay" (QA only): the drop check is done; play 心胜于物 on the turn so a later fight / evade offers the skill choice
        if (flags.Contains("momplay") || !(legacy || flags.Contains("momdrop"))) momDropTested = true;
        // learning (tools/ai/loop.py): "rec" writes every turn decision to <dir>/decisions.jsonl, "explore" tries other
        // good-looking choices now and then, "net" chooses by the trained model (evidence/ai/model/policy.json)
        AiBrain.UseNet = flags.Contains("net"); if (AiBrain.UseNet && !AiNet.Load()) GD.Print("AI_NET_FALLBACK 规则");
        AiBrain.Explore = flags.Contains("explore") ? 0.2 : 0;
        AiNet.RecordFile = flags.Contains("rec") ? Path.Combine(App.I.Root, dir, "decisions.jsonl") : null;
        aiStats.Run = dir; aiStats.Ai = legacy ? "legacy" : AiBrain.UseNet && AiNet.Loaded ? "net" : AiBrain.Version; aiStats.Flags = string.Join("+", flags.OrderBy(f => f));
        GD.Print("AUTOPLAY_START ai=" + aiStats.Ai + " flags=" + aiStats.Flags + " seed=" + (System.Environment.GetEnvironmentVariable("ARKHAM_QA_SEED") ?? ""));
        int roundSteps = 0, lastRound = -1;
        // stop when the act becomes this code, or when the scenario changes to this code (QA: run a built situation up to a point)
        string stopAt = parts.Length > 3 ? parts[3] : "";
        for (int step = 0; step < limit; step++)
        {
            await ToSignal(GetTree().CreateTimer(fast ? 0.03 : 0.35), SceneTreeTimer.SignalName.Timeout);
            while (busy || advancing > 0) await ToSignal(GetTree().CreateTimer(fast ? 0.05 : 0.2), SceneTreeTimer.SignalName.Timeout);
            if (model is null) return;
            if (stopAt.Length > 0 && (CoreCatalog.Normalize(model.Act?.Code ?? "") == stopAt || ScenarioCode(model) == stopAt)) { GD.Print("AUTOPLAY_STOP_AT " + stopAt); autoplaying = false; return; }
            // "q:<text>": stop at the first question with a choice whose title contains the text (to look at that screen)
            if (stopAt.StartsWith("q:") && model.Question.Choices.Any(c => c.Enabled && c.Title.Contains(stopAt[2..]))) { GD.Print("AUTOPLAY_STOP_AT " + stopAt); autoplaying = false; Render(); return; }
            aiStats.Step(model);
            if (!fast) { RulesAudit(model, dir); await Snapshot(dir); }
            if (lossUndo && model.IsOver && J.String(J.Get(model.Game, "outcome"), "result") is var lr && lr != "Won" && lossTries < 60)
            {
                lossTries++; aiStats.Undo("loss");
                string back = lossTries == 1 || lossTries % 12 == 0 ? "scenario" : "round";
                GD.Print("AUTOPLAY_LOSS_UNDO try=" + lossTries + " to=" + back + " result=" + lr);
                await Busy("撤回…", async () => { state = await B.UndoBoundary(state!, back); });
                if (back == "round") await Busy("撤回…", async () => { state = await B.UndoBoundary(state!, "round"); });
                AfterUndo(); await Advance();
                continue;
            }
            // "lossundo" in a campaign: a chapter ends and the campaign goes on even when everyone was defeated (no resolution),
            // so the IsOver check above never fires there. An investigator defeated mid-chapter is rewound instead: the round
            // first, the chapter's start after many tries (4-player run r11play2: the Gathering ended with no resolution).
            if (lossUndo && !model.IsOver && defeatTries < 80 && model.Investigators.FirstOrDefault(i => J.Get(i.Raw, "defeated").ValueKind == JsonValueKind.True) is { } down)
            {
                defeatTries++; aiStats.Undo("loss");
                string back = defeatTries % 10 == 0 ? "scenario" : "round";
                GD.Print("AUTOPLAY_DEFEAT_UNDO try=" + defeatTries + " to=" + back + " who=" + model.Name(down));
                AppendRecord("调查员被击败 · " + model.Name(down) + " · 剧本 " + ScenarioCode(model) + " · 撤回到" + (back == "round" ? "本轮开始" : "剧本开始") + "（第 " + defeatTries + " 次）");
                try { await Busy("撤回…", async () => { state = await B.UndoBoundary(state!, back); }); }
                catch (Exception e) { GD.Print("AUTOPLAY_DEFEAT_UNDO_FAIL " + e.Message); }
                AfterUndo(); await Advance();
                continue;
            }
            if (model.IsOver && model.QuestionTag == "")
            {
                string result = J.String(J.Get(model.Game, "outcome"), "result");
                if (result != "Won") AppendRecord("游戏失败 · 步数 " + step + " · 撤回 " + undosUsed + " 次 · 幕 " + (model.Act?.Code ?? "?") + " · 结果 " + result);
                aiStats.End(model, AiStats.Result(model));
                if (!fast) Sample(dir, "campaign-log-end", J.Get(J.Get(J.Get(model.Game, "mode"), "This"), "log"));
                GD.Print("AUTOPLAY_END " + result + " steps=" + step); if (!fast) await Shot(dir, "end");
                autoplaying = false; return;
            }
            // Team payment: spread the required amount over the investigators within their bounds (the core's rule), then submit.
            if (model.QuestionTag is "ChooseAmounts" or "ChoosePaymentAmounts")
            {
                var q = ClientQuestions.Unwrap(model.State.Question);
                var rows = ClientQuestions.AmountRows(q);
                bool payment = model.QuestionTag == "ChoosePaymentAmounts";
                var values = TableScreen.SuggestAmounts(q, rows, payment);
                GD.Print("AUTOPLAY_AMOUNTS " + string.Join(",", rows.Select(r => r.Investigator.Substring(0, Math.Min(6, r.Investigator.Length)) + "=" + values[r.Id])));
                await Do("分配", () => B.Amounts(state!, values));
                await Advance();
                continue;
            }
            // The Circle Undone's tarot reading (PickDestiny): reverse the first half (rounded up), as the dialog's QA answer does
            // (later-cycles sweep 11-22: autoplay never answered it and got stuck)
            if (model.QuestionTag == "PickDestiny")
            {
                var dq = ClientQuestions.Unwrap(model.State.Question);
                int nd = J.Items(J.Get(dq, "drawings")).Count(), half = (nd + 1) / 2;
                GD.Print("AUTOPLAY_DESTINY " + nd + " reversed=" + half);
                App.I.CloseAllModals();
                await Do("选择命运", () => B.Answer(state!, DestinyAnswer(dq, Enumerable.Range(0, nd).Select(i => i < half).ToArray())));
                await Advance();
                continue;
            }
            // The Scarlet Keys' embarking (QQ #45): stop at the nearest place the core lets the cell stop at (the finale when it is the only one)
            if (model.QuestionTag == "PickCampaignSpecific")
            {
                var eq = ClientQuestions.Unwrap(model.State.Question); var ec = J.Get(eq, "contents");
                var routes = ec.ValueKind == JsonValueKind.Array && ec.GetArrayLength() > 1 ? RoutesFromOffer(ec[1]) : new();
                var go = routes.Where(r => r.Has("travel")).OrderBy(r => r.Finale ? -1 : r.Days ?? 99).FirstOrDefault();
                if (go is null) { GD.Print("AUTOPLAY_EMBARK none"); autoplaying = false; return; }
                GD.Print("AUTOPLAY_EMBARK " + go.Id + " days=" + go.Days);
                App.I.CloseAllModals(); CloseWorldMap();
                await Do("旅行", () => B.Answer(state!, JsonSerializer.SerializeToElement(new { tag = "CampaignSpecificAnswer", contents = new object[] { "travel", go.Id } })));
                await Advance();
                continue;
            }
            // Between chapters of a campaign: take the core's next step (scenario, deck upgrade, interlude…).
            if (model.QuestionTag == "ContinueCampaign")
            {
                var nextStep = J.Get(ClientDisplay.Continuation(model.Game), "nextStep");
                GD.Print("AUTOPLAY_CAMPAIGN " + nextStep.GetRawText());
                if (Completed(model).Overlaps(Camp.Chapters.Select(c => c.Code))) aiStats.End(model, "章节结束 " + AiStats.Result(model));
                App.I.CloseAllModals();
                // once per continuation after a scenario: go through the upgrade route, to exercise it (decks are kept)
                string stepKey = nextStep.GetRawText();
                string nextCode = J.Get(nextStep, "contents") is { ValueKind: JsonValueKind.String } nc ? CoreCatalog.Normalize(nc.GetString()!) : "";
                // only where the core lets decks be upgraded (canUpgradeDecks false: The Circle Undone before c05050, The Dream-Eaters,
                // parts of Return to Innsmouth — "This continuation does not allow upgrading"; as TableScreen.Campaign does)
                bool canUpgrade = J.Get(ClientDisplay.Continuation(model.Game), "canUpgradeDecks").ValueKind != JsonValueKind.False;
                if (canUpgrade && J.Tag(nextStep) == "ScenarioStep" && nextCode.Length > 0 && Completed(model).Overlaps(Camp.Chapters.Select(c => c.Code)) && upgradedAt.Add(stepKey))
                {
                    GD.Print("AUTOPLAY_BEGIN_UPGRADE");
                    await Do("开始升级", () => B.BeginUpgrade(state!));
                    await Advance();
                    continue;
                }
                await Do("继续战役", () => B.Continue(state!));
                await Advance();
                continue;
            }
            // Deck upgrade between chapters (autoplay keeps the deck as it is).
            if (model.QuestionTag == "ChooseUpgradeDeck" && model.Me is { } lostMe && LostFate(lostMe) is { } lostFate)
            {
                // a killed or insane investigator: replace them the way a player does (TableScreen.Replace) — the first investigator
                // this campaign has not used (its own box first) that has a deck without experience cards
                if (!fast) await Shot(dir, "replace-" + CoreCatalog.Normalize(lostMe.Code));
                if (replaceScrim is not null && IsInstanceValid(replaceScrim)) App.I.CloseModal(replaceScrim);
                var used = B.UsedInvestigators(state!).Select(CoreCatalog.Normalize).ToHashSet();
                var box = Camp.Id == Campaigns.Zealot.Id ? "01" : "02";
                var pickNew = S.Decks.All.Where(d => J.String(d.Value, "investigator_code") is { Length: > 0 } ic && !used.Contains(CoreCatalog.Normalize(ic)) && DeckXp(d.Value) == 0)
                    .OrderBy(d => J.String(d.Value, "investigator_code").StartsWith(box) ? 0 : 1).ThenBy(d => J.String(d.Value, "investigator_code")).FirstOrDefault();
                GD.Print("AUTOPLAY_REPLACE " + CoreCatalog.Normalize(lostMe.Code) + " " + lostFate + " -> " + (pickNew.Key is null ? "none" : J.String(pickNew.Value, "investigator_code") + " deck=" + pickNew.Key));
                if (pickNew.Key is null) { replaceDeferred.Add(DeferKey(model, lostMe)); await Do("暂不替换", () => B.KeepDeck(state!, lostMe.Id)); }
                else await Replace(lostMe, J.String(pickNew.Value, "investigator_code"), pickNew.Key);
                await Advance();
                continue;
            }
            if (model.QuestionTag == "ChooseUpgradeDeck")
            {
                int seat = Array.FindIndex(state!.Seats, s => s.Player == state.Player);
                var me = model.Me!; var slots = B.CurrentDeck(state!, me.Id);
                bool buy = false;
                // QA (rush): Roland buys Police Badge (level 2) in place of one .45 Automatic, to check the core spends experience
                if (rush && CoreCatalog.Normalize(me.Code) == "01001" && J.Int(me.Raw, "xp") >= 2 && slots.GetValueOrDefault("01016") > 0)
                {
                    slots["01016"]--; if (slots["01016"] == 0) slots.Remove("01016");
                    slots["01027"] = slots.GetValueOrDefault("01027") + 1;
                    GD.Print("AUTOPLAY_UPGRADE_BUY 01027 for 01016"); buy = true;
                }
                // the AI spends its experience (AiUpgrade, the same plan as the simulator); the core prices and checks the list
                else if (!rush)
                {
                    int xpLeft = Math.Max(0, J.Int(me.Raw, "xp") - J.Int(me.Raw, "spentXp"));
                    var (planned, bought) = AiUpgrade.Plan(me.Code, slots, xpLeft, J.Int(me.Raw, "mentalTrauma"), J.Int(me.Raw, "physicalTrauma"));
                    if (bought.Count > 0) { slots = planned; buy = true; }
                    GD.Print("AUTOPLAY_UPGRADE_AI xp=" + xpLeft + " buy=" + string.Join(",", bought.Select(b => b.Code + "/" + b.Removed + ":" + b.Cost)));
                }
                GD.Print("AUTOPLAY_UPGRADE " + model.Name(me) + " xp=" + J.Int(me.Raw, "xp") + " cards=" + slots.Values.Sum());
                if (!fast) await Shot(dir, "upgrade-" + seat);
                if (upgradeOverlay is not null && IsInstanceValid(upgradeOverlay)) upgradeOverlay.QueueFree();
                var deck = JsonSerializer.SerializeToElement(new { name = model.Name(me), investigator_code = CoreCatalog.Normalize(me.Code), slots, sideSlots = new Dictionary<string, int>() });
                if (buy) await Do("升级牌组", () => B.UpgradeDeck(state!, deck));
                else await Do("保持牌组", () => B.KeepDeck(state!, me.Id));
                var after = new TableModel(state!, S).Investigators.FirstOrDefault(i => i.Id == me.Id);
                GD.Print("AUTOPLAY_UPGRADE_DONE " + model.Name(me) + " xp=" + (after is null ? -1 : J.Int(after.Raw, "xp")) + " spent=" + (after is null ? -1 : J.Int(after.Raw, "spentXp")) + " seq=" + state!.Sequence);
                System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(UpgradeFile(gameId, seat))!);   // an imported save has no setups folder yet
                System.IO.File.WriteAllText(UpgradeFile(gameId, seat), JsonSerializer.Serialize(slots));
                await Advance();
                continue;
            }
            // QA (Yunke 11-18 #18): the first time 心胜于物 can be played inside a test, drop it on the test panel the way a drag
            // does (DropFromHand), and record what happened
            if (!momDropTested && model.InSkillTest && model.Question.Choices.FirstOrDefault(x => x.Enabled && CoreCatalog.Normalize(x.CardCode) == "01036") is { } mom
                && model.Anchor(mom) is { Length: > 0 } momKey && commitBox is not null && IsInstanceValid(commitBox))
            {
                momDropTested = true;
                await Shot(dir, "mom-before");
                bool ok = DropFromHand(momKey, commitBox.GetGlobalRect().GetCenter());
                GD.Print("QA_MOM_DROP key=" + momKey + " accepted=" + ok);
                await ToSignal(GetTree().CreateTimer(2.0), SceneTreeTimer.SignalName.Timeout);
                while (busy) await ToSignal(GetTree().CreateTimer(0.2), SceneTreeTimer.SignalName.Timeout);
                await Shot(dir, "mom-after");
                GD.Print("QA_MOM_AFTER question=" + model!.QuestionTag + " handHasMom=" + (model.Me is { } mm && model.Hand(mm).Any(h => CoreCatalog.Normalize(h.Code) == "01036")) + " inTest=" + model.InSkillTest);
                continue;
            }
            int round = aiStats.Rounds;
            if (round != lastRound) { lastRound = round; roundSteps = 0; }
            roundSteps++;
            ChoicePresentation? c = null;
            if (legacy)
            {
                // the move lookahead only picks WHERE to go once the policy has decided to move; it used to pre-empt every turn
                // whenever two exits existed, so in 无人知晓 (many connected places) the investigators walked back and forth ~100
                // times and hardly tested anything (run pd8)
                if (model.QuestionTag == "PlayerWindowChooseOne" && model.Phase == "InvestigationPhase" && model.Me is not null && Pick(model) is { } first && model.ActionVerb(first) == "Move")
                    c = await BestMove(model) ?? first;
                c ??= Pick(model);
            }
            else
            {
                var decideWatch = System.Diagnostics.Stopwatch.StartNew();
                var d = brain.Decide(model);
                decideMs += decideWatch.Elapsed.TotalMilliseconds;
                // a helper has committed what it wants to another investigator's test: the tester's seat answers next
                if (d is not null && d.Choice is null && brain.SwitchTo is { } to && Guid.TryParse(to, out var seatPlayer) && seatPlayer != state!.Player)
                {
                    GD.Print("AI_SWITCH to=" + to + " " + d.Why);
                    await Busy("切换调查员…", async () => { state = await B.Open(gameId, seatPlayer); });
                    model = new TableModel(state!, S); Render();
                    continue;
                }
                c = d?.Choice;
                if (c is not null && d!.Why.Length > 0 && model.QuestionTag != "PlayerWindowChooseOne") GD.Print("AI " + model.QuestionTag + " -> " + c.Title + " | " + d.Why);
            }
            if (rush && model.QuestionTag == "PlayerWindowChooseOne") c = model.Question.Choices.FirstOrDefault(x => x.Enabled && x.Tag == "EndTurnButton") ?? c;
            // runaway guard: hundreds of answers inside one round means a loop (two choices undoing each other): move on
            if (roundSteps > 400 && roundSteps % 50 == 1)
            {
                var exit = model.Question.Choices.FirstOrDefault(x => x.Enabled && x.Tag is "EndTurnButton" or "SkipTriggersButton" or "StartSkillTestButton" or "SkillTestApplyResultsButton");
                GD.Print("AI_LOOP_GUARD round=" + round + " steps=" + roundSteps + " exit=" + (exit?.Title ?? "none"));
                c = exit ?? c;
            }
            c ??= model.Question.Choices.FirstOrDefault(x => x.Enabled && !x.Title.StartsWith("撤回投入"));
            if (c is null) { GD.Print("AUTOPLAY_STUCK " + model.QuestionTag + " " + model.Question.Title); if (!fast) await Shot(dir, "stuck"); aiStats.End(model, "卡住 " + model.QuestionTag); return; }
            // Turn-level retry (undo is allowed): at the start of an investigation phase remember the state; if the turn
            // ends with a team member at 3 or less health or sanity, rewind to that start and play the phase again.
            // A rewind refreshes the randomness, so the chaos draws differ on the retry.
            if (model.Phase == "InvestigationPhase" && model.QuestionTag == "PlayerWindowChooseOne" && (turnRound != round || turnStart is null))
            { turnStart = state; turnRound = round; turnRetries = 0; }
            // Ending the turn in the hallway with the clues for the Barrier is the act advance itself: never rewind it.
            // (Rewinding there sent the team back to the cellar in every 01109 run so far: the hallway was reached with 6 clues, then undone.)
            bool barrierEnd = (model.Act?.Code ?? "") == "01109"
                && model.MyLocation is not null && model.Name(model.MyLocation) == "走廊"
                && model.Investigators.Sum(i => model.Clues(i)) >= 3 * Math.Max(1, model.Investigators.Count());
            if (retries && c.Tag == "EndTurnButton" && turnStart is not null && retriesTotal < RunawayUndos && turnRetries < 8 && !barrierEnd
                && model.Investigators.Any(i => model.Health(i) - model.Damage(i) <= 4 || model.Sanity(i) - model.Horror(i) <= 4))
            {
                turnRetries++; retriesTotal++; aiStats.Undo("turn");
                GD.Print("AUTOPLAY_RETRY round=" + round + " attempt=" + turnRetries + " total=" + retriesTotal);
                await Busy("撤回…", async () => { state = await B.RevertTo(state!, turnStart.Sequence); });
                AfterUndo(); await Advance();
                continue;
            }
            GD.Print("AUTOPLAY " + model.Phase + " " + model.QuestionTag + " -> " + c.Title);
            if (c.Tag == "SkillTestApplyResultsButton" || c.Title == "应用检定结果")
            {
                var st = model.SkillTest;
                var faces = string.Join(",", J.Items(J.Get(st, "resolvedChaosTokens")).Select(t => J.String(t, "chaosTokenFace")));
                string res = J.Tag(J.Get(st, "result"));
                GD.Print("AUTOPLAY_RESULT action=" + J.String(st, "action") + " value=" + J.Int(st, "modifiedSkillValue") + " difficulty=" + J.Int(st, "modifiedDifficulty") + " chaos=" + faces + " result=" + res + " location_clues=" + (model.MyLocation is null ? -1 : model.Clues(model.MyLocation)));
            }
            if ((retries || forceUndo) && (c.Tag == "SkillTestApplyResultsButton" || c.Title == "应用检定结果") && undosUsed < RunawayUndos)
            {
                var st = model.SkillTest; string action = J.String(st, "action");
                // A failed test is undone while the chaos bag can still pass it: a retry is a new draw from the same bag.
                double chance = SuccessChance(model, J.Int(st, "modifiedSkillValue"), J.Int(st, "modifiedDifficulty"));
                // at most 3 retries of the same test (pd4: one test in 百赌百胜 came back failed 736 times in a row)
                string testKey = model.State.Round + "|" + J.String(model.Me?.Raw ?? default, "id") + "|" + action;
                if (testKey != lastUndoTest) { lastUndoTest = testKey; undosThisTest = 0; }
                if ((forceUndo ? action == "Investigate" : J.Tag(J.Get(st, "result")) == "FailedBy" && chance > 0) && undosThisTest++ < 3)
                {
                    undosUsed++; commitAll = 1; aiStats.Undo("test");
                    GD.Print("AUTOPLAY_UNDO " + undosUsed + " action=" + action + " chance=" + chance.ToString("F2"));
                    UndoTo("step");
                    continue;
                }
            }
            // a turn that is ended for real starts a new turn record at the next window (per seat, not per phase)
            if (c.Tag == "EndTurnButton") turnStart = null;
            if (!fast) AuditChoice(model, c);
            var chooseWatch = System.Diagnostics.Stopwatch.StartNew();
            await Choose2Async(c);
            chooseMs += chooseWatch.Elapsed.TotalMilliseconds; timedSteps++;
            if (timedSteps % 50 == 0) GD.Print("AI_TIMING steps=" + timedSteps + " decideMs/step=" + (decideMs / timedSteps).ToString("F0") + " chooseMs/step=" + (chooseMs / timedSteps).ToString("F0") + " backendMs(last)=" + B.LastOperationMs.ToString("F0"));
        }
        GD.Print("AUTOPLAY_LIMIT");
        if (model is not null) aiStats.End(model, "步数用完");
        autoplaying = false;
    }

    async Task Snapshot(string dir)
    {
        var m = model!; var kinds = new List<string>();
        // every distinct story page (campaign flow check: which intro parts and resolutions the core read)
        if (m.QuestionTag == "Read") kinds.Add("read-" + ScenarioCode(m) + "-" + (m.Question.ReadingCards.FirstOrDefault() ?? "") + "-" + (uint)(m.Question.Title + m.Question.Body).GetHashCode() % 100000);
        if (m.Enemies.Any()) kinds.Add("enemy");
        if (m.Me is not null && m.EngagedWith(m.Me).Any()) kinds.Add("engaged");
        if (m.Question.Choices.Any(c => c.Title.StartsWith("给") && c.Title.Contains("分配"))) kinds.Add("assign");
        if (m.Question.Choices.Any(c => c.Tag is "FightLabel" or "EvadeLabel")) kinds.Add("fight-choice");
        if (m.InSkillTest && J.Tag(J.Get(m.SkillTest, "result")) != "Unrun") kinds.Add("test-result");
        if (m.QuestionTag == "ChooseOneAtATime") kinds.Add("ordering");
        if (m.QuestionTag is "ChooseAmounts" or "ChoosePaymentAmounts") kinds.Add("amounts");
        if (Attackers(m).Count > 0) kinds.Add("attack");
        if (m.Question.Choices.Any(c => c.Enabled && m.Anchor(c) == "act")) kinds.Add("act-advance");
        if (m.Question.Choices.Any(c => c.Enabled && m.Anchor(c) == "agenda")) kinds.Add("agenda-action");
        if (m.Treacheries.Any()) kinds.Add("treachery");
        // which skill to test with (心胜于物 lets 智力 replace 战斗/敏捷): the choice buttons carry the skills' icons (11-18 #18)
        if (m.Question.Choices.Any(c => c.Enabled && c.Tag is "SkillLabel" or "SkillLabelWithLabel"))
        {
            kinds.Add("skill-choice");
            GD.Print("QA_SKILL_CHOICE " + string.Join(" | ", m.Question.Choices.Where(c => c.Enabled).Select(c => c.Tag + ":" + J.String(c.Raw, "skillType") + ":" + c.Title)));
        }
        // the investigator's own ability can be used now: the portrait glows (Yunke 11-18 #15)
        if (m.Me is not null && m.Question.Choices.Any(c => c.Enabled && c.Tag == "AbilityLabel" && m.Anchor(c) == "inv:" + m.Me.Id)) kinds.Add("inv-ability-" + CoreCatalog.Normalize(m.Me.Code));
        kinds.Add(("loc-" + (m.MyLocation is null ? "none" : m.Label(m.MyLocation)) + "-" + (scenario?.Background(m) ?? "")).Replace(":", "-"));
        kinds.Add("phase-" + m.Phase);
        kinds.Add("act-" + m.Act?.Code + "-agenda-" + m.Agenda?.Code);
        foreach (var k in kinds) if (captured.Add(k)) await Shot(dir, k);
    }

    async Task Shot(string dir, string kind)
    {
        await ToSignal(GetTree().CreateTimer(0.5), SceneTreeTimer.SignalName.Timeout);
        // a fan-made scenario's code carries colons (:circus-ex-mortis:001), which Windows file names cannot hold
        string safe = string.Concat(kind.Select(ch => Path.GetInvalidFileNameChars().Contains(ch) ? '_' : ch));
        App.I.Capture(Path.Combine(App.I.Root, dir, (++shot).ToString("00") + "-" + safe + ".png"));
    }

    // Movement lookahead: try each move, read the team's state straight after it, and rewind to the basis.
    // Forced effects of entering a room (horror, damage) and the enemies there show up in the score.
    async Task<ChoicePresentation?> BestMove(TableModel m)
    {
        var moves = m.Question.Choices.Where(x => x.Enabled && m.ActionVerb(x) == "Move").ToArray();
        if (moves.Length < 2) return null;
        // The act's goal room comes first: the lookahead below only scores the team's state and would otherwise
        // send the team to the attic in act 01110 (the Ghoul Priest's room) instead of the parlor.
        string act = m.Act?.Code ?? "";
        bool hold = m.Investigators.Sum(i => m.Clues(i)) >= 3 * Math.Max(1, m.Investigators.Count());
        string goal = act == "01109" ? (hold ? "走廊" : "") : act == "01110" ? "客厅" : act is "02278" or "02279" or "02280" or "02281" ? "哨兵峰" : "";
        // Already in the goal room: do not look for a better room. goal-2p26 sat in the hallway with 8 clues, found no
        // hallway move, and walked to the attic and back (forced horror each time) until the retries ran out.
        // Returning null lets Pick end the turn here (the barrier) or fight the priest in the parlor.
        if (goal.Length > 0 && m.MyLocation is not null && m.Name(m.MyLocation) == goal) return null;
        if (goal.Length > 0)
        {
            var g = moves.FirstOrDefault(x => x.Title.Contains(goal));
            if (g is not null) { GD.Print("AUTOPLAY_GOAL " + g.Title); return g; }
        }
        double best = double.MinValue; ChoicePresentation? pick = null;
        foreach (var mv in moves)
        {
            // each trial starts from the state as it is now: a rewind gives back a new sequence number
            var cur = state!;
            try
            {
                var trial = await B.Choice(cur, mv.Index);
                var tm = new TableModel(trial, S);
                double score = TeamScore(tm);
                GD.Print("AUTOPLAY_LOOK " + mv.Title + " score=" + score.ToString("F1"));
                state = await B.RevertTo(trial, cur.Sequence);
                if (score > best) { best = score; pick = mv; }
            }
            catch (Exception e) { GD.Print("AUTOPLAY_LOOK_FAIL " + mv.Title + " " + e.Message); }
        }
        return pick;
    }

    static double TeamScore(TableModel tm)
    {
        int health = tm.Investigators.Sum(i => tm.Health(i) - tm.Damage(i));
        int sanity = tm.Investigators.Sum(i => tm.Sanity(i) - tm.Horror(i));
        int clues = tm.Investigators.Sum(i => tm.Clues(i));
        double score = 8 * health + 8 * sanity + 12 * clues;
        if (tm.Me is not null)
        {
            score -= 6 * tm.EngagedWith(tm.Me).Count();
            var here = tm.MyLocation;
            if (here is not null) { score -= 4 * tm.EnemiesAt(here.Id).Count(); score += 4 * tm.Clues(here); }
        }
        return score;
    }

    // Chance that a test with this value passes, from the chaos bag as it stands (the same estimate the test box shows).
    static double SuccessChance(TableModel m, int value, int difficulty) => PassChance(m, value, difficulty);

    // A plain record of failures, one line each, with the time: appended to evidence/failure-log.txt.
    void AppendRecord(string text)
    {
        var file = Path.Combine(App.I.Root, "evidence", "failure-log.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.AppendAllText(file, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  " + text + System.Environment.NewLine, System.Text.Encoding.UTF8);
    }

    static string Trunc(string t, int n) => t.Length <= n ? t : t[..n];

    ChoicePresentation? Pick(TableModel m)
    {
        var ch = m.Question.Choices.Where(c => c.Enabled).ToArray();
        // QA audit: every question tag and the distinct choice tags the UI is asked to present, once each.
        if (auditSeen.Add(m.QuestionTag + "|" + string.Join(",", m.Question.Choices.Select(c => c.Tag).Distinct().OrderBy(t => t))))
            GD.Print("AUDIT question=" + m.QuestionTag + " choiceTags=" + string.Join(",", m.Question.Choices.Select(c => c.Tag).Distinct().OrderBy(t => t)) + " phase=" + m.Phase);
        if (ch.Length == 0) return null;
        if (m.Phase == "UpkeepPhase" || m.Phase == "MythosPhase" && m.QuestionTag.Contains("Window"))
            GD.Print("AUTOPLAY_WINDOW phase=" + m.Phase + " tag=" + m.QuestionTag + " myClues=" + (m.Me is null ? -1 : m.Clues(m.Me)) + " here=" + (m.MyLocation is null ? "none" : m.Name(m.MyLocation)) + " choices=" + string.Join(" | ", m.Question.Choices.Select(c => (c.Enabled ? "" : "(off)") + c.Title + "@" + m.Anchor(c))));
        ChoicePresentation? F(Func<ChoicePresentation, bool> p) => ch.FirstOrDefault(p);
        if (m.QuestionTag == "Read") return ch[0];
        // The Gathering's act three ends with the lead's choice; resolution 1 ("烧了它") is the win path.
        var burn = F(c => c.Title.Contains("烧了它"));
        if (burn is not null) return burn;
        var sel = F(c => c.Title.Contains("结束选牌")) ?? F(c => c.Tag == "SkillTestApplyResultsButton");
        if (sel is not null) return sel;
        if (ch.Any(c => c.Tag == "StartSkillTestButton"))
        {
            var t = m.SkillTest; bool weak = J.Int(t, "modifiedSkillValue") < J.Int(t, "modifiedDifficulty") + 2;
            GD.Print("AUTOPLAY_TEST value=" + J.Int(t, "modifiedSkillValue") + " difficulty=" + J.Int(t, "modifiedDifficulty") + " action=" + J.String(t, "action") + " weak=" + weak + " commitOptions=" + ch.Count(c => c.Title.StartsWith("投入")));
            var commitOpt = F(c => c.Title.StartsWith("投入"));
            bool priestFight = (m.Act?.Code ?? "") == "01110" && J.String(t, "action") == "Fight";
            if ((weak || commitAll > 0 || priestFight) && commitOpt is not null) return commitOpt;
            commitAll = 0;
            return F(c => c.Tag == "StartSkillTestButton");
        }
        sel = F(c => c.Title.Contains("随机弃"));
        if (sel is not null) return sel;
        if (m.QuestionTag == "PlayerWindowChooseOne" && m.Me is not null)
        {
            var me = m.Me; var here = m.MyLocation;
            string act = m.Act?.Code ?? "";
            GD.Print("AUTOPLAY_TEAM act=" + act + " teamClues=" + m.Investigators.Sum(i => m.Clues(i)) + " seats=" + m.Investigators.Count() + " here=" + (here is null ? "none" : m.Name(here)) + " hereClues=" + (here is null ? -1 : m.Clues(here)) + " health=" + (m.Health(me) - m.Damage(me)) + " horror=" + (m.Sanity(me) - m.Horror(me)));
            sel = F(c => m.Anchor(c) == "act");
            // Engaged: every other action draws an attack of opportunity (run r11play2c: Daisy investigated and walked away from
            // a Flesh-Eater 40 times and was defeated each time). Deal with the enemy first: fight it when this investigator's
            // combat matches its fight value, else evade it; the Ghoul Priest in act three is always fought.
            if (m.EngagedWith(me).FirstOrDefault() is { } foe)
            {
                bool strong = m.SkillValue(me, "combat") >= m.EnemyFight(foe) || act == "01110";
                var fight = F(c => c.Tag is "FightLabel" or "FightLabelWithSkill") ?? F(c => m.ActionVerb(c) == "Fight");
                var evade = F(c => c.Tag is "EvadeLabel" or "EvadeLabelWithSkill") ?? F(c => m.ActionVerb(c) == "Evade");
                sel ??= strong ? fight ?? evade : evade ?? fight;
            }
            // Low health: evade the engaged enemy rather than trade blows; below 3 remaining, do not attack at all.
            int left = m.Health(me) - m.Damage(me);
            if (left <= 4) sel ??= F(c => c.Tag == "EvadeLabel" || c.Tag == "EvadeLabelWithSkill");
            // Before the Ghoul Priest, avoid other enemies: evade them. Fight only the priest in act three, or when evasion is not offered.
            // Clues here first, while healthy: investigating does not need the enemy to be dealt with, and the clues win the game.
            // (when engaged, only while healthy: otherwise evade first; when not engaged, investigating is always allowed)
            if (here is not null && m.Clues(here) > 0 && (left > 4 || !m.EngagedWith(me).Any())) sel ??= F(c => m.ActionVerb(c) == "Investigate");
            if (act != "01110") sel ??= F(c => c.Tag == "EvadeLabel" || c.Tag == "EvadeLabelWithSkill");
            // Not engaged and nothing to find here: go where the clues are (or a place not yet seen) instead of attacking an enemy
            // that is merely present — in 毁灭之地 the team stood at 山脚 with no clues and fought the same enemy for 20 replays.
            if (act != "01110" && here is not null && m.Clues(here) == 0 && !m.EngagedWith(me).Any())
                sel ??= ch.Where(c => m.ActionVerb(c) == "Move").Select(c => (c, d: m.Location(c.TargetId.Length > 0 ? c.TargetId : m.Anchor(c).Replace("loc:", ""))))
                    .Where(x => x.d is not null && (!m.Revealed(x.d) || m.Clues(x.d) > 0) && !(x.d.Code is "01113" or "01114"))
                    .OrderByDescending(x => (m.Revealed(x.d!) ? m.Clues(x.d!) * 10 : 5) - 30 * m.EnemiesAt(x.d!.Id).Count()).Select(x => x.c).FirstOrDefault();
            // nowhere better to go: investigate here even with no clues showing — some places reveal the way on with a successful
            // investigation (毁灭之地's 山脚 puts the next stretch of the path into play), and the act asks for progress, not fights
            if (act != "01110" && here is not null && m.Clues(here) == 0 && ScenarioCode(m).StartsWith("02") && (left > 4 || !m.EngagedWith(me).Any()))
                sel ??= F(c => m.ActionVerb(c) == "Investigate");
            if (left > 3) sel ??= F(c => (c.Tag is "FightLabel" or "FightLabelWithSkill") && c.Title.Contains("祭司")) ?? (act == "01110" ? null : F(c => c.Tag is "FightLabel" or "FightLabelWithSkill") ?? F(c => m.ActionVerb(c) == "Fight"));
            sel ??= F(c => c.Title.StartsWith("打出") && (c.CardCode is "01016" or "01020" or "01030" or "01033" or "01059" or "01018" or "01033"));
            // never a card whose effect is to resign (珍妮's "我要走了！" ended 无人知晓 in its first turn, run pd9)
            sel ??= F(c => c.Title.StartsWith("打出") && c.CardCode is not null && !J.String(S.Card(CoreCatalog.Normalize(c.CardCode)), "text").Contains("撤退")
                && !(!momDropTested && CoreCatalog.Normalize(c.CardCode) == "01036"));   // QA: keep 心胜于物 for the in-test drop check
            // The Barrier (01109) is advanced at the end of the round in the hallway by spending 3 clues per investigator,
            // and those clues sit on the investigator. So: gather clues, go to the hallway once 3 are held, end the turn there.
            // The Barrier costs 3 clues per investigator, paid by the group in the hallway: count the whole team.
            int teamClues = m.Investigators.Sum(i => m.Clues(i)), teamSize = Math.Max(1, m.Investigators.Count());
            bool hold = m.Me is not null && teamClues >= 3 * teamSize;
            bool atHall = here is not null && m.Name(here) == "走廊";
            if (act == "01109" && (hold || probeBarrier) && atHall) sel ??= F(c => c.Tag == "EndTurnButton");
            // Act goals: 01108 gather clues; 01109 the hallway once 3 clues are held; 01110 the Ghoul Priest is in the parlor.
            // 毁灭之地: act 2 advances on entering 哨兵峰, act 3 is paid for by the team at 哨兵峰 (rules source AscendingTheHill*, TheGateOpens)
            string goal = act == "01109" ? (hold || probeBarrier ? "走廊" : "") : act == "01110" ? "客厅" : act is "02278" or "02279" or "02280" or "02281" ? "哨兵峰" : "";
            if (act == "02281" && here is not null && m.Name(here) == "哨兵峰") sel ??= F(c => c.Tag == "EndTurnButton");
            sel ??= ch.Where(c => m.ActionVerb(c) == "Move").OrderByDescending(c =>
            {
                if (goal.Length > 0 && c.Title.Contains(goal)) return 1000;
                var d = m.Location(c.TargetId.Length > 0 ? c.TargetId : m.Anchor(c).Replace("loc:", ""));
                if (d is null) return 0;
                // a location with enemies on it is a bad place to be: prefer quiet rooms, clues or not
                int enemies = m.EnemiesAt(d.Id).Count();
                // the attic costs horror and the cellar costs damage on every entry (forced effects): avoid them unless needed
                int forcedCost = d.Code is "01113" or "01114" ? (m.Revealed(d) && m.Clues(d) > 0 ? 30 : 120) : 0;
                // the attic costs this investigator 1 horror, the cellar 1 damage: off-limits only when that would finish them
                bool tooLow = d.Code == "01113" ? m.Sanity(me) - m.Horror(me) <= 2 : d.Code == "01114" && m.Health(me) - m.Damage(me) <= 2;
                if (tooLow) return -10000;
                // the study is safe and holds clues: keep gathering there before risking the rest of the house
                int studyPull = d.Code == "01111" && m.Clues(d) > 0 ? 150 : 0;
                return (!m.Revealed(d) ? 90 : m.Clues(d) > 0 ? 100 : 10) - 60 * enemies - forcedCost + studyPull;
            }).FirstOrDefault();
            sel ??= F(c => c.Tag == "EndTurnButton");
            return sel ?? ch[0];
        }
        // Upkeep and other windows: the Barrier advances at the end of the round in the hallway, so take the act's button before skipping.
        sel = F(c => m.Anchor(c) == "act") ?? F(c => c.Tag == "SkipTriggersButton");
        return sel ?? ch[0];
    }
}
