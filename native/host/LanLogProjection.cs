using System.Text.Json;

namespace Arkham.NativeHost;

public sealed record LanLogPage(int Page,int PageCount,int TotalRows,bool ImportedWithoutPriorLog,IReadOnlyList<ClientLogRow> Rows);

// Input MUST be ClientLogs.Read's recipient-filtered, retract-resolved history.
// This is a DTO projection, not a second parser or a branch evaluator.
public static class LanLogProjection
{
    public const int MaximumPageBytes=1024*1024;
    public const int MaximumPageRows=100;
    sealed class Budget {int nodes;public void Visit(int depth){if(depth>32||++nodes>16384)throw new InvalidDataException("Network log structure exceeds limit");}}
    static ClientLogRef? Reference(ClientLogRef? value)=>value is null?null:value.FaceDown
        ?new(value.Kind,"未揭示卡牌",null,null,null,true)
        :new(value.Kind,value.Name,value.CardCode,value.CardId,value.EntityId,false);
    static ClientCampaignLogKey Key(ClientCampaignLogKey key,Budget budget,int depth)
    {budget.Visit(depth);if(key.Value is not null&&key.Nested is not null)throw new InvalidDataException("Ambiguous log key");return new(key.Tag,key.Value,key.Nested is null?null:Key(key.Nested,budget,depth+1));}
    static ClientLogPart Part(ClientLogPart part,Budget budget,int depth)
    {
        budget.Visit(depth);
        // Reconstruct from the selected case. Never retain unrelated optional
        // fields attached to a typed node by a caller or future implementation.
        return part.Tag switch{
            "LogText" or "LogToken" or "LogIcon"=>new(part.Tag,Text:part.Text??throw new InvalidDataException("Missing log text")),
            "LogNumber" or "LogDelta"=>new(part.Tag,Number:part.Number??throw new InvalidDataException("Missing log number")),
            "LogRefPart"=>new(part.Tag,Reference:Reference(part.Reference??throw new InvalidDataException("Missing log reference"))),
            "LogList"=>new(part.Tag,Items:(part.Items??throw new InvalidDataException("Missing log list")).Select(p=>Part(p,budget,depth+1)).ToArray()),
            "LogI18n"=>new(part.Tag,Text:part.Text??throw new InvalidDataException("Missing translation key"),Variables:(part.Variables??throw new InvalidDataException("Missing translation variables")).ToDictionary(p=>p.Key,p=>Part(p.Value,budget,depth+1),StringComparer.Ordinal)),
            "LogCampaignKey"=>new(part.Tag,CampaignKey:Key(part.CampaignKey??throw new InvalidDataException("Missing campaign key"),budget,depth+1)),
            _=>throw new InvalidDataException("Unsupported network log part")};
    }
    static ClientLogEntry Entry(ClientLogEntry value,Budget budget,int depth)
    {
        budget.Visit(depth);
        return new(value.Sequence,value.Step,value.Kind,value.Body.Select(p=>Part(p,budget,depth+1)).ToArray(),Reference(value.Source),value.Children.Select(c=>Entry(c,budget,depth+1)).ToArray(),value.Round,value.Phase,value.Turn,value.Tone,value.GroupId,value.GroupRole,null,"");
    }
    public static LanLogPage Page(ClientLogHistory history,int page)
    {
        if(page<0||history.Rows.Count>100000)throw new InvalidDataException("Invalid log page");
        var pages=new List<IReadOnlyList<ClientLogRow>>();var current=new List<ClientLogRow>();int bytes=4096,total=0;
        foreach(var row in history.Rows){
            if(row.Type!="log")continue;
            var entry=row.Entry??throw new InvalidDataException("Missing structured log");var safe=new ClientLogRow(row.Key,"log",Entry:Entry(entry,new Budget(),0));
            int size=JsonSerializer.SerializeToUtf8Bytes(safe).Length+1;
            if(size>MaximumPageBytes-4096)throw new InvalidDataException("A structured log exceeds network page size");
            if(current.Count==MaximumPageRows||bytes+size>MaximumPageBytes){pages.Add(current.ToArray());current.Clear();bytes=4096;}
            current.Add(safe);bytes+=size;total++;
        }
        if(current.Count>0||pages.Count==0)pages.Add(current.ToArray());
        if(page>=pages.Count)throw new InvalidDataException("Log page is outside current branch");
        var result=new LanLogPage(page,pages.Count,total,history.ImportedWithoutPriorLog,pages[page]);
        if(JsonSerializer.SerializeToUtf8Bytes(result).Length>MaximumPageBytes)throw new InvalidDataException("Encoded log page exceeds limit");return result;
    }
    // Focused pure adapter checks, no SQLite, DLL, Godot or rule execution.
    public static int CheckContracts()
    {
        int checks=0;void Check(bool ok){if(!ok)throw new InvalidDataException("LAN log projection contract failed");checks++;}
        Guid player=Guid.NewGuid(),game=Guid.NewGuid();var hidden=new ClientLogRef("RefCard","secret-name","secret-code","secret-card","secret-entity",true);
        var entry=new ClientLogEntry(1,null,"Narrative",new[]{new ClientLogPart("LogI18n",Text:"public-key",Variables:new Dictionary<string,ClientLogPart>{{"card",new("LogList",Items:new[]{new ClientLogPart("LogRefPart",Reference:hidden)})}})},hidden,Array.Empty<ClientLogEntry>(),null,null,null,null,null,null,"internal-tag","internal-path");
        var key=new ClientLogKey(game.ToString("D"),1,player,0);var row=new ClientLogRow(key,"log",Text:"unexpected-field-canary",Entry:entry);
        ClientLogHistory History(params ClientLogRow[] rows)=>new(new long[]{0,1},rows,false);
        var page=Page(History(row,new(key,"error",Text:"error-canary"),new(key,"text",Text:"free-text-canary")),0);string encoded=JsonSerializer.Serialize(page);
        Check(page.TotalRows==1&&page.Rows.Count==1);
        Check(new[]{"secret-name","secret-code","secret-card","secret-entity","unexpected-field-canary","error-canary","free-text-canary","internal-tag","internal-path"}.All(c=>!encoded.Contains(c,StringComparison.Ordinal)));
        Check(page.Rows[0].Entry!.Source is {FaceDown:true,CardCode:null,CardId:null,EntityId:null});
        var visible=new ClientLogRef("RefCard","public-name","c01001","public-card",null,false);var publicRow=row with{Entry=entry with{Source=visible,Body=new[]{new ClientLogPart("LogNumber",Number:9007199254740993)}}};
        Check(JsonSerializer.Serialize(Page(History(publicRow),0)).Contains("9007199254740993",StringComparison.Ordinal)&&Page(History(publicRow),0).Rows[0].Entry!.Source!.CardCode=="c01001");
        var many=History(Enumerable.Range(0,101).Select(i=>publicRow with{Key=key with{EventIndex=i}}).ToArray());Check(Page(many,0).Rows.Count==100&&Page(many,1).Rows.Count==1&&Page(many,0).PageCount==2);
        var large=publicRow with{Entry=entry with{Body=new[]{new ClientLogPart("LogText",Text:new string('x',600000))}}};var sized=Page(History(large,large),0);Check(sized.Rows.Count==1&&sized.PageCount==2&&JsonSerializer.SerializeToUtf8Bytes(sized).Length<=MaximumPageBytes);
        bool rejected=false;try{Page(History(row with{Entry=entry with{Body=new[]{new ClientLogPart("Unknown",Text:"not-for-wire")}}}),0);}catch(InvalidDataException){rejected=true;}Check(rejected);
        Check(Page(new(Array.Empty<long>(),Array.Empty<ClientLogRow>(),true),0) is {PageCount:1,TotalRows:0,ImportedWithoutPriorLog:true});
        return checks;
    }
}
