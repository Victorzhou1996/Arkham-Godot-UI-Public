using System.Security.Cryptography;
using System.Text.Json;

namespace Arkham.NativeHost;

public sealed record StoredEpicState(Guid EventId,long Revision,string SourceId,byte[] Snapshot,string Sha256,long? Parent,byte[]? Events=null);
public sealed record EpicCommitReceipt(Guid EventId,Guid RequestId,long CommittedRevision,long CurrentRevision,string Sha256,bool Replayed);
public sealed record EpicUndoObject(string Sha256,string SourceId,byte[] Snapshot);

// A separate database for entire events. Never split an event into ordinary
// LocalGameStore rows: one immutable blob owns shared state AND all groups.
// This is durability only; Haskell remains responsible for shared rules/Undo.
public sealed class LocalEpicStore : IDisposable
{
    const long ApplicationId=0x41484550; // AHEP, not an ordinary local-game DB.
    const int MaxSnapshotBytes=64*1024*1024;
    readonly object gate=new();readonly string file;NativeSqlite db;bool recoveryRequired;
#if NATIVE_PROBE
    internal Action<string>? FaultInjection {get;set;}
#endif
    public static string Hash(byte[] bytes)=>Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    public LocalEpicStore(string absoluteFile)
    {
        if(!Path.IsPathFullyQualified(absoluteFile))throw new ArgumentException("Absolute event database path required");
        file=absoluteFile;db=new NativeSqlite(file);
        try{
            long version=(long)db.Query("PRAGMA user_version")[0][0]!,app=(long)db.Query("PRAGMA application_id")[0][0]!;
            if(version is not(0 or 3)||version==3&&app!=ApplicationId||version==0&&app!=0)throw new InvalidDataException("Not a supported Epic database; preserve the existing file");
            Durability();
            db.Transaction(()=>{
                long current=(long)db.Query("PRAGMA user_version")[0][0]!;
                if(current==0){
                    if(db.Query("SELECT name FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%'").Count!=0)throw new InvalidDataException("Refuse an unrelated unversioned event database");
                    db.ExecuteScript("""
                    CREATE TABLE epic_events(event_id TEXT PRIMARY KEY,title TEXT NOT NULL,current_revision INTEGER NOT NULL CHECK(current_revision>=0),
                      FOREIGN KEY(event_id,current_revision) REFERENCES epic_states(event_id,revision) DEFERRABLE INITIALLY DEFERRED);
                    CREATE TABLE epic_states(event_id TEXT NOT NULL,revision INTEGER NOT NULL CHECK(revision>=0),parent_revision INTEGER,source_id TEXT NOT NULL,
                      sha256 TEXT NOT NULL CHECK(length(sha256)=64),snapshot_bytes BLOB NOT NULL,events_bytes BLOB NOT NULL,PRIMARY KEY(event_id,revision),
                      FOREIGN KEY(event_id) REFERENCES epic_events(event_id),FOREIGN KEY(event_id,parent_revision) REFERENCES epic_states(event_id,revision),
                      CHECK(parent_revision IS NULL OR (revision>0 AND parent_revision=revision-1)));
                    CREATE TABLE epic_requests(event_id TEXT NOT NULL,request_id TEXT NOT NULL,payload_hash TEXT NOT NULL CHECK(length(payload_hash)=64),committed_revision INTEGER NOT NULL,
                      PRIMARY KEY(event_id,request_id),FOREIGN KEY(event_id,committed_revision) REFERENCES epic_states(event_id,revision));
                    CREATE TRIGGER epic_states_no_update BEFORE UPDATE ON epic_states BEGIN SELECT RAISE(ABORT,'immutable event state'); END;
                    CREATE TRIGGER epic_states_no_delete BEFORE DELETE ON epic_states BEGIN SELECT RAISE(ABORT,'immutable event state'); END;
                    CREATE TRIGGER epic_requests_no_update BEFORE UPDATE ON epic_requests BEGIN SELECT RAISE(ABORT,'immutable event request'); END;
                    CREATE TRIGGER epic_requests_no_delete BEFORE DELETE ON epic_requests BEGIN SELECT RAISE(ABORT,'immutable event request'); END;
                    CREATE TABLE epic_undo_objects(sha256 TEXT PRIMARY KEY CHECK(length(sha256)=64),source_id TEXT NOT NULL,snapshot_bytes BLOB NOT NULL);
                    CREATE TRIGGER epic_undo_no_update BEFORE UPDATE ON epic_undo_objects BEGIN SELECT RAISE(ABORT,'immutable undo object'); END;
                    CREATE TRIGGER epic_undo_no_delete BEFORE DELETE ON epic_undo_objects BEGIN SELECT RAISE(ABORT,'immutable undo object'); END;
                    PRAGMA application_id=1095255376;
                    PRAGMA user_version=3;
                    """);
                }else if(current!=3||(long)db.Query("PRAGMA application_id")[0][0]!!=ApplicationId)throw new InvalidDataException("Event schema changed while opening");
                return true;
            });
            Integrity();
        }catch{db.Dispose();throw;}
    }
    void Durability()
    {
        db.ExecuteScript("PRAGMA foreign_keys=ON; PRAGMA synchronous=FULL;");
        if((string)db.Query("PRAGMA journal_mode=DELETE")[0][0]! !="delete"||(long)db.Query("PRAGMA synchronous")[0][0]!!=2||(long)db.Query("PRAGMA foreign_keys")[0][0]!!=1)throw new IOException("Required event durability unavailable");
    }
    void Integrity()
    {
        if((long)db.Query("PRAGMA user_version")[0][0]!!=3||(long)db.Query("PRAGMA application_id")[0][0]!!=ApplicationId
            ||(string)db.Query("PRAGMA quick_check")[0][0]! !="ok"||db.Query("PRAGMA foreign_key_check").Count!=0)throw new InvalidDataException("Event database consistency failed; preserve file for recovery");
    }
    static void Identity(Guid id){if(id==Guid.Empty)throw new InvalidDataException("Empty event/request identity");}
    static void Digest(string sha){if(sha.Length!=64||sha.Any(c=>c is not(>='0' and <='9') and not(>='a' and <='f')))throw new InvalidDataException("Invalid SHA256");}
    static void UniqueProperties(JsonElement element)
    {
        if(element.ValueKind==JsonValueKind.Object){var names=new HashSet<string>(StringComparer.Ordinal);foreach(var p in element.EnumerateObject()){if(!names.Add(p.Name))throw new InvalidDataException("Duplicate event JSON field");UniqueProperties(p.Value);}}
        else if(element.ValueKind==JsonValueKind.Array)foreach(var item in element.EnumerateArray())UniqueProperties(item);
    }
    public static StoredEpicState Candidate(byte[] bytes,long? parent,byte[]? events=null)
    {
        bytes=(byte[])bytes.Clone();if(bytes.Length is <2 or >MaxSnapshotBytes)throw new InvalidDataException("Event snapshot exceeds native transport budget; do not truncate history");
        using var document=JsonDocument.Parse(bytes,new JsonDocumentOptions{MaxDepth=256});var root=document.RootElement;UniqueProperties(root);
        Guid eventId=root.GetProperty("eventId").GetGuid();Identity(eventId);
        string source=root.GetProperty("coreSourceId").GetString()!;long revision=root.GetProperty("revision").GetInt64();
        if(root.GetProperty("eventFormat").GetInt32()!=1||revision<0||revision==0&&parent is not null||revision>0&&parent is not null&&parent!=revision-1
            ||!source.StartsWith("arkham-core-sha256:",StringComparison.Ordinal)||source.Length!=83)throw new InvalidDataException("Event source/revision/format mismatch");
        Digest(source[19..]);
        // Envelope checks only. No shared counter, gameplay, floor or undo
        // decision is calculated in managed code.
        var games=new HashSet<Guid>();var members=new HashSet<Guid>();int ordinal=0;
        foreach(var group in root.GetProperty("groups").EnumerateArray()){
            var game=group.GetProperty("gameId").GetGuid();Identity(game);long seq=group.GetProperty("sequence").GetInt64();
            int seats=group.GetProperty("seatCount").GetInt32();long floor=group.GetProperty("undoFloor").GetInt64();
            if(!games.Add(game)||game==eventId||group.GetProperty("ordinal").GetInt32()!=ordinal++||seats is <1 or >4||seq<0||floor<0||floor>seq)throw new InvalidDataException("Invalid event group envelope");
            var players=group.GetProperty("members");if(players.GetArrayLength()!=seats)throw new InvalidDataException("Event seat envelope mismatch");
            foreach(var p in players.EnumerateArray()){var id=p.GetGuid();Identity(id);if(!members.Add(id))throw new InvalidDataException("Duplicate event member");}
            var snapshot=group.GetProperty("snapshot");if(snapshot.GetProperty("snapshotFormat").GetInt32()!=2||snapshot.GetProperty("coreSourceId").GetString()!=source)throw new InvalidDataException("Group core identity mismatch");
        }
        if(ordinal==0)throw new InvalidDataException("Event requires groups");
        events=events is null?"[]"u8.ToArray():(byte[])events.Clone();
        if(events.Length>MaxSnapshotBytes)throw new InvalidDataException("Event logs exceed persistence budget");
        using(var log=JsonDocument.Parse(events,new JsonDocumentOptions{MaxDepth=256})){if(log.RootElement.ValueKind!=JsonValueKind.Array)throw new InvalidDataException("Event logs require stream array");UniqueProperties(log.RootElement);}
        return new(eventId,revision,source,bytes,Hash(bytes),parent,events);
    }
    public static EpicUndoObject UndoObject(string sha,string source,byte[] bytes)
    {
        Digest(sha);bytes=(byte[])bytes.Clone();
        if(bytes.Length>MaxSnapshotBytes||Hash(bytes)!=sha)throw new InvalidDataException("Undo object size/SHA mismatch");
        using var document=JsonDocument.Parse(bytes,new JsonDocumentOptions{MaxDepth=256});var snapshot=document.RootElement;UniqueProperties(snapshot);
        if(snapshot.GetProperty("snapshotFormat").GetInt32()!=2||snapshot.GetProperty("coreSourceId").GetString()!=source||snapshot.GetProperty("revision").GetInt64()<0)throw new InvalidDataException("Undo object source/format mismatch");
        return new(sha,source,bytes);
    }
    void Ready(){if(recoveryRequired)throw new InvalidOperationException("Recover event database on a fresh connection before reuse");}
    StoredEpicState ReadUnsafe(Guid id,long revision)
    {
        var rows=db.Query("SELECT source_id,sha256,snapshot_bytes,parent_revision,events_bytes FROM epic_states WHERE event_id=? AND revision=?",id.ToString("D"),revision);
        if(rows.Count!=1)throw new KeyNotFoundException("Event state missing");var r=rows[0];var state=Candidate((byte[])r[2]!, (long?)r[3],(byte[])r[4]!);
        if(state.EventId!=id||state.Revision!=revision||state.SourceId!=(string)r[0]!||state.Sha256!=(string)r[1]!)throw new InvalidDataException("Event state identity/hash mismatch");return state;
    }
    public StoredEpicState ReadCurrent(Guid id)
    {
        lock(gate){Ready();Identity(id);var rows=db.Query("SELECT current_revision FROM epic_events WHERE event_id=?",id.ToString("D"));if(rows.Count!=1)throw new KeyNotFoundException("Local event missing");return ReadUnsafe(id,(long)rows[0][0]!);}
    }
    public IReadOnlyList<(Guid Id,string Title,long Revision)> ListEvents()
    {
        lock(gate){Ready();return db.Query("SELECT event_id,title,current_revision FROM epic_events ORDER BY rowid DESC")
            .Select(r=>(Guid.Parse((string)r[0]!),(string)r[1]!, (long)r[2]!)).ToArray();}
    }
    public StoredEpicState ReadState(Guid id,long revision){lock(gate){Ready();return ReadUnsafe(id,revision);}}
    EpicCommitReceipt? ReplayUnsafe(Guid id,Guid request,string payloadHash)
    {
        Identity(id);Identity(request);Digest(payloadHash);
        var rows=db.Query("SELECT payload_hash,committed_revision FROM epic_requests WHERE event_id=? AND request_id=?",id.ToString("D"),request.ToString("D"));if(rows.Count==0)return null;
        if((string)rows[0][0]! !=payloadHash)throw new InvalidOperationException("Event request ID reused with another payload");
        var committed=ReadUnsafe(id,(long)rows[0][1]!);var current=db.Query("SELECT current_revision FROM epic_events WHERE event_id=?",id.ToString("D"));if(current.Count!=1)throw new InvalidDataException("Event receipt missing current pointer");
        return new(id,request,committed.Revision,(long)current[0][0]!,committed.Sha256,true);
    }
    public EpicCommitReceipt? Replay(Guid id,Guid request,string payloadHash){lock(gate){Ready();return ReplayUnsafe(id,request,payloadHash);}}
    public EpicCommitReceipt Commit(string title,Guid request,string payloadHash,StoredEpicState candidate,StoredEpicState? basis)=>CommitCore(title,request,payloadHash,candidate,basis,false);
    // The controller must validate the whole event with the loaded DLL first.
    // Preserve imported internal history/revisions; never rebase them to zero.
    public EpicCommitReceipt ImportRoot(string title,Guid request,string payloadHash,byte[] snapshot,IReadOnlyList<EpicUndoObject>? objects=null)=>CommitCore(title,request,payloadHash,Candidate(snapshot,null),null,true,objects);
    EpicCommitReceipt CommitCore(string title,Guid request,string payloadHash,StoredEpicState candidate,StoredEpicState? basis,bool importing,IReadOnlyList<EpicUndoObject>? objects=null)
    {
        lock(gate){Ready();candidate=Candidate(candidate.Snapshot,candidate.Parent,candidate.Events);Identity(request);Digest(payloadHash);
            var imported=(objects??Array.Empty<EpicUndoObject>()).Select(o=>UndoObject(o.Sha256,o.SourceId,o.Snapshot)).ToArray();
            if(imported.Any(o=>o.SourceId!=candidate.SourceId)||imported.Select(o=>o.Sha256).Distinct(StringComparer.Ordinal).Count()!=imported.Length)throw new InvalidDataException("Duplicate or incompatible undo object");
            if(string.IsNullOrWhiteSpace(title)||title.Length>512)throw new InvalidDataException("Invalid event title");
            try{return db.Transaction(()=>{
                var replay=ReplayUnsafe(candidate.EventId,request,payloadHash);if(replay is not null)return replay;
                var rows=db.Query("SELECT current_revision FROM epic_events WHERE event_id=?",candidate.EventId.ToString("D"));
                if(basis is null){
                    if(rows.Count!=0||!importing&&candidate.Revision!=0||candidate.Parent is not null)throw new StaleLocalState();
                    db.Query("INSERT INTO epic_events(event_id,title,current_revision) VALUES(?,?,?)",candidate.EventId.ToString("D"),title,candidate.Revision);
                }else{
                    if(rows.Count!=1||basis.EventId!=candidate.EventId||(long)rows[0][0]! !=basis.Revision||candidate.Revision!=checked(basis.Revision+1)||candidate.Parent!=basis.Revision)throw new StaleLocalState();
                    var actual=ReadUnsafe(basis.EventId,basis.Revision);
                    if(actual.Sha256!=basis.Sha256||actual.SourceId!=basis.SourceId)throw new StaleLocalState();
                    if(candidate.SourceId!=actual.SourceId)throw new InvalidDataException("Epic source migration is explicit, not automatic");
                }
                foreach(var obj in imported){
                    var old=db.Query("SELECT source_id,snapshot_bytes FROM epic_undo_objects WHERE sha256=?",obj.Sha256);
                    if(old.Count==0)db.Query("INSERT INTO epic_undo_objects(sha256,source_id,snapshot_bytes) VALUES(?,?,?)",obj.Sha256,obj.SourceId,obj.Snapshot);
                    else if((string)old[0][0]! !=obj.SourceId||!((byte[])old[0][1]!).SequenceEqual(obj.Snapshot))throw new InvalidDataException("Undo content-address collision");
                }
                db.Query("INSERT INTO epic_states(event_id,revision,parent_revision,source_id,sha256,snapshot_bytes,events_bytes) VALUES(?,?,?,?,?,?,?)",candidate.EventId.ToString("D"),candidate.Revision,candidate.Parent,candidate.SourceId,candidate.Sha256,candidate.Snapshot,candidate.Events);
#if NATIVE_PROBE
                FaultInjection?.Invoke("after-state-before-pointer");
#endif
                db.Query("UPDATE epic_events SET current_revision=? WHERE event_id=?",candidate.Revision,candidate.EventId.ToString("D"));
                db.Query("INSERT INTO epic_requests(event_id,request_id,payload_hash,committed_revision) VALUES(?,?,?,?)",candidate.EventId.ToString("D"),request.ToString("D"),payloadHash,candidate.Revision);
                return new EpicCommitReceipt(candidate.EventId,request,candidate.Revision,candidate.Revision,candidate.Sha256,false);
            });}catch{recoveryRequired=true;throw;}
        }
    }
    public EpicCommitReceipt? RecoverReceipt(Guid id,Guid request,string payloadHash)
    {
        lock(gate){recoveryRequired=true;db.Dispose();db=new NativeSqlite(file,create:false);Durability();Integrity();var result=ReplayUnsafe(id,request,payloadHash);recoveryRequired=false;return result;}
    }
    public List<(Guid Id,string Title,long Revision)> List(){lock(gate){Ready();return db.Query("SELECT event_id,title,current_revision FROM epic_events ORDER BY event_id").Select(r=>(Guid.Parse((string)r[0]!), (string)r[1]!, (long)r[2]!)).ToList();}}
    public EpicUndoObject? FindUndoObject(string sha)
    {
        lock(gate){Ready();Digest(sha);var rows=db.Query("SELECT source_id,snapshot_bytes FROM epic_undo_objects WHERE sha256=?",sha);return rows.Count==0?null:UndoObject(sha,(string)rows[0][0]!, (byte[])rows[0][1]!);}
    }
    public void BackupTo(string newAbsoluteFile){lock(gate){Ready();db.BackupTo(newAbsoluteFile);}}
    public void Dispose(){lock(gate)db.Dispose();}
}

