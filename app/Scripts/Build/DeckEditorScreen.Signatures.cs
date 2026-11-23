using Godot;
using Gathering;
using System.Text.Json;

namespace ArkhamUI;

// 专属与弱点 (Yunke 11-23: "调查员的专属和弱点移除卡组之后找不到在哪里添加（尤其是有些调查员还有平行专属和弱点）"): every card that
// belongs to this deck's investigator — the required signature cards and weakness, the advanced and replacement versions,
// and those of the parallel investigator — in one panel, each with how many are in the deck and a button to put one in
// or take one out. The sets are build's own deck-create sets (decks.createInit / createInfo), read for the original back
// and, when there is one, for the parallel back.
public partial class DeckEditorScreen
{
    Button? signatureButton;
    static readonly JsonElement BasicWeaknessCard = JsonDocument.Parse("{\"code\":\"01000\",\"name\":\"随机基础弱点\",\"subtype_code\":\"basicweakness\"}").RootElement.Clone();
    // basic weaknesses in the deck: the 随机基础弱点 card and the ones already drawn for it (or put in by hand)
    int BasicWeaknessesInDeck() => J.Items(J.Get(view, "slots")).Where(x => J.String(x, "code") == RandomWeakness || J.String(x, "subtype_code") == "basicweakness").Sum(x => J.Int(x, "quantity"));

    async Task<List<(string Title, JsonElement Card, int Qty)>> SignatureSets()
    {
        var sides = J.Get(view, "sides");
        string original = J.String(sides, "original"); if (original.Length == 0) original = J.String(view, "investigator");
        string parallel = J.String(sides, "parallel");
        var found = new List<(string, JsonElement, int)>(); var seen = new HashSet<string>(); int basic = 0;
        void Take(JsonElement info, bool par)
        {
            foreach (var set in J.Items(J.Get(info, "sets")))
            {
                string sid = J.String(set, "id");
                // the basic weakness (bug #27 "弱点不小心删掉找不到了"): the 随机基础弱点 card goes back in from here too; a game's
                // start draws a real one for it (StandaloneDecks.Prepare)
                if (sid == "random_basic_weakness") { if (!par && seen.Add(RandomWeakness)) basic = J.Items(J.Get(set, "cards")).FirstOrDefault(c => J.String(c, "code") == RandomWeakness) is { ValueKind: JsonValueKind.Object } bw ? J.Int(bw, "quantity", 1) : 1; continue; }
                string title = sid switch { "requiredCards" => "专属卡", "advanced" => "进阶专属卡", "replacement" => "替换专属卡", "extra" => "平行调查员附加卡", _ => J.String(set, "title", sid) };
                if (par && sid != "extra") title = "平行 · " + title;
                foreach (var c in J.Items(J.Get(set, "cards")))
                {
                    string code = J.String(c, "code");
                    if (code.Length == 0 || !seen.Add(code)) continue;
                    found.Add((title, c.Clone(), J.Get(c, "quantity").ValueKind == JsonValueKind.Number ? J.Int(c, "quantity") : 1));
                }
            }
        }
        Take(await BuildDecks.CreateInit(original), false);
        if (parallel.Length > 0)
        {
            await BuildDecks.CreateSet("createSide", parallel, "back");
            Take(await BuildDecks.CreateInfo(), true);
        }
        if (basic > 0) found.Add(("基础弱点", BasicWeaknessCard, basic));
        return found;
    }

    async void OpenSignatures()
    {
        if (signatureButton is null) return;
        List<(string Title, JsonElement Card, int Qty)> sets;
        try { sets = await SignatureSets(); }
        catch (Exception e) { App.I.Toast("读取专属卡失败：" + e.Message, true); return; }
        var col = new VBoxContainer { CustomMinimumSize = new Vector2(760, 0) }; col.AddThemeConstantOverride("separation", 10);
        col.AddChild(Style.Text("专属与弱点", 26, Style.HoverText, Style.Kai));
        var hint = Style.Text("这名调查员自己的卡（含平行调查员的）。移出卡组后，可以在这里加回来。", 17, Style.PaperDim, Style.Kai);
        hint.AutowrapMode = TextServer.AutowrapMode.WordSmart; hint.CustomMinimumSize = new Vector2(720, 0); col.AddChild(hint);
        if (sets.Count == 0) col.AddChild(Caption("这名调查员没有专属卡"));
        var scroll = new ScrollContainer { CustomMinimumSize = new Vector2(760, Math.Min(620, 70 * sets.Count + 60)), HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled };
        var list = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill }; list.AddThemeConstantOverride("separation", 6); scroll.AddChild(list); col.AddChild(scroll);
        string lastTitle = "";
        foreach (var (title, card, qty) in sets)
        {
            if (title != lastTitle) { list.AddChild(Style.Text(title, 19, Style.Brass, Style.Kai)); lastTitle = title; }
            string code = J.String(card, "code");
            var row = new HBoxContainer(); row.AddThemeConstantOverride("separation", 10); list.AddChild(row);
            var pic = Ui.Image(Ui.Card(code), new Vector2(44, 62), true); pic.MouseFilter = MouseFilterEnum.Stop; Peek.Attach(pic, code); row.AddChild(pic);
            bool weak = J.String(card, "subtype_code") is "weakness" or "basicweakness";
            var name = Style.Text(J.String(card, "name") + (weak && code != RandomWeakness ? "（弱点）" : ""), 20, weak ? new Color("d86a6a") : Style.Paper, Style.Kai);
            name.SizeFlagsHorizontal = SizeFlags.ExpandFill; name.SizeFlagsVertical = SizeFlags.ShrinkCenter; row.AddChild(name);
            bool isBasic = code == RandomWeakness;
            int now = isBasic ? BasicWeaknessesInDeck() : InDeck(code);
            var count = Style.Text("牌组中 " + now + " / " + qty, 18, now >= qty ? Style.Good : Style.PaperDim, Style.Kai); count.SizeFlagsVertical = SizeFlags.ShrinkCenter; row.AddChild(count);
            var add = Ui.Button("加入", async () => { await Do(() => BuildDecks.Edit("quantity", deckId, code, 1, Math.Max(qty, 1), "slots", "increment"), true); OpenSignatures(); }, 18, now < qty);
            add.Disabled = now >= qty; add.SizeFlagsVertical = SizeFlags.ShrinkCenter; row.AddChild(add);
            var take = Ui.Button("移出", async () => { await Do(() => BuildDecks.Edit("quantity", deckId, code, -1, 99, "slots", "increment"), true); OpenSignatures(); }, 18);
            // 移出 takes out the 随机基础弱点 card itself (a drawn one comes out from the deck list)
            take.Disabled = (isBasic ? InDeck(code) : now) == 0; take.SizeFlagsVertical = SizeFlags.ShrinkCenter; row.AddChild(take);
        }
        Popover(signatureButton, col);
    }
}
