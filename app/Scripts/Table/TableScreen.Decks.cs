using System.Text.Json;
using System.Text.Json.Nodes;
using Godot;
using Gathering;

namespace ArkhamUI;

// Yunke 11-19: a campaign's deck lives with its save. Upgrades between chapters go to the rules core and stay in the game
// (no new deck in the workshop); the deck as it stands now is seen from inside the game (OpenDeckViewer). When the campaign
// ends, each investigator's final deck goes to the workshop as a 通关卡组, which can start a new game.
public partial class TableScreen
{
    // the seat (SetupStore / state.Seats index) whose starting deck belongs to this investigator, or -1
    int SeatOf(Entity inv)
    {
        var keys = SetupStore.Load(gameId); string code = CoreCatalog.Normalize(inv.Code);
        return Array.FindIndex(keys, k => S.Decks.Has(k) && CoreCatalog.Normalize(J.String(S.Decks.Get(k), "investigator_code")) == code);
    }

    // the seat whose side deck is this investigator's: its starting deck's seat, else (an online guest, whose seat has no deck in
    // this library) the local player's own seat when the investigator is theirs
    int SideSeat(TableModel m, Entity inv)
    {
        int seat = SeatOf(inv);
        if (seat < 0 && state is not null && m.Me?.Id == inv.Id) seat = Array.FindIndex(state.Seats, s => s.Player == state.Player);
        return seat;
    }

    bool InCampaign(TableModel m) =>J.Get(J.Get(m.Game, "mode"), "This").ValueKind == JsonValueKind.Object;

    // the whole deck this investigator plays in this game: in a campaign the campaign's record (with its upgrades), else the
    // deck the seat brought
    Dictionary<string, int> GameDeck(TableModel m, Entity inv)
    {
        int seat = SeatOf(inv);
        if (InCampaign(m) && state is not null)
        {
            try { var d = B.CurrentDeck(state, inv.Id); if (d.Count > 0) return d; } catch { }
        }
        return seat >= 0 ? CurrentDeckSlots(seat) : new();
    }

    // ───────── the campaign is over: its decks go to the workshop
    bool completedSaved;
    int SaveCompletedDecks()
    {
        if (completedSaved || state is null || model is null) return 0;
        completedSaved = true;
        var m = model; var cd = Camp; var keys = SetupStore.Load(gameId);
        bool won = J.String(J.Get(m.Game, "outcome"), "result") == "Won";
        string now = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ");
        int saved = 0;
        foreach (var inv in m.Investigators)
        {
            string code = CoreCatalog.Normalize(inv.Code);
            string id = "completed-" + gameId.ToString("N")[..12] + "-" + code;
            if (S.Decks.Dismissed(id)) continue;   // deleted in the workshop: not made again each time the finished save is opened
            if (S.Decks.Has(id))
            {
                // already kept (and maybe edited in the workshop since): left alone, except the trauma it lacks (kept before 11-22)
                if (JsonNode.Parse(S.Decks.Get(id).GetRawText()) is JsonObject old && JsonNode.Parse(old["meta"]?.GetValue<string>() ?? "{}") is JsonObject om && om["arkhamui_physical"] is null)
                {
                    om["arkhamui_physical"] = J.Int(inv.Raw, "physicalTrauma"); om["arkhamui_mental"] = J.Int(inv.Raw, "mentalTrauma");
                    old["meta"] = om.ToJsonString(); S.Decks.Put(id, JsonSerializer.SerializeToElement(old)); saved++;
                }
                continue;
            }
            var slots = GameDeck(m, inv); if (slots.Count == 0) continue;
            int seat = SeatOf(inv);
            var deck = seat >= 0 ? JsonNode.Parse(S.Decks.Get(keys[seat]).GetRawText()) as JsonObject ?? new JsonObject() : new JsonObject();
            JsonObject meta;
            try { meta = seat >= 0 ? StandaloneDecks.Metadata(S.Decks.Get(keys[seat])) : new JsonObject(); } catch { meta = new JsonObject(); }
            // the standalone adapter's own counts belong to the starting deck, not to this one
            foreach (var k in new[] { StandaloneDecks.BudgetKey, StandaloneDecks.ExtrasKey, StandaloneDecks.BuildXpKey }) meta.Remove(k);
            meta[BuildDecks.KindKey] = "completed";
            meta[BuildDecks.CampaignKey] = cd.Name;
            bool killed = J.Get(inv.Raw, "killed").ValueKind == JsonValueKind.True, insane = J.Get(inv.Raw, "drivenInsane").ValueKind == JsonValueKind.True;
            meta[BuildDecks.ResultKey] = (won ? "胜利" : "失败") + (killed ? " · 已被击败" : insane ? " · 已发疯" : "");
            meta["arkhamui_game"] = gameId.ToString("D");
            // the trauma the investigator came out with (Yunke 11-22: it was nowhere to be seen when the deck was picked again)
            meta["arkhamui_physical"] = J.Int(inv.Raw, "physicalTrauma"); meta["arkhamui_mental"] = J.Int(inv.Raw, "mentalTrauma");
            deck["id"] = id;
            deck["name"] = S.Name(code) + " · " + cd.Name;   // no "通关" in the name: a mark shows it (Yunke 11-22)
            deck["investigator_code"] ??= code;
            deck["slots"] = JsonSerializer.SerializeToNode(slots);
            // the side deck as the campaign left it (after its upgrades), not the starting deck's copy
            deck["sideSlots"] = JsonSerializer.SerializeToNode(SideSeat(m, inv) is int ss and >= 0 ? CurrentSideSlots(ss) : new Dictionary<string, int>());
            deck["meta"] = meta.ToJsonString();
            // experience not spent yet goes with it (build's "xp" on a deck)
            deck["xp"] = Math.Max(0, J.Int(inv.Raw, "xp") - J.Int(inv.Raw, "spentXp")) + (seat >= 0 ? CarryXp(gameId, seat) : 0);
            deck["date_creation"] = now; deck["date_update"] = now;
            deck.Remove("previous_deck"); deck.Remove("next_deck");
            S.Decks.Put(id, JsonSerializer.SerializeToElement(deck));
            saved++;
        }
        if (saved > 0) { S.Decks.Save(); BuildDecks.Invalidate(); }
        if (App.I.QaRunning) GD.Print("QA_COMPLETED_DECKS " + saved);
        return saved;
    }

    // ───────── the deck in play, from inside the game (read-only)
    void OpenDeckViewer(Entity? who = null)
    {
        var m = model; if (m?.Me is null) return;
        var inv = who ?? m.Me;
        var slots = GameDeck(m, inv);
        int total = slots.Values.Sum();
        var vp = GetViewportRect().Size;
        var col = new VBoxContainer { CustomMinimumSize = new Vector2(Math.Min(1320, vp.X - 160), 0) }; col.AddThemeConstantOverride("separation", 12);
        Control scrim = null!;
        if (m.Investigators.Count() > 1)
        {
            var who2 = new HBoxContainer(); who2.AddThemeConstantOverride("separation", 8); col.AddChild(who2);
            foreach (var other in m.Investigators)
            {
                var o = other; var b = Ui.MinimalButton(m.Name(o), () => { App.I.CloseModal(scrim); OpenDeckViewer(o); }, 19, o.Id == inv.Id);
                b.CustomMinimumSize = new Vector2(0, 40); who2.AddChild(b);
            }
        }
        col.AddChild(Style.Title(m.Name(inv) + " · 当前牌组 · " + total + " 张", 30));
        col.AddChild(Style.Text(InCampaign(m) ? "本次战役的牌组，含各章之间的升级，随存档保存。" : "本局带入的牌组。", 19, Style.PaperDim, Style.Kai));
        var body = new VBoxContainer(); body.AddThemeConstantOverride("separation", 10);
        static string Group(JsonElement c) => J.String(c, "subtype_code") is "weakness" or "basicweakness" ? "weakness" : J.String(c, "type_code");
        var groups = slots.Select(p => (Code: p.Key, N: p.Value, Card: S.Card(p.Key)))
            .GroupBy(x => Group(x.Card)).OrderBy(g => g.Key switch { "asset" => 0, "event" => 1, "skill" => 2, "weakness" => 4, _ => 3 });
        foreach (var g in groups)
        {
            body.AddChild(Style.Text((g.Key == "weakness" ? "弱点" : BuildDecks.TypeName(g.Key)) + "（" + g.Sum(x => x.N) + "）", 21, Style.Brass, Style.Kai));
            var flow = new HFlowContainer(); flow.AddThemeConstantOverride("h_separation", 12); flow.AddThemeConstantOverride("v_separation", 12); body.AddChild(flow);
            foreach (var x in g.OrderBy(x => J.Int(x.Card, "cost")).ThenBy(x => S.Name(x.Code)))
            {
                var cell = new VBoxContainer(); cell.AddThemeConstantOverride("separation", 2);
                var code = x.Code;
                var cv = new CardView { Code = code, CustomMinimumSize = new Vector2(104, 146), Size = new Vector2(104, 146), OnClick = () => CardReader.Open(code, printed: true) };
                cell.AddChild(cv);
                int lv = J.Int(x.Card, "xp");
                var q = Style.Text("×" + x.N + (lv > 0 ? "  " + new string('•', Math.Min(5, lv)) : ""), 18, Style.Paper, Style.Kai); q.HorizontalAlignment = HorizontalAlignment.Center; cell.AddChild(q);
                flow.AddChild(cell);
            }
        }
        // the side deck (not played; kept with the save, to buy from between scenarios)
        var side = SideSeat(m, inv) is int sideSeat and >= 0 ? CurrentSideSlots(sideSeat) : new Dictionary<string, int>();
        if (side.Count > 0)
        {
            body.AddChild(Style.Text("侧卡组（" + side.Values.Sum() + "）", 21, Style.Brass, Style.Kai));
            var sflow = new HFlowContainer(); sflow.AddThemeConstantOverride("h_separation", 12); sflow.AddThemeConstantOverride("v_separation", 12); body.AddChild(sflow);
            foreach (var (code, n) in side.OrderBy(p => J.Int(S.Card(p.Key), "cost")).ThenBy(p => S.Name(p.Key)))
            {
                var cell = new VBoxContainer(); cell.AddThemeConstantOverride("separation", 2);
                var c0 = code;
                cell.AddChild(new CardView { Code = c0, CustomMinimumSize = new Vector2(104, 146), Size = new Vector2(104, 146), OnClick = () => CardReader.Open(c0, printed: true), Modulate = new Color(1, 1, 1, 0.8f) });
                int lv = J.Int(S.Card(code), "xp");
                var q = Style.Text("×" + n + (lv > 0 ? "  " + new string('•', Math.Min(5, lv)) : ""), 18, Style.PaperDim, Style.Kai); q.HorizontalAlignment = HorizontalAlignment.Center; cell.AddChild(q);
                sflow.AddChild(cell);
            }
        }
        if (total == 0) body.AddChild(Style.Text("读不到这副牌组。", 20, Style.PaperDim));
        var scroll = Ui.Scroll(body); scroll.CustomMinimumSize = new Vector2(0, Math.Min(640, vp.Y - 300)); col.AddChild(scroll); SmoothScroll.Attach(scroll);
        col.AddChild(Ui.MinimalButton("关闭", () => App.I.CloseModal(scrim), 22));
        scrim = App.I.Modal(Ui.Panel(col, 30));
        if (App.I.QaRunning) GD.Print("QA_DECK_VIEW " + m.Name(inv) + " " + total + " side=" + side.Values.Sum());
    }
}
