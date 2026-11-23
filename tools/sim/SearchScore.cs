using Gathering;
using System.Text.Json;

namespace ArkhamUI;

// How good a position is, roughly in campaign points (docs/AI-PLAY-PLAN-20261119.md §18): the end of a rollout (the next
// round's first turn decision, or the chapter's end) is judged by this; only differences between candidates matter.
//  - each investigator: health and sanity left (a point near defeat costs much more than one far from it), defeat, lost
//  - clues held, the act advanced, cards in the victory display (午夜假面: a cultist interrogated is +1 and not escaped +2)
//  - doom on the agenda and agendas advanced (time), enemies engaged / in play, damage on the act's target enemy
public static class SearchScore
{
    public static double Score(TableModel m, Guid _)
    {
        double v = 0;
        foreach (var i in m.Investigators)
        {
            bool lost = J.Get(i.Raw, "killed").ValueKind == JsonValueKind.True || J.Get(i.Raw, "drivenInsane").ValueKind == JsonValueKind.True;
            bool down = J.Get(i.Raw, "defeated").ValueKind == JsonValueKind.True || J.Get(i.Raw, "resigned").ValueKind == JsonValueKind.True;
            if (lost) { v -= 14; continue; }
            int hp = m.Health(i) - m.Damage(i), san = m.Sanity(i) - m.Horror(i);
            if (down && J.Get(i.Raw, "resigned").ValueKind != JsonValueKind.True) v -= 6;
            else v -= 4 * Math.Exp(-Math.Max(0, hp) / 2.0) + 4 * Math.Exp(-Math.Max(0, san) / 2.0);
            v += 0.5 * m.Clues(i);
            v -= 0.8 * m.EngagedWith(i).Count(e => !AiContext.Exhausted(e));
        }
        string act = m.Act?.Code ?? "", agenda = m.Agenda?.Code ?? "";
        // acts in a scenario have consecutive codes; an act is worth clearly more than the clues it costs (held clues count
        // 0.5 each + up to 2 toward the act's cost: at 3 per act, advancing 灼热屏障 scored −0.5 and the search sat on the
        // clues for rounds until the agenda ran out, §18c)
        v += 6 * Index(act);
        v -= 2 * Index(agenda) + 0.4 * m.TotalDoom;
        var victory = m.VictoryDisplay.ToList();
        v += victory.Sum(c => AiKnow.Traits(c).Contains("异教徒") && AiKnow.Unique(c) ? 3.0 : 1.0);
        v -= 0.3 * m.Enemies.Count();
        v += Goal(m);
        // a chapter played to its end (a resolution) rather than everyone falling
        if (m.IsOver && m.Investigators.Any(i => J.Get(i.Raw, "defeated").ValueKind != JsonValueKind.True)) v += 2;
        return v;
    }

    // Progress toward what the current act asks for, which a one-round rollout rarely sees paid off (§18b):
    //  - clues toward the act's clue cost (its share of the +3 the act is worth; more than the flat +0.5 a clue gets)
    //  - damage on the act's target enemy (the Ghoul Priest: its share of the act +3 and the "priest alive" −2)
    //  - 午夜假面 揭露阴谋: clues buy cultists (2 per investigator); a wanted cultist drawn and in play (worth more than the clues
    //    spent drawing it, or the search would never draw: v1 scored a draw −1.5), and the damage on it
    //  - 深入黑暗: steps left to the ritual site
    public static double Goal(TableModel m)
    {
        if (m.IsOver || m.Act is null) return 0;
        var g = AiGoal.Read(m);
        int players = Math.Max(1, m.Investigators.Count()), team = m.Investigators.Sum(i => m.Clues(i));
        double v = 0;
        if (g.CluesNeeded > 0) v += 2.0 * Math.Min(team, g.CluesNeeded) / g.CluesNeeded;
        if (g.Act == "01110")
            foreach (var e in m.Enemies.Where(e => e.Code.StartsWith("01116")))
                v += 5.0 * m.Damage(e) / Math.Max(1, m.EnemyHealth(e));
        if (g.Act == "01123")
        {
            v += 0.6 * Math.Min(team, 2 * players);   // toward the next draw (on top of the +0.5 each)
            foreach (var e in m.Enemies.Where(e => AiKnow.WantedCultist(e.Code) && J.Tag(J.Get(e.Raw, "placement")) is "AtLocation" or "InThreatArea"))
                v += 2.8 + 1.5 * m.Damage(e) / Math.Max(1, m.EnemyHealth(e));   // more than the 2 clues it cost (2 × (0.5 + 0.6))
        }
        if (g.GoTo is not null)
        {
            int best = m.Investigators.Select(i => AiContext.Distances(m, m.LocationOf(i)).TryGetValue(g.GoTo, out int d) ? d : 6).DefaultIfEmpty(6).Min();
            v -= 0.6 * best;
        }
        return v;
    }

    // an act / agenda code's place in the scenario (the number of its code: higher = further on)
    static int Index(string code) => code.Length >= 5 && int.TryParse(code[..5], out int n) ? n % 1000 : 0;
}
