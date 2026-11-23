using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;

namespace Arkham.NativeHost;

// Created only by explicit host action. In-process TLS, no HTTP service,
// automatic firewall changes, discovery broadcasts or background restart.
public sealed class LanHost:IAsyncDisposable
{
    readonly ILanRoomGateway gateway;readonly X509Certificate2 certificate;
    readonly TcpListener listener;readonly CancellationTokenSource stopping=new();
    readonly object sync=new();readonly HashSet<Task> clients=new();
    readonly SemaphoreSlim slots=new(16,16);readonly Task accepting;Task? closeTask;
    public IPEndPoint Endpoint=>(IPEndPoint)listener.LocalEndpoint;
    public string Fingerprint=>LanTls.Fingerprint(certificate);
    // Host-local diagnostics only. Never encoded into a remote response.
    public event Action<Exception>? LocalFault;
    void Fault(Exception error){try{LocalFault?.Invoke(error);}catch{/* A local diagnostic subscriber cannot own transport lifetime. */}}
    public LanHost(IPAddress bindAddress,int port,ILanRoomGateway gateway,X509Certificate2 certificate)
    {
        if(bindAddress.AddressFamily!=AddressFamily.InterNetwork||port is <0 or >65535||!certificate.HasPrivateKey)throw new ArgumentException("明确的 IPv4 地址、端口和宿主身份为必需");
        this.gateway=gateway;this.certificate=certificate;listener=new TcpListener(bindAddress,port);
        listener.Server.ExclusiveAddressUse=true;listener.Start(16);accepting=AcceptLoop();
    }
    async Task AcceptLoop()
    {
        try{while(!stopping.IsCancellationRequested){
            var socket=await listener.AcceptTcpClientAsync(stopping.Token).ConfigureAwait(false);
            if(!slots.Wait(0)){socket.Dispose();continue;}
            // Hold the lock while registering, so normal shutdown cannot miss
            // a just-accepted task. The task removes itself only when terminal.
            lock(sync){Task task=Task.Run(()=>Serve(socket));clients.Add(task);_ = RemoveWhenDone(task);}
        }}catch(OperationCanceledException)when(stopping.IsCancellationRequested){}
        catch(SocketException)when(stopping.IsCancellationRequested){}
        catch(ObjectDisposedException)when(stopping.IsCancellationRequested){}
    }
    async Task RemoveWhenDone(Task task){try{await task.ConfigureAwait(false);}catch(Exception error){Fault(error);}finally{lock(sync)clients.Remove(task);}}
    async Task Serve(TcpClient socket)
    {
        Guid connection=Guid.Empty;
        try{
            using(socket){socket.NoDelay=true;using var tls=new System.Net.Security.SslStream(socket.GetStream(),false);
                using(var timeout=CancellationTokenSource.CreateLinkedTokenSource(stopping.Token)){
                    timeout.CancelAfter(TimeSpan.FromSeconds(10));await LanTls.AuthenticateHostAsync(tls,certificate,timeout.Token).ConfigureAwait(false);
                }
                var first=await Read(tls).ConfigureAwait(false);
                if(LanProtocol.DecodeClient(first) is not LanHello hello)throw new InvalidDataException("First packet must be hello");
                connection=await gateway.ConnectAsync(hello).ConfigureAwait(false);
                await Write(tls,gateway.HelloPacket()).ConfigureAwait(false);
                while(!stopping.IsCancellationRequested){
                    var frame=LanProtocol.DecodeClient(await Read(tls).ConfigureAwait(false));JsonElement response;
                    try{response=await gateway.HandleAsync(connection,frame).ConfigureAwait(false);}
                    catch(Exception error){response=Error(error);}
                    await Write(tls,response).ConfigureAwait(false);
                }
            }
        }catch(Exception error) when(connection!=Guid.Empty||stopping.IsCancellationRequested){
            // Private exception details stay inside the host. A lost response
            // is resolved through the original durable request, never replayed
            // here by generating a fresh request ID.
            if(!stopping.IsCancellationRequested)Fault(error);
        }catch(Exception error){Fault(error);/* Unapproved/failed handshakes closed; details only on host. */}
        finally{try{if(connection!=Guid.Empty)await gateway.DisconnectAsync(connection).ConfigureAwait(false);}finally{slots.Release();}}
    }
    async Task<byte[]> Read(Stream stream)
    {
        using var timeout=CancellationTokenSource.CreateLinkedTokenSource(stopping.Token);timeout.CancelAfter(TimeSpan.FromSeconds(70));
        return await LanProtocol.ReadFrameAsync(stream,timeout.Token).ConfigureAwait(false);
    }
    Task Write(Stream stream,JsonElement packet)=>LanProtocol.WriteFrameAsync(stream,JsonSerializer.SerializeToUtf8Bytes(packet),stopping.Token);
    static JsonElement Error(Exception error)=>JsonSerializer.SerializeToElement(new{
        protocol=LanProtocol.Version,kind="error",body=new{code=error switch{
            StaleLocalState=>"stale",UnauthorizedAccessException=>"forbidden",
            InvalidDataException or JsonException or KeyNotFoundException or FormatException=>"invalid",
            _=>"hostFailure"}}});
    public ValueTask DisposeAsync()
    {lock(sync)return new ValueTask(closeTask??=Close());}
    async Task Close()
    {
        Exception? acceptError=null,clientError=null;stopping.Cancel();listener.Stop();
        try{await accepting.ConfigureAwait(false);}catch(Exception error){acceptError=error;}
        finally{
            Task[] remaining;lock(sync)remaining=clients.ToArray();
            try{await Task.WhenAll(remaining).ConfigureAwait(false);}catch(Exception error){clientError=error;}
        }
        // Certificate ownership remains with the caller/identity store scope.
        if(acceptError is not null&&clientError is not null)throw new AggregateException(acceptError,clientError);
        if(acceptError is not null)throw new IOException("房间接入循环已停止；全部连接已结束",acceptError);
        if(clientError is not null)throw new IOException("房间已停止；连接清理遇到错误",clientError);
    }
}
