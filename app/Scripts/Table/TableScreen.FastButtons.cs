using Godot;
using Gathering;

namespace ArkhamUI;

// Yunke 11-19: the abilities on a small card, by kind —
//  · fast (lightning) abilities: a button the size of the card's other symbols, on the card itself; on a location it is always
//    there (lit when it can be used now), on other cards only while it can be used. Its choices are not in the card's own menu.
//  · reaction abilities: no symbol; when one can be used the whole card is the button (it glows, a click triggers it).
//  · forced abilities: when one triggers, its card shakes (TableScreen.Notice).
public partial class TableScreen
{
    // keys whose fast abilities have their own button on the card this render (a click on the card leaves them out)
    readonly HashSet<string> fastOnCard = new();

    static bool IsFast(ChoicePresentation c) => c.Tag == "AbilityLabel" && J.Tag(J.Get(J.Get(c.Raw, "ability"), "type")).StartsWith("Fast");

    List<ChoicePresentation> FastChoices(string key) => byAnchor.TryGetValue(key, out var l) ? l.Where(c => c.Enabled && IsFast(c)).ToList() : new();

    // what a click on the card itself offers once its fast abilities have their own button
    bool CardActionable(string key, bool fastHasButton, bool actionHasButton = false) =>
        byAnchor.TryGetValue(key, out var l) && l.Any(c => !(fastHasButton && IsFast(c)) && !(actionHasButton && IsPrintedAction(c)));

    // keys whose printed action abilities have their own button on the card (a location): a click on the card itself
    // investigates (Yunke 11-20: "点按地点的时候默认就是调查，点按小按钮的时候才是发动技能")
    readonly HashSet<string> actionOnCard = new();
    // A card's own [action] ability: one with a printed line, or any action ability numbered below 100 — the core numbers the
    // basic investigate / move from 100 up, and 撤退 (resign, index 99: 月下美人) has no line of its own in the text tables
    // (Yunke 11-22: the location's button stayed dim while a click on the card offered the resign)
    static bool IsPrintedAction(ChoicePresentation c)
    {
        if (c.Tag != "AbilityLabel" || IsFast(c)) return false;
        if (AbilityText.Of(c.Raw) is { } ab && AbilityText.Line(ab.Code, ab.Index) is not null) return true;
        var a = J.Get(c.Raw, "ability");
        return J.Tag(J.Get(a, "type")) == "ActionAbility" && J.Get(a, "index").ValueKind == System.Text.Json.JsonValueKind.Number && J.Int(a, "index") < 100;
    }

    // the card's printed text has a fast / free ability (on the side that is up)
    static bool PrintsFast(string rulesText) => rulesText.Contains("[fast]") || rulesText.Contains("[free]");

    // A lightning button of the card symbols' size. always: shown (dim) even when it cannot be used now.
    Control? FastButton(string key, string code, float d, bool always)
    {
        var list = FastChoices(key);
        if (list.Count == 0 && !always) return null;
        fastOnCard.Add(key);
        return AbilityButton(code, d, "fast", list, "快速能力（现在不能使用）");
    }

    // The card's printed action (→) abilities that can be used now — not the basic actions (investigate, move…) offered on it.
    List<ChoicePresentation> PrintedActionChoices(string key) => byAnchor.TryGetValue(key, out var l)
        ? l.Where(c => c.Enabled && IsPrintedAction(c)).ToList()
        : new();

    // A round ability button on a card (Yunke 11-20: bigger on a location; the pointer on it lifts and brightens it; while it can
    // be used a soft ring breathes round it — a glow fading outward, no hard edge). Dim and inert when it cannot be used now.
    Control AbilityButton(string code, float d, string glyph, List<ChoicePresentation> list, string idleTip)
    {
        bool live = list.Count > 0 && !busy;
        string tip = list.Count > 0 ? string.Join("\n", list.Select(c => c.Title)) : idleTip;
        var b = new Button { FocusMode = FocusModeEnum.None, CustomMinimumSize = new Vector2(d, d), Size = new Vector2(d, d), TooltipText = tip,
            MouseDefaultCursorShape = live ? CursorShape.PointingHand : CursorShape.Arrow, PivotOffset = new Vector2(d / 2, d / 2) };
        foreach (var (st, k) in new[] { ("normal", 0f), ("hover", 0.25f), ("pressed", 0.4f), ("disabled", 0f) })
        {
            var sb = new StyleBoxFlat { AntiAliasing = true,
                BgColor = live ? new Color(0.36f, 0.24f, 0.08f, 0.95f).Lightened(k) : new Color(0.06f, 0.05f, 0.04f, 0.88f).Lightened(k * 0.5f),
                BorderColor = live ? new Color("ffcf6e") : new Color("e0b56a", 0.8f) };
            sb.SetBorderWidthAll(live ? 2 : 1); sb.SetCornerRadiusAll((int)(d / 2)); b.AddThemeStyleboxOverride(st, sb);
        }
        b.AddThemeStyleboxOverride("focus", new StyleBoxEmpty());
        if (live)
        {
            // the ring: a soft amber glow behind the button, breathing
            var ring = new Panel { MouseFilter = MouseFilterEnum.Ignore, ShowBehindParent = true, Position = Vector2.Zero, Size = new Vector2(d, d) };
            var rsb = new StyleBoxFlat { BgColor = new Color(Style.Amber, 0.55f), ShadowColor = new Color(1f, 0.78f, 0.36f, 1f), ShadowSize = (int)(d * 0.6f), AntiAliasing = true };
            rsb.SetCornerRadiusAll((int)(d / 2)); ring.AddThemeStyleboxOverride("panel", rsb); b.AddChild(ring);
            if (S.Settings.ReducedMotion != true)
            {
                ring.Modulate = new Color(1, 1, 1, 0.65f);
                var tw = ring.CreateTween().SetLoops();
                tw.TweenProperty(ring, "modulate:a", 1f, 0.9f).SetTrans(Tween.TransitionType.Sine);
                tw.TweenProperty(ring, "modulate:a", 0.65f, 0.9f).SetTrans(Tween.TransitionType.Sine);
            }
        }
        var g = Ui.Glyph(glyph, (int)(d * 0.62f), live ? Colors.White : new Color("ffe2a6")); g.HorizontalAlignment = HorizontalAlignment.Center; g.VerticalAlignment = VerticalAlignment.Center; g.MouseFilter = MouseFilterEnum.Ignore;
        b.AddChild(g); g.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        // the pointer on it: it grows a little and brightens (usable or not, so it reads as a thing to look at)
        Tween? hov = null;
        void Hover(bool on)
        {
            hov?.Kill(); hov = b.CreateTween().SetParallel();
            hov.TweenProperty(b, "scale", on ? new Vector2(1.18f, 1.18f) : Vector2.One, 0.12f).SetTrans(Tween.TransitionType.Sine);
            hov.TweenProperty(b, "self_modulate", on ? new Color(1.3f, 1.2f, 1.0f) : Colors.White, 0.12f);
        }
        b.MouseEntered += () => Hover(true); b.MouseExited += () => Hover(false);
        if (live)
        {
            b.Pressed += () =>
            {
                if (busy) return;
                Sfx.Click();
                if (list.Count == 1) Choose(list[0]); else InPlaceMenu(b, S.Name(code), list, code);
            };
            if (list.Count == 1 && ChoiceAbility(list[0]) is { } ca)
            { b.MouseEntered += () => Peek.Schedule(b, () => AbilityText.BoxOf(ca.Title, ca.Line), 0.25); b.MouseExited += Peek.Hide; }
        }
        return b;
    }
}
