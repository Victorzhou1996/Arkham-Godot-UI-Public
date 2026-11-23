using System.Text.Json;

// Read-only protocol projection and answer construction, never rule messages.
public sealed record IndexedClientChoice(int Index,int Group,JsonElement Value);
public sealed record ClientAmountRow(string Id,string Label,string Investigator,int Minimum,int Maximum);
public static class ClientQuestions
{
    // ChooseOneAtATime requires every effect; the answer only chooses their order.
    // Actual target/cost/optional-response questions remain separate core questions.
    public static int? AutomaticIndex(JsonElement question)
    {
        question=Unwrap(question);string tag=J.Tag(question);
        if(tag is not ("ChooseOneAtATime" or "ChooseOneAtATimeWithAuto" or "ChooseOne" or "WindowChooseOne"))return null;
        var choices=J.Items(J.Get(question,"choices")).ToArray();
        if(choices.Length==0)return null;
        if(tag is "ChooseOneAtATime" or "ChooseOneAtATimeWithAuto")
        {
            if(choices.Any(c=>J.Tag(c) is "Done" or "SkipTriggersButton" or "StartSkillTestButton" or "EndTurnButton"))return null;
            return tag=="ChooseOneAtATimeWithAuto"?1:0;
        }
        for(int i=0;i<choices.Length;i++)
            if(J.Tag(choices[i])=="AbilityLabel"&&J.Tag(J.Get(J.Get(choices[i],"ability"),"type")) is "ForcedAbility" or "SilentForcedAbility")return i;
        if(choices.Length==1&&J.Tag(choices[0]) is "SkipTriggersButton" or "SkillTestApplyResultsButton")return 0;
        return null;
    }
    public static JsonElement IndexAnswer(int index,Guid player,int? version)
    {
        if(index<0)throw new InvalidDataException("No selected original choice");return JsonSerializer.SerializeToElement(new{tag="Answer",contents=new{choice=index,playerId=player.ToString("D"),questionVersion=version}});
    }
    public static bool CanBatch(JsonElement question)
    {
        question=Unwrap(question);if(J.Tag(question) is not ("ChooseOneAtATime" or "ChooseOneAtATimeWithAuto"))return false;var choices=J.Get(question,"choices");if(choices.ValueKind!=JsonValueKind.Array||choices.GetArrayLength()==0)return false;
        return choices.EnumerateArray().All(c=>J.Tag(c) is "Label" or "TooltipLabel" or "CostLabel" or "CardLabel" or "ChaosTokenLabel" or "KeyLabel" or "PortraitLabel" or "TargetLabel" or "SkillLabel" or "SkillLabelWithLabel" or "EvadeLabel" or "EvadeLabelWithSkill" or "FightLabel" or "FightLabelWithSkill" or "EngageLabel" or "GridLabel" or "ConnectionLabel" or "TarotLabel" or "AbilityLabel" or "ComponentLabel" or "AuxiliaryComponentLabel" or "EndTurnButton" or "StartSkillTestButton" or "SkillTestApplyResultsButton" or "ChaosTokenGroupChoice" or "EffectActionButton" or "Done" or "SkipTriggersButton" or "CardPile" or "ScenarioLabel");
    }
    static bool IsPermutation(int count,IReadOnlyList<int> order)=>order.Count==count&&order.Order().SequenceEqual(Enumerable.Range(0,count));
    public static JsonElement OrderedAnswer(JsonElement question,IReadOnlyList<int> order,Guid player,int? version)
    {
        question=Unwrap(question);if(!CanBatch(question)||!IsPermutation(J.Get(question,"choices").GetArrayLength(),order))throw new InvalidDataException("Only a complete original actionable choice ordering is allowed");
        return JsonSerializer.SerializeToElement(new{tag="OrderedAnswer",contents=new{choices=order,playerId=player.ToString("D"),questionVersion=version}});
    }
    public static JsonElement DestinyAnswer(JsonElement question,IReadOnlyList<int> order)
    {
        question=Unwrap(question);var drawings=J.Get(question,"drawings");if(J.Tag(question)!="PickDestiny"||drawings.ValueKind!=JsonValueKind.Array||!IsPermutation(drawings.GetArrayLength(),order))throw new InvalidDataException("Destiny must reassign exactly the original tarot instances");
        var rows=drawings.EnumerateArray().ToArray();return JsonSerializer.SerializeToElement(new{tag="PickDestinyAnswer",contents=rows.Select((r,i)=>new{scenario=J.Get(r,"scenario"),tarot=J.Get(rows[order[i]],"tarot")}).ToArray()});
    }
    public static JsonElement Unwrap(JsonElement question)
    {
        for(int depth=0;depth<16;depth++){if(J.Tag(question) is not ("QuestionLabel" or "PayCostQuestion" or "QuestionWithSource"))return question;question=J.Get(question,"question");}
        throw new InvalidDataException("Question wrappers too deep");
    }
    public static string[] ReadingCards(JsonElement question)
    {
        question=Unwrap(question);if(J.Tag(question)!="Read")throw new InvalidDataException("Not a reading question");var cards=J.Get(question,"readCards");
        if(cards.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)return Array.Empty<string>();
        if(cards.ValueKind!=JsonValueKind.Array||cards.EnumerateArray().Any(c=>c.ValueKind!=JsonValueKind.String||c.GetString() is not string wire||wire.Length<2||wire[0]!='c'))throw new InvalidDataException("阅读卡图列表格式无法识别");
        return cards.EnumerateArray().Select(c=>c.GetString()!).ToArray();
    }
    public static IReadOnlyList<IndexedClientChoice> Choices(JsonElement question)
    {
        question=Unwrap(question);var result=new List<IndexedClientChoice>();int index=0;
        void Append(JsonElement choices,int group=0){if(choices.ValueKind!=JsonValueKind.Array)throw new InvalidDataException("Missing choice list");foreach(var choice in choices.EnumerateArray())result.Add(new(index++,group,choice));}
        switch(J.Tag(question)){
            case "ChooseOneFromEach":int group=0;var groups=J.Get(question,"groups");if(groups.ValueKind!=JsonValueKind.Array)throw new InvalidDataException("Missing groups");foreach(var choices in groups.EnumerateArray())Append(choices,group++);break;
            case "ChooseOneAtATimeWithAuto":result.Add(new(index++,0,JsonSerializer.SerializeToElement(new{tag="Label",label=J.String(question,"label")})));Append(J.Get(question,"choices"));break;
            case "DropDown":foreach(var option in J.Items(J.Get(question,"options"))){if(option.ValueKind!=JsonValueKind.Array||option.GetArrayLength()!=2||option[0].ValueKind!=JsonValueKind.String)throw new InvalidDataException("Invalid dropdown option");result.Add(new(index++,0,JsonSerializer.SerializeToElement(new{tag="Label",label=option[0].GetString()})));}break;
            case "ChooseOneWizard":foreach(var choice in J.Items(J.Get(question,"wizardChoices")))result.Add(new(index++,0,JsonSerializer.SerializeToElement(new{tag="Label",label=J.String(choice,"label")})));break;
            case "Read":var read=J.Get(question,"readChoices");var list=J.Get(read,"contents");if(J.Tag(read) is "BasicReadChoicesN" or "BasicReadChoicesUpToN"){if(list.ValueKind!=JsonValueKind.Array||list.GetArrayLength()!=2)throw new InvalidDataException("Invalid bounded read choices");list=list[1];}else if(J.Tag(read) is not ("BasicReadChoices" or "LeadInvestigatorMustDecide"))throw new InvalidDataException("Unknown read choices");Append(list);break;
            case "ChooseOne":case "WindowChooseOne":case "PlayerWindowChooseOne":case "ChooseN":case "ChooseSome":case "ChooseSome1":case "ChooseUpToN":case "ChooseOneAtATime":case "PickSupplies":Append(J.Get(question,"choices"));break;
            default:throw new InvalidDataException("Question does not offer indexed choices");
        }return result;
    }
    public static IReadOnlyList<ClientAmountRow> AmountRows(JsonElement question)
    {
        question=Unwrap(question);bool payment=J.Tag(question)=="ChoosePaymentAmounts";if(!payment&&J.Tag(question)!="ChooseAmounts")throw new InvalidDataException("Not an amount question");
        var rows=new List<ClientAmountRow>();var ids=new HashSet<string>();var list=J.Get(question,payment?"paymentAmountChoices":"amountChoices");if(list.ValueKind!=JsonValueKind.Array)throw new InvalidDataException("Missing amount choices");
        foreach(var choice in list.EnumerateArray()){
            string id=J.String(choice,"choiceId");if(!Guid.TryParse(id,out _)||!ids.Add(id))throw new InvalidDataException("Invalid or duplicate choice UUID");
            if(!J.Get(choice,"minBound").TryGetInt32Safe(out int lo)||!J.Get(choice,"maxBound").TryGetInt32Safe(out int hi))throw new InvalidDataException("Invalid amount bounds");if(payment)lo=Math.Max(0,lo);if(lo>hi)throw new InvalidDataException("Impossible amount bounds");
            rows.Add(new(id,J.String(choice,payment?"title":"label"),J.String(choice,"investigatorId"),lo,hi));
        }return rows;
    }
    public static bool ValidAmounts(JsonElement question,IReadOnlyDictionary<string,int> values)
    {
        question=Unwrap(question);var rows=AmountRows(question);if(values.Count!=rows.Count||rows.Any(r=>!values.TryGetValue(r.Id,out int n)||n<r.Minimum||n>r.Maximum))return false;
        long sum=0;foreach(var n in values.Values)sum+=n;bool payment=J.Tag(question)=="ChoosePaymentAmounts";var target=J.Get(question,payment?"paymentAmountTargetValue":"amountTargetValue");if(payment&&target.ValueKind==JsonValueKind.Null)return true;var v=J.Get(target,"contents");
        if(J.Tag(target)=="AmountOneOf")return J.Items(v).Any(n=>n.ValueKind==JsonValueKind.Number&&n.TryGetInt64(out long allowed)&&sum==allowed);
        if(v.ValueKind!=JsonValueKind.Number||!v.TryGetInt64(out long total))return false;
        return J.Tag(target) switch{"TotalAmountTarget"=>sum==total,"MinAmountTarget"=>sum>=total,"MaxAmountTarget"=>sum<=total,_=>false};
    }
    public static JsonElement AmountAnswer(JsonElement question,IReadOnlyDictionary<string,int> values,Guid player,int? version)
    {
        question=Unwrap(question);if(!ValidAmounts(question,values))throw new InvalidDataException("Amount answer violates displayed bounds");return JsonSerializer.SerializeToElement(new{tag=J.Tag(question)=="ChoosePaymentAmounts"?"PaymentAmountsAnswer":"AmountsAnswer",contents=new{amounts=values,playerId=player.ToString("D"),questionVersion=version}});
    }
    public static (int Minimum,int Maximum) ExchangeBounds(JsonElement question)
    {
        question=Unwrap(question);if(J.Tag(question)!="ChooseExchangeAmounts"||!J.Get(question,"investigator1InitialAmount").TryGetInt32Safe(out int first)||!J.Get(question,"investigator2InitialAmount").TryGetInt32Safe(out int second)||first<0||second<0)throw new InvalidDataException("Invalid exchange bounds");return(-second,first);
    }
    public static JsonElement ExchangeAnswer(JsonElement question,int amount)
    {
        question=Unwrap(question);var(lo,hi)=ExchangeBounds(question);if(amount<lo||amount>hi)throw new InvalidDataException("Exchange outside displayed range");
        return JsonSerializer.SerializeToElement(new{tag="ExchangeAmountsAnswer",source=J.Get(question,"source"),fromInvestigator=J.Get(question,"investigator1Id"),toInvestigator=J.Get(question,"investigator2Id"),token=J.Get(question,"token"),amount});
    }
}
