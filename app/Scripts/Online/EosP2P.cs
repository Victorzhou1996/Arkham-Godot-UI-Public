#if ARKHAM_EOS
using System;
using Epic.OnlineServices;
using Epic.OnlineServices.P2P;
using Epic.OnlineServices.Platform;

namespace ArkhamUI;

// Game messages between two players over EOS P2P (reliable, ordered). Envelope: [version][kind][payload...].
// Version 1 is the only one; a receiver drops any other version, so a newer build never misreads an older message.
// EOS limits one packet to 1170 bytes, so a payload is at most 1168 bytes; larger state goes in several messages.
public sealed class EosP2P
{
    public const byte Version = 1;
    public const int MaxPacket = 1170;
    const string SocketName = "ArkhamOnline";

    readonly P2PInterface p2p;
    readonly ProductUserId me;
    readonly SocketId socket = new() { SocketName = SocketName };
    readonly ulong notifyHandle;

    // from, kind, payload (a copy)
    public event Action<ProductUserId, byte, byte[]>? Received;
    public event Action<string>? Connected;   // another player opened a connection to this one (logged for diagnosis)

    // how each connection actually travels (Yunke 11-23: "同一个网络下 EOS 会不会自动直连"): "direct" or "relay", by the other
    // player's id; gone when the connection is lost. Logged as ONLINE_P2P_TYPE / ONLINE_P2P_LOST; the room shows it.
    public static readonly Dictionary<string, string> Path = new();
    public static string PathText(string id) => Path.TryGetValue(id, out var p) ? (p == "direct" ? "直连正常（局域网 / 直连）" : "已连接（经 Epic 中继）") : "";
    static string Tail(ProductUserId? id) { var s = id?.ToString() ?? ""; return s.Length > 4 ? s[^4..] : s; }

    public EosP2P(PlatformInterface platform, ProductUserId me)
    {
        this.me = me;
        p2p = platform.GetP2PInterface();
        // accept every incoming connection on this socket: only players in the same room ever reach it
        var ro = new AddNotifyPeerConnectionRequestOptions { LocalUserId = me, SocketId = socket };
        notifyHandle = p2p.AddNotifyPeerConnectionRequest(ref ro, null, (ref OnIncomingConnectionRequestInfo info) =>
        {
            var acc = new AcceptConnectionOptions { LocalUserId = info.LocalUserId, RemoteUserId = info.RemoteUserId, SocketId = info.SocketId };
            var r = p2p.AcceptConnection(ref acc);
            Connected?.Invoke(info.RemoteUserId?.ToString() + " accept=" + r);
        });
        var eo = new AddNotifyPeerConnectionEstablishedOptions { LocalUserId = me, SocketId = socket };
        p2p.AddNotifyPeerConnectionEstablished(ref eo, null, (ref OnPeerConnectionEstablishedInfo info) =>
        {
            string kind = info.NetworkType == NetworkConnectionType.DirectConnection ? "direct" : info.NetworkType == NetworkConnectionType.RelayedConnection ? "relay" : "none";
            Path[info.RemoteUserId?.ToString() ?? ""] = kind;
            Godot.GD.Print("ONLINE_P2P_TYPE " + Tail(info.RemoteUserId) + " " + kind + " (" + info.ConnectionType + ")");
        });
        var io = new AddNotifyPeerConnectionInterruptedOptions { LocalUserId = me, SocketId = socket };
        p2p.AddNotifyPeerConnectionInterrupted(ref io, null, (ref OnPeerConnectionInterruptedInfo info) =>
        {
            Path.Remove(info.RemoteUserId?.ToString() ?? "");
            Godot.GD.Print("ONLINE_P2P_LOST " + Tail(info.RemoteUserId) + " interrupted");
        });
        var co = new AddNotifyPeerConnectionClosedOptions { LocalUserId = me, SocketId = socket };
        p2p.AddNotifyPeerConnectionClosed(ref co, null, (ref OnRemoteConnectionClosedInfo info) =>
        {
            Path.Remove(info.RemoteUserId?.ToString() ?? "");
            Godot.GD.Print("ONLINE_P2P_LOST " + Tail(info.RemoteUserId) + " " + info.Reason);
        });
    }

    public Result Send(ProductUserId to, byte kind, byte[] payload)
    {
        if (payload.Length > MaxPacket - 2) throw new ArgumentException("payload too large for one EOS packet");
        var data = new byte[payload.Length + 2];
        data[0] = Version;
        data[1] = kind;
        Buffer.BlockCopy(payload, 0, data, 2, payload.Length);
        var so = new SendPacketOptions
        {
            LocalUserId = me,
            RemoteUserId = to,
            SocketId = socket,
            Channel = 0,
            Data = new ArraySegment<byte>(data),
            AllowDelayedDelivery = true,
            Reliability = PacketReliability.ReliableOrdered,
            DisableAutoAcceptConnection = false,
        };
        return p2p.SendPacket(ref so);
    }

    // called every frame on the main thread (EosOnlineService.Tick)
    public void Poll()
    {
        for (int guard = 0; guard < 64; guard++)
        {
            var sizeOpts = new GetNextReceivedPacketSizeOptions { LocalUserId = me, RequestedChannel = null };
            if (p2p.GetNextReceivedPacketSize(ref sizeOpts, out uint size) != Result.Success || size == 0) return;
            var data = new byte[size];
            var ro = new ReceivePacketOptions { LocalUserId = me, MaxDataSizeBytes = size, RequestedChannel = null };
            ProductUserId? peer = null;
            SocketId sid = default;
            Result r = p2p.ReceivePacket(ref ro, ref peer, ref sid, out byte _, new ArraySegment<byte>(data), out uint written);
            if (r != Result.Success || peer is null || written < 2 || data[0] != Version) continue;
            var payload = new byte[written - 2];
            Buffer.BlockCopy(data, 2, payload, 0, payload.Length);
            Received?.Invoke(peer, data[1], payload);
        }
    }
}
#endif
