using System.Text;
using System.Text.Json;
using System.Security.Cryptography;

namespace Arkham.NativeHost;

public sealed record LanEndpoint(string Address,int Port,string Fingerprint,string SourceId,string ArtIndexSha256)
{
    public string Key=>LocalGameStore.Hash(JsonSerializer.SerializeToUtf8Bytes(new{address=Address.ToLowerInvariant(),Port,Fingerprint,SourceId}));
    public void Validate()
    {
        if(string.IsNullOrWhiteSpace(Address)||Address.Length>253||Address.Any(c=>char.IsWhiteSpace(c)||char.IsControl(c)||c is '/' or '\\')||Port is <1 or >65535)throw new InvalidDataException("宿主地址无效");
        _=LanProtocol.EncodeClient(new LanHello(Guid.NewGuid(),SourceId,ArtIndexSha256,"客户端"));
        using var pin=LanTls.PinnedClient(new MemoryStream(),Fingerprint);
    }
}
public sealed record LanSavedRoom(LanEndpoint Endpoint,LanSeatBinding Binding);
public sealed record LanPendingIntent(LanAnswer Intent,bool KnownNotCommitted);

// Remote client metadata only. Never stores rule snapshots, RNG, queue or a
// host game database. Intent rows are durable BEFORE any answer transmission.
public sealed class LanClientStore:IDisposable
{
    const long ApplicationId=0x41484C43;readonly NativeSqlite db;readonly object sync=new();
    public Guid ClientInstance {get;}
    public LanClientStore(string file)
    {
        if(!Path.IsPathFullyQualified(file))throw new ArgumentException("Absolute client metadata path required");db=new NativeSqlite(file);
        try{
            long initialVersion=(long)db.Query("PRAGMA user_version")[0][0]!,initialApp=(long)db.Query("PRAGMA application_id")[0][0]!;
            if(initialVersion is not(0 or 1)||initialVersion==0&&initialApp!=0||initialVersion==1&&initialApp!=ApplicationId)throw new InvalidDataException("Unknown client metadata schema; preserve file");
            if(initialVersion==0&&db.Query("SELECT name FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%'").Count!=0)throw new InvalidDataException("Unrelated client metadata; preserve file");
            db.ExecuteScript("PRAGMA foreign_keys=ON; PRAGMA synchronous=FULL;");if((string)db.Query("PRAGMA journal_mode=DELETE")[0][0]! !="delete"||(long)db.Query("PRAGMA synchronous")[0][0]!!=2)throw new IOException("Client metadata durability unavailable");
            db.Transaction(()=>{
                long version=(long)db.Query("PRAGMA user_version")[0][0]!,app=(long)db.Query("PRAGMA application_id")[0][0]!;
                if(version==0&&app==0){
                    if(db.Query("SELECT name FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%'").Count!=0)throw new InvalidDataException("Unrelated client metadata; preserve file");
                    db.ExecuteScript("""
                    CREATE TABLE lan_client(singleton INTEGER PRIMARY KEY CHECK(singleton=1),client_id TEXT NOT NULL);
                    CREATE TABLE lan_rooms(binding_id TEXT PRIMARY KEY,endpoint_key TEXT NOT NULL,endpoint BLOB NOT NULL,binding BLOB NOT NULL,protected_token BLOB NOT NULL);
                    CREATE TABLE lan_intents(binding_id TEXT NOT NULL,request_id TEXT NOT NULL,payload BLOB NOT NULL,payload_sha TEXT NOT NULL,
                      status TEXT NOT NULL CHECK(status IN ('pending','notCommitted','committed','abandoned')),receipt BLOB,
                      PRIMARY KEY(binding_id,request_id),FOREIGN KEY(binding_id) REFERENCES lan_rooms(binding_id));
                    CREATE UNIQUE INDEX lan_one_pending ON lan_intents(binding_id) WHERE status IN ('pending','notCommitted');
                    """);db.Query("INSERT INTO lan_client(singleton,client_id) VALUES(1,?)",Guid.NewGuid().ToString("D"));db.ExecuteScript($"PRAGMA application_id={ApplicationId}; PRAGMA user_version=1;");
                }else if(version!=1||app!=ApplicationId)throw new InvalidDataException("Unknown client metadata schema; preserve file");return true;
            });
            if((string)db.Query("PRAGMA quick_check")[0][0]! !="ok")throw new InvalidDataException("Client metadata consistency failure");
            var rows=db.Query("SELECT client_id FROM lan_client WHERE singleton=1");if(rows.Count!=1||!Guid.TryParse((string)rows[0][0]!,out var id)||id==Guid.Empty)throw new InvalidDataException("Client identity missing");ClientInstance=id;
        }catch{db.Dispose();throw;}
    }
    static string Key(Guid id)=>id!=Guid.Empty?id.ToString("D"):throw new InvalidDataException("Empty client identity");
    public LanSavedRoom? FindRoom(LanEndpoint endpoint)
    {
        endpoint.Validate();lock(sync){var rows=db.Query("SELECT endpoint,binding FROM lan_rooms WHERE endpoint_key=? ORDER BY rowid DESC LIMIT 1",endpoint.Key);if(rows.Count==0)return null;
            var saved=JsonSerializer.Deserialize<LanEndpoint>((byte[])rows[0][0]!)!;var binding=JsonSerializer.Deserialize<LanSeatBinding>((byte[])rows[0][1]!)!;
            if(saved.Key!=endpoint.Key||binding.ClientInstance!=ClientInstance||binding.SourceId!=endpoint.SourceId)throw new InvalidDataException("Saved pairing identity differs");return new(saved,binding);}
    }
    internal string Token(LanSavedRoom room)
    {
        lock(sync){var rows=db.Query("SELECT protected_token FROM lan_rooms WHERE binding_id=? AND endpoint_key=?",Key(room.Binding.Id),room.Endpoint.Key);if(rows.Count!=1)throw new InvalidDataException("Pairing credential missing");
            byte[] bytes=LanSecrets.Unprotect((byte[])rows[0][0]!);try{return Encoding.UTF8.GetString(bytes);}finally{CryptographicOperations.ZeroMemory(bytes);}}
    }
    public LanSavedRoom SaveGrant(LanEndpoint endpoint,LanSeatGrant grant)
    {
        endpoint.Validate();if(grant.Binding.ClientInstance!=ClientInstance||grant.Binding.SourceId!=endpoint.SourceId)throw new InvalidDataException("Pairing grant identity differs");_=Key(grant.Binding.GameId);_=Key(grant.Binding.PlayerId);_=LanSecrets.TokenHash(grant.Token);
        byte[] raw=Encoding.UTF8.GetBytes(grant.Token);try{byte[] secret=LanSecrets.Protect(raw);lock(sync){db.Transaction(()=>{
            var old=db.Query("SELECT endpoint_key,binding FROM lan_rooms WHERE binding_id=?",Key(grant.Binding.Id));
            if(old.Count!=0){if((string)old[0][0]! !=endpoint.Key||JsonSerializer.Deserialize<LanSeatBinding>((byte[])old[0][1]!)!=grant.Binding)throw new InvalidDataException("Pairing binding changed");return true;}
            db.Query("INSERT INTO lan_rooms(binding_id,endpoint_key,endpoint,binding,protected_token) VALUES(?,?,?,?,?)",Key(grant.Binding.Id),endpoint.Key,JsonSerializer.SerializeToUtf8Bytes(endpoint),JsonSerializer.SerializeToUtf8Bytes(grant.Binding),secret);return true;
        });}return new(endpoint,grant.Binding);}finally{CryptographicOperations.ZeroMemory(raw);}
    }
    public LanPendingIntent? Pending(LanSavedRoom room)
    {
        lock(sync){var rows=db.Query("SELECT payload,payload_sha,status FROM lan_intents WHERE binding_id=? AND status IN ('pending','notCommitted')",Key(room.Binding.Id));if(rows.Count==0)return null;if(rows.Count!=1)throw new InvalidDataException("Multiple unresolved operations");
            byte[] bytes=(byte[])rows[0][0]!;if(LocalGameStore.Hash(bytes)!=(string)rows[0][1]!)throw new InvalidDataException("Pending intent content differs");
            var intent=(LanAnswer)LanProtocol.DecodeClient(bytes);if(intent.GameId!=room.Binding.GameId)throw new InvalidDataException("Pending game differs from pairing");return new(intent,(string)rows[0][2]! == "notCommitted");}
    }
    public LanAnswer Prepare(LanSavedRoom room,LanSeatView basis,JsonElement answer)
    {
        if(basis.GameId!=room.Binding.GameId||basis.SourceId!=room.Binding.SourceId||basis.View.GetProperty("recipient").GetGuid()!=room.Binding.PlayerId)throw new InvalidDataException("Answer basis differs from approved seat");LanAnswerPolicy.Validate(answer,room.Binding.PlayerId);
        lock(sync){return db.Transaction(()=>{
            if(Pending(room) is not null)throw new InvalidOperationException("上一个操作结果待确认；请先查询回执，不要重复点击");
            var intent=new LanAnswer(Guid.NewGuid(),basis.GameId,basis.Sequence,basis.CoreRevision,answer.Clone());byte[] bytes=LanProtocol.EncodeClient(intent);
            db.Query("INSERT INTO lan_intents(binding_id,request_id,payload,payload_sha,status) VALUES(?,?,?,?,'pending')",Key(room.Binding.Id),Key(intent.RequestId),bytes,LocalGameStore.Hash(bytes));return intent;
        });}
    }
    public void AcceptOutcome(LanSavedRoom room,JsonElement packet)
    {
        string kind=packet.GetProperty("kind").GetString()!;var body=packet.GetProperty("body");if(kind is not("receipt" or "notCommitted"))throw new InvalidDataException("Not an operation outcome");
        Guid request=body.GetProperty("requestId").GetGuid();lock(sync){db.Transaction(()=>{
            var pending=Pending(room)??throw new InvalidOperationException("No pending operation");if(pending.Intent.RequestId!=request)throw new InvalidDataException("Response belongs to another request");
            if(kind=="receipt"){
                long committed=body.GetProperty("committedSequence").GetInt64(),current=body.GetProperty("currentSequence").GetInt64();_=body.GetProperty("replayed").GetBoolean();
                if(committed!=checked(pending.Intent.ExpectedSequence+1)||current<committed)throw new InvalidDataException("Invalid durable receipt revision");
            }
            db.Query("UPDATE lan_intents SET status=?,receipt=? WHERE binding_id=? AND request_id=?",kind=="receipt"?"committed":"notCommitted",JsonSerializer.SerializeToUtf8Bytes(packet),Key(room.Binding.Id),Key(request));return true;
        });}
    }
    public void CancelKnownUncommitted(LanSavedRoom room)
    {
        lock(sync)db.Transaction(()=>{var pending=Pending(room)??throw new InvalidOperationException("No pending operation");if(!pending.KnownNotCommitted)throw new InvalidOperationException("提交结果未知，必须先向宿主查询回执");db.Query("UPDATE lan_intents SET status='abandoned' WHERE binding_id=? AND request_id=?",Key(room.Binding.Id),Key(pending.Intent.RequestId));return true;});
    }
    public LanAnswer MarkRetryStarted(LanSavedRoom room)
    {
        lock(sync)return db.Transaction(()=>{
            var pending=Pending(room)??throw new InvalidOperationException("No pending operation");if(!pending.KnownNotCommitted)throw new InvalidOperationException("必须先查询操作回执");
            // A second lost response is uncertain again. Clear the previous
            // negative query result durably BEFORE another byte is sent.
            db.Query("UPDATE lan_intents SET status='pending',receipt=NULL WHERE binding_id=? AND request_id=?",Key(room.Binding.Id),Key(pending.Intent.RequestId));return pending.Intent;
        });
    }
    public void Dispose(){lock(sync)db.Dispose();}
}
