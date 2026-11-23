using System.Text.Json;
using System.Text.Json.Nodes;
using Godot;
using Gathering;

namespace ArkhamUI;

// What happened in play that changes the next upgrade (Yunke 11-22, docs/INGAME-DECKBUILDING-CARDS-20261122.md). The rules core
// does not price upgrades (it marks all experience spent), and some of these cards it does not record at all, so the table
// keeps them per game and seat and hands them to the upgrade editor (build), which prices them:
//   鸦翎羽毛笔 09042  [反应] 撤退或游戏结束时：勾选升级卡一格，或下场冒险前所附支援的升级费用 −1  (core: a "[DECKBUILDING]" label only)
//   毛皮货 10109      [反应] 游戏结束或撤退时仍在手牌：下场冒险前，购买的下一张新卡经验 −1        (core: no effect)
//   绿人奖章 98002    [反应] 每6贡品：下场冒险前，购买的下一张卡经验 −1                          (core: investigator deckBuildingAdjustments)
//   精益求精 09121    局内在一张定制卡上勾一格                                                    (core: the card's customizations)
//   似曾相识 60531    回购上场被放逐的卡（每张 −1）                                               (build: needs the exiled cards)
// Handed over in the upgrade request's meta as "arkhamui_upgrade_notes" (our own key: never sent to the core).
public partial class TableScreen
{
    public const string UpgradeNotesKey = "arkhamui_upgrade_notes";

    static string NotesFile(Guid game, int seat) => System.IO.Path.Combine(App.I.DataRoot, "setups", game.ToString("D") + "-upgrade-notes-" + seat + ".json");
    // the deck's metadata after its last upgrade (customization boxes bought then), kept beside the upgraded slots
    static string UpgradeMetaFile(Guid game, int seat) => System.IO.Path.Combine(App.I.DataRoot, "setups", game.ToString("D") + "-upgrade-meta-" + seat + ".json");

    JsonObject LoadNotes(int seat)
    {
        try { var f = NotesFile(gameId, seat); if (System.IO.File.Exists(f)) return JsonNode.Parse(System.IO.File.ReadAllText(f))!.AsObject(); } catch { }
        return new JsonObject();
    }
    void SaveNotes(int seat, JsonObject notes)
    {
        System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(NotesFile(gameId, seat))!);
        System.IO.File.WriteAllText(NotesFile(gameId, seat), notes.ToJsonString());
    }

    // a choice about to be sent: the end-of-game reactions the core does not record
    void NoteDeckbuilding(ChoicePresentation c)
    {
        if (c.Tag != "AbilityLabel" || state is null || model is null) return;
        var ability = J.Get(c.Raw, "ability");
        string code = CoreCatalog.Normalize(J.String(ability, "cardCode")); int index = J.Int(ability, "index");
        if (index != 1 || code is not ("09042" or "10109")) return;
        int seat = Array.FindIndex(state.Seats, s => s.Player == state.Player); if (seat < 0) return;
        var notes = LoadNotes(seat);
        if (code == "10109") notes["furs"] = (notes["furs"]?.GetValue<int>() ?? 0) + 1;
        else
        {
            // the asset it is attached to (its upgrade is what the other option makes cheaper)
            string asset = "";
            var src = J.Get(ability, "source"); string eventId = J.String(src, "contents");
            var placement = J.Get(J.Get(J.Get(model.Game, "events"), eventId), "placement");
            if (J.Tag(placement) == "AttachedToAsset")
            {
                var on = J.Get(placement, "contents"); string aid = on.ValueKind == JsonValueKind.Array && on.GetArrayLength() > 0 ? on[0].GetString() ?? "" : on.ValueKind == JsonValueKind.String ? on.GetString()! : "";
                asset = CoreCatalog.Normalize(ClientDisplay.CardCode(J.Get(J.Get(model.Game, "assets"), aid)));
            }
            var list = notes["ravenQuill"] as JsonArray ?? new JsonArray(); list.Add(asset); notes["ravenQuill"] = list;
        }
        SaveNotes(seat, notes);
        GD.Print("QA_UPGRADE_NOTE seat=" + seat + " " + code + " " + notes.ToJsonString());
    }

    // Everything for this seat's next upgrade, in build's terms. ravenChoices: per raven quill, true = a free box on its
    // upgrade sheet, false = its asset's upgrade −1. Returns the notes object (null when there is nothing).
    JsonObject? UpgradeNotes(int seat, Entity me, Dictionary<string, int> before, Dictionary<string, int> now, List<bool> ravenChoices)
    {
        var notes = LoadNotes(seat); var res = new JsonObject(); var lines = new JsonArray();
        var raven = notes["ravenQuill"] as JsonArray ?? new JsonArray();
        int boxes = 0; var assetDiscounts = new JsonArray();
        for (int i = 0; i < raven.Count; i++)
        {
            string asset = raven[i]?.GetValue<string>() ?? "";
            if (i < ravenChoices.Count && !ravenChoices[i] && asset.Length > 0) { assetDiscounts.Add(new JsonObject { ["code"] = asset, ["amount"] = 1 }); lines.Add(S.Name("09042") + "：" + S.Name(asset) + " 的升级费用 −1"); }
            else { boxes++; lines.Add(S.Name("09042") + "：免费勾选升级卡一格"); }
        }
        if (boxes > 0) res["freeBoxes"] = new JsonObject { ["09042"] = boxes };
        if (assetDiscounts.Count > 0) res["assetUpgradeDiscounts"] = assetDiscounts;
        int furs = notes["furs"]?.GetValue<int>() ?? 0;
        var newCard = new JsonArray(); for (int i = 0; i < furs; i++) { newCard.Add(1); lines.Add(S.Name("10109") + "：下一张新卡经验 −1"); }
        if (newCard.Count > 0) res["newCardDiscounts"] = newCard;
        // 绿人奖章: the core keeps every adjustment it ever made; only those after the ones already used count
        var adj = J.Items(J.Get(me.Raw, "deckBuildingAdjustments")).Where(a => J.Tag(a) == "ReduceXpCostOfNextCardYouPurchaseBy").Select(a => J.Get(a, "contents").ValueKind == JsonValueKind.Number ? J.Get(a, "contents").GetInt32() : 0).ToList();
        int used = notes["greenManUsed"]?.GetValue<int>() ?? 0;
        var anyCard = new JsonArray(); foreach (var x in adj.Skip(used).Where(x => x > 0)) { anyCard.Add(x); lines.Add(S.Name("98002") + "：下一张卡经验 −" + x); }
        if (anyCard.Count > 0) res["cardDiscounts"] = anyCard;
        res["greenManSeen"] = adj.Count;
        // 似曾相识: the cards that left the deck during the scenario (exiled), which it buys back cheaper
        if (now.ContainsKey("60531"))
        {
            var exiled = new JsonObject();
            foreach (var (code, n) in before) { int gone = n - now.GetValueOrDefault(code); if (gone > 0 && code != "01000") exiled[code] = gone; }
            if (exiled.Count > 0) { res["exiled"] = exiled; lines.Add(S.Name("60531") + "：上场放逐 " + string.Join("、", exiled.Select(p => S.Name(p.Key)))); }
        }
        if (lines.Count == 0) return null;
        res["lines"] = lines;
        return res;
    }

    // 精益求精 (and any box the core checked during play): the boxes the core's own cards carry, over the deck's metadata
    string WithCoreCustomizations(string meta, Entity me)
    {
        JsonObject o; try { o = JsonNode.Parse(meta) as JsonObject ?? new JsonObject(); } catch { o = new JsonObject(); }
        var best = new Dictionary<string, Dictionary<int, (int Count, string Choice)>>();
        void Walk(JsonElement e)
        {
            if (e.ValueKind == JsonValueKind.Array) { foreach (var x in e.EnumerateArray()) Walk(x); return; }
            if (e.ValueKind != JsonValueKind.Object) return;
            if (e.TryGetProperty("customizations", out var cus) && cus.ValueKind == JsonValueKind.Array && cus.GetArrayLength() > 0
                && J.String(e, "owner") is var owner && (owner.Length == 0 || owner == me.Id))
            {
                string code = CoreCatalog.Normalize(J.String(e, "cardCode"));
                foreach (var pair in cus.EnumerateArray())
                {
                    if (pair.ValueKind != JsonValueKind.Array || pair.GetArrayLength() < 2) continue;
                    int idx = pair[0].GetInt32(); var v = pair[1]; if (v.ValueKind != JsonValueKind.Array || v.GetArrayLength() < 1) continue;
                    int count = v[0].GetInt32();
                    string choice = v.GetArrayLength() > 1 ? string.Join("^", J.Items(v[1]).Select(ch => J.Get(ch, "contents") is { ValueKind: JsonValueKind.String } s ? s.GetString()! : J.Get(ch, "contents").ValueKind == JsonValueKind.Number ? J.Get(ch, "contents").GetRawText() : "")) : "";
                    var map = best.TryGetValue(code, out var m) ? m : best[code] = new();
                    if (!map.TryGetValue(idx, out var had) || count > had.Count) map[idx] = (count, choice);
                }
            }
            foreach (var p in e.EnumerateObject()) if (p.Value.ValueKind is JsonValueKind.Object or JsonValueKind.Array) Walk(p.Value);
        }
        Walk(model!.Game);
        foreach (var (code, map) in best)
        {
            // build's form: "index|boxes|choice,…"; a box the deck already has stays as it is
            var have = new Dictionary<int, (int Count, string Extra)>();
            if (o["cus_" + code] is JsonValue hv && hv.TryGetValue<string>(out var text))
                foreach (var part in text.Split(',', StringSplitOptions.RemoveEmptyEntries)) { var f = part.Split('|'); if (f.Length > 1 && int.TryParse(f[0], out int i) && int.TryParse(f[1], out int n)) have[i] = (n, f.Length > 2 ? f[2] : ""); }
            bool changed = false;
            foreach (var (idx, v) in map) if (!have.TryGetValue(idx, out var h) || v.Count > h.Count) { have[idx] = (v.Count, h.Extra is { Length: > 0 } r ? r : v.Choice); changed = true; }
            if (changed) { o["cus_" + code] = string.Join(",", have.OrderBy(p => p.Key).Select(p => p.Key + "|" + p.Value.Count + (p.Value.Extra.Length > 0 ? "|" + p.Value.Extra : ""))); GD.Print("QA_CORE_CUSTOMIZATION " + code + " " + o["cus_" + code]); }
        }
        return o.ToJsonString();
    }

    // after the upgrade (or keeping the deck): the notes are used up; the green medallion's adjustments seen so far stay counted
    void UseUpNotes(int seat, JsonObject? used)
    {
        var fresh = new JsonObject();
        if (used?["greenManSeen"] is JsonNode g) fresh["greenManUsed"] = g.GetValue<int>();
        else if (LoadNotes(seat)["greenManUsed"] is JsonNode k) fresh["greenManUsed"] = k.GetValue<int>();
        SaveNotes(seat, fresh);
    }

    // 鸦翎羽毛笔: one question per quill used at the game's end — a free box, or its asset's next upgrade −1 (official texts)
    async Task<List<bool>> AskRavenQuill(int seat)
    {
        var raven = LoadNotes(seat)["ravenQuill"] as JsonArray ?? new JsonArray();
        var answers = new List<bool>();
        foreach (var node in raven)
        {
            string asset = node?.GetValue<string>() ?? "";
            if (asset.Length == 0) { answers.Add(true); continue; }
            var done = new TaskCompletionSource<bool>();
            var col = new VBoxContainer { CustomMinimumSize = new Vector2(640, 0) }; col.AddThemeConstantOverride("separation", 16);
            col.AddChild(Style.Title(S.Name("09042"), 30));
            Control scrim = null!;
            col.AddChild(Ui.Button("勾选" + S.Name("09042") + "升级卡上的一个方框", () => { App.I.CloseModal(scrim); done.TrySetResult(true); }, 24, true));
            col.AddChild(Ui.Button(S.Name(asset) + " 的升级费用 −1", () => { App.I.CloseModal(scrim); done.TrySetResult(false); }, 24));
            scrim = App.I.Modal(Ui.Panel(col, 36), false);
            QaScript.Register("raven", a => { App.I.CloseModal(scrim); done.TrySetResult(a != "asset"); });
            answers.Add(await done.Task);
        }
        return answers;
    }
}
