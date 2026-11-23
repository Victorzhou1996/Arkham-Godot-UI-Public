using System.Text.RegularExpressions;
using Godot;
using Gathering;

namespace ArkhamUI;

// Cards that stop a basic step (Yunke 11-22): 刺耳之声 "你不能打出支援或事件", 不洁之地 "你不能打出[[盟友]]支援", 上锁的门 "附属的地点不能
// 被调查", 呆若木鸡 / 棱镜光华 / 屋中无人 "须支付额外1个行动"… When one of them bites — a hand card that cannot be played because of
// it, a click on something it forbids, or the extra action it charges — that card shakes and sends out red rings, and its own
// (official) line is shown, so the player sees at once what is holding them back.
// Only cards that lie on the investigator (threat area, attached to them) or on their location are read; their lasting lines
// only (a 显现 / 强制 line happens once and is not a standing ban).
public partial class TableScreen
{
    enum Bite { Play, Move, Investigate, Fight, Evade, Extra }

    static readonly Color BiteRed = new("ff4a3a");

    static readonly Regex BanPlay = new(@"不能打出"), BanMove = new(@"不能移动"), BanInvestigate = new(@"不能(被)?调查"),
        BanFight = new(@"不能(进行)?攻击"), BanEvade = new(@"不能(进行)?(躲避|闪避)"), Extra = new(@"(须支付|必须花费)额外\s*\d*\s*个?行动");

    // the standing lines of the cards that hold this investigator back
    IEnumerable<(Entity Card, string Line)> Holds(TableModel m, Entity inv)
    {
        var on = m.ThreatsOf(inv).ToList();
        if (m.LocationOf(inv) is string loc) on.AddRange(m.Attached("AttachedToLocation", loc));
        foreach (var e in on.DistinctBy(e => e.Id))
            foreach (var raw in J.String(S.Card(e.Code), "text").Split('\n'))
            {
                var line = Regex.Replace(raw, "<[^>]+>", "").Trim();
                if (line.Length == 0 || line.StartsWith("显现") || line.StartsWith("强制") || line.StartsWith("[")) continue;
                yield return (e, line);
            }
    }

    // the cards that stop this kind of step; for a hand card, only a ban that names its type or trait (or names none)
    List<(Entity Card, string Line)> Biters(TableModel m, Entity inv, Bite kind, string handCode = "")
    {
        var res = new List<(Entity, string)>();
        foreach (var (card, line) in Holds(m, inv))
        {
            bool hit = kind switch
            {
                Bite.Play => BanPlay.IsMatch(line) && PlayBanFits(line, handCode),
                Bite.Move => BanMove.IsMatch(line),
                Bite.Investigate => BanInvestigate.IsMatch(line),
                Bite.Fight => BanFight.IsMatch(line),
                Bite.Evade => BanEvade.IsMatch(line),
                Bite.Extra => Extra.IsMatch(line),
                _ => false
            };
            if (hit) res.Add((card, line));
        }
        return res;
    }

    bool PlayBanFits(string line, string code)
    {
        if (code.Length == 0) return true;
        var c = S.Card(code); string type = J.String(c, "type_code"), traits = J.String(c, "traits");
        var kinds = new List<string>();
        if (line.Contains("支援")) kinds.Add("asset");
        if (line.Contains("事件")) kinds.Add("event");
        if (line.Contains("技能")) kinds.Add("skill");
        if (kinds.Count > 0 && !kinds.Contains(type)) return false;
        var named = Regex.Matches(line, @"\[\[([^\]]+)\]\]").Select(x => x.Groups[1].Value).ToList();
        return named.Count == 0 || named.Any(t => traits.Contains(t));
    }

    // the card bites: it shakes, red rings go out from it, and its line is told
    void BiteNow(TableModel m, Entity card, string line, int index = 0)
    {
        string key = m.ThreatKey(card);
        if (App.I.QaRunning) GD.Print("QA_BITE " + card.Code + " " + key + " anchor=" + anchors.ContainsKey(key) + " " + line);
        Float(m.Name(card) + "：" + line, BiteRed, index);
        if (!anchors.TryGetValue(key, out var node) || !IsInstanceValid(node) || !node.IsVisibleInTree()) return;
        Ripple(node, anchorFrames.TryGetValue(key, out var fr) ? fr : new Rect2(Vector2.Zero, node.Size), 0f, 10, BiteRed);
        if (S.Settings.ReducedMotion == true) return;
        // shake by turning about its centre (a position shake would fight the row container that places it)
        BeginShake(node); KeepPivot(node, node.Size / 2); float r0 = node.Rotation;
        var tw = node.CreateTween();
        foreach (var d in new[] { -6f, 6f, -5f, 5f, -3f, 3f, 0f }) tw.TweenProperty(node, "rotation", r0 + Mathf.DegToRad(d), 0.05f);
        tw.TweenCallback(Callable.From(() => EndShake(node)));
        var mod = node.Modulate; node.Modulate = new Color(1.5f, 0.75f, 0.7f, mod.A);
        node.CreateTween().TweenProperty(node, "modulate", mod, 0.7f);
    }

    // a hand card that cannot be played: is a card in play the reason? (TableScreen.Render · Deny)
    bool BitePlay(TableModel m, Entity me, string code)
    {
        // out of this investigator's own turn an ordinary card is held back by the timing anyway: only a fast card blames the ban
        if (J.String(m.Game, "turnPlayerInvestigatorId") != me.Id && !IsFastPlay(code)) return false;
        var who = Biters(m, me, Bite.Play, code);
        Callable.From(() => { for (int i = 0; i < who.Count; i++) BiteNow(m, who[i].Card, who[i].Line, i); }).CallDeferred();
        return who.Count > 0;
    }

    // a click on a place or an enemy that offers nothing, on this investigator's own turn: is a card in play the reason?
    bool BiteClick(string key)
    {
        var m = model; var me = m?.Me; if (m is null || me is null) return false;
        if (J.String(m.Game, "turnPlayerInvestigatorId") != me.Id) return false;
        List<(Entity Card, string Line)> who;
        if (key.StartsWith("loc:")) who = Biters(m, me, key[4..] == m.LocationOf(me) ? Bite.Investigate : Bite.Move);
        else if (key.StartsWith("enemy:")) who = Biters(m, me, Bite.Fight).Concat(Biters(m, me, Bite.Evade)).DistinctBy(w => w.Card.Id).ToList();
        else return false;
        for (int i = 0; i < who.Count; i++) BiteNow(m, who[i].Card, who[i].Line, i);
        return who.Count > 0;
    }

    // after a render: an action that cost one more than it should (呆若木鸡's first move / fight / evade each round)
    (string Id, int Round, int Actions)? lastActions;
    void BitesAfterRender()
    {
        var m = model; var me = m?.Me; if (m is null || me is null) return;
        int round = m.State.Round, acts = m.Actions(me);
        var prev = lastActions; lastActions = (me.Id, round, acts);
        if (prev is not { } p || p.Id != me.Id || p.Round != round || p.Actions - acts < 2) return;
        var who = Biters(m, me, Bite.Extra);
        if (who.Count == 0) return;
        GetTree().CreateTimer(0.3).Timeout += () =>
        {
            if (!IsInstanceValid(this) || model is not { } mm) return;
            for (int i = 0; i < who.Count; i++) BiteNow(mm, who[i].Card, who[i].Line, i);
        };
    }
}
