using System.Text.Json;
using Godot;
using Gathering;

namespace ArkhamUI;

// The deck list in the workshop's preview (Yunke 11-19): three ways to look at the same cards —
//  · 横条 (strips, the default): one bar per card, like a deck list in a digital card game — cost roundel in the card's class
//    colour, name and level, a band of the card's art on the right fading into the bar, the copies at the end; two columns;
//  · 小卡: the table's small cards in a grid;
//  · 大卡: the printed cards in a grid.
// Art: the redrawn illustration where the html version has one, else the art region cut from the printed card (支援 / 事件 /
// 技能 each print their art in a different place). Printed cards of later cycles are read from disk one by one, a few per
// frame, so switching mode does not stall.
public static class DeckPreview
{
    public enum Mode { Strips, Small, Large }
    static Services S => App.I.S!;

    public static Mode Saved => S.Settings.DeckPreviewMode switch { "small" => Mode.Small, "large" => Mode.Large, _ => Mode.Strips };
    public static void Save(Mode m) { S.Settings.DeckPreviewMode = m switch { Mode.Small => "small", Mode.Large => "large", _ => "strips" }; S.Settings.Save(); }

    // ───────── art
    // where the illustration sits on a printed card, as fractions of its width and height (750×1050 scans)
    // (checked card by card on contact sheets of every cycle: tools/dev/crop_sheets.py)
    static Rect2 ArtFrame(string type, string code)
    {
        // cards printed in another layout (Yunke 11-20: some enemies put the art on top and the text below — the spider's legs
        // of cycle 5, Cthulhu, the cosmic emissaries; 8L-08 is a landscape card): their own region, as fractions of the card
        if (Frames().TryGetValue(CoreCatalog.Normalize(code), out var own)) return own;
        var f = type switch
        {
            "asset" => new Rect2(0.13f, 0.11f, 0.84f, 0.355f),    // below the title ribbon some assets carry over their art
            "event" => new Rect2(0.15f, 0.03f, 0.83f, 0.44f),     // above the class shield at the art's foot
            "skill" => new Rect2(0.13f, 0.085f, 0.85f, 0.46f),
            "enemy" => new Rect2(0.04f, 0.64f, 0.92f, 0.30f),     // enemies print their art in the lower half (below the parchment edge)
            _ => new Rect2(0.10f, 0.08f, 0.85f, 0.40f),           // treacheries (weaknesses)
        };
        // the novella / promo / parallel layouts (90xxx, 98xxx) print a title and a subtitle band over the top of the art
        if (code.StartsWith("98") || code.StartsWith("90")) f = new Rect2(f.Position.X, f.Position.Y + 0.05f, f.Size.X, f.Size.Y - 0.05f);
        return f;
    }

    // app/assets/build-data/art-frames.json {code: [x, y, w, h]} (tools: inbox/art-crop-task-20261119/frames, checked by eye)
    static Dictionary<string, Rect2>? frames;
    static Dictionary<string, Rect2> Frames()
    {
        if (frames is not null) return frames;
        frames = new();
        try
        {
            var file = System.IO.Path.Combine(App.I.Assets, "build-data", "art-frames.json");
            if (System.IO.File.Exists(file))
                foreach (var p in J.Props(JsonDocument.Parse(System.IO.File.ReadAllText(file)).RootElement))
                    if (p.Value.ValueKind == JsonValueKind.Array && p.Value.GetArrayLength() == 4)
                        frames[p.Name] = new Rect2((float)p.Value[0].GetDouble(), (float)p.Value[1].GetDouble(), (float)p.Value[2].GetDouble(), (float)p.Value[3].GetDouble());
        }
        catch (Exception e) { GD.PushWarning("art frames: " + e.Message); }
        return frames;
    }

    static readonly Dictionary<string, Texture2D?> artCache = new();
    // The card's illustration: the redrawn picture, or its art region cut from the printed card. Null until loaded.
    public static Texture2D? Art(string code, string type, bool load = true)
    {
        code = CoreCatalog.Normalize(code);
        if (artCache.TryGetValue(code, out var hit)) return hit;
        if (artCache.Count > 400) artCache.Clear();
        if (Ui.Art(code) is { } drawn) return artCache[code] = drawn;
        if (!load) return null;
        var full = Ui.Card(code);
        if (full is null) return artCache[code] = null;
        var f = ArtFrame(type, code); float w = full.GetWidth(), h = full.GetHeight();
        return artCache[code] = new AtlasTexture { Atlas = full, Region = new Rect2(f.Position.X * w, f.Position.Y * h, f.Size.X * w, f.Size.Y * h) };
    }
    // Where the subject of each cut illustration is (tools/dev/art_focus.py: faces, saliency and detail found in the art region):
    // [fx, fy] as fractions of the region — the small card's window is centred on fx, the strip's band on fy (Yunke 11-19:
    // the middle of the printed art often cut the subject off).
    static Dictionary<string, float[]>? focus;
    static float[]? Focus(string code)
    {
        if (focus is null)
        {
            focus = new();
            try
            {
                var file = System.IO.Path.Combine(App.I.Assets, "build-data", "art-focus.json");
                if (System.IO.File.Exists(file))
                    foreach (var p in J.Props(JsonDocument.Parse(System.IO.File.ReadAllText(file)).RootElement))
                        if (p.Value.ValueKind == JsonValueKind.Array && p.Value.GetArrayLength() == 2) focus[p.Name] = new[] { (float)p.Value[0].GetDouble(), (float)p.Value[1].GetDouble() };
            }
            catch (Exception e) { GD.PushWarning("art focus: " + e.Message); }
        }
        return focus.TryGetValue(CoreCatalog.Normalize(code), out var f) ? f : null;
    }

    public static bool HasFocus(string code) => Focus(code) is not null || Picked(code, "small") is not null;

    // Hand-picked boxes (Yunke 11-19: an outside helper chose them card by card, tools/dev/import_crops.py checked them):
    // app/assets/build-data/art-crops.json {code: {"small": [x, y, w, h], "strip": [x, y, w, h]}} as fractions of the card image.
    static Dictionary<string, Dictionary<string, float[]>>? picked;
    static float[]? Picked(string code, string kind)
    {
        if (picked is null)
        {
            picked = new();
            try
            {
                var file = System.IO.Path.Combine(App.I.Assets, "build-data", "art-crops.json");
                if (System.IO.File.Exists(file))
                    foreach (var p in J.Props(JsonDocument.Parse(System.IO.File.ReadAllText(file)).RootElement))
                    {
                        var boxes = new Dictionary<string, float[]>();
                        foreach (var b in J.Props(p.Value))
                            if (b.Value.ValueKind == JsonValueKind.Array && b.Value.GetArrayLength() == 4) boxes[b.Name] = b.Value.EnumerateArray().Select(x => (float)x.GetDouble()).ToArray();
                        picked[p.Name] = boxes;
                    }
            }
            catch (Exception e) { GD.PushWarning("art crops: " + e.Message); }
        }
        return picked.TryGetValue(CoreCatalog.Normalize(code), out var d) && d.TryGetValue(kind, out var box) ? box : null;
    }
    static Rect2 Scale(float[] b, Texture2D atlas) => new(b[0] * atlas.GetWidth(), b[1] * atlas.GetHeight(), b[2] * atlas.GetWidth(), b[3] * atlas.GetHeight());
    // the largest rectangle of the given aspect inside the box, around its centre, kept inside the art region
    static Rect2 Within(Rect2 box, float aspect, Rect2 bounds)
    {
        float w = box.Size.X, h = box.Size.Y;
        if (w / h > aspect) w = h * aspect; else h = w / aspect;
        var c = box.GetCenter();
        float x = Math.Clamp(c.X - w / 2, bounds.Position.X, Math.Max(bounds.Position.X, bounds.End.X - w));
        float y = Math.Clamp(c.Y - h / 2, bounds.Position.Y, Math.Max(bounds.Position.Y, bounds.End.Y - h));
        return new Rect2(x, y, w, h);
    }

    // The cut illustration at a given width / height, around its subject (the whole region when it is narrower already).
    public static Texture2D? ArtFor(string code, string type, float aspect)
    {
        var art = Art(code, type);
        if (art is not AtlasTexture at || aspect <= 0) return art;
        // a hand-picked small-card box (art-crops.json) wins; another aspect is cut from it around its centre
        if (Picked(code, "small") is { } pick) return new AtlasTexture { Atlas = at.Atlas, Region = Within(Scale(pick, at.Atlas), aspect, at.Region) };
        var r = at.Region; float fx = Focus(code)?[0] ?? 0.5f;
        if (r.Size.X / r.Size.Y <= aspect) return art;
        float w = r.Size.Y * aspect;
        float x = Math.Clamp(r.Position.X + fx * r.Size.X - w / 2, r.Position.X, r.End.X - w);
        return new AtlasTexture { Atlas = at.Atlas, Region = new Rect2(x, r.Position.Y, w, r.Size.Y) };
    }

    static bool Ready(string code) => artCache.ContainsKey(CoreCatalog.Normalize(code)) || Ui.Art(CoreCatalog.Normalize(code)) is not null;

    // a band of the art at the strip's proportions, a little above the middle (faces and objects sit there)
    static Texture2D? Band(string code, Texture2D? art, float aspect)
    {
        if (art is null) return null;
        Rect2 src = art is AtlasTexture at ? at.Region : new Rect2(0, 0, art.GetWidth(), art.GetHeight());
        var atlas = art is AtlasTexture a2 ? a2.Atlas : art;
        if (art is AtlasTexture && Picked(code, "strip") is { } pick) return new AtlasTexture { Atlas = atlas, Region = Within(Scale(pick, atlas), aspect, src) };
        float bh = Math.Min(src.Size.Y, src.Size.X / aspect);
        float cy = src.Position.Y + src.Size.Y * (art is AtlasTexture && Focus(code) is { } f ? f[1] : 0.42f);
        float y = Math.Clamp(cy - bh / 2, src.Position.Y, src.End.Y - bh);
        return new AtlasTexture { Atlas = atlas, Region = new Rect2(src.Position.X, y, src.Size.X, bh) };
    }

    // ───────── the list
    static string Group(JsonElement c) => J.String(c, "subtype_code") is "weakness" or "basicweakness" ? "weakness" : J.String(c, "type_code");
    static int GroupOrder(string g) => g switch { "asset" => 0, "event" => 1, "skill" => 2, "weakness" => 4, _ => 3 };
    static string GroupName(string g) => g switch
    {
        "weakness" => "弱点",
        // the decks beside the main one, named as the editor's deck switch names them (Yunke 11-22: they were not shown here)
        "sideSlots" => "侧卡组", "extraSlots" => "额外卡组", "bonded" => "附属卡",
        _ => BuildDecks.TypeName(g),
    };

    static List<JsonElement> ByCost(IEnumerable<JsonElement> cards) =>
        cards.OrderBy(c => J.Get(c, "cost").ValueKind == JsonValueKind.Number ? J.Int(c, "cost") : 99).ThenBy(c => J.String(c, "name")).ToList();

    // the main deck by card type; then the side deck, an investigator's extra deck and the bonded cards, each as one group
    static List<(string Group, List<JsonElement> Cards)> Groups(JsonElement view)
    {
        var groups = J.Items(J.Get(view, "slots")).GroupBy(Group).OrderBy(g => GroupOrder(g.Key)).Select(g => (g.Key, ByCost(g))).ToList();
        foreach (var key in new[] { "extraSlots", "sideSlots", "bonded" })
        {
            var cards = J.Items(J.Get(view, key)).Where(c => J.Int(c, "quantity") > 0).ToList();
            if (cards.Count > 0) groups.Add((key, ByCost(cards)));
        }
        return groups;
    }

    public static Control Build(JsonElement view, Mode mode, float width, Func<string, string> name)
    {
        var groups = Groups(view);
        // the customizable cards' sheets (what this deck has ticked), for the hover boxes (Yunke 11-19)
        sheets.Clear();
        string deckId = J.String(view, "id");
        foreach (var c in J.Items(J.Get(view, "slots")).Where(c => J.Get(c, "customization_options").ValueKind == JsonValueKind.True))
            _ = FetchSheet(deckId, CoreCatalog.Normalize(J.String(c, "code")));
        var loads = new List<(string Code, string Type, Action<Texture2D?> Set)>();
        Control body = mode switch
        {
            Mode.Small => Grid(groups, width, small: true, loads),
            Mode.Large => Grid(groups, width, small: false, loads),
            _ => StripColumns(groups, width, loads),
        };
        // the printed cards not loaded yet come in a few per frame
        if (loads.Count > 0) body.Ready += () => _ = LoadLater(body, loads);
        return body;
    }

    static async Task LoadLater(Control owner, List<(string Code, string Type, Action<Texture2D?> Set)> loads)
    {
        int n = 0;
        foreach (var (code, type, set) in loads)
        {
            if (!GodotObject.IsInstanceValid(owner) || !owner.IsInsideTree()) return;
            set(Art(code, type));
            if (++n % 3 == 0) await owner.ToSignal(owner.GetTree(), SceneTree.SignalName.ProcessFrame);
        }
    }

    // ── 横条: two columns; a group is not split unless it has to be, and a column never starts on a lone header
    const float StripH = 40, StripGap = 4;
    static Control StripColumns(List<(string Group, List<JsonElement> Cards)> groups, float width, List<(string, string, Action<Texture2D?>)> loads)
    {
        float colW = (width - 28) / 2;
        var rows = new List<(string? Header, JsonElement Card)>();
        foreach (var (g, cards) in groups)
        {
            rows.Add((GroupName(g) + "（" + cards.Sum(c => J.Int(c, "quantity")) + "）", default));
            foreach (var c in cards) rows.Add((null, c));
        }
        int split = (rows.Count + 1) / 2;
        // prefer a group boundary near the middle; otherwise split inside a group (its header repeats as 续)
        int best = -1;
        for (int i = 1; i < rows.Count; i++) if (rows[i].Header is not null && Math.Abs(i - split) <= 3 && (best < 0 || Math.Abs(i - split) < Math.Abs(best - split))) best = i;
        if (best > 0) split = best;
        else if (split < rows.Count && rows[split - 1].Header is not null) split--;

        var cols = new HBoxContainer(); cols.AddThemeConstantOverride("separation", 28);
        for (int k = 0; k < 2; k++)
        {
            var col = new VBoxContainer { CustomMinimumSize = new Vector2(colW, 0), SizeFlagsVertical = Control.SizeFlags.ShrinkBegin }; col.AddThemeConstantOverride("separation", (int)StripGap); cols.AddChild(col);
            var part = k == 0 ? rows.Take(split) : rows.Skip(split);
            bool first = true;
            foreach (var (header, card) in part)
            {
                if (header is not null) { col.AddChild(Header(header)); first = false; continue; }
                if (first && k == 1)
                {
                    // the second column starts inside a group: name the group again
                    int at = rows.FindLastIndex(split - 1, r => r.Header is not null);
                    if (at >= 0) col.AddChild(Header(rows[at].Header!.Split('（')[0] + "（续）"));
                }
                first = false;
                col.AddChild(Strip(card, colW, loads));
            }
        }
        return cols;
    }

    static Control Header(string text)
    {
        var l = Style.Text(text, 20, Style.Brass, Style.Kai); l.CustomMinimumSize = new Vector2(0, 32); l.VerticalAlignment = VerticalAlignment.Bottom;
        return l;
    }

    static Color ClassColor(JsonElement c) => J.String(c, "faction_code") switch
    {
        "guardian" => new Color("3f6fb0"), "seeker" => new Color("c08a3e"), "rogue" => new Color("3f8f5a"),
        "mystic" => new Color("7a55b0"), "survivor" => new Color("b04848"), "mythos" => new Color("6a4a3a"), _ => new Color("8a8478"),
    };

    static Shader? fadeShader;
    // the art band fades into the bar from the left (alpha 0 at the band's left edge, full at ~60%)
    static ShaderMaterial Fade => new() { Shader = fadeShader ??= new Shader { Code = @"shader_type canvas_item;
void fragment(){ vec4 c = texture(TEXTURE, UV); float a = smoothstep(0.0, 0.62, UV.x); COLOR = vec4(c.rgb, c.a * a); }" } };

    public static Control Strip(JsonElement c, float w, List<(string, string, Action<Texture2D?>)>? loads = null)
    {
        string code = CoreCatalog.Normalize(J.String(c, "code")), type = J.String(c, "type_code");
        int qty = J.Int(c, "quantity"), xp = J.Int(c, "xp");
        bool weak = Group(c) == "weakness";
        var tint = weak ? new Color("8a2f2a") : ClassColor(c);

        var box = new Panel { CustomMinimumSize = new Vector2(w, StripH), MouseFilter = Control.MouseFilterEnum.Stop, MouseDefaultCursorShape = Control.CursorShape.PointingHand, ClipChildren = CanvasItem.ClipChildrenMode.AndDraw };
        var sb = new StyleBoxFlat { BgColor = weak ? new Color(0.16f, 0.04f, 0.035f, 0.92f) : new Color(0.06f, 0.05f, 0.045f, 0.9f), BorderColor = new Color(tint, 0.85f), AntiAliasing = true };
        sb.SetCornerRadiusAll(8); sb.BorderWidthLeft = 4; sb.BorderWidthTop = sb.BorderWidthBottom = sb.BorderWidthRight = 1;
        sb.BorderColor = new Color(tint, 0.9f);
        box.AddThemeStyleboxOverride("panel", sb);

        // the art band on the right, under everything else
        float artW = w * 0.46f, qW = 40;
        var art = new TextureRect { ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize, StretchMode = TextureRect.StretchModeEnum.KeepAspectCovered, MouseFilter = Control.MouseFilterEnum.Ignore, Material = Fade, TextureFilter = CanvasItem.TextureFilterEnum.LinearWithMipmaps };
        art.Position = new Vector2(w - artW - qW, 1); art.Size = new Vector2(artW, StripH - 2);
        box.AddChild(art);
        float aspect = artW / (StripH - 2);
        if (Ready(code)) art.Texture = Band(code, Art(code, type), aspect);
        else loads?.Add((code, type, t => { if (GodotObject.IsInstanceValid(art)) art.Texture = Band(code, t, aspect); }));

        // cost roundel in the class colour
        float d = 30;
        var cost = new Panel { MouseFilter = Control.MouseFilterEnum.Ignore, Position = new Vector2(9, (StripH - d) / 2), Size = new Vector2(d, d) };
        var csb = new StyleBoxFlat { BgColor = tint.Darkened(0.35f), BorderColor = new Color("e0b56a"), AntiAliasing = true }; csb.SetBorderWidthAll(2); csb.SetCornerRadiusAll((int)d); cost.AddThemeStyleboxOverride("panel", csb);
        bool hasCost = J.Get(c, "cost").ValueKind == JsonValueKind.Number;
        int cv = J.Int(c, "cost");
        var cn = Style.Text(hasCost ? (cv < 0 ? "X" : cv.ToString()) : "–", 18, Colors.White, Style.Symbols);
        cn.AddThemeConstantOverride("outline_size", 4); cn.AddThemeColorOverride("font_outline_color", new Color(0, 0, 0, 0.8f));
        cost.AddChild(cn); Ui.CenterDigits(cn);
        box.AddChild(cost);

        // name, level pips and small marks (专属 / 可定制)
        var line = new HBoxContainer { MouseFilter = Control.MouseFilterEnum.Ignore, Position = new Vector2(48, 0), Size = new Vector2(w - 48 - qW - 4, StripH) }; line.AddThemeConstantOverride("separation", 6);
        var nm = Style.Text(J.String(c, "name", code), 19, Style.HoverText, Style.Kai); nm.VerticalAlignment = VerticalAlignment.Center;   // Chinese names are short: no clipping (a clipped label in a box shrinks to nothing)
        nm.AddThemeConstantOverride("outline_size", 6); nm.AddThemeColorOverride("font_outline_color", new Color(0, 0, 0, 0.9f));
        nm.SizeFlagsHorizontal = Control.SizeFlags.ShrinkBegin; line.AddChild(nm);
        if (xp > 0)
        {
            var pips = Style.Text(new string('•', Math.Min(5, xp)), 20, new Color("f0d9a0"), Style.Symbols); pips.VerticalAlignment = VerticalAlignment.Center;
            pips.AddThemeConstantOverride("outline_size", 4); pips.AddThemeColorOverride("font_outline_color", Colors.Black); line.AddChild(pips);
        }
        var marks = new List<string>();
        if (J.Get(c, "restrictions").ValueKind == JsonValueKind.Object && J.Get(J.Get(c, "restrictions"), "investigator").ValueKind != JsonValueKind.Undefined) marks.Add("专属");
        if (J.Get(c, "customization_options").ValueKind == JsonValueKind.True) marks.Add("可定制");
        if (marks.Count > 0)
        {
            var mk = Style.Text(string.Join(" ", marks), 14, Style.PaperDim, Style.Kai); mk.VerticalAlignment = VerticalAlignment.Center;
            mk.AddThemeConstantOverride("outline_size", 4); mk.AddThemeColorOverride("font_outline_color", Colors.Black); line.AddChild(mk);
        }
        box.AddChild(line);

        // the copies, in a dark box at the end
        var q = new Panel { MouseFilter = Control.MouseFilterEnum.Ignore, Position = new Vector2(w - qW, 0), Size = new Vector2(qW, StripH) };
        var qsb = new StyleBoxFlat { BgColor = new Color(0.03f, 0.025f, 0.02f, 0.95f) }; qsb.CornerRadiusTopRight = qsb.CornerRadiusBottomRight = 8; q.AddThemeStyleboxOverride("panel", qsb);
        var qt = Style.Text(qty > 1 ? qty.ToString() : J.Int(c, "deck_limit") == 1 || J.Get(c, "exceptional").ValueKind == JsonValueKind.True ? "★" : "1", 19, new Color("f0c060"), Style.Symbols);
        qt.HorizontalAlignment = HorizontalAlignment.Center; qt.VerticalAlignment = VerticalAlignment.Center; q.AddChild(qt); qt.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        box.AddChild(q);

        // hover: lighten and show the printed card; click: the card reader
        box.SetMeta("strip_code", code);   // QA: lib-peek-strip finds it
        box.MouseEntered += () => { box.Modulate = new Color(1.18f, 1.12f, 1.02f); Peek.Schedule(box, () => BigCard(code)); };
        box.MouseExited += () => { box.Modulate = Colors.White; Peek.Hide(); };
        box.GuiInput += e => { if (e is InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Left }) { Peek.Hide(); CardReader.Open(code, printed: true); } };
        return box;
    }

    public static Control? HoverCard(string code) => BigCard(code);
    static Control? BigCard(string code)
    {
        var tex = Ui.Card(code); if (tex is null) return null;
        // framed like the hover's 原图模式, so no box round it (meta "bare"); the ticked options in a slim dark strip under it
        var col = new VBoxContainer { MouseFilter = Control.MouseFilterEnum.Ignore }; col.AddThemeConstantOverride("separation", 8);
        col.SetMeta("bare", true);
        col.AddChild(Peek.FramedCard(tex, new Vector2(300, 420)));
        if (Customized(code) is { } ticks)
        {
            var strip = new PanelContainer { MouseFilter = Control.MouseFilterEnum.Ignore, CustomMinimumSize = new Vector2(300, 0) };
            var ss = new StyleBoxFlat { BgColor = new Color(0.045f, 0.04f, 0.035f, 0.94f), BorderColor = new Color(Style.Brass, 0.35f), ContentMarginLeft = 14, ContentMarginRight = 14, ContentMarginTop = 8, ContentMarginBottom = 10 };
            ss.SetBorderWidthAll(1); ss.SetCornerRadiusAll(8); strip.AddThemeStyleboxOverride("panel", ss);
            strip.AddChild(ticks); col.AddChild(strip);
        }
        return col;
    }

    // A printed card loaded later, in the one frame every large card has (Peek.FramedCard: rounded corners, 3px brass rim
    // #e0b56a, dark shadow — Yunke 11-20 "所有地方的大卡都要有边框"); `set` puts the card in when it is read.
    public static Control LaterFramedCard(Vector2 size, out Action<Texture2D?> set)
    {
        var holder = new Control { CustomMinimumSize = size, MouseFilter = Control.MouseFilterEnum.Ignore };
        void Place(Texture2D? t)
        {
            if (!GodotObject.IsInstanceValid(holder)) return;
            foreach (var c in holder.GetChildren()) if (c.HasMeta("framed")) { holder.RemoveChild(c); c.QueueFree(); }   // not what sits on it (×2)
            var f = Peek.FramedCard(t, size); f.SetMeta("framed", true); holder.AddChild(f); holder.MoveChild(f, 0);
            f.Position = ((size - f.CustomMinimumSize) / 2).Round(); f.Size = f.CustomMinimumSize;
        }
        Place(null); set = Place;
        return holder;
    }

    // ── customizable cards: the options this deck has ticked (build's customizations), shown under the card on hover
    static readonly Dictionary<string, JsonElement> sheets = new();
    static async Task FetchSheet(string deckId, string code)
    {
        if (deckId.Length == 0) return;
        try { var sh = await BuildEngine.I.CallJson("decks.customizable", deckId, code); if (sh.ValueKind == JsonValueKind.Object) sheets[code] = sh.Clone(); }
        catch (Exception e) { GD.PushWarning("customizable: " + e.Message); }
    }
    // one line per option: its boxes (■ ticked, □ open) and its name; the chosen skill / traits after it
    public static Control? Customized(string code)
    {
        if (!sheets.TryGetValue(CoreCatalog.Normalize(code), out var sheet)) return null;
        var chosen = new Dictionary<int, (int, string)>();
        foreach (var p in J.Props(J.Get(sheet, "chosen")))
            if (int.TryParse(p.Name, out int index)) chosen[index] = (J.Int(p.Value, "xp_spent"), J.String(p.Value, "selections"));
        return CustomTicks.Render(code, chosen);   // the same box as on the table
    }

    // "×2" in a small dark lozenge, pinned to the bottom-right corner of the card it sits on
    static Control Copies(int qty, bool small)
    {
        var box = new PanelContainer { MouseFilter = Control.MouseFilterEnum.Ignore, ZIndex = 2 };
        var sb = new StyleBoxFlat { BgColor = new Color(0.03f, 0.025f, 0.02f, 0.88f), BorderColor = new Color(Style.Brass, 0.7f), AntiAliasing = true, ContentMarginLeft = 7, ContentMarginRight = 7, ContentMarginTop = 0, ContentMarginBottom = 1 };
        sb.SetBorderWidthAll(1); sb.SetCornerRadiusAll(9); box.AddThemeStyleboxOverride("panel", sb);
        var l = Style.Text("×" + qty, small ? 16 : 19, new Color("f0d9a0"), Style.Kai); l.MouseFilter = Control.MouseFilterEnum.Ignore; box.AddChild(l);
        box.AnchorLeft = box.AnchorRight = 1; box.AnchorTop = box.AnchorBottom = 1;
        box.GrowHorizontal = Control.GrowDirection.Begin; box.GrowVertical = Control.GrowDirection.Begin;
        // on a small card: above its name band, so the name stays readable
        box.OffsetRight = small ? -4 : -9; box.OffsetBottom = small ? -27 : -9;
        return box;
    }

    // ── 小卡 / 大卡: a grid per group, the copies on each card's corner
    static Control Grid(List<(string Group, List<JsonElement> Cards)> groups, float width, bool small, List<(string, string, Action<Texture2D?>)> loads)
    {
        var col = new VBoxContainer(); col.AddThemeConstantOverride("separation", 8);
        var size = small ? new Vector2(112, 157) : new Vector2(196, 274);
        foreach (var (g, cards) in groups)
        {
            col.AddChild(Header(GroupName(g) + "（" + cards.Sum(c => J.Int(c, "quantity")) + "）"));
            var flow = new HFlowContainer { CustomMinimumSize = new Vector2(width, 0) }; flow.AddThemeConstantOverride("h_separation", small ? 12 : 16); flow.AddThemeConstantOverride("v_separation", 10); col.AddChild(flow);
            foreach (var c in cards)
            {
                string code = CoreCatalog.Normalize(J.String(c, "code")), type = J.String(c, "type_code"); int qty = J.Int(c, "quantity");
                var cell = new VBoxContainer(); cell.AddThemeConstantOverride("separation", 2);
                if (small)
                {
                    // the table's small card; without a redrawn picture, the art cut from the printed card fills it
                    var holder = new Control { CustomMinimumSize = size, MouseFilter = Control.MouseFilterEnum.Ignore };
                    cell.AddChild(holder);
                    string c2 = code; int xp2 = J.Int(c, "xp");
                    void Put(Texture2D? pic)
                    {
                        if (!GodotObject.IsInstanceValid(holder)) return;
                        var cv = new CardView { Code = c2, CustomMinimumSize = size, Size = size, ShowSkills = true, Picture = pic, OnClick = () => CardReader.Open(c2, printed: true) };
                        cv.PeekExtra = () => Customized(c2);   // a customizable card: what is ticked, in its hover box
                        holder.AddChild(cv);
                        // the card's level as pips at its foot (the small card itself shows only the cost — Yunke 11-19)
                        if (xp2 > 0)
                        {
                            var pips = Style.Text(new string('•', Math.Min(5, xp2)), 22, new Color("f0d9a0"), Style.Symbols); pips.MouseFilter = Control.MouseFilterEnum.Ignore;
                            pips.AddThemeConstantOverride("outline_size", 5); pips.AddThemeColorOverride("font_outline_color", Colors.Black); pips.TooltipText = xp2 + " 级";
                            pips.HorizontalAlignment = HorizontalAlignment.Right; holder.AddChild(pips); pips.Position = new Vector2(size.X - 46, 1); pips.Size = new Vector2(42, 24);
                        }
                    }
                    if (Ready(code)) Put(Ui.Art(code) is null ? ArtFor(code, type, size.X / size.Y) : null);
                    else loads.Add((code, type, _ => Put(ArtFor(c2, type, size.X / size.Y))));
                }
                else
                {
                    // the printed card in the hover's frame (Yunke 11-20)
                    var holder = LaterFramedCard(size, out var setCard);
                    holder.MouseFilter = Control.MouseFilterEnum.Stop; holder.MouseDefaultCursorShape = Control.CursorShape.PointingHand;
                    string c2 = code;
                    loads.Add((code, type, _ => setCard(Ui.Card(c2))));
                    holder.GuiInput += e => { if (e is InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Left }) CardReader.Open(c2, printed: true); };
                    holder.MouseEntered += () => holder.Modulate = new Color(1.12f, 1.08f, 1f); holder.MouseExited += () => holder.Modulate = Colors.White;
                    cell.AddChild(holder);
                }
                // the copies on the card's bottom-right corner, not a row of their own (Yunke 11-21: 少占一点行高)
                if (cell.GetChild(0) is Control onCard) onCard.AddChild(Copies(qty, small));
                flow.AddChild(cell);
            }
        }
        return col;
    }
}
