using System.Text.Json;
using System.Text.Json.Serialization;
namespace Arkham.NativeHost;

public sealed record LanEpicSeatView
{
    [JsonPropertyName("eventId")] public Guid EventId {get;init;}
    [JsonPropertyName("eventRevision")] public long EventRevision {get;init;}
    [JsonPropertyName("groupId")] public Guid GroupId {get;init;}
    [JsonPropertyName("groupSequence")] public long GroupSequence {get;init;}
    [JsonPropertyName("coreRevision")] public long CoreRevision {get;init;}
    [JsonPropertyName("sourceId")] public string SourceId {get;init;}="";
    [JsonPropertyName("playerId")] public Guid PlayerId {get;init;}
    [JsonPropertyName("seat")] public LanSeatView Seat {get;init;}=null!;
    [JsonPropertyName("shared")] public JsonElement Shared {get;init;}
    static void Exact(JsonElement v,params string[] names){if(v.ValueKind!=JsonValueKind.Object)throw new InvalidDataException("Invalid Epic view");var remaining=names.ToHashSet(StringComparer.Ordinal);foreach(var p in v.EnumerateObject())if(!remaining.Remove(p.Name))throw new InvalidDataException("Unknown Epic view field");if(remaining.Count!=0)throw new InvalidDataException("Incomplete Epic view");}
    public static LanEpicSeatView FromWire(JsonElement v)
    {
        Exact(v,"eventId","eventRevision","groupId","groupSequence","coreRevision","sourceId","playerId","seat","shared");
        var seat=LanSeatView.FromWire(v.GetProperty("seat"));var result=new LanEpicSeatView{EventId=v.GetProperty("eventId").GetGuid(),EventRevision=v.GetProperty("eventRevision").GetInt64(),GroupId=v.GetProperty("groupId").GetGuid(),GroupSequence=v.GetProperty("groupSequence").GetInt64(),CoreRevision=v.GetProperty("coreRevision").GetInt64(),SourceId=v.GetProperty("sourceId").GetString()!,PlayerId=v.GetProperty("playerId").GetGuid(),Seat=seat,Shared=v.GetProperty("shared").Clone()};
        if(result.EventId==Guid.Empty||result.EventId==result.GroupId||result.PlayerId==Guid.Empty||result.EventRevision<0||result.GroupId!=seat.GameId||result.GroupSequence!=seat.Sequence||result.CoreRevision!=seat.CoreRevision||result.SourceId!=seat.SourceId||result.PlayerId!=seat.View.GetProperty("recipient").GetGuid())throw new InvalidDataException("Epic view identity mismatch");
        var shared=result.Shared;Exact(shared,"schemaVersion","totalGroups","totalInvestigators","countermeasures","timeLimitMinutes","timerStartedAt","groupsReady","awaitingOrganizer");
        if(shared.GetProperty("schemaVersion").GetInt32()!=1)throw new InvalidDataException("Epic summary version unsupported");
        foreach(string field in new[]{"totalGroups","totalInvestigators","countermeasures","timeLimitMinutes","timerStartedAt","groupsReady"})if(shared.GetProperty(field).GetInt64()<0)throw new InvalidDataException("Negative Epic summary");
        if(shared.GetProperty("awaitingOrganizer").ValueKind is not(JsonValueKind.True or JsonValueKind.False))throw new InvalidDataException("Invalid Epic gate");return result;
    }
}
public interface ILanEpicAuthority
{
    Task<JsonElement> NetworkEpicReadAsync(Guid player,LanClientFrame request);
    Task<LanEpicSeatView> NetworkEpicViewAsync(Guid eventId,Guid player);
    Task<EpicCommitReceipt> NetworkEpicAnswerAsync(Guid bindingId,Guid player,LanEpicAnswer intent);
    Task<EpicCommitReceipt?> NetworkEpicReceiptAsync(Guid bindingId,Guid player,LanEpicAnswer intent);
}
