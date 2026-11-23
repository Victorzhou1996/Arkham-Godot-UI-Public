using Godot;
using System.Text.Json;

namespace ArkhamUI;

// The title screen's painting (Yunke 11-20): ten homepage paintings, each split into layers so parts of it can move
// (ArkhamHorror-Homepage-10-And-Animation-Layers-20261120.zip; app/assets/bg/home/home-scenes.json says, back to front, the
// layers, how each moves and which scenarios the painting belongs to).
// · Each time the menu is shown again, another painting, at random (never the same twice running).
// · When the save played last is in a scenario that has paintings of its own (the museum for 博物馆之夜, the theatre for
//   谢幕 …), one of those.
// · All layers sit on one 1672×941 canvas that is scaled as a whole to cover the screen (the package's rule: never fit layers
//   one by one). The motion is a shader per layer: tentacles and smoke bend about their root, the stars and moon behind the
//   window drift, snow falls slant and fades, water ripples inside a fixed outline, a flame breathes.
// · Moons that move (Yunke 11-22, H01 H06 H07 H10): the moon is cut from the painting into three layers by
//   tools/make_moon_layers.py — the sky behind it, the disc, and what lies in front of it (clouds, branches, bats, the
//   window's bar) — and "moonmove" slides the disc slowly out along dir and back, with a little sideways sway, its light
//   breathing; at t = 0 (and with reduced motion, which stops t) the moon is where the painter put it.
public partial class HomeBackground : Control
{
    sealed record Layer(string File, Vector2 Offset, string Motion, JsonElement Spec);
    sealed record Scene(string Id, string Title, string[] Scenarios, string[] Campaigns, Layer[] Layers);
    static List<Scene>? scenes;
    static string shownId = "";
    static readonly Vector2 Canvas = new(1672, 941);

    Control stage = null!;
    readonly List<ShaderMaterial> mats = new();
    double t;
    public string SceneId { get; private set; } = "";

    static List<Scene> Scenes()
    {
        if (scenes is not null) return scenes;
        scenes = new();
        try
        {
            using var doc = JsonDocument.Parse(System.IO.File.ReadAllText(System.IO.Path.Combine(App.I.Assets, "bg", "home", "home-scenes.json")));
            foreach (var s in doc.RootElement.GetProperty("scenes").EnumerateArray())
            {
                var layers = s.GetProperty("layers").EnumerateArray().Select(l =>
                {
                    var off = l.TryGetProperty("offset", out var o) ? new Vector2(o[0].GetSingle(), o[1].GetSingle()) : Vector2.Zero;
                    return new Layer(l.GetProperty("file").GetString() ?? "", off, l.TryGetProperty("motion", out var m) ? m.GetString() ?? "" : "", l.Clone());
                }).ToArray();
                string[] Strs(string k) => s.TryGetProperty(k, out var a) ? a.EnumerateArray().Select(x => x.GetString() ?? "").ToArray() : Array.Empty<string>();
                scenes.Add(new Scene(s.GetProperty("id").GetString() ?? "", s.GetProperty("title").GetString() ?? "", Strs("scenarios"), Strs("campaigns"), layers));
            }
        }
        catch (Exception ex) { GD.PushWarning("home scenes: " + ex.Message); }
        return scenes;
    }

    // ── the save played last: remembered (its id and the scenario it is in) when the menu is reached from a table, and kept
    // for the next start of the game in the data folder; without that, the newest save and the chapter its title names
    static string LastFile => System.IO.Path.Combine(App.I.DataRoot, "home-last-game.txt");

    public static void NoteFrom(Control? previous)
    {
        if (previous is not TableScreen ts) return;
        // TableScreen keeps its game id and model private; read them here rather than change the table's code (another session's)
        const System.Reflection.BindingFlags F = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
        var id = typeof(TableScreen).GetField("gameId", F)?.GetValue(ts) as Guid?;
        if (id is null) return;
        string scen = "";
        if (typeof(TableScreen).GetField("model", F)?.GetValue(ts) is TableModel m && J.String(m.Scenario, "id").Length > 0) scen = TableScreen.ScenarioCode(m);
        try { System.IO.Directory.CreateDirectory(App.I.DataRoot); System.IO.File.WriteAllText(LastFile, id + "\n" + scen); } catch { }
    }

    static (string Scenario, string Campaign) Last()
    {
        var games = App.I.S is { HasCore: true } svc ? svc.Backend.Games() : Array.Empty<(string Id, string Title, long Sequence)>();   // no rules core (a phone): no saves
        if (games.Length == 0) return ("", "");
        string[] rec = Array.Empty<string>();
        try { if (System.IO.File.Exists(LastFile)) rec = System.IO.File.ReadAllText(LastFile).Split('\n'); } catch { }
        var g = rec.Length > 0 ? games.FirstOrDefault(x => x.Id == rec[0].Trim()) : default;
        string scen = g.Id is not null && rec.Length > 1 ? rec[1].Trim() : "";
        if (g.Id is null) g = games[^1];
        // the title it was created with: "敦威治遗产 · 课外活动 · 2人", or a scenario played on its own "聚集于此 · 2人"
        var camp = Campaigns.All.FirstOrDefault(c => g.Title.StartsWith(c.Name)) ?? Campaigns.All.FirstOrDefault(c => c.Chapters.Any(ch => g.Title.Contains(ch.Name)));
        if (scen.Length == 0 && camp is not null) scen = camp.Chapters.FirstOrDefault(ch => g.Title.Contains(ch.Name))?.Code ?? "";
        if (scen.Length > 0) camp = Campaigns.ForScenario(scen);
        return (scen, camp?.Id ?? "");
    }

    // QA: home-bg:<scene id or scenario code> forces the next menus' painting ("-" back to the last save's)
    public static string? qaPick;
    // the menu's "换一张壁纸" button: the next painting comes from all of them (not the last save's), never the one showing
    public static bool anyNext;

    public HomeBackground()
    {
        MouseFilter = MouseFilterEnum.Ignore; ClipContents = true;
        var all = Scenes();
        if (all.Count == 0) return;
        var (scen, camp) = anyNext ? ("", "") : qaPick is { } q ? (q, "") : Last();
        anyNext = false;
        var pool = all.Where(s => s.Id == scen || (scen.Length > 0 && s.Scenarios.Contains(scen))).ToList();
        if (pool.Count == 0 && camp.Length > 0) pool = all.Where(s => s.Campaigns.Contains(camp)).ToList();
        bool matched = pool.Count > 0;
        if (!matched) pool = all;
        if (pool.Count > 1) pool = pool.Where(s => s.Id != shownId).ToList();
        var pick = pool[Random.Shared.Next(pool.Count)];
        shownId = SceneId = pick.Id;

        stage = new Control { MouseFilter = MouseFilterEnum.Ignore, Size = Canvas };
        AddChild(stage);
        foreach (var l in pick.Layers)
        {
            var r = new TextureRect { Texture = Load(l.File), ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize, StretchMode = TextureRect.StretchModeEnum.Scale,
                                      MouseFilter = MouseFilterEnum.Ignore, Position = l.Offset, Size = Canvas, TextureFilter = TextureFilterEnum.LinearWithMipmaps };
            if (l.Motion.Length > 0) { var m = Material(l); r.Material = m; mats.Add(m); }
            stage.AddChild(r);
        }
        Resized += Fit;
        if (App.I.QaRunning) GD.Print("QA_HOME_BG " + pick.Id + " " + pick.Title + " last=" + scen + "/" + camp + " matched=" + matched + " pool=" + pool.Count);
    }

    // one canvas, scaled as a whole to cover the screen and centred
    void Fit()
    {
        if (Size.X <= 0) return;
        float k = Math.Max(Size.X / Canvas.X, Size.Y / Canvas.Y);
        stage.Scale = new Vector2(k, k);
        stage.Position = (Size - Canvas * k) / 2;
    }

    static float F(JsonElement e, string k, float d) => e.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetSingle() : d;
    static Vector2 V(JsonElement e, string k, Vector2 d) => e.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.Array ? new Vector2(v[0].GetSingle(), v[1].GetSingle()) : d;

    static ShaderMaterial Material(Layer l)
    {
        var m = new ShaderMaterial { Shader = Shader };
        int mode = l.Motion switch { "bend" => 1, "drift" => 2, "snow" => 3, "ripple" => 4, "breathe" => 5, "moon" => 6, "moonmove" => 7, _ => 0 };
        m.SetShaderParameter("mode", mode);
        m.SetShaderParameter("root", V(l.Spec, "root", new Vector2(0.5f, 0.5f)));
        m.SetShaderParameter("reach", F(l.Spec, "reach", 0.1f));
        m.SetShaderParameter("amp", F(l.Spec, "amp", 2f));
        m.SetShaderParameter("speed", F(l.Spec, "speed", 0.5f));
        m.SetShaderParameter("zoom", F(l.Spec, "zoom", 1f));
        m.SetShaderParameter("dir", V(l.Spec, "dir", new Vector2(-60, 40)));
        m.SetShaderParameter("period", F(l.Spec, "period", 14f));
        m.SetShaderParameter("phase", F(l.Spec, "phase", 0f));
        m.SetShaderParameter("from", F(l.Spec, "from", 0f));
        m.SetShaderParameter("seed", (float)(l.File.GetHashCode() % 100) * 0.37f);
        return m;
    }

    static Texture2D? Load(string file)
    {
        var res = "res://assets/bg/home/" + file;
        if (ResourceLoader.Exists(res)) return GD.Load<Texture2D>(res);
        var path = System.IO.Path.Combine(App.I.Assets, "bg", "home", file);   // not imported by the editor yet: read the file
        if (!System.IO.File.Exists(path)) return null;
        var img = Image.LoadFromFile(path);
        if (img is null) return null;
        img.GenerateMipmaps();
        return ImageTexture.CreateFromImage(img);
    }

    public override void _Ready()
    {
        Fit();
        // QA: home-bg-still holds every layer at its first frame (to compare); the canvas's place on screen
        QaScript.Register("home-bg-still", _ => { foreach (var m in mats) m.SetShaderParameter("t", 0f); SetProcess(false); GD.Print("QA_HOME_BG_STAGE " + stage.Position + " x" + stage.Scale.X); });
    }

    public override void _Process(double delta)
    {
        if (App.I.S?.Settings.ReducedMotion == true) return;   // the painting as it is, still
        t += delta;
        foreach (var m in mats) m.SetShaderParameter("t", (float)t);
    }

    static Shader? shader;
    static Shader Shader => shader ??= new Shader { Code = @"shader_type canvas_item;
uniform float t = 0.0;
uniform int mode = 0;            // 1 bend, 2 drift, 3 snow, 4 ripple, 5 breathe, 6 moon, 7 moonmove
uniform vec2 root = vec2(0.5);   // bend: the fixed end (canvas fractions)
uniform float reach = 0.1;       // bend: how far from the root the motion is full
uniform float amp = 2.0;         // canvas pixels
uniform float speed = 0.5;
uniform float zoom = 1.0;        // drift: a little zoom so the edges never show
uniform vec2 dir = vec2(-60.0, 40.0);   // snow: travel over one period, in canvas pixels; moonmove: the farthest it goes
uniform float period = 14.0;
uniform float phase = 0.0;
uniform float from = 0.0;        // ripple: rows above this stay still (the horizon)
uniform float seed = 0.0;
const vec2 C = vec2(1672.0, 941.0);

float hash(vec2 p) { return fract(sin(dot(p, vec2(127.1, 311.7))) * 43758.5453); }
float noise(float x) { float i = floor(x), f = fract(x); return mix(hash(vec2(i, seed)), hash(vec2(i + 1.0, seed)), f * f * (3.0 - 2.0 * f)); }
vec4 at(sampler2D tex, vec2 uv) { vec4 c = texture(tex, uv); if (uv.x < 0.0 || uv.y < 0.0 || uv.x > 1.0 || uv.y > 1.0) c.a = 0.0; return c; }

void fragment() {
    vec2 uv = UV;
    vec4 col;
    if (mode == 1) {
        // bend: the root stays put; further out the shape sways more, a slow wave running along it
        vec2 d = (uv - root) * C;
        float dist = length(d) / C.x;
        float w = smoothstep(0.0, reach, dist);
        float s = t * speed;
        vec2 o = vec2(sin(s + dist * 18.0 + seed), cos(s * 0.83 + dist * 14.0 + seed)) * amp * w;
        col = at(TEXTURE, uv - o / C);
    } else if (mode == 2) {
        vec2 o = vec2(sin(t * speed + seed), cos(t * speed * 0.77 + seed)) * amp / C;
        col = at(TEXTURE, vec2(0.5) + (uv - vec2(0.5)) / zoom + o);
    } else if (mode == 3) {
        // snow: drifts along dir over a period and fades in and out; two copies half a period apart
        float p = fract(t / period + phase);
        col = at(TEXTURE, uv - dir * p / C);
        col.a *= sin(3.14159 * p);
    } else if (mode == 4) {
        // ripple: the water moves inside, its outline (alpha) stays where it is
        float w = smoothstep(from, from + 0.04, uv.y);
        vec2 o = vec2(sin(uv.y * 140.0 + t * speed * 2.0) + 0.5 * sin(uv.x * 60.0 - t * speed * 1.3),
                      cos(uv.x * 90.0 + t * speed * 1.6)) * amp * w / C;
        vec4 inner = texture(TEXTURE, uv + o);
        col = vec4(inner.rgb, texture(TEXTURE, uv).a);
        float lum = dot(inner.rgb, vec3(0.3, 0.6, 0.1));
        col.rgb *= 1.0 + w * smoothstep(0.55, 0.9, lum) * 0.25 * sin(t * 2.1 + hash(floor(uv * 300.0)) * 30.0);
    } else if (mode == 6) {
        // moon: the surface turns a little (amp degrees) under its fixed outline and the base's glazing bars (left out of
        // the alpha); its light breathes, with a soft halo round the rim (reach = the radius, a fraction of the width)
        vec2 d = (uv - root) * C;
        float a = sin(t * speed + seed) * radians(amp);
        vec2 rd = vec2(cos(a) * d.x - sin(a) * d.y, sin(a) * d.x + cos(a) * d.y);
        vec3 inner = texture(TEXTURE, root + rd / C).rgb;
        float body = texture(TEXTURE, uv).a;
        float breath = sin(t * speed * 1.7 + seed);
        float r = reach * C.x, dist = length(d);
        float halo = step(r - 0.5, dist) * exp(-max(dist - r, 0.0) / (r * 0.45)) * (0.14 + 0.06 * breath);
        col = vec4(mix(vec3(0.80, 0.96, 0.92), inner * (1.0 + 0.06 * breath), body), max(body, halo));
    } else if (mode == 7) {
        // moonmove: the disc goes out along dir and back over a period (eased, starting where the painter put it), swaying
        // amp pixels to the side; its light breathes a little (speed). At t = 0 the moon is where the painter put it. The moon's
        // glow stays the painting's own (a halo drawn here showed as a ring through the thin clouds over the moon)
        float s = 6.28318 * (t / period + phase);
        vec2 n = normalize(dir);
        vec2 o = dir * (0.5 - 0.5 * cos(s)) + vec2(-n.y, n.x) * amp * sin(s);
        col = at(TEXTURE, uv - o / C);
        col.rgb *= 1.0 + 0.035 * sin(t * speed);
    } else if (mode == 5) {
        float f = 0.78 + 0.16 * noise(t * 2.2 * speed) + 0.06 * noise(t * 7.0 * speed);
        col = texture(TEXTURE, uv);
        col.a *= f; col.rgb *= 0.92 + 0.12 * f;
    } else {
        col = texture(TEXTURE, uv);
    }
    COLOR = col;
}" };
}
