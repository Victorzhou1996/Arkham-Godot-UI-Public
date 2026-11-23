// EOS login-only probe: init, create platform, device-ID login (no lobby, no P2P).
// Reads C:\Users\victo\.arkham\eos-config.local.json (or the path given as arg 0).
// Never prints the client secret; prints only field presence and result codes.
using System;
using System.IO;
using System.Text.Json;
using System.Threading;
using Epic.OnlineServices;
using Epic.OnlineServices.Connect;
using Epic.OnlineServices.Lobby;
using Epic.OnlineServices.P2P;
using Epic.OnlineServices.Platform;

internal static class Program
{
    private const string DefaultConfig = @"C:\Users\victo\.arkham\eos-config.local.json";

    private static int Main(string[] args)
    {
        // Usage: EosProbe.exe [--config <path>] [lobby | p2p-recv | p2p-send <peerPuid>]
        string cfgPath = DefaultConfig;
        var positional = new System.Collections.Generic.List<string>();
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "--config" && i + 1 < args.Length)
            {
                cfgPath = args[++i];
            }
            else
            {
                positional.Add(args[i]);
            }
        }
        string mode = positional.Count > 0 ? positional[0] : "lobby";
        string peerArg = positional.Count > 1 ? positional[1] : null;
        Console.WriteLine($"mode={mode} config={cfgPath}");
        JsonElement cfg = JsonSerializer.Deserialize<JsonElement>(File.ReadAllText(cfgPath));
        string Get(string key) => cfg.TryGetProperty(key, out var v) ? (v.GetString() ?? "") : "";

        string productId = Get("productId");
        string sandboxId = Get("sandboxId");
        string deploymentId = Get("deploymentId");
        string clientId = Get("clientId");
        string clientSecret = Get("clientSecret");
        Console.WriteLine($"config: productId={(productId.Length > 0)} sandboxId={(sandboxId.Length > 0)} deploymentId={(deploymentId.Length > 0)} clientId={(clientId.Length > 0)} clientSecret(len)={clientSecret.Length}");
        if (productId.Length == 0 || sandboxId.Length == 0 || deploymentId.Length == 0 || clientId.Length == 0 || clientSecret.Length == 0)
        {
            Console.WriteLine("FAIL: config incomplete");
            return 2;
        }

        var initOpts = new InitializeOptions { ProductName = "ArkhamHorror", ProductVersion = "1.0" };
        Result init = PlatformInterface.Initialize(ref initOpts);
        Console.WriteLine($"Initialize: {init}");
        if (init != Result.Success && init != Result.AlreadyConfigured)
        {
            return 3;
        }

#if EOS_PLATFORM_OSX
        // macOS: the generic Options struct is used (no WindowsOptions on Mac).
        var platOpts = new Epic.OnlineServices.Platform.Options
        {
            ProductId = productId,
            SandboxId = sandboxId,
            DeploymentId = deploymentId,
            ClientCredentials = new ClientCredentials { ClientId = clientId, ClientSecret = clientSecret },
            Flags = PlatformFlags.DisableOverlay,
            IsServer = false,
        };
        PlatformInterface platform = PlatformInterface.Create(ref platOpts);
#else
        var winOpts = new WindowsOptions
        {
            ProductId = productId,
            SandboxId = sandboxId,
            DeploymentId = deploymentId,
            ClientCredentials = new ClientCredentials { ClientId = clientId, ClientSecret = clientSecret },
            Flags = PlatformFlags.DisableOverlay,
            IsServer = false,
        };
        PlatformInterface platform = PlatformInterface.Create(ref winOpts);
#endif
        if (platform == null)
        {
            Console.WriteLine("FAIL: PlatformInterface.Create returned null");
            return 4;
        }
        Console.WriteLine("Create: ok");

        int exit = 0;
        try
        {
            ConnectInterface connect = platform.GetConnectInterface();

            // 1) Create (or reuse) a device ID.
            bool cdDone = false;
            Result cdRes = Result.UnexpectedError;
            var cdOpts = new CreateDeviceIdOptions { DeviceModel = "ArkhamHorror-PC" };
            connect.CreateDeviceId(ref cdOpts, null, (ref CreateDeviceIdCallbackInfo info) =>
            {
                cdRes = info.ResultCode;
                cdDone = true;
            });
            Pump(platform, () => cdDone);
            Console.WriteLine($"CreateDeviceId: {cdRes}");

            // 2) Login with the device ID credential.
            ProductUserId puid = null;
            ContinuanceToken token = null;
            Result loginRes = LoginOnce(platform, connect, out puid, out token);
            Console.WriteLine($"Login(DeviceidAccessToken): {loginRes}");

            // 3) New user: create the product user from the continuance token, then log in again.
            if (loginRes == Result.InvalidUser && token != null)
            {
                bool cuDone = false;
                Result cuRes = Result.UnexpectedError;
                var cuOpts = new CreateUserOptions { ContinuanceToken = token };
                connect.CreateUser(ref cuOpts, null, (ref CreateUserCallbackInfo info) =>
                {
                    cuRes = info.ResultCode;
                    cuDone = true;
                });
                Pump(platform, () => cuDone);
                Console.WriteLine($"CreateUser: {cuRes}");
                if (cuRes == Result.Success)
                {
                    loginRes = LoginOnce(platform, connect, out puid, out token);
                    Console.WriteLine($"Login(retry): {loginRes}");
                }
            }

            if (loginRes == Result.Success && puid != null)
            {
                Console.WriteLine($"PRODUCT USER LOGIN OK (puid length {puid.ToString().Length})");
                exit = (mode == "p2p-recv" || mode == "p2p-send") ? P2pProbe.Run(platform, puid, mode, peerArg) : (LobbyRoundTrip(platform, puid) ? 0 : 6);
            }
            else
            {
                Console.WriteLine("PRODUCT USER LOGIN NOT OK");
                exit = 5;
            }
        }
        finally
        {
            platform.Release();
            PlatformInterface.Shutdown();
        }
        return exit;
    }

    private static Result LoginOnce(PlatformInterface platform, ConnectInterface connect, out ProductUserId puid, out ContinuanceToken token)
    {
        bool done = false;
        Result res = Result.UnexpectedError;
        ProductUserId gotPuid = null;
        ContinuanceToken gotToken = null;
        var lo = new LoginOptions
        {
            Credentials = new Credentials { Type = ExternalCredentialType.DeviceidAccessToken, Token = null },
            UserLoginInfo = new UserLoginInfo { DisplayName = "ArkhamPlayer" },
        };
        connect.Login(ref lo, null, (ref LoginCallbackInfo info) =>
        {
            res = info.ResultCode;
            gotPuid = info.LocalUserId;
            gotToken = info.ContinuanceToken;
            done = true;
        });
        Pump(platform, () => done);
        puid = gotPuid;
        token = gotToken;
        return res;
    }

    // Lobby: create (invite-only, no presence), read member count via copied details, destroy.
    private static bool LobbyRoundTrip(PlatformInterface platform, ProductUserId puid)
    {
        LobbyInterface lobby = platform.GetLobbyInterface();

        bool cDone = false;
        Result cRes = Result.UnexpectedError;
        string lobbyId = null;
        var co = new CreateLobbyOptions
        {
            LocalUserId = puid,
            MaxLobbyMembers = 4,
            PermissionLevel = LobbyPermissionLevel.Inviteonly,
            PresenceEnabled = false,
            AllowInvites = false,
            DisableHostMigration = true,
            EnableRTCRoom = false,
            EnableJoinById = false,
            BucketId = "ArkhamHorror:Probe",
        };
        lobby.CreateLobby(ref co, null, (ref CreateLobbyCallbackInfo info) =>
        {
            cRes = info.ResultCode;
            if (info.ResultCode == Result.Success)
            {
                lobbyId = info.LobbyId;
            }
            cDone = true;
        });
        Pump(platform, () => cDone);
        Console.WriteLine($"CreateLobby: {cRes} (lobby id length {(lobbyId ?? "").Length})");
        if (cRes != Result.Success || string.IsNullOrEmpty(lobbyId))
        {
            return false;
        }

        bool ok = false;
        try
        {
            var cdo = new CopyLobbyDetailsHandleOptions { LobbyId = lobbyId, LocalUserId = puid };
            Result copyRes = lobby.CopyLobbyDetailsHandle(ref cdo, out LobbyDetails details);
            Console.WriteLine($"CopyLobbyDetailsHandle: {copyRes}");
            if (copyRes == Result.Success && details != null)
            {
                var mcOpts = new LobbyDetailsGetMemberCountOptions();
                uint members = details.GetMemberCount(ref mcOpts);
                Console.WriteLine($"Lobby member count: {members}");
                details.Release();
                ok = members == 1;
            }
        }
        finally
        {
            bool dDone = false;
            Result dRes = Result.UnexpectedError;
            var dOpts = new DestroyLobbyOptions { LobbyId = lobbyId, LocalUserId = puid };
            lobby.DestroyLobby(ref dOpts, null, (ref DestroyLobbyCallbackInfo info) =>
            {
                dRes = info.ResultCode;
                dDone = true;
            });
            Pump(platform, () => dDone);
            Console.WriteLine($"DestroyLobby: {dRes}");
        }
        Console.WriteLine(ok ? "LOBBY ROUND TRIP OK" : "LOBBY ROUND TRIP NOT OK");
        return ok;
    }

    private static void Pump(PlatformInterface platform, Func<bool> isDone)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (!isDone() && sw.Elapsed < TimeSpan.FromSeconds(30))
        {
            platform.Tick();
            Thread.Sleep(16);
        }
    }
}
