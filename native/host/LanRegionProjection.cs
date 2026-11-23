using System.Text.Json;

namespace Arkham.NativeHost;

// Reuse the original native region permissions and the one shared parser.
// Only this explicit DTO, never the trusted local region response, leaves LAN.
public static class LanRegionProjection
{
    static readonly HashSet<string> Personal=new(["hand","discard","under","bonded","deck","assets","threat","sideDeck"]),AssetKinds=new(["under","attachments","marketDeck","knownMarketDeck","spiritDeck"]),ScenarioKinds=new(["encounterDiscard","encounterDeck","victory","setAside","scenarioDeck"]);
    static InvalidDataException Invalid()=>new("Network region projection is invalid");
    public static JsonElement Project(LanSeatView basis,JsonElement trusted)
    {
        Guid player=basis.View.GetProperty("recipient").GetGuid();var parsed=ClientRegions.Read(trusted,player,basis.CoreRevision);var board=basis.View.GetProperty("board");
        Dictionary<string,JsonElement> Entities(string kind)=>board.GetProperty(kind).EnumerateArray().ToDictionary(e=>e.GetProperty("id").GetString()!,e=>e,StringComparer.Ordinal);
        var investigators=Entities("investigators");var assets=Entities("assets");var locations=Entities("locations");
        var own=investigators.Where(p=>p.Value.GetProperty("playerId").GetGuid()==player).Select(p=>p.Key).ToHashSet(StringComparer.Ordinal);var zones=new List<object>();
        foreach(var zone in parsed.Zones){
            var o=zone.Owner;JsonElement entity=default;bool face=false;
            switch(o.Kind){
                case "Investigator":if(!own.Contains(o.Id))continue;if(!Personal.Contains(zone.Kind))throw Invalid();entity=investigators[o.Id];face=true;break;
                case "Asset":if(!assets.TryGetValue(o.Id,out entity))continue;if(!AssetKinds.Contains(zone.Kind))throw Invalid();face=J.Get(entity,"flipped").ValueKind!=JsonValueKind.True;break;
                case "Location":if(!locations.TryGetValue(o.Id,out entity))continue;if(zone.Kind!="under")throw Invalid();face=J.Get(entity,"revealed").ValueKind==JsonValueKind.True;break;
                case "Scenario":if(!ScenarioKinds.Contains(zone.Kind))throw Invalid();break;
                case "Game":if(zone.Kind!="removed"||zone.Id!="game:removed")throw Invalid();break;
                default:throw Invalid();
            }
            if(zone.Kind is "marketDeck" or "knownMarketDeck"){
                string controller=J.String(entity,"controller");if(!own.Contains(controller))continue;
            }
            string title=face?J.String(entity,"name"):"",code=face?J.String(entity,"cardCode"):"";
            object owner=o.Kind=="Game"?new{kind="Game"}:(object)new{kind=o.Kind,id=o.Id,title=title.Length>0&&code.Length>0?title:null,cardCode=title.Length>0&&code.Length>0?code:null};
            bool flipped=o.Kind=="Asset"&&!face;var items=flipped?Array.Empty<ClientZoneItem>():zone.Items.ToArray();
            if((zone.Kind is "deck" or "encounterDeck" or "spiritDeck")&&items.Length>1||(zone.Kind is "marketDeck" or "scenarioDeck")&&items.Length!=0)throw Invalid();
            var projected=new List<object>();int ordinal=0;foreach(var item in items){string key=zone.Id+":"+ordinal++;
                if(item.Face)projected.Add(new{key,visibility="face",cardId=item.CardId,cardCode=item.Code,art=item.Art,name=item.Name,face=item.Side,vengeance=false,inspectAllowed=true});
                else{var back=new Dictionary<string,object?>{{"key",key},{"visibility","back"},{"backKind",item.BackKind},{"vengeance",item.Vengeance},{"inspectAllowed",false}};if(item.Reason is not null){back["reasonKey"]=item.Reason;back["status"]="unsupported";}projected.Add(back);}
            }
            zones.Add(new{id=zone.Id,kind=zone.Kind,owner,status=flipped?"unsupported":zone.Supported?"supported":"unsupported",order=flipped?"unknown":zone.Order,count=flipped?null:zone.Count,items=projected,reasonKey=flipped?"flippedAssetUnderPolicyPending":zone.Reason,access=zone.CountOnly?"countOnly":null});
        }
        var result=JsonSerializer.SerializeToElement(new{viewVersion=1,revision=basis.CoreRevision,recipient=player,zoneSchema=new{schemaVersion=1,recipient=player,supported=true,zones}});
        _=ClientRegions.Read(result,player,basis.CoreRevision);if(JsonSerializer.SerializeToUtf8Bytes(result).Length>3*1024*1024)throw Invalid();return result;
    }
}
