using Godot;
using Gathering;
using System.Text.Json;

namespace ArkhamUI;

// Which of a card's abilities a choice uses, in the card's own official words (Yunke 11-19: the Knife offered two buttons both
// called "攻击 · 刀子"; the button keeps its name and a hover box shows that ability's text). The rules core numbers a card's
// abilities from 1 (choice.ability.index) in printed order: the lines that start with an action, free or reaction icon, or
// with 强制 (forced).
public static class AbilityText
{
    static Services S => App.I.S!;

    public static string? Line(string code, int index)
    {
        if (index < 1) return null;
        var card = S.Card(CoreCatalog.Normalize(code));
        var lines = J.String(card, "text").Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.Trim())
            .Where(l => l.StartsWith("[action]") || l.StartsWith("[free]") || l.StartsWith("[fast]") || l.StartsWith("[reaction]") || l.StartsWith("<b>强制") || l.StartsWith("强制")).ToList();
        return index <= lines.Count ? lines[index - 1] : null;
    }

    // the indexes of a card's printed fast / reaction abilities (the ones a trigger switch applies to), numbered as Line counts
    public static List<int> Switchable(string code)
    {
        var card = S.Card(CoreCatalog.Normalize(code));
        var lines = J.String(card, "text").Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.Trim())
            .Where(l => l.StartsWith("[action]") || l.StartsWith("[free]") || l.StartsWith("[fast]") || l.StartsWith("[reaction]") || l.StartsWith("<b>强制") || l.StartsWith("强制")).ToList();
        var res = new List<int>();
        for (int i = 0; i < lines.Count; i++) if (lines[i].StartsWith("[fast]") || lines[i].StartsWith("[free]") || lines[i].StartsWith("[reaction]")) res.Add(i + 1);
        return res;
    }

    // a choice's raw ability → its card and index
    public static (string Code, int Index)? Of(JsonElement raw)
    {
        var ability = J.Get(raw, "ability");
        if (ability.ValueKind != JsonValueKind.Object || J.Get(ability, "index").ValueKind != JsonValueKind.Number) return null;
        int index = J.Int(ability, "index");
        // the source card: an asset / event / … id resolved by the caller, or a card code here
        var src = J.Get(ability, "source");
        string code = J.String(src, "contents") is { Length: > 1 } s && s[0] == 'c' && char.IsDigit(s[1]) ? s[1..] : "";
        // a location's (or other entity's) ability names its source by entity id; the ability itself carries the card code
        if (code.Length == 0 && J.String(ability, "cardCode") is { Length: > 1 } cc && cc[0] == 'c' && char.IsDigit(cc[1])) code = cc[1..];
        return (code, index);
    }

    // the hover box: the card's name, then the one ability in the card's own words (icons drawn)
    public static Control? Box(string code, int index) => code.Length == 0 || Line(code, index) is not { } line ? null : BoxOf(S.Name(code), line);
    public static Control BoxOf(string title, string line)
    {
        var col = new VBoxContainer { CustomMinimumSize = new Vector2(380, 0), MouseFilter = Control.MouseFilterEnum.Ignore }; col.AddThemeConstantOverride("separation", 8);
        col.AddChild(Style.Text(title, 22, Style.HoverText, Style.Kai));
        var r = CardReader.Rules(line, 19); r.CustomMinimumSize = new Vector2(360, 0); r.MouseFilter = Control.MouseFilterEnum.Ignore; col.AddChild(r);
        return col;   // Peek wraps it in its box
    }
}
