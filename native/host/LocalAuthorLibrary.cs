using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Arkham.NativeHost;

public sealed record AuthorSet(string Id,string Name,long Generation,bool Deleted);
public sealed record AuthorDraft(string Id,string SetId,string Name,long Generation,byte[] Package,string Sha256);
public sealed record AuthorRevision(string Id,string DraftId,string SetId,string Name,long Number,byte[] Package,string Sha256,string[] ArtHashes);
public sealed class StaleAuthorDraft : InvalidOperationException { public StaleAuthorDraft():base("作者库已被更新，请重新读取后保存。"){} }

/// <summary>Inert local storage, not a Haskell card validator or an execution registry.
/// Complete UTF-8 JSON is retained byte-for-byte. Published versions/art are immutable.
/// No game database, network, runtime execution, or filesystem art path is accepted here.</summary>
public sealed class LocalAuthorLibrary : IDisposable
{
    const int ApplicationId=0x41484155,Schema=1;
    public const int MaxPackageBytes=8*1024*1024,MaxArtBytes=16*1024*1024,MaxBundleBytes=64*1024*1024;
    static readonly UTF8Encoding Utf8=new(false,true);
    readonly object gate=new(); readonly NativeSqlite db; bool disposed,recoveryRequired;
#if NATIVE_PROBE
    internal Action? BeforeRevisionCommit {get;set;}
    internal Action? RollbackFault {set=>db.BeforeRollback=value;}
#endif
    public static string Hash(byte[] bytes)=>Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    static string Name(string value){if(string.IsNullOrWhiteSpace(value)||value.Length>160||value.Any(char.IsControl))throw new ArgumentException("名称须为1至160个非控制字符。");return value.Trim();}
    static string Id(string value){if(!Guid.TryParseExact(value,"D",out var id)||id.ToString("D")!=value)throw new ArgumentException("规范本地标识必需。");return value;}
    static string Digest(string value){if(value.Length!=64||value.Any(c=>!((c>='0'&&c<='9')||(c>='a'&&c<='f'))))throw new ArgumentException("SHA256必需。");return value;}
    void Ready(){ObjectDisposedException.ThrowIf(disposed,this);if(recoveryRequired)throw new InvalidOperationException("作者库连接状态不确定；请关闭后重新打开，禁止继续使用此连接。");}
    T Transaction<T>(Func<T> action){try{return db.Transaction(action);}catch(AggregateException){recoveryRequired=true;throw;}}
    public LocalAuthorLibrary(string absoluteFile)
    {
        if(!Path.IsPathFullyQualified(absoluteFile)||absoluteFile.StartsWith(@"\\",StringComparison.Ordinal)||absoluteFile.Contains('\0'))throw new ArgumentException("本地绝对路径必需。");
        db=new NativeSqlite(absoluteFile);
        try{
            long version=(long)db.Query("PRAGMA user_version")[0][0]!,app=(long)db.Query("PRAGMA application_id")[0][0]!;
            if((version!=0||app!=0)&&(version!=Schema||app!=ApplicationId))throw new InvalidDataException("不是受支持的作者库；保留原文件。");
            if(version==0&&db.Query("SELECT name FROM sqlite_master WHERE name NOT LIKE 'sqlite_%'").Count>0)throw new InvalidDataException("拒绝覆盖未知数据库。");
            db.ExecuteScript("PRAGMA foreign_keys=ON; PRAGMA synchronous=FULL;");
            if((string)db.Query("PRAGMA journal_mode=DELETE")[0][0]! !="delete"||(long)db.Query("PRAGMA synchronous")[0][0]!!=2)throw new IOException("作者库持久化模式不可用。");
            Transaction(()=>{
                long current=(long)db.Query("PRAGMA user_version")[0][0]!,currentApp=(long)db.Query("PRAGMA application_id")[0][0]!;
                if(current==0&&currentApp==0){
                    if(db.Query("SELECT name FROM sqlite_master WHERE name NOT LIKE 'sqlite_%'").Count>0)throw new InvalidDataException("未知数据库。");
                    db.ExecuteScript("""
                    CREATE TABLE author_sets(id TEXT PRIMARY KEY,name TEXT NOT NULL,generation INTEGER NOT NULL CHECK(generation>=0),deleted INTEGER NOT NULL DEFAULT 0 CHECK(deleted IN(0,1)));
                    CREATE TABLE author_drafts(id TEXT PRIMARY KEY,set_id TEXT NOT NULL REFERENCES author_sets(id),name TEXT NOT NULL,generation INTEGER NOT NULL CHECK(generation>=0),package BLOB NOT NULL,sha256 TEXT NOT NULL CHECK(length(sha256)=64),deleted INTEGER NOT NULL DEFAULT 0 CHECK(deleted IN(0,1)));
                    CREATE TABLE author_revisions(id TEXT PRIMARY KEY,draft_id TEXT NOT NULL REFERENCES author_drafts(id),set_id TEXT NOT NULL REFERENCES author_sets(id),name TEXT NOT NULL,revision INTEGER NOT NULL CHECK(revision>0),package BLOB NOT NULL,sha256 TEXT NOT NULL CHECK(length(sha256)=64),UNIQUE(draft_id,revision));
                    CREATE TABLE author_art(sha256 TEXT PRIMARY KEY CHECK(length(sha256)=64),mime TEXT NOT NULL,bytes BLOB NOT NULL);
                    CREATE TABLE author_revision_art(revision_id TEXT NOT NULL REFERENCES author_revisions(id),sha256 TEXT NOT NULL REFERENCES author_art(sha256),PRIMARY KEY(revision_id,sha256));
                    CREATE TRIGGER immutable_author_revisions_u BEFORE UPDATE ON author_revisions BEGIN SELECT RAISE(ABORT,'immutable revision'); END;
                    CREATE TRIGGER immutable_author_revisions_d BEFORE DELETE ON author_revisions BEGIN SELECT RAISE(ABORT,'immutable revision'); END;
                    CREATE TRIGGER immutable_author_art_u BEFORE UPDATE ON author_art BEGIN SELECT RAISE(ABORT,'immutable art'); END;
                    CREATE TRIGGER immutable_author_art_d BEFORE DELETE ON author_art BEGIN SELECT RAISE(ABORT,'immutable art'); END;
                    CREATE TRIGGER immutable_author_links_u BEFORE UPDATE ON author_revision_art BEGIN SELECT RAISE(ABORT,'immutable art reference'); END;
                    CREATE TRIGGER immutable_author_links_d BEFORE DELETE ON author_revision_art BEGIN SELECT RAISE(ABORT,'immutable art reference'); END;
                    """);
                    db.ExecuteScript($"PRAGMA application_id={ApplicationId}; PRAGMA user_version={Schema};");
                }else if(current!=Schema||currentApp!=ApplicationId)throw new InvalidDataException("作者库版本改变。");
                return 0;
            });
            if((string)db.Query("PRAGMA quick_check")[0][0]! !="ok"||db.Query("PRAGMA foreign_key_check").Count>0)throw new InvalidDataException("作者库完整性检查失败。");
        }catch{db.Dispose();throw;}
    }
    // This checks only an inert transport envelope. Original CardDef/behavior validation
    // belongs to the source-bound Haskell author API, not a parallel C# rule engine.
    public static byte[] CheckEnvelope(byte[] package)
    {
        if(package.Length==0||package.Length>MaxPackageBytes)throw new InvalidDataException("作者包大小超过限制。");
        byte[] copy=(byte[])package.Clone();_ = Utf8.GetString(copy);
        using var doc=JsonDocument.Parse(copy,new JsonDocumentOptions{MaxDepth=64});
        void Unique(JsonElement node){if(node.ValueKind==JsonValueKind.Object){var names=new HashSet<string>(StringComparer.Ordinal);foreach(var p in node.EnumerateObject()){if(!names.Add(p.Name))throw new InvalidDataException("JSON包含重复字段："+p.Name);Unique(p.Value);}}else if(node.ValueKind==JsonValueKind.Array)foreach(var item in node.EnumerateArray())Unique(item);}
        Unique(doc.RootElement);var root=doc.RootElement;
        if(root.ValueKind!=JsonValueKind.Object||!root.TryGetProperty("format",out var format)||format.ValueKind!=JsonValueKind.String||format.GetString()!="arkham-author-package"||!root.TryGetProperty("version",out var version)||!version.TryGetInt32(out int v)||v!=1||!root.TryGetProperty("cards",out var cards)||cards.ValueKind!=JsonValueKind.Array||cards.GetArrayLength() is <1 or >500)throw new InvalidDataException("需要版本1作者包和1至500张卡。");
        foreach(var card in cards.EnumerateArray()){
            if(card.ValueKind!=JsonValueKind.Object||!card.TryGetProperty("def",out var def)||def.ValueKind!=JsonValueKind.Object)throw new InvalidDataException("每张卡必须保留完整def对象。");
            foreach(var art in CardArtSlots(card)){if(art.ValueKind!=JsonValueKind.String)throw new InvalidDataException("卡图必须是本地SHA引用。");string key=art.GetString()!;if(!key.StartsWith("sha256:",StringComparison.Ordinal))throw new InvalidDataException("不自动接受远程、文件或data URI卡图；请显式导入本地图片。");Digest(key[7..]);}
        }
        return copy;
    }
    // Explicit original image slots; unknown advanced metadata is preserved inertly,
    // never recursively interpreted as a URL or automatically fetched.
    static IEnumerable<JsonElement> CardArtSlots(JsonElement card)
    {
        if(card.TryGetProperty("art",out var art)&&art.ValueKind!=JsonValueKind.Null)yield return art;
        if(card.GetProperty("def").TryGetProperty("meta",out var meta)&&meta.ValueKind==JsonValueKind.Object)foreach(string key in new[]{"backArt","portrait","portraitBack"})if(meta.TryGetProperty(key,out var value)&&value.ValueKind!=JsonValueKind.Null)yield return value;
    }
    static string[] ArtReferences(byte[] package)
    {
        using var document=JsonDocument.Parse(package);return document.RootElement.GetProperty("cards").EnumerateArray().SelectMany(CardArtSlots).Select(a=>Digest(a.GetString()![7..])).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
    }
    public AuthorSet CreateSet(string name){name=Name(name);lock(gate){Ready();string id=Guid.NewGuid().ToString("D");db.Query("INSERT INTO author_sets(id,name,generation) VALUES(?,?,0)",id,name);return new(id,name,0,false);}}
    public AuthorSet[] Sets(bool includeDeleted=false){lock(gate){Ready();return db.Query("SELECT id,name,generation,deleted FROM author_sets WHERE deleted=0 OR ?=1 ORDER BY name,id",includeDeleted?1:0).Select(r=>new AuthorSet((string)r[0]!, (string)r[1]!, (long)r[2]!, (long)r[3]! !=0)).ToArray();}}
    AuthorSet SetUnsafe(string id){var rows=db.Query("SELECT id,name,generation,deleted FROM author_sets WHERE id=?",Id(id));if(rows.Count!=1)throw new KeyNotFoundException("作者卡组不存在。");var r=rows[0];return new((string)r[0]!, (string)r[1]!, (long)r[2]!, (long)r[3]! !=0);}
    public AuthorSet UpdateSet(string id,long expectedGeneration,string name,bool deleted=false){name=Name(name);lock(gate){Ready();return Transaction(()=>{var old=SetUnsafe(id);if(old.Generation!=expectedGeneration)throw new StaleAuthorDraft();long next=checked(old.Generation+1);db.Query("UPDATE author_sets SET name=?,generation=?,deleted=? WHERE id=?",name,next,deleted?1:0,id);return old with{Name=name,Generation=next,Deleted=deleted};});}}
    AuthorDraft DraftUnsafe(string id,bool includeDeleted=false){var rows=db.Query("SELECT id,set_id,name,generation,package,sha256,deleted FROM author_drafts WHERE id=?",Id(id));if(rows.Count!=1||(!includeDeleted&&(long)rows[0][6]! !=0))throw new KeyNotFoundException("草稿不存在。");var r=rows[0];byte[] bytes=(byte[])r[4]!;if(Hash(bytes)!=(string)r[5]!)throw new InvalidDataException("草稿摘要不符。");return new((string)r[0]!, (string)r[1]!, (string)r[2]!, (long)r[3]!, bytes, (string)r[5]!);}
    public AuthorDraft ReadDraft(string id){lock(gate){Ready();return DraftUnsafe(id);}}
    public AuthorDraft[] Drafts(string setId){lock(gate){Ready();return db.Query("SELECT id FROM author_drafts WHERE set_id=? AND deleted=0 ORDER BY name,id",Id(setId)).Select(r=>DraftUnsafe((string)r[0]!)).ToArray();}}
    public AuthorDraft CreateDraft(string setId,string name,byte[] package){name=Name(name);byte[] bytes=CheckEnvelope(package);lock(gate){Ready();return Transaction(()=>{if(SetUnsafe(setId).Deleted)throw new InvalidOperationException("卡组已归档。");string id=Guid.NewGuid().ToString("D"),sha=Hash(bytes);db.Query("INSERT INTO author_drafts(id,set_id,name,generation,package,sha256) VALUES(?,?,?,0,?,?)",id,setId,name,bytes,sha);return new AuthorDraft(id,setId,name,0,bytes,sha);});}}
    public AuthorDraft SaveDraft(string id,long expectedGeneration,string name,byte[] package){name=Name(name);byte[] bytes=CheckEnvelope(package);lock(gate){Ready();return Transaction(()=>{var old=DraftUnsafe(id);if(old.Generation!=expectedGeneration)throw new StaleAuthorDraft();if(SetUnsafe(old.SetId).Deleted)throw new InvalidOperationException("卡组已归档。");long next=checked(old.Generation+1);string sha=Hash(bytes);db.Query("UPDATE author_drafts SET name=?,generation=?,package=?,sha256=? WHERE id=?",name,next,bytes,sha,id);return old with{Name=name,Generation=next,Package=bytes,Sha256=sha};});}}
    public void DeleteDraft(string id,long expectedGeneration){lock(gate){Ready();Transaction(()=>{var old=DraftUnsafe(id);if(old.Generation!=expectedGeneration)throw new StaleAuthorDraft();db.Query("UPDATE author_drafts SET deleted=1,generation=? WHERE id=?",checked(old.Generation+1),id);return 0;});}}
    public AuthorRevision Publish(string draftId,long expectedGeneration,string expectedSha256)
    {
        Digest(expectedSha256);lock(gate){Ready();return Transaction(()=>{
            var draft=DraftUnsafe(draftId);if(draft.Generation!=expectedGeneration||draft.Sha256!=expectedSha256)throw new StaleAuthorDraft();if(SetUnsafe(draft.SetId).Deleted)throw new InvalidOperationException("卡组已归档。");
            var refs=ArtReferences(CheckEnvelope(draft.Package));foreach(string hash in refs)if(db.Query("SELECT sha256 FROM author_art WHERE sha256=?",hash).Count!=1)throw new InvalidDataException("作者包引用的本地卡图缺失："+hash);
            var latest=db.Query("SELECT id,sha256,name FROM author_revisions WHERE draft_id=? ORDER BY revision DESC LIMIT 1",draftId);
            if(latest.Count==1&&(string)latest[0][1]! ==draft.Sha256&&(string)latest[0][2]! ==draft.Name)return RevisionUnsafe((string)latest[0][0]!);
            long next=checked((long)db.Query("SELECT COALESCE(MAX(revision),0) FROM author_revisions WHERE draft_id=?",draftId)[0][0]!+1);string id=Guid.NewGuid().ToString("D");
            db.Query("INSERT INTO author_revisions(id,draft_id,set_id,name,revision,package,sha256) VALUES(?,?,?,?,?,?,?)",id,draftId,draft.SetId,draft.Name,next,draft.Package,draft.Sha256);foreach(string hash in refs)db.Query("INSERT INTO author_revision_art(revision_id,sha256) VALUES(?,?)",id,hash);
#if NATIVE_PROBE
            BeforeRevisionCommit?.Invoke();
#endif
            return RevisionUnsafe(id);
        });}
    }
    AuthorRevision RevisionUnsafe(string id){var rows=db.Query("SELECT id,draft_id,set_id,name,revision,package,sha256 FROM author_revisions WHERE id=?",Id(id));if(rows.Count!=1)throw new KeyNotFoundException("版本不存在。");var r=rows[0];byte[] bytes=(byte[])r[5]!;if(Hash(bytes)!=(string)r[6]!)throw new InvalidDataException("版本摘要不符。");var refs=db.Query("SELECT sha256 FROM author_revision_art WHERE revision_id=? ORDER BY sha256",id).Select(r=>(string)r[0]!).ToArray();return new((string)r[0]!, (string)r[1]!, (string)r[2]!, (string)r[3]!, (long)r[4]!,bytes,(string)r[6]!,refs);}
    public AuthorRevision ReadRevision(string id){lock(gate){Ready();return RevisionUnsafe(id);}}
    public AuthorRevision[] Revisions(string draftId){lock(gate){Ready();return db.Query("SELECT id FROM author_revisions WHERE draft_id=? ORDER BY revision",Id(draftId)).Select(r=>RevisionUnsafe((string)r[0]!)).ToArray();}}
    static byte[] CheckArt(byte[] bytes,string mime)
    {
        if(bytes.Length==0||bytes.Length>MaxArtBytes)throw new InvalidDataException("卡图大小超过限制。");byte[] copy=(byte[])bytes.Clone();
        bool magic=mime switch{"image/png"=>copy.AsSpan().StartsWith(new byte[]{137,80,78,71,13,10,26,10}),"image/jpeg"=>copy.Length>=3&&copy[0]==255&&copy[1]==216&&copy[2]==255,"image/webp"=>copy.Length>=12&&Encoding.ASCII.GetString(copy,0,4)=="RIFF"&&Encoding.ASCII.GetString(copy,8,4)=="WEBP",_=>false};
        if(!magic)throw new InvalidDataException("仅接受具有对应文件头的PNG/JPEG/WebP；文件头检查不等于图像解码验证。");return copy;
    }
    public string ImportArt(byte[] bytes,string mime){byte[] copy=CheckArt(bytes,mime);string hash=Hash(copy);lock(gate){Ready();db.Query("INSERT OR IGNORE INTO author_art(sha256,mime,bytes) VALUES(?,?,?)",hash,mime,copy);return hash;}}
    public (string Mime,byte[] Bytes) ReadArt(string hash){Digest(hash);lock(gate){Ready();var rows=db.Query("SELECT mime,bytes FROM author_art WHERE sha256=?",hash);if(rows.Count!=1)throw new KeyNotFoundException("本地卡图不存在。");byte[] bytes=(byte[])rows[0][1]!;if(Hash(bytes)!=hash)throw new InvalidDataException("卡图摘要不符。");return((string)rows[0][0]!,bytes);}}
    public byte[] ExportBundle(string revisionId)
    {
        lock(gate){Ready();var revision=RevisionUnsafe(revisionId);long size=revision.Package.Length;var art=new List<object>();
            foreach(string hash in revision.ArtHashes){var item=ReadArt(hash);size=checked(size+item.Bytes.Length);if(size>40*1024*1024)throw new InvalidDataException("完整作者包超过导出限制。");art.Add(new{sha256=hash,mime=item.Mime,base64=Convert.ToBase64String(item.Bytes)});}
            byte[] bundle=JsonSerializer.SerializeToUtf8Bytes(new{format="arkham-author-bundle",version=1,name=revision.Name,packageSha256=revision.Sha256,packageBase64=Convert.ToBase64String(revision.Package),art});if(bundle.Length>MaxBundleBytes)throw new InvalidDataException("作者包过大。");return bundle;
        }
    }
    public AuthorDraft ImportBundle(string setId,byte[] bundle)
    {
        if(bundle.Length==0||bundle.Length>MaxBundleBytes)throw new InvalidDataException("完整作者包过大。");_ = Utf8.GetString(bundle);
        using var document=JsonDocument.Parse(bundle,new JsonDocumentOptions{MaxDepth=64});var root=document.RootElement;
        // Bundle has a strict envelope: detect duplicate properties at every object too.
        void Unique(JsonElement n){if(n.ValueKind==JsonValueKind.Object){var seen=new HashSet<string>(StringComparer.Ordinal);foreach(var p in n.EnumerateObject()){if(!seen.Add(p.Name))throw new InvalidDataException("完整作者包包含重复字段。");Unique(p.Value);}}else if(n.ValueKind==JsonValueKind.Array)foreach(var x in n.EnumerateArray())Unique(x);}
        Unique(root);
        if(root.GetProperty("format").GetString()!="arkham-author-bundle"||root.GetProperty("version").GetInt32()!=1)throw new InvalidDataException("未知作者包格式。");
        string name=Name(root.GetProperty("name").GetString()!);byte[] package=CheckEnvelope(Convert.FromBase64String(root.GetProperty("packageBase64").GetString()!));string digest=Digest(root.GetProperty("packageSha256").GetString()!);if(Hash(package)!=digest)throw new InvalidDataException("作者包摘要不符。");
        var rows=root.GetProperty("art");if(rows.ValueKind!=JsonValueKind.Array||rows.GetArrayLength()>2000)throw new InvalidDataException("卡图清单过大。");var objects=new Dictionary<string,(string Mime,byte[] Bytes)>(StringComparer.Ordinal);long size=package.Length;
        foreach(var item in rows.EnumerateArray()){string hash=Digest(item.GetProperty("sha256").GetString()!),mime=item.GetProperty("mime").GetString()!;byte[] bytes=CheckArt(Convert.FromBase64String(item.GetProperty("base64").GetString()!),mime);size=checked(size+bytes.Length);if(size>40*1024*1024||Hash(bytes)!=hash||!objects.TryAdd(hash,(mime,bytes)))throw new InvalidDataException("卡图摘要、重复项或体积不符。");}
        string[] refs=ArtReferences(package);if(!refs.SequenceEqual(objects.Keys.Order(StringComparer.Ordinal)))throw new InvalidDataException("作者包必须携带恰好全部引用卡图。");
        lock(gate){Ready();return Transaction(()=>{if(SetUnsafe(setId).Deleted)throw new InvalidOperationException("卡组已归档。");foreach(var (hash,item) in objects)db.Query("INSERT OR IGNORE INTO author_art(sha256,mime,bytes) VALUES(?,?,?)",hash,item.Mime,item.Bytes);string id=Guid.NewGuid().ToString("D");db.Query("INSERT INTO author_drafts(id,set_id,name,generation,package,sha256) VALUES(?,?,?,0,?,?)",id,setId,name,package,digest);return new AuthorDraft(id,setId,name,0,package,digest);});}
    }
    public void Dispose(){lock(gate){if(disposed)return;db.Dispose();disposed=true;}}
}
