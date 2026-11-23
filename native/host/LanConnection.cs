using System.Net.Security;
using System.Net.Sockets;
using System.Text.Json;

namespace Arkham.NativeHost;

// One request/response at a time. No automatic reconnect or answer replay.
// The higher-level client persists the original intent BEFORE calling this.
public sealed class LanConnection:IAsyncDisposable
{
    readonly TcpClient socket;readonly SslStream tls;readonly SemaphoreSlim gate=new(1,1);bool disposed;
    public JsonElement HostHello {get;private set;}
    LanConnection(TcpClient socket,SslStream tls){this.socket=socket;this.tls=tls;}
    public static async Task<LanConnection> ConnectAsync(string address,int port,string approvedFingerprint,LanHello hello,CancellationToken cancellation)
    {
        if(string.IsNullOrWhiteSpace(address)||port is <1 or >65535)throw new ArgumentException("明确的宿主地址和端口为必需");
        var socket=new TcpClient();SslStream? tls=null;try{
            await socket.ConnectAsync(address,port,cancellation).ConfigureAwait(false);socket.NoDelay=true;
            tls=LanTls.PinnedClient(socket.GetStream(),approvedFingerprint);await LanTls.AuthenticateClientAsync(tls,cancellation).ConfigureAwait(false);
            var connection=new LanConnection(socket,tls);connection.HostHello=await connection.ExchangeAsync(hello,cancellation).ConfigureAwait(false);
            if(connection.HostHello.GetProperty("kind").GetString()!="hello"||connection.HostHello.GetProperty("body").GetProperty("coreSourceId").GetString()!=hello.CoreSourceId)throw new InvalidDataException("宿主规则身份不一致");
            return connection;
        }catch{tls?.Dispose();socket.Dispose();throw;}
    }
    public async Task<JsonElement> ExchangeAsync(LanClientFrame frame,CancellationToken cancellation)
    {
        await gate.WaitAsync(cancellation).ConfigureAwait(false);try{
            ObjectDisposedException.ThrowIf(disposed,this);
            await LanProtocol.WriteFrameAsync(tls,LanProtocol.EncodeClient(frame),cancellation).ConfigureAwait(false);
            byte[] bytes=await LanProtocol.ReadFrameAsync(tls,cancellation).ConfigureAwait(false);
            using var parsed=JsonDocument.Parse(bytes,new JsonDocumentOptions{MaxDepth=64});var packet=parsed.RootElement;
            if(packet.ValueKind!=JsonValueKind.Object||packet.EnumerateObject().Count()!=3||packet.EnumerateObject().Select(p=>p.Name).Distinct(StringComparer.Ordinal).Count()!=3||packet.GetProperty("protocol").GetInt32()!=LanProtocol.Version||packet.GetProperty("kind").ValueKind!=JsonValueKind.String||!packet.TryGetProperty("body",out _))throw new InvalidDataException("无效宿主响应");
            return packet.Clone();
        }catch{disposed=true;tls.Dispose();socket.Dispose();throw;}finally{gate.Release();}
    }
    public async ValueTask DisposeAsync()
    {
        // Closing the transport cancels a blocked read; then wait until its
        // serialized exchange is terminal. Never keep a hidden reconnect loop.
        disposed=true;socket.Dispose();await gate.WaitAsync().ConfigureAwait(false);try{await tls.DisposeAsync().ConfigureAwait(false);}finally{gate.Release();}
    }
}
