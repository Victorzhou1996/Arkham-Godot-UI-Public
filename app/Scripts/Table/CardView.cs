using Godot;
using Gathering;
using System.Text.Json;

namespace ArkhamUI;

// Core-set player card costs / skill icons, extracted read-only from the rules source (tools/extract_core_costs.py).
public static class CoreCosts
{
    static Dictionary<string, (int? Cost, string[] Skills)>? data;
    static Dictionary<string, (int? Cost, string[] Skills)> Data
    {
        get
        {
            if (data is not null) return data;
            data = new();
            // core-costs.json plus each later cycle's <stem>-costs.json (dwl-costs.json)
            foreach (var file in System.IO.Directory.GetFiles(System.IO.Path.Combine(App.I.Assets, "data"), "*-costs.json"))
            try
            {
                using var doc = JsonDocument.Parse(System.IO.File.ReadAllText(file));
                foreach (var p in doc.RootElement.EnumerateObject())
                {
                    if (p.Name.StartsWith("_")) continue;
                    var c = p.Value.GetProperty("cost");
                    data[p.Name] = (c.ValueKind == JsonValueKind.Number ? c.GetInt32() : null, p.Value.GetProperty("skills").EnumerateArray().Select(x => x.GetString() ?? "").ToArray());
                }
            }
            catch (Exception e) { GD.PushWarning(file + ": " + e.Message); }
            return data;
        }
    }
    public static int? Cost(string code) => Data.TryGetValue(CoreCatalog.Normalize(code), out var d) ? d.Cost : null;
    public static string[] Skills(string code) => Data.TryGetValue(CoreCatalog.Normalize(code), out var d) ? d.Skills : Array.Empty<string>();
}

// One card on the table, drawn in the redrawn style: the painted illustration fills the card, the name sits on a
// dark fade at the bottom, the cost is a roundel on the frame. Gold frame = actionable now. Hovering for a moment
// shows the rules text box. (The deck workshop sets Redrawn = false to keep the printed card.)
public partial class CardView : Control
{
    public string Code = "";
    public string Key = "";          // anchor key, e.g. "hand:<id>", "enemy:<id>"
    public bool Actionable;
    public bool Exhausted;
    public bool FaceDown;
    public string Caption = "";
    public Action? OnClick;
    public Func<Vector2, bool>? OnDragRelease;   // returns true when dropped on a valid target
    public bool Draggable;
    public (string Kind, int Value)[] Badges = Array.Empty<(string, int)>();
    public Texture2D? Override;
    public Texture2D? Picture;      // redrawn face art to use instead of the card's own illustration
    public bool HoverZoom = true;
    public bool PassClicks;          // a marker on a map: clicks with no OnClick fall through to what is under it
    public float ZoomHeight = 600;
    public float Lift;               // hand cards rise by this much under the pointer
    // redrawn style
    public bool Redrawn = true, ShowName = true, ShowCost = true;
    public Color? Rim;                               // e.g. red for an engaged enemy
    public Color? Glow;                              // the actionable glow's colour (red while damage is assigned, r10)
    public (int Fight, int Health, int Evade)? EnemyStats;   // plaque inside the top edge
    public bool HealthHurt;                          // health shown in red when damaged
    public Func<Control?>? PeekExtra;                // extra block in the hover text box
    public Action<bool>? HoverChanged;
    public Action<bool>? DragChanged;                // drag started / ended (drop-zone feedback)
    public bool ShowSkills;                          // skill icons on the frame edge (commit step)
    public int? CostShown;                           // the cost the rules core charges now, when it differs from the printed one
    float restY = float.NaN; Tween? liftTween;
    float restRot;

    TextureRect face = null!;
    Panel frame = null!;
    bool hovering, pressed, dragging;
    Vector2 pressAt, grabOffset, homePos;
    Control? zoom;
    StyleBoxFlat normalFrame = null!, hotFrame = null!;
    static ShaderMaterial? nameFade;
    static ShaderMaterial NameFade => nameFade ??= new ShaderMaterial { Shader = new Shader { Code = @"shader_type canvas_item;
void fragment(){ float t = smoothstep(0.0, 0.72, UV.y); COLOR = vec4(COLOR.rgb, COLOR.a * t * t * (3.0 - 2.0 * t)); }" } };

    // the card's class (Yunke 11-19 r11 #3): its frame and the band behind its name take the class colour, as the printed
    // card's border does. Neutral, story and encounter cards keep the plain frame.
    public static Color? ClassColor(string code)
    {
        if (code.Length == 0 || App.I.S is not { } s) return null;
        var c = s.Card(CoreCatalog.Normalize(code));
        if (J.String(c, "encounter_code").Length > 0 || J.String(c, "subtype_code") is "weakness" or "basicweakness") return null;
        return J.String(c, "faction_code") switch
        {
            "guardian" => new Color("4f86d0"), "seeker" => new Color("d6923e"), "rogue" => new Color("4fa86a"),
            "mystic" => new Color("9068cc"), "survivor" => new Color("cc5454"), _ => (Color?)null,
        };
    }

    static StyleBoxFlat Frame(Color c, int w, int radius, bool glow) { var b = new StyleBoxFlat { BgColor = new Color(0, 0, 0, 0), BorderColor = c, DrawCenter = false, AntiAliasing = true }; b.SetBorderWidthAll(w); b.SetCornerRadiusAll(radius); b.ShadowColor = new Color(c, glow ? 0.85f : 0.5f); b.ShadowSize = glow ? 22 : 0; return b; }   // a card that can act casts a wide, bright glow (Yunke 11-18 #6: the old rim was too faint)

    // An investigator as the redrawn portrait small card (assets/ui/mini), always at that picture's own proportions and never
    // stretched by the container it sits in (Yunke 11-18: on the campaign's end screen a column stretched it square and the
    // figure slid out of the frame). Height in, width from the picture.
    public static CardView PortraitCard(string code, float height, bool actionable = false)
    {
        var tex = Ui.Mini(code);
        float w = MathF.Round(height * (tex is null ? 0.645f : tex.GetWidth() / (float)Math.Max(1, tex.GetHeight())));
        return new CardView { Code = code, Picture = tex, CustomMinimumSize = new Vector2(w, height), Size = new Vector2(w, height), ShowCost = false, HoverZoom = false, Actionable = actionable,
            SizeFlagsHorizontal = SizeFlags.ShrinkCenter, SizeFlagsVertical = SizeFlags.ShrinkCenter };
    }

    // a place on the table not yet revealed: drawn by its unrevealed side only — picture, name, zoom (local-rclick-location: in
    // a picker or target box it showed the revealed side). A caller's own Picture / Override is left alone.
    bool HiddenPlace => !FaceDown && Override is null && Picture is null && Code.Length > 0 && CardReader.IsUnrevealed(Code);
    static Texture2D? UnrevealedArt(string code) => CardReader.UnrevealedArt(code);

    public override void _Ready()
    {
        MouseFilter = PassClicks ? MouseFilterEnum.Pass : MouseFilterEnum.Stop;
        MouseDefaultCursorShape = OnClick is not null || Draggable ? CursorShape.PointingHand : CursorShape.Arrow;
        PivotOffset = Size / 2;
        int radius = (int)Math.Clamp(Size.X * 0.075f, 5, 12);
        if (Redrawn && !FaceDown && Override is null) BuildRedrawn(radius);
        else
        {
            face = new TextureRect { ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize, StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered, MouseFilter = MouseFilterEnum.Ignore, TextureFilter = TextureFilterEnum.LinearWithMipmapsAnisotropic };
            face.Texture = Override ?? (FaceDown ? Ui.CardBack(false) : HiddenPlace ? CardReader.UnrevealedPicture(Code) ?? UnrevealedArt(Code) : Ui.Card(Code));
            // clipped to the frame's rounded corners (Yunke 11-20: the printed card's corners stuck out of the treachery reveal's frame)
            var pclip = new Panel { MouseFilter = MouseFilterEnum.Ignore, ClipChildren = ClipChildrenMode.Only };
            var pmask = new StyleBoxFlat { BgColor = Colors.White, AntiAliasing = true }; pmask.SetCornerRadiusAll(radius); pclip.AddThemeStyleboxOverride("panel", pmask);
            AddChild(pclip); pclip.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
            pclip.AddChild(face); face.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        }
        var cls = FaceDown ? null : ClassColor(Code);
        var rest = Rim ?? (Actionable ? new Color("e0b56a") : cls is { } cc ? new Color(cc, 0.95f) : new Color(0.75f, 0.68f, 0.52f, 0.7f));
        normalFrame = Frame(Actionable ? Glow ?? new Color("ffcf6e") : rest, Actionable ? 3 : 2, radius, Actionable);
        hotFrame = Frame(Actionable ? (Glow is { } g ? g.Lightened(0.35f) : new Color("fff1d5")) : rest.Lightened(0.25f), Actionable ? 4 : 2, radius, Actionable);
        frame = new Panel { MouseFilter = MouseFilterEnum.Ignore }; AddChild(frame); frame.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        frame.AddThemeStyleboxOverride("panel", normalFrame);
        if (Redrawn && ShowCost && !FaceDown && (CostShown ?? CoreCosts.Cost(Code)) is int cost)
        {
            var roundel = CostRoundel(cost, Math.Clamp(Size.X * 0.21f, 20, 34)); AddChild(roundel);
            // changed by an effect (a customization, a card in play): green when cheaper, red when dearer, the printed cost in the tooltip
            if (CostShown is int shown && CoreCosts.Cost(Code) is int printed && shown != printed && roundel.GetChild(0) is Label n)
            { n.AddThemeColorOverride("font_color", shown < printed ? new Color("9be08a") : new Color("ff8a7a")); roundel.MouseFilter = MouseFilterEnum.Pass; roundel.TooltipText = "费用 " + shown + "（印刷 " + printed + "）"; }
        }
        if (Redrawn && !FaceDown) AddLevelPips(Redrawn && ShowCost && (CostShown ?? CoreCosts.Cost(Code)) is int);
        if (Redrawn && ShowSkills && !FaceDown) AddSkillIcons();
        if (Exhausted) { RotationDegrees = 90; }
        var badges = new HBoxContainer { MouseFilter = MouseFilterEnum.Ignore }; badges.AddThemeConstantOverride("separation", 4);
        AddChild(badges); badges.AnchorTop = badges.AnchorBottom = 1; badges.OffsetTop = -40; badges.OffsetLeft = 6;
        foreach (var (kind, value) in Badges) if (value > 0) { var bb = Badge(kind, value, 34); if (kind == "doom") bb.SetMeta("doomBadge", true); badges.AddChild(bb); }
        if (Caption.Length > 0)
        {
            var cap = Style.Text(Caption, 17, Style.HoverText); cap.HorizontalAlignment = HorizontalAlignment.Center;
            var bg = new PanelContainer { MouseFilter = MouseFilterEnum.Ignore }; bg.AddThemeStyleboxOverride("panel", Style.Box(new Color(0, 0, 0, 0.75f), new Color(0, 0, 0, 0), 0, 4, 6, 2));
            bg.AddChild(cap); AddChild(bg); bg.AnchorRight = 1; bg.AnchorTop = bg.AnchorBottom = 1; bg.OffsetTop = 4; bg.OffsetBottom = 32;
        }
        if (Actionable && App.I.S?.Settings.ReducedMotion != true)
        {
            var tw = CreateTween().SetLoops(); tw.TweenProperty(frame, "modulate:a", 0.35f, 0.8f).SetTrans(Tween.TransitionType.Sine); tw.TweenProperty(frame, "modulate:a", 1f, 0.8f).SetTrans(Tween.TransitionType.Sine);
        }
    }

    // a player card without a redrawn picture: its illustration cut from the printed card (DeckPreview), so every small card
    // shows a picture and not a shrunken printed card (Yunke 11-19: all small cards)
    static Texture2D? CroppedArt(string code, float aspect)
    {
        if (code.Length == 0 || App.I.S is not { } s) return null;
        var card = s.Card(CoreCatalog.Normalize(code));
        // an encounter enemy of a later cycle is not in the game's catalog yet; art-focus.json lists it (only enemies are
        // added beyond the catalog), so its art is cut the same way, ready for when its scenarios come
        if (card.ValueKind != System.Text.Json.JsonValueKind.Object) return DeckPreview.HasFocus(code) ? DeckPreview.ArtFor(code, "enemy", aspect) : null;
        string type = J.String(card, "type_code");
        // encounter cards keep the printed card, except enemies: their art (the lower part) cut around the monster
        // (art-focus.json, GPU subject detection; Yunke 11-19)
        if (J.String(card, "encounter_code").Length > 0 && type != "enemy" || type is not ("asset" or "event" or "skill" or "treachery" or "enemy")) return null;
        return DeckPreview.ArtFor(code, type, aspect);
    }

    // A wide fallback illustration whose subject is off centre: the small card's window is cut around this x (fraction of the
    // picture's width) instead of the middle (Yunke 11-22: “爵士”马利根's face was cut off at the left edge)
    static readonly Dictionary<string, float> FallbackFocusX = new() { ["02060"] = 0.33f };
    static Texture2D? Focused(Texture2D? art, string code, float aspect)
    {
        if (art is null || aspect <= 0 || !FallbackFocusX.TryGetValue(CoreCatalog.Normalize(code), out float fx)) return art;
        float w = art.GetWidth(), h = art.GetHeight();
        if (w / h <= aspect) return art;
        float cw = h * aspect, x = Math.Clamp(fx * w - cw / 2, 0, w - cw);
        return new AtlasTexture { Atlas = art, Region = new Rect2(x, 0, cw, h) };
    }

    void BuildRedrawn(int radius)
    {
        // rounded clip so the art follows the frame's corners
        var clip = new Panel { MouseFilter = MouseFilterEnum.Ignore, ClipChildren = ClipChildrenMode.Only };
        var mask = new StyleBoxFlat { BgColor = Colors.White }; mask.SetCornerRadiusAll(radius); clip.AddThemeStyleboxOverride("panel", mask);
        AddChild(clip); clip.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        face = new TextureRect { Texture = HiddenPlace ? UnrevealedArt(Code) : Picture ?? Focused(Ui.Art(CoreCatalog.Normalize(Code)), Code, Size.Y > 0 ? Size.X / Size.Y : 0.7f) ?? CroppedArt(Code, Size.Y > 0 ? Size.X / Size.Y : 0.7f) ?? Ui.Card(Code), ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize, StretchMode = TextureRect.StretchModeEnum.KeepAspectCovered, MouseFilter = MouseFilterEnum.Ignore, TextureFilter = TextureFilterEnum.LinearWithMipmapsAnisotropic };
        clip.AddChild(face); face.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        if (ShowName)
        {
            // The fade behind the name (keep it on every small card, playtest 2 #7): one rect whose alpha is computed per pixel,
            // smooth from clear to dark. The earlier thin strips left faint seams and a stepped edge, worst on the tilted hand
            // cards (Yunke 11-18: "小卡底部的黑色渐变有点奇怪"); a shader has no seams and always draws.
            float bandH = Math.Max(36, Size.Y * 0.42f);
            // tinted with the card's class colour (r11 #3)
            var bandColor = ClassColor(Code) is { } fc ? new Color(0.03f, 0.025f, 0.02f).Lerp(fc.Darkened(0.35f), 0.55f) : new Color(0.03f, 0.025f, 0.02f);
            var band = new ColorRect { Color = new Color(bandColor, 0.92f), MouseFilter = MouseFilterEnum.Ignore, Material = NameFade };
            clip.AddChild(band); band.AnchorTop = band.AnchorBottom = 1; band.AnchorRight = 1; band.OffsetTop = -bandH;
            int fs = (int)Math.Clamp(Size.Y * 0.1f, 11, 20);
            var name = Style.Text(HiddenPlace ? CardReader.ShownName(Code) : App.I.S?.Name(Code) ?? "", fs, Style.Paper, Style.Kai);
            while (fs > 10 && name.GetMinimumSize().X > Size.X - 12) { fs--; name.AddThemeFontSizeOverride("font_size", fs); }
            name.HorizontalAlignment = HorizontalAlignment.Center; name.VerticalAlignment = VerticalAlignment.Center; name.ClipText = true;
            clip.AddChild(name); name.AnchorTop = name.AnchorBottom = 1; name.AnchorRight = 1; name.OffsetTop = -fs * 1.9f; name.OffsetBottom = -fs * 0.3f;
        }
        if (EnemyStats is { } st) clip.AddChild(EnemyPlaque(st));
    }

    // Fight · health · evade on a dark plaque inside the top edge (Yunke's pick: option 3).
    Control EnemyPlaque((int Fight, int Health, int Evade) st)
    {
        float m = Math.Max(5, Size.X * 0.045f), h = Math.Max(24, Size.Y * 0.14f);
        var plate = new PanelContainer { MouseFilter = MouseFilterEnum.Ignore };
        var sb = new StyleBoxFlat { BgColor = new Color(0.06f, 0.055f, 0.045f, 0.88f), BorderColor = new Color(Style.Brass, 0.75f) }; sb.SetBorderWidthAll(1); sb.SetCornerRadiusAll(6);
        plate.AddThemeStyleboxOverride("panel", sb);
        plate.Position = new Vector2(m, m); plate.Size = new Vector2(Size.X - 2 * m, h); plate.CustomMinimumSize = plate.Size;
        var row = new HBoxContainer { Alignment = BoxContainer.AlignmentMode.Center, MouseFilter = MouseFilterEnum.Ignore }; row.AddThemeConstantOverride("separation", 0); plate.AddChild(row);
        int fs = (int)Math.Clamp(h * 0.7f, 14, 26);
        Control Cell(Control icon, int v, Color numColor)
        {
            var c = new HBoxContainer { Alignment = BoxContainer.AlignmentMode.Center, SizeFlagsHorizontal = SizeFlags.ExpandFill, MouseFilter = MouseFilterEnum.Ignore }; c.AddThemeConstantOverride("separation", 3);
            c.AddChild(icon); var n = Style.Text(v < 0 ? "-" : v.ToString(), fs, numColor, Style.Symbols); n.VerticalAlignment = VerticalAlignment.Center; c.AddChild(n);
            return c;
        }
        var g1 = Ui.Glyph("combat", (int)(fs * 0.8f), Style.Combat); g1.VerticalAlignment = VerticalAlignment.Center;
        var heart = Ui.Image(Ui.Ref("ui/icons/damage"), new Vector2(fs * 0.85f, fs * 0.85f)); heart.SizeFlagsVertical = SizeFlags.ShrinkCenter;
        var g3 = Ui.Glyph("agility", (int)(fs * 0.8f), Style.Agility); g3.VerticalAlignment = VerticalAlignment.Center;
        row.AddChild(Cell(g1, st.Fight, Style.HoverText));
        row.AddChild(new ColorRect { Color = new Color(Style.Brass, 0.45f), CustomMinimumSize = new Vector2(1, h * 0.55f), SizeFlagsVertical = SizeFlags.ShrinkCenter, MouseFilter = MouseFilterEnum.Ignore });
        row.AddChild(Cell(heart, st.Health, HealthHurt ? Style.Danger : Style.HoverText));
        row.AddChild(new ColorRect { Color = new Color(Style.Brass, 0.45f), CustomMinimumSize = new Vector2(1, h * 0.55f), SizeFlagsVertical = SizeFlags.ShrinkCenter, MouseFilter = MouseFilterEnum.Ignore });
        row.AddChild(Cell(g3, st.Evade, Style.HoverText));
        return plate;
    }

    // The card's level as the printed card shows it — pips, one per level — on a dark pill along the top edge, right of the cost
    // (Yunke 11-20: a 2-level 巡警 looked the same as the level-0 one in the hand). Level 0 shows nothing.
    void AddLevelPips(bool afterCost)
    {
        if (App.I.S is not { } s) return;
        var card = s.Card(CoreCatalog.Normalize(Code));
        if (card.ValueKind != System.Text.Json.JsonValueKind.Object || J.String(card, "encounter_code").Length > 0) return;
        int xp = J.Int(card, "xp"); if (xp <= 0) return;
        float costD = Math.Clamp(Size.X * 0.21f, 20, 34), dot = Math.Clamp(Size.X * 0.07f, 6, 10);
        var pill = new PanelContainer { MouseFilter = MouseFilterEnum.Pass, TooltipText = "等级 " + xp };
        var sb = new StyleBoxFlat { BgColor = new Color(0.05f, 0.04f, 0.03f, 0.88f), BorderColor = new Color("e0b56a", 0.85f), AntiAliasing = true,
            ContentMarginLeft = dot * 0.6f, ContentMarginRight = dot * 0.6f, ContentMarginTop = dot * 0.45f, ContentMarginBottom = dot * 0.45f };
        sb.SetBorderWidthAll(1); sb.SetCornerRadiusAll((int)dot); sb.ShadowColor = new Color(0, 0, 0, 0.5f); sb.ShadowSize = 3;
        pill.AddThemeStyleboxOverride("panel", sb);
        var row = new HBoxContainer { MouseFilter = MouseFilterEnum.Ignore }; row.AddThemeConstantOverride("separation", (int)Math.Max(2, dot * 0.35f)); pill.AddChild(row);
        for (int i = 0; i < Math.Min(xp, 5); i++)
        {
            var p = new Panel { MouseFilter = MouseFilterEnum.Ignore, CustomMinimumSize = new Vector2(dot, dot) };
            var ps = new StyleBoxFlat { BgColor = new Color("fff3d6"), AntiAliasing = true }; ps.SetCornerRadiusAll((int)dot);
            p.AddThemeStyleboxOverride("panel", ps); row.AddChild(p);
        }
        AddChild(pill);
        pill.Position = afterCost ? new Vector2(costD * 0.78f + 3, -dot * 0.3f) : new Vector2(4, 4);
    }

    static Control CostRoundel(int cost, float d)
    {
        var p = new Panel { MouseFilter = MouseFilterEnum.Ignore, Size = new Vector2(d, d), Position = new Vector2(-d * 0.22f, -d * 0.22f) };
        var sb = new StyleBoxFlat { BgColor = new Color(0.14f, 0.2f, 0.36f), BorderColor = new Color("e0b56a"), AntiAliasing = true }; sb.SetBorderWidthAll(2); sb.SetCornerRadiusAll((int)d);
        sb.ShadowColor = new Color(0, 0, 0, 0.5f); sb.ShadowSize = 4;
        p.AddThemeStyleboxOverride("panel", sb);
        var n = Style.Text(cost.ToString(), (int)(d * 0.62f), Colors.White, Style.Symbols);
        p.AddChild(n); Ui.CenterDigits(n);
        return p;
    }

    // the icons it commits to a test: a row just inside the card's top edge, right of the cost roundel (Yunke 11-19 r9 #4)
    void AddSkillIcons()
    {
        var skills = CoreCosts.Skills(Code); if (skills.Length == 0) return;
        // down the left edge under the cost, as tabs on the printed card (Yunke 11-19 r11 #2): each a box in the skill's colour
        // fixed to the edge, its icon in white
        float d = Math.Clamp(Size.X * 0.17f, 18, 26);
        var row = new VBoxContainer { MouseFilter = MouseFilterEnum.Ignore }; row.AddThemeConstantOverride("separation", 2);
        float costD = Math.Clamp(Size.X * 0.21f, 20, 34);
        AddChild(row); row.Position = new Vector2(0, costD * 0.78f + 4);
        foreach (var s in skills)
        {
            var p = new Panel { MouseFilter = MouseFilterEnum.Ignore, CustomMinimumSize = new Vector2(d + 2, d) };
            var color = s switch { "willpower" => Style.Willpower, "intellect" => Style.Intellect, "combat" => Style.Combat, "agility" => Style.Agility, _ => new Color("b8b0a0") };
            var sb = new StyleBoxFlat { BgColor = new Color(color.Darkened(0.45f), 0.95f), BorderColor = new Color(color.Lightened(0.2f), 0.9f), AntiAliasing = true };
            sb.BorderWidthTop = sb.BorderWidthBottom = sb.BorderWidthRight = 1; sb.CornerRadiusTopRight = sb.CornerRadiusBottomRight = (int)(d / 2.4f);
            sb.ShadowColor = new Color(0, 0, 0, 0.45f); sb.ShadowSize = 2;
            p.AddThemeStyleboxOverride("panel", sb);
            var g = Ui.Glyph(s, (int)(d * 0.66f), Colors.White); g.HorizontalAlignment = HorizontalAlignment.Center; g.VerticalAlignment = VerticalAlignment.Center;
            p.AddChild(g); g.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
            row.AddChild(p);
        }
    }

    public static Control Badge(string kind, int value, int size)
    {
        var box = new Control { CustomMinimumSize = new Vector2(size, size), MouseFilter = MouseFilterEnum.Ignore };
        var icon = Ui.Image(Ui.Stat(kind), new Vector2(size, size)); box.AddChild(icon);
        var n = Style.Text(value.ToString(), (int)(size * 0.58f), Colors.White, Style.Symbols ?? Style.Sans);
        n.AddThemeConstantOverride("outline_size", 6); n.AddThemeColorOverride("font_outline_color", new Color(0, 0, 0, 0.95f));
        box.AddChild(n); Ui.CenterDigits(n);
        // the doom roundel's number sat a little low in its circle (Yunke 11-21: "毁灭标记上的数字有点歪")
        if (kind == "doom") { float up = MathF.Round(size * 0.08f); n.OffsetTop -= up; n.OffsetBottom -= up; }
        return box;
    }

    public override void _GuiInput(InputEvent e)
    {
        if (e is InputEventMouseButton mb)
        {
            if (mb.ButtonIndex == MouseButton.Right && mb.Pressed) { CloseZoom(); if (!FaceDown && Code.Length > 0) CardReader.Open(Code, printed: true); AcceptEvent(); return; }   // the printed card, large, in either art mode (Yunke 11-22)
            if (mb.ButtonIndex != MouseButton.Left) return;
            if (PassClicks && OnClick is null) return;
            if (mb.Pressed) { pressed = true; pressAt = mb.GlobalPosition; grabOffset = mb.GlobalPosition - GlobalPosition; homePos = float.IsNaN(restY) ? Position : new Vector2(Position.X, restY); CloseZoom(); AcceptEvent(); }
            else
            {
                AcceptEvent();
                if (dragging)
                {
                    dragging = false; ZIndex = 0; DragChanged?.Invoke(false);
                    bool ok = OnDragRelease?.Invoke(mb.GlobalPosition) ?? false;
                    if (ok) { var tw = CreateTween().SetParallel(); tw.TweenProperty(this, "position:y", Position.Y - 60, 0.16f); tw.TweenProperty(this, "scale", new Vector2(0.7f, 0.7f), 0.16f); tw.TweenProperty(this, "modulate:a", 0f, 0.16f); }
                    else
                    {
                        // back to the hand with a small shake
                        var tw = CreateTween(); tw.TweenProperty(this, "position", homePos, 0.16f).SetTrans(Tween.TransitionType.Cubic).SetEase(Tween.EaseType.Out);
                        tw.Parallel().TweenProperty(this, "rotation_degrees", restRot, 0.16f); tw.Parallel().TweenProperty(this, "scale", Vector2.One, 0.16f);
                        if (App.I.S?.Settings.ReducedMotion != true) { tw.TweenProperty(this, "position:x", homePos.X - 6, 0.05f); tw.TweenProperty(this, "position:x", homePos.X + 6, 0.05f); tw.TweenProperty(this, "position:x", homePos.X, 0.05f); }
                    }
                }
                else if (pressed)
                {
                    CloseZoom();
                    if (OnClick is not null) OnClick();
                    else if (!FaceDown && Code.Length > 0 && !Redrawn) CardReader.Open(Code);
                }
                pressed = false;
            }
        }
        else if (e is InputEventMouseMotion mm && pressed && Draggable)
        {
            if (!dragging && mm.GlobalPosition.DistanceTo(pressAt) > 10) { dragging = true; liftTween?.Kill(); CloseZoom(); ZIndex = 50; Scale = new Vector2(1.08f, 1.08f); RotationDegrees = 0; grabOffset = mm.GlobalPosition - GlobalPosition; DragChanged?.Invoke(true); }
            // The card follows the pointer at the exact point where it was grabbed.
            if (dragging) GlobalPosition = mm.GlobalPosition - grabOffset;
        }
    }

    public bool IsDragging => dragging;

    public override void _Notification(int what)
    {
        if (what == NotificationMouseEnter)
        {
            hovering = true; if (frame is null) return; if (!dragging) OpenZoom(); frame.AddThemeStyleboxOverride("panel", hotFrame);
            if (Lift > 0 && !dragging) { if (float.IsNaN(restY)) { restY = Position.Y; restRot = RotationDegrees; } liftTween?.Kill(); liftTween = CreateTween().SetParallel(); liftTween.TweenProperty(this, "position:y", restY - Lift, 0.12f); liftTween.TweenProperty(this, "rotation_degrees", 0f, 0.12f); ZIndex = 20; }
            HoverChanged?.Invoke(true);
        }
        else if (what == NotificationMouseExit)
        {
            hovering = false; CloseZoom(); frame?.AddThemeStyleboxOverride("panel", normalFrame);
            if (Lift > 0 && !dragging && !float.IsNaN(restY)) { liftTween?.Kill(); liftTween = CreateTween().SetParallel(); liftTween.TweenProperty(this, "position:y", restY, 0.15f); liftTween.TweenProperty(this, "rotation_degrees", restRot, 0.15f); ZIndex = 0; }
            HoverChanged?.Invoke(false);
        }
        else if (what == NotificationExitTree) CloseZoom();
    }

    // Redrawn cards: the delayed rules text box. Workshop (printed cards): a large preview of the card image.
    void OpenZoom()
    {
        if (!HoverZoom || FaceDown || Code.Length == 0) return;
        if (Redrawn) { Peek.ShowCard(this, Code, PeekExtra); return; }
        if (zoom is not null) return;
        var tex = Override ?? (HiddenPlace ? CardReader.UnrevealedPicture(Code) : Ui.Card(Code)); if (tex is null) return;
        float h = ZoomHeight, w = h * tex.GetWidth() / tex.GetHeight();
        if (w > 860) { w = 860; h = w * tex.GetHeight() / tex.GetWidth(); }
        var pic = Ui.Image(tex, new Vector2(w, h));
        var shell = new PanelContainer { MouseFilter = MouseFilterEnum.Ignore, ZIndex = 100 };
        shell.AddThemeStyleboxOverride("panel", Style.Box(new Color(0, 0, 0, 0.85f), Style.Brass, 2, 10, 6, 6));
        shell.AddChild(pic);
        App.I.Overlay.AddChild(shell);
        var vp = GetViewportRect().Size; var r = GetGlobalRect();
        float x = r.Position.X + r.Size.X / 2 - (w + 12) / 2;
        float y = r.Position.Y - h - 22;
        if (y < 8) { y = Math.Clamp(r.Position.Y + r.Size.Y / 2 - h / 2, 8, vp.Y - h - 20); x = r.End.X + 16; if (x + w > vp.X - 8) x = r.Position.X - w - 28; }
        shell.Position = new Vector2(Math.Clamp(x, 8, vp.X - w - 20), y);
        shell.Modulate = new Color(1, 1, 1, 0); shell.CreateTween().TweenProperty(shell, "modulate:a", 1f, 0.1f);
        zoom = shell;
    }
    void CloseZoom()
    {
        if (Redrawn) Peek.HideSoon();
        if (zoom is not null && IsInstanceValid(zoom)) zoom.QueueFree(); zoom = null;
    }
}
