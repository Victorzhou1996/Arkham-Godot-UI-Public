using Godot;
using Gathering;
using System.Text.Json;

namespace ArkhamUI;

// 新手教程 at the table (Scripts/Tutorial/Tutorial.cs, docs/TUTORIAL-PLAN.md §4).
//   The opening is arranged with the rules core's own debug operations, which go through the core like any change and are
//   kept in the game's history: the two opening hands; then, once play begins, each deck's next cards (黑帮打手 on top of
//   Roland's, 恶灵附身 second in Daisy's; the signature weaknesses to the bottom) and the encounter deck's top two (攫噬之手,
//   鼠群). The chaos tokens stay random (the core refuses test debug while both investigators may commit cards); the tips
//   explain whichever comes up.
//   Once the first act has advanced, nothing more is arranged.
//   Tips: each shows the first time its condition holds, one at a time, with a soft glow over what it talks about.
public partial class TableScreen
{
    Control? tutTip; Panel? tutGlow; string tutTipId = ""; string tutLastMe = "";
    Tween? tutPulse; TutorialRipple? tutRipple;
    // the opening hand: the cards the tutorial swaps (by hand card id) for the investigator in view (Yunke 11-23: "要引导玩家换
    // 固定的几张牌，其他的牌不允许换")
    readonly HashSet<string> tutMullSwap = new(); string tutMullFor = "";
    readonly Dictionary<string, Control> tutMullNodes = new();   // the cards to swap, as drawn (each gets its own ripple)
    readonly List<TutorialRipple> tutMullRipples = new();
    Control? mullConfirm;                                   // the mulligan's confirm button (TableScreen.Interact)
    readonly Dictionary<string, Control> pickNodes = new();  // investigators offered as picks, by card code (TableScreen.Interact)

    bool TutorialMullAllowed(string handId) => !InTutorial || !InMulligan(model!) || tutMullSwap.Contains(handId);
    bool TutorialMullReady => !InTutorial || model is null || !InMulligan(model) || tutMullSwap.IsSubsetOf(mulliganPick);

    // the opening hand's cards to swap: those not in the tutorial's hand for this investigator (Tutorial.RolandHand / DaisyHand)
    void TutorialMulligan(TableModel m)
    {
        if (!InMulligan(m) || m.Me is not { } me || tutMullFor == me.Id) return;
        tutMullFor = me.Id; tutMullSwap.Clear();
        var swap = (CoreCatalog.Normalize(me.Code) == Tutorial.Roland ? Tutorial.RolandSwap : Tutorial.DaisySwap).ToList();
        foreach (var (id, code) in m.Hand(me)) if (swap.Remove(CoreCatalog.Normalize(code))) tutMullSwap.Add(id);
        GD.Print("TUTORIAL mulligan " + CoreCatalog.Normalize(me.Code) + " hand " + string.Join(",", m.Hand(me).Select(h => CoreCatalog.Normalize(h.Code))) + " swap " + string.Join(",", m.Hand(me).Where(h => tutMullSwap.Contains(h.Id)).Select(h => h.Code)));
    }

    // a choice the tutorial does not allow now (its fixed course), with why
    string? TutorialBlocks(ChoicePresentation c)
    {
        if (!InTutorial || model is not { } m) return null;
        string Who() => m.Investigators.FirstOrDefault(i => "inv:" + i.Id == m.Anchor(c)) is { } inv ? CoreCatalog.Normalize(inv.Code) : "";
        if (J.String(m.State.Question, "label").Contains("chooseLeadInvestigator") && Who() is { Length: > 0 } lead && lead != Tutorial.Roland) return "新手教程中请选罗兰·班克斯做领队";
        if (m.Question.Title.Contains("开始的调查员") && Who() is { Length: > 0 } first && first != Tutorial.Daisy) return "新手教程中这一轮请让黛西·沃克先行动";
        return null;
    }

    bool InTutorial => Tutorial.Is(gameId) && !Tutorial.M.Skipped;

    // ─────────────── the arranged opening (called in the advance loop; true when the game changed) ───────────────

    async Task<bool> TutorialDirect()
    {
        if (!InTutorial || model is null || state is null || Tutorial.M.OpeningOver) return false;
        var m = model; var t = Tutorial.M;
        try
        {
            if (m.Act is { } act && CoreCatalog.Normalize(act.Code) != "01108") { t.OpeningOver = true; Tutorial.Save(); GD.Print("TUTORIAL opening over"); return false; }
            // 1 · the opening hands — at the first stable action window (the core moves cards only there, not during the mulligan)
            bool window = m.Phase == "InvestigationPhase" && m.QuestionTag == "PlayerWindowChooseOne" && !m.InSkillTest && !InMulligan(m);
            if (window)
            {
                foreach (var inv in m.Investigators)
                {
                    string code = CoreCatalog.Normalize(inv.Code);
                    if (t.HandsDone.Contains(code) || !m.Hand(inv).Any()) continue;
                    await ArrangeHand(inv.Id, code == Tutorial.Roland ? Tutorial.RolandHand : Tutorial.DaisyHand);
                    t.HandsDone.Add(code); Tutorial.Save();
                    return true;
                }
            }
            // 2 · the decks and the encounter deck, once play begins (after the mulligans, which would draw from the decks)
            if (!t.DecksDone && t.HandsDone.Count == 2 && window)
            {
                await ArrangeDecks();
                t.DecksDone = true; Tutorial.Save();
                return true;
            }
            // (the chaos tokens stay random: the core takes no test debug while both investigators may commit — 11-23 QA)
        }
        catch (Exception e) { GD.PrintErr("TUTORIAL arrange failed: " + e.Message); GD.Print("TUTORIAL_FAIL " + e.Message); }
        return false;
    }

    static string C(string code) => "c" + code;

    async Task<JsonElement[]> DebugCards()
    {
        var sc = await B.DebugSchema(state!);
        return J.Items(J.Get(J.Get(sc, "actions"), "cards")).Select(c => c.Clone()).ToArray();
    }

    async Task MoveCard(JsonElement card, Func<JsonElement, bool> where)
    {
        var dest = J.Items(J.Get(card, "destinations")).FirstOrDefault(where);
        if (dest.ValueKind == JsonValueKind.Undefined) { GD.Print("TUTORIAL no destination for " + J.String(card, "cardCode")); return; }
        var cmd = JsonSerializer.SerializeToElement(new { operation = "moveCard", cardId = J.String(card, "id"), destination = dest });
        state = await B.Debug(state!, cmd);
    }

    static bool ToHand(JsonElement d, string inv) => J.String(d, "tag") == "DebugCardHand" && J.String(d, "contents") == inv;
    static bool ToDeck(JsonElement d, string inv, string end)
    {
        if (J.String(d, "tag") != "DebugCardDeck") return false;
        var c = J.Get(d, "contents"); if (c.ValueKind != JsonValueKind.Array || c.GetArrayLength() != 2) return false;
        return J.String(c[0], "tag") == (inv.Length > 0 ? "InvestigatorDeck" : "EncounterDeck") && (inv.Length == 0 || J.String(c[0], "contents") == inv) && c[1].GetString() == end;
    }
    static bool CanGo(JsonElement card, Func<JsonElement, bool> where) => J.Items(J.Get(card, "destinations")).Any(where);

    // the wanted cards into the hand (from the deck), the others back into the deck (shuffled in)
    async Task ArrangeHand(string invId, string[] wanted)
    {
        var inv = model!.Investigators.First(i => i.Id == invId);
        string who = C(CoreCatalog.Normalize(inv.Code));
        var hand = model.Hand(inv).ToList();
        GD.Print("TUTORIAL before-arrange " + who + ": " + string.Join(",", hand.Select(h => CoreCatalog.Normalize(h.Code))));
        var need = wanted.ToList();
        foreach (var (_, code) in hand) need.Remove(CoreCatalog.Normalize(code));
        var cards = await DebugCards();
        var handIds = hand.Select(h => h.Id).ToHashSet();
        foreach (var code in need)
        {
            var card = cards.FirstOrDefault(c => J.String(c, "cardCode") == C(code) && !handIds.Contains(J.String(c, "id")) && CanGo(c, d => ToHand(d, who)));
            if (card.ValueKind == JsonValueKind.Undefined) { GD.Print("TUTORIAL card not found " + code); continue; }
            await MoveCard(card, d => ToHand(d, who));
            handIds.Add(J.String(card, "id"));
        }
        // the others back: the ones not wanted (a second copy of a wanted card counts as not wanted)
        var keep = wanted.ToList();
        model = new TableModel(state!, S);
        inv = model.Investigators.First(i => i.Id == invId);
        cards = await DebugCards();
        foreach (var (id, code) in model.Hand(inv).ToList())
        {
            if (keep.Remove(CoreCatalog.Normalize(code))) continue;
            var card = cards.FirstOrDefault(c => J.String(c, "id") == id);
            if (card.ValueKind != JsonValueKind.Undefined) await MoveCard(card, d => ToDeck(d, who, "DebugDeckShuffle"));
        }
        model = new TableModel(state!, S);
        GD.Print("TUTORIAL hand " + who + ": " + string.Join(",", model.Hand(model.Investigators.First(i => i.Id == invId)).Select(h => h.Code)));
    }

    async Task ArrangeDecks()
    {
        string r = C(Tutorial.Roland), d = C(Tutorial.Daisy);
        async Task Put(string code, string inv, string end, Func<JsonElement, bool>? extra = null)
        {
            var cards = await DebugCards();
            var card = cards.FirstOrDefault(c => J.String(c, "cardCode") == C(code) && CanGo(c, x => ToDeck(x, inv, end)) && (extra is null || extra(c)));
            if (card.ValueKind == JsonValueKind.Undefined) { GD.Print("TUTORIAL not in a deck " + code); return; }
            await MoveCard(card, x => ToDeck(x, inv, end));
        }
        // the signature weaknesses out of the way
        await Put("01007", r, "DebugDeckBottom");   // 掩盖罪证
        await Put("01009", d, "DebugDeckBottom");   // 死灵之书
        // Roland draws 黑帮打手 at the end of the first round
        await Put(Tutorial.RolandWeakness, r, "DebugDeckTop");
        // Daisy: 恶灵附身 second, under one ordinary card
        await Put(Tutorial.DaisyWeakness, d, "DebugDeckTop");
        await Put("01037", d, "DebugDeckTop");      // 专业的直觉
        // the encounter deck: 攫噬之手 first, then 鼠群
        await Put("01159", "", "DebugDeckTop");
        await Put("01162", "", "DebugDeckTop");
        GD.Print("TUTORIAL decks arranged");
    }

    // ─────────────── tips ───────────────

    // Step: a tip about a step the player must take (choose the lead, the opening hand): it closes by itself once that step is over
    sealed record Tip(string Id, Func<TableModel, bool> When, string Text, string Target = "", bool Step = false);

    static bool HasToken(TableModel m, string face) => m.RevealedTokens.Contains(face);
    static bool Is(Entity? e, string code) => e is not null && CoreCatalog.Normalize(e.Code) == code;

    List<Tip> TutorialTips() => new()
    {
        new("welcome", m => true, "欢迎来到新手教程！｜这一局你同时操作两名调查员：罗兰·班克斯（守卫者，擅长战斗）和黛西·沃克（探求者，擅长调查）。｜随时可以点“跳过教程”自己玩。"),
        new("lead", m => J.String(m.State.Question, "label").Contains("chooseLeadInvestigator"), "先选「领队」：每个阶段由领队先行动，遇到“由领队决定”的事也由他来选。｜这一局点击发光的罗兰·班克斯，让他做领队。", "pick:01001", Step: true),
        new("mulligan", m => InMulligan(m), "开局先看起手的 5 张牌，可以把不想要的牌换掉一次（起手调度）。｜教程里请点选发光的牌，再点“确认换掉”。", "mullswap", Step: true),
        new("first", m => m.Question.Title.Contains("开始的调查员"), "每轮调查阶段，由你们决定谁先行动。｜这一轮让黛西·沃克先行动：点击发光的黛西。", "pick:01002", Step: true),
        new("investigator", m => m.Phase == "InvestigationPhase" && m.QuestionTag == "PlayerWindowChooseOne", "这是你当前操作的调查员。｜四项属性是意志、智力、战斗、敏捷，检定时用得上；下面是生命值和理智值，降到 0 就会被击败。悬停可以看能力。", "me"),
        new("actions", m => m.Phase == "InvestigationPhase" && m.QuestionTag == "PlayerWindowChooseOne", "调查阶段每名调查员有 3 个行动，可以做：｜· 调查：在所在地点找线索｜· 移动：去相连的地点｜· 抽 1 张牌，或拿 1 个资源｜· 打出手牌、启动卡牌能力｜· 战斗、闪避、交涉：对付敌人｜做完 3 个行动，就轮到下一名调查员。"),
        new("magnifier", m => m.Phase == "InvestigationPhase" && m.QuestionTag == "PlayerWindowChooseOne" && Is(m.Me, Tutorial.Daisy) && m.Hand(m.Me!).Any(h => CoreCatalog.Normalize(h.Code) == "01030"),
            "先做准备：把黛西手里发光的「放大镜」拖到桌面中央松开，打出它（花 1 个行动、1 个资源）。｜放大镜在场时，黛西调查时智力 +1。", "card:01030", Step: true),
        new("investigate", m => m.Phase == "InvestigationPhase" && m.QuestionTag == "PlayerWindowChooseOne" && Is(m.Me, Tutorial.Daisy) && !m.Hand(m.Me!).Any(h => CoreCatalog.Normalize(h.Code) == "01030"),
            "「书房」里有线索。点击发光的「书房」进行调查：｜用智力对抗地点的遮蔽值，成功就拿到 1 个线索。黛西的智力很高，最适合调查。", "here", Step: true),
        new("commit", m => m.InSkillTest && m.Question.Choices.Any(c => c.Enabled && c.Title.StartsWith("投入")), "技能检定：属性值 + 投入卡牌的图标 + 混沌袋抽出的标记，大于等于难度就成功。｜手里带对应图标的牌可以只拿来投入：拖进检定框即可。", "hand"),
        new("chaos", m => m.RevealedTokens.Any(), "这就是混沌袋抽出的标记：数字直接加减到检定值上，总值大于等于难度就成功，否则失败。｜混沌袋是随机的，关键检定最好多投入几张牌留点余量。｜结果会一直显示，看清后点击任意处继续。"),
        new("autofail", m => HasToken(m, "AutoFail"), "「触手」标记：检定自动失败，不管数值多高。这就是关键检定要留余量的原因。"),
        new("eldersign", m => HasToken(m, "ElderSign"), "「旧印」标记：好标记，触发你调查员卡上的旧印能力。"),
        new("skull", m => HasToken(m, "Skull") || HasToken(m, "Cultist") || HasToken(m, "Tablet") || HasToken(m, "ElderThing"), "骷髅、邪教徒、石板、远古邪物这几种标记的效果写在场景卡背面，每个场景都不一样，悬停场景卡可以查看。", "act"),
        new("clue", m => m.Investigators.Any(i => m.Clues(i) > 0), "拿到线索了！线索放在调查员身上，用来推进场景。", "me"),
        new("peek", m => m.Phase == "InvestigationPhase" && m.QuestionTag == "PlayerWindowChooseOne" && Is(m.Me, Tutorial.Daisy) && m.Investigators.Any(i => m.Clues(i) > 0),
            "右上角是队友的头像。点击发光的罗兰·班克斯头像，就切到他的视角，看看他的手牌和交战的敌人。｜地图上调查员的小卡也可以点来切换。看完再点黛西的头像切回来。", "teampic:01001", Step: true),
        new("switch", m => tutLastMe.Length > 0 && m.Me is not null && m.Me.Id != tutLastMe && m.Phase == "InvestigationPhase" && m.QuestionTag == "PlayerWindowChooseOne", "视角切换了：轮到哪名调查员做选择，桌面就会自动切到他。｜没轮到他时只能看，不能替他行动。", "team"),
        new("act", m => m.Investigators.Sum(i => m.Clues(i)) >= 2 && m.Act is not null, "场景卡写着推进条件：「被困于此」需要调查员花费线索。｜线索够了就能推进，书房的门会打开。", "act"),
        new("advance", m => byAnchor.TryGetValue("act", out var al) && al.Any(c => c.Enabled), "线索够了！点击发光的场景卡「被困于此」，花费线索推进场景。", "act", Step: true),
        new("asset", m => m.QuestionTag == "PlayerWindowChooseOne" && m.Me is not null && m.Hand(m.Me).Any(h => CoreCatalog.Normalize(h.Code) is "01006" or "01008"), "发光的手牌现在可以打出：拖到桌面中央松开即可。｜资产打出后留在场上，占用手部、身体等槽位；打出需要花费资源。", "hand"),
        new("endturn", m => m.Phase == "InvestigationPhase" && m.Me is not null && m.Actions(m.Me) == 0 && m.QuestionTag == "PlayerWindowChooseOne", "行动用完了。点右下角的“结束回合”，轮到下一名调查员。"),
        new("enemyphase", m => m.Phase == "EnemyPhase", "敌人阶段：猎手敌人会向调查员移动，和你交战的敌人会攻击你。"),
        new("upkeep", m => m.Phase == "UpkeepPhase", "维持阶段：重置横置的卡牌，每人抽 1 张牌、拿 1 个资源。｜手牌超过 8 张要弃到 8 张。"),
        new("weakness", m => m.Enemies.Any(e => Is(e, Tutorial.RolandWeakness)), "罗兰抽到了弱点「黑帮打手」：每副牌都有弱点，抽到就要处理。｜它是猎手，只追罗兰，会和他交战。可以战斗打倒它，也可以花 4 资源“谈判”让它离开。", "enemy"),
        new("mythos", m => m.Phase == "MythosPhase", "神话阶段（从第 2 轮开始）：｜先在密谋卡上放 1 个毁灭，再每名调查员抽 1 张遭遇牌。", "agenda"),
        new("doom", m => m.TotalDoom > 0, "毁灭标记积累到密谋卡上的数字，密谋就会推进，局势变得更糟。｜时间有限，要尽快推进场景。", "agenda"),
        new("treachery", m => m.EncounterDiscard.Any(c => c is "01162" or "01163"), "这是诡计牌：遭遇牌里的坏事，通常要求一次检定，失败就受到伤害或其他惩罚。"),
        new("spawn", m => m.Enemies.Any(e => !Is(e, Tutorial.RolandWeakness)), "遭遇牌里的敌人入场了。悬停敌人可以看它的战斗值、生命值和闪避值。", "enemy"),
        new("engaged", m => m.Me is not null && m.EngagedWith(m.Me).Any(), "你和敌人交战了：｜· 战斗：用战斗对抗敌人的战斗值，成功造成伤害｜· 闪避：用敏捷对抗闪避值，成功就脱离交战｜交战时做战斗、闪避以外的行动，会被敌人借机攻击。", "enemy"),
        new("damage", m => m.Investigators.Any(i => m.Damage(i) > 0 || m.Horror(i) > 0), "受到伤害或恐惧了。可以分给能承受伤害的资产（比如盟友）；｜调查员的生命值或理智值降到 0 就被击败，这一关出局并得到创伤。", "me"),
        new("haunted", m => m.Investigators.Any(i => m.ThreatsOf(i).Any(x => Is(x, Tutorial.DaisyWeakness))), "黛西抽到了弱点「恶灵附身」：它放在威胁区里，让她每项技能 −1。｜花两个行动可以弃掉它——卡牌上带行动图标的能力，都是这样花行动来启动的。", "me"),
        new("opened", m => m.Act is { } a && CoreCatalog.Normalize(a.Code) != "01108", "场景推进了，书房的门打开了！｜教程的引导到此结束，接下来就是自由游戏。点击发光的地点可以移动，遇到新东西还会提示。"),
        new("priest", m => m.Enemies.Any(e => Is(e, "01116")), "「食尸鬼祭司」是这一关的精英敌人，很难对付。打倒它是一种结局，也可以先找到别的出路。"),
        new("over", m => m.IsOver, "这一关结束了。结局会写进战役日志，经验值可以在两章之间升级卡组。｜教程存档会保留在“继续调查”里，可以接着玩第二章「午夜假面」。"),
    };

    // after every render: keep the tip shown, or show the next one whose time has come
    void TutorialAfterRender()
    {
        if (!InTutorial || model is null) { ClearTip(); return; }
        var m = model;
        var seen = Tutorial.M.Seen;
        if (tutTip is not null && IsInstanceValid(tutTip))
        {
            // a step tip whose step is over goes away by itself
            if (TutorialTips().FirstOrDefault(t => t.Id == tutTipId) is { Step: true } st && !Safe(() => st.When(m)))
            { if (!seen.Contains(st.Id)) seen.Add(st.Id); Tutorial.Save(); ClearTip(); }
            else { PlaceGlow(); RememberMe(m); return; }
        }
        TutorialMulligan(m);
        var next = TutorialTips().FirstOrDefault(t => !seen.Contains(t.Id) && Safe(() => t.When(m)));
        RememberMe(m);
        if (next is null) return;
        ShowTip(next);
    }

    void RememberMe(TableModel m) { if (m.Me is not null && m.Phase == "InvestigationPhase" && m.QuestionTag == "PlayerWindowChooseOne") tutLastMe = m.Me.Id; }
    static bool Safe(Func<bool> f) { try { return f(); } catch { return false; } }

    void ShowTip(Tip tip)
    {
        ClearTip();
        tutTipId = tip.Id;
        GD.Print("TUTORIAL_TIP " + tip.Id);
        var box = new PanelContainer { ZIndex = 160, MouseFilter = MouseFilterEnum.Stop };
        var sb = new StyleBoxFlat { BgColor = new Color(0.035f, 0.03f, 0.025f, 0.94f), BorderColor = new Color(Style.Amber, 0.8f), ContentMarginLeft = 22, ContentMarginRight = 22, ContentMarginTop = 14, ContentMarginBottom = 14 };
        sb.SetBorderWidthAll(1); sb.BorderWidthLeft = 4; sb.SetCornerRadiusAll(10); box.AddThemeStyleboxOverride("panel", sb);
        var col = new VBoxContainer(); col.AddThemeConstantOverride("separation", 10); box.AddChild(col);
        col.AddChild(Style.Text("新手教程", 17, Style.Amber, Style.Kai));
        col.AddChild(TutorialText.Label(tip.Text, 21, 620));   // coloured names, ｜ as new lines (Yunke 11-23)
        var row = new HBoxContainer { Alignment = BoxContainer.AlignmentMode.End }; row.AddThemeConstantOverride("separation", 12); col.AddChild(row);
        var skip = Ui.Button("跳过教程", () => { Tutorial.M.Skipped = true; Tutorial.Save(); ClearTip(); GD.Print("TUTORIAL skipped"); }, 18); skip.CustomMinimumSize = new Vector2(120, 44); row.AddChild(skip);
        var ok = Ui.Button("知道了", () => { if (!Tutorial.M.Seen.Contains(tip.Id)) Tutorial.M.Seen.Add(tip.Id); Tutorial.Save(); ClearTip(); TutorialAfterRender(); }, 20, true); ok.CustomMinimumSize = new Vector2(130, 44); row.AddChild(ok);
        AddChild(box);
        box.AnchorLeft = box.AnchorRight = 0.5f; box.GrowHorizontal = GrowDirection.Both; box.OffsetTop = 96;
        tutTip = box;
        // the glow over what the tip is about (soft all round, no hard edge; breathing)
        var rect = TipTarget(tip.Target);
        if (rect is { } r)
        {
            var glow = new Panel { MouseFilter = MouseFilterEnum.Ignore, ZIndex = 150 };
            var gs = new StyleBoxFlat { BgColor = new Color(Style.Amber, 0.04f), ShadowColor = new Color(Style.Amber, 0.42f), ShadowSize = 24, DrawCenter = true };
            gs.SetCornerRadiusAll(14); glow.AddThemeStyleboxOverride("panel", gs);
            AddChild(glow); tutGlow = glow; tutGlowTarget = tip.Target;
            PlaceGlow();
            if (!tutFollowing) { GetTree().ProcessFrame += PlaceGlow; tutFollowing = true; }
            if (S.Settings.ReducedMotion != true) { tutPulse = glow.CreateTween().SetLoops(); tutPulse.TweenProperty(glow, "modulate:a", 0.45f, 0.9f).SetTrans(Tween.TransitionType.Sine); tutPulse.TweenProperty(glow, "modulate:a", 1f, 0.9f).SetTrans(Tween.TransitionType.Sine); }
            // a step the player must take: the ring ripples out from where to click (Yunke 11-23)
            if (tip.Step) tutRipple = TutorialRipple.Start(this, () => TipTarget(tutGlowTarget), 151);
        }
    }

    string tutGlowTarget = ""; bool tutFollowing;
    void PlaceGlow()
    {
        if (!IsInstanceValid(this) || !IsInsideTree()) return;
        TutorialMullRipples();
        if (tutGlow is null || !IsInstanceValid(tutGlow)) return;
        if (TipTarget(tutGlowTarget) is not { } r) { tutGlow.Visible = false; return; }
        tutGlow.Visible = true;
        var pad = new Vector2(10, 10);
        tutGlow.GlobalPosition = r.Position - pad; tutGlow.Size = r.Size + pad * 2;
    }

    // where a tip points: a named part of the table, from the anchors the render keeps
    Rect2? TipTarget(string target)
    {
        if (target.Length == 0 || model is null) return null;
        IEnumerable<Control> nodes = target switch
        {
            "hand" => anchors.Where(kv => kv.Key.StartsWith("hand:")).Select(kv => kv.Value),
            "me" => model.Me is { } me && anchors.TryGetValue("inv:" + me.Id, out var a) ? new[] { a } : Array.Empty<Control>(),
            "team" => teamPics.Values,
            "here" => model.MyLocation is { } l && anchors.TryGetValue("loc:" + l.Id, out var b) ? new[] { b } : Array.Empty<Control>(),
            "enemy" => anchors.Where(kv => kv.Key.StartsWith("enemy:")).Select(kv => kv.Value).Take(1),
            // the opening hand's cards still to mark; once all are marked, the confirm button
            "mullswap" => tutMullSwap.Except(mulliganPick).Any()
                ? Array.Empty<Control>()
                : mullConfirm is not null && IsInstanceValid(mullConfirm) ? new[] { mullConfirm } : Array.Empty<Control>(),
            _ when target.StartsWith("pick:") => pickNodes.TryGetValue(target[5..], out var pn) ? new[] { pn } : Array.Empty<Control>(),
            _ when target.StartsWith("card:") => model.Me is { } cm ? model.Hand(cm).Where(h => CoreCatalog.Normalize(h.Code) == target[5..]).Select(h => anchors.TryGetValue("hand:" + h.Id, out var hc) ? hc : null).Where(x => x is not null).Select(x => x!).Take(1) : Array.Empty<Control>(),
            _ when target.StartsWith("teampic:") => model.Investigators.FirstOrDefault(i => CoreCatalog.Normalize(i.Code) == target[8..]) is { } ti && teamPics.TryGetValue(ti.Id, out var tp) ? new[] { tp } : Array.Empty<Control>(),
            _ => anchors.TryGetValue(target, out var c) ? new[] { c } : Array.Empty<Control>(),
        };
        Rect2? all = null;
        foreach (var n in nodes)
        {
            if (!IsInstanceValid(n) || !n.IsVisibleInTree()) continue;
            var r = n.GetGlobalRect();
            if (r.Size.X < 2 || r.Size.Y < 2) continue;
            all = all is { } x ? x.Merge(r) : r;
        }
        return all;
    }

    // during the guided mulligan: one ripple per card still to mark (they are found again after each render)
    void TutorialMullRipples()
    {
        bool want = tutTipId == "mulligan" && model is not null && InMulligan(model);
        if (!want) { if (tutMullRipples.Count > 0) { foreach (var r in tutMullRipples) if (IsInstanceValid(r)) r.QueueFree(); tutMullRipples.Clear(); } return; }
        if (tutMullRipples.Count > 0) return;
        foreach (var id in tutMullSwap)
        {
            var hid = id;
            if (TutorialRipple.Start(this, () => !mulliganPick.Contains(hid) && tutMullNodes.TryGetValue(hid, out var n) && IsInstanceValid(n) && n.IsVisibleInTree() ? n.GetGlobalRect() : null, 151) is { } rp)
                tutMullRipples.Add(rp);
        }
    }

    void ClearTip()
    {
        if (tutTip is not null && IsInstanceValid(tutTip)) tutTip.QueueFree();
        if (tutGlow is not null && IsInstanceValid(tutGlow)) tutGlow.QueueFree();
        if (tutRipple is not null && IsInstanceValid(tutRipple)) tutRipple.QueueFree();
        if (tutFollowing && IsInsideTree()) { GetTree().ProcessFrame -= PlaceGlow; tutFollowing = false; }
        foreach (var r in tutMullRipples) if (IsInstanceValid(r)) r.QueueFree();
        tutMullRipples.Clear();
        tutTip = null; tutGlow = null; tutTipId = ""; tutPulse = null; tutRipple = null;
    }
}
