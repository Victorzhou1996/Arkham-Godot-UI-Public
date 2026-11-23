using Godot;
using Gathering;
using System.Text.RegularExpressions;

namespace ArkhamUI;

// Assigning damage and horror, on the cards themselves (Yunke 11-20 #13, #15, #23).
// Before: the investigator (and each soaking asset) was shown again as a card in the middle of the table, with "承受 1 伤害"
// buttons — the player could not tell what was being assigned where, and the "1" was wrong: the rules core assigns ALL that is
// left in one click when there is only one card that can take it (reference Investigator/Runner/Damage.hs, applyAll =
// targetCount == 1), one point per click otherwise. Now each card that can take damage or horror — your portrait at the
// bottom left, an asset in your slots, a teammate's portrait — gets a red (damage) / blue (horror) breathing edge and, at
// its foot, one chip per kind saying exactly how much a click places there. A line above the hand says what is left.
public partial class TableScreen
{
    sealed record AssignOpt(ChoicePresentation C, bool Damage, string Key, int Amount);

    static readonly Regex LeftRe = new(@"待分配(\d+)点");
    static readonly Color HorrorBlue = new("6f8fe0");
    readonly Dictionary<string, Control> teamPics = new();   // others' portraits in the team column, by investigator id

    static bool IsAssignChoice(ChoicePresentation c) => c.Enabled && c.Tag is "ComponentLabel" or "AuxiliaryComponentLabel" && AssignRe.IsMatch(c.Title);

    // what is still to place of each kind (the rules core's label, carried in the titles)
    static int AssignLeft(TableModel m, bool damage) => m.Question.Choices.Where(c => IsAssignChoice(c) && c.Title.Contains(damage ? "分配伤害" : "分配恐惧"))
        .Select(c => LeftRe.Match(c.Title)).Where(x => x.Success).Select(x => int.Parse(x.Groups[1].Value)).DefaultIfEmpty(0).Max();

    List<AssignOpt> AssignOptions(TableModel m)
    {
        var all = m.Question.Choices.Where(IsAssignChoice).ToList();
        var opts = new List<AssignOpt>();
        foreach (bool dmg in new[] { true, false })
        {
            var mine = all.Where(c => c.Title.Contains(dmg ? "分配伤害" : "分配恐惧")).ToList();
            int left = AssignLeft(m, dmg);
            foreach (var c in mine) opts.Add(new AssignOpt(c, dmg, m.Anchor(c), RawAmount(c, dmg) ?? (mine.Count == 1 ? Math.Max(1, left) : 1)));
        }
        return opts;
    }

    // the amount the choice's own messages place (InvestigatorDamage_ [iid, source, damage, horror]; an asset's
    // Assign…Damage… [aid, source, damage, horror]) — exact, read from what the rules core will do
    static int? RawAmount(ChoicePresentation c, bool damage)
    {
        foreach (var msg in J.Items(J.Get(c.Raw, "messages")))
        {
            var inner = J.Tag(msg) is "InvestigatorMessage" or "AssetMessage" ? J.Get(msg, "contents") : msg;
            string tag = J.Tag(inner);
            if (tag.StartsWith("InvestigatorDamage") || (tag.StartsWith("Assign") && tag.Contains("AssetDamage")) || tag.StartsWith("AssetDamage"))
            {
                var args = J.Items(J.Get(inner, "contents")).ToList();
                int ix = damage ? 2 : 3;
                if (args.Count > ix && args[ix].ValueKind == System.Text.Json.JsonValueKind.Number) { int n = args[ix].GetInt32(); if (n > 0) return n; }
            }
        }
        return null;
    }

    // how much one click on this choice places (for the floating note after it)
    int AssignAmount(ChoicePresentation c) => model is { } m ? AssignOptions(m).FirstOrDefault(o => o.C == c)?.Amount ?? 1 : 1;

    // the card on the table that stands for an assignment target
    Control? AssignHost(TableModel m, string key)
    {
        if (key.StartsWith("inv:") && key != "inv:" + m.Me?.Id && teamPics.TryGetValue(key[4..], out var tp) && IsInstanceValid(tp)) return tp;
        return anchors.TryGetValue(key, out var a) && IsInstanceValid(a) && a.IsVisibleInTree() ? a : null;
    }

    // the choices that are shown on their cards (and so not again in the middle of the table)
    HashSet<ChoicePresentation> AssignOnCards(TableModel m) => AssignOptions(m).Where(o => AssignHost(m, o.Key) is not null).Select(o => o.C).ToHashSet();

    // After the table is built: the edges and chips on every card that can take damage or horror now.
    void AssignOverlay()
    {
        var m = model; if (m is null) return;
        var opts = AssignOptions(m); if (opts.Count == 0) return;
        if (App.I.QaRunning) GD.Print("QA_ASSIGN_OVERLAY " + string.Join(",", opts.Select(o => o.Key + (o.Damage ? ":d" : ":h") + o.Amount + (AssignHost(m, o.Key) is null ? "(no card)" : ""))));
        foreach (var group in opts.GroupBy(o => o.Key))
        {
            var host = AssignHost(m, group.Key); if (host is null) continue;
            var size = host.Size; if (size.X <= 0) size = host.CustomMinimumSize;
            // a host that clips its contents (your portrait strip) carries the rig on its parent, at its own place
            var rig = new Node2D { ZIndex = 8 };
            if (host.ClipContents && host.GetParent() is Control hp) { hp.AddChild(rig); rig.Position = host.Position; } else host.AddChild(rig);
            bool anyDmg = group.Any(o => o.Damage);
            // the edge: red for damage, blue when only horror can go here; breathing
            var edge = new Panel { MouseFilter = MouseFilterEnum.Ignore, Position = new Vector2(-3, -3), Size = size + new Vector2(6, 6) };
            var col = anyDmg ? AssignRed : HorrorBlue;
            var sb = new StyleBoxFlat { DrawCenter = false, BgColor = new Color(0, 0, 0, 0), BorderColor = col, ShadowColor = new Color(col, 0.6f), ShadowSize = 14, AntiAliasing = true };
            sb.SetBorderWidthAll(3); sb.SetCornerRadiusAll(10); edge.AddThemeStyleboxOverride("panel", sb); rig.AddChild(edge);
            if (S.Settings.ReducedMotion != true) { var tw = edge.CreateTween().SetLoops(); tw.TweenProperty(edge, "modulate:a", 0.4f, 0.7f); tw.TweenProperty(edge, "modulate:a", 1f, 0.7f); }
            // the chips: only the token itself — a damage or horror token, big enough to read (Yunke 11-20: the "+1" pills were hard to
            // read, and on an ally they ran into its mode switch at the foot of the card) — in the middle of the card
            float d = size.Y > 150 ? 64 : Math.Clamp(Math.Min(size.X, size.Y) * 0.48f, 34, 52);
            var row = new HBoxContainer { MouseFilter = MouseFilterEnum.Ignore }; row.AddThemeConstantOverride("separation", (int)(d * 0.12f)); rig.AddChild(row);
            foreach (var o in group.OrderBy(o => o.Damage ? 0 : 1))
            {
                var b = new TextureButton { TextureNormal = Ui.Stat(o.Damage ? "health" : "horror"), IgnoreTextureSize = true, StretchMode = TextureButton.StretchModeEnum.KeepAspectCentered,
                    CustomMinimumSize = new Vector2(d, d), PivotOffset = new Vector2(d / 2, d / 2), MouseDefaultCursorShape = CursorShape.PointingHand,
                    TooltipText = "点击：把" + (o.Amount > 1 ? "剩下的 " + o.Amount + " 点" : " 1 点") + (o.Damage ? "伤害" : "恐惧") + "分配到这里" };
                // a soft glow behind it in its colour, so it stands off the card's picture
                var c = o.Damage ? AssignRed : HorrorBlue;
                var halo = new Panel { MouseFilter = MouseFilterEnum.Ignore, ShowBehindParent = true, Position = new Vector2(d * 0.12f, d * 0.12f), Size = new Vector2(d * 0.76f, d * 0.76f) };
                var hs = new StyleBoxFlat { BgColor = new Color(0, 0, 0, 0.35f), ShadowColor = new Color(c, 0.85f), ShadowSize = (int)(d * 0.28f), AntiAliasing = true }; hs.SetCornerRadiusAll((int)d);
                halo.AddThemeStyleboxOverride("panel", hs); b.AddChild(halo);
                Tween? hov = null;
                void Hover(bool on) { hov?.Kill(); hov = b.CreateTween().SetParallel(); hov.TweenProperty(b, "scale", on ? new Vector2(1.18f, 1.18f) : Vector2.One, 0.1f); hov.TweenProperty(b, "self_modulate", on ? new Color(1.3f, 1.25f, 1.2f) : Colors.White, 0.1f); }
                b.MouseEntered += () => Hover(true); b.MouseExited += () => Hover(false);
                // two choices of the same kind here (1 point, or all that is left): the number on the token tells them apart
                if (group.Count(x => x.Damage == o.Damage) > 1)
                {
                    var num = Style.Text(o.Amount.ToString(), (int)(d * 0.5f), Colors.White, Style.Symbols); num.MouseFilter = MouseFilterEnum.Ignore;
                    num.AddThemeConstantOverride("outline_size", 7); num.AddThemeColorOverride("font_outline_color", Colors.Black);
                    b.AddChild(num); num.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect); num.HorizontalAlignment = HorizontalAlignment.Center; num.VerticalAlignment = VerticalAlignment.Center;
                }
                var pick = o.C; var at = host;
                b.Pressed += () => { if (busy) return; Sfx.Click(); assignFrom = at; Choose(pick); };
                row.AddChild(b);
            }
            int n = group.Count(); float rw = d * n + d * 0.12f * (n - 1);
            row.Position = new Vector2((size.X - rw) / 2, (size.Y - d) / 2);
        }
    }

    // the line above the hand: what is being assigned, from what, and how much of each is left
    Control AssignPill(TableModel m)
    {
        int dmgLeft = AssignLeft(m, true), horLeft = AssignLeft(m, false);
        var pill = new PanelContainer { MouseFilter = MouseFilterEnum.Ignore };
        pill.AddThemeStyleboxOverride("panel", Ui.SoftBox(0.7f, AssignRed, 2, 16, 6));
        var h = new HBoxContainer { MouseFilter = MouseFilterEnum.Ignore }; h.AddThemeConstantOverride("separation", 8); pill.AddChild(h);
        string what = dmgLeft > 0 && horLeft > 0 ? "分配伤害和恐惧" : dmgLeft > 0 ? "分配伤害" : "分配恐惧";
        string from = !string.IsNullOrEmpty(effectSource) && effectSourceAge <= 1 ? "（" + effectSource + "）" : "";
        var t = Style.Text(what + from + " · 还要分配", 20, Style.Paper, Style.Kai); t.VerticalAlignment = VerticalAlignment.Center; h.AddChild(t);
        foreach (var (kind, n, color) in new[] { ("health", dmgLeft, Style.Danger), ("horror", horLeft, Style.Willpower) })
        {
            var ic = Ui.Image(Ui.Stat(kind), new Vector2(30, 30)); ic.MouseFilter = MouseFilterEnum.Ignore; ic.SizeFlagsVertical = SizeFlags.ShrinkCenter; ic.Modulate = n > 0 ? Colors.White : new Color(1, 1, 1, 0.35f); h.AddChild(ic);
            var nt = Style.Text(n.ToString(), 24, n > 0 ? color : Style.PaperDim, Style.Symbols); nt.VerticalAlignment = VerticalAlignment.Center; h.AddChild(nt);
        }
        var tip = Style.Text("· 点击发光卡片下方的标记", 18, Style.PaperDim, Style.Kai); tip.VerticalAlignment = VerticalAlignment.Center; h.AddChild(tip);
        return pill;
    }
}
