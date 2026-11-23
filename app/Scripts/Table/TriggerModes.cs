using Godot;
using Gathering;
using System.Text.Json;

namespace ArkhamUI;

// Per-card trigger modes for fast and reaction abilities (Yunke 11-18 r7; the 0712 html version's
// AbilityTriggerModeToggle). Cards like 巡警 offer their fast ability in every window; each such ability can be set to
//   总是触发 (AbilityAlwaysAsk, the default) · 只在自己回合 (AbilityOwnerOnly) · 停止触发 (AbilityAutoSkip).
// The table keeps the setting itself (setups/<game>-trigger-modes.json) and answers "skip" by itself in a window whose
// every offered ability is switched off for now (html NFe). The rules core is always left on "总是触发": with
// 只在自己回合 / 停止触发 stored in the core (perCardSettings[card].cardAbilityModes, html's SetTriggerModeAnswer) the
// core skips the window itself, and that skip left the next player's options computed without blanking — 爵士 马利根's
// blank on 教师办公室 was lost and Daisy could not move there while Mark's 巡警 was set to 只在自己回合 (Yunke 11-22;
// AI-play reproduced it on game 4a341275 seq 361 in both directions). Older saves that still hold a mode in the core
// are moved over to the table once (Legacy / Forget).
public static class TriggerModes
{
    public const string Always = "AbilityAlwaysAsk", OwnerOnly = "AbilityOwnerOnly", Off = "AbilityAutoSkip";
    public static string Name(string mode) => mode switch { OwnerOnly => "只在自己回合", Off => "停止触发", _ => "总是触发" };
    // the order a click steps through
    public static string Next(string mode) => mode switch { Always => OwnerOnly, OwnerOnly => Off, _ => Always };

    // the table's own record: "<investigator>|<card>|<index>" → mode, one file per game
    static Guid storeGame; static Dictionary<string, string> store = new();
    static string StoreFile(Guid game) => Path.Combine(App.I.DataRoot, "setups", game.ToString("D") + "-trigger-modes.json");
    static Dictionary<string, string> Store(Guid game)
    {
        if (storeGame == game) return store;
        storeGame = game; store = new();
        try { if (File.Exists(StoreFile(game))) store = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(StoreFile(game))) ?? new(); }
        catch (Exception e) { GD.PushWarning("trigger modes: " + e.Message); }
        return store;
    }
    static string Key(string inv, string card, int index) => inv + "|" + card + "|" + index;
    public static void Set(Guid game, string inv, string card, int index, string mode)
    {
        var s = Store(game);
        s[Key(inv, card, index)] = mode;   // 总是触发 too: an undo to an older save must not bring the core's old mode back
        Directory.CreateDirectory(Path.GetDirectoryName(StoreFile(game))!);
        File.WriteAllText(StoreFile(game), JsonSerializer.Serialize(s));
    }
    // a mode found in the core: taken over unless the table already has its own for that ability
    public static void Adopt(Guid game, string inv, string card, int index, string mode)
    {
        if (!Store(game).ContainsKey(Key(inv, card, index))) Set(game, inv, card, index, mode);
    }
    // modes still held by the rules core (set before 11-22): each is to be copied to the table and reset in the core
    public static List<(string Inv, string Card, int Index, string Mode)> Legacy(TableModel m)
    {
        var list = new List<(string, string, int, string)>();
        foreach (var inv in m.Investigators)
            foreach (var card in J.Props(J.Get(J.Get(inv.Raw, "settings"), "perCardSettings")))
                foreach (var p in J.Props(J.Get(card.Value, "cardAbilityModes")))
                    if (p.Value.ValueKind == JsonValueKind.String && p.Value.GetString() != Always && int.TryParse(p.Name, out int i))
                        list.Add((inv.Id, card.Name, i, p.Value.GetString()!));
        return list;
    }
    static string CoreMode(TableModel m, string inv, string card, int index) =>
        CoreModes(m, inv, card).TryGetValue(index.ToString(), out var v) ? v : Always;

    // ability indexes seen offered for a card this session (a card in play shows its switch once its ability has come up)
    static readonly Dictionary<string, HashSet<int>> seen = new();

    static JsonElement Unwrap(JsonElement q) { for (int i = 0; i < 8 && J.Get(q, "question").ValueKind == JsonValueKind.Object; i++) q = J.Get(q, "question"); return q; }

    static string AbilityType(JsonElement ability)
    {
        var t = J.Get(ability, "type");
        for (int i = 0; i < 6 && J.Tag(t) is "Objective" or "DelayedAbility"; i++) t = J.Get(t, "abilityType");
        return J.Tag(t);
    }

    // A fast or reaction ability choice: (investigator id, card code as the core keys it, ability index)
    public static (string Inv, string Card, int Index)? Ability(ChoicePresentation c)
    {
        if (c.Tag != "AbilityLabel") return null;
        var ab = J.Get(c.Raw, "ability");
        if (AbilityType(ab) is not ("FastAbility" or "ReactionAbility" or "ConstantReaction")) return null;
        string card = J.String(ab, "cardCode"); if (card.Length == 0) return null;
        int index = J.Get(ab, "index").ValueKind == JsonValueKind.Number ? J.Int(ab, "index") : -2; if (index < -1) return null;
        return (J.String(c.Raw, "investigatorId"), card, index);
    }

    static Dictionary<string, string> CoreModes(TableModel m, string invId, string card)
    {
        var inv = m.Investigators.FirstOrDefault(i => i.Id == invId);
        var res = new Dictionary<string, string>();
        if (inv is null) return res;
        foreach (var p in J.Props(J.Get(J.Get(J.Get(J.Get(inv.Raw, "settings"), "perCardSettings"), card), "cardAbilityModes")))
            if (p.Value.ValueKind == JsonValueKind.String) res[p.Name] = p.Value.GetString()!;
        return res;
    }
    // index → mode for one card: the core's (older saves) overlaid by the table's own record
    public static Dictionary<string, string> Modes(TableModel m, string invId, string card)
    {
        var res = CoreModes(m, invId, card);
        string prefix = invId + "|" + card + "|";
        foreach (var (k, v) in Store(m.State.Id)) if (k.StartsWith(prefix)) res[k[prefix.Length..]] = v;
        return res;
    }
    public static string Mode(TableModel m, string invId, string card, int index) =>
        Store(m.State.Id).TryGetValue(Key(invId, card, index), out var v) ? v : CoreMode(m, invId, card, index);
    // the core still holds a mode other than 总是触发 for this ability (to be reset there)
    public static bool HeldByCore(TableModel m, string invId, string card, int index) => CoreMode(m, invId, card, index) != Always;

    // offered right now but switched off for now (html RFe)
    static bool Suppressed(TableModel m, ChoicePresentation c)
    {
        if (Ability(c) is not { } a) return false;
        var mode = Mode(m, a.Inv, a.Card, a.Index);
        return mode == Off || (mode == OwnerOnly && J.String(m.Game, "activeInvestigatorId") != a.Inv);
    }

    // The skip button to press by itself: a window (player window or window response) with exactly one skip button and
    // every other offered choice a fast/reaction ability that is switched off for now (html NFe).
    public static int? AutoSkip(TableModel m)
    {
        var q = Unwrap(m.State.Question);
        // our core names window questions directly (WindowChooseOne / PlayerWindowChooseOne); html saw ChooseOne + flags
        string qt = J.Tag(q);
        bool window = qt is "WindowChooseOne" or "PlayerWindowChooseOne"
            || (qt == "ChooseOne" && (J.Get(q, "isPlayerWindow").ValueKind == JsonValueKind.True || J.Get(q, "isWindowResponse").ValueKind == JsonValueKind.True));
        if (!window) return null;
        var all = m.Question.Choices.Where(c => c.Enabled).ToList();
        var skips = all.Where(c => c.Tag == "SkipTriggersButton").ToList();
        if (skips.Count != 1) return null;
        var rest = all.Where(c => c.Tag is not ("SkipTriggersButton" or "Info" or "InvalidLabel")).ToList();
        return rest.Count > 0 && rest.All(c => Suppressed(m, c)) ? skips[0].Index : null;
    }

    // the abilities of the viewed investigator's cards that can be switched, offered now; remembered for later
    public static List<(string Inv, string Card, int Index)> Offered(TableModel m)
    {
        var me = m.Me?.Id; var list = new List<(string, string, int)>();
        foreach (var c in m.Question.Choices.Where(c => c.Enabled))
            if (Ability(c) is { } a && a.Inv == me && !list.Contains(a))
            {
                list.Add(a);
                if (!seen.TryGetValue(a.Card, out var s)) seen[a.Card] = s = new();
                s.Add(a.Index);
            }
        return list;
    }

    // the switchable abilities known for a card in play: seen offered, or already set in the core
    public static List<int> Known(TableModel m, string invId, string card)
    {
        var set = new HashSet<int>(seen.TryGetValue(card, out var s) ? s : new HashSet<int>());
        foreach (var k in Modes(m, invId, card).Keys) if (int.TryParse(k, out int i) && i >= 0) set.Add(i);
        // and the card's printed fast / reaction abilities, before they are first offered (Yunke 11-20: 放大镜's [fast] ability had
        // no switch — a lightning ability is the one that most needs it)
        foreach (var i in AbilityText.Switchable(card.StartsWith("c") ? card[1..] : card)) set.Add(i);
        return set.OrderBy(i => i).ToList();
    }

    // the core's own answer (used only to put an older save's ability back to 总是触发 in the core)
    public static JsonElement Answer(string invId, string card, int index, string mode) =>
        JsonSerializer.SerializeToElement(new { tag = "SetTriggerModeAnswer", contents = new { investigatorId = invId, cardCode = card, abilityIndex = index, mode } });
}
