using System.Text.Json;

public sealed record ClientSeat(Guid Player,int Number,bool HasQuestion);

// Local hotseat metadata only. Switching a view never answers a question.
public static class ClientSeats
{
    public static ClientSeat[] Read(JsonElement savedGame)
    {
        var players=savedGame.GetProperty("gamePlayers").EnumerateArray().Select(p=>Guid.Parse(p.GetString()!)).ToArray();
        if(players.Length is <1 or >4||players.Any(p=>p==Guid.Empty)||players.Distinct().Count()!=players.Length)throw new InvalidDataException("本地存档的玩家座位无效");
        var questions=savedGame.GetProperty("gameQuestion");
        if(questions.ValueKind!=JsonValueKind.Object)throw new InvalidDataException("本地存档的问题列表无效");
        return players.Select((p,i)=>new ClientSeat(p,i+1,questions.TryGetProperty(p.ToString("D"),out _))).ToArray();
    }
    public static Guid Select(IReadOnlyList<ClientSeat> seats,Guid? preferred)
    {
        if(seats.Count==0)throw new InvalidDataException("没有可查看的本地座位");
        if(preferred is not Guid player)return seats[0].Player;
        if(!seats.Any(s=>s.Player==player))throw new InvalidOperationException("该玩家不属于此本地存档");
        return player;
    }
    public static void Count(int seats){if(seats is <1 or >4)throw new ArgumentOutOfRangeException(nameof(seats),"本地游玩支持一至四个座位");}
    // Only after a committed roster mutation/undo may the viewed seat no longer
    // exist. Select a remaining view; never submit another player's answer.
    public static Guid AfterMutation(IReadOnlyList<ClientSeat> seats,Guid preferred,Guid? leadPlayer=null)
    {
        if(seats.Any(s=>s.Player==preferred))return preferred;
        if(seats.Count==0)throw new InvalidDataException("没有可查看的本地座位");
        return leadPlayer is Guid lead&&seats.Any(s=>s.Player==lead)?lead:seats[0].Player;
    }
    public static Guid? LeadPlayer(JsonElement savedGame)
    {
        static string Text(JsonElement value,string name)=>value.ValueKind==JsonValueKind.Object&&value.TryGetProperty(name,out var field)&&field.ValueKind==JsonValueKind.String?field.GetString()!:"";
        string iid=Text(savedGame,"gameLeadInvestigatorId");
        if(iid.Length==0||!savedGame.TryGetProperty("gameEntities",out var entities)||entities.ValueKind!=JsonValueKind.Object||!entities.TryGetProperty("investigators",out var actors)||actors.ValueKind!=JsonValueKind.Object)return null;
        foreach(var actor in actors.EnumerateObject())if((actor.Name==iid||Text(actor.Value,"id")==iid)&&Guid.TryParse(Text(actor.Value,"playerId"),out var player))return player;
        return null;
    }
}
