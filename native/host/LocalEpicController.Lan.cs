using System.Text.Json;
namespace Arkham.NativeHost;

public sealed partial class LocalEpicController:ILanEpicAuthority
{
    static JsonElement MemberGroup(JsonElement snapshot,Guid player)
    {
        var groups=snapshot.GetProperty("groups").EnumerateArray().Where(g=>g.GetProperty("members").EnumerateArray().Any(p=>p.GetGuid()==player)).ToArray();
        if(player==Guid.Empty||groups.Length!=1)throw new UnauthorizedAccessException("Epic player has no unique current group");return groups[0];
    }
    async Task<LanEpicSeatView> NetworkEpicViewLocked(StoredEpicState state,Guid player)
    {
        var snapshot=Parse(state.Snapshot);var group=MemberGroup(snapshot,player);
        var response=await Ask(new{apiVersion=1,method="epicNetworkView",snapshot,player=player.ToString("D")}).ConfigureAwait(false);
        var view=LanEpicSeatView.FromWire(response.GetProperty("nativeView"));
        if(view.EventId!=state.EventId||view.EventRevision!=state.Revision||view.SourceId!=sourceId||view.PlayerId!=player||view.GroupId!=group.GetProperty("gameId").GetGuid()||view.GroupSequence!=group.GetProperty("sequence").GetInt64()||view.CoreRevision!=group.GetProperty("snapshot").GetProperty("revision").GetInt64())throw new InvalidDataException("Native Epic projection differs from committed state");
        _=Current(state.EventId,state.Revision,state.Sha256);return view;
    }
    public async Task<LanEpicSeatView> NetworkEpicViewAsync(Guid eventId,Guid player)
    {await gate.WaitAsync().ConfigureAwait(false);try{Usable();return await NetworkEpicViewLocked(Current(eventId),player).ConfigureAwait(false);}finally{gate.Release();}}
    static string NetworkEpicHash(Guid binding,Guid player,LanEpicAnswer intent)
    {
        if(binding==Guid.Empty||player==Guid.Empty)throw new UnauthorizedAccessException("Epic binding required");_=LanProtocol.EncodeClient(intent);
        return Hash("networkAnswer",intent.EventId,intent.ExpectedEventRevision,new{bindingId=binding,playerId=player,groupId=intent.GroupId,expectedSequence=intent.ExpectedSequence,expectedCoreRevision=intent.ExpectedCoreRevision,answer=intent.Answer});
    }
    public async Task<EpicCommitReceipt?> NetworkEpicReceiptAsync(Guid bindingId,Guid player,LanEpicAnswer intent)
    {await gate.WaitAsync().ConfigureAwait(false);try{Usable();LanAnswerPolicy.Validate(intent.Answer,player);return store.Replay(intent.EventId,intent.RequestId,NetworkEpicHash(bindingId,player,intent));}finally{gate.Release();}}
    async Task ValidateEpicDeck(JsonElement view,JsonElement answer)
    {
        string tag=J.Tag(answer);JsonElement deck;if(tag=="DeckListAnswer")deck=answer.GetProperty("deckList");else if(tag=="UpgradeDeckAnswer"){deck=answer.GetProperty("contents").GetProperty("deck");if(deck.ValueKind==JsonValueKind.Null)return;}else return;
        var meta=LanDeckMetadata.Meta(deck);
        if(LanDeckMetadata.RequiresNativeValidation(meta)){
            var checkedMetadata=await Ask(new{apiVersion=1,method="deckMetadataValidate",request=new{deck}}).ConfigureAwait(false);
            if(checkedMetadata.GetProperty("coreSourceId").GetString()!=sourceId)throw new InvalidDataException("Deck metadata source mismatch");
            meta=LanDeckMetadata.RemainingAfterNative(meta,checkedMetadata.GetProperty("result"));
        }
        if(!LanDeckMetadata.RequiresDeckSchema(deck,meta))return;
        var q=view.GetProperty("question");int depth=0;while(J.Tag(q) is "QuestionLabel" or "QuestionWithSource" or "PayCostQuestion"){if(++depth>32)throw new InvalidDataException("Question depth exceeded");q=q.GetProperty("question");}
        string mode=J.Tag(q) switch{"ChooseDeck"=>"initial","ChooseUpgradeDeck"=>"upgrade",_=>throw new UnauthorizedAccessException("No deck question offered")};
        async Task<JsonElement> Schema(JsonElement selected){var r=await Ask(new{apiVersion=1,method="deckBuildSchema",request=LanDeckMetadata.Request(deck,mode,selected)}).ConfigureAwait(false);if(r.GetProperty("coreSourceId").GetString()!=sourceId)throw new InvalidDataException("Deck schema source mismatch");return r.GetProperty("result").Clone();}
        var schema=await Schema(JsonSerializer.SerializeToElement(new{})).ConfigureAwait(false);var selected=LanDeckMetadata.Selections(meta,schema);if(selected.EnumerateObject().Any())_=LanDeckMetadata.Selections(meta,await Schema(selected).ConfigureAwait(false));
    }
    public async Task<EpicCommitReceipt> NetworkEpicAnswerAsync(Guid bindingId,Guid player,LanEpicAnswer intent)
    {
        await gate.WaitAsync().ConfigureAwait(false);try{
            Usable();LanAnswerPolicy.Validate(intent.Answer,player);string hash=NetworkEpicHash(bindingId,player,intent);
            var replay=store.Replay(intent.EventId,intent.RequestId,hash);if(replay is not null)return replay;
            var basis=Current(intent.EventId,intent.ExpectedEventRevision);var snapshot=Parse(basis.Snapshot);var group=MemberGroup(snapshot,player);
            if(group.GetProperty("gameId").GetGuid()!=intent.GroupId||group.GetProperty("sequence").GetInt64()!=intent.ExpectedSequence||group.GetProperty("snapshot").GetProperty("revision").GetInt64()!=intent.ExpectedCoreRevision)throw new StaleLocalState();
            var view=await NetworkEpicViewLocked(basis,player).ConfigureAwait(false);await ValidateEpicDeck(view.Seat.View,intent.Answer).ConfigureAwait(false);
            var command=new{tag="GroupAnswer",gameId=intent.GroupId,expectedSequence=intent.ExpectedSequence,playerId=player,answer=intent.Answer};
            long now=DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var result=await Ask(new{apiVersion=1,method="epicApply",request=new{snapshot,expectedRevision=basis.Revision,operationId=intent.RequestId,nowUnixSeconds=now,command,undoBase=(object?)null}}).ConfigureAwait(false);
            var candidate=await Candidate(result,basis).ConfigureAwait(false);return Publish("本地多组事件",intent.RequestId,hash,candidate,basis);
        }finally{gate.Release();}
    }
}
