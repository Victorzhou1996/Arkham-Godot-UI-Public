using Godot;
using System.Text.Json;

namespace ArkhamUI;

// Printed card images for every cycle, for the deck workshop (Yunke 11-18: build covers all cycles, our own assets only
// hold the core set and The Dunwich Legacy). Read on demand, read-only, from the html green package's art store
// (art/index.json maps "cards/<code>.avif" to a webp object); scaled down and kept in a small cache.
// Folder: userdata/settings.json "ArtStore" if set, else the green package next to this project.
public static class ArtStore
{
    static Dictionary<string, string>? index;   // code → absolute file
    static readonly Dictionary<string, ImageTexture> cache = new();
    static readonly LinkedList<string> order = new();
    const int CacheMax = 260, MaxHeight = 520;

    static string? Root()
    {
        var candidates = new[]
        {
            System.Environment.GetEnvironmentVariable("ARKHAM_ART_STORE"),
            // the compressed copy shipped with the game (tools/compress_artstore.py: cards <= 640 px, WebP 85; Yunke 11-22)
            Path.Combine(App.I.Root, "artstore"),
            Path.Combine(App.I.Root, "..", "ArkhamHorrorLocal-WinDeploy-20260712", "green-packages", "green-1.0.39-fast-20261118", "art"),
        };
        return candidates.FirstOrDefault(c => !string.IsNullOrEmpty(c) && File.Exists(Path.Combine(c!, "index.json")));
    }

    // 饥饿霉菌（食肉菌株）10060: the store's cards/10060 is a 滋长菌株 (10061) scan; 10060-10 is the right card (Yunke 11-22)
    static readonly (string Code, string Other)[] Misfiled = { ("10060", "10060-10") };

    static void Load()
    {
        if (index is not null) return;
        index = new();
        var root = Root(); if (root is null) return;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "index.json")));
            foreach (var p in doc.RootElement.GetProperty("paths").EnumerateObject())
            {
                // the html table's investigator portraits (parallel investigators too): kept as "portrait:<code>"
                if (p.Name.StartsWith("game/frontend/dist/img/arkham/portraits/"))
                {
                    string pcode = Path.GetFileNameWithoutExtension(p.Name);
                    // a parallel / reprint investigator whose portrait is only another card's (90017 → 01004: the html shows
                    // the original's face): cut from its own printed card instead, as the html did for 90084 (Yunke 11-19)
                    if (!pcode.EndsWith('b') && p.Value.TryGetProperty("aliasOf", out var al) && al.GetString() is string alias
                        && Path.GetFileNameWithoutExtension(alias) is var other && other != pcode && other.TrimEnd('a', 'b') != pcode.TrimEnd('a', 'b')
                        && !SameArt(pcode, other))
                    { borrowed.Add(pcode); continue; }
                    if (p.Value.TryGetProperty("file", out var pf) && pf.GetString() is string pfile) index["portrait:" + pcode] = Path.Combine(root, pfile);
                    continue;
                }
                if (!p.Name.StartsWith("cards/")) continue;
                string code = Path.GetFileNameWithoutExtension(p.Name);
                if (p.Value.TryGetProperty("file", out var f) && f.GetString() is string file) index[code] = Path.Combine(root, file);
            }
            // scans the store files under the wrong code: the right card's scan is another entry of the store
            foreach (var (code, other) in Misfiled) if (index.TryGetValue(other, out var right)) index[code] = right;
        }
        catch (Exception e) { GD.PushWarning("art store: " + e.Message); }
    }

    public static bool Has(string code) { Load(); return index!.ContainsKey(code); }
    // an investigator's portrait from the html table's set (later cycles), or cut from the printed card for the parallel and
    // reprint investigators the html only lends another card's portrait
    public static Texture2D? Portrait(string code)
    {
        Load();
        return borrowed.Contains(code) && index!.ContainsKey(code) ? PortraitCut(code) : Card("portrait:" + code);
    }

    // investigators whose html portrait is another card's
    static readonly HashSet<string> borrowed = new();
    // reprints with the very same art keep the shared portrait (Revised Core 01501–01505 are the core five; 11008 is the
    // other side of 11007)
    static bool SameArt(string code, string other) => code.StartsWith("015") || code == "11008";

    // The art beside the text box of a landscape investigator card, under its title and subtitle band, at the portrait's
    // own proportions (0.652): what the html cut for 90084 (its portraits/90084.jpg is this region of cards/90084).
    static Texture2D? PortraitCut(string code)
    {
        string key = "cut:" + code;
        if (cache.TryGetValue(key, out var hit)) return hit;
        if (!index!.TryGetValue(code, out var file) || !File.Exists(file)) return null;
        try
        {
            var img = Godot.Image.LoadFromFile(file);
            if (img is null || img.GetWidth() < img.GetHeight()) return null;
            float W = img.GetWidth(), H = img.GetHeight(), y0 = 0.165f * H, h = 0.785f * H, w = 0.652f * h;
            float x = Math.Clamp(0.27f * W - w / 2, 0, 0.545f * W - w);
            var sub = img.GetRegion(new Rect2I((int)x, (int)y0, (int)w, (int)h));
            if (sub.GetHeight() > MaxHeight) sub.Resize((int)(sub.GetWidth() * (MaxHeight / (float)sub.GetHeight())), MaxHeight, Godot.Image.Interpolation.Lanczos);
            sub.GenerateMipmaps();
            var tex = ImageTexture.CreateFromImage(sub);
            cache[key] = tex; order.AddFirst(key);
            while (order.Count > CacheMax) { var old = order.Last!.Value; order.RemoveLast(); cache.Remove(old); }
            return tex;
        }
        catch { return null; }
    }

    // The same picture as Card, decoded and scaled on a worker thread ahead of its first use (Yunke 11-22: the workshop's
    // small cards stuttered while scrolling — each later-cycle card cost about 25–30 ms of decoding on the main thread).
    // The texture is made back on the main thread and goes into the same cache.
    static readonly Dictionary<string, Task> pending = new();
    public static Task Prefetch(string code)
    {
        if (cache.ContainsKey(code)) return Task.CompletedTask;
        if (pending.TryGetValue(code, out var running)) return running;
        Load();
        if (!index!.TryGetValue(code, out var file)) return Task.CompletedTask;
        bool trim = !code.StartsWith("portrait:"); if (trim) CardTrim.Has(code);   // its table read here, on the main thread
        var t = PrefetchAsync(code, file, trim); pending[code] = t; return t;
    }
    static async Task PrefetchAsync(string code, string file, bool trim)
    {
        Godot.Image? img = null;
        try
        {
            img = await Task.Run(() =>
            {
                if (!File.Exists(file)) return null;
                var im = Godot.Image.LoadFromFile(file);
                if (im is null) return null;
                if (im.GetHeight() > MaxHeight) im.Resize((int)(im.GetWidth() * (MaxHeight / (float)im.GetHeight())), MaxHeight, Godot.Image.Interpolation.Lanczos);
                if (trim) im = CardTrim.Apply(code, im);
                im.GenerateMipmaps();
                return im;
            });
        }
        catch { img = null; }
        pending.Remove(code);
        if (img is null || cache.ContainsKey(code)) return;
        cache[code] = ImageTexture.CreateFromImage(img); order.AddFirst(code);
        while (order.Count > CacheMax) { var old = order.Last!.Value; order.RemoveLast(); cache.Remove(old); }
    }

    public static Texture2D? Card(string code)
    {
        if (cache.TryGetValue(code, out var hit)) { order.Remove(code); order.AddFirst(code); return hit; }
        Load();
        if (!index!.TryGetValue(code, out var file) || !File.Exists(file)) return null;
        try
        {
            var img = Godot.Image.LoadFromFile(file);
            if (img is null) return null;
            if (img.GetHeight() > MaxHeight) img.Resize((int)(img.GetWidth() * (MaxHeight / (float)img.GetHeight())), MaxHeight, Godot.Image.Interpolation.Lanczos);
            // white paper along an edge or in the corners of the scan (CardTrim, card-trim.json)
            if (!code.StartsWith("portrait:")) img = CardTrim.Apply(code, img);
            img.GenerateMipmaps();
            var tex = ImageTexture.CreateFromImage(img);
            cache[code] = tex; order.AddFirst(code);
            while (order.Count > CacheMax) { var old = order.Last!.Value; order.RemoveLast(); cache.Remove(old); }
            return tex;
        }
        catch { return null; }
    }
}
