using System.Text.Json;
using Gathering;

static class UndoProbe
{
    public static async Task Run(string root)
    {
        string data=Path.Combine(root,"evidence","ui-fixes-20261117","undo-"+DateTime.UtcNow.ToString("HHmmss"));
        await using var backend=new PrototypeBackend(root,data);await backend.Ready;
        var catalog=new CoreCatalog(Path.Combine(root,"app/assets/data/core-cards.json"));
        using var locale=JsonDocument.Parse(File.ReadAllText(Path.Combine(root,"app/assets/data/locale-zh.json")));
        var presenter=new QuestionPresenter(catalog,new ClientLocale(locale.RootElement));
        var state=await backend.Create(2);
        for(int i=0;i<60;i++)
        {
            if(state.Question.ValueKind!=JsonValueKind.Object)
                state=await backend.Open(state.Id,state.Seats.First(s=>s.HasQuestion).Player);
            var ui=presenter.Present(state);var q=ClientQuestions.Unwrap(state.Question);
            if(J.Tag(q)=="PlayerWindowChooseOne"&&J.String(state.Game,"phase")=="InvestigationPhase")break;
            if(ui.Tag=="PickScenarioSettings")state=await backend.DefaultSettings(state);
            else if(ui.Tag=="ChooseDeck")
            {
                string code="0100"+(Array.FindIndex(state.Seats,s=>s.Player==state.Player)+1);
                var schema=await backend.BuildSchema(code);state=await backend.SelectDeck(state,catalog.Starter(code,schema));
            }
            else if(ui.Tag is "ChooseAmounts" or "ChoosePaymentAmounts")
                state=await backend.Amounts(state,ClientQuestions.AmountRows(q).ToDictionary(r=>r.Id,r=>r.Minimum));
            else
            {
                var c=ui.Choices.FirstOrDefault(c=>c.Enabled&&(c.Tag=="Done"||c.Title.Contains("Keep")||c.Title.Contains("保留")))??ui.Choices.First(c=>c.Enabled);
                state=await backend.Choice(state,c.Index);
            }
        }
        if(J.Tag(ClientQuestions.Unwrap(state.Question))!="PlayerWindowChooseOne")throw new Exception("Preparation failed");
        var start=state;string expected=backend.SavedDocument(start).GetProperty("snapshot").GetProperty("game").GetRawText();
        Console.WriteLine("START "+start.Sequence+" "+string.Join(" | ",presenter.Present(start).Choices.Select(c=>c.Index+":"+c.Title)));
        var resource=presenter.Present(state).Choices.First(c=>c.Enabled&&ClientDisplay.ComponentCaption(J.Get(c.Raw,"component"))=="资源操作");
        state=await backend.Choice(state,resource.Index);
        await CheckRestore("resource",start,state);
        state=await backend.Open(start.Id,start.Player);
        var investigate=presenter.Present(state).Choices.First(c=>c.Enabled&&ClientDisplay.BasicActionCaption(J.Get(c.Raw,"ability"))=="调查");
        var investigationStart=state;
        state=await backend.Choice(state,investigate.Index);
        await CheckRestore("investigation-mid-action",investigationStart,state);
        state=await backend.Open(start.Id,start.Player);investigationStart=state;
        investigate=presenter.Present(state).Choices.First(c=>c.Enabled&&ClientDisplay.BasicActionCaption(J.Get(c.Raw,"ability"))=="调查");
        state=await backend.Choice(state,investigate.Index);
        bool revealed=false;
        for(int i=0;i<16&&J.Tag(ClientQuestions.Unwrap(state.Question))!="PlayerWindowChooseOne";i++)
        {
            var test=J.Get(state.Game,"skillTest");
            if(J.Items(J.Get(test,"revealedChaosTokens")).Any())revealed=true;
            var ui=presenter.Present(state);
            Console.WriteLine("TEST "+state.Sequence+" "+string.Join(" | ",ui.Choices.Select(c=>c.Tag+":"+c.Title)));
            var c=ui.Choices.FirstOrDefault(c=>c.Enabled&&c.Tag is "Done" or "StartSkillTestButton" or "SkillTestApplyResultsButton")??ui.Choices.First(c=>c.Enabled);
            state=await backend.Choice(state,c.Index);
        }
        if(!revealed)throw new Exception("No chaos token was revealed");
        // Reopen also exercises persisted history, rather than a UI checkpoint.
        state=await backend.Open(state.Id,state.Player);
        await CheckRestore("completed-test-after-reopen",investigationStart,state);
        File.WriteAllText(Path.Combine(data,"result.json"),JsonSerializer.Serialize(new{status="pass",gameId=state.Id,checks=new[]{"resource","mid-action","completed-test-after-reopen"}}));
        Console.WriteLine("PASS undo checks; data="+data);

        async Task CheckRestore(string label,TableState target,TableState current)
        {
            if(current.ActionStartSequence!=target.Sequence)throw new Exception(label+" wrong target "+current.ActionStartSequence+" != "+target.Sequence);
            var restored=await backend.UndoAction(current);
            string actual=backend.SavedDocument(restored).GetProperty("snapshot").GetProperty("game").GetRawText();
            if(actual!=expected)throw new Exception(label+" restored game differs");
            try{await backend.UndoAction(current);throw new Exception("Stale undo accepted");}
            catch(InvalidOperationException){}
            Console.WriteLine("PASS "+label+" -> "+target.Sequence);
        }
    }
}
