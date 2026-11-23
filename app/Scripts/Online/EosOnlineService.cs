#if ARKHAM_EOS
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using Godot;
using Epic.OnlineServices;
using Epic.OnlineServices.Connect;
using Epic.OnlineServices.Lobby;
using Epic.OnlineServices.Platform;

namespace ArkhamUI;

// EOS online play for Online.Service: device-ID login, a lobby per room with a 6-character code, member state kept in
// lobby attributes. Every EOS call and callback runs on the main thread: EosPump calls Tick each frame, so the async
// methods here resume on the main thread too.
// Not here yet: the game-sync messages (EosP2P carries them; the design is in docs/ONLINE-SYNC-DESIGN.md).
public sealed class EosOnlineService : IOnlineService
{
    public static EosOnlineService? Instance { get; private set; }

    public const string Bucket = "ArkhamHorror:Online";
    // lobby attributes (public; the room code is what joiners search for)
    const string KRoomCode = "ROOM_CODE", KGame = "GAME", KTitle = "TITLE", KSeats = "SEATS";
    // member attributes
    const string MName = "NAME", MReady = "READY", MSeat = "SEAT", MInv = "INV", MDeck = "DECK";
    // the game's version (Updater.VersionTag: release | rules core), on the room and on each member: players of different
    // versions do not play together (Yunke 11-23)
    const string KVer = "VER", MVer = "VER";
    // a seat no player sits in, set by the host: PLAN<n> = investigator|deck name|controller
    const string KPlan = "PLAN";
    const string CodeAlphabet = "ABCDEFGHJKMNPQRSTUVWXYZ23456789";   // no I, L, O, 0, 1
    const int MaxSeats = 4;
    const string DefaultTitle = "联机调查";

    PlatformInterface? platform;
    ConnectInterface? connect;
    LobbyInterface? lobby;
    ProductUserId? me;
    ulong updateNotify, memberNotify, statusNotify;
    string? lobbyId;
    bool isHost;
    OnlineRoom? room;
    string status = "正在启动联机…";

    public bool Ready { get; private set; }
    public string Status => status;
    public string LocalName { get; private set; } = "玩家";
    public string LocalId => me?.ToString() ?? "";
    public OnlineRoom? Room => room;
    public event Action? RoomChanged;
    public EosP2P? Peer { get; private set; }

    // ---- start-up ---------------------------------------------------------------------------------------------------

    public static void Start(Node host)
    {
        if (Instance is not null) return;
        var svc = new EosOnlineService();
        Instance = svc;
        Online.Service = svc;
        host.CallDeferred(Node.MethodName.AddChild, new EosPump { Name = "EosPump" });
        svc.Boot();
        if (SelfTestMode() is not null) _ = GuardSelfTestAsync(svc);
    }

    // QA only: a self-test must not hang the game when EOS never becomes ready
    static async Task GuardSelfTestAsync(EosOnlineService svc)
    {
        var until = DateTime.UtcNow.AddSeconds(120);
        while (!svc.Ready && DateTime.UtcNow < until) await svc.NextFrame();
        if (svc.Ready) return;
        GD.Print("[online-selftest] not ready: " + svc.Status);
        (Engine.GetMainLoop() as SceneTree)?.Quit();
    }

    void Boot()
    {
        var cfg = EosConfig.Load(out var why);
        if (cfg is null) { status = why; return; }
        if (!SdkPresent()) { status = "联机未就绪：缺少 EOS 运行库"; return; }
        InstallNativeResolver();
        try
        {
            var init = new InitializeOptions { ProductName = "ArkhamHorror", ProductVersion = "1.0" };
            Result ir = PlatformInterface.Initialize(ref init);
            if (ir != Result.Success && ir != Result.AlreadyConfigured) { status = $"联机未就绪：EOS 初始化失败（{ir}）"; return; }
#if EOS_PLATFORM_OSX
            var opts = new Epic.OnlineServices.Platform.Options
            {
                ProductId = cfg.ProductId, SandboxId = cfg.SandboxId, DeploymentId = cfg.DeploymentId,
                ClientCredentials = new ClientCredentials { ClientId = cfg.ClientId, ClientSecret = cfg.ClientSecret },
                Flags = PlatformFlags.DisableOverlay, IsServer = false,
            };
            platform = PlatformInterface.Create(ref opts);
#else
            var wopts = new WindowsOptions
            {
                ProductId = cfg.ProductId, SandboxId = cfg.SandboxId, DeploymentId = cfg.DeploymentId,
                ClientCredentials = new ClientCredentials { ClientId = cfg.ClientId, ClientSecret = cfg.ClientSecret },
                Flags = PlatformFlags.DisableOverlay, IsServer = false,
            };
            platform = PlatformInterface.Create(ref wopts);
#endif
            if (platform is null) { status = "联机未就绪：EOS 平台创建失败"; return; }
        }
        catch (Exception e)
        {
            GD.PrintErr("EOS start failed: " + e.GetType().Name);   // type only: messages may echo configuration
            status = "联机未就绪：EOS 启动失败";
            return;
        }
        connect = platform.GetConnectInterface();
        lobby = platform.GetLobbyInterface();
        _ = LoginAsync();
    }

    // the folder this game's assembly was loaded from: the SDK library is copied next to it (ArkhamApp.csproj)
    static string NativeDir => Path.GetDirectoryName(typeof(EosOnlineService).Assembly.Location) ?? AppContext.BaseDirectory;

    static string NativeFileName => OperatingSystem.IsWindows() ? "EOSSDK-Win64-Shipping.dll"
                                  : OperatingSystem.IsMacOS() ? "libEOSSDK-Mac-Shipping.dylib"
                                  : "";

    static bool SdkPresent() => NativeFileName.Length > 0 && File.Exists(Path.Combine(NativeDir, NativeFileName));

    // The SDK binds its library by name; the game's assemblies are loaded outside the default probing folders (Godot), so
    // the library is resolved explicitly from NativeDir. Other names fall back to the normal lookup.
    static void InstallNativeResolver()
    {
        try { NativeLibrary.SetDllImportResolver(typeof(EosOnlineService).Assembly, ResolveNative); }
        catch (InvalidOperationException) { }   // already installed for this assembly
    }

    static IntPtr ResolveNative(string name, Assembly assembly, DllImportSearchPath? searchPath)
    {
        if (name != Epic.OnlineServices.Common.LIBRARY_NAME) return IntPtr.Zero;
        string file = name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".dylib", StringComparison.OrdinalIgnoreCase)
            ? name : name + ".dylib";
        string path = Path.Combine(NativeDir, file);
        return File.Exists(path) ? NativeLibrary.Load(path) : IntPtr.Zero;
    }

    async Task LoginAsync()
    {
        try
        {
            var cd = new CreateDeviceIdOptions { DeviceModel = OperatingSystem.IsMacOS() ? "ArkhamHorror-Mac" : "ArkhamHorror-PC" };
            var cdTcs = Pending<Result>();
            connect!.CreateDeviceId(ref cd, null, (ref CreateDeviceIdCallbackInfo info) => cdTcs.TrySetResult(info.ResultCode));
            await Within(cdTcs.Task);   // AlreadyConfigured is fine; the login below decides

            var (res, puid, token) = await LoginOnce();
            if (res == Result.InvalidUser && token is not null)
            {
                // first run for this device: create the product user, then log in again
                var cuTcs = Pending<Result>();
                var cu = new CreateUserOptions { ContinuanceToken = token };
                connect!.CreateUser(ref cu, null, (ref CreateUserCallbackInfo info) => cuTcs.TrySetResult(info.ResultCode));
                Result cur = await Within(cuTcs.Task);
                if (cur == Result.Success) (res, puid, _) = await LoginOnce();
            }
            if (res != Result.Success || puid is null)
            {
                status = $"联机未就绪：登录 EOS 失败（{res}）";
                return;
            }
            me = puid;
            string id = puid.ToString();
            LocalName = "玩家" + (id.Length >= 4 ? id[^4..] : id);
            Peer = new EosP2P(platform!, puid);
            OnlineGame.UseLink(new EosLink(Peer, id));
            Subscribe();
            WatchAuth();
            status = "";
            Ready = true;
            RoomChanged?.Invoke();   // the menus re-read Online.Available
            if (SelfTestMode() is { } mode) _ = SelfTestAsync(mode);
        }
        catch (Exception e)
        {
            GD.PrintErr("EOS login failed: " + e.GetType().Name);
            status = "联机未就绪：登录 EOS 超时或失败";
        }
    }

    // The device login's token lasts about an hour. EOS says so a few minutes before it runs out; without a new login every
    // room call after that fails with InvalidAuth (a player 11-23: "建房失败（InvalidAuth）" after the game had been open a while).
    bool authWatched;
    void WatchAuth()
    {
        if (authWatched) return;
        authWatched = true;
        var o = new AddNotifyAuthExpirationOptions();
        connect!.AddNotifyAuthExpiration(ref o, null, (ref AuthExpirationCallbackInfo info) => { _ = Relogin("expiring"); });
        QaScript.Register("eos-relogin", async _ => await Relogin("qa"));   // QA: the same new login, without waiting an hour
    }

    Task? relogin;
    Task Relogin(string why) => relogin is { IsCompleted: false } r ? r : (relogin = DoRelogin(why));
    async Task DoRelogin(string why)
    {
        try
        {
            var (res, puid, _) = await LoginOnce();
            GD.Print("EOS_RELOGIN " + why + " " + res);
            if (res == Result.Success && puid is not null) { me = puid; status = ""; }
            else status = $"联机登录已过期，重新登录失败（{res}）";
        }
        catch (Exception e) { GD.PrintErr("EOS relogin failed: " + e.Message); }
    }

    // a room call that failed because the login had run out: log in again and try once more
    async Task<T> WithAuth<T>(Func<Task<T>> call)
    {
        try { return await call(); }
        catch (InvalidOperationException e) when (e.Message.Contains("InvalidAuth"))
        {
            await Relogin("retry");
            return await call();
        }
    }

    Task<(Result, ProductUserId?, ContinuanceToken?)> LoginOnce()
    {
        var tcs = Pending<(Result, ProductUserId?, ContinuanceToken?)>();
        var lo = new LoginOptions
        {
            Credentials = new Credentials { Type = ExternalCredentialType.DeviceidAccessToken, Token = null },
            UserLoginInfo = new UserLoginInfo { DisplayName = "ArkhamPlayer" },
        };
        connect!.Login(ref lo, null, (ref LoginCallbackInfo info) => tcs.TrySetResult((info.ResultCode, info.LocalUserId, info.ContinuanceToken)));
        return Within(tcs.Task);
    }

    // ---- QA self-test (only with --online-selftest=host | join:<CODE>) ----------------------------------------------
    // Runs the room flow and logs each step as "[online-selftest] ...", then quits the game. Not used by players.
    static string? SelfTestMode()
    {
        foreach (var a in OS.GetCmdlineUserArgs())
            if (a.StartsWith("--online-selftest=", StringComparison.Ordinal)) return a["--online-selftest=".Length..];
        return null;
    }

    async Task SelfTestAsync(string mode)
    {
        void Log(string s) => GD.Print("[online-selftest] " + s);
        try
        {
            Log($"ready, local name {LocalName}, mode {mode}");
            if (mode == "host")
            {
                string code = await HostAsync(null, MaxSeats, "");
                Log($"host ok, room code {code}");
                await SetReadyAsync(true);
                Log("host ready");
                bool joined = await WaitUntil(() => room is not null && room.Members.Count >= 2, 600);
                Log(joined ? $"host sees {room!.Members.Count} members" : "host: nobody joined within 600s");
                if (room is not null) foreach (var m in room.Members) Log($"  member {m.Name} host={m.Host} ready={m.Ready} seat={m.Seat}");
                await Pause(10);
                await LeaveAsync();
                Log("host left");
            }
            else if (mode.StartsWith("join:", StringComparison.Ordinal))
            {
                await JoinAsync(mode["join:".Length..]);
                Log($"join ok, {room?.Members.Count ?? 0} members");
                if (room is not null) foreach (var m in room.Members) Log($"  member {m.Name} host={m.Host} ready={m.Ready} seat={m.Seat}");
                await SetReadyAsync(true);
                Log("join ready");
                await Pause(5);
                await LeaveAsync();
                Log("join left");
            }
        }
        catch (Exception e)
        {
            Log($"FAIL {e.GetType().Name}: {e.Message}");
        }
        Log("done");
        (Engine.GetMainLoop() as SceneTree)?.Quit();
    }

    // checks the condition once per frame until it holds or the time runs out
    async Task<bool> WaitUntil(Func<bool> condition, int seconds)
    {
        var until = DateTime.UtcNow.AddSeconds(seconds);
        while (DateTime.UtcNow < until)
        {
            if (condition()) return true;
            await NextFrame();
        }
        return condition();
    }

    // ---- main-thread pump -------------------------------------------------------------------------------------------

    public void Tick()
    {
        platform?.Tick();
        Peer?.Poll();
        if (frameWaiters.Count == 0) return;
        var waiting = frameWaiters.ToArray();
        frameWaiters.Clear();
        foreach (var w in waiting) w.TrySetResult(true);
    }

    // the waits below resume on Tick (the game's frames) rather than on a timer, so they never depend on the timer
    // thread or on Godot's dispatch queue running
    readonly List<TaskCompletionSource<bool>> frameWaiters = new();

    Task NextFrame()
    {
        var w = Pending<bool>();
        frameWaiters.Add(w);
        return w.Task;
    }

    Task Pause(double seconds)
    {
        var until = DateTime.UtcNow.AddSeconds(seconds);
        return PauseUntil(until);
    }

    async Task PauseUntil(DateTime until)
    {
        while (DateTime.UtcNow < until) await NextFrame();
    }

    public void Shutdown()
    {
        if (platform is null) return;
        try { platform.Release(); PlatformInterface.Shutdown(); }
        catch (Exception) { }
        platform = null; Ready = false;
    }

    // ---- lobby subscriptions ----------------------------------------------------------------------------------------

    void Subscribe()
    {
        var uo = new AddNotifyLobbyUpdateReceivedOptions();
        updateNotify = lobby!.AddNotifyLobbyUpdateReceived(ref uo, null, (ref LobbyUpdateReceivedCallbackInfo info) =>
        {
            string id = info.LobbyId;
            if (id == lobbyId) Refresh();
        });
        var mo = new AddNotifyLobbyMemberUpdateReceivedOptions();
        memberNotify = lobby!.AddNotifyLobbyMemberUpdateReceived(ref mo, null, (ref LobbyMemberUpdateReceivedCallbackInfo info) =>
        {
            string id = info.LobbyId;
            if (id == lobbyId) Refresh();
        });
        var so = new AddNotifyLobbyMemberStatusReceivedOptions();
        statusNotify = lobby!.AddNotifyLobbyMemberStatusReceived(ref so, null, (ref LobbyMemberStatusReceivedCallbackInfo info) =>
        {
            string id = info.LobbyId;
            if (id != lobbyId) return;
            bool isMe = info.TargetUserId?.ToString() == me?.ToString();
            bool gone = info.CurrentStatus is LobbyMemberStatus.Kicked or LobbyMemberStatus.Closed or LobbyMemberStatus.Left or LobbyMemberStatus.Disconnected;
            if (isMe && gone) Forget();   // this player is no longer in the room
            else Refresh();
        });
    }

    void Forget()
    {
        lobbyId = null; isHost = false; room = null;
        RoomChanged?.Invoke();
        if (OnlineGame.IsGuest) OnlineGame.End("主持人关闭了房间，联机对局结束");   // the host's room is gone
    }

    // ---- IOnlineService: rooms --------------------------------------------------------------------------------------

    public Task<string> HostAsync(Guid? gameId, int seats, string title = "") => WithAuth(() => HostOnceAsync(gameId, seats, title));
    public Task JoinAsync(string roomCode) => WithAuth(async () => { await JoinOnceAsync(roomCode); return true; });

    async Task<string> HostOnceAsync(Guid? gameId, int seats, string title)
    {
        RequireReady();
        if (lobbyId is not null) await LeaveAsync();
        string code = NewCode();
        var co = new CreateLobbyOptions
        {
            LocalUserId = me,
            MaxLobbyMembers = (uint)Math.Clamp(seats, 1, MaxSeats),
            PermissionLevel = LobbyPermissionLevel.Publicadvertised,   // findable by search (room code)
            PresenceEnabled = false,
            AllowInvites = false,
            DisableHostMigration = true,   // the room ends when its host leaves (first version)
            EnableRTCRoom = false,
            EnableJoinById = false,
            BucketId = Bucket,
        };
        var tcs = Pending<(Result, string?)>();
        lobby!.CreateLobby(ref co, null, (ref CreateLobbyCallbackInfo info) => tcs.TrySetResult((info.ResultCode, info.LobbyId)));
        var (cr, id) = await Within(tcs.Task);
        if (cr != Result.Success || id is null) throw new InvalidOperationException($"建房失败（{cr}）");
        lobbyId = id; isHost = true;
        try
        {
            string game = gameId?.ToString() ?? "";
            int seatCount = Math.Clamp(seats, 1, MaxSeats);
            await ModifyAsync(m =>
            {
                SetLobbyAttr(m, KRoomCode, code);
                SetLobbyAttr(m, KGame, game);
                SetLobbyAttr(m, KTitle, title.Length > 0 ? title : DefaultTitle);
                SetLobbyAttr(m, KSeats, seatCount.ToString());
                SetLobbyAttr(m, KVer, Updater.VersionTag);
                SetMemberAttr(m, MVer, Updater.VersionTag);
                SetMemberAttr(m, MName, LocalName);
                SetMemberAttr(m, MReady, "0");
                SetMemberAttr(m, MSeat, "-1");
                SetMemberAttr(m, MInv, "");
                SetMemberAttr(m, MDeck, "");
            });
        }
        catch
        {
            await LeaveAsync();
            throw;
        }
        return code;
    }

    async Task JoinOnceAsync(string roomCode)
    {
        RequireReady();
        if (lobbyId is not null) await LeaveAsync();
        var so = new CreateLobbySearchOptions { MaxResults = 4 };
        Result sr = lobby!.CreateLobbySearch(ref so, out LobbySearch search);
        if (sr != Result.Success) throw new InvalidOperationException($"搜索房间失败（{sr}）");
        try
        {
            var p1 = new LobbySearchSetParameterOptions { Parameter = new AttributeData { Key = KRoomCode, Value = (Utf8String)roomCode }, ComparisonOp = ComparisonOp.Equal };
            search.SetParameter(ref p1);
            var p2 = new LobbySearchSetParameterOptions { Parameter = new AttributeData { Key = LobbyInterface.SEARCH_BUCKET_ID, Value = (Utf8String)Bucket }, ComparisonOp = ComparisonOp.Equal };
            search.SetParameter(ref p2);

            var fTcs = Pending<Result>();
            var fo = new LobbySearchFindOptions { LocalUserId = me };
            search.Find(ref fo, null, (ref LobbySearchFindCallbackInfo info) => fTcs.TrySetResult(info.ResultCode));
            Result fr = await Within(fTcs.Task);
            if (fr != Result.Success) throw new InvalidOperationException($"搜索房间失败（{fr}）");

            var cnt = new LobbySearchGetSearchResultCountOptions();
            if (search.GetSearchResultCount(ref cnt) == 0) throw new InvalidOperationException("没有找到这个房间码");

            var ci = new LobbySearchCopySearchResultByIndexOptions { LobbyIndex = 0 };
            Result cr = search.CopySearchResultByIndex(ref ci, out LobbyDetails details);
            if (cr != Result.Success) throw new InvalidOperationException($"读取房间失败（{cr}）");
            try
            {
                string theirs = LobbyAttr(details, KVer);
                if (!SameVersion(theirs, Updater.VersionTag))
                    throw new InvalidOperationException(theirs.Length == 0 ? "房主的游戏版本太旧，不能一起联机" : "你的版本（" + Show(Updater.VersionTag) + "）和房主（" + Show(theirs) + "）不同，请先更新");
                var jTcs = Pending<(Result, string?)>();
                var jo = new JoinLobbyOptions { LobbyDetailsHandle = details, LocalUserId = me, PresenceEnabled = false };
                lobby!.JoinLobby(ref jo, null, (ref JoinLobbyCallbackInfo info) => jTcs.TrySetResult((info.ResultCode, info.LobbyId)));
                var (jr, jid) = await Within(jTcs.Task);
                if (jr != Result.Success || jid is null) throw new InvalidOperationException($"加入失败（{jr}）");
                lobbyId = jid; isHost = false;
            }
            finally
            {
                details.Release();
            }
        }
        finally
        {
            search.Release();
        }

        try
        {
            await ModifyAsync(m =>
            {
                SetMemberAttr(m, MVer, Updater.VersionTag);
                SetMemberAttr(m, MName, LocalName);
                SetMemberAttr(m, MReady, "0");
                SetMemberAttr(m, MSeat, "-1");
                SetMemberAttr(m, MInv, "");
                SetMemberAttr(m, MDeck, "");
            });
        }
        catch
        {
            await LeaveAsync();
            throw;
        }
    }

    public async Task LeaveAsync()
    {
        if (lobbyId is null || me is null) { Forget(); return; }
        string id = lobbyId;
        var tcs = Pending<Result>();
        try
        {
            if (isHost)
            {
                var d = new DestroyLobbyOptions { LobbyId = id, LocalUserId = me };
                lobby!.DestroyLobby(ref d, null, (ref DestroyLobbyCallbackInfo info) => tcs.TrySetResult(info.ResultCode));
            }
            else
            {
                var l = new LeaveLobbyOptions { LobbyId = id, LocalUserId = me };
                lobby!.LeaveLobby(ref l, null, (ref LeaveLobbyCallbackInfo info) => tcs.TrySetResult(info.ResultCode));
            }
            await Within(tcs.Task);
        }
        catch (Exception)
        {
            // the room is dropped locally either way
        }
        Forget();
    }

    public Task SetReadyAsync(bool ready)
    {
        RequireInRoom();
        return ModifyAsync(m => SetMemberAttr(m, MReady, ready ? "1" : "0"));
    }

    // the packed deck in member attributes DK0..DK15 (each at most 900 characters; EOS allows 1000)
    const int DeckParts = 16, DeckPart = 900;
    public Task SetDeckDataAsync(string packed)
    {
        RequireInRoom();
        if (packed.Length > DeckParts * DeckPart) throw new InvalidOperationException("牌组太大，无法放进房间");
        return ModifyAsync(m =>
        {
            for (int i = 0; i < DeckParts; i++)
                SetMemberAttr(m, "DK" + i, i * DeckPart < packed.Length ? packed.Substring(i * DeckPart, Math.Min(DeckPart, packed.Length - i * DeckPart)) : "");
        });
    }

    public Task SetSeatPlanAsync(int seat, string investigatorCode, string deckName, string controller)
    {
        RequireInRoom();
        if (!isHost) throw new InvalidOperationException("只有建房的人能安排这个座位");
        string v = investigatorCode.Length == 0 && controller.Length == 0 ? "" : investigatorCode + "|" + deckName.Replace('|', '/') + "|" + controller;
        return ModifyAsync(m => SetLobbyAttr(m, KPlan + seat, v));
    }

    public Task ClaimSeatAsync(int seat, string investigatorCode, string deckKey)
    {
        RequireInRoom();
        return ModifyAsync(m =>
        {
            SetMemberAttr(m, MSeat, seat.ToString());
            SetMemberAttr(m, MInv, investigatorCode ?? "");
            SetMemberAttr(m, MDeck, deckKey ?? "");
        });
    }

    // ---- helpers ----------------------------------------------------------------------------------------------------

    void RequireReady()
    {
        if (!Ready || me is null || lobby is null) throw new InvalidOperationException(status.Length > 0 ? status : "联机未就绪");
    }

    void RequireInRoom()
    {
        RequireReady();
        if (lobbyId is null) throw new InvalidOperationException("还没有加入房间");
    }

    // applies edits to this player's room (lobby attributes or own member attributes), then reads the room back
    async Task ModifyAsync(Action<LobbyModification> edit)
    {
        string id = lobbyId ?? throw new InvalidOperationException("还没有加入房间");
        var uo = new UpdateLobbyModificationOptions { LocalUserId = me, LobbyId = id };
        Result r = lobby!.UpdateLobbyModification(ref uo, out LobbyModification mod);
        if (r != Result.Success) throw new InvalidOperationException($"更新房间失败（{r}）");
        try
        {
            edit(mod);
            var tcs = Pending<Result>();
            var o = new UpdateLobbyOptions { LobbyModificationHandle = mod };
            lobby!.UpdateLobby(ref o, null, (ref UpdateLobbyCallbackInfo info) => tcs.TrySetResult(info.ResultCode));
            Result ur = await Within(tcs.Task);
            if (ur != Result.Success) throw new InvalidOperationException($"更新房间失败（{ur}）");
        }
        finally
        {
            mod.Release();
        }
        Refresh();
    }

    static void SetLobbyAttr(LobbyModification m, string key, string value)
    {
        var o = new LobbyModificationAddAttributeOptions
        {
            Attribute = new AttributeData { Key = key, Value = (Utf8String)value },
            Visibility = LobbyAttributeVisibility.Public,
        };
        m.AddAttribute(ref o);
    }

    static void SetMemberAttr(LobbyModification m, string key, string value)
    {
        var o = new LobbyModificationAddMemberAttributeOptions
        {
            Attribute = new AttributeData { Key = key, Value = (Utf8String)value },
            Visibility = LobbyAttributeVisibility.Public,
        };
        m.AddMemberAttribute(ref o);
    }

    // re-reads the current room from EOS and tells the screens
    void Refresh()
    {
        room = lobbyId is null || me is null ? null : ReadRoom(lobbyId);
        RoomChanged?.Invoke();
        if (room is not null) OnlineGame.RoomMembers(room.Members.Select(m => m.Id));   // a player back in the room is told the game again
    }

    OnlineRoom? ReadRoom(string id)
    {
        var dopts = new CopyLobbyDetailsHandleOptions { LobbyId = id, LocalUserId = me };
        if (lobby!.CopyLobbyDetailsHandle(ref dopts, out LobbyDetails details) != Result.Success || details is null) return null;
        try
        {
            string code = LobbyAttr(details, KRoomCode);
            string game = LobbyAttr(details, KGame);
            string title = LobbyAttr(details, KTitle);
            int seats = int.TryParse(LobbyAttr(details, KSeats), out var s) ? s : MaxSeats;
            var owner = OwnerOf(details);
            var mc = new LobbyDetailsGetMemberCountOptions();
            uint n = details.GetMemberCount(ref mc);
            var members = new List<OnlineMember>();
            for (uint i = 0; i < n; i++)
            {
                var mi = new LobbyDetailsGetMemberByIndexOptions { MemberIndex = i };
                ProductUserId pid = details.GetMemberByIndex(ref mi);
                if (pid is null) continue;
                members.Add(MemberOf(details, pid, owner));
            }
            Guid? gameId = Guid.TryParse(game, out var g) ? g : null;
            var plans = new List<OnlineSeatPlan>();
            for (int i = 0; i < Math.Max(seats, MaxSeats); i++)
            {
                var p = LobbyAttr(details, KPlan + i).Split('|');
                if (p.Length == 3 && (p[0].Length > 0 || p[2].Length > 0)) plans.Add(new OnlineSeatPlan(i, p[0], p[1], p[2]));
            }
            return new OnlineRoom(code, gameId, title.Length > 0 ? title : DefaultTitle, seats, members, plans);
        }
        finally
        {
            details.Release();
        }
    }

    static ProductUserId? OwnerOf(LobbyDetails d)
    {
        var o = new LobbyDetailsGetLobbyOwnerOptions();
        return d.GetLobbyOwner(ref o);
    }

    OnlineMember MemberOf(LobbyDetails d, ProductUserId pid, ProductUserId? owner)
    {
        string name = MemberAttr(d, pid, MName);
        bool ready = MemberAttr(d, pid, MReady) == "1";
        int seat = int.TryParse(MemberAttr(d, pid, MSeat), out var s) ? s : -1;
        string inv = MemberAttr(d, pid, MInv), deck = MemberAttr(d, pid, MDeck);
        var packed = new StringBuilder();
        for (int i = 0; i < DeckParts; i++) { var part = MemberAttr(d, pid, "DK" + i); if (part.Length == 0) break; packed.Append(part); }
        bool host = owner is not null && owner.ToString() == pid.ToString();
        return new OnlineMember(name.Length > 0 ? name : "玩家", host, ready, seat, inv, pid.ToString(), deck, packed.ToString(), MemberAttr(d, pid, MVer));
    }

    // the same rules core, and the same release unless either side is a development run ("dev")
    public static bool SameVersion(string a, string b)
    {
        var x = a.Split('|'); var y = b.Split('|');
        if (x.Length != 2 || y.Length != 2) return false;
        if (x[1] != y[1]) return false;
        return x[0] == y[0] || x[0] == "dev" || y[0] == "dev";
    }
    static string Show(string tag) { var p = tag.Split('|'); return p[0] == "dev" ? "开发版" : p[0]; }

    static string LobbyAttr(LobbyDetails d, string key)
    {
        var o = new LobbyDetailsCopyAttributeByKeyOptions { AttrKey = key };
        if (d.CopyAttributeByKey(ref o, out var a) != Result.Success || a?.Data is not { } data) return "";
        string? s = data.Value.AsUtf8;
        return s ?? "";
    }

    static string MemberAttr(LobbyDetails d, ProductUserId pid, string key)
    {
        var o = new LobbyDetailsCopyMemberAttributeByKeyOptions { TargetUserId = pid, AttrKey = key };
        if (d.CopyMemberAttributeByKey(ref o, out var a) != Result.Success || a?.Data is not { } data) return "";
        string? s = data.Value.AsUtf8;
        return s ?? "";
    }

    static string NewCode()
    {
        var sb = new StringBuilder(6);
        for (int i = 0; i < 6; i++) sb.Append(CodeAlphabet[System.Random.Shared.Next(CodeAlphabet.Length)]);
        return sb.ToString();
    }

    // a TaskCompletionSource whose awaiters resume on the next pump (main thread), never inside the EOS callback
    static TaskCompletionSource<T> Pending<T>() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    // waits for an EOS callback, checked once per frame; EOS answers during Tick, so no answer within 30 s means the SDK or
    // the network stopped
    async Task<T> Within<T>(Task<T> task)
    {
        var until = DateTime.UtcNow.AddSeconds(30);
        while (!task.IsCompleted)
        {
            if (DateTime.UtcNow > until) throw new TimeoutException("EOS 没有响应");
            await NextFrame();
        }
        return await task;
    }
}

// OnlineGame's messages over EOS P2P (reliable, ordered), addressed by the players' EOS ids
public sealed class EosLink : OnlineLink
{
    readonly EosP2P p2p;
    readonly string me;
    readonly Dictionary<string, ProductUserId> ids = new();
    public EosLink(EosP2P p2p, string me)
    {
        this.p2p = p2p; this.me = me;
        p2p.Received += (from, kind, payload) => { if (kind != 1) return; string f = from.ToString(); ids[f] = from; Receive(f, payload); };
        p2p.Connected += who => GD.Print("ONLINE_P2P_REQUEST from " + who);
    }
    public override string LocalId => me;
    protected override void SendRaw(string peer, byte[] packet)
    {
        if (!ids.TryGetValue(peer, out var to)) ids[peer] = to = ProductUserId.FromString(peer);
        var r = p2p.Send(to, 1, packet);
        if (r != Result.Success) GD.PrintErr("ONLINE_SEND " + r);
    }
}

// Drives EOS on the main thread. Added under the scene root when online play starts.
public partial class EosPump : Node
{
    public override void _Process(double delta) => EosOnlineService.Instance?.Tick();

    public override void _ExitTree() => EosOnlineService.Instance?.Shutdown();
}
#endif
