using Godot;
using Gathering;

namespace ArkhamUI;

// The Dunwich Legacy's investigator picker: the five investigators side by side as tall portraits (each the art of the
// investigator's own card, html version scans), melting into the dark at their edges — no frames. Hover lifts a
// person into full colour; a click pushes them forward and opens the same detail panel and deck notebook as the core
// set's painted scene (SelectionStage.BuildInfo), on the side away from them.
public partial class LineupStage : Control
{
    readonly string[] people;
    readonly HashSet<string> taken;
    readonly Action<string, string> chosen;
    readonly List<Button> deckBars = new();
    readonly Dictionary<string, (Control Panel, TextureRect Art, Label Name, ColorRect Line)> cards = new();
    Control info = null!;
    Control? side;
    string? hover, picked;
    string deckKey = "";
    bool closing;
    static bool Calm => SelectionStage.Calm;

    // what the line-up is (the box's name: "第三循环 · 失落的时代"), and where "其他调查员" leads (later cycles, 11-22)
    public string Label { get; init; } = "敦威治遗产";
    public Action? Others { get; init; }
    public LineupStage(IEnumerable<string> people, IEnumerable<string> taken, Action<string, string> chosen)
    { this.people = people.ToArray(); this.taken = taken.ToHashSet(); this.chosen = chosen; }

    // grey and dim when not in focus; the edges fade to transparent (Yunke: fade to clear, not to a dark block)
    static Shader? shader;
    static Shader Grade => shader ??= new Shader { Code = @"shader_type canvas_item;
uniform float lit = 0.0; uniform float dim = 0.0;
void fragment(){
  vec4 t = texture(TEXTURE, UV);
  float l = dot(t.rgb, vec3(0.2126, 0.7152, 0.0722));
  vec3 c = mix(vec3(l) * vec3(0.78, 0.86, 0.92), t.rgb, 0.35 + 0.65 * lit);
  c *= mix(0.5, 1.04, lit) * (1.0 - dim * 0.62);
  float a = smoothstep(0.0, 0.16, UV.x) * smoothstep(0.0, 0.16, 1.0 - UV.x) * smoothstep(0.0, 0.08, UV.y) * smoothstep(0.0, 0.3, 1.0 - UV.y);
  COLOR = vec4(c, t.a * a);
}" };

    public override void _Ready()
    {
        MouseFilter = MouseFilterEnum.Stop;
        var bg = new ColorRect { Color = Style.Ink, MouseFilter = MouseFilterEnum.Ignore }; AddChild(bg); bg.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        var row = new HBoxContainer { Alignment = BoxContainer.AlignmentMode.Center, MouseFilter = MouseFilterEnum.Ignore };
        row.AddThemeConstantOverride("separation", 0);
        AddChild(row); row.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect); row.OffsetTop = 150; row.OffsetBottom = -40; row.OffsetLeft = 40; row.OffsetRight = -40;
        foreach (var code in people) row.AddChild(Person(code));
        var head = new Control { MouseFilter = MouseFilterEnum.Ignore }; AddChild(head); head.AnchorRight = 1; head.OffsetBottom = 200;
        Ui.EdgeFade(head, new Color(0.02f, 0.02f, 0.02f, 0.8f), 0, 200, 0, 0);
        var title = Style.Title("选择调查员", 44); AddChild(title); title.Position = new Vector2(104, 44);
        var hint = Style.Text(Label + " · 点击人物查看；已入队的调查员会变暗。右键或 Esc 返回。", 21, Style.PaperDim); AddChild(hint); hint.Position = new Vector2(106, 108);
        // the other boxes' investigators (any investigator may play any campaign): the painted group scenes
        if (Others is not null)
        {
            var other = Ui.MinimalButton("其他调查员 ›", () => { var o = Others; QueueFree(); o(); }, 22, true);
            AddChild(other); other.AnchorLeft = other.AnchorRight = 1; other.OffsetLeft = -300; other.OffsetRight = -60; other.OffsetTop = 48; other.OffsetBottom = 100;
        }
        info = new Control { MouseFilter = MouseFilterEnum.Ignore, ZIndex = 5 }; AddChild(info); info.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        Ui.BackButton(this, "返回", Back);
        Modulate = new Color(1, 1, 1, Calm ? 1 : 0);
        if (!Calm) CreateTween().TweenProperty(this, "modulate:a", 1f, 0.35f);
        QaScript.Register("pick", code => Pick(code));
        QaScript.Register("pick-confirm", _ => { if (picked is not null) Confirm(); });
        QaScript.Register("pick-deck", a => { if (int.TryParse(a, out int i) && i >= 0 && i < deckBars.Count) deckBars[i].EmitSignal(BaseButton.SignalName.Pressed); });
        QaScript.Register("sel-unpick", _ => Back());
        QaScript.Register("sel-hover", code => hover = code);
    }

    Control Person(string code)
    {
        var S = App.I.S!;
        var panel = new Control { SizeFlagsHorizontal = SizeFlags.ExpandFill, SizeFlagsVertical = SizeFlags.ExpandFill, MouseFilter = MouseFilterEnum.Stop, PivotOffset = Vector2.Zero,
            MouseDefaultCursorShape = taken.Contains(code) ? CursorShape.Arrow : CursorShape.PointingHand };
        var art = new TextureRect { Texture = Ui.Art(code) ?? Ui.Card(code), ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize, StretchMode = TextureRect.StretchModeEnum.KeepAspectCovered,
            MouseFilter = MouseFilterEnum.Ignore, TextureFilter = TextureFilterEnum.LinearWithMipmapsAnisotropic, Material = new ShaderMaterial { Shader = Grade } };
        panel.AddChild(art); art.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect); art.OffsetBottom = -110;
        // name, title and class line under the portrait
        var col = new VBoxContainer { MouseFilter = MouseFilterEnum.Ignore, Alignment = BoxContainer.AlignmentMode.Center }; col.AddThemeConstantOverride("separation", 4);
        panel.AddChild(col); col.AnchorTop = col.AnchorBottom = 1; col.AnchorRight = 1; col.OffsetTop = -112; col.OffsetBottom = -8;
        var name = Style.Text(S.Name(code) + (taken.Contains(code) ? "（已入队）" : ""), 28, Style.PaperDim, Style.Kai); name.HorizontalAlignment = HorizontalAlignment.Center;
        name.AddThemeColorOverride("font_shadow_color", new Color(0, 0, 0, 0.9f)); name.AddThemeConstantOverride("shadow_offset_y", 2); col.AddChild(name);
        var sub = Style.Text(J.String(S.Card(code), "subname"), 19, new Color(Style.PaperDim, 0.8f), Style.Kai); sub.HorizontalAlignment = HorizontalAlignment.Center; col.AddChild(sub);
        var line = new ColorRect { Color = new Color(SelectionStage.FactionColor(code), 0.5f), CustomMinimumSize = new Vector2(120, 2), SizeFlagsHorizontal = SizeFlags.ShrinkCenter, MouseFilter = MouseFilterEnum.Ignore };
        col.AddChild(line);
        panel.MouseEntered += () => hover = code;
        panel.MouseExited += () => { if (hover == code) hover = null; };
        panel.GuiInput += e =>
        {
            if (e is InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Left }) Pick(code);
            else if (e is InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Right }) Back();
        };
        cards[code] = (panel, art, name, line);
        return panel;
    }

    public override void _Process(double delta)
    {
        float a = Calm ? 1f : 1f - Mathf.Exp(-(float)delta * 9f);
        foreach (var (code, (panel, art, name, line)) in cards)
        {
            bool isTaken = taken.Contains(code), isPicked = code == picked, isHover = code == hover && !isTaken;
            var sm = (ShaderMaterial)art.Material;
            float lit = isPicked ? 1f : picked is null && isHover ? 1f : 0f;
            float dim = isTaken ? 0.7f : picked is not null && !isPicked ? 0.75f : 0f;
            sm.SetShaderParameter("lit", Mathf.Lerp((float)sm.GetShaderParameter("lit"), lit, a));
            sm.SetShaderParameter("dim", Mathf.Lerp((float)sm.GetShaderParameter("dim"), dim, a));
            // the person in focus steps forward a little
            float scale = isPicked ? 1.06f : isHover && picked is null ? 1.03f : 1f;
            panel.PivotOffset = new Vector2(panel.Size.X / 2, panel.Size.Y);
            panel.Scale = panel.Scale.Lerp(new Vector2(scale, scale), a);
            panel.ZIndex = isPicked || isHover ? 1 : 0;
            Color target = isTaken ? new Color(Style.PaperDim, 0.45f) : isPicked ? Style.Amber : isHover ? Style.HoverText : Style.PaperDim;
            name.AddThemeColorOverride("font_color", name.GetThemeColor("font_color").Lerp(target, a));
            line.Color = line.Color.Lerp(isPicked || isHover ? SelectionStage.FactionColor(code) : new Color(SelectionStage.FactionColor(code), 0.45f), a);
        }
    }

    // Esc: the back button's own (Ui.BackButton)
    // right click: back; a left click on no one while someone is picked: back to the whole line-up (Yunke 11-20)
    public override void _GuiInput(InputEvent e)
    {
        if (e is InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Right }) Back();
        else if (e is InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Left } && picked is not null) Back();
    }

    void Pick(string code)
    {
        if (closing || !cards.ContainsKey(code)) return;
        if (taken.Contains(code)) { App.I.Toast(App.I.S!.Name(code) + " 已经在队伍中", true); return; }
        if (code == picked) return;
        picked = code;
        deckKey = App.I.S!.Decks.For(code).Select(d => d.Key).FirstOrDefault() ?? "";
        // the panel opens on the side away from the person
        bool personOnRight = Array.IndexOf(people, code) >= people.Length / 2;
        HideSide();
        side = SelectionStage.BuildInfo(info, code, personOnRight, Confirm, new SelectionStage.DeckPick(deckKey, k => deckKey = k, deckBars), notebookWithText: true);
    }

    void HideSide()
    {
        if (side is null) return;
        var old = side; side = null; deckBars.Clear();
        if (Calm) { old.QueueFree(); return; }
        var tw = old.CreateTween(); tw.TweenProperty(old, "modulate:a", 0f, 0.15f); tw.TweenCallback(Callable.From(old.QueueFree));
    }

    void Back()
    {
        if (closing) return;
        if (picked is not null) { picked = null; HideSide(); return; }
        closing = true;
        if (Calm) { QueueFree(); return; }
        var tw = CreateTween(); tw.TweenProperty(this, "modulate:a", 0f, 0.2f); tw.TweenCallback(Callable.From(QueueFree));
    }

    void Confirm()
    {
        if (picked is null || closing) return;
        var c = picked; var deck = deckKey; closing = true;
        App.I.Dip(); QueueFree(); chosen(c, deck);   // a dip to black back to the screen that asked (Yunke 11-20 #1)
    }
}
