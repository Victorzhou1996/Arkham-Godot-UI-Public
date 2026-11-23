using Godot;

namespace ArkhamUI;

// Online play over Epic Online Services (Yunke 11-22: "新游戏和继续游戏都可通过eos联机"). The screens show where online
// play goes — host a new game, host a saved game, join by room code, the room itself — but every entry stays disabled
// (shown, not usable) until the EOS side reports a working connection.
//
// What exists so far (tools/eos-probe, evidence/eos): device-ID login, lobby create / query / destroy, and P2P between two
// Windows accounts on one machine. Not done: P2P across networks (relay fallback), and syncing a game between two
// tables. The networking session owns that work; the UI only reads this class.
public static class Online
{
    // the EOS work plugs in here: set once a login and a cross-network test pass on this machine
    public static IOnlineService? Service;

    // and this copy is the newest version (Yunke 11-23: "不更新就不能联机，可以单机")
    public static bool Available => Service?.Ready == true && !Updater.OnlineBlocked;

    // why the online entries are disabled (their hover text)
    public static string Reason => Updater.OnlineBlocked ? "有新版本，更新后才能联机（主菜单右上角）" : Service switch
    {
        null => "联机尚未接通：EOS 已能登录、建大厅，同一台电脑两个账户之间能收发；跨网络联机和对局同步还没完成。",
        { Ready: false } s => s.Status,
        _ => "",
    };

    // a button that opens an online flow: disabled with the reason as its hover text while online play is not there
    public static void Gate(BaseButton b)
    {
        var filter = b.MouseFilter; var cursor = b.MouseDefaultCursorShape;
        bool gated = false;
        void Close()
        {
            if (!gated) { filter = b.MouseFilter; cursor = b.MouseDefaultCursorShape; }
            gated = true;
            b.Disabled = true; b.TooltipText = Reason; b.MouseFilter = Control.MouseFilterEnum.Pass;
            b.MouseDefaultCursorShape = Control.CursorShape.Forbidden;
        }
        if (!Available) Close();
        // EOS logs in a few seconds after start-up, often after the menu is drawn (the installed game, 11-22: "联机是灰的"):
        // the entry comes alive by itself once online play is there. The update check (Updater) can close it again.
        Action? wake = null;
        wake = () =>
        {
            if (!GodotObject.IsInstanceValid(b)) { if (Service is { } s0) s0.RoomChanged -= wake; Updater.Changed -= wake; return; }
            if (!Available) { if (gated) b.TooltipText = Reason; else { Close(); GD.Print("ONLINE_GATE_CLOSED " + b.Name); } return; }
            if (!gated) return;
            gated = false;
            b.Disabled = false; b.TooltipText = ""; b.MouseFilter = filter; b.MouseDefaultCursorShape = cursor;
            GD.Print("ONLINE_GATE_OPEN " + b.Name);
        };
        if (Service is { } s) s.RoomChanged += wake;
        Updater.Changed += wake;
    }
}

// What the screens need from the EOS side (to be implemented by the networking work; nothing implements it yet).
public interface IOnlineService
{
    bool Ready { get; }                       // logged in, and the cross-network path works
    string Status { get; }                    // one line for the player when not ready (why)
    string LocalName { get; }                 // this player's display name
    string LocalId { get; }                   // this player's id in the room (OnlineMember.Id)
    // a room for a new game (gameId null) or for a saved one; returns its code to share
    Task<string> HostAsync(Guid? gameId, int seats, string title = "");
    Task JoinAsync(string roomCode);
    Task LeaveAsync();
    OnlineRoom? Room { get; }                 // the room this player is in, if any
    event Action? RoomChanged;                // members, seats, ready marks or the room's game changed
    Task SetReadyAsync(bool ready);
    Task ClaimSeatAsync(int seat, string investigatorCode, string deckKey);
    // this player's whole deck for a new game, stored with the room (OnlineGame.PackDeck: gzip + base64), so the host has it
    // without a direct connection
    Task SetDeckDataAsync(string packed);
    // the host: a seat no player sits in (Yunke 11-23: "联机和一人多控结合"): its investigator and deck name (a new game; the
    // deck itself stays on the host) and who controls it (OnlineGame.Everyone, Ai, "" = the host, or a member id); all empty
    // clears it
    Task SetSeatPlanAsync(int seat, string investigatorCode, string deckName, string controller);
}

// a seat no player sits in: played by its controller (docs/ONLINE-MULTISEAT-DESIGN.md)
public sealed record OnlineSeatPlan(int Seat, string InvestigatorCode, string DeckName, string Controller);

public sealed record OnlineMember(string Name, bool Host, bool Ready, int Seat, string InvestigatorCode, string Id = "", string DeckName = "", string DeckData = "", string Version = "");
public sealed record OnlineRoom(string Code, Guid? GameId, string Title, int Seats, IReadOnlyList<OnlineMember> Members, IReadOnlyList<OnlineSeatPlan>? Plans = null)
{
    public OnlineSeatPlan? Plan(int seat) => Plans?.FirstOrDefault(p => p.Seat == seat);
}
