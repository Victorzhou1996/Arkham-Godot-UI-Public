// P2P probe: receiver accepts incoming connections and waits for one packet;
// sender sends one reliable packet to a peer product user id.
using System;
using System.Diagnostics;
using System.Text;
using System.Threading;
using Epic.OnlineServices;
using Epic.OnlineServices.P2P;
using Epic.OnlineServices.Platform;

internal static class P2pProbe
{
    private const string SocketName = "ArkhamProbe";
    private static readonly byte[] Buf = new byte[1170];

    public static int Run(PlatformInterface platform, ProductUserId me, string mode, string peerText)
    {
        P2PInterface p2p = platform.GetP2PInterface();
        var socket = new SocketId { SocketName = SocketName };

        // Accept every incoming connection request on this socket (probe only).
        var reqOpts = new AddNotifyPeerConnectionRequestOptions { LocalUserId = me, SocketId = socket };
        ulong reqHandle = p2p.AddNotifyPeerConnectionRequest(ref reqOpts, null, (ref OnIncomingConnectionRequestInfo info) =>
        {
            var acc = new AcceptConnectionOptions
            {
                LocalUserId = info.LocalUserId,
                RemoteUserId = info.RemoteUserId,
                SocketId = info.SocketId,
            };
            Result r = p2p.AcceptConnection(ref acc);
            Console.WriteLine($"incoming connection request: AcceptConnection={r}");
        });
        Console.WriteLine($"notify request handle: {(reqHandle != 0 ? "ok" : "zero")}");

        if (mode == "p2p-recv")
        {
            Console.WriteLine($"RECEIVER PUID: {me}");
            Console.WriteLine("waiting up to 90s for a packet...");
            return Receive(platform, p2p, me);
        }
        return Send(platform, p2p, me, socket, peerText);
    }

    private static int Receive(PlatformInterface platform, P2PInterface p2p, ProductUserId me)
    {
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < TimeSpan.FromSeconds(90))
        {
            platform.Tick();
            var sizeOpts = new GetNextReceivedPacketSizeOptions { LocalUserId = me, RequestedChannel = null };
            Result rs = p2p.GetNextReceivedPacketSize(ref sizeOpts, out uint size);
            if (rs == Result.Success && size > 0)
            {
                var ropts = new ReceivePacketOptions { LocalUserId = me, MaxDataSizeBytes = (uint)Buf.Length, RequestedChannel = null };
                ProductUserId peer = null;
                SocketId sid = default;
                Result rr = p2p.ReceivePacket(ref ropts, ref peer, ref sid, out byte ch, new ArraySegment<byte>(Buf), out uint written);
                string text = Encoding.UTF8.GetString(Buf, 0, (int)written);
                Console.WriteLine($"RECV: {rr}, channel {ch}, bytes {written}, text=\"{text}\"");
                return rr == Result.Success ? 0 : 7;
            }
            Thread.Sleep(16);
        }
        Console.WriteLine("RECV: timeout, no packet");
        return 8;
    }

    private static int Send(PlatformInterface platform, P2PInterface p2p, ProductUserId me, SocketId socket, string peerText)
    {
        if (string.IsNullOrEmpty(peerText))
        {
            Console.WriteLine("SEND: missing peer PUID argument");
            return 9;
        }
        ProductUserId peer = ProductUserId.FromString(peerText);
        byte[] data = Encoding.UTF8.GetBytes("hello from probe " + DateTime.UtcNow.ToString("o"));
        var so = new SendPacketOptions
        {
            LocalUserId = me,
            RemoteUserId = peer,
            SocketId = socket,
            Channel = 0,
            Data = new ArraySegment<byte>(data),
            AllowDelayedDelivery = true,
            Reliability = PacketReliability.ReliableOrdered,
            DisableAutoAcceptConnection = false,
        };
        Result sr = p2p.SendPacket(ref so);
        Console.WriteLine($"SEND: SendPacket={sr}, bytes {data.Length}");
        // Let the connection establish and the packet flush.
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < TimeSpan.FromSeconds(10))
        {
            platform.Tick();
            Thread.Sleep(16);
        }
        return sr == Result.Success ? 0 : 10;
    }
}
