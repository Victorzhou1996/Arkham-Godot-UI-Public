using Godot;
using Gathering;
using System.Text.Json;

namespace ArkhamUI;

// The rest of arkham.build's deck editor (Yunke 11-19 r10: "build的功能已经全量加进组牌系统了吗？…要添加这些功能"), in the
// places build puts them:
//   牌组设置 (build's config tab, an entry of the 编辑主卡组 switch) — tags, description / notes, the investigator's front
//     and back (original or parallel), the deck-building choices (secondary class, deck size, option), the taboo list,
//     the limited card pool, the standalone experience allowance, and in a campaign upgrade the experience adjustment;
//   牌组统计 (build's deck tools) — cost curve, classes, skill icons, traits, and the draw simulator;
//   the card window (build's card modal; right-click a card in the deck list, or left-click a customizable one) —
//     the customization sheet, the card's note, moving it between main and side deck, its higher versions, the big view.
// Every change is one of build's own actions on the deck's pending edits (decks.tags / description / side / meta /
// taboo / xpAdjustment / customization / annotation / swap); 保存并确定 saves them as before.
public partial class DeckEditorScreen
{
    static string W(string key, string fallback) => BuildDecks.Word(key, fallback);
    static string Plain(string html) => System.Text.RegularExpressions.Regex.Replace(html ?? "", "<[^>]+>", "").Replace("[[", "").Replace("]]", "");

    Label Section(string title) { var l = Style.Text(title, 20, Style.HoverText, Style.Kai); return l; }
    static Control Rule() => new ColorRect { Color = new Color(Style.Brass, 0.25f), CustomMinimumSize = new Vector2(0, 1), MouseFilter = MouseFilterEnum.Ignore };
    static LineEdit Field(string text, string placeholder)
    {
        var e = new LineEdit { Text = text, PlaceholderText = placeholder, CustomMinimumSize = new Vector2(0, 44) }; e.AddThemeFontSizeOverride("font_size", 19);
        if (Style.Kai is not null) e.AddThemeFontOverride("font", Style.Kai);
        e.AddThemeStyleboxOverride("normal", Ui.SoftBox(0.45f, false, 12, 4)); e.AddThemeStyleboxOverride("focus", Ui.SoftBox(0.6f, true, 12, 4));
        return e;
    }
    HFlowContainer Pills() { var f = new HFlowContainer { CustomMinimumSize = new Vector2(360, 0) }; f.AddThemeConstantOverride("h_separation", 6); f.AddThemeConstantOverride("v_separation", 6); return f; }
    static string Meta(JsonElement view, string key) => J.Get(J.Get(view, "metaParsed"), key) is { ValueKind: JsonValueKind.String } v ? v.GetString()! : J.Get(J.Get(view, "metaParsed"), key) is { ValueKind: JsonValueKind.Number } n ? n.GetRawText() : "";

    // ───────── 牌组设置
    Control ConfigPanel()
    {
        var col = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill }; col.AddThemeConstantOverride("separation", 10);

        // tags (build: space separated) and the description / notes
        col.AddChild(Section(W("deck_edit.config.tags", "标签")));
        var tags = Field(J.String(view, "tags"), "例如：独立剧本 进攻");
        tags.TextSubmitted += t => _ = Do(() => BuildDecks.Edit("tags", deckId, t.Trim()));
        tags.FocusExited += () => { if (tags.Text.Trim() != J.String(view, "tags")) _ = Do(() => BuildDecks.Edit("tags", deckId, tags.Text.Trim())); };
        col.AddChild(tags);
        col.AddChild(Section("描述与笔记"));
        var notes = new TextEdit { Text = J.String(view, "description"), PlaceholderText = "牌组思路、换牌说明、战役记录……", CustomMinimumSize = new Vector2(0, 150), WrapMode = TextEdit.LineWrappingMode.Boundary };
        notes.AddThemeFontSizeOverride("font_size", 18); if (Style.Kai is not null) notes.AddThemeFontOverride("font", Style.Kai);
        notes.AddThemeStyleboxOverride("normal", Ui.SoftBox(0.45f, false, 12, 8)); notes.AddThemeStyleboxOverride("focus", Ui.SoftBox(0.6f, true, 12, 8));
        notes.FocusExited += () => { if (notes.Text != J.String(view, "description")) _ = Do(() => BuildDecks.Edit("description", deckId, notes.Text)); };
        col.AddChild(notes);
        col.AddChild(Rule());

        // the investigator's sides (original / parallel), as build's config editor offers them
        var sides = J.Get(view, "sides");
        if (J.String(sides, "parallel").Length > 0)
        {
            // the four combinations of front and back, picked in one go (Yunke 11-23: "无法直接选择正反面的原版平行调查员卡背组合")
            col.AddChild(Section("调查员正反面"));
            var row = Pills(); col.AddChild(row);
            string orig = J.String(sides, "original"), par = J.String(sides, "parallel");
            string nowF = J.String(sides, "front"), nowB = J.String(sides, "back");
            foreach (var (f, b) in new[] { (orig, orig), (orig, par), (par, orig), (par, par) })
            {
                string label = (f == orig ? "原版" : "平行") + "正面 · " + (b == orig ? "原版" : "平行") + "背面";
                var ff = f; var bb = b;
                row.AddChild(Pill(label, nowF == f && nowB == b, () => _ = Do(async () => { await BuildDecks.Edit("side", deckId, "front", ff); await BuildDecks.Edit("side", deckId, "back", bb); }, true), 18));
            }
            col.AddChild(Rule());
        }

        // deck-building choices (build's selection editor): secondary class, deck size, an option
        foreach (var o in J.Items(J.Get(view, "choices")))
        {
            string optionId = J.String(o, "id");
            if (J.Get(o, "faction_select").ValueKind == JsonValueKind.Array)
            {
                string key = optionId.Length > 0 ? optionId : "faction_selected";
                col.AddChild(Section(W("deck_edit.config.faction_select", "选择职业") + (J.String(o, "name").Length > 0 ? " · " + J.String(o, "name") : "")));
                var row = Pills(); col.AddChild(row);
                foreach (var f in J.Items(J.Get(o, "faction_select"))) { var v = f.GetString()!; row.AddChild(Pill(BuildDecks.FactionName(v), Meta(view, key) == v, () => _ = Do(() => BuildDecks.Edit("meta", deckId, key, v, null), true), 18)); }
            }
            if (J.Get(o, "deck_size_select").ValueKind == JsonValueKind.Array)
            {
                col.AddChild(Section(W("deck_edit.config.deck_size", "牌组张数")));
                var row = Pills(); col.AddChild(row);
                foreach (var f in J.Items(J.Get(o, "deck_size_select"))) { var v = f.ValueKind == JsonValueKind.String ? f.GetString()! : f.GetRawText(); row.AddChild(Pill(v + " 张", Meta(view, "deck_size_selected") == v, () => _ = Do(() => BuildDecks.Edit("meta", deckId, "deck_size_selected", v, null), true), 18)); }
            }
            if (J.Get(o, "option_select").ValueKind == JsonValueKind.Array)
            {
                col.AddChild(Section(W("deck_edit.config.option_select", "选择一项")));
                var row = Pills(); col.AddChild(row);
                foreach (var f in J.Items(J.Get(o, "option_select"))) { var v = J.String(f, "id"); row.AddChild(Pill(J.String(f, "name", v), Meta(view, "option_selected") == v, () => _ = Do(() => BuildDecks.Edit("meta", deckId, "option_selected", v, null), true), 18)); }
            }
        }

        // the taboo list (the rules core plays it: card changes, chains, experience)
        col.AddChild(Section(W("deck_edit.config.taboo", "禁卡表")));
        var tabooRow = Pills(); col.AddChild(tabooRow);
        _ = FillTaboos(tabooRow);
        col.AddChild(Rule());

        // the limited card pool (build: card_pool, the packs this deck may use)
        col.AddChild(Section(W("deck_edit.config.card_pool.section_title", "卡池")));
        var poolNow = J.Items(J.Get(view, "cardPool")).Select(p => p.GetString()!).ToHashSet();
        var poolHint = Style.Text(poolNow.Count == 0 ? "不限制：所有扩展包的卡都可以用。选中扩展包后，只能用这些包里的卡（专属卡和已在牌组里的卡除外）。" : "只用这些扩展包里的卡（" + poolNow.Count + " 个）。", 16, Style.PaperDim, Style.Kai);
        poolHint.AutowrapMode = TextServer.AutowrapMode.WordSmart; poolHint.CustomMinimumSize = new Vector2(360, 0); col.AddChild(poolHint);
        var poolRow = Pills(); col.AddChild(poolRow);
        poolRow.AddChild(Pill("不限制", poolNow.Count == 0, () => _ = SetPool(new HashSet<string>()), 17));
        foreach (var (code, name, packs) in cycles)
        {
            var all = packs.Select(p => p.Code).ToArray();
            poolRow.AddChild(Pill(name, all.All(poolNow.Contains), () => { var next = new HashSet<string>(poolNow); bool on = !all.All(poolNow.Contains); foreach (var k in all) { if (on) next.Add(k); else next.Remove(k); } _ = SetPool(next); }, 17));
        }
        col.AddChild(Rule());

        // standalone experience (our rule layer, FFG standalone mode) and, in a campaign upgrade, build's xp adjustment
        int need = J.Int(J.Get(view, "stats"), "xpRequired");
        if (upgrade is null)
        {
            // no budget to set any more (bug #36, Yunke 11-23): the standalone rules hold only when the deck starts a scenario
            // played on its own, and the experience used is the deck's own; the workshop and a campaign have no limit
            col.AddChild(Section("单场冒险模式"));
            string extra = need >= 10 && need <= 49 ? "（这副牌组 " + need / 10 + " 张）" : "";
            var hint = Style.Text("本牌组需要 " + need + " 经验（含升级项与禁卡表）。只在单场冒险模式（单独打一个剧本）开局时检查：不能包含50或更多经验的卡牌，每10经验额外加入1张随机基础弱点" + extra + "。战役中不受此限制。", 17, need > 49 ? Style.Danger : Style.PaperDim, Style.Kai);
            // wraps inside the column (unwrapped it pushed the whole deck column off the right edge)
            hint.AutowrapMode = TextServer.AutowrapMode.WordSmart; hint.SizeFlagsHorizontal = SizeFlags.ExpandFill; hint.CustomMinimumSize = new Vector2(160, 0); col.AddChild(hint);
        }
        else
        {
            col.AddChild(Section(W("deck_edit.config.xp_adjustment", "经验调整")));
            var row = new HBoxContainer(); row.AddThemeConstantOverride("separation", 10); col.AddChild(row);
            var spin = new SpinBox { MinValue = -20, MaxValue = 20, Value = J.Int(view, "xpAdjustment"), CustomMinimumSize = new Vector2(130, 44) };
            spin.ValueChanged += v => _ = Do(() => BuildDecks.Edit("xpAdjustment", deckId, (int)v));
            row.AddChild(spin);
        }
        return col;
    }

    Task FillTaboos(HFlowContainer row) => TabooChip.Fill(row, CurrentTaboo(), id => _ = Do(() => BuildDecks.Edit("taboo", deckId, id), true));

    Task SetPool(HashSet<string> packs) => Do(() => BuildDecks.Edit("meta", deckId, "card_pool", packs.Count == 0 ? null : string.Join(",", packs), null), true);

    // ───────── 牌组统计 (build's deck tools)
    Control StatsPanel()
    {
        var col = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill }; col.AddThemeConstantOverride("separation", 10);
        var cards = J.Items(J.Get(view, "slots")).Where(c => J.String(c, "subtype_code").Length == 0 && J.String(c, "type_code") is "asset" or "event" or "skill").ToList();
        int Qty(JsonElement c) => J.Int(c, "quantity");

        // cost curve: assets and events by cost
        col.AddChild(Section(W("deck.stats.cost_curve", "费用曲线")));
        var curve = new int[8];
        foreach (var c in cards.Where(c => J.String(c, "type_code") != "skill")) { int k = Cost(c); curve[k < 0 ? 7 : Math.Min(6, k)] += Qty(c); }
        col.AddChild(Bars(Enumerable.Range(0, 8).Where(i => i < 7 || curve[7] > 0).Select(i => (i == 7 ? "X" : i == 6 ? "6+" : i.ToString(), curve[i], Style.Amber)).ToList()));

        // classes
        col.AddChild(Section(W("deck.stats.factions", "职业分布")));
        var fac = cards.GroupBy(c => J.String(c, "faction_code")).Select(g => (BuildDecks.FactionName(g.Key), g.Sum(Qty), FactionColour(g.Key))).OrderByDescending(x => x.Item2).ToList();
        col.AddChild(Bars(fac));

        // skill icons
        col.AddChild(Section(W("deck.stats.skill_icons", "技能图标")));
        var skills = new (string Field, string Name, Color Colour)[] { ("skill_willpower", "意志", Style.Willpower), ("skill_intellect", "智力", Style.Intellect), ("skill_combat", "战斗", Style.Combat), ("skill_agility", "敏捷", Style.Agility), ("skill_wild", "万能", Style.Paper) };
        col.AddChild(Bars(skills.Select(s => (s.Name, cards.Sum(c => J.Int(c, s.Field) * Qty(c)), s.Colour)).ToList()));

        // traits
        col.AddChild(Section(W("deck.stats.traits", "特性")));
        var traits = cards.SelectMany(c => J.String(c, "traits").Split(new[] { '.', '。', '·' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(t => (t, Qty(c))))
            .GroupBy(x => x.t).Select(g => (g.Key, g.Sum(x => x.Item2), Style.Brass)).OrderByDescending(x => x.Item2).Take(8).ToList();
        col.AddChild(Bars(traits));
        col.AddChild(Rule());

        // draw simulator
        col.AddChild(Section(W("draw_simulator.title", "抽牌模拟")));
        var hand = new HFlowContainer { CustomMinimumSize = new Vector2(360, 0) }; hand.AddThemeConstantOverride("h_separation", 6); hand.AddThemeConstantOverride("v_separation", 6);
        var buttons = new HBoxContainer(); buttons.AddThemeConstantOverride("separation", 8); col.AddChild(buttons); col.AddChild(hand);
        var deck = new List<string>(); var drawn = new List<string>();
        void Shuffle()
        {
            deck.Clear(); drawn.Clear(); Ui.Clear(hand);
            foreach (var c in J.Items(J.Get(view, "slots"))) for (int i = 0; i < Qty(c); i++) deck.Add(J.String(c, "code"));
            var rnd = new Random(); for (int i = deck.Count - 1; i > 0; i--) { int j = rnd.Next(i + 1); (deck[i], deck[j]) = (deck[j], deck[i]); }
        }
        void Draw(int n)
        {
            for (int i = 0; i < n && deck.Count > 0; i++)
            {
                var code = deck[0]; deck.RemoveAt(0); drawn.Add(code);
                var size = new Vector2(84, MathF.Round(84 / CardAspect));
                var cv = new CardView { Code = code, CustomMinimumSize = size, Size = size, HoverZoom = false, Redrawn = false, Actionable = false };
                cv.GuiInput += e => { if (e is InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Right }) CardReader.Open(code, printed: true); };
                hand.AddChild(cv); RoundCorners(cv);
            }
        }
        buttons.AddChild(Ui.Button("起手 5 张", () => { Shuffle(); Draw(5); }, 18, true));
        buttons.AddChild(Ui.Button("再抽 1 张", () => { if (deck.Count == 0 && drawn.Count == 0) Shuffle(); Draw(1); }, 18));
        buttons.AddChild(Ui.Button("重洗", () => Shuffle(), 18));
        Shuffle();
        return col;
    }

    static Color FactionColour(string f) => f switch { "guardian" => new Color("5a8fd6"), "seeker" => new Color("d99a52"), "rogue" => new Color("54b47a"), "mystic" => new Color("9a78d0"), "survivor" => new Color("d86a6a"), _ => new Color("9a9284") };

    // horizontal bars: label, a soft bar as long as the share, the number
    static Control Bars(List<(string Label, int Value, Color Colour)> rows)
    {
        var grid = new GridContainer { Columns = 3, SizeFlagsHorizontal = SizeFlags.ExpandFill }; grid.AddThemeConstantOverride("h_separation", 10); grid.AddThemeConstantOverride("v_separation", 4);
        int max = Math.Max(1, rows.Count == 0 ? 1 : rows.Max(r => r.Value));
        foreach (var (label, value, colour) in rows)
        {
            var l = Style.Text(label, 17, Style.Paper, Style.Kai); l.CustomMinimumSize = new Vector2(64, 0); grid.AddChild(l);
            var track = new Control { CustomMinimumSize = new Vector2(230, 20), SizeFlagsHorizontal = SizeFlags.ExpandFill, MouseFilter = MouseFilterEnum.Ignore };
            var bar = new Panel { MouseFilter = MouseFilterEnum.Ignore }; var sb = new StyleBoxFlat { BgColor = new Color(colour, 0.8f), ShadowColor = new Color(colour, 0.35f), ShadowSize = 6, AntiAliasing = true }; sb.SetCornerRadiusAll(6);
            bar.AddThemeStyleboxOverride("panel", sb); track.AddChild(bar);
            float share = value / (float)max;
            track.Resized += () => { bar.Position = new Vector2(0, 3); bar.Size = new Vector2(Math.Max(value > 0 ? 6 : 0, track.Size.X * share), 14); };
            grid.AddChild(track);
            var n = Style.Text(value.ToString(), 17, Style.HoverText, Style.Sans); n.CustomMinimumSize = new Vector2(30, 0); grid.AddChild(n);
        }
        return grid;
    }

    // ───────── the card window
    // Right-click a card, in the deck list or in the pool (Yunke 11-21: smaller than the big card view of 11-20, and the deck
    // actions in it): the card, how many the deck part holds with −1 / +1, 升级为高级版… when build has higher versions, the
    // move to the side deck, the customization sheet and the note. Left clicks stay simple: the pool adds one, the deck list
    // takes one out; upgrading is the 升级 mark on the deck row or this window.
    async Task CardDialog(string code)
    {
        if (busy) return;
        string part = tab is "sideSlots" or "extraSlots" ? tab : "slots";
        var deckCard = J.Items(J.Get(view, part)).FirstOrDefault(c => J.String(c, "code") == code);
        var poolCard = pool.FirstOrDefault(c => J.String(c, "code") == code);
        bool inDeck = deckCard.ValueKind == JsonValueKind.Object;
        var card = inDeck ? deckCard : poolCard;
        JsonElement sheet = default, ups = default;
        if (inDeck)
        {
            try { sheet = await BuildEngine.I.CallJson("decks.customizable", deckId, code); } catch { }
            try { ups = await BuildEngine.I.CallJson("decks.upgrades", deckId, part); } catch { }
        }
        string name = card.ValueKind == JsonValueKind.Object ? J.String(card, "name", CardName(code)) : CardName(code);
        int lim = poolCard.ValueKind == JsonValueKind.Object ? Limit(poolCard) : inDeck ? Limit(deckCard) : 0;

        var root = new HBoxContainer(); root.AddThemeConstantOverride("separation", 26);
        var cv = BigCard(code, CompactSize()); root.AddChild(cv);
        var col = new VBoxContainer { CustomMinimumSize = new Vector2(440, 0) }; col.AddThemeConstantOverride("separation", 10); root.AddChild(col);
        var title = Style.Title(name, 30); title.AutowrapMode = TextServer.AutowrapMode.WordSmart; title.CustomMinimumSize = new Vector2(440, 0); col.AddChild(title);
        if (card.ValueKind == JsonValueKind.Object && Xp(card) > 0) col.AddChild(Style.Text(Xp(card) + " 级", 18, Style.PaperDim, Style.Kai));

        // how many, with −1 / +1 (the window stays open; the numbers follow)
        string partName = part == "sideSlots" ? "侧卡组" : part == "extraSlots" ? "额外卡组" : "主卡组";
        var qrow = new HBoxContainer(); qrow.AddThemeConstantOverride("separation", 10); col.AddChild(qrow);
        var qlabel = Style.Text("", 21, Style.Paper, Style.Kai); qlabel.SizeFlagsHorizontal = SizeFlags.ExpandFill; qlabel.VerticalAlignment = VerticalAlignment.Center; qrow.AddChild(qlabel);
        Button minus = null!, plus = null!;
        void Counts()
        {
            int have = InDeck(code, part);
            qlabel.Text = partName + "里有 " + have + (lim > 0 ? " / " + lim : "") + " 张";
            minus.Disabled = busy || have == 0 || tab == "bonded";
            plus.Disabled = busy || poolCard.ValueKind != JsonValueKind.Object || have >= lim || tab == "bonded";
        }
        minus = Ui.Button("−1", async () => { await Remove(code); if (IsInstanceValid(qlabel)) Counts(); }, 22); minus.CustomMinimumSize = new Vector2(74, 48); qrow.AddChild(minus);
        plus = Ui.Button("+1", async () => { await Add(code, null); if (IsInstanceValid(qlabel)) Counts(); }, 22, true); plus.CustomMinimumSize = new Vector2(74, 48); qrow.AddChild(plus);
        Counts();

        // upgrade and move
        var acts = new HFlowContainer(); acts.AddThemeConstantOverride("h_separation", 8); acts.AddThemeConstantOverride("v_separation", 8); col.AddChild(acts);
        if (ups.ValueKind == JsonValueKind.Object && ups.TryGetProperty(code, out var versions) && versions.GetArrayLength() > 0)
        { var list = versions.EnumerateArray().Select(x => x.Clone()).ToList(); acts.AddChild(Ui.Button("升级为高级版…", () => { ClosePopover(); UpgradeChoice(code, list); }, 19, true)); }
        if (inDeck && part is "slots" or "sideSlots")
        {
            string to = part == "slots" ? "sideSlots" : "slots";
            acts.AddChild(Ui.Button(part == "slots" ? "移一张到侧卡组" : "移一张到主卡组", () => { ClosePopover(); _ = Do(() => BuildDecks.Edit("swap", deckId, code, to), true); }, 19));
        }

        if (inDeck)
        {
            // the customization sheet
            if (sheet.ValueKind == JsonValueKind.Object)
            {
                col.AddChild(Rule());
                col.AddChild(Section("可定制升级项（勾选花费经验）"));
                var lines = Plain(J.String(sheet, "text")).Split('\n', StringSplitOptions.RemoveEmptyEntries);
                var chosen = J.Get(sheet, "chosen");
                var list = new VBoxContainer(); list.AddThemeConstantOverride("separation", 6);
                int i = 0;
                foreach (var option in J.Items(J.Get(sheet, "options")))
                {
                    int index = i++; int boxes = J.Int(option, "xp"); var mine = J.Get(chosen, index.ToString());
                    int spent = J.Int(mine, "xp_spent");
                    var row = new HBoxContainer(); row.AddThemeConstantOverride("separation", 8); list.AddChild(row);
                    var marks = new HBoxContainer { SizeFlagsVertical = SizeFlags.ShrinkBegin }; marks.AddThemeConstantOverride("separation", 2); row.AddChild(marks);
                    for (int k = 1; k <= boxes; k++)
                    {
                        int target = k;
                        var box = new Button { Text = k <= spent ? "■" : "□", ToggleMode = false, FocusMode = FocusModeEnum.None, CustomMinimumSize = new Vector2(30, 30), MouseDefaultCursorShape = CursorShape.PointingHand, TooltipText = "勾到第 " + k + " 格" };
                        box.AddThemeFontSizeOverride("font_size", 20); box.AddThemeColorOverride("font_color", k <= spent ? Style.Amber : Style.PaperDim);
                        box.AddThemeStyleboxOverride("normal", new StyleBoxEmpty()); box.AddThemeStyleboxOverride("hover", Ui.SoftBox(0.4f, true, 2, 0)); box.AddThemeStyleboxOverride("pressed", new StyleBoxEmpty()); box.AddThemeStyleboxOverride("focus", new StyleBoxEmpty());
                        box.Pressed += () => _ = Customize(code, index, spent == target ? target - 1 : target, null);
                        marks.AddChild(box);
                    }
                    string text = index < lines.Length ? lines[index].TrimStart('□', ' ') : "";
                    var tl = Style.Text(text, 17, spent >= boxes ? Style.HoverText : Style.Paper, Style.Kai); tl.AutowrapMode = TextServer.AutowrapMode.WordSmart; tl.SizeFlagsHorizontal = SizeFlags.ExpandFill; tl.CustomMinimumSize = new Vector2(370, 0);
                    var textCol = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill }; textCol.AddChild(tl); row.AddChild(textCol);
                    // what an unlocked option asks to choose (build: choose_skill / choose_trait; cards and slots by build's own window)
                    if (spent >= boxes && boxes > 0)
                    {
                        string choice = J.String(option, "choice"), sel = J.String(mine, "selections");
                        if (choice == "choose_skill")
                        {
                            var pr = Pills(); textCol.AddChild(pr);
                            foreach (var (key, label) in new[] { ("willpower", "意志"), ("intellect", "智力"), ("combat", "战斗"), ("agility", "敏捷") })
                            { var k2 = key; pr.AddChild(Pill(label, sel == key, () => _ = Customize(code, index, spent, k2), 16)); }
                        }
                        else if (choice == "choose_trait")
                        {
                            var f = Field(sel.Replace("^", "、"), "填写特性，多项用“、”分开"); f.TextSubmitted += t => _ = Customize(code, index, spent, string.Join("^", t.Split('、', ',', '，').Select(x => x.Trim()).Where(x => x.Length > 0)));
                            textCol.AddChild(f);
                        }
                        else if (choice.Length > 0) textCol.AddChild(Style.Text("（此项的选择暂不能在这里设置）", 15, Style.PaperDim, Style.Kai));
                    }
                }
                var sc = new ScrollContainer { CustomMinimumSize = new Vector2(440, Math.Min(260, 64 * (i + 1))), HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled };
                sc.AddChild(list); list.SizeFlagsHorizontal = SizeFlags.ExpandFill; col.AddChild(sc); SmoothScroll.Attach(sc, 80);
            }
        }

        // the card's note (build: annotations) — only for a card the deck holds
        if (inDeck)
        {
            col.AddChild(Rule());
            col.AddChild(Section("注释"));
            var note = Field(J.String(J.Get(view, "annotations"), code), "为什么带这张卡、怎么用……");
            note.TextSubmitted += t => _ = Do(() => BuildDecks.Edit("annotation", deckId, code, t.Trim()));
            note.FocusExited += () => { if (note.Text.Trim() != J.String(J.Get(view, "annotations"), code)) _ = Do(() => BuildDecks.Edit("annotation", deckId, code, note.Text.Trim())); };
            col.AddChild(note);
        }
        col.AddChild(Ui.Spacer(0, 0, true));
        var foot = new HBoxContainer { Alignment = BoxContainer.AlignmentMode.End }; col.AddChild(foot);
        foot.AddChild(Ui.Button("关闭", ClosePopover, 19));
        Dialog(root);
        RoundCorners(cv);
        if (App.I.QaRunning) GD.Print("QA_CARD_DIALOG " + code + " inDeck=" + inDeck + " card=" + cv.Size);
    }

    // the card window's card: about half the screen's height (Yunke 11-21: the three-quarter one was too big here)
    Vector2 CompactSize() { float h = MathF.Round(Math.Min(GetViewportRect().Size.Y * 0.5f, 540)); return new Vector2(MathF.Round(h * CardAspect), h); }

    // the printed card as big as the screen allows (about three quarters of its height), in CardView's rounded class frame
    Vector2 BigSize() { float h = MathF.Round(Math.Min(GetViewportRect().Size.Y * 0.74f, 800)); return new Vector2(MathF.Round(h * CardAspect), h); }
    CardView BigCard(string code, Vector2? size = null)
    {
        var s = size ?? BigSize();
        return new CardView { Code = code, CustomMinimumSize = s, Size = s, HoverZoom = false, Redrawn = false, Actionable = false, SizeFlagsVertical = SizeFlags.ShrinkCenter, SizeFlagsHorizontal = SizeFlags.ShrinkCenter };
    }

    // a card of the pool, right-clicked: the same card window (its −1 / +1 work on the deck part being edited)
    void LibraryCardDialog(string code) => _ = CardDialog(code);

    async Task Customize(string code, int index, int xp, string? selections)
    {
        var patch = new Dictionary<string, object> { ["xp_spent"] = Math.Max(0, xp) };
        if (selections is not null) patch["selections"] = selections;
        ClosePopover();
        await Do(() => BuildDecks.Edit("customization", deckId, code, index, JsonSerializer.Serialize(patch)), true);
        await CardDialog(code);
    }
}
