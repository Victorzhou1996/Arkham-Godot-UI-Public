using Godot;
using Gathering;
using System.Text.Json;

namespace ArkhamUI;

// Between scenarios of a campaign (Yunke 11-18: the upgrade runs on build too). The investigator's current deck goes
// into build's store as the previous version and build's upgradeDeck makes the next version with the experience the
// investigator has; the editor then edits that version like any deck, and build's getChangeStats prices the changes
// (view().upgrade: xp, xpSpent, xpAvailable). 完成升级 hands the slots and build's cost to the campaign flow
// (UpgradeRequest.Done), 保持牌组不变 hands back nothing. Neither version is kept in the deck library.
// Meta: the metadata of the deck the seat started with (build's selections, e.g. faction_selected), so the access list
// and validation follow the same choices.
// The table may add "arkhamui_upgrade_notes" to Meta (TableScreen.UpgradeNotes: what happened in play that lowers the price —
// free boxes, discounts, the cards exiled last scenario); the editor prices them on top of build and keeps them out of the
// deck. ResultMeta: build's metadata of the upgraded deck (with its cus_ boxes), set right before Done.
// SideSlots: the side deck the seat carries in this campaign (the core holds none: TableScreen keeps it per seat); the editor
// shows and edits it like any deck's, and ResultSide is the side deck after 完成升级 (null when nothing was applied).
public sealed record UpgradeRequest(string Investigator, string InvestigatorName, Dictionary<string, int> Slots, int Xp, Func<Dictionary<string, int>?, int, Task> Done, string? Meta = null, Dictionary<string, int>? SideSlots = null)
{
    public string? ResultMeta { get; set; }
    public Dictionary<string, int>? ResultSide { get; set; }
}

public partial class DeckEditorScreen
{
    bool finishing;
    bool qaForceSend;   // QA only: send an unchanged list as an upgrade (to compare with the core's charge)

    public const string UpgradeNotesKey = "arkhamui_upgrade_notes";
    string startMeta = "{}";                     // the request's meta without the notes
    List<string> noteLines = new();              // the table's lines of what applies (shown under the pill)

    async Task<string> BeginUpgrade()
    {
        var up = upgrade!;
        await BuildDecks.Sync();
        // the notes come out of the meta (never saved with the deck); the exiled cards go back into the previous version and
        // build takes them out again as exiled (似曾相识's buy-back, the free level-0 replacements)
        string notes = ""; var exile = new List<string>(); var slots = new Dictionary<string, int>(up.Slots);
        try
        {
            var mo = System.Text.Json.Nodes.JsonNode.Parse(up.Meta is { Length: > 0 } m ? m : "{}") as System.Text.Json.Nodes.JsonObject ?? new();
            if (mo[UpgradeNotesKey] is System.Text.Json.Nodes.JsonValue nv && nv.TryGetValue<string>(out var nt) && nt.Length > 0)
            {
                notes = nt;
                using var nd = JsonDocument.Parse(nt);
                noteLines = J.Items(J.Get(nd.RootElement, "lines")).Where(l => l.ValueKind == JsonValueKind.String).Select(l => l.GetString()!).ToList();
                foreach (var p in J.Props(J.Get(nd.RootElement, "exiled")))
                {
                    int n = p.Value.ValueKind == JsonValueKind.Number ? p.Value.GetInt32() : 0; if (n <= 0) continue;
                    slots[p.Name] = slots.GetValueOrDefault(p.Name) + n;
                    for (int i = 0; i < n; i++) exile.Add(p.Name);
                }
            }
            mo.Remove(UpgradeNotesKey);
            startMeta = mo.ToJsonString();
        }
        catch (Exception e) { GD.PushWarning("upgrade notes: " + e.Message); startMeta = up.Meta ?? "{}"; }
        if (App.I.QaRunning) GD.Print("QA_EDITOR_NOTES " + (notes.Length > 0 ? notes : "none") + " exile=" + string.Join(",", exile));
        // the side deck goes in too (build's upgradeDeck keeps it), so it is there to see and to buy from (Yunke 11-23: "升级的时候
        // 侧卡组会消失" — it was sent in empty)
        var side = up.SideSlots is { } ss ? ss.Where(p => p.Value > 0).ToDictionary(p => p.Key, p => p.Value) : new Dictionary<string, int>();
        if (App.I.QaRunning) GD.Print("QA_UPGRADE_SIDE_IN " + JsonSerializer.Serialize(side));
        var prev = JsonSerializer.Serialize(new { investigator_code = up.Investigator, name = up.InvestigatorName + " · 战役", slots, sideSlots = side, meta = startMeta });
        using var doc = JsonDocument.Parse(await BuildEngine.I.Call("decks.upgradeBegin", prev, up.Xp, notes, string.Join(",", exile)));
        return J.String(doc.RootElement, "next");
    }

    // what the notes saved in this upgrade: "鸦翎羽毛笔 → 放大镜 −1"
    string DiscountLine(JsonElement d)
    {
        string src = J.String(d, "source"); int amount = J.Int(d, "amount");
        string who = src.StartsWith("freeBox:") || src.StartsWith("asset:") ? CardName("09042") : src == "newCard" ? CardName("10109") : src == "card" ? CardName("98002") : src;
        return who + " → " + CardName(J.String(d, "code")) + " −" + amount;
    }

    JsonElement UpgradeStats => J.Get(view, "upgrade");

    void RefreshUpgrade()
    {
        var u = UpgradeStats; int spent = J.Int(u, "xpSpent"), left = J.Int(u, "xpAvailable"), xp = J.Int(u, "xp");
        var st = J.Get(view, "stats");
        int size = J.Int(st, "deckSize"), need = J.Int(J.Get(J.Get(view, "investigatorCard"), "deck_requirements"), "size", 30);
        pill.Text = "经验 " + spent + "/" + xp + " · 卡组需求 " + size + "/" + need;
        pill.AddThemeColorOverride("font_color", left < 0 || size != need ? Style.Danger : Style.Paper);
        editState.Text = "关卡之间 · 升级牌组 · 剩余经验 " + left;
        // the table's notes (what happened in play) and what they took off this time
        if (notesBox is not null)
        {
            Ui.Clear(notesBox);
            var used = J.Items(J.Get(u, "discounts")).ToList();
            foreach (var line in noteLines) notesBox.AddChild(NoteLabel(line, new Color(Style.Amber, 0.9f)));
            if (used.Count > 0)
            {
                notesBox.AddChild(NoteLabel("本次已减 " + J.Int(u, "discount") + " 经验（原价 " + J.Int(u, "buildXpSpent") + "）", Style.Paper));
                foreach (var d in used) notesBox.AddChild(NoteLabel("　· " + DiscountLine(d), Style.Paper));
            }
            notesBox.Visible = notesBox.GetChildCount() > 0;
            if (App.I.QaRunning) GD.Print("QA_UPGRADE_PRICE build=" + J.Int(u, "buildXpSpent") + " spent=" + spent + " left=" + left + " used=" + string.Join("|", used.Select(DiscountLine)));
        }
        if (left < 0) { var l = Style.Text("经验不足：需要 " + spent + "，可用 " + xp, 16, Style.Danger, Style.Kai); l.AutowrapMode = TextServer.AutowrapMode.WordSmart; l.CustomMinimumSize = new Vector2(360, 0); problems.AddChild(l); }
    }

    static Label NoteLabel(string text, Color ink)
    {
        var l = Style.Text(text, 15, ink, Style.Kai); l.AutowrapMode = TextServer.AutowrapMode.WordSmart; l.CustomMinimumSize = new Vector2(300, 0); l.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        return l;
    }

    async Task FinishUpgrade(bool apply)
    {
        var up = upgrade; if (up is null || finishing || deckId.Length == 0) return;
        if (apply)
        {
            var u = UpgradeStats;
            if (J.Int(u, "xpAvailable") < 0) { App.I.Toast("经验不足：需要 " + J.Int(u, "xpSpent") + "，可用 " + J.Int(u, "xp"), true); return; }
            // a 随机基础弱点 placeholder in the upgraded deck is drawn here (the core takes no placeholder between scenarios)
            while (InDeck(RandomWeakness) > 0) { int before = InDeck(RandomWeakness); await DrawWeakness(); if (InDeck(RandomWeakness) >= before) { App.I.Toast("随机基础弱点没有抽成，请稍后再试", true); return; } }
            // the upgraded deck is checked as a game start checks one (local-deck-legality, Yunke 11-23: "局间升级的时候也要做
            // 合法性检查"): build's verdict, then the rules core's — what this upgrade makes wrong cannot be confirmed
            var problems = J.Get(J.Get(view, "validation"), "valid").ValueKind == JsonValueKind.True ? new List<string>()
                : J.Items(J.Get(J.Get(view, "validation"), "errors")).Select(e => BuildDecks.ErrorText(e, CardName, DeckInvestigator)).Where(t => t.Length > 0).Distinct().ToList();
            if (J.Get(J.Get(view, "validation"), "valid").ValueKind != JsonValueKind.True && problems.Count == 0) problems.Add("卡组无效。");
            if (problems.Count == 0) problems.AddRange(await UpgradeCoreProblems(up));
            if (App.I.QaRunning) GD.Print("QA_UPGRADE_CHECK " + (problems.Count == 0 ? "valid" : string.Join(" | ", problems)));
            if (problems.Count > 0) { UpgradeRefused(problems); return; }
        }
        finishing = true;
        try
        {
            int cost = J.Int(UpgradeStats, "xpSpent");
            using var doc = JsonDocument.Parse(await BuildEngine.I.Call("decks.upgradeEnd", deckId, apply));
            // build's metadata of the deck handed back (boxes bought now; never the notes)
            up.ResultMeta = ResultMeta(doc.RootElement);
            if (App.I.QaRunning) GD.Print("QA_RESULT_META " + up.ResultMeta);
            if (!apply) { GD.Print("UPGRADE_DONE kept"); await up.Done(null, 0); return; }
            var slots = J.Props(J.Get(doc.RootElement, "slots")).Where(p => p.Value.ValueKind == JsonValueKind.Number && p.Value.GetInt32() > 0).ToDictionary(p => p.Name, p => p.Value.GetInt32());
            // the side deck as it stands now: the table keeps it with the save (a side-deck-only change sends the core nothing)
            up.ResultSide = J.Props(J.Get(doc.RootElement, "sideSlots")).Where(p => p.Value.ValueKind == JsonValueKind.Number && p.Value.GetInt32() > 0).ToDictionary(p => p.Name, p => p.Value.GetInt32());
            GD.Print("UPGRADE_SIDE " + up.ResultSide.Values.Sum() + " " + JsonSerializer.Serialize(up.ResultSide));
            // unchanged only when nothing was bought and the meta is the same: boxes on a customizable card change the meta, not the
            // slots, and a checked option (精益求精, bug #14) changes only the meta — without this the change was dropped on the way back
            bool metaSame = MetaWithoutNotes(up.ResultMeta) == MetaWithoutNotes(startMeta);
            bool same = cost == 0 && metaSame && slots.Count == up.Slots.Count && slots.All(p => up.Slots.GetValueOrDefault(p.Key) == p.Value);
            GD.Print("UPGRADE_DONE " + (same ? "unchanged" : "cost=" + cost + " cards=" + slots.Values.Sum() + " " + JsonSerializer.Serialize(slots)));
            if (same && !qaForceSend) await up.Done(null, 0); else await up.Done(slots, cost);
        }
        catch (Exception e) { finishing = false; App.I.Toast("升级失败：" + e.Message, true); }
    }

    string DeckInvestigator => J.String(J.Get(view, "sides"), "original") is { Length: > 0 } o ? o : J.String(view, "investigator");

    // The rules core's objections the upgrade brings: the deck before and after are both checked as a campaign's deck (no
    // standalone experience rules, #36), and only what the upgraded deck adds is told. A campaign deck holds cards the core put
    // there itself (story assets, earned weaknesses) that its construction check calls unsupported; they are in both, so they
    // never stop an upgrade.
    async Task<List<string>> UpgradeCoreProblems(UpgradeRequest up)
    {
        var told = new List<string>();
        var S = App.I.S; if (S?.Backend is not { } backend) return told;
        string inv = DeckInvestigator;
        var metaNow = J.Get(view, "metaParsed") is { ValueKind: JsonValueKind.Object } mp ? mp.GetRawText() : startMeta;
        var after = J.Items(J.Get(view, "slots")).Where(c => J.Int(c, "quantity") > 0).ToDictionary(c => J.String(c, "code"), c => J.Int(c, "quantity"));
        JsonElement Deck(Dictionary<string, int> slots, string meta) => JsonSerializer.SerializeToElement(new Dictionary<string, object?>
        {
            ["investigator_code"] = inv, ["slots"] = slots, ["meta"] = meta,
            ["taboo_id"] = J.Get(view, "taboo_id").ValueKind == JsonValueKind.Number ? J.Int(view, "taboo_id") : null,
        });
        async Task<Dictionary<string, string>> Check(JsonElement deck)
        {
            var found = new Dictionary<string, string>();
            try
            {
                var prepared = await backend.PrepareDeck(deck, false);
                var schema = await backend.BuildSchema(J.String(prepared, "investigator_code"));
                var r = await backend.ValidateDeck(J.String(prepared, "investigator_code"), J.String(schema, "schemaId"), prepared, false);
                foreach (var e in J.Items(J.Get(r, "errors"))) found[J.String(e, "code") + "|" + J.Get(e, "detail").GetRawText()] = J.String(e, "message", J.String(e, "code"));
                foreach (var u in J.Items(J.Get(r, "unsupportedReasons"))) found["u|" + u.GetRawText()] = J.String(r, "unsupportedText", "规则核心暂不支持此牌组的部分设置");
            }
            catch (Exception e) { found["x|" + e.Message] = e.Message; }
            return found;
        }
        var was = await Check(Deck(new Dictionary<string, int>(up.Slots), startMeta));
        var now = await Check(Deck(after, metaNow));
        foreach (var (k, m) in now) if (!was.ContainsKey(k) && !told.Contains(m)) told.Add(m);
        return told;
    }

    void UpgradeRefused(List<string> problems)
    {
        var col = new VBoxContainer { CustomMinimumSize = new Vector2(620, 0) }; col.AddThemeConstantOverride("separation", 14);
        col.AddChild(Style.Text("升级后的牌组不符合构筑规则", 26, Style.HoverText, Style.Kai));
        foreach (var p in problems.Take(6))
        {
            var l = Style.Text(p, 19, Style.Danger, Style.Kai); l.AutowrapMode = TextServer.AutowrapMode.WordSmart; l.CustomMinimumSize = new Vector2(600, 0); col.AddChild(l);
        }
        var note = Style.Text("改好以后才能完成升级、进入下一关。", 18, Style.PaperDim, Style.Kai); note.AutowrapMode = TextServer.AutowrapMode.WordSmart; note.CustomMinimumSize = new Vector2(600, 0); col.AddChild(note);
        var row = new HBoxContainer { Alignment = BoxContainer.AlignmentMode.End }; col.AddChild(row);
        Control scrim = null!;
        row.AddChild(Ui.Button("继续升级", () => App.I.CloseModal(scrim), 20, true));
        scrim = App.I.Modal(Ui.Panel(col, 34));
    }

    // the meta text without the upgrade notes (they are not part of the deck's own metadata), for comparing two metas
    static string MetaWithoutNotes(string? meta)
    {
        try
        {
            var o = System.Text.Json.Nodes.JsonNode.Parse(string.IsNullOrEmpty(meta) ? "{}" : meta) as System.Text.Json.Nodes.JsonObject;
            if (o is null) return meta ?? "";
            o.Remove(UpgradeNotesKey);
            return o.ToJsonString();
        }
        catch { return meta ?? ""; }
    }

    string ResultMeta(JsonElement deck)
    {
        try
        {
            var m = J.Get(deck, "meta");
            var o = (m.ValueKind == JsonValueKind.String ? System.Text.Json.Nodes.JsonNode.Parse(m.GetString() is { Length: > 0 } t ? t : "{}") : m.ValueKind == JsonValueKind.Object ? System.Text.Json.Nodes.JsonNode.Parse(m.GetRawText()) : null) as System.Text.Json.Nodes.JsonObject;
            if (o is null) return startMeta;
            o.Remove(UpgradeNotesKey);
            return o.ToJsonString();
        }
        catch { return startMeta; }
    }
}
