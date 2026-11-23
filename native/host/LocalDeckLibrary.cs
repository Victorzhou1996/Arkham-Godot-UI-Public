using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Arkham.NativeHost;

public sealed record LocalDeckDraft(string Id,string Name,long Generation,byte[] Json,string Sha256);
public sealed record LocalDeckVersion(string Id,string DraftId,long Number,string Name,byte[] Json,string Sha256);

// Independent local drafts. Saving/publishing is NOT a deck-legality certificate.
// Store the complete input bytes; do not round-trip unknown metadata through doubles.
public sealed class LocalDeckLibrary : IDisposable
{
    const int ApplicationId=0x4148444C;readonly NativeSqlite db;bool failed;
    static string Hash(byte[] bytes)=>Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    static string Name(string name)=>!string.IsNullOrWhiteSpace(name)&&name.Length<=160&&!name.Any(char.IsControl)?name.Trim():throw new ArgumentException("牌组名称须为1至160字。");
    static byte[] Input(byte[] bytes)
    {
        if(bytes.Length is <2 or >1048576)throw new InvalidDataException("牌组JSON大小超限。");_=new UTF8Encoding(false,true).GetString(bytes);
        using var doc=JsonDocument.Parse(bytes,new JsonDocumentOptions{MaxDepth=64});var root=doc.RootElement;
        void Unique(JsonElement node){if(node.ValueKind==JsonValueKind.Object){var keys=new HashSet<string>(StringComparer.Ordinal);foreach(var p in node.EnumerateObject()){if(!keys.Add(p.Name))throw new InvalidDataException("JSON含重复字段。");Unique(p.Value);}}else if(node.ValueKind==JsonValueKind.Array)foreach(var v in node.EnumerateArray())Unique(v);}
        Unique(root);
        if(root.ValueKind!=JsonValueKind.Object||!root.TryGetProperty("investigator_code",out var inv)||inv.ValueKind!=JsonValueKind.String||string.IsNullOrWhiteSpace(inv.GetString())||!root.TryGetProperty("slots",out var slots)||slots.ValueKind!=JsonValueKind.Object)throw new InvalidDataException("需要调查员编号和slots数量表。");
        long total=0;int entries=0;foreach(var p in slots.EnumerateObject()){if(p.Name.Length is <1 or >160||!p.Value.TryGetInt32(out int n)||n is <0 or >2000)throw new InvalidDataException("无效卡牌数量。");total+=n;entries++;}if(total>2000||entries>500)throw new InvalidDataException("牌组数量超限。");return (byte[])bytes.Clone();
    }
    T Tx<T>(Func<T> action){if(failed)throw new IOException("牌组库连接异常，请关闭并重新打开。");try{return db.Transaction(action);}catch(AggregateException){failed=true;throw;}}
    public LocalDeckLibrary(string file)
    {
        if(!Path.IsPathFullyQualified(file)||file.StartsWith(@"\\",StringComparison.Ordinal))throw new ArgumentException("需要本地绝对路径。");db=new NativeSqlite(file);
        try{db.ExecuteScript("PRAGMA synchronous=FULL;");Tx(()=>{long version=(long)db.Query("PRAGMA user_version")[0][0]!,app=(long)db.Query("PRAGMA application_id")[0][0]!;
            if(version==0&&app==0){if(db.Query("SELECT name FROM sqlite_master WHERE name NOT LIKE 'sqlite_%'").Count!=0)throw new InvalidDataException("拒绝覆盖未知数据库。");db.ExecuteScript("""
                CREATE TABLE deck_drafts(id TEXT PRIMARY KEY,name TEXT NOT NULL,generation INTEGER NOT NULL CHECK(generation>=0),json BLOB NOT NULL,sha TEXT NOT NULL,deleted INTEGER NOT NULL DEFAULT 0);
                CREATE TABLE deck_versions(id TEXT PRIMARY KEY,draft_id TEXT NOT NULL REFERENCES deck_drafts(id),number INTEGER NOT NULL,name TEXT NOT NULL,json BLOB NOT NULL,sha TEXT NOT NULL,UNIQUE(draft_id,number));
                CREATE TRIGGER immutable_deck_version_u BEFORE UPDATE ON deck_versions BEGIN SELECT RAISE(ABORT,'immutable deck version'); END;
                CREATE TRIGGER immutable_deck_version_d BEFORE DELETE ON deck_versions BEGIN SELECT RAISE(ABORT,'immutable deck version'); END;
                """);db.ExecuteScript($"PRAGMA application_id={ApplicationId}; PRAGMA user_version=1;");}
            else if(version!=1||app!=ApplicationId)throw new InvalidDataException("不支持的牌组库版本；未修改原文件。");return 0;});db.ExecuteScript("PRAGMA foreign_keys=ON;");}
        catch{db.Dispose();throw;}
    }
    LocalDeckDraft Read(object?[] row){byte[] bytes=(byte[])row[3]!;if(Hash(bytes)!=(string)row[4]!)throw new InvalidDataException("牌组摘要不符。");return new((string)row[0]!, (string)row[1]!, (long)row[2]!,bytes,(string)row[4]!);}
    public LocalDeckDraft[] Drafts()=>db.Query("SELECT id,name,generation,json,sha FROM deck_drafts WHERE deleted=0 ORDER BY name,id").Select(Read).ToArray();
    public LocalDeckDraft Save(string? id,long expectedGeneration,string name,byte[] json)
    {
        name=Name(name);byte[] bytes=Input(json);string sha=Hash(bytes);
        return Tx(()=>{if(id is null){id=Guid.NewGuid().ToString("D");db.Query("INSERT INTO deck_drafts(id,name,generation,json,sha) VALUES(?,?,0,?,?)",id,name,bytes,sha);return new LocalDeckDraft(id,name,0,bytes,sha);}
            var rows=db.Query("SELECT generation FROM deck_drafts WHERE id=? AND deleted=0",id);if(rows.Count!=1||(long)rows[0][0]!!=expectedGeneration)throw new InvalidOperationException("牌组已改变，请重新读取。");long next=checked(expectedGeneration+1);db.Query("UPDATE deck_drafts SET name=?,generation=?,json=?,sha=? WHERE id=?",name,next,bytes,sha,id);return new LocalDeckDraft(id,name,next,bytes,sha);});
    }
    public LocalDeckVersion Publish(string id,long expectedGeneration)
    {
        return Tx(()=>{var rows=db.Query("SELECT id,name,generation,json,sha FROM deck_drafts WHERE id=? AND deleted=0",id);if(rows.Count!=1)throw new KeyNotFoundException("牌组不存在。");var draft=Read(rows[0]);if(draft.Generation!=expectedGeneration)throw new InvalidOperationException("牌组已改变，请重新读取。");
            var latest=db.Query("SELECT id,number,name,sha FROM deck_versions WHERE draft_id=? ORDER BY number DESC LIMIT 1",id);if(latest.Count==1&&(string)latest[0][2]! ==draft.Name&&(string)latest[0][3]! ==draft.Sha256)return new LocalDeckVersion((string)latest[0][0]!,id,(long)latest[0][1]!,draft.Name,draft.Json,draft.Sha256);
            long number=latest.Count==0?1:checked((long)latest[0][1]!+1);string version=Guid.NewGuid().ToString("D");db.Query("INSERT INTO deck_versions(id,draft_id,number,name,json,sha) VALUES(?,?,?,?,?,?)",version,id,number,draft.Name,draft.Json,draft.Sha256);return new LocalDeckVersion(version,id,number,draft.Name,draft.Json,draft.Sha256);});
    }
    public LocalDeckVersion[] Versions()=>db.Query("SELECT id,draft_id,number,name,json,sha FROM deck_versions ORDER BY draft_id,number").Select(r=>{byte[] bytes=(byte[])r[4]!;if(Hash(bytes)!=(string)r[5]!)throw new InvalidDataException("版本摘要不符。");return new LocalDeckVersion((string)r[0]!, (string)r[1]!, (long)r[2]!, (string)r[3]!,bytes,(string)r[5]!);}).ToArray();
    public void Archive(string id,long expectedGeneration)=>Tx(()=>{var rows=db.Query("SELECT generation FROM deck_drafts WHERE id=? AND deleted=0",id);if(rows.Count!=1||(long)rows[0][0]!!=expectedGeneration)throw new InvalidOperationException("牌组已改变。");db.Query("UPDATE deck_drafts SET deleted=1,generation=generation+1 WHERE id=?",id);return 0;});
    public void Dispose()=>db.Dispose();
}
