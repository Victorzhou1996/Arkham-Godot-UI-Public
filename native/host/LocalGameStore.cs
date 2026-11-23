using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Arkham.NativeHost;

public sealed record UndoPhaseEdge([property:JsonPropertyName("from")]string From,[property:JsonPropertyName("to")]string To);
public sealed record UndoStepMetadata(
    [property:JsonPropertyName("schemaVersion")]int SchemaVersion,
    [property:JsonPropertyName("metadataComplete")]bool MetadataComplete,
    [property:JsonPropertyName("hasRandomResult")]bool HasRandomResult,
    [property:JsonPropertyName("intermissionBoundary")]bool IntermissionBoundary,
    [property:JsonPropertyName("phaseEdges")]UndoPhaseEdge[] PhaseEdges)
{
    public static UndoStepMetadata Incomplete {get;}=new(1,false,false,false,Array.Empty<UndoPhaseEdge>());
}
public sealed record StoredState(string GameId,long Sequence,long CoreRevision,string SourceId,byte[] Save,string Sha256,byte[] Events,long? Parent,long? RestoredFrom,string Kind,UndoStepMetadata? UndoMetadata=null);
public sealed record StoredLogState(string GameId,long Sequence,string SourceId,byte[] Events,long? Parent,long? RestoredFrom,string Kind);
public sealed record CommitReceipt(string GameId,string RequestId,long CommittedSequence,long CurrentSequence,string Sha256,bool Replayed);
public sealed class StaleLocalState : InvalidOperationException{public StaleLocalState():base("Local state changed; reload the latest committed game") {}}

/// <summary>Immutable snapshots and request receipts committed as one unit.</summary>
public sealed partial class LocalGameStore : IDisposable
{
    readonly object gate=new();NativeSqlite db;readonly string databasePath;bool recoveryRequired;
    // Rows are immutable under this store's schema. Cache owned, already
    // validated bytes; callers always receive copies, never mutable cache data.
    readonly Dictionary<(string Game,long Sequence),StoredState> stateCache=new();
    readonly Queue<(string Game,long Sequence)> stateOrder=new();
    readonly Dictionary<(string Game,long Sequence),StoredLogState> logCache=new();
    readonly Queue<(string Game,long Sequence)> logOrder=new();
    readonly Dictionary<string,StoredState> currentCache=new(StringComparer.Ordinal);
    long stateCacheBytes,logCacheBytes;
    const long StateCacheLimit=32L*1024*1024,LogCacheLimit=8L*1024*1024;
    static StoredState CopyState(StoredState state)=>state with{Save=(byte[])state.Save.Clone(),Events=(byte[])state.Events.Clone(),UndoMetadata=null};
    static StoredLogState CopyLog(StoredLogState state)=>state with{Events=(byte[])state.Events.Clone()};
    StoredState CacheState(StoredState state)
    {
        var key=(state.GameId,state.Sequence);long bytes=state.Save.LongLength+state.Events.LongLength;
        if(bytes<=StateCacheLimit&&!stateCache.ContainsKey(key)){
            while(stateOrder.Count!=0&&(stateCacheBytes+bytes>StateCacheLimit||stateCache.Count>=128)){
                var oldest=stateOrder.Dequeue();var removed=stateCache[oldest];stateCacheBytes-=removed.Save.LongLength+removed.Events.LongLength;stateCache.Remove(oldest);
                if(currentCache.TryGetValue(oldest.Game,out var current)&&current.Sequence==oldest.Sequence)currentCache.Remove(oldest.Game);
            }
            stateCache[key]=state;stateOrder.Enqueue(key);stateCacheBytes+=bytes;
        }
        return state;
    }
    void ClearCaches(){stateCache.Clear();stateOrder.Clear();logCache.Clear();logOrder.Clear();currentCache.Clear();historyCache.Clear();historyOrder.Clear();stateCacheBytes=0;logCacheBytes=0;}
    public string? MigrationBackupPath {get;private set;}
    public static string Hash(byte[] bytes)=>Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
#if NATIVE_PROBE
    internal Action<string>? FaultInjection {get;set;}
    internal Action? RollbackFault {set=>db.BeforeRollback=value;}
    internal Action? BeforeRecoveryOpen {get;set;}
#endif
    public LocalGameStore(string databaseFile) : this(databaseFile, fullIntegrityCheck:true) { }
    public LocalGameStore(string databaseFile,bool fullIntegrityCheck)
    {
        databasePath=Path.GetFullPath(databaseFile);db=new NativeSqlite(databasePath);
        try{
            long version=(long)db.Query("PRAGMA user_version")[0][0]!;
            if(version is not (0 or 1 or 2))throw new InvalidDataException("Unsupported database schema; do not replace this file");
            db.ExecuteScript("PRAGMA foreign_keys=ON; PRAGMA synchronous=FULL;");
            if((string)db.Query("PRAGMA journal_mode=DELETE")[0][0]! !="delete"||(long)db.Query("PRAGMA synchronous")[0][0]!!=2||(long)db.Query("PRAGMA foreign_keys")[0][0]!!=1)throw new IOException("Required SQLite durability mode unavailable");
            if(version==1){
                MigrationBackupPath=databasePath+".pre-undo-v2-"+Guid.NewGuid().ToString("N")+".sqlite";
                db.BackupTo(MigrationBackupPath);
            }
            db.Transaction(()=>{
                long current=(long)db.Query("PRAGMA user_version")[0][0]!;
                if(current==0){
                    if(db.Query("SELECT name FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%'").Count!=0)throw new InvalidDataException("Unrecognized unversioned database; refusing automatic replacement");
                    db.ExecuteScript("""
                    CREATE TABLE games(id TEXT PRIMARY KEY,title TEXT NOT NULL,current_seq INTEGER NOT NULL CHECK(current_seq>=0),
                      FOREIGN KEY(id,current_seq) REFERENCES states(game_id,seq) DEFERRABLE INITIALLY DEFERRED);
                    CREATE TABLE states(game_id TEXT NOT NULL,seq INTEGER NOT NULL CHECK(seq>=0),core_revision INTEGER NOT NULL CHECK(core_revision>=0),source_id TEXT NOT NULL,
                      save BLOB NOT NULL,sha256 TEXT NOT NULL CHECK(length(sha256)=64),events BLOB NOT NULL,parent_seq INTEGER,restored_from INTEGER,kind TEXT NOT NULL,
                      PRIMARY KEY(game_id,seq),FOREIGN KEY(game_id) REFERENCES games(id),
                      FOREIGN KEY(game_id,parent_seq) REFERENCES states(game_id,seq),FOREIGN KEY(game_id,restored_from) REFERENCES states(game_id,seq),
                      CHECK((seq=0 AND parent_seq IS NULL) OR (seq>0 AND parent_seq IS NOT NULL AND parent_seq<seq)));
                    CREATE TABLE requests(game_id TEXT NOT NULL,request_id TEXT NOT NULL,payload_hash TEXT NOT NULL CHECK(length(payload_hash)=64),
                      expected_seq INTEGER NOT NULL,committed_seq INTEGER NOT NULL,PRIMARY KEY(game_id,request_id),
                      FOREIGN KEY(game_id,committed_seq) REFERENCES states(game_id,seq));
                    CREATE TRIGGER states_no_update BEFORE UPDATE ON states BEGIN SELECT RAISE(ABORT,'immutable state'); END;
                    CREATE TRIGGER states_no_delete BEFORE DELETE ON states BEGIN SELECT RAISE(ABORT,'immutable state'); END;
                    CREATE TRIGGER requests_no_update BEFORE UPDATE ON requests BEGIN SELECT RAISE(ABORT,'immutable request'); END;
                    CREATE TRIGGER requests_no_delete BEFORE DELETE ON requests BEGIN SELECT RAISE(ABORT,'immutable request'); END;
                    PRAGMA user_version=1;
                    """);
                }else if(current is not (1 or 2))throw new InvalidDataException("Database schema changed while opening");
                if(current!=2){
                    db.ExecuteScript(UndoMetadataSchema);
                    db.ExecuteScript("PRAGMA user_version=2;");
                }
                return true;
            });
            // Ordinary startup must not walk every historical snapshot. New
            // databases, schema migrations and explicit import/repair checks
            // retain the full check. Each subsequently read state still goes
            // through Validate (SHA, format, identity and revision), and writes
            // retain SQLite foreign keys and the immutable-history triggers.
            if((fullIntegrityCheck||version!=2)
                &&((string)db.Query("PRAGMA quick_check")[0][0]! !="ok"||db.Query("PRAGMA foreign_key_check").Count!=0))
                throw new InvalidDataException("Database consistency check failed; preserve file for recovery");
            VerifyUndoMetadataSchema();
            // Additive derived index; Save bytes and the v2 history/receipt
            // contract remain readable by the preceding host. Never scan or
            // rewrite old snapshots to populate it during startup.
            db.ExecuteScript(HistoryIndexSchema);
            VerifyHistoryIndexSchema();
        }catch{db.Dispose();throw;}
    }
    const string UndoMetadataSchema="""
CREATE TABLE undo_metadata(
  game_id TEXT NOT NULL,
  state_seq INTEGER NOT NULL CHECK(state_seq>=0),
  schema_version INTEGER NOT NULL CHECK(schema_version=1),
  metadata_complete INTEGER NOT NULL CHECK(metadata_complete IN (0,1)),
  has_random_result INTEGER NOT NULL CHECK(has_random_result IN (0,1)),
  intermission_boundary INTEGER NOT NULL CHECK(intermission_boundary IN (0,1)),
  phase_edges_json BLOB NOT NULL CHECK(length(phase_edges_json)<=65536),
  PRIMARY KEY(game_id,state_seq),
  FOREIGN KEY(game_id,state_seq) REFERENCES states(game_id,seq)
);
CREATE TRIGGER undo_metadata_no_update BEFORE UPDATE ON undo_metadata BEGIN SELECT RAISE(ABORT,'immutable undo metadata'); END;
CREATE TRIGGER undo_metadata_no_delete BEFORE DELETE ON undo_metadata BEGIN SELECT RAISE(ABORT,'immutable undo metadata'); END;
""";
    void VerifyUndoMetadataSchema()
    {
        if(db.Query("SELECT name FROM sqlite_master WHERE type='table' AND name='undo_metadata'").Count!=1)throw new InvalidDataException("Undo metadata schema missing; preserve database");
        foreach(string action in new[]{"update","delete"}){
            string name="undo_metadata_no_"+action;
            var rows=db.Query("SELECT sql FROM sqlite_master WHERE type='trigger' AND name=? AND tbl_name='undo_metadata'",name);
            if(rows.Count!=1||rows[0][0] is not string sql||!string.Concat(sql.Where(c=>!char.IsWhiteSpace(c))).Contains("raise(abort,'immutableundometadata')",StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Undo metadata immutability trigger missing; preserve database");
        }
        db.Query("SELECT schema_version,metadata_complete,has_random_result,intermission_boundary,phase_edges_json FROM undo_metadata LIMIT 0");
    }
    static readonly string StateColumns="game_id,seq,core_revision,source_id,save,sha256,events,parent_seq,restored_from,kind";
    StoredState Decode(object?[] row)
    {
        var state=new StoredState((string)row[0]!, (long)row[1]!, (long)row[2]!, (string)row[3]!, (byte[])row[4]!, (string)row[5]!, (byte[])row[6]!, (long?)row[7], (long?)row[8], (string)row[9]!);
        CacheHistory(Validate(state));return state;
    }
    static StoredHistoryIndex Validate(StoredState state)
    {
        long at=HostTiming.Timestamp();
        if(Hash(state.Save)!=state.Sha256||!Guid.TryParse(state.GameId,out var id)||id==Guid.Empty||state.CoreRevision<0)throw new InvalidDataException("State SHA/identity invalid");
        HostTiming.Observe("host.save.validateHash",at,1,state.Save.Length);at=HostTiming.Timestamp();
        using var save=JsonDocument.Parse(state.Save);
        var root=save.RootElement;var snapshot=root.GetProperty("snapshot");
        if(root.GetProperty("saveFormat").GetInt32()!=1||snapshot.GetProperty("snapshotFormat").GetInt32()!=2||!snapshot.TryGetProperty("historicalBootstrap",out _)||root.GetProperty("gameId").GetString()!=state.GameId||snapshot.GetProperty("revision").GetInt64()!=state.CoreRevision||snapshot.GetProperty("coreSourceId").GetString()!=state.SourceId)throw new InvalidDataException("State identity/revision/format mismatch");
        using var events=JsonDocument.Parse(state.Events);if(events.RootElement.ValueKind!=JsonValueKind.Array)throw new InvalidDataException("State events must be a JSON stream array");
        if(state.UndoMetadata is not null)ValidateUndoMetadata(state.UndoMetadata);
        var result=HistoryFromSnapshot(state,snapshot);HostTiming.Observe("host.save.validateJson",at,1,state.Save.Length+state.Events.Length);return result;
    }
    static void ValidateUndoMetadata(UndoStepMetadata metadata)
    {
        if(metadata.SchemaVersion!=1||metadata.PhaseEdges is null||metadata.PhaseEdges.Length>256)throw new InvalidDataException("Undo metadata schema or edge count invalid");
        foreach(var edge in metadata.PhaseEdges)
            if(edge is null||string.IsNullOrWhiteSpace(edge.From)||edge.From.Length>96||string.IsNullOrWhiteSpace(edge.To)||edge.To.Length>96)
                throw new InvalidDataException("Undo phase edge invalid");
    }
    void Ready(){if(recoveryRequired)throw new InvalidOperationException("Database requires a fresh-connection recovery before reuse");}
    StoredState ReadStateUnsafe(string game,long seq)
    {
        if(stateCache.TryGetValue((game,seq),out var cached))return cached;
        var rows=db.Query("SELECT "+StateColumns+" FROM states WHERE game_id=? AND seq=?",game,seq);if(rows.Count!=1)throw new KeyNotFoundException("Local game state missing");return CacheState(Decode(rows[0]));
    }
    public StoredState ReadState(string game,long seq){lock(gate){Ready();return CopyState(ReadStateUnsafe(game,seq));}}
    UndoStepMetadata? ReadUndoMetadataUnsafe(string game,long seq)
    {
        var rows=db.Query("SELECT schema_version,metadata_complete,has_random_result,intermission_boundary,phase_edges_json FROM undo_metadata WHERE game_id=? AND state_seq=?",game,seq);
        if(rows.Count==0)return null;
        if(rows.Count!=1||rows[0][0] is not long schema||rows[0][1] is not long complete||rows[0][2] is not long random||rows[0][3] is not long boundary||rows[0][4] is not byte[] bytes)
            throw new InvalidDataException("Undo metadata row malformed; preserve database");
        try{
            var edges=JsonSerializer.Deserialize<UndoPhaseEdge[]>(bytes)??throw new InvalidDataException("Undo phase edge list missing");
            var metadata=new UndoStepMetadata(checked((int)schema),complete==1,random==1,boundary==1,edges);
            ValidateUndoMetadata(metadata);return metadata;
        }catch(Exception error)when(error is JsonException or InvalidDataException or InvalidCastException or OverflowException){throw new InvalidDataException("Undo metadata corrupted; preserve database",error);}
    }
    public UndoStepMetadata? ReadUndoMetadata(string game,long seq){lock(gate){Ready();return ReadUndoMetadataUnsafe(game,seq);}}
    // Log traversal needs immutable history metadata, not every historical
    // multi-megabyte Game snapshot. Current basis is checked separately.
    public StoredLogState ReadLogState(string game,long seq)
    {
        lock(gate){
            Ready();var key=(game,seq);if(logCache.TryGetValue(key,out var cached))return CopyLog(cached);
            var rows=db.Query("SELECT game_id,seq,source_id,events,parent_seq,restored_from,kind FROM states WHERE game_id=? AND seq=?",game,seq);if(rows.Count!=1)throw new KeyNotFoundException("Local log state missing");var r=rows[0];
            var state=new StoredLogState((string)r[0]!, (long)r[1]!, (string)r[2]!, (byte[])r[3]!, (long?)r[4], (long?)r[5], (string)r[6]!);
            if(state.Events.LongLength<=LogCacheLimit){
                while(logOrder.Count!=0&&(logCacheBytes+state.Events.LongLength>LogCacheLimit||logCache.Count>=1024)){var oldest=logOrder.Dequeue();logCacheBytes-=logCache[oldest].Events.LongLength;logCache.Remove(oldest);}
                logCache[key]=state;logOrder.Enqueue(key);logCacheBytes+=state.Events.LongLength;
            }
            return CopyLog(state);
        }
    }
    public StoredState ReadCurrent(string game)
    {
        lock(gate){Ready();if(currentCache.TryGetValue(game,out var cached))return CopyState(cached);var rows=db.Query("SELECT current_seq FROM games WHERE id=?",game);if(rows.Count!=1)throw new KeyNotFoundException("Local game missing");var state=ReadStateUnsafe(game,(long)rows[0][0]!);if(stateCache.ContainsKey((game,state.Sequence))){if(currentCache.Count>=32)currentCache.Remove(currentCache.Keys.First());currentCache[game]=state;}return CopyState(state);}
    }
    CommitReceipt? ReplayUnsafe(string game,string requestId,string hash)
    {
        var rows=db.Query("SELECT payload_hash,committed_seq FROM requests WHERE game_id=? AND request_id=?",game,requestId);
        if(rows.Count==0)return null;
        if((string)rows[0][0]! != hash)throw new InvalidOperationException("Request ID was already used with different payload");
        var state=ReadStateUnsafe(game,(long)rows[0][1]!);var current=db.Query("SELECT current_seq FROM games WHERE id=?",game);
        if(current.Count!=1)throw new InvalidDataException("Receipt references missing game");
        return new(game,requestId,state.Sequence,(long)current[0][0]!,state.Sha256,true);
    }
    public CommitReceipt? Replay(string game,string requestId,string hash){lock(gate){Ready();return ReplayUnsafe(game,requestId,hash);}}
    public CommitReceipt? RecoverReceipt(string game,string requestId,string hash)
    {
        lock(gate){
            recoveryRequired=true;ClearCaches();db.Dispose();
#if NATIVE_PROBE
            BeforeRecoveryOpen?.Invoke();
#endif
            db=new NativeSqlite(databasePath,create:false);
            db.ExecuteScript("PRAGMA foreign_keys=ON; PRAGMA synchronous=FULL;");
            if((string)db.Query("PRAGMA journal_mode=DELETE")[0][0]! !="delete"||(long)db.Query("PRAGMA synchronous")[0][0]!!=2||(long)db.Query("PRAGMA foreign_keys")[0][0]!!=1)throw new IOException("Recovered SQLite durability mode unavailable");
            if((long)db.Query("PRAGMA user_version")[0][0]!!=2||(string)db.Query("PRAGMA quick_check")[0][0]! !="ok"||db.Query("PRAGMA foreign_key_check").Count!=0)throw new InvalidDataException("Fresh-connection recovery failed; preserve database");
            VerifyUndoMetadataSchema();VerifyHistoryIndexSchema();var result=ReplayUnsafe(game,requestId,hash);
            if(result is not null&&ReadUndoMetadataUnsafe(game,result.CommittedSequence) is null)throw new InvalidDataException("Committed request is missing atomic undo metadata; preserve database");
            recoveryRequired=false;return result;
        }
    }
    internal CommitReceipt Commit(string title,string requestId,string payloadHash,long expectedSequence,StoredState candidate,StoredState? basis)
    {
        lock(gate){Ready();long at=HostTiming.Timestamp();var undoMetadata=candidate.UndoMetadata??UndoStepMetadata.Incomplete;undoMetadata=undoMetadata with{PhaseEdges=undoMetadata.PhaseEdges.ToArray()};candidate=candidate with{Save=(byte[])candidate.Save.Clone(),Events=(byte[])candidate.Events.Clone(),UndoMetadata=undoMetadata};HostTiming.Observe("host.save.ownershipCopy",at,1,candidate.Save.Length+candidate.Events.Length);var history=Validate(candidate);try{var receipt=db.Transaction(()=>{
            var replay=ReplayUnsafe(candidate.GameId,requestId,payloadHash);if(replay is not null)return replay;
            long next;
            var rows=db.Query("SELECT current_seq FROM games WHERE id=?",candidate.GameId);
            if(basis is null){
                if(expectedSequence!=-1||rows.Count!=0)throw new StaleLocalState();next=0;
                db.Query("INSERT INTO games(id,title,current_seq) VALUES(?,?,0)",candidate.GameId,title);
            }else{
                if(rows.Count!=1||(long)rows[0][0]! != expectedSequence||basis.Sequence!=expectedSequence||basis.GameId!=candidate.GameId)throw new StaleLocalState();
                var actual=ReadStateUnsafe(candidate.GameId,expectedSequence);
                if(actual.Sha256!=basis.Sha256||actual.SourceId!=basis.SourceId)throw new StaleLocalState();
                // A step may move a game from an earlier core to the current one (the core imported the
                // earlier save itself, see LocalGameController.EnsureActive); the history keeps both ids.
                next=checked(expectedSequence+1);
            }
            // Validate above hashed our privately owned byte clone. Nothing
            // can mutate it between validation and this synchronous INSERT.
            if(candidate.Sequence!=next||candidate.Parent!=basis?.Sequence)throw new InvalidDataException("Candidate sequence/hash does not match durable basis");
#if NATIVE_PROBE
            FaultInjection?.Invoke("before-state");
#endif
            db.Query("INSERT INTO states("+StateColumns+") VALUES(?,?,?,?,?,?,?,?,?,?)",candidate.GameId,next,candidate.CoreRevision,candidate.SourceId,candidate.Save,candidate.Sha256,candidate.Events,candidate.Parent,candidate.RestoredFrom,candidate.Kind);
            WriteHistoryIndex(history);
            db.Query("UPDATE games SET current_seq=? WHERE id=?",next,candidate.GameId);
            db.Query("INSERT INTO requests(game_id,request_id,payload_hash,expected_seq,committed_seq) VALUES(?,?,?,?,?)",candidate.GameId,requestId,payloadHash,expectedSequence,next);
#if NATIVE_PROBE
            FaultInjection?.Invoke("before-undo-metadata");
#endif
            db.Query("INSERT INTO undo_metadata(game_id,state_seq,schema_version,metadata_complete,has_random_result,intermission_boundary,phase_edges_json) VALUES(?,?,?,?,?,?,?)",
                candidate.GameId,next,undoMetadata.SchemaVersion,undoMetadata.MetadataComplete?1:0,undoMetadata.HasRandomResult?1:0,undoMetadata.IntermissionBoundary?1:0,JsonSerializer.SerializeToUtf8Bytes(undoMetadata.PhaseEdges));
#if NATIVE_PROBE
            FaultInjection?.Invoke("after-undo-metadata");
            FaultInjection?.Invoke("before-commit");
#endif
            return new CommitReceipt(candidate.GameId,requestId,next,next,candidate.Sha256,false);
        });if(!receipt.Replayed){CacheHistory(history);var committed=CacheState(candidate with{UndoMetadata=null});if(stateCache.ContainsKey((candidate.GameId,candidate.Sequence))){if(currentCache.Count>=32&&!currentCache.ContainsKey(candidate.GameId))currentCache.Remove(currentCache.Keys.First());currentCache[candidate.GameId]=committed;}else currentCache.Remove(candidate.GameId);}return receipt;}catch{ClearCaches();recoveryRequired=true;throw;}}
    }
    public List<(string Id,string Title,long Sequence)> ListGames(){lock(gate){Ready();return db.Query("SELECT id,title,current_seq FROM games ORDER BY id").Select(r=>((string)r[0]!, (string)r[1]!, (long)r[2]!)).ToList();}}
    public void BackupTo(string newAbsoluteFile){lock(gate){Ready();db.BackupTo(newAbsoluteFile);}}
    public void Dispose(){lock(gate)db.Dispose();}
}
