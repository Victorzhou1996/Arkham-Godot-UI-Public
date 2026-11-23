using System.Text.Json;
using Gathering;

static class ScenarioProbe
{
    static string Action(ChoicePresentation c)
    {
        return QuestionPresenter.AbilityActions(J.Get(c.Raw,"ability")).FirstOrDefault()??"";
    }
    public static async Task Run(PrototypeBackend backend,CoreCatalog catalog,QuestionPresenter presenter,JsonElement deck,string data,string policy)
    {
        var state=await backend.Create(1,"Easy");var rows=new List<object>();var tags=new HashSet<string>();var choiceTags=new HashSet<string>();bool choseResign=false;double maximumMs=0;
        for(int tick=0;tick<450;tick++)
        {
            if(state.Question.ValueKind!=JsonValueKind.Object)
            {
                var asked=state.Seats.FirstOrDefault(s=>s.HasQuestion);
                if(asked is null)
                {
                    File.WriteAllText(Path.Combine(data,policy+"-final-view.json"),state.View.GetRawText());
                    File.WriteAllText(Path.Combine(data,policy+"-final-save.json"),backend.SavedDocument(state).GetRawText());
                    File.WriteAllText(Path.Combine(data,policy+"-trace.json"),JsonSerializer.Serialize(rows));
                    var outcome=J.Get(state.Game,"outcome");
                    bool expected=policy=="resign"?choseResign:policy=="victory"?J.String(outcome,"result")=="Won":J.String(outcome,"result")=="Lost";
                    File.WriteAllText(Path.Combine(data,policy+"-result.json"),JsonSerializer.Serialize(new{policy,difficulty="Easy",choseResign,expected,outcome,questionTags=tags,choiceTags,maximumMs}));
                    File.WriteAllText(Path.Combine(data,policy+"-logs.json"),JsonSerializer.Serialize(await backend.Logs(state)));
                    Console.WriteLine(policy+" TERMINAL expected="+expected+" "+J.Get(state.Game,"gameState").GetRawText()+" outcome="+outcome.GetRawText());return;
                }
                state=await backend.Open(state.Id,asked.Player);
            }
            var q=ClientQuestions.Unwrap(state.Question);var ui=presenter.Present(state);
            maximumMs=Math.Max(maximumMs,backend.LastOperationMs);foreach(var choice in ui.Choices)choiceTags.Add(choice.Tag);
            if(tags.Add(ui.Tag)||tick%20==0)File.WriteAllText(Path.Combine(data,policy+"-step-"+state.Sequence+".json"),state.View.GetRawText());
            rows.Add(new{sequence=state.Sequence,lastOperationMs=backend.LastOperationMs,question=state.Question,choices=ui.Choices.Select(c=>new{c.Index,c.Title,c.Tag,c.CardCode,c.TargetId})});
            if(ui.Tag=="PickScenarioSettings"){state=await backend.DefaultSettings(state);continue;}
            if(ui.Tag=="ChooseDeck"){state=await backend.SelectDeck(state,deck);continue;}
            if(ui.Tag is "ChooseAmounts" or "ChoosePaymentAmounts")
            {
                var amountRows=ClientQuestions.AmountRows(q);var amounts=amountRows.ToDictionary(r=>r.Id,r=>r.Minimum);
                var target=J.Get(q,ui.Tag=="ChooseAmounts"?"amountTargetValue":"paymentAmountTargetValue");
                int wanted=J.Get(target,"contents").TryGetInt32Safe(out int total)?total:amounts.Values.Sum();
                foreach(var row in amountRows){int add=Math.Min(Math.Max(0,wanted-amounts.Values.Sum()),row.Maximum-amounts[row.Id]);amounts[row.Id]+=add;}
                if(!ClientQuestions.ValidAmounts(q,amounts))throw new Exception(policy+" cannot allocate "+q.GetRawText());
                state=await backend.Amounts(state,amounts);continue;
            }
            var choices=ui.Choices.Where(c=>c.Enabled).ToArray();ChoicePresentation? selected=null;
            selected=choices.FirstOrDefault(c=>c.Title.Contains("随机弃掉",StringComparison.Ordinal));
            if(ui.Tag=="PlayerWindowChooseOne")
            {
                var investigator=J.Props(J.Get(state.Game,"investigators")).Select(p=>p.Value).First(i=>J.String(i,"playerId")==state.Player.ToString("D"));
                var placement=J.Get(investigator,"placement");var placed=J.Get(placement,"contents");
                var location=J.Get(J.Get(state.Game,"locations"),placed.ValueKind==JsonValueKind.String?placed.GetString()!:"");
                if(policy=="failure")selected=choices.FirstOrDefault(c=>c.Tag=="EndTurnButton");
                else
                {
                    selected=choices.FirstOrDefault(c=>J.Tag(J.Get(J.Get(c.Raw,"ability"),"source"))=="ActSource"||J.Tag(J.Get(c.Raw,"target"))=="ActTarget");
                    if(policy=="resign")selected??=choices.FirstOrDefault(c=>Action(c)=="Resign");
                    if(policy=="resign")selected??=choices.FirstOrDefault(c=>Action(c)=="Move"&&c.CardCode=="01115");
                    selected??=choices.FirstOrDefault(c=>Action(c)=="Fight"&&J.Get(J.Get(c.Raw,"ability"),"basic").ValueKind!=JsonValueKind.True);
                    selected??=choices.FirstOrDefault(c=>Action(c)=="Fight");
                    bool hasWeapon=J.Items(J.Get(investigator,"assets")).Any(id=>new[]{"01006","01016","01020","01029"}.Contains(ClientDisplay.CardCode(J.Get(J.Get(state.Game,"assets"),id.GetString()!))));
                    if(!hasWeapon)selected??=choices.FirstOrDefault(c=>c.Tag=="TargetLabel"&&new[]{"01006","01016","01020","01029"}.Contains(c.CardCode));
                    bool Owns(string code)=>J.Items(J.Get(investigator,"assets")).Any(id=>ClientDisplay.CardCode(J.Get(J.Get(state.Game,"assets"),id.GetString()!))==code);
                    selected??=choices.FirstOrDefault(c=>c.Tag=="TargetLabel"&&c.CardCode=="01033"&&!Owns("01033"));
                    selected??=choices.FirstOrDefault(c=>c.Tag=="TargetLabel"&&c.CardCode=="01030"&&!Owns("01030"));
                    selected??=choices.FirstOrDefault(c=>c.Tag=="TargetLabel"&&c.CardCode=="01059"&&!Owns("01059"));
                    selected??=choices.FirstOrDefault(c=>c.Tag=="TargetLabel"&&c.CardCode=="01028"&&!Owns("01028")&&!Owns("01033"));
                    if(ClientDisplay.Token(location,"Clue")>0)selected??=choices.FirstOrDefault(c=>c.Tag=="TargetLabel"&&c.CardCode=="01037");
                    int clues=ClientDisplay.Token(investigator,"Clue")??0;
                    string here=ClientDisplay.CardCode(location);
                    if(clues>=3&&here!="01111")
                    {
                        if(here=="01112")selected??=choices.FirstOrDefault(c=>c.Tag=="EndTurnButton");
                        else selected??=choices.FirstOrDefault(c=>Action(c)=="Move"&&c.CardCode=="01112");
                    }
                    selected??=choices.FirstOrDefault(c=>Action(c)=="Investigate"&&ClientDisplay.Token(location,"Clue")>0);
                    selected??=choices.Where(c=>Action(c)=="Move").OrderByDescending(c=>
                    {
                        var destination=J.Get(J.Get(state.Game,"locations"),c.TargetId);
                        if(policy=="resign"&&c.CardCode=="01115")return 200;
                        if(c.CardCode=="01113"&&(ClientDisplay.Token(destination,"Clue")>0||J.Get(destination,"revealed").ValueKind==JsonValueKind.False))return 110;
                        if(ClientDisplay.Token(destination,"Clue")>0)return 100;
                        if(J.Get(destination,"revealed").ValueKind==JsonValueKind.False)return c.CardCode=="01113"?95:90;
                        if(c.CardCode=="01112")return 20;
                        return 0;
                    }).FirstOrDefault();
                    selected??=choices.FirstOrDefault(c=>c.Tag=="EndTurnButton");
                }
            }
            selected??=choices.FirstOrDefault(c=>J.Tag(J.Get(J.Get(c.Raw,"ability"),"source"))=="ActSource"||J.Tag(J.Get(c.Raw,"target"))=="ActTarget");
            if(selected is null&&choices.Any(c=>c.Tag=="StartSkillTestButton"))
            {
                var test=J.Get(state.Game,"skillTest");
                if(J.Int(test,"modifiedSkillValue")<J.Int(test,"modifiedDifficulty")+2)
                    selected=choices.Where(c=>c.Tag=="TargetLabel"&&c.Title.StartsWith("投入 · ",StringComparison.Ordinal)&&J.Tag(J.Get(c.Raw,"target"))=="CardIdTarget")
                        .OrderBy(c=>Array.IndexOf(new[]{"01093","01090","01039","01091","01025","01089"},c.CardCode) is int n&&n>=0?n:100)
                        .FirstOrDefault(c=>new[]{"01093","01090","01039","01091","01025","01089"}.Contains(c.CardCode));
            }
            if(selected is null&&ui.Tag=="WindowChooseOne")
                selected=choices.FirstOrDefault(c=>c.Tag=="TargetLabel"&&c.CardCode is "01022" or "01023" or "01065")
                    ??choices.FirstOrDefault(c=>J.Tag(J.Get(J.Get(c.Raw,"ability"),"source"))=="InvestigatorSource"&&J.String(J.Get(J.Get(c.Raw,"ability"),"source"),"contents")=="c01001");
            selected??=choices.FirstOrDefault(c=>c.Tag=="SkipTriggersButton")??choices.FirstOrDefault(c=>c.Tag=="StartSkillTestButton")??choices.FirstOrDefault(c=>c.Tag=="SkillTestApplyResultsButton");
            selected??=choices.FirstOrDefault(c=>c.Tag=="Done"||c.Title.Contains("结束选牌")||c.Title.Contains("Keep"));
            // Location selections are genuine offered targets. Prefer the hall
            // for resignation, and rooms with clues for progressing the act.
            if(selected is null&&choices.Any(c=>J.Tag(J.Get(c.Raw,"target"))=="LocationTarget"))
                selected=choices.FirstOrDefault(c=>c.CardCode=="01112"&&policy=="resign")
                    ??choices.FirstOrDefault(c=>ClientDisplay.Token(J.Get(J.Get(state.Game,"locations"),c.TargetId),"Clue")>0)
                    ??choices.FirstOrDefault(c=>c.CardCode is "01113" or "01114")??choices.FirstOrDefault(c=>c.Enabled);
            selected??=choices.FirstOrDefault();
            if(selected is null)throw new Exception(policy+" unsupported question "+q.GetRawText());
            if(Action(selected)=="Resign")choseResign=true;
            Console.WriteLine(policy+" #"+state.Sequence+" "+ui.Tag+" -> "+selected.Title);
            state=await backend.Choice(state,selected.Index);
        }
        File.WriteAllText(Path.Combine(data,policy+"-limit-view.json"),state.View.GetRawText());
        File.WriteAllText(Path.Combine(data,policy+"-trace.json"),JsonSerializer.Serialize(rows));
        throw new Exception(policy+" reached action limit");
    }
}
