using System.Text.Json;

namespace Arkham.NativeHost;

// Finite projection from the actual native upgrade offer, not an accounting
// ledger or authorization to spend a user-supplied quote. Original rules remain
// responsible for applying UpgradeDeck and setting spentXp.
public static class LocalUpgradeContext
{
    public static JsonElement Project(JsonElement response,Guid game,long sequence,long revision,Guid player,string sourceId,Guid? eventId=null,long? eventRevision=null)
    {
        if(J.String(response,"scope")!="trusted-local-hotseat"||J.String(response,"recipient")!=player.ToString("D")||response.GetProperty("revision").GetInt64()!=revision)
            throw new UnauthorizedAccessException("升级预览的原生座位或版本不匹配。");
        var schema=J.Get(response,"deckSchema");
        if(J.Int(schema,"schemaVersion")!=1||J.String(schema,"task")!="upgrade"||J.Get(schema,"supported").ValueKind!=JsonValueKind.True||J.Get(schema,"canKeepDeck").ValueKind!=JsonValueKind.True)
            throw new UnauthorizedAccessException("当前座位没有正在等待的升级任务。");
        string iid=J.String(schema,"investigatorId");var owners=J.Props(J.Get(J.Get(response,"game"),"investigators")).Where(p=>J.String(p.Value,"playerId")==player.ToString("D")).ToArray();
        if(owners.Length!=1||owners[0].Name!=iid&&J.String(owners[0].Value,"id")!=iid)throw new UnauthorizedAccessException("升级调查员不属于当前座位。");
        var actor=owners[0].Value;
        if(!J.Get(actor,"xp").TryGetInt32Safe(out int xp)||!J.Get(actor,"spentXp").TryGetInt32Safe(out int spent))throw new InvalidDataException("原生经验记录缺失。");
        var cards=J.Get(schema,"currentCards");if(cards.ValueKind!=JsonValueKind.Object)throw new InvalidDataException("当前战役牌组缺失。");
        return JsonSerializer.SerializeToElement(new{schemaVersion=1,gameId=game,sequence,coreRevision=revision,playerId=player,sourceId,eventId,eventRevision,
            investigatorId=iid,reportedXp=xp,reportedSpentXp=spent,reportedUnspentXp=checked(xp-spent),currentCards=cards,
            contextAuthority="native-current-state-only",declaredQuoteAuthorizesMutation=false,previousDeckHistoryComplete=false});
    }
}

public sealed partial class LocalGameController
{
    public async Task<JsonElement> UpgradeContextAsync(Guid gameId,long expectedSequence,string expectedSha,long expectedRevision,Guid playerId)
    {
        await gate.WaitAsync().ConfigureAwait(false);try{
            Usable();string game=Key(gameId);var basis=store.ReadCurrent(game);
            if(basis.Sequence!=expectedSequence||basis.CoreRevision!=expectedRevision||basis.Sha256!=expectedSha)throw new StaleLocalState();
            await EnsureActive(basis).ConfigureAwait(false);var response=await Ask(new{apiVersion=1,method="view",session=live,player=Key(playerId)}).ConfigureAwait(false);
            var result=LocalUpgradeContext.Project(response,gameId,basis.Sequence,basis.CoreRevision,playerId,sourceId);var after=store.ReadCurrent(game);
            if(after.Sequence!=basis.Sequence||after.Sha256!=basis.Sha256)throw new StaleLocalState();return result;
        }finally{gate.Release();}
    }
}

public sealed partial class LocalEpicController
{
    public async Task<JsonElement> UpgradeContextAsync(Guid eventId,long expectedEventRevision,string expectedSha,Guid game,long expectedSequence,long expectedCoreRevision,Guid player)
    {
        await gate.WaitAsync().ConfigureAwait(false);try{
            Usable();var basis=Current(eventId,expectedEventRevision,expectedSha);
            var snapshot=Parse(basis.Snapshot);var group=Group(snapshot,game);
            if(group.GetProperty("sequence").GetInt64()!=expectedSequence)throw new StaleLocalState();
            var response=await Ask(new{apiVersion=1,method="epicView",request=new{snapshot,selection=new{role="Group",gameId=game,playerId=player}}}).ConfigureAwait(false);
            var result=LocalUpgradeContext.Project(response.GetProperty("response"),game,expectedSequence,expectedCoreRevision,player,sourceId,eventId,expectedEventRevision);
            _=Current(eventId,expectedEventRevision,expectedSha);return result;
        }finally{gate.Release();}
    }
}
