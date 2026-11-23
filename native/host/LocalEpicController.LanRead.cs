using System.Text.Json;
namespace Arkham.NativeHost;

public sealed partial class LocalEpicController
{
    // All three readonly operations bind the entire event revision as well as
    // current membership; an unchanged group sequence is not sufficient.
    public async Task<JsonElement> NetworkEpicReadAsync(Guid player,LanClientFrame request)
    {
        var basis=request switch {
            LanEpicSettingsPreview p=>(p.EventId,p.ExpectedEventRevision,p.GroupId,p.ExpectedSequence,p.ExpectedCoreRevision),
            LanEpicRegions p=>(p.EventId,p.ExpectedEventRevision,p.GroupId,p.ExpectedSequence,p.ExpectedCoreRevision),
            LanEpicLogs p=>(p.EventId,p.ExpectedEventRevision,p.GroupId,p.ExpectedSequence,p.ExpectedCoreRevision),
            _=>throw new UnauthorizedAccessException("Unknown Epic readonly operation")
        };
        await gate.WaitAsync().ConfigureAwait(false);try{
            Usable();_=LanProtocol.EncodeClient(request);var state=Current(basis.EventId,basis.ExpectedEventRevision);
            var view=await NetworkEpicViewLocked(state,player).ConfigureAwait(false);
            if(view.GroupId!=basis.GroupId||view.GroupSequence!=basis.ExpectedSequence||view.CoreRevision!=basis.ExpectedCoreRevision)throw new StaleLocalState();
            var snapshot=Parse(state.Snapshot);JsonElement result;
            if(request is LanEpicSettingsPreview settings){
                var r=await Ask(new{apiVersion=1,method="epicSettingsPreview",snapshot,gameId=view.GroupId,player,draft=settings.Draft}).ConfigureAwait(false);
                result=JsonSerializer.SerializeToElement(new{eventId=view.EventId,eventRevision=view.EventRevision,gameId=view.GroupId,sequence=view.GroupSequence,coreRevision=view.CoreRevision,sourceId,playerId=player,preview=r.GetProperty("preview")});
            }else if(request is LanEpicRegions){
                var r=await Ask(new{apiVersion=1,method="epicNetworkRegions",snapshot,player}).ConfigureAwait(false);
                var native=LanEpicSeatView.FromWire(r.GetProperty("nativeView"));
                if(native.EventId!=view.EventId||native.EventRevision!=view.EventRevision||native.GroupId!=view.GroupId||native.GroupSequence!=view.GroupSequence||native.CoreRevision!=view.CoreRevision||native.PlayerId!=player||native.SourceId!=sourceId)throw new InvalidDataException("Epic region basis differs");
                var regions=LanRegionProjection.Project(native.Seat,r.GetProperty("regions"));
                result=JsonSerializer.SerializeToElement(new{eventId=view.EventId,eventRevision=view.EventRevision,gameId=view.GroupId,sequence=view.GroupSequence,coreRevision=view.CoreRevision,sourceId,playerId=player,regions});
            }else{
                var logs=(LanEpicLogs)request;
                var history=ClientLogs.ReadEpic(view.GroupId.ToString("D"),sourceId,state.Revision,player,n=>{
                    var row=store.ReadState(state.EventId,n);
                    using var d=JsonDocument.Parse(row.Events??"[]"u8.ToArray());
                    var streams=d.RootElement.EnumerateArray().Where(s=>s.GetProperty("groupId").GetGuid()==view.GroupId).Select(s=>s.Clone()).ToArray();
                    return new ClientLogState(view.GroupId.ToString("D"),row.SourceId,row.Revision,row.Parent,null,"event",JsonSerializer.SerializeToElement(streams));
                });
                result=JsonSerializer.SerializeToElement(new{eventId=view.EventId,eventRevision=view.EventRevision,gameId=view.GroupId,sequence=view.GroupSequence,coreRevision=view.CoreRevision,sourceId,playerId=player,pageData=LanLogProjection.Page(history,logs.Page)});
            }
            _=Current(state.EventId,state.Revision,state.Sha256);return result;
        }finally{gate.Release();}
    }
}
