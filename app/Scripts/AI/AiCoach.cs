using Gathering;
using System.Text.Json;

namespace ArkhamUI;

// The coach (docs/AI-PLAY-PLAN-20261119.md §19): guidance written from analysing many played campaigns
// (tools/ai/coach_report.py), kept as data in evidence/ai/model/coach.json so it can be revised without touching the
// rules. Each rule names the acts it applies to and what it changes:
//   - kind (+ enemy / card): an action kind's value is set, multiplied or shifted ("撤退" "躲避" "资源" …)
//   - kind2: the same as kind, for a rule with card / when conditions a build before 11-21 cannot read (such a build skips it)
//   - card: only for this card ("weapon" / "ally" / a card code) — the card played (打出)
//   - when: conditions joined by "+", all must hold —
//       "lethal"      the ready enemies engaged with me would defeat me with their next attacks
//       "lastAction" / "moreActions"   one action left this turn / two or more
//       "cluesReady"  the act's clue cost is met (the next spend advances it)
//       "noWeapon"    no weapon of mine in play;  "noWeaponInHand" none in my hand either;  "hasWeapon" one in play
//       "noTarget"    the act has no enemy to hunt now;  "targetFar" the enemy to hunt is 2+ moves away
//   - painSan / painHp: how much a point of horror / damage hurts, as a multiple of the usual (AiContext.PainSan / PainHp)
// The file is read again when it changes. Every rule carries its reason ("why") and the numbers behind it ("evidence").
public static class AiCoach
{
    public sealed record Rule(string Id, string Why, HashSet<string> Acts, string? Kind, string? Enemy, string? Card, string? When,
        double? Set, double Mult, double Delta, double PainSan, double PainHp);

    static List<Rule> rules = new();
    static DateTime stamp;

    public static IReadOnlyList<Rule> Rules()
    {
        try
        {
            // ARKHAM_COACH_FILE: another rule file (the sim's A/B tests; the game reads the usual one)
            var file = Environment.GetEnvironmentVariable("ARKHAM_COACH_FILE") is { Length: > 0 } alt ? alt : Path.Combine(AiHost.ModelDir, "coach.json");
            var t = File.Exists(file) ? File.GetLastWriteTimeUtc(file) : default;
            if (t != stamp)
            {
                stamp = t; rules = new();
                if (t != default)
                {
                    using var doc = JsonDocument.Parse(File.ReadAllText(file));
                    foreach (var r in J.Items(J.Get(doc.RootElement, "rules")))
                    {
                        if (r.TryGetProperty("off", out var off) && off.ValueKind == JsonValueKind.True) continue;
                        rules.Add(new Rule(J.String(r, "id") ?? "", J.String(r, "why") ?? "",
                            J.Items(J.Get(r, "acts")).Select(a => a.GetString() ?? "").ToHashSet(),
                            Str(r, "kind") ?? Str(r, "kind2"), Str(r, "enemy"), Str(r, "card"), Str(r, "when"),
                            r.TryGetProperty("set", out var s) && s.ValueKind == JsonValueKind.Number ? s.GetDouble() : null,
                            Num(r, "mult", 1), Num(r, "delta", 0), Num(r, "painSan", 1), Num(r, "painHp", 1)));
                    }
                }
            }
        }
        catch (Exception e) { AiHost.Log("AI_COACH_ERROR " + e.Message); rules = new(); }
        return rules;
    }

    static bool Applies(Rule r, AiContext x) => r.Acts.Count == 0 || r.Acts.Contains(x.Goal.Act);

    // the multiples for a point of horror / damage in the current act
    public static (double San, double Hp) Pain(AiContext x)
    {
        double s = 1, h = 1;
        foreach (var r in Rules()) if (Applies(r, x)) { s *= r.PainSan; h *= r.PainHp; }
        return (s, h);
    }

    // an option's value after the coach's rules (kind: 调查 移动 攻击 躲避 资源 抽牌 打出 撤退 谈判 结束回合 其他)
    public static double Adjust(TableModel m, AiContext x, string kind, string? enemyCode, string? cardCode, double v, List<string> used)
    {
        foreach (var r in Rules())
        {
            if (r.Kind is null || r.Kind != kind || !Applies(r, x)) continue;
            if (r.Enemy is not null && (enemyCode is null || !enemyCode.StartsWith(r.Enemy))) continue;
            if (r.Card is not null && !CardIs(cardCode, r.Card)) continue;
            if (r.When is not null && !r.When.Split('+').All(w => When(m, x, w))) continue;
            double before = v;
            v = r.Set ?? v * r.Mult + r.Delta;
            if (v != before) used.Add(r.Id);
        }
        return v;
    }

    static bool CardIs(string? code, string want) => code is { Length: > 0 } && want switch
    {
        "weapon" => AiKnow.IsWeapon(code),
        "ally" => AiKnow.Traits(code).Contains("盟友"),
        _ => CoreCatalog.Normalize(code).StartsWith(want)
    };

    static bool When(TableModel m, AiContext x, string w) => w switch
    {
        "lethal" => Lethal(m, x),
        "lastAction" => x.Actions <= 1,
        "moreActions" => x.Actions >= 2,
        "cluesReady" => x.Goal.CluesNeeded > 0 && x.TeamClues >= x.Goal.CluesNeeded,
        "noWeapon" => !x.MyAssetCodes.Any(AiKnow.IsWeapon),
        "noWeaponInHand" => !m.Hand(x.Me).Any(h => AiKnow.IsWeapon(h.Code)),
        "hasWeapon" => x.MyAssetCodes.Any(AiKnow.IsWeapon),
        "noTarget" => x.Goal.TargetEnemy is null,
        "targetFar" => TargetDistance(m, x) >= 2,
        _ => false
    };

    // moves from me to the act's target enemy (where it stands, or where the investigator it is engaged with stands); 0 if none
    static int TargetDistance(TableModel m, AiContext x)
    {
        if (x.Goal.TargetEnemy is not { } id || m.Enemies.FirstOrDefault(e => e.Id == id) is not { } t) return 0;
        var at = J.Tag(J.Get(t.Raw, "placement")) == "AtLocation" ? J.String(J.Get(t.Raw, "placement"), "contents")
            : m.Investigators.FirstOrDefault(i => m.EngagedWith(i).Any(e => e.Id == t.Id)) is { } held ? m.LocationOf(held) : null;
        return at is null ? 0 : x.Dist.GetValueOrDefault(at, 9);
    }

    // the ready enemies engaged with me would defeat me with their next attacks (damage or horror)
    public static bool Lethal(TableModel m, AiContext x)
    {
        var ready = m.EngagedWith(x.Me).Where(e => !AiContext.Exhausted(e)).ToArray();
        return ready.Length > 0 && (ready.Sum(e => J.Int(e.Raw, "healthDamage")) >= x.Hp || ready.Sum(e => J.Int(e.Raw, "sanityDamage")) >= x.San);
    }

    static string? Str(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    static double Num(JsonElement e, string name, double dflt) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : dflt;
}
