using System.Text.Json;

namespace Arkham.NativeHost;

// Shape/permission adapter only. The core's actual deck_options provides all
// selector accessors and values; no second faction/level/capacity rule engine.
public static class LanDeckMetadata
{
    static readonly HashSet<string> Base=new(StringComparer.Ordinal){"alternate_front","alternate_back","attachments_09077","attachments_11080"};
    static bool NativeKey(string key)=>key=="extra_deck"||key is "attachments_03264" or "attachments_05002" or "attachments_07303" or "attachments_10079" or "attachments_09077" or "attachments_11080"||key.StartsWith("cus_",StringComparison.Ordinal);
    public static bool RequiresNativeValidation(JsonElement meta)=>meta.EnumerateObject().Any(p=>NativeKey(p.Name));
    // This is only a selector-gate view. Never replace the deck's original meta
    // with this object: duplicates in extra_deck are meaningful quantities.
    public static JsonElement RemainingAfterNative(JsonElement meta,JsonElement result)
    {
        Unique(meta);Unique(result);
        if(result.GetProperty("schemaVersion").GetInt32()!=1||!result.GetProperty("preservesOriginalMeta").GetBoolean()||result.GetProperty("validatesExperienceBudget").GetBoolean())throw new InvalidDataException("原生牌组元数据协议不匹配");
        var keys=meta.EnumerateObject().Select(p=>p.Name).ToHashSet(StringComparer.Ordinal);var validated=new HashSet<string>(StringComparer.Ordinal);var remaining=new HashSet<string>(StringComparer.Ordinal);
        foreach(var value in result.GetProperty("validatedKeys").EnumerateArray()){
            string key=value.GetString()??"";if(!keys.Contains(key)||!NativeKey(key)||!validated.Add(key))throw new InvalidDataException("原生已校验字段不匹配");
        }
        foreach(var value in result.GetProperty("remainingKeys").EnumerateArray()){
            string key=value.GetString()??"";if(!keys.Contains(key)||validated.Contains(key)||!remaining.Add(key)||NativeKey(key))throw new InvalidDataException("原生剩余字段不匹配");
        }
        if(validated.Count+remaining.Count!=keys.Count)throw new InvalidDataException("原生校验结果遗漏元数据字段");
        return JsonSerializer.SerializeToElement(meta.EnumerateObject().Where(p=>remaining.Contains(p.Name)).ToDictionary(p=>p.Name,p=>p.Value.Clone(),StringComparer.Ordinal));
    }
    public static bool RequiresSelectionSchema(JsonElement meta)=>meta.EnumerateObject().Any(p=>p.Name is "alternate_front" or "alternate_back"||!Base.Contains(p.Name));
    public static bool RequiresDeckSchema(JsonElement deck,JsonElement meta)=>RequiresSelectionSchema(meta)
        ||deck.TryGetProperty("taboo_id",out var value)&&value.ValueKind!=JsonValueKind.Null&&(!value.TryGetInt32Safe(out int id)||id!=0);
    static void Unique(JsonElement value)
    {
        if(value.ValueKind!=JsonValueKind.Object)throw new InvalidDataException("牌组元数据必须为JSON对象");
        var seen=new HashSet<string>(StringComparer.Ordinal);foreach(var p in value.EnumerateObject())if(!seen.Add(p.Name))throw new InvalidDataException("牌组元数据包含重复字段");
    }
    public static JsonElement Meta(JsonElement deck)
    {
        if(!deck.TryGetProperty("meta",out var value)||value.ValueKind==JsonValueKind.Null)return JsonSerializer.SerializeToElement(new{});
        if(value.ValueKind!=JsonValueKind.String)throw new InvalidDataException("联机牌组meta须使用原JSON字符串格式");
        string text=value.GetString()!;if(text.Length>65536)throw new InvalidDataException("联机牌组元数据过大");
        if(string.IsNullOrWhiteSpace(text))return JsonSerializer.SerializeToElement(new{});
        using var parsed=JsonDocument.Parse(text,new JsonDocumentOptions{MaxDepth=8});var meta=parsed.RootElement;Unique(meta);
        if(meta.GetRawText().Length>65536||meta.EnumerateObject().Count()>64)throw new InvalidDataException("联机牌组元数据过大");
        foreach(var p in meta.EnumerateObject())if(p.Name.Length is <1 or >160||p.Value.ValueKind is not(JsonValueKind.String or JsonValueKind.Null)||p.Value.ValueKind==JsonValueKind.String&&(p.Value.GetString()!.Length>16384||p.Value.GetString()!.Any(char.IsControl)))throw new InvalidDataException("联机牌组元数据仅支持有限字符串；不会删去不支持内容");
        return meta.Clone();
    }
    public static JsonElement Request(JsonElement deck,string mode,JsonElement selections)
    {
        string investigator=deck.GetProperty("investigator_code").GetString()??"";if(investigator.Length is <1 or >160)throw new InvalidDataException("无效调查员编号");
        int? taboo=null;if(deck.TryGetProperty("taboo_id",out var t)&&t.ValueKind!=JsonValueKind.Null){if(!t.TryGetInt32(out int n)||n<0)throw new InvalidDataException("无效禁忌表编号");taboo=n;}
        var meta=Meta(deck);string? Side(string key){if(!meta.TryGetProperty(key,out var value)||value.ValueKind==JsonValueKind.Null||value.GetString()=="")return null;return value.GetString();}
        return JsonSerializer.SerializeToElement(new{investigatorCode=investigator,mode,tabooId=taboo,alternateFront=Side("alternate_front"),alternateBack=Side("alternate_back"),selections});
    }
    public static JsonElement Selections(JsonElement meta,JsonElement schema)
    {
        Unique(meta);if(schema.GetProperty("schemaVersion").GetInt32()!=2)throw new InvalidDataException("宿主未提供构筑选择协议");
        if(meta.TryGetProperty("alternate_front",out _)||meta.TryGetProperty("alternate_back",out _)){
            var sides=schema.GetProperty("investigatorSides");var choices=sides.GetProperty("choices").EnumerateArray().Select(c=>c.GetProperty("value").GetString()).ToHashSet(StringComparer.Ordinal);
            foreach(string key in new[]{"alternate_front","alternate_back"})if(meta.TryGetProperty(key,out var value)&&value.ValueKind!=JsonValueKind.Null&&value.GetString() is {Length:>0} code&&!choices.Contains(code))throw new UnauthorizedAccessException("调查员前后面不是核心提供的关联版本");
        }
        var offered=new Dictionary<string,HashSet<string>>(StringComparer.Ordinal);
        foreach(var field in schema.GetProperty("selectionFields").EnumerateArray()){
            string key=field.GetProperty("key").GetString()??"";if(key.Length is <1 or >160||Base.Contains(key)||offered.Count>=32)throw new InvalidDataException("构筑选择字段与游戏元数据冲突");
            var values=new HashSet<string>(StringComparer.Ordinal);foreach(var choice in field.GetProperty("choices").EnumerateArray()){string v=choice.GetProperty("value").GetString()??"";if(v.Length is <1 or >160||values.Count>=64||!values.Add(v))throw new InvalidDataException("构筑选择值无效");}
            if(values.Count==0||!offered.TryAdd(key,values))throw new InvalidDataException("构筑选择字段无效");
        }
        var selected=new Dictionary<string,string>(StringComparer.Ordinal);
        foreach(var p in meta.EnumerateObject()){
            if(Base.Contains(p.Name))continue;
            if(!offered.TryGetValue(p.Name,out var values))throw new UnauthorizedAccessException("该牌组包含尚未开放的联机元数据："+p.Name+"；请由宿主处理，不会删掉后继续");
            if(p.Value.ValueKind==JsonValueKind.Null||p.Value.ValueKind==JsonValueKind.String&&p.Value.GetString()=="")continue;
            if(p.Value.ValueKind!=JsonValueKind.String||!values.Contains(p.Value.GetString()!))throw new InvalidDataException("构筑选择不在核心提供的选项中："+p.Name);
            selected.Add(p.Name,p.Value.GetString()!);
        }
        return JsonSerializer.SerializeToElement(selected);
    }
}
