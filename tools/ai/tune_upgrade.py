# Deck-upgrade A/B (docs/AI-PLAY-PLAN-20261119.md §15): the same campaigns (same seeds) played by the rule AI, each variant
# buying a different card first between chapters (AiUpgrade.First, sim --upgrade-first), plus "base" (the worth table as it is)
# and "none" (no upgrades). The campaign score per variant says which buys pay off; the worth table is then set from it.
#   python tools/ai/tune_upgrade.py --games 12                 run every variant (2 sim workers), then print the table
#   python tools/ai/tune_upgrade.py --report                   only print the table from runs already made
#   python tools/ai/tune_upgrade.py --variants base,01029 --tag upab2 --worth evidence/ai/train/worth-try.json
import argparse, concurrent.futures, glob, json, math, os, subprocess, sys

ROOT = os.path.abspath(os.path.join(os.path.dirname(__file__), "..", ".."))
sys.path.insert(0, os.path.dirname(__file__))
import campaign_reward

SIM = os.path.join(ROOT, "tools", "sim", "bin", "ArkhamSim.exe")
# Roland (the sim's solo investigator): guardian 0-5, seeker 0-2, neutral — the core set experience cards he can take
ROLAND = ["01026", "01027", "01028", "01029", "01094", "01095", "01040", "01041", "01042"]
SEEDS = {"Easy": 9201, "Standard": 9301}


def run(tag, variant, diff, games, worth):
    flags = "rec+noupgrade" if variant == "none" else "rec"
    cmd = [SIM, "--campaign", "01", "--flags", flags, "--seed", str(SEEDS[diff]), "--diff", diff, "--games", str(games),
           "--tag", "%s-%s-%s" % (tag, variant, diff[0])]
    if variant not in ("base", "none"):
        cmd += ["--upgrade-first", variant]
    if worth:
        cmd += ["--worth", worth]
    subprocess.run(cmd, cwd=ROOT, capture_output=True, text=True, encoding="utf-8", errors="replace", creationflags=0x00004000)


def camps(tag, variant):
    out = {}
    for d in glob.glob(os.path.join(ROOT, "evidence", "ai", "%s-%s-*-g*" % (tag, variant))):
        f = os.path.join(d, "decisions.jsonl")
        if not os.path.exists(f):
            continue
        for line in open(f, encoding="utf-8"):
            if '"campaignEnd"' in line:
                out[os.path.basename(d)[len(tag) + len(variant) + 2:]] = json.loads(line)   # key: "E-g3"
    return out


def report(tag, variants):
    base = camps(tag, "base")
    print("%-8s %4s %7s %6s %9s %5s %6s  %s" % ("variant", "n", "score", "±se", "vs base", "won", "lost", "bought (first upgrade)"))
    for v in variants:
        cs = camps(tag, v)
        if not cs:
            continue
        s = {k: campaign_reward.campaign(c["rec"]) for k, c in cs.items()}
        n = len(s); mean = sum(s.values()) / n
        se = math.sqrt(sum((x - mean) ** 2 for x in s.values()) / max(1, n - 1) / n)
        pair = [s[k] - campaign_reward.campaign(base[k]["rec"]) for k in s if k in base]
        dv = "%+.2f" % (sum(pair) / len(pair)) if pair and v != "base" else ""
        won = sum(any("Repelled" in k or "Sacrificed" in k for k in c["rec"]["keys"]) for c in cs.values())
        lost = sum(any(t.get("lost") for t in c["rec"]["trauma"]) for c in cs.values())
        firsts = {}
        for c in cs.values():
            u = c.get("upgrades") or []
            b = u[0].split(":", 1)[1] if u else "-"
            firsts[b] = firsts.get(b, 0) + 1
        top = ", ".join("%s×%d" % kv for kv in sorted(firsts.items(), key=lambda kv: -kv[1])[:4])
        print("%-8s %4d %7.2f %6.2f %9s %5d %6d  %s" % (v, n, mean, se, dv, won, lost, top))


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--games", type=int, default=12, help="campaigns per variant and difficulty")
    ap.add_argument("--tag", default="upab")
    ap.add_argument("--variants", default="base,none," + ",".join(ROLAND))
    ap.add_argument("--worth", default="", help="JSON of worth values to try (sim --worth)")
    ap.add_argument("--report", action="store_true")
    a = ap.parse_args()
    variants = a.variants.split(",")
    if not a.report:
        jobs = [(v, d) for v in variants for d in SEEDS]
        with concurrent.futures.ThreadPoolExecutor(2) as ex:   # 2 sim workers: the CPU is left for the game
            list(ex.map(lambda j: run(a.tag, j[0], j[1], a.games, a.worth), jobs))
    report(a.tag, variants)


if __name__ == "__main__":
    main()
