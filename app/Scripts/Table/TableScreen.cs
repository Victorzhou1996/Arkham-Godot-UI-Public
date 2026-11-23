using Godot;
using Gathering;
using System.Text.Json;

namespace ArkhamUI;

// The game table. State comes only from the rules core; every answer is one of the core's own choices.
public partial class TableScreen : Control
{
    string? advancePhase;   // the phase at the previous advance (to tell the step that enters the mythos phase)
    Guid? pendingSeat;   // a seat to turn to after the current choice (the investigator picked to go first)
    readonly Guid gameId;
    TableState? state;
    TableModel? model;
    ScenarioData? scenario;
    bool busy;
    readonly Dictionary<string, Control> anchors = new();
    readonly List<string> autoLog = new();
    string lastTokens = "";
    readonly Dictionary<string, (int Dmg, int Hor, int Res, int Clue)> lastStats = new();
    Services S => App.I.S!;
    // the rules core this table plays on: this machine's, or the host's in an online game this player joined (OnlineGame)
    Gathering.IGameBackend B => OnlineGame.Backend ?? S.Backend;
    readonly Guid? firstSeat;
    public static TableScreen? Current { get; private set; }
    public Guid QaGameId => gameId;
    bool remoteDirty;   // the host moved on while this table was busy: read it again when free
    Label waitLabel = null!;

    // layers
    TextureRect background = null!;
    Control layer = null!;          // rebuilt every render
    ArrowLayer arrows = null!;
    Label busyLabel = null!;

    public TableScreen(Guid gameId, Guid? seat = null) { this.gameId = gameId; firstSeat = seat; }

    public override void _Ready()
    {
        MouseFilter = MouseFilterEnum.Stop;
        var ink = new ColorRect { Color = Style.Ink, MouseFilter = MouseFilterEnum.Ignore }; AddChild(ink); ink.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        background = Ui.Image(null, Vector2.Zero, true); AddChild(background); background.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        // the painting runs a quarter past the right and bottom edges: the photographs and the magnifier in its lower-right
        // corner were too big and took table space (Yunke 11-19 r11); the lamp in the upper left stays
        background.AnchorRight = 1.25f; background.AnchorBottom = 1.25f;
        // The table is Yunke's desk painting (11-18 r8), shown as it is, only a little darker so the cards on it read clearly
        // (the old blurred-scene backdrop is gone with the painted maps).
        background.Material = new ShaderMaterial { Shader = new Shader { Code = @"shader_type canvas_item;
uniform float dim = 0.78;
void fragment(){ vec4 c = texture(TEXTURE, UV); COLOR = vec4(c.rgb * dim, 1.0); }" } };
        var vignette = new TextureRect { Texture = Vignette(), ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize, StretchMode = TextureRect.StretchModeEnum.Scale, MouseFilter = MouseFilterEnum.Ignore };
        AddChild(vignette); vignette.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        layer = new Control { MouseFilter = MouseFilterEnum.Ignore }; AddChild(layer); layer.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        arrows = new ArrowLayer(); AddChild(arrows); arrows.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        busyLabel = Style.Text("", 22, Style.PaperDim); AddChild(busyLabel); busyLabel.Position = new Vector2(30, 1040);
        // online: whose turn it is, centred under the act and agenda bars
        waitLabel = Style.Text("", 26, Style.Brass, Style.Kai); waitLabel.HorizontalAlignment = HorizontalAlignment.Center; waitLabel.MouseFilter = MouseFilterEnum.Ignore;
        waitLabel.AddThemeColorOverride("font_outline_color", new Color(0, 0, 0, 0.85f)); waitLabel.AddThemeConstantOverride("outline_size", 8);
        AddChild(waitLabel); waitLabel.SetAnchorsAndOffsetsPreset(LayoutPreset.TopWide); waitLabel.OffsetTop = 104; waitLabel.OffsetBottom = 144;
        RegisterQa();
        Current = this;
        OnlineGame.Changed += OnRemoteChanged;
        OnlineGame.Ended += OnOnlineEnded;
        TreeExiting += () =>
        {
            OnlineGame.Changed -= OnRemoteChanged; OnlineGame.Ended -= OnOnlineEnded; if (Current == this) Current = null;
            CardReader.Unrevealed.Clear();   // off the table, cards read normally again
            // left the table for good (not for another table of the same game): the online game ends here
            var gid = gameId;
            Callable.From(() => { if (Current is null || Current.QaGameId != gid) OnlineGame.Leave(gid); }).CallDeferred();
        };
        // Compile the story-screen blur once, invisibly, so the first interlude doesn't stall the game.
        var warm = new TextureRect { Texture = Ui.Bg("exterior"), ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize, StretchMode = TextureRect.StretchModeEnum.Scale, Size = new Vector2(4, 4), Position = new Vector2(-8, -8), MouseFilter = MouseFilterEnum.Ignore, Material = Ui.Blur() };
        AddChild(warm); GetTree().CreateTimer(1.5).Timeout += () => { if (IsInstanceValid(warm)) warm.QueueFree(); };
        _ = Load(firstSeat);
    }

    // ---- online (OnlineGame) ------------------------------------------------------------------------------------------

    // a new step at the host: read it again (later, if this table is in the middle of something)
    void OnRemoteChanged(long seq)
    {
        if (state is not null && state.Sequence == seq) return;
        if (busy || advancing > 0) { remoteDirty = true; return; }
        _ = RemoteRefresh();
    }

    async Task RemoteRefresh()
    {
        remoteDirty = false;
        if (state is null) return;
        var seat = state.Player;
        await Busy("同步中…", async () => { state = await B.Open(gameId, seat); });
        await Advance();
    }

    void OnOnlineEnded(string why)
    {
        App.I.Toast(why, true, 5);
        GD.Print("ONLINE_ENDED " + why);
        var mm = new MainMenu(); App.I.Go(mm);
        if (S.HasCore) mm.Ready(); else App.CoreMissing?.Invoke(mm);   // a phone without the rules core: its own menu
    }

    // online: this machine does not answer the seat's question — say whose turn it is
    bool WaitingForOther()
    {
        if (!OnlineGame.Active || state is null || model is null) { waitLabel.Text = ""; return false; }
        if (state.Question.ValueKind != JsonValueKind.Object)
        {
            // nothing asked here: name whoever is asked elsewhere
            var other = state.Seats.FirstOrDefault(x => x.HasQuestion && x.Player != state.Player);
            waitLabel.Text = other is null ? "" : "等待 " + OnlineGame.SeatName(other.Player) + " 操作…";
            return other is not null && !OnlineGame.Mine(other.Player);
        }
        if (OnlineGame.Answers(state.Player, model.QuestionTag)) { waitLabel.Text = ""; return false; }
        waitLabel.Text = OnlineGame.IsGuest && OnlineGame.HostSteps.Contains(model.QuestionTag) ? "等待主持人处理…" : "等待 " + OnlineGame.SeatName(state.Player) + " 操作…";
        return true;
    }

    static GradientTexture2D Vignette()
    {
        var g = new Gradient(); g.SetColor(0, new Color(0, 0, 0, 0)); g.SetColor(1, new Color(0, 0, 0, 0.62f));
        g.AddPoint(0.55f, new Color(0, 0, 0, 0.12f));
        return new GradientTexture2D { Gradient = g, Fill = GradientTexture2D.FillEnum.Radial, FillFrom = new Vector2(0.5f, 0.45f), FillTo = new Vector2(1.05f, 1.05f), Width = 512, Height = 288 };
    }

    async Task Load(Guid? player)
    {
        await Busy("正在读取牌桌…", async () =>
        {
            state = await B.Open(gameId, player);
            await PreloadPictures(state, withFixed: true);
        });
        await Advance();
    }

    // Every card on the table (any "cardCode" in the view: places, enemies, assets, hands, acts, agendas …) — and on entering,
    // the table's fixed pictures — loaded on worker threads before the render that shows them, so a render does not stop to
    // read them on the main thread (perf 11-21: entering the table froze 1.1 s; a new chapter's places and encounter cards
    // did the same at the chapter's first render). What is already loaded or was tried before is skipped at once.
    async Task PreloadPictures(TableState st, bool withFixed = false)
    {
        var w = System.Diagnostics.Stopwatch.StartNew();
        // only what can be on screen (Yunke 11-23: "卡死甚至闪退…主要发生在最后一个人换完牌的时候，也就是初次绘制桌面牌局的时候"):
        // the decks, the encounter deck, the set-aside cards and the view's card registry are not drawn, and preloading them took
        // every deck card's illustration (1448 x 1086, about 8 MB in memory each): textures went from 78 MB to 411 MB at the
        // first table of a two-player game (evidence/p0/mem-b.log). A card that comes into view is loaded by the render.
        var codes = VisibleCodes(st.Game).Distinct();
        int loaded = await Ui.Preload((withFixed ? Ui.TablePaths() : Enumerable.Empty<string>()).Concat(codes.SelectMany(Ui.CardPaths)));
        if (loaded > 0) Gathering.PrototypeBackend.Perf("preload ms=" + w.Elapsed.TotalMilliseconds.ToString("F0") + " textures=" + loaded);
    }

    static readonly HashSet<string> HiddenKeys = new() { "cards", "decks", "deck", "encounterDeck", "setAsideCards", "storyCards", "bonded", "sideDeck", "cardsUnderneath", "underneath" };
    static IEnumerable<string> VisibleCodes(JsonElement e)
    {
        switch (e.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var p in e.EnumerateObject())
                {
                    if (p.Name == "cardCode" && p.Value.ValueKind == JsonValueKind.String) { yield return p.Value.GetString()!; continue; }
                    if (HiddenKeys.Contains(p.Name)) continue;
                    foreach (var c in VisibleCodes(p.Value)) yield return c;
                }
                break;
            case JsonValueKind.Array:
                foreach (var x in e.EnumerateArray()) foreach (var c in VisibleCodes(x)) yield return c;
                break;
        }
    }

    async Task Busy(string text, Func<Task> work)
    {
        busy = true; busyLabel.Text = text;
        // a new state may bring new cards (a new chapter's places, acts and agendas, a drawn encounter card): their pictures
        // are loaded on worker threads while still busy, before any render shows them (perf 11-21; every render path — the
        // act / agenda flips, the story pages, the chapter's first table — comes after the backend call that made the state)
        long? before = state?.Sequence; Guid? seatBefore = state?.Player;
        try
        {
            await work();
            if (state is not null && state.Sequence != before && OnlineGame.IsHost) OnlineGame.HostChanged(state.Sequence);
            if (state is not null && (state.Sequence != before || state.Player != seatBefore)) await PreloadPictures(state);
        }
        catch (Exception e) when (IsStale(e) && state is not null)
        {
            // the save moved on under this answer (another answer for the same step got there first: a click while the table
            // was answering by itself, Yunke 11-22 "Local state changed; reload the latest committed game"): nothing was done
            // and nothing is lost — take the latest state and carry on from there, without a toast
            GD.Print("STALE_RELOAD " + state.Sequence + " " + e.Message);
            try { state = await B.Open(gameId, state.Player); }
            catch (Exception e2) { App.I.Toast("操作未完成：" + e2.Message, true, 5); GD.PushError(e2.ToString()); }
        }
        catch (Exception e) { App.I.Toast("操作未完成：" + e.Message, true, 5); GD.PushError(e.ToString()); }
        finally
        {
            busy = false; busyLabel.Text = "";
            if (remoteDirty && advancing == 0) Callable.From(() => { if (!busy && advancing == 0 && remoteDirty) _ = RemoteRefresh(); }).CallDeferred();
        }
    }

    static bool IsStale(Exception e) => e.GetType().Name.Contains("StaleLocalState") || e.Message.Contains("Local state changed");

    // After every state change: switch to the seat being asked, answer setup and decision-free steps, then render.
    // QA: the autoplay waits while the table is still settling a state (an undo's Advance runs on after its Busy ends; answering
    // in between sent a stale sequence to the core — StaleLocalState, run r11play)
    internal int advancing;
    async Task Advance()
    {
        advancing++;
        try { await AdvanceInner(); }
        finally { advancing--; }
        if (advancing == 0 && remoteDirty && !busy) await RemoteRefresh();
    }

    async Task AdvanceInner()
    {
        int legacyTries = 0;
        var emptySeats = new HashSet<Guid>();   // seats seen with a question that offers nothing (switch away from them once)
        bool online = OnlineGame.Active;
        var peeked = new HashSet<Guid>();   // online host: other players' seats looked at for a step the host answers
        bool returned = false;
        for (int guard = 0; guard < 60 && state is not null; guard++)
        {
            // the investigator just picked to go first: turn to them once they are asked
            if (pendingSeat is { } ps)
            {
                pendingSeat = null;
                if (ps != state.Player && state.Seats.Any(x => x.Player == ps && x.HasQuestion))
                {
                    pendingLocate = true;
                    if (App.I.QaRunning) GD.Print("QA_LEAD_VIEW " + ps);
                    await Busy("切换调查员…", async () => { state = await B.Open(gameId, ps); });
                    continue;
                }
            }
            // switch to the seat that has to answer: when this seat has no question, or has one with nothing to choose while
            // another seat is asked too (a story page where only the lead investigator decides: the other seat gets the same
            // page with no buttons — 无人知晓's calm / warn choice left the table stuck on it, run pd7)
            bool nothingHere = state.Question.ValueKind == JsonValueKind.Object && new TableModel(state, S).Question.Choices.Count(c => c.Enabled) == 0;
            if (nothingHere) emptySeats.Add(state.Player);
            if (state.Question.ValueKind != JsonValueKind.Object || nothingHere)
            {
                // a seat of this player's own before one everyone controls (OnlineGame.Shared)
                var byOwn = state.Seats.OrderBy(s => OnlineGame.Shared(s.Player) ? 1 : 0).ToArray();
                var asked = byOwn.FirstOrDefault(s => s.HasQuestion && s.Player != state.Player && !emptySeats.Contains(s.Player) && (!online || OnlineGame.Mine(s.Player)))
                    ?? (nothingHere ? null : byOwn.FirstOrDefault(s => s.HasQuestion && (!online || OnlineGame.Mine(s.Player))));
                if (asked is not null && asked.Player != state.Player)
                {
                    var prev = state.Player; pendingLocate = true;   // the new seat has to answer: show where
                    await Busy("切换调查员…", async () => { state = await B.Open(gameId, asked.Player); });
                    continue;
                }
            }
            // online: looking at a seat this machine does not answer while one of its own is asked: turn to its own; the host
            // also looks at the others' seats once for a step it answers for everyone (OnlineGame.HostSteps)
            if (online && (state.Question.ValueKind != JsonValueKind.Object || !OnlineGame.Answers(state.Player, new TableModel(state, S).QuestionTag)))
            {
                var own = state.Seats.OrderBy(s => OnlineGame.Shared(s.Player) ? 1 : 0).FirstOrDefault(s => s.HasQuestion && s.Player != state.Player && OnlineGame.Mine(s.Player) && !emptySeats.Contains(s.Player));
                var other = own is null && OnlineGame.IsHost ? state.Seats.FirstOrDefault(s => s.HasQuestion && s.Player != state.Player && !peeked.Contains(s.Player)) : null;
                peeked.Add(state.Player);
                // looking at another player's seat whose step is theirs (the host peeked, or the core turned to the seat asked
                // next): back to this player's own investigator, which then says whose turn it is
                var back = own is null && other is null && !OnlineGame.Mine(state.Player) && !returned
                    ? state.Seats.FirstOrDefault(s => OnlineGame.Mine(s.Player)) : null;
                if ((own ?? other ?? back) is { } go)
                {
                    peeked.Add(go.Player); if (back is not null) returned = true;
                    await Busy("切换调查员…", async () => { state = await B.Open(gameId, go.Player); });
                    continue;
                }
            }
            model = new TableModel(state, S);
            // 新手教程: the arranged opening (hands, deck order, the first chaos tokens) through the core's debug operations
            if (await TutorialDirect()) continue;
            // In a campaign the scenario changes between chapters: reload its presentation data whenever its code changes.
            // Before the online wait below (Yunke 11-23: a guest that joined while another player was acting saw no map until
            // its own turn — the wait broke out before the scenario, and with it the map, was loaded).
            string scenarioCode = J.String(model.Scenario, "id").Length > 0 ? CoreCatalog.Normalize(J.String(model.Scenario, "id")) : "";
            if (scenarioCode.Length > 0 && (scenario is null || J.String(scenario.Raw, "code") != scenarioCode)) scenario = ScenarioData.Load(scenarioCode) ?? scenario;
            scenario ??= ScenarioData.Load(Camp.Chapters[0].Code);   // before any chapter (a prologue): the campaign's first chapter
            // online: another player answers this seat — show the table and wait for the host's next step
            if (WaitingForOther()) break;
            // in the order they happened at the table (r9 #16, #20): drawn encounter cards, weaknesses drawn, doom placed, then
            // the act / agenda advance
            var flips = PendingFlips();
            // The mythos phase places its doom (and advances the agenda if that fills it) before anyone draws an encounter card
            // (rules core, Game/Runner.hs: PlaceDoomOnAgendaStep → CheckDoomThresholdStep → EachInvestigatorDrawsEncounterCardStep).
            // So on the step that enters the mythos phase the doom flies, and the agenda turns, before the draws are shown
            // (Yunke 11-20: the doom flew up only after the first investigator had drawn). Doom placed by a drawn card (远古恶魔)
            // still flies after that card is shown.
            bool mythosEntered = model.Phase == "MythosPhase" && advancePhase is not null && advancePhase != "MythosPhase";
            advancePhase = model.Phase;
            bool agendaTurned = flips.Any(f => f.Kind == "agenda" && f.Old is not null);
            if (mythosEntered)
            {
                await DoomFlights(agendaTurned);
                await PlayFlips(flips.Where(f => f.Kind == "agenda").ToList());
                flips = flips.Where(f => f.Kind != "agenda").ToList();
            }
            // an act that advanced is told before what it put into play (Yunke 11-21: its back says "put the Ghoul Minion into
            // play" — the enemy was shown first, the act's story after it)
            if (!mythosEntered && flips.Any(f => f.Kind == "act"))
            {
                // a new agenda arriving with it (the scenario's opening: agenda 1a and act 1a) is told first (Yunke 11-22: the
                // agenda's story before the act's, as the html table lays them out)
                var opening = flips.Where(f => f.Kind == "agenda" && f.Old is null).ToList();
                if (opening.Count > 0) { await PlayFlips(opening); flips = flips.Except(opening).ToList(); }
                await PlayFlips(flips.Where(f => f.Kind == "act").ToList());
                flips = flips.Where(f => f.Kind != "act").ToList();
            }
            await CheckEncounterDraws();
            await CheckWeaknessDraws();
            await CheckForcedWeaknesses();
            if (!mythosEntered) await DoomFlights(agendaTurned);
            await PlayFlips(flips);
            var tag = model.QuestionTag;
            // Settings forms the core asks before play. Their rows are shown to the player when there are any; with no rows
            // (a fresh scenario, or the campaign's default log) there is nothing to decide and the defaults are sent.
            if (tag is "PickScenarioSettings" or "PickCampaignSettings")
            {
                // QA audit only: a campaign started at a later chapter (prepare-start-at) asks for the campaign log so far, a
                // form the table does not offer (players always start from the beginning). The audit answers it with an empty
                // log — a valid campaign state in which nothing was recorded.
                if (tag == "PickScenarioSettings" && App.I.QaRunning && J.Get(J.Get(state.View, "settingsSchema"), "supported").ValueKind != JsonValueKind.True)
                {
                    var raw = J.Get(state.View, "settingsSchema").GetRawText(); GD.Print("QA_SETTINGS_SCHEMA " + (raw.Length > 3000 ? raw[..3000] : raw));
                    var empty = JsonSerializer.SerializeToElement(new { tag = "StandaloneSettingsAnswer", contents = Array.Empty<object>() });
                    await Do("准备冒险", () => B.Answer(state, empty)); continue;
                }
                if (tag == "PickCampaignSettings" && App.I.QaRunning && J.Get(J.Get(state.View, "settingsSchema"), "supported").ValueKind != JsonValueKind.True)
                {
                    var empty = JsonSerializer.SerializeToElement(new { tag = "CampaignSettingsAnswer", contents = new { keys = Array.Empty<object>(), counts = Array.Empty<object>(), sets = Array.Empty<object>(), options = Array.Empty<object>() } });
                    GD.Print("QA_EMPTY_CAMPAIGN_LOG");
                    await Do("准备冒险", () => B.Answer(state, empty)); continue;
                }
                await Do("准备冒险", () => B.DefaultSettings(state)); continue;
            }
            if (tag == "ChooseDeck")
            {
                // online: the deck the seat's player picked in the room (OnlineGame), sent by the host
                if (OnlineGame.SeatDeck(state.Player) is { } od) { await Do("带入牌组", () => B.SelectDeck(state, od)); continue; }
                var keys = SetupStore.Load(gameId); int seat = Array.FindIndex(state.Seats, s => s.Player == state.Player);
                if (App.I.QaRunning) GD.Print("QA_CHOOSEDECK seat=" + seat + " keys=" + string.Join(",", keys) + " has=" + (seat >= 0 && seat < keys.Length && S.Decks.Has(keys[seat])) + " decks=" + S.Decks.All.Count());
                if (seat >= 0 && seat < keys.Length && S.Decks.Has(keys[seat])) { var deck = S.Decks.Get(keys[seat]); var before = state.Sequence; await Do("带入牌组", () => B.SelectDeck(state, deck)); if (state.Sequence != before) StartCarry(gameId, seat, deck); continue; }
                Render(); ChooseDeckDialog(); return;
            }
            if (tag is "ContinueCampaign")
            {
                // at the campaign's very start (nothing played yet) there is nothing to show: no screen, straight on (Yunke 11-19 r9 #8)
                if (!Completed(model).Overlaps(Camp.Chapters.Select(c => c.Code))) { await Do("继续战役", () => B.Continue(state!)); continue; }
                Render(); CampaignDialog(); return;
            }   // between chapters: the campaign screen
            if (tag is "ChooseUpgradeDeck")
            {
                // a lost investigator the player chose not to replace yet: the core's repeat asks before the next chapter (TableScreen.Replace)
                if (ReplaceDeferred(model)) { var lostId = model.Me!.Id; await Do("暂不替换", () => B.KeepDeck(state!, lostId), quiet: true); continue; }
                Render(); UpgradeDialog(); return;    // spend experience: the workshop in upgrade mode (or replace a lost investigator)
            }
            if (model.IsOver && tag == "") { Render(); return; }
            // a card dropped on the test panel in the window before the commit step: commit it now that the step has come
            if (pendingCommit is { } pc && model.InSkillTest && J.String(model.SkillTest, "step") != "SkillTestFastWindow1")
            {
                pendingCommit = null;
                var put = model.Question.Choices.FirstOrDefault(c => c.Enabled && c.Title.StartsWith("投入") && model.Anchor(c) == "hand:" + pc);
                if (put is not null)
                {
                    if (App.I.QaRunning) GD.Print("QA_PRECOMMIT_DONE " + pc);
                    await Do(put.Title, () => B.Choice(state!, put.Index));
                    continue;
                }
                App.I.Toast("这张牌不能投入这次检定", true);
            }
            // an older save whose trigger modes are still held by the rules core: copy them to the table, put the core back to
            // 总是触发 (TriggerModes: the core's own skip lost 爵士's blanking — Yunke 11-22, 教师办公室)
            // (checked at every step: an undo can go back to a state from before the move)
            if (legacyTries < 3)
            {
                var legacy = TriggerModes.Legacy(model);
                if (legacy.Count > 0)
                {
                    legacyTries++;
                    foreach (var (inv, card, index, mode) in legacy) TriggerModes.Adopt(gameId, inv, card, index, mode);
                    if (App.I.QaRunning) GD.Print("QA_TRIGGER_MODES_MOVED " + string.Join(",", legacy.Select(x => x.Card + "#" + x.Index + "=" + x.Mode)));
                    await Busy("整理触发模式…", async () => { foreach (var (inv, card, index, _) in legacy) await ResetCoreTriggerMode(inv, card, index); });
                    continue;
                }
            }
            // bug #10: this player's seats follow 检定中也可使用闪电能力 (the core's ignoreUnrelatedSkillTestTriggers per investigator)
            if (fastTries < 3 && FastSettingDiffers() is { Count: > 0 } wrong)
            {
                fastTries++;
                await Busy("应用设置…", async () => { foreach (var inv in wrong) await SetCoreFastInTests(inv); });
                if (App.I.QaRunning) GD.Print("QA_FAST_IN_TESTS " + S.Settings.FastInSkillTests + " " + string.Join(",", wrong.Select(x => x.Code)));
                continue;
            }
            var auto = AutoPolicy.Decide(model, S.Settings.AutoSkip);
            if (auto is not null)
            {
                // the commit step with nothing that can be committed (no card with this test's icon or a wild one): the core offers
                // only "开始检定" and the table starts it — say why, so the test does not seem to skip the commit step (Yunke 11-20)
                if (model.InSkillTest && model.Question.Choices.FirstOrDefault(c => c.Index == auto.Value.Index) is { Tag: "StartSkillTestButton" })
                {
                    string skillTag = J.String(J.Get(model.SkillTest, "type"), "contents"); if (skillTag.Length == 0) skillTag = J.Items(J.Get(model.SkillTest, "skills")).Select(x => x.GetString() ?? "").FirstOrDefault() ?? "";
                    string skill = skillTag.Replace("Skill", "") switch { "Willpower" => "意志", "Intellect" => "智力", "Combat" => "战力", "Agility" => "敏捷", _ => "" };
                    App.I.Toast(skill.Length > 0 ? "没有可以投入的牌（需要" + skill + "或万能图标），直接开始检定" : "没有可以投入的牌，直接开始检定", false, 3.5);
                }
                // an attack that resolves by itself is still shown: the attacker glows and wiggles for a moment first
                // (the table is busy meanwhile: nothing else may answer the core during the pause, or its state goes stale)
                if (S.Settings.ReducedMotion != true && Attackers(model).Count > 0)
                {
                    Render();
                    QaMidShot(0.5);   // QA only: the attacker's glow, mid-pause
                    busy = true;
                    try { await ToSignal(GetTree().CreateTimer(1.0), SceneTreeTimer.SignalName.Timeout); }
                    finally { busy = false; }
                }
                autoLog.Add(auto.Value.Why); if (autoLog.Count > 6) autoLog.RemoveAt(0);
                if (App.I.QaRunning) GD.Print("QA_AUTO " + state.Sequence + " " + tag + " " + auto.Value.Why + " :: " + (state.Question.GetRawText() is var qraw && qraw.Length > 1500 ? qraw[..1500] : qraw));
                if (tag is "ChooseOneAtATime" or "ChooseOneAtATimeWithAuto" || auto.Value.Why.Contains("强制")) ForcedNotice(System.Text.RegularExpressions.Regex.Replace(System.Text.RegularExpressions.Regex.Replace(auto.Value.Why, @"（[^）]*）", ""), @"^(强制效果\s*·\s*)+", "强制效果 · "));
                if (model.Question.Choices.FirstOrDefault(c => c.Index == auto.Value.Index) is { } autoPick) AssignHint(autoPick, null);
                var idx = auto.Value.Index; await Do("自动：" + auto.Value.Why, () => B.Choice(state, idx), quiet: true); continue;
            }
            // QA only: a lone choice that still waits for the player, with its raw question (to see why it was not skipped)
            if (App.I.QaRunning && model.Question.Choices.Count(c => c.Enabled) <= 2 && model.Question.Choices.Any(c => c.Title.Contains("发现") || c.Tag == "TargetLabel"))
                GD.Print("QA_WAIT " + tag + " " + (state.Question.GetRawText() is var raw && raw.Length > 3000 ? raw[..3000] : raw));
            break;
        }
        Render();
        // The drawn token and the result stay on screen until the player clicks anywhere (Yunke 11-23: "抽出混乱标记后的窗口不要
        // 立即消失，点击其他地方后再消失"); QA runs keep the old short beat
        applyWait = null;
        if (model is not null)
        {
            var only = model.Question.Choices.Where(c => c.Enabled).ToArray();
            if (only.Length == 1 && only[0].Tag == "SkillTestApplyResultsButton")
            {
                var seq = state!.Sequence; var c = only[0];
                if (App.I.QaRunning && S.Settings.AutoSkip && !OS.GetCmdlineUserArgs().Contains("--qa-linger"))
                    GetTree().CreateTimer(S.Settings.ReducedMotion ? 0.6 : 1.8).Timeout += () => { if (!busy && state is not null && state.Sequence == seq && IsInsideTree()) _ = Choose2Async(c); };
                else applyWait = (seq, c);
            }
        }
    }

    async Task Do(string what, Func<Task<TableState>> work, bool quiet = false)
    {
        // online: never answer a seat (or a step) another machine answers
        if (OnlineGame.Active && state is not null && model is not null && state.Question.ValueKind == JsonValueKind.Object && !OnlineGame.Answers(state.Player, model.QuestionTag))
        { App.I.Toast(OnlineGame.IsGuest && OnlineGame.HostSteps.Contains(model.QuestionTag) ? "这一步由主持人处理" : "现在轮到 " + OnlineGame.SeatName(state.Player) + " 操作", false, 2.5); return; }
        await Busy(what + "…", async () => { state = await work(); });
    }

    public async void Choose(ChoicePresentation c)
    {
        if (busy || state is null) return;
        // a click that lands after the question it was for was already answered (a page's fade-out finishing after the table
        // moved on): it is not sent as an answer to whatever is asked now (Yunke 11-22, "Local state changed …")
        if (model is not null && !model.Question.Choices.Any(x => x.Index == c.Index && x.Title == c.Title && x.Raw.GetRawText() == c.Raw.GetRawText()))
        { GD.Print("STALE_CLICK " + c.Title); return; }
        if (TutorialBlocks(c) is { } tutWhy) { App.I.Toast(tutWhy); return; }   // 新手教程's fixed course
        if (c.Tag == "EndTurnButton" && S.Settings.ConfirmEndTurn && model is not null && model.Me is not null && model.Actions(model.Me) > 0)
        {
            Confirm("还剩 " + model.Actions(model.Me) + " 个行动，确定结束回合？", () => Choose2(c)); return;
        }
        await Choose2Async(c);
    }
    async void Choose2(ChoicePresentation c) => await Choose2Async(c);
    async Task Choose2Async(ChoicePresentation c)
    {
        App.I.CloseAllModals(); DismissCoach();
        NoteDeckbuilding(c);   // 鸦翎羽毛笔 / 毛皮货 at the game's end: kept for the next upgrade (TableScreen.UpgradeNotes)
        AssignHint(c, assignFrom); assignFrom = null;   // a point of damage / horror placed: say where it went (r10)
        NoteHandPlay(c);   // a played hand card flies to where it lands after the render
        if (c.Tag == "TargetLabel" && J.Tag(J.Get(c.Raw, "target")) == "ChaosTokenTarget") tokenSubstitute = J.String(J.Get(J.Get(c.Raw, "target"), "contents"), "chaosTokenFace");
        // any choice whose effect resolves the revealed token as another face (百赌百胜's skull: pay to treat it as 0)
        else if (model?.InSkillTest == true && System.Text.RegularExpressions.Regex.Match(c.Raw.GetRawText(), @"""contents"":\[""(\w+)""\],""tag"":""ChaosTokenFaceModifier""|""tag"":""ChaosTokenFaceModifier"",""contents"":\[""(\w+)""\]") is { Success: true } fm)
            tokenSubstitute = fm.Groups[1].Success ? fm.Groups[1].Value : fm.Groups[2].Value;
        if (DedicatedKey(c) == "encdeck" && model?.Me is { } drawer) { lastDrawer = model.Name(drawer); NoteDeckDraw(); }   // the reveal names who drew, and starts from the deck
        // picking who goes first this round ("选择调查阶段开始的调查员"): the table then turns to that investigator (Yunke 11-20)
        if (model is { } pm && pm.Question.Title.Contains("开始的调查员") && pm.Anchor(c) is { } pa && pa.StartsWith("inv:")
            && pm.Investigators.FirstOrDefault(i => "inv:" + i.Id == pa) is { } lead && Guid.TryParse(J.String(lead.Raw, "playerId"), out var leadPlayer))
            pendingSeat = leadPlayer;
        var actWatch = System.Diagnostics.Stopwatch.StartNew();
        var seatBefore = state!.Player;
        // the backend reads the state back from the seat that has to answer next (followAsked), so Advance need not read it a
        // second time to switch seats; the seat change still points at where the new seat acts, as Advance's switch does
        await Do(c.Title, () => B.Choice(state!, c.Index, followAsked: true));
        if (state is not null && state.Player != seatBefore) pendingLocate = true;
        double answered = actWatch.Elapsed.TotalMilliseconds;
        await Advance();
        // from the click to the table drawn again (answer = the core's answer and reading the state back; the rest is
        // switching seats, automatic steps, waiting for flips and banners, and the render)
        Gathering.PrototypeBackend.Perf($"act ms={actWatch.Elapsed.TotalMilliseconds:F0} answer={answered:F0} after={actWatch.Elapsed.TotalMilliseconds - answered:F0} tag={c.Tag}");
    }

    public async void ViewSeat(Guid player)
    {
        if (busy || state is null || player == state.Player) return;
        await Busy("切换视角…", async () => { state = await B.Open(gameId, player); });
        model = new TableModel(state!, S); Render();
    }

    void Confirm(string text, Action yes)
    {
        var col = new VBoxContainer { CustomMinimumSize = new Vector2(620, 0) }; col.AddThemeConstantOverride("separation", 22);
        var l = Style.Text(text, 26, Style.Paper, Style.Kai); l.AutowrapMode = TextServer.AutowrapMode.WordSmart; col.AddChild(l);
        var row = new HBoxContainer { Alignment = BoxContainer.AlignmentMode.End }; col.AddChild(row);
        Control scrim = null!;
        row.AddChild(Ui.Button("取消", () => App.I.CloseModal(scrim), 22));
        row.AddChild(Ui.Button("确定", () => { App.I.CloseModal(scrim); yes(); }, 22, true));
        scrim = App.I.Modal(Ui.Panel(col, 36));
    }

    void RegisterQa()
    {
        RegisterHistoryQa();
        QaScript.Register("choose", a =>
        {
            if (model is null) return;
            var c = int.TryParse(a, out int n) ? model.Question.Choices.FirstOrDefault(x => x.Index == n) : model.Question.Choices.FirstOrDefault(x => x.Enabled && x.Title.Contains(a));
            if (c is not null) _ = Choose2Async(c); else GD.PrintErr("QA no choice " + a);
        });
        QaScript.Register("until-turn", async _ =>
        {
            for (int i = 0; i < 20 && model is not null; i++)
            {
                if (model.QuestionTag == "PlayerWindowChooseOne") return;
                if (model.QuestionTag == "ContinueCampaign") { App.I.CloseAllModals(); await Do("继续战役", () => B.Continue(state!)); await Advance(); await ToSignal(GetTree().CreateTimer(0.6), SceneTreeTimer.SignalName.Timeout); continue; }
                var c = model.QuestionTag == "Read" ? model.Question.Choices.FirstOrDefault(x => x.Enabled)
                    : model.Question.Choices.FirstOrDefault(x => x.Enabled && x.Title.Contains("结束选牌")) ?? (model.QuestionTag == "ChooseOne" && model.Question.Choices.All(x => x.Tag == "PortraitLabel") ? model.Question.Choices[0] : null);
                if (c is null) return;
                await Choose2Async(c);
                await ToSignal(GetTree().CreateTimer(0.6), SceneTreeTimer.SignalName.Timeout);
            }
        });
        QaScript.Register("autoplay", async a => await Autoplay(a));
        QaScript.Register("read", a => { if (model is not null) CardReader.Open(a); });
        // QA: a raw question run through the presenter against a save's entities (save|raw-question file|variant), titles shown
        // as buttons and printed. variant: "" as saved, "unrevealed" (the target place face down), "concealed" (it as a 隐匿 mini-card)
        QaScript.Register("present-raw", a =>
        {
            var p = a.Split('|'); string variant = p.Length > 2 ? p[2] : "";
            var save = System.Text.Json.Nodes.JsonNode.Parse(System.IO.File.ReadAllText(System.IO.Path.Combine(App.I.Root, p[0])))!;
            var ents = save["snapshot"]!["game"]!["gameEntities"]!.AsObject();
            string rawText = System.IO.File.ReadAllText(System.IO.Path.Combine(App.I.Root, p[1])); int brace = rawText.IndexOf('{'); rawText = rawText[brace..];
            var q = System.Text.Json.Nodes.JsonNode.Parse(rawText)!;
            var game = new System.Text.Json.Nodes.JsonObject();
            foreach (var kv in ents) game[kv.Key] = kv.Value?.DeepClone();
            foreach (var ch in q["choices"]!.AsArray())
            {
                string id = ch?["enemyId"]?.ToString() ?? ""; if (id.Length == 0 || game["locations"]?[id] is not System.Text.Json.Nodes.JsonObject loc) continue;
                if (variant == "unrevealed") loc["revealed"] = false;
                if (variant == "concealed") { game["locations"]!.AsObject().Remove(id); game["concealed"]![id] = loc; }
            }
            var gameEl = System.Text.Json.JsonSerializer.SerializeToElement(game);
            var col = new VBoxContainer(); col.AddThemeConstantOverride("separation", 10);
            col.AddChild(Style.Text("QA：选目标题目（" + (variant.Length > 0 ? variant : "原样") + "）", 22, Style.Brass, Style.Kai));
            int i = 0;
            foreach (var ch in q["choices"]!.AsArray())
            {
                var pres = S.Presenter.Describe(gameEl, new IndexedClientChoice(i++, 0, System.Text.Json.JsonSerializer.SerializeToElement(ch)));
                GD.Print("QA_PRESENT " + variant + " " + pres.Tag + " :: " + pres.Title);
                col.AddChild(Ui.MinimalButton(pres.Title, () => { }, 22));
            }
            App.I.Modal(Ui.Panel(col, 30), true, 0.6f);
        });
        // QA: one key of the game view, raw (dump-game:concealed)
        QaScript.Register("dump-game", a => { if (model is not null) { var raw = J.Get(model.Game, a); var t = raw.ValueKind == JsonValueKind.Undefined ? "(none)" : raw.GetRawText(); GD.Print("QA_GAME " + a + " :: " + (t.Length > 3000 ? t[..3000] : t)); } });
        // QA: a card drawn the way the pickers draw it (redrawn small card and printed card), to check what a code shows
        QaScript.Register("show-card", a =>
        {
            var row = new HBoxContainer(); row.AddThemeConstantOverride("separation", 30);
            row.AddChild(new CardView { Code = a, CustomMinimumSize = new Vector2(140, 196), Size = new Vector2(140, 196), HoverZoom = true, Actionable = true });
            row.AddChild(new CardView { Code = a, CustomMinimumSize = new Vector2(140, 196), Size = new Vector2(140, 196), HoverZoom = true, Redrawn = false });
            App.I.Modal(Ui.Panel(row, 24), true, 0.6f);
        });
        QaScript.Register("read-printed", a => { if (model is not null) CardReader.Open(a, printed: true); });
        QaScript.Register("map", _ => OpenMap());
        QaScript.Register("bag", _ => OpenChaosBag());
        QaScript.Register("undo", _ => Undo());
        QaScript.Register("menu-open", _ => OpenMenu());
        QaScript.Register("tut-mull", _ => { mulliganPick.UnionWith(tutMullSwap); Render(); GD.Print("QA_TUT_MULL " + mulliganPick.Count); ConfirmMulligan(); });   // QA: the tutorial's swap, as clicking the glowing cards
        QaScript.Register("tut-pick", code => { if (pickNodes.TryGetValue(code, out var pn) && pn is CardView pcv) pcv.OnClick?.Invoke(); else GD.Print("QA no pick " + code); });
        QaScript.Register("owners-dialog", _ => ShowOwners());   // QA: 调整控制权
        QaScript.Register("debug-export", a => { if (state is { } st) SaveTransfer.ExportDebugTo(st, B, DebugTitle(), System.IO.Path.Combine(App.I.Root, a)); });   // QA: 导出调试信息 to a file under the project root
        QaScript.Register("close", _ => { App.I.CloseAllModals(); ClosePopup(); Peek.Hide(); DragFeedback(false); });
        QaScript.Register("home", _ => { var mm = new MainMenu(); App.I.Go(mm); mm.Ready(); });
        QaScript.Register("log", _ => OpenLog());
        QaScript.Register("hover", a => { if (anchors.TryGetValue(a, out var n)) arrows.Point(null, n); });
        // QA: drop hand card i in the middle of the table / on the commit box, through the same code as a real drag
        QaScript.Register("drop-hand", a =>
        {
            var parts = a.Split(','); int i = parts[0] == "p" ? fan.FindIndex(f => f.Card.Actionable) : int.TryParse(parts[0], out int n) ? n : -1;
            if (i < 0 || i >= fan.Count) { GD.PrintErr("QA no hand card " + a); return; }
            var key = fan[i].Card.Key; var at = parts.Length > 1 && parts[1] == "commit" && commitBox is not null ? commitBox.GetGlobalRect().GetCenter() : PlayRect().GetCenter();
            GD.Print("QA_DROP " + key + " ok=" + DropFromHand(key, at));
        });
        QaScript.Register("ability-faces", code => QaAbilityFaces(code));
        QaScript.Register("act", a => { if (anchors.TryGetValue(a, out var n)) ActOn(a, n, ""); else GD.PrintErr("QA no anchor " + a); });
        // QA: open the screens that only appear mid-game, for screenshots
        QaScript.Register("show-drag", a => { if (int.TryParse(a, out int i) && i >= 0 && i < fan.Count) { var cv = fan[i].Card; cv.Position = new Vector2(cv.Position.X + 40, cv.Position.Y - 300); cv.RotationDegrees = 0; cv.Scale = new Vector2(1.08f, 1.08f); cv.ZIndex = 50; } DragFeedback(true); });
        QaScript.Register("help", _ => OpenHelp());
        QaScript.Register("reveal", a => { _ = RevealCard(a); });
        QaScript.Register("interlude", a => { var p = a.Split(','); Task _t = Interlude(p[0].Length > 0 ? p[0] : null, p.Length > 1 && p[1].Length > 0 ? p[1] : null, p.Length > 2 ? p[2] : "act"); });
        QaScript.Register("notice", a => ForcedNotice(a));
        QaScript.Register("peek-hand", a => { if (int.TryParse(a, out int i) && i >= 0 && i < fan.Count) Peek.ShowCard(fan[i].Card, fan[i].Card.Code, null, 0); });
        // QA: click hand card i (as a pointer click would), confirm the opening hand, open the act / agenda popups
        QaScript.Register("hand-click", a => { if (int.TryParse(a, out int i) && i >= 0 && i < fan.Count) fan[i].Card.OnClick?.Invoke(); });
        QaScript.Register("mull-confirm", _ => ConfirmMulligan());
        QaScript.Register("midshot", a => qaMidShot = a);
        QaScript.Register("deck-view", _ => OpenDeckViewer());
        QaScript.Register("save-completed", _ => { completedSaved = false; GD.Print("QA_SAVED " + SaveCompletedDecks()); });
        // QA: open the upgrade workshop for the viewed investigator with some experience, without sending anything
        QaScript.Register("upgrade-demo", a =>
        {
            if (model?.Me is not { } me || state is null) return;
            int seat = Array.FindIndex(state.Seats, s => s.Player == state.Player);
            int xp = int.TryParse(a, out int x) ? x : 5;
            var req = new UpgradeRequest(CoreCatalog.Normalize(me.Code), model.Name(me), CurrentDeckSlots(seat), xp, async (chosen, cost) =>
            {
                GD.Print("QA_UPGRADE " + (chosen is null ? "kept" : "cost=" + cost + " cards=" + chosen.Values.Sum()));
                if (upgradeOverlay is not null && IsInstanceValid(upgradeOverlay)) upgradeOverlay.QueueFree();
                await Task.CompletedTask;
            }, SeatDeckMeta(seat), CurrentSideSlots(seat));
            upgradeOverlay = OpenUpgradeEditor(req);
        });
        // QA: fly hand card 0 to the discard pile, to capture the flight itself
        QaScript.Register("fly-demo", _ => { if (fan.Count > 0 && discardPileNode is not null) Launch(fan[0].Card.Code, fan[0].Card.GetGlobalRect(), discardPileNode); });
        QaScript.Register("tip", a => { if (model is null) return; if (a == "agenda" && anchors.TryGetValue("agenda", out var g)) ShowAgenda(g, model); else if (anchors.TryGetValue("act", out var t)) ShowGoal(t, model); });
        QaScript.Register("dump-locs", _ => { if (model is not null) GD.Print("QA_LOCS " + string.Join(" | ", model.Locations.Select(l => l.Code + "=" + model.Label(l) + "/" + model.Name(l) + (model.Revealed(l) ? "" : "(hidden)")))); });
        // QA: the map board's camera
        QaScript.Register("board-fit", _ => BoardFit(false));
        QaScript.Register("board-here", _ => BoardFocusHere());
        QaScript.Register("board-zoom", a => ZoomAt(BoardFree().GetCenter(), float.Parse(a, System.Globalization.CultureInfo.InvariantCulture)));
        // QA: each agenda's doom as the table counts it against the core's own modifiers (Carcosa 真相幻影 OtherDoomSubtracts)
        QaScript.Register("dump-agenda", a => { if (model is not null) foreach (var ag in model.Agendas) GD.Print("QA_AGENDA " + a + " " + ag.Code + " own=" + model.Doom(ag) + " count=" + model.AgendaDoomCount(ag) + " threshold=" + model.AgendaThreshold(ag) + " otherSubtracts=" + model.OtherDoomSubtracts(ag) + " allDoom=" + model.SubtractDoomCount + " totalDoom=" + model.TotalDoom + " mods=" + J.Get(ag.Raw, "modifiers").GetRawText()); });
        QaScript.Register("dump-view", async a => { if (state is not null) { var sec = J.Get(state.View, a); if (sec.ValueKind == JsonValueKind.Undefined) sec = await B.ViewSection(state, a); var v = sec.ValueKind == JsonValueKind.Undefined ? "(none)" : sec.GetRawText(); GD.Print("QA_VIEW " + a + " " + (v.Length > 6000 ? v[..6000] : v)); } });
        // QA: the core's debug command (html debug panel) — dbg-schema prints what it accepts, dbg:<json> applies one
        QaScript.Register("dbg-schema", async _ => { if (state is null) return; try { var sc = await B.DebugSchema(state); var raw = sc.GetRawText(); System.IO.File.WriteAllText(System.IO.Path.Combine(App.I.Root, "evidence", "debug-schema.json"), raw); GD.Print("QA_DEBUG_SCHEMA written " + raw.Length); } catch (Exception e) { GD.Print("QA_DEBUG_FAIL " + e.Message); } });
        // QA: a card of this deck into the hand of the investigator in view (dbg-hand:<code>), through the debug schema
        QaScript.Register("dbg-hand", async a =>
        {
            if (state is null) return;
            try
            {
                var sc = await B.DebugSchema(state);
                var card = J.Items(J.Get(J.Get(sc, "actions"), "cards")).FirstOrDefault(c => CoreCatalog.Normalize(J.String(c, "cardCode")) == a);
                var dest = J.Items(J.Get(card, "destinations")).FirstOrDefault(d => J.Tag(d) == "DebugCardHand");
                if (card.ValueKind != JsonValueKind.Object || dest.ValueKind != JsonValueKind.Object) { GD.Print("QA_DEBUG_FAIL no card/hand " + a); return; }
                var cmd = JsonSerializer.SerializeToElement(new { operation = "moveCard", cardId = J.String(card, "id"), destination = dest });
                await Do("调试", () => B.Debug(state!, cmd)); GD.Print("QA_DEBUG_OK hand " + a); await Advance();
            }
            catch (Exception e) { GD.Print("QA_DEBUG_FAIL " + a + " :: " + e.Message); }
        });
        QaScript.Register("dbg", async a =>
        {
            if (state is null) return;
            // single quotes allowed (the command line eats double quotes)
            try { using var doc = JsonDocument.Parse(a.Replace('\'', '"')); var cmd = doc.RootElement.Clone(); await Do("调试", () => B.Debug(state!, cmd)); GD.Print("QA_DEBUG_OK " + a); await Advance(); }
            catch (Exception e) { GD.Print("QA_DEBUG_FAIL " + a + " :: " + e.Message); }
        });
        QaScript.Register("dump-inv", a => { if (model is not null) GD.Print("QA_INV " + a + " " + string.Join(" | ", model.Investigators.Select(i => model.Name(i) + " 线索" + model.Clues(i) + " 资源" + model.Resources(i) + " 伤" + model.Damage(i) + " 恐" + model.Horror(i) + " @" + (model.Location(model.LocationOf(i)) is { } l ? model.Name(l) : "?"))) + " act=" + model.Act?.Code + " doom=" + model.TotalDoom + "/" + model.DoomThreshold + " phase=" + model.Phase); });
        QaScript.Register("dump-q-file", a => { if (state is not null) System.IO.File.WriteAllText(System.IO.Path.Combine(App.I.Root, a), state.Question.GetRawText()); });   // QA: the whole raw question
        QaScript.Register("dump-q", _ => { if (state is not null) { var raw = state.Question.GetRawText(); GD.Print("QA_RAWQ seats=" + string.Join(",", state.Seats.Select(x => x.Player.ToString("D")[..8] + (x.HasQuestion ? "*" : ""))) + " me=" + state.Player.ToString("D")[..8] + " :: " + (raw.Length > 5000 ? raw[..5000] : raw)); } });
        QaScript.Register("dump-raw", a => { if (model is not null && int.TryParse(a, out var n) && model.Question.Choices.FirstOrDefault(c => c.Index == n) is { } ch) { var r = ch.Raw.GetRawText(); GD.Print("QA_RAW " + n + " hint=" + ch.Hint + " card=" + ch.CardCode + " " + (r.Length > 3000 ? r[..3000] : r)); } });
        QaScript.Register("dump", _ => { if (model is not null) GD.Print("QA_QUESTION " + model.QuestionTag + " :: " + string.Join(" | ", model.Question.Choices.Select(c => c.Index + ":" + c.Tag + ":" + c.Title + "@" + model.Anchor(c)))); });
    }

    // The log's first read goes through the whole history once (0.7 s on a long campaign); after that it is cached. It is done
    // in the background a moment after the table first shows, so opening the sidebar is quick from the start (Yunke 11-22).
    bool logWarmed;
    void WarmLog()
    {
        if (logWarmed || state is null) return;
        logWarmed = true;
        GetTree().CreateTimer(1.5).Timeout += async () =>
        {
            if (!IsInstanceValid(this) || state is not { } st) return;
            var w = System.Diagnostics.Stopwatch.StartNew();
            try { await B.Logs(st); } catch (Exception e) { GD.Print("LOG_WARM skipped: " + e.Message); }   // a step taken meanwhile: what was read stays cached
            GD.Print("LOG_WARM ms=" + w.ElapsedMilliseconds);
        };
    }
}
