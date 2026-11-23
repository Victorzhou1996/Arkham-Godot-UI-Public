using System.Text.Json;

namespace ArkhamUI;

// What the AI needs from wherever it runs, so the same AI files compile into the game (Godot) and into the headless
// simulator (tools/sim, no Godot): card data, the chance to pass a test, the scenario code, the project root, a log.
// The game installs its own versions in AiHostGodot.Install(); the simulator installs its own in tools/sim/Program.cs.
public static class AiHost
{
    public static Func<string, JsonElement> Card = _ => default;
    public static Func<TableModel, int, int, double> PassChance = (_, v, d) => v >= d ? 0.7 : 0.3;
    public static Func<TableModel, string> ScenarioCode = _ => "01104";
    public static Action<string> Log = _ => { };
    public static string Root = ".";
    public static string AssetsDir => AssetsOverride ?? Path.Combine(Root, "app", "assets");
    // the trained model files and where AI result rows go; the exported game moves both (AiHostGodot), the editor and the
    // simulator keep the project's evidence/ai folders
    public static string ModelDir => ModelOverride ?? Path.Combine(Root, "evidence", "ai", "model");
    public static string ResultsDir => ResultsOverride ?? Path.Combine(Root, "evidence", "ai");
    public static string? AssetsOverride, ModelOverride, ResultsOverride;
}
