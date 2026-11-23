using Godot;
using Gathering;
using System.Text.Json;

namespace ArkhamUI;

// Between chapters of a campaign the core stops with a "ContinueCampaign" step: the campaign screen shows where the
// campaign stands (chapters, experience, trauma) and what comes next, and its button sends the core's own next step.
public partial class TableScreen
{
    // the campaign this game belongs to (its chapters, names and locale section): Campaigns registry
    CampaignDef Camp => model is not null ? Campaigns.ForGame(model.Game) : Campaigns.Zealot;

    // What the next step is, in words.
    string StepText(JsonElement step)
    {
        string tag = J.Tag(step); var c = J.Get(step, "contents");
        string code = StepCode(step);
        return tag switch
        {
            "ScenarioStep" or "ScenarioStepWithOptions" => "下一章：" + (Camp.Find(code) is not null ? Camp.Label(code) : S.Name(code)),
            "UpgradeDeckStep" => "整理牌组：用本章获得的经验升级牌组",
            // the official titles where the campaign has them (theDunwichLegacy.prologue.title = 引导故事, interlude1.title …)
            "PrologueStep" => Official(Camp.Key + ".prologue.title", "序章"),
            "InterludeStep" => Official(Camp.Key + ".interlude" + (c.ValueKind == JsonValueKind.Array && c.GetArrayLength() > 0 ? c[0].GetRawText() : "") + ".title", "幕间"),
            "EpilogueStep" => Official(Camp.Key + ".epilogue.title", "尾声"),
            "ResolutionStep" => "结局",
            // The Scarlet Keys' embarking: 从伦敦启程 (theScarletKeys.headings.embark.<place>), else 启程 (headings.kind.embark)
            "CampaignSpecificStep" when c.ValueKind == JsonValueKind.Array && c.GetArrayLength() > 0 && c[0].ValueKind == JsonValueKind.String =>
                Official(Camp.Key + ".headings.embark." + (c.GetArrayLength() > 1 && c[1].ValueKind == JsonValueKind.String ? c[1].GetString() : ""),
                    Official(Camp.Key + ".headings.kind." + c[0].GetString(), "继续战役")),
            _ => "继续战役",
        };
    }

    // A box that grows with its wrapped text up to `cap` and scrolls beyond it (the story pages, the campaign log). The
    // text keeps one width whether the scroll bar shows or not: when it filled the box, the bar coming and going changed
    // the width, a page whose height sat near the cap wrapped by one line more or less each time, and the layout never
    // settled — the game froze on 聚集于此's resolution 2 page, and froze again whenever that save was opened (QQ #32/#33:
    // heights 442 → 490 → 538 → 442 … forever within one frame). Should anything still go back and forth, the box stops
    // adjusting after a few changes in one frame and keeps what it has.
    static Action FitScroll(ScrollContainer scroll, Control content, float width, float cap)
    {
        content.SizeFlagsHorizontal = SizeFlags.ShrinkBegin;
        int changes = 0;
        void Fit()
        {
            if (!IsInstanceValid(content) || !IsInstanceValid(scroll)) return;
            var want = new Vector2(width, Mathf.Min(content.GetCombinedMinimumSize().Y, cap));
            if (scroll.CustomMinimumSize == want) return;
            if (scroll.IsInsideTree())
            {
                if (++changes > 12) return;
                if (changes == 1) scroll.GetTree().Connect(SceneTree.SignalName.ProcessFrame, Callable.From(() => changes = 0), (uint)ConnectFlags.OneShot);
            }
            scroll.CustomMinimumSize = want;
        }
        content.MinimumSizeChanged += Fit; Fit();
        return Fit;
    }

    // The scenario a campaign step goes to: "c01120" (ScenarioStep), or the first of ["c10501", {options}]
    // (ScenarioStepWithOptions: the Feast of Hemlock Vale's daytime chapters — the screen said only "下一章：" before)
    static string StepCode(JsonElement step)
    {
        if (J.Tag(step) == "CampaignSpecificStep") return "";   // ["embark", "London"]: a campaign's own step, no chapter
        var c = J.Get(step, "contents");
        if (c.ValueKind == JsonValueKind.Array && c.GetArrayLength() > 0) c = c[0];
        return c.ValueKind == JsonValueKind.String ? CoreCatalog.Normalize(c.GetString()!) : "";
    }

    string Official(string key, string fallback) => S.Locale.TryTemplate(key, out var t) && t.Length > 0 ? S.Locale.Plain(t) : fallback;

    // ───────── deck upgrade between scenarios: the workshop in upgrade mode, over the table
    static string UpgradeFile(Guid game, int seat) => System.IO.Path.Combine(App.I.DataRoot, "setups", game.ToString("D") + "-upgrade-" + seat + ".json");
    // Experience left over after an upgrade. The rules core counts all experience as spent whenever a deck is upgraded
    // (it does not price the change); like arkham.build's upgradeDeck, the experience the changes did not use carries
    // over to the next upgrade, so it is kept here per seat.
    static string CarryFile(Guid game, int seat) => System.IO.Path.Combine(App.I.DataRoot, "setups", game.ToString("D") + "-xp-carry-" + seat + ".json");
    // A starting deck's experience the rules core does not keep (Yunke 11-22): an investigator's bonus (马泰奥神父 "剧本开始时你有
    // 额外5点经验值") is not counted by it at all, and a card's (危险处境 3, 禁欲主义 10) is earned and at once spent whole. What
    // the deck's own cards did not use goes on to the first upgrade, like experience an upgrade left over.
    static void StartCarry(Guid game, int seat, System.Text.Json.JsonElement deck)
    {
        if (seat < 0) return;
        // the side deck the seat brought (the core is given none) is kept from here on with the save
        SaveSideSlots(game, seat, SideOf(deck));
        int granted = StandaloneDecks.GrantedXp(deck), spent = StandaloneDecks.Experience(App.I.S!.Catalog, deck), left = Math.Max(0, granted - spent);
        GD.Print("QA_START_XP seat=" + seat + " granted=" + granted + " spent=" + spent + " carry=" + left);
        if (granted == 0) return;
        System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(CarryFile(game, seat))!);
        System.IO.File.WriteAllText(CarryFile(game, seat), left.ToString());
    }
    static int CarryXp(Guid game, int seat) { try { return System.IO.File.Exists(CarryFile(game, seat)) ? Math.Max(0, int.Parse(System.IO.File.ReadAllText(CarryFile(game, seat)).Trim())) : 0; } catch { return 0; } }

    // The deck this seat plays now: its last upgrade in this campaign, or the deck it started with.
    Dictionary<string, int> CurrentDeckSlots(int seat)
    {
        var f = UpgradeFile(gameId, seat);
        if (System.IO.File.Exists(f)) return JsonSerializer.Deserialize<Dictionary<string, int>>(System.IO.File.ReadAllText(f)) ?? new();
        var keys = SetupStore.Load(gameId);
        if (seat >= 0 && seat < keys.Length && S.Decks.Has(keys[seat])) return CoreCatalog.Slots(S.Decks.Get(keys[seat]));
        return new();
    }

    // ───────── the side deck (ArkhamDB "sideSlots": cards set aside to buy later). The rules core never holds one (it refuses a
    // deck that carries one, sideDeckUnsupported), so a campaign keeps it here beside the save, per seat: written when the deck
    // is brought in (StartCarry), after each upgrade, and when an investigator is replaced (Yunke 11-23: "升级的时候侧卡组会消失").
    static string SideFile(Guid game, int seat) => System.IO.Path.Combine(App.I.DataRoot, "setups", game.ToString("D") + "-upgrade-side-" + seat + ".json");
    static Dictionary<string, int> SideOf(JsonElement deck)
        => J.Props(J.Get(deck, "sideSlots")).Where(p => p.Value.ValueKind == JsonValueKind.Number && p.Value.GetInt32() > 0).ToDictionary(p => p.Name, p => p.Value.GetInt32());
    static void SaveSideSlots(Guid game, int seat, Dictionary<string, int> side)
    {
        if (seat < 0) return;
        try { System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(SideFile(game, seat))!); System.IO.File.WriteAllText(SideFile(game, seat), JsonSerializer.Serialize(side)); }
        catch (Exception e) { GD.PushWarning("side deck: " + e.Message); }
        if (App.I.QaRunning) GD.Print("QA_SIDE_SAVED seat=" + seat + " " + side.Values.Sum() + " " + JsonSerializer.Serialize(side));
    }
    // The side deck this seat carries now: the one kept with the save, else the seat's starting deck's (a save from before 11-23),
    // else — an online guest, whose seat has no deck in this computer's library — the deck this player picked in the room.
    Dictionary<string, int> CurrentSideSlots(int seat)
    {
        try { if (seat >= 0 && System.IO.File.Exists(SideFile(gameId, seat))) return JsonSerializer.Deserialize<Dictionary<string, int>>(System.IO.File.ReadAllText(SideFile(gameId, seat))) ?? new(); }
        catch { }
        var keys = SetupStore.Load(gameId);
        if (seat >= 0 && seat < keys.Length && S.Decks.Has(keys[seat])) return SideOf(S.Decks.Get(keys[seat]));
        if (OnlineGame.IsGuest && OnlineGame.OwnDeck is { } own) return SideOf(own);
        return new();
    }

    // the metadata of the deck this seat started with (build's selections such as faction_selected), kept through upgrades
    string SeatDeckMeta(int seat)
    {
        // after an upgrade: the metadata build left then (customization boxes bought between scenarios)
        try { if (seat >= 0 && System.IO.File.Exists(UpgradeMetaFile(gameId, seat))) return System.IO.File.ReadAllText(UpgradeMetaFile(gameId, seat)); } catch { }
        var keys = SetupStore.Load(gameId);
        if (seat < 0 || seat >= keys.Length || !S.Decks.Has(keys[seat])) return "{}";
        var meta = J.Get(S.Decks.Get(keys[seat]), "meta");
        string text = meta.ValueKind == JsonValueKind.String ? meta.GetString()! : meta.ValueKind == JsonValueKind.Object ? meta.GetRawText() : "{}";
        // the standalone adapter's own keys are not deck metadata
        try { var o = System.Text.Json.Nodes.JsonNode.Parse(text)!.AsObject(); o.Remove(StandaloneDecks.BudgetKey); o.Remove(StandaloneDecks.ExtrasKey); return o.ToJsonString(); } catch { return "{}"; }
    }

    // the upgrade editor (build's rules) over the table
    Control OpenUpgradeEditor(UpgradeRequest req)
    {
        var ed = new DeckEditorScreen(req) { ZIndex = 200 };
        AddChild(ed); ed.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        return ed;
    }

    Control? upgradeOverlay;
    async void UpgradeDialog()
    {
        var m = model!; var me = m.Me; if (me is null || state is null) return;
        if (upgradeOverlay is not null && IsInstanceValid(upgradeOverlay)) return;
        // killed or insane: nothing to upgrade — the player replaces the investigator (TableScreen.Replace)
        if (LostFate(me) is { } fate) { ReplaceDialog(me, fate); return; }
        int seat = Array.FindIndex(state.Seats, s => s.Player == state.Player);
        // start from the deck the core holds now (fall back to the stored one only if it cannot be read)
        Dictionary<string, int> current;
        try { current = B.CurrentDeck(state, me.Id); if (current.Count == 0) current = CurrentDeckSlots(seat); } catch { current = CurrentDeckSlots(seat); }
        // the core keeps experience earned (xp) and experience spent (spentXp) apart: what is left is the difference
        // plus what earlier upgrades left unspent
        int xp = Math.Max(0, J.Int(me.Raw, "xp") - J.Int(me.Raw, "spentXp")) + CarryXp(gameId, seat);
        string code = CoreCatalog.Normalize(me.Code);
        // what happened in play that changes this upgrade (TableScreen.UpgradeNotes): boxes the core checked, the quill's
        // choice, the furs' and the medallion's discounts, the cards exiled for 似曾相识
        var hold = new Control(); upgradeOverlay = hold;   // holds the place while the quill is asked
        var ravenChoices = await AskRavenQuill(seat);
        upgradeOverlay = null; hold.Free();
        var notes = UpgradeNotes(seat, me, CurrentDeckSlots(seat), current, ravenChoices);
        string meta = WithCoreCustomizations(SeatDeckMeta(seat), me);
        if (notes is not null) { var mo = System.Text.Json.Nodes.JsonNode.Parse(meta)!.AsObject(); mo[UpgradeNotesKey] = notes.ToJsonString(); meta = mo.ToJsonString(); GD.Print("QA_UPGRADE_NOTES seat=" + seat + " " + notes.ToJsonString()); }
        UpgradeRequest req = null!;
        req = new UpgradeRequest(code, m.Name(me), current, xp, async (chosen, cost) =>
        {
            if (upgradeOverlay is not null && IsInstanceValid(upgradeOverlay)) upgradeOverlay.QueueFree();
            upgradeOverlay = null;
            UseUpNotes(seat, notes);
            // the side deck after the upgrade stays with the save (never sent to the core)
            if (req.ResultSide is { } side) SaveSideSlots(gameId, seat, side);
            // build's metadata of the upgraded deck (boxes bought now), when the editor hands it back (property ResultMeta)
            string sendMeta = meta;
            if (req.GetType().GetProperty("ResultMeta")?.GetValue(req) is string rm && rm.Length > 0)
            {
                try { var ro = System.Text.Json.Nodes.JsonNode.Parse(rm)!.AsObject(); ro.Remove(UpgradeNotesKey); sendMeta = ro.ToJsonString(); System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(UpgradeMetaFile(gameId, seat))!); System.IO.File.WriteAllText(UpgradeMetaFile(gameId, seat), sendMeta); } catch { }
            }
            else { try { var so = System.Text.Json.Nodes.JsonNode.Parse(meta)!.AsObject(); so.Remove(UpgradeNotesKey); sendMeta = so.ToJsonString(); } catch { } }
            var slots = chosen ?? current;
            System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(UpgradeFile(gameId, seat))!);
            System.IO.File.WriteAllText(UpgradeFile(gameId, seat), JsonSerializer.Serialize(slots));
            if (chosen is null) await Do("保持牌组", () => B.KeepDeck(state!, me.Id));
            else
            {
                var deck = JsonSerializer.SerializeToElement(new { name = m.Name(me) + " · 战役", investigator_code = code, slots, sideSlots = new Dictionary<string, int>(), meta = sendMeta });
                System.IO.File.WriteAllText(CarryFile(gameId, seat), Math.Max(0, xp - cost).ToString());
                await Do("升级牌组（花费 " + cost + " 经验）", () => B.UpgradeDeck(state!, me.Id, deck));
            }
            await Advance();
        }, meta, CurrentSideSlots(seat));
        upgradeOverlay = OpenUpgradeEditor(req);
    }

    // ───────── the end of the campaign (after the last chapter): outcome, chapter results, campaign log, investigators
    static string LowerFirst(string k) => k.Length == 0 ? k : char.ToLowerInvariant(k[0]) + k[1..];

    // True when this game is a campaign whose last chapter has just ended.
    bool CampaignFinished(TableModel m)
    {
        // a campaign game is over only when the campaign is (between chapters the core asks ContinueCampaign instead):
        // after its last chapter, or earlier when a chapter ends it (the Dunwich Legacy's Where Doom Awaits, lost)
        var camp = J.Get(J.Get(m.Game, "mode"), "This");
        return camp.ValueKind == JsonValueKind.Object && m.IsOver;
    }

    // the official resolution text (html version locale): <campaign>.<scenario>.resolutions.<noResolution|resolutionN>.<title|body>
    string ResolutionText(string chapter, JsonElement res, string part)
    {
        string which = J.Tag(res) == "Resolution" && J.Get(res, "contents") is { ValueKind: JsonValueKind.Number } v ? "resolution" + v.GetInt32() : "noResolution";
        string key = Campaigns.ScenarioSection(chapter) + ".resolutions." + which + "." + part;
        string text = S.Locale.TryTemplate(key, out var t) ? S.Locale.Plain(t).Trim() : "";
        // a chapter without an official title for this outcome (疯狂列车 has no "noResolution" entry: a defeat reads
        // resolution 2): the plain official wording of the outcome
        if (text.Length == 0 && part == "title") text = which == "noResolution" ? "未达成结局" : "结局 " + which[10..];
        // an official "no resolution" that only says "go to resolution N" (Where Doom Awaits): the story is that resolution's
        var go = System.Text.RegularExpressions.Regex.Match(t ?? "", @"前往\s*_?结局\s*(\d)");
        if (part == "body" && which == "noResolution" && go.Success && text.Length < 80
            && S.Locale.TryTemplate(Campaigns.ScenarioSection(chapter) + ".resolutions.resolution" + go.Groups[1].Value + ".body", out var r)) text = S.Locale.Plain(r).Trim();
        // ... and the resolution its story went to, when the core records none (迷失时空 → 结局 4, Yunke 11-18)
        if (part == "title" && which == "noResolution" && readResolution.TryGetValue(CoreCatalog.Normalize(chapter), out var went)) text += " · 结局 " + went;
        return text;
    }

    // a campaign-log entry in its official wording (<campaign>.key.<entry>; a few sit at the locale root)
    string LogKey(CampaignDef cd, string key)
    {
        foreach (var k in new[] { cd.Key + ".key." + LowerFirst(key), "base.key." + LowerFirst(key), key })
            if (S.Locale.TryTemplate(k, out var t) && t.Length > 0) return S.Locale.Plain(t);
        return key;
    }

    void CampaignEndDialog()
    {
        var m = model!; var vp = GetViewportRect().Size;
        var camp = J.Get(J.Get(m.Game, "mode"), "This");
        var outcome = J.Get(m.Game, "outcome"); var cd = Camp;
        // the chapter the campaign ended in: the core's recorded ending, else the campaign's last chapter
        string endCode = J.String(J.Get(outcome, "ending"), "scenario") is { Length: > 0 } ec ? CoreCatalog.Normalize(ec) : cd.Last.Code;
        bool won = J.String(outcome, "result") == "Won";
        // after the end no chapter is active: the picture comes from the last chapter's own data
        var last = ScenarioData.Load(endCode) ?? scenario;
        var lastRes = J.Get(J.Get(J.Get(camp, "resolutions"), "c" + endCode), "contents");
        string resKey = lastRes.ValueKind == JsonValueKind.Number ? "R" + lastRes.GetInt32() : readResolution.TryGetValue(endCode, out var went) ? "R" + went : "";
        var root = Ui.Backdrop(vp, Ui.Bg(last is null ? "forest-gate" : resKey.Length > 0 ? last.Ending(resKey) : last.ExteriorFor(m.Game)), 0.5f);
        float w = Math.Min(1180, vp.X - 160);
        var col = new VBoxContainer { Position = new Vector2((vp.X - w) / 2, vp.Y * 0.04f), Size = new Vector2(w, vp.Y * 0.92f) }; col.AddThemeConstantOverride("separation", 12);
        root.AddChild(col);
        var kicker = Style.Text(cd.Name + " · 战役结束", 24, Style.Brass, Style.Kai); kicker.HorizontalAlignment = HorizontalAlignment.Center; col.AddChild(kicker);
        // title and story: the last chapter's official resolution
        var lastResolution = J.Get(J.Get(camp, "resolutions"), "c" + endCode);
        var title = Style.Title(ScenarioData.Load(endCode)?.Name + " · " + ResolutionText(endCode, lastResolution, "title"), 44); title.HorizontalAlignment = HorizontalAlignment.Center; col.AddChild(title);
        string story = ResolutionText(endCode, lastResolution, "body");
        if (story.Length > 0)
        {
            var body = CardReader.Rules(story.Split('\n').FirstOrDefault(l => l.Trim().Length > 0) ?? story, 21); body.CustomMinimumSize = new Vector2(w, 0); col.AddChild(body);
        }
        col.AddChild(new ColorRect { Color = new Color(Style.Brass, 0.3f), CustomMinimumSize = new Vector2(0, 1) });
        // each chapter and how it ended
        var resolutions = J.Get(camp, "resolutions");
        // (eight chapters in the Dunwich Legacy: they wrap)
        var chapters = new HFlowContainer { Alignment = FlowContainer.AlignmentMode.Center }; chapters.AddThemeConstantOverride("h_separation", 40); chapters.AddThemeConstantOverride("v_separation", 10); col.AddChild(chapters);
        foreach (var ch in cd.Chapters)
        {
            string code = ch.Code, name = cd.Label(code);
            var res = J.Get(resolutions, "c" + code);
            var c = new VBoxContainer(); c.AddThemeConstantOverride("separation", 2);
            c.AddChild(Style.Text(name, 22, Style.HoverText, Style.Kai));
            c.AddChild(Style.Text(res.ValueKind == JsonValueKind.Object ? ResolutionText(code, res, "title") : "未进行", 19, res.ValueKind == JsonValueKind.Object ? Style.Amber : Style.PaperDim, Style.Kai));
            chapters.AddChild(c);
        }
        // the campaign log, in words
        var log = J.Get(camp, "log");
        var lines = new List<string>();
        // entries the campaign later crossed out (log.crossedOut) are shown as crossed out, not as still true
        // (playtest 11-18: "阿米蒂奇被绑架" and "解救了阿米蒂奇" both appeared as current entries)
        var crossed = J.Items(J.Get(log, "crossedOut")).Select(k => J.String(k, "contents")).Where(k => k.Length > 0).ToHashSet();
        foreach (var k in J.Items(J.Get(log, "recorded")))
        {
            string key = J.String(k, "contents"); if (key.Length == 0) continue;
            lines.Add(LogKey(cd, key) + (crossed.Contains(key) ? "（已划去）" : ""));
        }
        foreach (var key in crossed.Where(k => !J.Items(J.Get(log, "recorded")).Any(r => J.String(r, "contents") == k)))
            lines.Add(LogKey(cd, key) + "（已划去）");
        foreach (var set in J.Items(J.Get(log, "recordedSets")))
        {
            var pair = set.ValueKind == JsonValueKind.Array && set.GetArrayLength() == 2 ? set : default;
            if (pair.ValueKind != JsonValueKind.Array) continue;
            // the set's name: a campaign key ({tag: TheDunwichLegacyKey, contents: …}) or a general one ({tag: DrivenInsaneInvestigators})
            string key = J.String(pair[0], "contents") is { Length: > 0 } kc ? kc : J.Tag(pair[0]);
            string label = LogKey(cd, key);
            // each entry: {recordType, recordVal: {tag: Recorded | CrossedOut, contents: card code}}
            var names = J.Items(pair[1]).Select(e => J.Get(e, "recordVal") is var rv && J.String(rv, "contents") is { Length: > 0 } cc
                ? S.Name(CoreCatalog.Normalize(cc)) + (J.Tag(rv) == "CrossedOut" ? "（已划去）" : "") : "").Where(x => x.Length > 0).Distinct().ToList();
            lines.Add(label + "：" + (names.Count > 0 ? string.Join("、", names) : "无"));
        }
        // counted entries ("2" broods escaped into the wild)
        foreach (var cnt in J.Items(J.Get(log, "recordedCounts")))
        {
            if (cnt.ValueKind != JsonValueKind.Array || cnt.GetArrayLength() != 2) continue;
            string key = J.String(cnt[0], "contents") is { Length: > 0 } kc ? kc : J.Tag(cnt[0]);
            lines.Add(LogKey(cd, key) + "：" + cnt[1].GetRawText());
        }
        if (lines.Count > 0)
        {
            col.AddChild(Style.Text("战役日志", 22, Style.Brass, Style.Kai));
            // the Dunwich Legacy's log runs to a dozen lines and more: two columns in a box that scrolls, so the
            // investigators and the buttons stay on screen (playtest 11-18: they were pushed below the bottom edge)
            var grid = new GridContainer { Columns = 2, SizeFlagsHorizontal = SizeFlags.ExpandFill }; grid.AddThemeConstantOverride("h_separation", 30); grid.AddThemeConstantOverride("v_separation", 4);
            foreach (var l in lines) { var t = Style.Text("· " + l, 19, Style.Paper, Style.Kai); t.AutowrapMode = TextServer.AutowrapMode.WordSmart; t.CustomMinimumSize = new Vector2(w / 2 - 30, 0); grid.AddChild(t); }
            var logScroll = new ScrollContainer { HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled, CustomMinimumSize = new Vector2(w, 0) };
            logScroll.AddChild(grid); col.AddChild(logScroll);
            FitScroll(logScroll, grid, w, vp.Y * 0.3f);
        }
        // each investigator: experience earned over the campaign, trauma, fate
        var people = new HBoxContainer { Alignment = BoxContainer.AlignmentMode.Center }; people.AddThemeConstantOverride("separation", 36); col.AddChild(people);
        foreach (var inv in m.Investigators)
        {
            // every column starts at the top, so the cards stand in one line whatever is written under each (Yunke 11-21)
            var card = new VBoxContainer { Alignment = BoxContainer.AlignmentMode.Begin, SizeFlagsVertical = SizeFlags.ShrinkBegin, CustomMinimumSize = new Vector2(180, 0) }; card.AddThemeConstantOverride("separation", 4);
            var holder = new CenterContainer { CustomMinimumSize = new Vector2(180, 154), MouseFilter = MouseFilterEnum.Ignore }; holder.AddChild(CardView.PortraitCard(inv.Code, 148)); card.AddChild(holder);
            // fates in the campaign log's official wording (已被击败的调查员 / 已发疯的调查员)
            bool killed = J.Get(inv.Raw, "killed").ValueKind == JsonValueKind.True, insane = J.Get(inv.Raw, "drivenInsane").ValueKind == JsonValueKind.True;
            string fate = killed ? "已被击败" : insane ? "已发疯" : "存活";
            var f = Style.Text(fate + " · 经验 " + J.Int(inv.Raw, "xp"), 19, killed || insane ? Style.Danger : Style.Good, Style.Kai); f.HorizontalAlignment = HorizontalAlignment.Center; card.AddChild(f);
            int phys = J.Int(inv.Raw, "physicalTrauma"), ment = J.Int(inv.Raw, "mentalTrauma");
            if (phys + ment > 0) { var tr = Style.Text("创伤 " + phys + " 肉体 · " + ment + " 精神", 16, Style.PaperDim, Style.Kai); tr.HorizontalAlignment = HorizontalAlignment.Center; card.AddChild(tr); }
            people.AddChild(card);
        }
        // each investigator's final deck goes to the workshop as a 通关卡组 (Yunke 11-19)
        SaveCompletedDecks();
        {
            var note = Style.Text("通关卡组已存入牌组工坊（筛选「通关卡组」），可以带入新的游戏。", 19, Style.Brass, Style.Kai); note.HorizontalAlignment = HorizontalAlignment.Center; col.AddChild(note);
        }
        col.AddChild(Ui.Spacer(0, 0, true));
        var go = new HBoxContainer { Alignment = BoxContainer.AlignmentMode.Center }; go.AddThemeConstantOverride("separation", 20); col.AddChild(go);
        Control scrim = null!;
        var home = Ui.Button("返回主菜单", () => { var mm = new MainMenu(); App.I.Go(mm); mm.Ready(); }, 26, true); home.CustomMinimumSize = new Vector2(280, 62); go.AddChild(home);
        var look = Ui.Button("查看牌桌", () => App.I.CloseModal(scrim), 22); look.CustomMinimumSize = new Vector2(200, 62); go.AddChild(look);
        scrim = App.I.Modal(root, false, 0f);
    }

    // the chapters the core has finished, by code
    // Between pages of the story (prologue, resolutions…): the screen goes to black and comes back (Yunke 11-19 r9 #8).
    // The black stays until the answer has been worked out and the next thing is on screen, then lifts.
    // a story page comes up out of black (the other half of BlackFade)
    static void FromBlack(Control root)
    {
        var black = new ColorRect { Color = Colors.Black, MouseFilter = MouseFilterEnum.Ignore, Size = root.Size };
        root.AddChild(black);
        var tw = black.CreateTween(); tw.TweenProperty(black, "modulate:a", 0f, 0.5f).SetTrans(Tween.TransitionType.Sine); tw.TweenCallback(Callable.From(() => black.QueueFree()));
    }
    bool blackFading;
    void BlackFade(Action then)
    {
        if (blackFading) return;
        if (S.Settings.ReducedMotion == true) { then(); return; }
        blackFading = true;
        var black = new ColorRect { Color = Colors.Black, MouseFilter = MouseFilterEnum.Stop, ZIndex = 200, Modulate = new Color(1, 1, 1, 0) };
        App.I.Overlay.AddChild(black); black.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        var tw = black.CreateTween();
        tw.TweenProperty(black, "modulate:a", 1f, 0.35f).SetTrans(Tween.TransitionType.Sine);
        // a void callback (an async lambda returned a Task, which Godot cannot pass back: it logged an error on every fade)
        async void Rest()
        {
            then();
            // wait for the core's answer and the new render (busy clears), at most a few seconds
            for (int i = 0; i < 60 && (busy || revealing || advancing > 0) && IsInstanceValid(this); i++) await ToSignal(GetTree().CreateTimer(0.05), SceneTreeTimer.SignalName.Timeout);
            await ToSignal(GetTree().CreateTimer(0.12), SceneTreeTimer.SignalName.Timeout);
            blackFading = false;
            if (!IsInstanceValid(black)) return;
            var t2 = black.CreateTween(); t2.TweenProperty(black, "modulate:a", 0f, 0.5f).SetTrans(Tween.TransitionType.Sine);
            t2.TweenCallback(Callable.From(() => { if (IsInstanceValid(black)) black.QueueFree(); }));
        }
        tw.TweenCallback(Callable.From(Rest));
    }

    static HashSet<string> Completed(TableModel m) => J.Items(J.Get(J.Get(J.Get(m.Game, "mode"), "This"), "completedSteps"))
        .Select(st => J.Get(st, "contents")).Where(c => c.ValueKind == JsonValueKind.String).Select(c => CoreCatalog.Normalize(c.GetString()!)).ToHashSet();

    Control? campaignScrim;
    void CampaignDialog()
    {
        var m = model!;
        if (campaignScrim is not null && IsInstanceValid(campaignScrim)) App.I.CloseModal(campaignScrim);
        var next = J.Get(ClientDisplay.Continuation(m.Game), "nextStep");
        var vp = GetViewportRect().Size;
        string nextCode = StepCode(next);
        var nextData = nextCode.Length > 0 ? ScenarioData.Load(nextCode) : null;
        // the next chapter's picture; before any chapter (the prologue) the campaign's first chapter's
        var root = Ui.Backdrop(vp, Ui.Bg("desk-table"), 0.45f);   // the desk, not the house (Yunke 11-19 r9 #8)
        float w = Math.Min(1100, vp.X - 200);
        var col = new VBoxContainer { Position = new Vector2((vp.X - w) / 2, vp.Y * 0.1f), Size = new Vector2(w, vp.Y * 0.8f) }; col.AddThemeConstantOverride("separation", 18);
        root.AddChild(col);
        var cd = Camp;
        var kicker = Style.Text("战役 · " + cd.Name, 24, Style.Brass, Style.Kai); kicker.HorizontalAlignment = HorizontalAlignment.Center; col.AddChild(kicker);
        var title = Style.Title(StepText(next), 44); title.HorizontalAlignment = HorizontalAlignment.Center; col.AddChild(title);
        col.AddChild(new ColorRect { Color = new Color(Style.Brass, 0.3f), CustomMinimumSize = new Vector2(0, 1) });
        // the chapters: done, next, still ahead
        var chapters = new HFlowContainer { Alignment = FlowContainer.AlignmentMode.Center }; chapters.AddThemeConstantOverride("h_separation", 36); chapters.AddThemeConstantOverride("v_separation", 8); col.AddChild(chapters);
        // done = the core lists the chapter among its completed steps (the Dunwich Legacy's first two may come in either order)
        var completed = Completed(m);
        foreach (var ch in cd.Chapters)
        {
            bool done = completed.Contains(ch.Code), upcoming = ch.Code == nextCode;
            var t = Style.Text((done ? "✓ " : upcoming ? "▶ " : "· ") + cd.Label(ch.Code), 22, done ? Style.Good : upcoming ? Style.Amber : Style.PaperDim, Style.Kai);
            chapters.AddChild(t);
        }
        // each investigator: experience available and trauma
        var people = new HBoxContainer { Alignment = BoxContainer.AlignmentMode.Center }; people.AddThemeConstantOverride("separation", 36); col.AddChild(people);
        foreach (var inv in m.Investigators)
        {
            // every column starts at the top, the portrait in a holder of one size, so the cards stand in one line (Yunke 11-20 #30)
            var card = new VBoxContainer { Alignment = BoxContainer.AlignmentMode.Begin, SizeFlagsVertical = SizeFlags.ShrinkBegin, CustomMinimumSize = new Vector2(220, 0) }; card.AddThemeConstantOverride("separation", 6);
            var cv = CardView.PortraitCard(inv.Code, 200);
            var holder = new CenterContainer { CustomMinimumSize = new Vector2(220, 206), MouseFilter = MouseFilterEnum.Ignore }; holder.AddChild(cv); card.AddChild(holder);
            int earned = J.Int(inv.Raw, "xp"), xp = Math.Max(0, earned - J.Int(inv.Raw, "spentXp")), phys = J.Int(inv.Raw, "physicalTrauma"), ment = J.Int(inv.Raw, "mentalTrauma");
            // a lost investigator is greyed and says so: the player will be asked to replace them (TableScreen.Replace)
            if (LostFate(inv) is { } lost)
            {
                cv.Modulate = new Color(0.55f, 0.55f, 0.55f);
                var lf = Style.Text(lost + " · 需更换调查员", 22, Style.Danger, Style.Kai); lf.HorizontalAlignment = HorizontalAlignment.Center; card.AddChild(lf);
            }
            // the experience earned in the campaign, what is left after upgrades in brackets (Yunke 11-20 #30)
            else { var x = Style.Text("经验 " + earned + (xp != earned ? "（剩余 " + xp + "）" : ""), 22, Style.Amber, Style.Kai); x.HorizontalAlignment = HorizontalAlignment.Center; card.AddChild(x); }
            if (phys + ment > 0) { var tr = Style.Text("创伤 " + phys + " 肉体 · " + ment + " 精神", 17, Style.Danger, Style.Kai); tr.HorizontalAlignment = HorizontalAlignment.Center; card.AddChild(tr); }
            // why: the resolution's own sentence that gave the trauma (Yunke 11-20 #27), as read on its page
            foreach (var why in TraumaReasons(m, inv))
            {
                var wl = Style.Text(why, 15, new Color(Style.PaperDim, 0.9f), Style.Kai); wl.HorizontalAlignment = HorizontalAlignment.Center;
                wl.AutowrapMode = TextServer.AutowrapMode.WordSmart; wl.CustomMinimumSize = new Vector2(220, 0); card.AddChild(wl);
            }
            people.AddChild(card);
        }
        col.AddChild(Ui.Spacer(0, 0, true));
        var go = new HBoxContainer { Alignment = BoxContainer.AlignmentMode.Center }; go.AddThemeConstantOverride("separation", 20); col.AddChild(go);
        // Between scenarios the player may spend experience first: the core then asks each investigator for an upgraded deck.
        // (between chapters the core holds no scenario, so "after a chapter" is: the next step is a chapter other than the first)
        bool afterScenario = J.Tag(next) is "ScenarioStep" or "ScenarioStepWithOptions" && nextCode.Length > 0 && completed.Overlaps(cd.Chapters.Select(c => c.Code));
        // The Scarlet Keys spends experience before embarking (the html ContinueCampaign: a CampaignSpecificStep that allows upgrades)
        if (J.Tag(next) == "CampaignSpecificStep" && J.Get(ClientDisplay.Continuation(m.Game), "canUpgradeDecks").ValueKind == JsonValueKind.True) afterScenario = true;
        // the core says whether this continuation lets decks be upgraded (canUpgradeDecks: false after The Circle Undone's
        // prologue, The Dream-Eaters' … — "This continuation does not allow upgrading" otherwise; later cycles 11-22)
        if (J.Get(ClientDisplay.Continuation(m.Game), "canUpgradeDecks").ValueKind == JsonValueKind.False) afterScenario = false;
        if (afterScenario)
        {
            // a lost (killed / insane) investigator's experience is not spendable; their player replaces them through this step
            int totalXp = m.Investigators.Where(i => LostFate(i) is null).Sum(i => Math.Max(0, J.Int(i.Raw, "xp") - J.Int(i.Raw, "spentXp")));
            bool anyLost = m.Investigators.Any(i => LostFate(i) is not null);
            // each investigator's experience is their own: the button says only what it does (Yunke 11-20 #27)
            string upText = anyLost ? (totalXp > 0 ? "升级牌组 / 更换调查员" : "更换调查员") : totalXp > 0 ? "升级牌组" : "调整牌组";
            // behind black from the campaign screen to the upgrade screen (Yunke 11-20: the table flashed in between)
            var up = Ui.Button(upText, () => BlackFade(async () =>
            {
                if (campaignScrim is not null) App.I.CloseModal(campaignScrim);
                replaceDeferred.Clear();   // asked for on purpose: a "not now" given earlier no longer holds
                await Do("开始升级", () => B.BeginUpgrade(state!));
                await Advance();
            }), 24, totalXp > 0 || anyLost);
            up.CustomMinimumSize = new Vector2(300, 62); go.AddChild(up);
        }
        var b = Ui.Button(next.ValueKind == JsonValueKind.Object ? "继续" : "返回主菜单", () =>
        {
            if (next.ValueKind != JsonValueKind.Object) { if (campaignScrim is not null) App.I.CloseModal(campaignScrim); var mm = new MainMenu(); App.I.Go(mm); mm.Ready(); return; }
            BlackFade(async () =>
            {
                if (campaignScrim is not null) App.I.CloseModal(campaignScrim);
                await Do("继续战役", () => B.Continue(state!));
                await Advance();
            });
        }, 26, true);
        b.CustomMinimumSize = new Vector2(300, 62); go.AddChild(b);
        campaignScrim = App.I.Modal(root, false, 0f);
    }
}
