using Gathering;
using System.Text.Json;

namespace ArkhamUI;

// What the current act asks for, read from the core's act (advanceCost) plus a few scenario facts the act text states.
public sealed class AiGoal
{
    public string Act = "";
    public int CluesNeeded;                 // the act's group clue cost (0: the act is not paid with clues)
    public string? SpendAt;                 // location id where the clues must be spent (灼热屏障: the hallway), or null
    public bool SpendAtRoundEnd;            // the clues are spent at the end of the round by those standing there
    public bool CluesMatter;                // gathering clues still helps the act
    public string? GoTo;                    // a location id the act wants someone to reach (地底噬魔: the ritual site)
    public string? TargetEnemy;             // an enemy id the act wants defeated (the Ghoul Priest)
    public bool ResignWhenDone;
    public string? AllyAt;                  // the location of an ally waiting to be won over (丽塔·钱德勒 in the parlor)             // the scenario is best ended by resigning once the work is done
    public string Note = "";

    public static AiGoal Read(TableModel m)
    {
        var g = new AiGoal();
        var act = m.Act; g.Act = act is null ? "" : CoreCatalog.Normalize(act.Code);
        int players = Math.Max(1, m.Investigators.Count());
        if (act is not null)
        {
            var cost = J.Get(act.Raw, "advanceCost");
            if (J.Tag(cost) == "GroupClueCost")
            {
                var parts = J.Items(J.Get(cost, "contents")).ToArray();
                if (parts.Length > 0)
                {
                    var v = parts[0]; int n = J.Tag(v) == "PerPlayer" ? J.Int(v, "contents") * players : TableModel.Calc(v);
                    g.CluesNeeded = Math.Max(0, n);
                }
                if (parts.Length > 1 && J.Tag(parts[1]) != "Anywhere")
                {
                    string title = (J.String(parts[1], "contents") ?? "").ToLowerInvariant();
                    g.SpendAt = m.Locations.FirstOrDefault(l => title.Length > 0 && (m.Label(l).ToLowerInvariant() == title || J.String(AiHost.Card(l.Code), "real_name").ToLowerInvariant() == title))?.Id;
                }
            }
        }
        switch (g.Act)
        {
            case "01109":   // 灼热屏障: at the end of the round the investigators IN THE HALLWAY spend 3 per investigator
                g.CluesNeeded = g.CluesNeeded > 0 ? g.CluesNeeded : 3 * players;
                g.SpendAt ??= m.Locations.FirstOrDefault(l => l.Code == "01112")?.Id;
                g.SpendAtRoundEnd = true;
                break;
            case "01110":   // 瞧瞧你们做的好事: defeat the Ghoul Priest
                g.TargetEnemy = m.Enemies.FirstOrDefault(e => e.Code == "01116")?.Id;
                // Lita Chantler (+1 combat, +1 damage against monsters at her location) waits in the parlor until parleyed
                g.AllyAt = m.Assets.FirstOrDefault(a => a.Code == "01117" && J.Tag(J.Get(a.Raw, "placement")) == "AtLocation") is { } lita ? J.String(J.Get(lita.Raw, "placement"), "contents") : null;
                break;
            case "01123":   // 揭露阴谋 (午夜假面): 6 unique cultists in the victory display; each one defeated is interrogated
                            // (campaign log), each one left gets away: hunt the nearest cultist on the map
            {
                // the easiest first (card text): 彼得·沃伦 is won over for 2 clues, 维多利亚 for 5 resources, 赫尔曼 by discarding
                // 4 cards (谈判, straight to the victory display); the others by their health left and how hard they are to hit
                var seats = m.Investigators.ToArray();
                int clues = seats.Sum(i => m.Clues(i)), res = seats.Select(i => m.Resources(i)).DefaultIfEmpty(0).Max(),
                    hand = seats.Select(i => m.Hand(i).Count()).DefaultIfEmpty(0).Max(), combat = seats.Select(i => m.SkillValue(i, "combat")).DefaultIfEmpty(3).Max();
                Func<Entity, double> effort = e => CoreCatalog.Normalize(e.Code) switch
                {
                    "01139" when clues >= 2 => 0, "01140" when res >= 5 => 0, "01138" when hand >= 5 => 0.5,
                    _ => m.EnemyHealth(e) - m.Damage(e) + Math.Max(0, m.EnemyFight(e) - combat + 1) * 1.5
                };
                g.TargetEnemy = m.Enemies.Where(e => AiKnow.WantedCultist(e.Code) && J.Tag(J.Get(e.Raw, "placement")) is "AtLocation" or "InThreatArea")
                    .OrderBy(effort).FirstOrDefault()?.Id;
                break;
            }
            case "01147":   // 深入黑暗: someone enters the ritual site
                g.GoTo = m.Locations.FirstOrDefault(l => J.String(AiHost.Card(l.Code), "name") == "仪式地" || m.Label(l).Contains("ritual", StringComparison.OrdinalIgnoreCase))?.Id;
                break;
        }
        int team = m.Investigators.Sum(i => m.Clues(i));
        g.CluesMatter = g.CluesNeeded > 0 && team < g.CluesNeeded;
        // 午夜假面 (01123): clues buy cultists from the cultist deck; keep gathering
        if (g.Act == "01123") g.CluesMatter = true;
        if (g.Act == "01148") g.CluesMatter = true;   // 破坏仪式: clues pay for the act's tests
        // a scenario the AI has no plan for: clues are still the usual way forward
        if (g.CluesNeeded == 0 && g.TargetEnemy is null && g.GoTo is null && g.Act != "01110") g.CluesMatter = m.Locations.Any(l => m.Clues(l) > 0);
        g.Note = "act=" + g.Act + " need=" + g.CluesNeeded + " team=" + team + (g.SpendAt is null ? "" : " at=" + Short(m, g.SpendAt)) + (g.TargetEnemy is null ? "" : " kill=" + Short(m, g.TargetEnemy)) + (g.GoTo is null ? "" : " go=" + Short(m, g.GoTo));
        return g;
    }

    static string Short(TableModel m, string id) => m.Location(id) is { } l ? m.Name(l) : m.Enemies.FirstOrDefault(e => e.Id == id) is { } e ? m.Name(e) : id[..Math.Min(6, id.Length)];

    // clues held by the investigators standing where they must be spent
    public int CluesAtSpend(TableModel m) => SpendAt is null ? m.Investigators.Sum(i => m.Clues(i)) : m.InvestigatorsAt(SpendAt).Sum(i => m.Clues(i));
}

// The situation of the investigator who decides now (one seat), computed once per decision.
public sealed class AiContext
{
    public TableModel M = null!;
    public Entity Me = null!;
    public Entity? Here;
    public AiGoal Goal = null!;
    public int Hp, San, Actions, Resources, HandSize, CluesHere, Engaged, EngagedThreat, EnemiesOnMap, ExhaustedEnemiesHere, DoomMargin, MyClues, TeamClues, Players;
    public int Intellect, Combat, Agility, Willpower;
    public bool AffordWant;                       // a card in hand worth playing costs more than the resources held
    public string[] MyAssetCodes = Array.Empty<string>();
    public Dictionary<string, int> Dist = new();  // steps from here to each location id

    public static AiContext Build(TableModel m, Entity me, AiGoal goal)
    {
        var x = new AiContext { M = m, Me = me, Goal = goal, Here = m.Location(m.LocationOf(me)) };
        x.Hp = m.Health(me) - m.Damage(me); x.San = m.Sanity(me) - m.Horror(me);
        // campaign: a defeat adds a trauma; at the limit the investigator is killed / driven insane and lost for the campaign
        x.FatalHp = J.Int(me.Raw, "physicalTrauma") + 1 >= m.Health(me); x.FatalSan = J.Int(me.Raw, "mentalTrauma") + 1 >= m.Sanity(me);
        x.Actions = m.Actions(me); x.Resources = m.Resources(me); x.HandSize = m.Hand(me).Count();
        x.CluesHere = x.Here is null ? 0 : m.Clues(x.Here);
        var engaged = m.EngagedWith(me).ToArray();
        x.Engaged = engaged.Length; x.EngagedThreat = engaged.Where(e => !Exhausted(e)).Sum(e => Threat(e));
        x.EnemiesOnMap = m.Enemies.Count(e => J.Tag(J.Get(e.Raw, "placement")) is "AtLocation" or "InThreatArea");
        x.ExhaustedEnemiesHere = x.Here is null ? 0 : m.EnemiesHere(x.Here.Id).Count(Exhausted);
        x.DoomMargin = m.DoomThreshold > 0 ? m.DoomThreshold - m.TotalDoom : 99;
        x.MyClues = m.Clues(me); x.TeamClues = m.Investigators.Sum(i => m.Clues(i)); x.Players = Math.Max(1, m.Investigators.Count());
        x.Intellect = m.SkillValue(me, "intellect"); x.Combat = m.SkillValue(me, "combat"); x.Agility = m.SkillValue(me, "agility"); x.Willpower = m.SkillValue(me, "willpower");
        x.MyAssetCodes = m.AssetsOf(me).Select(a => CoreCatalog.Normalize(a.Code)).ToArray();
        x.Dist = Distances(m, x.Here?.Id);
        (x.CoachSan, x.CoachHp) = AiCoach.Pain(x);
        x.AffordWant = m.Hand(me).Any(h => AiKnow.Type(h.Code) == "asset" && AiKnow.Cost(h.Code) > x.Resources && AiKnow.PlayValue(h.Code, x) >= 5);
        return x;
    }

    public static bool Exhausted(Entity e) => J.Get(e.Raw, "exhausted").ValueKind == JsonValueKind.True;
    public static int Threat(Entity e) => Math.Max(1, J.Int(e.Raw, "healthDamage") + J.Int(e.Raw, "sanityDamage"));
    // where an enemy is: its own place, or the place of the investigator it is engaged with
    public string? EnemyLocation(Entity e)
    {
        var p = J.Get(e.Raw, "placement");
        if (J.Tag(p) == "AtLocation") return J.String(p, "contents");
        var inv = M.Investigators.FirstOrDefault(i => M.EngagedWith(i).Any(q => q.Id == e.Id));
        return inv is null ? null : M.LocationOf(inv);
    }
    public int EnemyLeft(Entity e) => Math.Max(1, M.EnemyHealth(e) - M.Damage(e));

    // Breadth-first steps over the core's connections (revealed or not)
    public static Dictionary<string, int> Distances(TableModel m, string? from)
    {
        var d = new Dictionary<string, int>(); if (from is null) return d;
        var locs = m.Locations.ToDictionary(l => l.Id);
        var q = new Queue<string>(); q.Enqueue(from); d[from] = 0;
        while (q.Count > 0)
        {
            var id = q.Dequeue(); if (!locs.TryGetValue(id, out var l)) continue;
            foreach (var n in J.Items(J.Get(l.Raw, "connectedLocations")).Select(x => x.GetString() ?? ""))
                if (n.Length > 0 && !d.ContainsKey(n)) { d[n] = d[id] + 1; q.Enqueue(n); }
        }
        return d;
    }

    // a point of damage / horror hurts more the closer the investigator is to defeat
    public bool FatalHp, FatalSan;          // being defeated by damage / horror would lose this investigator for the campaign
    public double CoachHp = 1, CoachSan = 1;   // the coach's multiples for this act (AiCoach, evidence/ai/model/coach.json)
    public double PainHp => Pain(Hp) * (FatalHp && Hp <= 2 ? 2.5 : 1) * CoachHp;
    public double PainSan => Pain(San) * (FatalSan && San <= 2 ? 2.5 : 1) * CoachSan;
    public static double Pain(int left) => left <= 1 ? 30 : left == 2 ? 14 : left == 3 ? 8 : left == 4 ? 5 : 3;
}
