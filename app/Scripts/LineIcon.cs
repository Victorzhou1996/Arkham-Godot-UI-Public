using Godot;
using System.Collections.Generic;

namespace ArkhamUI;

// Small line icons drawn in code, for buttons that should not carry words (Yunke 11-19 r11: the workshop had too much text —
// 编辑 / 复制 / 导出 / 删除 and the preview modes become icons, their names in the tooltip). Drawn with antialiased lines in
// the paper colour, so they match the rest of the minimal UI and scale cleanly.
public partial class LineIcon : Control
{
    public string Kind = "";
    public Color Ink = new(0.93f, 0.88f, 0.78f);
    public float Stroke = 1.8f;

    public LineIcon() { MouseFilter = MouseFilterEnum.Ignore; }

    // Icons Yunke gave as pictures (11-22: "我给了你两个图标，你直接用就行"): white masks in assets/ui/icons-user/<kind>.png, drawn
    // in the icon's ink. Read from the file (no import step needed).
    static readonly Dictionary<string, Texture2D?> userIcons = new();
    static Texture2D? UserIcon(string kind)
    {
        if (kind is not ("bag" or "history")) return null;
        if (userIcons.TryGetValue(kind, out var t)) return t;
        try
        {
            var file = System.IO.Path.Combine(App.I.Assets, "ui", "icons-user", kind + ".png");
            if (System.IO.File.Exists(file) && Image.LoadFromFile(file) is { } img) { img.GenerateMipmaps(); t = ImageTexture.CreateFromImage(img); }
        }
        catch { t = null; }
        return userIcons[kind] = t;
    }

    public override void _Draw()
    {
        float s = Math.Min(Size.X, Size.Y); var o = (Size - new Vector2(s, s)) / 2;
        if (UserIcon(Kind) is { } pic) { DrawTextureRect(pic, new Rect2(o + new Vector2(s * 0.06f, s * 0.06f), new Vector2(s * 0.88f, s * 0.88f)), false, Ink); return; }
        Vector2 P(float x, float y) => o + new Vector2(x * s, y * s);
        void L(float x1, float y1, float x2, float y2) => DrawLine(P(x1, y1), P(x2, y2), Ink, Stroke, true);
        // a rounded rectangle, filled or outlined, antialiased (a flat style box)
        void RR(float x, float y, float w, float h, float r, Color c, bool fill)
        {
            var sb = new StyleBoxFlat { BgColor = fill ? c : new Color(c, 0), DrawCenter = fill, BorderColor = c, AntiAliasing = true, AntiAliasingSize = 0.8f };
            if (!fill) sb.SetBorderWidthAll(Math.Max(1, (int)MathF.Round(Stroke)));
            sb.SetCornerRadiusAll((int)MathF.Round(r * s));
            DrawStyleBox(sb, new Rect2(P(x, y), new Vector2(w * s, h * s)));
        }
        void R(float x, float y, float w, float h) => DrawRect(new Rect2(P(x, y), new Vector2(w * s, h * s)), Ink, false, Stroke, true);
        switch (Kind)
        {
            case "edit":   // a pencil
                L(0.22f, 0.78f, 0.70f, 0.30f); L(0.32f, 0.88f, 0.80f, 0.40f); L(0.70f, 0.30f, 0.80f, 0.40f);
                L(0.22f, 0.78f, 0.18f, 0.92f); L(0.18f, 0.92f, 0.32f, 0.88f); L(0.62f, 0.38f, 0.72f, 0.48f);
                break;
            case "copy":   // two sheets
                R(0.18f, 0.30f, 0.46f, 0.54f); L(0.36f, 0.30f, 0.36f, 0.14f); L(0.36f, 0.14f, 0.82f, 0.14f); L(0.82f, 0.14f, 0.82f, 0.68f); L(0.82f, 0.68f, 0.64f, 0.68f);
                break;
            case "export": // a tray with an arrow leaving it
                L(0.18f, 0.56f, 0.18f, 0.84f); L(0.18f, 0.84f, 0.82f, 0.84f); L(0.82f, 0.84f, 0.82f, 0.56f);
                L(0.50f, 0.66f, 0.50f, 0.14f); L(0.50f, 0.14f, 0.34f, 0.30f); L(0.50f, 0.14f, 0.66f, 0.30f);
                break;
            case "import": // a tray with an arrow coming down into it
                L(0.18f, 0.56f, 0.18f, 0.84f); L(0.18f, 0.84f, 0.82f, 0.84f); L(0.82f, 0.84f, 0.82f, 0.56f);
                L(0.50f, 0.14f, 0.50f, 0.66f); L(0.50f, 0.66f, 0.34f, 0.50f); L(0.50f, 0.66f, 0.66f, 0.50f);
                break;
            // the deck workshop's top bar (Yunke 11-22): import as a cloud with an arrow going up, the sort and kind switches
            case "upload": // a cloud (three round lobes over a flat bottom) with an arrow going up inside it
            case "cloud":  // the cloud alone (the workshop's 导入, Yunke 11-22: no arrow)
            {
                Vector2[] Circle(float cx, float cy, float r) { var pts = new Vector2[28]; for (int i = 0; i < 28; i++) { float a = Mathf.Tau * i / 28; pts[i] = P(cx + r * Mathf.Cos(a), cy + r * Mathf.Sin(a)); } return pts; }
                var cloud = new[] { P(0.30f, 0.50f), P(0.70f, 0.50f), P(0.70f, 0.76f), P(0.30f, 0.76f) };
                foreach (var c in new[] { Circle(0.29f, 0.61f, 0.15f), Circle(0.48f, 0.45f, 0.21f), Circle(0.69f, 0.59f, 0.17f) })
                {
                    var m = Geometry2D.MergePolygons(cloud, c);
                    if (m.Count > 0) cloud = m[0];
                }
                var ring = new Vector2[cloud.Length + 1]; Array.Copy(cloud, ring, cloud.Length); ring[^1] = cloud[0];
                DrawPolyline(ring, Ink, Stroke, true);
                if (Kind == "upload") { L(0.50f, 0.68f, 0.50f, 0.38f); L(0.50f, 0.38f, 0.39f, 0.49f); L(0.50f, 0.38f, 0.61f, 0.49f); }
                break;
            }
            case "sort-time": // a clock face (most recently changed first)
                DrawArc(P(0.5f, 0.5f), 0.34f * s, 0, Mathf.Tau, 36, Ink, Stroke, true);
                L(0.50f, 0.50f, 0.50f, 0.28f); L(0.50f, 0.50f, 0.66f, 0.60f);
                break;
            case "sort-name": // lines getting shorter, an arrow going down beside them (by name)
                L(0.14f, 0.26f, 0.58f, 0.26f); L(0.14f, 0.48f, 0.48f, 0.48f); L(0.14f, 0.70f, 0.36f, 0.70f);
                L(0.76f, 0.18f, 0.76f, 0.82f); L(0.76f, 0.82f, 0.64f, 0.68f); L(0.76f, 0.82f, 0.88f, 0.68f);
                break;
            case "sort-inv": // a head and shoulders (by investigator)
                DrawArc(P(0.5f, 0.34f), 0.15f * s, 0, Mathf.Tau, 24, Ink, Stroke, true);
                DrawArc(P(0.5f, 0.88f), 0.30f * s, Mathf.Pi, Mathf.Tau, 24, Ink, Stroke, true);
                break;
            case "deck-new": // one card with a small diamond on it (a deck built here, not left by a campaign)
                RR(0.26f, 0.12f, 0.48f, 0.76f, 0.06f, Ink, false);
                L(0.50f, 0.36f, 0.62f, 0.50f); L(0.62f, 0.50f, 0.50f, 0.64f); L(0.50f, 0.64f, 0.38f, 0.50f); L(0.38f, 0.50f, 0.50f, 0.36f);
                break;
            case "neutral": // the neutral class has no symbol of its own: a plain ring
                DrawArc(P(0.5f, 0.5f), 0.30f * s, 0, Mathf.Tau, 32, Ink, Stroke * 1.15f, true);
                DrawCircle(P(0.5f, 0.5f), 0.07f * s, Ink, true, -1, true);
                break;
            case "delete": // a bin
                L(0.16f, 0.26f, 0.84f, 0.26f); L(0.40f, 0.26f, 0.42f, 0.16f); L(0.42f, 0.16f, 0.58f, 0.16f); L(0.58f, 0.16f, 0.60f, 0.26f);
                L(0.24f, 0.26f, 0.30f, 0.86f); L(0.30f, 0.86f, 0.70f, 0.86f); L(0.70f, 0.86f, 0.76f, 0.26f);
                L(0.42f, 0.40f, 0.43f, 0.74f); L(0.58f, 0.40f, 0.57f, 0.74f);
                break;
            // the three preview modes, drawn as soft filled shapes with rounded corners (Yunke 11-19: the outlined boxes
            // looked rough at button size)
            case "strips": // deck strips: a cost disc and a bar per row
                for (int i = 0; i < 3; i++)
                {
                    float y = 0.20f + i * 0.24f;
                    DrawCircle(P(0.20f, y + 0.07f), 0.075f * s, Ink, true, -1, true);
                    RR(0.34f, y, 0.52f, 0.14f, 0.05f, Ink, true);
                }
                break;
            case "small":  // a grid of small cards
                for (int r = 0; r < 2; r++) for (int c = 0; c < 3; c++) RR(0.13f + c * 0.26f, 0.15f + r * 0.37f, 0.20f, 0.30f, 0.04f, Ink, true);
                break;
            case "large":  // two big cards, each with its art window
                foreach (float x in new[] { 0.13f, 0.53f })
                {
                    RR(x, 0.12f, 0.34f, 0.76f, 0.06f, Ink, false);
                    RR(x + 0.06f, 0.20f, 0.22f, 0.28f, 0.03f, new Color(Ink, 0.85f), true);
                    L(x + 0.07f, 0.60f, x + 0.27f, 0.60f); L(x + 0.07f, 0.70f, x + 0.22f, 0.70f);
                }
                break;
            case "ok":     // a check mark
                L(0.20f, 0.52f, 0.42f, 0.74f); L(0.42f, 0.74f, 0.82f, 0.28f);
                break;
            case "warn":   // an exclamation in a triangle
                L(0.50f, 0.14f, 0.12f, 0.84f); L(0.12f, 0.84f, 0.88f, 0.84f); L(0.88f, 0.84f, 0.50f, 0.14f); L(0.50f, 0.40f, 0.50f, 0.62f); L(0.50f, 0.70f, 0.50f, 0.74f);
                break;
            case "chev-l":
                L(0.62f, 0.22f, 0.38f, 0.50f); L(0.38f, 0.50f, 0.62f, 0.78f);
                break;
            case "chev-r":
                L(0.38f, 0.22f, 0.62f, 0.50f); L(0.62f, 0.50f, 0.38f, 0.78f);
                break;
            case "cards":  // a small fan of two cards
                R(0.16f, 0.24f, 0.40f, 0.58f); L(0.44f, 0.16f, 0.82f, 0.22f); L(0.82f, 0.22f, 0.76f, 0.80f); L(0.76f, 0.80f, 0.58f, 0.78f); L(0.44f, 0.16f, 0.44f, 0.24f);
                break;
            case "victory":   // a laurel wreath (victory points): two leafy branches round a dark disc, open at the top
                {
                    var c = P(0.5f, 0.52f); float r = 0.36f * s;
                    DrawCircle(c, r * 0.86f, new Color(0.08f, 0.05f, 0.02f, 0.88f));
                    var dark = Ink.Darkened(0.45f);
                    void Leaf(Vector2 at, Vector2 dir, float len, float wid)
                    {
                        var perp = new Vector2(-dir.Y, dir.X); var pts = new Vector2[12];
                        for (int i = 0; i < 6; i++) { float t = -1 + i * 0.4f; pts[i] = at + dir * (t * len / 2) + perp * (wid / 2 * Mathf.Sqrt(Math.Max(0, 1 - t * t))); }
                        for (int i = 0; i < 6; i++) { float t = 1 - i * 0.4f; pts[6 + i] = at + dir * (t * len / 2) - perp * (wid / 2 * Mathf.Sqrt(Math.Max(0, 1 - t * t))); }
                        DrawColoredPolygon(pts, Ink);
                        var ring = new Vector2[13]; Array.Copy(pts, ring, 12); ring[12] = pts[0]; DrawPolyline(ring, dark, 0.8f, true);
                    }
                    foreach (int side in new[] { -1, 1 })
                    {
                        // from the bottom (90°) round the side to the upper part, leaving the top open
                        float a0 = Mathf.DegToRad(98), a1 = Mathf.DegToRad(242);
                        var stem = new Vector2[16];
                        for (int i = 0; i < 16; i++) { float a = a0 + (a1 - a0) * i / 15f; var v = new Vector2(Mathf.Cos(a), Mathf.Sin(a)); if (side > 0) v.X = -v.X; stem[i] = c + v * r; }
                        DrawPolyline(stem, dark, Math.Max(1f, s * 0.04f), true);
                        for (int k = 0; k < 5; k++)
                        {
                            float a = a0 + (a1 - a0) * (0.1f + k * 0.2f); var v = new Vector2(Mathf.Cos(a), Mathf.Sin(a)); if (side > 0) v.X = -v.X;
                            var at = c + v * r;
                            // the branch's direction of growth (towards the top), and the leaves leaning out of it
                            var tan = new Vector2(-Mathf.Sin(a), Mathf.Cos(a)); if (side > 0) tan.X = -tan.X;
                            float len = s * (0.2f - k * 0.012f), wid = len * 0.42f;
                            Leaf(at + (tan.Rotated(-0.75f * side) * len * 0.45f), tan.Rotated(-0.75f * side), len, wid);
                            Leaf(at + (tan.Rotated(0.75f * side) * len * 0.45f), tan.Rotated(0.75f * side), len, wid);
                        }
                    }
                }
                break;
            case "plus":
                L(0.50f, 0.18f, 0.50f, 0.82f); L(0.18f, 0.50f, 0.82f, 0.50f);
                break;
            // the table's top bar (Yunke 11-20 #3: all its buttons as icons)
            case "expand":   // four corners pointing out (map full screen)
                foreach (var (x, y, dx, dy) in new[] { (0.14f, 0.14f, 1f, 1f), (0.86f, 0.14f, -1f, 1f), (0.14f, 0.86f, 1f, -1f), (0.86f, 0.86f, -1f, -1f) })
                { L(x, y, x + dx * 0.24f, y); L(x, y, x, y + dy * 0.24f); L(x, y, x + dx * 0.20f, y + dy * 0.20f); }
                break;
            case "shrink":   // four corners pointing in (map back to the corner)
                foreach (var (x, y, dx, dy) in new[] { (0.40f, 0.40f, -1f, -1f), (0.60f, 0.40f, 1f, -1f), (0.40f, 0.60f, -1f, 1f), (0.60f, 0.60f, 1f, 1f) })
                { L(x, y, x + dx * 0.22f, y); L(x, y, x, y + dy * 0.22f); L(x, y, x + dx * 0.26f, y + dy * 0.26f); }
                break;
            case "lock":
            case "unlock":   // a padlock, its shackle closed or lifted open
                RR(0.20f, 0.46f, 0.60f, 0.42f, 0.06f, Ink, false);
                {
                    float lift = Kind == "unlock" ? 0.10f : 0f;
                    DrawArc(P(0.50f, 0.36f - lift), 0.19f * s, Mathf.Pi, Mathf.Tau, 18, Ink, Stroke, true);
                    L(0.31f, 0.36f - lift, 0.31f, Kind == "unlock" ? 0.30f : 0.46f); L(0.69f, 0.36f - lift, 0.69f, 0.46f);
                }
                L(0.50f, 0.60f, 0.50f, 0.72f);
                break;
            case "fit":      // the whole map in its frame (Home): a frame, and a smaller one centred in it with four ticks reaching it
                R(0.12f, 0.16f, 0.76f, 0.68f);
                R(0.34f, 0.36f, 0.32f, 0.28f);
                L(0.12f, 0.16f, 0.30f, 0.32f); L(0.88f, 0.16f, 0.70f, 0.32f); L(0.12f, 0.84f, 0.30f, 0.68f); L(0.88f, 0.84f, 0.70f, 0.68f);
                break;
            case "hint":     // show what can be clicked (F): a small ring with eight short rays round it, a flash
                DrawArc(P(0.5f, 0.5f), 0.13f * s, 0, Mathf.Tau, 24, Ink, Stroke, true);
                for (int i = 0; i < 8; i++)
                {
                    float a = i * Mathf.Pi / 4; var d = new Vector2(Mathf.Cos(a), Mathf.Sin(a));
                    float r0 = i % 2 == 0 ? 0.24f : 0.25f, r1 = i % 2 == 0 ? 0.40f : 0.34f;
                    DrawLine(P(0.5f, 0.5f) + d * r0 * s, P(0.5f, 0.5f) + d * r1 * s, Ink, Stroke, true);
                }
                break;
            case "reset":    // a circular arrow
                DrawArc(P(0.5f, 0.52f), 0.30f * s, -Mathf.Pi * 0.35f, Mathf.Pi * 1.35f, 28, Ink, Stroke, true);
                { var tip = P(0.5f, 0.52f) + new Vector2(Mathf.Cos(-Mathf.Pi * 0.35f), Mathf.Sin(-Mathf.Pi * 0.35f)) * 0.30f * s; DrawLine(tip, tip + new Vector2(-0.16f, -0.02f) * s, Ink, Stroke, true); DrawLine(tip, tip + new Vector2(0.02f, 0.16f) * s, Ink, Stroke, true); }
                break;
            case "bag":      // the chaos bag: a tied sack (Yunke 11-22: the old blob and the line pouch both read as a ring or a bottle; drawn
            {                //   large first and checked by eye): a broad body sitting flat, a gathered top flaring out, a dark tie and cord
                Vector2 Bz(Vector2 p0, Vector2 p1, Vector2 p2, Vector2 p3, float t) { float u = 1 - t; return u * u * u * p0 + 3 * u * u * t * p1 + 3 * u * t * t * p2 + t * t * t * p3; }
                var pts = new List<Vector2>();
                for (int i = 0; i <= 16; i++) pts.Add(Bz(P(0.42f, 0.40f), P(0.14f, 0.50f), P(0.08f, 0.90f), P(0.50f, 0.90f), i / 16f));
                for (int i = 1; i <= 16; i++) pts.Add(Bz(P(0.50f, 0.90f), P(0.92f, 0.90f), P(0.86f, 0.50f), P(0.58f, 0.40f), i / 16f));
                foreach (var (x, y) in new[] { (0.62f, 0.33f), (0.76f, 0.20f), (0.63f, 0.24f), (0.57f, 0.13f), (0.50f, 0.22f), (0.43f, 0.13f), (0.37f, 0.24f), (0.24f, 0.20f), (0.38f, 0.33f) }) pts.Add(P(x, y));
                DrawColoredPolygon(pts.ToArray(), Ink);
                var cord = new Color(0.08f, 0.06f, 0.05f, 0.92f);
                DrawLine(P(0.36f, 0.39f), P(0.64f, 0.39f), cord, Stroke * 1.5f, true);
                DrawLine(P(0.58f, 0.40f), P(0.66f, 0.56f), cord, Stroke, true); DrawLine(P(0.58f, 0.40f), P(0.72f, 0.50f), cord, Stroke, true);
                break;
            }
            case "log":      // a page of lines
                RR(0.20f, 0.12f, 0.60f, 0.76f, 0.05f, Ink, false);
                for (int i = 0; i < 4; i++) L(0.32f, 0.30f + i * 0.13f, i == 3 ? 0.56f : 0.68f, 0.30f + i * 0.13f);
                break;
            case "music":    // a pair of quavers on a beam
                DrawCircle(P(0.30f, 0.74f), 0.11f * s, Ink, true, -1, true); DrawCircle(P(0.72f, 0.66f), 0.11f * s, Ink, true, -1, true);
                L(0.40f, 0.74f, 0.40f, 0.22f); L(0.82f, 0.66f, 0.82f, 0.14f); L(0.40f, 0.22f, 0.82f, 0.14f); L(0.40f, 0.32f, 0.82f, 0.24f);
                break;
            case "wallpaper": // a framed picture: a frame, a hill and a moon (the menu's change-the-painting button)
                RR(0.14f, 0.20f, 0.72f, 0.60f, 0.05f, Ink, false);
                L(0.20f, 0.72f, 0.42f, 0.46f); L(0.42f, 0.46f, 0.56f, 0.62f); L(0.56f, 0.62f, 0.66f, 0.52f); L(0.66f, 0.52f, 0.80f, 0.72f);
                DrawCircle(P(0.66f, 0.34f), 0.06f * s, Ink, true, -1, true);
                break;
            case "scroll":   // a scroll: a sheet between two rolled ends, a few lines of writing (the game menu, Yunke 11-20)
                RR(0.27f, 0.20f, 0.46f, 0.60f, 0.02f, Ink, false);
                RR(0.17f, 0.12f, 0.66f, 0.12f, 0.06f, Ink, false); RR(0.17f, 0.76f, 0.66f, 0.12f, 0.06f, Ink, false);
                DrawCircle(P(0.17f, 0.18f), 0.03f * s, Ink, true, -1, true); DrawCircle(P(0.83f, 0.82f), 0.03f * s, Ink, true, -1, true);
                for (int i = 0; i < 3; i++) L(0.36f, 0.36f + i * 0.12f, i == 2 ? 0.54f : 0.64f, 0.36f + i * 0.12f);
                break;
            case "medal":    // an award medal: a disc on two ribbon tails cut in a V (victory points, Yunke 11-22: the icon he sent)
            {
                var dark = Ink.Darkened(0.35f);
                // the ribbon tails behind the disc
                DrawColoredPolygon(new[] { P(0.34f, 0.50f), P(0.46f, 0.56f), P(0.42f, 0.96f), P(0.35f, 0.88f), P(0.28f, 0.94f) }, dark);
                DrawColoredPolygon(new[] { P(0.54f, 0.56f), P(0.66f, 0.50f), P(0.72f, 0.94f), P(0.65f, 0.88f), P(0.58f, 0.96f) }, dark);
                // the disc: a filled ring with a dark centre for the number
                DrawCircle(P(0.5f, 0.38f), 0.32f * s, Ink, true, -1, true);
                DrawCircle(P(0.5f, 0.38f), 0.24f * s, new Color(0.10f, 0.07f, 0.03f, 0.92f), true, -1, true);
                DrawArc(P(0.5f, 0.38f), 0.32f * s, 0, Mathf.Tau, 40, dark, Stroke * 0.8f, true);
                break;
            }
            case "unrolled": // the log: a scroll as a line drawing (Yunke 11-22: redrawn plainer) — the roll across the top, curled at its
            {                //   right end (his reference, mirrored), the sheet hanging from it with a few lines of writing
                RR(0.20f, 0.14f, 0.52f, 0.13f, 0.065f, Ink, false);                                   // the roll
                DrawArc(P(0.75f, 0.205f), 0.065f * s, -Mathf.Pi * 0.5f, Mathf.Pi * 1.1f, 16, Ink, Stroke, true);   // its curl
                L(0.26f, 0.27f, 0.26f, 0.80f); L(0.66f, 0.27f, 0.66f, 0.74f);                         // the sheet's sides
                L(0.26f, 0.80f, 0.60f, 0.80f);                                                        // its foot …
                DrawArc(P(0.60f, 0.74f), 0.06f * s, Mathf.Pi * 0.5f, 0f, 10, Ink, Stroke, true);      // … turning up at the corner
                for (int i = 0; i < 3; i++) L(0.34f, 0.40f + i * 0.12f, i == 2 ? 0.50f : 0.58f, 0.40f + i * 0.12f);
                break;
            }
            case "undo":     // undo to here: a return arrow (Yunke 11-22, the icon he sent, its arrow only): pointing left at the foot,
            {                //   its tail running right and curling back up over the top
                L(0.22f, 0.64f, 0.62f, 0.64f);
                DrawArc(P(0.62f, 0.48f), 0.16f * s, Mathf.Pi * 0.5f, -Mathf.Pi * 0.5f, 18, Ink, Stroke, true);
                L(0.62f, 0.32f, 0.46f, 0.32f);
                L(0.22f, 0.64f, 0.34f, 0.53f); L(0.22f, 0.64f, 0.34f, 0.75f);                          // the head
                break;
            }
            case "menu":     // three bars
                for (int i = 0; i < 3; i++) L(0.18f, 0.28f + i * 0.22f, 0.82f, 0.28f + i * 0.22f);
                break;
            case "help":     // a question mark in a ring
                DrawArc(P(0.5f, 0.5f), 0.38f * s, 0, Mathf.Tau, 36, Ink, Stroke, true);
                DrawArc(P(0.5f, 0.40f), 0.12f * s, Mathf.Pi, Mathf.Pi * 2.35f, 16, Ink, Stroke, true);
                L(0.50f, 0.52f, 0.50f, 0.60f); DrawCircle(P(0.50f, 0.71f), Stroke * 0.8f, Ink, true, -1, true);
                break;
        }
    }

    // A button with only an icon; its name is the tooltip.
    public static Button Button(string kind, string tip, Action act, float size = 40, bool accent = false, Color? ink = null)
    {
        var b = Ui.MinimalButton("", act, 18, accent); b.CustomMinimumSize = new Vector2(size, size); b.TooltipText = tip;
        var ic = new LineIcon { Kind = kind, Ink = ink ?? (accent ? new Color("ffe2a6") : new Color(0.93f, 0.88f, 0.78f)), Stroke = Math.Max(1.6f, size / 22f) };
        b.AddChild(ic); ic.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect); ic.OffsetLeft = ic.OffsetTop = size * 0.2f; ic.OffsetRight = ic.OffsetBottom = -size * 0.2f;
        return b;
    }
}
