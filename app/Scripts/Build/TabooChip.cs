using Godot;
using System.Text.Json;

namespace ArkhamUI;

// A taboo list as a small framed number (Yunke 11-19: "禁卡表的版本用一个小框带数字来表示，尽量少显示汉字") — "2.5" in a
// little rounded box; the date and the words go in the tooltip. "—" is no taboo list. build's sets (decks.taboos) are read
// once and kept by id, so a deck's taboo_id can be shown anywhere.
public static class TabooChip
{
    static Dictionary<int, (string Name, string Date)>? sets;

    public static async Task<Dictionary<int, (string Name, string Date)>> Sets()
    {
        if (sets is not null) return sets;
        var d = new Dictionary<int, (string, string)>();
        try
        {
            foreach (var t in J.Items(await BuildEngine.I.CallJson("decks.taboos")))
            {
                string date = J.String(t, "date");
                d[J.Int(t, "id")] = (J.String(t, "name"), date.Length >= 10 ? date[..10] : date);
            }
        }
        catch (Exception e) { GD.PushWarning("taboo sets: " + e.Message); return d; }
        return sets = d;
    }
    public static string? NameOf(int id) => sets is not null && sets.TryGetValue(id, out var t) ? t.Name : null;
    public static string Tip(int? id) => id is int i && sets is not null && sets.TryGetValue(i, out var t) ? "禁卡表 " + t.Name + "（" + t.Date + "）" : "不使用禁卡表";

    static StyleBoxFlat Box(bool lit, float bg, float radius = 7)
    {
        var sb = new StyleBoxFlat { BgColor = new Color(lit ? new Color("3a2a12") : new Color(0.06f, 0.05f, 0.04f), bg), BorderColor = lit ? Style.Amber : new Color(Style.Paper, 0.45f), AntiAliasing = true };
        sb.SetBorderWidthAll(lit ? 2 : 1); sb.SetCornerRadiusAll((int)radius); sb.ContentMarginLeft = sb.ContentMarginRight = 9; sb.ContentMarginTop = sb.ContentMarginBottom = 2;
        if (lit) { sb.ShadowColor = new Color(Style.Amber, 0.35f); sb.ShadowSize = 6; }
        return sb;
    }

    // a static badge (not clickable): the number in its box
    public static Control Badge(int? id, int font = 17)
    {
        var p = new PanelContainer { MouseFilter = Control.MouseFilterEnum.Pass, TooltipText = Tip(id), SizeFlagsVertical = Control.SizeFlags.ShrinkCenter };
        p.AddThemeStyleboxOverride("panel", Box(id is not null, 0.7f, 6));
        var l = Style.Text(id is int i ? NameOf(i) ?? i.ToString() : "—", font, id is null ? Style.PaperDim : Style.Amber, Style.Sans);
        l.HorizontalAlignment = HorizontalAlignment.Center; l.MouseFilter = Control.MouseFilterEnum.Ignore; p.AddChild(l);
        return p;
    }

    // a choice: the number in its box, lit when chosen
    public static Button Choice(int? id, bool on, Action press, int font = 19)
    {
        var b = new Button { Text = id is int i ? NameOf(i) ?? i.ToString() : "—", ToggleMode = true, ButtonPressed = on, FocusMode = Control.FocusModeEnum.None, TooltipText = Tip(id),
            CustomMinimumSize = new Vector2(58, 40), MouseDefaultCursorShape = Control.CursorShape.PointingHand };
        b.AddThemeFontSizeOverride("font_size", font); if (Style.Sans is not null) b.AddThemeFontOverride("font", Style.Sans);
        b.AddThemeStyleboxOverride("normal", Box(false, 0.45f)); b.AddThemeStyleboxOverride("hover", Box(true, 0.35f));
        b.AddThemeStyleboxOverride("pressed", Box(true, 0.9f)); b.AddThemeStyleboxOverride("hover_pressed", Box(true, 0.95f)); b.AddThemeStyleboxOverride("focus", new StyleBoxEmpty());
        b.AddThemeColorOverride("font_color", Style.Paper); b.AddThemeColorOverride("font_hover_color", Style.HoverText);
        b.AddThemeColorOverride("font_pressed_color", Style.Amber); b.AddThemeColorOverride("font_hover_pressed_color", Style.Amber);
        b.Pressed += () => { Sfx.Click(); press(); };
        return b;
    }

    // every set as a row of choices, newest first, "—" (none) first
    public static async Task Fill(Container row, int? current, Action<int?> pick)
    {
        var all = await Sets();
        if (!GodotObject.IsInstanceValid(row)) return;
        var group = new List<Button>();
        void Add(int? id)
        {
            Button b = null!;
            b = Choice(id, current == id, () => { foreach (var o in group) o.SetPressedNoSignal(o == b); pick(id); });
            group.Add(b); row.AddChild(b);
        }
        Add(null);
        foreach (var (id, _) in all.OrderByDescending(kv => kv.Value.Date)) Add(id);
    }
}
