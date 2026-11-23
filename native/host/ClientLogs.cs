using System.Text.Json;

// Read-only display projection, not a rules evaluator. Never browse arbitrary
// PublicGame fields to reconstruct a log or turn a log row into an answer.
public sealed record ClientLogState(string GameId,string SourceId,long Sequence,long? Parent,long? RestoredFrom,string Kind,JsonElement Streams);
public sealed record ClientLogKey(string GameId,long HostSequence,Guid Recipient,int EventIndex);
public sealed record ClientLogRef(string Kind,string Name,string? CardCode,string? CardId,string? EntityId,bool FaceDown);
public sealed record ClientCampaignLogKey(string Tag,string? Value=null,ClientCampaignLogKey? Nested=null);
public sealed record ClientLogPart(string Tag,string? Text=null,long? Number=null,ClientLogRef? Reference=null,
    IReadOnlyList<ClientLogPart>? Items=null,IReadOnlyDictionary<string,ClientLogPart>? Variables=null,ClientCampaignLogKey? CampaignKey=null);
public sealed record ClientLogEntry(long Sequence,long? Step,string Kind,IReadOnlyList<ClientLogPart> Body,
    ClientLogRef? Source,IReadOnlyList<ClientLogEntry> Children,long? Round,string? Phase,string? Turn,string? Tone,
    string? GroupId,string? GroupRole,string? Tag,string IndexPath);
public sealed record ClientLogRow(ClientLogKey Key,string Type,string? Text=null,ClientLogEntry? Entry=null,string? Investigator=null);
public sealed record ClientLogHistory(IReadOnlyList<long> EffectiveSequences,IReadOnlyList<ClientLogRow> Rows,bool ImportedWithoutPriorLog);

public static class ClientLogs
{
    const int MaxDepth=32,MaxNodes=16384,MaxText=65536,MaxHistory=100000,MaxEventBytes=2*1024*1024,MaxHistoryBytes=64*1024*1024;
    static readonly HashSet<string> Kinds=new(["Structure","Action","Mechanic","Test","Narrative","Record","Notice","Problem","Chat"]);
    static readonly HashSet<string> RefKinds=new(["RefCard","RefInvestigator","RefEnemy","RefLocation","RefAsset","RefEvent","RefSkill","RefTreachery","RefAct","RefAgenda","RefStory","RefScenario","RefAbility"]);
    sealed class Budget
    {
        int nodes;
        public void Visit(int depth){if(depth>MaxDepth||++nodes>MaxNodes)throw new InvalidDataException("日志结构超过显示限制；未展开原始内容");}
    }
    static void Object(JsonElement value)
    {
        if(value.ValueKind!=JsonValueKind.Object)throw new InvalidDataException("日志对象结构无效");
        var names=new HashSet<string>(StringComparer.Ordinal);
        foreach(var p in value.EnumerateObject())if(!names.Add(p.Name))throw new InvalidDataException("日志对象包含重复字段");
    }
    static string String(JsonElement value)
    {
        if(value.ValueKind!=JsonValueKind.String)throw new InvalidDataException("日志文本类型无效");
        string result=value.GetString()!;if(result.Length>MaxText)throw new InvalidDataException("日志文本超过显示限制");return result;
    }
    static string Required(JsonElement value,string key)=>String(J.Get(value,key));
    static string? Optional(JsonElement value,string key){var v=J.Get(value,key);return v.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined?null:String(v);}
    static long Integer(JsonElement value){if(value.ValueKind!=JsonValueKind.Number||!value.TryGetInt64(out long n))throw new InvalidDataException("日志整数无效");return n;}
    static long? OptionalInt(JsonElement value,string key){var v=J.Get(value,key);return v.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined?null:Integer(v);}
    static IEnumerable<JsonElement> Array(JsonElement value){if(value.ValueKind!=JsonValueKind.Array)throw new InvalidDataException("日志列表无效");return value.EnumerateArray();}
    static bool SamePlayer(JsonElement value,Guid player)=>value.ValueKind==JsonValueKind.String&&Guid.TryParse(value.GetString(),out var id)&&id!=Guid.Empty&&id==player;
    static bool Audience(JsonElement value,Guid player)
    {
        if(value.ValueKind!=JsonValueKind.Object)return false;Object(value);
        return J.Tag(value) switch {"Everyone"=>true,"OnlyPlayer"=>SamePlayer(J.Get(value,"contents"),player),_=>false};
    }
    static ClientLogRef Ref(JsonElement value,Budget budget,int depth)
    {
        budget.Visit(depth);Object(value);string kind=Required(value,"kind");if(!RefKinds.Contains(kind))throw new InvalidDataException("尚未支持的日志引用类型");
        var face=J.Get(value,"faceDown");if(face.ValueKind is not (JsonValueKind.True or JsonValueKind.False))throw new InvalidDataException("日志卡面状态缺失");
        bool hidden=face.GetBoolean();
        // No name/code leaves this boundary for a face-down reference. Keep
        // instance identity, but any future zoom must also obey FaceDown.
        return new(kind,hidden?"未揭示卡牌":Required(value,"name"),hidden?null:Optional(value,"cardCode"),Optional(value,"cardId"),Optional(value,"entityId"),hidden);
    }
    static ClientLogPart Part(JsonElement value,Budget budget,int depth)
    {
        budget.Visit(depth);Object(value);string tag=Required(value,"tag");var contents=J.Get(value,"contents");
        switch(tag){
            case "LogText":case "LogToken":case "LogIcon":return new(tag,Text:String(contents));
            case "LogNumber":case "LogDelta":return new(tag,Number:Integer(contents));
            case "LogRefPart":return new(tag,Reference:Ref(contents,budget,depth+1));
            case "LogList":return new(tag,Items:Array(contents).Select(x=>Part(x,budget,depth+1)).ToArray());
            case "LogI18n":{
                var tuple=Array(contents).ToArray();if(tuple.Length!=2||tuple[1].ValueKind!=JsonValueKind.Object)throw new InvalidDataException("日志翻译变量结构无效");
                var vars=new Dictionary<string,ClientLogPart>(StringComparer.Ordinal);
                foreach(var p in tuple[1].EnumerateObject()){if(p.Name.Length>256||!vars.TryAdd(p.Name,Part(p.Value,budget,depth+1)))throw new InvalidDataException("日志翻译变量重复或过长");}
                return new(tag,Text:String(tuple[0]),Variables:vars);
            }
            case "LogCampaignKey":return new(tag,CampaignKey:CampaignKey(contents,budget,depth+1));
            default:throw new InvalidDataException("尚未支持的日志片段类型；未显示原始内容");
        }
    }
    static ClientCampaignLogKey CampaignKey(JsonElement key,Budget budget,int depth)
    {
        budget.Visit(depth);Object(key);string tag=Required(key,"tag");var c=J.Get(key,"contents");
        return c.ValueKind switch{
            JsonValueKind.Object=>new(tag,Nested:CampaignKey(c,budget,depth+1)),
            JsonValueKind.String=>new(tag,Value:String(c)),
            JsonValueKind.Undefined=>new(tag),
            _=>throw new InvalidDataException("战役记录键结构无效")};
    }
    static ClientLogEntry? Entry(JsonElement value,Guid player,Budget budget,int depth,string path="")
    {
        budget.Visit(depth);Object(value);if(!Audience(J.Get(value,"audience"),player))return null;
        string kind=Required(value,"kind");if(!Kinds.Contains(kind))throw new InvalidDataException("尚未支持的日志条目类型");
        var body=Array(J.Get(value,"body")).Select(x=>Part(x,budget,depth+1)).ToArray();
        var children=Array(J.Get(value,"children")).Select((x,i)=>Entry(x,player,budget,depth+1,path.Length==0?i.ToString():path+"."+i)).Where(x=>x is not null).Cast<ClientLogEntry>().ToArray();
        var source=J.Get(value,"source");var context=J.Get(value,"context");var group=J.Get(value,"group");
        if(context.ValueKind!=JsonValueKind.Undefined&&context.ValueKind!=JsonValueKind.Null)Object(context);
        if(group.ValueKind!=JsonValueKind.Undefined&&group.ValueKind!=JsonValueKind.Null)Object(group);
        string? role=Optional(group,"role");if(role is not (null or "GroupHeader" or "GroupMember" or "GroupSummary"))throw new InvalidDataException("日志组角色无效");
        string? tone=Optional(value,"tone");if(tone is not (null or "Good" or "Bad"))throw new InvalidDataException("日志结果类型无效");
        return new(Integer(J.Get(value,"seq")),OptionalInt(value,"step"),kind,body,
            source.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined?null:Ref(source,budget,depth+1),children,
            OptionalInt(context,"round"),Optional(context,"phase"),Optional(context,"turn"),tone,Optional(group,"id"),role,Optional(value,"tag"),path);
    }

    public static ClientLogRow? Project(ClientLogKey key,JsonElement value)
    {
        if(key.Recipient==Guid.Empty||!Guid.TryParse(key.GameId,out var game)||game==Guid.Empty||key.HostSequence<0||key.EventIndex<0)throw new ArgumentException("日志来源身份无效");
        Object(value);if(System.Text.Encoding.UTF8.GetByteCount(value.GetRawText())>MaxEventBytes)throw new InvalidDataException("日志事件超过显示限制");
        if(value.TryGetProperty("seat",out var seat)&&!SamePlayer(seat,key.Recipient))return null;
        string type=Required(value,"type");
        switch(type){
            case "log":var entry=Entry(J.Get(value,"entry"),key.Recipient,new Budget(),0);return entry is null?null:new(key,type,Entry:entry);
            case "text":return new(key,type,Text:Required(value,"text"));
            case "retractLog":return new(key,type,Text:Required(value,"tag"));
            // Notifications are not executable actions; their cards require a
            // separate permissioned zone adapter before any image is exposed.
            case "draw":return value.TryGetProperty("seat",out _)?new(key,type,Text:Required(value,"text")):null;
            case "card":return new(key,type,Text:Required(value,"text"));
            case "showDiscard":case "showUnder":return new(key,type,Investigator:Required(value,"investigator"));
            case "audio":case "ui":case "error":case "playability":case "customCardIssue":case "tarot":return null;
            default:throw new InvalidDataException("尚未支持的日志事件类型；未显示原始内容");
        }
    }

    public static ClientLogHistory Read(string gameId,string sourceId,long current,Guid player,Func<long,ClientLogState> read)
        =>ReadFull(gameId,sourceId,current,player,read,out _,out _);
    static ClientLogHistory ReadFull(string gameId,string sourceId,long current,Guid player,Func<long,ClientLogState> read,out int traversed,out long bytes)
    {
        if(current<0||player==Guid.Empty||string.IsNullOrWhiteSpace(sourceId)||!Guid.TryParse(gameId,out var game)||game==Guid.Empty)throw new ArgumentException("日志游戏或座位无效");
        var branch=new List<ClientLogState>();long next=current;traversed=0;bytes=0;
        while(true){
            if(++traversed>MaxHistory)throw new InvalidDataException("日志历史超过显示限制");var state=read(next);
            // states of one game may come from an earlier core (continued on the current one): same game and sequence suffice
            if(state.GameId!=gameId||state.Sequence!=next)throw new InvalidDataException("日志历史身份不匹配");
            bytes+=System.Text.Encoding.UTF8.GetByteCount(state.Streams.GetRawText());if(bytes>MaxHistoryBytes)throw new InvalidDataException("日志历史超过显示限制");
            if(next==0){if(state.Parent is not null||state.RestoredFrom is not null||state.Kind is not ("create" or "import"))throw new InvalidDataException("日志根状态无效");if(state.Kind=="import")Marker(state);branch.Add(state);break;}
            if(state.Parent is not long parent||parent<0||parent>=next)throw new InvalidDataException("日志父状态无效");
            if(state.Kind=="preference"){
                if(state.RestoredFrom is not null)throw new InvalidDataException("日志偏好标记无效");
                Marker(state);next=parent;
            }else if(state.Kind=="restore"){
                if(state.RestoredFrom is not long target||target<0||target>=next)throw new InvalidDataException("日志恢复目标无效");
                Marker(state);
                next=target; // Do not include abandoned parent-branch events.
            }else{
                if(state.Kind is not ("answer" or "debug")||state.RestoredFrom is not null)throw new InvalidDataException("日志历史操作类型无效");
                branch.Add(state);next=parent;
            }
        }
        branch.Reverse();var rows=new List<ClientLogRow>();
        foreach(var state in branch){
            if(state.Kind=="import")continue;
            Append(state,player,rows);
        }
        return new(System.Array.AsReadOnly(branch.Select(s=>s.Sequence).ToArray()),rows.AsReadOnly(),branch[0].Kind=="import");
    }
    // A validated branch has strictly increasing host sequences. Per-head
    // duplicate checking is equivalent to a whole-branch event-key set.
    static void Append(ClientLogState state,Guid player,List<ClientLogRow> rows)
    {
            int matching=0;var seen=new HashSet<ClientLogKey>();
            foreach(var stream in Array(state.Streams)){
                Object(stream);
                if(!SamePlayer(J.Get(stream,"recipient"),player))continue;
                if(++matching>1)throw new InvalidDataException("同一日志状态含重复的接收者流");int index=0;
                foreach(var e in Array(J.Get(stream,"events"))){
                    var key=new ClientLogKey(state.GameId,state.Sequence,player,index++);if(!seen.Add(key))throw new InvalidDataException("日志事件身份重复");
                    var row=Project(key,e);if(row is null)continue;
                    if(row.Type=="retractLog"){
                        for(int i=0;i<rows.Count;i++)if(rows[i].Entry is ClientLogEntry entry)rows[i]=rows[i] with{Entry=Retract(entry,row.Text!)};
                        rows.RemoveAll(r=>r.Entry is null&&r.Type=="log");
                    }else {if(rows.Count>=MaxHistory)throw new InvalidDataException("日志行数超过显示限制");rows.Add(row);}
                }
            }
    }
    // One host/store lifetime; no persisted recipient snapshots. Restore,
    // preference and disconnected heads retain the full original branch walk.
    public sealed class PrefixCache
    {
        sealed record Snapshot(long Sequence,ClientLogHistory History,int Traversed,long Bytes);
        readonly Dictionary<(string Game,string Source,Guid Seat),Snapshot> snapshots=new();
        readonly object gate=new();
        const int MaxSnapshots=8;
        const long CacheBytes=16L*1024*1024;
        void Remember((string Game,string Source,Guid Seat) key,Snapshot snapshot)
        {
            if(snapshot.Bytes>CacheBytes){snapshots.Remove(key);return;}
            snapshots[key]=snapshot;
            while(snapshots.Count>MaxSnapshots||snapshots.Values.Sum(s=>s.Bytes)>CacheBytes)
                snapshots.Remove(snapshots.Keys.First(k=>k!=key));
        }
        public void Invalidate(string gameId){lock(gate)foreach(var key in snapshots.Keys.Where(k=>k.Game==gameId).ToArray())snapshots.Remove(key);}
        public ClientLogHistory Read(string gameId,string sourceId,long current,Guid player,Func<long,ClientLogState> read,out bool prefixUsed,bool retain=true)
        {
            if(current<0||player==Guid.Empty||string.IsNullOrWhiteSpace(sourceId)||!Guid.TryParse(gameId,out var game)||game==Guid.Empty)throw new ArgumentException("日志游戏或座位无效");
            var key=(gameId,sourceId,player);
            lock(gate){
                prefixUsed=false;
                if(snapshots.TryGetValue(key,out var prior)){
                    if(current==prior.Sequence){prefixUsed=true;return prior.History;}
                    var head=read(current);
                    if(head.GameId!=gameId||head.Sequence!=current)throw new InvalidDataException("日志历史身份不匹配");
                    if(head.Kind is "answer" or "debug" && head.Parent==prior.Sequence && head.Parent<current && head.RestoredFrom is null){
                        int traversed=checked(prior.Traversed+1);
                        long bytes=prior.Bytes+System.Text.Encoding.UTF8.GetByteCount(head.Streams.GetRawText());
                        if(traversed>MaxHistory||bytes>MaxHistoryBytes)throw new InvalidDataException("日志历史超过显示限制");
                        var rows=new List<ClientLogRow>(prior.History.Rows);Append(head,player,rows);
                        var sequences=System.Array.AsReadOnly(prior.History.EffectiveSequences.Append(current).ToArray());
                        var history=new ClientLogHistory(sequences,rows.AsReadOnly(),prior.History.ImportedWithoutPriorLog);
                        if(retain)Remember(key,new(current,history,traversed,bytes));prefixUsed=true;return history;
                    }
                }
                var full=ReadFull(gameId,sourceId,current,player,read,out var visited,out var size);
                if(retain)Remember(key,new(current,full,visited,size));return full;
            }
        }
    }
    static ClientLogEntry? Retract(ClientLogEntry entry,string tag)=>entry.Tag==tag?null:entry with
    {Children=entry.Children.Select(c=>Retract(c,tag)).Where(c=>c is not null).Cast<ClientLogEntry>().ToArray()};
    // Event audit trail is append-only, including explicit group Undo actions.
    // It is not presented as the effective single-game rollback branch.
    public static ClientLogHistory ReadEpic(string gameId,string sourceId,long current,Guid player,Func<long,ClientLogState> read)
    {
        var states=new List<ClientLogState>();long next=current,bytes=0;
        while(true){
            if(states.Count>=MaxHistory)throw new InvalidDataException("事件记录超过显示限制");var state=read(next);
            if(state.GameId!=gameId||state.SourceId!=sourceId||state.Sequence!=next)throw new InvalidDataException("事件日志身份不符");
            bytes+=System.Text.Encoding.UTF8.GetByteCount(state.Streams.GetRawText());if(bytes>MaxHistoryBytes)throw new InvalidDataException("事件记录超过显示限制");states.Add(state);
            if(state.Parent is not long parent)break;if(parent<0||parent>=next)throw new InvalidDataException("事件日志父状态无效");next=parent;
        }
        states.Reverse();var rows=new List<ClientLogRow>();
        foreach(var state in states){int index=0;foreach(var stream in Array(state.Streams)){
            if(!SamePlayer(J.Get(stream,"recipient"),player))continue;
            foreach(var e in Array(J.Get(stream,"events"))){var row=Project(new(gameId,state.Sequence,player,index++),e);if(row is null)continue;
                if(row.Type=="retractLog"){for(int i=0;i<rows.Count;i++)if(rows[i].Entry is{} entry)rows[i]=rows[i] with{Entry=Retract(entry,row.Text!)};rows.RemoveAll(r=>r.Entry is null&&r.Type=="log");}
                else{if(rows.Count>=MaxHistory)throw new InvalidDataException("事件日志行数超过限制");rows.Add(row);}
            }
        }}return new(states.Select(s=>s.Sequence).ToArray(),rows,states[0].Sequence!=0);
    }
    static void Marker(ClientLogState state)
    {
        var markers=Array(state.Streams).ToArray();if(markers.Length!=1)throw new InvalidDataException("日志历史标记无效");var marker=markers[0];Object(marker);
        if(J.String(marker,"kind")!=state.Kind||OptionalInt(marker,"restoredFrom")!=state.RestoredFrom||marker.EnumerateObject().Any(p=>p.Name is not ("kind" or "restoredFrom")))throw new InvalidDataException("日志历史标记无效");
    }
}
