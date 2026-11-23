using System.Text.Json;

namespace Arkham.NativeHost;

// Scalar, immutable navigation data. Never a replacement for validating the
// actual target Save, preference-only change or restored-state equivalence.
public sealed record StoredHistoryIndex(string GameId,long Sequence,string SourceId,string Sha256,long? Parent,long? RestoredFrom,string Kind,long? ScenarioStep,string UndoMode);

public sealed partial class LocalGameStore
{
    readonly Dictionary<(string Game,long Sequence),StoredHistoryIndex> historyCache=new();
    readonly Queue<(string Game,long Sequence)> historyOrder=new();
    const string HistoryIndexSchema="""
CREATE TABLE IF NOT EXISTS history_index(
 game_id TEXT NOT NULL,state_seq INTEGER NOT NULL,save_sha256 TEXT NOT NULL,
 scenario_step INTEGER,undo_mode TEXT NOT NULL,index_digest TEXT NOT NULL CHECK(length(index_digest)=64),
 PRIMARY KEY(game_id,state_seq),FOREIGN KEY(game_id,state_seq) REFERENCES states(game_id,seq));
CREATE TRIGGER IF NOT EXISTS history_index_no_update BEFORE UPDATE ON history_index BEGIN SELECT RAISE(ABORT,'immutable history index'); END;
CREATE TRIGGER IF NOT EXISTS history_index_no_delete BEFORE DELETE ON history_index BEGIN SELECT RAISE(ABORT,'immutable history index'); END;
""";
    void VerifyHistoryIndexSchema()
    {
        db.Query("SELECT game_id,state_seq,save_sha256,scenario_step,undo_mode,index_digest FROM history_index LIMIT 0");
        foreach(string action in new[]{"update","delete"}){
            var rows=db.Query("SELECT sql FROM sqlite_master WHERE type='trigger' AND name=? AND tbl_name='history_index'","history_index_no_"+action);
            if(rows.Count!=1||rows[0][0] is not string sql||!string.Concat(sql.Where(c=>!char.IsWhiteSpace(c))).Contains("raise(abort,'immutablehistoryindex')",StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("History index immutability trigger missing; preserve database");
        }
    }
    static StoredHistoryIndex HistoryFromSnapshot(StoredState state,JsonElement snapshot)
    {
        var game=snapshot.GetProperty("game");long? step=null;string mode="";
        if(game.TryGetProperty("gameScenarioSteps",out var raw)&&raw.ValueKind==JsonValueKind.Number&&raw.TryGetInt64(out long value))step=value;
        if(game.TryGetProperty("gameSettings",out var settings)&&settings.ValueKind==JsonValueKind.Object&&settings.TryGetProperty("settingsUndoMode",out var undo)&&undo.ValueKind==JsonValueKind.String)mode=undo.GetString()??"";
        return new(state.GameId,state.Sequence,state.SourceId,state.Sha256,state.Parent,state.RestoredFrom,state.Kind,step,mode);
    }
    static string HistoryDigest(StoredHistoryIndex value)=>Hash(JsonSerializer.SerializeToUtf8Bytes(value));
    StoredHistoryIndex CacheHistory(StoredHistoryIndex value)
    {
        var key=(value.GameId,value.Sequence);
        if(!historyCache.ContainsKey(key)){
            while(historyOrder.Count>=8192)historyCache.Remove(historyOrder.Dequeue());
            historyCache.Add(key,value);historyOrder.Enqueue(key);
        }
        return value;
    }
    void WriteHistoryIndex(StoredHistoryIndex value)=>db.Query("INSERT INTO history_index(game_id,state_seq,save_sha256,scenario_step,undo_mode,index_digest) VALUES(?,?,?,?,?,?)",
        value.GameId,value.Sequence,value.Sha256,value.ScenarioStep,value.UndoMode,HistoryDigest(value));
    public StoredHistoryIndex ReadHistory(string game,long sequence)
    {
        lock(gate){
            Ready();long at=HostTiming.Timestamp();
            try{
                if(historyCache.TryGetValue((game,sequence),out var cached)){HostTiming.Observe("history.memoryHit",at);return cached;}
                var rows=db.Query("SELECT s.game_id,s.seq,s.source_id,s.sha256,s.parent_seq,s.restored_from,s.kind,i.save_sha256,i.scenario_step,i.undo_mode,i.index_digest FROM states s JOIN history_index i ON i.game_id=s.game_id AND i.state_seq=s.seq WHERE s.game_id=? AND s.seq=?",game,sequence);
                if(rows.Count==1){
                    var r=rows[0];var value=new StoredHistoryIndex((string)r[0]!, (long)r[1]!, (string)r[2]!, (string)r[3]!, (long?)r[4], (long?)r[5], (string)r[6]!, (long?)r[8], (string)r[9]!);
                    if(value.GameId!=game||value.Sequence!=sequence||value.Sha256!=(string)r[7]!||HistoryDigest(value)!=(string)r[10]!)throw new InvalidDataException("History index does not match immutable state; preserve database");
                    HostTiming.Observe("history.persistedHit",at);return CacheHistory(value);
                }
                // Legacy rows have no derived index. Validate once on demand,
                // retain only scalars across projections, and do not rewrite
                // the user's history or force an extra disk commit on reads.
                var state=ReadStateUnsafe(game,sequence);
                if(!historyCache.TryGetValue((game,sequence),out var legacy))legacy=CacheHistory(Validate(state));
                HostTiming.Observe("history.legacyFullSave",at,1,state.Save.Length);return legacy;
            }finally{HostTiming.Observe("history.readTotal",at);}
        }
    }
    public StoredHistoryIndex ReadCurrentHistory(string game)
    {
        lock(gate){
            Ready();if(currentCache.TryGetValue(game,out var current))return ReadHistory(game,current.Sequence);
            var rows=db.Query("SELECT current_seq FROM games WHERE id=?",game);if(rows.Count!=1)throw new KeyNotFoundException("Local game missing");
            return ReadHistory(game,(long)rows[0][0]!);
        }
    }
}
