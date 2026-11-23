using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Arkham.NativeHost;

public sealed record LanSeatBinding(Guid Id,Guid ClientInstance,Guid GameId,Guid PlayerId,string SourceId,string Name);
public sealed record LanSeatGrant(LanSeatBinding Binding,string Token)
{
    public override string ToString()=>"LanSeatGrant { Binding = "+Binding+", Token = [redacted] }";
}

// Host-local pairing metadata, not game storage. The gateway must verify the
// selected native game/seat before approval, and verify membership on every use.
public sealed class LanIdentityStore:IDisposable
{
    const long ApplicationId=0x41484C4E;readonly object gate=new();readonly NativeSqlite db;
    public LanIdentityStore(string file)
    {
        if(!Path.IsPathFullyQualified(file))throw new ArgumentException("Absolute LAN identity path required");db=new NativeSqlite(file);
        try{
            long version=(long)db.Query("PRAGMA user_version")[0][0]!,app=(long)db.Query("PRAGMA application_id")[0][0]!;
            if(version is not(0 or 1)||version==1&&app!=ApplicationId||version==0&&app!=0)throw new InvalidDataException("Unknown LAN identity database; preserve file");
            db.ExecuteScript("PRAGMA foreign_keys=ON; PRAGMA synchronous=FULL;");if((string)db.Query("PRAGMA journal_mode=DELETE")[0][0]! !="delete"||(long)db.Query("PRAGMA synchronous")[0][0]!!=2)throw new IOException("LAN identity durability unavailable");
            db.Transaction(()=>{
                long current=(long)db.Query("PRAGMA user_version")[0][0]!;
                if(current==0){
                    if(db.Query("SELECT name FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%'").Count!=0)throw new InvalidDataException("Unrelated unversioned LAN database");
                    db.ExecuteScript("""
                    CREATE TABLE lan_host(singleton INTEGER PRIMARY KEY CHECK(singleton=1),protected_pfx BLOB NOT NULL,certificate_sha TEXT NOT NULL);
                    CREATE TABLE lan_bindings(binding_id TEXT PRIMARY KEY,client_instance TEXT NOT NULL,game_id TEXT NOT NULL,player_id TEXT NOT NULL,
                      source_id TEXT NOT NULL,name TEXT NOT NULL,token_sha TEXT NOT NULL,active INTEGER NOT NULL CHECK(active IN (0,1)));
                    CREATE UNIQUE INDEX lan_active_seat ON lan_bindings(game_id,player_id) WHERE active=1;
                    """);db.ExecuteScript($"PRAGMA application_id={ApplicationId}; PRAGMA user_version=1;");
                }else if(current!=1||(long)db.Query("PRAGMA application_id")[0][0]!!=ApplicationId)throw new InvalidDataException("LAN identity schema changed during open");return true;
            });
            if((string)db.Query("PRAGMA quick_check")[0][0]! !="ok")throw new InvalidDataException("LAN identity database consistency failure");
        }catch{db.Dispose();throw;}
    }
    static string Key(Guid id){if(id==Guid.Empty)throw new InvalidDataException("Empty LAN binding identity");return id.ToString("D");}
    static void Source(string source){if(source.Length!=83||!source.StartsWith("arkham-core-sha256:",StringComparison.Ordinal)||source[19..].Any(c=>c is not(>='0' and <='9') and not(>='a' and <='f')))throw new InvalidDataException("Invalid LAN binding source");}
    static LanSeatBinding Binding(object?[] row)=>new(Guid.Parse((string)row[0]!),Guid.Parse((string)row[1]!),Guid.Parse((string)row[2]!),Guid.Parse((string)row[3]!), (string)row[4]!, (string)row[5]!);
    // Windows Schannel rejects EphemeralKeySet. UserKeySet without
    // PersistKeySet uses an OS-managed user-private temporary key container.
    // No certificate-store installation or trust change. Dispose the returned
    // certificate after the room closes. The durable PFX remains DPAPI-only.
    static X509Certificate2 ForTls(byte[] raw)=>new(raw,(string?)null,X509KeyStorageFlags.UserKeySet);
    public X509Certificate2 HostCertificate()
    {
        lock(gate){return db.Transaction(()=>{
            var rows=db.Query("SELECT protected_pfx,certificate_sha FROM lan_host WHERE singleton=1");
            if(rows.Count==1){
                byte[] raw=LanSecrets.Unprotect((byte[])rows[0][0]!);try{var certificate=ForTls(raw);if(!certificate.HasPrivateKey||LanTls.Fingerprint(certificate)!=(string)rows[0][1]!){certificate.Dispose();throw new InvalidDataException("Host certificate identity mismatch");}return certificate;}finally{CryptographicOperations.ZeroMemory(raw);}
            }
            using var key=RSA.Create(2048);var request=new CertificateRequest("CN=arkham-native-lan",key,HashAlgorithmName.SHA256,RSASignaturePadding.Pkcs1);
            var names=new SubjectAlternativeNameBuilder();names.AddDnsName("arkham-native-lan");request.CertificateExtensions.Add(names.Build());request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false,false,0,true));
            request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection{new Oid("1.3.6.1.5.5.7.3.1")},true));
            request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature|X509KeyUsageFlags.KeyEncipherment,true));
            using var created=request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5),DateTimeOffset.UtcNow.AddYears(2));byte[] bytes=created.Export(X509ContentType.Pfx);
            try{byte[] encrypted=LanSecrets.Protect(bytes);string pin=LanTls.Fingerprint(created);db.Query("INSERT INTO lan_host(singleton,protected_pfx,certificate_sha) VALUES(1,?,?)",encrypted,pin);return ForTls(bytes);}finally{CryptographicOperations.ZeroMemory(bytes);}
        });}
    }
    public LanSeatGrant Approve(Guid client,Guid game,Guid player,string source,string name)
    {
        string clientKey=Key(client),gameKey=Key(game),playerKey=Key(player);Source(source);if(string.IsNullOrWhiteSpace(name)||name.Length>80||name.Any(char.IsControl))throw new InvalidDataException("Invalid LAN client name");
        lock(gate){return db.Transaction(()=>{
            if(db.Query("SELECT binding_id FROM lan_bindings WHERE game_id=? AND player_id=? AND active=1",gameKey,playerKey).Count!=0)throw new InvalidOperationException("该座位已有远端绑定，请先撤销旧授权");
            Guid id=Guid.NewGuid();string token=LanSecrets.NewToken();db.Query("INSERT INTO lan_bindings(binding_id,client_instance,game_id,player_id,source_id,name,token_sha,active) VALUES(?,?,?,?,?,?,?,1)",Key(id),clientKey,gameKey,playerKey,source,name,LanSecrets.TokenHash(token));
            return new LanSeatGrant(new(id,client,game,player,source,name),token);
        });}
    }
    public LanSeatBinding Resume(Guid binding,Guid client,string source,string token)
    {
        Source(source);string digest=LanSecrets.TokenHash(token);lock(gate){var rows=db.Query("SELECT binding_id,client_instance,game_id,player_id,source_id,name,token_sha,active FROM lan_bindings WHERE binding_id=?",Key(binding));
            if(rows.Count!=1)throw new UnauthorizedAccessException("Unknown or revoked LAN binding");var row=rows[0];var result=Binding(row);
            if((long)row[7]!!=1||result.ClientInstance!=client||result.SourceId!=source||!CryptographicOperations.FixedTimeEquals(Convert.FromHexString(digest),Convert.FromHexString((string)row[6]!)))throw new UnauthorizedAccessException("Unknown or revoked LAN binding");return result;}
    }
    public bool IsActive(LanSeatBinding binding)
    {
        lock(gate){var rows=db.Query("SELECT binding_id,client_instance,game_id,player_id,source_id,name FROM lan_bindings WHERE binding_id=? AND active=1",Key(binding.Id));return rows.Count==1&&Binding(rows[0])==binding;}
    }
    public IReadOnlyList<LanSeatBinding> ListActive(Guid game){lock(gate)return db.Query("SELECT binding_id,client_instance,game_id,player_id,source_id,name FROM lan_bindings WHERE game_id=? AND active=1",Key(game)).Select(Binding).ToArray();}
    public void Revoke(Guid binding){lock(gate)db.Transaction(()=>{db.Query("UPDATE lan_bindings SET active=0 WHERE binding_id=?",Key(binding));return true;});}
    public void Dispose(){lock(gate)db.Dispose();}
}
