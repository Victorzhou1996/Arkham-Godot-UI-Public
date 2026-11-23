using System.Text.Json;
using System.Text.Json.Nodes;

namespace Gathering;

// Input preparation for a standalone adventure. This neither edits the rules
// DLL nor changes a running game. FFG Rules Reference, Standalone Mode p.19:
// https://images-cdn.fantasyflightgames.com/filer_public/50/be/50bed4be-034c-4ed5-9ce6-8509ce8d3352/ahc60_rules_reference_eng-compressed.pdf
public static class StandaloneDecks
{
    public const string BudgetKey="gathering_standalone_xp", ExtrasKey="gathering_standalone_extra_weaknesses";
    // the experience arkham.build counts for the deck (levels, customization marks, taboo changes), written by the
    // workshop when it saves a deck; the standalone rule uses the larger of it and our own count
    public const string BuildXpKey="gathering_build_xp";
    static readonly string[] OwnKeys={BudgetKey,ExtrasKey,BuildXpKey};
    // Experience a standalone deck needs: each card's level per copy, plus the marks bought on customizable cards
    // (meta cus_<code> = "index|xp|choice,…", one sheet per card), or build's own count when it is larger.
    public static int Experience(CoreCatalog catalog,JsonElement deck)
    {
        int xp=catalog.Experience(deck);
        JsonObject meta;try{meta=Metadata(deck);}catch{return xp;}
        foreach(var (key,value) in meta)
        {
            if(!key.StartsWith("cus_")||value is not JsonValue v||!v.TryGetValue<string>(out var text))continue;
            if(!CoreCatalog.Slots(deck).ContainsKey(key[4..]))continue;
            foreach(var entry in text.Split(',',StringSplitOptions.RemoveEmptyEntries)){var parts=entry.Split('|');if(parts.Length>1&&int.TryParse(parts[1],out int x))xp+=Math.Max(0,x);}
        }
        if(meta[BuildXpKey] is JsonValue b&&(b.TryGetValue<int>(out int bx)||b.TryGetValue<string>(out var bs)&&int.TryParse(bs,out bx)))xp=Math.Max(xp,bx);
        return xp;
    }
    // Experience a card gives the moment it is bought at deck creation (FFG text; the rules core's own list is the
    // reference cdGrantedXp, Helpers/Deck.hs initDeckXp, which earns and spends it when the deck is loaded):
    //   08125 危险处境 "承受总共2点肉体和/或精神创伤。然后，取得3经验值。"   11095 禁欲主义 "获得10经验值"
    //   53013 无法拒绝的提议 "在你成为本弱点的承受者时，获得2经验。"
    // So a starting deck may hold that much experience in cards on top of any standalone budget (Yunke 11-22: a deck
    // with 危险处境 and 3 XP of cards was refused "需要3经验，超过已选预算0").
    public static readonly IReadOnlyDictionary<string,int> GrantedXpCards=new Dictionary<string,int>{["08125"]=3,["11095"]=10,["53013"]=2};
    // Investigators whose back gives "额外经验值：剧本开始时你有额外5点经验值(这不影响你在单场冒险模式必须拿取的弱点数量)"
    // (Yunke 11-22: 马泰奥神父). The rules core does not count it anywhere (reference FatherMateo.hs has no experience), so the
    // starting deck may simply hold it in cards; a parallel Roland is the 01001 deck with build's alternate_back 90024.
    public static readonly IReadOnlyDictionary<string,int> BonusXpInvestigators=new Dictionary<string,int>{["04004"]=5,["09008"]=5,["90024"]=5};
    public static int BonusXp(JsonElement deck)
    {
        string inv=J.String(deck,"investigator_code"),back="";
        try{if(Metadata(deck)["alternate_back"] is JsonValue v&&v.TryGetValue<string>(out var b))back=b;}catch{}
        return BonusXpInvestigators.TryGetValue(back.Length>0?back:inv,out int x)?x:0;
    }
    // experience the deck has at creation besides any standalone budget: bought cards that give it, and the investigator's bonus
    public static int GrantedXp(JsonElement deck)=>BonusXp(deck)+CoreCatalog.Slots(deck).Sum(p=>GrantedXpCards.TryGetValue(p.Key,out int x)?x*Math.Max(0,p.Value):0);
    // the extra random basic weaknesses of the standalone rule follow the budget's experience, not what a card gave
    public static int ExtraWeaknessCount(int spent,int granted)=>Math.Max(0,spent-granted)/10;
    // FFG Rules Reference, Standalone Mode: "A player cannot include 50 or more experience worth of cards in a standalone deck."
    public static string StandaloneLimitText(int spent)=>"单场冒险模式（单独打一个剧本）的牌组不能包含50或更多经验的卡牌，这副牌组需要"+spent+"经验；战役中不受此限制。";
    static bool IntOf(JsonNode? node,out int value){value=0;return node is JsonValue v&&(v.TryGetValue<int>(out value)||v.TryGetValue<string>(out var s)&&int.TryParse(s,out value));}
    public static JsonObject Metadata(JsonElement deck)
    {
        var value=J.Get(deck,"meta");
        if(value.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)return new();
        // ArkhamDB exports an empty meta as ""
        if(value.ValueKind==JsonValueKind.String&&string.IsNullOrWhiteSpace(value.GetString()))return new();
        var node=value.ValueKind==JsonValueKind.String?JsonNode.Parse(value.GetString()!):JsonNode.Parse(value.GetRawText());
        return node as JsonObject??throw new InvalidOperationException("牌组元数据必须是JSON对象。");
    }
    // Any readable metadata object. Decks made in the workshop (arkham.build) carry build's own keys (faction_selected,
    // deck_size_selected, option_selected, alternate_front/back …); they go to the rules core unchanged, and the core's
    // validation says whether it supports them.
    public static bool HasOnlyPrototypeMetadata(JsonElement deck)
    {
        try{Metadata(deck);return true;}catch{return false;}
    }
    public const string Placeholder="01000";
    // arkham.build keeps bookkeeping fields the rules core does not know (it reports them as unknownDeckField). Empty
    // ones mean nothing for the rules and are dropped before the core sees the deck; a filled ignoreDeckLimitSlots or
    // exile_string does change the deck, so it stays and the core's verdict decides.
    public static JsonElement ForCore(JsonElement deck)
    {
        var node=JsonNode.Parse(deck.GetRawText())!.AsObject();
        foreach(var key in new[]{"problem","source","xp","xp_spent","xp_adjustment"})node.Remove(key);
        if(node["exile_string"] is null||node["exile_string"]!.ToJsonString() is "\"\"")node.Remove("exile_string");
        if(node["ignoreDeckLimitSlots"] is not JsonObject ign||ign.Count==0||ign.All(p=>p.Value is null||p.Value.GetValue<int>()==0))node.Remove("ignoreDeckLimitSlots");
        // this game's own bookkeeping in the meta (arkhamui_kind / _campaign / _game / _result: which decks a finished campaign
        // left) means nothing to the rules; the core refused such a deck outright (deckMetaUnsupported, QA 11-20)
        StripOwnMeta(node);
        MergeExtraDeck(node);
        return JsonSerializer.SerializeToElement(node);
    }
    // arkham.build keeps 乔·戴蒙德's hunch deck apart (meta extra_deck "05010,09046,…"); the rules core refuses that field
    // (deckMetaUnsupported extra_deck / extraDeckUnsupported) and builds the hunch deck itself from the Insight events in the
    // main deck at setup (JoeDiamond.hs setupHunchDeck: all of them when there are 11, else the player picks 10 besides 悬案).
    // So the extra deck's cards go into the main slots and the field is dropped (local-joe-hunch: the deck could not start).
    static void MergeExtraDeck(JsonObject deck)
    {
        JsonObject? meta=deck["meta"] as JsonObject;bool asText=false;
        if(meta is null&&deck["meta"] is JsonValue mv&&mv.TryGetValue<string>(out var text)&&text.Contains("extra_deck")){meta=JsonNode.Parse(text) as JsonObject;asText=true;}
        if(meta?["extra_deck"] is not JsonValue ev||!ev.TryGetValue<string>(out var extra))return;
        meta.Remove("extra_deck");
        if(deck["slots"] is not JsonObject slots){slots=new JsonObject();deck["slots"]=slots;}
        var codes=extra.Split(',',StringSplitOptions.RemoveEmptyEntries|StringSplitOptions.TrimEntries);
        foreach(var code in codes)
            slots[code]=(slots[code] is JsonValue v&&v.TryGetValue<int>(out int n)?n:0)+1;
        // the eleven picked for the hunch deck (悬案 among them) are named as the core's own preselection, so the game uses
        // exactly those (DeckBuild/Attachments.hs: attachments_05002, 11 cards, one 05010); otherwise it asks at setup
        if(deck["investigator_code"]?.ToString()=="05002"&&meta["attachments_05002"] is null&&codes.Length==11&&codes.Count(c=>c=="05010")==1)
            meta["attachments_05002"]=string.Join(",",codes);
        if(asText)deck["meta"]=meta.Count==0?"":meta.ToJsonString();
    }
    const string OwnMetaPrefix="arkhamui_";
    static void StripOwnMeta(JsonObject deck)
    {
        var meta=deck["meta"];
        if(meta is JsonObject mo){foreach(var k in mo.Select(p=>p.Key).Where(k=>k.StartsWith(OwnMetaPrefix)).ToList())mo.Remove(k);return;}
        if(meta is JsonValue mv&&mv.TryGetValue<string>(out var text)&&text.Contains(OwnMetaPrefix))
        {
            if(JsonNode.Parse(text) is not JsonObject parsed)return;
            foreach(var k in parsed.Select(p=>p.Key).Where(k=>k.StartsWith(OwnMetaPrefix)).ToList())parsed.Remove(k);
            deck["meta"]=parsed.Count==0?"":parsed.ToJsonString();
        }
    }
    // The basic weaknesses a random draw can give: no reprints, no "Multiplayer only." / "Campaign Mode only." ones, and none
    // with a class restriction ("[guardian] investigator only." — The Scarlet Keys' 09124–09128): the rules core does not support
    // those restrictions yet and refuses the whole deck (unsupportedCardRestriction), which stopped a first start cold (QA 11-20).
    static string[]? weaknessPool;
    public static string[] WeaknessPool(CoreCatalog catalog)=>weaknessPool??=catalog.Cards.Values.Where(c=>IsBasicWeakness(catalog,J.String(c,"code"))&&J.String(c,"duplicate_of_code").Length==0
        &&!J.String(c,"real_text").StartsWith("Multiplayer only.")&&!J.String(c,"real_text").StartsWith("Campaign Mode only.")
        &&J.Get(c,"restrictions").ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
        .Select(c=>J.String(c,"code")).ToArray();
    // build's "random basic weakness" placeholder replaced by real basic weaknesses (one per copy), drawn the way the rules
    // say: from all basic weaknesses, none twice, skipping reprints, "Multiplayer only." / "Campaign Mode only." ones
    // and those restricted to other classes. Returns the slots unchanged when there is no placeholder.
    public static Dictionary<string,int> ReplacePlaceholder(CoreCatalog catalog,Dictionary<string,int> slots,string investigator)
    {
        if(!slots.TryGetValue(Placeholder,out int count)||count<1)return slots;
        var result=new Dictionary<string,int>(slots);result.Remove(Placeholder);
        var pool=WeaknessPool(catalog);
        for(int i=0;i<count;i++)
        {
            var available=pool.Where(c=>!result.ContainsKey(c)).ToArray();if(available.Length==0)available=pool;
            var code=available[Random.Shared.Next(available.Length)];result[code]=result.GetValueOrDefault(code)+1;
        }
        return result;
    }
    public static int Budget(JsonElement deck)
    {
        var meta=Metadata(deck);if(meta[BudgetKey] is null)return 0;
        if(!IntOf(meta[BudgetKey],out int budget)||budget is <0 or >49)throw new InvalidOperationException("独立剧本经验预算必须为0至49。");
        return budget;
    }
    public static string[] Extras(JsonElement deck)
    {
        var meta=Metadata(deck);if(meta[ExtrasKey] is null)return [];
        if(meta[ExtrasKey] is not JsonArray array||array.Count>4)throw new InvalidOperationException("额外基础弱点记录无效。");
        return array.Select(n=>n is JsonValue value&&value.TryGetValue<string>(out var code)?code:throw new InvalidOperationException("额外弱点编号无效。")).ToArray();
    }
    // standalone: the deck starts a scenario played on its own (standalone mode). Only then do the standalone rules apply:
    // fewer than 50 experience, a random basic weakness more for each 10 (Yunke 11-23, bug #36: the 50 experience limit
    // holds in standalone mode only, never in a campaign or the workshop). A campaign's start (and a replacement
    // investigator) takes the deck as it is.
    public static JsonElement Prepare(CoreCatalog catalog,JsonElement deck)=>Prepare(catalog,deck,true);
    // keepPlaceholder: the game has the Boon of the Morrígan (莫莉根的恩赐, 11-23): the core itself offers three random basic
    // weaknesses for each 01000 and the player sends one back, so the placeholder goes to the core as it is
    public static JsonElement Prepare(CoreCatalog catalog,JsonElement deck,bool standalone,bool keepPlaceholder=false)
    {
        if(!CoreCatalog.IsCoreDeck(deck))throw new InvalidOperationException("牌组含有无法识别的卡牌或调查员。");
        deck=ForCore(deck);
        // the core never receives build's placeholder: each 01000 becomes a random basic weakness first
        if(!keepPlaceholder&&CoreCatalog.Slots(deck).ContainsKey(Placeholder))
        {
            var node=JsonNode.Parse(deck.GetRawText())!.AsObject();
            node["slots"]=JsonSerializer.SerializeToNode(ReplacePlaceholder(catalog,CoreCatalog.Slots(deck),J.String(deck,"investigator_code")));
            deck=JsonSerializer.SerializeToElement(node);
        }
        if(!standalone)
        {
            // a campaign: no experience limit, no extra weaknesses; the standalone records of an earlier start are dropped
            var plain=JsonNode.Parse(deck.GetRawText())!.AsObject();var pm=Metadata(deck);pm.Remove(BudgetKey);pm.Remove(ExtrasKey);
            if(pm.Count==0)plain.Remove("meta");else plain["meta"]=pm.ToJsonString();
            return JsonSerializer.SerializeToElement(plain);
        }
        // the experience used is the deck's own (the cards it holds); the old budget no longer gates the start (bug #36: a
        // finished campaign's deck carries none, so every one of them was refused)
        int spent=Experience(catalog,deck),budget=Budget(deck),granted=GrantedXp(deck);
        if(spent>49)throw new InvalidOperationException(StandaloneLimitText(spent));
        var slots=CoreCatalog.Slots(deck);var previous=Extras(deck);
        foreach(var code in previous){if(!IsBasicWeakness(catalog,code)||!slots.TryGetValue(code,out int count)||count<1)throw new InvalidOperationException("已选额外弱点与牌组不一致。");if(count==1)slots.Remove(code);else slots[code]=count-1;}
        int extra=ExtraWeaknessCount(spent,granted);
        var kept=previous.Take(extra).ToList();
        var pool=WeaknessPool(catalog);
        while(kept.Count<extra)
        {
            var available=pool.Where(c=>!slots.ContainsKey(c)&&!kept.Contains(c)).ToArray();
            if(available.Length==0)available=pool;
            kept.Add(available[Random.Shared.Next(available.Length)]);
        }
        foreach(var code in kept)slots[code]=slots.GetValueOrDefault(code)+1;
        var result=JsonNode.Parse(deck.GetRawText())!.AsObject();result["slots"]=JsonSerializer.SerializeToNode(slots);
        var meta=Metadata(deck);meta[BudgetKey]=budget;meta[ExtrasKey]=JsonSerializer.SerializeToNode(kept);result["meta"]=meta.ToJsonString();
        return JsonSerializer.SerializeToElement(result);
    }
    public static bool IsBasicWeakness(CoreCatalog catalog,string code)=>catalog.Cards.TryGetValue(code,out var card)&&code!="01000"&&J.String(card,"subtype_code")=="basicweakness";
    public static JsonElement ConstructionInput(CoreCatalog catalog,JsonElement deck)
    {
        var slots=CoreCatalog.Slots(deck);foreach(var code in Extras(deck)){if(!IsBasicWeakness(catalog,code)||!slots.TryGetValue(code,out int count)||count<1)throw new InvalidOperationException("额外基础弱点记录与牌组不一致。");if(count==1)slots.Remove(code);else slots[code]=count-1;}
        var result=JsonNode.Parse(deck.GetRawText())!.AsObject();result["slots"]=JsonSerializer.SerializeToNode(slots);
        // These fields are independently checked by this standalone adapter.
        // They are not rule-changing deck metadata and must not make the core
        // construction validator skip its otherwise complete structural work.
        var meta=Metadata(deck);foreach(var k in OwnKeys)meta.Remove(k);
        if(meta.Count==0)result.Remove("meta");else result["meta"]=meta.ToJsonString();
        return JsonSerializer.SerializeToElement(result);
    }
}
