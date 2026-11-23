using Godot;
using Gathering;
using Path = System.IO.Path;

namespace ArkhamUI;

// Ensemble investigator picker: the painted S01 group scene with a per-person colour mask
// (selection-mask-refined.png, aligned to the painting by tools/refine_selection_mask.py).
// A virtual camera pushes in on the picked investigator and glides between people; everyone else drops to a
// silhouette (reference version's selection_actor grade). Names sit on a dark fade at the bottom; the detail
// panel is plain text on a dark side fade — no ornate frames.
public partial class SelectionStage : Control
{
    // A group picture and its colour mask: who stands where. The core set's painted scene, and The Dunwich Legacy's group
    // picture (tools/make_dunwich_lineup.py) — both pickers work the same way (Yunke 11-18 #2).
    public sealed record Scene(string Box, string Label, string Picture, string Mask, (string Code, Color Mask)[] People);
    public static readonly Scene Core = new("01", "基础游戏", "selection", "selection-core-mask.png",   // Yunke's study painting (11-18); the old ensemble is kept as selection-ensemble-old.png
        new[] { ("01001", new Color(1, 0, 0)), ("01002", new Color(0, 1, 0)), ("01003", new Color(0, 0, 1)), ("01004", new Color(1, 1, 0)), ("01005", new Color(1, 0, 1)) });
    public static readonly Scene Dunwich = new("02", "第一循环 · 敦威治遗产", "selection-dunwich", "selection-dunwich-mask.png",
        new[] { ("02001", new Color(1, 0, 0)), ("02002", new Color(0, 1, 0)), ("02003", new Color(0, 0, 1)), ("02004", new Color(1, 1, 0)), ("02005", new Color(1, 0, 1)) });
    // The Path to Carcosa: six investigators in a theatre dressing room (img2 ensemble C02-A, picked by Yunke 11-20; outlines
    // drawn by hand in tools/make_lineup_mask.py "carcosa")
    public static readonly Scene Carcosa = new("03", "第二循环 · 卡尔克萨之路", "selection-carcosa", "selection-carcosa-mask.png",
        new[] { ("03001", new Color(1, 0, 0)), ("03002", new Color(0, 1, 0)), ("03003", new Color(0, 0, 1)), ("03004", new Color(1, 1, 0)), ("03005", new Color(1, 0, 1)), ("03006", new Color(0, 1, 1)) });
    // Every later box's group picture (Yunke 11-22: "所有调查员的选择界面都做进去"): the img2 ensembles of 11-20, the pick per box
    // in inbox/ensembles-20261120/REVIEW.md; outlines drawn by hand in tools/lineups/<id>.json (tools/make_lineup_mask.py).
    // The mask colours follow each file's "people" order: red, green, blue, yellow, magenta, cyan. Names: the official ones
    // (build-data metadata: cycle / pack names).
    static readonly Color[] Palette = { new(1, 0, 0), new(0, 1, 0), new(0, 0, 1), new(1, 1, 0), new(1, 0, 1), new(0, 1, 1) };
    static Scene Group(string box, string label, string id, params string[] codes) =>
        new(box, label, "selection-" + id, "selection-" + id + "-mask.png", codes.Select((c, i) => (c, Palette[i])).ToArray());
    public static readonly Scene[] Later =
    {
        Group("04", "失落的时代", "c03-a", "04001", "04002", "04003", "04004", "04005"),
        Group("05", "万象无终", "c04-a", "05001", "05002", "05003", "05004", "05005", "05006"),
        Group("06", "食梦者", "c05-b", "06001", "06002", "06003", "06004", "06005"),
        Group("07", "印斯茅斯暗潮", "c06-a", "07001", "07002", "07003", "07004", "07005"),
        Group("08", "地球边缘", "c07-c", "08001", "08004", "08007", "08010", "08016"),
        Group("09", "猩红钥匙", "c08-a", "09001", "09004", "09008", "09011", "09015", "09018"),
        Group("10", "毒堇谷盛宴", "c09-c", "10001", "10004", "10009", "10012", "10015"),
        Group("11", "淹灭之城", "c10-b", "11001", "11004", "11007", "11011", "11014", "11017"),   // Yunke's new picture 11-23 (outlines: tools/lineups/c10-new.json)
        Group("60", "调查员起始牌组", "st20-a", "60101", "60201", "60301", "60401", "60501"),
        Group("12", "第二季基础游戏", "core26-a", "12001", "12004", "12007", "12010", "12013"),
        Group("61", "第二季调查员牌组", "deck26-a", "60151", "60251", "60351", "60451", "60551"),
        Group("89", "重返吞噬万物", "suzi", "89001"),
    };
    // every group picture the picker can switch between (investigators of any box can play any campaign); a picture whose
    // mask is not there yet is left out
    public static readonly Scene[] All = new[] { Core, Dunwich, Carcosa }.Concat(Later.Where(s => System.IO.File.Exists(Path.Combine(App.I.Assets, "bg", s.Mask)) && ResourceLoader.Exists("res://assets/bg/" + s.Picture + ".png"))).ToArray();
    readonly Scene scene;
    (string Code, Color Mask)[] People => scene.People;
    // Printed core-set stats: willpower, intellect, combat, agility, health, sanity.
    public static readonly Dictionary<string, int[]> Stats = new()
    {
        ["01001"] = new[] { 3, 3, 4, 2, 9, 5 }, ["01002"] = new[] { 3, 5, 2, 2, 5, 9 }, ["01003"] = new[] { 2, 3, 3, 4, 8, 6 },
        ["01004"] = new[] { 5, 2, 2, 3, 6, 8 }, ["01005"] = new[] { 4, 3, 1, 4, 7, 7 },
        // The Dunwich Legacy (rules source Investigator/Cards/<Name>.hs Stats)
        ["02001"] = new[] { 4, 2, 4, 2, 9, 6 }, ["02002"] = new[] { 3, 4, 2, 3, 6, 9 }, ["02003"] = new[] { 3, 3, 3, 3, 8, 7 },
        ["02004"] = new[] { 4, 3, 3, 2, 7, 8 }, ["02005"] = new[] { 4, 2, 2, 3, 6, 5 },
        // The Path to Carcosa (rules source Investigator/Cards/<Name>.hs Stats; the html card data carries no stats)
        ["03001"] = new[] { 3, 2, 5, 3, 9, 5 }, ["03002"] = new[] { 4, 4, 2, 2, 7, 7 }, ["03003"] = new[] { 4, 2, 2, 4, 5, 9 },
        ["03004"] = new[] { 5, 2, 3, 3, 6, 8 }, ["03005"] = new[] { 3, 2, 4, 3, 8, 6 }, ["03006"] = new[] { 3, 3, 3, 3, 6, 6 },
    };
    // the printed stats: the table above for the first two boxes, the card data for the others
    public static int[] StatsOf(string code)
    {
        if (Stats.TryGetValue(code, out var st)) return st;
        var c = App.I.S!.Card(code);
        return new[] { J.Int(c, "skill_willpower"), J.Int(c, "skill_intellect"), J.Int(c, "skill_combat"), J.Int(c, "skill_agility"), J.Int(c, "health"), J.Int(c, "sanity") };
    }
    static readonly Dictionary<string, (Godot.Image Mask, Dictionary<string, Rect2> Boxes)> masks = new();
    Godot.Image? mask;
    Dictionary<string, Rect2> boxes = new();   // per-person region in painting pixels

    readonly HashSet<string> taken;
    readonly Action<string, string> chosen;   // investigator code, chosen deck key
    string deckKey = "";                       // the deck selected in the notebook for the picked investigator
    readonly List<Button> deckBars = new();
    TextureRect sceneRect = null!;
    ColorRect film = null!;
    Control info = null!;
    Control? side;
    readonly Dictionary<string, (Label Name, ColorRect Line)> tabs = new();
    string? hover, picked;
    bool closing, panelLeft;

    // camera: zoom over the base fit, a focus point in painting pixels, and where on screen (0..1) that point sits
    float camZ = 1f; Vector2 camF, camP = new(0.5f, 0.46f);
    float focusAmt, hoverAmt, closeAmt, seconds;
    readonly float[] pickW = new float[8], hovW = new float[8];   // light weight per person (picked / hover), eased
    string? lastPicked;   // keeps the light on the last person while it fades out after Back()

    // a person's light: centred on the upper body, about 1.2 times their reach (painting pixels); scale < 1 for the hover
    // Hand-placed camera centres for the core painting (1672 px wide), Yunke 11-18: the mask box takes in Roland's arm on the wall,
    // so a box-centred push left Roland off-centre. Centre on body + head (radii unused since the silhouette light).
    // (x, y, rx, ry)
    static readonly Dictionary<string, (float X, float Y, float RX, float RY)> SpotOverrides = new()
    {
        ["01004"] = (215, 346, 170, 280),    // Agnes
        ["01002"] = (466, 230, 135, 210),    // Daisy
        ["01001"] = (680, 385, 185, 335),    // Roland: right edge stops before Wendy's face
        ["01005"] = (945, 500, 190, 265),    // Wendy
        ["01003"] = (1405, 478, 240, 405),   // Pete
    };

    (Vector2 C, Vector2 R) SpotFor(string code, float scale)
    {
        if (scene.Box == "01" && SpotOverrides.TryGetValue(code, out var o))
        {
            float k = MW / 1672f;
            return (new Vector2(o.X, o.Y) * k, new Vector2(o.RX, o.RY) * k * scale);
        }
        if (!boxes.TryGetValue(code, out var b)) return (new Vector2(MW / 2, MH / 2), new Vector2(MW * 0.15f, MH * 0.3f));
        var c = new Vector2(b.GetCenter().X, b.Position.Y + b.Size.Y * 0.42f);
        var r = new Vector2(Math.Max(b.Size.X, b.Size.Y * 0.55f) * 0.62f, b.Size.Y * 0.6f) * scale;
        return (c, r);
    }

    internal static bool Calm => App.I.S?.Settings.ReducedMotion == true;
    float MW => mask?.GetWidth() ?? 1672;
    float MH => mask?.GetHeight() ?? 941;
    string MaskPath => File(Path.Combine(App.I.Assets, "bg", scene.Mask)) ?? Path.Combine(App.I.Assets, "bg", "selection-mask.png");
    static string? File(string p) => System.IO.File.Exists(p) ? p : null;

    public SelectionStage(IEnumerable<string> taken, Action<string, string> chosen, Scene? scene = null) { this.taken = taken.ToHashSet(); this.chosen = chosen; this.scene = scene ?? Core; }
    // the wording, when the picker is used for something other than forming the team (replacing an investigator mid-campaign)
    public string Heading { get; init; } = "选择调查员";
    public string Hint { get; init; } = "点击人物或下方的名字查看；已入队的调查员会变暗。右键或 Esc 返回。";
    public string TakenWord { get; init; } = "已入队";
    public string TakenToast { get; init; } = "已经在队伍中";
    // the other box's group (both boxes' investigators can play either campaign): the prepare screen swaps the picker
    public Action<Scene>? SwitchTo;

    // one compiled copy of each shader for every picker (a new Shader is compiled again on its first draw: a 0.2-0.5 s stall)
    static Shader? fillShader, sceneShader;

    public override void _Ready()
    {
        MouseFilter = MouseFilterEnum.Stop;
        var qaw = System.Diagnostics.Stopwatch.StartNew();
        EnsureMask();
        if (App.I.QaRunning) GD.Print("SEL_T mask " + qaw.ElapsedMilliseconds);
        var bg = new ColorRect { Color = Style.Ink, MouseFilter = MouseFilterEnum.Ignore }; AddChild(bg); bg.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        // Side fill for screens wider than the painting: the same scene, blurred, desaturated and dark.
        var fill = Ui.Image(Ui.Bg(scene.Picture), Vector2.Zero, true); AddChild(fill); fill.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        fill.Material = new ShaderMaterial { Shader = fillShader ??= new Shader { Code = @"shader_type canvas_item;
uniform float r = 0.006; uniform vec3 tint = vec3(0.26, 0.27, 0.29);
void fragment(){ vec4 c = vec4(0.0); for (int x = -3; x <= 3; x++) for (int y = -3; y <= 3; y++) c += texture(TEXTURE, UV + vec2(float(x), float(y)) * r);
  c /= 49.0; float l = dot(c.rgb, vec3(0.2126, 0.7152, 0.0722)); COLOR = vec4(mix(vec3(l), c.rgb, 0.6) * tint, 1.0); }" } };
        // The painting. Grade after the reference selection_actor shader: cool shadows / warm highlights; when someone
        // is in focus everyone else drops to a silhouette and the room recedes. Edges feather into the fill.
        sceneRect = new TextureRect { Texture = Ui.Bg(scene.Picture), ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize, StretchMode = TextureRect.StretchModeEnum.Scale, MouseFilter = MouseFilterEnum.Ignore, TextureFilter = TextureFilterEnum.LinearWithMipmapsAnisotropic };
        // Soft silhouette light (Yunke 11-18, after trying a round spotlight: "做成那种人物剪影蒙版，边缘模糊的感觉"). Each person's
        // mask, blurred into a light map (LightMaps), lights the picked person: fully over the figure, fading out past its edge
        // like a glow — no outline, no sharp edge (the glow-mask principle). The room recedes; hover is a weak preview.
        var sceneMat = new ShaderMaterial { Shader = sceneShader ??= new Shader { Code = @"shader_type canvas_item;
uniform sampler2D light_a : filter_linear, repeat_disable; uniform sampler2D light_b : filter_linear, repeat_disable;
uniform sampler2D halo_a : filter_linear, repeat_disable; uniform sampler2D halo_b : filter_linear, repeat_disable;   // the same, blurred much wider
uniform vec4 wa = vec4(0.0); uniform vec4 wb = vec4(0.0);   // picked person's weight per channel (glides between people)
uniform vec4 ha = vec4(0.0); uniform vec4 hb = vec4(0.0);   // hover preview weights
uniform float spot = 0.0; uniform float hov = 0.0; uniform float halo = 0.09;   // halo reach, fraction of the width (Yunke 11-18: wider still)
uniform float feather = 0.0; uniform float feather_y = 0.0; uniform float grade = 1.0;
void fragment(){
  vec4 tex = texture(TEXTURE, UV);
  vec3 c = tex.rgb;
  float lum = dot(c, vec3(0.2126, 0.7152, 0.0722));
  vec3 film = mix(vec3(lum), c, 0.78) * mix(vec3(0.62, 0.77, 0.84), vec3(1.07, 0.98, 0.85), smoothstep(0.08, 0.48, lum));
  c = mix(c, film, grade);
  vec4 la = texture(light_a, UV); vec4 lb = texture(light_b, UV);
  // a wide halo around the figure: two rings of taps on the light maps (Yunke 11-18: the glow fell off too fast)
  vec4 ra = texture(halo_a, UV); vec4 rb = texture(halo_b, UV);
  // coverage 0.5 sits on the figure's (already dilated) edge: full light from there in, then a long soft fade outward
  float core = smoothstep(0.02, 0.5, clamp(dot(la, wa) + dot(lb, wb), 0.0, 1.0));
  float wide = smoothstep(0.0, 0.55, clamp(dot(ra, wa) + dot(rb, wb), 0.0, 1.0));
  float body = max(core, wide * 0.75);
  float pre = smoothstep(0.02, 0.5, clamp(dot(la, ha) + dot(lb, hb), 0.0, 1.0));
  float light = clamp(body * spot + pre * hov * 0.5, 0.0, 1.0);
  // the room recedes, but stays readable (Yunke 11-18: it went too dark)
  float roomLevel = mix(1.0, 0.5, spot) * mix(1.0, 0.85, hov * (1.0 - spot));
  vec3 room = mix(c, mix(vec3(lum), c, 0.7), spot * 0.4) * roomLevel;
  vec3 lit = c * mix(vec3(1.0), vec3(1.10, 1.02, 0.88), spot);
  COLOR.rgb = mix(room, lit, light);
  float a = feather > 0.0 ? smoothstep(0.0, feather, UV.x) * smoothstep(0.0, feather, 1.0 - UV.x) : 1.0;
  if (feather_y > 0.0) a *= smoothstep(0.0, feather_y, UV.y) * smoothstep(0.0, feather_y, 1.0 - UV.y);
  COLOR.a = tex.a * a;
}" } };
        if (App.I.QaRunning) GD.Print("SEL_T pre-light " + qaw.ElapsedMilliseconds);
        var lm = LightMaps();
        if (App.I.QaRunning) GD.Print("SEL_T light " + qaw.ElapsedMilliseconds);
        sceneMat.SetShaderParameter("light_a", lm.A); sceneMat.SetShaderParameter("light_b", lm.B);
        sceneMat.SetShaderParameter("halo_a", lm.HA); sceneMat.SetShaderParameter("halo_b", lm.HB);
        sceneRect.Material = sceneMat; AddChild(sceneRect);
        // Film layer (reference selection_film shader): vignette, floor shade, grain, and the panel side darkened.
        film = new ColorRect { MouseFilter = MouseFilterEnum.Ignore, Color = Colors.White };
        film.Material = new ShaderMaterial { Shader = GD.Load<Shader>("res://Style/shaders/selection_film.gdshader") };
        AddChild(film); film.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        // Header on a dark fade.
        var head = new Control { MouseFilter = MouseFilterEnum.Ignore }; AddChild(head); head.AnchorRight = 1; head.OffsetBottom = 200;
        Ui.EdgeFade(head, new Color(0.02f, 0.02f, 0.02f, 0.8f), 0, 200, 0, 0);
        var title = Style.Title(Heading, 44); AddChild(title); title.Position = new Vector2(104, 44);
        var hint = Style.Text(Hint, 21, Style.PaperDim); AddChild(hint); hint.Position = new Vector2(106, 108);
        hint.AddThemeColorOverride("font_shadow_color", new Color(0, 0, 0, 0.9f)); hint.AddThemeConstantOverride("shadow_offset_y", 2);
        info = new Control { MouseFilter = MouseFilterEnum.Ignore }; AddChild(info); info.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        BuildNames();
        Ui.BackButton(this, "返回", Back);   // with someone picked: back to the whole group first; with nobody picked: back to the prepare screen (Yunke 11-18 r6 #5)
        if (SwitchTo is not null)
        {
            // both boxes' investigators can play either campaign: switch between the two group pictures (Yunke 11-18 #2)
            // every box (Yunke 11-22): the names wrap onto a second row on the right of the title; the box's own name only
            // two even rows, right-aligned (Yunke 11-22: three ragged rows looked untidy)
            var tabs2 = new VBoxContainer(); tabs2.AddThemeConstantOverride("separation", 4); AddChild(tabs2);
            tabs2.AnchorLeft = tabs2.AnchorRight = 1; tabs2.OffsetLeft = -1290; tabs2.OffsetRight = -50; tabs2.OffsetTop = 30; tabs2.OffsetBottom = 150;   // clear of the title and its hint line
            int perRow = (All.Length + 1) / 2;
            HBoxContainer? tabRow = null;
            for (int ti = 0; ti < All.Length; ti++)
            {
                var sc = All[ti];
                if (ti % perRow == 0) { tabRow = new HBoxContainer { Alignment = BoxContainer.AlignmentMode.End }; tabRow.AddThemeConstantOverride("separation", 6); tabs2.AddChild(tabRow); }
                var target = sc; string name = sc.Label.Contains(" · ") ? sc.Label[(sc.Label.IndexOf(" · ") + 3)..] : sc.Label;
                // 新手教程: only the base game's group (Yunke 11-23: switching boxes left the tip stuck)
                var b = Ui.MinimalButton(name, () => { if (target != scene) { if (Guide is not null) { App.I.Toast("新手教程中请在基础游戏的群像里选择"); return; } SwitchWithLight(target); } }, 17, sc == scene); b.CustomMinimumSize = new Vector2(0, 38);
                if (Guide is not null && sc != scene) b.Modulate = new Color(1, 1, 1, 0.35f);
                tabRow!.AddChild(b);
            }
        }
        camF = new Vector2(MW / 2, MH / 2);
        Resized += ApplyCamera;
        // Arrival: fade in while the camera settles back from a slight push-in.
        Modulate = new Color(1, 1, 1, Calm ? 1 : 0);
        if (!Calm) { CreateTween().TweenProperty(this, "modulate:a", 1f, 0.35f); camZ = 1.07f; MoveCamera(1f, camF, camP, 1.4f, Tween.EaseType.Out); }
        CallDeferred(nameof(ApplyCamera));
        // the other box: its picture loaded and its light maps worked out now, so the switch does not stall (Yunke 11-20 #1)
        if (SwitchTo is not null)
            foreach (var other in All.Where(o => o != scene && Math.Abs(Array.IndexOf(All, o) - Array.IndexOf(All, scene)) <= 1))   // the neighbours only: fifteen boxes now
            {
                Prewarm(other);
                var res = "res://assets/bg/" + other.Picture + ".png";   // loaded on a loader thread; Ui.Bg then finds it cached
                if (ResourceLoader.Exists(res)) ResourceLoader.LoadThreadedRequest(res, "Texture2D");
            }
        if (App.I.QaRunning) GD.Print("SEL_T ready " + qaw.ElapsedMilliseconds);
        QaScript.Register("pick", code => { Pick(code); });
        QaScript.Register("pick-confirm", _ => { if (picked is not null) Confirm(); });
        QaScript.Register("pick-deck", a => { if (int.TryParse(a, out int i) && i >= 0 && i < deckBars.Count) deckBars[i].EmitSignal(BaseButton.SignalName.Pressed); });
        QaScript.Register("sel-unpick", _ => Back());
        QaScript.Register("sel-switch", a => SwitchWithLight(All.FirstOrDefault(x => x.Box == a) ?? All[(Array.IndexOf(All, scene) + 1) % All.Length]));   // sel-switch[:<box>]
        // QA: feed mouse events at a person's upper body into the same handler the mouse uses.
        QaScript.Register("sel-hover", code => SimMouse(code, false));
        QaScript.Register("sel-click", code => SimMouse(code, true));
    }

    void SimMouse(string code, bool click)
    {
        if (!boxes.TryGetValue(code, out var b)) { GD.PrintErr("SEL_NO_BOX " + code); return; }
        var (k, ox, oy) = Map();
        var local = new Vector2(ox + (b.Position.X + b.Size.X / 2) * k, oy + (b.Position.Y + b.Size.Y * 0.35f) * k);
        _GuiInput(new InputEventMouseMotion { Position = local, GlobalPosition = local });
        if (click) _GuiInput(new InputEventMouseButton { Position = local, GlobalPosition = local, ButtonIndex = MouseButton.Left, Pressed = true });
        GD.Print($"SEL_SIM {code} click={click} local={local} hit={Hit(local)} picked={picked}");
    }

    // Per-person light maps for the silhouette light: the mask at 1/4 size, one channel per person (people 1-4 in A, 5-8 in B),
    // each pixel the share of that person in its block, then blurred twice (≈ a gaussian) so the edge becomes a glow.
    static readonly Dictionary<string, (ImageTexture A, ImageTexture B, ImageTexture HA, ImageTexture HB)> lightMaps = new();
    // the light maps' pixels (computed off the main thread by Prewarm, so switching boxes does not stall on them — Yunke 11-20 #1)
    static readonly Dictionary<string, (int W, int H, byte[] A, byte[] B, byte[] HA, byte[] HB)> lightBytes = new();
    (ImageTexture A, ImageTexture B, ImageTexture HA, ImageTexture HB) LightMaps()
    {
        if (lightMaps.TryGetValue(scene.Picture, out var cached)) return cached;
        var lb = LightBytes();
        ImageTexture Tex(byte[] bytes) => ImageTexture.CreateFromImage(Godot.Image.CreateFromData(lb.W, lb.H, false, Godot.Image.Format.Rgba8, bytes));
        var made = (Tex(lb.A), Tex(lb.B), Tex(lb.HA), Tex(lb.HB));
        lightMaps[scene.Picture] = made;
        return made;
    }

    // The other box's mask and light maps, worked out in the background while the player looks at this one.
    public static void Prewarm(Scene sc)
    {
        lock (lightBytes) if (lightBytes.ContainsKey(sc.Picture)) return;
        System.Threading.Tasks.Task.Run(() =>
        {
            try { var st = new SelectionStage(Array.Empty<string>(), (_, _) => { }, sc); st.EnsureMask(); st.LightBytes(); st.CallDeferred(GodotObject.MethodName.Free); }
            catch (Exception e) { GD.PrintErr("SEL_PREWARM " + e.Message); }
        });
    }

    (int W, int H, byte[] A, byte[] B, byte[] HA, byte[] HB) LightBytes()
    {
        lock (lightBytes) if (lightBytes.TryGetValue(scene.Picture, out var done)) return done;
        const int Q = 4;
        int W = (int)MW, H = (int)MH, w = Math.Max(1, W / Q), h = Math.Max(1, H / Q), n = Math.Min(8, People.Length);
        var cov = new float[n][];
        for (int i = 0; i < n; i++) cov[i] = new float[w * h];
        if (mask is not null)
        {
            var m = mask.GetData();
            var index = People.Select((p, i) => (p.Code, i)).ToDictionary(t => t.Code, t => t.i);
            for (int y = 0; y < h * Q; y++) for (int x = 0; x < w * Q; x++)
            {
                int j = (y * W + x) * 3; var who = Owner(m[j], m[j + 1], m[j + 2]);
                if (who is not null && index.TryGetValue(who, out int pi) && pi < n) cov[pi][(y / Q) * w + x / Q] += 1f / (Q * Q);
            }
        }
        // the wide halo: the raw coverage blurred about three times wider (≈ 9% of the painting's width), three passes
        var wide = new float[n][]; int rw = Math.Max(6, w / 22);
        for (int i = 0; i < n; i++) { wide[i] = cov[i]; for (int pass = 0; pass < 3; pass++) { wide[i] = BoxBlur(wide[i], w, h, rw, true); wide[i] = BoxBlur(wide[i], w, h, rw, false); } }
        int r = Math.Max(3, w / 45);   // ≈ 37 px of the painting per pass
        for (int i = 0; i < n; i++) for (int pass = 0; pass < 2; pass++) { cov[i] = BoxBlur(cov[i], w, h, r, true); cov[i] = BoxBlur(cov[i], w, h, r, false); }
        byte[] Pack(float[][] src, int first)
        {
            var bytes = new byte[w * h * 4];
            for (int c = 0; c < 4; c++) { int pi = first + c; if (pi >= n) continue; for (int k = 0; k < w * h; k++) bytes[k * 4 + c] = (byte)Math.Clamp((int)(src[pi][k] * 255f + 0.5f), 0, 255); }
            return bytes;
        }
        var made = (w, h, Pack(cov, 0), Pack(cov, 4), Pack(wide, 0), Pack(wide, 4));
        lock (lightBytes) lightBytes[scene.Picture] = made;
        return made;
    }

    static float[] BoxBlur(float[] src, int w, int h, int r, bool horizontal)
    {
        var dst = new float[src.Length]; float inv = 1f / (2 * r + 1);
        int lines = horizontal ? h : w, len = horizontal ? w : h;
        for (int l = 0; l < lines; l++)
        {
            int At(int t) => horizontal ? l * w + Math.Clamp(t, 0, len - 1) : Math.Clamp(t, 0, len - 1) * w + l;
            float sum = 0; for (int t = -r; t <= r; t++) sum += src[At(t)];
            for (int t = 0; t < len; t++) { dst[At(t)] = sum * inv; sum += src[At(t + r + 1)] - src[At(t - r)]; }
        }
        return dst;
    }

    void EnsureMask()
    {
        if (mask is not null) return;
        lock (masks) if (masks.TryGetValue(scene.Picture, out var cached)) { mask = cached.Mask; boxes = cached.Boxes; return; }
        var loaded = Godot.Image.LoadFromFile(MaskPath);
        if (loaded is null) return;
        loaded.Convert(Godot.Image.Format.Rgb8);
        mask = loaded;
        int w = mask.GetWidth(), h = mask.GetHeight(); var m = mask.GetData();
        foreach (var (code, c) in People)
        {
            int minX = w, minY = h, maxX = 0, maxY = 0;
            for (int y = 0; y < h; y += 2) for (int x = 0; x < w; x += 2)
            {
                int j = (y * w + x) * 3;
                if (Owner(m[j], m[j + 1], m[j + 2]) == code) { minX = Math.Min(minX, x); maxX = Math.Max(maxX, x); minY = Math.Min(minY, y); maxY = Math.Max(maxY, y); }
            }
            if (maxX > minX) boxes[code] = new Rect2(minX, minY, maxX - minX, maxY - minY);
        }
        lock (masks) masks[scene.Picture] = (mask, boxes);
    }

    string? Owner(int r, int g, int b)
    {
        bool R = r > 127, G = g > 127, B = b > 127;
        foreach (var (code, c) in People) if ((c.R > 0.5f) == R && (c.G > 0.5f) == G && (c.B > 0.5f) == B) return code;
        return null;
    }

    // ───────────── camera
    (float K, float Ox, float Oy) Map()
    {
        var s = Size; if (s.X <= 0 || s.Y <= 0) s = GetViewportRect().Size;
        // The whole painting, with room above it for the title (Yunke 11-18: too little space above the heads); the sides
        // left open are the blurred fill.
        const float TopRoom = 150f;   // Daisy's head nearly touched the top
        float baseK = Math.Min(s.X / MW, (s.Y - TopRoom) / MH);
        float k = baseK * camZ, W = MW * k, H = MH * k;
        float ox = camP.X * s.X - camF.X * k, oy = camP.Y * s.Y - camF.Y * k;
        // Pushed in, the camera may travel past the painting's edges (the blurred fill extends the picture),
        // so a person's upper body can be centred even when they stand near a top/side edge.
        float slack = Math.Clamp((camZ - 1f) / 0.5f, 0f, 1f), padX = s.X * 0.07f * slack, padY = s.Y * 0.2f * slack;   // less travel past the edges: the blurred fill showed too much (Yunke 11-18 r6 #6)
        ox = W >= s.X ? Math.Clamp(ox, s.X - W - padX, padX) : Math.Clamp(ox, -padX, s.X - W + padX);
        oy = H >= s.Y ? Math.Clamp(oy, s.Y - H - padY, padY) : Math.Clamp(oy, Math.Min(TopRoom, s.Y - H), s.Y - H) ;   // below the title
        return (k, ox, oy);
    }

    // The camera glides on its own clock (Yunke 11-18 r7: the push-in was not smooth): each frame advances it by the
    // frame time capped at 1/40 s along a smootherstep curve, so a slow frame (the info panel being built, a texture
    // loading) pauses it for a moment instead of making it jump.
    float camZ0, camZ1 = 1f, camT = 1f, camSeconds = 0.8f; Vector2 camF0, camF1, camP0, camP1; bool camEaseOut;
    // The glide runs on the view itself, not on the zoom / focus / anchor numbers: lerping those separately (and clamping
    // the result to the painting's edges, whose slack grows with the zoom) bent the path — the picture swung one way, then
    // turned (Yunke 11-19). Now the view's centre (in painting pixels) moves in a straight line and the zoom changes
    // evenly on a log scale, from the view as it is to the view the target gives.
    float camK0, camK1; Vector2 camC0, camC1;
    void MoveCamera(float z, Vector2 f, Vector2 p, float seconds = 0.8f, Tween.EaseType ease = Tween.EaseType.InOut)
    {
        if (Calm || seconds <= 0) { camZ = z; camF = f; camP = p; camT = 1f; ApplyCamera(); return; }
        var s = Size; if (s.X <= 0 || s.Y <= 0) s = GetViewportRect().Size;
        var (k0, ox0, oy0) = Map();
        camZ0 = camZ; camF0 = camF; camP0 = camP; camZ1 = z; camF1 = f; camP1 = p;
        camZ = z; camF = f; camP = p;
        var (k1, ox1, oy1) = Map();
        camZ = camZ0; camF = camF0; camP = camP0;
        camK0 = k0; camK1 = k1;
        camC0 = (s / 2 - new Vector2(ox0, oy0)) / k0; camC1 = (s / 2 - new Vector2(ox1, oy1)) / k1;
        camT = 0f; camSeconds = seconds; camEaseOut = ease == Tween.EaseType.Out;
    }
    void StepCamera(double delta)
    {
        if (camT >= 1f) return;
        camT = Math.Min(1f, camT + Math.Min((float)delta, 1f / 40f) / camSeconds);
        float t = camT, e = camEaseOut ? 1f - (1f - t) * (1f - t) * (1f - t) : t * t * t * (t * (t * 6f - 15f) + 10f);
        if (camT >= 1f) { camZ = camZ1; camF = camF1; camP = camP1; ApplyCamera(); return; }
        // the camera's own numbers follow along (other code reads them), the picture is placed from the straight path
        camZ = Mathf.Lerp(camZ0, camZ1, e); camF = camF0.Lerp(camF1, e); camP = camP0.Lerp(camP1, e);
        var s = Size; if (s.X <= 0 || s.Y <= 0) s = GetViewportRect().Size;
        float k = Mathf.Exp(Mathf.Lerp(Mathf.Log(camK0), Mathf.Log(camK1), e));
        var c = camC0.Lerp(camC1, e);
        PlaceScene(k, s / 2 - c * k);
    }

    void ApplyCamera()
    {
        if (sceneRect is null) return;
        var (k, ox, oy) = Map();
        PlaceScene(k, new Vector2(ox, oy));
    }

    void PlaceScene(float k, Vector2 o)
    {
        if (sceneRect is null) return;
        var s = Size; float ox = o.X, oy = o.Y;
        sceneRect.Position = new Vector2(ox, oy); sceneRect.Size = new Vector2(MW * k, MH * k);
        var sm = (ShaderMaterial)sceneRect.Material;
        bool gapX = ox > 0.5f || ox + sceneRect.Size.X < s.X - 0.5f, gapY = oy > 0.5f || oy + sceneRect.Size.Y < s.Y - 0.5f;
        // only the edges that actually meet the blurred fill fade, and only a little (Yunke 11-18 r6 #6: too much blur at the side)
        sm.SetShaderParameter("feather", gapX ? Math.Min(0.04f, 50f / sceneRect.Size.X) : 0f);
        sm.SetShaderParameter("feather_y", gapY ? Math.Min(0.05f, 50f / sceneRect.Size.Y) : 0f);
    }

    // ───────────── names on a dark fade at the bottom, ordered left to right as people stand in the painting
    void BuildNames()
    {
        var strip = new Control { MouseFilter = MouseFilterEnum.Ignore }; AddChild(strip);
        strip.AnchorTop = 1; strip.AnchorBottom = 1; strip.AnchorRight = 1; strip.OffsetTop = -170;
        Ui.EdgeFade(strip, new Color(0.01f, 0.01f, 0.01f, 0.92f), 0, 0, 0, 170);
        var row = new HBoxContainer { Alignment = BoxContainer.AlignmentMode.Center, MouseFilter = MouseFilterEnum.Ignore };
        row.AddThemeConstantOverride("separation", 56);
        AddChild(row); row.AnchorTop = row.AnchorBottom = 1; row.AnchorRight = 1; row.OffsetTop = -84; row.OffsetBottom = -18;
        foreach (var (code, _) in People.OrderBy(p => boxes.TryGetValue(p.Code, out var b) ? b.GetCenter().X : 0))
        {
            // the name only (Yunke: no pictures here), with the line that lights when the person is picked
            var card = new PanelContainer { MouseFilter = MouseFilterEnum.Stop, MouseDefaultCursorShape = taken.Contains(code) ? CursorShape.Arrow : CursorShape.PointingHand, CustomMinimumSize = new Vector2(132, 0) };
            card.AddThemeStyleboxOverride("panel", new StyleBoxEmpty { ContentMarginLeft = 8, ContentMarginRight = 8, ContentMarginTop = 6, ContentMarginBottom = 6 });
            var col = new VBoxContainer { MouseFilter = MouseFilterEnum.Ignore, Alignment = BoxContainer.AlignmentMode.Center };
            col.AddThemeConstantOverride("separation", 6); card.AddChild(col);
            var name = Style.Text(App.I.S!.Name(code) + (taken.Contains(code) ? "（" + TakenWord + "）" : ""), 24, Style.PaperDim, Style.Kai);
            name.HorizontalAlignment = HorizontalAlignment.Center; name.MouseFilter = MouseFilterEnum.Ignore;
            name.AddThemeColorOverride("font_shadow_color", new Color(0, 0, 0, 0.9f)); name.AddThemeConstantOverride("shadow_offset_y", 2);
            var line = new ColorRect { Color = new Color(Style.Amber, 0), CustomMinimumSize = new Vector2(0, 2), MouseFilter = MouseFilterEnum.Ignore };
            col.AddChild(name); col.AddChild(line); row.AddChild(card);
            card.MouseEntered += () => hover = code;
            card.MouseExited += () => { if (hover == code) hover = null; };
            card.GuiInput += e => { if (e is InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Left }) Pick(code); else if (e is InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Right }) Back(); };
            tabs[code] = (name, line);
        }
    }

    // ───────────── per-frame easing of the grading, film layer and names
    Color MaskColor(string? code) => code is null ? Colors.Black : People.FirstOrDefault(p => p.Code == code).Mask;
    static Vector3 V3(Color c) => new(c.R, c.G, c.B);

    public override void _Process(double delta)
    {
        if (sceneRect is null) return;
        StepCamera(delta);
        float a = Calm ? 1f : 1f - Mathf.Exp(-(float)delta * 8f);
        seconds += (float)delta;
        focusAmt = Mathf.Lerp(focusAmt, picked is null ? 0f : 1f, a);
        hoverAmt = Mathf.Lerp(hoverAmt, hover is not null && hover != picked && !taken.Contains(hover) ? 1f : 0f, a);
        closeAmt = Mathf.Lerp(closeAmt, picked is null ? 0f : 1f, a);
        // the light glides from one person to the next (each person's weight eases toward 1 or 0)
        float g = Calm ? 1f : 1f - Mathf.Exp(-(float)delta * 6f);
        for (int i = 0; i < 8; i++)
        {
            string? code = i < People.Length ? People[i].Code : null;
            pickW[i] = Mathf.Lerp(pickW[i], code is not null && code == (picked ?? lastPicked) ? 1f : 0f, g);
            hovW[i] = Mathf.Lerp(hovW[i], code is not null && code == hover && hover != picked ? 1f : 0f, g);
        }
        if (picked is not null) lastPicked = picked;
        var sm = (ShaderMaterial)sceneRect.Material;
        sm.SetShaderParameter("wa", new Vector4(pickW[0], pickW[1], pickW[2], pickW[3])); sm.SetShaderParameter("wb", new Vector4(pickW[4], pickW[5], pickW[6], pickW[7]));
        sm.SetShaderParameter("ha", new Vector4(hovW[0], hovW[1], hovW[2], hovW[3])); sm.SetShaderParameter("hb", new Vector4(hovW[4], hovW[5], hovW[6], hovW[7]));
        sm.SetShaderParameter("spot", focusAmt); sm.SetShaderParameter("hov", hoverAmt);
        var fm = (ShaderMaterial)film.Material;
        fm.SetShaderParameter("seconds", Calm ? 0f : seconds);
        fm.SetShaderParameter("close_amount", closeAmt);
        fm.SetShaderParameter("text_left", panelLeft);
        foreach (var (code, (name, line)) in tabs)
        {
            Color target = taken.Contains(code) ? new Color(Style.PaperDim, 0.45f) : code == picked ? Style.Amber : code == hover ? Style.HoverText : Style.PaperDim;
            name.Modulate = Colors.White; name.AddThemeColorOverride("font_color", name.GetThemeColor("font_color").Lerp(target, a));
            float la = code == picked ? 1f : code == hover && !taken.Contains(code) ? 0.55f : 0f;
            line.Color = new Color(Style.Amber, Mathf.Lerp(line.Color.A, la, a));
        }
    }

    // Hit test: the person whose (refined) mask covers the point.
    string? Hit(Vector2 p)
    {
        if (mask is null) return null;
        var (k, ox, oy) = Map(); int x = (int)((p.X - ox) / k), y = (int)((p.Y - oy) / k);
        if (x < 0 || y < 0 || x >= mask.GetWidth() || y >= mask.GetHeight()) return null;
        var c = mask.GetPixel(x, y);
        return Owner((int)(c.R * 255), (int)(c.G * 255), (int)(c.B * 255));
    }

    public override void _GuiInput(InputEvent e)
    {
        if (closing) return;
        if (e is InputEventMouseMotion mm)
        {
            hover = Hit(mm.Position);
            MouseDefaultCursorShape = hover is not null && !taken.Contains(hover) ? CursorShape.PointingHand : CursorShape.Arrow;
        }
        else if (e is InputEventMouseButton { Pressed: true } mb)
        {
            if (mb.ButtonIndex == MouseButton.Right) { Back(); return; }
            if (mb.ButtonIndex == MouseButton.Left)
            {
                var h = Hit(mb.Position);
                if (h is not null) Pick(h);
                // a click on no one (the empty floor, the walls, the sky) while someone is picked: back to the whole group
                // (Yunke 11-20). The info panel takes its own clicks, so they never come here.
                else if (picked is not null) { if (App.I.QaRunning) GD.Print("QA_SEL_BLANK_BACK"); Back(); }
            }
        }
    }
    // Esc: the back button's own (Ui.BackButton)

    // ───────────── pick / unpick
    // the deck notebook's + (a new deck): set by the screen that opened the picker; without it, the workshop from the main menu
    public Action<string>? NewDeck;
    public void Focus(string code) { if (IsInsideTree()) Pick(code); else Ready += () => Pick(code); }
    // 新手教程: where a person stands on screen now (the glow over Roland), and the "选择 …" button once their notebook is open
    public Rect2? PersonRect(string code)
    {
        if (sceneRect is null || !IsInstanceValid(sceneRect) || picked is not null) return null;
        float k = sceneRect.Size.X / MW; var (c, r) = SpotFor(code, 0.8f);
        var center = sceneRect.GlobalPosition + c * k; var half = r * k;
        return new Rect2(center - half, half * 2);
    }
    public Button? ChooseButton => chooseButton is not null && IsInstanceValid(chooseButton) && chooseButton.IsVisibleInTree() ? chooseButton : null;
    public string? Picked => picked;
    static Button? chooseButton;   // the last notebook's 选择 button (built by the static notebook code)
    // 新手教程 (Tutorial/Guide): who may be picked now (null) or why not; the deck the tutorial gives
    public static Func<string, string?>? Guide;
    public static Func<string, string?>? GuideDeck;

    void Pick(string code)
    {
        if (closing) return;
        if (taken.Contains(code)) { App.I.Toast(App.I.S!.Name(code) + " " + TakenToast, true); return; }
        if (Guide?.Invoke(code) is { } notNow) { App.I.Toast(notNow); return; }
        if (code == picked || !boxes.TryGetValue(code, out var b)) return;
        picked = code;
        deckKey = GuideDeck?.Invoke(code) ?? App.I.S!.Decks.For(code).Select(d => d.Key).FirstOrDefault() ?? "";   // default: the first deck
        // Push in gently on the person (the same point the spotlight centres on) and set them in the middle of the half of the
        // screen the info panel leaves free (Yunke 11-18: Roland sat off-centre because his outstretched arm widened the box).
        var focus = SpotFor(code, 1f).C;
        bool personOnRight = focus.X > MW / 2f;
        MoveCamera(1.35f, focus, new Vector2(personOnRight ? 0.66f : 0.34f, 0.5f), 0.9f);
        panelLeft = personOnRight; ShowInfo(code, personOnRight);
    }

    void Back()
    {
        if (closing) return;
        if (picked is not null)
        {
            picked = null; HideSide();
            MoveCamera(1f, new Vector2(MW / 2, MH / 2), new Vector2(0.5f, 0.46f), 0.7f);
            return;
        }
        Close();
    }

    void HideSide()
    {
        if (side is null) return;
        var old = side; side = null;
        if (Calm) { old.QueueFree(); return; }
        var tw = old.CreateTween(); tw.TweenProperty(old, "modulate:a", 0f, 0.15f); tw.TweenCallback(Callable.From(old.QueueFree));
    }

    // Investigator details, no printed card and no frame: plain text on a dark fade from the screen edge.
    void ShowInfo(string code, bool onLeft)
    {
        HideSide();
        var decks = new DeckPick(deckKey, k => deckKey = k, deckBars);
        side = BuildInfo(info, code, onLeft, Confirm, decks);
    }

    // The deck choice the detail panel edits (shared with the five-person lineup).
    public sealed record DeckPick(string Current, Action<string> Set, List<Button> Bars);

    // The detail panel: plain text on a dark fade from one screen edge, the choose button, and the deck notebook on the other side.
    // Shared by the core set's painted scene and the Dunwich Legacy's lineup (LineupStage).
    // notebookWithText: the deck notebook goes under the text on the same side (the lineup, where the other side holds people)
    public static Control BuildInfo(Control info, string code, bool onLeft, Action confirm, DeckPick decks, bool notebookWithText = false)
    {
        var S = App.I.S!; var card = S.Card(code); var st = StatsOf(code);
        var root = new Control { MouseFilter = MouseFilterEnum.Ignore }; info.AddChild(root); root.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        // the fade
        var scrim = new Control { MouseFilter = MouseFilterEnum.Ignore }; root.AddChild(scrim);
        scrim.AnchorTop = 0; scrim.AnchorBottom = 1;
        if (onLeft) { scrim.AnchorLeft = scrim.AnchorRight = 0; scrim.OffsetRight = 900; Ui.EdgeFade(scrim, new Color(0.01f, 0.01f, 0.01f, 0.86f), 900, 0, 0, 0); }
        else { scrim.AnchorLeft = scrim.AnchorRight = 1; scrim.OffsetLeft = -900; Ui.EdgeFade(scrim, new Color(0.01f, 0.01f, 0.01f, 0.86f), 0, 0, 900, 0); }
        // the text
        var col = new VBoxContainer { CustomMinimumSize = new Vector2(560, 0), MouseFilter = MouseFilterEnum.Ignore }; col.AddThemeConstantOverride("separation", 16);
        root.AddChild(col);
        col.AnchorTop = col.AnchorBottom = 0.46f; col.GrowVertical = GrowDirection.Both;
        if (onLeft) { col.AnchorLeft = col.AnchorRight = 0; col.OffsetLeft = 84; col.OffsetRight = 644; }
        else { col.AnchorLeft = col.AnchorRight = 1; col.OffsetLeft = -644; col.OffsetRight = -84; }
        col.AddChild(Style.Title(S.Name(code), 52));
        // the class as its icon (the deck builder's glyph in the class colour), then the subtitle (Yunke 11-22)
        var subRow = new HBoxContainer { MouseFilter = MouseFilterEnum.Ignore }; subRow.AddThemeConstantOverride("separation", 12); col.AddChild(subRow);
        string fac = J.String(card, "faction_code");
        bool glyph = Style.Glyph.TryGetValue(fac, out var facGlyph) && Style.Symbols is not null;
        bool neutralMark = !glyph && fac == "neutral" && Style.ClassIcons is not null;
        var classIcon = Style.Text(glyph ? facGlyph : neutralMark ? Style.NeutralGlyph : "", glyph ? 44 : 36, fac switch { "guardian" => new Color("5a8fd6"), "seeker" => new Color("d99a52"), "rogue" => new Color("54b47a"), "mystic" => new Color("9a78d0"), "survivor" => new Color("d86a6a"), _ => Style.Paper }, glyph ? Style.Symbols : Style.ClassIcons);
        classIcon.SizeFlagsVertical = SizeFlags.ShrinkCenter; classIcon.TooltipText = BuildDecks.FactionName(fac); classIcon.MouseFilter = MouseFilterEnum.Pass;
        if (classIcon.Text.Length > 0) subRow.AddChild(classIcon);
        var subText = Style.Text(J.String(card, "subname") is string sb && sb.Length > 0 ? sb : J.String(card, "faction_name"), 26, Style.Brass, Style.Kai); subText.SizeFlagsVertical = SizeFlags.ShrinkCenter; subRow.AddChild(subText);
        col.AddChild(new ColorRect { Color = new Color(Style.Brass, 0.35f), CustomMinimumSize = new Vector2(0, 1), MouseFilter = MouseFilterEnum.Ignore });
        var row = new HBoxContainer(); row.AddThemeConstantOverride("separation", 26);
        foreach (var (v, key, color) in new[] { (st[0], "willpower", Style.Willpower), (st[1], "intellect", Style.Intellect), (st[2], "combat", Style.Combat), (st[3], "agility", Style.Agility) })
        {
            var h = new HBoxContainer(); h.AddThemeConstantOverride("separation", 6);
            var g = Ui.Glyph(key, 30, color); g.VerticalAlignment = VerticalAlignment.Center; h.AddChild(g);
            var n = Style.Text(v.ToString(), 36, Style.HoverText, Style.Symbols); n.VerticalAlignment = VerticalAlignment.Center; h.AddChild(n);
            row.AddChild(h);
        }
        row.AddChild(Ui.Spacer(0, 0, true));
        foreach (var (kind, v, label) in new[] { ("health", st[4], "生命"), ("horror", st[5], "理智") })
        {
            var h = new HBoxContainer(); h.AddThemeConstantOverride("separation", 6);
            h.AddChild(Ui.TokenNumber(kind, v, 48));
            var l = Style.Text(label, 18, Style.PaperDim); l.VerticalAlignment = VerticalAlignment.Center; h.AddChild(l);
            row.AddChild(h);
        }
        col.AddChild(row);
        var text = J.String(card, "text");
        if (text.Length > 0) col.AddChild(CardReader.Rules(text, 22));
        var flavor = J.String(card, "flavor");
        if (flavor.Length > 0)
        {
            var f = Style.Text(System.Text.RegularExpressions.Regex.Replace(flavor, "<[^>]+>", ""), 19, new Color(Style.PaperDim, 0.85f), Style.Kai);
            f.AutowrapMode = TextServer.AutowrapMode.WordSmart; col.AddChild(f);
        }
        col.AddChild(Ui.Spacer(0, 8));
        // the confirm sits under the deck notebook: pick a deck, then confirm (Yunke 11-18 r6 #7)
        AddDeckList(root, code, onLeft, decks, notebookWithText, confirm);
        if (notebookWithText) { col.AnchorTop = col.AnchorBottom = 0.36f; }
        if (Calm) return root;
        // slide in from that screen edge
        float dx = onLeft ? -60 : 60;
        root.Modulate = new Color(1, 1, 1, 0); root.Position = new Vector2(dx, 0);
        var tw = root.CreateTween().SetParallel().SetTrans(Tween.TransitionType.Cubic).SetEase(Tween.EaseType.Out);
        tw.TweenProperty(root, "modulate:a", 1f, 0.3f).SetDelay(0.1f);
        tw.TweenProperty(root, "position:x", 0f, 0.5f).SetDelay(0.1f);
        return root;
    }

    // A notebook in the free space under the choice: one horizontal bar per deck the investigator has.
    // Clicking a bar selects that deck for the table; the choice button hands it back to the prepare screen.
    // a campaign's finished decks (kept at the end of a campaign) are not the usual way to start: hidden until asked for (Yunke 11-22)
    static bool showCompleted;
    static System.Text.Json.Nodes.JsonObject DeckMeta(System.Text.Json.JsonElement deck)
    {
        try { return System.Text.Json.Nodes.JsonNode.Parse(J.String(deck, "meta") is { Length: > 0 } m ? m : "{}") as System.Text.Json.Nodes.JsonObject ?? new(); } catch { return new(); }
    }
    static bool IsCompleted(System.Text.Json.JsonElement deck) => DeckMeta(deck)[BuildDecks.KindKey]?.GetValue<string>() == "completed";

    static void AddDeckList(Control root, string code, bool onLeft, DeckPick pick, bool withText = false, Action? confirm = null)
    {
        var S = App.I.S!;
        var every = S.Decks.For(code).ToList();
        int finished = every.Count(d => IsCompleted(d.Value));
        var decks = showCompleted ? every : every.Where(d => !IsCompleted(d.Value)).ToList();
        string deckKey = pick.Current; var deckBars = pick.Bars;
        if (decks.Count == 0 || !decks.Any(d => d.Key == deckKey)) { deckKey = decks.Count > 0 ? decks[0].Key : ""; pick.Set(deckKey); }
        var box = new PanelContainer { MouseFilter = MouseFilterEnum.Stop, CustomMinimumSize = new Vector2(360, 0) }; box.SetMeta("deckNotebook", true);
        // a solid dark patch with softly glowing edges, no hard rim (every box and button, Yunke 11-18)
        box.AddThemeStyleboxOverride("panel", Ui.SoftPanel(0.86f, 18, 14));
        root.AddChild(box);
        // the notebook sits on the opposite side from the investigator's text, low on that side, clear of the names
        box.AnchorTop = 0.5f; box.AnchorBottom = 0.9f; box.AnchorLeft = box.AnchorRight = onLeft ? 1 : 0;
        if (onLeft) { box.OffsetLeft = -420; box.OffsetRight = -60; } else { box.OffsetLeft = 60; box.OffsetRight = 420; }
        if (withText)
        {
            // under the text, same side
            box.AnchorTop = 0.7f; box.AnchorBottom = 0.95f; box.AnchorLeft = box.AnchorRight = onLeft ? 0 : 1;
            if (onLeft) { box.OffsetLeft = 84; box.OffsetRight = 644; } else { box.OffsetLeft = -644; box.OffsetRight = -84; }
        }
        var col = new VBoxContainer(); col.AddThemeConstantOverride("separation", 6); box.AddChild(col);
        // heading, then on the right: show / hide the finished decks (when there are any) and + (a new deck: to the workshop)
        var headRow = new HBoxContainer(); headRow.AddThemeConstantOverride("separation", 4); col.AddChild(headRow);
        var headText = Style.Text("调查员笔记 · 牌组（" + decks.Count + "）", 20, Style.Brass, Style.Kai); headText.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill; headRow.AddChild(headText);
        if (finished > 0)
        {
            var vis = LineIcon.Button("victory", showCompleted ? "隐藏通关卡组（" + finished + "）" : "显示通关卡组（" + finished + "）", () =>
            {
                showCompleted = !showCompleted;
                if (IsInstanceValid(root)) { foreach (var c in root.GetChildren().OfType<PanelContainer>().Where(b => b.HasMeta("deckNotebook")).ToList()) c.QueueFree(); }
                AddDeckList(root, code, onLeft, pick, withText, confirm);
            }, 34, showCompleted);   // accent (filled amber) while they are shown (Yunke 11-22: the pressed state needs a background)
            if (showCompleted)
            {
                var onBox = Ui.SoftBox(0.85f, true, 8, 2); if (onBox is StyleBoxFlat fb) fb.BgColor = new Color(Style.Amber, 0.45f);
                foreach (var st in new[] { "normal", "hover", "pressed" }) vis.AddThemeStyleboxOverride(st, onBox);
            }
            else vis.Modulate = new Color(1, 1, 1, 0.6f);
            headRow.AddChild(vis);
        }
        var add = LineIcon.Button("plus", "创建新卡组（打开牌组工坊）", () =>
        {
            for (Node? n = root; n is not null; n = n.GetParent()) if (n is SelectionStage st && st.NewDeck is { } nd) { nd(code); return; }
            var ready = System.Threading.Tasks.Task.WhenAll(BuildDecks.List(), TabooChip.Sets());
            App.I.HoldNextDip(ready, "正在整理牌组…"); App.I.Go(new DeckLibraryScreen());
        }, 34);
        headRow.AddChild(add);
        // wrapped: unwrapped, this line pushed the notebook off the screen's edge (r16: the Carcosa investigators, who have no deck yet)
        if (decks.Count == 0) { var none = Style.Text(finished > 0 ? "只有通关卡组（已隐藏），点右上角的桂冠显示。" : "还没有这名调查员的牌组，请先在牌组工坊构筑。", 18, Style.PaperDim, Style.Kai); none.AutowrapMode = TextServer.AutowrapMode.WordSmart; none.CustomMinimumSize = new Vector2(300, 0); col.AddChild(none); }
        else col.AddChild(Style.Text("点选一套牌组，开局使用它", 16, new Color(Style.PaperDim, 0.85f), Style.Kai));
        var list = new VBoxContainer(); list.AddThemeConstantOverride("separation", 6);
        var group = new ButtonGroup(); deckBars.Clear();
        foreach (var (key, deck) in decks)
        {
            int total = 0, kinds = 0, xp = 0;
            if (deck.TryGetProperty("slots", out var slots) && slots.ValueKind == System.Text.Json.JsonValueKind.Object)
                foreach (var p in slots.EnumerateObject())
                {
                    int n = p.Value.GetInt32(); total += n; if (n > 0) kinds++;
                    xp += J.Int(S.Card(p.Name), "xp") * n;   // experience cost of the cards in the deck
                }
            var name = deck.TryGetProperty("name", out var nm) ? nm.GetString() ?? key : key;
            bool done = IsCompleted(deck); var meta = done ? DeckMeta(deck) : null;
            if (done && name.EndsWith(" 通关")) name = name[..^3];   // kept before 11-22 with the word in the name: the mark says it now
            // Build-page look (0712): the investigator's portrait, the deck's name as the title, its size and experience below.
            var bar = new Button { Text = "", ToggleMode = true, ButtonGroup = group, ButtonPressed = key == deckKey, CustomMinimumSize = new Vector2(0, 74), FocusMode = FocusModeEnum.None, MouseDefaultCursorShape = CursorShape.PointingHand };
            var on = Ui.SoftBox(0.7f, true, 0, 0); var off = Ui.SoftBox(0.4f, false, 0, 0);   // soft-edged, no hard strip
            bar.AddThemeStyleboxOverride("pressed", on); bar.AddThemeStyleboxOverride("hover_pressed", on); bar.AddThemeStyleboxOverride("normal", off); bar.AddThemeStyleboxOverride("hover", off);
            var row = new HBoxContainer { MouseFilter = MouseFilterEnum.Ignore }; row.AddThemeConstantOverride("separation", 12);
            row.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect); row.OffsetLeft = 10; row.OffsetRight = -8; row.OffsetTop = 6; row.OffsetBottom = -6;
            bar.AddChild(row);
            var pic = Ui.SquareFace(code, 54); pic.SizeFlagsVertical = Control.SizeFlags.ShrinkCenter; row.AddChild(pic);   // square, class-colour rim (Yunke 11-20 #1)
            var text = new VBoxContainer { MouseFilter = MouseFilterEnum.Ignore, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, Alignment = BoxContainer.AlignmentMode.Center };
            text.AddThemeConstantOverride("separation", 2); row.AddChild(text);
            var title = Style.Text(name, 20, new Color(Style.Paper), Style.Kai); title.AutowrapMode = TextServer.AutowrapMode.Off;
            title.ClipText = true; title.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis; title.CustomMinimumSize = new Vector2(10, 0); bar.TooltipText = name;
            if (done)
            {
                // the finished mark: a small laurel by the name (Yunke 11-22: a mark, not the word)
                var line = new HBoxContainer { MouseFilter = MouseFilterEnum.Ignore }; line.AddThemeConstantOverride("separation", 6);
                var mark = new LineIcon { Kind = "victory", Ink = Style.Amber, CustomMinimumSize = new Vector2(22, 22) }; mark.SizeFlagsVertical = Control.SizeFlags.ShrinkCenter;
                title.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill; line.AddChild(title); line.AddChild(mark); text.AddChild(line);
                string result = meta![BuildDecks.ResultKey]?.GetValue<string>() ?? "", camp = meta[BuildDecks.CampaignKey]?.GetValue<string>() ?? "";
                bar.TooltipText = name + "\n战役通关卡组" + (camp.Length > 0 ? " · " + camp : "") + (result.Length > 0 ? " · " + result : "");
            }
            else text.AddChild(title);
            string sub = total + " 张 · " + kinds + " 种 · 经验 " + xp;
            text.AddChild(Style.Text(sub, 20, new Color(Style.PaperDim, 0.95f), Style.Kai));
            if (done)
            {
                // the trauma the investigator carries out of that campaign (Yunke 11-22)
                var phys = meta!["arkhamui_physical"]; var ment = meta["arkhamui_mental"];
                string trauma = phys is null && ment is null ? "创伤未记录"
                    : "身体创伤 " + (phys?.GetValue<int>() ?? 0) + " · 精神创伤 " + (ment?.GetValue<int>() ?? 0);
                text.AddChild(Style.Text(trauma, 17, new Color("d58b72"), Style.Kai));
                bar.CustomMinimumSize = new Vector2(0, 96);
            }
            var k = key; bar.Pressed += () => pick.Set(k); deckBars.Add(bar);
            list.AddChild(bar);
        }
        col.AddChild(WithHeight(Ui.Scroll(list), Math.Min(decks.Count, 3) * (decks.Any(d => IsCompleted(d.Value)) ? 96 : 80)));
        if (confirm is not null)
        {
            col.AddChild(Ui.Spacer(0, 6));
            var choose = Ui.MinimalButton("选择 " + S.Name(code), confirm, 26, true); choose.CustomMinimumSize = new Vector2(0, 60); chooseButton = choose;
            choose.Disabled = decks.Count == 0; col.AddChild(choose);
        }
    }

    // The faction colour of an investigator, as the build page marks decks (0712).
    internal static Color FactionColor(string code) => J.String(App.I.S!.Card(code), "faction_code") switch
    {
        "guardian" => new Color("3f6fb0"), "seeker" => new Color("b0743f"), "rogue" => new Color("3f8f5a"),
        "mystic" => new Color("6e4fa0"), "survivor" => new Color("b04848"), _ => new Color("7a7a7a"),
    };

    static ScrollContainer WithHeight(ScrollContainer sc, float h) { sc.CustomMinimumSize = new Vector2(340, h); sc.SizeFlagsVertical = Control.SizeFlags.ExpandFill; return sc; }

    // Switching between the two group pictures: a warm band of light sweeps across while the screen dims, the picture is
    // swapped at the darkest moment, then the light passes on and the new group fades in (Yunke 11-18 r6 #5).
    void SwitchWithLight(Scene target)
    {
        if (SwitchTo is null || closing || target == scene) return;
        if (Calm) { SwitchTo(target); return; }
        closing = true;
        var host = GetParent(); if (host is null) { SwitchTo(target); return; }   // the picker may sit on a canvas layer, not a control
        var qaw = System.Diagnostics.Stopwatch.StartNew();
        Ui.Bg(target.Picture);   // load the other picture now, so the sweep does not stall on it halfway
        if (App.I.QaRunning) GD.Print("SEL_T switch-bg " + qaw.ElapsedMilliseconds);
        var veil = new LightVeil { Mid = () => { var w2 = System.Diagnostics.Stopwatch.StartNew(); SwitchTo(target); if (App.I.QaRunning) GD.Print("SEL_T mid " + w2.ElapsedMilliseconds); } };
        host.AddChild(veil); veil.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
    }

    void Confirm()
    {
        if (picked is null || closing) return;
        var c = picked; closing = true;
        var deck = deckKey;
        // a dip to black back to the screen that asked (Yunke 11-20 #1: it returned with no transition)
        App.I.Dip(); QueueFree(); chosen(c, deck);
    }

    void Close()
    {
        if (closing) return; closing = true;
        if (Calm) { QueueFree(); return; }
        var tw = CreateTween(); tw.TweenProperty(this, "modulate:a", 0f, 0.2f); tw.TweenCallback(Callable.From(QueueFree));
    }
}

// The light sweep between the two group pictures: a warm band crosses the screen while it dims to dark; at the darkest
// moment Mid() swaps the picture; then the band passes on and the dark lifts. It advances on frame time capped at 1/30 s
// per frame, so a long frame (the new picture and its masks loading) cannot make it jump to the end unseen.
public partial class LightVeil : ColorRect
{
    public Action? Mid;
    public static bool QaShots;
    float t; bool fired;
    const float Seconds = 1.0f;
    static Shader? shader;
    public override void _Ready()
    {
        Color = Colors.White; MouseFilter = MouseFilterEnum.Stop; ZIndex = 120;
        Material = new ShaderMaterial { Shader = shader ??= new Shader { Code = @"shader_type canvas_item;
uniform float t = 0.0;
void fragment(){
  float pos = mix(-0.35, 1.35, t);
  float d = (UV.x * 0.85 + UV.y * 0.15) - pos;
  float band = exp(-d * d / 0.02);                 // a soft core, no flat edge
  float halo = exp(-d * d / 0.12);                 // a wide glow around it
  float veil = sin(3.14159 * clamp(t, 0.0, 1.0));
  vec3 dark = vec3(0.025, 0.02, 0.015);
  vec3 warm = vec3(1.0, 0.8, 0.5);
  float light = band * 0.55 + halo * 0.25;
  vec3 col = mix(dark, warm, clamp(light, 0.0, 1.0));
  COLOR = vec4(col, clamp(max(veil, light * 0.9), 0.0, 1.0));
}" } };
    }
    public override void _Process(double delta)
    {
        float before = t;
        t += Math.Min((float)delta, 1f / 30f) / Seconds;
        // QA: three frames of the sweep (only when asked: saving a shot stalls a frame and spoils frame-time checks)
        if (App.I.QaRunning && QaShots) foreach (var (mark, name) in new[] { (0.3f, "a"), (0.5f, "b"), (0.7f, "c") }) if (before < mark && t >= mark) App.I.Capture(System.IO.Path.GetFullPath(System.IO.Path.Combine(App.I.Root, "evidence/r6/sweep-" + name + ".png")));
        ((ShaderMaterial)Material).SetShaderParameter("t", Math.Min(t, 1f));
        if (!fired && t >= 0.5f) { fired = true; Mid?.Invoke(); }
        if (t >= 1f) QueueFree();
    }
}
