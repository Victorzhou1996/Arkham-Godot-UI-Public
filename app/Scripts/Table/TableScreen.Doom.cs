using System.Text.Json;
using Godot;
using Gathering;

namespace ArkhamUI;

// Yunke 11-19 r9 #16 and #20: what the core settles in one step is shown one step at a time.
//  · doom: each doom placed flies as a token onto the agenda's doom track, its pip filling as it lands; a full track flares
//    before the agenda's advance is told (the interlude follows, PlayFlips);
//  · weaknesses: a weakness drawn off a player's deck is turned over like an encounter card (on the player's card back) and,
//    if it stays in play there, flies into the threat area on the right.
public partial class TableScreen
{
    static bool IsWeakness(Services s, string code) => J.String(s.Card(CoreCatalog.Normalize(code)), "subtype_code") is "weakness" or "basicweakness";

    int lastDoom = -1, lastDoomMax, lastCardDoom;
    Dictionary<string, int> lastDoomOn = new();   // doom on each card in play (not the agendas), by anchor key

    // Doom lying on cards in play (an enemy, a place, a treachery…) rather than on the agenda: it counts against the agenda but
    // stays on its card, and goes with it (Yunke 11-20: the 祭司's doom). Keyed as the table anchors them.
    static Dictionary<string, int> DoomOnCards(TableModel m)
    {
        var d = new Dictionary<string, int>();
        foreach (var e in m.Enemies) if (m.Doom(e) > 0) d["enemy:" + e.Id] = m.Doom(e);
        foreach (var l in m.Locations) if (m.Doom(l) > 0) d["loc:" + l.Id] = m.Doom(l);
        foreach (var t in m.Treacheries.Concat(m.Assets)) if (m.Doom(t) > 0) d[m.ThreatKey(t)] = m.Doom(t);
        return d;
    }
    static int DoomOnAgendas(TableModel m) => m.Agendas.Sum(a => m.Doom(a));

    // a doom pip: 0 empty, 1 on the agenda (filled red), 2 on a card in play (an orange ring — Yunke 11-20)
    internal static StyleBoxFlat DoomPip(int kind) => kind switch
    {
        1 => Style.Box(new Color("b0402e"), new Color("d58b72"), 1, 8, 0, 0),
        2 => Style.Box(new Color(0.91f, 0.6f, 0.3f, 0.5f), new Color(1f, 0.74f, 0.48f, 0.75f), 1, 8, 0, 0),   // solid orange (Yunke 11-20), paler (11-21)
        _ => Style.Box(new Color(0, 0, 0, 0.4f), new Color("d58b72"), 1, 8, 0, 0),
    };

    async Task DoomFlights(bool agendaAdvanced)
    {
        var m = model; if (m is null) return;
        int now = m.TotalDoom, max = Math.Max(1, m.DoomThreshold);
        int from = lastDoom, fromMax = lastDoomMax, cardFrom = lastCardDoom;
        var onCards = DoomOnCards(m); var before = lastDoomOn;
        int cardNow = Math.Clamp(now - DoomOnAgendas(m), 0, now);
        lastDoom = now; lastDoomMax = max; lastCardDoom = cardNow; lastDoomOn = onCards;
        // where each card with doom was last drawn (the table still shows the previous state until the next render): a card that
        // leaves play takes its doom with it, and that doom is seen leaving from there
        var removedFrom = new Dictionary<string, Rect2>();
        foreach (var (k, n) in before)
            if (onCards.GetValueOrDefault(k) < n)
                removedFrom[k] = anchors.TryGetValue(k, out var an) && IsInstanceValid(an) && an.IsVisibleInTree() ? an.GetGlobalRect() : doomRects.GetValueOrDefault(k);
        foreach (var k in onCards.Keys) if (anchors.TryGetValue(k, out var an2) && IsInstanceValid(an2) && an2.IsVisibleInTree()) doomRects[k] = an2.GetGlobalRect();
        if (App.I.QaRunning) GD.Print("QA_DOOM_CHECK " + from + "->" + now + " max " + fromMax + "->" + max + " adv=" + agendaAdvanced + " cards " + cardFrom + "->" + cardNow);
        if (from < 0 || m.IsOver || S.Settings.ReducedMotion == true) return;
        // doom gone with the cards it lay on (an enemy defeated, a treachery discarded) - not a spent agenda
        if (!agendaAdvanced && now < from && max == fromMax && removedFrom.Count > 0)
        {
            await DoomRemovals(m, before, onCards, removedFrom, from, cardFrom, max);
            return;
        }
        int to, trackMax;
        if (agendaAdvanced) { trackMax = Math.Max(1, fromMax); to = Math.Max(from, trackMax); }
        else { if (now <= from || max != fromMax) return; to = now; trackMax = max; }
        // where each new doom comes from: the cards whose doom went up (it flies off them, and stays on them), the rest from the
        // middle of the table onto the agenda
        var sources = new List<string>();
        if (!agendaAdvanced)
            foreach (var (k, n) in onCards) for (int i = before.GetValueOrDefault(k); i < n && sources.Count < Math.Max(0, cardNow - cardFrom); i++) sources.Add(k);
        int a = Math.Max(0, from - cardFrom), c = Math.Min(cardFrom, from);   // the track as it stood: on the agenda, on cards
        revealing = true;
        try
        {
            Render();
            // the copy of the track goes over the plate at once, before the phase band plays: under the band the plate already
            // showed the new doom (Yunke 11-21: the track grew before the doom had flown)
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            bool tokenFirst = m.InSkillTest && m.RevealedTokens.Any() && sources.Count > 0;
            if (!anchors.TryGetValue("agenda", out var dial) || !IsInstanceValid(dial)) return;
            // a copy of the doom track over the real one, as it stood before (the real one already shows the new state) — the
            // same size as the plate's own track, so nothing grows out of the plate (Yunke 11-20)
            var r = dial.GetGlobalRect();
            var box = new PanelContainer { MouseFilter = MouseFilterEnum.Ignore, ZIndex = 96 };
            var bsb = new StyleBoxFlat { BgColor = new Color(0.05f, 0.03f, 0.03f, 0.92f), ContentMarginLeft = 2, ContentMarginRight = 2, ContentMarginTop = 0, ContentMarginBottom = 0 }; bsb.SetCornerRadiusAll(6);
            box.AddThemeStyleboxOverride("panel", bsb);
            var row = new HBoxContainer { MouseFilter = MouseFilterEnum.Ignore }; row.AddThemeConstantOverride("separation", 5); box.AddChild(row);
            var label = Style.Text(from + " / " + trackMax, 15, Style.Danger, Style.Sans); label.VerticalAlignment = VerticalAlignment.Center;
            var pips = new List<Panel>();
            void Paint() { for (int i = 0; i < pips.Count; i++) pips[i].AddThemeStyleboxOverride("panel", DoomPip(i < a ? 1 : i < a + c ? 2 : 0)); }
            for (int i = 0; i < trackMax; i++)
            {
                var p = new Panel { CustomMinimumSize = new Vector2(13, 13), SizeFlagsVertical = SizeFlags.ShrinkCenter, MouseFilter = MouseFilterEnum.Ignore };
                row.AddChild(p); pips.Add(p);
            }
            row.AddChild(label);
            Paint();
            AddChild(box); box.ResetSize(); box.GlobalPosition = r.Position - new Vector2(2, 0);
            // the real track is hidden while its copy stands: a new agenda's track is longer or shorter than the old one, and
            // "毁灭已满 · 密谋推进" widens the copy, so the two showed side by side (Yunke 11-22: two doom tracks at once)
            var dialNode = dial; dialNode.Modulate = new Color(1, 1, 1, 0);
            await bannerDone.Task;
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            if (!IsInstanceValid(box)) return;
            // a token drawn in a test placed it (a cultist token): the token turns over first, then the doom (Yunke 11-20, 11-21: the
            // new doom already showed while the token was turning) — the track copy above shows it as it stood, and the doom
            // badges on the cards it lands on wait hidden until it lands
            if (tokenFirst)
            {
                var held = sources.Distinct().SelectMany(k => anchors.TryGetValue(k, out var an) && IsInstanceValid(an) ? DoomBadges(an) : Enumerable.Empty<Control>()).ToList();
                foreach (var hb in held) hb.Visible = false;
                if (App.I.QaRunning) GD.Print("QA_DOOM_WAIT_TOKEN held=" + held.Count);
                await ToSignal(GetTree().CreateTimer(1.0), SceneTreeTimer.SignalName.Timeout);
                foreach (var hb in held) if (IsInstanceValid(hb)) hb.Visible = true;
            }
            if (App.I.QaRunning) { GD.Print("QA_DOOM " + from + "->" + to + "/" + trackMax + (agendaAdvanced ? " advance" : "") + " fromCards=" + string.Join(",", sources)); QaMidShot(0.4 + 0.75 * Math.Max(0, to - from - 1)); }
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            var vp = GetViewportRect().Size;
            int fromCard = 0;
            for (int i = from; i < to && i < pips.Count; i++)
            {
                // a doom placed on a card leaves that card (a copy: the card keeps its own); the rest leave the middle of the table
                string? src = fromCard < sources.Count ? sources[fromCard] : null;
                bool onCard = src is not null; if (onCard) fromCard++;
                // where the doom comes from: the skill test that revealed the token (a cultist token places doom), else the
                // middle of the table
                var origin = testBox is not null && IsInstanceValid(testBox) && testBox.IsVisibleInTree() ? testBox.GetGlobalRect().GetCenter() : new Vector2(vp.X / 2, vp.Y * 0.42f);
                Control? host = src is not null && anchors.TryGetValue(src, out var h0) && IsInstanceValid(h0) ? h0 : null;
                int slot = onCard ? a + c : a;   // where it lands: after the agenda's own doom, or after the cards' doom
                var tok = Ui.Image(Ui.Stat("doom"), new Vector2(56, 56)); tok.MouseFilter = MouseFilterEnum.Ignore; tok.ZIndex = 97; tok.PivotOffset = new Vector2(28, 28);
                AddChild(tok); tok.GlobalPosition = origin - new Vector2(28, 28); tok.Modulate = new Color(1, 1, 1, 0);
                var target = pips[Math.Min(slot, pips.Count - 1)].GetGlobalRect().GetCenter() - new Vector2(28, 28);
                // doom put on a card (it lies there, only counted here): no token flies at all, the track's new ring just ripples
                // (Yunke 11-20) — the card shows its own doom badge
                if (onCard) { tok.Visible = false; host = null; }
                var tw = tok.CreateTween();
                tw.TweenProperty(tok, "modulate:a", 1f, 0.12f);
                tw.Parallel().TweenProperty(tok, "scale", new Vector2(1.15f, 1.15f), 0.12f);
                if (onCard) tw.TweenInterval(0.05);
                else if (host is not null)
                {
                    // first onto the card it is placed on (it stays there: the card's own doom badge), then up to the agenda's
                    // track, where it shows as an orange ring (Yunke 11-20)
                    var onIt = host.GetGlobalRect().GetCenter() - new Vector2(28, 28);
                    tw.TweenProperty(tok, "global_position", onIt, 0.45f).SetTrans(Tween.TransitionType.Cubic).SetEase(Tween.EaseType.InOut);
                    tw.TweenProperty(tok, "scale", new Vector2(1.35f, 1.35f), 0.12f);
                    tw.TweenProperty(tok, "scale", new Vector2(1f, 1f), 0.12f);
                    // it stays on that card (Yunke 11-20: doom not placed on the agenda does not fly up): it melts into the card's
                    // own doom badge there, and the agenda's track only lights its new orange ring
                    tw.TweenInterval(0.2);
                    tw.TweenProperty(tok, "modulate:a", 0f, 0.25f);
                }
                if (!onCard)
                {
                    tw.TweenProperty(tok, "global_position", target, 0.5f).SetTrans(Tween.TransitionType.Cubic).SetEase(Tween.EaseType.InOut);
                    tw.Parallel().TweenProperty(tok, "scale", new Vector2(0.4f, 0.4f), 0.5f).SetTrans(Tween.TransitionType.Cubic).SetEase(Tween.EaseType.In);
                }
                await ToSignal(tw, Tween.SignalName.Finished);
                tok.QueueFree();
                if (onCard) c++; else a++;
                Paint(); label.Text = (i + 1) + " / " + trackMax;
                var lp = pips[Math.Min(slot, pips.Count - 1)];
                lp.PivotOffset = new Vector2(6.5f, 6.5f); lp.Scale = new Vector2(1.3f, 1.3f);
                lp.CreateTween().TweenProperty(lp, "scale", Vector2.One, 0.3f).SetTrans(Tween.TransitionType.Back).SetEase(Tween.EaseType.Out);
                if (onCard)
                {
                    // a soft orange halo swells and fades round the new ring: a doom that counts here but lies elsewhere
                    var halo = new Panel { MouseFilter = MouseFilterEnum.Ignore, Size = new Vector2(13, 13), PivotOffset = new Vector2(6.5f, 6.5f), ShowBehindParent = true };
                    var hs = new StyleBoxFlat { BgColor = new Color(1f, 0.6f, 0.2f, 0.5f), ShadowColor = new Color(1f, 0.62f, 0.22f, 0.9f), ShadowSize = 12, AntiAliasing = true }; hs.SetCornerRadiusAll(7);
                    halo.AddThemeStyleboxOverride("panel", hs); lp.AddChild(halo);
                    var ht = halo.CreateTween().SetParallel();
                    ht.TweenProperty(halo, "scale", new Vector2(2.6f, 2.6f), 0.9f).SetTrans(Tween.TransitionType.Sine).SetEase(Tween.EaseType.Out);
                    ht.TweenProperty(halo, "modulate:a", 0f, 0.9f).SetTrans(Tween.TransitionType.Sine).SetEase(Tween.EaseType.In);
                    ht.Chain().TweenCallback(Callable.From(halo.QueueFree));
                    await ToSignal(GetTree().CreateTimer(0.5), SceneTreeTimer.SignalName.Timeout);
                }
                await ToSignal(GetTree().CreateTimer(0.18), SceneTreeTimer.SignalName.Timeout);
            }
            if (agendaAdvanced)
            {
                // the track is full: it flares red twice, and says so, before the agenda turns over
                label.Text = "毁灭已满 · 密谋推进";
                box.PivotOffset = box.Size / 2;
                var fl = box.CreateTween();
                for (int k = 0; k < 2; k++)
                {
                    fl.TweenProperty(box, "modulate", new Color(1.9f, 0.75f, 0.6f), 0.18f);
                    fl.Parallel().TweenProperty(box, "scale", new Vector2(1.12f, 1.12f), 0.18f);
                    fl.TweenProperty(box, "modulate", Colors.White, 0.26f);
                    fl.Parallel().TweenProperty(box, "scale", Vector2.One, 0.26f);
                }
                fl.TweenInterval(0.5);
                await ToSignal(fl, Tween.SignalName.Finished);
            }
            else await ToSignal(GetTree().CreateTimer(0.35), SceneTreeTimer.SignalName.Timeout);
            var fade = box.CreateTween(); fade.TweenProperty(box, "modulate:a", 0f, 0.25f);
            if (IsInstanceValid(dialNode)) fade.Parallel().TweenProperty(dialNode, "modulate:a", 1f, 0.25f);
            await ToSignal(fade, Tween.SignalName.Finished);
            box.QueueFree();
        }
        finally
        {
            revealing = false;
            // a render during the animation made a new track: whatever track stands now is shown
            if (anchors.TryGetValue("agenda", out var d2) && IsInstanceValid(d2)) d2.Modulate = Colors.White;
        }
    }

    readonly Dictionary<string, Rect2> doomRects = new();
    static IEnumerable<Control> DoomBadges(Node n)
    {
        foreach (var c in n.GetChildren())
        {
            if (c is Control cc && cc.HasMeta("doomBadge")) yield return cc;
            foreach (var d in DoomBadges(c)) yield return d;
        }
    }   // the last place each card with doom on it was seen

    // Doom that leaves with its card (Yunke 11-20): each token rises off the place the card was and goes where the card went -
    // the act plate for an enemy taken into the victory display, your discard pile for a player card, and for an encounter
    // card discarded it sinks away where it lay - while its orange ring on the agenda's track flares and fades out.
    async Task DoomRemovals(TableModel m, Dictionary<string, int> before, Dictionary<string, int> onCards, Dictionary<string, Rect2> removedFrom, int from, int cardFrom, int max)
    {
        revealing = true;
        try
        {
            Render();
            await bannerDone.Task;
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            if (!anchors.TryGetValue("agenda", out var dial) || !IsInstanceValid(dial)) return;
            var r = dial.GetGlobalRect();
            // the same copy of the plate's track as when doom is added (same size: nothing grows out of the plate)
            var box = new PanelContainer { MouseFilter = MouseFilterEnum.Ignore, ZIndex = 96 };
            var bsb = new StyleBoxFlat { BgColor = new Color(0.05f, 0.03f, 0.03f, 0.92f), ContentMarginLeft = 2, ContentMarginRight = 2, ContentMarginTop = 0, ContentMarginBottom = 0 }; bsb.SetCornerRadiusAll(6);
            box.AddThemeStyleboxOverride("panel", bsb);
            var row = new HBoxContainer { MouseFilter = MouseFilterEnum.Ignore }; row.AddThemeConstantOverride("separation", 5); box.AddChild(row);
            var label = Style.Text(from + " / " + max, 15, Style.Danger, Style.Sans); label.VerticalAlignment = VerticalAlignment.Center;
            int a = Math.Max(0, from - cardFrom), c = Math.Min(cardFrom, from);
            var pips = new List<Panel>();
            void Paint() { for (int i = 0; i < pips.Count; i++) pips[i].AddThemeStyleboxOverride("panel", DoomPip(i < a ? 1 : i < a + c ? 2 : 0)); }
            for (int i = 0; i < max; i++) { var p = new Panel { CustomMinimumSize = new Vector2(13, 13), SizeFlagsVertical = SizeFlags.ShrinkCenter, MouseFilter = MouseFilterEnum.Ignore }; row.AddChild(p); pips.Add(p); }
            row.AddChild(label);
            Paint();
            AddChild(box); box.ResetSize(); box.GlobalPosition = r.Position - new Vector2(2, 0);
            if (App.I.QaRunning) { GD.Print("QA_DOOM_GONE " + from + "->" + m.TotalDoom + " from=" + string.Join(",", removedFrom.Keys)); QaMidShot(0.5); }
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            int total = from;
            foreach (var (k, rect) in removedFrom)
            {
                int gone = before[k] - onCards.GetValueOrDefault(k);
                var start = rect.Size.X > 0 ? rect.GetCenter() : GetViewportRect().Size / 2;
                for (int j = 0; j < gone && c > 0; j++)
                {
                    var tok = Ui.Image(Ui.Stat("doom"), new Vector2(56, 56)); tok.MouseFilter = MouseFilterEnum.Ignore; tok.ZIndex = 97; tok.PivotOffset = new Vector2(28, 28);
                    AddChild(tok); tok.GlobalPosition = start - new Vector2(28, 28) + new Vector2(j * 10, 0);
                    var tw = tok.CreateTween();
                    // it dissolves where the card was (Yunke 11-21: no flight anywhere): a brief glow, then it swells a little,
                    // rises a few pixels and fades out
                    tok.Modulate = new Color(1.6f, 1.2f, 1f, 1f);
                    tw.TweenProperty(tok, "scale", new Vector2(1.2f, 1.2f), 0.12f);
                    tw.TweenProperty(tok, "modulate", new Color(1f, 1f, 1f, 0f), 0.6f).SetTrans(Tween.TransitionType.Sine).SetEase(Tween.EaseType.In);
                    tw.Parallel().TweenProperty(tok, "scale", new Vector2(1.7f, 1.7f), 0.6f).SetTrans(Tween.TransitionType.Sine).SetEase(Tween.EaseType.Out);
                    tw.Parallel().TweenProperty(tok, "global_position", tok.GlobalPosition + new Vector2(0, -24), 0.6f);
                    // its orange ring on the track flares and goes out
                    var lp = pips[Math.Clamp(a + c - 1, 0, pips.Count - 1)];
                    var halo = new Panel { MouseFilter = MouseFilterEnum.Ignore, Size = new Vector2(16, 16), PivotOffset = new Vector2(8, 8), ShowBehindParent = true };
                    var hs = new StyleBoxFlat { BgColor = new Color(1f, 0.6f, 0.2f, 0.5f), ShadowColor = new Color(1f, 0.62f, 0.22f, 0.9f), ShadowSize = 14, AntiAliasing = true }; hs.SetCornerRadiusAll(8);
                    halo.AddThemeStyleboxOverride("panel", hs); lp.AddChild(halo);
                    var ht = halo.CreateTween().SetParallel();
                    ht.TweenProperty(halo, "scale", new Vector2(2.6f, 2.6f), 0.8f).SetTrans(Tween.TransitionType.Sine).SetEase(Tween.EaseType.Out);
                    ht.TweenProperty(halo, "modulate:a", 0f, 0.8f).SetTrans(Tween.TransitionType.Sine).SetEase(Tween.EaseType.In);
                    ht.Chain().TweenCallback(Callable.From(halo.QueueFree));
                    await ToSignal(GetTree().CreateTimer(0.3), SceneTreeTimer.SignalName.Timeout);
                    c--; total--; Paint(); label.Text = total + " / " + max;
                    await ToSignal(tw, Tween.SignalName.Finished);
                    tok.QueueFree();
                }
            }
            await ToSignal(GetTree().CreateTimer(0.35), SceneTreeTimer.SignalName.Timeout);
            var fade = box.CreateTween(); fade.TweenProperty(box, "modulate:a", 0f, 0.25f);
            await ToSignal(fade, Tween.SignalName.Finished);
            box.QueueFree();
        }
        finally { revealing = false; }
    }

    // ───────── weaknesses drawn
    HashSet<string>? knownWeakIds;   // threat-area treacheries and enemies that are weaknesses, by id
    int lastWeakDiscard = -1;
    // weaknesses already turned over while resolving or as they went to hand, by code, with the step they were shown on: the
    // same card reaching the discard pile a step later is not turned over a second time
    readonly Dictionary<string, long> weakShownAt = new();

    async Task CheckWeaknessDraws()
    {
        var m = model; if (m?.Me is null) return;
        // every investigator's (Yunke 11-20: 佐伊's 时间恐惧症 came out with no animation while another seat was in view)
        var inPlay = m.Investigators.SelectMany(i => m.ThreatsOf(i)).Where(t => IsWeakness(S, t.Code)).Select(t => (t.Id, t.Code, Threat: true))
            .Concat(m.Enemies.Where(e => IsWeakness(S, e.Code)).Select(e => (e.Id, e.Code, Threat: false))).ToList();
        // also (Yunke 11-22: 马克's 炮弹休克 came out with no animation, "确认一下这一项修改是否已经应用到了所有的弱点牌里"):
        // a weakness treachery still resolving (placement Limbo: its revelation asks something, 炮弹休克's "分配 1 点恐惧",
        // before it is discarded), and a weakness drawn into a hand (an asset / event / skill weakness: 塔罗 XVI …)
        var resolving = m.Treacheries.Where(t => J.Tag(J.Get(t.Raw, "placement")) == "Limbo" && IsWeakness(S, t.Code))
            .Select(t => (t.Id, t.Code, Owner: m.Investigators.FirstOrDefault(i => i.Id == J.String(t.Raw, "owner") || i.Id == J.String(t.Raw, "drawnBy")))).ToList();
        var inHand = m.Investigators.SelectMany(i => m.Hand(i).Where(h => IsWeakness(S, h.Code)).Select(h => (h.Id, h.Code, Owner: (Entity?)i))).ToList();
        var discardWeak = m.Investigators.SelectMany(i => m.Discard(i)).Where(c => IsWeakness(S, c)).ToList();
        bool first = knownWeakIds is null;
        var known = knownWeakIds ?? new();
        var fresh = inPlay.Where(x => !known.Contains(x.Id)).ToList();
        var freshOther = resolving.Concat(inHand).Where(x => !known.Contains("w:" + x.Id)).ToList();
        // a weakness that resolved at once (its revelation, then discarded) only shows as a new weakness in the discard pile —
        // but so does one discarded straight from the deck without being drawn (Yunke 11-22: 人文学大楼 discarded 黛西's 晕血 from
        // her deck and the table said 马克 drew it): only one the log says was drawn is turned over, as its owner's draw
        var discardedAll = m.Investigators.SelectMany(i => m.Discard(i).Where(c => IsWeakness(S, c)).Select(c => (Code: c, Owner: i))).ToList();
        var discarded = lastWeakDiscard >= 0 && discardWeak.Count > lastWeakDiscard ? discardWeak.Skip(lastWeakDiscard).ToList() : new List<string>();
        knownWeakIds = inPlay.Select(x => x.Id).Concat(resolving.Concat(inHand).Select(x => "w:" + x.Id)).ToHashSet(); lastWeakDiscard = discardWeak.Count;
        long seqNow = state?.Sequence ?? 0;
        // a card turned over a few steps ago (resolving, then discarded) is not turned over again
        discarded = discarded.Where(c => !(weakShownAt.TryGetValue(CoreCatalog.Normalize(c), out var at) && seqNow - at <= 12 && seqNow >= at)).ToList();
        if (first || fresh.Count + freshOther.Count + discarded.Count == 0 || fresh.Count + freshOther.Count + discarded.Count > 4) return;
        // the opening hand: a weakness drawn now is set aside and replaced (rules: setup) — no draw animation (Yunke 11-20 #8)
        if (InMulligan(m) || m.State.Round == 0) return;
        revealing = true;
        try
        {
            Render();
            await bannerDone.Task;
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            if (App.I.QaRunning) GD.Print("QA_WEAKNESS " + string.Join(",", fresh.Select(f => f.Code).Concat(freshOther.Select(f => f.Code + "*")).Concat(discarded)));
            foreach (var (id, code, owner) in freshOther)
            {
                if (owner is not null) lastDrawer = m.Name(owner);
                await RevealCard(CoreCatalog.Normalize(code), weakness: true);
                lastDrawer = null;
                weakShownAt[CoreCatalog.Normalize(code)] = seqNow;
            }
            foreach (var (id, code, threat) in fresh)
            {
                // whose weakness it is: the investigator whose threat area it lies in
                if (threat && m.Investigators.FirstOrDefault(i => m.ThreatsOf(i).Any(t => t.Id == id)) is { } owner) lastDrawer = m.Name(owner);
                // already turned over while it resolved a step or two ago: it only flies to its place now
                bool seen = weakShownAt.TryGetValue(CoreCatalog.Normalize(code), out var at) && seqNow - at <= 12 && seqNow >= at;
                if (!seen) await RevealCard(CoreCatalog.Normalize(code), weakness: true);
                lastDrawer = null;
                if (threat) await FlyToThreat(id, CoreCatalog.Normalize(code));
            }
            foreach (var code in discarded)
            {
                if (!await DrewRecently(code)) { if (App.I.QaRunning) GD.Print("QA_WEAKNESS_MILLED " + code); continue; }   // discarded unseen
                if (discardedAll.FirstOrDefault(d => CoreCatalog.Normalize(d.Code) == CoreCatalog.Normalize(code)).Owner is { } owner) lastDrawer = m.Name(owner);
                await RevealCard(code, weakness: true);
                lastDrawer = null;
            }
        }
        finally { revealing = false; }
    }

    // Was this card drawn in the latest log rows (an entry that says 抽取 / 抽到 and names it)? A card milled from a deck is not.
    async Task<bool> DrewRecently(string code)
    {
        try
        {
            code = CoreCatalog.Normalize(code);
            var h = await B.Logs(state!);
            var renderer = new ClientLogText(S.Locale, r => r.CardCode is string c ? CardReader.ShownName(c) : null);
            bool found = false;
            void Refs(ClientLogPart p, int depth)
            {
                if (depth > 8 || found) return;
                if (p.Reference is { } r && r.CardCode is string c && CoreCatalog.Normalize(c) == code) found = true;
                if (p.Items is not null) foreach (var i in p.Items) Refs(i, depth + 1);
                if (p.Variables is not null) foreach (var v in p.Variables.Values) Refs(v, depth + 1);
            }
            bool Scan(ClientLogEntry e)
            {
                string body = renderer.Body(e);
                if (body.Contains("抽取") || body.Contains("抽到") || body.Contains("抽了")) { found = false; foreach (var p in e.Body) Refs(p, 0); if (found) return true; }
                return e.Children.Any(Scan);
            }
            return h.Rows.TakeLast(12).Any(row => row.Entry is ClientLogEntry entry && Scan(entry));
        }
        catch { return true; }   // the log could not be read: keep the old behaviour
    }

    // the drawn weakness goes from the middle of the table to its place in the threat area
    async Task FlyToThreat(string id, string code)
    {
        if (S.Settings.ReducedMotion == true || !anchors.TryGetValue("tr:" + id, out var target) || !IsInstanceValid(target)) return;
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        var to = target.GetGlobalRect(); if (to.Size.X <= 0) return;
        target.Modulate = new Color(1, 1, 1, 0);
        var vp = GetViewportRect().Size; var size = new Vector2(300, 420);
        var cv = new CardView { Code = code, Redrawn = false, CustomMinimumSize = size, Size = size, ShowCost = false, HoverZoom = false, MouseFilter = MouseFilterEnum.Ignore, ZIndex = 140, Rim = new Color("8a5aa8") };
        AddChild(cv); cv.GlobalPosition = (vp - size) / 2;
        var tw = cv.CreateTween().SetParallel();
        tw.TweenProperty(cv, "global_position", to.Position, 0.55f).SetTrans(Tween.TransitionType.Cubic).SetEase(Tween.EaseType.InOut);
        tw.TweenProperty(cv, "scale", new Vector2(to.Size.X / size.X, to.Size.Y / size.Y), 0.55f).SetTrans(Tween.TransitionType.Cubic).SetEase(Tween.EaseType.InOut);
        await ToSignal(tw, Tween.SignalName.Finished);
        cv.QueueFree();
        if (IsInstanceValid(target)) target.Modulate = Colors.White;
    }
}
