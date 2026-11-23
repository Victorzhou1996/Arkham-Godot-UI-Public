using System.Text.Json;

static class AutomaticFlowProbe
{
    public static void Run(string root)
    {
        // Recorded core questions: verify that automation never consumes real decisions.
        using var trace=JsonDocument.Parse(File.ReadAllText(Path.Combine(root,"evidence/probe-20261116-034828/victory-trace.json")));
        var records=trace.RootElement.EnumerateArray().ToArray();
        foreach(var check in new (int Sequence,int? Index,string Name)[]{
            (10,0,"apply skill result"),(21,0,"mandatory attic trigger"),
            (3,null,"mulligan"),(9,null,"commit cards"),(18,null,"start test"),
            (35,null,"Roland reaction"),(42,null,"agenda consequence choice"),
            (47,null,"discard target"),(57,null,"act response"),(78,null,"campaign reward")})
        {
            var question=records.First(r=>r.GetProperty("sequence").GetInt32()==check.Sequence).GetProperty("question");
            Check(check.Name,question,check.Index);
        }
        CheckJson("required effects in listed order","""{"tag":"ChooseOneAtATime","choices":[{"tag":"Label","label":"discover clue"},{"tag":"CardLabel","cardCode":"c01039"}]}""",0);
        CheckJson("ordered effects with synthetic auto option","""{"tag":"ChooseOneAtATimeWithAuto","choices":[{"tag":"Label"},{"tag":"CardLabel"}]}""",1);
        CheckJson("optional effects keep skip","""{"tag":"ChooseOneAtATime","choices":[{"tag":"CardLabel"},{"tag":"SkipTriggersButton"}]}""",null);
        CheckJson("empty response window","""{"tag":"WindowChooseOne","choices":[{"tag":"SkipTriggersButton"}]}""",0);
        Console.WriteLine("PASS 14 automatic-flow checks; recorded core decisions preserved; no save modified.");
    }
    static void CheckJson(string name,string json,int? expected)
    {using var document=JsonDocument.Parse(json);Check(name,document.RootElement,expected);}
    static void Check(string name,JsonElement question,int? expected)
    {
        int? actual=ClientQuestions.AutomaticIndex(question);
        if(actual!=expected)throw new Exception(name+": expected "+expected+", got "+actual);
        if(actual is int index&&!ClientQuestions.Choices(question).Any(c=>c.Index==index))throw new Exception(name+": non-original index");
        Console.WriteLine("PASS "+name+" => "+(actual?.ToString()??"wait for player"));
    }
}
