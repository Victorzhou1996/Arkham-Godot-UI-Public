using System.Text.Json;

namespace Arkham.NativeHost;

// Remote session has no NativeCoreWorker or game storage. It receives only
// finite seat views; a reconnect queries a pending UUID before enabling answers.
public sealed class LanRemoteSession:ILanPlaySession
{
    readonly LanClientStore store;readonly LanConnection connection;readonly LanEndpoint endpoint;
    readonly SemaphoreSlim gate=new(1,1);bool disposed;
    public LanSavedRoom? Room {get;private set;}
    public LanSeatView? View {get;private set;}
    public string Status {get;private set;}="等待宿主分配座位";
    public bool Connected {get;private set;}=true;
    public LanPendingIntent? Pending=>Room is {} room?store.Pending(room):null;
    LanPendingStatus? ILanPlaySession.Pending=>Pending is {} p?new(p.Intent.RequestId,p.KnownNotCommitted):null;
    public bool CanAnswer=>Connected&&View is not null&&Room is not null&&Pending is null;
    LanRemoteSession(LanClientStore store,LanConnection connection,LanEndpoint endpoint){this.store=store;this.connection=connection;this.endpoint=endpoint;}
    static string Kind(JsonElement packet)=>packet.GetProperty("kind").GetString()!;
    static void Accepted(JsonElement packet,string expected)
    {
        if(Kind(packet)==expected)return;
        if(Kind(packet)=="error"){
            string code=packet.GetProperty("body").GetProperty("code").GetString()??"";
            throw new InvalidOperationException(code switch{"stale"=>"游戏已经变化，请先刷新；旧操作不会自动重发", "forbidden"=>"宿主未授权此操作或该座位已失效", "invalid"=>"宿主拒绝了无效操作", _=>"宿主无法完成此操作，请查询回执或检查宿主状态"});
        }
        throw new InvalidDataException("非预期的房间响应");
    }
    public static async Task<LanRemoteSession> ConnectAsync(LanClientStore store,LanEndpoint endpoint,string displayName,CancellationToken cancellation)
    {
        endpoint.Validate();var hello=new LanHello(store.ClientInstance,endpoint.SourceId,endpoint.ArtIndexSha256,displayName);
        var connection=await LanConnection.ConnectAsync(endpoint.Address,endpoint.Port,endpoint.Fingerprint,hello,cancellation).ConfigureAwait(false);
        var session=new LanRemoteSession(store,connection,endpoint);try{
            if(connection.HostHello.GetProperty("body").TryGetProperty("roomKind",out var kind)&&kind.GetString()=="epic")throw new InvalidDataException("请选择多组事件房间；普通与事件授权不混用");
            if(store.FindRoom(endpoint) is {} room){
                Accepted(await connection.ExchangeAsync(new LanResume(room.Binding.Id,store.Token(room)),cancellation).ConfigureAwait(false),"resumed");session.Room=room;
                session.Status="已恢复座位；正在确认上次操作";
                if(store.Pending(room) is {} pending){var packet=await connection.ExchangeAsync(new LanReceiptQuery(pending.Intent),cancellation).ConfigureAwait(false);session.Outcome(packet,room);}
            }else Accepted(await connection.ExchangeAsync(new LanJoin(),cancellation).ConfigureAwait(false),"pending");
            return session;
        }catch{await connection.DisposeAsync().ConfigureAwait(false);throw;}
    }
    void SetView(JsonElement packet)
    {
        var view=LanSeatView.FromWire(packet);var room=Room??throw new InvalidDataException("视图之前未获得座位授权");
        if(view.GameId!=room.Binding.GameId||view.SourceId!=endpoint.SourceId||view.View.GetProperty("recipient").GetGuid()!=room.Binding.PlayerId||View is{} old&&view.Sequence<old.Sequence)throw new InvalidDataException("过期或其他座位的视图");
        View=view;Status=Pending is null?"已连接 · 只显示当前座位可见信息":"上次操作结果待确认，不能提交新操作";
    }
    void Outcome(JsonElement packet,LanSavedRoom room)
    {
        if(Kind(packet) is "receipt" or "notCommitted"){
            store.AcceptOutcome(room,packet);Status=Kind(packet)=="receipt"?"上次操作已保存":"宿主未提交上次操作；可明确重试或取消";return;
        }
        Accepted(packet,"receipt");
    }
    public async Task PollAsync(CancellationToken cancellation)
    {
        await gate.WaitAsync(cancellation).ConfigureAwait(false);try{
            ObjectDisposedException.ThrowIf(disposed,this);var packet=await connection.ExchangeAsync(Room is{} current?new LanRefresh(current.Binding.GameId):new LanStatus(),cancellation).ConfigureAwait(false);
            if(Room is null&&Kind(packet)=="pending"){Status="等待宿主分配座位";return;}
            if(Room is null&&Kind(packet)=="paired"){
                var body=packet.GetProperty("body");var binding=body.GetProperty("binding").Deserialize<LanSeatBinding>()??throw new InvalidDataException("无效座位授权");
                Room=store.SaveGrant(endpoint,new(binding,body.GetProperty("token").GetString()!));
                // Pairing credential is durable before acknowledging it. A
                // process exit here can resume; the host does not reassign us.
                Accepted(await connection.ExchangeAsync(new LanResume(binding.Id,store.Token(Room)),cancellation).ConfigureAwait(false),"resumed");SetView(body.GetProperty("view"));return;
            }
            Accepted(packet,"view");SetView(packet.GetProperty("body"));
        }catch(IOException){Connected=false;Status="连接中断；保留待确认操作，请恢复连接";throw;}
        finally{gate.Release();}
    }
    public async Task SubmitAsync(JsonElement answer,CancellationToken cancellation)
    {
        await gate.WaitAsync(cancellation).ConfigureAwait(false);try{
            ObjectDisposedException.ThrowIf(disposed,this);if(!CanAnswer)throw new InvalidOperationException("当前不能提交；请先连接并确认上次操作");var room=Room!;var intent=store.Prepare(room,View!,answer);
            var packet=await connection.ExchangeAsync(intent,cancellation).ConfigureAwait(false);Outcome(packet,room);
            var fresh=await connection.ExchangeAsync(new LanRefresh(room.Binding.GameId),cancellation).ConfigureAwait(false);Accepted(fresh,"view");SetView(fresh.GetProperty("body"));
        }catch(IOException){Connected=false;Status="连接中断；原操作编号已保存，请恢复连接查询";throw;}
        finally{gate.Release();}
    }
    public async Task<JsonElement> PreviewSettingsAsync(LanSeatView basis,JsonElement draft,CancellationToken cancellation)
    {
        await gate.WaitAsync(cancellation).ConfigureAwait(false);try{
            ObjectDisposedException.ThrowIf(disposed,this);var room=Room??throw new InvalidOperationException("尚未分配座位");
            bool SameBasis()=>Connected&&View is{} current&&current.GameId==basis.GameId&&current.Sequence==basis.Sequence&&current.CoreRevision==basis.CoreRevision&&current.SourceId==basis.SourceId&&current.View.GetProperty("recipient").GetGuid()==room.Binding.PlayerId&&basis.View.GetProperty("recipient").GetGuid()==room.Binding.PlayerId;
            if(!SameBasis()||Pending is not null)throw new InvalidOperationException("设置草稿已过期或有操作结果待确认");
            var packet=await connection.ExchangeAsync(new LanSettingsPreview(room.Binding.GameId,basis.Sequence,basis.CoreRevision,draft.Clone()),cancellation).ConfigureAwait(false);Accepted(packet,"settingsPreview");var body=packet.GetProperty("body");
            if(!SameBasis()||body.GetProperty("gameId").GetGuid()!=basis.GameId||body.GetProperty("sequence").GetInt64()!=basis.Sequence||body.GetProperty("coreRevision").GetInt64()!=basis.CoreRevision||body.GetProperty("playerId").GetGuid()!=room.Binding.PlayerId||body.GetProperty("sourceId").GetString()!=basis.SourceId||body.GetProperty("preview").GetProperty("schemaId").GetString()!=draft.GetProperty("schemaId").GetString())throw new InvalidDataException("设置预览不属于当前连接与座位状态");
            return body.Clone();
        }catch(IOException){Connected=false;Status="连接中断；未提交的设置草稿不会自动保存";throw;}
        finally{gate.Release();}
    }
    public async Task<JsonElement> LogsAsync(LanSeatView basis,int page,CancellationToken cancellation)
    {
        await gate.WaitAsync(cancellation).ConfigureAwait(false);try{
            ObjectDisposedException.ThrowIf(disposed,this);var room=Room??throw new InvalidOperationException("尚未分配座位");
            bool SameBasis()=>Connected&&View is{} current&&current.GameId==basis.GameId&&current.Sequence==basis.Sequence&&current.CoreRevision==basis.CoreRevision&&current.SourceId==basis.SourceId&&current.View.GetProperty("recipient").GetGuid()==room.Binding.PlayerId&&basis.View.GetProperty("recipient").GetGuid()==room.Binding.PlayerId;
            if(!SameBasis()||Pending is not null)throw new InvalidOperationException("日志版本已变化，请刷新座位并确认上次操作");
            var packet=await connection.ExchangeAsync(new LanLogs(room.Binding.GameId,basis.Sequence,basis.CoreRevision,page),cancellation).ConfigureAwait(false);Accepted(packet,"logs");var body=packet.GetProperty("body");
            if(!SameBasis()||body.GetProperty("gameId").GetGuid()!=basis.GameId||body.GetProperty("sequence").GetInt64()!=basis.Sequence||body.GetProperty("coreRevision").GetInt64()!=basis.CoreRevision||body.GetProperty("sourceId").GetString()!=basis.SourceId||body.GetProperty("playerId").GetGuid()!=room.Binding.PlayerId)throw new InvalidDataException("日志不属于当前座位或版本");
            return body.Clone();
        }catch(IOException){Connected=false;Status="日志读取连接中断，请重新连接后刷新";throw;}
        finally{gate.Release();}
    }
    public async Task<JsonElement> RegionsAsync(LanSeatView basis,CancellationToken cancellation)
    {
        await gate.WaitAsync(cancellation).ConfigureAwait(false);try{
            ObjectDisposedException.ThrowIf(disposed,this);var room=Room??throw new InvalidOperationException("尚未分配座位");
            bool SameBasis()=>Connected&&View is{} current&&current.GameId==basis.GameId&&current.Sequence==basis.Sequence&&current.CoreRevision==basis.CoreRevision&&current.SourceId==basis.SourceId&&current.View.GetProperty("recipient").GetGuid()==room.Binding.PlayerId&&basis.View.GetProperty("recipient").GetGuid()==room.Binding.PlayerId;
            if(!SameBasis()||Pending is not null)throw new InvalidOperationException("区域版本已变化，请先刷新座位并确认上次操作");
            var packet=await connection.ExchangeAsync(new LanRegions(room.Binding.GameId,basis.Sequence,basis.CoreRevision),cancellation).ConfigureAwait(false);Accepted(packet,"regions");var body=packet.GetProperty("body");
            if(!SameBasis()||body.GetProperty("gameId").GetGuid()!=basis.GameId||body.GetProperty("sequence").GetInt64()!=basis.Sequence||body.GetProperty("coreRevision").GetInt64()!=basis.CoreRevision||body.GetProperty("sourceId").GetString()!=basis.SourceId||body.GetProperty("playerId").GetGuid()!=room.Binding.PlayerId)throw new InvalidDataException("区域不属于当前座位或版本");
            _=ClientRegions.Read(body.GetProperty("regions"),room.Binding.PlayerId,basis.CoreRevision);return body.Clone();
        }catch(IOException){Connected=false;Status="区域读取连接中断，请重新连接后刷新";throw;}
        finally{gate.Release();}
    }
    public async Task ResolvePendingAsync(CancellationToken cancellation)
    {
        await gate.WaitAsync(cancellation).ConfigureAwait(false);try{
            var room=Room??throw new InvalidOperationException("尚未分配座位");var pending=store.Pending(room)??throw new InvalidOperationException("没有待确认操作");
            Outcome(await connection.ExchangeAsync(new LanReceiptQuery(pending.Intent),cancellation).ConfigureAwait(false),room);
        }finally{gate.Release();}
    }
    public async Task RetryKnownUncommittedAsync(CancellationToken cancellation)
    {
        await gate.WaitAsync(cancellation).ConfigureAwait(false);try{
            var room=Room??throw new InvalidOperationException("尚未分配座位");var intent=store.MarkRetryStarted(room);
            // Same UUID, same payload, same expected basis. No auto rebase.
            Outcome(await connection.ExchangeAsync(intent,cancellation).ConfigureAwait(false),room);
        }finally{gate.Release();}
    }
    public void CancelKnownUncommitted(){if(Room is not {} room)throw new InvalidOperationException("尚未分配座位");store.CancelKnownUncommitted(room);}
    public async ValueTask DisposeAsync()
    {disposed=true;Connected=false;await connection.DisposeAsync().ConfigureAwait(false);await gate.WaitAsync().ConfigureAwait(false);gate.Release();}
}
