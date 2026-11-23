using Godot;
using Gathering;
using System.Text.Json;

namespace ArkhamUI;

// A customizable card's ticked upgrades, for hover boxes (Yunke 11-19): one line per option bought into — its boxes (■ ticked,
// □ open) and its name from the official text, then what was chosen (a skill, traits, cards). Used by the workshop preview
// (build's sheet) and by the table (the rules core's card: customizations = [[option index, [ticks, [choices]]], …]).
public static class CustomTicks
{
    static Services S => App.I.S!;

    public static bool IsCustomizable(string code)
    {
        var c = S.Card(CoreCatalog.Normalize(code));
        return c.ValueKind == JsonValueKind.Object && J.Get(c, "customization_options").ValueKind == JsonValueKind.Array;
    }

    // from the rules core: an asset in play or a card in hand
    public static Control? FromCore(JsonElement customizations, string code)
    {
        if (!IsCustomizable(code)) return null;
        var chosen = new Dictionary<int, (int, string)>();
        foreach (var pair in J.Items(customizations))
        {
            if (pair.ValueKind != JsonValueKind.Array || pair.GetArrayLength() != 2 || !pair[0].TryGetInt32(out int index)) continue;
            var v = pair[1]; int spent = 0; var sel = new List<string>();
            if (v.ValueKind == JsonValueKind.Array && v.GetArrayLength() >= 1 && v[0].TryGetInt32(out int n)) spent = n;
            if (v.ValueKind == JsonValueKind.Array && v.GetArrayLength() >= 2) foreach (var s in J.Items(v[1])) sel.Add(Choice(s));
            chosen[index] = (spent, string.Join("、", sel.Where(x => x.Length > 0)));
        }
        return Render(code, chosen);
    }

    // a choice as the core writes it (a skill, a trait, a card, …) in Chinese where we can
    static string Choice(JsonElement s)
    {
        string raw = s.ValueKind == JsonValueKind.String ? s.GetString()! : s.ValueKind == JsonValueKind.Object ? (J.Get(J.Get(s, "contents"), "contents").ValueKind == JsonValueKind.String ? J.String(J.Get(s, "contents"), "contents") : J.String(s, "contents")) : s.GetRawText();
        if (raw.Contains("Willpower", StringComparison.OrdinalIgnoreCase)) return "意志";
        if (raw.Contains("Intellect", StringComparison.OrdinalIgnoreCase)) return "智力";
        if (raw.Contains("Combat", StringComparison.OrdinalIgnoreCase)) return "战斗";
        if (raw.Contains("Agility", StringComparison.OrdinalIgnoreCase)) return "敏捷";
        string code = CoreCatalog.Normalize(raw);
        if (code.Length >= 5 && char.IsDigit(code[0]) && S.Name(code) is { Length: > 0 } nm) return nm;
        return raw;
    }

    // option index → (ticks, what was chosen); the option names and box counts from the card's own data
    public static Control? Render(string code, IReadOnlyDictionary<int, (int Spent, string Sel)> chosen)
    {
        var card = S.Card(CoreCatalog.Normalize(code));
        var options = J.Items(J.Get(card, "customization_options")).ToList();
        if (options.Count == 0) return null;
        var lines = System.Text.RegularExpressions.Regex.Replace(J.String(card, "customization_text"), "<[^>]+>", "").Replace("[[", "").Replace("]]", "").Split('\n', StringSplitOptions.RemoveEmptyEntries);
        var col = new VBoxContainer { MouseFilter = Control.MouseFilterEnum.Ignore, CustomMinimumSize = new Vector2(390, 0) }; col.AddThemeConstantOverride("separation", 4);
        col.AddChild(Style.Text("可定制升级（■ 已勾 □ 未勾）", 17, Style.Brass, Style.Kai));
        // every option, bought or not, with its full official text (Yunke 11-19: the whole sheet, not only the ticked names)
        for (int index = 0; index < options.Count; index++)
        {
            int boxes = Math.Max(1, J.Int(options[index], "xp"));
            var mine = chosen.TryGetValue(index, out var v) ? v : (0, "");
            int spent = Math.Clamp(mine.Item1, 0, boxes); bool done = spent >= boxes;
            string text = index < lines.Length ? lines[index].TrimStart('□', '■', ' ') : "";
            string sel = mine.Item2 switch { "willpower" => "意志", "intellect" => "智力", "combat" => "战斗", "agility" => "敏捷", var x => x.Replace("^", "、") };
            var row = new HBoxContainer { MouseFilter = Control.MouseFilterEnum.Ignore }; row.AddThemeConstantOverride("separation", 6); col.AddChild(row);
            var marks = Style.Text(new string('■', spent) + new string('□', boxes - spent), 15, done ? new Color("f0c060") : spent > 0 ? new Color("c9a86a") : Style.PaperDim, Style.Symbols);
            marks.SizeFlagsVertical = Control.SizeFlags.ShrinkBegin; marks.CustomMinimumSize = new Vector2(46, 0); row.AddChild(marks);
            var nl = Style.Text(text + (sel.Length > 0 && done ? "（已选：" + sel + "）" : ""), 15, done ? Style.HoverText : new Color(Style.PaperDim, 0.85f), Style.Kai);
            nl.AutowrapMode = TextServer.AutowrapMode.WordSmart; nl.CustomMinimumSize = new Vector2(330, 0); nl.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill; row.AddChild(nl);
        }
        return col;
    }
}
