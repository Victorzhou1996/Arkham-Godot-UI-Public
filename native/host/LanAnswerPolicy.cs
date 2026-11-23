using System.Text.Json;

namespace Arkham.NativeHost;

// Permission policy is distinct from rule legality. Native validation still
// checks the actual offered question, owner and revision. Never admit local
// author/install/roster/history operations merely because they are typed.
public static class LanAnswerPolicy
{
    static readonly HashSet<string> Gameplay=new(StringComparer.Ordinal){
        "Answer","OrderedAnswer","PaymentAmountsAnswer","AmountsAnswer",
        "ExchangeAmountsAnswer","PickDestinyAnswer","CampaignSpecificAnswer",
        "ScenarioSpecificAnswer","SetTriggerModeAnswer","ResumeAutomaticTriggersAnswer",
        "CampaignStepAnswer","BeginUpgradeAnswer","BeginSideStoryAnswer","SettingsFormAnswer"};
    static void Fields(JsonElement obj,params string[] allowed)
    {
        if(obj.ValueKind!=JsonValueKind.Object)throw new InvalidDataException("回答必须为对象");
        var seen=new HashSet<string>(StringComparer.Ordinal);foreach(var p in obj.EnumerateObject())if(!allowed.Contains(p.Name,StringComparer.Ordinal)||!seen.Add(p.Name))throw new UnauthorizedAccessException("不支持的远端字段");
    }
    public static void Validate(JsonElement answer,Guid boundPlayer)
    {
        string tag=answer.GetProperty("tag").GetString()??"";
        if(tag=="ExchangeAmountsAnswer"){
            Fields(answer,"tag","source","fromInvestigator","toInvestigator","token","amount");
            if(answer.GetProperty("source").ValueKind!=JsonValueKind.Object||answer.GetProperty("fromInvestigator").ValueKind!=JsonValueKind.String||answer.GetProperty("toInvestigator").ValueKind!=JsonValueKind.String||answer.GetProperty("token").ValueKind!=JsonValueKind.String||!answer.GetProperty("amount").TryGetInt32(out _))throw new InvalidDataException("无效的交换数值回答");
            // Native validation still requires this exact source, pair, token
            // and bound in the host-assigned recipient's current question.
            return;
        }
        if(tag=="DeckListAnswer"){
            Fields(answer,"tag","deckList","playerId");
            if(answer.GetProperty("playerId").GetGuid()!=boundPlayer)throw new UnauthorizedAccessException("牌组不属于当前绑定座位");
            Deck(answer.GetProperty("deckList"));return;
        }
        if(tag=="UpgradeDeckAnswer"){
            Fields(answer,"tag","contents");var request=answer.GetProperty("contents");Fields(request,"investigatorId","deck");
            if(request.GetProperty("deck").ValueKind!=JsonValueKind.Null)Deck(request.GetProperty("deck"));return;
        }
        if(!Gameplay.Contains(tag))throw new UnauthorizedAccessException("此设置、作者内容或席位管理操作需在宿主执行");
        Fields(answer,"tag","contents");
    }
    static void Deck(JsonElement deck)
    {
        // No description Markdown/custom definitions/URLs from a remote. Only
        // standard deck counts and finite existing attachment metadata. Actual
        // card validity is resolved from the host's registered rule catalog.
        Fields(deck,"slots","sideSlots","investigator_code","investigator_name","meta","taboo_id","id","name");
        foreach(var name in new[]{"slots","sideSlots"})if(deck.TryGetProperty(name,out var counts)){
            if(counts.ValueKind!=JsonValueKind.Object)throw new InvalidDataException("无效牌组数量");
            foreach(var p in counts.EnumerateObject())if(p.Value.ValueKind!=JsonValueKind.Number||!p.Value.TryGetInt32(out int n)||n is <0 or >999)throw new InvalidDataException("无效牌组数量");
        }
        if(deck.TryGetProperty("meta",out var meta)&&meta.ValueKind!=JsonValueKind.Null){
            if(meta.ValueKind!=JsonValueKind.String)throw new InvalidDataException("无效牌组元信息");
            // This is only structural filtering. Inside the actual controller
            // gate, the host obtains selector keys/values from its own core.
            _=LanDeckMetadata.Meta(deck);
        }
    }
}
