using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Arkham.NativeHost;

namespace Gathering;

// Round: the scenario's round from the save (the player view the core sends has no round count; 11-22: the table read
// "roundCount" from it, always 0, so the round never showed and drawn weaknesses were never turned over)
// Shuffled: the investigators whose deck the core shuffled in this step (it logs no shuffle; 11-22: after 研究馆员's search the
// deck was shuffled, but nothing on the table said so, and three tomes that happened to land on top looked like no shuffle)
public sealed record TableState(Guid Id, Guid Player, long Sequence, long Revision, JsonElement View, ClientSeat[] Seats, long? ActionStartSequence = null, long? PreviousSequence = null, int Round = 0, string[]? Shuffled = null)
{
    public JsonElement Game => View.GetProperty("game");
    public JsonElement Question => J.Get(J.Get(Game, "question"), Player.ToString("D"));
}

// Codes follow the saved player-seat order; an empty code is an unselected seat.
// The title is fixed when the game is created, so a campaign save would keep its first chapter's name; the rest says
// where the game stands now: the core's campaign id ("" for a standalone scenario), the scenario it is in or about to
// start, the campaign step tag, the phase, the round and whether the game is over.
public sealed record SavedGameSummary(string Id, string Title, long Sequence, string[] InvestigatorCodes,
    string CampaignId = "", string ScenarioCode = "", string Step = "", string Phase = "", int Round = 0, bool Over = false);

// This prototype owns its own database. It never discovers installed profiles,
// logs into an account or connects to a production service.
public sealed class PrototypeBackend : IAsyncDisposable, IGameBackend
{
    readonly NativeCoreWorker core;
    readonly LocalGameStore store;
    readonly LocalGameController controller;
    readonly CoreCatalog catalog;
    public string Root { get; }
    // diagnostics (the game routes it to its log)
    public static Action<string> Trace = Console.WriteLine;
    public double LastOperationMs { get; private set; }
    public PrototypeBackend(string root, string dataDirectory, string? assetsDirectory = null)
    {
        Root = Path.GetFullPath(root);
        catalog=new CoreCatalog(assetsDirectory is null ? Path.Combine(Root,"app/assets/data/core-cards.json") : Path.Combine(assetsDirectory,"data","core-cards.json"));   // the exported game keeps its assets elsewhere
        var config = JsonDocument.Parse(File.ReadAllText(Path.Combine(Root, "runtime/core.json"))).RootElement;
        Directory.CreateDirectory(dataDirectory);
        NativeSqlite.Load(Path.Combine(Root, "runtime/sqlite3.dll"));
        store = new LocalGameStore(Path.Combine(dataDirectory, "gathering.sqlite"));
        removedFile = Path.Combine(dataDirectory, "removed-saves.json");
        dbFile = Path.Combine(dataDirectory, "gathering.sqlite"); dataDir = dataDirectory;
        var identity = config.GetProperty("sourceId").GetString()!;
        core = new NativeCoreWorker(Path.Combine(Root, "runtime/arkham_core.dll"), identity);
        controller = new LocalGameController(core, store, identity);
        // saves hidden by the earlier "hide only" deletion are not erased by themselves (that would be a permanent deletion the
        // player did not ask for at this point): they show in the list again and can be deleted there
    }
    public Task Ready => core.Ready;
    public async Task<JsonElement> BuildSchema(string investigator)
    {
        if (!CoreCatalog.IsInvestigator(investigator)) throw new InvalidOperationException("规则核心不支持这名调查员。");
        return await Pure("deckBuildSchema", new { investigatorCode = investigator, mode = "initial" });
    }
    // The rules core's deck-building schema for this very deck: its taboo list (taboo_id), its investigator sides
    // (meta alternate_front / alternate_back: parallel investigators) and its deck-building choices (the meta keys the
    // core lists in selectionFields: faction_selected, deck_size_selected, option_selected …).
    public async Task<JsonElement> SchemaFor(JsonElement deck)
    {
        string investigator = J.String(deck, "investigator_code");
        if (!CoreCatalog.IsInvestigator(investigator)) throw new InvalidOperationException("规则核心不支持这名调查员。");
        JsonObject meta; try { meta = StandaloneDecks.Metadata(deck); } catch { meta = new(); }
        string? Text(string key) => meta[key] is JsonValue v && v.TryGetValue<string>(out var s) && s.Length > 0 ? s : null;
        int? taboo = J.Get(deck, "taboo_id").ValueKind == JsonValueKind.Number && J.Int(deck, "taboo_id") > 0 ? J.Int(deck, "taboo_id") : null;
        string? front = Text("alternate_front"), back = Text("alternate_back");
        if (front == investigator) front = null; if (back == investigator) back = null;
        var first = await Pure("deckBuildSchema", new { investigatorCode = investigator, mode = "initial", tabooId = taboo, alternateFront = front, alternateBack = back, selections = new Dictionary<string, string>() });
        var selections = J.Items(J.Get(first, "selectionFields")).Select(f => J.String(f, "key")).Where(k => Text(k) is not null).ToDictionary(k => k, k => Text(k)!);
        if (selections.Count == 0) return first;
        return await Pure("deckBuildSchema", new { investigatorCode = investigator, mode = "initial", tabooId = taboo, alternateFront = front, alternateBack = back, selections });
    }
    // Cards the core's deck builder calls unsupported only because its English card-text cache ("shipped Oct15 Build cache")
    // lacks a printed field, while the rules core itself plays them. With such a card in it the core checks a deck only
    // provisionally (no deck-size or copy-limit errors), so its construction is judged by build (arkham.build, the
    // workshop's own rules; Yunke 11-20: "组卡的规则直接去0712的build系统里看"), and the standalone experience rules and any
    // other card the core cannot run still apply. Exact code, owner and reason only.
    //   03009 Sophie (Mark Harrigan's signature): "missingEnglishFields 03009: back_text". Played 2026-11-20 with the rules
    //   probe and in game: in play at setup, [fast] ability takes 1 damage, Forced flip to 03009b at 5 damage, game goes on
    //   (evidence/build/r17mark).
    static readonly Dictionary<string,(string Detail,string Owner)> TextOnlyCards=new(){["03009"]=("03009: back_text","03001")};
    // build's construction check, set by the app (the build engine runs there): valid, and its errors in Chinese
    public Func<JsonElement,Task<(bool Valid,string[] Errors)>>? BuildCheck;
    static readonly HashSet<string> StandaloneCodes=new(){"standaloneMetadata","standaloneExperience","additionalWeaknesses"};
    public Task<JsonElement> ValidateDeck(string investigator, string schemaId, JsonElement deck)=>ValidateDeck(investigator,schemaId,deck,true);
    // standalone: checked for a scenario played on its own (the standalone experience rules, StandaloneDecks.Prepare); a
    // campaign's start and the workshop check the construction alone, with no experience limit (bug #36)
    public async Task<JsonElement> ValidateDeck(string investigator, string schemaId, JsonElement deck, bool standalone)
    {
        var slots=J.Get(deck,"slots");
        var held=TextOnlyCards.Keys.Where(c=>slots.ValueKind==JsonValueKind.Object&&slots.TryGetProperty(c,out _)).ToList();
        // only in its own investigator's deck (anywhere else the core's verdict stands)
        if(held.Count==0||BuildCheck is null||held.Any(c=>TextOnlyCards[c].Owner!=investigator))return await ValidateOnce(investigator,schemaId,deck,standalone);
        var with=await ValidateOnce(investigator,schemaId,deck,standalone);
        // the core must name these cards for exactly the cache reason and nothing else, or the deck stays unsupported
        var reasons=J.Items(J.Get(with,"unsupportedReasons")).Select(r=>J.String(r,"code")+"|"+J.String(r,"detail")).ToList();
        if(reasons.Any(r=>!held.Any(c=>r=="missingEnglishFields|"+TextOnlyCards[c].Detail)))return with;
        // the standalone rules (and any other card the core cannot run) from the core's check of the deck without these cards
        var without=JsonNode.Parse(deck.GetRawText())!.AsObject();
        foreach(var c in held)without["slots"]!.AsObject().Remove(c);
        var rest=JsonNode.Parse((await ValidateOnce(investigator,schemaId,JsonSerializer.SerializeToElement(without),standalone)).GetRawText())!.AsObject();
        if(rest["unsupportedReasons"] is JsonArray u&&u.Count>0){rest["deck"]=JsonNode.Parse(deck.GetRawText());return JsonSerializer.SerializeToElement(rest);}
        var errors=new JsonArray();
        foreach(var e in rest["errors"]!.AsArray())if(StandaloneCodes.Contains(e?["code"]?.ToString()??""))errors.Add(e!.DeepClone());
        var (valid,messages)=await BuildCheck(deck);
        foreach(var m in messages)errors.Add(new JsonObject{["code"]="build",["message"]=m});
        if(!valid&&messages.Length==0)errors.Add(new JsonObject{["code"]="build",["message"]="牌组不符合构筑规则"});
        // build does not count an investigator's required cards; each goes in at its deck limit
        foreach(var c in held)
        {
            int limit=catalog.Cards.TryGetValue(c,out var card)?J.Int(card,"deck_limit",1):1,count=slots.GetProperty(c).GetInt32();
            if(count!=limit)errors.Add(new JsonObject{["code"]="requiredSignature",["detail"]=c,["message"]="专属卡数量不对："+catalog.Name(c)+"应放 "+limit+" 张"});
        }
        rest["errors"]=errors;rest["unsupportedReasons"]=new JsonArray();rest.Remove("unsupportedText");rest.Remove("statistics");
        rest["status"]=errors.Count>0?"invalid":"valid";
        rest["deck"]=JsonNode.Parse(deck.GetRawText());
        rest["acceptedTextOnly"]=JsonNode.Parse(J.Get(with,"unsupportedReasons").GetRawText());
        rest["constructionValidation"]="arkham.build (build engine); the core's check is provisional with "+string.Join(",",held);
        return JsonSerializer.SerializeToElement(rest);
    }
    async Task<JsonElement> ValidateOnce(string investigator, string schemaId, JsonElement deck, bool standalone)
    {
        if (!CoreCatalog.IsCoreDeck(deck) || J.String(deck,"investigator_code") != investigator)
            return JsonSerializer.SerializeToElement(new { status="invalid", errors=new[]{new{code="corePool", message="牌组含有无法识别的卡牌或调查员。"}} });
        // the schema of this deck (taboo list, sides, selections), not the investigator's plain one
        var initial=await SchemaFor(deck);
        if(J.String(initial,"supportStatus") is {Length:>0} support&&support!="supported"&&J.Get(initial,"unsupportedReasons").GetArrayLength()>0)
            return JsonSerializer.SerializeToElement(new{status="unsupported",errors=Array.Empty<object>(),unsupportedReasons=J.Get(initial,"unsupportedReasons"),unsupportedText=UnsupportedText(JsonNode.Parse(J.Get(initial,"unsupportedReasons").GetRawText())!.AsArray())});
        var errors=new List<object>();int budget=0,spent=StandaloneDecks.Experience(catalog,deck);string[] extras=[];
        if(standalone)
        {
            try{budget=StandaloneDecks.Budget(deck);extras=StandaloneDecks.Extras(deck);}catch(Exception error){errors.Add(new{code="standaloneMetadata",message=error.Message});}
            int granted=StandaloneDecks.GrantedXp(deck),extraCount=StandaloneDecks.ExtraWeaknessCount(spent,granted);
            if(spent>49)errors.Add(new{code="standaloneExperience",message=StandaloneDecks.StandaloneLimitText(spent)});
            if(extras.Length!=extraCount)errors.Add(new{code="additionalWeaknesses",message="单场冒险模式的牌组需额外加入"+extraCount+"张随机基础弱点；请准备并校验牌组。"});
        }
        JsonElement construction;
        try{construction=StandaloneDecks.ConstructionInput(catalog,deck);}catch(Exception error){return JsonSerializer.SerializeToElement(new{status="invalid",errors=new[]{new{code="additionalWeaknesses",message=error.Message}}});}
        // The native upgrade validator intentionally marks purchase-history XP
        // unsupported, which also makes structural totals provisional. Use the
        // complete initial construction check and handle only its level-zero
        // restriction here under the explicit standalone XP rule instead.
        const string mode="initial";
        var schema=initial;
        var native=await Pure("deckBuildValidate",new{schema=J.Get(schema,"request"),schemaId=J.String(schema,"schemaId"),deck=construction});
        var result=JsonNode.Parse(native.GetRawText())!.AsObject();
        var nativeErrors=result["errors"]!.AsArray();
        if(spent>0)for(int i=nativeErrors.Count-1;i>=0;i--)if(nativeErrors[i]?["code"]?.GetValue<string>()=="initialLevel")nativeErrors.RemoveAt(i);
        // every native error in Chinese (the core words them in English): message, named cards from its detail
        foreach(var e in nativeErrors)if(e is JsonObject o&&o["message"] is null)o["message"]=NativeErrorText(o["code"]?.GetValue<string>()??"",o["detail"]?.ToJsonString()??"",investigator,result);
        foreach(var error in errors)nativeErrors.Add(JsonSerializer.SerializeToNode(error));
        // the core's own verdict on what it cannot run (cards not implemented, deck options it does not know): the gate for
        // later-cycle cards, worded in Chinese with the cards it names
        var unsupported=result["unsupportedReasons"]!.AsArray();
        // another investigator's signature cards (Daisy's tote bag in Joe Diamond's deck): the core calls them unsupported
        // (cardProvenanceUnsupported) and checks nothing else, so the player heard only "暂不支持" and none of the rest. Told
        // as what they are, with the core's verdict on the deck without them (its size is then off by them: not repeated).
        var foreign=unsupported.Select(r=>r?["code"]?.ToString()=="cardProvenanceUnsupported"?CoreCatalog.Normalize(r?["detail"]?.ToString()??""):"").ToList();
        if(unsupported.Count>0&&foreign.All(c=>c.Length>0&&SignatureOwner(c) is {} owner&&owner!=investigator))
        {
            var without=JsonNode.Parse(deck.GetRawText())!.AsObject();var ws=without["slots"]!.AsObject();
            foreach(var c in foreign){ws.Remove(c);ws.Remove("c"+c);}
            var rest=JsonNode.Parse((await ValidateOnce(investigator,schemaId,JsonSerializer.SerializeToElement(without),standalone)).GetRawText())!.AsObject();
            var all=new JsonArray{new JsonObject{["code"]="otherInvestigatorSignature",["detail"]=string.Join(",",foreign),
                ["message"]="其他调查员的专属卡不能放进这副牌组："+string.Join("、",foreign.Distinct().Select(c=>catalog.Name(c)+"（"+catalog.Name(SignatureOwner(c)!)+"）"))}};
            foreach(var e in rest["errors"]?.AsArray()??new JsonArray())if(e?["code"]?.ToString()!="deckSize")all.Add(e!.DeepClone());
            rest["errors"]=all;rest["status"]=rest["status"]?.ToString()=="unsupported"?"unsupported":"invalid";rest["deck"]=JsonNode.Parse(deck.GetRawText());
            return JsonSerializer.SerializeToElement(rest);
        }
        if(unsupported.Count>0)result["unsupportedText"]=UnsupportedText(unsupported);
        result["status"]=unsupported.Count>0?"unsupported":nativeErrors.Count>0?"invalid":"valid";
        result["deck"]=JsonNode.Parse(deck.GetRawText());result["constructionSchemaId"]=J.String(schema,"schemaId");result["schemaId"]=schemaId;
        result["standalone"]=JsonSerializer.SerializeToNode(new{spentXp=spent,budgetXp=budget,additionalWeaknesses=extras,constructionMode="initial structure plus standalone XP allowance",experienceValidation="prototype application layer; FFG standalone rule",constructionValidation="unchanged native rule DLL; initialLevel restriction handled only by standalone rule"});
        return JsonSerializer.SerializeToElement(result);
    }
    // "规则核心暂不支持：<cards>" from the core's unsupportedReasons (each reason names cards by code, or is a plain reason)
    string UnsupportedText(JsonArray reasons)
    {
        var names=new List<string>();var other=new List<string>();
        foreach(var r in reasons)
        {
            string raw=r?.ToJsonString()??"";
            var codes=System.Text.RegularExpressions.Regex.Matches(raw,@"(?<![0-9])c?(\d{5}[a-z]?)(?![0-9])").Select(m=>m.Groups[1].Value).Where(c=>catalog.Cards.ContainsKey(c)).Distinct().ToList();
            string code=r?["code"]?.ToString()??"";
            if(code=="sideDeckUnsupported")other.Add("副卡组");
            else if(codes.Count>0)names.AddRange(codes.Select(catalog.Name));else other.Add(raw);
        }
        // every reason is told, not only the ones naming cards (a side deck was left out and the message blamed Sophie alone)
        var parts=new List<string>();
        if(names.Count>0)parts.Add("这些卡牌："+string.Join("、",names.Distinct()));
        if(other.Contains("副卡组"))parts.Add("副卡组");
        if(other.Any(o=>o!="副卡组"))parts.Add("此牌组的部分设置");
        string text="规则核心暂不支持"+(parts.Count>0?string.Join("；",parts):"此牌组的部分设置");
        Trace("DECK_UNSUPPORTED "+string.Join(" | ",reasons.Select(r=>r?.ToJsonString())));
        return text;
    }
    // The investigator whose required (signature) cards include this card (deck_requirements.card, with its alternatives), or null
    string? SignatureOwner(string code)
    {
        foreach(var inv in catalog.Investigators)
            foreach(var req in J.Props(J.Get(J.Get(inv,"deck_requirements"),"card")))
                if(req.Name==code||J.Props(req.Value).Any(alt=>alt.Name==code))return J.String(inv,"code");
        return null;
    }
    // The investigator's 附加需求 as printed on its back (official Chinese), e.g. Joe Diamond: "你的牌库必须包括至少11张洞察事件(包括悬案)"
    string AdditionalRequirement(string investigator)
    {
        var back=catalog.Cards.TryGetValue(CoreCatalog.Normalize(investigator),out var c)?J.String(c,"back_text"):"";
        int at=back.IndexOf("附加需求");if(at<0)return "";
        var t=System.Text.RegularExpressions.Regex.Replace(back[at..],@"<[^>]+>|\[\[|\]\]","");
        t=t.Replace("附加需求：","").Replace("附加需求:","").Trim();
        int end=t.IndexOf('。');return end>0?t[..end]:t;
    }
    // A native construction error in Chinese, with the cards its detail names (codes from the rules core, Native/DeckBuild:
    // Validate.hs, Investigators.hs, Attachments.hs). investigator / result: the deck's, for the requirement texts and counts.
    string NativeErrorText(string code, string detail, string investigator = "", JsonObject? result = null)
    {
        var names=System.Text.RegularExpressions.Regex.Matches(detail,@"(?<![0-9])c?(\d{5}[a-z]?)(?![0-9])").Select(m=>m.Groups[1].Value).Where(catalog.Cards.ContainsKey).Distinct().Select(catalog.Name).ToArray();
        string cards=names.Length>0?"："+string.Join("、",names):"";
        var nums=System.Text.RegularExpressions.Regex.Matches(detail,@"\d+").Select(m=>m.Value).ToArray();
        string plain=detail.Trim('"');
        switch(code)
        {
            case "deckSize" when plain.StartsWith("Expected")&&nums.Length>=2: return "牌组张数不符合要求：需要 "+nums[0]+" 张，现在 "+nums[1]+" 张";
            case "atLeast":
            {
                string req=AdditionalRequirement(investigator);
                // Joe Diamond: how many insight events the deck holds, from the core's own hunch deck candidates
                int have=-1;
                foreach(var f in result?["attachments"]?["fields"]?.AsArray()??new JsonArray())
                    if(f?["ownerCode"]?.ToString()=="05002")have=f["candidates"]?.AsArray().Sum(x=>x?["maxQuantity"]?.GetValue<int>()??0)??-1;
                return req.Length>0?"不满足调查员的附加需求："+req+(have>=0?"；这副牌组只有 "+have+" 张":""):"牌组不满足调查员的附加需求";
            }
            case "copyLimit": { var n=names.Length>0?string.Join("、",names):catalog.NameFromEnglish(plain); return "同名卡数量超过上限"+(n.Length>0?"："+n:""); }
            case "virtualLimit" when nums.Length>=2: return "某类卡牌超过上限：最多 "+nums[0]+" 张，现在 "+nums[1]+" 张";
            case "deckOptionsCapacity": return "有些卡牌超出了这名调查员的牌库构建选项（职业、等级或张数）";
            case "requiredSignature" or "requiredCards" or "missingRequiredCard": return "缺少调查员的专属卡"+cards;
            case "investigatorRestricted": return "这些卡是其他调查员专用的"+cards;
            case "excludedCard": return "这名调查员不能使用这些卡"+cards;
            case "basicWeakness": return "随机基础弱点的数量不对";
            case "attachmentRequiredQuantity": return "直觉牌库必须包括1张悬案";
            case "attachmentQuantity": return "预选的直觉牌超过11张";
            case "attachmentCandidate": return "预选的卡牌不在牌组中或不符合要求"+cards;
        }
        return code switch
        {
            "initialLevel"=>"开局牌组含有需要经验的卡牌或升级项"+cards,
            "parallelFrontRequirement"=>"平行调查员正面的组牌要求未满足"+cards,
            "parallelBackRequirement"=>"平行调查员背面的组牌要求未满足"+cards,
            "deckSize" or "tooFewCards" or "tooManyCards"=>"牌组张数不符合要求"+cards,
            "deckLimit"=>"同名卡数量超过上限"+cards,
            "forbidden" or "forbiddenCard"=>"牌组含有禁用的卡牌"+cards,
            "taboo" or "tabooForbidden"=>"禁卡表不允许这些卡牌"+cards,
            "customization" or "customizationInvalid"=>"可定制卡的升级项不合规"+cards,
            _=>"牌组不符合规则核心的构筑规则"+cards,
        };
    }
    // A validation result in one Chinese line: the core's unsupported verdict first, then each error's message.
    public static string Verdict(JsonElement validation)
    {
        if(J.String(validation,"unsupportedText").Length>0)return J.String(validation,"unsupportedText");
        var messages=J.Items(J.Get(validation,"errors")).Select(e=>J.String(e,"message",J.String(e,"code"))).Where(m=>m.Length>0).Distinct().ToArray();
        return messages.Length>0?string.Join("；",messages):"牌组未通过构筑校验";
    }
    // A side deck (ArkhamDB / arkham.build "sideSlots": cards set aside to buy later) is not part of the deck that is played;
    // the core refuses any deck that carries one ("sideDeckUnsupported"), so it is left out of what the table gets (Yunke 11-22:
    // an imported Mark deck would not start, and the message blamed Sophie). The saved deck keeps it.
    public Task<JsonElement> PrepareStandaloneDeck(JsonElement deck)=>Task.FromResult(StandaloneDecks.Prepare(catalog,WithoutSideDeck(deck)));
    // the deck as a game takes it: standalone (a scenario on its own) or a campaign's (bug #36)
    public Task<JsonElement> PrepareDeck(JsonElement deck,bool standalone)=>Task.FromResult(StandaloneDecks.Prepare(catalog,WithoutSideDeck(deck),standalone));
    Task<JsonElement> PrepareDeck(JsonElement deck,bool standalone,bool keepPlaceholder)=>Task.FromResult(StandaloneDecks.Prepare(catalog,WithoutSideDeck(deck),standalone,keepPlaceholder));
    // the game was started with the Boon of the Morrígan (gameSettings.settingsUltimatumsAndBoons)
    bool HasMorrigan(TableState basis)=>J.Items(J.Get(J.Get(J.Get(J.Get(SavedDocument(basis),"snapshot"),"game"),"gameSettings"),"settingsUltimatumsAndBoons")).Any(x=>x.ValueKind==JsonValueKind.String&&x.GetString()=="BoonOfTheMorrigan");
    // a game in campaign mode (the core's gameMode This): its decks are not held to the standalone rules
    public bool IsCampaign(TableState basis)=>J.Get(J.Get(J.Get(J.Get(SavedDocument(basis),"snapshot"),"game"),"gameMode"),"This").ValueKind==JsonValueKind.Object;
    static JsonElement WithoutSideDeck(JsonElement deck)
    {
        if(deck.ValueKind!=JsonValueKind.Object||!(deck.TryGetProperty("sideSlots",out _)||deck.TryGetProperty("side_slots",out _)))return deck;
        var o=JsonNode.Parse(deck.GetRawText())!.AsObject();o.Remove("sideSlots");o.Remove("side_slots");
        return JsonSerializer.SerializeToElement(o);
    }
    // diagnostics: one pure rules-core call with a raw request (QA probes of deckBuildSchema / deckBuildValidate)
    public Task<JsonElement> PureRaw(string method, JsonElement request) => Pure(method, request);
    async Task<JsonElement> Pure(string method, object request)
    {
        var response = await core.RequestAsync(new { apiVersion = 1, method, request }).ConfigureAwait(false);
        if (!response.GetProperty("ok").GetBoolean()) throw new InvalidOperationException(response.GetRawText());
        return response.GetProperty("result").Clone();
    }
    // Deleting a save from 继续调查 removes it for good: its rows in gathering.sqlite and its files under setups/ (SaveEraser;
    // Yunke 11-20). EraseHidden would erase the saves the earlier "hide only" deletion listed in removed-saves.json; it is not
    // called on its own.
    readonly string removedFile, dbFile, dataDir;
    public void DeleteGame(string id) => SaveEraser.Erase(Path.Combine(Root, "runtime/sqlite3.dll"), dbFile, dataDir, Guid.Parse(id));
    public Task DeleteGameAsync(string id) => Task.Run(() => DeleteGame(id));
    void EraseHidden()
    {
        if (!File.Exists(removedFile)) return;
        try
        {
            var ids = JsonSerializer.Deserialize<string[]>(File.ReadAllText(removedFile)) ?? Array.Empty<string>();
            foreach (var id in ids) if (Guid.TryParse(id, out _)) DeleteGame(id);
            File.Delete(removedFile);
        }
        catch (Exception e) { Trace("removed-saves.json: " + e.Message); }
    }
    public (string Id, string Title, long Sequence)[] Games() => store.ListGames().ToArray();
    public SavedGameSummary[] SavedGames()
    {
        return Games().Select(item =>
        {
            // Read the committed snapshot only. Listing portraits must not open a
            // rules view, choose a player or change the active investigation.
            var state=store.ReadCurrent(item.Id);
            using var save=JsonDocument.Parse(state.Save);
            var game=save.RootElement.GetProperty("snapshot").GetProperty("game");
            var seats=ClientSeats.Read(game);
            var investigators=J.Props(J.Get(J.Get(game,"gameEntities"),"investigators")).ToArray();
            var codes=seats.Select(seat =>
            {
                foreach(var actor in investigators)
                    if(Guid.TryParse(J.String(actor.Value,"playerId"),out var player)&&player==seat.Player)
                        return CoreCatalog.Normalize(J.String(actor.Value,"cardCode"));
                return "";
            }).ToArray();
            // The snapshot keeps the core's own field names (gameMode, gamePhase …), not the client view's.
            var mode=J.Get(game,"gameMode");
            var campaign=J.Get(mode,"This");
            var step=J.Get(campaign,"step");
            string stepTag=J.Tag(step);
            string scenario=CoreCatalog.Normalize(J.String(J.Get(mode,"That"),"id"));
            if(scenario.Length==0&&stepTag=="ScenarioStep")scenario=CoreCatalog.Normalize(J.String(step,"contents"));
            return new SavedGameSummary(item.Id,item.Title,state.Sequence,codes,
                J.String(campaign,"id"),scenario,stepTag,J.String(game,"gamePhase"),J.Int(game,"gameRoundCount"),
                J.Tag(J.Get(game,"gameGameState"))=="IsOver");
        }).ToArray();
    }
    public Task<TableState> Create(int seats)=>Create(seats,"Standard");
    public Task<TableState> Create(int seats,string difficulty)=>Create(seats,difficulty,null,"01104");
    // campaign "01" is Night of the Zealot: the core plays its scenarios in order, keeps the campaign log and experience,
    // and asks for deck upgrades between them. A campaign may start at a later scenario (scenario != null).
    public async Task<TableState> Create(int seats,string difficulty,string? campaign,string? scenario)=>await Create(seats,difficulty,campaign,scenario,null).ConfigureAwait(false);
    // variants: the web version's game options the core's CreateRequest takes (tarot readings, ultimatums and boons)
    public async Task<TableState> Create(int seats,string difficulty,string? campaign,string? scenario,GameVariants? variants)
    {
        ClientSeats.Count(seats);
        if(difficulty is not("Easy" or "Standard" or "Hard" or "Expert"))throw new InvalidOperationException("请选择简单、标准、困难或专家难度。");
        var id = Guid.NewGuid();
        var players = Enumerable.Range(0, seats).Select(_ => Guid.NewGuid().ToString("D")).ToArray();
        int seed = int.TryParse(Environment.GetEnvironmentVariable("ARKHAM_QA_SEED"), out int qaSeed) && qaSeed > 0 ? qaSeed : Random.Shared.Next(1, int.MaxValue);   // QA (AI comparison): a fixed seed from the environment
        var body=new Dictionary<string,object?>();
        if(campaign is null)body["scenario"]=scenario??"01104";
        else{body["campaign"]=campaign;if(scenario is not null)body["scenario"]=scenario;}
        if(variants is{Seed:>0})seed=variants.Seed;   // 新手教程: one fixed opening
        body["seed"]=seed;body["players"]=players;body["difficulty"]=difficulty;
        if(variants is{Tarot:true})body["tarot"]=true;
        if(variants is{}&&variants.Boons.Length>0)body["ultimatumsAndBoons"]=variants.Boons;
        JsonElement request=JsonSerializer.SerializeToElement(body);
        string title = campaign == "03"
            ? "卡尔克萨之路 · " + (scenario switch { "03061" => "最后的王者", "03120" => "往事回声", "03159" => "邪秽誓约", "03200" => "真相幻影", "03240" => "苍白面具", "03274" => "黑星升起", "03316" => "卡城幽影", _ => "谢幕" }) + " · " + seats + "人"
            : campaign == "02"
            ? "敦威治遗产 · " + (scenario switch { "02062" => "百赌百胜", "02118" => "博物馆之夜", "02159" => "疯狂列车", "02195" => "血色祭坛", "02236" => "无人知晓", "02274" => "毁灭之地", "02311" => "迷失时空", _ => "课外活动" }) + " · " + seats + "人"
            : (campaign == "01" ? "狂热之夜 · " : "") + (scenario == "01120" ? "午夜假面" : scenario == "01142" ? "地底噬魔" : "聚集于此") + " · " + seats + "人";
        await controller.CreateAsync(id, title, Guid.NewGuid(), request).ConfigureAwait(false);
        return await Open(id).ConfigureAwait(false);
    }
    // followAsked: when the selected seat has no question and another seat has one, open that seat instead — what the table
    // does right after anyway (TableScreen.AdvanceInner switches to the asked seat), so the view is generated once, not twice
    // (11-21 perf: 12 of 78 answers read the same state twice, about 30 ms each).
    // The table reads only these parts of the core's view (TableState.Game, and settingsSchema for the scenario / campaign
    // settings page). The full view also builds the debug panel's schema (100 KB), the trigger, zone and interaction schemas
    // and more: 36–43 ms in the core against about 7 ms for these two (perf 11-21, tools/rules-probe viewbench). QA's
    // dump-view and the debug panel ask for their own sections.
    static readonly string[] TableSections = { "game", "settingsSchema" };
    public async Task<TableState> Open(Guid id, Guid? selected = null, bool followAsked = false)
    {
        var w = Stopwatch.StartNew();
        var state = store.ReadCurrent(id.ToString("D"));
        double tRead = w.Elapsed.TotalMilliseconds;
        using var save = JsonDocument.Parse(state.Save);
        var seats = ClientSeats.Read(save.RootElement.GetProperty("snapshot").GetProperty("game"));
        var player = ClientSeats.Select(seats, selected);
        if (followAsked && seats.FirstOrDefault(s => s.Player == player) is { HasQuestion: false } && seats.FirstOrDefault(s => s.HasQuestion) is { } asked) player = asked.Player;
        double tSeats = w.Elapsed.TotalMilliseconds;
        var viewParts = new System.Collections.Concurrent.ConcurrentDictionary<string, double>();
        JsonElement view;
        using (HostTiming.Capture((name, ms, calls, bytes) => { if (ms >= 1) viewParts.AddOrUpdate(name, ms, (_, v) => v + ms); }))
            view = await controller.ViewAsync(id, player, TableSections).ConfigureAwait(false);
        double tView = w.Elapsed.TotalMilliseconds;
        var actionStart = FindActionStart(state); double tAction = w.Elapsed.TotalMilliseconds;
        var previous = FindPreviousStep(state);
        Perf("open seq=" + state.Sequence + " read=" + tRead.ToString("F0") + " seats=" + (tSeats - tRead).ToString("F0") + " view=" + (tView - tSeats).ToString("F0")
            + " actionStart=" + (tAction - tView).ToString("F0") + " prev=" + (w.Elapsed.TotalMilliseconds - tAction).ToString("F0") + " saveKB=" + state.Save.Length / 1024 + " " + string.Join(" ", viewParts.OrderBy(p => p.Key).Select(p => p.Key + "=" + p.Value.ToString("F0"))));
        int round = J.Int(save.RootElement.GetProperty("snapshot").GetProperty("game"), "gameRoundCount");
        return new(id, player, state.Sequence, state.CoreRevision, view, seats, actionStart, previous, round, ShuffledIn(state));
    }
    // The state just before the last answer on the active branch: every step can be undone, a test's chaos draw included.
    // A row written by a restore stands for the restored target, whose predecessor is that target's parent.
    // The decks the step that made this state shuffled: the cards still in a deck are in a different order relative to each other
    // (a draw, a card put on top or at the bottom keep it). Not for a restore (an undo is not a shuffle).
    string[] ShuffledIn(StoredState current)
    {
        try
        {
            if(Environment.GetEnvironmentVariable("SHUFFLE_DEBUG") is not null)Trace("SHUFFLE_ENTRY seq="+current.Sequence+" parent="+current.Parent+" restored="+current.RestoredFrom);
            if(current.RestoredFrom is not null||current.Parent is not long parent)return [];
            var now=Info(current);var was=Info(current.GameId,parent);
            var res=new List<string>();
            foreach(var(inv,deck)in now.Decks)
            {
                if(!was.Decks.TryGetValue(inv,out var before)||before.Length<2)continue;
                var keep=deck.Intersect(before).ToHashSet();
                var a=before.Where(keep.Contains).ToArray();var b=deck.Where(keep.Contains).ToArray();
                if(a.Length>=2&&!a.SequenceEqual(b))res.Add(inv);
            }
            if(res.Count>0)Trace("SHUFFLED seq="+current.Sequence+" "+string.Join(",",res));
            else if(Environment.GetEnvironmentVariable("SHUFFLE_DEBUG") is not null)Trace("SHUFFLE_CHECK seq="+current.Sequence+" parent="+parent+" decks="+string.Join(",",now.Decks.Select(d=>d.Key+":"+d.Value.Length+"/"+(was.Decks.TryGetValue(d.Key,out var bb)?bb.Length:-1))));
            return res.ToArray();
        }
        catch(Exception e){Trace("SHUFFLE_CHECK failed: "+e.Message);return [];}
    }
    long? FindPreviousStep(StoredState current)
    {
        var cursor=current;
        while(cursor.RestoredFrom is long restored)cursor=store.ReadState(current.GameId,restored);
        return cursor.Parent;
    }
    // Undo one step (Yunke: every step must be undoable). The randomness is refreshed, so a redrawn token differs.
    public async Task<TableState?> UndoStep(TableState basis)
    {
        var watch=Stopwatch.StartNew();
        var current=store.ReadCurrent(basis.Id.ToString("D"));
        if(current.Sequence!=basis.Sequence)throw new InvalidOperationException("牌桌已更新，请重新读取后撤回。");
        if(FindPreviousStep(current) is not long to)return null;
        return await RestoreTo(basis,current,to,watch);
    }
    // Follow the active history branch, not sequence-1: restoring creates a new
    // immutable row whose logical predecessor is the restored target's parent.
    // The core's action marker covers the complete action (including its test).
    // A preceding player action window also covers choosing a target before
    // BeginAction, and fast plays that do not create a BeginAction marker.
    // What the undo boundaries need from one stored state, read from its save once and kept (a stored state never changes):
    // walking back through a long phase no longer parses every save on the way each step (Yunke 11-21: long games slowed)
    sealed record StepInfo(long Sequence,long? Parent,long? RestoredFrom,bool Setup,bool InAction,long? Steps,string Phase,bool ActionWindow,
        long? UndoAction,long? UndoTurn,long? UndoRound,Dictionary<string,string[]> Decks,bool Mulligan,bool Lead);
    readonly Dictionary<(string,long),StepInfo> stepCache=new();
    StepInfo Info(StoredState row)
    {
        lock(stepCache)if(stepCache.TryGetValue((row.GameId,row.Sequence),out var hit))return hit;
        using var save=JsonDocument.Parse(row.Save);
        var g=J.Get(J.Get(save.RootElement,"snapshot"),"game");
        long? Num(string f){var v=J.Get(g,f);return v.ValueKind==JsonValueKind.Number&&v.TryGetInt64(out long x)?x:null;}
        var info=new StepInfo(row.Sequence,row.Parent,row.RestoredFrom,
            J.Get(g,"gameInSetup").ValueKind==JsonValueKind.True,J.Get(g,"gameInAction").ValueKind==JsonValueKind.True,
            Num("gameScenarioSteps"),J.String(g,"gamePhase"),
            J.Props(J.Get(g,"gameQuestion")).Any(p=>J.Tag(ClientQuestions.Unwrap(p.Value))=="PlayerWindowChooseOne"),
            Num("gameUndoActionStep"),Num("gameUndoTurnStep"),Num("gameUndoRoundStep"),
            J.Props(J.Get(J.Get(g,"gameEntities"),"investigators")).ToDictionary(p=>p.Name,p=>{var a=J.Get(p.Value,"contents").ValueKind==JsonValueKind.Object?J.Get(p.Value,"contents"):p.Value;return J.Items(J.Get(a,"deck")).Select(c=>J.String(J.Get(c,"contents").ValueKind==JsonValueKind.Object?J.Get(c,"contents"):c,"id")).ToArray();}),
            J.Props(J.Get(g,"gameQuestion")).Any(p=>p.Value.GetRawText().Contains("$label.doneMulligan",StringComparison.Ordinal)||p.Value.GetRawText().Contains("mulligan",StringComparison.OrdinalIgnoreCase)),
            J.Props(J.Get(g,"gameQuestion")).Any(p=>p.Value.GetRawText().Contains("chooseLeadInvestigator",StringComparison.Ordinal)));
        lock(stepCache)stepCache[(row.GameId,row.Sequence)]=info;
        return info;
    }
    StepInfo Info(string game,long seq)
    {
        lock(stepCache)if(stepCache.TryGetValue((game,seq),out var hit))return hit;
        return Info(store.ReadState(game,seq));
    }
    // a restore's row stands for the state it restored
    StepInfo Resolve(string game,StepInfo s){while(s.RestoredFrom is long r)s=Info(game,r);return s;}
    long? FindActionStart(StoredState current)
    {
        var now=Info(current);
        if(now.Setup)return null;
        long? marker=now.UndoAction;
        string phase=now.Phase;
        var cursor=Resolve(current.GameId,now);
        while(cursor.Parent is long parent)
        {
            var prior=Info(current.GameId,parent);
            if(prior.Setup)return null;
            bool idle=!prior.InAction;
            if(idle&&marker is long start&&prior.Steps==start)return prior.Sequence;
            if(idle&&phase=="InvestigationPhase"&&prior.Phase==phase&&prior.ActionWindow)return prior.Sequence;
            if(prior.Phase!=phase)return null;
            cursor=Resolve(current.GameId,prior);
        }
        return null;
    }
    // Turn and round boundaries: the most recent idle ancestor whose game-step count equals the core's marker
    // (gameUndoTurnStep / gameUndoRoundStep), the same rule the action boundary uses.
    long? FindStepBoundary(StoredState current,string field)
    {
        var now=Info(current);
        if(now.Setup)return null;
        if((field=="gameUndoTurnStep"?now.UndoTurn:now.UndoRound) is not long marker)return null;
        var cursor=Resolve(current.GameId,now);
        while(cursor.Parent is long parent)
        {
            var prior=Info(current.GameId,parent);
            if(prior.Setup)return null;
            if(!prior.InAction&&prior.Steps==marker)return prior.Sequence;
            cursor=Resolve(current.GameId,prior);
        }
        return null;
    }
    // Scenario start: the earliest state on the active branch that is past setup.
    // The scenario's start for an undo: just before this scenario's opening hands are dealt (Yunke 11-22: first "回到选择卡牌弃置
    // 的时候", then "起始手牌选择之前" so the hands are dealt anew), else, as before, the first state after its setup.
    long? FindScenarioStart(StoredState current)
    {
        long? found=null,mulligan=null,lead=null;bool inSetup=false;
        var cursor=Resolve(current.GameId,Info(current));
        if(cursor.Mulligan)mulligan=cursor.Sequence;
        if(cursor.Lead)lead=cursor.Sequence;
        while(cursor.Parent is long parent)
        {
            var prior=Resolve(current.GameId,Info(current.GameId,parent));
            if(prior.Setup){inSetup=true;if(prior.Mulligan)mulligan=prior.Sequence;if(prior.Lead)lead=prior.Sequence;cursor=prior;continue;}
            if(inSetup)break;   // before this scenario's setup: an earlier scenario or the campaign
            if(prior.Mulligan)mulligan=prior.Sequence;
            found=prior.Sequence;
            cursor=prior;
        }
        // the step before the first opening-hand question: the hands are not dealt yet there, and a restore draws its randomness
        // anew, so the hands come out different (Yunke 11-22: "撤回到起始手牌选择之前，现在的时点手牌已经随机好了")
        if(mulligan is long mm&&Info(current.GameId,mm).Parent is long beforeDeal)mulligan=Resolve(current.GameId,Info(current.GameId,beforeDeal)).Sequence;
        // earliest of all: the choice of the lead investigator, when this scenario asks it (Yunke 11-22: "一关最早的一步应该就是
        // 选队长之前，撤回整个冒险的选项也应该是撤到这一步")
        long? target=lead??mulligan??found;
        return target is long f&&f!=current.Sequence?f:null;
    }
    // Rewinds to the start of the named boundary: "action", "turn", "round" or "scenario".
    public async Task<TableState> UndoBoundary(TableState basis,string boundary)
    {
        var watch=Stopwatch.StartNew();
        var current=store.ReadCurrent(basis.Id.ToString("D"));
        if(current.Sequence!=basis.Sequence)throw new InvalidOperationException("牌桌已更新，请重新读取后撤回。");
        long? target=boundary switch
        {
            "action"=>FindActionStart(current),
            "turn"=>FindStepBoundary(current,"gameUndoTurnStep"),
            "round"=>FindStepBoundary(current,"gameUndoRoundStep"),
            "scenario"=>FindScenarioStart(current),
            _=>null
        };
        if(target is not long to)throw new InvalidOperationException("当前没有可以撤回到这里的节点。");
        return await RestoreTo(basis,current,to,watch);
    }
    // Undo everything from a log entry onwards: restore to the state that row started from.
    public async Task<TableState> UndoToLogRow(TableState basis,long rowSequence)
    {
        var watch=Stopwatch.StartNew();
        var current=store.ReadCurrent(basis.Id.ToString("D"));
        if(current.Sequence!=basis.Sequence)throw new InvalidOperationException("牌桌已更新，请重新打开日志。");
        var row=store.ReadState(current.GameId,rowSequence);
        // A row written by a restore has no parent of its own; its logical predecessor is the restored target's parent.
        long? predecessor=row.Parent??(row.RestoredFrom is long restored?store.ReadState(current.GameId,restored).Parent:null);
        if(predecessor is not long parent)throw new InvalidOperationException("这条记录之前没有可以回到的状态。");
        return await RestoreTo(basis,current,parent,watch);
    }
    async Task<TableState> RestoreTo(TableState basis,StoredState current,long target,Stopwatch watch)
    {
        var targetRow=store.ReadState(current.GameId,target);
        using var saved=JsonDocument.Parse(targetRow.Save);
        var seats=ClientSeats.Read(saved.RootElement.GetProperty("snapshot").GetProperty("game"));
        // keep the viewed seat when it is asked something there; otherwise the seat that is asked (playtest 2 #14)
        var player=seats.FirstOrDefault(s=>s.HasQuestion&&s.Player==basis.Player)?.Player??seats.FirstOrDefault(s=>s.HasQuestion)?.Player??basis.Player;
        await controller.RestoreAsync(basis.Id,Guid.NewGuid(),basis.Sequence,target,refreshRandom:true).ConfigureAwait(false);
        var result=await Open(basis.Id,player).ConfigureAwait(false);
        LastOperationMs=watch.Elapsed.TotalMilliseconds;
        return result;
    }
    // Rewinds to a state the table already had (the basis of a trial), for lookahead: the trial's row is left behind in history.
    public async Task<TableState> RevertTo(TableState trial, long sequence)
    {
        var watch=Stopwatch.StartNew();
        var current=store.ReadCurrent(trial.Id.ToString("D"));
        if(current.Sequence!=trial.Sequence)throw new InvalidOperationException("牌桌已更新，请重新读取。");
        return await RestoreTo(trial,current,sequence,watch);
    }
    public async Task<TableState> UndoAction(TableState basis)
    {
        var watch=Stopwatch.StartNew();
        var current=store.ReadCurrent(basis.Id.ToString("D"));
        if(current.Sequence!=basis.Sequence)throw new InvalidOperationException("牌桌已更新，请重新读取后撤回。");
        long target=FindActionStart(current)??throw new InvalidOperationException("当前没有可撤回的行动。");
        var targetRow=store.ReadState(current.GameId,target);
        using var saved=JsonDocument.Parse(targetRow.Save);
        var seats=ClientSeats.Read(saved.RootElement.GetProperty("snapshot").GetProperty("game"));
        var player=seats.FirstOrDefault(s=>s.HasQuestion)?.Player??basis.Player;
        await controller.RestoreAsync(basis.Id,Guid.NewGuid(),basis.Sequence,target,refreshRandom:true).ConfigureAwait(false);
        var result=await Open(basis.Id,player).ConfigureAwait(false);
        LastOperationMs=watch.Elapsed.TotalMilliseconds;
        return result;
    }
    // timings of the backend's steps (Yunke 11-21: a long game must stay fast); written to the game's log
    public static void Perf(string what) => Trace("PERF " + what);
    public async Task<TableState> Answer(TableState basis, JsonElement answer, bool followAsked = false)
    {
        var watch = Stopwatch.StartNew();
        // the native host's own timing points (HostTiming: each core request, the save's export / serialize / hash, the sqlite
        // commit), summed by name, so "answer core" splits into the rules, the save and the database (perf 11-21)
        var parts = new System.Collections.Concurrent.ConcurrentDictionary<string, double>();
        using (HostTiming.Capture((name, ms, calls, bytes) => parts.AddOrUpdate(name, ms, (_, v) => v + ms)))
            await controller.AnswerAsync(basis.Id, Guid.NewGuid(), basis.Sequence, basis.Player, answer).ConfigureAwait(false);
        Perf("answer core=" + watch.Elapsed.TotalMilliseconds.ToString("F0") + " " + string.Join(" ", parts.OrderBy(p => p.Key).Select(p => p.Key + "=" + p.Value.ToString("F0"))));
        var result = await Open(basis.Id, basis.Player, followAsked).ConfigureAwait(false);
        LastOperationMs = watch.Elapsed.TotalMilliseconds;
        return result;
    }
    // QA / testing (Yunke 11-18: build the situation to test directly instead of playing up to it). The native host's debug
    // command — the html table's debug panel (createCard, setForcedDraws, adjustTest, setRevealStrategy …) — is applied
    // through the core like any other change, recorded in the game's history and undoable. Never used in normal play.
    // QA / testing: a save built for the situation to test (e.g. a real campaign save whose next step was set to a later
    // chapter) is imported as a new game; the core checks it like any import. Never used in normal play.
    public async Task<Guid> ImportCrafted(JsonElement save, string title)
    {
        var id = Guid.NewGuid();
        await controller.ImportAsync(id, title, Guid.NewGuid(), save).ConfigureAwait(false);
        return id;
    }
    // QA: one part of the core's view that the table does not keep (TableSections), e.g. dump-view:zoneSchema
    public async Task<JsonElement> ViewSection(TableState basis, string section)
    {
        var view = await controller.ViewAsync(basis.Id, basis.Player, new[] { section }).ConfigureAwait(false);
        return J.Get(view, section).ValueKind == JsonValueKind.Undefined ? default : J.Get(view, section).Clone();
    }
    public async Task<JsonElement> DebugSchema(TableState basis)
    {
        await controller.SetDebugEnabledAsync(true).ConfigureAwait(false);
        var view = await controller.ViewAsync(basis.Id, basis.Player, new[] { "debugSchema" }).ConfigureAwait(false);
        return J.Get(view, "debugSchema").ValueKind == JsonValueKind.Undefined ? view : J.Get(view, "debugSchema").Clone();
    }
    public async Task<TableState> Debug(TableState basis, JsonElement command)
    {
        var watch = Stopwatch.StartNew();
        await controller.SetDebugEnabledAsync(true).ConfigureAwait(false);
        var current = store.ReadCurrent(basis.Id.ToString("D"));
        if (current.Sequence != basis.Sequence) throw new InvalidOperationException("牌桌已更新，请重新读取后再调试。");
        await controller.DebugAsync(basis.Id, Guid.NewGuid(), current.Sequence, current.Sha256, current.CoreRevision, basis.Player, command).ConfigureAwait(false);
        var result = await Open(basis.Id, basis.Player).ConfigureAwait(false);
        LastOperationMs = watch.Elapsed.TotalMilliseconds;
        return result;
    }
    public Task<TableState> Choice(TableState basis, int index, bool followAsked = false)
        => Answer(basis, ClientQuestions.IndexAnswer(index, basis.Player, null), followAsked);
    public async Task<TableState> SelectDeck(TableState basis, JsonElement deck)
    {
        if (!CoreCatalog.IsCoreDeck(deck)) throw new InvalidOperationException("牌组含有无法识别的卡牌或调查员。");
        bool standalone=!IsCampaign(basis);
        // 莫莉根的恩赐: the deck goes with its placeholder (the core offers the weaknesses), but is checked as a drawn deck — the
        // deck check itself counts 01000 as unresolved ("randomWeaknessUnresolved")
        bool morrigan=HasMorrigan(basis);
        var prepared=await PrepareDeck(deck,standalone,morrigan);var investigator=J.String(prepared,"investigator_code");
        var checkedDeck=morrigan?await PrepareDeck(deck,standalone):prepared;
        var schema=await BuildSchema(investigator);var validation=await ValidateDeck(investigator,J.String(schema,"schemaId"),checkedDeck,standalone);
        if(J.String(validation,"status")!="valid"){Trace("DECK_REJECTED "+validation.GetRawText());throw new InvalidOperationException("牌组未能开始："+Verdict(validation));}
        Trace("DECK_SELECTED standalone="+standalone+" xp="+StandaloneDecks.Experience(catalog,prepared)+" extras="+StandaloneDecks.Extras(prepared).Length+" "+J.String(prepared,"meta"));
        return await Answer(basis, JsonSerializer.SerializeToElement(new { tag = "DeckListAnswer", deckList = prepared, playerId = basis.Player.ToString("D") }));
    }
    // Between scenarios: the investigator's deck after the player's upgrades (or unchanged). The core checks it and
    // records the experience; nothing standalone (budget metadata, extra random weaknesses) is added here.
    // The same answer in the core's own upgrade form (as KeepDeck, with the new list): the core prices the change itself.
    public Task<TableState> UpgradeDeck(TableState basis, string investigatorId, JsonElement deck)
    {
        if (!CoreCatalog.IsCoreDeck(deck)) throw new InvalidOperationException("牌组含有无法识别的卡牌或调查员。");
        deck = StandaloneDecks.ForCore(WithoutSideDeck(deck));   // the side deck stays with the save (TableScreen.CurrentSideSlots)
        return Answer(basis, JsonSerializer.SerializeToElement(new { tag = "UpgradeDeckAnswer", contents = new { investigatorId, deck } }));
    }
    public Task<TableState> UpgradeDeck(TableState basis, JsonElement deck)
    {
        if (!CoreCatalog.IsCoreDeck(deck)) throw new InvalidOperationException("牌组含有无法识别的卡牌或调查员。");
        deck = StandaloneDecks.ForCore(WithoutSideDeck(deck));
        return Answer(basis, JsonSerializer.SerializeToElement(new { tag = "DeckListAnswer", deckList = deck, playerId = basis.Player.ToString("D") }));
    }
    // "Keep my deck": the core's own form for an unchanged upgrade (no deck list). Re-sending a list that differs from the
    // core's current deck in any way makes the core charge experience for the difference.
    public Task<TableState> KeepDeck(TableState basis, string investigatorId)
        => Answer(basis, JsonSerializer.SerializeToElement(new { tag = "UpgradeDeckAnswer", contents = new { investigatorId, deck = (object?)null } }));
    // A killed or insane investigator is replaced (rules: that player continues the campaign with a new investigator not used
    // in it before, with a new deck). The core asks ChooseUpgradeDeck and accepts ReplaceInvestigatorAnswer with a complete deck
    // (reference Native/Answer.hs validateReplacementRequest); the deck is prepared like a starting deck (random weakness …).
    public async Task<TableState> ReplaceInvestigator(TableState basis, string investigatorId, JsonElement deck)
    {
        if (!CoreCatalog.IsCoreDeck(deck)) throw new InvalidOperationException("牌组含有无法识别的卡牌或调查员。");
        // a replacement joins a campaign: no standalone experience rules (bug #36)
        var prepared=await PrepareDeck(deck,false);var investigator=J.String(prepared,"investigator_code");
        var schema=await BuildSchema(investigator);var validation=await ValidateDeck(investigator,J.String(schema,"schemaId"),prepared,false);
        if(J.String(validation,"status")!="valid"){Trace("DECK_REJECTED "+validation.GetRawText());throw new InvalidOperationException("牌组不能使用："+Verdict(validation));}
        return await Answer(basis, JsonSerializer.SerializeToElement(new { tag = "ReplaceInvestigatorAnswer", contents = new { investigatorId, deck = prepared } }));
    }
    // Investigators this campaign has already used (card codes as the core writes them, "c02001"): those at the table now,
    // retired ones and replaced ones (reference Native/Answer.hs usedCampaignInvestigators). None of them may replace.
    public HashSet<string> UsedInvestigators(TableState basis)
    {
        var game = J.Get(J.Get(SavedDocument(basis),"snapshot"),"game");
        var used = new HashSet<string>();
        foreach (var p in J.Props(J.Get(J.Get(game,"gameEntities"),"investigators"))) used.Add(p.Name);
        foreach (var p in J.Props(J.Get(game,"gameRetiredInvestigators"))) used.Add(p.Name);
        foreach (var r in J.Items(J.Get(J.Get(J.Get(game,"gameMode"),"This"),"replacedInvestigators"))) if (r.ValueKind == JsonValueKind.String) used.Add(r.GetString()!);
        return used;
    }
    // The investigator's deck as the core holds it now (card code → copies), from the committed save.
    public Dictionary<string,int> CurrentDeck(TableState basis, string investigatorId)
    {
        var save = SavedDocument(basis);
        var game = J.Get(J.Get(save,"snapshot"),"game");
        var inv = J.Get(J.Get(J.Get(game,"gameEntities"),"investigators"), investigatorId);
        // between scenarios the campaign's own record is the deck (gameMode.This.decks); the investigator entity also
        // holds story cards the campaign keeps apart (Across Space and Time …), and sending those back would make the
        // core charge experience for them as if they were bought
        var campaignDeck = J.Get(J.Get(J.Get(J.Get(game,"gameMode"),"This"),"decks"), investigatorId);
        var cards = campaignDeck.ValueKind == JsonValueKind.Array ? campaignDeck : J.Get(inv,"deck");
        var slots = new Dictionary<string,int>();
        foreach (var card in J.Items(cards))
        {
            string code = J.String(card,"cardCode"); if (code.Length == 0) code = J.String(J.Get(card,"contents"),"cardCode");
            code = CoreCatalog.Normalize(code); if (code.Length == 0) continue;
            slots[code] = slots.GetValueOrDefault(code) + 1;
        }
        return slots;
    }
    // At a campaign continuation the player may choose to upgrade first: the core then asks ChooseUpgradeDeck per seat.
    public Task<TableState> BeginUpgrade(TableState basis)
        => Answer(basis, JsonSerializer.SerializeToElement(new { tag = "BeginUpgradeAnswer" }));
    public Task<TableState> DefaultSettings(TableState basis)
    {
        var schema=J.Get(basis.View,"settingsSchema");
        return Answer(basis,ClientSettings.Answer(schema,ClientSettings.Defaults(schema)));
    }
    public Task<TableState> Amounts(TableState basis,IReadOnlyDictionary<string,int> values)
        => Answer(basis,ClientQuestions.AmountAnswer(basis.Question,values,basis.Player,null));
    public Task<TableState> Continue(TableState basis)
    {
        var next=J.Get(ClientDisplay.Continuation(basis.Game),"nextStep");
        if(next.ValueKind!=JsonValueKind.Object)throw new InvalidOperationException("规则核心没有提供可继续的步骤。");
        return Answer(basis,JsonSerializer.SerializeToElement(new{tag="CampaignStepAnswer",contents=next}));
    }
    public JsonElement SavedDocument(TableState basis)
    {
        var state=store.ReadCurrent(basis.Id.ToString("D"));
        if(state.Sequence!=basis.Sequence)throw new InvalidOperationException("界面已过期，请重新读取后导出。");
        using var doc=JsonDocument.Parse(state.Save);return doc.RootElement.Clone();
    }
    // Yunke 11-19 r10: the cards whose forced abilities fired in the steps after `since` (newest state back to it, on the
    // active branch), from each state's log events ("log.triggersForcedAbility" with its source). The table shakes the ones
    // that are weaknesses, so an ability the core settles by itself (Rex's Curse) is still seen.
    public Task<(string Code, string Id)[]> ForcedSources(TableState basis,long since)=>Task.Run(()=>
    {
        var found=new List<(string,string)>();
        var cursor=store.ReadCurrent(basis.Id.ToString("D"));
        if(cursor.Sequence!=basis.Sequence||since>=cursor.Sequence)return found.ToArray();
        long seq=cursor.Sequence;
        for(int hops=0;hops<24&&seq>since;hops++)
        {
            var row=store.ReadState(cursor.GameId,seq);
            try
            {
                using var doc=JsonDocument.Parse(row.Events);
                foreach(var seat in J.Items(doc.RootElement))
                {
                    if(J.String(seat,"recipient") is var who&&who.Length>0&&who!=basis.Player.ToString("D"))continue;
                    foreach(var ev in J.Items(J.Get(seat,"events")))
                    foreach(var part in J.Items(J.Get(J.Get(ev,"entry"),"body")))
                    {
                        var c=J.Get(part,"contents");
                        if(c.ValueKind!=JsonValueKind.Array||c.GetArrayLength()<2||c[0].GetString()!="log.triggersForcedAbility")continue;
                        var src=J.Get(J.Get(c[1],"source"),"contents");
                        string code=J.String(src,"cardCode");
                        if(code.Length>0)found.Add((code,J.String(src,"entityId")));
                    }
                }
            }
            catch(JsonException){}
            if(row.Parent is not long parent)break;
            seq=parent;
        }
        found.Reverse();
        return found.ToArray();
    });
    readonly Dictionary<(string,long),ClientLogState> logCache=new();
    public Task<ClientLogHistory> Logs(TableState basis)=>Task.Run(()=>
    {
        var lw=Stopwatch.StartNew(); int reads=0;
        var current=store.ReadCurrent(basis.Id.ToString("D"));
        if(current.Sequence!=basis.Sequence)throw new InvalidOperationException("牌桌已更新，请重新打开日志。");
        var history=ClientLogs.Read(current.GameId,current.SourceId,current.Sequence,basis.Player,n=>
        {
            // a stored state never changes once written: each is read once (its log part only, not the whole save) and kept
            // (Yunke 11-21: a long game slowed down — every log read went through all of its states again, saves included)
            lock(logCache)
            {
                if(logCache.TryGetValue((current.GameId,n),out var hit))return hit;
                reads++;var row=store.ReadLogState(current.GameId,n);using var events=JsonDocument.Parse(row.Events);
                var made=new ClientLogState(row.GameId,row.SourceId,row.Sequence,row.Parent,row.RestoredFrom,row.Kind,events.RootElement.Clone());
                logCache[(current.GameId,n)]=made;return made;
            }
        });
        if(store.ReadCurrent(current.GameId).Sha256!=current.Sha256)throw new InvalidOperationException("读取日志期间牌桌已更新，请重试。");
        Perf("logs rows="+history.Rows.Count+" reads="+reads+" ms="+lw.Elapsed.TotalMilliseconds.ToString("F0"));
        // the whole scenario (Yunke 11-22: undo anything in it, not only the last 80 entries): from the latest "scenario begins"
        // entry on; the sidebar folds it by round and draws only the rounds that are open
        var rows=history.Rows;int from=0;
        for(int i=rows.Count-1;i>=0;i--)
            if(rows[i].Entry is { } e&&e.Body.Count>0&&e.Body[0].Tag=="LogI18n"&&e.Body[0].Text=="log.scenarioBegins"){from=i;break;}
        var kept=rows.Skip(from).ToArray();
        if(kept.Length>6000)kept=kept[^6000..];
        Perf("logs kept="+kept.Length+" from="+from);
        return history with{Rows=kept};
    });
    public async ValueTask DisposeAsync()
    {
        await controller.DisposeAsync();
        store.Dispose();
        await core.DisposeAsync();
    }
}

// The web version's optional game rules at a game start (CreateRequest: tarot, ultimatumsAndBoons): tarot readings at each
// scenario's start, and the chosen ultimatums and boons by their core names (BoonOfAthena, UltimatumOfFailure …).
public sealed record GameVariants(bool Tarot,string[] Boons,int Seed=0)
{
    public static readonly GameVariants None=new(false,[]);
    public bool Any=>Tarot||Boons.Length>0;
}
