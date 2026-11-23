using System.Text.Json;
using System.Text.Json.Serialization;

namespace Arkham.NativeHost;

// Dedicated native projection / validated remote wire object, never a
// ClientGame, StoredState or hotseat PublicGame. No raw save bytes are present.
public sealed record LanSeatView
{
    [JsonPropertyName("gameId")] public Guid GameId {get;}
    [JsonPropertyName("sequence")] public long Sequence {get;}
    [JsonPropertyName("coreRevision")] public long CoreRevision {get;}
    [JsonPropertyName("sourceId")] public string SourceId {get;}
    [JsonPropertyName("view")] public JsonElement View {get;}
    internal LanSeatView(Guid gameId,long sequence,long coreRevision,string sourceId,JsonElement view)
    {GameId=gameId;Sequence=sequence;CoreRevision=coreRevision;SourceId=sourceId;View=view;}
    public static LanSeatView FromWire(JsonElement wire)
    {
        if(wire.ValueKind!=JsonValueKind.Object||!wire.EnumerateObject().Select(p=>p.Name).Order().SequenceEqual(new[]{"coreRevision","gameId","sequence","sourceId","view"}))throw new InvalidDataException("网络视图封套无效");
        Guid game=wire.GetProperty("gameId").GetGuid();long sequence=wire.GetProperty("sequence").GetInt64(),revision=wire.GetProperty("coreRevision").GetInt64();string source=wire.GetProperty("sourceId").GetString()!;var view=wire.GetProperty("view");
        if(game==Guid.Empty||sequence<0||revision<0||view.GetProperty("schemaVersion").GetInt32()!=1||view.GetProperty("scope").GetString()!="network-seat"||view.GetProperty("coreSourceId").GetString()!=source||view.GetProperty("revision").GetInt64()!=revision||view.GetProperty("recipient").GetGuid()==Guid.Empty)throw new InvalidDataException("网络视图身份无效");
        var allowed=new HashSet<string>(StringComparer.Ordinal){"schemaVersion","scope","recipient","phase","inSetup","state","activeInvestigatorId","leadInvestigatorId","scenarioSteps","board","own","question","interactionSchema","capabilities","revision","coreSourceId","settingsFormSchema","deckSchema","continuationSchema","campaignManagementSchema","triggerSchema","skillTest","choiceCards"};
        var seen=new HashSet<string>(StringComparer.Ordinal);foreach(var p in view.EnumerateObject())if(!allowed.Contains(p.Name)||!seen.Add(p.Name))throw new InvalidDataException("拒绝本地热座或未知网络视图字段");
        return new(game,sequence,revision,source,view.Clone());
    }
}

// Finite network surface only, not the full local/admin controller API.
public interface ILanGameAuthority
{
    Task<LanSeatView> NetworkViewAsync(Guid gameId,Guid hostBoundPlayer);
    Task<CommitReceipt> NetworkAnswerAsync(Guid bindingId,Guid gameId,Guid requestId,long expectedSequence,long expectedCoreRevision,Guid hostBoundPlayer,JsonElement answer);
    Task<CommitReceipt?> NetworkReceiptAsync(Guid bindingId,Guid gameId,Guid requestId,long expectedSequence,long expectedCoreRevision,Guid hostBoundPlayer,JsonElement originalAnswer);
    Task<JsonElement> NetworkSettingsPreviewAsync(Guid gameId,long expectedSequence,long expectedCoreRevision,Guid hostBoundPlayer,JsonElement draft);
    Task<JsonElement> NetworkLogsAsync(Guid gameId,long expectedSequence,long expectedCoreRevision,Guid hostBoundPlayer,int page);
    Task<JsonElement> NetworkRegionsAsync(Guid gameId,long expectedSequence,long expectedCoreRevision,Guid hostBoundPlayer);
}
