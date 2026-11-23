using System.Text.Json;
using System.Text.Json.Nodes;
using System.Security.Cryptography;

namespace Arkham.NativeHost;

/// <summary>Publish a candidate only after durable snapshot+receipt commit.</summary>
public sealed partial class LocalGameController : IAsyncDisposable, ILanGameAuthority
{
    readonly NativeCoreWorker core;readonly LocalGameStore store;readonly string sourceId;
    readonly SemaphoreSlim gate=new(1,1);
    ulong live;StoredState? liveState;bool disposed;
    readonly record struct LocalViewKey(string Player,string Sections);
    ulong viewSession;readonly Dictionary<LocalViewKey,JsonElement> localViews=new();
    Exception? terminalFault;readonly HashSet<ulong> cleanupTokens=new();
    bool debugEnabled;
    public Action<string>? StateCommitted {get;set;}
#if NATIVE_PROBE
    internal Action<string>? FaultInjection {get;set;}
#endif
    public LocalGameController(NativeCoreWorker worker,LocalGameStore database,string expectedSource){core=worker;store=database;sourceId=expectedSource;}
    static string Key(Guid id){if(id==Guid.Empty)throw new ArgumentException("Nonempty ID required");return id.ToString("D");}
    static string Payload(string method,string game,long expected,object body)=>LocalGameStore.Hash(JsonSerializer.SerializeToUtf8Bytes(new{format=1,method,gameId=game,expectedSequence=expected,body}));
    void Usable(){ObjectDisposedException.ThrowIf(disposed,this);if(terminalFault is not null)throw new InvalidOperationException("Local session transport failed; restart and recover from committed database",terminalFault);}
    async Task<JsonElement> Ask(object request)
    {long at=HostTiming.Timestamp();JsonElement response;try{response=await core.RequestAsync(request).ConfigureAwait(false);}catch(Exception error){terminalFault??=error;throw;}finally{if(at!=0){var method=request.GetType().GetProperty("method")?.GetValue(request) as string??"unknown";HostTiming.Observe("host.request."+method,at);}}if(!response.GetProperty("ok").GetBoolean())throw new InvalidOperationException(response.GetProperty("error").GetRawText());return response;}
    async Task Release(ulong token){if(token==0)return;if(viewSession==token){viewSession=0;localViews.Clear();}cleanupTokens.Add(token);try{await Ask(new{apiVersion=1,method="release",session=token}).ConfigureAwait(false);cleanupTokens.Remove(token);}catch(Exception error){terminalFault??=error;throw;}}
    static readonly HashSet<string> LocalSectionNames=new(["game","events","triggerSchema","zoneSchema","deckSchema","interactionSchema","settingsSchema","settingsFormSchema","campaignManagementSchema","debugSchema","historicalSchema","authorCards"],StringComparer.Ordinal);
    static LocalViewKey ViewKey(string player,string[]? sections)
    {
        if(sections is not null&&sections.Any(name=>!LocalSectionNames.Contains(name)))throw new InvalidOperationException("Unknown local view section");
        return new(player,sections is null?"*":String.Join(",",sections.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)));
    }
    static JsonElement SelectViewSections(JsonElement full,string[] sections)
    {
        var names=sections.ToHashSet(StringComparer.Ordinal);
        using var bytes=new MemoryStream();
        using(var writer=new Utf8JsonWriter(bytes)){
            writer.WriteStartObject();
            foreach(var field in full.EnumerateObject())
                if(names.Contains(field.Name)||field.Name is "ok" or "viewVersion" or "revision" or "recipient" or "scope"){
                    writer.WritePropertyName(field.Name);writer.WriteRawValue(field.Value.GetRawText());
                }
            writer.WriteEndObject();
        }
        using var parsed=JsonDocument.Parse(bytes.GetBuffer().AsMemory(0,checked((int)bytes.Length)));
        return parsed.RootElement.Clone();
    }
    async Task<JsonElement> LocalView(ulong session,string player,string[]? sections=null)
    {
        // Native sessions are immutable: an answer/import creates another token.
        // Reuse only this exact session and recipient, under the controller gate.
        // Never share a trusted hotseat view with network-seat projections.
        if(viewSession!=session){viewSession=session;localViews.Clear();}
        var key=ViewKey(player,sections);
        if(localViews.TryGetValue(key,out var cached))return cached;
        // A prepared full view may already exist. Select only requested fields
        // without a second native call or changing their raw JSON spelling.
        if(sections is not null&&localViews.TryGetValue(ViewKey(player,null),out var full)){
            var selected=SelectViewSections(full,sections);localViews.Add(key,selected);return selected;
        }
        var response=sections is null
            ?await Ask(new{apiVersion=1,method="view",session,player}).ConfigureAwait(false)
            :await Ask(new{apiVersion=1,method="view",session,player,sections}).ConfigureAwait(false);
        localViews.Add(key,response);return response;
    }
    async Task CleanupCandidate(ulong candidate,Exception? primary)
    {try{if(candidate!=0&&candidate!=live)await Release(candidate).ConfigureAwait(false);}catch(Exception cleanup){if(primary is not null)throw new AggregateException("Operation and candidate cleanup failed; process restart required",primary,cleanup);throw;}}
    static UndoStepMetadata ReadUndoMetadata(JsonElement response)
    {
        if(response.ValueKind!=JsonValueKind.Object||!response.TryGetProperty("undoMetadata",out var raw)||raw.ValueKind!=JsonValueKind.Object)
            return UndoStepMetadata.Incomplete;
        if(!raw.TryGetProperty("schemaVersion",out var version)||version.ValueKind!=JsonValueKind.Number||!version.TryGetInt32(out int schemaVersion)||schemaVersion!=1
            ||!raw.TryGetProperty("metadataComplete",out var complete)||complete.ValueKind is not (JsonValueKind.True or JsonValueKind.False)
            ||!raw.TryGetProperty("hasRandomResult",out var random)||random.ValueKind is not (JsonValueKind.True or JsonValueKind.False)
            ||!raw.TryGetProperty("intermissionBoundary",out var boundary)||boundary.ValueKind is not (JsonValueKind.True or JsonValueKind.False)
            ||!raw.TryGetProperty("phaseEdges",out var edges)||edges.ValueKind!=JsonValueKind.Array||edges.GetArrayLength()>256)
            return UndoStepMetadata.Incomplete;
        var parsed=new List<UndoPhaseEdge>(edges.GetArrayLength());
        foreach(var edge in edges.EnumerateArray()){
            if(edge.ValueKind!=JsonValueKind.Object||!edge.TryGetProperty("from",out var from)||from.ValueKind!=JsonValueKind.String
                ||!edge.TryGetProperty("to",out var to)||to.ValueKind!=JsonValueKind.String)
                return UndoStepMetadata.Incomplete;
            var fromText=from.GetString();var toText=to.GetString();
            if(string.IsNullOrWhiteSpace(fromText)||fromText.Length>96||string.IsNullOrWhiteSpace(toText)||toText.Length>96)
                return UndoStepMetadata.Incomplete;
            parsed.Add(new(fromText,toText));
        }
        return new(schemaVersion,complete.GetBoolean(),random.GetBoolean(),boundary.GetBoolean(),parsed.ToArray());
    }
    async Task EnsureActive(StoredState state)
    {
        // A state saved by another core is no longer refused here: the core's own import decides (its
        // compatibleImportSourceIds lists the earlier cores whose saves it reads; any other is rejected there).
        // QQ fixes 2026-11-23: saves of 5bb054d7 continue on the next core without a conversion step.
        if(live!=0&&liveState?.GameId==state.GameId&&liveState.Sequence==state.Sequence&&liveState.Sha256==state.Sha256)return;
        using var document=JsonDocument.Parse(state.Save);
        ulong candidate=(await Ask(new{apiVersion=1,method="import",save=document.RootElement}).ConfigureAwait(false)).GetProperty("session").GetUInt64();
        ulong old=live;live=candidate;liveState=state;
        await Release(old).ConfigureAwait(false);
    }
    async Task<StoredState> Stage(string game,ulong candidate,long sequence,StoredState? basis,string kind,long? restoredFrom,JsonElement? debugCommand=null,UndoStepMetadata? undoMetadata=null,string? onlyRecipient=null)
    {
        // RequestAsync returns an owning cloned root. A property keeps that
        // document alive; another full Save clone here served no consumer.
        var save=(await Ask(new{apiVersion=1,method="export",session=candidate}).ConfigureAwait(false)).GetProperty("save");
        var snapshot=save.GetProperty("snapshot");
        if(save.GetProperty("gameId").GetString()!=game||snapshot.GetProperty("coreSourceId").GetString()!=sourceId)throw new InvalidDataException("Candidate identity differs from local game");
        long at=HostTiming.Timestamp();byte[] saveBytes=JsonSerializer.SerializeToUtf8Bytes(save);HostTiming.Observe("host.save.serialize",at,1,saveBytes.Length);
        if(kind=="preference"&&(basis is null||!PreferenceSaveMatchesParent(saveBytes,basis.Save)))
            throw new InvalidDataException("Trigger preference changed gameplay outside its allowed fields");
        at=HostTiming.Timestamp();var events=await StageEvents(candidate,snapshot,kind,restoredFrom,debugCommand,onlyRecipient).ConfigureAwait(false);HostTiming.Observe("host.events.stage",at,1,events.Length);
        at=HostTiming.Timestamp();string saveHash=LocalGameStore.Hash(saveBytes);HostTiming.Observe("host.save.stageHash",at,1,saveBytes.Length);
        return new(game,sequence,snapshot.GetProperty("revision").GetInt64(),sourceId,saveBytes,saveHash,events,basis?.Sequence,restoredFrom,kind,kind=="preference"?null:undoMetadata??UndoStepMetadata.Incomplete);
    }
    async Task<byte[]> StageEvents(ulong candidate,JsonElement snapshot,string kind,long? restoredFrom,JsonElement? debugCommand=null,string? onlyRecipient=null)
    {
        // Preserve per-recipient delivery exactly; public events can appear in
        // multiple streams. The UI must consume a sequence/recipient only once.
        var streams=new List<object>();
        if(kind is "create" or "answer" or "debug" or "author-answer" or "author-install")foreach(var player in snapshot.GetProperty("game").GetProperty("gamePlayers").EnumerateArray()){
            if(onlyRecipient is not null&&player.GetString()!=onlyRecipient)continue;
            // The durable stream stores recipient events, never UI schemas.
            var view=await LocalView(candidate,player.GetString()!,["events"]).ConfigureAwait(false);
            var events=view.GetProperty("events").EnumerateArray().Select(e=>(object)e.Clone()).ToList();
            if(kind.StartsWith("author-",StringComparison.Ordinal))events.Insert(0,new{type="text",text="【本地作者内容】显式安装了用户信任的自制卡定义；旧版本不会自动更新"});
            if(debugCommand is JsonElement command)events.Insert(0,new{type="text",text="【调试操作】已直接修改状态或调试推进；不是自然游玩结果",debugCommand=command.Clone()});
            streams.Add(new{recipient=player.GetString(),events});
        }
        else streams.Add(new{kind,restoredFrom}); // Restore does not replay historical game events.
        return JsonSerializer.SerializeToUtf8Bytes(streams);
    }
    async Task<CommitReceipt> Publish(string title,string request,string hash,long expected,StoredState candidate,StoredState? basis,ulong token)
    {
#if NATIVE_PROBE
        FaultInjection?.Invoke("before-durable-commit");
#endif
        CommitReceipt receipt;
        try{receipt=store.Commit(title,request,hash,expected,candidate,basis);}
        catch(Exception commitError){
            // A failed/uncertain COMMIT is resolved by its durable request and
            // state SHA, never by blindly evaluating the rule a second time.
            liveState=null;
            try{var recovered=store.RecoverReceipt(candidate.GameId,request,hash);if(recovered is null)throw;await EnsureActive(store.ReadCurrent(candidate.GameId)).ConfigureAwait(false);return recovered;}
            catch(Exception recoveryError)when(!ReferenceEquals(commitError,recoveryError)){throw new AggregateException("Commit outcome requires recovery; no rule retry was performed",commitError,recoveryError);}
        }
        StateCommitted?.Invoke(candidate.GameId);
#if NATIVE_PROBE
        FaultInjection?.Invoke("after-commit");
#endif
        if(receipt.Replayed){liveState=null;await EnsureActive(store.ReadCurrent(candidate.GameId)).ConfigureAwait(false);return receipt;}
        ulong old=live;live=token;liveState=candidate;
        await Release(old).ConfigureAwait(false);
        return receipt;
    }
    public async Task<CommitReceipt> CreateAsync(Guid gameId,string title,Guid requestId,JsonElement createRequest,bool historical=false)
    {
        await gate.WaitAsync().ConfigureAwait(false);ulong candidate=0;Exception? primary=null;
        try{
            Usable();
            string method=historical?"createHistory":"create";
            string game=Key(gameId),request=Key(requestId),hash=Payload(method,game,-1,new{title,request=createRequest});
            var replay=store.Replay(game,request,hash);if(replay is not null)return replay;
            candidate=(await Ask(new{apiVersion=1,method,gameId=game,request=createRequest}).ConfigureAwait(false)).GetProperty("session").GetUInt64();
            var state=await Stage(game,candidate,0,null,"create",null).ConfigureAwait(false);
            return await Publish(title,request,hash,-1,state,null,candidate).ConfigureAwait(false);
        }catch(Exception error){primary=error;throw;}finally{try{await CleanupCandidate(candidate,primary).ConfigureAwait(false);}finally{gate.Release();}}
    }
    public Task<CommitReceipt> AnswerAsync(Guid gameId,Guid requestId,long expectedSequence,Guid playerId,JsonElement answer)=>AnswerUsingAsync(gameId,requestId,expectedSequence,playerId,answer,null,null);
    public async Task<JsonElement?> CachedViewAsync(Guid gameId,Guid playerId,long sequence,string saveHash,string[]? sections=null)
    {
        await gate.WaitAsync().ConfigureAwait(false);
        try{
            Usable();
            if(liveState?.GameId!=Key(gameId)||liveState.Sequence!=sequence||liveState.Sha256!=saveHash||viewSession!=live)return null;
            if(localViews.TryGetValue(ViewKey(Key(playerId),sections),out var view))return view;
            return sections is not null&&localViews.TryGetValue(ViewKey(Key(playerId),null),out var full)?SelectViewSections(full,sections):null;
        }finally{gate.Release();}
    }
    // Explicitly negotiated local-only transport. The callback receives only
    // the actor's pending view/events. No incomplete event row reaches SQLite.
    // The gate remains held until all events and the normal atomic commit finish.
    public async Task<CommitReceipt> AnswerWithEarlyViewAsync(Guid gameId,Guid requestId,long expectedSequence,Guid playerId,JsonElement answer,Func<StoredState,JsonElement,Task> sendActor)
    {
        if(J.Tag(answer)!="Answer")throw new ArgumentException("Early local view only supports option answers");
        await gate.WaitAsync().ConfigureAwait(false);ulong candidate=0;Exception? primary=null;bool sent=false;
        try{
            Usable();string game=Key(gameId),request=Key(requestId),player=Key(playerId);
            string hash=Payload("answer",game,expectedSequence,new{player,answer});
            var replay=store.Replay(game,request,hash);if(replay is not null)return replay;
            var basis=store.ReadCurrent(game);if(basis.Sequence!=expectedSequence)throw new StaleLocalState();
            await EnsureActive(basis).ConfigureAwait(false);
            var result=await Ask(new{apiVersion=1,method="answer",session=live,player,revision=basis.CoreRevision,answer}).ConfigureAwait(false);
            candidate=result.GetProperty("session").GetUInt64();
            var state=await Stage(game,candidate,checked(expectedSequence+1),basis,"answer",null,undoMetadata:ReadUndoMetadata(result),onlyRecipient:player).ConfigureAwait(false);
            // First response consumes only PublicGame (including own question).
            var actor=await LocalView(candidate,player,["game"]).ConfigureAwait(false);
            await sendActor(state,actor).ConfigureAwait(false);sent=true;
            using(var save=JsonDocument.Parse(state.Save))
                state=state with{Events=await StageEvents(candidate,save.RootElement.GetProperty("snapshot"),"answer",null).ConfigureAwait(false)};
            return await Publish("",request,hash,expectedSequence,state,basis,candidate).ConfigureAwait(false);
        }catch(Exception error){primary=error;if(sent)terminalFault??=error;throw;}
        finally{try{await CleanupCandidate(candidate,primary).ConfigureAwait(false);}finally{gate.Release();}}
    }
    public Task<CommitReceipt> NetworkAnswerAsync(Guid bindingId,Guid gameId,Guid requestId,long expectedSequence,long expectedCoreRevision,Guid hostBoundPlayer,JsonElement answer)=>AnswerUsingAsync(gameId,requestId,expectedSequence,hostBoundPlayer,answer,bindingId,expectedCoreRevision);
    // Host-only adoption of bytes calculated by a separate unchanged core.
    // The caller already checked the exact option and connection ownership.
    // This is not an exported native interface and does not alter Save encoding.
    public async Task<CommitReceipt> AnswerPreparedAsync(Guid gameId,Guid requestId,long expectedSequence,Guid playerId,JsonElement answer,string basisHash,StoredState prepared,IReadOnlyDictionary<string,JsonElement> views,Func<Task> sendActor)
    {
        await gate.WaitAsync().ConfigureAwait(false);ulong candidate=0;Exception? primary=null;bool sent=false;
        try{
            Usable();string game=Key(gameId),request=Key(requestId),player=Key(playerId);
            if(J.Tag(answer)!="Answer")throw new ArgumentException("Prepared local answer must be an option");
            string hash=Payload("answer",game,expectedSequence,new{player,answer});
            var replay=store.Replay(game,request,hash);if(replay is not null)return replay;
            var basis=store.ReadCurrent(game);
            if(basis.Sequence!=expectedSequence||basis.Sha256!=basisHash||prepared.GameId!=game||prepared.SourceId!=sourceId||prepared.Parent!=expectedSequence||prepared.Sequence!=expectedSequence+1||prepared.Sha256!=LocalGameStore.Hash(prepared.Save))throw new StaleLocalState();
            using var document=JsonDocument.Parse(prepared.Save);var save=document.RootElement;var snapshot=save.GetProperty("snapshot");
            if(save.GetProperty("gameId").GetString()!=game||snapshot.GetProperty("coreSourceId").GetString()!=sourceId||snapshot.GetProperty("revision").GetInt64()!=prepared.CoreRevision)throw new InvalidDataException("Prepared candidate identity differs");
            var players=snapshot.GetProperty("game").GetProperty("gamePlayers").EnumerateArray().Select(p=>p.GetString()!).ToArray();
            if(!players.Contains(player)||views.Count!=players.Length||players.Any(p=>!views.ContainsKey(p)))throw new InvalidDataException("Prepared recipient set differs");
            // Send the already decoded actor before import/commit. On any later
            // failure freeze this controller and report GameError, never retry.
            await sendActor().ConfigureAwait(false);sent=true;
            candidate=(await Ask(new{apiVersion=1,method="import",save}).ConfigureAwait(false)).GetProperty("session").GetUInt64();
            viewSession=candidate;localViews.Clear();foreach(var pair in views)localViews.Add(ViewKey(pair.Key,null),pair.Value);
            return await Publish("",request,hash,expectedSequence,prepared,basis,candidate).ConfigureAwait(false);
        }catch(Exception error){primary=error;if(sent)terminalFault??=error;throw;}
        finally{try{await CleanupCandidate(candidate,primary).ConfigureAwait(false);}finally{gate.Release();}}
    }
    static string NetworkPayload(string game,string request,long expected,Guid player,Guid binding,long revision,JsonElement answer)
    {_=Key(binding);if(revision<0||expected<0)throw new ArgumentOutOfRangeException(nameof(revision));return Payload("networkAnswer",game,expected,new{player=Key(player),bindingId=Key(binding),expectedCoreRevision=revision,answer});}
    public async Task<CommitReceipt?> NetworkReceiptAsync(Guid bindingId,Guid gameId,Guid requestId,long expectedSequence,long expectedCoreRevision,Guid hostBoundPlayer,JsonElement originalAnswer)
    {
        await gate.WaitAsync().ConfigureAwait(false);try{Usable();string game=Key(gameId),request=Key(requestId);return store.Replay(game,request,NetworkPayload(game,request,expectedSequence,hostBoundPlayer,bindingId,expectedCoreRevision,originalAnswer));}finally{gate.Release();}
    }
    async Task<CommitReceipt> AnswerUsingAsync(Guid gameId,Guid requestId,long expectedSequence,Guid playerId,JsonElement answer,Guid? bindingId,long? expectedCoreRevision)
    {
        await gate.WaitAsync().ConfigureAwait(false);ulong candidate=0;Exception? primary=null;
        try{
            Usable();
            string game=Key(gameId),request=Key(requestId),player=Key(playerId),hash=bindingId is Guid binding?NetworkPayload(game,request,expectedSequence,playerId,binding,expectedCoreRevision!.Value,answer):Payload("answer",game,expectedSequence,new{player,answer});
            var replay=store.Replay(game,request,hash);if(replay is not null)return replay;
            var basis=store.ReadCurrent(game);if(basis.Sequence!=expectedSequence||expectedCoreRevision is long revision&&basis.CoreRevision!=revision)throw new StaleLocalState();
            await EnsureActive(basis).ConfigureAwait(false);
            if(bindingId is not null)await ValidateNetworkDeckAsync(playerId,answer).ConfigureAwait(false);
            var answerResult=await Ask(new{apiVersion=1,method="answer",session=live,player,revision=basis.CoreRevision,answer}).ConfigureAwait(false);
            candidate=answerResult.GetProperty("session").GetUInt64();
            var undoMetadata=ReadUndoMetadata(answerResult);
            bool preference=answer.ValueKind==JsonValueKind.Object&&answer.TryGetProperty("tag",out var answerTag)&&answerTag.GetString()=="SetTriggerModeAnswer";
            var state=await Stage(game,candidate,checked(expectedSequence+1),basis,preference?"preference":"answer",null,undoMetadata:preference?null:undoMetadata).ConfigureAwait(false);
            return await Publish("",request,hash,expectedSequence,state,basis,candidate).ConfigureAwait(false);
        }catch(Exception error){primary=error;throw;}finally{try{await CleanupCandidate(candidate,primary).ConfigureAwait(false);}finally{gate.Release();}}
    }
    // Ordinary player undo renews only the RNG seed, matching the original
    // server. Exact restores remain the default for recovery/replay callers.
    static JsonObject SeededGame(JsonNode root)
    {
        if(root is not JsonObject save || save["snapshot"] is not JsonObject snapshot
            || snapshot["game"] is not JsonObject game || game["gameSeed"] is not JsonValue seed
            || !seed.TryGetValue<long>(out _))throw new InvalidDataException("Restore Save lacks an integer game seed");
        return game;
    }
    static JsonObject? SavedInvestigators(JsonNode root)
    {
        if(root is not JsonObject save||save["snapshot"] is not JsonObject snapshot||snapshot["game"] is not JsonObject game)
            throw new InvalidDataException("Invalid preference Save envelope");
        if(game["gameEntities"] is null)return null;
        if(game["gameEntities"] is not JsonObject entities)throw new InvalidDataException("Invalid saved entities");
        if(entities["investigators"] is null)return null;
        return entities["investigators"] as JsonObject??throw new InvalidDataException("Invalid saved investigators");
    }
    static JsonObject PerCardSettings(JsonNode? investigator)
    {
        if(investigator is not JsonObject attrs||attrs["settings"] is not JsonObject settings||settings["perCardSettings"] is not JsonObject cards)
            throw new InvalidDataException("Invalid saved investigator card settings");
        return cards;
    }
    static JsonObject TriggerModes(JsonObject cards,string code)
    {
        if(cards[code] is null)return new JsonObject();
        if(cards[code] is not JsonObject card)throw new InvalidDataException("Invalid per-card settings");
        if(card["cardAbilityModes"] is null)return new JsonObject();
        if(card["cardAbilityModes"] is not JsonObject modes)throw new InvalidDataException("Invalid card ability modes");
        foreach(var pair in modes)
            if(!int.TryParse(pair.Key,out var index)||index < -1||pair.Value is not JsonValue value
                ||!value.TryGetValue<string>(out var mode)||mode is not ("AbilityOwnerOnly" or "AbilityAlwaysAsk" or "AbilityAutoSkip"))
                throw new InvalidDataException("Invalid trigger preference value");
        return modes;
    }
    static JsonObject DefaultPerCardSettings()=>new()
    {
        ["cardIgnoreUnrelatedSkillTestTriggers"]=false,["cardIgnoreDuringSkillTests"]=false,
        ["cardAttachments"]=new JsonArray(),["cardAbilityModes"]=new JsonObject(),
        ["cardOptions"]=new JsonObject(),["cardSilenced"]=false
    };
    // Copy only mode maps for the same investigator/card-code. A newly needed
    // card entry uses the exact native defaults, never the source's other settings.
    static void ApplyCurrentTriggerModes(JsonNode target,JsonNode preferenceBasis)
    {
        var destinations=SavedInvestigators(target);var sources=SavedInvestigators(preferenceBasis);
        if(destinations is null||sources is null)return;
        foreach(var investigator in destinations)
        {
            if(!sources.ContainsKey(investigator.Key))continue;
            var to=PerCardSettings(investigator.Value);var from=PerCardSettings(sources[investigator.Key]);
            foreach(var code in to.Select(p=>p.Key).Concat(from.Select(p=>p.Key)).Distinct().ToArray())
            {
                var modes=TriggerModes(from,code);
                if(to[code] is null)
                {
                    if(modes.Count==0)continue;
                    to[code]=DefaultPerCardSettings();
                }
                if(to[code] is not JsonObject card)throw new InvalidDataException("Invalid target per-card settings");
                card["cardAbilityModes"]=modes.DeepClone();
            }
        }
    }
    // A separate non-gameplay journal row is admitted only when the complete
    // state differs by revision +1 and precisely the native trigger mode maps.
    public static bool PreferenceSaveMatchesParent(byte[] preference,byte[] parent)
    {
        try
        {
            var actual=JsonNode.Parse(preference);var expected=JsonNode.Parse(parent);
            if(actual is not JsonObject a||a["snapshot"] is not JsonObject actualSnapshot
                ||expected is not JsonObject p||p["snapshot"] is not JsonObject expectedSnapshot
                ||actualSnapshot["revision"] is not JsonValue actualRevision||expectedSnapshot["revision"] is not JsonValue expectedRevision
                ||!actualRevision.TryGetValue<long>(out long next)||!expectedRevision.TryGetValue<long>(out long previous)
                ||previous==long.MaxValue||next!=previous+1)return false;
            expectedSnapshot["revision"]=next;
            ApplyCurrentTriggerModes(expected,actual);
            return JsonNode.DeepEquals(actual,expected);
        }
        catch(Exception error)when(error is JsonException or InvalidDataException){return false;}
    }
    static JsonNode PlayerUndoSave(byte[] target,byte[] preferenceBasis)
    {
        var expected=JsonNode.Parse(target)??throw new InvalidDataException("Invalid restore Save");
        var current=JsonNode.Parse(preferenceBasis)??throw new InvalidDataException("Invalid current preference Save");
        ApplyCurrentTriggerModes(expected,current);
        // The same prepared tree goes directly into the import request. Do not
        // serialize, parse, reseed, serialize and parse the whole Save again.
        SeededGame(expected)["gameSeed"]=RandomNumberGenerator.GetInt32(int.MinValue,int.MaxValue);
        return expected;
    }
    // Do not ignore RNG-looking fields elsewhere: queue, action diffs, card
    // metadata and every other Save member must still match the selected target.
    public static bool RestoredSaveMatchesTarget(byte[] restored,byte[] target,byte[]? preferenceBasis=null)
    {
        if(preferenceBasis is null&&restored.AsSpan().SequenceEqual(target))return true;
        try
        {
            var actual=JsonNode.Parse(restored);var expected=JsonNode.Parse(target);
            if(actual is null || expected is null)return false;
            if(preferenceBasis is not null)ApplyCurrentTriggerModes(expected,JsonNode.Parse(preferenceBasis)??throw new InvalidDataException("Invalid current preferences"));
            SeededGame(actual)["gameSeed"]=SeededGame(expected)["gameSeed"]!.DeepClone();
            return JsonNode.DeepEquals(actual,expected);
        }
        catch(Exception error)when(error is JsonException or InvalidDataException){return false;}
    }
    public async Task<CommitReceipt> RestoreAsync(Guid gameId,Guid requestId,long expectedSequence,long targetSequence,bool refreshRandom=false)
    {
        await gate.WaitAsync().ConfigureAwait(false);ulong candidate=0;Exception? primary=null;
        try{
            Usable();
            string game=Key(gameId),request=Key(requestId),hash=Payload(refreshRandom?"restore-random":"restore",game,expectedSequence,new{targetSequence});
            var replay=store.Replay(game,request,hash);if(replay is not null)return replay;
            var basis=store.ReadCurrent(game);if(basis.Sequence!=expectedSequence)throw new StaleLocalState();
            var target=store.ReadState(game,targetSequence);
            // Replay is resolved before drawing a new seed. A retry of the same
            // durable request must not produce a second random candidate.
            long at=HostTiming.Timestamp();using var doc=refreshRandom?null:JsonDocument.Parse(target.Save);
            object prepared=refreshRandom?PlayerUndoSave(target.Save,basis.Save):doc!.RootElement;
            HostTiming.Observe("host.undo.prepareSave",at,1,target.Save.Length);
            candidate=(await Ask(new{apiVersion=1,method="import",save=prepared}).ConfigureAwait(false)).GetProperty("session").GetUInt64();
            var state=await Stage(game,candidate,checked(expectedSequence+1),basis,"restore",targetSequence).ConfigureAwait(false);
            return await Publish("",request,hash,expectedSequence,state,basis,candidate).ConfigureAwait(false);
        }catch(Exception error){primary=error;throw;}finally{try{await CleanupCandidate(candidate,primary).ConfigureAwait(false);}finally{gate.Release();}}
    }
    public async Task SetDebugEnabledAsync(bool enabled)
    {
        await gate.WaitAsync().ConfigureAwait(false);
        try{Usable();debugEnabled=enabled;}finally{gate.Release();}
    }
    public async Task<JsonElement> AuthorPreviewAsync(byte[] package)
    {
        byte[] checkedBytes=LocalAuthorLibrary.CheckEnvelope(package);string text=new System.Text.UTF8Encoding(false,true).GetString(checkedBytes),digest=LocalAuthorLibrary.Hash(checkedBytes);
        await gate.WaitAsync().ConfigureAwait(false);try{Usable();var response=await Ask(new{apiVersion=1,method="authorPreview",packageText=text,packageSha256=digest}).ConfigureAwait(false);if(response.GetProperty("coreSourceId").GetString()!=sourceId||response.GetProperty("preview").GetProperty("packageSha256").GetString()!=digest)throw new InvalidDataException("Author preview content/source differs");return response.GetProperty("preview").Clone();}finally{gate.Release();}
    }
    public async Task<JsonElement> AuthorSchemaAsync()
    {
        await gate.WaitAsync().ConfigureAwait(false);try{Usable();var response=await Ask(new{apiVersion=1,method="authorSchema"}).ConfigureAwait(false);if(response.GetProperty("coreSourceId").GetString()!=sourceId)throw new InvalidDataException("Author schema source differs");return response.GetProperty("schema").Clone();}finally{gate.Release();}
    }
    public async Task<JsonElement> DeckBuildPreviewAsync(string operation,JsonElement request)
    {
        if(operation is not("deckBuildSchema" or "deckBuildValidate" or "deckMetadataValidate" or "deckUpgradeQuote")||System.Text.Encoding.UTF8.GetByteCount(request.GetRawText())>1048576)throw new InvalidDataException("无效或过大的构筑检查请求。");
        await gate.WaitAsync().ConfigureAwait(false);try{Usable();var response=await Ask(new{apiVersion=1,method=operation,request}).ConfigureAwait(false);if(response.GetProperty("coreSourceId").GetString()!=sourceId)throw new InvalidDataException("构筑规则来源与当前核心不一致。");return response.GetProperty("result").Clone();}finally{gate.Release();}
    }
    public async Task<CommitReceipt> AuthorAsync(Guid gameId,Guid requestId,long expectedSequence,string expectedSha,long expectedCoreRevision,Guid playerId,byte[] package,bool explicitlyTrusted,bool installOnly,JsonElement? answer=null)
    {
        if(!explicitlyTrusted)throw new InvalidOperationException("必须显式确认信任此版本的作者内容");byte[] checkedBytes=LocalAuthorLibrary.CheckEnvelope(package);string text=new System.Text.UTF8Encoding(false,true).GetString(checkedBytes),digest=LocalAuthorLibrary.Hash(checkedBytes);
        await gate.WaitAsync().ConfigureAwait(false);ulong candidate=0;Exception? primary=null;
        try{
            Usable();if(installOnly&&!debugEnabled)throw new InvalidOperationException("游戏中安装作者内容需要先启用调试模式");if(!installOnly&&answer is null)throw new InvalidOperationException("作者牌组提交需要原牌组操作");
            string game=Key(gameId),request=Key(requestId),player=Key(playerId),kind=installOnly?"author-install":"author-answer",hash=Payload(kind,game,expectedSequence,new{player,expectedSha,expectedCoreRevision,packageSha256=digest,answer});
            var replay=store.Replay(game,request,hash);if(replay is not null)return replay;var basis=store.ReadCurrent(game);if(basis.Sequence!=expectedSequence||basis.Sha256!=expectedSha||basis.CoreRevision!=expectedCoreRevision)throw new StaleLocalState();await EnsureActive(basis).ConfigureAwait(false);
            candidate=(await Ask(new{apiVersion=1,method=installOnly?"authorInstall":"authorAnswer",session=live,player,revision=expectedCoreRevision,authorEnabled=true,packageText=text,packageSha256=digest,answer}).ConfigureAwait(false)).GetProperty("session").GetUInt64();
            var state=await Stage(game,candidate,checked(expectedSequence+1),basis,kind,null).ConfigureAwait(false);return await Publish("",request,hash,expectedSequence,state,basis,candidate).ConfigureAwait(false);
        }catch(Exception error){primary=error;throw;}finally{try{await CleanupCandidate(candidate,primary).ConfigureAwait(false);}finally{gate.Release();}}
    }
    public async Task<CommitReceipt> DebugAsync(Guid gameId,Guid requestId,long expectedSequence,string expectedSha,long expectedCoreRevision,Guid playerId,JsonElement command)
    {
        await gate.WaitAsync().ConfigureAwait(false);ulong candidate=0;Exception? primary=null;
        try{
            Usable();if(!debugEnabled)throw new InvalidOperationException("本次进程未启用调试模式");
            string game=Key(gameId),request=Key(requestId),player=Key(playerId),hash=Payload("debug",game,expectedSequence,new{player,expectedSha,expectedCoreRevision,command});
            var replay=store.Replay(game,request,hash);if(replay is not null)return replay;
            var basis=store.ReadCurrent(game);
            if(basis.Sequence!=expectedSequence||basis.Sha256!=expectedSha||basis.CoreRevision!=expectedCoreRevision)throw new StaleLocalState();
            await EnsureActive(basis).ConfigureAwait(false);
            candidate=(await Ask(new{apiVersion=1,method="debug",session=live,player,revision=expectedCoreRevision,debugEnabled=true,command}).ConfigureAwait(false)).GetProperty("session").GetUInt64();
            var state=await Stage(game,candidate,checked(expectedSequence+1),basis,"debug",null,command).ConfigureAwait(false);
            return await Publish("",request,hash,expectedSequence,state,basis,candidate).ConfigureAwait(false);
        }catch(Exception error){primary=error;throw;}finally{try{await CleanupCandidate(candidate,primary).ConfigureAwait(false);}finally{gate.Release();}}
    }
    public async Task<CommitReceipt> ImportAsync(Guid newGameId,string title,Guid requestId,JsonElement importedSave)
    {
        await gate.WaitAsync().ConfigureAwait(false);ulong candidate=0;Exception? primary=null;
        try{
            Usable();
            string game=Key(newGameId),request=Key(requestId),hash=Payload("import",game,-1,new{title,save=importedSave});
            var replay=store.Replay(game,request,hash);if(replay is not null)return replay;
            if(importedSave.GetProperty("saveFormat").GetInt32()!=1||!Guid.TryParse(importedSave.GetProperty("gameId").GetString(),out var original)||original==Guid.Empty)throw new InvalidDataException("Invalid import envelope");
            // The gameId is local envelope identity, not part of the rules Game.
            var rewritten=JsonNode.Parse(importedSave.GetRawText())!;rewritten["gameId"]=game;
            candidate=(await Ask(new{apiVersion=1,method="import",save=rewritten}).ConfigureAwait(false)).GetProperty("session").GetUInt64();
            var state=await Stage(game,candidate,0,null,"import",null).ConfigureAwait(false);
            return await Publish(title,request,hash,-1,state,null,candidate).ConfigureAwait(false);
        }catch(Exception error){primary=error;throw;}finally{try{await CleanupCandidate(candidate,primary).ConfigureAwait(false);}finally{gate.Release();}}
    }
    public async Task ExportFileAsync(Guid gameId,string newAbsoluteFile)
    {
        await gate.WaitAsync().ConfigureAwait(false);string? temporary=null;bool owned=false;
        try{
            Usable();
            if(!Path.IsPathFullyQualified(newAbsoluteFile)||File.Exists(newAbsoluteFile))throw new IOException("A new absolute export file is required");
            var state=store.ReadCurrent(Key(gameId));temporary=newAbsoluteFile+".partial-"+Guid.NewGuid().ToString("N");
            using(var stream=new FileStream(temporary,FileMode.CreateNew,FileAccess.Write,FileShare.None)){owned=true;stream.Write(state.Save);stream.Flush(true);}
            if(LocalGameStore.Hash(File.ReadAllBytes(temporary))!=state.Sha256)throw new IOException("Export verification failed");
            File.Move(temporary,newAbsoluteFile,false);owned=false;temporary=null;
        }finally{try{if(owned&&temporary is not null)File.Delete(temporary);}finally{gate.Release();}}
    }
    public async Task<JsonElement> ViewAsync(Guid gameId,Guid playerId,string[]? sections=null)
    {
        await gate.WaitAsync().ConfigureAwait(false);
        try{Usable();var state=store.ReadCurrent(Key(gameId));await EnsureActive(state).ConfigureAwait(false);return await LocalView(live,Key(playerId),sections).ConfigureAwait(false);}
        finally{gate.Release();}
    }
    public async Task<LanSeatView> NetworkViewAsync(Guid gameId,Guid hostBoundPlayer)
    {
        await gate.WaitAsync().ConfigureAwait(false);try{
            Usable();string game=Key(gameId),player=Key(hostBoundPlayer);var state=store.ReadCurrent(game);await EnsureActive(state).ConfigureAwait(false);
            var result=await Ask(new{apiVersion=1,method="networkView",session=live,player}).ConfigureAwait(false);var view=result.GetProperty("networkView");
            if(view.GetProperty("scope").GetString()!="network-seat"||view.GetProperty("schemaVersion").GetInt32()!=1||view.GetProperty("recipient").GetGuid()!=hostBoundPlayer||view.GetProperty("revision").GetInt64()!=state.CoreRevision||view.GetProperty("coreSourceId").GetString()!=sourceId)throw new InvalidDataException("Native network projection identity mismatch");
            return new(gameId,state.Sequence,state.CoreRevision,sourceId,view.Clone());
        }finally{gate.Release();}
    }
    public async Task<JsonElement> NetworkLogsAsync(Guid gameId,long expectedSequence,long expectedCoreRevision,Guid hostBoundPlayer,int page)
    {
        await gate.WaitAsync().ConfigureAwait(false);try{
            Usable();string game=Key(gameId);var basis=store.ReadCurrent(game);
            if(basis.Sequence!=expectedSequence||basis.CoreRevision!=expectedCoreRevision)throw new StaleLocalState();
            await EnsureActive(basis).ConfigureAwait(false);var native=await Ask(new{apiVersion=1,method="networkView",session=live,player=Key(hostBoundPlayer)}).ConfigureAwait(false);var view=native.GetProperty("networkView");
            if(view.GetProperty("recipient").GetGuid()!=hostBoundPlayer||view.GetProperty("scope").GetString()!="network-seat"||view.GetProperty("revision").GetInt64()!=basis.CoreRevision||view.GetProperty("coreSourceId").GetString()!=sourceId)throw new UnauthorizedAccessException("Log seat is not current");
            var history=ClientLogs.Read(game,sourceId,basis.Sequence,hostBoundPlayer,sequence=>{
                var state=store.ReadLogState(game,sequence);
                if(state.Kind=="preference"){
                    var full=store.ReadState(game,sequence);
                    if(full.Parent is not long parent||!PreferenceSaveMatchesParent(full.Save,store.ReadState(game,parent).Save))throw new InvalidDataException("Trigger preference log changed gameplay");
                }
                using var streams=JsonDocument.Parse(state.Events);return new ClientLogState(state.GameId,state.SourceId,state.Sequence,state.Parent,state.RestoredFrom,state.Kind,streams.RootElement.Clone());
            });
            var data=LanLogProjection.Page(history,page);var after=store.ReadCurrent(game);if(after.Sequence!=basis.Sequence||after.CoreRevision!=basis.CoreRevision||after.Sha256!=basis.Sha256)throw new StaleLocalState();
            return JsonSerializer.SerializeToElement(new{gameId,sequence=basis.Sequence,coreRevision=basis.CoreRevision,sourceId,playerId=hostBoundPlayer,pageData=data});
        }finally{gate.Release();}
    }
    public async Task<JsonElement> NetworkRegionsAsync(Guid gameId,long expectedSequence,long expectedCoreRevision,Guid hostBoundPlayer)
    {
        await gate.WaitAsync().ConfigureAwait(false);try{
            Usable();string game=Key(gameId);var basis=store.ReadCurrent(game);if(basis.Sequence!=expectedSequence||basis.CoreRevision!=expectedCoreRevision)throw new StaleLocalState();await EnsureActive(basis).ConfigureAwait(false);
            var response=await Ask(new{apiVersion=1,method="networkView",session=live,player=Key(hostBoundPlayer)}).ConfigureAwait(false);var seat=new LanSeatView(gameId,basis.Sequence,basis.CoreRevision,sourceId,response.GetProperty("networkView").Clone());
            if(seat.View.GetProperty("recipient").GetGuid()!=hostBoundPlayer||seat.View.GetProperty("scope").GetString()!="network-seat"||seat.View.GetProperty("revision").GetInt64()!=basis.CoreRevision||seat.View.GetProperty("coreSourceId").GetString()!=sourceId)throw new UnauthorizedAccessException("Region seat differs");
            var trusted=await Ask(new{apiVersion=1,method="regionView",session=live,player=Key(hostBoundPlayer)}).ConfigureAwait(false);var regions=LanRegionProjection.Project(seat,trusted);var after=store.ReadCurrent(game);if(after.Sequence!=basis.Sequence||after.CoreRevision!=basis.CoreRevision||after.Sha256!=basis.Sha256)throw new StaleLocalState();
            return JsonSerializer.SerializeToElement(new{gameId,sequence=basis.Sequence,coreRevision=basis.CoreRevision,sourceId,playerId=hostBoundPlayer,regions});
        }finally{gate.Release();}
    }
    // Read-only form evaluation is bound to the complete durable basis. It
    // cannot publish a candidate, append a receipt, or hold a DB transaction
    // across the native await. Final answers still use the atomic commit guard.
    public Task<JsonElement> SettingsPreviewAsync(Guid gameId,long expectedSequence,string expectedSaveSha,long expectedCoreRevision,Guid playerId,JsonElement draft,string method="settingsPreview")=>PreviewSettingsUsingAsync(gameId,expectedSequence,expectedSaveSha,expectedCoreRevision,playerId,draft,method);
    public async Task<JsonElement> NetworkSettingsPreviewAsync(Guid gameId,long expectedSequence,long expectedCoreRevision,Guid hostBoundPlayer,JsonElement draft)
    {
        var response=await PreviewSettingsUsingAsync(gameId,expectedSequence,null,expectedCoreRevision,hostBoundPlayer,draft,"settingsPreview").ConfigureAwait(false);
        // The network never supplies or receives the host's whole-save hash,
        // an arbitrary method name, historical bootstrap data or generic FFI.
        return JsonSerializer.SerializeToElement(new{gameId,sequence=expectedSequence,coreRevision=expectedCoreRevision,playerId=hostBoundPlayer,sourceId,preview=response.GetProperty("preview").Clone()});
    }
    async Task<JsonElement> PreviewSettingsUsingAsync(Guid gameId,long expectedSequence,string? expectedSaveSha,long expectedCoreRevision,Guid playerId,JsonElement draft,string method)
    {
        if(method is not ("settingsPreview" or "historyChapterPreview" or "historyPrepare" or "historyPreview"))throw new InvalidOperationException("Unknown local preview operation");
        await gate.WaitAsync().ConfigureAwait(false);
        try{
            Usable();string game=Key(gameId),player=Key(playerId);
            void CheckBasis(StoredState state,string sha){
                if(state.GameId!=game||state.Sequence!=expectedSequence||state.CoreRevision!=expectedCoreRevision||state.Sha256!=sha)throw new StaleLocalState();
                using var saved=JsonDocument.Parse(state.Save);
                if(!saved.RootElement.GetProperty("snapshot").GetProperty("game").GetProperty("gamePlayers").EnumerateArray().Any(p=>p.GetString()==player))throw new InvalidOperationException("Settings player is not a current local seat");
            }
            var basis=store.ReadCurrent(game);string capturedSha=expectedSaveSha??basis.Sha256;CheckBasis(basis,capturedSha);await EnsureActive(basis).ConfigureAwait(false);
            var response=await Ask(new{apiVersion=1,method,session=live,player,revision=expectedCoreRevision,draft}).ConfigureAwait(false);
            if(response.GetProperty("recipient").GetString()!=player||response.GetProperty("revision").GetInt64()!=expectedCoreRevision)throw new InvalidDataException("Preview response basis differs");
            if(method is "settingsPreview" or "historyChapterPreview" && response.GetProperty("preview").GetProperty("schemaId").GetString()!=draft.GetProperty("schemaId").GetString())throw new InvalidDataException("Settings preview schema differs");
#if NATIVE_PROBE
            FaultInjection?.Invoke("after-settings-preview");
#endif
            CheckBasis(store.ReadCurrent(game),capturedSha);return response;
        }finally{gate.Release();}
    }
    public async ValueTask DisposeAsync()
    {
        await gate.WaitAsync().ConfigureAwait(false);try{if(disposed)return;foreach(ulong token in cleanupTokens.Append(live).Where(t=>t!=0).Distinct().ToArray())await Release(token).ConfigureAwait(false);live=0;liveState=null;disposed=true;}finally{gate.Release();}
    }
}
