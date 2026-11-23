using System.Text.Json;

// Presentation only. Never use decoded resource keys as entity IDs or Answers.
public static class ClientDisplay
{
    public static JsonElement Reference(JsonElement game,JsonElement reference,int depth=0)
    {
        if(depth>8)return default;
        if(J.Tag(reference)=="AbilitySource"){var values=J.Get(reference,"contents");return values.ValueKind==JsonValueKind.Array&&values.GetArrayLength()==2?Reference(game,values[0],depth+1):default;}
        if(J.Tag(reference)=="ProxySource")
        {
            // A location may grant an ability to a visible asset (e.g. Lita).
            // The public proxy source is the card presented to the player;
            // originalSource is only the card hosting that granted ability.
            var source=Reference(game,J.Get(reference,"source"),depth+1);
            return source.ValueKind==JsonValueKind.Object?source:Reference(game,J.Get(reference,"originalSource"),depth+1);
        }
        var id=J.Get(reference,"contents");if(id.ValueKind!=JsonValueKind.String)return default;
        // Only resolve explicit presentation references, never UI.messages or legality.
        string? group=J.Tag(reference) switch{"CardIdTarget" or "CardIdSource"=>"cards","InvestigatorTarget" or "InvestigatorSource" or "ResourceSource"=>"investigators","AssetTarget" or "AssetSource"=>"assets","EventTarget" or "EventSource"=>"events","EnemyTarget" or "EnemySource"=>"enemies","LocationTarget" or "LocationSource"=>"locations","SkillTarget" or "SkillSource"=>"skills","TreacheryTarget" or "TreacherySource"=>"treacheries","ActSource" or "ActTarget"=>"acts","AgendaSource" or "AgendaTarget"=>"agendas","EffectSource" or "EffectTarget"=>"effects","StorySource" or "StoryTarget"=>"stories",_=>null};
        return group is null?default:J.Get(J.Get(game,group),id.GetString()!);
    }
    public static JsonElement SkillSourceCard(JsonElement game,JsonElement test)
    {
        var source=Reference(game,J.Get(test,"source"));if(CardCode(source).Length>0)return source;var id=J.String(test,"sourceCard");return id.Length>0?J.Get(J.Get(game,"cards"),id):default;
    }
    public static string ComponentCaption(JsonElement component)
    {
        if(J.Tag(component)=="InvestigatorDeckComponent")return "抽牌";if(J.Tag(component) is not ("InvestigatorComponent" or "AssetComponent"))return "";
        return J.String(component,"tokenType") switch{"ResourceToken"=>"资源操作","ClueToken"=>"线索操作","DamageToken"=>"伤害操作","HorrorToken"=>"恐惧操作","DoomToken"=>"毁灭操作",_=>""};
    }
    public static string BasicActionCaption(JsonElement ability)
    {
        if(J.Get(ability,"basic").ValueKind!=JsonValueKind.True)return "";var actions=J.Get(J.Get(ability,"type"),"actions");if(J.Tag(actions)!="SingleAction")return "";
        var action=J.Get(actions,"contents");return action.ValueKind==JsonValueKind.String?action.GetString() switch{"Investigate"=>"调查","Move"=>"移动","Fight"=>"攻击","Evade"=>"躲避","Engage"=>"交战",_=>""}:"";
    }
    public static string ChaosFace(JsonElement token)=>J.String(token,"chaosTokenFace");
    public static string CardCode(JsonElement entity,int depth=0)
    {
        if(depth>5)return "";
        if(entity.ValueKind==JsonValueKind.String){var s=entity.GetString()!;return s.Length>1&&s[0]=='c'?s[1..]:"";}
        foreach(string key in new[]{"cardCode","code","cardDef","card","contents"}){string found=CardCode(J.Get(entity,key),depth+1);if(found.Length>0)return found;}
        return "";
    }
    public static int? Token(JsonElement entity,string token)
    {
        var tokens=J.Get(entity,"tokens");
        // Aeson Map Token Int is an array of key/value pairs, not top-level stats.
        if(tokens.ValueKind==JsonValueKind.Array){foreach(var pair in tokens.EnumerateArray()){if(pair.ValueKind!=JsonValueKind.Array||pair.GetArrayLength()!=2)return null;if(pair[0].ValueKind!=JsonValueKind.String||!pair[1].TryGetInt32Safe(out int n))return null;if(pair[0].GetString()==token)return n;}return 0;}
        if(tokens.ValueKind==JsonValueKind.Object){var n=J.Get(tokens,token);return n.ValueKind==JsonValueKind.Undefined?0:n.TryGetInt32Safe(out int value)?value:null;}
        return null;
    }
    public static JsonElement Continuation(JsonElement game)
    {
        var mode=J.Get(game,"mode");var campaign=J.Get(mode,"This");var scenario=J.Get(mode,"That");
        // Data.These uses both This and That keys, never a "These" array.
        if(campaign.ValueKind==JsonValueKind.Object&&scenario.ValueKind==JsonValueKind.Object&&J.Tag(J.Get(scenario,"campaignStep"))=="ScenarioStepWithOptions")return default;
        JsonElement Unwrap(JsonElement step){if(J.Tag(step)=="StandaloneScenarioStep"){var a=J.Get(step,"contents");if(a.ValueKind==JsonValueKind.Array&&a.GetArrayLength()==2)step=a[1];}return J.Tag(step)=="ContinueCampaignStep"?J.Get(step,"contents"):default;}
        var continuation=Unwrap(J.Get(scenario,"campaignStep"));return continuation.ValueKind==JsonValueKind.Object?continuation:Unwrap(J.Get(campaign,"step"));
    }
    public static string SkillOutcome(JsonElement result)
    {
        if(J.Tag(result)=="Unrun")return "尚未结算";var values=J.Get(result,"contents");
        if(values.ValueKind!=JsonValueKind.Array||values.GetArrayLength()!=2||!values[1].TryGetInt32Safe(out int n))return "结果未适配";
        return J.Tag(result) switch{"SucceededBy"=>"成功 · 超过 "+n+" 点","FailedBy"=>"失败 · 差 "+n+" 点",_=>"结果未适配"};
    }
}
