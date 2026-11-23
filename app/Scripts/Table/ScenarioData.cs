using System.Text.Json;
using System.Text.RegularExpressions;
using Path = System.IO.Path;
using File = System.IO.File;
using Gathering;
using Godot;

namespace ArkhamUI;

// Per-scenario presentation data (backgrounds and map hotspots). Rules state only selects among them.
public sealed class ScenarioData
{
    public JsonElement Raw;
    public static ScenarioData? Load(string code)
    {
        var f = Path.Combine(App.I.Assets, "scenarios", CoreCatalog.Normalize(code) + ".json");
        if (!File.Exists(f)) return null;
        using var d = JsonDocument.Parse(File.ReadAllText(f));
        return new ScenarioData { Raw = d.RootElement.Clone() };
    }
    public string Background(TableModel m)
    {
        var loc = m.MyLocation; if (loc is null) return Exterior;
        string label = m.Label(loc);
        string act = m.Act?.Code ?? "", agenda = m.Agenda?.Code ?? "";
        foreach (var s in J.Items(J.Get(Raw, "states")))
        {
            if (J.String(s, "location").Length > 0 && J.String(s, "location") != label) continue;   // a state may name only a card code
            if (J.String(s, "code").Length > 0 && J.String(s, "code") != loc.Code) continue;   // one side of a two-version location
            if (J.String(s, "act").Length > 0 && J.String(s, "act") != act) continue;
            if (J.String(s, "agenda").Length > 0 && J.String(s, "agenda") != agenda) continue;
            return J.String(s, "background");
        }
        var b = J.String(J.Get(Raw, "backgrounds"), label);
        if (b.Length > 0) return b;
        // no painting for this place: the location card's own art (its unrevealed side while unrevealed)
        if (J.Get(Raw, "cardBackgrounds").ValueKind == JsonValueKind.True)
        {
            string code = CoreCatalog.Normalize(loc.Code);
            return "card:" + (!m.Revealed(loc) && Ui.Art(code + "b") is not null ? code + "b" : code);
        }
        return ExteriorFor(m.Game);
    }
    // the scenario's outdoor picture (setup, story screens, the prepare screen)
    public string Exterior => J.String(Raw, "exterior") is { Length: > 0 } e ? e : "exterior";
    // The outdoor picture for this game: a chapter whose setup depends on the campaign log names the picture to use
    // when a key is recorded ("exteriorByRecord": The Midnight Masks starts in Rivertown once your house has burned).
    public string ExteriorFor(JsonElement game)
    {
        var log = J.Get(J.Get(J.Get(J.Get(game, "mode"), "This"), "log"), "recorded");
        foreach (var p in J.Props(J.Get(Raw, "exteriorByRecord")))
            if (J.Items(log).Any(k => J.String(k, "contents") == p.Name) && p.Value.GetString() is { Length: > 0 } pic) return pic;
        return Exterior;
    }
    // the picture for a resolution ("R1", "R2"…), or the exterior
    public string Ending(string resolution) => J.String(J.Get(Raw, "endings"), resolution) is { Length: > 0 } e ? e : Exterior;
    public string MapImage => J.String(J.Get(Raw, "map"), "image");
    public string Name => J.String(Raw, "name");
    // where a location lies on the map, for locations that share a name (the four woods of chapter three)
    public string Place(string label) => J.String(J.Get(Raw, "places"), label);
    // what the overview map is called (the house in chapter one, the town of Arkham in chapter two)
    public string MapName => J.String(Raw, "mapName") is { Length: > 0 } n ? n : "地图总览";
    // Display text for an act's goal whose cost is not a plain clue cost (e.g. the Barrier, paid at the end of the round
    // in the hallway). Presentation only: the core decides when the advance is offered.
    public (string Text, int PerPlayer)? Objective(string actCode)
    {
        var o = J.Get(J.Get(Raw, "objectives"), CoreCatalog.Normalize(actCode));
        if (o.ValueKind != JsonValueKind.Object) return null;
        return (J.String(o, "text"), J.Int(o, "perPlayer"));
    }
    // a painted map with hand-placed hotspots; without one the map is drawn from the core's location layout
    public bool PaintedMap => MapImage.Length > 0 && J.String(J.Get(Raw, "map"), "style") != "cards";
    // "painting": the map is one whole picture whose regions are the locations — no location cards on it (Yunke 11-18)
    public bool WholePicture => J.String(J.Get(Raw, "map"), "style") == "painting";
    public IEnumerable<string> HotspotLabels => J.Props(J.Get(J.Get(Raw, "map"), "hotspots")).Select(p => p.Name);
    // Connection lines on a painted map (Yunke 11-18): "none" where the picture already shows which places touch (the house);
    // otherwise drawn, along map.paths."<label>-<label>" (points as fractions of the picture, a curve along the road) when given
    public bool MapLinks => J.String(J.Get(Raw, "map"), "links") != "none";
    // map.paths."a-b": "none" — the picture itself joins the two (a glass corridor, two halls side by side): no line
    public bool MapPairHidden(string a, string b) => J.String(J.Get(J.Get(Raw, "map"), "paths"), a + "-" + b) == "none" || J.String(J.Get(J.Get(Raw, "map"), "paths"), b + "-" + a) == "none";
    public Vector2[]? MapPath(string a, string b)
    {
        var paths = J.Get(J.Get(Raw, "map"), "paths");
        foreach (var (key, rev) in new[] { (a + "-" + b, false), (b + "-" + a, true) })
        {
            var p = J.Get(paths, key); if (p.ValueKind != JsonValueKind.Array) continue;
            var pts = J.Items(p).Select(q => new Vector2((float)q[0].GetDouble(), (float)q[1].GetDouble())).ToArray();
            return rev ? pts.Reverse().ToArray() : pts;
        }
        return null;
    }
    // the thumbnail for the map button: the painted map, or the scenario's picture
    public string MapThumb => PaintedMap ? MapImage : Exterior;

    // Where each location sits on the drawn map, from the scenario's locationLayout (rows of grid-area names, "." is empty;
    // a name repeated over neighbouring cells spans them) — the html version lays out its board the same way.
    // Normalized rects (0..1). Locations missing from the layout go in a row underneath.
    public static Dictionary<string, Rect2> LayoutRects(TableModel m) => LayoutRects(m, out _, out _);
    public static Dictionary<string, Rect2> LayoutRects(TableModel m, out int cols, out int rowsCount)
    {
        var rows = J.Items(J.Get(m.Scenario, "locationLayout")).Select(r => (r.GetString() ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries)).Where(r => r.Length > 0).ToList();
        var labels = m.Locations.Select(l => m.Label(l)).ToList();
        var missing = labels.Where(l => !rows.Any(r => r.Contains(l))).Distinct().ToList();
        if (missing.Count > 0) rows.Add(missing.ToArray());
        int nr = Math.Max(1, rows.Count), nc = Math.Max(1, rows.Count == 0 ? 1 : rows.Max(r => r.Length));
        cols = nc; rowsCount = nr;
        var cells = new Dictionary<string, (int r0, int c0, int r1, int c1)>();
        for (int r = 0; r < rows.Count; r++)
        {
            // a short row (the missing ones) is spread over the full width
            float stretch = rows[r].Length < nc ? (float)nc / rows[r].Length : 1;
            for (int c = 0; c < rows[r].Length; c++)
            {
                string k = rows[r][c]; if (k == ".") continue;
                int cs = (int)(c * stretch), ce = (int)((c + 1) * stretch) - 1;
                cells[k] = cells.TryGetValue(k, out var b) ? (Math.Min(b.r0, r), Math.Min(b.c0, cs), Math.Max(b.r1, r), Math.Max(b.c1, ce)) : (r, cs, r, ce);
            }
        }
        var outRects = new Dictionary<string, Rect2>();
        foreach (var (k, b) in cells)
        {
            float w = 1f / nc, h = 1f / nr;
            outRects[k] = new Rect2(b.c0 * w + w * 0.06f, b.r0 * h + h * 0.08f, (b.c1 - b.c0 + 1) * w - w * 0.12f, (b.r1 - b.r0 + 1) * h - h * 0.16f);
        }
        return outRects;
    }
    public float[]? Hotspot(TableModel m, string label)
    {
        if (PaintedMap) return Hotspot(label);
        return LayoutRects(m).TryGetValue(label, out var r) ? new[] { r.Position.X, r.Position.Y, r.Size.X, r.Size.Y } : null;
    }

    public float[]? Hotspot(string label)
    {
        var h = J.Get(J.Get(J.Get(Raw, "map"), "hotspots"), label);
        return h.ValueKind == JsonValueKind.Array ? h.EnumerateArray().Select(x => (float)x.GetDouble()).ToArray() : null;
    }
}
