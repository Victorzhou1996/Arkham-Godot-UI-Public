using System.Text.Json;
using System.Text.Json.Nodes;

namespace Gathering;

public sealed class CoreCatalog
{
    public Dictionary<string, JsonElement> Cards { get; }
    public CoreCatalog(string file)
    {
        using var json = JsonDocument.Parse(File.ReadAllText(file));
        Cards = json.RootElement.EnumerateArray().ToDictionary(c => c.GetProperty("code").GetString()!, c => c.Clone());
        // later cycles sit beside the core set as <stem>-cards.json (dwl-cards.json: The Dunwich Legacy), same fields
        foreach (var extra in Directory.GetFiles(Path.GetDirectoryName(file)!, "*-cards.json").Where(f => !Path.GetFileName(f).Equals(Path.GetFileName(file), StringComparison.OrdinalIgnoreCase)).OrderBy(f => f))
        {
            using var more = JsonDocument.Parse(File.ReadAllText(extra));
            foreach (var c in more.RootElement.EnumerateArray())
            {
                string code = c.GetProperty("code").GetString()!;
                if (Cards.TryAdd(code, c.Clone()) || code.StartsWith("01") || code.StartsWith("02")) continue;   // the core set and Dunwich stay as they were
                // the same card in a later file (ptc-cards.json from the html version, then zz-build-cards.json from
                // arkham.build): the first file's official text stays; fields it lacks (deck_limit, health, skill icons …)
                // are filled in from the later one (Carcosa 11-20: the html card data carries no numbers)
                var have = Cards[code];
                var missing = c.EnumerateObject().Where(p => have.ValueKind == JsonValueKind.Object && !have.TryGetProperty(p.Name, out _)).ToList();
                if (missing.Count == 0) continue;
                var node = JsonNode.Parse(have.GetRawText())!.AsObject();
                foreach (var p in missing) node[p.Name] = JsonNode.Parse(p.Value.GetRawText());
                Cards[code] = JsonSerializer.SerializeToElement(node);
            }
        }
        // the supported pool: player cards are the cards without an encounter set (core 01006-01103, Dunwich 02006-02310 ...);
        // investigators are each set's five (01001-01005, 02001-02005)
        foreach (var (code, card) in Cards)
        {
            KnownCodes.Add(code);
            string type = J.String(card, "type_code");
            // every player investigator: not only a set's first ones (Yunke 11-22: 陈丽丽 08010, 查理·凯恩 09018, 歌莉娅 11014, 乔治·巴纳比 11017
            // were refused as "无法识别" because only codes like 05002 counted); story investigators (with an encounter set) and the
            // parallel ones (90xxx: chosen as another side of the original, build's alternate_front / alternate_back) stay out
            if (type == "investigator" && code.Length == 5 && code.All(char.IsDigit) && !code.StartsWith("90") && J.String(card, "encounter_code").Length == 0) SupportedInvestigators.Add(code);
            else if (code != "01000" && type is not ("investigator" or "scenario" or "act" or "agenda" or "location") && J.String(card, "encounter_code").Length == 0) PlayerCodes.Add(code);
        }
    }
    static readonly HashSet<string> PlayerCodes = new(), SupportedInvestigators = new(), KnownCodes = new();
    public static bool IsInvestigator(string code) => SupportedInvestigators.Contains(code);
    // The official name of a card known only by its English name (the core sometimes sends a location's printed English
    // name in a label): the first card whose real_name matches, else "".
    public string NameFromEnglish(string english) => Cards.Values.FirstOrDefault(c => J.String(c, "real_name") == english) is { ValueKind: JsonValueKind.Object } c ? Runes(J.String(c, "name")) : "";
    // a side the card data lists only without its letter (卡城幽影's 03325a 哈利湖滨 is "03325"): the plain code's name, as Ui.Card does for images
    public string Name(string code) => Runes(Cards.TryGetValue(Normalize(code), out var card) ? J.String(card, "name", code)
        : Normalize(code) is { Length: 6 } c6 && char.IsLetter(c6[5]) && Cards.TryGetValue(c6[..5], out var plain) ? J.String(plain, "name", code) : code);
    // The Drowned City's runes in official card names ("古代秘库[tdc_rune_o]"): as the html version (icons.ts runeTokenPlaceholder →
    // .rune-O, ArkhamSlim U+E90E) each becomes its glyph, U+E900 + the letter (A = E900); the game's text fonts carry ArkhamSlim
    // as a fallback, so every label draws it (local-rune-icon: a button read "古代秘库[tdcrunen]")
    public static string Runes(string text) => text.Contains("[tdc_rune_")
        ? System.Text.RegularExpressions.Regex.Replace(text, @"\[tdc_rune_([A-Za-z])\]", m => ((char)(0xE900 + char.ToUpperInvariant(m.Groups[1].Value[0]) - 'A')).ToString())
        : text;
    public static string Normalize(string code) => code.StartsWith('c') ? code[1..] : code;
    // The name a card in play shows: a location not yet revealed shows only its unrevealed side's name (绝望深坑's 水下洞窟 is
    // "潮水通道" until revealed — QQ #34), as the html table shows only the card's back; anything else its own name.
    public string ShownName(JsonElement entity, string code) =>
        J.Get(entity, "revealed").ValueKind == JsonValueKind.False && Cards.TryGetValue(Normalize(code), out var card)
        && J.String(card, "type_code") == "location" && J.String(card, "back_name") is { Length: > 0 } back ? back : Name(code);
    public IEnumerable<JsonElement> Investigators => Cards.Values.Where(c => J.String(c,"type_code") == "investigator").OrderBy(c => J.String(c,"code"));
    public IEnumerable<JsonElement> Pool(JsonElement schema, int maxLevel = 5)
    {
        var options = J.Items(J.Get(schema, "options")).ToArray();
        return Cards.Values.Where(card =>
            J.String(card,"type_code") is "asset" or "event" or "skill" && IsCorePlayerCard(J.String(card,"code"))
            && J.Get(card,"xp").ValueKind == JsonValueKind.Number && J.Int(card,"xp") <= maxLevel
            && options.Any(option => J.Items(J.Get(option,"faction")).Any(f => f.GetString() == J.String(card,"faction_code"))
                && J.Int(card,"xp") >= J.Int(J.Get(option,"level"),"min")
                && J.Int(card,"xp") <= J.Int(J.Get(option,"level"),"max",5)))
            .OrderBy(c => J.String(c,"faction_code") == "neutral").ThenBy(c => J.String(c,"code"));
    }
    public IEnumerable<JsonElement> InitialPool(JsonElement schema) => Pool(schema, 0);
    public int Experience(JsonElement deck) => Slots(deck).Sum(row => Cards.TryGetValue(row.Key, out var card) ? Math.Max(0,J.Int(card,"xp")) * row.Value : 0);
    public JsonElement Starter(string investigator, JsonElement schema)
    {
        var slots = new Dictionary<string,int>();
        // Demonstration builds, not official starter lists. Each includes an
        // economy/draw/skill core instead of taking the first catalog rows.
        string[] codes=investigator switch {
            "01001"=>["01016","01020","01030","01033","01034","01037","01039","01022","01023","01025","01088","01089","01090","01091","01093"],
            "01002"=>["01030","01031","01032","01033","01034","01035","01036","01037","01039","01059","01065","01088","01089","01090","01093"],
            "01003"=>["01016","01020","01018","01023","01025","01046","01047","01048","01050","01051","01052","01088","01090","01091","01093"],
            "01004"=>["01058","01059","01060","01063","01064","01065","01067","01072","01075","01079","01080","01088","01089","01090","01093"],
            "01005"=>["01046","01048","01050","01051","01052","01072","01074","01075","01076","01079","01080","01088","01089","01090","01093"],
            // The Dunwich Legacy investigators: core + Dunwich level-0 cards of their own class and neutral (validated by the core)
            "02001"=>["01016","01017","01018","01019","01020","01021","01022","01023","01025","02016","02017","02147","01088","01089","01091"],
            "02002"=>["01030","01031","01032","01033","01034","01035","01036","01037","01039","02020","02021","02023","01088","01089","01093"],
            "02003"=>["01046","01047","01048","01050","01051","01052","02024","02025","02026","02151","01088","01089","01090","01091","01093"],
            "02004"=>["01059","01060","01062","01063","01064","01065","01066","02029","02030","02111","01088","01089","01090","01091","01093"],
            "02005"=>["01072","01074","01075","01076","01077","01078","01079","01080","02032","02033","02034","01088","01089","01090","01091"],
            // The Path to Carcosa investigators (Yunke 11-20: for the Carcosa group picture): Carcosa's level-0 cards of their
            // own class with the core set's, in the same shape as above (validated by the core)
            "03001"=>["03020","03021","03022","03106","03147","01016","01018","01020","01022","01023","01025","01088","01089","01091","01093"],   // Mark Harrigan
            "03002"=>["03024","03025","03026","03231","01030","01031","01032","01033","01036","01037","01039","03039","01088","01089","01093"],   // Minh Thi Phan (+ survivor 0-2)
            "03003"=>["03028","03029","03030","03194","03233","01046","01047","01048","01050","01051","01052","01060","01065","01088","01089","01093"],   // Sefina Rousseau, 33 cards (+ mystic 0-2)
            "03004"=>["03032","03033","03034","03153","03235","01059","01060","01061","01063","01064","01065","01066","01088","01089","01090"],   // Akachi Onyele
            "03005"=>["03036","03037","03038","03039","03272","01074","01075","01076","01078","01079","01080","01018","01020","01088","01089"],   // William Yorick (+ guardian 0-2)
            "03006"=>["01016","01020","01023","01025","01030","01037","01039","03026","01075","01080","03038","03039","01088","01089","01091","01093","03116"],   // Lola Hayes, 35 cards: 8 each of guardian, seeker, survivor
            _=>throw new InvalidOperationException("本原型没有这名调查员的示例牌组。")};
        foreach(var code in codes)slots[code]=2;
        // decks of 33 and 35 (Sefina, Lola): one more single copy
        if (investigator == "03003") slots["01091"] = 1;
        if (investigator == "03006") slots["01090"] = 1;
        foreach (var signature in J.Props(J.Get(J.Get(schema,"requirements"),"card")))
        {
            // the printed signature itself when the requirement also allows other printings (Lola's 03018 or the 90088-90092 replacements)
            var code = J.Props(signature.Value).Select(p => p.Name).OrderBy(n => n == signature.Name ? 0 : 1).FirstOrDefault(Cards.ContainsKey);
            // as many copies as the card's own limit: the core wants all of them (Sefina's The Painted World ×3, Lola's ×2)
            if (code is not null) slots[code] = Math.Max(1, J.Int(Cards[code], "deck_limit", 1));
        }
        // the basic weakness stays random (Yunke 11-19: no fixed weakness in the example decks): build's placeholder 01000,
        // drawn anew at the start of every game (StandaloneDecks.Prepare)
        slots[StandaloneDecks.Placeholder] = 1;
        return Deck(investigator, slots);
    }
    // "基础+第一循环" decks (Yunke 11-18 #2): one per investigator, built from the level-0 cards of both boxes (core set and
    // The Dunwich Legacy) that the investigator may take; signature cards from the schema, the random basic weakness placeholder (01000).
    public JsonElement Mixed(string investigator, JsonElement schema)
    {
        string[] codes = investigator switch {
            "01001"=>["01016","01020","01022","01023","01025","02016","02017","02147","02105","01030","01037","01039","01088","01089","01093"],
            "01002"=>["01030","01031","01032","01033","01035","01036","01037","01039","02020","02021","02023","02149","01065","01088","01093"],
            "01003"=>["01046","01047","01048","01050","01051","01052","02024","02025","02026","02151","01016","01020","01023","01088","01091"],
            "01004"=>["01059","01060","01062","01063","01064","01065","01066","02028","02029","02030","02111","01073","01080","01088","01089"],
            "01005"=>["01072","01074","01075","01076","01078","01079","01080","02032","02033","02034","02113","01046","01050","01088","01093"],
            "02001"=>["01016","01017","01018","01019","01020","01021","01022","01023","01025","02016","02017","02147","02105","01088","01091"],
            "02002"=>["01030","01031","01032","01033","01034","01035","01036","01037","01039","02020","02021","02023","02149","01088","01093"],
            "02003"=>["01046","01047","01048","01050","01051","01052","02024","02025","02026","02151","02109","01088","01089","01090","01093"],
            "02004"=>["01059","01060","01062","01063","01064","01065","01066","02029","02030","02111","02028","01088","01089","01090","01091"],
            "02005"=>["01072","01074","01075","01076","01077","01078","01079","01080","02032","02033","02034","02113","01088","01089","01091"],
            _=>throw new InvalidOperationException("没有这名调查员的复合牌组。")};
        var slots = codes.ToDictionary(c => c, _ => 2);
        foreach (var signature in J.Props(J.Get(J.Get(schema,"requirements"),"card")))
        {
            var code = J.Props(signature.Value).Select(p => p.Name).FirstOrDefault(Cards.ContainsKey);
            if (code is not null) slots[code] = 1;
        }
        slots[StandaloneDecks.Placeholder] = 1;   // random basic weakness, drawn at the start of every game
        return Deck(investigator, slots, Name(investigator) + " · 基础+第一循环");
    }
    public JsonElement Deck(string investigator, IReadOnlyDictionary<string,int> slots, string? title = null)
        => JsonSerializer.SerializeToElement(new { name = title ?? Name(investigator) + (investigator[..2] switch { "01" => " · 核心盒示例", "03" => " · 卡尔克萨示例", _ => " · 敦威治示例" }), investigator_code = investigator, slots, sideSlots = new { } });
    public static Dictionary<string,int> Slots(JsonElement deck)
        => J.Props(J.Get(deck,"slots")).Where(p => p.Value.GetInt32() > 0).ToDictionary(p => p.Name, p => p.Value.GetInt32());
    // A deck the game can hand to the rules core: a known investigator, known card codes with positive counts, readable
    // metadata. Whether the core can actually run every card is the core's own verdict (deckBuildValidate's
    // unsupportedReasons, PrototypeBackend.ValidateDeck), not a list kept here. 01000 (build's random basic weakness
    // placeholder) passes: StandaloneDecks.Prepare replaces it before the core sees the deck.
    public static bool IsCoreDeck(JsonElement deck)
        => IsInvestigator(J.String(deck,"investigator_code"))
            && J.Get(deck,"slots").ValueKind == JsonValueKind.Object
            && J.Props(J.Get(deck,"slots")).All(p => KnownCodes.Contains(p.Name) && p.Value.TryGetInt32Safe(out int count) && count >= 0)
            && J.Props(J.Get(deck,"sideSlots")).All(p => KnownCodes.Contains(p.Name) && p.Value.TryGetInt32Safe(out int count) && count >= 0)
            && J.Get(deck,"native_build").ValueKind is JsonValueKind.Undefined or JsonValueKind.Null
            && J.Get(deck,"taboo_id").ValueKind is JsonValueKind.Undefined or JsonValueKind.Null or JsonValueKind.Number
            && StandaloneDecks.HasOnlyPrototypeMetadata(deck);
    // name kept from the core-only prototype: now any supported player card (core set and the imported cycles)
    public static bool IsCorePlayerCard(string code) => PlayerCodes.Contains(code);
}
