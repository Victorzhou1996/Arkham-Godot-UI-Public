using Godot;
using System.Text.Json;

namespace ArkhamUI;

// White edges on printed card scans (Yunke 11-19): some scans carry a strip of white paper along an edge, or white paper in
// the corners outside the card's rounded corner, which shows on the dark table. tools/dev/card_trim.py measured every card
// image (8934) and wrote app/assets/build-data/card-trim.json {code: [left, top, right, bottom, radius]} for the 558 that
// need it: the white strips to trim (fractions of the width / height) and a corner radius (fraction of the trimmed short
// side) outside which the image becomes transparent. Applied when a card image is loaded from the green package; the few
// of our own scans that needed it were re-cut on disk by the same tool.
public static class CardTrim
{
    static Dictionary<string, float[]>? table;

    static Dictionary<string, float[]> Table()
    {
        if (table is not null) return table;
        table = new();
        try
        {
            var file = Path.Combine(App.I.Assets, "build-data", "card-trim.json");
            if (File.Exists(file))
                foreach (var p in JsonDocument.Parse(File.ReadAllText(file)).RootElement.EnumerateObject())
                    if (p.Value.ValueKind == JsonValueKind.Array && p.Value.GetArrayLength() == 5)
                        table[p.Name] = p.Value.EnumerateArray().Select(x => (float)x.GetDouble()).ToArray();
        }
        catch (Exception e) { GD.PushWarning("card trim: " + e.Message); }
        return table;
    }

    public static bool Has(string code) => Table().ContainsKey(code);

    // The image without its white strips, with transparent rounded corners (anti-aliased over one pixel).
    public static Godot.Image Apply(string code, Godot.Image img)
    {
        if (!Table().TryGetValue(code, out var t)) return img;
        int W = img.GetWidth(), H = img.GetHeight();
        int l = (int)MathF.Round(t[0] * W), top = (int)MathF.Round(t[1] * H), r = (int)MathF.Round(t[2] * W), b = (int)MathF.Round(t[3] * H);
        if (l + r + top + b > 0 && W - l - r > 8 && H - top - b > 8) img = img.GetRegion(new Rect2I(l, top, W - l - r, H - top - b));
        if (t[4] <= 0) return img;
        if (img.IsCompressed()) img.Decompress();
        if (img.GetFormat() != Godot.Image.Format.Rgba8) img.Convert(Godot.Image.Format.Rgba8);
        int w = img.GetWidth(), h = img.GetHeight();
        float R = t[4] * Math.Min(w, h);
        int n = (int)MathF.Ceiling(R);
        for (int y = 0; y < n; y++)
            for (int x = 0; x < n; x++)
            {
                // distance of this pixel's centre from the arc's centre, outside the arc fades to transparent
                float dx = R - (x + 0.5f), dy = R - (y + 0.5f);
                float a = Math.Clamp(R - MathF.Sqrt(dx * dx + dy * dy) + 0.5f, 0f, 1f);
                if (a >= 1f) continue;
                foreach (var (px, py) in new[] { (x, y), (w - 1 - x, y), (x, h - 1 - y), (w - 1 - x, h - 1 - y) })
                {
                    var c = img.GetPixel(px, py); c.A *= a; img.SetPixel(px, py, c);
                }
            }
        return img;
    }
}
