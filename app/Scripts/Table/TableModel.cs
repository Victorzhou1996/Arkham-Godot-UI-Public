using Gathering;
using System.Text.Json;

namespace ArkhamUI;

public sealed record Entity(string Id, string Code, JsonElement Raw);

// Read-only projection of the rules core's view for one viewing seat. Scenario-agnostic.
public sealed class TableModel
{
    public TableState State { get; }
    public JsonElement Game => State.Game;
    public QuestionPresentation Question { get; }
    public string QuestionTag => Question.Tag;
    public Entity? Me { get; }
    readonly Services S;

    public TableModel(TableState state, Services s)
    {
        State = state; S = s;
        Players = Math.Max(1, state.Seats.Length);
        Question = s.Presenter.Present(state);
        Me = Investigators.FirstOrDefault(i => J.String(i.Raw, "playerId") == state.Player.ToString("D"));
    }

    static IEnumerable<Entity> Group(JsonElement game, string group) =>
        J.Props(J.Get(game, group)).Select(p => new Entity(p.Name, CodeOf(p.Value), p.Value));
    static string CodeOf(JsonElement e)
    {
        var c = ClientDisplay.CardCode(e);
        if (c.Length == 0) { var id = J.String(e, "id"); if (id.Length > 1 && id[0] == 'c' && (char.IsDigit(id[1]) || id[1] == ':')) c = id[1..]; }   // fan-made cards: "c:circus-ex-mortis:007"
        return CoreCatalog.Normalize(c);
    }

    public IEnumerable<Entity> Investigators => Group(Game, "investigators");
    public IEnumerable<Entity> Locations => Group(Game, "locations");
    // enemies in play: a defeated one the core keeps out of play (OutOfPlay, with its old doom and damage) is not counted —
    // QA 11-20: the Midnight Masks' skull counted a defeated cultist's doom (table −2, core −1)
    public IEnumerable<Entity> Enemies => Group(Game, "enemies").Where(e => J.Tag(J.Get(e.Raw, "placement")) != "OutOfPlay");
    public IEnumerable<Entity> Assets => Group(Game, "assets");
    // concealed mini-cards (Scarlet Keys): the core's "concealed" map, keyed by id; placement AtLocation <id>
    public IEnumerable<Entity> Concealed => Group(Game, "concealed");
    public IEnumerable<Entity> ConcealedAt(string locationId) => Concealed.Where(c => J.Tag(J.Get(c.Raw, "placement")) == "AtLocation" && J.String(J.Get(c.Raw, "placement"), "contents") == locationId);
    public IEnumerable<Entity> Treacheries => Group(Game, "treacheries");
    public Entity? Act => Group(Game, "acts").FirstOrDefault();
    public Entity? Agenda => Group(Game, "agendas").FirstOrDefault();
    public IEnumerable<Entity> Agendas => Group(Game, "agendas");   // 黑星升起 has two agendas at once
    public JsonElement Scenario => J.Get(J.Get(Game, "mode"), "That");
    public string Phase => J.String(Game, "phase");
    public bool IsOver => J.Tag(J.Get(Game, "gameState")) == "IsOver";
    public JsonElement SkillTest => J.Get(Game, "skillTest");
    public bool InSkillTest => SkillTest.ValueKind == JsonValueKind.Object;

    public Entity? Location(string? id) => id is null ? null : Locations.FirstOrDefault(l => l.Id == id);
    public string? LocationOf(Entity inv) { var p = J.Get(inv.Raw, "placement"); var c = J.Get(p, "contents"); return c.ValueKind == JsonValueKind.String ? c.GetString() : null; }
    public Entity? MyLocation => Me is null ? null : Location(LocationOf(Me));
    public string Label(Entity loc) => J.String(loc.Raw, "label");
    public bool Revealed(Entity loc) => J.Get(loc.Raw, "revealed").ValueKind == JsonValueKind.True;
    public int Tokens(Entity e, string token) => ClientDisplay.Token(e.Raw, token) ?? 0;
    public string InvestigatorId(Entity inv) => inv.Id;

    public IEnumerable<Entity> EnemiesAt(string locationId) => Enemies.Where(e => J.Tag(J.Get(e.Raw, "placement")) == "AtLocation" && J.String(J.Get(e.Raw, "placement"), "contents") == locationId);
    // Enemies "at a location" as the rules count them: those placed there plus those engaged with an investigator
    // there (an engaged enemy sits in the threat area; the core counted the Gathering's engaged ghoul for its skull).
    public IEnumerable<Entity> EnemiesHere(string locationId) => EnemiesAt(locationId).Concat(InvestigatorsAt(locationId).SelectMany(EngagedWith)).DistinctBy(e => e.Id);
    public IEnumerable<Entity> EngagedWith(Entity inv) => Enemies.Where(e => J.Items(J.Get(e.Raw, "engagedInvestigators")).Any(x => x.GetString() == inv.Id)
        || (J.Tag(J.Get(e.Raw, "placement")) == "InThreatArea" && J.String(J.Get(e.Raw, "placement"), "contents") == inv.Id));
    // An asset placed in a threat area: when the rules core gives it one of the investigator's slots (The Necronomicon takes a
    // hand slot) it is shown in that slot like any asset (Yunke 11-19: weakness and encounter assets sit in their slots); one
    // without a slot stays with the threats.
    static bool InThreatArea(Entity e) => J.Tag(J.Get(e.Raw, "placement")) == "InThreatArea";
    bool Slotted(Entity a) => Investigators.Any(i => SlotOf(i, a.Id).Length > 0);
    public IEnumerable<Entity> AssetsOf(Entity inv) => Assets.Where(a => J.String(a.Raw, "controller") == inv.Id && (!InThreatArea(a) || SlotOf(inv, a.Id).Length > 0));
    // Assets lying at a location with no controller (e.g. Lita Chantler in the parlor, waiting to be won over).
    public IEnumerable<Entity> AssetsAt(string locationId) => Assets.Where(a => J.Tag(J.Get(a.Raw, "placement")) == "AtLocation" && J.String(J.Get(a.Raw, "placement"), "contents") == locationId);
    // a Hidden ("隐私") treachery the core keeps secretly in a player's hand (placement HiddenInHand, The Path to Carcosa's
    // 脑海中的低语): drawn in that player's hand like the html version (Player.vue inHandTreacheries), not in the threat area
    public IEnumerable<Entity> HiddenInHand(Entity inv) => Treacheries.Where(t => J.Tag(J.Get(t.Raw, "placement")) == "HiddenInHand" && J.String(J.Get(t.Raw, "placement"), "contents") == inv.Id);
    public IEnumerable<Entity> ThreatsOf(Entity inv) => Treacheries.Where(t => J.String(J.Get(t.Raw, "placement"), "contents") == inv.Id && J.Tag(J.Get(t.Raw, "placement")) != "HiddenInHand")
        .Concat(Assets.Where(a => InThreatArea(a) && J.String(J.Get(a.Raw, "placement"), "contents") == inv.Id && !Slotted(a)));
    public bool IsAsset(Entity e) => Assets.Any(a => a.Id == e.Id);
    // the anchor key of a card in a threat area: its own kind (an asset's abilities are offered on "asset:<id>")
    public string ThreatKey(Entity e) => (IsAsset(e) ? "asset:" : "tr:") + e.Id;
    // Cards attached to a location, an act or an agenda (rules-core placement AttachedToLocation / AttachedToAct /
    // AttachedToAgenda, or NextToAct / NextToAgenda): Obscuring Fog on a place, a card on the agenda (Yunke 11-20 #20).
    public IEnumerable<Entity> Attached(string tag, string? id) => Treacheries.Concat(Assets).Concat(Group(Game, "events"))
        .Where(t => J.Tag(J.Get(t.Raw, "placement")) == tag && (id is null || J.String(J.Get(t.Raw, "placement"), "contents") == id));
    public int PrintedShroud(Entity loc) => Calc(J.Get(loc.Raw, "shroud"));
    public IEnumerable<Entity> InvestigatorsAt(string locationId) => Investigators.Where(i => LocationOf(i) == locationId);

    // Investigator slots from the rules core: [["HandSlot", [{assets:[...]}, ...]], ...] → (kind, free, total), kind = hand/arcane/…
    public IEnumerable<(string Kind, int Free, int Total)> Slots(Entity inv)
    {
        foreach (var pair in J.Items(J.Get(inv.Raw, "slots")))
        {
            if (pair.ValueKind != JsonValueKind.Array || pair.GetArrayLength() != 2) continue;
            var kind = (pair[0].GetString() ?? "").Replace("Slot", "").ToLowerInvariant();
            var slots = J.Items(pair[1]).ToArray();
            if (slots.Length == 0) continue;
            yield return (kind, slots.Count(s => !J.Items(J.Get(s, "assets")).Any()), slots.Length);
        }
    }
    // Which slot kind an asset occupies (first match), or "".
    // The core's slots of one kind ("hand", "arcane" …), each with the asset ids it holds — a two-handed asset (长柄大锤) is
    // listed in two hand slots
    public List<string[]> SlotEntries(Entity inv, string kind)
    {
        var res = new List<string[]>();
        foreach (var pair in J.Items(J.Get(inv.Raw, "slots")))
            if (pair.ValueKind == JsonValueKind.Array && pair.GetArrayLength() == 2 && (pair[0].GetString() ?? "").Replace("Slot", "").ToLowerInvariant() == kind)
                foreach (var s in J.Items(pair[1])) res.Add(J.Items(J.Get(s, "assets")).Select(a => a.GetString() ?? "").Where(a => a.Length > 0).ToArray());
        return res;
    }
    public string SlotOf(Entity inv, string assetId)
    {
        foreach (var pair in J.Items(J.Get(inv.Raw, "slots")))
            if (pair.ValueKind == JsonValueKind.Array && pair.GetArrayLength() == 2 && J.Items(pair[1]).Any(s => J.Items(J.Get(s, "assets")).Any(a => a.GetString() == assetId)))
                return (pair[0].GetString() ?? "").Replace("Slot", "").ToLowerInvariant();
        return "";
    }
    // Uses left on an asset (ammo, supplies, charges, secrets, resources).
    // Uses and other counters on an asset: ammo, supplies, charges, secrets, resources — and any other kind the card keeps
    // (饥饿霉菌's growth, evidence…; Yunke 11-22). Damage, horror, doom and clues are drawn as their own tokens.
    static readonly HashSet<string> OwnTokens = new() { "Damage", "Horror", "Doom", "Clue" };
    static string? UsesKind(Entity a) => J.Items(J.Get(a.Raw, "tokens"))
        .Where(p => p.ValueKind == JsonValueKind.Array && p.GetArrayLength() == 2 && p[1].ValueKind == JsonValueKind.Number && p[1].GetInt32() > 0)
        .Select(p => p[0].GetString()).FirstOrDefault(t => t is not null && !OwnTokens.Contains(t));
    public static int Uses(Entity a) => UsesKind(a) is { } kind ? J.Items(J.Get(a.Raw, "tokens")).Where(p => p.ValueKind == JsonValueKind.Array && p.GetArrayLength() == 2 && p[0].GetString() == kind).Sum(p => p[1].GetInt32()) : 0;
    // its name from the official locale (token.Growth = 生长); the five common kinds as before
    public static Func<string, string>? TokenName;
    public static string UsesName(Entity a) => UsesKind(a) switch
    {
        "Ammo" => "弹药", "Supply" => "补给", "Charge" => "充能", "Secret" => "秘密", "Resource" => "资源", null => "",
        var k => TokenName?.Invoke(k) is { Length: > 0 } n && n != k ? n : k,
    };

    public IEnumerable<(string Id, string Code)> Hand(Entity inv) => J.Items(J.Get(inv.Raw, "hand"))
        .Select(c => J.Get(c, "contents")).Select(c => (J.String(c, "id"), CoreCatalog.Normalize(J.String(c, "cardCode"))));
    public int DeckSize(Entity inv) => J.Int(inv.Raw, "deckSize");
    // 乔·戴蒙德's hunch deck (the core's investigator "decks": [["HunchDeck", [card…]]]), top card first; null when the
    // investigator has none. Its top card is face up only once revealed (revealedHunchCard = its id), as in the html version
    // (Player.vue hunchDeck / topOfHunchDeckRevealed) — local-joe-hunch: it was not shown at all.
    public List<(string Id, string Code)>? HunchDeck(Entity inv)
    {
        foreach (var pair in J.Items(J.Get(inv.Raw, "decks")))
            if (pair.ValueKind == JsonValueKind.Array && pair.GetArrayLength() == 2 && pair[0].ValueKind == JsonValueKind.String && pair[0].GetString() == "HunchDeck")
                return J.Items(pair[1]).Select(c => J.Get(c, "contents").ValueKind == JsonValueKind.Object ? J.Get(c, "contents") : c)
                    .Select(c => (J.String(c, "id"), CoreCatalog.Normalize(J.String(c, "cardCode")))).ToList();
        return null;
    }
    public string RevealedHunchCard(Entity inv) => J.String(inv.Raw, "revealedHunchCard");
    // Discard entries with their card ids (newest first), so a discard card's own choices can be matched to it.
    public IEnumerable<(string Id, string Code)> DiscardCards(Entity inv) => J.Items(J.Get(inv.Raw, "discard")).Select(c =>
    {
        var o = J.Get(c, "contents").ValueKind == JsonValueKind.Object ? J.Get(c, "contents") : c;
        return (J.String(o, "id"), CoreCatalog.Normalize(J.String(o, "cardCode")));
    });
    public IEnumerable<string> Discard(Entity inv) =>J.Items(J.Get(inv.Raw, "discard")).Select(c => CoreCatalog.Normalize(J.String(J.Get(c, "contents").ValueKind == JsonValueKind.Object ? J.Get(c, "contents") : c, "cardCode")));
    public int Stat(Entity inv, string k) => J.Int(inv.Raw, k);
    // A skill as it stands, with the core's modifiers on the investigator applied (the html version's Investigator.vue
    // does the same): base override (BaseSkillOf / BaseSkill during a test), + SkillModifier, + AnySkillValue during a test,
    // + ActionSkillModifier for the test's action, or SetSkillValue outright. k = willpower / intellect / combat / agility.
    public int SkillValue(Entity inv, string k)
    {
        string skill = "Skill" + char.ToUpperInvariant(k[0]) + k[1..];
        var test = SkillTest; bool inTest = test.ValueKind == JsonValueKind.Object;
        bool active = inTest && J.Items(J.Get(test, "skills")).Any(x => x.GetString() == skill);
        string action = inTest ? J.String(test, "action") : "";
        int? baseOverride = null, set = null; int plus = 0;
        foreach (var mod in J.Items(J.Get(inv.Raw, "modifiers")))
        {
            var t = J.Get(mod, "type");
            switch (J.Tag(t))
            {
                case "BaseSkillOf": if (J.String(t, "skillType") == skill) baseOverride = J.Int(t, "value"); break;
                case "BaseSkill": if (active) baseOverride = J.Int(t, "contents"); break;
                case "SkillModifier": if (J.String(t, "skillType") == skill) plus += J.Int(t, "value"); break;
                case "AnySkillValue": if (active) plus += J.Int(t, "contents"); break;
                case "ActionSkillModifier": if (J.String(t, "skillType") == skill && inTest && J.String(t, "action") == action) plus += J.Int(t, "value"); break;
                case "SetSkillValue": if (J.String(t, "skillType") == skill) set = J.Int(t, "value"); break;
            }
        }
        return set ?? (baseOverride ?? Stat(inv, k)) + plus;
    }
    public int Health(Entity inv) => J.Int(inv.Raw, "health");
    public int Sanity(Entity inv) => J.Int(inv.Raw, "sanity");
    public int Damage(Entity e) => Tokens(e, "Damage");
    public int Horror(Entity e) => Tokens(e, "Horror");
    public int Resources(Entity inv) => Tokens(inv, "Resource");
    public int DiscardCount(Entity inv) => J.Items(J.Get(inv.Raw, "discard")).Count();
    public int Clues(Entity e) => Tokens(e, "Clue");
    public int Doom(Entity e) => Tokens(e, "Doom") + (J.Get(e.Raw, "doom").ValueKind == JsonValueKind.Number ? J.Int(e.Raw, "doom") : 0);
    public int Actions(Entity inv) => J.Int(inv.Raw, "remainingActions");
    public string Name(Entity e) => S.Catalog.ShownName(e.Raw, e.Code);   // an unrevealed location: its unrevealed side's name (QQ #34)

    // Investigators in the game, for values printed "per investigator" (the core's PerPlayer), e.g. the Ghoul Priest's 5 health.
    public static int Players = 1;
    public static int Calc(JsonElement v)
    {
        // GameValueCalculation{contents:{tag:Static,contents:n}} | {tag:PerPlayer,contents:n} | n
        // PerPlayer is n × investigators: showing the bare n made the priest look dead at 5 damage (playtest 2 #15).
        int mult = 1;
        for (int i = 0; i < 6 && v.ValueKind == JsonValueKind.Object; i++) { if (J.Tag(v) == "PerPlayer") mult *= Math.Max(1, Players); v = J.Get(v, "contents"); }
        return v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out int n) ? n * mult : -1;
    }
    public int Shroud(Entity loc) => Modified(loc, Calc(J.Get(loc.Raw, "shroud")), "ShroudModifier", "SetShroud");
    // Enemy values as they stand: the printed value with the core's modifiers on the enemy (EnemyFight +n, HealthModifier…).
    public int EnemyFight(Entity e) => Modified(e, Calc(J.Get(e.Raw, "fight")), "EnemyFight", null);
    public int EnemyEvade(Entity e) => Modified(e, Calc(J.Get(e.Raw, "evade")), "EnemyEvade", null);
    public int EnemyHealth(Entity e) => Modified(e, Calc(J.Get(e.Raw, "health")), "HealthModifier", null);
    static int Modified(Entity e, int printed, string plusTag, string? setTag)
    {
        if (printed < 0) return printed;   // no value (e.g. a massive enemy without fight)
        int v = printed;
        foreach (var mod in J.Items(J.Get(e.Raw, "modifiers")))
        {
            var t = J.Get(mod, "type"); string tag = J.Tag(t);
            var c = J.Get(t, "contents");
            int n = c.ValueKind == JsonValueKind.Number ? c.GetInt32() : c.ValueKind == JsonValueKind.Array && c.GetArrayLength() > 0 && c[0].ValueKind == JsonValueKind.Number ? c[0].GetInt32() : 0;
            if (tag == plusTag || tag == plusTag + "WithMin") v += n;
            else if (setTag is not null && tag == setTag) return n;
        }
        return Math.Max(0, v);
    }
    public int DoomThreshold => Agenda is null ? 0 : AgendaThreshold(Agenda);
    // An agenda's doom against its threshold, the way the rules core decides whether it advances (Game.hs, the agenda's
    // threshold check; Helpers/Doom.hs): the threshold with its DoomThresholdModifiers; the doom in play (the view's
    // totalDoom) minus the doom on the other agendas (黑星升起's two decks); and for an agenda with OtherDoomSubtracts
    // (真相幻影's nights while conviction outweighs doubt) its own doom minus every other doom in play.
    static IEnumerable<JsonElement> ModTypes(Entity e) => J.Items(J.Get(e.Raw, "modifiers")).Select(m => J.Get(m, "type"));
    public int AgendaThreshold(Entity ag) => ModTypes(ag).Where(t => J.Tag(t) == "DoomThresholdModifier")
        .Aggregate(Calc(J.Get(ag.Raw, "doomThreshold")), (acc, t) => Math.Max(0, acc + J.Int(t, "contents")));
    public bool OtherDoomSubtracts(Entity ag) => ModTypes(ag).Any(t => J.Tag(t) == "OtherDoomSubtracts");
    public int AgendaDoomCount(Entity ag)
    {
        int own = Doom(ag);
        if (OtherDoomSubtracts(ag)) return own - (SubtractDoomCount - own);
        return TotalDoom - Agendas.Where(o => o.Id != ag.Id).Sum(o => Doom(o));
    }
    // every doom in play counted as adding (getSubtractDoomCount): assets (less IgnoreDoomOnThis), enemies, events,
    // locations, treacheries, agendas and investigators still in the game
    public int SubtractDoomCount
    {
        get
        {
            int assets = Assets.Sum(a => { int d = Doom(a); int ignore = ModTypes(a).Where(t => J.Tag(t) == "IgnoreDoomOnThis").Sum(t => J.Int(t, "contents")); return d - Math.Min(ignore, d); });
            int sum = assets + Enemies.Sum(Doom) + Group(Game, "events").Sum(Doom) + Locations.Sum(Doom) + Treacheries.Sum(Doom) + Agendas.Sum(Doom)
                + Investigators.Where(i => J.Get(i.Raw, "eliminated").ValueKind != JsonValueKind.True).Sum(Doom);
            return Math.Max(0, sum);
        }
    }
    public int TotalDoom => J.Int(Game, "totalDoom");
    public string[] Layout => J.Items(J.Get(Scenario, "locationLayout")).Select(r => r.GetString() ?? "").ToArray();
    public string Difficulty => J.String(Scenario, "difficulty");
    public IEnumerable<string> ChaosFaces => J.Items(J.Get(J.Get(Scenario, "chaosBag"), "chaosTokens")).Select(t => J.String(t, "chaosTokenFace"));
    public IEnumerable<string> RevealedTokens => J.Items(J.Get(Game, "skillTestChaosTokens")).Select(t => J.String(t, "chaosTokenFace"))
        .Concat(InSkillTest ? J.Items(J.Get(SkillTest, "revealedChaosTokens")).Select(t => J.String(t, "chaosTokenFace")) : Enumerable.Empty<string>())
        .Concat(FreshTestTokens.Select(t => t.Face)).Distinct();
    // A token just drawn in a test while its "when revealed" window is open (温蒂·亚当斯's reaction, SkillTestFastWindow2): the
    // core holds it only among the focused tokens — the test's own lists are still empty (QQ #28: the panel showed no token, so
    // the reaction looked as if it came before the draw). As the html version (SkillTest.vue focusedChaosTokens): the focused
    // tokens with a revealer that the test does not list yet.
    public List<(string Face, string Id, string By)> FreshTestTokens
    {
        get
        {
            var list = new List<(string, string, string)>();
            if (!InSkillTest) return list;
            var known = J.Items(J.Get(Game, "skillTestChaosTokens")).Concat(J.Items(J.Get(SkillTest, "revealedChaosTokens"))).Select(t => J.String(t, "chaosTokenId")).ToHashSet();
            var f = J.Get(Game, "focusedChaosTokens"); if (f.ValueKind != JsonValueKind.Array) f = J.Get(Game, "gameFocusedChaosTokens");
            foreach (var t in J.Items(f))
                if (J.String(t, "chaosTokenFace") is { Length: > 0 } face && J.String(t, "chaosTokenRevealedBy") is { Length: > 0 } by && !known.Contains(J.String(t, "chaosTokenId")))
                    list.Add((face, J.String(t, "chaosTokenId"), by));
            return list;
        }
    }
    public int EncounterDeckSize => J.Int(Game, "encounterDeckSize");
    public IEnumerable<string> EncounterDiscard => J.Items(J.Get(Scenario, "discard")).Select(c => CoreCatalog.Normalize(J.String(J.Get(c, "contents").ValueKind == JsonValueKind.Object ? J.Get(c, "contents") : c, "cardCode")));
    public IEnumerable<string> VictoryDisplay => J.Items(J.Get(Scenario, "victoryDisplay")).Select(c => CoreCatalog.Normalize(ClientDisplay.CardCode(c)));

    // Where a choice points on the table: "hand:<cardId>", "loc:<id>", "enemy:<id>", "conc:<id>" (concealed mini-card), "asset:<id>", "inv:<id>", "act", "agenda", "tr:<id>" or "".
    public string Anchor(ChoicePresentation c)
    {
        var raw = c.Raw;
        switch (c.Tag)
        {
            case "TargetLabel": return Target(J.Get(raw, "target"));
            case "FightLabel": case "FightLabelWithSkill": case "EvadeLabel": case "EvadeLabelWithSkill": return FightTarget(J.String(raw, "enemyId"));
            case "EngageLabel": return "enemy:" + J.String(raw, "enemyId");
            case "PortraitLabel": return "inv:" + J.String(raw, "investigatorId");
            case "ComponentLabel": case "AuxiliaryComponentLabel":
                var comp = J.Get(raw, "component");
                return J.Tag(comp) == "AssetComponent" ? "asset:" + J.String(comp, "assetId") : "inv:" + J.String(comp, "investigatorId");
            case "AbilityLabel": return Source(J.Get(J.Get(raw, "ability"), "source"));
        }
        return "";
    }
    // A fight or evade choice names what it hits: an enemy, a concealed mini-card, a location or an asset (the core's FightLabel
    // carries any of their ids). Before, all of them were "enemy:<id>", and a concealed mini-card has no enemy node on the board,
    // so the phone could not tap it (bug #16: the enchanted sword on an 8-cycle 隐匿 mini-card).
    string FightTarget(string id) =>
        Enemies.Any(e => e.Id == id) ? "enemy:" + id
        : Concealed.Any(c => c.Id == id) ? "conc:" + id
        : Locations.Any(l => l.Id == id) ? "loc:" + id
        : Assets.Any(a => a.Id == id) ? "asset:" + id
        : "enemy:" + id;
    string Target(JsonElement t)
    {
        var id = J.Get(t, "contents"); string s = id.ValueKind == JsonValueKind.String ? id.GetString()! : "";
        return J.Tag(t) switch
        {
            "CardIdTarget" => "hand:" + s, "LocationTarget" => "loc:" + s, "EnemyTarget" => "enemy:" + s, "AssetTarget" => "asset:" + s,
            "InvestigatorTarget" => "inv:" + s, "ActTarget" => "act", "AgendaTarget" => "agenda", "TreacheryTarget" => "tr:" + s, _ => ""
        };
    }
    string Source(JsonElement src)
    {
        for (int i = 0; i < 6; i++)
        {
            string tag = J.Tag(src);
            if (tag == "AbilitySource") { var a = J.Get(src, "contents"); src = a.ValueKind == JsonValueKind.Array && a.GetArrayLength() > 0 ? a[0] : default; continue; }
            if (tag == "ProxySource") { src = J.Get(src, "source"); continue; }
            var id = J.Get(src, "contents"); string s = id.ValueKind == JsonValueKind.String ? id.GetString()! : "";
            return tag switch
            {
                "LocationSource" => "loc:" + s, "EnemySource" => "enemy:" + s, "AssetSource" => "asset:" + s, "InvestigatorSource" => "inv:" + s,
                "ActSource" => "act", "AgendaSource" => "agenda", "TreacherySource" => "tr:" + s, "EventSource" => "", "CardIdSource" => "hand:" + s,
                "ConcealedCardSource" => "conc:" + s,   // a concealed mini-card's own attack / evade (QQ #44)
                _ => ""
            };
        }
        return "";
    }

    public string ActionVerb(ChoicePresentation c) => c.Tag == "AbilityLabel" ? QuestionPresenter.AbilityActions(J.Get(c.Raw, "ability")).FirstOrDefault() ?? "" : "";
}
