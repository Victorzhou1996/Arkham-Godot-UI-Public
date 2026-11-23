using System.Buffers.Binary;
using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;

namespace Arkham.NativeHost;

// This is transport/protocol only. It has no reference to a rule worker,
// database, local hotseat view or UI callback. No socket/listener is started.
// A future host must bind PlayerId from its approved connection, not payload.
public abstract record LanClientFrame;
public sealed record LanHello(Guid ClientInstance,string CoreSourceId,string ArtIndexSha256,string Name):LanClientFrame;
public sealed record LanJoin:LanClientFrame;
public sealed record LanStatus:LanClientFrame;
public sealed record LanAnswer(Guid RequestId,Guid GameId,long ExpectedSequence,long ExpectedCoreRevision,JsonElement Answer):LanClientFrame;
public sealed record LanRefresh(Guid GameId):LanClientFrame;
public sealed record LanSettingsPreview(Guid GameId,long ExpectedSequence,long ExpectedCoreRevision,JsonElement Draft):LanClientFrame;
public sealed record LanLogs(Guid GameId,long ExpectedSequence,long ExpectedCoreRevision,int Page):LanClientFrame;
public sealed record LanRegions(Guid GameId,long ExpectedSequence,long ExpectedCoreRevision):LanClientFrame;
public sealed record LanReceiptQuery(LanAnswer Intent):LanClientFrame;
public sealed record LanEpicSettingsPreview(Guid EventId,long ExpectedEventRevision,Guid GroupId,long ExpectedSequence,long ExpectedCoreRevision,JsonElement Draft):LanClientFrame;
public sealed record LanEpicRegions(Guid EventId,long ExpectedEventRevision,Guid GroupId,long ExpectedSequence,long ExpectedCoreRevision):LanClientFrame;
public sealed record LanEpicLogs(Guid EventId,long ExpectedEventRevision,Guid GroupId,long ExpectedSequence,long ExpectedCoreRevision,int Page):LanClientFrame;
public sealed record LanEpicRefresh(Guid EventId):LanClientFrame;
public sealed record LanEpicAnswer(Guid RequestId,Guid EventId,long ExpectedEventRevision,Guid GroupId,long ExpectedSequence,long ExpectedCoreRevision,JsonElement Answer):LanClientFrame;
public sealed record LanEpicReceiptQuery(LanEpicAnswer Intent):LanClientFrame;
public sealed record LanResume(Guid BindingId,string Token):LanClientFrame
{
    public override string ToString()=>"LanResume { BindingId = "+BindingId+", Token = [redacted] }";
}

public static class LanProtocol
{
    public const int Version=1,MaxFrameBytes=4*1024*1024;
    static void Identity(Guid id){if(id==Guid.Empty)throw new InvalidDataException("LAN identity must not be empty");}
    static void Sha(string sha){if(sha.Length!=64||sha.Any(c=>c is not(>='0' and <='9') and not(>='a' and <='f')))throw new InvalidDataException("LAN requires lowercase SHA256");}
    static void Source(string source){if(!source.StartsWith("arkham-core-sha256:",StringComparison.Ordinal))throw new InvalidDataException("Unknown rule core identity");Sha(source[19..]);}
    static void Fields(JsonElement obj,params string[] fields)
    {
        if(obj.ValueKind!=JsonValueKind.Object)throw new InvalidDataException("LAN frame must be an object");var expected=fields.ToHashSet(StringComparer.Ordinal);
        foreach(var property in obj.EnumerateObject())if(!expected.Remove(property.Name))throw new InvalidDataException("Unknown or duplicate LAN field");
        if(expected.Count!=0)throw new InvalidDataException("Missing LAN field");
    }
    static void Unique(JsonElement element)
    {
        if(element.ValueKind==JsonValueKind.Object){var keys=new HashSet<string>(StringComparer.Ordinal);foreach(var p in element.EnumerateObject()){if(!keys.Add(p.Name))throw new InvalidDataException("Duplicate LAN JSON property");Unique(p.Value);}}
        else if(element.ValueKind==JsonValueKind.Array)foreach(var item in element.EnumerateArray())Unique(item);
    }
    static Guid Id(JsonElement obj,string field){Guid id=obj.GetProperty(field).GetGuid();Identity(id);return id;}
    static long Revision(JsonElement obj,string field){long n=obj.GetProperty(field).GetInt64();if(n<0)throw new InvalidDataException("Negative LAN revision");return n;}
    static string Text(JsonElement obj,string field){var value=obj.GetProperty(field);return value.ValueKind==JsonValueKind.String?value.GetString()!:throw new InvalidDataException("LAN text field required");}
    public static LanClientFrame DecodeClient(ReadOnlyMemory<byte> bytes)
    {
        try{return DecodeClientCore(bytes);}
        catch(Exception error) when(error is JsonException or KeyNotFoundException or InvalidOperationException or FormatException or OverflowException or ArgumentOutOfRangeException){throw new InvalidDataException("Malformed LAN client frame",error);}
    }
    static LanClientFrame DecodeClientCore(ReadOnlyMemory<byte> bytes)
    {
        if(bytes.Length is <2 or >MaxFrameBytes)throw new InvalidDataException("LAN frame exceeds bound");
        using var document=JsonDocument.Parse(bytes,new JsonDocumentOptions{MaxDepth=64});var root=document.RootElement;Unique(root);
        if(root.GetProperty("protocol").GetInt32()!=Version)throw new InvalidDataException("LAN protocol differs; update both clients");
        switch(Text(root,"kind")){
            case "join":Fields(root,"protocol","kind");return new LanJoin();
            case "status":Fields(root,"protocol","kind");return new LanStatus();
            case "hello":{
                Fields(root,"protocol","kind","clientInstance","coreSourceId","artIndexSha256","name");
                string source=Text(root,"coreSourceId"),art=Text(root,"artIndexSha256"),name=Text(root,"name");Source(source);Sha(art);
                if(string.IsNullOrWhiteSpace(name)||name.Length>80||name.Any(char.IsControl))throw new InvalidDataException("Invalid LAN display name");
                return new LanHello(Id(root,"clientInstance"),source,art,name);
            }
            case "answer":{
                Fields(root,"protocol","kind","requestId","gameId","expectedSequence","expectedCoreRevision","answer");var answer=root.GetProperty("answer");
                if(answer.ValueKind!=JsonValueKind.Object||!answer.TryGetProperty("tag",out var tag)||tag.ValueKind!=JsonValueKind.String||tag.GetString()=="Raw")throw new InvalidDataException("LAN accepts typed answers only, never raw rule messages");
                // This does NOT authorize gameplay. Native validation must still
                // use the host-bound seat and actual current question/basis.
                return new LanAnswer(Id(root,"requestId"),Id(root,"gameId"),Revision(root,"expectedSequence"),Revision(root,"expectedCoreRevision"),answer.Clone());
            }
            case "refresh":Fields(root,"protocol","kind","gameId");return new LanRefresh(Id(root,"gameId"));
            case "epicRefresh":Fields(root,"protocol","kind","eventId");return new LanEpicRefresh(Id(root,"eventId"));
            case "epicSettingsPreview":{
                Fields(root,"protocol","kind","eventId","expectedEventRevision","groupId","expectedSequence","expectedCoreRevision","draft");var draft=root.GetProperty("draft");if(draft.ValueKind!=JsonValueKind.Object)throw new InvalidDataException("Invalid Epic settings draft");
                return new LanEpicSettingsPreview(Id(root,"eventId"),Revision(root,"expectedEventRevision"),Id(root,"groupId"),Revision(root,"expectedSequence"),Revision(root,"expectedCoreRevision"),draft.Clone());
            }
            case "epicRegions":
                Fields(root,"protocol","kind","eventId","expectedEventRevision","groupId","expectedSequence","expectedCoreRevision");
                return new LanEpicRegions(Id(root,"eventId"),Revision(root,"expectedEventRevision"),Id(root,"groupId"),Revision(root,"expectedSequence"),Revision(root,"expectedCoreRevision"));
            case "epicLogs":{
                Fields(root,"protocol","kind","eventId","expectedEventRevision","groupId","expectedSequence","expectedCoreRevision","page");int page=root.GetProperty("page").GetInt32();if(page is <0 or >1000000)throw new InvalidDataException("Invalid log page");
                return new LanEpicLogs(Id(root,"eventId"),Revision(root,"expectedEventRevision"),Id(root,"groupId"),Revision(root,"expectedSequence"),Revision(root,"expectedCoreRevision"),page);
            }
            case "epicAnswer":{
                Fields(root,"protocol","kind","requestId","eventId","expectedEventRevision","groupId","expectedSequence","expectedCoreRevision","answer");var answer=root.GetProperty("answer");
                if(answer.ValueKind!=JsonValueKind.Object||!answer.TryGetProperty("tag",out var tag)||tag.ValueKind!=JsonValueKind.String||tag.GetString()=="Raw")throw new InvalidDataException("Epic accepts typed answers only");
                return new LanEpicAnswer(Id(root,"requestId"),Id(root,"eventId"),Revision(root,"expectedEventRevision"),Id(root,"groupId"),Revision(root,"expectedSequence"),Revision(root,"expectedCoreRevision"),answer.Clone());
            }
            case "epicReceipt":{
                Fields(root,"protocol","kind","intent");var intent=root.GetProperty("intent");if(Text(intent,"kind")!="epicAnswer")throw new InvalidDataException("Epic receipt requires original intent");
                return new LanEpicReceiptQuery((LanEpicAnswer)DecodeClient(JsonSerializer.SerializeToUtf8Bytes(intent)));
            }
            case "settingsPreview":{
                Fields(root,"protocol","kind","gameId","expectedSequence","expectedCoreRevision","draft");var draft=root.GetProperty("draft");
                if(draft.ValueKind!=JsonValueKind.Object)throw new InvalidDataException("LAN settings draft must be an object");
                return new LanSettingsPreview(Id(root,"gameId"),Revision(root,"expectedSequence"),Revision(root,"expectedCoreRevision"),draft.Clone());
            }
            case "logs":{
                Fields(root,"protocol","kind","gameId","expectedSequence","expectedCoreRevision","page");int page=root.GetProperty("page").GetInt32();if(page is <0 or >1000000)throw new InvalidDataException("Invalid log page");
                return new LanLogs(Id(root,"gameId"),Revision(root,"expectedSequence"),Revision(root,"expectedCoreRevision"),page);
            }
            case "regions":Fields(root,"protocol","kind","gameId","expectedSequence","expectedCoreRevision");return new LanRegions(Id(root,"gameId"),Revision(root,"expectedSequence"),Revision(root,"expectedCoreRevision"));
            case "receipt":{
                Fields(root,"protocol","kind","intent");var intent=root.GetProperty("intent");if(Text(intent,"kind")!="answer")throw new InvalidDataException("Receipt query requires the original answer envelope");
                return new LanReceiptQuery((LanAnswer)DecodeClient(JsonSerializer.SerializeToUtf8Bytes(intent)));
            }
            case "resume":{
                Fields(root,"protocol","kind","bindingId","token");string token=Text(root,"token");Sha(token);return new LanResume(Id(root,"bindingId"),token);
            }
            default:throw new InvalidDataException("LAN command not supported; no arbitrary FFI/admin endpoint exists");
        }
    }
    public static byte[] EncodeClient(LanClientFrame frame)
    {
        byte[] bytes=frame switch{
            LanJoin=>JsonSerializer.SerializeToUtf8Bytes(new{protocol=Version,kind="join"}),
            LanStatus=>JsonSerializer.SerializeToUtf8Bytes(new{protocol=Version,kind="status"}),
            LanHello h=>JsonSerializer.SerializeToUtf8Bytes(new{protocol=Version,kind="hello",clientInstance=h.ClientInstance,coreSourceId=h.CoreSourceId,artIndexSha256=h.ArtIndexSha256,name=h.Name}),
            LanAnswer a=>JsonSerializer.SerializeToUtf8Bytes(new{protocol=Version,kind="answer",requestId=a.RequestId,gameId=a.GameId,expectedSequence=a.ExpectedSequence,expectedCoreRevision=a.ExpectedCoreRevision,answer=a.Answer}),
            LanRefresh r=>JsonSerializer.SerializeToUtf8Bytes(new{protocol=Version,kind="refresh",gameId=r.GameId}),
            LanEpicSettingsPreview p=>JsonSerializer.SerializeToUtf8Bytes(new{protocol=Version,kind="epicSettingsPreview",eventId=p.EventId,expectedEventRevision=p.ExpectedEventRevision,groupId=p.GroupId,expectedSequence=p.ExpectedSequence,expectedCoreRevision=p.ExpectedCoreRevision,draft=p.Draft}),
            LanEpicRegions p=>JsonSerializer.SerializeToUtf8Bytes(new{protocol=Version,kind="epicRegions",eventId=p.EventId,expectedEventRevision=p.ExpectedEventRevision,groupId=p.GroupId,expectedSequence=p.ExpectedSequence,expectedCoreRevision=p.ExpectedCoreRevision}),
            LanEpicLogs p=>JsonSerializer.SerializeToUtf8Bytes(new{protocol=Version,kind="epicLogs",eventId=p.EventId,expectedEventRevision=p.ExpectedEventRevision,groupId=p.GroupId,expectedSequence=p.ExpectedSequence,expectedCoreRevision=p.ExpectedCoreRevision,page=p.Page}),
            LanEpicRefresh r=>JsonSerializer.SerializeToUtf8Bytes(new{protocol=Version,kind="epicRefresh",eventId=r.EventId}),
            LanEpicAnswer a=>JsonSerializer.SerializeToUtf8Bytes(new{protocol=Version,kind="epicAnswer",requestId=a.RequestId,eventId=a.EventId,expectedEventRevision=a.ExpectedEventRevision,groupId=a.GroupId,expectedSequence=a.ExpectedSequence,expectedCoreRevision=a.ExpectedCoreRevision,answer=a.Answer}),
            LanEpicReceiptQuery q=>JsonSerializer.SerializeToUtf8Bytes(new{protocol=Version,kind="epicReceipt",intent=JsonSerializer.Deserialize<JsonElement>(EncodeClient(q.Intent))}),
            LanSettingsPreview p=>JsonSerializer.SerializeToUtf8Bytes(new{protocol=Version,kind="settingsPreview",gameId=p.GameId,expectedSequence=p.ExpectedSequence,expectedCoreRevision=p.ExpectedCoreRevision,draft=p.Draft}),
            LanLogs p=>JsonSerializer.SerializeToUtf8Bytes(new{protocol=Version,kind="logs",gameId=p.GameId,expectedSequence=p.ExpectedSequence,expectedCoreRevision=p.ExpectedCoreRevision,page=p.Page}),
            LanRegions p=>JsonSerializer.SerializeToUtf8Bytes(new{protocol=Version,kind="regions",gameId=p.GameId,expectedSequence=p.ExpectedSequence,expectedCoreRevision=p.ExpectedCoreRevision}),
            LanReceiptQuery q=>JsonSerializer.SerializeToUtf8Bytes(new{protocol=Version,kind="receipt",intent=JsonSerializer.Deserialize<JsonElement>(EncodeClient(q.Intent))}),
            LanResume r=>JsonSerializer.SerializeToUtf8Bytes(new{protocol=Version,kind="resume",bindingId=r.BindingId,token=r.Token}),
            _=>throw new InvalidDataException("Unknown LAN request type")};
        _=DecodeClient(bytes);return bytes;
    }
    // Callers serialize complete writes per connection. The length prefix is
    // checked before allocating; cancellation/EOF never yields a partial frame.
    public static async Task<byte[]> ReadFrameAsync(Stream stream,CancellationToken cancellation)
    {
        byte[] header=new byte[4];await stream.ReadExactlyAsync(header,cancellation).ConfigureAwait(false);int length=BinaryPrimitives.ReadInt32BigEndian(header);
        if(length is <2 or >MaxFrameBytes)throw new InvalidDataException("LAN frame length exceeds bound");byte[] bytes=new byte[length];await stream.ReadExactlyAsync(bytes,cancellation).ConfigureAwait(false);return bytes;
    }
    public static async Task WriteFrameAsync(Stream stream,ReadOnlyMemory<byte> bytes,CancellationToken cancellation)
    {
        if(bytes.Length is <2 or >MaxFrameBytes)throw new InvalidDataException("LAN frame length exceeds bound");byte[] header=new byte[4];BinaryPrimitives.WriteInt32BigEndian(header,bytes.Length);
        await stream.WriteAsync(header,cancellation).ConfigureAwait(false);await stream.WriteAsync(bytes,cancellation).ConfigureAwait(false);await stream.FlushAsync(cancellation).ConfigureAwait(false);
    }
}

public static class LanTls
{
    public static string Fingerprint(X509Certificate2 certificate)=>Convert.ToHexString(SHA256.HashData(certificate.RawData)).ToLowerInvariant();
    public static SslStream PinnedClient(Stream transport,string approvedFingerprint)
    {
        if(approvedFingerprint.Length!=64||approvedFingerprint.Any(c=>c is not(>='0' and <='9') and not(>='a' and <='f')))throw new ArgumentException("Host fingerprint must be explicitly approved");
        byte[] pin=Convert.FromHexString(approvedFingerprint);
        return new SslStream(transport,false,(_,certificate,_,_)=>{
            if(certificate is null)return false;using var copy=new X509Certificate2(certificate);var now=DateTime.UtcNow;
            return now>=copy.NotBefore.ToUniversalTime()&&now<=copy.NotAfter.ToUniversalTime()&&CryptographicOperations.FixedTimeEquals(pin,SHA256.HashData(copy.RawData));
        });
    }
    public static Task AuthenticateClientAsync(SslStream stream,CancellationToken cancellation)=>stream.AuthenticateAsClientAsync(new SslClientAuthenticationOptions{
        TargetHost="arkham-native-lan",EnabledSslProtocols=SslProtocols.Tls12|SslProtocols.Tls13,CertificateRevocationCheckMode=X509RevocationMode.NoCheck},cancellation);
    public static Task AuthenticateHostAsync(SslStream stream,X509Certificate2 certificate,CancellationToken cancellation)
    {
        if(!certificate.HasPrivateKey)throw new ArgumentException("Host identity requires a private key");
        return stream.AuthenticateAsServerAsync(new SslServerAuthenticationOptions{ServerCertificate=certificate,ClientCertificateRequired=false,EnabledSslProtocols=SslProtocols.Tls12|SslProtocols.Tls13,CertificateRevocationCheckMode=X509RevocationMode.NoCheck},cancellation);
    }
}
