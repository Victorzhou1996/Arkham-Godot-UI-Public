using Godot;
using Gathering;

namespace ArkhamUI;

// Cropped card art (CPU crop, cached) for immersive display instead of whole cards.
public static class Art
{
    static readonly Dictionary<string, Texture2D?> cache = new();
    public static Texture2D? Crop(string code, string kind)
    {
        code = CoreCatalog.Normalize(code); string key = code + "/" + kind;
        if (cache.TryGetValue(key, out var t)) return t;
        var tex = Ui.Card(code); if (tex is null) return cache[key] = null;
        var img = tex.GetImage(); if (img.IsCompressed()) img.Decompress();
        float w = img.GetWidth(), h = img.GetHeight();
        Rect2 r = kind switch
        {
            "enemy" => new Rect2(w * 0.22f, h * 0.60f, w * 0.56f, w * 0.56f),
            "player" => new Rect2(w * 0.18f, h * 0.10f, w * 0.64f, h * 0.34f),
            "location" => new Rect2(w * 0.10f, h * 0.10f, w * 0.80f, h * 0.36f),
            "treachery" => new Rect2(w * 0.20f, h * 0.10f, w * 0.60f, w * 0.60f),
            // landscape scenario cards: the agenda's picture is the left part, the act's the right part (above the footer)
            "agenda" => new Rect2(0, 0, w * 0.39f, h * 0.93f),
            "act" => new Rect2(w * 0.62f, 0, w * 0.38f, h * 0.93f),
            _ => new Rect2(0, 0, w, h)
        };
        var sub = img.GetRegion(new Rect2I((int)r.Position.X, (int)r.Position.Y, (int)r.Size.X, (int)r.Size.Y));
        sub.GenerateMipmaps();
        return cache[key] = ImageTexture.CreateFromImage(sub);
    }

    static Shader? circle;
    public static Shader Circle => circle ??= new Shader { Code = @"shader_type canvas_item;
uniform vec4 ring : source_color = vec4(0.88,0.71,0.42,1.0); uniform float ring_w = 0.06; uniform float dim = 0.0;
void fragment(){ vec4 c = texture(TEXTURE, UV); float d = distance(UV, vec2(0.5));
  float inside = 1.0 - smoothstep(0.495 - ring_w, 0.5 - ring_w, d);
  float band = (1.0 - smoothstep(0.49, 0.5, d)) * (1.0 - inside);
  vec3 col = mix(c.rgb * (1.0 - dim), ring.rgb, band);
  COLOR = vec4(col, max(inside * c.a, band * ring.a)); }" };
}

// Round art medallion (enemy, portrait, asset) with hover zoom of the full card and click/act behaviour.
public partial class Medallion : Control
{
    public string Code = "", Kind = "enemy", Key = "";
    public Color Ring = Style.Brass;
    public bool Actionable, Dim;
    public bool NoZoom;   // a marker on a painted map: the place's own info box shows instead of the card zoom
    public Action? OnClick;
    public Texture2D? Picture;
    TextureRect img = null!;
    ShaderMaterial mat = null!;

    public override void _Ready()
    {
        MouseFilter = MouseFilterEnum.Stop; MouseDefaultCursorShape = CursorShape.PointingHand;
        img = new TextureRect { Texture = Picture ?? Art.Crop(Code, Kind), ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize, StretchMode = TextureRect.StretchModeEnum.KeepAspectCovered, MouseFilter = MouseFilterEnum.Ignore, TextureFilter = TextureFilterEnum.LinearWithMipmapsAnisotropic };
        mat = new ShaderMaterial { Shader = Art.Circle };
        mat.SetShaderParameter("ring", Actionable ? new Color("e0b56a") : Ring);
        mat.SetShaderParameter("ring_w", Actionable ? 0.075f : 0.055f);
        mat.SetShaderParameter("dim", Dim ? 0.55f : 0f);
        if (Actionable)
        {
            // a marker that can be acted on glows behind its ring and breathes, like every other clickable thing (Yunke 11-18 #6)
            var glow = new Panel { MouseFilter = MouseFilterEnum.Ignore, ShowBehindParent = false };
            var gsb = new StyleBoxFlat { BgColor = new Color(Style.Amber, 0.25f), ShadowColor = new Color(Style.Amber, 0.85f), ShadowSize = (int)Math.Max(8, Size.X * 0.22f) };
            gsb.SetCornerRadiusAll((int)Math.Max(Size.X, CustomMinimumSize.X)); glow.AddThemeStyleboxOverride("panel", gsb);
            AddChild(glow); glow.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
            if (App.I.S?.Settings.ReducedMotion != true) { var gt = glow.CreateTween().SetLoops(); gt.TweenProperty(glow, "modulate:a", 0.3f, 0.8f).SetTrans(Tween.TransitionType.Sine); gt.TweenProperty(glow, "modulate:a", 1f, 0.8f).SetTrans(Tween.TransitionType.Sine); }
        }
        img.Material = mat; AddChild(img); img.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        PivotOffset = Size / 2;
        if (Actionable && App.I.S?.Settings.ReducedMotion != true)
        { var tw = CreateTween().SetLoops(); tw.TweenProperty(this, "scale", new Vector2(1.04f, 1.04f), 0.9f); tw.TweenProperty(this, "scale", Vector2.One, 0.9f); }
    }

    public override void _GuiInput(InputEvent e)
    {
        if (e is InputEventMouseButton { Pressed: true } mb)
        {
            Zoom.Hide();
            if (mb.ButtonIndex == MouseButton.Right && Code.Length > 0) { CardReader.Open(Code, printed: true); AcceptEvent(); }
            else if (mb.ButtonIndex == MouseButton.Left) { if (OnClick is not null) OnClick(); else if (Code.Length > 0) CardReader.Open(Code); AcceptEvent(); }
        }
    }
    public override void _Notification(int what)
    {
        if (what == NotificationMouseEnter) { if (!NoZoom) Zoom.Show(this, Code, 560); mat?.SetShaderParameter("ring", new Color("fff1d5")); }
        else if (what == NotificationMouseExit) { Zoom.Hide(); mat?.SetShaderParameter("ring", Actionable ? new Color("e0b56a") : Ring); }
        else if (what == NotificationExitTree) Zoom.Hide();
    }
}

// Shared hover help: now the delayed text box (Peek), never the printed card image.
public static class Zoom
{
    public static void Show(Control anchor, string code, float height) => Peek.ShowCard(anchor, code);
    public static void Hide() => Peek.Hide();
}
