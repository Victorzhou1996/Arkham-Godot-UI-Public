using Godot;
using Gathering;

namespace ArkhamUI;

// Texture cache and reusable widgets in the reference style.
public static class Ui
{
    static readonly Dictionary<string, Texture2D?> cache = new();

    public static Texture2D? Tex(string resPath)
    {
        if (cache.TryGetValue(resPath, out var t)) return t;
        long at = System.Diagnostics.Stopwatch.GetTimestamp();
        t = ResourceLoader.Exists(resPath) ? GD.Load<Texture2D>(resPath) : null;
        double took = System.Diagnostics.Stopwatch.GetElapsedTime(at).TotalMilliseconds; LoadMs += took; Loads++;
        if (TraceLoads) GD.Print($"TEX_LOAD {took:F0} {resPath}");
        cache[resPath] = t; return t;
    }
    // perf: time spent loading textures on the main thread (cache misses), read and reset by the table's render log
    public static double LoadMs; public static int Loads;
    public static readonly bool TraceLoads = OS.GetCmdlineUserArgs().Contains("--perf-probe");
    // Load textures on worker threads ahead of their first use (the table's first render, perf 11-21): what is already
    // cached or missing is skipped; the rest comes into the cache as it would through Tex.
    static readonly HashSet<string> preloadTried = new();
    public static async Task<int> Preload(IEnumerable<string> resPaths)
    {
        var want = resPaths.Where(p => !cache.ContainsKey(p) && preloadTried.Add(p) && ResourceLoader.Exists(p)).ToList();
        if (want.Count == 0) return 0;
        foreach (var p in want) ResourceLoader.LoadThreadedRequest(p, "Texture2D", useSubThreads: true);
        var tree = (SceneTree)Engine.GetMainLoop();
        foreach (var p in want)
        {
            while (ResourceLoader.LoadThreadedGetStatus(p) == ResourceLoader.ThreadLoadStatus.InProgress) await tree.ToSignal(tree, SceneTree.SignalName.ProcessFrame);
            if (ResourceLoader.LoadThreadedGetStatus(p) == ResourceLoader.ThreadLoadStatus.Loaded && !cache.ContainsKey(p)) cache[p] = ResourceLoader.LoadThreadedGet(p) as Texture2D;
        }
        return want.Count;
    }
    public static Texture2D? Card(string code)
    {
        code = CoreCatalog.Normalize(code);
        return Tex("res://assets/cards/" + code + ".webp") ?? Tex("res://assets/cards/" + code.TrimEnd('a', 'b') + ".webp")
            ?? ArtStore.Card(code) ?? ArtStore.Card(code.TrimEnd('a', 'b'));   // later cycles: the green package's card images (workshop)
    }
    // One side's printed image exactly: no falling back to the other side (Card("07102b") answers with the front when there is
    // no back file — the unrevealed side must never be stood in for by the revealed one, local-rclick-location)
    public static Texture2D? CardExact(string code)
    {
        code = CoreCatalog.Normalize(code);
        return Tex("res://assets/cards/" + code + ".webp") ?? ArtStore.Card(code);
    }
    // the html version's own card backs (Yunke 11-18: the encounter deck and the draw use the original back)
    public static Texture2D? CardBack(bool encounter) => Tex(encounter ? "res://assets/ui/backs/encounter-0712.jpg" : "res://assets/ui/backs/player-0712.webp");
    // A painted background (assets/bg/<name>.png), or card art for scenarios without paintings (the Dunwich Legacy):
    // "card:<code>" is the card's illustration (or a crop of its printed art), "agenda:<code>" / "act:<code>" the picture part.
    public static Texture2D? Bg(string name)
    {
        if (string.IsNullOrEmpty(name)) return null;
        if (name.StartsWith("card:")) { var c = name[5..]; return Art(c) ?? Card(c); }
        if (name.StartsWith("agenda:")) return ArkhamUI.Art.Crop(name[7..], "agenda");
        if (name.StartsWith("act:")) return ArkhamUI.Art.Crop(name[4..], "act");
        return Tex("res://assets/bg/" + name + ".png");
    }

    // Reference-version painted illustration for a card code (assets/ref/art/cards/<code>-<slug>.webp), or null.
    static Dictionary<string, string>? artIndex;
    public static Texture2D? Art(string code) => ArtPath(code) is { } p ? Tex(p) : null;
    public static string? ArtPath(string code)
    {
        if (artIndex is null)
        {
            artIndex = new();
            // reference-version illustrations first, then crops of the printed art for the few cards it lacks
            foreach (var dir in new[] { "res://assets/ref/art/cards", "res://assets/art-fallback" })
            {
                using var d = DirAccess.Open(dir);
                if (d is null) continue;
                foreach (var f in d.GetFiles())
                {
                    var name = f.EndsWith(".import") ? f[..^7] : f;   // exported builds list the .import stubs
                    if (name.EndsWith(".webp")) artIndex.TryAdd(name.Split('-')[0], dir + "/" + name);
                }
            }
        }
        return artIndex.TryGetValue(code, out var p) ? p : null;
    }
    // The texture files the table may show for a card (its illustration, its printed card, an investigator's mini card) and
    // the table's fixed pictures (slots, tokens, card backs, the desk): preloaded on worker threads before the first render
    // (perf 11-21: the first render loaded 47 textures on the main thread, 0.9–1.0 s of a 1.1 s freeze).
    public static IEnumerable<string> CardPaths(string code)
    {
        code = CoreCatalog.Normalize(code);
        // the printed card only for a card without an illustration (the table shows illustrations; a printed card opens on demand)
        if (ArtPath(code) is { } art) yield return art; else yield return "res://assets/cards/" + code + ".webp";
        yield return "res://assets/ui/mini/" + code + ".jpg";
        yield return "res://assets/art-story/" + code + ".webp";   // an act's or agenda's story page picture
        if (ArtPath(code + "b") is { } back) yield return back;      // a location's unrevealed side (01150b)
    }
    public static IEnumerable<string> TablePaths()
    {
        yield return "res://assets/bg/desk-table.png";
        yield return "res://assets/ui/backs/encounter-0712.jpg"; yield return "res://assets/ui/backs/player-0712.webp";
        foreach (var k in new[] { "clue", "health", "horror", "doom", "resource", "sanity" }) yield return "res://assets/ui/tokens/" + k + ".png";
        foreach (var k in new[] { "hand", "arcane", "body", "ally", "accessory", "tarot", "head" }) yield return "res://assets/ui/slots/" + k + ".png";
        foreach (var f in new[] { "plus1", "0", "minus1", "minus2", "minus3", "minus4", "minus5", "minus6", "minus7", "minus8", "skull", "cultist", "tablet", "elderthing", "autofail", "eldersign", "bless", "curse", "frost" })
            yield return "res://assets/tokens/ct_" + f + ".png";
    }

    // Fades a picture's own edges to transparent (not to a dark colour), so it melts into whatever is behind it.
    // `origin` and `window` give the visible rect in the picture's local pixels; l/t/r/b are the fade widths in px.
    static Shader? edgeAlphaShader;
    public static ShaderMaterial EdgeAlpha(Vector2 origin, Vector2 window, float l, float t, float r, float b)
    {
        edgeAlphaShader ??= new Shader { Code = @"shader_type canvas_item;
uniform vec2 origin; uniform vec2 window; uniform vec4 fade;
varying vec2 p;
void vertex(){ p = VERTEX; }
void fragment(){
    vec2 q = p - origin;
    float a = smoothstep(0.0, max(fade.x, 0.001), q.x) * smoothstep(0.0, max(fade.y, 0.001), q.y)
            * smoothstep(0.0, max(fade.z, 0.001), window.x - q.x) * smoothstep(0.0, max(fade.w, 0.001), window.y - q.y);
    COLOR.a *= a;
}" };
        var m = new ShaderMaterial { Shader = edgeAlphaShader };
        m.SetShaderParameter("origin", origin); m.SetShaderParameter("window", window); m.SetShaderParameter("fade", new Vector4(l, t, r, b));
        return m;
    }

    // Soft dark fades laid over the edges of a picture so it melts into a dark panel (sizes in px: left, top, right, bottom).
    // One rect per side whose alpha is computed per pixel (a shader), smooth from the edge inwards. The earlier version stacked
    // thin colour strips (a gradient texture had once stopped drawing, playtest 2 #7); the strips left faint seams and a stepped
    // edge on every small card, worst when tilted or scaled (Yunke 11-18). A shader has neither and always draws.
    static Shader? edgeFadeShader;
    static readonly Dictionary<int, ShaderMaterial> edgeFadeMats = new();
    static ShaderMaterial EdgeFadeMat(int side)
    {
        if (edgeFadeMats.TryGetValue(side, out var m)) return m;
        edgeFadeShader ??= new Shader { Code = @"shader_type canvas_item;
uniform int side = 0;   // 0 left, 1 top, 2 right, 3 bottom: the edge where the colour is strongest
void fragment(){
    float x = side == 0 ? UV.x : side == 1 ? UV.y : side == 2 ? 1.0 - UV.x : 1.0 - UV.y;   // 0 at the edge, 1 inside
    float s = 1.0 - smoothstep(0.0, 1.0, x);
    COLOR = vec4(COLOR.rgb, COLOR.a * s * s);
}" };
        m = new ShaderMaterial { Shader = edgeFadeShader }; m.SetShaderParameter("side", side); edgeFadeMats[side] = m; return m;
    }
    public static void EdgeFade(Control host, Color c, float l, float t, float r, float b)
    {
        void Add(float size, int side)
        {
            if (size <= 0) return;
            var rect = new ColorRect { Color = c, MouseFilter = Control.MouseFilterEnum.Ignore, Material = EdgeFadeMat(side) };
            host.AddChild(rect);
            switch (side)
            {
                case 0: rect.AnchorTop = 0; rect.AnchorBottom = 1; rect.AnchorLeft = rect.AnchorRight = 0; rect.OffsetRight = size; break;
                case 1: rect.AnchorLeft = 0; rect.AnchorRight = 1; rect.AnchorTop = rect.AnchorBottom = 0; rect.OffsetBottom = size; break;
                case 2: rect.AnchorTop = 0; rect.AnchorBottom = 1; rect.AnchorLeft = rect.AnchorRight = 1; rect.OffsetLeft = -size; break;
                default: rect.AnchorLeft = 0; rect.AnchorRight = 1; rect.AnchorTop = rect.AnchorBottom = 1; rect.OffsetTop = -size; break;
            }
        }
        Add(l, 0); Add(t, 1); Add(r, 2); Add(b, 3);
    }

    // The reverse of EdgeFade: transparent at the outer edges, fully the colour inside, so a block fades in from its outside.
    public static void EdgeFadeIn(Control host, Color c, float l, float t, float r, float b)
    {
        void Add(float size, bool vertical, bool fromStart)
        {
            if (size <= 0) return;
            var g = new Gradient(); g.SetColor(0, new Color(c, 0)); g.SetColor(1, c);
            var tex = new GradientTexture2D { Gradient = g, Width = 64, Height = 64, FillFrom = Vector2.Zero, FillTo = vertical ? new Vector2(0, 1) : new Vector2(1, 0) };
            if (!fromStart) { tex.FillFrom = vertical ? new Vector2(0, 1) : new Vector2(1, 0); tex.FillTo = Vector2.Zero; }
            var rect = new TextureRect { Texture = tex, ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize, StretchMode = TextureRect.StretchModeEnum.Scale, MouseFilter = Control.MouseFilterEnum.Ignore };
            host.AddChild(rect);
            if (vertical) { rect.AnchorLeft = 0; rect.AnchorRight = 1; if (fromStart) { rect.AnchorTop = rect.AnchorBottom = 0; rect.OffsetBottom = size; } else { rect.AnchorTop = rect.AnchorBottom = 1; rect.OffsetTop = -size; } }
            else { rect.AnchorTop = 0; rect.AnchorBottom = 1; if (fromStart) { rect.AnchorLeft = rect.AnchorRight = 0; rect.OffsetRight = size; } else { rect.AnchorLeft = rect.AnchorRight = 1; rect.OffsetLeft = -size; } }
        }
        Add(l, false, true); Add(t, true, true); Add(r, false, false); Add(b, true, false);
    }

    // Shared backdrop for story screens: the scene blurred and darkened, with a dark vignette at the edges.
    static Shader? blurShader;
    public static ShaderMaterial Blur(float dim = 0.45f, float radius = 0.0018f)
    {
        var m = new ShaderMaterial { Shader = blurShader ??= new Shader { Code = @"shader_type canvas_item;
uniform vec2 r = vec2(0.0018, 0.0032); uniform float dim = 0.45;
void fragment(){ vec4 c = vec4(0.0); for (int x = -3; x <= 3; x++) for (int y = -3; y <= 3; y++) c += texture(TEXTURE, UV + vec2(float(x), float(y)) * r);
  c /= 49.0; float l = dot(c.rgb, vec3(0.2126, 0.7152, 0.0722)); COLOR = vec4(mix(vec3(l), c.rgb, 0.8) * dim, 1.0); }" } };
        m.SetShaderParameter("dim", dim); m.SetShaderParameter("r", new Vector2(radius, radius * 1.78f));
        return m;
    }
    public static Control Backdrop(Vector2 vp, Texture2D? tex, float dim = 0.45f)
    {
        var root = new Control { CustomMinimumSize = vp, Size = vp, MouseFilter = Control.MouseFilterEnum.Stop };
        root.AddChild(new ColorRect { Color = Style.Ink, Size = vp, MouseFilter = Control.MouseFilterEnum.Ignore });
        if (tex is not null) { var bg = Image(tex, vp, true); bg.Size = vp; bg.Material = Blur(dim); root.AddChild(bg); }
        var fade = new Control { Size = vp, MouseFilter = Control.MouseFilterEnum.Ignore }; root.AddChild(fade);
        EdgeFade(fade, new Color(0.01f, 0.01f, 0.01f, 0.75f), vp.X * 0.18f, vp.Y * 0.2f, vp.X * 0.18f, vp.Y * 0.28f);
        return root;
    }

    // On/off switch that reads clearly in both states: "开" amber with an underline, "关" dim.
    public static Button Toggle(bool on)
    {
        var b = new Button { ToggleMode = true, ButtonPressed = on, FocusMode = Control.FocusModeEnum.None, MouseDefaultCursorShape = Control.CursorShape.PointingHand, CustomMinimumSize = new Vector2(96, 44), SizeFlagsHorizontal = Control.SizeFlags.ShrinkBegin };
        b.AddThemeFontSizeOverride("font_size", 22); if (Style.Kai is not null) b.AddThemeFontOverride("font", Style.Kai);
        var onBox = SoftBox(0.6f, true); var offBox = SoftBox(0.25f, false);
        b.AddThemeStyleboxOverride("normal", offBox); b.AddThemeStyleboxOverride("hover", offBox);
        b.AddThemeStyleboxOverride("pressed", onBox); b.AddThemeStyleboxOverride("hover_pressed", onBox); b.AddThemeStyleboxOverride("focus", new StyleBoxEmpty());
        b.AddThemeColorOverride("font_color", new Color(Style.PaperDim, 0.7f)); b.AddThemeColorOverride("font_hover_color", Style.Paper);
        b.AddThemeColorOverride("font_pressed_color", Style.Amber); b.AddThemeColorOverride("font_hover_pressed_color", Style.Amber);
        void Label(bool v) => b.Text = v ? "开" : "关";
        Label(on); b.Toggled += Label;
        return b;
    }

    // Plain button for the simplified look: translucent black, an amber hairline under it on hover / when accented.
    // A small square investigator portrait in a rounded frame coloured by the investigator's class, as on the html home page
    // (GameRow.vue .investigator-portrait-container: 50 px, radius 5, 3 px class border, soft shadow) — Yunke 11-18 #1.
    public static Control SquarePortrait(string code, float size = 56)
    {
        string faction = J.String(App.I.S!.Card(code), "faction_code");
        var border = new Color(faction switch { "guardian" => "1d5a8e", "seeker" => "b0601a", "rogue" => "0e5c13", "mystic" => "3a2a9c", "survivor" => "9c2229", _ => "6f6f6f" });
        var frame = new PanelContainer { CustomMinimumSize = new Vector2(size, size), MouseFilter = Control.MouseFilterEnum.Pass, TooltipText = App.I.S!.Name(code) };
        var sb = new StyleBoxFlat { BgColor = new Color(0, 0, 0, 0.6f), BorderColor = border, AntiAliasing = true, ShadowColor = new Color(0, 0, 0, 0.45f), ShadowSize = 6, ShadowOffset = new Vector2(1, 1) };
        sb.SetBorderWidthAll(3); sb.SetCornerRadiusAll(5); frame.AddThemeStyleboxOverride("panel", sb);
        // the picture is clipped to the inside of the frame; the html portrait art shows the face (its upper part)
        var clip = new Panel { MouseFilter = Control.MouseFilterEnum.Ignore, ClipChildren = CanvasItem.ClipChildrenMode.Only, CustomMinimumSize = new Vector2(size - 6, size - 6) };
        var mask = new StyleBoxFlat { BgColor = Colors.White }; mask.SetCornerRadiusAll(3); clip.AddThemeStyleboxOverride("panel", mask);
        frame.AddChild(clip);
        var tex = Mini(code); float w = size - 6;
        var img = new TextureRect { Texture = tex, ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize, StretchMode = TextureRect.StretchModeEnum.KeepAspectCovered, MouseFilter = Control.MouseFilterEnum.Ignore, TextureFilter = CanvasItem.TextureFilterEnum.LinearWithMipmapsAnisotropic };
        float h = tex is null ? w : Math.Max(w, w * tex.GetHeight() / Math.Max(1, tex.GetWidth()));
        img.Size = new Vector2(w, h); img.Position = new Vector2(0, -h * 0.06f); clip.AddChild(img);
        return frame;
    }

    // Every screen's way back sits in the same place: the bottom-left corner (Yunke 11-18 #3: it was top-right on some screens,
    // bottom-left on others). Screens that lay out their own bottom row put it first in that row instead, which lands there too.
    // the way back on every screen (Yunke 11-21): one round icon button in the top-left corner, its words as a tooltip, and Esc
    public static Button BackButton(Control parent, string text, Action onPress) => WorkshopBack.Attach(parent, text, onPress);

    public static Button MinimalButton(string text, Action onPress, int size = 24, bool accent = false)
    {
        var b = new Button { Text = text, FocusMode = Control.FocusModeEnum.None, MouseDefaultCursorShape = Control.CursorShape.PointingHand };
        b.AddThemeFontSizeOverride("font_size", size);
        if (Style.Kai is not null) b.AddThemeFontOverride("font", Style.Kai);
        StyleBoxFlat Box(float bg, Color line, int lineW) => SoftBox(bg, line, lineW, 26, 10);
        b.AddThemeColorOverride("font_disabled_color", new Color(Style.PaperDim, 0.35f));   // a disabled item must read as unavailable
        b.AddThemeStyleboxOverride("normal", Box(accent ? 0.55f : 0.38f, new Color(Style.Amber, accent ? 0.8f : 0f), accent ? 2 : 0));
        b.AddThemeStyleboxOverride("hover", Box(0.72f, Style.Amber, 2));
        b.AddThemeStyleboxOverride("pressed", Box(0.82f, Style.Amber, 2));
        b.AddThemeStyleboxOverride("disabled", Box(0.25f, new Color(0, 0, 0, 0), 0));
        b.AddThemeStyleboxOverride("focus", new StyleBoxEmpty());
        b.AddThemeColorOverride("font_color", accent ? Style.HoverText : Style.Paper);
        b.AddThemeColorOverride("font_hover_color", Style.HoverText);
        b.AddThemeColorOverride("font_pressed_color", Style.Amber);
        b.Pressed += () => { Sfx.Click(); onPress(); };
        return b;
    }

    // Every button's backdrop (Yunke 11-18: "所有的按钮的背景也要做边缘渐变化", one style for the whole game): a dark patch
    // whose edges melt away — a light fill plus a wide blurred shadow of the same dark, no hard rim. The accent underline stays.
    public static StyleBoxFlat SoftBox(float bg, Color line, int lineW, int padX, int padY)
    {
        // Yunke 11-18 r6: "我们的按钮都用纯色边缘柔光做背景" — a solid patch of one colour whose edge glows softly away into the
        // table: the fill and a wide shadow of the same colour, rounded, no rim. "lit" (accent / hover / chosen) is a warm
        // bronze fill with an amber glow.
        bool lit = lineW > 0 && line.A > 0.01f;
        var fill = lit ? new Color(0.27f, 0.19f, 0.085f) : new Color(0.05f, 0.043f, 0.036f);
        float a = lit ? Math.Clamp(0.62f + bg * 0.35f, 0f, 0.95f) : Math.Clamp(0.45f + bg * 0.45f, 0f, 0.9f);
        // Yunke 11-19 r9: the amber glow was too wide ("光晕加的太大了") — a tight, faint halo now
        var glow = lit ? new Color(0.86f, 0.6f, 0.26f, 0.1f + bg * 0.1f) : new Color(fill, a * 0.6f);
        var sb = new StyleBoxFlat { BgColor = new Color(fill, a), DrawCenter = true,
            ContentMarginLeft = padX, ContentMarginRight = padX, ContentMarginTop = padY, ContentMarginBottom = padY,
            ShadowColor = glow, ShadowSize = lit ? 6 : 7, AntiAliasing = true, AntiAliasingSize = 2.5f };
        sb.SetCornerRadiusAll(14); return sb;
    }
    // a panel (notebook, list box): the same solid dark patch with a soft edge, no rim
    public static StyleBoxFlat SoftPanel(float alpha = 0.85f, int padX = 16, int padY = 12)
    {
        var c = new Color(0.03f, 0.026f, 0.022f);
        var sb = new StyleBoxFlat { BgColor = new Color(c, alpha), ShadowColor = new Color(c, alpha * 0.8f), ShadowSize = 20, AntiAliasing = true, AntiAliasingSize = 3f,
            ContentMarginLeft = padX, ContentMarginRight = padX, ContentMarginTop = padY, ContentMarginBottom = padY };
        sb.SetCornerRadiusAll(16); return sb;
    }
    // A bright glowing bar behind a menu line (Yunke 11-18 r6 #1: like the dark strip behind a hand card's name, but lit):
    // strongest a little in from the left, fading out to the right and at the top and bottom edges.
    static ImageTexture? glowBarTex;
    public static StyleBoxTexture GlowBar(int padL = 6, int padR = 24, int padY = 8)
    {
        if (glowBarTex is null)
        {
            const int W = 512, H = 64; var img = Godot.Image.CreateEmpty(W, H, false, Godot.Image.Format.Rgba8);
            for (int y = 0; y < H; y++) for (int x = 0; x < W; x++)
            {
                float u = x / (W - 1f), v = y / (H - 1f);
                float hx = Mathf.SmoothStep(0f, 0.08f, u) * (1f - Mathf.SmoothStep(0.35f, 1f, u));
                float dv = (v - 0.5f) / 0.5f; float vy = Mathf.Exp(-dv * dv * 2.6f) * Mathf.SmoothStep(1f, 0.82f, Math.Abs(dv));
                float a = hx * vy;
                var col = new Color(0.93f, 0.74f, 0.42f).Lerp(new Color(1f, 0.9f, 0.68f), vy * hx * 0.6f);
                img.SetPixel(x, y, new Color(col, a * 0.92f));
            }
            glowBarTex = ImageTexture.CreateFromImage(img);
        }
        var sb = new StyleBoxTexture { Texture = glowBarTex, ContentMarginLeft = padL, ContentMarginRight = padR, ContentMarginTop = padY, ContentMarginBottom = padY };
        return sb;
    }
    // ───────── menus in the title screen's manner (Yunke 11-19: "游戏中菜单的按钮形式和特效做成首页那种ui效果，其他地方的菜单
    // 也是"): a line of text with no box; under the pointer a bright glowing bar lights behind it and the text turns dark.
    public static void MenuStyle(Button b, int padL = 18, int padR = 40, int padY = 8)
    {
        var off = new StyleBoxFlat { BgColor = new Color(0, 0, 0, 0), DrawCenter = false, ContentMarginLeft = padL, ContentMarginRight = padR, ContentMarginTop = padY, ContentMarginBottom = padY };
        var on = GlowBar(padL, padR, padY);
        b.AddThemeStyleboxOverride("normal", off); b.AddThemeStyleboxOverride("disabled", off);
        b.AddThemeStyleboxOverride("hover", on); b.AddThemeStyleboxOverride("pressed", on); b.AddThemeStyleboxOverride("hover_pressed", on);
        b.AddThemeStyleboxOverride("focus", new StyleBoxEmpty());
        b.AddThemeColorOverride("font_color", Style.Paper);
        b.AddThemeColorOverride("font_disabled_color", new Color(Style.PaperDim, 0.4f));
        var ink = new Color(0.12f, 0.08f, 0.04f);
        b.AddThemeColorOverride("font_hover_color", ink); b.AddThemeColorOverride("font_pressed_color", ink); b.AddThemeColorOverride("font_hover_pressed_color", ink);
        b.Alignment = HorizontalAlignment.Left;
    }
    public static Button MenuItem(string text, Action onPress, int size = 30, float minWidth = 420)
    {
        var b = MinimalButton(text, onPress, size);
        MenuStyle(b);
        b.CustomMinimumSize = new Vector2(minWidth, size * 2);
        return b;
    }
    // the title screen's entrance: each line fades in a moment after the one above it
    public static void StaggerIn(Node owner, IEnumerable<Control> items, double step = 0.07)
    {
        if (App.I.S?.Settings.ReducedMotion == true) return;
        int i = 0;
        foreach (var c in items)
        {
            c.Modulate = new Color(1, 1, 1, 0);
            var tw = owner.CreateTween(); tw.TweenInterval(step * i++); tw.TweenProperty(c, "modulate:a", 1f, 0.3f);
        }
    }
    // A menu over the screen in the title screen's manner: the screen dims a little, a black fade rises from the left edge, and
    // the title and its lines stand on it, left aligned like the title screen's list; a click on the dim part closes it.
    public static Control SideMenu(string title, string subtitle, IEnumerable<(string Text, Action Act)> items)
    {
        var scrim = App.I.Modal(new Control { CustomMinimumSize = Vector2.Zero }, true, 0.3f);
        var g = new Gradient(); g.SetColor(0, new Color(0, 0, 0, 0.9f)); g.SetColor(1, new Color(0, 0, 0, 0));
        g.AddPoint(0.45f, new Color(0, 0, 0, 0.72f));
        var shade = new TextureRect { Texture = new GradientTexture2D { Gradient = g, FillFrom = new Vector2(0, 0.5f), FillTo = new Vector2(1, 0.5f), Width = 256, Height = 8 }, ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize, StretchMode = TextureRect.StretchModeEnum.Scale, MouseFilter = Control.MouseFilterEnum.Ignore };
        scrim.AddChild(shade); shade.AnchorBottom = 1; shade.OffsetRight = 900;
        var col = new VBoxContainer { MouseFilter = Control.MouseFilterEnum.Ignore }; col.AddThemeConstantOverride("separation", 14);
        scrim.AddChild(col); col.AnchorTop = col.AnchorBottom = 0.5f; col.OffsetLeft = 110; col.GrowVertical = Control.GrowDirection.Both;
        var head = Style.Text(title, 44, Style.HoverText, Style.Kai); col.AddChild(head);
        if (subtitle.Length > 0) col.AddChild(Style.Text(subtitle, 22, Style.PaperDim, Style.Kai));
        col.AddChild(Spacer(0, 28));
        var list = new VBoxContainer { MouseFilter = Control.MouseFilterEnum.Ignore }; list.AddThemeConstantOverride("separation", 16); col.AddChild(list);
        foreach (var (text, act) in items) list.AddChild(MenuItem(text, act));
        if (App.I.S?.Settings.ReducedMotion != true)
        {
            shade.Modulate = new Color(1, 1, 1, 0); scrim.CreateTween().TweenProperty(shade, "modulate:a", 1f, 0.2f);
            scrim.CreateTween().TweenProperty(col, "offset_left", 110f, 0.28f).From(80f).SetEase(Tween.EaseType.Out).SetTrans(Tween.TransitionType.Cubic);
        }
        StaggerIn(scrim, new Control[] { head }.Concat(list.GetChildren().OfType<Control>()));
        return scrim;
    }

    public static StyleBoxFlat SoftBox(float bg, bool lit, int padX = 16, int padY = 8) => SoftBox(bg, lit ? Style.Amber : new Color(0, 0, 0, 0), lit ? 2 : 0, padX, padY);

    // give any button (or option button) the soft backdrop in all its states
    public static void Soften(Button b, int padX = 16, int padY = 8, bool toggle = false)
    {
        b.AddThemeStyleboxOverride("normal", SoftBox(0.38f, false, padX, padY));
        b.AddThemeStyleboxOverride("hover", SoftBox(0.62f, true, padX, padY));
        b.AddThemeStyleboxOverride("pressed", SoftBox(0.72f, true, padX, padY));
        b.AddThemeStyleboxOverride("hover_pressed", SoftBox(0.72f, true, padX, padY));
        b.AddThemeStyleboxOverride("disabled", SoftBox(0.2f, false, padX, padY));
        b.AddThemeStyleboxOverride("focus", new StyleBoxEmpty());
    }

    // A token picture with a big card-style numeral on top (0712 web look).
    public static Control TokenNumber(string kind, int value, float height = 46)
    {
        var tex = Stat(kind); var box = new Control { MouseFilter = Control.MouseFilterEnum.Ignore };
        float w = tex is null ? height : height * tex.GetWidth() / tex.GetHeight();
        box.CustomMinimumSize = new Vector2(w, height);
        var img = Image(tex, new Vector2(w, height)); box.AddChild(img); img.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        var n = Style.Text(value.ToString(), (int)(height * 0.62f), Colors.White, Style.Symbols);
        n.AddThemeColorOverride("font_outline_color", new Color(0.05f, 0.04f, 0.03f)); n.AddThemeConstantOverride("outline_size", 7);
        box.AddChild(n); CenterDigits(n);
        return box;
    }

    // ───────── numbers in the very middle of their token (Yunke 11-19: "对于所有的token 数字都要放在token正中")
    // A label centres the font's line box (ascent + descent), not the digits: in the symbols font the digits stood low. This
    // measures the digits' own ink once per font and size and moves the label up by the difference.
    static readonly Dictionary<(ulong, int), float> digitShift = new();
    public static float DigitShift(Font f, int size)
    {
        var key = (f.GetInstanceId(), size);
        if (digitShift.TryGetValue(key, out var hit)) return hit;
        float asc = f.GetAscent(size), desc = f.GetDescent(size), top = 0, bottom = 0; bool ok = false;
        try
        {
            var ts = TextServerManager.Singleton.GetPrimaryInterface(); var sz = new Vector2I(size, 0);
            foreach (var rid in f.GetRids())
            {
                ts.FontRenderRange(rid, sz, '0', '9');
                for (char c = '0'; c <= '9'; c++)
                {
                    long gi = ts.FontGetGlyphIndex(rid, size, c, 0); if (gi == 0) continue;
                    var off = ts.FontGetGlyphOffset(rid, sz, gi); var gs = ts.FontGetGlyphSize(rid, sz, gi);
                    if (gs.Y <= 0) continue;
                    top = ok ? Math.Min(top, off.Y) : off.Y; bottom = ok ? Math.Max(bottom, off.Y + gs.Y) : off.Y + gs.Y; ok = true;
                }
                if (ok) break;
            }
        }
        catch (Exception e) { GD.PushWarning("digit metrics: " + e.Message); }
        if (!ok) { top = -asc * 0.7f; bottom = 0; }
        // the label's baseline sits (asc - desc) / 2 below its middle; the digits' middle is (top + bottom) / 2 from the baseline
        float shift = (asc - desc) / 2 + (top + bottom) / 2;
        return digitShift[key] = shift;
    }
    // fill the parent and stand the digits in its exact middle (call after adding the label to its token)
    public static Label CenterDigits(Label l)
    {
        l.HorizontalAlignment = HorizontalAlignment.Center; l.VerticalAlignment = VerticalAlignment.Center;
        l.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        if (l.GetThemeFont("font") is Font f)
        {
            int size = l.GetThemeFontSize("font_size");
            float s = DigitShift(f, size); l.OffsetTop = -s; l.OffsetBottom = -s;
            // and across: a label centres the digits' advance, not their ink — a "2" stood a little left (Yunke 11-22, clue token)
            float x = InkShiftX(f, size, l.Text); l.OffsetLeft = x; l.OffsetRight = x;
        }
        return l;
    }
    // how far the ink's middle lies left of the advance's middle (moved right by this much to centre the ink)
    static readonly Dictionary<(ulong, int, string), float> inkShiftX = new();
    static float InkShiftX(Font f, int size, string text)
    {
        if (text.Length == 0 || text.Length > 3 || !text.All(char.IsAsciiDigit)) return 0;
        var key = (f.GetInstanceId(), size, text);
        if (inkShiftX.TryGetValue(key, out var hit)) return hit;
        float shift = 0;
        try
        {
            var ts = TextServerManager.Singleton.GetPrimaryInterface(); var sz = new Vector2I(size, 0);
            foreach (var rid in f.GetRids())
            {
                ts.FontRenderRange(rid, sz, '0', '9');
                float pen = 0, left = float.MaxValue, right = float.MinValue; bool ok = true;
                foreach (char c in text)
                {
                    long gi = ts.FontGetGlyphIndex(rid, size, c, 0); if (gi == 0) { ok = false; break; }
                    var off = ts.FontGetGlyphOffset(rid, sz, gi); var gs = ts.FontGetGlyphSize(rid, sz, gi); var adv = ts.FontGetGlyphAdvance(rid, size, gi);
                    left = Math.Min(left, pen + off.X); right = Math.Max(right, pen + off.X + gs.X); pen += adv.X;
                }
                if (!ok || right <= left) continue;
                shift = pen / 2 - (left + right) / 2;
                break;
            }
        }
        catch (Exception e) { GD.PushWarning("digit ink: " + e.Message); }
        return inkShiftX[key] = Mathf.Clamp(shift, -size * 0.2f, size * 0.2f);
    }
    // A token picture in a round gold ring with its number in the middle (the location's clues, as big as its shroud roundel)
    public static Control RingToken(string kind, int value, float d, string tooltip = "")
    {
        var box = new Control { MouseFilter = string.IsNullOrEmpty(tooltip) ? Control.MouseFilterEnum.Ignore : Control.MouseFilterEnum.Pass, CustomMinimumSize = new Vector2(d, d), Size = new Vector2(d, d), TooltipText = tooltip };
        var back = new Panel { MouseFilter = Control.MouseFilterEnum.Ignore, Size = new Vector2(d, d) };
        var bsb = new StyleBoxFlat { BgColor = new Color(0.06f, 0.08f, 0.05f), AntiAliasing = true, ShadowColor = new Color(0, 0, 0, 0.5f), ShadowSize = 4 }; bsb.SetCornerRadiusAll((int)d);
        back.AddThemeStyleboxOverride("panel", bsb); box.AddChild(back);
        var img = Image(Stat(kind), new Vector2(d * 0.92f, d * 0.92f)); img.MouseFilter = Control.MouseFilterEnum.Ignore; box.AddChild(img);
        img.Size = new Vector2(d * 0.92f, d * 0.92f); img.Position = new Vector2(d * 0.04f, d * 0.04f);
        var ring = new Panel { MouseFilter = Control.MouseFilterEnum.Ignore, Size = new Vector2(d, d) };
        var rsb = new StyleBoxFlat { DrawCenter = false, BgColor = new Color(0, 0, 0, 0), BorderColor = new Color("e0b56a"), AntiAliasing = true }; rsb.SetBorderWidthAll(2); rsb.SetCornerRadiusAll((int)d);
        ring.AddThemeStyleboxOverride("panel", rsb); box.AddChild(ring);
        var n = Style.Text(value.ToString(), (int)(d * 0.6f), Colors.White, Style.Symbols);
        n.AddThemeColorOverride("font_outline_color", new Color(0.05f, 0.04f, 0.03f)); n.AddThemeConstantOverride("outline_size", 6); n.MouseFilter = Control.MouseFilterEnum.Ignore;
        box.AddChild(n); n.Size = new Vector2(d, d); CenterDigits(n);
        return box;
    }
    public static Texture2D? Ref(string rel) => Tex("res://assets/ref/" + rel + ".webp");
    public static Texture2D? Token(string face)
    {
        string f = face switch
        {
            "PlusOne" => "plus1", "Zero" => "0", "MinusOne" => "minus1", "MinusTwo" => "minus2", "MinusThree" => "minus3", "MinusFour" => "minus4",
            "MinusFive" => "minus5", "MinusSix" => "minus6", "MinusSeven" => "minus7", "MinusEight" => "minus8", "Skull" => "skull", "Cultist" => "cultist",
            "Tablet" => "tablet", "ElderThing" => "elderthing", "AutoFail" => "autofail", "ElderSign" => "eldersign", "BlessToken" => "bless", "CurseToken" => "curse",
            "FrostToken" => "frost", _ => "blank"
        };
        return Tex("res://assets/tokens/ct_" + f + ".png");
    }
    // Square face crop taken from the investigator card art.
    static readonly Dictionary<string, Texture2D?> portraits = new();
    public static Texture2D? Portrait(string investigatorCode)
    {
        investigatorCode = CoreCatalog.Normalize(investigatorCode);
        if (portraits.TryGetValue(investigatorCode, out var p)) return p;
        var card = Card(investigatorCode);
        if (card is null) return portraits[investigatorCode] = null;
        var img = card.GetImage(); if (img.IsCompressed()) img.Decompress();
        float w = img.GetWidth(), h = img.GetHeight(); int side = (int)(w * 0.25f);
        var sub = img.GetRegion(new Rect2I((int)(w * 0.175f), (int)(h * 0.25f), side, side)); sub.GenerateMipmaps();
        return portraits[investigatorCode] = ImageTexture.CreateFromImage(sub);
    }
    // The 0712 web table's investigator mini-card art (assets/ui/mini, copied from its portraits folder); falls back to the crop.
    // A small square head-and-shoulders picture of an investigator in a thin border of their class colour (Yunke 11-20 #1:
    // the deck notebook's pictures). The square is the top of the portrait mini card.
    public static Control SquareFace(string code, float size)
    {
        var root = new Control { CustomMinimumSize = new Vector2(size, size), Size = new Vector2(size, size), MouseFilter = Control.MouseFilterEnum.Ignore };
        if (Mini(code) is { } tex)
        {
            float w = tex.GetWidth(), h = tex.GetHeight(), side = Math.Min(w, h);
            var at = new AtlasTexture { Atlas = tex, Region = new Rect2((w - side) / 2, Math.Min(h - side, h * 0.03f), side, side) };
            var img = new TextureRect { Texture = at, ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize, StretchMode = TextureRect.StretchModeEnum.Scale, MouseFilter = Control.MouseFilterEnum.Ignore };
            root.AddChild(img); img.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect, Control.LayoutPresetMode.KeepSize, 2);
        }
        var rim = new Panel { MouseFilter = Control.MouseFilterEnum.Ignore };
        var sb = new StyleBoxFlat { DrawCenter = false, BorderColor = SelectionStage.FactionColor(code).Lightened(0.15f), AntiAliasing = true, ShadowColor = new Color(0, 0, 0, 0.5f), ShadowSize = 3 };
        sb.SetBorderWidthAll(2); sb.SetCornerRadiusAll(3); rim.AddThemeStyleboxOverride("panel", sb);
        root.AddChild(rim); rim.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        return root;
    }
    public static Texture2D? Mini(string code)
    {
        code = CoreCatalog.Normalize(code);
        var t = Tex("res://assets/ui/mini/" + code + ".jpg") ?? ArtStore.Portrait(code);
        if (t is not null) return t;
        // not an investigator (a story ally such as Lita Chantler, Yunke 11-20 #19: the investigator-card crop cut out her hand):
        // the card's own illustration, cut to an upright mini card's proportions round its upper middle
        if (J.String(App.I.S?.Card(code) ?? default, "type_code") is { Length: > 0 } type && type != "investigator") return ArtMini(code);
        return Portrait(code);
    }
    static readonly Dictionary<string, Texture2D?> artMinis = new();
    static Texture2D? ArtMini(string code)
    {
        if (artMinis.TryGetValue(code, out var cached)) return cached;
        Texture2D? src = Art(code); Rect2 r;
        if (src is not null) r = new Rect2(0, 0, src.GetWidth(), src.GetHeight());
        else if (Card(code) is { } card) { src = card; float w0 = card.GetWidth(), h0 = card.GetHeight(); r = new Rect2(w0 * 0.07f, h0 * 0.09f, w0 * 0.86f, h0 * 0.47f); }   // the art window of an upright card
        else return artMinis[code] = null;
        const float aspect = 0.645f;   // width / height of the mini cards
        float w = r.Size.X, h = r.Size.Y;
        Rect2 cut = w / h > aspect ? new Rect2(r.Position.X + (w - h * aspect) / 2, r.Position.Y, h * aspect, h) : new Rect2(r.Position.X, r.Position.Y, w, w / aspect);
        return artMinis[code] = new AtlasTexture { Atlas = src, Region = cut };
    }
    // The web table's slot icons (assets/ui/slots: hand, body, arcane, ally, accessory, tarot, head).
    public static Texture2D? SlotIcon(string kind) => Tex("res://assets/ui/slots/" + kind + ".png") ?? Ref("ui/icons/slot-" + kind);

    public static Texture2D? Stat(string kind) => Tex("res://assets/ui/tokens/" + kind + ".png");

    // Scaled texture so ornate source corners stay a sensible on-screen size.
    static readonly Dictionary<string, Texture2D> scaled = new();
    public static Texture2D? Scaled(string rel, float factor)
    {
        string key = rel + "@" + factor;
        if (scaled.TryGetValue(key, out var s)) return s;
        var t = Ref(rel); if (t is null) return null;
        Godot.Image img = t.GetImage(); if (img.IsCompressed()) img.Decompress();
        img.Resize(Math.Max(8, (int)(img.GetWidth() * factor)), Math.Max(8, (int)(img.GetHeight() * factor)), Godot.Image.Interpolation.Lanczos);
        img.GenerateMipmaps();
        var tex = ImageTexture.CreateFromImage(img); scaled[key] = tex; return tex;
    }

    // Simplified look (REVAMP-SUMMARY §0): plain dark panel, hairline edge, soft shadow — no brass nine-patch.
    public static StyleBox PanelBox(int pad = 22, float alpha = 0.97f)
    {
        var b = new StyleBoxFlat { BgColor = new Color(0.03f, 0.028f, 0.025f, 0.9f * alpha), BorderColor = new Color(Style.Brass, 0.16f), ShadowColor = new Color(0, 0, 0, 0.5f), ShadowSize = 18, AntiAliasing = true };
        b.SetBorderWidthAll(1); b.SetCornerRadiusAll(8); b.SetContentMarginAll(pad);
        return b;
    }
    public static StyleBox PaperBox(int padX = 26, int padY = 12)
    {
        var b = new StyleBoxFlat { BgColor = new Color(0, 0, 0, 0.5f), ContentMarginLeft = padX, ContentMarginRight = padX, ContentMarginTop = padY, ContentMarginBottom = padY };
        b.SetCornerRadiusAll(4); return b;
    }
    // Label pill on a dark fade-like background (tint < 1 dims it, > 1 brightens it).
    public static StyleBox PlaqueBox(int padX = 60, int padY = 10, Color? tint = null)
    {
        float k = tint is { } c ? (c.R + c.G + c.B) / 3f : 1f;
        var b = new StyleBoxFlat { BgColor = new Color(0.02f * k, 0.02f * k, 0.02f * k, Math.Clamp(0.6f * k, 0.25f, 0.85f)), ContentMarginLeft = Math.Min(padX, 28), ContentMarginRight = Math.Min(padX, 28), ContentMarginTop = padY, ContentMarginBottom = padY };
        if (k > 1.05f) { b.BorderColor = new Color(Style.Amber, 0.8f); b.BorderWidthBottom = 2; }
        b.SetCornerRadiusAll(20); return b;
    }

    public static PanelContainer Panel(Control content, int pad = 22, float alpha = 0.97f)
    {
        var p = new PanelContainer();
        p.AddThemeStyleboxOverride("panel", PanelBox(pad, alpha));
        p.AddChild(content); return p;
    }

    // Every button in the app uses the plain style; `primary` adds the amber underline.
    public static Button Button(string text, Action onPress, int size = 22, bool primary = false) => MinimalButton(text, onPress, size, primary);

    public static TextureRect Image(Texture2D? t, Vector2 size, bool cover = false)
    {
        var r = new TextureRect { Texture = t, ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize, StretchMode = cover ? TextureRect.StretchModeEnum.KeepAspectCovered : TextureRect.StretchModeEnum.KeepAspectCentered, CustomMinimumSize = size, MouseFilter = Control.MouseFilterEnum.Ignore };
        r.TextureFilter = CanvasItem.TextureFilterEnum.LinearWithMipmapsAnisotropic;
        return r;
    }

    public static Label Glyph(string key, int size, Color color)
    {
        var l = Style.Text(Style.Glyph.TryGetValue(key, out var g) ? g : "?", size, color, Style.Symbols);
        return l;
    }

    public static Control Spacer(float w = 0, float h = 0, bool expand = false)
    {
        var c = new Control { CustomMinimumSize = new Vector2(w, h), MouseFilter = Control.MouseFilterEnum.Ignore };
        if (expand) { c.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill; c.SizeFlagsVertical = Control.SizeFlags.ExpandFill; }
        return c;
    }

    public static void Clear(Node n) { foreach (var c in n.GetChildren()) { n.RemoveChild(c); c.QueueFree(); } }

    public static ScrollContainer Scroll(Control content, bool horizontal = false)
    {
        var s = new ScrollContainer { HorizontalScrollMode = horizontal ? ScrollContainer.ScrollMode.Auto : ScrollContainer.ScrollMode.Disabled, SizeFlagsVertical = Control.SizeFlags.ExpandFill, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        content.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill; s.AddChild(content); return s;
    }
}

// Audio is optional (this machine falls back to a dummy driver); calls are safe no-ops.
public static class Sfx
{
    static AudioStreamPlayer? player;
    public static void Attach(Node root) { player = new AudioStreamPlayer { VolumeDb = -14 }; root.AddChild(player); GameAudio.Attach(root); }   // noncore audio 11-19: GameAudio (app/Scripts/Audio)
    public static void Click() => GameAudio.Cue("click");
}
