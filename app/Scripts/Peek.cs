using Godot;
using Gathering;

namespace ArkhamUI;

// Hover text box ("文字框"): after the pointer rests on something for a moment, a plain dark box shows the card's
// name, type · traits and rules text — never the printed card image. Disappears as soon as the pointer leaves.
public static class Peek
{
    // the hover delay set in 设置 (0.1 s steps); a caller may still pass its own shorter delay
    public static double Delay => Math.Clamp(App.I.S?.Settings.HoverDelay ?? 1.0, 0.1, 3.0);
    public static bool ImageMode => App.I.S?.Settings.HoverMode == "image";
    static Control? current;
    static Control? pendingAnchor;
    static int generation;

    // Card-text content (optionally with an extra block such as an enemy's stats).
    // back: the card's other side (a location not yet revealed shows only its unrevealed side — Yunke 11-18)
    public static Control CardBox(string code, Control? extra = null, string? titleOverride = null, bool back = false)
    {
        back |= CardReader.IsUnrevealed(code);   // an unrevealed place on the table, wherever it is hovered (a log link …)
        if (ImageMode && CardImage(code, back, extra) is { } img) return img;
        var S = App.I.S!; var card = S.Card(code);
        if (back && J.String(card, "back_name").Length > 0) titleOverride = J.String(card, "back_name");
        var col = new VBoxContainer { CustomMinimumSize = new Vector2(400, 0), MouseFilter = Control.MouseFilterEnum.Ignore };
        col.AddThemeConstantOverride("separation", 8);
        col.AddChild(Style.Text(titleOverride ?? S.Name(code), 26, Style.HoverText, Style.Kai));
        var type = J.String(card, "type_name"); var traits = back ? "" : J.String(card, "traits");   // the traits are the revealed side's
        var sub = string.Join("  ·  ", new[] { type, traits }.Where(s => s.Length > 0));
        if (sub.Length > 0) col.AddChild(Style.Text(sub, 17, Style.PaperDim));
        col.AddChild(new ColorRect { Color = new Color(Style.Brass, 0.3f), CustomMinimumSize = new Vector2(0, 1), MouseFilter = Control.MouseFilterEnum.Ignore });
        if (extra is not null) col.AddChild(extra);
        var text = back ? J.String(card, "back_text") : J.String(card, "text");
        if (text.Length == 0 && back) text = J.String(card, "back_flavor");   // an unrevealed side with no rules: its own flavour line
        if (text.Length > 0) { var r = CardReader.Rules(text, 20); r.CustomMinimumSize = new Vector2(400, 0); r.SizeFlagsHorizontal = Control.SizeFlags.Fill; col.AddChild(r); }
        else if (extra is null) col.AddChild(Style.Text("（无规则文字）", 18, new Color(Style.PaperDim, 0.7f)));
        return col;
    }

    // 原图模式: the printed card (its unrevealed side when asked), large, in a rounded frame with a soft shadow; what the table
    // knows about it now (an enemy's damage, a place's clues — the `extra` block) in a slim dark strip under it
    static Control? CardImage(string code, bool back, Control? extra)
    {
        // its unrevealed side exactly (Ui.Card(code + "b") answers with the front when there is no back scan); none: the text box
        var tex = back ? CardReader.UnrevealedPicture(code) : Ui.Card(code);
        if (tex is null) return null;
        bool wide = tex.GetWidth() > tex.GetHeight();
        // as the html version's CardOverlay: 300 wide for an upright card, 300 tall for a sideways one
        var size = wide ? new Vector2(MathF.Round(300f * tex.GetWidth() / tex.GetHeight()), 300) : new Vector2(300, MathF.Round(300f * tex.GetHeight() / tex.GetWidth()));
        var col = new VBoxContainer { MouseFilter = Control.MouseFilterEnum.Ignore }; col.AddThemeConstantOverride("separation", 8);
        col.SetMeta("bare", true);
        col.AddChild(FramedCard(tex, size));
        if (extra is not null)
        {
            var strip = new PanelContainer { MouseFilter = Control.MouseFilterEnum.Ignore, CustomMinimumSize = new Vector2(size.X, 0) };
            var ss = new StyleBoxFlat { BgColor = new Color(0.045f, 0.04f, 0.035f, 0.94f), BorderColor = new Color(Style.Brass, 0.35f), ContentMarginLeft = 14, ContentMarginRight = 14, ContentMarginTop = 8, ContentMarginBottom = 10 };
            ss.SetBorderWidthAll(1); ss.SetCornerRadiusAll(8); strip.AddThemeStyleboxOverride("panel", ss);
            strip.AddChild(extra); col.AddChild(strip);
        }
        return col;
    }

    // A printed card shown large, in the one frame every large card has (Yunke 11-20: "所有地方的大卡都要有边框，包括调查员卡"):
    // rounded corners, a thin brass rim and a dark shadow round it. Fits the picture inside `box` keeping its proportions.
    public static Control FramedCard(Texture2D? tex, Vector2 box)
    {
        var size = box;
        if (tex is not null && tex.GetHeight() > 0)
        {
            float k = Math.Min(box.X / tex.GetWidth(), box.Y / tex.GetHeight());
            size = new Vector2(MathF.Round(tex.GetWidth() * k), MathF.Round(tex.GetHeight() * k));
        }
        float rad = Math.Clamp(Math.Min(size.X, size.Y) * 0.045f, 8, 22);
        var frame = new Panel { CustomMinimumSize = size, MouseFilter = Control.MouseFilterEnum.Ignore, ClipChildren = Control.ClipChildrenMode.AndDraw };
        var fs = new StyleBoxFlat { BgColor = Colors.Black, AntiAliasing = true }; fs.SetCornerRadiusAll((int)rad);
        frame.AddThemeStyleboxOverride("panel", fs);
        var img = new TextureRect { Texture = tex, ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize, StretchMode = TextureRect.StretchModeEnum.KeepAspectCovered, MouseFilter = Control.MouseFilterEnum.Ignore, TextureFilter = CanvasItem.TextureFilterEnum.LinearWithMipmapsAnisotropic };
        frame.AddChild(img); img.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        // the frame on top: a thin brass rim and a dark outer edge, so the card stands off whatever is behind it
        var rim = new Panel { MouseFilter = Control.MouseFilterEnum.Ignore };
        var rs = new StyleBoxFlat { DrawCenter = false, BorderColor = new Color("e0b56a"), AntiAliasing = true, ShadowColor = new Color(0, 0, 0, 0.7f), ShadowSize = 18 };
        rs.SetBorderWidthAll(3); rs.SetCornerRadiusAll((int)rad); rim.AddThemeStyleboxOverride("panel", rs);
        var holder = new Control { CustomMinimumSize = size, MouseFilter = Control.MouseFilterEnum.Ignore, SizeFlagsHorizontal = Control.SizeFlags.ShrinkCenter, SizeFlagsVertical = Control.SizeFlags.ShrinkCenter };
        holder.AddChild(frame); frame.Size = size;
        holder.AddChild(rim); rim.Position = Vector2.Zero; rim.Size = size;
        return holder;
    }

    static PanelContainer Shell(Control content)
    {
        if (content.HasMeta("bare"))
        {
            // the framed card needs no box round it
            var bare = new PanelContainer { MouseFilter = Control.MouseFilterEnum.Ignore, ZIndex = 100 };
            bare.AddThemeStyleboxOverride("panel", new StyleBoxEmpty());
            bare.AddChild(content); bare.SetMeta("bare", true);
            return bare;
        }
        // fixed width so wrapped rules text never stretches the box past the screen
        var shell = new PanelContainer { MouseFilter = Control.MouseFilterEnum.Ignore, ZIndex = 100, CustomMinimumSize = new Vector2(440, 0) };
        var sb = new StyleBoxFlat { BgColor = new Color(0.045f, 0.04f, 0.035f, 0.95f), BorderColor = new Color(Style.Brass, 0.3f), ContentMarginLeft = 20, ContentMarginRight = 20, ContentMarginTop = 16, ContentMarginBottom = 18 };
        sb.SetBorderWidthAll(1); sb.SetCornerRadiusAll(8); sb.ShadowColor = new Color(0, 0, 0, 0.55f); sb.ShadowSize = 14;
        shell.AddThemeStyleboxOverride("panel", sb);
        shell.AddChild(content);
        return shell;
    }

    // Show `build()` next to `anchor` after the hover delay (0 = now). Calling Hide or hovering something else cancels it.
    public static void Schedule(Control anchor, Func<Control?> build, double delay = -1)
    {
        if (delay < 0) delay = Delay;
        Hide(); pendingAnchor = anchor; int gen = ++generation;
        void Open()
        {
            if (gen != generation || !GodotObject.IsInstanceValid(anchor) || !anchor.IsInsideTree()) return;
            var content = build(); if (content is null) return;
            var shell = Shell(content); App.I.Overlay.AddChild(shell); shell.ResetSize();
            if (!shell.HasMeta("bare")) shell.Size = new Vector2(Math.Min(shell.Size.X, 440), shell.Size.Y);
            var vp = anchor.GetViewportRect().Size; var r = anchor.GetGlobalRect(); var s = shell.Size;
            // Above the anchor, centred on it. Things in the lower half (hand cards, the player area) always get the box
            // above them, pinned to the top edge if needed; only things near the top fall back to beside them.
            float x = r.Position.X + r.Size.X / 2 - s.X / 2, y = r.Position.Y - s.Y - 14;
            // the printed card goes beside what is pointed at, its top level with it (html CardOverlay: to the right, else left)
            if (shell.HasMeta("bare")) { x = r.End.X + 10; if (x + s.X > vp.X - 8) x = r.Position.X - s.X - 10; y = Math.Clamp(r.Position.Y, 8, Math.Max(8, vp.Y - s.Y - 8)); }
            else if (y < 8 && r.GetCenter().Y < vp.Y / 2) { y = Math.Clamp(r.Position.Y + r.Size.Y / 2 - s.Y / 2, 8, Math.Max(8, vp.Y - s.Y - 8)); x = r.End.X + 16; if (x + s.X > vp.X - 8) x = r.Position.X - s.X - 16; }
            shell.Position = new Vector2(Math.Clamp(x, 8, Math.Max(8, vp.X - s.X - 8)), Math.Clamp(y, 8, Math.Max(8, vp.Y - s.Y - 8)));
            if (App.I.S?.Settings.ReducedMotion != true) { shell.Modulate = new Color(1, 1, 1, 0); shell.CreateTween().TweenProperty(shell, "modulate:a", 1f, 0.12f); }
            current = shell;
        }
        if (delay <= 0) Open(); else anchor.GetTree().CreateTimer(delay).Timeout += Open;
    }

    public static void ShowCard(Control anchor, string code, Func<Control?>? extra = null, double delay = -1)
    {
        if (code.Length == 0) { Hide(); return; }
        Schedule(anchor, () => CardBox(code, extra?.Invoke()), delay);
    }

    // the pointer left: the box goes after the hover delay set in 设置 ("触发的多快，消失就多快", Yunke 11-20); a new hover in
    // between replaces it at once
    public static void HideSoon()
    {
        int gen = generation;
        if (current is null || !GodotObject.IsInstanceValid(current)) { Hide(); return; }
        current.GetTree().CreateTimer(Math.Min(Delay, 1.0)).Timeout += () => { if (gen == generation) Hide(); };
        pendingAnchor = null;
    }

    public static void Hide()
    {
        generation++; pendingAnchor = null;
        if (current is not null && GodotObject.IsInstanceValid(current)) current.QueueFree();
        current = null;
    }

    // Wire hover → delayed text box on any control.
    public static void Attach(Control c, string code, Func<Control?>? extra = null)
    {
        c.MouseEntered += () => ShowCard(c, code, extra);
        c.MouseExited += HideSoon;
        c.TreeExiting += () => { if (pendingAnchor == c) Hide(); };
    }
}
