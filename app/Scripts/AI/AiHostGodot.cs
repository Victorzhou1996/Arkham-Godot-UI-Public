using Godot;

namespace ArkhamUI;

// The game's side of AiHost: the table's own card catalog, test odds (TableScreen.Odds) and the Godot log.
// Not compiled into the headless simulator (tools/sim/ArkhamSim.csproj leaves this file out).
public static class AiHostGodot
{
    public static void Install()
    {
        AiHost.Card = c => App.I.S!.Card(c);
        AiHost.PassChance = TableScreen.PassChance;
        AiHost.ScenarioCode = TableScreen.ScenarioCode;
        AiHost.Log = s => GD.Print(s);
        AiHost.Root = App.I.Root;
        // exported build (App.ExportedWindows): the model ships in gamei, the assets in gamessets; result rows go to the
        // player's data folder, never the install folder
        if (App.ExportedWindows) { AiHost.AssetsOverride = App.I.Assets; AiHost.ModelOverride = System.IO.Path.Combine(App.I.Root, "ai"); AiHost.ResultsOverride = System.IO.Path.Combine(App.I.DataRoot, "ai"); }
    }
}
