using System.Text.Json;
using System.Text.RegularExpressions;

namespace Gathering;

public sealed record ChoicePresentation(int Index, int Group, string Tag, string Title, string Hint, string CardCode, string TargetId, bool Enabled, JsonElement Raw);
public sealed record QuestionPresentation(string Tag, string Title, string Body, IReadOnlyList<ChoicePresentation> Choices, IReadOnlyList<string> ReadingCards);

// Only the current core-offered question is adapted; original indexes and
// targets are retained, and the core remains the answer-permission authority.
public sealed class QuestionPresenter(CoreCatalog catalog, ClientLocale locale)
{
    public QuestionPresentation Present(TableState state)
    {
        var q=ClientQuestions.Unwrap(state.Question);var tag=J.Tag(q);
        string title=tag switch {
            "ChooseDeck"=>"选择调查员牌组","PickScenarioSettings"=>"准备冒险","ChooseAmounts"=>"分配数量","ChoosePaymentAmounts"=>"分配支付","ChooseExchangeAmounts"=>"转移标记",
            "Read"=>"阅读剧情","ChooseOneWizard"=>"作出决定","ContinueCampaign"=>"冒险记录","PlayerWindowChooseOne" or "WindowChooseOne"=>"当前可执行的行动",
            "ChooseN"=>"选择 "+J.Int(q,"amount")+" 项","ChooseUpToN"=>"最多选择 "+J.Int(q,"amount")+" 项","ChooseOneAtATime"=>"按顺序选择",""=>"等待其他调查员",_=>"选择下一步"};
        int? pendingDamage=null,pendingHorror=null;
        for(var wrapper=state.Question;J.Tag(wrapper) is "QuestionLabel" or "PayCostQuestion" or "QuestionWithSource";wrapper=J.Get(wrapper,"question"))
        {
            if(J.Tag(wrapper)=="QuestionLabel")
            {
                string raw=J.String(wrapper,"label");var label=LabelText(raw);if(label.Length>0)title=label;
                if(Assignment(raw,out var damage,out var horror)){pendingDamage=damage;pendingHorror=horror;}
            }
            else if(J.Tag(wrapper)=="PayCostQuestion")title="支付费用";
        }
        var body=locale.Flavor(J.Get(q,"flavorText"));if(body.Length==0)body=locale.Plain(J.String(q,"label"));
        var choices=new List<ChoicePresentation>();try{foreach(var item in ClientQuestions.Choices(q))choices.Add(Describe(state.Game,item));}catch(InvalidDataException){}
        for(int i=0;i<choices.Count;i++)
        {
            var choice=choices[i];if(choice.Tag is not("ComponentLabel" or "AuxiliaryComponentLabel"))continue;
            var component=J.Get(choice.Raw,"component");string token=J.String(component,"tokenType");
            int? pending=token=="DamageToken"?pendingDamage:token=="HorrorToken"?pendingHorror:null;
            if(pending is not int amount||J.Tag(component) is not("InvestigatorComponent" or "AssetComponent"))continue;
            string kind=token=="DamageToken"?"伤害":"恐惧";
            string name=choice.CardCode.Length>0?catalog.Name(choice.CardCode):J.Tag(component)=="AssetComponent"?"该支援":"该调查员";
            // The component does not expose a per-click quantity. The public
            // question label supplies only the remaining assignment total.
            choices[i]=choice with{Title="给"+name+"分配"+kind+"（待分配"+amount+"点）",Hint="选择承受"+kind+"的对象；仍需分配"+amount+"点"+kind};
        }
        bool mulligan=choices.Any(c=>c.Title.Contains("结束选牌",StringComparison.Ordinal)||c.Title.Contains("mulligan",StringComparison.OrdinalIgnoreCase));
        var skillTest=J.Get(state.Game,"skillTest");
        // The testing investigator receives StartSkillTestButton. Helpers get
        // only their original commit/uncommit card choices in the same public
        // commit step, so absence of that button does not mean "play a card".
        bool commit=skillTest.ValueKind==JsonValueKind.Object&&(
            choices.Any(c=>c.Tag=="StartSkillTestButton")
            || tag=="ChooseOne"&&J.String(skillTest,"step")=="CommitCardsFromHandToSkillTestStep"
                && choices.Any(c=>c.Enabled&&c.Tag=="TargetLabel"&&J.Tag(J.Get(c.Raw,"target"))=="CardIdTarget"&&c.CardCode.Length>0));
        var committed=J.Props(J.Get(skillTest,"committedCards")).SelectMany(p=>J.Items(p.Value))
            .Select(card=>J.String(J.Get(card,"contents"),"id")).Where(id=>id.Length>0).ToHashSet(StringComparer.Ordinal);
        for(int i=0;i<choices.Count;i++)
        {
            var choice=choices[i];if(choice.Tag!="TargetLabel"||J.Tag(J.Get(choice.Raw,"target"))!="CardIdTarget"||choice.CardCode.Length==0)continue;
            bool uncommit=commit&&committed.Contains(choice.TargetId);
            string? prefix=mulligan?"换掉":commit?uncommit?"撤回投入":"投入":tag is "PlayerWindowChooseOne" or "WindowChooseOne"?"打出":null;
            if(prefix is not null)choices[i]=choice with{Title=prefix+" · "+catalog.Name(choice.CardCode),Hint=mulligan?"替换这张起手牌；完成选牌后重新抽取":commit?uncommit?"将这张已投入的牌收回手牌":"将这张牌投入本次检定":"按规则支付费用并打出这张牌"};
        }
        // one card offering two abilities at once (雅沃托: the skill bonus and the discard) gave two identical buttons: each
        // gets the opening of its own official ability text (the core's tooltip key; the full text stays as the hover hint)
        foreach(var same in choices.Where(c=>c.Tag=="AbilityLabel").GroupBy(c=>c.Title).Where(g=>g.Count()>1).ToList())
            foreach(var choice in same)
            {
                if(choice.Hint.StartsWith("执行规则允许的",StringComparison.Ordinal))continue;
                string brief=System.Text.RegularExpressions.Regex.Replace(choice.Hint,@"^(快速|反应|行动)\s*[:：]?\s*","");
                int stop=brief.IndexOf('。');if(stop>0)brief=brief[..stop];
                if(brief.Length>18)brief=brief[..17]+"…";
                int at=choices.IndexOf(choice);choices[at]=choice with{Title=choice.Title+"："+brief};
            }
        if(mulligan)title="调整起手牌 · 点击想换掉的牌，或结束选牌";
        else if(commit)title="投入卡牌 · 完成后开始检定";
        string[] reading=tag=="Read"?ClientQuestions.ReadingCards(q).Select(CoreCatalog.Normalize).ToArray():[];
        // the generic "选择下一步" says what is being chosen when every option is the same kind of thing (Yunke 11-22: 智慧古书's
        // "选择你所在地点的一位调查员" showed only 选择下一步), and from which card when the question names its source
        if(title=="选择下一步"&&choices.Count(c=>c.Enabled)>0)
        {
            var kinds=choices.Where(c=>c.Enabled).Select(c=>c.Tag=="PortraitLabel"?"InvestigatorTarget":J.Tag(J.Get(c.Raw,"target"))).Distinct().ToList();
            string? what=kinds.Count!=1?null:kinds[0] switch{"InvestigatorTarget"=>"选择一位调查员","LocationTarget"=>"选择一个地点","EnemyTarget"=>"选择一个敌人","AssetTarget"=>"选择一张支援","CardIdTarget" or "CardTarget"=>"选择一张卡牌","TreacheryTarget"=>"选择一张诡计",_=>null};
            if(what is not null)
            {
                string from="";
                for(var w=state.Question;J.Tag(w) is "QuestionLabel" or "PayCostQuestion" or "QuestionWithSource";w=J.Get(w,"question"))
                    if(J.Tag(w)=="QuestionWithSource"){var src=ClientDisplay.Reference(state.Game,J.Get(w,"source"));var sc=ClientDisplay.CardCode(src);if(sc.Length>0)from=catalog.Name(sc);}
                title=from.Length>0?from+"："+what:what;
            }
        }
        return new(tag,title,body,choices,reading);
    }
    // official chaos token names (html version locale, log.json), and the number tokens as their numbers
    public static string ChaosTokenName(string face)=>face switch{"PlusOne"=>"+1","Zero"=>"0","MinusOne"=>"−1","MinusTwo"=>"−2","MinusThree"=>"−3","MinusFour"=>"−4","MinusFive"=>"−5","MinusSix"=>"−6","MinusSeven"=>"−7","MinusEight"=>"−8","ElderSign"=>"远古印记","AutoFail"=>"自动失败","Skull"=>"骷髅","Cultist"=>"邪教徒","Tablet"=>"石板","ElderThing"=>"远古之物","CurseToken"=>"诅咒","BlessToken"=>"祝福","FrostToken"=>"霜冻",_=>face};
    public ChoicePresentation Describe(JsonElement game,IndexedClientChoice item)
    {
        var c=item.Value;var tag=J.Tag(c);var code="";var target="";var hint="点击确认此操作";
        string Entity(string group,string id){target=id;var e=J.Get(J.Get(game,group),id);code=ClientDisplay.CardCode(e);return code.Length>0?catalog.ShownName(e,code):locale.Plain(J.String(e,"name",id));}
        // what a fight / evade / engage choice names: the core's FightLabel carries the id of an enemy, a concealed mini-card, a
        // location fought as an enemy (迷雾高塔) or an asset — looked up in that order, as TableModel.FightTarget (D 组 #20: it
        // read "攻击 · b534e166-…", the location's id). A concealed mini-card stays face down: "隐匿迷你卡牌", no card code.
        string FightEntity(string id){
            if(J.Get(J.Get(game,"enemies"),id).ValueKind==JsonValueKind.Object)return Entity("enemies",id);
            if(J.Get(J.Get(game,"concealed"),id).ValueKind==JsonValueKind.Object){target=id;code="";return "隐匿迷你卡牌";}
            if(J.Get(J.Get(game,"locations"),id).ValueKind==JsonValueKind.Object)return Entity("locations",id);
            if(J.Get(J.Get(game,"assets"),id).ValueKind==JsonValueKind.Object)return Entity("assets",id);
            return Entity("enemies",id);}
        string Reference(JsonElement reference){
            // a concealed mini-card (its own attack / evade: ConcealedCardSource) stays face down — QQ #44: "攻击 · 当前对象"
            if(J.Tag(reference) is "ConcealedCardSource" or "ConcealedCardTarget"){if(J.Get(reference,"contents").ValueKind==JsonValueKind.String)target=J.String(reference,"contents");code="";return "隐匿迷你卡牌";}
            var e=ClientDisplay.Reference(game,reference);code=ClientDisplay.CardCode(e);var id=J.Get(reference,"contents");if(id.ValueKind==JsonValueKind.String)target=id.GetString()!;if(code.Length>0)return catalog.ShownName(e,code);if(catalog.Cards.ContainsKey(CoreCatalog.Normalize(target))){code=CoreCatalog.Normalize(target);return catalog.Name(code);}return J.Tag(reference)=="EncounterDeckTarget"?"遭遇牌库":locale.Plain(J.String(e,"name","当前对象"));}
        string title;
        switch(tag)
        {
            case "Info":title=System.Text.RegularExpressions.Regex.Replace(locale.Flavor(J.Get(c,"flavor")),"</?dim>","");hint="说明";break;
            case "InvalidLabel":title=locale.Plain(J.String(c,"label"));hint="当前不可执行";break;
            case "Label":case "TooltipLabel":case "Done":case "ScenarioLabel":case "SkillLabelWithLabel":title=LabelText(J.String(c,"label"));break;
            case "GridLabel":title=locale.Plain(J.String(c,"gridLabel"));break;
            case "CardLabel":code=CoreCatalog.Normalize(J.String(c,"cardCode"));title=catalog.Name(code);hint="选择这张卡牌";break;
            case "AbilityLabel":var ability=J.Get(c,"ability");var action=ClientDisplay.BasicActionCaption(ability);var name=Reference(J.Get(ability,"source"));
                // a card's ability while the card is not on the table yet (a tarot card's "在游戏开始时，如果…在你起始手牌中：将其放置
                //入场", Yunke 11-22): its source names an asset that is still in hand, so the name comes from the ability's own card
                if(code.Length==0&&J.String(ability,"cardCode") is {Length:>0} own&&catalog.Cards.ContainsKey(CoreCatalog.Normalize(own))){code=CoreCatalog.Normalize(own);name=catalog.Name(code);}
                var type=J.Get(ability,"type");if(action.Length==0)action=string.Join(" / ",AbilityActions(ability).Select(ActionName).Where(s=>s.Length>0));if(action.Length==0)action=J.Tag(type) switch{"ReactionAbility"=>"响应","FastAbility" or "FastAbility'"=>"快速能力","ForcedAbility"=>"强制效果",_=>"发动能力"};title=action+(name.Length>0?" · "+name:"");hint="执行规则允许的"+action;var abilityTip=J.String(ability,"tooltip");if(abilityTip.Length>0&&locale.Plain(abilityTip) is {Length:>0} tipText&&!tipText.StartsWith("$",StringComparison.Ordinal))hint=tipText;
                // a location's "take control of its key" ability (The Innsmouth Conspiracy, index 500 + the key's place on it):
                // named by the key (QQ #29: it read "发动能力 · 陌生洞室")
                if(abilityTip.StartsWith("$tooltips.takeControlOfKey",StringComparison.Ordinal)){var keys=J.Items(J.Get(ClientDisplay.Reference(game,J.Get(ability,"source")),"keys")).ToList();int ki=J.Int(ability,"index")-500;
                    title=ki>=0&&ki<keys.Count?KeyControlText(keys[ki]):"获得钥匙的控制权";if(name.Length>0)title+=" · "+name;hint="快速能力，免费：拿取这个地点上的钥匙";}
                break;
            case "ComponentLabel":case "AuxiliaryComponentLabel":
                var component=J.Get(c,"component");title=ClientDisplay.ComponentCaption(component);
                target=J.String(component,"investigatorId",J.String(component,"assetId"));
                if(J.Tag(component)=="InvestigatorComponent"&&J.String(component,"tokenType")=="ResourceToken")title="获取1资源";
                else if((J.String(component,"tokenType") is "DamageToken" or "HorrorToken")&&(J.Tag(component) is "InvestigatorComponent" or "AssetComponent"))
                    title=Entity(J.Tag(component)=="AssetComponent"?"assets":"investigators",target)+" · "+title;
                if(title.Length==0)title="使用当前组件";break;
            case "EndTurnButton":title="结束回合";hint="确认结束这名调查员的回合";break;
            case "SkipTriggersButton":title="继续 · 跳过可选响应";hint="不发动本次可选能力，继续结算";break;
            case "StartSkillTestButton":title="开始检定";hint="完成投入，开始抽取混沌标记";break;
            case "SkillTestApplyResultsButton":title="应用检定结果";hint="执行检定的成功或失败效果";break;
            case "ChaosTokenLabel":title="混沌标记 · "+locale.Plain(J.String(c,"face"));break;
            case "ChaosTokenGroupChoice":title="选择这组混沌标记";break;
            case "TargetLabel":
                // a chaos token as the target (歪打正着: treat the revealed token as one of the bag's tokens): the token's own name
                if(J.Tag(J.Get(c,"target"))=="ChaosTokenTarget"){var tk=J.Get(J.Get(c,"target"),"contents");var face=tk.ValueKind==JsonValueKind.Object?J.String(tk,"chaosTokenFace"):"";title="当作 · "+ChaosTokenName(face);hint="把揭示的标记当作这枚标记结算";break;}
                // a token of the bag by its face (不屈不挠 seals tokens; QQ #42: it read "选择 · 当前对象" for every token)
                if(J.Tag(J.Get(c,"target"))=="ChaosTokenFaceTarget"){title="选择 · "+ChaosTokenName(J.String(J.Get(c,"target"),"contents"));break;}
                title="选择 · "+Reference(J.Get(c,"target"));break;
            case "PortraitLabel":title="选择 · "+Entity("investigators",J.String(c,"investigatorId"));break;
            case "FightLabel":case "FightLabelWithSkill":title="攻击 · "+FightEntity(J.String(c,"enemyId"));hint="选择攻击目标";break;
            case "EvadeLabel":case "EvadeLabelWithSkill":title="躲避 · "+FightEntity(J.String(c,"enemyId"));hint="选择躲避目标";break;
            case "EngageLabel":title="交战 · "+FightEntity(J.String(c,"enemyId"));break;
            case "SkillLabel":title="使用"+locale.Plain(J.String(c,"skillType"))+"检定";break;
            case "EffectActionButton":title=locale.Plain(J.String(c,"tooltip","发动效果"));break;
            case "CostLabel":title="支付费用";hint="按当前规则支付显示的费用";break;
            case "CardPile":title="选择这组卡牌";break;
            case "ConnectionLabel":title="选择连接";break;
            case "KeyLabel":title="选择 · "+KeyName(J.Get(c,"key"));break;
            default:title=locale.Plain(J.String(c,"label",J.String(c,"tooltip","执行当前操作")));hint="规则核心提供的操作";break;
        }
        title=title switch{"Draw a card"=>"抽一张牌","Gain 1 resource"=>"获取1资源","Take 1 resource"=>"获取1资源","End turn"=>"结束回合","Continue"=>"继续","Done"=>"完成",
            // a label the core writes in English with no locale key: 发掘古物 (2) (UnearthTheAncients2.hs chooseUpToN), worded as the
            // html's own doNotChooseAnyMoreTokens "不选择更多的标记" (Yunke 11-22)
            "Do not choose any more assets"=>"不选择更多的支援",_=>title};
        if(title.Length==0)title=tag=="Done"?"完成":"继续";
        // official labels carry story emphasis (_冒险 I-A: 课外活动_, *x*): a button shows plain text (playtest 11-18)
        title=System.Text.RegularExpressions.Regex.Replace(title,@"_([^_\n]*)_|\*([^*\n]*)\*","$1$2");
        return new(item.Index,item.Group,tag,title,hint,CoreCatalog.Normalize(code),target,tag is not("Info" or "InvalidLabel"),c.Clone());
    }
    public static IReadOnlyList<string> AbilityActions(JsonElement ability)
    {
        var result=new List<string>();
        void Read(JsonElement actions)
        {
            var contents=J.Get(actions,"contents");
            if(J.Tag(actions)=="SingleAction"&&contents.ValueKind==JsonValueKind.String)result.Add(contents.GetString()!);
            else if(J.Tag(actions) is "AndActions" or "OrActions")foreach(var child in J.Items(contents))Read(child);
        }
        Read(J.Get(J.Get(ability,"type"),"actions"));return result.Distinct(StringComparer.Ordinal).ToArray();
    }
    string LabelText(string raw)
    {
        if(raw.StartsWith("Evade ",StringComparison.Ordinal))
        {
            string printedName=raw["Evade ".Length..];
            var enemy=catalog.Cards.Values.FirstOrDefault(card=>J.String(card,"type_code")=="enemy"&&J.String(card,"real_name")==printedName);
            if(enemy.ValueKind==JsonValueKind.Object)return "躲避 · "+catalog.Name(J.String(enemy,"code"));
        }
        // labels the rules core builds from English names (AI-play 11-22, cycle 1): the order a passed test's results resolve in,
        // "Damage Ghoul Priest" / "Damage \"Wolf-Man\" Drew: The Cannibal" (Enemy/Runner.hs SkillTestResultOption), and
        // "Resolve Through the Gates"; the card's official name from the catalog (a "Name: Subname" title matches on Name)
        var named=Regex.Match(raw,@"^(Damage|Resolve) (.+)$",RegexOptions.CultureInvariant);
        if(named.Success&&EnglishCard(named.Groups[2].Value) is {Length:>0} zh)return (named.Groups[1].Value=="Damage"?"造成伤害 · ":"结算 · ")+zh;
        // the Devil XV (reversed) tarot: "Remove Hand Slot" — the slot's official name (locale slot.*)
        var slot=Regex.Match(raw,@"^(Add|Remove) (Hand|Body|Ally|Accessory|Arcane|Tarot|Head) Slot$",RegexOptions.CultureInvariant);
        if(slot.Success)
        {
            string k=slot.Groups[2].Value.ToLowerInvariant(),nm=locale.Plain("$slot."+k);
            if(nm=="$slot."+k||nm.Length==0)nm=k=="head"?"头部":k;
            return (slot.Groups[1].Value=="Add"?"增加一个":"移除一个")+nm+"槽位";
        }
        if(Assignment(raw,out var damage,out var horror))
        {
            var parts=new List<string>();if(damage is int d)parts.Add(d+"点伤害");if(horror is int h)parts.Add(h+"点恐惧");
            return "分配"+string.Join("和",parts);
        }
        if(raw.StartsWith("$name ",StringComparison.Ordinal)||raw.StartsWith("$label.doNotAddCardToDeck ",StringComparison.Ordinal)||raw.StartsWith("$label.addCardToDeck ",StringComparison.Ordinal))
        {
            var code=Regex.Match(raw,@"(?:^|\s)__name=s:""(\d{5})""");
            if(code.Success&&catalog.Cards.ContainsKey(code.Groups[1].Value))
            {
                string name=catalog.Name(code.Groups[1].Value);
                return raw.StartsWith("$name ",StringComparison.Ordinal)?name:raw.StartsWith("$label.addCardToDeck ",StringComparison.Ordinal)?"选择将“"+name+"”加入哪位调查员的牌组":"不将“"+name+"”加入牌组";
            }
        }
        return locale.Plain(raw);
    }
    string EnglishCard(string english)
    {
        string Find(string n)=>catalog.Cards.Values.FirstOrDefault(c=>J.String(c,"real_name")==n) is {ValueKind:JsonValueKind.Object} c?catalog.Name(J.String(c,"code")):"";
        string hit=Find(english);
        if(hit.Length==0&&english.IndexOf(": ",StringComparison.Ordinal) is int colon and >0)hit=Find(english[..colon]);
        return hit;
    }
    static bool Assignment(string raw,out int? damage,out int? horror)
    {
        damage=null;horror=null;
        var match=Regex.Match(raw,@"^Assign (?:(?<damage>\d+) damage(?: and (?<horror>\d+) horror)?|(?<horror>\d+) horror)$",RegexOptions.CultureInvariant);
        if(!match.Success)return false;
        if(match.Groups["damage"].Success){if(!int.TryParse(match.Groups["damage"].Value,out int d))return false;damage=d;}
        if(match.Groups["horror"].Success){if(!int.TryParse(match.Groups["horror"].Value,out int h))return false;horror=h;}
        return true;
    }
    // A key (The Innsmouth Conspiracy): its colour as the official setup text names it ("红色钥匙"); one lying face down shows
    // no colour (QQ #29); a chaos token used as a key (Edge of the Earth) by its token.
    public static string KeyName(JsonElement key)=>J.Tag(key) switch{"UnrevealedKey"=>"背面朝下的钥匙","TokenKey"=>"标记钥匙",
        "BlueKey"=>"蓝色钥匙","GreenKey"=>"绿色钥匙","RedKey"=>"红色钥匙","YellowKey"=>"黄色钥匙","PurpleKey"=>"紫色钥匙","BlackKey"=>"黑色钥匙","WhiteKey"=>"白色钥匙",_=>"钥匙"};
    static string KeyControlText(JsonElement key)=>J.Tag(key)=="UnrevealedKey"?"获得钥匙的控制权（背面朝下）":"获得"+KeyName(key)+"的控制权";
    static string ActionName(string action)=>action switch{"Draw"=>"抽牌","Resource"=>"获取资源","Play"=>"打出卡牌","Investigate"=>"调查","Move"=>"移动","Fight"=>"攻击","Evade"=>"躲避","Engage"=>"交战","Parley"=>"谈判","Resign"=>"撤退",_=>""};
}
