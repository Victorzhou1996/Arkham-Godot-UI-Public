# Compare campaign runs (docs/AI-PLAY-PLAN-20261119.md §8): campaign score, cultists interrogated / got away, and for each
# chapter the rounds played and investigators defeated (evidence/ai/results.jsonl, one row per chapter).
#   python tools/ai/compare_camp.py "阶段一 规则=tceval-i0(0[1-9]|1[01])-rules" "新规则=tceval-ch23-rules"
import collections, glob, json, os, re, sys

ROOT = os.path.abspath(os.path.join(os.path.dirname(__file__), "..", ".."))
sys.path.insert(0, os.path.dirname(__file__))
import campaign_reward


def camps(rx):
    out = []
    for d in glob.glob(os.path.join(ROOT, "evidence", "ai", "*")):
        if re.match(rx, os.path.basename(d)) and os.path.exists(os.path.join(d, "decisions.jsonl")):
            for line in open(os.path.join(d, "decisions.jsonl"), encoding="utf-8"):
                if '"campaignEnd"' in line:
                    out.append(json.loads(line))
    return out


def chapters(rx):
    n, rounds, down = collections.Counter(), collections.Counter(), collections.Counter()
    for line in open(os.path.join(ROOT, "evidence", "ai", "results.jsonl"), encoding="utf-8"):
        try:
            r = json.loads(line)
        except Exception:
            continue
        if not re.match(rx, os.path.basename(r.get("run", ""))) or r.get("players") != 1:
            continue
        sc = r.get("scenario"); n[sc] += 1; rounds[sc] += r.get("rounds", 0); down[sc] += r.get("defeats", 0)
    return {sc: (n[sc], rounds[sc] / n[sc], down[sc] / n[sc]) for sc in n}


def setn(c, key):
    return next((s["n"] for s in c["rec"]["sets"] if s["key"] == key), 0)


for arg in sys.argv[1:]:
    name, rx = arg.split("=", 1)
    cs = camps(rx)
    if not cs:
        print(name, "no campaigns"); continue
    k = len(cs)
    score = sum(campaign_reward.campaign(c["rec"]) for c in cs) / k
    inter = sum(setn(c, "CultistsWeInterrogated") for c in cs) / k
    away = sum(setn(c, "CultistsWhoGotAway") for c in cs) / k
    lost = sum(any(t.get("lost") for t in c["rec"]["trauma"]) for c in cs)
    won = sum(any("Broken" in x or "Repelled" in x or "Sacrificed" in x for x in c["rec"]["keys"]) for c in cs)
    ch = chapters(rx)
    chs = "  ".join("%s:%d局 %.1f回合 倒下%.2f" % (sc, *ch[sc]) for sc in sorted(ch))
    print("%-14s 战役%3d 平均分%6.2f 审问%.2f 逃脱%.2f 失去调查员%d 第三章好结局%d | %s" % (name, k, score, inter, away, lost, won, chs))
