using System.Text.Json;

public sealed record ClientZoneOwner(string Kind,string Id,string? Title=null,string? Code=null);
public sealed record ClientZoneItem(string Key,bool Face,Guid? CardId,string? Code,string? Art,string? Name,string? Side,string? BackKind,bool Vengeance,bool InspectAllowed,string? Reason);
public sealed record ClientZone(string Id,string Kind,ClientZoneOwner Owner,string Order,int? Count,bool Supported,bool CountOnly,string? Reason,IReadOnlyList<ClientZoneItem> Items);
public sealed record ClientRegionView(Guid Recipient,long Revision,IReadOnlyList<ClientZone> Zones);

// This adapter only accepts the core's permission projection. It never traverses
// Game/cards/metadata or resolves a hidden presentation key through a registry.
public static class ClientRegions
{
    const int MaxZones=4096,MaxItems=32768,MaxText=4096;
    static InvalidDataException Invalid()=>new("区域查看数据不完整，已停止显示；存档未修改。");
    static void Object(JsonElement value){if(value.ValueKind!=JsonValueKind.Object)throw Invalid();var names=new HashSet<string>(StringComparer.Ordinal);foreach(var p in value.EnumerateObject())if(!names.Add(p.Name))throw Invalid();}
    static JsonElement Field(JsonElement value,string key)=>value.TryGetProperty(key,out var result)?result:throw Invalid();
    static string Text(JsonElement value){if(value.ValueKind!=JsonValueKind.String)throw Invalid();string text=value.GetString()!;if(text.Length==0||text.Length>MaxText||text.Any(char.IsControl))throw Invalid();return text;}
    static string Text(JsonElement value,string key)=>Text(Field(value,key));
    static string? OptionalText(JsonElement value,string key)=>value.TryGetProperty(key,out var item)&&item.ValueKind!=JsonValueKind.Null?Text(item):null;
    static bool Boolean(JsonElement value){if(value.ValueKind is not(JsonValueKind.True or JsonValueKind.False))throw Invalid();return value.GetBoolean();}
    static Guid GuidValue(JsonElement value){if(value.ValueKind!=JsonValueKind.String||!value.TryGetGuid(out var result)||result==Guid.Empty)throw Invalid();return result;}
    static bool Missing(JsonElement value,string key)=>!value.TryGetProperty(key,out var item)||item.ValueKind==JsonValueKind.Null;
    static JsonElement Array(JsonElement value){if(value.ValueKind!=JsonValueKind.Array)throw Invalid();return value;}
    static string Choice(string value,params string[] allowed)=>allowed.Contains(value,StringComparer.Ordinal)?value:throw Invalid();
    public static ClientRegionView Read(JsonElement response,Guid recipient,long revision)
    {
        Object(response);
        if(recipient==Guid.Empty||revision<0||!Field(response,"viewVersion").TryGetInt32(out int version)||version!=1||!Field(response,"revision").TryGetInt64(out long actual)||actual!=revision||GuidValue(Field(response,"recipient"))!=recipient)throw Invalid();
        var schema=Field(response,"zoneSchema");Object(schema);
        if(!Field(schema,"schemaVersion").TryGetInt32(out int schemaVersion)||schemaVersion!=1||GuidValue(Field(schema,"recipient"))!=recipient||!Boolean(Field(schema,"supported")))throw Invalid();
        var zones=Array(Field(schema,"zones"));if(zones.GetArrayLength()>MaxZones)throw Invalid();
        var zoneIds=new HashSet<string>(StringComparer.Ordinal);var itemKeys=new HashSet<string>(StringComparer.Ordinal);var result=new List<ClientZone>();int itemCount=0;
        foreach(var zone in zones.EnumerateArray()){
            Object(zone);string id=Text(zone,"id"),kind=Text(zone,"kind");if(!zoneIds.Add(id))throw Invalid();
            var owner=Field(zone,"owner");Object(owner);string ownerKind=Choice(Text(owner,"kind"),"Investigator","Asset","Location","Scenario","Game");
            string? ownerTitle=OptionalText(owner,"title"),ownerCode=OptionalText(owner,"cardCode");if((ownerTitle is null)!=(ownerCode is null))throw Invalid();
            var entity=new ClientZoneOwner(ownerKind,ownerKind=="Game"?"game":Text(owner,"id"),ownerTitle,ownerCode);
            string status=Choice(Text(zone,"status"),"supported","unsupported"),order=Choice(Text(zone,"order"),"listed","topFirst","unordered","unknown","bottomKnownSequence");
            string? reason=OptionalText(zone,"reasonKey"),access=OptionalText(zone,"access");if(access is not(null or "countOnly"))throw Invalid();
            var countValue=Field(zone,"count");int? count=null;if(countValue.ValueKind!=JsonValueKind.Null){if(!countValue.TryGetInt32(out int n)||n<0)throw Invalid();count=n;}
            var rows=Array(Field(zone,"items"));if(rows.GetArrayLength()>MaxItems-itemCount)throw Invalid();itemCount+=rows.GetArrayLength();
            if(status=="unsupported"&&(reason is null||rows.GetArrayLength()!=0)||access=="countOnly"&&rows.GetArrayLength()!=0||count is int total&&rows.GetArrayLength()>total)throw Invalid();
            var items=new List<ClientZoneItem>();
            foreach(var row in rows.EnumerateArray()){
                Object(row);string key=Text(row,"key");if(!itemKeys.Add(key))throw Invalid();
                bool face=Choice(Text(row,"visibility"),"face","back")=="face",inspect=Boolean(Field(row,"inspectAllowed"));bool vengeance=row.TryGetProperty("vengeance",out var revenge)&&Boolean(revenge);
                if(!face){
                    if(inspect||new[]{"cardId","cardCode","art","name","entityRef","entityKind","face"}.Any(p=>!Missing(row,p)))throw Invalid();
                    string back=Choice(Text(row,"backKind"),"player","encounter");string? itemReason=OptionalText(row,"reasonKey");
                    if(row.TryGetProperty("status",out var itemStatus)&&(Text(itemStatus)!="unsupported"||itemReason is null))throw Invalid();
                    items.Add(new(key,false,null,null,null,null,null,back,vengeance,false,itemReason));
                }else{
                    if(!inspect||vengeance)throw Invalid();Guid card=GuidValue(Field(row,"cardId"));string code=Text(row,"cardCode"),art=Text(row,"art"),name=Text(row,"name"),side=Choice(Text(row,"face"),"current","reverse");
                    // Art is an index key, never a file path assembled by this adapter.
                    items.Add(new(key,true,card,code,art,name,side,null,false,true,null));
                }
            }
            result.Add(new(id,kind,entity,order,count,status=="supported",access=="countOnly",reason,items.AsReadOnly()));
        }
        return new(recipient,revision,result.AsReadOnly());
    }
    public static bool Matches(ClientZoneItem item,string search,Func<string,string?>? visibleName=null)
    {
        string term=search.Trim();if(term.Length==0)return true;if(!item.Face)return false;
        return item.Code!.Contains(term,StringComparison.OrdinalIgnoreCase)||item.Name!.Contains(term,StringComparison.OrdinalIgnoreCase)||(visibleName?.Invoke(item.Code)??"").Contains(term,StringComparison.OrdinalIgnoreCase);
    }
    public static string Title(string kind)=>kind switch{
        "hand"=>"手牌","discard"=>"弃牌堆","under"=>"卡牌下方","bonded"=>"连结卡","deck"=>"牌库","assets"=>"支援与在场卡","threat"=>"威胁区","attachments"=>"附着卡牌",
        "marketDeck"=>"地下市场 · 未知牌堆","knownMarketDeck"=>"地下市场 · 已知置底顺序","spiritDeck"=>"彼岸 · 灵体牌堆","encounterDiscard"=>"遭遇弃牌堆","encounterDeck"=>"遭遇牌堆","victory"=>"胜利区","setAside"=>"暂置区","scenarioDeck"=>"剧本牌堆","removed"=>"移出游戏","sideDeck"=>"副牌组",_=>"其他区域"};
    // Owners without an explicitly projected public title stay neutral. In
    // particular, never ask PublicGame for a flipped Asset's secret name.
    public static string OwnerTitle(ClientZone zone,int ordinal,Func<ClientZoneOwner,string?>? visibleName=null)
    {
        if(zone.Owner.Title is string title&&zone.Owner.Code is not null&&zone.Owner.Kind is "Investigator" or "Asset" or "Location")return visibleName?.Invoke(zone.Owner) is string name&&name.Length>0?name:title;
        return zone.Owner.Kind switch{"Game"=>"游戏","Scenario"=>"剧本","Asset"=>"支援 "+ordinal,"Location"=>"地点 "+ordinal,"Investigator"=>"调查员 "+ordinal,_=>"对象 "+ordinal};
    }
    public static string Policy(ClientZone zone)=>zone.Reason switch{
        "locationUnderContainsFaceDown"=>"此处有朝下卡牌，只显示数量。","shuffledShopStackInspectionPolicyPending"=>"此地点有特殊查看规则，尚未接入。",
        "flippedAssetUnderPolicyPending"=>"此支援已翻面，下方卡的特殊查看规则尚未接入。","setAsideInspectionPolicyPending"=>"暂置卡牌不默认公开，特殊查看规则尚未接入。",
        "sideDeckPolicyPending"=>"此副牌组的特殊查看规则尚未接入。","marketMetadataUnavailable" or "spiritMetadataUnavailable"=>"核心未提供可用的特殊牌堆信息。",
        _ when !zone.Supported=>"此区域的特殊查看规则尚未接入。",_ when zone.CountOnly=>"此牌堆只允许查看数量。",_ when zone.Kind is "deck" or "encounterDeck" or "spiritDeck"=>"仅显示规则允许公开的牌顶，其余卡牌不可浏览。",_=>"只读查看 · 不会抽牌、移牌或提交答案。"};
}
