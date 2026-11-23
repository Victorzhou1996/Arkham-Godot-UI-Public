using System.Text.Json;
using System.Text.RegularExpressions;
using Godot;
using Gathering;

namespace ArkhamUI;

// The Scarlet Keys: embarking (QQ #45 "8循打完无法正常旅行"). Between scenarios the rules core asks the lead player
// PickCampaignSpecific "embark" with the world map (current place, the places one may stop at, each place's travel time,
// the expedited ticket); the answer is CampaignSpecificAnswer ["travel" | "travelVia" | "travelWithTicket", place]. The
// table had no form for it: the question showed no choice and the campaign could not go on.
// Drawn as the html version does it (TheScarletKeys/WorldMap.vue, WorldMapDrawerContent.vue, KeysStatus.vue, Calendar.vue):
// the whole world map painting (the html version's own world-map.jpg) with each place a spot on it — the places one can
// stop at breathe a soft glow, the place the cell is at carries the pin; pointing at a place lights it and shows its name,
// file number and travel time; clicking it brings up the drawer with the travel buttons and the place's dossier. Beside
// the map: the time passed (calendar) and the keys. Which places can be gone to, how (stop / pass through / ticket) and
// for how many days comes from the core's own route list (interactionSchema, Native/Validation.hs embarkMetadata).
// Official words: scarletKeys.*, theScarletKeys.locations.*, theScarletKeys.worldMap.*, theScarletKeys.headings.embark.*,
// keysStatus.*, calendar.timePassed (html locale); key names from the card data.
public partial class TableScreen
{
    // the place's marker on the 3000×1952 painting and its printed name label (left edge from the marker, width), from WorldMap.vue
    static readonly Dictionary<string, (float X, float Y, float LabelX, float LabelW)> MapSpots = new()
    {
        ["Anchorage"] = (226, 289, -160, 153), ["SanFrancisco"] = (273, 545, -218, 213), ["Arkham"] = (771, 494, 0, 89),
        ["YborCity"] = (616, 657, -155, 159), ["Havana"] = (617, 712, -125, 129), ["Bermuda"] = (815, 603, 0, 134),
        ["NewOrleans"] = (548, 635, -134, 134), ["SanJuan"] = (758, 766, 0, 145), ["Quito"] = (626, 979, 0, 100),
        ["RioDeJaneiro"] = (991, 1240, -225, 220), ["BuenosAires"] = (866, 1382, -200, 200), ["Lagos"] = (1437, 904, 0, 100),
        ["Nairobi"] = (1776, 990, -132, 132), ["Marrakesh"] = (1334, 612, 0, 168), ["Alexandria"] = (1691, 613, -179, 179),
        ["Cairo"] = (1706, 646, 0, 75), ["BermudaTriangle"] = (728, 680, 0, 0), ["London"] = (1420.5f, 384, 0, 120),
        ["Reykjavik"] = (1258, 249, 0, 161), ["Stockholm"] = (1568, 299, -155, 155), ["Rome"] = (1530, 498, 0, 85),
        ["Moscow"] = (1746, 332, 0, 120), ["Constantinople"] = (1675, 503, 0, 230), ["Bombay"] = (2117, 760, 0, 120),
        ["Kathmandu"] = (2238, 673, 0, 170), ["Shanghai"] = (2565, 619, -150, 150), ["Tokyo"] = (2714, 573, 0, 100),
        ["Perth"] = (2506, 1345, 0, 100), ["Sydney"] = (2837, 1362, -115, 115), ["Venice"] = (1523, 453, 0, 85),
        ["MonteCarlo"] = (1483, 472, -135, 135), ["Tunguska"] = (2299, 295, 0, 153), ["Kabul"] = (2064, 568, 0, 103),
        ["HongKong"] = (2515, 719, -160, 160), ["KualaLumpur"] = (2413, 943, -210, 210), ["Manokwari"] = (2735, 987, -160, 160),
    };
    const float MapW = 3000, MapH = 1952;
    // the keys in the order of the campaign log's table (KeysStatus.vue)
    static readonly string[] ScarletKeys = { "09519", "09544", "09659", "09634", "09565", "09590", "09680", "09768", "09769", "09770", "88045" };

    // one place as the core offers it: whether the cell is there, the finale, why it cannot be stopped at, and what can be done
    record MapRoute(string Id, bool Current, bool Finale, string Reason, List<(string Op, int? Days, bool Ticket)> Actions)
    {
        public bool Has(string op) => Actions.Any(a => a.Op == op);
        public int? Days => Actions.FirstOrDefault(a => a.Op is "travel" or "travelVia").Days;
    }

    Control? worldMapScrim;
    long worldMapSeq = -1;
    bool worldMapLoading;
    // the map goes when the question it answers has gone (an undo, the travel taken)
    void CloseWorldMap()
    {
        if (worldMapScrim is not null && IsInstanceValid(worldMapScrim)) App.I.CloseModal(worldMapScrim);
        worldMapScrim = null;
    }

    async void WorldMapDialog()
    {
        var m = model!; var basis = state!;
        var q = ClientQuestions.Unwrap(m.State.Question);
        var c = J.Get(q, "contents");
        if (c.ValueKind != JsonValueKind.Array || c.GetArrayLength() < 2 || J.Tag(q) != "PickCampaignSpecific") return;
        if (c[0].GetString() != "embark") { App.I.Toast("这个战役选择暂不支持", true); return; }
        // already up (or on its way) for this question: every render of the table asks again
        if (worldMapSeq == basis.Sequence && (worldMapLoading || worldMapScrim is not null && IsInstanceValid(worldMapScrim))) return;
        worldMapSeq = basis.Sequence; worldMapLoading = true;
        var details = c[1].Clone();
        // the core's own route list; a host that does not send it (an older online host): the same rule worked out here
        List<MapRoute>? routes = null;
        try { var schema = await B.ViewSection(basis, "interactionSchema"); routes = RoutesFromSchema(schema); }
        catch (Exception e) { GD.Print("WORLDMAP schema " + e.Message); }
        routes ??= RoutesFromOffer(details);
        worldMapLoading = false;
        if (state is null || state.Sequence != basis.Sequence || model?.QuestionTag != "PickCampaignSpecific") return;
        if (App.I.QaRunning) GD.Print("QA_WORLDMAP current=" + J.String(details, "current") + " stop=" + string.Join(",", routes.Where(r => r.Has("travel")).Select(r => r.Id + ":" + r.Days)) + " pass=" + routes.Count(r => r.Has("travelVia")) + " ticket=" + J.Get(details, "hasTicket").GetRawText());
        CloseWorldMap();
        worldMapSeq = basis.Sequence;
        BuildWorldMap(details, routes);
    }

    static List<MapRoute>? RoutesFromSchema(JsonElement schema)
    {
        if (J.Get(schema, "supported").ValueKind != JsonValueKind.True || J.String(schema, "kind") != "embark") return null;
        var list = new List<MapRoute>();
        foreach (var d in J.Items(J.Get(schema, "destinations")))
        {
            var acts = J.Items(J.Get(d, "actions")).Select(a => (J.String(a, "operation"), J.Get(a, "days").ValueKind == JsonValueKind.Number ? (int?)J.Int(a, "days") : null, J.Get(a, "consumesTicket").ValueKind == JsonValueKind.True)).ToList();
            list.Add(new MapRoute(J.String(d, "id"), J.Get(d, "isCurrent").ValueKind == JsonValueKind.True, J.Get(d, "isFinale").ValueKind == JsonValueKind.True, J.String(d, "unavailableReasonCode"), acts));
        }
        return list.Count > 0 ? list : null;
    }

    // the core's rule (Native/Validation.hs embarkRoutes), for when its list is not to hand
    static readonly HashSet<string> GreenPlaces = new() { "Arkham", "Cairo", "NewOrleans", "Venice", "MonteCarlo" };
    static List<MapRoute> RoutesFromOffer(JsonElement d)
    {
        string current = J.String(d, "current"); bool ticket = J.Get(d, "hasTicket").ValueKind == JsonValueKind.True;
        var available = J.Items(J.Get(d, "available")).Select(x => x.GetString() ?? "").ToHashSet();
        bool terminal = current == "Tunguska" && available.Count == 1 && available.Contains("Tunguska");
        var list = new List<MapRoute>();
        foreach (var e in J.Items(J.Get(d, "locations")))
        {
            if (e.ValueKind != JsonValueKind.Array || e.GetArrayLength() < 2) continue;
            string id = e[0].GetString() ?? ""; var t = J.Get(e[1], "travel");
            int? days = t.ValueKind == JsonValueKind.Number ? t.GetInt32() + (GreenPlaces.Contains(id) ? 1 : 0) : null;
            bool here = id == current, final = terminal && id == "Tunguska", reach = t.ValueKind == JsonValueKind.Number && t.GetInt32() >= 0;
            var acts = new List<(string, int?, bool)>();
            if (final) acts.Add(("travel", null, false));
            else if (!terminal && !here && reach)
            {
                if (available.Contains(id)) acts.Add(("travel", days, false));
                acts.Add(("travelVia", days, false));
                if (available.Contains(id) && ticket && days > 1) acts.Add(("travelWithTicket", 1, true));
            }
            string reason = final ? "" : terminal ? "finaleOnly" : here ? "currentLocation" : !reach ? "unreachable" : !available.Contains(id) ? "passOnly" : "";
            list.Add(new MapRoute(id, here, final, reason, acts));
        }
        return list;
    }

    string PlaceName(string id) => Official("theScarletKeys.locations." + id + ".name", id);
    string PlaceFile(string id) => Official("theScarletKeys.locations." + id + ".subtitle", "");

    // the dossier shown for a place (WorldMapDrawerContent.vue dossierKey)
    static string? DossierKey(string id) => id switch
    {
        "Marrakesh" or "BuenosAires" or "Constantinople" or "Havana" or "Shanghai" or "Anchorage" or "Tokyo" or "Alexandria" or "Sydney" or "YborCity" or "Kathmandu" or "Nairobi" => id,
        "SanFrancisco" or "Moscow" => "Sanctums",
        "RioDeJaneiro" or "Perth" => "DrIrawan",
        _ => null,
    };

    void BuildWorldMap(JsonElement details, List<MapRoute> routes)
    {
        var m = model!;
        var vp = GetViewportRect().Size;
        string current = J.String(details, "current");
        var meta = J.Get(J.Get(J.Get(m.Game, "mode"), "This"), "meta");
        var root = Ui.Backdrop(vp, Ui.Bg("desk-table"), 0.45f);

        // ── title: 从伦敦启程 (theScarletKeys.headings.embark.<place>)
        float topH = Math.Clamp(vp.Y * 0.11f, 74, 110);
        var head = new VBoxContainer { Position = new Vector2(0, 8), Size = new Vector2(vp.X, topH - 8), Alignment = BoxContainer.AlignmentMode.Center, MouseFilter = MouseFilterEnum.Ignore };
        head.AddThemeConstantOverride("separation", 0); root.AddChild(head);
        var kicker = Style.Text("战役 · " + Camp.Name, vp.Y < 800 ? 18 : 22, Style.Brass, Style.Kai); kicker.HorizontalAlignment = HorizontalAlignment.Center; head.AddChild(kicker);
        var title = Style.Title(Official("theScarletKeys.headings.embark." + current, Official("theScarletKeys.headings.kind.embark", "启程")), vp.Y < 800 ? 32 : 40);
        title.HorizontalAlignment = HorizontalAlignment.Center; head.AddChild(title);

        // ── the map: as large as fits right of the side column
        float sideW = vp.X >= 1500 ? 300 : 250, gap = 24;
        float availW = vp.X - sideW - gap * 3, availH = vp.Y - topH - gap;
        float mw = Math.Min(availW, availH * MapW / MapH), mh = mw * MapH / MapW, k = mw / MapW;
        var mapPos = new Vector2(sideW + gap * 2 + (availW - mw) / 2, topH + (availH - mh) / 2);
        var map = new Control { Position = mapPos, Size = new Vector2(mw, mh), ClipContents = true, MouseFilter = MouseFilterEnum.Stop };
        var shadow = new Panel { Position = mapPos - new Vector2(6, 6), Size = new Vector2(mw + 12, mh + 12), MouseFilter = MouseFilterEnum.Ignore };
        var sb = new StyleBoxFlat { BgColor = new Color(0, 0, 0, 0.5f), ShadowColor = new Color(0, 0, 0, 0.55f), ShadowSize = 22 }; sb.SetCornerRadiusAll(8);
        shadow.AddThemeStyleboxOverride("panel", sb); root.AddChild(shadow);
        root.AddChild(map);
        var pic = Ui.Image(Ui.Tex("res://assets/ui/scarlet-keys/world-map.jpg"), new Vector2(mw, mh)); pic.Size = new Vector2(mw, mh); pic.StretchMode = TextureRect.StretchModeEnum.Scale;
        pic.SelfModulate = new Color(0.8f, 0.79f, 0.76f);   // a little dimmed so the places that can be gone to light up on the pale paper
        map.AddChild(pic);
        Vector2 At(float x, float y) => new(x * k, y * k);

        // the info box shown while pointing at a place, and the drawer opened by a click
        var tip = new PanelContainer { Visible = false, MouseFilter = MouseFilterEnum.Ignore, ZIndex = 5 };
        tip.AddThemeStyleboxOverride("panel", Ui.SoftPanel(0.9f, 16, 10));
        var tipCol = new VBoxContainer { MouseFilter = MouseFilterEnum.Ignore }; tipCol.AddThemeConstantOverride("separation", 2); tip.AddChild(tipCol);
        Control? drawer = null;

        var byId = routes.ToDictionary(r => r.Id);
        var spots = new Dictionary<string, (Control Glow, Control Lit)>();
        foreach (var (id, s) in MapSpots)
        {
            byId.TryGetValue(id, out var r);
            r ??= new MapRoute(id, id == current, false, "unreachable", new());
            // the Bermuda Triangle is on the map only once it is unlocked (WorldMap.vue)
            if (id == "BermudaTriangle" && !r.Has("travel")) continue;
            bool stop = r.Has("travel"), here = r.Current;
            var p = At(s.X, s.Y);
            float rad = Math.Max(13f, 30 * k);
            // the place's area: the marker and its printed name
            var label = s.LabelW > 0 ? new Rect2(At(s.X + s.LabelX, s.Y - 16), new Vector2(s.LabelW * k, 32 * k)) : new Rect2(p, Vector2.Zero);
            var area = new Rect2(p - new Vector2(rad, rad), new Vector2(rad * 2, rad * 2)).Merge(label.Size == Vector2.Zero ? new Rect2(p, Vector2.Zero) : label);
            // soft glow over marker and name (glow-mask principle: covers it all, a little larger, no hard edge)
            var glow = GlowPatch(area.Grow(Math.Max(8, 16 * k)), stop ? new Color(1f, 0.72f, 0.3f) : new Color(1, 0.97f, 0.88f));
            glow.Modulate = new Color(1, 1, 1, stop ? 0.55f : 0f); map.AddChild(glow);
            if (stop && S.Settings.ReducedMotion != true)
            {
                var tw = glow.CreateTween().SetLoops(); tw.TweenProperty(glow, "modulate:a", 0.8f, 1.1f).SetTrans(Tween.TransitionType.Sine); tw.TweenProperty(glow, "modulate:a", 0.35f, 1.1f).SetTrans(Tween.TransitionType.Sine);
            }
            // lit while pointed at
            var lit = GlowPatch(area.Grow(Math.Max(10, 20 * k)), new Color(0.75f, 0.68f, 0.52f)); lit.Visible = false; map.AddChild(lit);
            spots[id] = (glow, lit);
            var hit = new Control { Position = area.Position - new Vector2(4, 4), Size = area.Size + new Vector2(8, 8), MouseFilter = MouseFilterEnum.Stop, MouseDefaultCursorShape = CursorShape.PointingHand, TooltipText = "" };
            map.AddChild(hit);
            var rr = r;
            hit.MouseEntered += () => { lit.Visible = true; ShowTip(rr, area); };
            hit.MouseExited += () => { lit.Visible = false; tip.Visible = false; };
            hit.GuiInput += e => { if (e is InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Left }) OpenDrawer(rr); };
        }
        // the pin on the place the cell is at (WorldMap.vue: a dark pin over the marker)
        if (MapSpots.TryGetValue(current, out var cs)) map.AddChild(MapPin(At(cs.X, cs.Y), Math.Max(0.45f, k)));
        map.AddChild(tip);

        void ShowTip(MapRoute r, Rect2 area)
        {
            foreach (var ch in tipCol.GetChildren()) ch.QueueFree();
            tipCol.AddChild(Style.Text(PlaceName(r.Id), 24, Style.Paper, Style.Kai));
            if (PlaceFile(r.Id) is { Length: > 0 } f) tipCol.AddChild(Style.Text(f, 16, Style.PaperDim, Style.Kai));
            foreach (var line in StatusLines(r)) { var l = Style.Text(line.Text, 18, line.Color, Style.Kai); tipCol.AddChild(l); }
            tip.Visible = true; tip.Size = Vector2.Zero; tip.ResetSize();
            var size = tip.GetCombinedMinimumSize();
            var pos = new Vector2(area.End.X + 12, area.Position.Y + area.Size.Y / 2 - size.Y / 2);
            if (pos.X + size.X > mw - 6) pos.X = area.Position.X - size.X - 12;
            pos.Y = Math.Clamp(pos.Y, 6, mh - size.Y - 6); pos.X = Math.Clamp(pos.X, 6, mw - size.X - 6);
            tip.Position = pos;
        }

        List<(string Text, Color Color)> StatusLines(MapRoute r)
        {
            var lines = new List<(string, Color)>();
            if (r.Current && !r.Finale) lines.Add((Word("scarletKeys.youAreCurrentlyHere", "你当前在这里。"), Style.Amber));
            if (r.Finale) lines.Add((Word("scarletKeys.travelHere", "旅行至此"), Style.Amber));
            if (!r.Current && r.Days is int d) lines.Add((Word("scarletKeys.travelTime", "旅行时间") + "：" + d, Style.Paper));
            if (r.Reason == "passOnly") lines.Add((Word("scarletKeys.locationLocked", "该地点当前已锁定。"), Style.Danger));
            if (r.Id == "Venice" && !r.Current) lines.Add((Word("scarletKeys.sideStoryLocation", "这是一个支线剧情地点。"), Style.PaperDim));
            return lines;
        }

        void OpenDrawer(MapRoute r)
        {
            if (drawer is not null && IsInstanceValid(drawer)) drawer.QueueFree();
            float dw = Math.Min(560, Math.Max(380, mw * 0.4f)), dh = Math.Min(mh * 0.62f, DossierKey(r.Id) is null ? 300 : 520);   // no dossier: a short drawer
            var box = new PanelContainer { Size = new Vector2(dw, dh), CustomMinimumSize = new Vector2(dw, dh), MouseFilter = MouseFilterEnum.Stop, ZIndex = 6 };
            box.AddThemeStyleboxOverride("panel", Ui.SoftPanel(0.94f, 20, 14));
            var col = new VBoxContainer(); col.AddThemeConstantOverride("separation", 8); box.AddChild(col);
            var top = new HBoxContainer(); col.AddChild(top);
            var names = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill }; names.AddThemeConstantOverride("separation", 0); top.AddChild(names);
            names.AddChild(Style.Title(PlaceName(r.Id), 30));
            if (PlaceFile(r.Id) is { Length: > 0 } f) names.AddChild(Style.Text(f, 17, Style.PaperDim, Style.Kai));
            var close = Ui.Button("×", () => { if (IsInstanceValid(box)) box.QueueFree(); }, 24); close.CustomMinimumSize = new Vector2(48, 44); close.SizeFlagsVertical = SizeFlags.ShrinkBegin; top.AddChild(close);
            col.AddChild(new ColorRect { Color = new Color(Style.Brass, 0.3f), CustomMinimumSize = new Vector2(0, 1) });
            var scroll = new ScrollContainer { SizeFlagsVertical = SizeFlags.ExpandFill, HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled };
            col.AddChild(scroll);
            var body = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill }; body.AddThemeConstantOverride("separation", 8); scroll.AddChild(body);
            Label Para(string t, int size, Color color) { var l = Style.Text(t, size, color, Style.Kai); l.AutowrapMode = TextServer.AutowrapMode.WordSmart; l.CustomMinimumSize = new Vector2(dw - 60, 0); return l; }
            if (r.Current)
            {
                if (r.Finale) body.AddChild(TravelButton(r, "travel", Word("scarletKeys.travelHere", "旅行至此"), true));
                else body.AddChild(Para(Word("scarletKeys.youAreCurrentlyHere", "你当前在这里。"), 20, Style.Amber));
            }
            else
            {
                if (r.Days is int d) body.AddChild(Para(Word("scarletKeys.travelTime", "旅行时间") + "：" + d, 20, Style.Paper));
                if (r.Id == "Venice")
                {
                    body.AddChild(Para(Word("scarletKeys.sideStoryLocation", "这是一个支线剧情地点。"), 17, Style.PaperDim));
                    body.AddChild(Para(Word("scarletKeys.sideStoryDescription", ""), 17, Style.PaperDim));
                }
                if (r.Has("travel")) body.AddChild(TravelButton(r, "travel", Word("scarletKeys.travelHere", "旅行至此"), true));
                else if (r.Reason == "passOnly") body.AddChild(Para(Word("scarletKeys.locationLocked", "该地点当前已锁定。"), 19, Style.Danger));
                if (r.Has("travelWithTicket")) body.AddChild(TravelButton(r, "travelWithTicket", Word("scarletKeys.travelWithExpeditedTicket", "使用特快车票旅行（1 次）"), false));
                if (r.Has("travelVia")) body.AddChild(TravelButton(r, "travelVia", Word("scarletKeys.travelWithoutStopping", "旅行至此但不停留"), false));
            }
            if (DossierKey(r.Id) is { } dk && S.Locale.TryTemplate("theScarletKeys.worldMap.dossiers." + dk, out var html) && html.Length > 0)
                body.AddChild(DossierBox(html, dw - 60));
            // slides up from the map's bottom edge (the html embark drawer)
            float x = Math.Clamp(mw * 0.5f - dw / 2, 8, mw - dw - 8);
            box.Position = new Vector2(x, mh);
            map.AddChild(box); drawer = box;
            var to = new Vector2(x, mh - dh);
            if (S.Settings.ReducedMotion == true) box.Position = to;
            else box.CreateTween().TweenProperty(box, "position", to, 0.3f).SetTrans(Tween.TransitionType.Sine).SetEase(Tween.EaseType.Out);
        }

        Button TravelButton(MapRoute r, string op, string text, bool primary)
        {
            var b = Ui.Button(text, () => Travel(op, r.Id), 22, primary);
            b.CustomMinimumSize = new Vector2(0, 52); b.SizeFlagsHorizontal = SizeFlags.ExpandFill;
            return b;
        }

        void Travel(string op, string id)
        {
            if (App.I.QaRunning) GD.Print("QA_TRAVEL " + op + " " + id);
            var ans = JsonSerializer.SerializeToElement(new { tag = "CampaignSpecificAnswer", contents = new object[] { op, id } });
            BlackFade(async () =>
            {
                CloseWorldMap(); worldMapSeq = -1;
                await Do("旅行", () => B.Answer(state!, ans));
                await Advance();
            });
        }

        // ── beside the map: the time passed and the keys (the campaign log's Calendar and KeysStatus)
        var side = new VBoxContainer { Position = new Vector2(gap, topH), Size = new Vector2(sideW, vp.Y - topH - gap), Alignment = BoxContainer.AlignmentMode.Center, MouseFilter = MouseFilterEnum.Ignore };
        side.AddThemeConstantOverride("separation", 14); root.AddChild(side);
        int time = J.Items(J.Get(J.Get(J.Get(J.Get(m.Game, "mode"), "This"), "log"), "recordedCounts"))
            .Where(rc => rc.ValueKind == JsonValueKind.Array && rc.GetArrayLength() == 2 && J.String(rc[0], "tag") == "TheScarletKeysKey" && J.String(rc[0], "contents") == "Time")
            .Select(rc => rc[1].ValueKind == JsonValueKind.Number ? rc[1].GetInt32() : 0).FirstOrDefault();
        side.AddChild(CalendarBox(time, meta, sideW, vp.Y < 800));
        side.AddChild(KeysBox(meta, sideW, vp.Y < 800));

        worldMapScrim = App.I.Modal(root, false, 0f);

        // QA: point at / open / travel to a place through the same controls a player uses
        QaScript.Register("map-hover", a => { if (spots.TryGetValue(a, out var sp) && byId.TryGetValue(a, out var r) && MapSpots.TryGetValue(a, out var s)) { sp.Lit.Visible = true; var p = At(s.X, s.Y); ShowTip(r, new Rect2(p - new Vector2(14, 14), new Vector2(28, 28)).Merge(new Rect2(At(s.X + s.LabelX, s.Y - 16), new Vector2(Math.Max(1, s.LabelW) * k, 32 * k)))); } });
        QaScript.Register("map-open", a => { if (byId.TryGetValue(a, out var r)) OpenDrawer(r); });
        QaScript.Register("travel", a => { var parts = a.Split(':'); string op = parts.Length > 1 ? parts[0] : "travel", id = parts[^1]; if (byId.TryGetValue(id, out var r) && r.Has(op)) Travel(op, id); else GD.Print("QA_TRAVEL_NOT_OFFERED " + a); });
    }

    // a soft glowing patch over a rectangle: bright in the middle, fading to nothing at the edge (an ellipse, no corners)
    static GradientTexture2D? glowTex;
    static CanvasItemMaterial? glowAdd;
    static TextureRect GlowPatch(Rect2 r, Color color)
    {
        if (glowTex is null)
        {
            var g = new Gradient(); g.SetColor(0, new Color(1, 1, 1, 0.85f)); g.SetColor(1, new Color(1, 1, 1, 0f)); g.AddPoint(0.55f, new Color(1, 1, 1, 0.45f));
            glowTex = new GradientTexture2D { Gradient = g, Fill = GradientTexture2D.FillEnum.Radial, FillFrom = new Vector2(0.5f, 0.5f), FillTo = new Vector2(1f, 0.5f), Width = 128, Height = 128 };
        }
        // the size is set once the texture no longer sets a minimum (IgnoreSize first): else it stays 128 px from the corner
        var t = new TextureRect { ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize, StretchMode = TextureRect.StretchModeEnum.Scale, MouseFilter = MouseFilterEnum.Ignore };
        t.Texture = glowTex; t.SelfModulate = color; t.Position = r.Position; t.Size = r.Size;
        t.Material = glowAdd ??= new CanvasItemMaterial { BlendMode = CanvasItemMaterial.BlendModeEnum.Add };   // light added to the paper, as a glow
        return t;
    }

    // the dark pin standing on the current place (WorldMap.vue: "M 0 0 L -8 -16 A 16 16 0 1 1 8 -16 Z", scaled 1.5)
    static Control MapPin(Vector2 tip, float scale)
    {
        var pin = new Control { Position = tip, MouseFilter = MouseFilterEnum.Ignore, ZIndex = 2 };
        pin.Draw += () =>
        {
            float s = 1.5f * scale;
            var head = new Vector2(0, -24 * s);
            pin.DrawCircle(head + new Vector2(0, 2 * s), 18 * s, new Color(0, 0, 0, 0.25f));
            pin.DrawColoredPolygon(new[] { Vector2.Zero, new Vector2(-9 * s, -17 * s), new Vector2(9 * s, -17 * s) }, new Color(0.04f, 0.04f, 0.04f, 0.85f));
            pin.DrawCircle(head, 16 * s, new Color(0.04f, 0.04f, 0.04f, 0.85f));
            pin.DrawArc(head, 16 * s, 0, Mathf.Tau, 40, new Color(Style.Brass, 0.9f), 1.6f * s, true);
            pin.DrawCircle(head, 5.5f * s, Style.Amber);
        };
        return pin;
    }

    // the dossier: typewritten paper, censored words blacked out (WorldMapDrawerContent.vue .dossier)
    Control DossierBox(string html, float width)
    {
        var box = new PanelContainer();
        var sb = new StyleBoxFlat { BgColor = new Color("f9f1de"), ContentMarginLeft = 16, ContentMarginRight = 16, ContentMarginTop = 12, ContentMarginBottom = 14 }; sb.SetCornerRadiusAll(8);
        box.AddThemeStyleboxOverride("panel", sb);
        var col = new VBoxContainer(); col.AddThemeConstantOverride("separation", 6); box.AddChild(col);
        var h = Style.Text(Word("theScarletKeys.worldMap.dossierHeading", "你可以在任何时候阅读此密档"), 18, new Color("1a1a1a"), Style.Serif); h.HorizontalAlignment = HorizontalAlignment.Center; col.AddChild(h);
        col.AddChild(new ColorRect { Color = new Color(0, 0, 0, 0.6f), CustomMinimumSize = new Vector2(0, 1) });
        var text = new RichTextLabel { BbcodeEnabled = true, FitContent = true, ScrollActive = false, AutowrapMode = TextServer.AutowrapMode.WordSmart, CustomMinimumSize = new Vector2(width - 32, 0), MouseFilter = MouseFilterEnum.Ignore };
        if (Style.Serif is not null) text.AddThemeFontOverride("normal_font", Style.Serif);
        text.AddThemeFontSizeOverride("normal_font_size", 16); text.AddThemeColorOverride("default_color", new Color("262320"));
        text.Text = DossierBbcode(html);
        col.AddChild(text);
        return box;
    }

    static string DossierBbcode(string html)
    {
        string s = html.Replace("[", "[lb]");
        s = Regex.Replace(s, @"<span class=['""]censor['""]>(.*?)</span>", m => "[bgcolor=#111111][color=#111111]" + m.Groups[1].Value + "[/color][/bgcolor]", RegexOptions.Singleline);
        s = Regex.Replace(s, @"<br\s*/?>", "\n");
        s = Regex.Replace(s, @"</p>\s*<p[^>]*>", "\n\n");
        s = Regex.Replace(s, @"<(b|strong)>", "[b]"); s = Regex.Replace(s, @"</(b|strong)>", "[/b]");
        s = Regex.Replace(s, @"<(i|em)>", "[i]"); s = Regex.Replace(s, @"</(i|em)>", "[/i]");
        s = Regex.Replace(s, @"<[^>]+>", "");
        return System.Net.WebUtility.HtmlDecode(s).Trim();
    }

    // 流逝的时间: 35 days, those passed crossed out, the campaign's marked days (Calendar.vue)
    Control CalendarBox(int time, JsonElement meta, float width, bool small)
    {
        var p = new PanelContainer(); p.AddThemeStyleboxOverride("panel", Ui.SoftPanel(0.86f, 14, 10));
        var col = new VBoxContainer(); col.AddThemeConstantOverride("separation", 6); p.AddChild(col);
        var t = Style.Text(Word("calendar.timePassed", "流逝的时间") + "  " + time + " / 35", small ? 18 : 21, Style.Brass, Style.Kai); col.AddChild(t);
        var grid = new GridContainer { Columns = 7 }; grid.AddThemeConstantOverride("h_separation", 2); grid.AddThemeConstantOverride("v_separation", 2); col.AddChild(grid);
        int theta = J.Int(meta, "theta"), delta = J.Int(meta, "delta"), psi = J.Int(meta, "psi");
        float cell = (width - 28 - 12) / 7f;
        for (int n = 1; n <= 35; n++)
        {
            string sym = n switch { 7 => "α", 10 => "ε", 15 => "β", 20 => "ζ", 24 => "γ", 35 => "Ω", _ => "" } + (n == theta ? "Θ" : "") + (n == delta ? "δ" : "") + (n == psi ? "ψ" : "");
            bool done = n <= time;
            var c = new PanelContainer { CustomMinimumSize = new Vector2(cell, small ? cell * 0.78f : cell * 0.86f) };
            var cb = new StyleBoxFlat { BgColor = done ? new Color(Style.Amber, 0.32f) : new Color(1, 1, 1, 0.05f) }; cb.SetCornerRadiusAll(4); c.AddThemeStyleboxOverride("panel", cb);
            var v = new VBoxContainer { Alignment = BoxContainer.AlignmentMode.Center }; v.AddThemeConstantOverride("separation", -4); c.AddChild(v);
            var num = Style.Text(done ? "✕" : n.ToString(), small ? 13 : 15, done ? Style.Amber : Style.PaperDim, Style.Serif); num.HorizontalAlignment = HorizontalAlignment.Center; v.AddChild(num);
            if (sym.Length > 0) { var sy = Style.Text(sym, small ? 11 : 13, Style.Paper, Style.Serif); sy.HorizontalAlignment = HorizontalAlignment.Center; v.AddChild(sy); }
            grid.AddChild(c);
        }
        return p;
    }

    // 钥匙: each Scarlet Key, ticked once known, and who holds it (KeysStatus.vue)
    Control KeysBox(JsonElement meta, float width, bool small)
    {
        var p = new PanelContainer(); p.AddThemeStyleboxOverride("panel", Ui.SoftPanel(0.86f, 14, 10));
        var col = new VBoxContainer(); col.AddThemeConstantOverride("separation", small ? 1 : 3); p.AddChild(col);
        col.AddChild(Style.Text(Word("keysStatus.keys", "钥匙"), small ? 18 : 21, Style.Brass, Style.Kai));
        var grid = new GridContainer { Columns = 2 }; grid.AddThemeConstantOverride("h_separation", 10); grid.AddThemeConstantOverride("v_separation", small ? 0 : 2); col.AddChild(grid);
        int fs = small ? 14 : 16;
        grid.AddChild(Style.Text(Word("keysStatus.nameOfArtifact", "超维神器名称"), fs, Style.PaperDim, Style.Kai));
        grid.AddChild(Style.Text(Word("keysStatus.bearer", "拥有者"), fs, Style.PaperDim, Style.Kai));
        var status = J.Get(meta, "keyStatus");
        foreach (var code in ScarletKeys)
        {
            var st = J.Get(status, "c" + code);
            if (st.ValueKind == JsonValueKind.Undefined) st = J.Get(status, code);
            bool known = st.ValueKind == JsonValueKind.Object;
            string name = S.Name(code + "a") is var na && na != code + "a" ? na : S.Name(code);
            var n = Style.Text((known ? "✓ " : "　 ") + name, fs, known ? Style.Paper : new Color(Style.PaperDim, 0.6f), Style.Kai);
            n.CustomMinimumSize = new Vector2((width - 38) * 0.5f, 0); n.ClipText = true; grid.AddChild(n);
            string bearer = "";
            if (known)
            {
                var who = J.Get(st, "contents");
                var whoCode = who.ValueKind == JsonValueKind.Array && who.GetArrayLength() > 0 ? who[0] : who;
                if (whoCode.ValueKind == JsonValueKind.String) bearer = S.Name(whoCode.GetString()!);
            }
            var b = Style.Text(bearer, fs, Style.Paper, Style.Kai); b.CustomMinimumSize = new Vector2((width - 38) * 0.5f, 0); b.ClipText = true; grid.AddChild(b);
        }
        return p;
    }
}
