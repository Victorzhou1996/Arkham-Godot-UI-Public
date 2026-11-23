using System.Text.Json;
using System.Text.Json.Nodes;

// Metadata comes from the current DLL view, not a second client-side key list.
public static class ClientSettings
{
    public static JsonElement[] Rows(JsonElement schema)
    {
        if(J.Int(schema,"schemaVersion")!=1||J.Get(schema,"supported").ValueKind!=JsonValueKind.True)throw new InvalidOperationException("本场景的高级设置尚未接入；不会提交空设置替代");
        string tag=J.String(schema,"answerTag");if(tag is not("StandaloneSettingsAnswer" or "CampaignSettingsAnswer"))throw new InvalidOperationException("未知设置协议");
        var rows=J.Items(J.Get(schema,"rows")).ToArray();if(J.Get(schema,"rows").ValueKind!=JsonValueKind.Array||rows.Length>128)throw new InvalidOperationException("无效设置定义");
        foreach(var row in rows){string kind=J.String(row,"kind"),type=J.String(row,"type");if(!(kind=="toggle"&&type is "ToggleKey" or "ToggleOption"||kind=="pick"&&type=="PickKey"||kind=="records"&&type=="ToggleCrossedOut"))throw new InvalidOperationException("尚未支持的设置项："+type);if(J.String(row,"label").Length==0)throw new InvalidOperationException("设置项缺少名称");}
        if(tag=="CampaignSettingsAnswer"&&rows.Length>0)throw new InvalidOperationException("此战役设置表单尚未接入");
        return rows;
    }
    public static JsonElement[] Defaults(JsonElement schema)=>Rows(schema).Select(row=>J.String(row,"kind") switch{
        "records"=>JsonSerializer.SerializeToElement(J.Items(J.Get(row,"entries")).Select(e=>J.Get(e,"default").GetBoolean()).ToArray()),
        _=>J.Get(row,"default").Clone()
    }).ToArray();
    static bool Same(JsonElement a,JsonElement b)=>JsonNode.DeepEquals(JsonNode.Parse(a.GetRawText()),JsonNode.Parse(b.GetRawText()));
    public static JsonElement Answer(JsonElement schema,IReadOnlyList<JsonElement> values)
    {
        var rows=Rows(schema);if(values.Count!=rows.Length)throw new InvalidOperationException("设置数量不完整");var settings=new List<JsonElement>();
        for(int i=0;i<rows.Length;i++){
            var row=rows[i];var value=values[i];string type=J.String(row,"type"),kind=J.String(row,"kind");
            if(kind=="toggle"){
                if(value.ValueKind is not(JsonValueKind.True or JsonValueKind.False)||J.Get(row,"key").ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)throw new InvalidOperationException("无效开关设置");
                settings.Add(JsonSerializer.SerializeToElement(new{type,key=J.Get(row,"key"),content=value.GetBoolean()}));
            }else if(kind=="pick"){
                if(!J.Items(J.Get(row,"choices")).Any(c=>Same(c,value)))throw new InvalidOperationException("请选择提供的设置选项");
                settings.Add(JsonSerializer.SerializeToElement(new{type,content=value}));
            }else{
                var entries=J.Items(J.Get(row,"entries")).ToArray();if(value.ValueKind!=JsonValueKind.Array||value.GetArrayLength()!=entries.Length||entries.Length>512||J.String(row,"recordable")!="RecordableCardCode")throw new InvalidOperationException("无效记录集设置");
                var content=new List<object>();for(int j=0;j<entries.Length;j++){var selected=value[j];if(selected.ValueKind is not(JsonValueKind.True or JsonValueKind.False))throw new InvalidOperationException("无效记录状态");content.Add(new{key=J.Get(entries[j],"key"),content=selected.GetBoolean()});}
                settings.Add(JsonSerializer.SerializeToElement(new{type,key=J.Get(row,"key"),recordable=J.String(row,"recordable"),content}));
            }
        }
        string answerTag=J.String(schema,"answerTag");return answerTag=="CampaignSettingsAnswer"
            ?JsonSerializer.SerializeToElement(new{tag=answerTag,contents=new{keys=Array.Empty<object>(),counts=Array.Empty<object>(),sets=Array.Empty<object>(),options=Array.Empty<object>()}})
            :JsonSerializer.SerializeToElement(new{tag=answerTag,contents=settings});
    }
}
