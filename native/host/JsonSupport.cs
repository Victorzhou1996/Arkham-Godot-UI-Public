using System.Text.Json;

public static class J
{
    public static JsonElement Get(JsonElement value,string key)=>value.ValueKind==JsonValueKind.Object&&value.TryGetProperty(key,out var result)?result:default;
    public static string String(JsonElement value,string key,string fallback=""){var v=Get(value,key);return v.ValueKind==JsonValueKind.String?v.GetString()!:fallback;}
    public static string Tag(JsonElement value)=>String(value,"tag");
    public static int Int(JsonElement value,string key,int fallback=0){var v=Get(value,key);return v.TryGetInt32Safe(out int n)?n:fallback;}
    public static bool TryGetInt32Safe(this JsonElement value,out int result){result=0;return value.ValueKind==JsonValueKind.Number&&value.TryGetInt32(out result);}
    public static IEnumerable<JsonElement> Items(JsonElement value)=>value.ValueKind==JsonValueKind.Array?value.EnumerateArray():Enumerable.Empty<JsonElement>();
    public static IEnumerable<JsonProperty> Props(JsonElement value)=>value.ValueKind==JsonValueKind.Object?value.EnumerateObject():Enumerable.Empty<JsonProperty>();
}
