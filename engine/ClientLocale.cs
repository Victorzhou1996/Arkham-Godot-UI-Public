using System.Net;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

public sealed class ClientLocale
{
    readonly JsonElement dictionary;
    public ClientLocale(JsonElement source){dictionary=source.Clone();}
    // card names for labels that carry a card code or an English card name (set by the app from its catalog)
    public Func<string,string>? CardName;public Func<string,string>? CardNameFromEnglish;
    public bool TryTemplate(string raw,out string text)
    {
        text="";string key=raw.StartsWith('$')?raw[1..]:raw;JsonElement current=dictionary;int position=0;
        // Supports Vue's key['[section]'].item paths without evaluating code.
        foreach(Match m in Regex.Matches(key,@"(?:^|\.)([^.\[\]]+)|\['([^']*)'\]")){
            if(m.Index!=position)return false;position=m.Index+m.Length;string part=m.Groups[1].Success?m.Groups[1].Value:m.Groups[2].Value;
            if(current.ValueKind!=JsonValueKind.Object||!current.TryGetProperty(part,out current))return false;
        }
        if(position!=key.Length||key.Length==0||current.ValueKind!=JsonValueKind.String)return false;text=current.GetString()!;return true;
    }
    public string Text(string? raw,JsonElement variables=default)
    {
        if(string.IsNullOrEmpty(raw))return "";
        // Core offers this label with inline presentation variables rather than
        // a separate variables object. The packaged dictionary lacks this key.
        if(raw.StartsWith("$label.discoverClueAt ",StringComparison.Ordinal))
        {
            var room=Regex.Match(raw,@"(?:^|\s)__name=s:""(\d{5})""");
            // the location's official name from its card code; else its English name looked up in the catalog
            // (playtest 11-18: "在Your House发现1个线索", "在Arkham Woods发现1个线索")
            string name=room.Success&&CardName is not null?CardName(room.Groups[1].Value):"";
            if(name==room.Groups[1].Value)name="";
            if(name.Length==0)
            {
                var fallback=Regex.Match(raw,@"(?:^|\s)name=s:(""(?:[^""\\]|\\.)*"")");
                if(fallback.Success)try{name=JsonSerializer.Deserialize<string>(fallback.Groups[1].Value)??"";}catch(JsonException){}
                if(name.Length>0&&Regex.IsMatch(name,"[A-Za-z]")&&CardNameFromEnglish?.Invoke(name) is {Length:>0} zh)name=zh;
            }
            if(name.Length>0)return "在"+name+"发现1个线索";
        }
        string lookup=raw;var inlineValues=new Dictionary<string,JsonElement>();
        // I18n.ikey appends typed presentation variables to its lookup key.
        // Parse only that wire format; these values never become game answers.
        var inline=Regex.Match(raw,@"^(?<key>\$[^\s]+)(?:\s+(?<name>[A-Za-z_]\w*)=(?<value>i:-?\d+(?:\.\d+)?(?:[eE][+-]?\d+)?|s:""(?:[^""\\]|\\.)*""))+$",RegexOptions.CultureInvariant);
        if(inline.Success)
        {
            try
            {
                var names=inline.Groups["name"].Captures;var values=inline.Groups["value"].Captures;
                for(int i=0;i<names.Count;i++){using var value=JsonDocument.Parse(values[i].Value[2..]);inlineValues[names[i].Value]=value.RootElement.Clone();}
                lookup=inline.Groups["key"].Value;
            }
            catch(JsonException){inlineValues.Clear();}
        }
        string text=TryTemplate(lookup,out var found)?found:raw;
        // Separate presentation additions for this new UI. Donor translations
        // above always win; there is no write-back into their dictionary.
        if(text==raw)text=raw switch{"$physical"=>"肉体","$mental"=>"精神","Continue"=>"继续","Done"=>"完成",_=>raw};
        // rule keywords the core logs in English: the official card wording (core-cards.json)
        if(text==raw)text=raw switch{"Revelation"=>"显现","Forced"=>"强制","Objective"=>"目标","Prey"=>"猎物","Hunter"=>"猎手","Retaliate"=>"反击","Spawn"=>"生成",_=>raw};
        if(text==raw)text=raw switch{"SkillWillpower"=>"意志","SkillIntellect"=>"智力","SkillCombat"=>"战力","SkillAgility"=>"敏捷",_=>raw};
        if(text==raw)text=raw switch{"Willpower"=>"意志","Intellect"=>"智力","Combat"=>"战力","Agility"=>"敏捷","PlusOne"=>"+1","Zero"=>"0","MinusOne"=>"-1","MinusTwo"=>"-2","MinusThree"=>"-3","MinusFour"=>"-4","MinusFive"=>"-5","MinusSix"=>"-6","MinusSeven"=>"-7","MinusEight"=>"-8","Skull"=>"骷髅","Cultist"=>"邪教徒","Tablet"=>"石板","ElderThing"=>"远古之物","AutoFail"=>"自动失败","ElderSign"=>"远古印记","Bless"=>"祝福","Curse"=>"诅咒",_=>raw};
        text=Regex.Replace(text,@"Suffer (\d+) total physical and/or mental trauma",m=>"承受总共"+m.Groups[1].Value+"点肉体和/或精神创伤");
        if(variables.ValueKind==JsonValueKind.Object)foreach(var p in variables.EnumerateObject())inlineValues[p.Name]=p.Value;
        // vue-i18n plural forms "one | many" (e.g. "受到1点肉体创伤 | 受到{count}点肉体创伤"): pick the form by count
        if(text.Contains(" | ",StringComparison.Ordinal))
        {
            var forms=text.Split(" | ");
            // the core sends counts as "i:2.0": a whole number written with a decimal point, which TryGetInt32 refuses (Yunke 11-22:
            // 否决存在 read "取消1点伤害" for a 2-damage attack — the one-form was picked)
            int count=inlineValues.TryGetValue("count",out var cv)&&cv.ValueKind==JsonValueKind.Number&&cv.TryGetDouble(out double dn)?(int)Math.Round(dn):1;
            text=forms.Length==2?(count==1?forms[0]:forms[1]):forms.Length>=3?(count==0?forms[0]:count==1?forms[1]:forms[2]):text;
            if(!inlineValues.ContainsKey("count"))text=text.Replace("{count}","1");
        }
        // a person named by the core in English inside a story entry (Carcosa: thePathToCarcosa.checkIfInterviewed with
        // name "Sebastien Moreau"): the card's official name when a card has that English name (塞巴斯蒂安·莫罗)
        foreach(var p in inlineValues)
        {
            string value=Variable(p.Value);
            if(p.Key=="name"&&p.Value.ValueKind==JsonValueKind.String&&Regex.IsMatch(value,"[A-Za-z]")&&CardNameFromEnglish?.Invoke(value) is {Length:>0} zhName)value=zhName;
            text=text.Replace("{"+p.Key+"}",value);
        }
        return text;
    }
    string Variable(JsonElement value)
    {
        if(value.ValueKind==JsonValueKind.String)return Text(value.GetString());
        if(value.ValueKind==JsonValueKind.Number)return value.TryGetDecimal(out var number)?number.ToString("G29",CultureInfo.InvariantCulture):value.GetRawText();
        if(value.ValueKind==JsonValueKind.Object&&value.TryGetProperty("contents",out var contents))return Variable(contents);
        return value.GetRawText();
    }
    public string Plain(string? raw,JsonElement variables=default)
    {
        string text=Text(raw,variables);
        text=Regex.Replace(text,@"<(?:br\s*/?|/p|/h[1-6]|/li)>","\n",RegexOptions.IgnoreCase);
        text=Regex.Replace(text,@"<[^>]*>","");
        // vue-i18n literal interpolation ({'{'}tablet{'}'} = "{tablet}"), as the html version's i18n resolves it before
        // formatContent turns {tablet} into the token (playtest 11-18: the raw braces showed in a Dunwich resolution)
        text=Regex.Replace(text,@"\{'([^']*)'\}","$1");
        text=Regex.Replace(text,@"\{(elderSign|elderThing|autoFail)\}",m=>"{"+Regex.Replace(m.Groups[1].Value,"[A-Z]",c=>"_"+c.Value.ToLowerInvariant())+"}");
        text=Regex.Replace(text,@"\{([a-z_]+)\}|\[([a-z_]+)\]",m=>
        {
            string token=m.Groups[1].Success?m.Groups[1].Value:m.Groups[2].Value;
            return token switch {
                "intellect"=>"智力","willpower"=>"意志","combat"=>"战力","agility"=>"敏捷","wild"=>"任意技能",
                "action"=>"行动","reaction"=>"反应","fast" or "free"=>"快速","elder_sign"=>"远古印记","skull"=>"骷髅",
                "cultist"=>"邪教徒","tablet"=>"石板","elder_thing"=>"远古之物","autofail" or "auto_fail"=>"自动失败",
                "per_investigator"=>"每名调查员","resource"=>"资源","clue"=>"线索","damage"=>"伤害",
                "horror"=>"恐惧","health"=>"生命值","sanity"=>"理智值","doom"=>"毁灭","curse"=>"诅咒","bless"=>"祝福",
                "guardian"=>"守护者","seeker"=>"探求者","rogue"=>"流浪者","mystic"=>"潜修者","survivor"=>"生存者","neutral"=>"中立",
                _=>m.Value};
        });
        return WebUtility.HtmlDecode(text).Trim();
    }
    public string Flavor(JsonElement flavor)
    {
        if(flavor.ValueKind!=JsonValueKind.Object)return "";var text=new StringBuilder();
        if(flavor.TryGetProperty("title",out var title)&&title.ValueKind==JsonValueKind.String)text.AppendLine(Plain(title.GetString())).AppendLine();
        if(flavor.TryGetProperty("body",out var body))foreach(var entry in body.EnumerateArray())Entry(entry,text);
        return Regex.Replace(text.ToString().Replace("\r\n","\n"),@"\n{3,}","\n\n").Trim();
    }
    void Entry(JsonElement entry,StringBuilder text)
    {
        string tag=J.Tag(entry);
        switch(tag){
            case "BasicEntry":Paragraph(Plain(J.String(entry,"text")),text);break;
            case "I18nEntry":Paragraph(Plain(J.String(entry,"key"),J.Get(entry,"variables")),text);break;
            case "HeaderEntry":Paragraph(Plain(J.String(entry,"key")),text);break;
            // the core marks story and setup lines that do not apply to this campaign (InvalidEntry): html greys them
            // (FormattedEntry.vue .invalid); here each such paragraph is wrapped in <dim>…</dim> for the reader to grey
            case "ModifyEntry":
                if(J.Items(J.Get(entry,"modifiers")).Any(m=>m.ValueKind==JsonValueKind.String&&m.GetString()=="InvalidEntry"))
                {
                    var inner=new StringBuilder();Entry(J.Get(entry,"entry"),inner);
                    foreach(var line in inner.ToString().Split('\n'))text.Append(line.Trim().Length>0?"<dim>"+line.Trim()+"</dim>":"").Append('\n');
                    text.Append('\n');
                }
                else Entry(J.Get(entry,"entry"),text);
                break;
            case "CompositeEntry":case "ColumnEntry":foreach(var child in J.Get(entry,"entries").EnumerateArray())Entry(child,text);break;
            case "ListEntry":foreach(var item in J.Get(entry,"list").EnumerateArray()){text.Append("• ");Entry(J.Get(item,"entry"),text);if(J.Get(item,"nested").ValueKind==JsonValueKind.Array)foreach(var nested in J.Get(item,"nested").EnumerateArray())Entry(nested,text);}break;
            default:text.AppendLine("[待支持的剧情元素："+tag+"]");break;
        }
    }
    static void Paragraph(string value,StringBuilder text){if(!string.IsNullOrWhiteSpace(value))text.Append(value.Trim()).Append("\n\n");}
}


