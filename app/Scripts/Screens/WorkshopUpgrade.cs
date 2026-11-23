using Godot;
using Gathering;
using System.Text.Json;

namespace ArkhamUI;

// Between scenarios of a campaign: the deck workshop opens over the table in upgrade mode. The investigator and the
// starting deck come from the game; the experience to spend is the investigator's own; the cost of the changes is
// worked out as the player edits (Yunke: "使用组卡UI，自动计算经验").
// UpgradeRequest now lives with the build editor (Build/DeckEditorScreen.Upgrade.cs); this old workshop mode is unused.

public partial class WorkshopScreen
{
    public UpgradeRequest? Upgrade;
    Dictionary<string, int> upgradeFrom = new();
    Button? finishButton;

    // Experience the changes cost, following the campaign rules: each copy added costs its level (at least 1); a copy
    // that replaces a lower-level copy of the same card costs the difference (at least 1). Removing cards is free.
    int UpgradeCost(Dictionary<string, int> now)
    {
        var removed = new List<(string Name, int Level)>();
        foreach (var (code, n) in upgradeFrom)
            for (int i = now.GetValueOrDefault(code); i < n; i++) removed.Add((S.Name(code), Math.Max(0, J.Int(S.Card(code), "xp"))));
        int cost = 0;
        foreach (var (code, n) in now)
            for (int i = upgradeFrom.GetValueOrDefault(code); i < n; i++)
            {
                int level = Math.Max(0, J.Int(S.Card(code), "xp")); string name = S.Name(code);
                int k = removed.FindIndex(r => r.Name == name && r.Level < level);
                if (k >= 0) { cost += Math.Max(1, level - removed[k].Level); removed.RemoveAt(k); }
                else cost += Math.Max(1, level);
            }
        return cost;
    }

    async Task StartUpgrade()
    {
        var up = Upgrade!;
        investigator = up.Investigator; invPick.Selected = Array.IndexOf(Investigators, investigator); invPick.Disabled = true;
        loadPick.Visible = false; budgetSpin.Editable = false;
        status.Text = "读取构筑规则…";
        try { schema = await S.Backend.BuildSchema(investigator); } catch (Exception e) { status.Text = "读取失败：" + e.Message; return; }
        upgradeFrom = new Dictionary<string, int>(up.Slots);
        slots = new Dictionary<string, int>(up.Slots);
        budget = up.Xp; budgetSpin.SetValueNoSignal(budget);
        deckName = up.InvestigatorName + " · 升级"; nameEdit.Text = deckName; nameEdit.Editable = false;
        maxLevel = 5; levelPick.Selected = 1;   // every level is open when there is experience to spend
        dirty = false; FillLibrary(); Refresh();
        status.Text = up.InvestigatorName + " 有 " + up.Xp + " 点经验可用。加入卡牌会按等级自动计算花费；移出卡牌不返还经验。";
    }

    async Task FinishUpgrade(bool keep)
    {
        var up = Upgrade!;
        if (keep) { await up.Done(null, 0); return; }
        int cost = UpgradeCost(slots);
        if (cost > up.Xp) { status.Text = "经验不足：需要 " + cost + "，可用 " + up.Xp; return; }
        if (Ordinary() != DeckSize()) { status.Text = "牌组需要正好 " + DeckSize() + " 张（现在 " + Ordinary() + " 张）"; return; }
        await up.Done(new Dictionary<string, int>(slots), cost);
    }
}
