using System.Text.RegularExpressions;
using Godot;
using Gathering;

namespace ArkhamUI;

// Questions about an enemy (Yunke 11-20):
//  · which investigator an enemy engages ("EnemyEngageInvestigator_"), or where a hunter goes ("EnemyMove_", "HunterMove_") —
//    the question says so in words ("选择 X 与谁交战" / "选择 X 移动到哪里") instead of "选择下一步", and that enemy shakes
//    on the table now and then until it is answered; its move is then the map's own slide (TableScreen.Moves);
//  · damage put on an enemy outside a fight (看门狗 biting back, an event…): a damage token flies onto it.
public partial class TableScreen
{
    // (enemy id, what is asked about it) for the open question, or null
    (string Enemy, string Ask)? EnemyAsk(TableModel m)
    {
        string raw = m.State.Question.ValueKind == System.Text.Json.JsonValueKind.Object ? m.State.Question.GetRawText() : "";
        if (raw.Length == 0) return null;
        foreach (var (tag, ask) in new[] { ("EnemyEngageInvestigator_", "engage"), ("EnemyMove_", "move"), ("HunterMove_", "move") })
        {
            var mt = Regex.Match(raw, "\"contents\":\\s*\\[?\\s*\"([0-9a-f-]{36})\"[^\\]}]*\\]?\\s*,\\s*\"tag\":\\s*\"" + tag + "\"");
            if (!mt.Success) mt = Regex.Match(raw, "\"tag\":\\s*\"" + tag + "\"\\s*,\\s*\"contents\":\\s*\\[?\\s*\"([0-9a-f-]{36})\"");
            if (mt.Success && m.Enemies.Any(e => e.Id == mt.Groups[1].Value)) return (mt.Groups[1].Value, ask);
        }
        return null;
    }

    string? EnemyAskTitle(TableModel m)
    {
        if (EnemyAsk(m) is not { } a) return null;
        var en = m.Enemies.FirstOrDefault(e => e.Id == a.Enemy); if (en is null) return null;
        return a.Ask == "engage" ? "选择" + m.Name(en) + "与谁交战" : "选择" + m.Name(en) + "移动到哪里";
    }

    // after each render: the enemy asked about shakes a little every couple of seconds
    void ShakeAskedEnemy()
    {
        var m = model; if (m is null || S.Settings.ReducedMotion == true) return;
        if (EnemyAsk(m) is not { } a || !anchors.TryGetValue("enemy:" + a.Enemy, out var node) || !IsInstanceValid(node)) return;
        if (App.I.QaRunning) GD.Print("QA_ENEMY_ASK " + a.Ask + " " + a.Enemy);
        KeepPivot(node, node.Size / 2);
        var tw = node.CreateTween().SetLoops();
        tw.TweenInterval(1.4);
        foreach (var deg in new[] { -8f, 7f, -6f, 4f, -2f, 0f }) tw.TweenProperty(node, "rotation_degrees", deg, 0.06f).SetTrans(Tween.TransitionType.Sine);
    }

    // damage that lands on an enemy outside a fight: a damage token flies onto it (the threat area's card, or its card on the map)
    Dictionary<string, int> enemyDamageWas = new();
    void FlyEnemyDamage()
    {
        var m = model; if (m is null) return;
        var now = m.Enemies.ToDictionary(e => e.Id, e => m.Damage(e));
        var before = enemyDamageWas; enemyDamageWas = now;
        if (before.Count == 0 || S.Settings.ReducedMotion == true) return;
        foreach (var (id, dmg) in now)
        {
            if (!before.TryGetValue(id, out var was) || dmg <= was) continue;
            if (!anchors.TryGetValue("enemy:" + id, out var node) || !IsInstanceValid(node)) continue;
            // from the investigator in view (whose card or ally did it), else the middle of the table
            var vp = GetViewportRect().Size;
            Vector2 from = anchors.TryGetValue("inv:" + (m.Me?.Id ?? ""), out var me) && IsInstanceValid(me) ? me.GetGlobalRect().GetCenter() : new Vector2(vp.X / 2, vp.Y * 0.6f);
            if (m.Me is not null && from == new Vector2(vp.X / 2, vp.Y * 0.6f)) from = new Vector2(SideMargin + SideW / 2, vp.Y - PlayerH / 2);
            int n = Math.Min(dmg - was, 4);
            if (App.I.QaRunning) GD.Print("QA_ENEMY_DAMAGE_FLY " + id + " +" + n);
            Callable.From(() =>
            {
                if (!IsInstanceValid(node)) return;
                var to = node.GetGlobalRect().GetCenter();
                for (int k = 0; k < n; k++)
                {
                    var tok = Ui.Image(Ui.Stat("health"), new Vector2(46, 46)); tok.MouseFilter = MouseFilterEnum.Ignore; tok.ZIndex = 120; tok.PivotOffset = new Vector2(23, 23);
                    AddChild(tok); tok.GlobalPosition = from - new Vector2(23, 23); tok.Modulate = new Color(1, 1, 1, 0);
                    var tw = tok.CreateTween();
                    tw.TweenInterval(k * 0.12);
                    tw.TweenProperty(tok, "modulate:a", 1f, 0.1f);
                    tw.TweenProperty(tok, "global_position", to - new Vector2(23, 23), 0.45f).SetTrans(Tween.TransitionType.Cubic).SetEase(Tween.EaseType.InOut);
                    tw.Parallel().TweenProperty(tok, "scale", new Vector2(0.6f, 0.6f), 0.45f);
                    tw.TweenProperty(tok, "modulate:a", 0f, 0.15f);
                    tw.TweenCallback(Callable.From(tok.QueueFree));
                }
            }).CallDeferred();
        }
    }
}
