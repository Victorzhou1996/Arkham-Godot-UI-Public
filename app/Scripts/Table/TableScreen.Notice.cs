using System.Text.RegularExpressions;
using Godot;
using Gathering;

namespace ArkhamUI;

// Yunke 11-19 r10:
//  · a weakness whose forced ability the core settles by itself (Rex's Curse re-reveals the tokens) still shows: its card shakes
//    and flushes red where it lies, with the usual "已自动结算" notice naming it;
//  · damage and horror assigned one point at a time: every point says where it went and what is left, floating up from the card
//    that took it (the assigning patch and its cards are red, see ChoicesOnTable).
public partial class TableScreen
{
    long lastForcedSeq = -1;
    string recentNotice = ""; ulong recentNoticeAt;

    static readonly Regex AssignRe = new(@"^给(.+?)分配(伤害|恐惧)");
    static readonly Color AssignRed = new("e0503c");
    // the cards on the table offered to take a point of damage / horror glow red instead of amber
    Color? AssignGlow(string key) => byAnchor.TryGetValue(key, out var l) && l.Any(c => AssignRe.IsMatch(c.Title)) ? AssignRed : null;

    async Task CheckForcedWeaknesses()
    {
        var st = state; var m = model; if (st is null || m is null) return;
        long since = lastForcedSeq; lastForcedSeq = st.Sequence;
        if (since < 0 || since >= st.Sequence) return;
        (string Code, string Id)[] found;
        try { found = await B.ForcedSources(st, since); }
        catch (Exception e) { GD.PushWarning("forced sources: " + e.Message); return; }
        if (App.I.QaRunning) GD.Print("QA_FORCED_SCAN " + since + "->" + st.Sequence + " " + string.Join(",", found.Select(f => f.Code + "@" + f.Id + (OnTable(f.Id) ? "+" : "-"))));
        // Yunke 11-19: every forced ability shakes its card — a weakness wherever it is (a copy over the table if it is not on
        // it), any other card (a location, an enemy, an asset, a card in a threat area) when it is on the table
        var weak = found.Where(f => IsWeakness(S, f.Code) || OnTable(f.Id)).GroupBy(f => f.Id.Length > 0 ? f.Id : f.Code).Select(g => g.First()).Take(3).ToList();
        if (weak.Count == 0) return;
        revealing = true;
        try
        {
            Render();
            await bannerDone.Task;
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            if (App.I.QaRunning) { GD.Print("QA_FORCED " + string.Join(",", weak.Select(w => w.Code))); QaMidShot(0.25); }
            foreach (var (code, id) in weak) await ShakeWeakness(CoreCatalog.Normalize(code), id);
        }
        finally { revealing = false; }
    }

    // A test failed automatically on a face that is not the auto-fail token, because a weakness in play says so (The
    // Necronomicon turns the elder sign into an auto-fail): that weakness shakes and names itself, like a forced one
    // (Yunke 11-19). Once per test.
    string lastConstantTest = "";
    void CheckConstantWeakness()
    {
        var m = model; if (m is null || !m.InSkillTest) return;
        var st = m.SkillTest; var res = J.Get(st, "result");
        if (J.Tag(res) != "FailedBy") return;
        var c = J.Get(res, "contents");
        if (!(c.ValueKind == System.Text.Json.JsonValueKind.Array && c.GetArrayLength() > 0 && c[0].GetString() == "Automatic")) return;
        string testId = J.String(st, "id") + "|" + J.Items(J.Get(st, "resolvedChaosTokens")).Count();
        if (testId == lastConstantTest) return;
        var faces = J.Items(J.Get(st, "resolvedChaosTokens")).Select(t => J.String(t, "chaosTokenFace")).ToList();
        if (faces.Contains("AutoFail")) return;
        var tester = m.Investigators.FirstOrDefault(i => i.Id == J.String(st, "investigator")); if (tester is null) return;
        foreach (var f in faces)
            if (AutoFailSource(m, tester, f) is { } src)
            {
                lastConstantTest = testId;
                if (App.I.QaRunning) GD.Print("QA_CONSTANT_WEAKNESS " + src.Code + " face=" + f);
                _ = ShakeWeakness(CoreCatalog.Normalize(src.Code), src.Id);
                return;
            }
    }

    static IEnumerable<string> TableKeys(string id) => new[] { "tr:" + id, "enemy:" + id, "asset:" + id, "loc:" + id };
    bool OnTable(string id) => id.Length > 0 && TableKeys(id).Any(k => anchors.TryGetValue(k, out var a) && IsInstanceValid(a) && a.IsVisibleInTree());

    async Task ShakeWeakness(string code, string id)
    {
        // the core may have offered it as a lone forced step that was answered by itself, with its notice already up
        string nm = S.Name(code);
        if (!(Time.GetTicksMsec() - recentNoticeAt < 5000 && recentNotice.Contains(nm))) ForcedNotice("强制效果 · " + nm);
        Control? target = null;
        foreach (var key in TableKeys(id).Append("hand:" + id))
            if (anchors.TryGetValue(key, out var a) && IsInstanceValid(a) && a.IsVisibleInTree() && a.Size.X > 0) { target = a; break; }
        bool temp = target is null;
        if (temp)
        {
            // not on the table (it went back into the deck, or was discarded): a copy of the card shakes over the table instead
            var size = new Vector2(200, 280);
            var cv = new CardView { Code = code, Redrawn = false, CustomMinimumSize = size, Size = size, ShowCost = false, HoverZoom = false, MouseFilter = MouseFilterEnum.Ignore, ZIndex = 125, Rim = new Color("8a5aa8") };
            AddChild(cv); cv.GlobalPosition = new Vector2((GetViewportRect().Size.X - size.X) / 2, GetViewportRect().Size.Y * 0.3f);
            cv.Modulate = new Color(1, 1, 1, 0);
            var fin = cv.CreateTween(); fin.TweenProperty(cv, "modulate:a", 1f, 0.15f);
            await ToSignal(fin, Tween.SignalName.Finished);
            target = cv;
        }
        if (S.Settings.ReducedMotion == true) { await ToSignal(GetTree().CreateTimer(0.6), SceneTreeTimer.SignalName.Timeout); }
        else
        {
            var t = target!;
            BeginShake(t);
            float r0 = t.Rotation; var s0 = t.Scale; var mod0 = t.Modulate; int z0 = t.ZIndex;
            KeepPivot(t, t.Size / 2); t.ZIndex = Math.Max(z0, 20);
            var tw = t.CreateTween();
            tw.TweenProperty(t, "scale", s0 * 1.14f, 0.1f).SetTrans(Tween.TransitionType.Back).SetEase(Tween.EaseType.Out);
            tw.Parallel().TweenProperty(t, "modulate", new Color(1.6f, 0.75f, 0.7f, mod0.A), 0.1f);
            for (int k = 0; k < 6; k++)
                tw.TweenProperty(t, "rotation", r0 + (k % 2 == 0 ? 1 : -1) * Mathf.DegToRad(7 - k), 0.055f).SetTrans(Tween.TransitionType.Sine);
            tw.TweenProperty(t, "rotation", r0, 0.06f);
            tw.TweenProperty(t, "scale", s0, 0.22f).SetTrans(Tween.TransitionType.Cubic).SetEase(Tween.EaseType.Out);
            tw.Parallel().TweenProperty(t, "modulate", mod0, 0.3f);
            await ToSignal(tw, Tween.SignalName.Finished);
            EndShake(t);
            if (IsInstanceValid(t)) t.ZIndex = z0;
            await ToSignal(GetTree().CreateTimer(0.25), SceneTreeTimer.SignalName.Timeout);
        }
        if (temp && IsInstanceValid(target))
        {
            var fo = target!.CreateTween(); fo.TweenProperty(target, "modulate:a", 0f, 0.25f);
            await ToSignal(fo, Tween.SignalName.Finished);
            target.QueueFree();
        }
    }

    // ───────── one point of damage / horror assigned
    int hintStack; ulong hintStackAt;
    Control? assignFrom;   // the card in the assigning box that was clicked (the box is not anchored)

    void AssignHint(ChoicePresentation c, Control? at)
    {
        var mt = AssignRe.Match(c.Title); if (!mt.Success) return;
        string who = mt.Groups[1].Value; bool dmg = mt.Groups[2].Value == "伤害";
        // what is left of each kind once this point is placed (the core's titles carry "待分配N点" per kind)
        int Left(string kind) => model?.Question.Choices.Where(x => x.Enabled && x.Title.Contains("分配" + kind)).Select(x => Regex.Match(x.Title, @"待分配(\d+)点")).Where(x => x.Success).Select(x => int.Parse(x.Groups[1].Value)).DefaultIfEmpty(0).Max() ?? 0;
        int amt = AssignAmount(c);   // one click places all that is left when only one card can take it (TableScreen.Assign)
        int d = Left("伤害") - (dmg ? amt : 0), h = Left("恐惧") - (dmg ? 0 : amt);
        d = Math.Max(0, d); h = Math.Max(0, h);
        string more = d + h == 0 ? "　分配完毕" : "　还要分配 " + string.Join("、", new[] { d > 0 ? d + " 点伤害" : "", h > 0 ? h + " 点恐惧" : "" }.Where(x => x.Length > 0));
        string text = who + " 承受 " + amt + " 点" + (dmg ? "伤害" : "恐惧") + more;
        if (App.I.QaRunning) GD.Print("QA_ASSIGN " + text);
        // where it floats from: the card clicked, or (assigned by itself) whatever on the table stands for the one who took it
        if (at is null || !IsInstanceValid(at))
        {
            string key = model?.Anchor(c) ?? "";
            if (key.Length > 0 && anchors.TryGetValue(key, out var a) && IsInstanceValid(a) && a.IsVisibleInTree()) at = a;
        }
        var vp = GetViewportRect().Size;
        var origin = at is not null && IsInstanceValid(at) ? at.GetGlobalRect() : new Rect2(vp.X / 2 - 60, vp.Y * 0.55f, 120, 0);
        ulong now = Time.GetTicksMsec();
        if (now - hintStackAt > 1400) hintStack = 0;
        hintStackAt = now; int lane = hintStack++ % 4;

        var row = new HBoxContainer { MouseFilter = MouseFilterEnum.Ignore }; row.AddThemeConstantOverride("separation", 6);
        var bg = new PanelContainer { MouseFilter = MouseFilterEnum.Ignore, ZIndex = 130 };
        var sb = new StyleBoxFlat { BgColor = new Color(0.06f, 0.015f, 0.01f, 0.88f), ContentMarginLeft = 14, ContentMarginRight = 16, ContentMarginTop = 6, ContentMarginBottom = 6, AntiAliasing = true };
        sb.SetCornerRadiusAll(16); sb.ShadowColor = new Color(dmg ? AssignRed : Style.Willpower, 0.45f); sb.ShadowSize = 10;
        bg.AddThemeStyleboxOverride("panel", sb); bg.AddChild(row);
        var ic = Ui.Image(Ui.Stat(dmg ? "health" : "horror"), new Vector2(28, 28)); ic.MouseFilter = MouseFilterEnum.Ignore; ic.SizeFlagsVertical = SizeFlags.ShrinkCenter; row.AddChild(ic);
        var l = Style.Text(text, 20, dmg ? new Color("ffb4a6") : new Color("c9c2ff"), Style.Kai); l.VerticalAlignment = VerticalAlignment.Center; row.AddChild(l);
        AddChild(bg); bg.ResetSize();
        // over the middle of the card that took it (above it, the assigning box's own title sits)
        var start = new Vector2(origin.GetCenter().X - bg.Size.X / 2, origin.GetCenter().Y - bg.Size.Y / 2 - lane * (bg.Size.Y + 6));
        start.X = Mathf.Clamp(start.X, 8, vp.X - bg.Size.X - 8); start.Y = Mathf.Clamp(start.Y, 60, vp.Y - bg.Size.Y - 8);
        bg.GlobalPosition = start + new Vector2(0, 14); bg.Modulate = new Color(1, 1, 1, 0);
        var tw = bg.CreateTween();
        tw.TweenProperty(bg, "modulate:a", 1f, 0.15f);
        tw.Parallel().TweenProperty(bg, "global_position", start, 0.25f).SetTrans(Tween.TransitionType.Cubic).SetEase(Tween.EaseType.Out);
        tw.TweenInterval(1.8);
        tw.TweenProperty(bg, "global_position", start - new Vector2(0, 26), 0.45f);
        tw.Parallel().TweenProperty(bg, "modulate:a", 0f, 0.45f);
        tw.TweenCallback(Callable.From(bg.QueueFree));
    }
}
