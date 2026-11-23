using System.Text.Json;

namespace Arkham.NativeHost;

public sealed partial class LocalGameController
{
    // Caller already owns the controller gate and has loaded the actual basis.
    // Never call the public DeckBuildPreviewAsync here (would re-enter its gate).
    async Task ValidateNetworkDeckAsync(Guid player,JsonElement answer)
    {
        string tag=answer.GetProperty("tag").GetString()!;JsonElement deck;
        if(tag=="DeckListAnswer")deck=answer.GetProperty("deckList");
        else if(tag=="UpgradeDeckAnswer"){deck=answer.GetProperty("contents").GetProperty("deck");if(deck.ValueKind==JsonValueKind.Null)return;}
        else return;
        var meta=LanDeckMetadata.Meta(deck);
        if(LanDeckMetadata.RequiresNativeValidation(meta)){
            var checkedMetadata=await Ask(new{apiVersion=1,method="deckMetadataValidate",request=new{deck}}).ConfigureAwait(false);
            if(checkedMetadata.GetProperty("coreSourceId").GetString()!=sourceId)throw new InvalidDataException("牌组元数据核心来源不同");
            meta=LanDeckMetadata.RemainingAfterNative(meta,checkedMetadata.GetProperty("result"));
        }
        // Existing base metadata is already the old finite permission surface;
        // do not make shipped Build catalog coverage a new prerequisite for a
        // host-installed homebrew investigator with no extra selector metadata.
        if(!LanDeckMetadata.RequiresDeckSchema(deck,meta))return;
        var response=await Ask(new{apiVersion=1,method="networkView",session=live,player=Key(player)}).ConfigureAwait(false);
        var view=response.GetProperty("networkView");var q=view.GetProperty("question");int wrappers=0;
        while(q.ValueKind==JsonValueKind.Object&&q.GetProperty("tag").GetString() is "QuestionLabel" or "QuestionWithSource" or "PayCostQuestion"){
            if(++wrappers>32)throw new InvalidDataException("牌组问题嵌套过深");q=q.GetProperty("question");
        }
        string question=q.ValueKind==JsonValueKind.Object?q.GetProperty("tag").GetString()!:"";
        if(question is not("ChooseDeck" or "ChooseUpgradeDeck"))throw new UnauthorizedAccessException("当前座位未收到选择或升级牌组的问题");
        string mode=question=="ChooseUpgradeDeck"?"upgrade":"initial";
        async Task<JsonElement> Schema(JsonElement selected){
            var r=await Ask(new{apiVersion=1,method="deckBuildSchema",request=LanDeckMetadata.Request(deck,mode,selected)}).ConfigureAwait(false);
            if(r.GetProperty("coreSourceId").GetString()!=sourceId)throw new InvalidDataException("构筑协议核心来源不同");return r.GetProperty("result").Clone();
        }
        var empty=JsonSerializer.SerializeToElement(new{});var schema=await Schema(empty).ConfigureAwait(false);
        var selected=LanDeckMetadata.Selections(meta,schema);
        if(selected.EnumerateObject().Any()){
            var confirmed=await Schema(selected).ConfigureAwait(false);
            _=LanDeckMetadata.Selections(meta,confirmed);
        }
        // supportStatus may include original Build unsupported-definition or
        // incomplete-base-construction diagnostics. Do not invent a new deck
        // legality gate from those; native original Answer still validates it.
    }
}
