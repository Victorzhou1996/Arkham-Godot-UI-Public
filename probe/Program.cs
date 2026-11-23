using System.Text.Json;
using Gathering;

var root=Path.GetFullPath(args[0]);
if(args.Contains("--undo")){await UndoProbe.Run(root);return;}
if(args.Length>2&&args[1]=="--labels"){Console.OutputEncoding=System.Text.Encoding.UTF8;var cat=new CoreCatalog(Path.Combine(root,"app/assets/data/core-cards.json"));using var dict=JsonDocument.Parse(File.ReadAllText(Path.Combine(root,"app/assets/data/locale-zh.json")));var pr=new QuestionPresenter(cat,new ClientLocale(dict.RootElement));var mi=typeof(QuestionPresenter).GetMethod("LabelText",System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Instance)!;foreach(var raw in args.Skip(2))Console.WriteLine(raw+" => "+mi.Invoke(pr,new object[]{raw}));return;}
if(args.Length>2&&args[1]=="--setupdecks"){await SetupCardsProbe.RunDecks(root,args[2]);return;}
if(args.Length>2&&args[1]=="--validate"){await MakeDeckProbe.Run(root,args[2]);return;}
if(args.Length>4&&args[1]=="--keepdeck"){await KeepDeckProbe.Run(root,args[2],args[3],long.Parse(args[4]));return;}
if(args.Length>2&&args[1]=="--setupcards"){await SetupCardsProbe.Run(root,args[2]);return;}
if(args.Length>2&&args[1]=="--xpcards"){await XpCardsProbe.Run(root,args[2],args.Length>3?args[3]:"01",args.Length>4?int.Parse(args[4]):1);return;}
if(args.Contains("--automatic")){AutomaticFlowProbe.Run(root);return;}
if(args.Length>1&&args[1]=="--starts"){await StartsProbe.Run(root,args[2],args.Length>3?int.Parse(args[3]):40);return;}
if(args.Length>1&&args[1]=="--host-methods"){CoreListProbe.HostMethods();return;}
if(args.Length>1&&args[1]=="--pure"){await CoreListProbe.Pure(root,args[2],args.Length>3?args[3]:"{}");return;}
if(args.Length>1&&args[1]=="--carcosa"){await CarcosaProbe.Run(root,args.Length>2?args[2]:"flow",args.Length>3?int.Parse(args[3]):40);return;}
if(args.Length>1&&args[1]=="--searchshuffle"){await SearchShuffleProbe.Run(root,args.Length>2?args[2]:"01032",args.Length>3?int.Parse(args[3]):3);return;}
if(args.Length>1&&args[1]=="--campaign"){await DunwichProbe.Run(root,args[2],args.Length>3?args[3]:null);return;}
var data=Path.Combine(root,"evidence","probe-"+DateTime.UtcNow.ToString("yyyyMMdd-HHmmss"));
Directory.CreateDirectory(data);
await using var backend=new PrototypeBackend(root,data);await backend.Ready;
var catalog=new CoreCatalog(Path.Combine(root,"app/assets/data/core-cards.json"));
using var dictionary=JsonDocument.Parse(File.ReadAllText(Path.Combine(root,"app/assets/data/locale-zh.json")));
var presenter=new QuestionPresenter(catalog,new ClientLocale(dictionary.RootElement));
var decks=new Dictionary<string,JsonElement>();
foreach(var investigator in catalog.Investigators)
{
    var code=J.String(investigator,"code");var schema=await backend.BuildSchema(code);var deck=catalog.Starter(code,schema);
    var validation=await backend.ValidateDeck(code,J.String(schema,"schemaId"),deck);
    if(J.String(validation,"status")!="valid")throw new Exception("Invalid starter "+code+": "+validation.GetRawText());
    decks.Add(code,deck);File.WriteAllText(Path.Combine(data,"starter-"+code+".json"),deck.GetRawText());
}
var upgradedSlots=CoreCatalog.Slots(decks["01001"]);
upgradedSlots.Remove("01016");upgradedSlots["01029"]=2;
upgradedSlots.Remove("01034");upgradedSlots["01028"]=2;
var upgradedNode=System.Text.Json.Nodes.JsonNode.Parse(catalog.Deck("01001",upgradedSlots,"罗兰 · 12经验独立剧本").GetRawText())!.AsObject();
upgradedNode["meta"]="{\"gathering_standalone_xp\":19}";
var upgraded=await backend.PrepareStandaloneDeck(JsonSerializer.SerializeToElement(upgradedNode));
var upgradedAgain=await backend.PrepareStandaloneDeck(upgraded);
if(upgraded.GetRawText()!=upgradedAgain.GetRawText())throw new Exception("Repeated standalone preparation changed chosen weaknesses");
var upgradedSchema=await backend.BuildSchema("01001");var upgradedCheck=await backend.ValidateDeck("01001",J.String(upgradedSchema,"schemaId"),upgraded);
if(J.String(upgradedCheck,"status")!="valid")throw new Exception("12XP construction rejected: "+upgradedCheck.GetRawText());
if(StandaloneDecks.Extras(upgraded).Length!=1||catalog.Experience(upgraded)!=12)throw new Exception("Standalone XP/weakness mismatch");
var boundaryCases=new List<object>();
async Task RejectDeck(string title,System.Text.Json.Nodes.JsonObject node)
{
    var value=JsonSerializer.SerializeToElement(node);var check=await backend.ValidateDeck("01001",J.String(upgradedSchema,"schemaId"),value);
    if(J.String(check,"status")=="valid")throw new Exception("Boundary accepted "+title);
    boundaryCases.Add(new{title,status=J.String(check,"status"),errors=J.Get(check,"errors")});
}
var outside=System.Text.Json.Nodes.JsonNode.Parse(decks["01001"].GetRawText())!.AsObject();outside["slots"]!["02001"]=1;await RejectDeck("outside-core-slot",outside);
var hidden=System.Text.Json.Nodes.JsonNode.Parse(decks["01001"].GetRawText())!.AsObject();hidden["meta"]="{\"extra_deck\":\"02001\"}";await RejectDeck("outside-core-extra-deck-metadata",hidden);
var parallel=System.Text.Json.Nodes.JsonNode.Parse(decks["01001"].GetRawText())!.AsObject();parallel["meta"]="{\"alternate_front\":\"90024\"}";await RejectDeck("parallel-face-metadata",parallel);
var wrongFaction=System.Text.Json.Nodes.JsonNode.Parse(upgraded.GetRawText())!.AsObject();wrongFaction["slots"]!["01020"]=1;wrongFaction["slots"]!["01043"]=1;await RejectDeck("outside-investigator-level-grant",wrongFaction);
var fifty=System.Text.Json.Nodes.JsonNode.Parse(upgraded.GetRawText())!.AsObject();fifty["slots"]!["01029"]=12;
bool fiftyRejected=false;try{await backend.PrepareStandaloneDeck(JsonSerializer.SerializeToElement(fifty));}catch(InvalidOperationException){fiftyRejected=true;}
if(!fiftyRejected)throw new Exception("50+ XP accepted");boundaryCases.Add(new{title="50-plus-xp",status="rejected"});
File.WriteAllText(Path.Combine(data,"core-boundaries.json"),JsonSerializer.Serialize(boundaryCases));
File.WriteAllText(Path.Combine(data,"starter-01001-12xp.json"),upgraded.GetRawText());
File.WriteAllText(Path.Combine(data,"standalone-12xp-validation.json"),upgradedCheck.GetRawText());
var traces=new List<object>();
foreach(var sample in new[]{(Count:1,Advanced:false),(Count:4,Advanced:false),(Count:1,Advanced:true)})
{
    int count=sample.Count;string prefix=sample.Advanced?"1p-12xp":count+"p";
    var state=await backend.Create(count);var reached=false;
    for(int step=0;step<80;step++)
    {
        if(state.Question.ValueKind!=JsonValueKind.Object)
        {
            var asked=state.Seats.FirstOrDefault(s=>s.HasQuestion);
            if(asked is null)break;
            state=await backend.Open(state.Id,asked.Player);
        }
        var q=ClientQuestions.Unwrap(state.Question);var ui=presenter.Present(state);
        Console.WriteLine(prefix+" #"+state.Sequence+" "+ui.Tag+" "+string.Join(" | ",ui.Choices.Take(5).Select(c=>c.Index+":"+c.Title)));
        traces.Add(new{seats=count,gameId=state.Id,sequence=state.Sequence,player=state.Player,question=state.Question,presentation=ui});
        File.WriteAllText(Path.Combine(data,prefix+"-step-"+state.Sequence+".json"),state.View.GetRawText());
        if(J.String(state.Game,"phase")=="InvestigationPhase" && ui.Tag is "PlayerWindowChooseOne" or "WindowChooseOne" && J.Props(J.Get(state.Game,"locations")).Any())
        {
            reached=true;
            File.WriteAllText(Path.Combine(data,prefix+"-investigation-view.json"),state.View.GetRawText());
            File.WriteAllText(Path.Combine(data,prefix+"-investigation-save.json"),backend.SavedDocument(state).GetRawText());
            if(count==1&&!sample.Advanced)
            {
                var investigate=ui.Choices.FirstOrDefault(c=>c.Tag=="AbilityLabel"&&ClientDisplay.BasicActionCaption(J.Get(c.Raw,"ability"))=="调查");
                if(investigate is null)throw new Exception("No investigate action in study");
                state=await backend.Choice(state,investigate.Index);
                for(int tick=0;tick<14;tick++)
                {
                    var testUi=presenter.Present(state);
                    File.WriteAllText(Path.Combine(data,"1p-skilltest-"+tick+".json"),state.View.GetRawText());
                    traces.Add(new{seats=count,gameId=state.Id,sequence=state.Sequence,player=state.Player,question=state.Question,presentation=testUi});
                    Console.WriteLine("skill #"+state.Sequence+" "+testUi.Tag+" "+string.Join(" | ",testUi.Choices.Select(c=>c.Index+":"+c.Title)));
                    var stepChoice=testUi.Choices.FirstOrDefault(c=>c.Enabled&&c.Tag=="SkipTriggersButton")
                        ??testUi.Choices.FirstOrDefault(c=>c.Enabled&&c.Tag=="StartSkillTestButton")
                        ??testUi.Choices.FirstOrDefault(c=>c.Enabled&&c.Tag=="SkillTestApplyResultsButton");
                    if(stepChoice is null)break;
                    state=await backend.Choice(state,stepChoice.Index);
                }
            }
            break;
        }
        switch(ui.Tag)
        {
            case "PickScenarioSettings":state=await backend.DefaultSettings(state);break;
            case "ChooseDeck":
                var index=Array.FindIndex(state.Seats,s=>s.Player==state.Player);
                state=await backend.SelectDeck(state,sample.Advanced?upgraded:decks["0100"+(index+1)]);break;
            case "ChooseAmounts":case "ChoosePaymentAmounts":
                var rows=ClientQuestions.AmountRows(q);var amounts=rows.ToDictionary(r=>r.Id,r=>r.Minimum);
                if(!ClientQuestions.ValidAmounts(q,amounts))throw new Exception("Need manual amount allocation: "+q.GetRawText());
                state=await backend.Amounts(state,amounts);break;
            default:
                var selected=ui.Choices.FirstOrDefault(c=>c.Enabled && (c.Tag=="Done" || c.Title.Contains("保留") || c.Title.Contains("Keep"))) ?? ui.Choices.FirstOrDefault(c=>c.Enabled);
                if(selected is null)throw new Exception("Unimplemented question "+ui.Tag+": "+q.GetRawText());
                state=await backend.Choice(state,selected.Index);break;
        }
    }
    if(!reached)throw new Exception("Did not reach investigation "+count+"p");
}
File.WriteAllText(Path.Combine(data,"trace.json"),JsonSerializer.Serialize(traces,new JsonSerializerOptions{WriteIndented=true}));
File.WriteAllText(Path.Combine(root,"evidence","latest-probe.json"),JsonSerializer.Serialize(new{dataDirectory=data,createdAt=DateTimeOffset.UtcNow}));
Console.WriteLine("PASS 5 starter decks; 1p and 4p investigation. Evidence: "+data);
if(args.Contains("--full")||args.Contains("--paths")||args.Contains("--victory"))foreach(var policy in args.Contains("--victory")?new[]{"victory"}:args.Contains("--paths")?new[]{"resign","victory"}:new[]{"failure","resign","victory"})await ScenarioProbe.Run(backend,catalog,presenter,policy=="resign"?decks["01002"]:upgraded,data,policy);
