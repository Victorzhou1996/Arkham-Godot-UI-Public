using Gathering;

namespace ArkhamUI;

// Replaces ClientQuestions.AutomaticIndex for the UI. A step is answered automatically only when
// exactly one choice is offered AND that choice carries no player decision. Several forced effects
// that need ordering, any target pick, and any optional trigger always go to the player.
public static class AutoPolicy
{
    // (StartSkillTestButton is not here: the player starts every test, even with nothing to commit — Yunke 11-20)
    static readonly HashSet<string> DecisionFree = new() { "SkipTriggersButton", "Done" };

    public static (int Index, string Why)? Decide(TableModel m, bool enabled)
    {
        var tag = m.QuestionTag;
        // Damage or horror with only one place to go (no ally or other asset can take it) is assigned at once, even with
        // auto-skip off: there is nothing to decide (playtest 2 #8).
        var on = m.Question.Choices.Where(c => c.Enabled).ToArray();
        if (on.Length == 1 && on[0].Tag is "ComponentLabel" or "AuxiliaryComponentLabel" && on[0].Title.StartsWith("给") && on[0].Title.Contains("分配"))
            return (on[0].Index, on[0].Title);
        // The order in which a test's own results resolve (discover the clue, then 洞察力 draws a card — or the other way round):
        // the core offers each order as a choice, but nothing depends on it. The first (the test's own result first) is taken
        // without asking (Yunke 11-18 #16: "在阁楼发现1个线索 / 洞察力" was a step with nothing to decide).
        // Kept behind the auto-skip setting (review 11-18 "检定排序代答边界"): with auto-skip off the player orders them.
        // a window whose every offered fast/reaction ability is switched off for now (TriggerModes): skip it by itself
        if (TriggerModes.AutoSkip(m) is int skip) return (skip, "已设为不触发的能力，跳过");
        if (!enabled) return null;
        if (tag is "ChooseOne" && on.Length >= 1 && on.All(c => c.Tag == "Label" && c.Raw.GetRawText().Contains("SkillTestResultOptions_")))
            return (on[0].Index, on[0].Title);
        if (tag is "ChooseOne" && on.Length == 1 && on[0].Tag == "Label" && on[0].Raw.GetRawText().Contains("\"Successful_\""))
            return (on[0].Index, on[0].Title);
        // The scenario's physical "Setup" sheet (find encounter sets, set cards aside…) is table bookkeeping
        // the core already performed; it is acknowledged silently and stays in the log.
        if (tag == "Read" && IsSetupSheet(m) && m.Question.Choices.Count(c => c.Enabled) == 1) return (m.Question.Choices.First(c => c.Enabled).Index, "剧本设置已完成");
        if (tag is "Read" or "ChooseAmounts" or "ChoosePaymentAmounts" or "ChooseDeck" or "PickScenarioSettings" or "" or "ChooseN" or "ChooseUpToN" or "ChooseSome" or "ChooseSome1") return null;
        var enabledChoices = m.Question.Choices.Where(c => c.Enabled).ToArray();
        if (enabledChoices.Length != 1) return null;
        var only = enabledChoices[0];
        if (tag is "ChooseOneAtATime" or "ChooseOneAtATimeWithAuto") return (only.Index, only.Title.Length > 0 ? only.Title : "唯一待结算效果");
        if (DecisionFree.Contains(only.Tag)) return (only.Index, only.Title);
        if (only.Tag == "SkillTestApplyResultsButton") return null;   // shown, then applied after a short beat (see TableScreen)
        if (only.Tag == "AbilityLabel")
        {
            var type = J.Tag(J.Get(J.Get(only.Raw, "ability"), "type"));
            if (type is "ForcedAbility" or "SilentForcedAbility") return (only.Index, "强制效果 · " + only.Title);
            // any other ability (a reaction, a fast or action ability) is the player's to use, even offered alone (Yunke 11-20:
            // "只要不是强制能力，就不要自动帮我触发") — switched off ones are skipped above (TriggerModes.AutoSkip)
            return null;
        }
        // advancing an act: the player clicks it, even when it is the only thing to do (Yunke 11-20: the sixth cultist advanced
        // the act by itself). An agenda filled with doom still turns by itself (the rules' forced step)
        // …but once the act already lies on its back (its back page has been shown and read: the advance is under way), the
        // core's lone "advance" is no decision: it goes on to the next act by itself (Yunke 11-21: "先出现当前目标的反面…然后再出现
        // 下个目标的正面…中间没有我可以操作的地方就不用我再按一次了")
        if (only.Raw.GetRawText() is var raw && (raw.Contains("\"ActSource\"") || raw.Contains("AdvanceAct")))
            return m.Act is { } act && J.Get(act.Raw, "flipped").ValueKind == System.Text.Json.JsonValueKind.True && raw.Contains("AdvanceAct") ? (only.Index, "推进到下一张目标") : null;
        // The mythos draw: the core asks each investigator to draw from the encounter deck. The player draws it by clicking
        // the deck, so everyone sees who draws and what comes out (Yunke 11-18 #11; the html table's EncounterDeckTarget).
        if (only.Tag == "TargetLabel" && J.Tag(J.Get(only.Raw, "target")) == "EncounterDeckTarget") return null;
        // A card held up to look at (保安室: the exhibit deck's top card; any "look at" effect): the core shows it with a lone
        // "继续" — that is the player's moment to see it, not a step to skip (Yunke 11-22: "保安室的动作能力没有动画效果")
        if (only.Tag == "Label" && J.String(only.Raw, "label") == "$label.continue" && LookedAt(m).Count > 0) return null;
        // A lone offered option in a plain choice has no alternative (an optional step always offers a skip).
        // Never auto-end a turn, and never auto-play/commit a hand card.
        if (tag is "ChooseOne" or "WindowChooseOne" && only.Tag is not ("EndTurnButton" or "Info" or "InvalidLabel")
            && !(only.Tag == "TargetLabel" && J.Tag(J.Get(only.Raw, "target")) == "CardIdTarget"))
            return (only.Index, only.Title);
        return null;
    }

    // the cards the core is holding up for view (focusedCards)
    public static List<System.Text.Json.JsonElement> LookedAt(TableModel m)
    {
        var f = J.Get(m.Game, "focusedCards"); if (f.ValueKind != System.Text.Json.JsonValueKind.Array) f = J.Get(m.Game, "gameFocusedCards");
        return f.ValueKind == System.Text.Json.JsonValueKind.Array ? f.EnumerateArray().ToList() : new();
    }

    // Two or more forced effects at once still need the player's ordering choice.
    public static bool NeedsOrdering(TableModel m) =>
        m.Question.Choices.Count(c => c.Enabled && c.Tag == "AbilityLabel" && J.Tag(J.Get(J.Get(c.Raw, "ability"), "type")) is "ForcedAbility" or "SilentForcedAbility") >= 2;

    static bool IsSetupSheet(TableModel m)
    {
        string t = m.Question.Title.Trim();
        string first = m.Question.Body.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0) ?? "";
        return t is "设置" or "Setup" || first is "设置" or "Setup";
    }
}
