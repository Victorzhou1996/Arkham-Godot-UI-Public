using Godot;
using Path = System.IO.Path;

namespace ArkhamUI;

// Named UI actions that screens register, so automated runs can drive and capture the real UI.
public static class QaScript
{
    public static readonly Dictionary<string, Func<string, Task>> Actions = new();
    public static void Register(string name, Func<string, Task> action) => Actions[name] = action;
    public static void Register(string name, Action<string> action) => Actions[name] = a => { action(a); return Task.CompletedTask; };

    static float[] Nums(string arg) => arg.Split(',').Select(s => float.Parse(s, System.Globalization.CultureInfo.InvariantCulture)).ToArray();

    // Sends press, motion and release through the viewport, so Controls receive them exactly as from a mouse.
    static async Task Pointer(App app, float x1, float y1, float x2, float y2, int steps)
    {
        var vp = app.GetViewport();
        var from = new Vector2(x1, y1);
        vp.PushInput(new InputEventMouseMotion { Position = from, GlobalPosition = from });
        await app.ToSignal(app.GetTree().CreateTimer(0.08), SceneTreeTimer.SignalName.Timeout);
        vp.PushInput(new InputEventMouseButton { Position = from, GlobalPosition = from, ButtonIndex = MouseButton.Left, Pressed = true, ButtonMask = MouseButtonMask.Left });
        await app.ToSignal(app.GetTree().CreateTimer(0.1), SceneTreeTimer.SignalName.Timeout);
        Vector2 last = from;
        for (int i = 1; i <= steps; i++)
        {
            var to = from.Lerp(new Vector2(x2, y2), i / (float)steps);
            vp.PushInput(new InputEventMouseMotion { Position = to, GlobalPosition = to, Relative = to - last, ButtonMask = MouseButtonMask.Left });
            last = to;
            await app.ToSignal(app.GetTree().CreateTimer(0.025), SceneTreeTimer.SignalName.Timeout);
        }
        await app.ToSignal(app.GetTree().CreateTimer(0.15), SceneTreeTimer.SignalName.Timeout);
        var end = new Vector2(x2, y2);
        vp.PushInput(new InputEventMouseButton { Position = end, GlobalPosition = end, ButtonIndex = MouseButton.Left, Pressed = false });
        await app.ToSignal(app.GetTree().CreateTimer(0.3), SceneTreeTimer.SignalName.Timeout);
    }

    static void ScanEnglish(Node node, string where)
    {
        if (node is CanvasItem ci && !ci.IsVisibleInTree()) return;
        string text = node switch { RichTextLabel r => r.GetParsedText(), Label l => l.Text, Button b => b.Text, LineEdit e => e.Text, _ => "" };
        if (System.Text.RegularExpressions.Regex.IsMatch(text, "[A-Za-z]{3,}")) GD.Print("QA_ENGLISH " + where + " " + node.GetType().Name + " :: " + text.Replace("\n", " ⏎ "));
        foreach (var child in node.GetChildren()) ScanEnglish(child, where);
    }

    public static async Task Run(App app, string step)
    {
        var parts = step.Split(':', 2); string name = parts[0], arg = parts.Length > 1 ? parts[1] : "";
        GD.Print("QA_STEP " + step); MemProbe.Step = step;
        try
        {
            switch (name)
            {
                case "wait": await app.ToSignal(app.GetTree().CreateTimer(double.Parse(arg)), SceneTreeTimer.SignalName.Timeout); return;
                case "capture": app.Capture(Path.GetFullPath(Path.Combine(app.Root, arg))); return;
                case "quit": app.GetTree().Quit(); return;
                // Real pointer input through the viewport (the same path a mouse takes): click x,y or drag x1,y1,x2,y2.
                // Key press through the viewport, e.g. key:U (the same path a keyboard takes).
                case "key": { var k = System.Enum.Parse<Key>(arg, true); app.GetViewport().PushInput(new InputEventKey { Keycode = k, PhysicalKeycode = k, Pressed = true }); await app.ToSignal(app.GetTree().CreateTimer(0.3), SceneTreeTimer.SignalName.Timeout); return; }
                // Mouse move only (to hover): move:x,y
                case "move": { var p = Nums(arg); var pt = new Vector2(p[0], p[1]); app.GetViewport().PushInput(new InputEventMouseMotion { Position = pt, GlobalPosition = pt }); await app.ToSignal(app.GetTree().CreateTimer(0.4), SceneTreeTimer.SignalName.Timeout); return; }
                case "click": { var p = Nums(arg); await Pointer(app, p[0], p[1], p[0], p[1], 1); return; }
                case "rclick":   // a right click at x,y (the card reader on cards)
                {
                    var p = Nums(arg); var at = new Vector2(p[0], p[1]); var vp = app.GetViewport();
                    vp.PushInput(new InputEventMouseMotion { Position = at, GlobalPosition = at });
                    await app.ToSignal(app.GetTree().CreateTimer(0.08), SceneTreeTimer.SignalName.Timeout);
                    vp.PushInput(new InputEventMouseButton { Position = at, GlobalPosition = at, ButtonIndex = MouseButton.Right, Pressed = true, ButtonMask = MouseButtonMask.Right });
                    await app.ToSignal(app.GetTree().CreateTimer(0.08), SceneTreeTimer.SignalName.Timeout);
                    vp.PushInput(new InputEventMouseButton { Position = at, GlobalPosition = at, ButtonIndex = MouseButton.Right, Pressed = false });
                    return;
                }
                // which control the pointer is over at x,y (and its ancestors): finds an invisible layer swallowing clicks
                // press the visible button whose text is exactly arg (the last one drawn, i.e. the top-most modal's)
                case "press":
                {
                    var hit = app.FindChildren("*", "BaseButton", true, false).OfType<Button>().LastOrDefault(b => b.IsVisibleInTree() && !b.Disabled && (b.Text == arg || (arg.StartsWith("~") && b.Text.Contains(arg[1..]))));   // ~text: a button whose text contains it
                    if (hit is null) GD.Print("QA_PRESS_MISS " + arg); else { hit.EmitSignal(BaseButton.SignalName.Pressed); GD.Print("QA_PRESS " + arg); }
                    return;
                }
                case "hit":
                {
                    var p = Nums(arg); var pt = new Vector2(p[0], p[1]);
                    app.GetViewport().PushInput(new InputEventMouseMotion { Position = pt, GlobalPosition = pt });
                    await app.ToSignal(app.GetTree().CreateTimer(0.2), SceneTreeTimer.SignalName.Timeout);
                    Node? n = app.GetViewport().GuiGetHoveredControl(); var chain = new List<string>();
                    while (n is not null && chain.Count < 12) { chain.Add(n.GetType().Name + (n is Control c ? $"[{c.Name} mf={c.MouseFilter} z={c.ZIndex} {c.GetGlobalRect()}]" : "")); n = n.GetParent(); }
                    GD.Print("QA_HIT " + arg + " :: " + string.Join(" < ", chain));
                    if (app.GetViewport().GuiGetHoveredControl() is Control hc && hc.GetParent() is Node par)
                        foreach (var ch in par.GetChildren()) if (ch is Control cc)
                            GD.Print($"QA_HIT_SIB {cc.GetIndex()} {cc.GetType().Name} {cc.Name} mf={cc.MouseFilter} vis={cc.Visible} z={cc.ZIndex} kids={cc.GetChildCount()} first={(cc.GetChildCount() > 0 ? cc.GetChild(0).GetType().Name : "-")} rect={cc.GetGlobalRect()}");
                    return;
                }

                case "drag": { var p = Nums(arg); await Pointer(app, p[0], p[1], p[2], p[3], 16); return; }
                // every visible text on screen that still has an English word in it (all player text must be Chinese)
                case "scan-english": { ScanEnglish(app, arg); return; }
            }
            if (Actions.TryGetValue(name, out var a)) await a(arg);
            else GD.PrintErr("QA_UNKNOWN " + name);
        }
        catch (Exception e) { GD.PrintErr("QA_FAIL " + step + " " + e.Message); }
    }
}
