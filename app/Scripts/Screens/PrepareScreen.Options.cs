using Godot;
using Gathering;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ArkhamUI;

// The web version's game options that the new version lacked (Yunke 11-23, docs/WEB-SETUP-GAP-20261123.md #1 #2 #7):
//   剧本模式 / 单场冒险模式（使用剧本关卡）: a campaign played from its start, or one of its chapters on its own, with the
//     standalone rules (50 experience limit, a random basic weakness more for each 10: DeckProblem);
//   使用塔罗牌解牌: the core's tarot readings at each scenario's start (CreateRequest tarot);
//   恩赐与最后通牒: the chosen entries by their core names (CreateRequest ultimatumsAndBoons). The deckbuilding ones the web
//     version checks at deck choice (deckRestrictions.ts) are checked here before the game is made (UltimatumDeckProblem).
// Words: the html locale (create.*, ultimatumsAndBoons.*). Hidden in the 新手教程.
public partial class PrepareScreen
{
    // kept while the app runs, so coming back from the workshop or switching 原版 / 重返 keeps the choice
    static bool tarotOn;
    static readonly HashSet<string> boonsOn = new();
    bool standaloneMode;
    string standaloneChapter = "";
    // the chapter chosen for 单场冒险 per campaign, so the workshop's way back (a new PrepareScreen) keeps it
    static readonly Dictionary<string, string> standaloneByCamp = new();
    void KeepMode() { if (standaloneMode) standaloneByCamp[camp.Id] = standaloneChapter; else standaloneByCamp.Remove(camp.Id); }
    VBoxContainer routeHost = null!;
    Button? boonsButton;

    // the web version's lists (GameOptions.vue uabGroups; Types.hs refractionScope for the campaign-bound ones)
    static readonly string[] BoonTags = { "BoonOfTheAncients", "BoonOfAthena", "BoonOfDestiny", "BoonOfHades", "BoonOfHermes", "BoonOfThoth", "BoonOfOsiris", "BoonOfTheMorrigan", "BoonOfPersephone", "BoonOfTheExplorer", "BoonOfTheChild" };
    static readonly string[] UltimatumTags = { "UltimatumOfAgony", "UltimatumOfBrokenPromises", "UltimatumOfTheBrokenVeil", "UltimatumOfChaos", "UltimatumOfDisaster", "UltimatumOfDread", "UltimatumOfExile", "UltimatumOfFailure", "UltimatumOfFinality", "UltimatumOfForbiddenKnowledge", "UltimatumOfHardship", "UltimatumOfTheHighlander", "UltimatumOfInduction", "UltimatumOfMalevolence", "UltimatumOfOrthodoxy", "UltimatumOfTheScream", "UltimatumOfTheSpiral", "UltimatumOfSurvival", "UltimatumOfUltimatums" };
    static readonly (string Tag, string[] Campaigns)[] Refractions =
    {
        ("UltimatumOfVenom", new[] { "04", "53" }), ("UltimatumOfAmbuscade", new[] { "04", "53" }), ("UltimatumOfAnnoyance", new[] { "08" }),
        ("UltimatumOfTheUnspeakableName", new[] { "03", "52" }), ("UltimatumOfTheBrassCrown", new[] { "03", "52" }), ("UltimatumOfTheSleeper", new[] { "11" }),
        ("UltimatumOfTheFaultyCarburetor", new[] { "07" }), ("UltimatumOfInvisibility", new[] { "02", "51" }), ("UltimatumOfTheMan", new[] { "03", "52" }),
        ("UltimatumOfSpoilage", new[] { "11" }), ("UltimatumOfDeath", new[] { "03", "52" }), ("UltimatumOfMultiplication", new[] { "02", "51" }),
        ("UltimatumOfTheDrowned", new[] { "07" }), ("BoonOfTheDreamer", new[] { "06" }), ("BoonOfAtonement", new[] { "09" }), ("BoonOfBliss", new[] { "10" }),
        ("BoonOfTheMiners", new[] { "10" }), ("BoonOfTheDance", new[] { "10" }),
    };
    // enforced at deck choice, not by the in-game switch (GameOptions.vue UAB_DECKBUILDING_TAGS)
    static readonly HashSet<string> DeckbuildingTags = new() { "UltimatumOfChaos", "UltimatumOfDisaster", "UltimatumOfTheHighlander", "UltimatumOfInduction", "UltimatumOfOrthodoxy", "UltimatumOfExile" };
    // the core draws the Morrígan's three weaknesses only for a deck that still holds the random-weakness placeholder; this
    // front end draws that weakness itself before the start (StandaloneDecks.Prepare), so the boon would never come up
    const string Morrigan = "BoonOfTheMorrigan";

    string RefractionCampaign => camp.ReturnOf.Length > 0 ? camp.ReturnOf : camp.Id;
    IEnumerable<string> RefractionTags => Refractions.Where(r => r.Campaigns.Contains(camp.Id) || r.Campaigns.Contains(RefractionCampaign)).Select(r => r.Tag);
    // only what this campaign offers goes into the request (a refraction chosen for another campaign stays out)
    string[] ChosenBoons => boonsOn.Where(t => BoonTags.Contains(t) || UltimatumTags.Contains(t) || RefractionTags.Contains(t)).OrderBy(t => t).ToArray();
    GameVariants Variants => Guide.On ? GameVariants.None : new GameVariants(tarotOn, ChosenBoons);
    bool PlaysStandalone => camp.IsScenario || standaloneMode;

    static string W(string key, string fallback) => App.I.S!.Locale.TryTemplate(key, out var t) && t.Length > 0 ? App.I.S!.Locale.Plain(t) : fallback;

    static Button Toggle(string text, bool on, Action<bool> changed, int size = 20)
    {
        var b = new Button { Text = text, ToggleMode = true, ButtonPressed = on, FocusMode = FocusModeEnum.None, MouseDefaultCursorShape = CursorShape.PointingHand, SizeFlagsVertical = SizeFlags.ShrinkCenter };
        b.AddThemeFontSizeOverride("font_size", size); if (Style.Kai is not null) b.AddThemeFontOverride("font", Style.Kai);
        var lit = Ui.SoftBox(0.6f, true); var off = Ui.SoftBox(0.25f, false);
        b.AddThemeStyleboxOverride("pressed", lit); b.AddThemeStyleboxOverride("hover_pressed", lit); b.AddThemeStyleboxOverride("normal", off);
        b.AddThemeColorOverride("font_pressed_color", Style.Amber);
        b.Toggled += on2 => changed(on2);
        return b;
    }

    // 剧本模式 / 单场冒险模式（使用剧本关卡）: only for a campaign (a side story is a single scenario already)
    Control ModeChoice()
    {
        var row = new HBoxContainer(); row.AddThemeConstantOverride("separation", 8);
        if (camp.IsScenario || Guide.On || camp.Chapters.Length < 2) { standaloneMode = false; return row; }
        if (standaloneByCamp.TryGetValue(camp.Id, out var kept) && camp.Chapters.Any(c => c.Code == kept)) { standaloneMode = true; standaloneChapter = kept; }
        var g = new ButtonGroup();
        foreach (var (stand, key, fb) in new[] { (false, "create.campaign", "剧本模式"), (true, "create.standalone", "单场冒险模式（使用剧本关卡）") })
        {
            var b = Toggle(W(key, fb), standaloneMode == stand, on => { if (on && standaloneMode != stand) { standaloneMode = stand; if (stand && standaloneChapter.Length == 0) standaloneChapter = camp.Chapters[0].Code; KeepMode(); RebuildRoute(); Rebuild(); } });
            b.ButtonGroup = g; b.CustomMinimumSize = new Vector2(0, 44); row.AddChild(b);
        }
        QaScript.Register("prepare-mode", a =>
        {
            standaloneMode = a.StartsWith("standalone");
            var code = a.Contains(',') ? a.Split(',')[1] : "";
            standaloneChapter = code.Length > 0 ? code : camp.Chapters[0].Code;
            KeepMode(); RebuildRoute(); Rebuild();
        });
        return row;
    }

    void RebuildRoute()
    {
        Ui.Clear(routeHost);
        routeHost.AddChild(standaloneMode ? ChapterPicker() : Route());
    }

    // the chapters to play on their own: one is chosen (选择冒险, the web version's pickScenario)
    Control ChapterPicker()
    {
        var col = new VBoxContainer(); col.AddThemeConstantOverride("separation", 10);
        col.AddChild(Style.Text(W("create.pickScenario", "选择冒险"), 26, Style.HoverText, Style.Kai));
        HBoxContainer flow = null!; var g = new ButtonGroup();
        for (int i = 0; i < camp.Chapters.Length; i++)
        {
            if (i % 5 == 0) { flow = new HBoxContainer(); flow.AddThemeConstantOverride("separation", 8); col.AddChild(flow); }
            var c = camp.Chapters[i];
            var b = Toggle(camp.Label(c.Code), c.Code == standaloneChapter, on => { if (on) { standaloneChapter = c.Code; KeepMode(); Rebuild(); } }, 21);
            b.ButtonGroup = g; b.CustomMinimumSize = new Vector2(0, 44); flow.AddChild(b);
        }
        return col;
    }

    // 使用塔罗牌解牌 and 恩赐与最后通牒, under the chaos bag
    Control OptionsBox()
    {
        var col = new VBoxContainer(); col.AddThemeConstantOverride("separation", 10);
        if (Guide.On) return col;
        col.AddChild(Ui.Spacer(0, 6));
        var row = new HBoxContainer(); row.AddThemeConstantOverride("separation", 12); col.AddChild(row);
        var tarot = Toggle(W("create.includeTarotReadings", "使用塔罗牌解牌"), tarotOn, on => tarotOn = on, 21); tarot.CustomMinimumSize = new Vector2(0, 46);
        row.AddChild(tarot);
        boonsButton = Ui.MinimalButton("", OpenBoons, 21); boonsButton.CustomMinimumSize = new Vector2(0, 46); row.AddChild(boonsButton);
        RefreshBoonsButton();
        QaScript.Register("prepare-variants", a =>
        {
            // QA: prepare-variants:tarot,BoonOfAthena,UltimatumOfFailure (empty: none)
            var parts = a.Split(',', StringSplitOptions.RemoveEmptyEntries);
            tarotOn = parts.Contains("tarot"); tarot.SetPressedNoSignal(tarotOn);
            boonsOn.Clear(); foreach (var p in parts.Where(p => p != "tarot")) boonsOn.Add(p);
            RefreshBoonsButton();
            GD.Print("QA_VARIANTS tarot=" + tarotOn + " boons=" + string.Join(",", ChosenBoons));
        });
        QaScript.Register("prepare-boons", _ => OpenBoons());
        return col;
    }

    // the chaos bag as these ultimatums leave it at the campaign's (or scenario's) setup: 失败 adds an auto-fail token, 破碎承诺
    // takes the elder sign out (the core does the same at the start; the table then shows the bag the core holds)
    string[] BagWithUltimatums(string[] faces)
    {
        var chosen = ChosenBoons; var l = faces.ToList();
        if (chosen.Contains("UltimatumOfBrokenPromises")) l.Remove("ElderSign");
        if (chosen.Contains("UltimatumOfFailure")) { int i = l.IndexOf("AutoFail"); l.Insert(i >= 0 ? i : l.Count, "AutoFail"); }
        return l.ToArray();
    }

    void RefreshBoonsButton()
    {
        if (chaosBox is not null && IsInstanceValid(chaosBox)) RebuildChaos();
        if (boonsButton is null) return;
        int n = ChosenBoons.Length;
        boonsButton.Text = W("ultimatumsAndBoons.title", "恩赐与最后通牒") + " · " + (n == 0 ? W("ultimatumsAndBoons.noneSelected", "未选择") : W("ultimatumsAndBoons.selectedCount", "已选择 {count} 项").Replace("{count}", n.ToString()));
    }

    // the web version's panel: the boons, the ultimatums (测试版) and this campaign's refractions (折光, 测试版), each entry
    // its official name and text; a deckbuilding entry says it is checked at deck choice
    void OpenBoons()
    {
        var outer = new VBoxContainer { CustomMinimumSize = new Vector2(Math.Min(1180, GetViewportRect().Size.X - 200), 0) }; outer.AddThemeConstantOverride("separation", 14);
        outer.AddChild(Style.Text(W("ultimatumsAndBoons.title", "恩赐与最后通牒"), 36, Style.HoverText, Style.Kai));   // the title face lacks 牒
        var scroll = new ScrollContainer { CustomMinimumSize = new Vector2(0, Math.Min(720, GetViewportRect().Size.Y - 300)), HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled };
        outer.AddChild(scroll);
        var list = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill }; list.AddThemeConstantOverride("separation", 14); scroll.AddChild(list);
        var groups = new List<(string Key, bool Beta, string[] Tags)> { ("boons", false, BoonTags), ("ultimatums", true, UltimatumTags) };
        var refr = RefractionTags.ToArray(); if (refr.Length > 0) groups.Add(("refractions", true, refr));
        foreach (var (key, beta, tags) in groups)
        {
            var head = new HBoxContainer(); head.AddThemeConstantOverride("separation", 12); list.AddChild(head);
            head.AddChild(Style.Text(W("ultimatumsAndBoons." + key, key), 28, Style.HoverText, Style.Kai));
            if (beta) head.AddChild(Style.Text(W("ultimatumsAndBoons.betaBadge", "测试版"), 18, Style.Brass, Style.Kai));
            foreach (var tag in tags) list.AddChild(Entry(tag));
            list.AddChild(Ui.Spacer(0, 8));
        }
        Control scrim = null!;
        var done = Ui.Button(W("ok", "确定"), () => App.I.CloseModal(scrim), 24, true); done.CustomMinimumSize = new Vector2(240, 58); done.SizeFlagsHorizontal = SizeFlags.ShrinkEnd;
        outer.AddChild(done);
        scrim = App.I.Modal(Ui.Panel(outer, 32));
        scrim.TreeExited += RefreshBoonsButton;
    }

    Control Entry(string tag)
    {
        bool unusable = false;   // 莫莉根的恩赐 works since 11-23 (the placeholder goes to the core, StandaloneDecks.Prepare)
        var row = new HBoxContainer(); row.AddThemeConstantOverride("separation", 14);
        var b = Toggle(W("ultimatumsAndBoons.entries." + tag + ".name", tag), boonsOn.Contains(tag) && !unusable, on => { if (on) boonsOn.Add(tag); else boonsOn.Remove(tag); RefreshBoonsButton(); }, 21);
        b.CustomMinimumSize = new Vector2(300, 46); b.SizeFlagsVertical = SizeFlags.ShrinkBegin; row.AddChild(b);
        var words = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill }; words.AddThemeConstantOverride("separation", 2); row.AddChild(words);
        var text = Style.Text(W("ultimatumsAndBoons.entries." + tag + ".text", ""), 19, Style.Paper, Style.Kai);
        text.AutowrapMode = TextServer.AutowrapMode.WordSmart; text.CustomMinimumSize = new Vector2(600, 0); words.AddChild(text);
        if (DeckbuildingTags.Contains(tag)) words.AddChild(Style.Text(W("ultimatumsAndBoons.deckbuildingBadge", "组牌条目——不受游戏内开关影响"), 16, Style.Brass, Style.Kai));
        if (unusable)
        {
            b.Disabled = true; b.TooltipText = "新版在开局前就抽好了随机基础弱点，这一项暂时无法生效";
            words.AddChild(Style.Text(b.TooltipText, 16, Style.PaperDim, Style.Kai));
        }
        return row;
    }

    // The deckbuilding ultimatums the web version checks at deck choice (deckRestrictions.ts ultimatumDeckError), in its
    // official words (ultimatumsAndBoons.deckErrors.*): 正统 needs a taboo list; 入门仪式 (campaign) only level 0 cards; 放逐
    // (standalone) no card with exile; 高地人 one copy of each title, except titles the investigator's deck must hold.
    // Weaknesses are exempt from all of them.
    static string? UltimatumDeckProblem(Services S, JsonElement deck, string[] chosen, bool standalone)
    {
        if (chosen.Length == 0) return null;
        if (chosen.Contains("UltimatumOfOrthodoxy") && !(J.Get(deck, "taboo_id").ValueKind == JsonValueKind.Number && J.Int(deck, "taboo_id") > 0))
            return W("ultimatumsAndBoons.deckErrors.orthodoxy", "正统的最后通牒：此牌组必须使用禁书名单");
        var slots = CoreCatalog.Slots(deck);
        bool induction = !standalone && chosen.Contains("UltimatumOfInduction"), exile = standalone && chosen.Contains("UltimatumOfExile");
        foreach (var (code, count) in slots)
        {
            if (count <= 0) continue;
            var card = S.Card(code);
            if (card.ValueKind != JsonValueKind.Object || J.String(card, "subtype_code").Length > 0) continue;
            if (induction && J.Int(card, "xp") > 0)
                return W("ultimatumsAndBoons.deckErrors.induction", "入门仪式的最后通牒：牌组只能包含0级卡牌（{name}）").Replace("{name}", S.Name(code));
            if (exile && (Regex.IsMatch(J.String(card, "real_text"), "exile", RegexOptions.IgnoreCase) || J.String(card, "text").Contains("放逐")))
                return W("ultimatumsAndBoons.deckErrors.exile", "放逐的最后通牒：带有放逐的卡牌仅限战役模式（{name}）").Replace("{name}", S.Name(code));
        }
        if (chosen.Contains("UltimatumOfTheHighlander"))
        {
            var required = new HashSet<string>();
            var inv = S.Card(J.String(deck, "investigator_code"));
            foreach (var req in J.Props(J.Get(J.Get(inv, "deck_requirements"), "card")))
            {
                required.Add(Title(S, req.Name));
                foreach (var alt in J.Props(req.Value)) required.Add(Title(S, alt.Name));
            }
            var byTitle = new Dictionary<string, int>();
            foreach (var (code, count) in slots)
            {
                if (count <= 0) continue;
                var card = S.Card(code);
                if (card.ValueKind != JsonValueKind.Object || J.String(card, "subtype_code").Length > 0) continue;
                string title = Title(S, code);
                if (required.Contains(title)) continue;
                byTitle[title] = byTitle.GetValueOrDefault(title) + count;
                if (byTitle[title] > 1) return W("ultimatumsAndBoons.deckErrors.highlander", "高地人的最后通牒：每个{name}只能有1张").Replace("{name}", S.Name(code));
            }
        }
        return null;
    }
    static string Title(Services S, string code) { var c = S.Card(code); var t = J.String(c, "real_name"); return t.Length > 0 ? t : J.String(c, "name", code); }
}
