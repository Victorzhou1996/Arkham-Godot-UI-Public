using System.Text.Json;
using System.Text.Json.Nodes;
using Gathering;

namespace ArkhamUI;

// The workshop's decks, kept in arkham.build's store (BuildEngine "decks.*") and mirrored to userdata/decks.json, which
// the rest of the game reads. Every change goes through build's own actions; after a save / create / copy / delete the
// deck as build left it is written back. Deleted decks are kept in userdata/decks-trash.json (nothing is lost).
public static class BuildDecks
{
    static Services S => App.I.S!;
    static bool synced;

    // Load decks.json into build's store once (again after an outside change with force).
    public static async Task Sync(bool force = false)
    {
        await BuildEngine.I.Ready;
        if (synced && !force) return;
        var map = new JsonObject();
        foreach (var (k, d) in S.Decks.All) map[k] = JsonNode.Parse(d.GetRawText());
        await BuildEngine.I.Call("decks.load", map.ToJsonString());
        synced = true;
    }

    // decks.json changed from outside the workshop (a campaign's deck saved at its end): load it again next time
    public static void Invalidate() => synced = false;

    // Yunke 11-19: the workshop keeps two kinds of deck — the ones built here (初始卡组) and the decks a finished campaign
    // left (通关卡组, saved by the table at the campaign's end, marked in their meta). A deck half way through a campaign lives
    // only in that game's save and is seen from inside the game.
    public const string KindKey = "arkhamui_kind", CampaignKey = "arkhamui_campaign", ResultKey = "arkhamui_result", TraumaKey = "arkhamui_trauma";
    public static bool IsCompleted(string id) => S.Decks.Has(id) && MetaString(S.Decks.Get(id), KindKey) == "completed";
    // the name as the workshop shows it: a finished campaign's deck carries a laurel instead of the " 通关" older saves put at the
    // end of its name (Yunke 11-22)
    public static string ShownName(string id, string name)
    {
        if (!IsCompleted(id)) return name;
        var n = name.TrimEnd();
        return n.EndsWith("通关") ? n[..^2].TrimEnd(' ', '·') : name;
    }
    public static string MetaString(JsonElement deck, string key)
    {
        var meta = J.Get(deck, "meta");
        try
        {
            using var doc = meta.ValueKind == JsonValueKind.String ? JsonDocument.Parse(meta.GetString() is { Length: > 0 } t ? t : "{}") : null;
            var o = doc?.RootElement ?? meta;
            return o.ValueKind == JsonValueKind.Object && o.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()! : "";
        }
        catch { return ""; }
    }

    public static async Task<JsonElement> List() { await Sync(); return await BuildEngine.I.CallJson("decks.list"); }
    public static async Task<JsonElement> View(string id) { await Sync(); return await BuildEngine.I.CallJson("decks.view", id); }
    public static async Task<JsonElement> Access(string id, string target = "slots") { await Sync(); return await BuildEngine.I.CallJson("decks.access", id, target); }
    public static async Task<JsonElement> Investigators() { await BuildEngine.I.Ready; return await BuildEngine.I.CallJson("investigators"); }
    public static async Task<JsonElement> Packs() { await BuildEngine.I.Ready; return await BuildEngine.I.CallJson("packs"); }

    // an edit (build keeps it pending until Save)
    public static async Task Edit(string fn, params object?[] args) { await Sync(); await BuildEngine.I.Call("decks." + fn, args); }

    public static async Task<string> Save(string id)
    {
        var text = await BuildEngine.I.Call("decks.save", id);
        // the experience build counts (levels, customization marks, taboo changes) goes with the deck, for the
        // standalone rule at the start of a game (StandaloneDecks.BuildXpKey)
        try
        {
            var view = await BuildEngine.I.CallJson("decks.view", id);
            var node = JsonNode.Parse(text)!.AsObject();
            JsonObject meta; try { meta = JsonNode.Parse(node["meta"]?.GetValue<string>() is { Length: > 0 } m ? m : "{}")!.AsObject(); } catch { meta = new(); }
            meta[Gathering.StandaloneDecks.BuildXpKey] = J.Int(J.Get(view, "stats"), "xpRequired");
            node["meta"] = meta.ToJsonString();
            text = node.ToJsonString();
        }
        catch (Exception e) { Godot.GD.PushWarning("build xp: " + e.Message); }
        Mirror(id, text);
        return id;
    }
    public static Task Discard(string id) => BuildEngine.I.Call("decks.discard", id);

    public static async Task<string?> Copy(string id)
    {
        await Sync();
        var text = await BuildEngine.I.Call("decks.copy", id);
        using var doc = JsonDocument.Parse(text);
        if (doc.RootElement.ValueKind != JsonValueKind.Object) return null;
        string nid = J.String(doc.RootElement, "id");
        Mirror(nid, text);
        return nid;
    }

    // Import decks from JSON text (build's import: one deck, a list, or a { key: deck } map such as our own decks.json /
    // an export from ArkhamDB or arkham.build). Each imported deck gets a new id and goes into decks.json. Returns the new
    // ids and one Chinese line per deck that could not come in.
    public static async Task<(List<string> Added, List<string> Refused)> Import(string text)
    {
        await Sync();
        var added = new List<string>(); var refused = new List<string>();
        string t = text.Trim();
        if (t.StartsWith("http", StringComparison.OrdinalIgnoreCase)) { refused.Add("分享链接需要联网，请在网站上导出 JSON 文件后再导入"); return (added, refused); }
        JsonElement r;
        try { r = await BuildEngine.I.CallJson("decks.importJson", t); }
        catch { refused.Add("不是牌组 JSON"); return (added, refused); }
        foreach (var d in J.Items(J.Get(r, "added")))
        {
            string id = J.String(d, "id"); if (id.Length == 0) continue;
            Mirror(id, d.GetRawText()); added.Add(id);
        }
        foreach (var x in J.Items(J.Get(r, "refused")))
        {
            string name = J.String(x, "name"); name = name.Length > 0 ? "“" + name + "”" : "一副牌组";
            refused.Add(J.String(x, "reason") switch
            {
                "unknown" => name + "含有本地卡池没有的卡牌（同人内容等）：" + string.Join("、", J.Items(J.Get(x, "codes")).Select(c => c.GetString()).Take(6)),
                "invalid" => name + "不是有效的牌组（缺少调查员或卡牌，或含同人内容）",
                _ => name + "导入出错",
            });
        }
        return (added, refused);
    }

    public static async Task Remove(string id)
    {
        await Sync();
        // keep a copy in the trash file first
        if (S.Decks.Has(id))
        {
            var trashFile = Path.Combine(App.I.DataRoot, "decks-trash.json");
            JsonObject trash;
            try { trash = File.Exists(trashFile) ? (JsonNode.Parse(File.ReadAllText(trashFile)) as JsonObject ?? new()) : new(); } catch { trash = new(); }
            var entry = JsonNode.Parse(S.Decks.Get(id).GetRawText())!.AsObject();
            entry["deleted_at"] = DateTime.Now.ToString("s");
            trash[id + "@" + DateTime.Now.ToString("yyyyMMddHHmmss")] = entry;
            File.WriteAllText(trashFile, trash.ToJsonString(new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
        }
        await BuildEngine.I.Call("decks.remove", id);
        S.Decks.Remove(id); S.Decks.Save();
    }

    // ── create ──
    public static async Task<JsonElement> CreateInit(string investigator) { await Sync(); return await BuildEngine.I.CallJson("decks.createInit", investigator); }
    public static Task<JsonElement> CreateInfo() => BuildEngine.I.CallJson("decks.createInfo");
    public static Task CreateSet(string fn, params object?[] args) => BuildEngine.I.Call("decks." + fn, args);
    public static async Task<string> Create()
    {
        var text = await BuildEngine.I.Call("decks.create");
        using var doc = JsonDocument.Parse(text);
        string id = J.String(doc.RootElement, "id");
        Mirror(id, text);
        return id;
    }

    // write build's deck into decks.json (the game reads it from there)
    static void Mirror(string id, string deckJson)
    {
        using var doc = JsonDocument.Parse(deckJson);
        if (doc.RootElement.ValueKind != JsonValueKind.Object) return;
        S.Decks.Put(id, doc.RootElement);
        S.Decks.Save();
    }

    // ── words ──
    static JsonElement? locale;
    // build's own zh-cn wording for a key under "translation." (fallback when missing)
    public static string Word(string key, string fallback) => L(key, fallback);
    static string L(string path, string fallback)
    {
        try
        {
            locale ??= JsonDocument.Parse(File.ReadAllText(Path.Combine(App.I.Assets, "build-data", "locale-zh-cn.json"))).RootElement.Clone();
            JsonElement e = locale.Value;
            if (e.TryGetProperty("translation", out var tr)) e = tr;   // build's i18n file: { translation: { … } }
            foreach (var part in path.Split('.')) { if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(part, out e)) return fallback; }
            return e.ValueKind == JsonValueKind.String ? e.GetString()! : fallback;
        }
        catch { return fallback; }
    }

    // One of build's validation errors in Chinese (build's own zh-cn wording where it has one), with the cards it names.
    public static string ErrorText(JsonElement err, Func<string, string> name, string investigator = "")
    {
        string type = J.String(err, "type"); var d = J.Get(err, "details");
        string Cards() => string.Join("、", J.Items(d).Select(x => name(J.String(x, "code")) + (J.Get(x, "quantity").ValueKind == JsonValueKind.Number ? " ×" + J.Int(x, "quantity") : "")).Where(s => s.Length > 0).Take(6));
        string Deck(JsonElement det) => J.String(det, "target") == "extraSlots" ? "额外牌组" : "牌组";
        return type switch
        {
            "TOO_FEW_CARDS" => L("deck.validation.too_few_cards", "{{deck}} 包含的卡牌数量不足。").Replace("{{deck}}", Deck(d)) + "（" + J.Int(d, "count") + " / " + J.Int(d, "countRequired") + "）",
            "TOO_MANY_CARDS" => L("deck.validation.too_many_cards", "{{deck}} 包含过多卡牌。").Replace("{{deck}}", Deck(d)) + "（" + J.Int(d, "count") + " / " + J.Int(d, "countRequired") + "）",
            "DECK_REQUIREMENTS_NOT_MET" => L("deck.validation.investigator_requirements", "卡组不符合调查员要求：") + Cards(),
            "FORBIDDEN" => L("deck.validation.forbidden_cards", "卡组包含禁用卡牌：") + Cards(),
            "INVALID_CARD_COUNT" => L("deck.validation.invalid_card_count", "卡组中包含以下卡牌的非法复制数量：") + Cards(),
            "CARD_NOT_IN_LIMITED_POOL" => L("deck.validation.card_not_in_limited_pool", "以下卡牌不属于本卡组的卡池：") + Cards(),
            "INVALID_INVESTIGATOR" => L("deck.validation.invalid_investigator", "调查员无效。"),
            "INVALID_DECK_OPTION" => "不满足调查员的组牌选项" + OptionError(J.String(d, "error"), investigator),
            _ => L("deck.validation.invalid", "卡组无效。"),
        };
    }

    // build's deck option error is the English of the card data ("Deck must have at least 11 Insights."): told instead with the
    // rule as printed on the investigator's back in Chinese (official text: 附加需求 for an "at least", else 牌库构建选项)
    static string OptionError(string error, string investigator)
    {
        if (error.Length == 0) return "";
        if (!System.Text.RegularExpressions.Regex.IsMatch(error, "[A-Za-z]{3,}")) return "：" + error;
        var back = investigator.Length > 0 ? J.String(App.I.S!.Card(investigator), "back_text") : "";
        string key = error.Contains("at least", StringComparison.OrdinalIgnoreCase) && back.Contains("附加需求") ? "附加需求" : "牌库构建选项";
        int at = back.IndexOf(key); if (at < 0) return "";
        var t = System.Text.RegularExpressions.Regex.Replace(back[at..], @"<[^>]+>|\[\[|\]\]", "");
        t = t[key.Length..].TrimStart('：', ':', ' ');
        int end = t.IndexOfAny(new[] { '。', '\n' }); return "：" + (end > 0 ? t[..end] : t).Trim();
    }

    public static string FactionName(string f) => f switch
    {
        "guardian" => "守护者", "seeker" => "探求者", "rogue" => "流浪者", "mystic" => "神秘学家", "survivor" => "生存者", "neutral" => "中立", "mythos" => "神话", _ => f,
    };
    public static string TypeName(string t) => t switch { "asset" => "支援", "event" => "事件", "skill" => "技能", "investigator" => "调查员", "treachery" => "诡计", "enemy" => "敌人", _ => t };
}
