using System.Text.Json;
namespace Arkham.NativeHost;

// The identity store MUST be an Epic-only database. Its GameId column is an
// EventScopeId here; it never grants a fixed group or a different player.
public sealed class LanEpicGateway:ILanRoomGateway
{
    sealed class Peer(LanHello hello){public readonly LanHello Hello=hello;public bool Requested;public LanSeatBinding? Binding;public LanSeatGrant? Grant;}
    readonly ILanEpicAuthority authority;readonly LanIdentityStore identities;readonly string source,art;
    readonly SemaphoreSlim gate=new(1,1);readonly Dictionary<Guid,Peer> peers=new();
    public LanEpicGateway(ILanEpicAuthority authority,LanIdentityStore epicIdentities,string source,string art)
    {_=LanProtocol.EncodeClient(new LanHello(Guid.NewGuid(),source,art,"宿主"));this.authority=authority;identities=epicIdentities;this.source=source;this.art=art;}
    static JsonElement Packet(string kind,object? body=null)=>JsonSerializer.SerializeToElement(new{protocol=LanProtocol.Version,kind,body});
    public JsonElement HelloPacket()=>Packet("hello",new{coreSourceId=source,artIndexSha256=art,roomKind="epic"});
    public async Task<Guid> ConnectAsync(LanHello hello)
    {_=LanProtocol.EncodeClient(hello);await gate.WaitAsync().ConfigureAwait(false);try{if(hello.CoreSourceId!=source)throw new InvalidDataException("Rule source mismatch");if(peers.Count>=16)throw new InvalidOperationException("Room is full");Guid id=Guid.NewGuid();peers.Add(id,new(hello));return id;}finally{gate.Release();}}
    public async Task DisconnectAsync(Guid connection){await gate.WaitAsync().ConfigureAwait(false);try{peers.Remove(connection);}finally{gate.Release();}}
    public async Task<IReadOnlyList<LanPendingPeer>> PendingAsync(){await gate.WaitAsync().ConfigureAwait(false);try{return peers.Where(p=>p.Value.Requested&&p.Value.Binding is null).Select(p=>new LanPendingPeer(p.Key,p.Value.Hello.Name,p.Value.Hello.ArtIndexSha256!=art)).ToArray();}finally{gate.Release();}}
    Peer Get(Guid id)=>peers.TryGetValue(id,out var peer)?peer:throw new UnauthorizedAccessException("Unknown connection");
    async Task<LanEpicSeatView> View(Guid eventId,Guid player)
    {var view=await authority.NetworkEpicViewAsync(eventId,player).ConfigureAwait(false);if(view.EventId!=eventId||view.PlayerId!=player||view.SourceId!=source)throw new InvalidDataException("Epic view binding mismatch");return view;}
    public async Task ApproveAsync(Guid connection,Guid eventId,Guid player)
    {await gate.WaitAsync().ConfigureAwait(false);try{var peer=Get(connection);if(!peer.Requested||peer.Binding is not null)throw new InvalidOperationException("No pending request");_=await View(eventId,player).ConfigureAwait(false);var grant=identities.Approve(peer.Hello.ClientInstance,eventId,player,source,peer.Hello.Name);peer.Binding=grant.Binding;peer.Grant=grant;}finally{gate.Release();}}
    public async Task RevokeAsync(Guid binding)
    {await gate.WaitAsync().ConfigureAwait(false);try{identities.Revoke(binding);foreach(var peer in peers.Values.Where(p=>p.Binding?.Id==binding)){peer.Binding=null;peer.Grant=null;peer.Requested=false;}}finally{gate.Release();}}
    LanSeatBinding Active(Peer peer,Guid? eventId=null)
    {var b=peer.Binding??throw new UnauthorizedAccessException("Await host approval");if(!identities.IsActive(b)||b.SourceId!=source||b.ClientInstance!=peer.Hello.ClientInstance||(eventId is Guid id&&b.GameId!=id))throw new UnauthorizedAccessException("Epic binding revoked or mismatched");return b;}
    static object Receipt(EpicCommitReceipt r)=>new{eventId=r.EventId,requestId=r.RequestId,committedEventRevision=r.CommittedRevision,currentEventRevision=r.CurrentRevision,replayed=r.Replayed};
    public async Task<JsonElement> HandleAsync(Guid connection,LanClientFrame frame)
    {
        await gate.WaitAsync().ConfigureAwait(false);try{
            var peer=Get(connection);
            switch(frame){
                case LanJoin:if(peer.Binding is not null)throw new InvalidOperationException("Already bound");peer.Requested=true;return Packet("pending");
                case LanResume resume:{
                    var b=identities.Resume(resume.BindingId,peer.Hello.ClientInstance,source,resume.Token);
                    if(peers.Any(p=>p.Key!=connection&&p.Value.Binding?.Id==b.Id)||peer.Binding is not null&&peer.Binding.Id!=b.Id)throw new UnauthorizedAccessException("Binding already connected");
                    // Authenticate history even after movement/removal. Only a
                    // receipt query may proceed without current membership.
                    peer.Binding=b;peer.Grant=null;return Packet("epicResumed",b);
                }
                case LanStatus:{
                    if(peer.Binding is null)return Packet(peer.Requested?"pending":"unpaired");var b=Active(peer);var view=await View(b.GameId,b.PlayerId).ConfigureAwait(false);
                    return peer.Grant is{} grant?Packet("epicPaired",new{binding=grant.Binding,token=grant.Token,view,artDiffers=peer.Hello.ArtIndexSha256!=art}):Packet("epicView",view);
                }
                case LanEpicRefresh refresh:{var b=Active(peer,refresh.EventId);return Packet("epicView",await View(b.GameId,b.PlayerId).ConfigureAwait(false));}
                case LanEpicSettingsPreview p:{var b=Active(peer,p.EventId);return Packet("settingsPreview",await authority.NetworkEpicReadAsync(b.PlayerId,p).ConfigureAwait(false));}
                case LanEpicRegions p:{var b=Active(peer,p.EventId);return Packet("regions",await authority.NetworkEpicReadAsync(b.PlayerId,p).ConfigureAwait(false));}
                case LanEpicLogs p:{var b=Active(peer,p.EventId);return Packet("logs",await authority.NetworkEpicReadAsync(b.PlayerId,p).ConfigureAwait(false));}
                case LanEpicAnswer answer:{
                    var b=Active(peer,answer.EventId);LanAnswerPolicy.Validate(answer.Answer,b.PlayerId);
                    // Controller resolves receipt BEFORE current membership,
                    // so a committed pre-swap request is never rerun elsewhere.
                    var receipt=await authority.NetworkEpicAnswerAsync(b.Id,b.PlayerId,answer).ConfigureAwait(false);return Packet("epicReceipt",Receipt(receipt));
                }
                case LanEpicReceiptQuery query:{var b=Active(peer,query.Intent.EventId);LanAnswerPolicy.Validate(query.Intent.Answer,b.PlayerId);var receipt=await authority.NetworkEpicReceiptAsync(b.Id,b.PlayerId,query.Intent).ConfigureAwait(false);return receipt is null?Packet("notCommitted",new{requestId=query.Intent.RequestId}):Packet("epicReceipt",Receipt(receipt));}
                default:throw new UnauthorizedAccessException("Epic group room does not expose ordinary game or organizer commands");
            }
        }finally{gate.Release();}
    }
}
