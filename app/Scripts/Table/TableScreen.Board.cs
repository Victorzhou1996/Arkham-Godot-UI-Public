using Godot;
using Gathering;
using System.Text.Json;

namespace ArkhamUI;

// The map (Yunke, after the html version's board): every location of the scenario is on it at once, where it lies, joined to
// its neighbours; the investigators and enemies stand on their locations. By default it is a small map in the top-left corner
// (Yunke 11-18 #5); Tab or the button under the scenario title shows / hides it. It pans (drag it), zooms (wheel; double-click
// zooms in on a spot and back), and M fits the whole map again. Each place is a small card on the map with its tokens, its
// shroud in a roundel, and a breathing edge when it can be acted on (#6). Unlocked (html "unlock locations", #10), a card is
// dragged to nudge it; the nudges are kept per scenario.
public partial class TableScreen
{
    Control? boardView, boardWorld, boardMarks;
    // the map is drawn inside its frame only (small corner / large middle): a camera push-in never spills over the table
    Control? boardClip; Vector2 clipOrigin;
    float boardZoom = 1f, boardFitZoom = 1f;
    Vector2 boardPan, boardWorldSize;
    Rect2 boardContent;   // what "the whole map" shows: the painting, or the occupied part of a layout grid
    string boardFor = "";
    Vector2? panStart; Vector2 panOrigin; bool panMoved;
    Tween? boardTween;
    // The small location cards keep a readable size while the map zooms: each is drawn at its design size and scaled to its
    // room (within limits), centred on the room plus the player's own nudge (in map pixels).
    readonly List<(Control Node, Rect2 Room, string Label)> marks = new();
    readonly List<(Control Node, Rect2 Room, string Label)> pins = new();   // painting mode: the markers standing on a region
    static readonly Vector2 TileDesign = new(156, 212);   // a location as an upright card (Yunke 11-18 r8: back to html's cards and lines)
    bool mapShown = true;
    bool mapUnlocked;
    Control? lastColumn;   // the standing column LocationTile made for the place just built (added to the board after all cards)
    string? dragLabel; Vector2 dragStart, dragOffset0; bool dragMoved;
    string? tilePress;   // the location card a left press began on (it acts on release if the pointer did not move)
    // the connections drawn between cards (Yunke 11-19 r9 #1: they follow a card while it is dragged; one-way ones carry an arrow)
    sealed record LinkDraw(Line2D[] Lines, Node2D? Arrow, Rect2 Ra, string La, Rect2 Rb, string Lb, string IdA = "", string IdB = "");
    readonly List<LinkDraw> links = new();
    // card maps have no frame (Yunke 11-19 r9 #2): the map can be moved anywhere on the screen, under the table's own parts
    bool Boundless => scenario?.PaintedMap != true;
    static IEnumerable<string> Conn(Entity l) => J.Items(J.Get(l.Raw, "connectedLocations")).Select(x => x.GetString() ?? "");

    // The corner the map is fitted into: the top-left, under the scenario title and the map buttons, above the player's slots.
    // Small map: the top-left corner. Large map (Tab, Yunke 11-18): the middle of the table between the side columns, above
    // the hand, where every place's markers and shroud can be read.
    bool mapLarge;
    Rect2 BoardFree()
    {
        var vp = GetViewportRect().Size;
        // Tab (Yunke 11-18): the map in the top-left corner, or the map over the whole screen (the table's own parts stay on top)
        // the map over the whole table ("focus on the map", Yunke 11-18): a margin all round, the table blurred behind it
        if (mapLarge) return new Rect2(60, 84, vp.X - 120, vp.Y - 144);
        // card maps (Yunke 11-18 r8): the whole middle of the table, between the two side columns, under the top bar and above
        // the hand — panned by dragging, zoomed with the wheel, cards moved when unlocked (as on the html table)
        if (scenario?.PaintedMap != true) { float side = SideMargin + SideW + 24; return new Rect2(side, 100, Math.Max(400, vp.X - 2 * side), Math.Max(300, vp.Y - 100 - 340)); }   // bottom stays above the turn line and the hand
        // the corner frame takes the map's own proportions, so the whole picture fits in it uncut (the attic was cut off)
        float w = Math.Min(640, vp.X * 0.34f), maxH = Math.Max(220, Math.Min(480, vp.Y - 160 - 440));
        float aspect = boardContent.Size.X > 0 ? boardContent.Size.Y / boardContent.Size.X : 0.5625f;
        float h = w * aspect; if (h > maxH) { h = maxH; w = h / aspect; }
        return new Rect2(24, 160, w, h);
    }

    // The most a card map may take when its cards would otherwise be too small: from under the top bar to just above the hand
    // cards (the row of choice pills may lie over it). Other maps: their frame.
    Rect2 BoardRoom()
    {
        var free = BoardFree();
        if (mapLarge || scenario?.PaintedMap == true) return free;
        var vp = GetViewportRect().Size;
        return new Rect2(free.Position.X, 86, free.Size.X, Math.Max(free.Size.Y, vp.Y - 86 - 250));
    }

    // ───────── the player's nudges of location cards, kept per scenario across games (html keeps them per game on its server)
    static string OffsetFile => System.IO.Path.Combine(App.I.DataRoot, "settings", "map-offsets.json");
    Dictionary<string, Dictionary<string, float[]>>? mapOffsets;
    Dictionary<string, float[]> Offsets(string scen)
    {
        if (mapOffsets is null)
        {
            try { mapOffsets = System.IO.File.Exists(OffsetFile) ? JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, float[]>>>(System.IO.File.ReadAllText(OffsetFile)) : null; } catch { mapOffsets = null; }
            mapOffsets ??= new();
        }
        if (!mapOffsets.TryGetValue(scen, out var d)) mapOffsets[scen] = d = new();
        return d;
    }
    Vector2 OffsetOf(string label) => model is not null && Offsets(ScenarioCode(model)).TryGetValue(label, out var v) && v.Length == 2 ? new Vector2(v[0], v[1]) : Vector2.Zero;
    void SaveOffsets()
    {
        try { System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(OffsetFile)!); System.IO.File.WriteAllText(OffsetFile, JsonSerializer.Serialize(mapOffsets)); }
        catch (Exception e) { GD.PushWarning("map offsets: " + e.Message); }
    }
    void ResetOffsets() { if (model is null) return; Offsets(ScenarioCode(model)).Clear(); SaveOffsets(); Render(); }

    void EnsureBoardView()
    {
        if (boardView is not null && IsInstanceValid(boardView)) return;
        boardView = new Control { MouseFilter = MouseFilterEnum.Stop };
        AddChild(boardView); UnderLayer();   // above the background, below the table's own elements
        boardView.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        boardClip = new Control { MouseFilter = MouseFilterEnum.Ignore, ClipContents = true }; boardView.AddChild(boardClip);
        boardWorld = new Control { MouseFilter = MouseFilterEnum.Ignore }; boardClip.AddChild(boardWorld);
        boardMarks = new Control { MouseFilter = MouseFilterEnum.Ignore }; boardClip.AddChild(boardMarks); boardMarks.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        // (no dark ring round the frame any more — Yunke 11-18 saw it as a black box; the map itself turns transparent near
        //  the frame's edges instead, ScreenFade below, so a pushed-in map still melts into the table)
        boardView.GuiInput += BoardInput;
    }

    // Map focus mode (Tab): the map in front of every table element, the table behind it blurred and dark, a way back in the
    // corner; the corner map sits under the table's own parts as before.
    ColorRect? focusShade; Button? focusBack; HBoxContainer? focusUndo;
    void FocusLayering()
    {
        if (boardView is null) return;
        if (mapLarge)
        {
            if (focusShade is null || !IsInstanceValid(focusShade))
            {
                focusShade = new ColorRect { MouseFilter = MouseFilterEnum.Stop, Color = Colors.White, Material = new ShaderMaterial { Shader = FocusBlur } };
                AddChild(focusShade); focusShade.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
                focusShade.GuiInput += e => { if (e is InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Right }) ToggleMap(); };
            }
            MoveChild(focusShade, GetChildCount() - 1); MoveChild(boardView, GetChildCount() - 1);
            // above every table element, whatever its z (the test panel sits at 35)
            focusShade.ZIndex = 90; boardView.ZIndex = 91;
            if (focusBack is null || !IsInstanceValid(focusBack))
            {
                focusBack = Ui.MinimalButton("返回牌桌（Tab）", ToggleMap, 20, true); focusBack.CustomMinimumSize = new Vector2(0, 46);
                boardView.AddChild(focusBack); focusBack.Position = new Vector2(24, 20);
            }
            focusBack.Visible = true; focusShade.Visible = true;
            // 撤回 stays in front over the full-screen map too (Yunke 11-23 "永远保持在最前"): the top bar is under the blur and its
            // input goes to the shade, so the map layer has its own row where the top bar's begins — 撤回, back to the table, and
            // 整张地图 (Home; the phone's "⋯" menu that also held it is gone, Yunke 11-23)
            if (focusUndo is null || !IsInstanceValid(focusUndo))
            {
                focusUndo = new HBoxContainer(); focusUndo.AddThemeConstantOverride("separation", 10);
                focusUndo.AddChild(LineIcon.Button("undo", "撤回一步（U）\n只撤回上一步，像快速读档；更早的步骤在记录里撤回", Undo, 46));
                focusUndo.AddChild(LineIcon.Button("shrink", "返回牌桌（M）", ToggleMap, 46));
                focusUndo.AddChild(LineIcon.Button("fit", "整张地图（Home）\n把地图缩放到完整显示", () => BoardFit(), 46));
                boardView.AddChild(focusUndo);
            }
            focusUndo.Visible = true;
            Callable.From(() =>
            {
                if (focusUndo is null || !IsInstanceValid(focusUndo) || boardView is null || topUndo is null || !IsInstanceValid(topUndo)) return;
                focusUndo.Position = boardView.GetGlobalTransform().AffineInverse() * topUndo.GlobalPosition;
            }).CallDeferred();
        }
        else
        {
            if (focusShade is not null && IsInstanceValid(focusShade)) focusShade.Visible = false;
            if (focusBack is not null && IsInstanceValid(focusBack)) focusBack.Visible = false;
            if (focusUndo is not null && IsInstanceValid(focusUndo)) focusUndo.Visible = false;
            boardView.ZIndex = 0;
            UnderLayer();   // back under the table's own parts
        }
    }

    // Put the map just below the table's own layer. Only move it when it is above: moving it to the layer's index while it
    // already sat below put it one place ABOVE the layer, so every render swapped the two and, every other render, the
    // full-screen map view caught all clicks meant for the hand and the buttons (Yunke 11-18: the opening hand could not be
    // changed after marking one card; any card pick could hang the same way).
    void UnderLayer()
    {
        if (boardView is null) return;
        int li = layer.GetIndex(), bi = boardView.GetIndex();
        if (bi > li) MoveChild(boardView, li);
    }
    static Shader? focusBlurShader;
    static Shader FocusBlur => focusBlurShader ??= new Shader { Code = @"shader_type canvas_item;
uniform sampler2D screen_tex : hint_screen_texture, filter_linear;
void fragment() {
    vec2 px = SCREEN_PIXEL_SIZE; vec3 c = vec3(0.0); float n = 0.0;
    for (int r = 1; r <= 3; r++) for (int k = 0; k < 12; k++) {
        float a = float(k) * 0.5236 + float(r) * 0.3; vec2 o = vec2(cos(a), sin(a)) * px * float(r) * 7.0;
        c += texture(screen_tex, SCREEN_UV + o).rgb; n += 1.0;
    }
    c /= n;
    COLOR = vec4(c * 0.32, 1.0);
}" };

    // Each location's place on the map in map pixels: a painted room (its hotspot), or a cell of the core's layout grid.
    Dictionary<string, Rect2> BoardRects(TableModel m, out Vector2 world)
    {
        var outR = new Dictionary<string, Rect2>();
        var tex = scenario!.PaintedMap ? Ui.Bg(scenario.MapImage) : null;
        var layout = ScenarioData.LayoutRects(m, out int cols, out int rows);
        if (tex is not null)
        {
            world = new Vector2(2000, 2000f * tex.GetHeight() / tex.GetWidth());
            foreach (var loc in m.Locations)
            {
                var hs = scenario.Hotspot(m.Label(loc));
                outR[loc.Id] = hs is not null ? new Rect2(hs[0] * world.X, hs[1] * world.Y, hs[2] * world.X, hs[3] * world.Y)
                    : layout.TryGetValue(m.Label(loc), out var lr) ? new Rect2(lr.Position * world, lr.Size * world) : new Rect2(world / 2, Vector2.One);
            }
        }
        else
        {
            world = new Vector2(Math.Max(3, cols) * 290, Math.Max(2, rows) * 262);   // room to the right of each card for who stands there
            foreach (var loc in m.Locations)
                if (layout.TryGetValue(m.Label(loc), out var lr)) outR[loc.Id] = new Rect2(lr.Position * world, lr.Size * world);
        }
        return outR;
    }

    void Board()
    {
        var m = model!; if (scenario is null) return;
        EnsureBoardView();
        boardView!.Visible = mapShown;
        FocusLayering();
        foreach (var c in boardWorld!.GetChildren()) { boardWorld.RemoveChild(c); c.QueueFree(); }
        foreach (var c in boardMarks!.GetChildren()) { boardMarks.RemoveChild(c); c.QueueFree(); }
        marks.Clear(); pins.Clear();
        var rects = BoardRects(m, out var size);
        boardWorld.Size = size; boardWorldSize = size;
        bool painted = scenario.PaintedMap;
        boardContent = painted || rects.Count == 0 ? new Rect2(Vector2.Zero, size) : rects.Values.Aggregate((a, b) => a.Merge(b)).Grow(40);
        // the zoom at which the smallest location card is still readable (a little over half its design size): the map opens
        // no smaller than that, now that the cards scale with the map (Yunke 11-20 #9)
        tileReadZoom = rects.Count == 0 ? 0f : 0.6f / rects.Values.Min(r => Math.Min(r.Size.X * 0.92f / TileDesign.X, r.Size.Y * 0.92f / TileDesign.Y));
        boardFitZoom = FitZoom();
        if (painted)
        {
            // the realistic map, its edges melting into the table
            tileGroundMat = new ShaderMaterial { Shader = GroundFade }; tileGroundMat.SetShaderParameter("fade", new Vector2(90f / size.X, 90f / size.Y));
            var ground = new TextureRect { Texture = Ui.Bg(scenario.MapImage), ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize, StretchMode = TextureRect.StretchModeEnum.Scale,
                MouseFilter = MouseFilterEnum.Ignore, Size = size, TextureFilter = TextureFilterEnum.LinearWithMipmapsAnisotropic, Modulate = new Color(0.9f, 0.88f, 0.86f),
                Material = tileGroundMat };
            boardWorld.AddChild(ground);
        }
        if (painted && scenario.WholePicture)
        {
            // Yunke 11-18: the map is the whole picture; a place is a region of it that lights up under the pointer and shows
            // its information, glows when it can be acted on, and carries only small markers. No cards; the connections are lines
            // across the gaps between places (RegionLinks).
            BoardPicture(m, rects, size);
            string code0 = ScenarioCode(m);
            if (boardFor != code0) { boardFor = code0; BoardFit(false); } else ApplyBoard();
            return;
        }
        // the connections between neighbouring places, from card centre to card centre (nudges included); a connection that
        // only goes one way (A lists B, B does not list A) carries an arrow towards where it leads
        links.Clear();
        var drawn = new HashSet<string>();
        foreach (var a in m.Locations)
            foreach (var bid in Conn(a))
            {
                if (!rects.TryGetValue(a.Id, out var ra) || !rects.TryGetValue(bid, out var rb) || m.Location(bid) is not { } bl) continue;
                if (!drawn.Add(string.CompareOrdinal(a.Id, bid) < 0 ? a.Id + bid : bid + a.Id)) continue;
                bool twoWay = Conn(bl).Contains(a.Id);
                bool live = m.MyLocation is { } my && (a.Id == my.Id || bid == my.Id) && (byAnchor.ContainsKey("loc:" + a.Id) || byAnchor.ContainsKey("loc:" + bid));
                bool seen = m.Revealed(a) && m.Revealed(bl);
                // fine, clear, softly glowing threads, their width in screen pixels (TableScreen.Links)
                var lines = LinkLines(boardWorld, live, seen);
                Node2D? arrow = null;
                if (!twoWay)
                {
                    // the original arrow shape in the thread's own colour, outlined like the thread (Yunke 11-20: "箭头本身和线交错的地方
                    // 不要有黑线边框…箭头的外侧还是要边线的"): its dark edge lies in the same layer as the thread's dark bed, under the
                    // thread's bright core, and the arrow's fill on top — so thread and arrow read as one outlined shape, with no dark
                    // seam where they meet
                    var lineColor = live ? LinkLive : new Color(LinkGold, seen ? 1f : 0.6f);
                    arrow = new Polygon2D { Polygon = ArrowShape, Color = lineColor, Antialiased = true };
                    // the edge: a closed soft stroke round the shape, the thread's own bed (same texture, colour and screen width per side)
                    var edge = LinkLine(3.7f, new Color(0.08f, 0.04f, 0.01f, 0.7f), false); edge.Points = ArrowShape; edge.Closed = true; edge.JointMode = Line2D.LineJointMode.Sharp;
                    boardWorld.AddChild(edge); boardWorld.MoveChild(edge, lines[^1].GetIndex());   // between the bed and the core
                    arrow.SetMeta("edge", edge);
                    boardWorld.AddChild(arrow);
                }
                links.Add(new LinkDraw(lines.ToArray(), arrow, ra, m.Label(a), rb, m.Label(bl), a.Id, bid));
            }
        UpdateLinks();
        var columns = new List<(Control, Rect2, string)>();
        foreach (var loc in m.Locations)
        {
            if (!rects.TryGetValue(loc.Id, out var r)) continue;
            lastColumn = null;
            var tile = LocationTile(m, loc);
            boardMarks.AddChild(tile);
            marks.Add((tile, r, m.Label(loc)));
            if (lastColumn is not null) columns.Add((lastColumn, r, m.Label(loc)));
        }
        foreach (var col in columns) { boardMarks.AddChild(col.Item1); marks.Add(col); }
        // a new scenario (or the first look): show the whole map; afterwards the player's own view is kept
        string code = ScenarioCode(m);
        if (boardFor != code) { boardFor = code; BoardFit(false); }
        else ApplyBoard();
    }

    void UpdateLinks()
    {
        foreach (var k in links)
        {
            var p0 = k.Ra.GetCenter() + OffsetOf(k.La); var p1 = k.Rb.GetCenter() + OffsetOf(k.Lb);
            var dir = p1 - p0; if (dir.LengthSquared() < 1) continue;
            // a gentle bow instead of a ruler line, like a thread on the table
            var nrm = new Vector2(-dir.Y, dir.X).Normalized() * Math.Min(26f, dir.Length() * 0.08f);
            var curve = SmoothPath(new[] { p0, (p0 + p1) / 2 + nrm, p1 });
            foreach (var l in k.Lines) if (IsInstanceValid(l)) l.Points = curve;
            if (k.Arrow is { } ar && IsInstanceValid(ar))
            {
                int i = curve.Length / 2;
                ar.Position = curve[i]; ar.Rotation = (curve[Math.Min(i + 1, curve.Length - 1)] - curve[Math.Max(i - 1, 0)]).Angle();
                if (ar.HasMeta("edge") && ar.GetMeta("edge").AsGodotObject() is Node2D ed && IsInstanceValid(ed)) { ed.Position = ar.Position; ed.Rotation = ar.Rotation; }
            }
        }
    }

    // Shows or hides the map (Tab, or the button under the scenario title).
    // Tab: the small map in the corner or the large map in the middle (Yunke 11-18)
    // the full-screen map and the table's map keep their own zoom and position (Yunke 11-20): each is put back as it was left;
    // a view not seen yet is fitted to its frame
    readonly Dictionary<bool, (float Zoom, Vector2 Pan, string For)> mapViews = new();
    void ToggleMap()
    {
        mapViews[mapLarge] = (boardZoom, boardPan, boardFor);
        mapShown = true; mapLarge = !mapLarge;
        hoverRegion = -1; CancelPush(); Peek.Hide();
        if (mapViews.TryGetValue(mapLarge, out var v) && v.For == boardFor && boardFor.Length > 0) { boardTween?.Kill(); boardZoom = v.Zoom; boardPan = v.Pan; }
        else boardFor = "";   // fitted in its new frame
        Render();
    }

    void ToggleMapLock() { mapUnlocked = !mapUnlocked; dragLabel = null; Render(); }

    // One place as a small card: its picture and name, the tokens placed on it (clues, doom), who stands there and what lurks
    // there, and its shroud in a roundel at the bottom-right (like a hand card's cost). No "隐藏 N" text and no buttons beside it:
    // a place that can be acted on (investigate, move, its own abilities) glows and breathes along its edge (Yunke 11-18 #5, #6).
    // The picture of a place not yet revealed: its unrevealed side's own art when there is one, else the art of the first printing
    // that shares its name (Yunke 11-20: the second 南区 01127 had no art, so the tile fell back to its whole revealed card face —
    // and versions of one place share an unrevealed side, so showing a version's own art would tell which one it is).
    const float LocAbilityD = 36;   // a location's ability buttons (Yunke 11-20: 26 was too small to aim at)

    static Texture2D? UnrevealedArt(string code) => CardReader.UnrevealedArt(code);   // never the revealed side's picture (QQ #47)

    Control LocationTile(TableModel m, Entity loc)
    {
        var size = TileDesign; string label = m.Label(loc);
        string key = "loc:" + loc.Id, code = CoreCatalog.Normalize(loc.Code);
        bool rev = m.Revealed(loc);
        // its fast abilities have their own button on the card (TableScreen.FastButtons): they alone do not light the card
        bool fastPrinted = PrintsFast(J.String(S.Card(code), rev ? "text" : "back_text"));
        bool actionPrinted = J.String(S.Card(code), rev ? "text" : "back_text").Contains("[action]");
        bool act = CardActionable(key, fastPrinted, actionPrinted) && !mapUnlocked, here = loc.Id == m.MyLocation?.Id;
        var tile = new Control { Size = size, MouseFilter = MouseFilterEnum.Stop, MouseDefaultCursorShape = mapUnlocked ? CursorShape.Move : act ? CursorShape.PointingHand : CursorShape.Arrow };
        // Yunke 11-19 r9 #3: drawn like a hand card — the picture fills a rounded card, a thin rim, the name on a dark fade at
        // the bottom, the shroud in a roundel on the bottom-right corner, a little outside the card (as a hand card's cost).
        const int rad = 12;
        var shadow = new Panel { MouseFilter = MouseFilterEnum.Ignore, Size = size };
        var ssb = new StyleBoxFlat { BgColor = new Color(0, 0, 0, 0), DrawCenter = false, ShadowColor = here ? new Color(Style.Good, 0.75f) : new Color(0, 0, 0, 0.6f), ShadowSize = here ? 18 : 10, ShadowOffset = here ? Vector2.Zero : new Vector2(0, 3), AntiAliasing = true };
        ssb.SetCornerRadiusAll(rad); shadow.AddThemeStyleboxOverride("panel", ssb); tile.AddChild(shadow);
        var clip = new Panel { MouseFilter = MouseFilterEnum.Ignore, ClipChildren = ClipChildrenMode.Only, Size = size };
        var mask = new StyleBoxFlat { BgColor = Colors.White, AntiAliasing = true }; mask.SetCornerRadiusAll(rad); clip.AddThemeStyleboxOverride("panel", mask);
        tile.AddChild(clip);
        var picture = rev ? Ui.Art(code) ?? Ui.Card(code) : UnrevealedArt(code);
        var art = new TextureRect { Texture = picture, ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize, StretchMode = TextureRect.StretchModeEnum.KeepAspectCovered, MouseFilter = MouseFilterEnum.Ignore,
            TextureFilter = TextureFilterEnum.LinearWithMipmapsAnisotropic, Modulate = rev ? new Color(0.95f, 0.93f, 0.9f) : new Color(0.46f, 0.46f, 0.5f), Size = size };
        clip.AddChild(art);
        var fadeSize = new Vector2(size.X, size.Y * 0.42f);
        clip.AddChild(new TextureRect { Texture = VGrad(0.92f, true), ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize, StretchMode = TextureRect.StretchModeEnum.Scale, MouseFilter = MouseFilterEnum.Ignore,
            Position = new Vector2(0, size.Y - fadeSize.Y), Size = fadeSize });
        // the name sits between the shroud (bottom-left) and the clues (bottom-right) — Yunke 11-19 r11 #1. It is never cut short
        // (QA 11-20: "阿卡姆…", "米斯卡…"): a long name gets a smaller size, and two lines when one would be too small to read
        string nm = m.Name(loc); float avail = size.X - (rev ? 64 : 20), nx = rev ? 30 : 10;
        bool twoLines = nm.Length * 16 > avail;
        int fs = twoLines ? (int)Math.Clamp(avail / Math.Ceiling(nm.Length / 2.0), 13, 20) : (int)Math.Min(22, avail / Math.Max(1, nm.Length));
        var name = Style.Text(nm, fs, here ? Style.HoverText : Style.Paper, Style.Kai);
        name.AddThemeConstantOverride("outline_size", 6); name.AddThemeColorOverride("font_outline_color", Colors.Black); name.MouseFilter = MouseFilterEnum.Ignore;
        if (twoLines) { name.AutowrapMode = TextServer.AutowrapMode.Arbitrary; name.AddThemeConstantOverride("line_spacing", -2); }
        else { name.ClipText = true; name.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis; }
        float nh = twoLines ? fs * 2 + 8 : 28;
        clip.AddChild(name); name.Position = new Vector2(nx, size.Y - 12 - nh); name.Size = new Vector2(avail, nh); name.VerticalAlignment = VerticalAlignment.Bottom;
        // places that share a name (地底噬魔's six 阿卡姆密林) differ by the subtitle on their revealed side: shown above the name
        // (QA 11-20: they could not be told apart)
        if (rev && J.String(S.Card(code), "subname") is { Length: > 0 } subname)
        {
            var sub = Style.Text(subname, 15, new Color(Style.Brass, 0.95f), Style.Kai); sub.MouseFilter = MouseFilterEnum.Ignore;
            sub.AddThemeConstantOverride("outline_size", 5); sub.AddThemeColorOverride("font_outline_color", Colors.Black);
            sub.ClipText = true; sub.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis;
            clip.AddChild(sub); sub.Position = new Vector2(nx, size.Y - 12 - nh - 20); sub.Size = new Vector2(avail + 10, 20);
        }
        var rim = new Panel { MouseFilter = MouseFilterEnum.Ignore, Size = size };
        var rimSb = new StyleBoxFlat { BgColor = new Color(0, 0, 0, 0), DrawCenter = false, AntiAliasing = true,
            BorderColor = mapUnlocked ? new Color(Style.Brass, 0.95f) : here ? new Color(Style.Good, 0.9f) : new Color(0.75f, 0.68f, 0.52f, 0.7f) };
        rimSb.SetBorderWidthAll(2); rimSb.SetCornerRadiusAll(rad); rim.AddThemeStyleboxOverride("panel", rimSb); tile.AddChild(rim);
        // abilities a player can trigger here (Yunke 11-19 r9: easy to forget when nobody stands there): their icons, top-left.
        // Yunke 11-19: a fast ability is a button of the symbol's size, always on the card (lit when usable); a reaction has no
        // symbol — the whole card glows and is the button when it can be used; a forced one shakes the card when it triggers.
        string rulesText = J.String(S.Card(code), rev ? "text" : "back_text");
        var kinds = new List<(string Key, string Word)>();
        // (also when the core offers one the text tables do not mark — a click on the card itself always investigates)
        if (rulesText.Contains("[action]") || PrintedActionChoices(key).Count > 0) kinds.Add(("action", "行动能力"));
        if (fastPrinted) kinds.Add(("fast", "快速能力"));
        if (kinds.Count > 0)
        {
            var abil = new HBoxContainer { MouseFilter = MouseFilterEnum.Ignore, Position = new Vector2(6, 6) }; abil.AddThemeConstantOverride("separation", 6);
            foreach (var (k, _) in kinds)
            {
                if (k == "fast" && !mapUnlocked && FastButton(key, code, LocAbilityD, true) is { } fb) { abil.AddChild(fb); continue; }
                // the printed action: a button too (Yunke 11-20) — usable now: it glows and a click uses it; else a dim symbol
                if (k == "action" && !mapUnlocked) { actionOnCard.Add(key); abil.AddChild(AbilityButton(code, LocAbilityD, "action", PrintedActionChoices(key), "行动能力（现在不能使用）")); continue; }
                var dot = new Panel { MouseFilter = MouseFilterEnum.Ignore, CustomMinimumSize = new Vector2(LocAbilityD, LocAbilityD) };
                var dsb = new StyleBoxFlat { BgColor = new Color(0.06f, 0.05f, 0.04f, 0.88f), BorderColor = new Color("e0b56a"), AntiAliasing = true }; dsb.SetBorderWidthAll(1); dsb.SetCornerRadiusAll((int)(LocAbilityD / 2));
                dot.AddThemeStyleboxOverride("panel", dsb);
                var g = Ui.Glyph(k, (int)(LocAbilityD * 0.62f), new Color("ffe2a6")); g.HorizontalAlignment = HorizontalAlignment.Center; g.VerticalAlignment = VerticalAlignment.Center; g.MouseFilter = MouseFilterEnum.Ignore;
                dot.AddChild(g); g.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect); abil.AddChild(dot);
            }
            tile.AddChild(abil);
        }
        if (rev)
        {
            const float d = 36;
            var ring = new Panel { MouseFilter = MouseFilterEnum.Ignore, Size = new Vector2(d, d), Position = new Vector2(-d * 0.22f, size.Y - d * 0.78f) };   // bottom-left
            var rsb = new StyleBoxFlat { BgColor = new Color(0.14f, 0.2f, 0.36f), BorderColor = new Color("e0b56a"), AntiAliasing = true, ShadowColor = new Color(0, 0, 0, 0.5f), ShadowSize = 4 };
            rsb.SetBorderWidthAll(2); rsb.SetCornerRadiusAll((int)d); ring.AddThemeStyleboxOverride("panel", rsb);
            // a shroud changed by something in play (Obscuring Fog …): red when raised, green when lowered (Yunke 11-20 #20)
            int shroudNow = m.Shroud(loc), shroudPrinted = m.PrintedShroud(loc);
            var shroudInk = shroudNow > shroudPrinted ? new Color("ff7a66") : shroudNow < shroudPrinted ? new Color("8de08a") : Colors.White;
            var sn = Style.Text(shroudNow.ToString(), 22, shroudInk, Style.Symbols); sn.MouseFilter = MouseFilterEnum.Ignore;
            if (shroudNow != shroudPrinted) { ring.MouseFilter = MouseFilterEnum.Pass; ring.TooltipText = "隐藏值 " + shroudNow + "（印刷值 " + shroudPrinted + "）"; }
            ring.AddChild(sn); Ui.CenterDigits(sn); tile.AddChild(ring);
        }
        // cards attached to the place (Obscuring Fog …): tucked under its left edge, each one its own small card that can be
        // pointed at and acted on (Yunke 11-20 #20: the fog was invisible, only the shroud had changed)
        int ai = 0;
        foreach (var at in m.Attached("AttachedToLocation", loc.Id))
        {
            string akey = m.ThreatKey(at); var acode = at.Code;
            // a little bigger, and under the place's card (Yunke 11-20: the fog covered the card's ability button): it peeks out on the left
            var acv = new CardView { Code = acode, Key = akey, CustomMinimumSize = new Vector2(70, 98), Size = new Vector2(70, 98), ShowCost = false, ShowName = false, Rim = new Color("8a5aa8"),
                Actionable = byAnchor.ContainsKey(akey) && !mapUnlocked, RotationDegrees = -7, TooltipText = S.Name(acode) + "（附着在" + m.Name(loc) + "）" };
            acv.OnClick = () => ActOn(akey, acv, acode); Anchor(akey, acv);
            tile.AddChild(acv); tile.MoveChild(acv, 0); acv.Position = new Vector2(-48, 6 + ai * 34); ai++;   // the upper half: clear of the thread that leaves the card's middle (QA 11-20)
        }
        // cards lying underneath the place (Blood on the Altar: one encounter card under each location, and its skull token counts
        // the locations with none left): their backs peek out above the card's top edge, as many as there are (up to three
        // shown), with the count — after the html version's pool item for cards underneath
        var under = J.Get(loc.Raw, "cardsUnderneath");
        if (under.ValueKind == System.Text.Json.JsonValueKind.Array && under.GetArrayLength() > 0)
        {
            int enc = under.EnumerateArray().Count(c => J.String(c, "tag") == "EncounterCard"), ply = under.GetArrayLength() - enc;
            var pile = new Control { MouseFilter = MouseFilterEnum.Pass, Position = new Vector2(size.X * 0.16f, -34), Size = new Vector2(70, 52) };   // 30% larger (Yunke 11-20)
            pile.TooltipText = m.Name(loc) + "下面有" + (enc > 0 ? " " + enc + " 张遭遇牌" : "") + (ply > 0 ? " " + ply + " 张玩家牌" : "") + "（面朝下）";
            int shown = Math.Min(3, enc + ply);
            for (int i = 0; i < shown; i++)
            {
                var back = new TextureRect { Texture = Ui.CardBack(i < enc), ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize, StretchMode = TextureRect.StretchModeEnum.KeepAspectCovered,
                    MouseFilter = MouseFilterEnum.Ignore, Size = new Vector2(52, 73), Position = new Vector2(i * 9, i * -4), RotationDegrees = -6 + i * 4, PivotOffset = new Vector2(26, 36) };
                pile.AddChild(back);
            }
            if (enc + ply > 1) { var nb = CardView.Badge("cards", enc + ply, 26); nb.MouseFilter = MouseFilterEnum.Ignore; pile.AddChild(nb); nb.Position = new Vector2(40, -10); }
            tile.AddChild(pile); tile.MoveChild(pile, 0);   // under the card: only the top of the backs shows
        }
        // victory points (revealed side) in the top-right corner, the doom on the place below them
        int vic = rev ? Victory(code) : 0;
        if (vic > 0) { var vb = VictoryBadge(vic, 26); tile.AddChild(vb); vb.Position = new Vector2(size.X - vb.Size.X - 4, 4); }
        if (m.Doom(loc) > 0) { var bd = CardView.Badge("doom", m.Doom(loc), 28); bd.MouseFilter = MouseFilterEnum.Ignore; bd.SetMeta("doomBadge", true); tile.AddChild(bd); bd.Position = new Vector2(size.X - 36, vic > 0 ? 40 : 6); }
        // the keys lying on the place (QQ #29): a column down its right edge, under the victory points and the doom
        if (KeyRow(loc.Raw, 30, vertical: true, m.Name(loc), loc.Id) is { } keyCol)
        { tile.AddChild(keyCol); keyCol.Position = new Vector2(size.X - 34, 6 + (vic > 0 ? 34 : 0) + (m.Doom(loc) > 0 ? 34 : 0)); }
        // the clues on the place: the clue token with its number, on the card (Yunke 11-18 r8)
        // bottom-right corner, a little outside the card, mirroring the shroud (Yunke 11-19 r11 #1); dim when none are left
        if (rev)
        {
            // as big as the shroud roundel and ringed like it (Yunke 11-19)
            const float cd = 36;
            var clue = Ui.RingToken("clue", m.Clues(loc), cd);
            if (m.Clues(loc) == 0) clue.Modulate = new Color(1, 1, 1, 0.45f);
            tile.AddChild(clue); clue.Position = new Vector2(size.X - cd * 0.78f, size.Y - cd * 0.78f);
        }
        // who is there and what lurks there: a column to the card's right (Yunke 11-18 r8). Each investigator has a row of their
        // own with the enemies engaged with them to their right (Yunke 11-19 r9 #19); enemies engaged with nobody, then the
        // story allies standing here (as small cards too, #18), below. No z-index: the column stays under the table's own
        // parts (encounter deck, banners — #12).
        var standing = new VBoxContainer { MouseFilter = MouseFilterEnum.Ignore }; standing.AddThemeConstantOverride("separation", 4);
        var engagedMe = m.Me is null ? new HashSet<string>() : m.EngagedWith(m.Me).Select(e => e.Id).ToHashSet();
        var placed = new HashSet<string>();
        const float mini = 62;
        HBoxContainer Row() { var r = new HBoxContainer { MouseFilter = MouseFilterEnum.Ignore }; r.AddThemeConstantOverride("separation", 4); standing.AddChild(r); return r; }
        foreach (var inv in m.InvestigatorsAt(loc.Id))
        {
            var row = Row();
            string ik = "inv:" + inv.Id; var icode = inv.Code;
            bool other = inv.Id != m.Me?.Id && !mapUnlocked;
            CardView tok = null!;
            var invPlayer = Guid.TryParse(J.String(inv.Raw, "playerId"), out var ipg) ? ipg : Guid.Empty;
            tok = InvToken(m, inv, mini, other && byAnchor.ContainsKey(ik),
                other && byAnchor.ContainsKey(ik) ? () => ActOn(ik, tok, icode)
                : inv.Id != m.Me?.Id && invPlayer != Guid.Empty ? () => ViewSeat(invPlayer) : null);
            NoteInvToken(inv, tok);
            if (other) Anchor(ik, tok);
            row.AddChild(tok);
        }
        // Yunke 11-20 #12, rules: an engaged enemy lies in its investigator's threat area, not at the location; only an enemy
        // that is placed at the location itself (engaged with nobody, or Massive — engaged with all but kept at the location)
        // shows on the map
        var loose = m.EnemiesAt(loc.Id).ToList();
        if (loose.Count > 0) { var row = Row(); foreach (var e in loose) { var em = EnemyMini(m, e, mini, engagedMe.Contains(e.Id) || mapUnlocked); NoteEnemyToken(e, loc.Id, em); row.AddChild(em); } }
        // concealed mini-cards lying here (The Scarlet Keys): face down, the size of an enemy's card; one that can be attacked or
        // evaded glows and is the button (QQ #44: only the painting map drew them, so on the card map — the phone's too — they
        // could not be seen or tapped)
        var concealed = m.ConcealedAt(loc.Id).ToList();
        if (concealed.Count > 0 && !mapUnlocked) { var row = Row(); foreach (var cc in concealed) row.AddChild(ConcealedMini(m, cc, mini)); }
        var allies = m.AssetsAt(loc.Id).ToList();
        if (allies.Count > 0)
        {
            var row = Row();
            foreach (var a in allies)
            {
                string ak = "asset:" + a.Id; var acode = a.Code; var ent = a;
                var cv = CardView.PortraitCard(acode, mini, byAnchor.ContainsKey(ak) && !mapUnlocked);
                cv.ShowName = false; cv.Rim = new Color("93b8a0"); cv.HoverZoom = true; cv.PeekExtra = () => UnitExtra(ent); cv.Glow = AssignGlow(ak);
                // its health and sanity on the mini, always (r10)
                if (HealthChips(ent, 30, vertical: true) is { } chips) { var host = cv; chips.MouseFilter = MouseFilterEnum.Ignore; host.Ready += () => { host.AddChild(chips); chips.Position = new Vector2(host.Size.X + 3, 0); }; }   // stacked to its right: nothing stands beyond it
                if (!mapUnlocked) { cv.OnClick = () => ActOn(ak, cv, acode); Anchor(ak, cv); } else cv.PassClicks = true;
                row.AddChild(cv);
            }
        }
        // the column lies on a holder of the card's size that the board adds after every card, so a neighbouring card drawn
        // later never covers who stands here (QA 11-20: in 无人知晓 the Brood and what was attached to it hid under the next card)
        if (standing.GetChildCount() > 0) { var holder = new Control { MouseFilter = MouseFilterEnum.Ignore, Size = size }; holder.AddChild(standing); standing.Position = new Vector2(size.X + 8, 4); lastColumn = holder; }
        // a place that can be acted on: a bright edge that breathes (over the art, so it reads at any size)
        if (act)
        {
            var glow = new Panel { MouseFilter = MouseFilterEnum.Ignore, Size = size };
            // an enemy deciding where to go (a hunter in the enemy phase: "选择…移动到哪里") lights the places in red (Yunke 11-22)
            bool enemyMove = EnemyAsk(m) is { Ask: not "engage" };
            var gsb = new StyleBoxFlat { DrawCenter = false, BgColor = new Color(0, 0, 0, 0), BorderColor = enemyMove ? new Color("ff6a52") : new Color("ffcf6e"), ShadowColor = enemyMove ? new Color(BiteRed, 0.8f) : new Color(Style.Amber, 0.8f), ShadowSize = 16, AntiAliasing = true };
            gsb.SetBorderWidthAll(3); gsb.SetCornerRadiusAll(10); glow.AddThemeStyleboxOverride("panel", gsb); tile.AddChild(glow);
            // right above the card's rim, under its markers: on top, its halo veiled the shroud and clue roundels where they stand
            // out past the card's edge
            tile.MoveChild(glow, rim.GetIndex() + 1);
            if (S.Settings.ReducedMotion != true) { var tw = glow.CreateTween().SetLoops(); tw.TweenProperty(glow, "modulate:a", 0.3f, 0.8f).SetTrans(Tween.TransitionType.Sine); tw.TweenProperty(glow, "modulate:a", 1f, 0.8f).SetTrans(Tween.TransitionType.Sine); }
        }
        tile.GuiInput += e =>
        {
            if (mapUnlocked)
            {
                // unlocked (html "unlock locations"): drag the card to nudge it; nothing is acted on
                if (e is InputEventMouseButton { ButtonIndex: MouseButton.Left } b)
                {
                    if (b.Pressed) { dragLabel = label; dragStart = b.GlobalPosition; dragOffset0 = OffsetOf(label); dragMoved = false; }
                    else if (dragLabel == label) { dragLabel = null; if (dragMoved) { SaveOffsets(); Board(); } }
                    tile.AcceptEvent();
                }
                else if (e is InputEventMouseMotion mm && dragLabel == label)
                {
                    var d = (mm.GlobalPosition - dragStart) / Math.Max(0.05f, boardZoom);
                    if (!dragMoved && d.Length() * boardZoom > 4) dragMoved = true;
                    if (dragMoved) { var o = dragOffset0 + d; Offsets(ScenarioCode(m))[label] = new[] { o.X, o.Y }; ApplyBoard(); UpdateLinks(); }
                    tile.AcceptEvent();
                }
                return;
            }
            // A click acts on the place (move there, investigate…) when the button comes up without the pointer having moved;
            // a press that moves pans the map instead, so starting a drag on a card never moves the investigator by mistake.
            // With nothing to do a click does nothing (right-click reads the card).
            if (e is InputEventMouseButton { ButtonIndex: MouseButton.Left } mb)
            {
                if (mb.Pressed && !mb.DoubleClick) { panStart = mb.GlobalPosition; panOrigin = boardPan; panMoved = false; tilePress = key; ClosePopup(); }
                else if (!mb.Pressed)
                {
                    bool click = tilePress == key && !panMoved;
                    panStart = null; tilePress = null;
                    if (click && act) ActOn(key, tile, loc.Code);
                }
                tile.AcceptEvent();
            }
            else if (e is InputEventMouseMotion mm && panStart is Vector2 ps && tilePress == key)
            {
                if (!panMoved && (mm.GlobalPosition - ps).Length() > 5) panMoved = true;
                if (panMoved) { boardTween?.Kill(); boardPan = panOrigin + (mm.GlobalPosition - ps); ApplyBoard(); }
                tile.AcceptEvent();
            }
            else if (e is InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Right }) CardReader.Open(code, printed: true, unrevealed: !m.Revealed(loc));   // only its unrevealed side until revealed
        };
        if (!mapUnlocked)
        {
            var lcode = loc.Code; var lent = loc;
            tile.MouseEntered += () => Peek.Schedule(tile, () => Peek.CardBox(lcode, LocationExtra(lent), back: !m.Revealed(lent)));
            tile.MouseExited += Peek.Hide;
            Anchor(key, tile);
        }
        return tile;
    }

    // ───────── painting mode: regions of one picture
    static Shader? regionShader;
    // a soft-edged rectangle of colour: full inside, fading out over the last `fade` part of each side
    static ShaderMaterial RegionMat(float fade)
    {
        regionShader ??= new Shader { Code = @"shader_type canvas_item;
uniform float fade = 0.18;
void fragment(){
    vec2 d = min(UV, 1.0 - UV) / fade;
    float a = smoothstep(0.0, 1.0, clamp(min(d.x, d.y), 0.0, 1.0));
    COLOR = vec4(COLOR.rgb, COLOR.a * a);
}" };
        var m = new ShaderMaterial { Shader = regionShader }; m.SetShaderParameter("fade", fade); return m;
    }

    // The painting carries a region mask (tools/make_map_regions.py): each place is its own silhouette. One shader does what
    // the investigator pickers do for people (Yunke 11-18): the place under the pointer stays lit while everything else drops
    // to a silhouette; places that can be acted on glow and breathe along their own outline; where you stand has a green
    // outline; unrevealed places are veiled, places not in play darker still. The pointer is matched against the mask itself.
    static Shader? regionLightShader;
    ShaderMaterial? groundMat;
    Godot.Image? regionImage;
    string regionImageFor = "";
    readonly List<string> regionLabels = new();
    readonly Dictionary<int, Entity> regionPlace = new();
    int hoverRegion = -1, pushedRegion = -1, pushGen;
    float regionFocus;
    Tween? focusTween;
    Control? pushCaption;

    static Shader RegionLight => regionLightShader ??= new Shader { Code = @"shader_type canvas_item;
uniform sampler2D regions : filter_nearest;
uniform sampler2D glow0 : filter_linear;
uniform sampler2D glow1 : filter_linear;
uniform sampler2D glow2 : filter_linear;
uniform float state[16];
uniform float act[16];
uniform float here_idx = -1.0;
uniform float focus_idx = -1.0;
uniform float focus = 0.0;
uniform vec2 texel = vec2(0.0006, 0.001);
uniform float glow_px = 26.0;   // how far past a place's outline its light fades out, in picture pixels
uniform vec2 fade = vec2(0.05, 0.08);
uniform vec4 clip_rect = vec4(0.0, 0.0, 1.0, 1.0);   // the map frame on screen, in SCREEN_UV units (x, y, w, h)
uniform vec2 clip_fade = vec2(0.02, 0.03);
float screen_fade(vec2 s) {
    float l = smoothstep(clip_rect.x, clip_rect.x + clip_fade.x, s.x), r = 1.0 - smoothstep(clip_rect.x + clip_rect.z - clip_fade.x, clip_rect.x + clip_rect.z, s.x);
    float t = smoothstep(clip_rect.y, clip_rect.y + clip_fade.y, s.y), b = 1.0 - smoothstep(clip_rect.y + clip_rect.w - clip_fade.y, clip_rect.y + clip_rect.w, s.y);
    return l * r * t * b;
}
uniform float flash = 0.0;   // ""where can I click"": the places that can be acted on flare up along their own outline
int idx_at(vec2 uv) { return int(round(texture(regions, uv).r * 255.0 / 20.0)) - 1; }
void fragment() {
    vec4 c = texture(TEXTURE, UV);
    vec3 base = c.rgb * 0.92;
    float lum = dot(base, vec3(0.2126, 0.7152, 0.0722));
    // Soft coverage (Yunke 11-18, the glow-mask rule: the mask fully covers the place and may be a little larger; no sharp
    // edge anywhere, the light shades off like a glow): each place's area grown and blurred offline (tools/make_map_regions.py,
    // <image>-glow0..2.png, one channel per place), read here with linear filtering — smooth, no stepping.
    vec4 g0 = texture(glow0, UV), g1 = texture(glow1, UV), g2 = texture(glow2, UV);
    float cv[12] = float[12](g0.r, g0.g, g0.b, g0.a, g1.r, g1.g, g1.b, g1.a, g2.r, g2.g, g2.b, g2.a);
    float dim = 0.86, fcov = 0.0, acov = 0.0, hcov = 0.0;
    for (int i = 0; i < 12; i++) {
        float k = smoothstep(0.0, 0.55, cv[i]);   // full inside the place, fading out past its outline
        if (k <= 0.0) continue;
        float st = state[i];
        dim = mix(dim, st > 1.5 ? 0.32 : (st > 0.5 ? 0.62 : 1.0), k);
        if (float(i) == focus_idx) fcov = k;
        if (float(i) == here_idx) hcov = k;
        acov = max(acov, act[i] * k);
    }
    vec3 col = base * dim;
    // the place under the pointer stays lit, everything else drops to a silhouette — blended by coverage, no hard cut
    vec3 sil = mix(vec3(lum), col, 0.22) * vec3(0.55, 0.62, 0.72) * 0.42;
    col = mix(col, mix(sil, col * 1.15, fcov), focus);
    float breathe = 0.55 + 0.45 * sin(TIME * 3.6);
    vec3 amber = vec3(1.0, 0.8, 0.43);
    float lit = max(breathe, flash);
    // a place that can be acted on: a faint warm wash over it, brighter round its outline, fading out beyond it (no line)
    float arim = acov * (1.0 - acov) * 4.0;
    col += amber * (arim * 0.5 + acov * 0.1) * lit + amber * acov * flash * 0.22;
    // where you stand: the same soft band in green
    float hrim = hcov * (1.0 - hcov) * 4.0;
    col = mix(col, vec3(0.55, 0.85, 0.6), hrim * 0.45 * (1.0 - arim));
    float a = smoothstep(0.0, fade.x, UV.x) * smoothstep(0.0, fade.x, 1.0 - UV.x) * smoothstep(0.0, fade.y, UV.y) * smoothstep(0.0, fade.y, 1.0 - UV.y) * screen_fade(SCREEN_UV);
    COLOR = vec4(col, c.a * a);
}" };

    // ───── connections on a painted map (Yunke 11-18): a place's mask covers its body only and places need not touch; which
    // places connect — the picture does not always show it — is a line across the gap between them. Dim brass normally;
    // breathing amber from where you stand to a place you can move to now; brighter for the place under the pointer.
    readonly Dictionary<int, Vector2> regionCentres = new();
    string regionCentresFor = "";
    readonly List<(int A, int B, Node2D Line)> regionLinks = new();

    // one point inside each place (mask pixels): the region pixel nearest its centroid, from a sparse sample of the mask
    void RegionCentres()
    {
        if (regionImage is null || regionCentresFor == regionImageFor) return;
        regionCentresFor = regionImageFor; regionCentres.Clear();
        int w = regionImage.GetWidth(), h = regionImage.GetHeight(), step = Math.Max(2, Math.Min(w, h) / 400);
        var pts = new Dictionary<int, List<Vector2>>();
        for (int y = 0; y < h; y += step)
            for (int x = 0; x < w; x += step)
            {
                int i = (int)Math.Round(regionImage.GetPixel(x, y).R * 255f / 20f) - 1; if (i < 0) continue;
                if (!pts.TryGetValue(i, out var l)) pts[i] = l = new(); l.Add(new Vector2(x, y));
            }
        foreach (var (i, l) in pts)
        {
            var c = l.Aggregate(Vector2.Zero, (s, p) => s + p) / l.Count;
            regionCentres[i] = l.OrderBy(p => p.DistanceSquaredTo(c)).First();
        }
    }

    int MaskIdx(Vector2 p)
    {
        int x = (int)p.X, y = (int)p.Y;
        if (regionImage is null || x < 0 || y < 0 || x >= regionImage.GetWidth() || y >= regionImage.GetHeight()) return -1;
        return (int)Math.Round(regionImage.GetPixel(x, y).R * 255f / 20f) - 1;
    }

    void RegionLinks(TableModel m, Vector2 world, float hereIdx)
    {
        regionLinks.Clear();
        if (regionImage is null || scenario?.MapLinks != true) return;   // the house: rooms that touch need no line (Yunke 11-18)
        RegionCentres();
        var scale = new Vector2(world.X / regionImage.GetWidth(), world.Y / regionImage.GetHeight());
        var idxOf = regionPlace.ToDictionary(p => p.Value.Id, p => p.Key);
        var done = new HashSet<(int, int)>();
        bool still = S.Settings.ReducedMotion == true;
        foreach (var (a, la) in regionPlace)
            foreach (var bid in J.Items(J.Get(la.Raw, "connectedLocations")).Select(x => x.GetString() ?? ""))
            {
                if (!idxOf.TryGetValue(bid, out int b) || !done.Add((Math.Min(a, b), Math.Max(a, b)))) continue;
                if (scenario!.MapPairHidden(regionLabels[a], regionLabels[b])) continue;
                if (!regionCentres.TryGetValue(a, out var ca) || !regionCentres.TryGetValue(b, out var cb)) continue;
                // where the straight line from centre to centre leaves the first place and enters the second
                float tA = 0, tB = 1; const int N = 240;
                for (int k = 1; k <= N; k++) { float t = k / (float)N; if (MaskIdx(ca.Lerp(cb, t)) != a) { tA = t; break; } }
                for (int k = N - 1; k >= 0; k--) { float t = k / (float)N; if (MaskIdx(ca.Lerp(cb, t)) != b) { tB = t; break; } }
                float len = ca.DistanceTo(cb); float pad = 22f / Math.Max(1f, len);
                // places side by side (only the thin gap between them): a short bridge across it
                if (tB - tA < 40f / Math.Max(1f, len)) { float mid = (tA + tB) / 2; tA = mid - 46f / Math.Max(1f, len); tB = mid + 46f / Math.Max(1f, len); }
                else { tA -= pad; tB += pad; }
                var p0 = ca.Lerp(cb, Math.Clamp(tA, 0, 1)) * scale; var p1 = ca.Lerp(cb, Math.Clamp(tB, 0, 1)) * scale;
                var lb = regionPlace[b];
                // a road drawn for this pair (map.paths): a smooth curve through its points instead of the straight line
                var road = scenario!.MapPath(regionLabels[a], regionLabels[b]);
                Vector2[] pts = road is { Length: >= 2 } ? SmoothPath(road.Select(q => q * world).ToArray()) : new[] { p0, p1 };
                bool revealed = m.Revealed(la) && m.Revealed(lb);
                // a move you can make now: one end is where you stand, the other a place with a choice
                bool live = (a == (int)hereIdx && byAnchor.ContainsKey("loc:" + lb.Id)) || (b == (int)hereIdx && byAnchor.ContainsKey("loc:" + la.Id));
                var g = new Node2D();
                foreach (var l in LinkLines(g, live, revealed)) l.Points = pts;
                boardWorld!.AddChild(g);
                regionLinks.Add((a, b, g));
            }
        HighlightLinks(hoverRegion);
    }

    // a curve through the given points (Catmull-Rom handles), tessellated for Line2D
    static Vector2[] SmoothPath(Vector2[] p)
    {
        if (p.Length < 3) return p;
        var c = new Curve2D();
        for (int i = 0; i < p.Length; i++)
        {
            var prev = p[Math.Max(0, i - 1)]; var next = p[Math.Min(p.Length - 1, i + 1)];
            var t = (next - prev) / 6f;
            c.AddPoint(p[i], i == 0 ? Vector2.Zero : -t, i == p.Length - 1 ? Vector2.Zero : t);
        }
        return c.Tessellate(7, 1.5f);   // finer: the bends were visibly polygonal
    }

    // the place under the pointer: its own lines come forward, the others step back
    void HighlightLinks(int idx)
    {
        foreach (var (a, b, line) in regionLinks)
        {
            if (!IsInstanceValid(line)) continue;
            line.Modulate = idx < 0 ? Colors.White : (a == idx || b == idx) ? new Color(1.5f, 1.35f, 1.1f) : new Color(1, 1, 1, 0.35f);
        }
    }

    // The painted ground of a tile-mode map: its own edges fade, and so does whatever lies near the map frame on screen.
    ShaderMaterial? tileGroundMat;
    static Shader? groundFadeShader;
    static Shader GroundFade => groundFadeShader ??= new Shader { Code = @"shader_type canvas_item;
uniform vec2 fade = vec2(0.05, 0.08);
uniform vec4 clip_rect = vec4(0.0, 0.0, 1.0, 1.0);   // the map frame on screen, in SCREEN_UV units (x, y, w, h)
uniform vec2 clip_fade = vec2(0.02, 0.03);
float screen_fade(vec2 s) {
    float l = smoothstep(clip_rect.x, clip_rect.x + clip_fade.x, s.x), r = 1.0 - smoothstep(clip_rect.x + clip_rect.z - clip_fade.x, clip_rect.x + clip_rect.z, s.x);
    float t = smoothstep(clip_rect.y, clip_rect.y + clip_fade.y, s.y), b = 1.0 - smoothstep(clip_rect.y + clip_rect.w - clip_fade.y, clip_rect.y + clip_rect.w, s.y);
    return l * r * t * b;
}
void fragment() {
    vec4 c = texture(TEXTURE, UV) * COLOR;
    float a = smoothstep(0.0, fade.x, UV.x) * smoothstep(0.0, fade.x, 1.0 - UV.x) * smoothstep(0.0, fade.y, UV.y) * smoothstep(0.0, fade.y, 1.0 - UV.y);
    COLOR = vec4(c.rgb, c.a * a * screen_fade(SCREEN_UV));
}" };

    int RegionAt(Vector2 local, Vector2 world)
    {
        if (regionImage is null || world.X <= 0) return -1;
        int x = (int)(local.X / world.X * regionImage.GetWidth()), y = (int)(local.Y / world.Y * regionImage.GetHeight());
        if (x < 0 || y < 0 || x >= regionImage.GetWidth() || y >= regionImage.GetHeight()) return -1;
        return (int)Math.Round(regionImage.GetPixel(x, y).R * 255f / 20f) - 1;
    }

    void BoardPicture(TableModel m, Dictionary<string, Rect2> rects, Vector2 world)
    {
        // the ground with the region shader (replaces the plain painting added by Board())
        var ground = boardWorld!.GetChildren().OfType<TextureRect>().FirstOrDefault();
        string maskPath = System.IO.Path.Combine(App.I.Assets, "bg", scenario!.MapImage + "-regions.png");
        if (regionImageFor != maskPath) { regionImage = System.IO.File.Exists(maskPath) ? Godot.Image.LoadFromFile(maskPath) : null; regionImageFor = maskPath; groundMat = null; }
        regionLabels.Clear(); regionLabels.AddRange(scenario.HotspotLabels);
        regionPlace.Clear();
        var inPlay = m.Locations.ToDictionary(l => m.Label(l));
        var state = new float[16]; var act = new float[16]; float hereIdx = -1;
        for (int i = 0; i < regionLabels.Count && i < 16; i++)
        {
            if (!inPlay.TryGetValue(regionLabels[i], out var loc)) { state[i] = 2; continue; }
            regionPlace[i] = loc;
            state[i] = m.Revealed(loc) ? 0 : 1;
            act[i] = byAnchor.ContainsKey("loc:" + loc.Id) ? 1 : 0;
            if (loc.Id == m.MyLocation?.Id) hereIdx = i;
        }
        if (ground is not null && regionImage is not null)
        {
            if (groundMat is null)
            {
                groundMat = new ShaderMaterial { Shader = RegionLight };
                groundMat.SetShaderParameter("regions", ImageTexture.CreateFromImage(regionImage));
                // the soft glow coverage pictures (one channel per place); missing ones read as no place
                for (int k = 0; k < 3; k++)
                {
                    string gp = System.IO.Path.Combine(App.I.Assets, "bg", scenario.MapImage + "-glow" + k + ".png");
                    var gi = System.IO.File.Exists(gp) ? Godot.Image.LoadFromFile(gp) : null;
                    if (gi is null) { gi = Godot.Image.CreateEmpty(4, 4, false, Godot.Image.Format.Rgba8); }
                    groundMat.SetShaderParameter("glow" + k, ImageTexture.CreateFromImage(gi));
                }
            }
            groundMat.SetShaderParameter("state", state); groundMat.SetShaderParameter("act", act);
            groundMat.SetShaderParameter("fade", new Vector2(0.004f, 0.004f));   // the picture's own edges stay; the frame fades it
            groundMat.SetShaderParameter("here_idx", hereIdx);
            groundMat.SetShaderParameter("focus_idx", (float)hoverRegion); groundMat.SetShaderParameter("focus", regionFocus);
            var tex = ground.Texture; if (tex is not null) groundMat.SetShaderParameter("texel", new Vector2(1f / tex.GetWidth(), 1f / tex.GetHeight()));
            ground.Material = groundMat; ground.Modulate = Colors.White;
        }
        RegionLinks(m, world, hereIdx);
        // one input surface over the painting: the pointer is matched against the mask
        var surface = new Control { Size = world, MouseFilter = MouseFilterEnum.Stop };
        boardWorld.AddChild(surface);
        surface.GuiInput += e =>
        {
            if (e is InputEventMouseMotion mm0)
            {
                if (panStart is Vector2 ps0 && tilePress is not null)
                {
                    if (!panMoved && (mm0.GlobalPosition - ps0).Length() > 5) { panMoved = true; CancelPush(); }
                    if (panMoved) { boardTween?.Kill(); boardPan = panOrigin + (mm0.GlobalPosition - ps0); ApplyBoard(); }
                    surface.AcceptEvent(); return;
                }
                int ri0 = RegionAt(mm0.Position, world);
                // a place that can be clicked shows the hand (link) cursor; elsewhere the arrow
                surface.MouseDefaultCursorShape = regionPlace.ContainsKey(ri0) && !mapUnlocked ? CursorShape.PointingHand : CursorShape.Arrow;
                SetHoverRegion(m, ri0, surface);
            }
            else if (e is InputEventMouseButton { ButtonIndex: MouseButton.Left } mb)
            {
                int idx = RegionAt(mb.Position, world);
                if (mb.Pressed && !mb.DoubleClick) { panStart = mb.GlobalPosition; panOrigin = boardPan; panMoved = false; tilePress = "region:" + idx; ClosePopup(); }
                else if (!mb.Pressed)
                {
                    bool click = tilePress == "region:" + idx && !panMoved; panStart = null; tilePress = null;
                    if (click && !mapUnlocked)
                    {
                        // first click: the camera pushes in on the place (its numbers and what can be done there in words);
                        // a click on the place already pushed in acts on it; a click off every place goes back to the whole map
                        if (regionPlace.TryGetValue(idx, out var place))
                        {
                            if (pushedRegion == idx && byAnchor.ContainsKey("loc:" + place.Id)) ActOn("loc:" + place.Id, surface, place.Code);
                            else if (pushedRegion != idx) PushTo(m, idx);
                        }
                        else if (pushedRegion >= 0) CancelPush(true);
                    }
                }
                surface.AcceptEvent();
            }
            else if (e is InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Right } rb && regionPlace.TryGetValue(RegionAt(rb.Position, world), out var rp)) CardReader.Open(CoreCatalog.Normalize(rp.Code), printed: true, unrevealed: !m.Revealed(rp));
        };
        surface.MouseExited += () => SetHoverRegion(m, -1, surface);
        foreach (var (i, loc) in regionPlace) if (rects.TryGetValue(loc.Id, out var rr)) { var anchorNode = new Control { Position = rr.Position, Size = rr.Size, MouseFilter = MouseFilterEnum.Ignore }; boardWorld.AddChild(anchorNode); Anchor("loc:" + loc.Id, anchorNode); }
        // markers standing on each place. Small map: only who is there (portraits, an enemy count), never token numbers —
        // those come as words when the camera pushes in and in the hover box. Large map: every token and the shroud.
        foreach (var (i, loc) in regionPlace)
        {
            if (!rects.TryGetValue(loc.Id, out var r)) continue;
            // the markers in a row; on the large map the place's numbers as words under them
            var pin = new VBoxContainer { MouseFilter = MouseFilterEnum.Ignore, Alignment = BoxContainer.AlignmentMode.Center }; pin.AddThemeConstantOverride("separation", 2);
            var row = new HBoxContainer { MouseFilter = MouseFilterEnum.Ignore, Alignment = BoxContainer.AlignmentMode.Center }; row.AddThemeConstantOverride("separation", mapLarge ? 6 : 2); pin.AddChild(row);
            var people = m.InvestigatorsAt(loc.Id).ToList();
            var foes = m.EnemiesAt(loc.Id).ToList();   // only enemies placed at the location (Yunke 11-20 #12)
            var engaged = m.Me is null ? new HashSet<string>() : m.EngagedWith(m.Me).Select(e => e.Id).ToHashSet();
            int shownPeople = mapLarge ? people.Count : Math.Min(2, people.Count);
            float tokSize = mapLarge ? 140 : 56;   // height of the upright portrait card (Yunke 11-18 r7: twice as big on the large map)
            foreach (var inv in people.Take(shownPeople))
            {
                string ik = "inv:" + inv.Id; var icode = inv.Code;
                bool other = inv.Id != m.Me?.Id;
                CardView tok = null!;
                tok = InvToken(m, inv, tokSize, other && byAnchor.ContainsKey(ik), other ? () => ActOn(ik, tok, icode) : null);
                NoteInvToken(inv, tok);
                if (other) Anchor(ik, tok);
                row.AddChild(tok);
            }
            if (people.Count > shownPeople) row.AddChild(CountChip("+" + (people.Count - shownPeople), new Color("d9c9a3")));
            if (mapLarge)
            {
                // concealed mini-cards here (bug #16: they must be on the board to be fought), then enemies as small upright cards, the same size as the investigators (Yunke 11-18 r7)
                foreach (var cc in m.ConcealedAt(loc.Id)) row.AddChild(ConcealedMini(m, cc, tokSize));
                foreach (var en in foes) row.AddChild(EnemyMini(m, en, tokSize, engaged.Contains(en.Id)));
                foreach (var asst in m.AssetsAt(loc.Id))
                {
                    string ak = "asset:" + asst.Id; var acode = asst.Code;
                    var md = new Medallion { Code = asst.Code, Kind = "player", CustomMinimumSize = new Vector2(56, 56), Size = new Vector2(56, 56), Ring = new Color("93b8a0"), Actionable = byAnchor.ContainsKey(ak), TooltipText = m.Name(asst), NoZoom = true };
                    md.OnClick = () => ActOn(ak, md, acode); Anchor(ak, md); row.AddChild(md);
                }
                // clues, shroud and doom as words that are always shown, not tokens (Yunke 11-18 r7)
                var facts = new List<string>();
                if (m.Revealed(loc)) { facts.Add("线索 " + m.Clues(loc)); facts.Add("隐藏值 " + m.Shroud(loc)); }
                if (m.Doom(loc) > 0) facts.Add("毁灭 " + m.Doom(loc));
                if (facts.Count > 0)
                {
                    var info = Style.Text(string.Join(" · ", facts), 22, Style.HoverText, Style.Kai); info.HorizontalAlignment = HorizontalAlignment.Center; info.MouseFilter = MouseFilterEnum.Pass;
                    info.AddThemeConstantOverride("outline_size", 8); info.AddThemeColorOverride("font_outline_color", new Color(0, 0, 0, 0.92f));
                    pin.AddChild(info);
                }
            }
            else
            {
                // enemies as small upright cards the size of the investigators' (Yunke 11-18 r7); more than two fold into a chip
                foreach (var cc in m.ConcealedAt(loc.Id)) row.AddChild(ConcealedMini(m, cc, tokSize));
                foreach (var en in foes.Take(foes.Count > 2 ? 1 : 2)) row.AddChild(EnemyMini(m, en, tokSize, engaged.Contains(en.Id)));
                if (foes.Count > 2)
                {
                    foes = foes.Skip(1).ToList();
                    var chip = CountChip("敌+" + foes.Count, new Color("c0543e"));
                    // an enemy here can be acted on (fight one not engaged with you, evade…): the chip glows like any clickable
                    // thing and is that enemy's handle — one such enemy acts at once, several open the large map where each has
                    // its own marker (Codex review / Yunke 11-18: a folded stack must not hide an action)
                    var live = foes.Where(en => !engaged.Contains(en.Id) && byAnchor.ContainsKey("enemy:" + en.Id)).ToList();
                    if (live.Count > 0)
                    {
                        var csb = (StyleBoxFlat)chip.GetThemeStylebox("panel"); csb.BorderColor = Style.Amber; csb.ShadowColor = new Color(Style.Amber, 0.7f); csb.ShadowSize = 8;
                        chip.MouseDefaultCursorShape = CursorShape.PointingHand; chip.TooltipText = string.Join("、", live.Select(m.Name));
                        if (S.Settings.ReducedMotion != true) { var ct = chip.CreateTween().SetLoops(); ct.TweenProperty(chip, "modulate", new Color(1.35f, 1.2f, 0.95f), 0.8f).SetTrans(Tween.TransitionType.Sine); ct.TweenProperty(chip, "modulate", Colors.White, 0.8f).SetTrans(Tween.TransitionType.Sine); }
                        var one = live.Count == 1 ? live[0] : null;
                        chip.GuiInput += e => { if (e is InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Left }) { chip.AcceptEvent(); if (one is not null) ActOn("enemy:" + one.Id, chip, one.Code); else ToggleMap(); } };
                        foreach (var en in live) if (!anchors.ContainsKey("enemy:" + en.Id)) Anchor("enemy:" + en.Id, chip);
                    }
                    row.AddChild(chip);
                }
                foreach (var asst in m.AssetsAt(loc.Id))
                {
                    string ak = "asset:" + asst.Id; var acode = asst.Code;
                    var md = new Medallion { Code = asst.Code, Kind = "player", CustomMinimumSize = new Vector2(30, 30), Size = new Vector2(30, 30), Ring = new Color("93b8a0"), Actionable = byAnchor.ContainsKey(ak), TooltipText = m.Name(asst), NoZoom = true };
                    md.OnClick = () => ActOn(ak, md, acode); Anchor(ak, md); row.AddChild(md);
                }
            }
            // a marker over a place counts as the place under the pointer
            int ri = i;
            foreach (var child in row.GetChildren().OfType<Control>().Concat(pin.GetChildren().OfType<Control>().Where(c => c != row)))
            {
                if (child.MouseFilter == MouseFilterEnum.Ignore) child.MouseFilter = MouseFilterEnum.Pass;
                child.MouseEntered += () => SetHoverRegion(m, ri, child);
            }
            if (row.GetChildCount() == 0) row.Visible = false;
            if (row.GetChildCount() > 0 || pin.GetChildCount() > 1)
            {
                boardMarks!.AddChild(pin); pin.ResetSize(); string plabel = m.Label(loc); pins.Add((pin, r, plabel));
                if (mapUnlocked) MovablePin(m, pin, plabel);
            }
        }
    }

    // A concealed mini-card at a place: its back, upright, the size of an enemy's card. The choice that fights or evades it is
    // anchored to it ("conc:<id>"), so it glows and taps like an enemy (bug #16: on the phone a 隐匿迷你卡牌 had no picture to tap).
    Control ConcealedMini(TableModel m, Entity cc, float height)
    {
        string ck = "conc:" + cc.Id;
        float w = MathF.Round(height * 0.645f);
        bool act = byAnchor.ContainsKey(ck);
        var frame = new PanelContainer { CustomMinimumSize = new Vector2(w, height), Size = new Vector2(w, height), SizeFlagsHorizontal = SizeFlags.ShrinkCenter, SizeFlagsVertical = SizeFlags.ShrinkCenter,
            MouseFilter = act ? MouseFilterEnum.Stop : MouseFilterEnum.Ignore, TooltipText = "隐匿迷你卡牌" };
        frame.AddChild(new TextureRect { Texture = Ui.CardBack(true), ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize, StretchMode = TextureRect.StretchModeEnum.KeepAspectCovered,
            MouseFilter = MouseFilterEnum.Ignore, SizeFlagsHorizontal = SizeFlags.ExpandFill, SizeFlagsVertical = SizeFlags.ExpandFill });
        if (act)
        {
            // the table's usual sign of something to act on: a bright edge with a soft amber glow that breathes
            var sb = new StyleBoxFlat { BgColor = new Color(0, 0, 0, 0), BorderColor = new Color("ffcf6e"), ShadowColor = new Color(Style.Amber, 0.8f), ShadowSize = 10, AntiAliasing = true }; sb.SetBorderWidthAll(2); sb.SetCornerRadiusAll(6);
            frame.AddThemeStyleboxOverride("panel", sb);
            if (S.Settings.ReducedMotion != true) { var tw = frame.CreateTween().SetLoops(); tw.TweenProperty(frame, "self_modulate:a", 0.45f, 0.8f).SetTrans(Tween.TransitionType.Sine); tw.TweenProperty(frame, "self_modulate:a", 1f, 0.8f).SetTrans(Tween.TransitionType.Sine); }
            frame.MouseDefaultCursorShape = CursorShape.PointingHand;
            frame.GuiInput += e => { if (e is InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Left }) { frame.AcceptEvent(); ActOn(ck, frame, ""); } };
        }
        else frame.AddThemeStyleboxOverride("panel", new StyleBoxEmpty());
        Anchor(ck, frame);
        return frame;
    }

    // An enemy at a place: a small upright card of its picture, red rim, the same size as an investigator's (Yunke 11-18
    // r7: no round enemy tokens). Exhausted ones are dimmed; one that can be acted on glows and is its handle.
    CardView EnemyMini(TableModel m, Entity en, float height, bool mine)
    {
        string ek = "enemy:" + en.Id; var ecode = en.Code;
        float w = MathF.Round(height * 0.645f);
        bool act = !mine && byAnchor.ContainsKey(ek);
        var cv = new CardView { Code = en.Code, CustomMinimumSize = new Vector2(w, height), Size = new Vector2(w, height), ShowCost = false, ShowName = false, HoverZoom = true, Actionable = act,
            SizeFlagsHorizontal = SizeFlags.ShrinkCenter, SizeFlagsVertical = SizeFlags.ShrinkCenter, Rim = new Color("c0543e"), PeekExtra = () => EnemyExtra(en) };
        if (J.Get(en.Raw, "exhausted").ValueKind == JsonValueKind.True) cv.Modulate = new Color(0.6f, 0.6f, 0.6f);
        AddVictoryCorner(cv, Victory(ecode), 16);
        AddDoomCorner(cv, m.Doom(en), 22);
        AddKeyCorner(cv, en.Raw, 18, m.Name(en));   // a key the enemy holds (QQ #29)
        if (!mine) { cv.OnClick = () => ActOn(ek, cv, ecode); Anchor(ek, cv); }
        else cv.OnClick = () => CardReader.Open(ecode, printed: true);
        TuckAttached(m, cv, en, "AttachedToEnemy");
        return cv;
    }

    // The doom lying on a small card (an enemy's, Yunke 11-20: it stays on the 祭司 until it leaves play): a doom token with its
    // count on the card's lower-left corner
    static void AddDoomCorner(CardView cv, int doom, int size)
    {
        if (doom <= 0) return;
        var bd = CardView.Badge("doom", doom, size); bd.MouseFilter = MouseFilterEnum.Ignore; bd.ZIndex = 3; bd.SetMeta("doomBadge", true); cv.AddChild(bd);
        bd.Position = new Vector2(-2, cv.CustomMinimumSize.Y - size * 0.85f);   // inside the card's lower-left corner (a neighbouring tile covered it outside)
    }

    // Cards attached to an enemy (高耸的野兽 on a Brood of Yog-Sothoth: +1 attack, +1 health): tucked behind the enemy's card,
    // peeking out at its top right, each one pointable and usable — as the cards attached to a place are (Yunke 11-20 #20:
    // "附在…上的卡都要确实…显示"; QA 11-20 found the ones on enemies were not shown at all)
    void TuckAttached(TableModel m, CardView host, Entity owner, string tag)
    {
        int i = 0; var hs = host.CustomMinimumSize;
        foreach (var at in m.Attached(tag, owner.Id))
        {
            string akey = m.ThreatKey(at); var acode = at.Code;
            var sz = new Vector2(MathF.Round(hs.X * 0.62f), MathF.Round(hs.Y * 0.62f));
            var acv = new CardView { Code = acode, Key = akey, CustomMinimumSize = sz, Size = sz, ShowCost = false, ShowName = false, Rim = new Color("8a5aa8"), HoverZoom = true,
                Actionable = byAnchor.ContainsKey(akey) && !mapUnlocked, RotationDegrees = 8, ShowBehindParent = true, TooltipText = S.Name(acode) + "（附着在" + m.Name(owner) + "）",
                Position = new Vector2(hs.X - sz.X * 0.45f + i * 6, -sz.Y * 0.28f + i * 4) };
            acv.OnClick = () => ActOn(akey, acv, acode); Anchor(akey, acv);
            host.AddChild(acv); i++;
        }
    }

    // An investigator standing somewhere: a small upright portrait card (the portrait small card, whole), not a round token
    // (Yunke 11-18: no more round investigator tokens). Amber rim for the one whose view this is.
    CardView InvToken(TableModel m, Entity inv, float height, bool actionable, Action? click)
    {
        var cv = CardView.PortraitCard(inv.Code, height, actionable);
        cv.ShowName = false; cv.HoverZoom = true; cv.PeekExtra = () => InvestigatorExtra(inv);
        cv.Rim = inv.Id == m.Me?.Id ? Style.Amber : new Color("d9c9a3");
        AddKeyCorner(cv, inv.Raw, 18, m.Name(inv));   // the keys they hold, on their token on the map too (QQ #29)
        var icode = inv.Code;
        cv.OnClick = click ?? (() => CardReader.Open(icode, printed: true));
        return cv;
    }

    // Unlocked painted map (html "unlock locations", Yunke 11-18 #10): a place's markers are one handle, dragged to nudge
    // them on the picture; the offset is kept per scenario (as the location cards' are) and 复位 clears it.
    void MovablePin(TableModel m, Control pin, string label)
    {
        pin.MouseFilter = MouseFilterEnum.Stop; pin.MouseDefaultCursorShape = CursorShape.Move;
        foreach (var c in pin.GetChildren().OfType<Control>()) { c.MouseFilter = MouseFilterEnum.Ignore; if (c is CardView cv) cv.OnClick = null; }
        // movable: the move cursor, and the markers lifted a little brighter (a frame inside the row would be laid out as an item)
        pin.Modulate = new Color(1.25f, 1.18f, 1.0f);
        pin.GuiInput += e =>
        {
            if (e is InputEventMouseButton { ButtonIndex: MouseButton.Left } b)
            {
                if (b.Pressed) { dragLabel = label; dragStart = b.GlobalPosition; dragOffset0 = OffsetOf(label); dragMoved = false; }
                else if (dragLabel == label) { dragLabel = null; if (dragMoved) SaveOffsets(); }
                pin.AcceptEvent();
            }
            else if (e is InputEventMouseMotion mm && dragLabel == label)
            {
                var d = (mm.GlobalPosition - dragStart) / Math.Max(0.05f, boardZoom);
                if (!dragMoved && d.Length() * boardZoom > 4) dragMoved = true;
                if (dragMoved) { var o = dragOffset0 + d; Offsets(ScenarioCode(m))[label] = new[] { o.X, o.Y }; ApplyBoard(); }
                pin.AcceptEvent();
            }
        };
    }

    static Control CountChip(string text, Color ring)
    {
        var p = new PanelContainer { MouseFilter = MouseFilterEnum.Pass, SizeFlagsVertical = SizeFlags.ShrinkCenter };
        var sb = new StyleBoxFlat { BgColor = new Color(0.05f, 0.04f, 0.04f, 0.92f), BorderColor = ring, ContentMarginLeft = 7, ContentMarginRight = 7, ContentMarginTop = 1, ContentMarginBottom = 2, AntiAliasing = true };
        sb.SetBorderWidthAll(2); sb.SetCornerRadiusAll(12); p.AddThemeStyleboxOverride("panel", sb);
        var l = Style.Text(text, 15, Colors.White, Style.Kai); l.MouseFilter = MouseFilterEnum.Ignore; p.AddChild(l);
        return p;
    }

    static Control ShroudRoundel(int shroud, float d)
    {
        var ring = new Panel { MouseFilter = MouseFilterEnum.Pass, CustomMinimumSize = new Vector2(d, d), SizeFlagsVertical = SizeFlags.ShrinkCenter, TooltipText = "隐藏值 " + shroud };
        var rsb = new StyleBoxFlat { BgColor = new Color(0.1f, 0.14f, 0.2f, 0.95f), BorderColor = new Color("e0b56a"), AntiAliasing = true }; rsb.SetBorderWidthAll(2); rsb.SetCornerRadiusAll((int)d);
        ring.AddThemeStyleboxOverride("panel", rsb);
        var n = Style.Text(shroud.ToString(), (int)(d * 0.6f), Colors.White, Style.Symbols); n.MouseFilter = MouseFilterEnum.Ignore;
        ring.AddChild(n); Ui.CenterDigits(n);
        return ring;
    }

    // The pointer moved onto another place (or off every place): light it, show its box, and after a moment push the camera in.
    void SetHoverRegion(TableModel m, int idx, Control anchor)
    {
        if (!regionPlace.ContainsKey(idx)) idx = -1;
        if (idx == hoverRegion) return;
        hoverRegion = idx;
        HighlightLinks(idx);
        Peek.Hide();
        focusTween?.Kill();
        float target = idx >= 0 ? 1f : 0f;
        if (groundMat is not null)
        {
            if (idx >= 0) groundMat.SetShaderParameter("focus_idx", (float)idx);
            if (S.Settings.ReducedMotion == true) { regionFocus = target; groundMat.SetShaderParameter("focus", target); }
            else focusTween = CreateTween();
            focusTween?.TweenMethod(Callable.From<float>(v => { regionFocus = v; groundMat?.SetShaderParameter("focus", v); }), regionFocus, target, idx >= 0 ? 0.18f : 0.3f);
        }
        // Yunke 11-18: the pointer only lights the place and shows its box; the camera pushes in on a click (see the surface)
        if (idx < 0) return;
        var place = regionPlace[idx];
        Peek.Schedule(anchor, () => PlaceInfo(m, place), 0.25);
    }

    // Camera push-in on one place, with its name and the numbers that the small map leaves out, in words.
    void PushTo(TableModel m, int idx)
    {
        if (!regionPlace.TryGetValue(idx, out var loc)) return;
        var rects = BoardRects(m, out _);
        if (!rects.TryGetValue(loc.Id, out var r)) return;
        var free = BoardFree();
        float z = Math.Clamp(Math.Min(free.Size.X / (r.Size.X * 1.6f), free.Size.Y / (r.Size.Y * 1.6f)), MinZoom(), MaxZoom());
        pushedRegion = idx;
        MoveBoard(z, free.GetCenter() - r.GetCenter() * z);
        if (pushCaption is not null && IsInstanceValid(pushCaption)) pushCaption.QueueFree();
        bool rev = m.Revealed(loc);
        string text = m.Name(loc) + (rev ? "　隐藏值 " + m.Shroud(loc) + "　线索 " + m.Clues(loc) : "　未揭示") + (m.Doom(loc) > 0 ? "　毁灭 " + m.Doom(loc) : "") + (rev && Victory(loc.Code) > 0 ? "　胜利" + Victory(loc.Code) : "")
            + (m.EnemiesHere(loc.Id).Any() ? "　敌人 " + string.Join("、", m.EnemiesHere(loc.Id).Select(e => m.Name(e))) : "")
            + (byAnchor.TryGetValue("loc:" + loc.Id, out var can) && can.Count > 0 ? "　·　再点一次：" + string.Join(" / ", can.Select(c => c.Title.Split(" · ")[0]).Distinct()) : "");
        var cap = new PanelContainer { MouseFilter = MouseFilterEnum.Ignore, ZIndex = mapLarge ? 95 : 30 };   // over the focus-mode map too
        var sb = new StyleBoxFlat { BgColor = new Color(0.03f, 0.025f, 0.02f, 0.88f), BorderColor = new Color(Style.Amber, 0.7f), ContentMarginLeft = 14, ContentMarginRight = 14, ContentMarginTop = 4, ContentMarginBottom = 6 };
        sb.BorderWidthBottom = 2; sb.SetCornerRadiusAll(6); cap.AddThemeStyleboxOverride("panel", sb);
        var l = Style.Text(text, 19, Style.HoverText, Style.Kai); l.MouseFilter = MouseFilterEnum.Ignore; cap.AddChild(l);
        AddChild(cap); cap.ResetSize();
        cap.Position = new Vector2(free.Position.X + (free.Size.X - cap.Size.X) / 2, mapLarge ? 104 : free.Position.Y + 6);   // under the top bar on the full-screen map
        if (S.Settings.ReducedMotion != true) { cap.Modulate = new Color(1, 1, 1, 0); cap.CreateTween().TweenProperty(cap, "modulate:a", 1f, 0.2f); }
        pushCaption = cap;
    }

    void CancelPush(bool refit = false)
    {
        if (pushCaption is not null && IsInstanceValid(pushCaption)) pushCaption.QueueFree();
        pushCaption = null;
        if (pushedRegion >= 0 && refit) BoardFit();
        pushedRegion = -1;
    }

    // What the hover box says about a place: the location card (name, traits, text) plus where things stand now.
    Control PlaceInfo(TableModel m, Entity loc)
    {
        var col = new VBoxContainer { MouseFilter = MouseFilterEnum.Ignore }; col.AddThemeConstantOverride("separation", 4);
        bool rev = m.Revealed(loc);
        col.AddChild(Style.Text(rev ? "隐藏值 " + m.Shroud(loc) + "　·　线索 " + m.Clues(loc) + (m.Doom(loc) > 0 ? "　·　毁灭 " + m.Doom(loc) : "") : "未揭示", 19, Style.Paper));
        if (rev && Victory(loc.Code) is int pv and > 0) col.AddChild(Style.Text("胜利" + pv, 19, new Color("f0c860"), Style.Kai));
        var people = m.InvestigatorsAt(loc.Id).Select(i => m.Name(i)).ToList();
        if (people.Count > 0) col.AddChild(Style.Text("在此：" + string.Join("、", people), 18, Style.PaperDim));
        var foes = m.EnemiesHere(loc.Id).Select(e => m.Name(e)).ToList();
        if (foes.Count > 0) col.AddChild(Style.Text("敌人：" + string.Join("、", foes), 18, Style.Danger));
        if (byAnchor.TryGetValue("loc:" + loc.Id, out var acts) && acts.Count > 0)
            col.AddChild(Style.Text("可执行：" + string.Join(" / ", acts.Select(c => c.Title)), 18, Style.Amber, Style.Kai));
        // not revealed yet: only what its unrevealed side says (no shroud, clues or revealed text ahead of time)
        return Peek.CardBox(CoreCatalog.Normalize(loc.Code), col, m.Name(loc), back: !rev);
    }

    // ───────── camera
    float FitZoom()
    {
        var free = BoardFree();
        if (boardContent.Size.X <= 0) return 1f;
        float contain = Math.Min(free.Size.X / boardContent.Size.X, free.Size.Y / boardContent.Size.Y);
        // the floor that keeps the cards readable may grow the map past its frame, but never so far that a place ends up under
        // the hand (QA 11-20, 聚集于此 at 1280×720: the cellar was mostly hidden): at most what fits above the hand cards
        var room = BoardRoom();
        float roomContain = Math.Min(room.Size.X / boardContent.Size.X, room.Size.Y / boardContent.Size.Y);
        float floor = Math.Min(tileReadZoom * (mapLarge ? 1.6f : 1f), Math.Max(contain, roomContain));
        return scenario?.WholePicture == true ? contain : Math.Max(floor, Math.Min(mapLarge ? 2.4f : 1.4f, contain));   // the full map fills the screen with cards (Yunke 11-18 r8)
    }
    float tileReadZoom;
    // the corner map is never smaller than its frame (it can be zoomed in and dragged, not zoomed out past it); the full-screen
    // map starts fitted to the screen and may be zoomed in well past its edges
    float MinZoom()
    {
        var free = BoardFree(); if (boardContent.Size.X <= 0) return 1f;
        if (scenario?.PaintedMap != true) return FitZoom() * 0.55f;   // card maps: out to well past the whole map
        return mapLarge ? FitZoom() : Math.Max(free.Size.X / boardContent.Size.X, free.Size.Y / boardContent.Size.Y);   // full map: never smaller than all of it
    }
    float MaxZoom() => Math.Max(MinZoom() * 5f, 1.6f);

    // keep the map covering its frame where it is larger than the frame, centred where it is smaller
    Vector2 ClampPan(float z, Vector2 pan)
    {
        var f = BoardFree(); var c = boardContent; if (c.Size.X <= 0) return pan;
        var size = c.Size * z;
        // (bounds ordered first: when the map exactly fills the frame, rounding can put the lower bound a hair above the upper)
        static float Clamp(float v, float a, float b) => Math.Clamp(v, Math.Min(a, b), Math.Max(a, b));
        if (Boundless)
        {
            // anywhere on the screen, as long as a corner of the map stays in sight
            var vp = GetViewportRect().Size; const float keep = 160;
            pan.X = Clamp(pan.X, keep - c.End.X * z, vp.X - keep - c.Position.X * z);
            pan.Y = Clamp(pan.Y, keep - c.End.Y * z, vp.Y - keep - c.Position.Y * z);
            return pan;
        }
        pan.X = size.X >= f.Size.X ? Clamp(pan.X, f.End.X - c.End.X * z, f.Position.X - c.Position.X * z) : f.GetCenter().X - c.GetCenter().X * z;
        pan.Y = size.Y >= f.Size.Y ? Clamp(pan.Y, f.End.Y - c.End.Y * z, f.Position.Y - c.Position.Y * z) : f.GetCenter().Y - c.GetCenter().Y * z;
        return pan;
    }

    void ApplyBoard()
    {
        if (boardWorld is null) return;
        boardPan = ClampPan(boardZoom, boardPan);
        boardWorld.Scale = new Vector2(boardZoom, boardZoom);
        ScaleLinkWidths();
        // the frame: exactly the map's area (its edges fade, no ring and no empty margin)
        var frame = Boundless ? new Rect2(Vector2.Zero, GetViewportRect().Size) : BoardFree();
        if (boardClip is not null) { boardClip.Position = frame.Position; boardClip.Size = frame.Size; }
        clipOrigin = frame.Position;
        // the map turns transparent over the last 36 px inside the frame (no dark ring): the frame in screen units
        var vpz = GetViewportRect().Size;
        var cr = new Vector4(frame.Position.X / vpz.X, frame.Position.Y / vpz.Y, frame.Size.X / vpz.X, frame.Size.Y / vpz.Y);
        float fadePx = mapLarge ? 40f : 22f;
        var cf = new Vector2(fadePx / vpz.X, fadePx / vpz.Y);
        foreach (var mat in new[] { groundMat, tileGroundMat }) { mat?.SetShaderParameter("clip_rect", cr); mat?.SetShaderParameter("clip_fade", cf); }
        boardWorld.Position = boardPan - clipOrigin;
        foreach (var (node, room, label) in marks)
        {
            if (!IsInstanceValid(node)) continue;
            // the card covers its room (a little inside it) and scales with the map like everything else on it (Yunke 11-20 #9:
            // zooming must scale every element in proportion; the cards used to stop at a readable size while the lines went on)
            var want = room.Size * boardZoom * 0.92f;
            float k = Math.Clamp(Math.Min(want.X / TileDesign.X, want.Y / TileDesign.Y), 0.05f, 12f);
            node.Scale = new Vector2(k, k);
            node.Position = (boardPan + (room.GetCenter() + OffsetOf(label)) * boardZoom - TileDesign * k / 2 - clipOrigin).Round();
        }
        foreach (var (node, room, label) in pins)
        {
            if (!IsInstanceValid(node)) continue;
            // markers scale with the map too (Yunke 11-20 #9)
            float k = Math.Clamp(boardZoom / Math.Max(0.01f, boardFitZoom) * 0.85f, 0.05f, 12f);
            node.Scale = new Vector2(k, k);
            var size = node.Size * k;
            node.Position = (boardPan + (new Vector2(room.GetCenter().X, room.End.Y) + OffsetOf(label)) * boardZoom - new Vector2(size.X / 2, size.Y + 6) - clipOrigin).Round();
        }
    }

    void MoveBoard(float zoom, Vector2 pan, bool animate = true)
    {
        boardTween?.Kill();
        zoom = Math.Clamp(zoom, MinZoom(), MaxZoom()); pan = ClampPan(zoom, pan);
        if (!animate || S.Settings.ReducedMotion == true) { boardZoom = zoom; boardPan = pan; ApplyBoard(); return; }
        float z0 = boardZoom; var p0 = boardPan;
        boardTween = CreateTween();
        boardTween.TweenMethod(Callable.From<float>(t => { boardZoom = Mathf.Lerp(z0, zoom, t); boardPan = p0.Lerp(pan, t); ApplyBoard(); }), 0f, 1f, 0.35f)
            .SetTrans(Tween.TransitionType.Cubic).SetEase(Tween.EaseType.Out);
    }

    // the whole map in its corner
    void BoardFit(bool animate = true)
    {
        // the whole map: whatever place the camera was pushed in on is let go, with its caption
        if (pushCaption is not null && IsInstanceValid(pushCaption)) pushCaption.QueueFree();
        pushCaption = null; pushedRegion = -1;
        boardFitZoom = FitZoom(); var free = BoardFree();
        float z0 = Math.Max(boardFitZoom, MinZoom());
        // a map grown past its frame (the readable-card floor) is centred in the larger room, so it does not spill under the hand
        if (boardContent.Size.Y * z0 > free.Size.Y + 1) free = BoardRoom();
        MoveBoard(z0, free.GetCenter() - boardContent.GetCenter() * z0, animate);
    }

    // zoom by a factor around a screen point (it stays under the pointer)
    void ZoomAt(Vector2 p, float factor)
    {
        float z = Math.Clamp(boardZoom * factor, MinZoom(), MaxZoom());
        MoveBoard(z, p - (p - boardPan) * (z / boardZoom), false);
    }

    // centre on where you stand
    void BoardFocusHere()
    {
        if (model?.MyLocation is not { } loc || boardWorld is null) return;
        var rects = BoardRects(model, out _);
        if (!rects.TryGetValue(loc.Id, out var r)) return;
        float z = Math.Max(boardZoom, boardFitZoom * 1.5f);
        MoveBoard(z, BoardFree().GetCenter() - r.GetCenter() * z);
    }

    // The map on screen (its content rect, grown a little): presses and the wheel only act on the map inside it, so the rest
    // of the table does not pan or zoom a map that sits in the corner.
    bool OnMap(Vector2 p) => mapShown && Boundless || mapShown && BoardFree().Grow(18).HasPoint(p) && new Rect2(boardPan + boardContent.Position * boardZoom, boardContent.Size * boardZoom).Grow(24).HasPoint(p);

    void BoardInput(InputEvent e)
    {
        if (e is InputEventMouseButton mb)
        {
            if (!mb.Pressed && mb.ButtonIndex is MouseButton.Left or MouseButton.Middle or MouseButton.Right) { panStart = null; return; }
            if (!OnMap(mb.Position)) return;
            if (mb.Pressed && mb.ButtonIndex == MouseButton.WheelUp) { ZoomAt(mb.Position, 1.12f); AcceptEvent(); return; }
            if (mb.Pressed && mb.ButtonIndex == MouseButton.WheelDown) { ZoomAt(mb.Position, 1 / 1.12f); AcceptEvent(); return; }
            if (mb.ButtonIndex is MouseButton.Left or MouseButton.Middle or MouseButton.Right)
            {
                if (mb.Pressed && mb.DoubleClick && mb.ButtonIndex == MouseButton.Left)
                {
                    // double-click: zoom in on the spot, or back to the whole map
                    if (boardZoom > boardFitZoom * 1.4f) BoardFit();
                    else { float z = boardFitZoom * 2f; MoveBoard(z, mb.Position - (mb.Position - boardPan) * (z / boardZoom)); }
                    return;
                }
                if (mb.Pressed) { panStart = mb.Position; panOrigin = boardPan; panMoved = false; ClosePopup(); }
            }
        }
        else if (e is InputEventMouseMotion mm && panStart is Vector2 s)
        {
            if (!panMoved && (mm.Position - s).Length() > 5) panMoved = true;
            if (panMoved) { boardTween?.Kill(); boardPan = panOrigin + (mm.Position - s); ApplyBoard(); }
        }
    }
}
