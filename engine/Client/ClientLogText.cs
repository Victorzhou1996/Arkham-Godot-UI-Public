using System.Text.RegularExpressions;

// Text-only typed rendering. Godot labels, not BBCode/HTML execution. Unknown
// translations stay visibly unresolved; never substitute raw JSON variables.
public sealed class ClientLogText
{
    readonly ClientLocale locale;readonly Func<ClientLogRef,string?>? name;
    public ClientLogText(ClientLocale locale,Func<ClientLogRef,string?>? name=null){this.locale=locale;this.name=name;}
    public string Body(ClientLogEntry entry)=>Join(entry.Body.Select(Part),"");
    static string Bound(string text){if(text.Length>262144)throw new InvalidDataException("日志显示文本超过限制");return text;}
    static string Join(IEnumerable<string> parts,string separator)
    {
        var builder=new System.Text.StringBuilder();bool first=true;
        foreach(string part in parts){int extra=part.Length+(first?0:separator.Length);if(extra>262144-builder.Length)throw new InvalidDataException("日志显示文本超过限制");if(!first)builder.Append(separator);builder.Append(part);first=false;}return builder.ToString();
    }
    string Term(string key,string fallback)=>locale.TryTemplate(key,out var text)?locale.Plain(text):fallback;
    public string Part(ClientLogPart part)=>Render(part,0);
    string Render(ClientLogPart part,int depth)
    {
        if(depth>32)throw new InvalidDataException("日志显示嵌套超过限制");
        string output=part.Tag switch{
            "LogText"=>Literal(part.Text!),
            "LogNumber"=>part.Number!.Value.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "LogDelta"=>(part.Number<0?"":"+")+part.Number!.Value.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "LogToken"=>locale.Plain(part.Text),
            // skills in words; ability and token symbols as a marker the table draws with its symbol font (\u0003name\u0004)
            "LogIcon"=>part.Text switch{"willpower"=>"意志","intellect"=>"智力","combat"=>"战力","agility"=>"敏捷","wild"=>"全能",
                "action" or "fast" or "free" or "reaction" or "elder_sign" or "elderSign" or "auto_fail" or "autoFail" or "skull" or "cultist" or "tablet" or "elder_thing" or "elderThing"=>"\u0003"+part.Text+"\u0004",
                var other=>"【"+other+"】"},
            "LogRefPart"=>Reference(part.Reference!),
            "LogList"=>List(part.Items!,depth+1),
            "LogI18n"=>Translate(part,depth+1),
            "LogCampaignKey"=>Campaign(part.CampaignKey!),
            _=>throw new InvalidDataException("尚未支持的日志显示片段")};
        return Bound(output);
    }
    string Reference(ClientLogRef reference)=>reference.FaceDown?"未揭示卡牌":locale.Plain(name?.Invoke(reference)??reference.Name);
    string Literal(string raw)
    {
        if(string.IsNullOrWhiteSpace(raw))return raw;
        return Regex.Match(raw,@"^\s*").Value+locale.Plain(raw)+Regex.Match(raw,@"\s*$").Value;
    }
    string List(IReadOnlyList<ClientLogPart> items,int depth)
    {
        if(items.Count==0)return "";var text=new System.Text.StringBuilder();
        for(int i=0;i<items.Count;i++){if(i>0)text.Append(Term(i==items.Count-1?"log.listLast":"log.listSeparator",i==items.Count-1?"和":"、"));text.Append(Render(items[i],depth));if(text.Length>262144)throw new InvalidDataException("日志列表显示超过限制");}return text.ToString();
    }
    string Translate(ClientLogPart part,int depth)
    {
        string key=part.Text!;var vars=part.Variables!;
        if(!locale.TryTemplate(key,out string template))return Join(new[]{"【未汉化："+key+"】",vars.Count==0?"":" "+Join(vars.Select(p=>p.Key+"="+Render(p.Value,depth)),"；")},"");
        if(vars.TryGetValue("count",out var count)&&count.Tag=="LogNumber"){
            var forms=template.Split('|');long n=count.Number!.Value;
            if(forms.Length==2)template=forms[n==1?0:1].Trim();else if(forms.Length==3)template=forms[n==0?0:n==1?1:2].Trim();
        }
        // Strip markup in the template first. Substituted card names remain
        // literal label text; they can never become formatting instructions.
        // Plain turns icon tokens such as {damage} / {horror} into words; the template's own variables
        // ("{damage}点伤害和{horror}点恐惧") must survive it, so they are hidden behind markers first.
        var hidden=new List<string>();
        template=Regex.Replace(template,@"\{([\w.:-]+)\}",m=>{if(!vars.ContainsKey(m.Groups[1].Value))return m.Value;hidden.Add(m.Value);return "\u0001"+(hidden.Count-1)+"\u0002";});
        template=locale.Plain(template);
        template=Regex.Replace(template,"\u0001(\\d+)\u0002",m=>hidden[int.Parse(m.Groups[1].Value)]);
        var rendered=new Dictionary<string,string>(StringComparer.Ordinal);
        IEnumerable<string> Parts(){int start=0;foreach(Match m in Regex.Matches(template,@"\{([\w.:-]+)\}")){yield return template[start..m.Index];string key=m.Groups[1].Value;if(!rendered.TryGetValue(key,out var value)){value=vars.TryGetValue(key,out var p)?Render(p,depth):m.Value;rendered.Add(key,value);}yield return value;start=m.Index+m.Length;}yield return template[start..];}
        return Join(Parts(),"");
    }
    string Campaign(ClientCampaignLogKey key)
    {
        string path=FormatKey(key);return locale.TryTemplate(path,out var text)?locale.Plain(text):"【未汉化："+path+"】";
    }
    public static string FormatKey(ClientCampaignLogKey key,string? homebrewScope=null)
    {
        static string Format(string text)=>(text.Length==0?"":char.ToLowerInvariant(text[0])+text[1..]).Replace("'","");
        string prefix=Format(key.Tag.EndsWith("Key",StringComparison.Ordinal)?key.Tag[..^3]:key.Tag);
        if(key.Nested is ClientCampaignLogKey nested){
            if(nested.Nested is not null)throw new InvalidDataException("战役记录键嵌套结构尚未支持");
            return nested.Value is not null?prefix+".key['["+Format(nested.Tag)+"]']."+Format(nested.Value):prefix+".key."+Format(nested.Tag);
        }
        if(key.Value is null)return "base.key."+Format(key.Tag);
        if(key.Tag=="HomebrewCampaignLogKey"){
            var pieces=key.Value.Split('.',3);if(pieces.Length>1&&!string.IsNullOrEmpty(pieces[1]))return Format(pieces[0])+".key."+Format(pieces[1]);
            if(!string.IsNullOrEmpty(homebrewScope))return homebrewScope+".key."+Format(key.Value);
        }
        return prefix+".key."+Format(key.Value);
    }
}
