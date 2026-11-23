using System.Text.Json;

namespace Arkham.NativeHost;

public sealed record LanPendingPeer(Guid ConnectionId,string Name,bool ArtDiffers);

// Host-owned authority: connection ID, game and player are never accepted from
// an unapproved request. All operations and revocation are serialized here.
public sealed class LanRoomGateway:ILanRoomGateway
{
    sealed class Peer(LanHello hello)
    {
        public readonly LanHello Hello=hello;
        public bool Requested;
        public LanSeatBinding? Binding;
        public LanSeatGrant? Grant;
    }
    readonly ILanGameAuthority authority;readonly LanIdentityStore identities;
    readonly string source,art;readonly SemaphoreSlim gate=new(1,1);
    readonly Dictionary<Guid,Peer> peers=new();
    public LanRoomGateway(ILanGameAuthority authority,LanIdentityStore identities,string source,string art)
    {
        // Reuse the exact wire identity validator, rather than a weaker path.
        _=LanProtocol.EncodeClient(new LanHello(Guid.NewGuid(),source,art,"宿主"));
        this.authority=authority;this.identities=identities;this.source=source;this.art=art;
    }
    static JsonElement Packet(string kind,object? body=null)=>JsonSerializer.SerializeToElement(new{protocol=LanProtocol.Version,kind,body});
    public async Task<Guid> ConnectAsync(LanHello hello)
    {
        _=LanProtocol.EncodeClient(hello);await gate.WaitAsync().ConfigureAwait(false);try{
            if(hello.CoreSourceId!=source)throw new InvalidDataException("规则核心版本不同，请更新双方客户端");
            if(peers.Count>=16)throw new InvalidOperationException("房间连接数已满");
            Guid id=Guid.NewGuid();peers.Add(id,new Peer(hello));return id;
        }finally{gate.Release();}
    }
    public JsonElement HelloPacket()=>Packet("hello",new{coreSourceId=source,artIndexSha256=art});
    public async Task DisconnectAsync(Guid connection)
    {await gate.WaitAsync().ConfigureAwait(false);try{peers.Remove(connection);}finally{gate.Release();}}
    public async Task<IReadOnlyList<LanPendingPeer>> PendingAsync()
    {
        await gate.WaitAsync().ConfigureAwait(false);try{return peers.Where(p=>p.Value.Requested&&p.Value.Binding is null)
            .Select(p=>new LanPendingPeer(p.Key,p.Value.Hello.Name,p.Value.Hello.ArtIndexSha256!=art)).ToArray();}
        finally{gate.Release();}
    }
    public async Task ApproveAsync(Guid connection,Guid game,Guid player)
    {
        await gate.WaitAsync().ConfigureAwait(false);try{
            var peer=Get(connection);if(!peer.Requested||peer.Binding is not null)throw new InvalidOperationException("没有待确认的加入请求");
            // The native finite projection rejects a non-member seat. No raw
            // save or hotseat view is inspected or delivered by this gateway.
            _=await ReadView(game,player).ConfigureAwait(false);
            var grant=identities.Approve(peer.Hello.ClientInstance,game,player,source,peer.Hello.Name);
            peer.Binding=grant.Binding;peer.Grant=grant;
        }finally{gate.Release();}
    }
    public async Task RevokeAsync(Guid binding)
    {
        await gate.WaitAsync().ConfigureAwait(false);try{
            identities.Revoke(binding);foreach(var peer in peers.Values.Where(p=>p.Binding?.Id==binding)){peer.Binding=null;peer.Grant=null;peer.Requested=false;}
        }finally{gate.Release();}
    }
    Peer Get(Guid connection)=>peers.TryGetValue(connection,out var peer)?peer:throw new UnauthorizedAccessException("连接不存在或已关闭");
    async Task<LanSeatView> ReadView(Guid game,Guid player)
    {
        var view=await authority.NetworkViewAsync(game,player).ConfigureAwait(false);
        if(view.GameId!=game||view.SourceId!=source||view.View.GetProperty("recipient").GetGuid()!=player||view.View.GetProperty("scope").GetString()!="network-seat")throw new InvalidDataException("座位视图来源不一致");
        return view;
    }
    LanSeatBinding Active(Peer peer,Guid? game=null)
    {
        var binding=peer.Binding??throw new UnauthorizedAccessException("请等待宿主分配座位");
        if(!identities.IsActive(binding)||binding.SourceId!=source||binding.ClientInstance!=peer.Hello.ClientInstance||game is Guid requested&&binding.GameId!=requested)throw new UnauthorizedAccessException("座位授权已失效或游戏不匹配");
        return binding;
    }
    public async Task<JsonElement> HandleAsync(Guid connection,LanClientFrame frame)
    {
        // Cancellation of a transport never cancels an in-flight durable rule
        // commit; the original UUID and exact intent resolve its outcome later.
        await gate.WaitAsync().ConfigureAwait(false);try{
            var peer=Get(connection);
            switch(frame){
                case LanJoin:
                    if(peer.Binding is not null)throw new InvalidOperationException("该连接已有座位");peer.Requested=true;return Packet("pending");
                case LanResume resume:{
                    var binding=identities.Resume(resume.BindingId,peer.Hello.ClientInstance,source,resume.Token);
                    // Reconnection authenticates the historical binding. A
                    // retired/removed seat may query its own accepted receipt;
                    // new views/answers still require current native membership.
                    if(peers.Any(p=>p.Key!=connection&&p.Value.Binding?.Id==binding.Id))throw new UnauthorizedAccessException("该座位仍有连接，请先关闭旧连接");
                    if(peer.Binding is not null&&peer.Binding.Id!=binding.Id)throw new UnauthorizedAccessException("不能更换已绑定座位");
                    peer.Binding=binding;peer.Grant=null;return Packet("resumed",binding);
                }
                case LanStatus:{
                    if(peer.Binding is null)return Packet(peer.Requested?"pending":"unpaired");
                    var binding=Active(peer);var view=await ReadView(binding.GameId,binding.PlayerId).ConfigureAwait(false);
                    if(peer.Grant is {} grant)return Packet("paired",new{binding=grant.Binding,token=grant.Token,view,artDiffers=peer.Hello.ArtIndexSha256!=art});
                    return Packet("view",view);
                }
                case LanRefresh refresh:{
                    var binding=Active(peer,refresh.GameId);return Packet("view",await ReadView(binding.GameId,binding.PlayerId).ConfigureAwait(false));
                }
                case LanSettingsPreview preview:{
                    var binding=Active(peer,preview.GameId);
                    var result=await authority.NetworkSettingsPreviewAsync(binding.GameId,preview.ExpectedSequence,preview.ExpectedCoreRevision,binding.PlayerId,preview.Draft).ConfigureAwait(false);
                    return Packet("settingsPreview",result);
                }
                case LanLogs logs:{
                    var binding=Active(peer,logs.GameId);var result=await authority.NetworkLogsAsync(binding.GameId,logs.ExpectedSequence,logs.ExpectedCoreRevision,binding.PlayerId,logs.Page).ConfigureAwait(false);
                    _=Active(peer,logs.GameId);return Packet("logs",result);
                }
                case LanRegions regions:{
                    var binding=Active(peer,regions.GameId);var result=await authority.NetworkRegionsAsync(binding.GameId,regions.ExpectedSequence,regions.ExpectedCoreRevision,binding.PlayerId).ConfigureAwait(false);
                    _=Active(peer,regions.GameId);return Packet("regions",result);
                }
                case LanAnswer answer:{
                    var binding=Active(peer,answer.GameId);
                    LanAnswerPolicy.Validate(answer.Answer,binding.PlayerId);
                    _=await ReadView(binding.GameId,binding.PlayerId).ConfigureAwait(false);
                    var receipt=await authority.NetworkAnswerAsync(binding.Id,binding.GameId,answer.RequestId,answer.ExpectedSequence,answer.ExpectedCoreRevision,binding.PlayerId,answer.Answer).ConfigureAwait(false);
                    return Packet("receipt",Receipt(receipt));
                }
                case LanReceiptQuery query:{
                    var intent=query.Intent;var binding=Active(peer,intent.GameId);
                    LanAnswerPolicy.Validate(intent.Answer,binding.PlayerId);
                    // A retired seat may resolve its own accepted request, but
                    // does not regain a view or permission to submit a new one.
                    var receipt=await authority.NetworkReceiptAsync(binding.Id,binding.GameId,intent.RequestId,intent.ExpectedSequence,intent.ExpectedCoreRevision,binding.PlayerId,intent.Answer).ConfigureAwait(false);
                    return receipt is null?Packet("notCommitted",new{requestId=intent.RequestId}):Packet("receipt",Receipt(receipt));
                }
                default:throw new UnauthorizedAccessException("房间不提供此操作");
            }
        }finally{gate.Release();}
    }
    // Explicit receipt fields only. In particular do not return game Save,
    // state SHA, delivery logs or any future fields added to CommitReceipt.
    static object Receipt(CommitReceipt receipt)=>new{requestId=receipt.RequestId,committedSequence=receipt.CommittedSequence,currentSequence=receipt.CurrentSequence,replayed=receipt.Replayed};
}
