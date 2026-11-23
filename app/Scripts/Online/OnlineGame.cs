using System.Collections.Concurrent;
using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Godot;
using Gathering;

namespace ArkhamUI;

// An online game (Yunke 11-22: "主机开局后把每一步的局面发给手机，手机显示桌面并把选择发回主机"). The host's machine runs
// the rules core and is the only authority (docs/ONLINE-SYNC-DESIGN.md): another player's table reads every state from the
// host and sends only its answers, as requests the host checks (the seat must be that player's, the step must be current)
// and runs on its own core. After any change the host tells everyone the new step, and each table reads it again.
//
// Messages: a JSON envelope, gzip-compressed, cut into chunks that fit one EOS packet (OnlineLink). The link is EOS P2P in
// play; QA runs two games on one machine over a local TCP link (--online-loop=host:<port> / join:<port>), since two games
// under one Windows account log in to EOS as the same player.
public static class OnlineGame
{
    // ---- the session -----------------------------------------------------------------------------------------------

    public static bool Active => game is not null;
    public static bool IsHost => Active && hosting;
    public static bool IsGuest => Active && !hosting;
    public static Guid? GameId => game;
    // the table's backend in a guest's game (the host's core over the link); null elsewhere (the local core)
    public static IGameBackend? Backend => IsGuest ? remote : null;
    // a new step at the host (its sequence): every table of this game reads it again
    public static event Action<long>? Changed;
    // the host ended the game or left: the guest's table goes back to the menu
    public static event Action<string>? Ended;

    static Guid? game;
    static bool hosting;
    static string hostPeer = "";
    static RemoteBackend? remote;
    // game seat (player id) → who plays it: "" the host, a member id, Everyone (anyone in the game), Ai (the host's AI —
    // offered, not yet usable). Yunke 11-23: "联机和一人多控结合" (docs/ONLINE-MULTISEAT-DESIGN.md): a player may control
    // several seats, the table switching between them as on one computer.
    static readonly Dictionary<Guid, string> owners = new();
    public const string Everyone = "*", Ai = "ai";
    // the players in this game besides the host (member id → name), whoever controls what
    static readonly Dictionary<string, string> peers = new();
    // the host: seats whose player left the room, handed to the host until they are back (seat → their own id)
    static readonly Dictionary<Guid, string> away = new();
    // the host, a new game: the deck from the host's own library for each seat no player sits in (room seat → deck key)
    public static readonly Dictionary<int, string> PlanDecks = new();
    // seats changed hands at the host (the tables read again; the menu's 调整控制权)
    public static event Action? OwnersChanged;
    static readonly HashSet<string> startedTo = new();
    static JsonElement startMessage;
    // the room's other players: their seats, names (for "等待 … 操作")
    static readonly Dictionary<Guid, string> seatNames = new();

    // The seats this machine answers for. The host also answers the setup steps of every seat (sending the starting deck
    // each player picked in the room, campaign settings) and the campaign screen between chapters; each player upgrades
    // their own deck (Yunke 11-22: "客人不能选自己的卡组（也有可能不能自己升级卡组）").
    public static bool Mine(Guid player) => !Active || (owners.TryGetValue(player, out var o) ? o == Everyone || o == (hosting ? "" : localId) || (o == Ai && hosting) : hosting);
    // a seat everyone controls: the table turns to it only when none of this player's own seats is asked
    public static bool Shared(Guid player) => Active && owners.TryGetValue(player, out var o) && o == Everyone;
    public static string Owner(Guid player) => owners.TryGetValue(player, out var o) ? o : "";
    public static IReadOnlyDictionary<string, string> Peers => peers;
    // the name of whoever controls a seat
    public static string ControllerName(string owner, string hostName = "主持人") =>
        owner == Everyone ? "大家" : owner == Ai ? "AI" : owner.Length == 0 ? hostName : peers.TryGetValue(owner, out var n) ? n : "其他玩家";
    public static readonly HashSet<string> HostSteps = new() { "PickScenarioSettings", "PickCampaignSettings", "ChooseDeck", "ContinueCampaign" };

    // ---- decks picked in the room (a new game) ------------------------------------------------------------------------

    public static string[] SeatCodes = Array.Empty<string>();   // a saved game's investigators by seat (the host's room)
    static readonly Dictionary<string, JsonElement> roomDecks = new();   // the host: member id → the deck they picked
    static readonly Dictionary<Guid, JsonElement> seatDecks = new();      // the host: game seat → the deck to send at ChooseDeck
    static string ownDeckKey = ""; static JsonElement ownDeck;
    // the deck this player picked in the room (a guest's side deck lives only here: TableScreen.CurrentSideSlots)
    public static JsonElement? OwnDeck => ownDeck.ValueKind == JsonValueKind.Object ? ownDeck : null;
    public static event Action? RoomDecksChanged;
    public static void ClearRoomDecks() { roomDecks.Clear(); ownDeckKey = ""; ownDeck = default; linked.Clear(); }

    public static string PackDeck(JsonElement deck)
    {
        using var ms = new MemoryStream();
        using (var gz = new GZipStream(ms, CompressionLevel.Optimal, leaveOpen: true)) gz.Write(Encoding.UTF8.GetBytes(deck.GetRawText()));
        return Convert.ToBase64String(ms.ToArray());
    }

    public static JsonElement? UnpackDeck(string packed)
    {
        if (packed.Length == 0) return null;
        try
        {
            using var ms = new MemoryStream(Convert.FromBase64String(packed));
            using var gz = new GZipStream(ms, CompressionMode.Decompress);
            using var outMs = new MemoryStream(); gz.CopyTo(outMs);
            using var doc = JsonDocument.Parse(outMs.ToArray());
            return doc.RootElement.Clone();
        }
        catch { return null; }
    }

    // a member's deck: stored with the room (lobby attributes), or sent over the direct connection
    static JsonElement? DeckOf(OnlineMember m) => UnpackDeck(m.DeckData) ?? (roomDecks.TryGetValue(m.Id, out var d) ? d : null);

    // ---- the direct connection (EOS P2P) in the room: the host pings everyone, each answers ------------------------------
    // The game itself needs it, so the room shows it per player and the host can start only when it works with everyone.
    static readonly HashSet<string> linked = new();   // the host: members who answered; a guest: the host, once pinged
    public static bool Linked(string memberId) => linked.Contains(memberId);
    public static void PingRoom()
    {
        if (Link is null || Active || Online.Service is not { } s || s.Room is not { } r) return;
        var me = r.Members.FirstOrDefault(m => m.Id == s.LocalId);
        if (me?.Host != true) return;
        foreach (var m in r.Members.Where(m => !m.Host)) Link.Send(m.Id, Envelope("evt", 0, "ping", default));
    }

    // this player picked a deck in the room: the host keeps it; a guest sends it to the host
    public static void ChooseOwnDeck(string key, JsonElement deck)
    {
        ownDeckKey = key; ownDeck = deck.Clone();
        if (Online.Service is { } s && s.Room is { } r && r.Members.FirstOrDefault(m => m.Id == s.LocalId) is { Host: true }) roomDecks[s.LocalId] = ownDeck;
        ResendDeck();
        // and with the room itself, which reaches the host even before the direct connection works
        if (Online.Service is { } svc) _ = StoreDeck(svc, ownDeck);
    }

    static async Task StoreDeck(IOnlineService s, JsonElement deck)
    {
        try { await s.SetDeckDataAsync(PackDeck(deck)); GD.Print("ONLINE_DECK_STORED"); }
        catch (Exception e) { GD.Print("ONLINE_DECK_STORE_FAIL " + e.Message); }
    }

    public static void ResendDeck()
    {
        if (Link is null || ownDeck.ValueKind != JsonValueKind.Object || Online.Service is not { } s || s.Room is not { } r) return;
        var host = r.Members.FirstOrDefault(m => m.Host);
        if (host is null || host.Id == s.LocalId) return;
        Link.Send(host.Id, Envelope("evt", 0, "deck", JsonSerializer.SerializeToElement(new { deck = ownDeck })));
    }

    // the host answers a guest seat's ChooseDeck with the deck that player picked
    public static JsonElement? SeatDeck(Guid player) => IsHost && seatDecks.TryGetValue(player, out var d) ? d : null;
    public static bool Answers(Guid player, string tag) => !Active || (hosting && HostSteps.Contains(tag)) || (Mine(player) && !(IsGuest && HostSteps.Contains(tag)));
    public static string SeatName(Guid player) => seatNames.TryGetValue(player, out var n) ? n : "其他玩家";
    static string localId = "";

    // the room's seats no player sits in that will be played (a new game: with an investigator; a saved game: every seat of the save)
    public static IEnumerable<OnlineSeatPlan> Planned(OnlineRoom room) =>
        Enumerable.Range(0, room.Seats).Where(i => room.Members.All(m => m.Seat != i))
            .Select(i => room.Plan(i) ?? new OnlineSeatPlan(i, "", "", ""))
            .Where(p => room.GameId is null ? p.InvestigatorCode.Length > 0 : p.Seat < SeatCodes.Length);

    public static bool CanStart(OnlineRoom room, out string why)
    {
        why = "";
        if (room.Members.Count < 2) { why = "还没有其他玩家加入"; return false; }
        // a player whose game is another version (an older copy that does not check when joining, Yunke 11-23)
#if ARKHAM_EOS
        if (Online.Service is EosOnlineService && room.Members.Where(m => !m.Host).FirstOrDefault(m => !EosOnlineService.SameVersion(m.Version, Updater.VersionTag)) is { } old)
        { why = old.Name + " 的游戏版本和你不同，请对方更新"; return false; }
#endif
        if (room.Members.Any(m => m.Seat < 0 || m.Seat >= room.Seats)) { why = "还有玩家没有入座"; return false; }
        // seats no player sits in: each needs who controls it (a player who is here), and in a new game an investigator and deck
        foreach (var p in Planned(room))
        {
            if (p.Controller == Ai) { why = "AI 控制还在开发中，请把座位 " + (p.Seat + 1) + " 交给玩家"; return false; }
            if (p.Controller is not ("" or Everyone) && room.Members.All(m => m.Id != p.Controller)) { why = "座位 " + (p.Seat + 1) + " 的控制者不在房间里"; return false; }
            if (room.GameId is null && !PlanDecks.ContainsKey(p.Seat)) { why = "请重新为座位 " + (p.Seat + 1) + " 选择调查员"; return false; }
        }
        if (room.GameId is null)
        {
            if (room.Members.Any(m => m.InvestigatorCode.Length == 0)) { why = "还有玩家没有选择调查员"; return false; }
            var codes = room.Members.Select(m => CoreCatalog.Normalize(m.InvestigatorCode)).Concat(Planned(room).Select(p => CoreCatalog.Normalize(p.InvestigatorCode))).ToList();
            if (codes.Distinct().Count() < codes.Count) { why = "两个座位选了同一位调查员"; return false; }
            var hostM = room.Members.FirstOrDefault(m => m.Host);
            if (hostM is not null && ownDeck.ValueKind != JsonValueKind.Object) { why = "请先选择你的调查员"; return false; }
            if (room.Members.Where(m => !m.Host).FirstOrDefault(m => DeckOf(m) is null) is { } noDeck) { why = "正在接收 " + noDeck.Name + " 的牌组…"; return false; }
        }
        if (room.Members.Where(m => !m.Host).Any(m => !m.Ready)) { why = "还有玩家没有准备"; return false; }
        if (Link is null) { why = "联机连接还没就绪"; return false; }
        if (room.Members.Where(m => !m.Host).FirstOrDefault(m => !linked.Contains(m.Id)) is { } far) { why = "正在和 " + far.Name + " 建立直连…"; return false; }
        return true;
    }

    // the host's "开始调查" / "继续调查": make (or open) the game, tell the room who plays which seat, and sit down
    // set by the prepare screen that opened the room: makes the game for this many seats, with the host's own deck keys
    // by seat ("" for a seat whose deck comes from another player)
    public static Func<int, string[], Task<Guid?>>? CreateNewGame;
    public static async void HostStart(OnlineRoom room)
    {
        if (Link is not { } link || Online.Service is not { } svc) return;
        try
        {
            Guid? id = room.GameId;
            foreach (var m in room.Members.Where(m => !m.Host)) peers[m.Id] = m.Name;
            // a new game: one game seat per taken room seat, in seat order — a seated player with the deck they picked, or a
            // seat the host arranged (its deck from the host's library, played by its controller)
            var taken = Enumerable.Range(0, room.Seats)
                .Select(i => (Seat: i, Member: room.Members.FirstOrDefault(x => x.Seat == i), Plan: room.Plan(i)))
                .Where(x => x.Member is not null || x.Plan is { InvestigatorCode.Length: > 0 }).ToList();
            if (id is null)
            {
                if (CreateNewGame is null) { App.I.Toast("请从准备界面开始新的联机调查", true); return; }
                id = await CreateNewGame(taken.Count, taken.Select(x => x.Member is { } m ? (m.Host ? ownDeckKey : "") : PlanDecks.GetValueOrDefault(x.Seat, "")).ToArray());
                if (id is null) return;
            }
            var st = await App.I.S!.Backend.Open(id.Value);
            var seats = st.Seats.OrderBy(s => s.Number).ToArray();
            var assign = new Dictionary<Guid, string>();
            var names = new Dictionary<Guid, string>();
            var decks = new Dictionary<Guid, JsonElement>();
            for (int i = 0; i < seats.Length; i++)
            {
                // a saved game: room seat i is game seat i; a new game: the i-th taken room seat
                int roomSeat = room.GameId is null ? (i < taken.Count ? taken[i].Seat : -1) : i;
                var m = room.Members.FirstOrDefault(x => x.Seat == roomSeat && roomSeat >= 0);
                string owner = m is not null ? (m.Host ? "" : m.Id) : room.Plan(roomSeat)?.Controller ?? "";
                if (owner.Length > 0 && owner != Everyone && owner != Ai && room.Members.All(x => x.Id != owner)) owner = "";
                assign[seats[i].Player] = owner;
                names[seats[i].Player] = m?.Name ?? ControllerName(owner, svc.LocalName);
                if (room.GameId is null && m is not null && !m.Host && DeckOf(m) is { } d) decks[seats[i].Player] = d;
            }
            BeginHost(id.Value, link, svc.LocalId, assign, names, room.Members.Where(x => !x.Host).ToDictionary(x => x.Id, x => x.Name));
            foreach (var (p, d) in decks) seatDecks[p] = d;
            App.I.CloseAllModals();
            App.I.Go(new TableScreen(id.Value));
        }
        catch (Exception e) { App.I.Toast("开局失败：" + e.Message, true, 5); GD.PushError(e.ToString()); }
    }

    public static void BeginHost(Guid id, OnlineLink link, string me, Dictionary<Guid, string> assign, Dictionary<Guid, string> names, Dictionary<string, string>? players = null)
    {
        End(null);
        game = id; hosting = true; localId = me; Link = link;
        foreach (var (p, o) in assign) owners[p] = o;
        foreach (var (p, n) in names) seatNames[p] = n;
        // everyone in the game: the given players (the room), and whoever controls a seat
        if (players is not null) foreach (var (k, v) in players) peers[k] = v;
        foreach (var o in assign.Values) if (o.Length > 0 && o != Everyone && o != Ai && !peers.ContainsKey(o)) peers[o] = names.FirstOrDefault(n => assign.GetValueOrDefault(n.Key) == o).Value ?? "其他玩家";
        hostName = Online.Service?.LocalName ?? "主持人";
        startMessage = StartMessage();
        foreach (var peer in peers.Keys) SendStart(peer);
        SaveOwners();
        GD.Print("ONLINE_HOST game=" + id + " seats=" + string.Join(",", assign.Select(a => OwnerTag(a.Value))));
    }
    static string hostName = "主持人";
    static string OwnerTag(string o) => o.Length == 0 ? "host" : o == Everyone ? "all" : o == Ai ? "ai" : "guest";

    static JsonElement StartMessage() => JsonSerializer.SerializeToElement(new
    {
        game = game!.Value.ToString("D"),
        host = hostName,
        seats = owners.Select(a => new { player = a.Key.ToString("D"), owner = a.Value, name = seatNames.GetValueOrDefault(a.Key, "") }).ToArray(),
        players = peers.Select(p => new { id = p.Key, name = p.Value }).ToArray(),
    });

    // the host hands a seat to someone else (调整控制权): everyone is told, and every table reads again
    public static void SetOwner(Guid player, string owner)
    {
        if (!IsHost || !owners.ContainsKey(player)) return;
        owners[player] = owner; away.Remove(player);
        seatNames[player] = ControllerName(owner, hostName);
        BroadcastOwners();
        GD.Print("ONLINE_OWNER " + player + " " + OwnerTag(owner));
    }

    static void BroadcastOwners()
    {
        startMessage = StartMessage();
        if (Link is not null) foreach (var peer in peers.Keys) Link.Send(peer, Envelope("evt", 0, "owners", startMessage));
        SaveOwners();
        OwnersChanged?.Invoke();
        Changed?.Invoke(-1);
    }

    static void ReadOwners(JsonElement start)
    {
        owners.Clear(); seatNames.Clear(); peers.Clear();
        hostName = J.String(start, "host").Length > 0 ? J.String(start, "host") : "主持人";
        foreach (var p in J.Items(J.Get(start, "players"))) peers[J.String(p, "id")] = J.String(p, "name");
        foreach (var s in J.Items(J.Get(start, "seats")))
        {
            var p = Guid.Parse(J.String(s, "player"));
            owners[p] = J.String(s, "owner"); seatNames[p] = J.String(s, "name");
        }
    }

    // who controlled each seat of a saved online game (by seat number), for the room when it goes on
    static string OwnersFile(Guid id) => System.IO.Path.Combine(App.I.DataRoot, "setups", id.ToString("D") + ".online.json");
    static void SaveOwners() => _ = SaveOwnersAsync();
    static async Task SaveOwnersAsync()
    {
        if (!IsHost || game is not { } id || App.I.S?.Backend is not { } B) return;
        try
        {
            var st = await B.Open(id);
            var bySeat = st.Seats.ToDictionary(s => s.Number.ToString(), s => owners.GetValueOrDefault(s.Player, ""));
            System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(OwnersFile(id))!);
            System.IO.File.WriteAllText(OwnersFile(id), JsonSerializer.Serialize(bySeat));
        }
        catch (Exception e) { GD.Print("ONLINE_OWNERS_SAVE_FAIL " + e.Message); }
    }
    // room seat (0-based) → controller saved for that game; empty when none
    public static Dictionary<int, string> SavedOwners(Guid id)
    {
        try
        {
            if (!System.IO.File.Exists(OwnersFile(id))) return new();
            var d = JsonSerializer.Deserialize<Dictionary<string, string>>(System.IO.File.ReadAllText(OwnersFile(id))) ?? new();
            return d.Where(kv => int.TryParse(kv.Key, out _)).ToDictionary(kv => int.Parse(kv.Key) - 1, kv => kv.Value);
        }
        catch { return new(); }
    }

    // ---- QA: seats by number → host / guest / all ----
    public static Dictionary<Guid, string> QaAssign(TableState st, string spec)
    {
        var want = new Dictionary<int, string>();   // a later entry wins
        foreach (var x in spec.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(x => x.Split('=')).Where(x => x.Length == 2 && int.TryParse(x[0], out _))) want[int.Parse(x[0])] = x[1];
        return st.Seats.ToDictionary(s => s.Player, s => want.GetValueOrDefault(s.Number, "host") switch { "guest" => TcpLink.GuestId, "all" => Everyone, _ => "" });
    }

    static void SendStart(string peer)
    {
        if (Link is null) return;
        startedTo.Add(peer);
        Link.Send(peer, Envelope("evt", 0, "start", startMessage));
    }

    // a member who joins (or rejoins) the room after the start is told again
    public static void RoomMembers(IEnumerable<string> memberIds)
    {
        if (!IsHost) return;
        var now = memberIds.Where(id => id.Length > 0).ToHashSet();
        startedTo.IntersectWith(now);   // someone who left and came back is told again
        // a player who left: their seats go to the host until they are back (docs/ONLINE-MULTISEAT-DESIGN.md §3)
        bool moved = false;
        foreach (var (p, o) in owners.ToList())
            if (o.Length > 0 && o != Everyone && o != Ai && !now.Contains(o)) { away[p] = o; owners[p] = ""; moved = true; GD.Print("ONLINE_AWAY " + p + " → host"); }
        foreach (var (p, o) in away.ToList())
            if (now.Contains(o)) { owners[p] = o; away.Remove(p); moved = true; GD.Print("ONLINE_BACK " + p); }
        if (moved) BroadcastOwners();
        foreach (var id in now)
            if (peers.ContainsKey(id) && !startedTo.Contains(id)) SendStart(id);
    }

    // This player left the online game's table (back to the menu): the game is over for this machine. The host tells the
    // others, whose tables go back to the menu too (Yunke 11-22: a second game in the same run hung at "正在建立直连…").
    public static void Leave(Guid table)
    {
        if (!Active || game != table) return;
        if (IsHost && Link is not null)
            foreach (var peer in peers.Keys) Link.Send(peer, Envelope("evt", 0, "end", default));
        GD.Print("ONLINE_LEAVE " + table + (hosting ? " host" : " guest"));
        End(null);
    }

    static void BeginGuest(string host, JsonElement start)
    {
        if (Link is null) return;
        var id = Guid.Parse(J.String(start, "game"));
        if (game == id && !hosting) return;   // told again (a repeat): already at the table
        End(null);
        game = id; hosting = false; hostPeer = host; localId = Link.LocalId;
        ReadOwners(start);
        remote = new RemoteBackend(Link, host);
        var mySeat = owners.FirstOrDefault(o => o.Value == localId).Key;
        if (mySeat == Guid.Empty) mySeat = owners.FirstOrDefault(o => o.Value == Everyone).Key;
        GD.Print("ONLINE_GUEST game=" + id + " seat=" + mySeat);
        App.I.CloseAllModals();
        App.I.Go(new TableScreen(id, mySeat == Guid.Empty ? null : mySeat));
    }

    public static void End(string? why)
    {
        bool was = Active;
        game = null; hosting = false; hostPeer = ""; remote?.Fail("联机对局已结束"); remote = null;
        owners.Clear(); seatNames.Clear(); startedTo.Clear(); seatDecks.Clear(); peers.Clear(); away.Clear();
        if (was && why is not null) Ended?.Invoke(why);
    }

    // the host after any step it took (its own table, a guest's request): everyone reads the new state
    public static void HostChanged(long sequence)
    {
        if (!IsHost || Link is null) return;
        var payload = JsonSerializer.SerializeToElement(new { seq = sequence });
        foreach (var peer in peers.Keys) Link.Send(peer, Envelope("evt", 0, "changed", payload));
    }

    // ---- the link -----------------------------------------------------------------------------------------------------

    public static OnlineLink? Link { get; private set; }

    public static void UseLink(OnlineLink link)
    {
        if (Link == link || Link is TcpLink) return;   // QA's local link wins over EOS
        Link = link;
        link.Message += OnMessage;
    }

    static void OnMessage(string from, JsonElement msg)
    {
        string t = J.String(msg, "t"), m = J.String(msg, "m");
        long id = J.Get(msg, "id").ValueKind == JsonValueKind.Number ? J.Get(msg, "id").GetInt64() : 0;
        if (t == "res") { remote?.Resolve(id, msg); return; }
        if (t == "evt")
        {
            if (m == "start") { BeginGuest(from, J.Get(msg, "a")); return; }
            if (m == "savefile") { SaveTransfer.Received(from, J.Get(msg, "a")); return; }      // a save sent from a computer
            if (m == "savefile-ok") { SaveTransfer.Acked(from, J.Get(msg, "a")); return; }
            if (m == "deck" && !Active) { roomDecks[from] = J.Get(J.Get(msg, "a"), "deck").Clone(); GD.Print("ONLINE_DECK from " + from); RoomDecksChanged?.Invoke(); return; }
            if (m == "ping") { if (linked.Add(from)) { GD.Print("ONLINE_PING from " + from); RoomDecksChanged?.Invoke(); } Link?.Send(from, Envelope("evt", 0, "pong", default)); return; }
            if (m == "pong") { if (linked.Add(from)) { GD.Print("ONLINE_PONG from " + from); RoomDecksChanged?.Invoke(); } return; }
            if (m == "changed" && IsGuest && from == hostPeer) { Changed?.Invoke(J.Get(J.Get(msg, "a"), "seq").GetInt64()); return; }
            if (m == "end" && IsGuest && from == hostPeer) { End("主持人结束了联机对局"); return; }
            if (m == "owners" && IsGuest && from == hostPeer) { ReadOwners(J.Get(msg, "a")); GD.Print("ONLINE_OWNERS " + string.Join(",", owners.Values.Select(OwnerTag))); OwnersChanged?.Invoke(); Changed?.Invoke(-1); return; }
            return;
        }
        if (t == "req" && IsHost) _ = Serve(from, id, m, J.Get(msg, "a"));
    }

    public static JsonElement Envelope(string t, long id, string m, JsonElement a, string? error = null)
    {
        var o = new JsonObject { ["t"] = t, ["id"] = id, ["m"] = m };
        if (a.ValueKind != JsonValueKind.Undefined) o["a"] = JsonNode.Parse(a.GetRawText());
        if (error is not null) o["e"] = error;
        return JsonSerializer.SerializeToElement(o);
    }

    // ---- the host: a guest's request, run on the host's core ---------------------------------------------------------

    static readonly HashSet<string> Mutating = new() { "choice", "answer", "amounts", "continue", "defaultSettings", "selectDeck", "upgradeDeck", "upgradeDeckList", "keepDeck", "replace", "beginUpgrade" };

    static async Task Serve(string from, long id, string method, JsonElement a)
    {
        if (Link is not { } link) return;
        JsonElement result = default; string? error = null;
        try
        {
            var B = App.I.S!.Backend;
            var g = game!.Value;
            if (!peers.ContainsKey(from)) throw new InvalidOperationException("你不在这局联机调查里");
            Guid player = Guid.TryParse(J.String(a, "player"), out var pl) ? pl : Guid.Empty;
            TableState? basis = null;
            if (Mutating.Contains(method) || method is "viewSection" or "forced" or "logs" or "side")
            {
                if (player == Guid.Empty) throw new InvalidOperationException("缺少座位");
                if (Mutating.Contains(method) && (!owners.TryGetValue(player, out var o) || (o != from && o != Everyone))) throw new InvalidOperationException("这个座位不归你操作");
                basis = await B.Open(g, player);
                long seq = J.Get(a, "seq").ValueKind == JsonValueKind.Number ? J.Get(a, "seq").GetInt64() : -1;
                if (Mutating.Contains(method) && seq != basis.Sequence) throw new InvalidOperationException("Local state changed; reload the latest committed game");
            }
            bool follow = J.Get(a, "follow").ValueKind == JsonValueKind.True;
            TableState? next = method switch
            {
                "open" => await B.Open(g, player == Guid.Empty ? null : player, follow),
                "choice" => await B.Choice(basis!, J.Int(a, "index"), follow),
                "answer" => await B.Answer(basis!, J.Get(a, "answer").Clone(), follow),
                "amounts" => await B.Amounts(basis!, J.Props(J.Get(a, "values")).ToDictionary(p => p.Name, p => p.Value.GetInt32())),
                "continue" => await B.Continue(basis!),
                "defaultSettings" => await B.DefaultSettings(basis!),
                "selectDeck" => await B.SelectDeck(basis!, J.Get(a, "deck").Clone()),
                "upgradeDeck" => await B.UpgradeDeck(basis!, J.String(a, "inv"), J.Get(a, "deck").Clone()),
                "upgradeDeckList" => await B.UpgradeDeck(basis!, J.Get(a, "deck").Clone()),
                "keepDeck" => await B.KeepDeck(basis!, J.String(a, "inv")),
                "replace" => await B.ReplaceInvestigator(basis!, J.String(a, "inv"), J.Get(a, "deck").Clone()),
                "beginUpgrade" => await B.BeginUpgrade(basis!),
                _ => null,
            };
            if (next is not null) result = StateJson(next);
            else result = method switch
            {
                "viewSection" => await B.ViewSection(basis!, J.String(a, "section")),
                "forced" => JsonSerializer.SerializeToElement((await B.ForcedSources(basis!, J.Get(a, "since").GetInt64())).Select(x => new[] { x.Code, x.Id })),
                "logs" => JsonSerializer.SerializeToElement(await B.Logs(basis!)),
                "buildSchema" => await B.BuildSchema(J.String(a, "inv")),
                "validateDeck" => await B.ValidateDeck(J.String(a, "inv"), J.String(a, "schema"), J.Get(a, "deck").Clone()),
                "prepareDeck" => await B.PrepareStandaloneDeck(J.Get(a, "deck").Clone()),
                "side" => Side(B, basis!),
                _ => throw new InvalidOperationException("未知的联机请求：" + method),
            };
            if (next is not null && Mutating.Contains(method))
            {
                GD.Print("ONLINE_SERVE " + method + " seq=" + next.Sequence + " seat=" + (basis?.Seats.FirstOrDefault(x => x.Player == player)?.Number ?? 0) + " owner=" + OwnerTag(owners.GetValueOrDefault(player, "")));
                HostChanged(next.Sequence);
                Changed?.Invoke(next.Sequence);   // the host's own table reads it again
            }
        }
        catch (Exception e) { error = e.Message; GD.Print("ONLINE_SERVE_FAIL " + method + " " + e.Message); }
        link.Send(from, Envelope("res", id, method, result, error));
    }

    // a guest's upgrade or replacement reads the save: its decks, the investigators used, the lead (RemoteBackend)
    static JsonElement Side(IGameBackend B, TableState basis)
    {
        var game = J.Get(J.Get(B.SavedDocument(basis), "snapshot"), "game");
        var decks = new Dictionary<string, Dictionary<string, int>>();
        foreach (var p in J.Props(J.Get(J.Get(game, "gameEntities"), "investigators")))
            try { decks[p.Name] = B.CurrentDeck(basis, p.Name); } catch { }
        return JsonSerializer.SerializeToElement(new { used = B.UsedInvestigators(basis), decks, lead = J.String(game, "gameLeadInvestigatorId") });
    }

    public static JsonElement StateJson(TableState s) => JsonSerializer.SerializeToElement(new
    {
        id = s.Id, player = s.Player, seq = s.Sequence, rev = s.Revision, view = s.View,
        seats = s.Seats.Select(x => new { player = x.Player, number = x.Number, asked = x.HasQuestion }).ToArray(),
        actionStart = s.ActionStartSequence, previous = s.PreviousSequence, round = s.Round, shuffled = s.Shuffled,
    });

    public static TableState StateFrom(JsonElement j) => new(
        Guid.Parse(J.String(j, "id")), Guid.Parse(J.String(j, "player")), J.Get(j, "seq").GetInt64(), J.Get(j, "rev").GetInt64(),
        J.Get(j, "view").Clone(),
        J.Items(J.Get(j, "seats")).Select(x => new ClientSeat(Guid.Parse(J.String(x, "player")), J.Int(x, "number"), J.Get(x, "asked").ValueKind == JsonValueKind.True)).ToArray(),
        J.Get(j, "actionStart").ValueKind == JsonValueKind.Number ? J.Get(j, "actionStart").GetInt64() : null,
        J.Get(j, "previous").ValueKind == JsonValueKind.Number ? J.Get(j, "previous").GetInt64() : null,
        J.Int(j, "round"),
        J.Get(j, "shuffled").ValueKind == JsonValueKind.Array ? J.Items(J.Get(j, "shuffled")).Select(x => x.GetString() ?? "").ToArray() : null);

    // ---- QA: two games on one machine over a local TCP link ---------------------------------------------------------

    public static void QaBoot(Node root)
    {
        foreach (var a in Godot.OS.GetCmdlineUserArgs())
        {
            if (!a.StartsWith("--online-loop=", StringComparison.Ordinal)) continue;
            var p = a["--online-loop=".Length..].Split(':');
            int port = int.Parse(p[1]);
            var link = p[0] == "host" ? TcpLink.Host(port) : TcpLink.Join(port);
            root.AddChild(link.Pump());
            UseLink(link);
            GD.Print("ONLINE_LOOP " + p[0] + " " + port);
        }
        // QA: the deck picker as a guest would see it
        QaScript.Register("online-pickdeck", _ => { if (Online.Service is { } svc) OnlineDialogs.QaPickDeck(svc); });
        // QA (loop guest): send a deck from this library to the host, as picking one in the room does
        QaScript.Register("online-send-deck", key =>
        {
            if (Link is null || App.I.S is not { } S || !S.Decks.Has(key)) { GD.PrintErr("QA online-send-deck: no link or deck " + key); return; }
            ownDeck = S.Decks.Get(key).Clone(); ownDeckKey = key;
            Link.Send(TcpLink.HostId, Envelope("evt", 0, "deck", JsonSerializer.SerializeToElement(new { deck = ownDeck })));
        });
        // QA (loop host): a new online game made the room's way — seat 1 the host's deck by key, seat 2 the deck the guest sent
        QaScript.Register("online-host-room", async key =>
        {
            if (Link is null || CreateNewGame is null) { GD.PrintErr("QA online-host-room: no link or prepare screen"); return; }
            for (int i = 0; i < 300 && !roomDecks.ContainsKey(TcpLink.GuestId); i++) await Task.Delay(100);
            if (!roomDecks.TryGetValue(TcpLink.GuestId, out var gd)) { GD.PrintErr("QA online-host-room: no guest deck"); return; }
            if (await CreateNewGame(2, new[] { key, "" }) is not { } id) return;
            var st = await App.I.S!.Backend.Open(id);
            var seats = st.Seats.OrderBy(x => x.Number).ToArray();
            BeginHost(id, Link, "", new() { [seats[0].Player] = "", [seats[1].Player] = TcpLink.GuestId }, new() { [seats[0].Player] = "主持", [seats[1].Player] = "玩家二" }, new() { [TcpLink.GuestId] = "玩家二" });
            seatDecks[seats[1].Player] = gd;
            App.I.Go(new TableScreen(id));
        });
        // QA (EOS room): pick a deck as the room does, then show what the room holds for each member
        QaScript.Register("online-store-deck", async key =>
        {
            if (App.I.S is not { } S || !S.Decks.Has(key) || Online.Service is not { } svc) { GD.PrintErr("QA online-store-deck: no deck or service"); return; }
            ChooseOwnDeck(key, S.Decks.Get(key));
            await Task.Delay(4000);
            foreach (var m in svc.Room?.Members ?? Array.Empty<OnlineMember>())
                GD.Print("QA_ROOM_DECK " + m.Name + " packed=" + m.DeckData.Length + " unpacked=" + (UnpackDeck(m.DeckData) is { } d ? J.String(d, "investigator_code") : "none"));
        });
        // QA (loop host): send a save to the loop's guest the way 发送到手机 does
        QaScript.Register("save-send-loop", async id =>
        {
            if (Link is null) return;
            for (int i = 0; i < 200 && Link is TcpLink { Connected: false }; i++) await Task.Delay(100);
            var st = await App.I.S!.Backend.Open(Guid.Parse(id));
            Link.Send(TcpLink.GuestId, Envelope("evt", 0, "savefile", JsonSerializer.SerializeToElement(new { title = "从电脑接收的测试存档", save = App.I.S!.Backend.SavedDocument(st) })));
            GD.Print("SAVE_SENT loop");
        });
        QaScript.Register("online-room-host", a => OnlineDialogs.Host(a.Length > 0 ? a : "联机测试", null, 2));   // QA: the live room as host (EOS)
        QaScript.Register("online-host-start", async a =>
        {
            // QA: host the game open on this table, seat 2 (or the given number) played by the loop's guest
            if (Link is null || TableScreen.Current is not { } t) { GD.PrintErr("QA online-host-start: no link or table"); return; }
            var id = t.QaGameId; int guestSeat = int.TryParse(a, out var n) ? n : 2;
            if (a == "asked") { var st0 = await App.I.S!.Backend.Open(id); guestSeat = st0.Seats.FirstOrDefault(x => x.HasQuestion)?.Number ?? 2; }
            var st = await App.I.S!.Backend.Open(id);
            a = a.Replace("asked=", (st.Seats.FirstOrDefault(x => x.HasQuestion)?.Number ?? 1) + "=");   // QA: the seat being asked now
            // or a spec of seat number = host / guest / all (online-host-start:1=host,2=guest,3=all)
            var assign = a.Contains('=') ? QaAssign(st, a) : st.Seats.OrderBy(s => s.Number).ToDictionary(s => s.Player, s => s.Number == guestSeat ? TcpLink.GuestId : "");
            var names = st.Seats.ToDictionary(s => s.Player, s => assign[s.Player] == TcpLink.GuestId ? "玩家二" : assign[s.Player] == Everyone ? "大家" : "主持");
            // wait for the guest's connection
            for (int i = 0; i < 200 && !((TcpLink)Link).Connected; i++) await Task.Delay(100);
            BeginHost(id, Link, "", assign, names, new() { [TcpLink.GuestId] = "玩家二" });
            Changed?.Invoke(st.Sequence);
        });
        // QA (loop host): hand seat n to host / guest / all (调整控制权)
        QaScript.Register("online-set-owner", async a =>
        {
            if (!IsHost || game is not { } g) return;
            var st = await App.I.S!.Backend.Open(g);
            foreach (var (p, o) in QaAssign(st, a)) if (a.Split(',').Any(x => x.StartsWith(st.Seats.First(s => s.Player == p).Number + "="))) SetOwner(p, o);
        });
        // QA (loop host): the guest leaves / comes back (as the room would say)
        QaScript.Register("online-qa-members", a => RoomMembers(a == "gone" ? Array.Empty<string>() : new[] { TcpLink.GuestId }));
    }
}

// The guest's table backend: every call goes to the host as a request and waits for its answer.
public sealed class RemoteBackend : IGameBackend
{
    readonly OnlineLink link;
    readonly string host;
    long nextId;
    readonly Dictionary<long, TaskCompletionSource<JsonElement>> waiting = new();

    public RemoteBackend(OnlineLink link, string host) { this.link = link; this.host = host; }
    public double LastOperationMs { get; private set; }

    public void Resolve(long id, JsonElement msg)
    {
        if (!waiting.Remove(id, out var tcs)) return;
        var e = J.String(msg, "e");
        if (e.Length > 0) tcs.TrySetException(new InvalidOperationException(e));
        else tcs.TrySetResult(J.Get(msg, "a").ValueKind == JsonValueKind.Undefined ? default : J.Get(msg, "a").Clone());
    }

    public void Fail(string why)
    {
        foreach (var t in waiting.Values) t.TrySetException(new InvalidOperationException(why));
        waiting.Clear();
    }

    async Task<JsonElement> Call(string method, object args)
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        long id = ++nextId;
        var tcs = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        waiting[id] = tcs;
        link.Send(host, OnlineGame.Envelope("req", id, method, JsonSerializer.SerializeToElement(args)));
        var done = await Task.WhenAny(tcs.Task, Task.Delay(TimeSpan.FromSeconds(90)));
        if (done != tcs.Task) { waiting.Remove(id); throw new TimeoutException("主持人没有回应（网络中断？）"); }
        var r = await tcs.Task;
        LastOperationMs = watch.Elapsed.TotalMilliseconds;
        GD.Print("ONLINE_CALL " + method + " ms=" + LastOperationMs.ToString("F0"));
        return r;
    }

    async Task<TableState> State(string method, object args)
    {
        var st = OnlineGame.StateFrom(await Call(method, args));
        // an upgrade or a replacement next: read what it needs from the host's save now (those reads are not async)
        var q = st.Question.ValueKind == JsonValueKind.Object ? st.Question.GetRawText() : "";
        if (q.Contains("UpgradeDeck") || q.Contains("ContinueCampaign") || q.Contains("Replace"))
        {
            try { side = await Call("side", new { player = P(st.Player) }); sideSeq = st.Sequence; }
            catch (Exception e) { GD.Print("ONLINE_SIDE_FAIL " + e.Message); }
        }
        return st;
    }
    JsonElement side; long sideSeq = -1;
    static string P(Guid g) => g.ToString("D");

    public Task<TableState> Open(Guid id, Guid? selected = null, bool followAsked = false) => State("open", new { player = selected is { } s ? P(s) : "", follow = followAsked });
    public Task<TableState> Choice(TableState b, int index, bool followAsked = false) => State("choice", new { player = P(b.Player), seq = b.Sequence, index, follow = followAsked });
    public Task<TableState> Answer(TableState b, JsonElement answer, bool followAsked = false) => State("answer", new { player = P(b.Player), seq = b.Sequence, answer, follow = followAsked });
    public Task<TableState> Amounts(TableState b, IReadOnlyDictionary<string, int> values) => State("amounts", new { player = P(b.Player), seq = b.Sequence, values });
    public Task<TableState> Continue(TableState b) => State("continue", new { player = P(b.Player), seq = b.Sequence });
    public Task<TableState> DefaultSettings(TableState b) => State("defaultSettings", new { player = P(b.Player), seq = b.Sequence });
    public Task<TableState> SelectDeck(TableState b, JsonElement deck) => State("selectDeck", new { player = P(b.Player), seq = b.Sequence, deck });
    public Task<TableState> UpgradeDeck(TableState b, string investigatorId, JsonElement deck) => State("upgradeDeck", new { player = P(b.Player), seq = b.Sequence, inv = investigatorId, deck });
    public Task<TableState> UpgradeDeck(TableState b, JsonElement deck) => State("upgradeDeckList", new { player = P(b.Player), seq = b.Sequence, deck });
    public Task<TableState> KeepDeck(TableState b, string investigatorId) => State("keepDeck", new { player = P(b.Player), seq = b.Sequence, inv = investigatorId });
    public Task<TableState> ReplaceInvestigator(TableState b, string investigatorId, JsonElement deck) => State("replace", new { player = P(b.Player), seq = b.Sequence, inv = investigatorId, deck });
    public Task<TableState> BeginUpgrade(TableState b) => State("beginUpgrade", new { player = P(b.Player), seq = b.Sequence });

    static InvalidOperationException HostOnly() => new("联机对局中只有主持人可以撤回");
    public Task<TableState?> UndoStep(TableState basis) => throw HostOnly();
    public Task<TableState> UndoBoundary(TableState basis, string boundary) => throw HostOnly();
    public Task<TableState> UndoToLogRow(TableState basis, long rowSequence) => throw HostOnly();
    public Task<TableState> RevertTo(TableState trial, long sequence) => throw HostOnly();
    public Task<TableState> Debug(TableState basis, JsonElement command) => throw HostOnly();
    public Task<JsonElement> DebugSchema(TableState basis) => throw HostOnly();

    public Task<JsonElement> ViewSection(TableState b, string section) => Call("viewSection", new { player = P(b.Player), section });
    public async Task<(string Code, string Id)[]> ForcedSources(TableState b, long since)
        => J.Items(await Call("forced", new { player = P(b.Player), since })).Select(x => (x[0].GetString() ?? "", x[1].GetString() ?? "")).ToArray();
    public async Task<ClientLogHistory> Logs(TableState b)
        => JsonSerializer.Deserialize<ClientLogHistory>((await Call("logs", new { player = P(b.Player) })).GetRawText())!;
    public Task<JsonElement> BuildSchema(string investigator) => Call("buildSchema", new { inv = investigator });
    public Task<JsonElement> ValidateDeck(string investigator, string schemaId, JsonElement deck) => Call("validateDeck", new { inv = investigator, schema = schemaId, deck });
    public Task<JsonElement> PrepareStandaloneDeck(JsonElement deck) => Call("prepareDeck", new { deck });
    public Task<JsonElement> PureRaw(string method, JsonElement request) => throw new InvalidOperationException("联机对局中不可用");

    // read straight from the save on the host's machine; the host handles the steps that need them (OnlineGame.HostSteps)
    static InvalidOperationException NotRead() => new("还没有读到主持人的存档信息");
    JsonElement Side(TableState basis) => side.ValueKind == JsonValueKind.Object && sideSeq == basis.Sequence ? side : throw NotRead();
    public HashSet<string> UsedInvestigators(TableState basis) => J.Items(J.Get(Side(basis), "used")).Select(x => x.GetString() ?? "").ToHashSet();
    public Dictionary<string, int> CurrentDeck(TableState basis, string investigatorId)
        => J.Props(J.Get(J.Get(Side(basis), "decks"), investigatorId)).ToDictionary(p => p.Name, p => p.Value.GetInt32());
    public JsonElement SavedDocument(TableState basis)
        => JsonSerializer.SerializeToElement(new { snapshot = new { game = new { gameLeadInvestigatorId = J.String(Side(basis), "lead") } } });
}

// Carries messages between players: a whole JSON message is gzip-compressed and cut into chunks small enough for one EOS
// packet; the receiver puts them back together. Subclasses move the chunks (EOS P2P, or TCP for QA).
public abstract class OnlineLink
{
    public const int Chunk = 1100;
    public abstract string LocalId { get; }
    protected abstract void SendRaw(string peer, byte[] packet);
    public event Action<string, JsonElement>? Message;
    uint nextMessage;
    readonly Dictionary<(string, uint), byte[]?[]> partial = new();

    public void Send(string peer, JsonElement message)
    {
        byte[] body;
        using (var ms = new MemoryStream())
        {
            using (var gz = new GZipStream(ms, CompressionLevel.Fastest, leaveOpen: true)) gz.Write(Encoding.UTF8.GetBytes(message.GetRawText()));
            body = ms.ToArray();
        }
        uint id = ++nextMessage;
        int count = Math.Max(1, (body.Length + Chunk - 1) / Chunk);
        if (count > ushort.MaxValue) throw new InvalidOperationException("联机消息太大");
        for (int i = 0; i < count; i++)
        {
            int len = Math.Min(Chunk, body.Length - i * Chunk);
            var packet = new byte[8 + len];
            BitConverter.TryWriteBytes(packet.AsSpan(0, 4), id);
            BitConverter.TryWriteBytes(packet.AsSpan(4, 2), (ushort)i);
            BitConverter.TryWriteBytes(packet.AsSpan(6, 2), (ushort)count);
            Buffer.BlockCopy(body, i * Chunk, packet, 8, len);
            SendRaw(peer, packet);
        }
    }

    // called on the main thread with one received chunk
    protected void Receive(string peer, byte[] packet)
    {
        if (packet.Length < 8) return;
        uint id = BitConverter.ToUInt32(packet, 0); int index = BitConverter.ToUInt16(packet, 4), count = BitConverter.ToUInt16(packet, 6);
        if (count == 0 || index >= count) return;
        var key = (peer, id);
        if (!partial.TryGetValue(key, out var parts)) partial[key] = parts = new byte[count][];
        if (parts.Length != count) return;
        parts[index] = packet[8..];
        if (parts.Any(p => p is null)) return;
        partial.Remove(key);
        try
        {
            using var ms = new MemoryStream(parts.SelectMany(p => p!).ToArray());
            using var gz = new GZipStream(ms, CompressionMode.Decompress);
            using var outMs = new MemoryStream(); gz.CopyTo(outMs);
            using var doc = JsonDocument.Parse(outMs.ToArray());
            Message?.Invoke(peer, doc.RootElement.Clone());
        }
        catch (Exception e) { GD.PrintErr("ONLINE_BAD_MESSAGE " + e.GetType().Name); }
    }
}

// QA only: two games on one machine. The host listens on 127.0.0.1:<port>, the guest connects; frames are
// [length u32][chunk]. Received chunks wait in a queue that the main thread drains each frame.
public sealed class TcpLink : OnlineLink
{
    public const string HostId = "loop-host", GuestId = "loop-guest";
    readonly bool isHost;
    NetworkStream? stream;
    readonly ConcurrentQueue<byte[]> inbox = new();
    readonly object writeLock = new();
    public bool Connected => stream is not null;
    public override string LocalId => isHost ? HostId : GuestId;

    TcpLink(bool host) { isHost = host; }

    public static TcpLink Host(int port)
    {
        var l = new TcpLink(true);
        var listener = new TcpListener(IPAddress.Loopback, port); listener.Start();
        _ = Task.Run(async () => { var c = await listener.AcceptTcpClientAsync(); c.NoDelay = true; l.stream = c.GetStream(); l.ReadLoop(); });
        return l;
    }

    public static TcpLink Join(int port)
    {
        var l = new TcpLink(false);
        _ = Task.Run(async () =>
        {
            for (int i = 0; i < 100; i++)
            {
                try { var c = new TcpClient { NoDelay = true }; await c.ConnectAsync(IPAddress.Loopback, port); l.stream = c.GetStream(); l.ReadLoop(); return; }
                catch { await Task.Delay(300); }
            }
        });
        return l;
    }

    void ReadLoop() => _ = Task.Run(async () =>
    {
        var head = new byte[4];
        try
        {
            while (true)
            {
                await stream!.ReadExactlyAsync(head);
                var buf = new byte[BitConverter.ToInt32(head)];
                await stream.ReadExactlyAsync(buf);
                inbox.Enqueue(buf);
            }
        }
        catch (Exception e) { GD.Print("ONLINE_LOOP_CLOSED " + e.GetType().Name); }
    });

    protected override void SendRaw(string peer, byte[] packet)
    {
        if (stream is null) { GD.PrintErr("ONLINE_LOOP not connected"); return; }
        lock (writeLock) { stream.Write(BitConverter.GetBytes(packet.Length)); stream.Write(packet); }
    }

    public Node Pump() => new LinkPump(() => { while (inbox.TryDequeue(out var p)) Receive(isHost ? GuestId : HostId, p); });
}

public partial class LinkPump : Node
{
    readonly Action drain;
    public LinkPump() { drain = () => { }; }
    public LinkPump(Action drain) { this.drain = drain; }
    public override void _Process(double delta) => drain();
}
