using Godot;
using Gathering;
using System.Text.Json;

namespace ArkhamUI;

// A killed or insane investigator is replaced (Yunke 11-19: "阵亡或发疯后更换调查员的ui需要修改 规避这类问题").
// Rules: that investigator cannot take part in the campaign again; the player goes on with a new investigator not used in this
// campaign before, with a new deck (the core starts the newcomer at 0 experience). The core asks for it with the same question
// as an upgrade (ChooseUpgradeDeck) at three points between two chapters — after the chapter, at the upgrade step, before the
// next chapter (reference Campaign/Runner.hs HandleKilledOrInsaneInvestigators / UpgradeDeckStep) — and accepts either a
// replacement (ReplaceInvestigatorAnswer) or "keep", after which the lost investigator simply sits the next chapter out.
// Before, the table showed the upgrade editor for the lost investigator each time, with no word of why, and could only keep.
public partial class TableScreen
{
    // killed / driven insane, in the campaign log's wording; null while the investigator can still play
    static string? LostFate(Entity inv) =>
        J.Get(inv.Raw, "killed").ValueKind == JsonValueKind.True ? "已被击败"
        : J.Get(inv.Raw, "drivenInsane").ValueKind == JsonValueKind.True ? "已发疯" : null;

    // "not now" for a lost investigator holds until the next chapter has been played (the core asks again up to three times
    // between two chapters; the player is not asked the same thing three times)
    readonly HashSet<string> replaceDeferred = new();
    string DeferKey(TableModel m, Entity inv) => inv.Id + "|" + Completed(m).Count;
    bool ReplaceDeferred(TableModel m) => m.Me is { } me && LostFate(me) is not null && replaceDeferred.Contains(DeferKey(m, me));

    Control? replaceScrim;

    void ReplaceDialog(Entity me, string fate)
    {
        var m = model!; var vp = GetViewportRect().Size;
        if (replaceScrim is not null && IsInstanceValid(replaceScrim)) App.I.CloseModal(replaceScrim);
        var root = Ui.Backdrop(vp, Ui.Bg("desk-table"), 0.55f);
        float w = Math.Min(1040, vp.X - 200);
        var col = new VBoxContainer { Position = new Vector2((vp.X - w) / 2, vp.Y * 0.12f), Size = new Vector2(w, vp.Y * 0.76f) }; col.AddThemeConstantOverride("separation", 18);
        root.AddChild(col);
        var kicker = Style.Text("战役 · " + Camp.Name, 24, Style.Brass, Style.Kai); kicker.HorizontalAlignment = HorizontalAlignment.Center; col.AddChild(kicker);
        var title = Style.Title(m.Name(me) + " " + fate, 44); title.HorizontalAlignment = HorizontalAlignment.Center; col.AddChild(title);
        col.AddChild(new ColorRect { Color = new Color(Style.Brass, 0.3f), CustomMinimumSize = new Vector2(0, 1) });
        var row = new HBoxContainer { Alignment = BoxContainer.AlignmentMode.Center }; row.AddThemeConstantOverride("separation", 40); col.AddChild(row);
        // the lost investigator: the whole portrait card, greyed
        var face = new VBoxContainer { Alignment = BoxContainer.AlignmentMode.Center }; face.AddThemeConstantOverride("separation", 6); row.AddChild(face);
        var pc = CardView.PortraitCard(me.Code, 220); pc.Modulate = new Color(0.55f, 0.55f, 0.55f); face.AddChild(pc);
        int phys = J.Int(me.Raw, "physicalTrauma"), ment = J.Int(me.Raw, "mentalTrauma");
        var tr = Style.Text("创伤 " + phys + " 肉体 · " + ment + " 精神", 18, Style.Danger, Style.Kai); tr.HorizontalAlignment = HorizontalAlignment.Center; face.AddChild(tr);
        var text = new VBoxContainer { CustomMinimumSize = new Vector2(Math.Min(560, w - 320), 0), SizeFlagsVertical = SizeFlags.ShrinkCenter }; text.AddThemeConstantOverride("separation", 14); row.AddChild(text);
        foreach (var (line, size, color) in new[]
        {
            ("这名调查员不能再参加本战役。", 26, Style.Paper),
            ("按规则，这位玩家换一名本战役没用过的调查员继续：新调查员带一副新牌组，从 0 经验开始，下一章起上场。", 21, Style.PaperDim),
            ("也可以暂不替换：这位玩家下一章不上场，下一章结束后会再问一次。", 19, new Color(Style.PaperDim, 0.8f)),
        })
        { var l = Style.Text(line, size, color, Style.Kai); l.AutowrapMode = TextServer.AutowrapMode.WordSmart; text.AddChild(l); }
        col.AddChild(Ui.Spacer(0, 0, true));
        var go = new HBoxContainer { Alignment = BoxContainer.AlignmentMode.Center }; go.AddThemeConstantOverride("separation", 20); col.AddChild(go);
        var later = Ui.Button("暂不替换", async () =>
        {
            if (replaceScrim is not null) App.I.CloseModal(replaceScrim);
            replaceDeferred.Add(DeferKey(m, me));
            await Do("暂不替换", () => B.KeepDeck(state!, me.Id));
            await Advance();
        }, 22);
        later.CustomMinimumSize = new Vector2(220, 62); go.AddChild(later);
        var pick = Ui.Button("选择新调查员", () => OpenReplacePicker(me), 26, true); pick.CustomMinimumSize = new Vector2(300, 62); go.AddChild(pick);
        replaceScrim = App.I.Modal(root, false, 0f);
        if (App.I.QaRunning) GD.Print("QA_REPLACE_DIALOG " + CoreCatalog.Normalize(me.Code) + " " + fate);
    }

    // the group picture, with every investigator this campaign has used dimmed; it opens on the campaign's own box
    void OpenReplacePicker(Entity me, SelectionStage.Scene? scene = null)
    {
        var used = B.UsedInvestigators(state!).Select(CoreCatalog.Normalize).ToHashSet();
        var stage = new SelectionStage(used, (code, deckKey) => _ = Replace(me, code, deckKey), scene ?? (Camp.Id == Campaigns.Zealot.Id ? SelectionStage.Core : SelectionStage.Dunwich))
        {
            Heading = "选择新调查员",
            Hint = "替换" + model!.Name(me) + "。本战役用过的调查员会变暗；新调查员要用不含升级卡的牌组。右键或 Esc 返回。",
            TakenWord = "本战役已用过", TakenToast = "在本战役里已经用过，不能再选",
        };
        stage.SwitchTo = other => { stage.QueueFree(); OpenReplacePicker(me, other); };
        App.I.Overlay.AddChild(stage); stage.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
    }

    // experience the cards of a deck cost (a starting deck: 0)
    int DeckXp(JsonElement deck) => CoreCatalog.Slots(deck).Sum(p => J.Int(S.Card(p.Key), "xp") * p.Value);

    async Task Replace(Entity me, string code, string deckKey)
    {
        if (deckKey.Length == 0 || !S.Decks.Has(deckKey)) { App.I.Toast("还没有" + S.Name(code) + "的牌组，请先在牌组工坊构筑。", true); return; }
        var deck = S.Decks.Get(deckKey);
        if (DeckXp(deck) > 0) { App.I.Toast("新调查员从 0 经验开始，请选一副不含升级卡的牌组。", true); OpenReplacePicker(me); return; }
        int seat = Array.FindIndex(state!.Seats, s => s.Player == state.Player);
        long before = state.Sequence;
        await Do("更换调查员：" + S.Name(code), () => B.ReplaceInvestigator(state!, me.Id, deck));
        if (state!.Sequence == before) return;   // refused: the message has been shown, the dialog is still there
        if (replaceScrim is not null && IsInstanceValid(replaceScrim)) App.I.CloseModal(replaceScrim);
        // the seat now plays this deck: later upgrades, the deck viewer and the campaign's final decks start from it
        var keys = SetupStore.Load(gameId);
        if (seat >= 0 && seat < keys.Length) { keys[seat] = deckKey; SetupStore.Save(gameId, keys); }
        if (seat >= 0)
        {
            System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(UpgradeFile(gameId, seat))!);
            System.IO.File.WriteAllText(UpgradeFile(gameId, seat), JsonSerializer.Serialize(CoreCatalog.Slots(deck)));
            System.IO.File.WriteAllText(CarryFile(gameId, seat), "0");
            SaveSideSlots(gameId, seat, SideOf(deck));   // the new deck's side deck, not the lost investigator's
        }
        if (App.I.QaRunning) GD.Print("QA_REPLACED " + CoreCatalog.Normalize(me.Code) + " -> " + code + " deck=" + deckKey + " seq=" + state.Sequence);
        App.I.Toast(S.Name(code) + " 加入战役，接替" + model!.Name(me), false);
        await Advance();
    }
}

// The trauma an investigator took, with the resolution's own sentence that gave it (Yunke 11-20 #27: "雷克斯收到1精神创伤是因为
// 剧本结局吗？…类似这样的原因要把文本写在这里"). The sentences are taken from the resolution pages as they are read, kept per
// game beside the save's other table notes, and shown on the campaign screen under the trauma they explain.
public partial class TableScreen
{
    List<string[]>? traumaNotes;   // [chapter title, sentence]
    string TraumaFile => System.IO.Path.Combine(App.I.DataRoot, "setups", gameId.ToString("D") + "-trauma.json");

    List<string[]> TraumaNotes()
    {
        if (traumaNotes is not null) return traumaNotes;
        try { traumaNotes = System.IO.File.Exists(TraumaFile) ? JsonSerializer.Deserialize<List<string[]>>(System.IO.File.ReadAllText(TraumaFile)) ?? new() : new(); }
        catch { traumaNotes = new(); }
        return traumaNotes;
    }

    void NoteTrauma(IEnumerable<string> paragraphs, string title)
    {
        var notes = TraumaNotes(); bool added = false;
        foreach (var p in paragraphs)
            foreach (var sentence in System.Text.RegularExpressions.Regex.Split(System.Text.RegularExpressions.Regex.Replace(p, "<[^>]+>", ""), @"(?<=[。！？])"))
            {
                var t = sentence.Trim(); if (!t.Contains("创伤") || t.Length < 4) continue;
                if (notes.Any(n => n[1] == t)) continue;
                notes.Add(new[] { title, t }); added = true;
            }
        if (!added) return;
        try { System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(TraumaFile)!); System.IO.File.WriteAllText(TraumaFile, JsonSerializer.Serialize(notes)); } catch { }
    }

    // the sentences that speak of this investigator: by name, as the lead investigator, or of every investigator
    IEnumerable<string> TraumaReasons(TableModel m, Entity inv)
    {
        if (J.Int(inv.Raw, "physicalTrauma") + J.Int(inv.Raw, "mentalTrauma") == 0) yield break;
        string lead = "";
        try { lead = J.String(J.Get(J.Get(B.SavedDocument(state!), "snapshot"), "game"), "gameLeadInvestigatorId"); } catch { }
        string name = m.Name(inv);
        foreach (var n in TraumaNotes())
        {
            string t = n[1];
            bool mine = t.Contains(name) || (t.Contains("调查员队长") && lead == inv.Id) || t.Contains("每位调查员") || t.Contains("所有调查员");
            if (mine) yield return (n[0].Length > 0 ? n[0] + "：" : "") + t;
        }
    }
}
