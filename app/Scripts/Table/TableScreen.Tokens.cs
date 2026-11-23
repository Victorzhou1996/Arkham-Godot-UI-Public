using Godot;
using Gathering;
using System.Text.Json;

namespace ArkhamUI;

// Chaos tokens revealed outside a skill test (扭转命运, 卡牌室's ability …): the core holds them among its focused tokens while
// the "when a token is revealed" window is open (歪打正着 can treat it as another token). They are shown in the middle of
// the table, so the player sees what came out before deciding what to do about it (Yunke 11-22: the table went straight to
// 歪打正着's choice and the drawn token was never shown).
public partial class TableScreen
{
    readonly HashSet<string> tokensPopped = new();   // token ids already shown with their pop

    // the focused chaos tokens (face, id); in a skill test only the one(s) just drawn that the test does not list yet (QQ #28 —
    // the test panel shows them with its own tokens)
    static List<(string Face, string Id, string By)> FocusedTokens(TableModel m)
    {
        var list = new List<(string, string, string)>();
        if (m.InSkillTest) return m.FreshTestTokens;
        var f = J.Get(m.Game, "focusedChaosTokens"); if (f.ValueKind != JsonValueKind.Array) f = J.Get(m.Game, "gameFocusedChaosTokens");
        foreach (var t in J.Items(f))
            if (J.String(t, "chaosTokenFace") is { Length: > 0 } face) list.Add((face, J.String(t, "chaosTokenId"), J.String(t, "chaosTokenRevealedBy")));
        // while 歪打正着 asks what to treat it as, the core focuses the whole bag too: only the revealed one(s) have a revealer
        // (QA hw2: the bag's sixteen tokens showed as "revealed")
        if (list.Any(t => t.Item3.Length > 0)) return list.Where(t => t.Item3.Length > 0).ToList();
        return list.Count <= 3 ? list : new();
    }

    // "<who> 揭示了" over the tokens, large; a token not seen before pops in
    Control RevealedTokenRow(TableModel m, List<(string Face, string Id, string By)> tokens, float size)
    {
        var col = new VBoxContainer { MouseFilter = MouseFilterEnum.Ignore, Alignment = BoxContainer.AlignmentMode.Center }; col.AddThemeConstantOverride("separation", 8);
        string who = tokens.Select(t => t.By).FirstOrDefault(b => b.Length > 0) is { } by && m.Investigators.FirstOrDefault(i => i.Id == by) is { } inv ? m.Name(inv) : "";
        var head = Style.Text((who.Length > 0 ? who + " " : "") + "从混沌袋揭示了", 24, Style.HoverText, Style.Kai); head.HorizontalAlignment = HorizontalAlignment.Center;
        head.AddThemeConstantOverride("outline_size", 8); head.AddThemeColorOverride("font_outline_color", Colors.Black); col.AddChild(head);
        var row = new HBoxContainer { MouseFilter = MouseFilterEnum.Ignore, Alignment = BoxContainer.AlignmentMode.Center }; row.AddThemeConstantOverride("separation", 16); col.AddChild(row);
        foreach (var (face, id, _) in tokens)
        {
            var cell = new VBoxContainer { MouseFilter = MouseFilterEnum.Ignore }; cell.AddThemeConstantOverride("separation", 2);
            var img = Ui.Image(Ui.Token(face), new Vector2(size, size)); img.MouseFilter = MouseFilterEnum.Ignore; img.PivotOffset = new Vector2(size / 2, size / 2); cell.AddChild(img);
            var name = Style.Text(TokenName(face), 20, Style.Amber, Style.Kai); name.HorizontalAlignment = HorizontalAlignment.Center; cell.AddChild(name);
            row.AddChild(cell);
            if (S.Settings.ReducedMotion != true && id.Length > 0 && tokensPopped.Add(id))
            {
                img.Scale = new Vector2(0.4f, 0.4f); img.Modulate = new Color(1, 1, 1, 0);
                var tw = img.CreateTween().SetParallel();
                tw.TweenProperty(img, "scale", Vector2.One, 0.35f).SetTrans(Tween.TransitionType.Back).SetEase(Tween.EaseType.Out);
                tw.TweenProperty(img, "modulate:a", 1f, 0.2f);
                Sfx.Click();
            }
        }
        return col;
    }

    // a dark rounded patch behind something shown over the map
    static PanelContainer Patch(Control inner, float alpha = 0.82f)
    {
        var p = new PanelContainer { MouseFilter = MouseFilterEnum.Ignore };
        var sb = new StyleBoxFlat { BgColor = new Color(0.02f, 0.015f, 0.01f, alpha), ContentMarginLeft = 28, ContentMarginRight = 28, ContentMarginTop = 18, ContentMarginBottom = 20, AntiAliasing = true,
            ShadowColor = new Color(0, 0, 0, 0.55f), ShadowSize = 24, BorderColor = new Color(Style.Brass, 0.45f) };
        sb.SetCornerRadiusAll(18); sb.SetBorderWidthAll(1); p.AddThemeStyleboxOverride("panel", sb); p.AddChild(inner);
        return p;
    }

    // the revealed tokens alone (no token picker on screen): upper middle of the table, above the map, not in the way of clicks
    void FocusedTokenBanner(TableModel m, bool pickerShown)
    {
        if (pickerShown || m.InSkillTest) return;   // in a test the panel shows them (TableScreen.Panels)
        var tokens = FocusedTokens(m); if (tokens.Count == 0) return;
        var patch = Patch(RevealedTokenRow(m, tokens, 96)); patch.ZIndex = 69;
        Place(patch, -210, -380, 210, -170, 0.5f, 0.5f, 0.5f, 0.5f);
        if (App.I.QaRunning) GD.Print("QA_TOKENS_SHOWN " + string.Join(",", tokens.Select(t => t.Face)));
    }
}
