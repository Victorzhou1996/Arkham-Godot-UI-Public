using System.Text;
using System.Text.Json;

namespace Arkham.NativeHost;

// One event command produces one candidate containing every group. No group is
// installed as an ordinary session, and only a durable event receipt publishes it.
public sealed partial class LocalEpicController : IAsyncDisposable
{
    readonly NativeCoreWorker core;readonly LocalEpicStore store;readonly string sourceId;
    readonly SemaphoreSlim gate=new(1,1);bool disposed;Exception? terminalFault;
    public LocalEpicController(NativeCoreWorker worker,LocalEpicStore database,string expectedSource){core=worker;store=database;sourceId=expectedSource;}
    static JsonElement Parse(byte[] bytes){using var doc=JsonDocument.Parse(bytes,new JsonDocumentOptions{MaxDepth=256});return doc.RootElement.Clone();}
    static string Hash(string method,Guid id,long expected,object body)=>LocalEpicStore.Hash(JsonSerializer.SerializeToUtf8Bytes(new{format=1,method,eventId=id,expectedRevision=expected,body}));
    void Usable(){ObjectDisposedException.ThrowIf(disposed,this);if(terminalFault is not null)throw new InvalidOperationException("Epic transport failed; reopen the committed database in a new process",terminalFault);}
    async Task<JsonElement> Ask(object request)
    {
        JsonElement response;try{response=await core.RequestAsync(request).ConfigureAwait(false);}catch(Exception error){terminalFault??=error;throw;}
        if(!response.GetProperty("ok").GetBoolean())throw new InvalidOperationException(response.GetProperty("error").GetProperty("message").GetString());return response;
    }
    StoredEpicState Current(Guid id,long? expected=null,string? sha=null)
    {
        var state=store.ReadCurrent(id);if(state.SourceId!=sourceId)throw new InvalidDataException("Epic core differs; keep the old client/save");
        if(expected is long revision&&state.Revision!=revision||sha is not null&&state.Sha256!=sha)throw new StaleLocalState();return state;
    }
    static JsonElement Group(JsonElement eventSnapshot,Guid game)=>eventSnapshot.GetProperty("groups").EnumerateArray().Single(g=>g.GetProperty("gameId").GetGuid()==game);
    async Task<byte[]> DeliveryEvents(JsonElement snapshot,JsonElement wrappers)
    {
        var streams=new List<object>();
        if(wrappers.ValueKind!=JsonValueKind.Array)throw new InvalidDataException("Native Epic event streams missing");
        foreach(var wrapper in wrappers.EnumerateArray()){
            Guid game=wrapper.GetProperty("gameId").GetGuid();var group=Group(snapshot,game);var events=wrapper.GetProperty("events");
            foreach(var member in group.GetProperty("members").EnumerateArray()){
                string player=member.GetString()!;
                var filtered=await Ask(new{apiVersion=1,method="epicFilterEvents",snapshot,gameId=game,player,events}).ConfigureAwait(false);
                streams.Add(new{groupId=game,recipient=player,events=filtered.GetProperty("events").Clone()});
            }
        }
        return JsonSerializer.SerializeToUtf8Bytes(streams);
    }
    async Task<StoredEpicState> Candidate(JsonElement response,StoredEpicState? basis)
    {
        var snapshot=response.GetProperty("snapshot");
        var events=await DeliveryEvents(snapshot,response.GetProperty("events")).ConfigureAwait(false);
        var state=LocalEpicStore.Candidate(JsonSerializer.SerializeToUtf8Bytes(snapshot),basis?.Revision,events);
        if(state.SourceId!=sourceId||state.Revision!=(basis is null?0:checked(basis.Revision+1))||basis is not null&&state.EventId!=basis.EventId)throw new InvalidDataException("Native event candidate identity/revision mismatch");return state;
    }
    EpicCommitReceipt Publish(string title,Guid request,string hash,StoredEpicState candidate,StoredEpicState? basis)
    {
        try{return store.Commit(title,request,hash,candidate,basis);}
        catch(Exception primary){
            // Never evaluate the rules a second time on an uncertain COMMIT.
            try{var receipt=store.RecoverReceipt(candidate.EventId,request,hash);if(receipt is not null)return receipt;}
            catch(Exception recovery){throw new AggregateException("Epic commit outcome requires a new-process recovery; no rule retry",primary,recovery);}
            throw;
        }
    }
    public async Task<EpicCommitReceipt> CreateAsync(string title,Guid requestId,JsonElement definition)
    {
        await gate.WaitAsync().ConfigureAwait(false);try{
            Usable();Guid id=definition.GetProperty("eventId").GetGuid();string hash=Hash("create",id,-1,new{title,request=definition});
            var replay=store.Replay(id,requestId,hash);if(replay is not null)return replay;
            var result=await Ask(new{apiVersion=1,method="epicCreate",request=definition}).ConfigureAwait(false);
            var candidate=await Candidate(result,null).ConfigureAwait(false);if(candidate.EventId!=id)throw new InvalidDataException("Created wrong event");
            return Publish(title,requestId,hash,candidate,null);
        }finally{gate.Release();}
    }
    async Task<JsonElement> UndoBase(StoredEpicState state,JsonElement command)
    {
        var snapshot=Parse(state.Snapshot);Guid game=command.GetProperty("gameId").GetGuid(),action=command.GetProperty("targetActionId").GetGuid();
        var history=Group(snapshot,game).GetProperty("history").EnumerateArray().Single(h=>h.GetProperty("actionId").GetGuid()==action);
        string expected=history.GetProperty("beforeSnapshotSha256").GetString()!;JsonElement value;
        try{
            var old=store.ReadState(state.EventId,history.GetProperty("beforeEventRevision").GetInt64());
            var result=await Ask(new{apiVersion=1,method="epicExtractUndoBase",snapshot=Parse(old.Snapshot),gameId=game}).ConfigureAwait(false);
            value=result.GetProperty("undoBase").Clone();
        }catch(KeyNotFoundException){
            var obj=store.FindUndoObject(expected)??throw new InvalidDataException("Undo object missing from imported event; do not drop/rebase history");
            if(obj.SourceId!=sourceId)throw new InvalidDataException("Undo object core mismatch");
            value=JsonSerializer.SerializeToElement(new{snapshotText=Encoding.UTF8.GetString(obj.Snapshot),sha256=obj.Sha256});
        }
        if(value.GetProperty("sha256").GetString()!=expected)throw new InvalidDataException("Immutable history undo object mismatch");return value;
    }
    public async Task<EpicCommitReceipt> ApplyAsync(Guid eventId,Guid requestId,long expectedRevision,string expectedSha,JsonElement command)
    {
        await gate.WaitAsync().ConfigureAwait(false);try{
            Usable();string hash=Hash("apply",eventId,expectedRevision,new{expectedSha,command});
            var replay=store.Replay(eventId,requestId,hash);if(replay is not null)return replay;
            var basis=Current(eventId,expectedRevision,expectedSha);JsonElement? undo=null;
            if(command.GetProperty("tag").GetString()=="UndoGroup")undo=await UndoBase(basis,command).ConfigureAwait(false);
            // Wall time is a trusted derived host input, obtained once AFTER
            // receipt lookup. A committed retry never reruns with a new clock.
            long now=DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var result=await Ask(new{apiVersion=1,method="epicApply",request=new{snapshot=Parse(basis.Snapshot),expectedRevision,operationId=requestId,nowUnixSeconds=now,command,undoBase=undo}}).ConfigureAwait(false);
            var candidate=await Candidate(result,basis).ConfigureAwait(false);
            return Publish("本地多组事件",requestId,hash,candidate,basis);
        }finally{gate.Release();}
    }
    public async Task<JsonElement> ViewAsync(Guid eventId,Guid? game=null,Guid? player=null)
    {
        await gate.WaitAsync().ConfigureAwait(false);try{
            Usable();var state=Current(eventId);object selection=game is Guid gid&&player is Guid pid?new{role="Group",gameId=gid,playerId=pid}:new{role="Organizer"};
            return await Ask(new{apiVersion=1,method="epicView",request=new{snapshot=Parse(state.Snapshot),selection}}).ConfigureAwait(false);
        }finally{gate.Release();}
    }
    public async Task<JsonElement> SettingsPreviewAsync(Guid eventId,long revision,string sha,Guid game,Guid player,JsonElement draft)
    {
        await gate.WaitAsync().ConfigureAwait(false);try{
            Usable();var basis=Current(eventId,revision,sha);var result=await Ask(new{apiVersion=1,method="epicSettingsPreview",snapshot=Parse(basis.Snapshot),gameId=game,player,draft}).ConfigureAwait(false);
            _=Current(eventId,revision,sha);return result;
        }finally{gate.Release();}
    }
    // Export/import use native canonical objects rather than C# reencoding
    // Haskell snapshots. Only still-referenced history objects are collected.
    public async Task<IReadOnlyList<EpicUndoObject>> ExportUndoObjectsAsync(Guid eventId,long revision,string sha)
    {
        await gate.WaitAsync().ConfigureAwait(false);try{
            Usable();var state=Current(eventId,revision,sha);var snapshot=Parse(state.Snapshot);var objects=new Dictionary<string,EpicUndoObject>(StringComparer.Ordinal);
            foreach(var group in snapshot.GetProperty("groups").EnumerateArray())foreach(var history in group.GetProperty("history").EnumerateArray()){
                string digest=history.GetProperty("beforeSnapshotSha256").GetString()!;if(objects.ContainsKey(digest))continue;
                var command=JsonSerializer.SerializeToElement(new{gameId=group.GetProperty("gameId").GetGuid(),targetActionId=history.GetProperty("actionId").GetGuid()});
                var value=await UndoBase(state,command).ConfigureAwait(false);
                objects[digest]=LocalEpicStore.UndoObject(digest,sourceId,Encoding.UTF8.GetBytes(value.GetProperty("snapshotText").GetString()!));
            }
            _=Current(eventId,revision,sha);return objects.Values.ToArray();
        }finally{gate.Release();}
    }
    public async Task<EpicCommitReceipt> ImportAsync(string title,Guid requestId,byte[] eventBytes,IReadOnlyList<EpicUndoObject> objects)
    {
        await gate.WaitAsync().ConfigureAwait(false);try{
            Usable();var state=LocalEpicStore.Candidate(eventBytes,null);if(state.SourceId!=sourceId)throw new InvalidDataException("Imported Epic core mismatch");
            var snapshot=Parse(eventBytes);string hash=Hash("import",state.EventId,-1,new{title,state.Sha256,objects=objects.Select(o=>o.Sha256).Order(StringComparer.Ordinal).ToArray()});
            var replay=store.Replay(state.EventId,requestId,hash);if(replay is not null)return replay;
            _=await Ask(new{apiVersion=1,method="epicValidate",snapshot}).ConfigureAwait(false);
            var byHash=objects.ToDictionary(o=>o.Sha256,StringComparer.Ordinal);var used=new HashSet<string>(StringComparer.Ordinal);
            foreach(var group in snapshot.GetProperty("groups").EnumerateArray())foreach(var history in group.GetProperty("history").EnumerateArray()){
                string digest=history.GetProperty("beforeSnapshotSha256").GetString()!;
                var obj=byHash.TryGetValue(digest,out var found)?LocalEpicStore.UndoObject(found.Sha256,sourceId,found.Snapshot):throw new InvalidDataException("Imported event is missing a referenced undo object");used.Add(digest);
                _=await Ask(new{apiVersion=1,method="epicValidateUndoBase",snapshot,gameId=group.GetProperty("gameId").GetGuid(),actionId=history.GetProperty("actionId").GetGuid(),undoBase=new{snapshotText=Encoding.UTF8.GetString(obj.Snapshot),sha256=obj.Sha256}}).ConfigureAwait(false);
            }
            if(used.Count!=byHash.Count)throw new InvalidDataException("Unreferenced objects in event archive");
            try{return store.ImportRoot(title,requestId,hash,eventBytes,objects);}catch(Exception primary){
                try{var receipt=store.RecoverReceipt(state.EventId,requestId,hash);if(receipt is not null)return receipt;}
                catch(Exception recovery){throw new AggregateException("Epic import outcome needs recovery",primary,recovery);}throw;
            }
        }finally{gate.Release();}
    }
    public async ValueTask DisposeAsync(){await gate.WaitAsync().ConfigureAwait(false);try{disposed=true;}finally{gate.Release();}}
}
