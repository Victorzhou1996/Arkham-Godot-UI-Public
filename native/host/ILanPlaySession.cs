using System.Text.Json;
namespace Arkham.NativeHost;
public sealed record LanPendingStatus(Guid RequestId,bool KnownNotCommitted);
// Shared UI contract, never a hotseat ClientGame. Epic implementations retain
// their event basis internally while exposing only the authorized seat view.
public interface ILanPlaySession:IAsyncDisposable
{
    LanSeatView? View {get;}
    string Status {get;}
    bool Connected {get;}
    bool CanAnswer {get;}
    LanPendingStatus? Pending {get;}
    Task PollAsync(CancellationToken cancellation);
    Task SubmitAsync(JsonElement answer,CancellationToken cancellation);
    Task ResolvePendingAsync(CancellationToken cancellation);
    Task RetryKnownUncommittedAsync(CancellationToken cancellation);
    void CancelKnownUncommitted();
    Task<JsonElement> PreviewSettingsAsync(LanSeatView basis,JsonElement draft,CancellationToken cancellation);
    Task<JsonElement> LogsAsync(LanSeatView basis,int page,CancellationToken cancellation);
    Task<JsonElement> RegionsAsync(LanSeatView basis,CancellationToken cancellation);
}
