# The AI's card memory (docs/AI-PLAY-PLAN-20261119.md §15): which experience cards are worth buying, learned from training.
# Every campaign the sim plays records its deck upgrades (campaignEnd.upgradeLog: investigator, experience, cards bought and
# cut, the campaign record before). In training (sim flag explore) the AI now and then buys another card or cuts another
# one, so the data holds the alternatives. For each investigator a ridge regression of the campaign score still to come
# (final score - score at the upgrade) on the cards bought / cut, with difficulty, chapter, experience and the score so far
# as controls, says what each card added. The result, shrunk by how often a card was seen, is written to
# evidence/ai/model/card-worth.json, which AiUpgrade reads (added to the hand-set worth of the card).
#   python tools/ai/card_worth.py            fit on every recorded campaign and write the memory
#   python tools/ai/card_worth.py --show     also print the table
#   python tools/ai/card_worth.py --collect 200 --tag cwm1   first play 200 campaigns per difficulty that only explore the
#        upgrades (rule AI, all five investigators in turn; 2 sim workers, batches of 10), then fit
import argparse, concurrent.futures, glob, json, math, os, random, subprocess, sys, time

import numpy as np

ROOT = os.path.abspath(os.path.join(os.path.dirname(__file__), "..", ".."))
sys.path.insert(0, os.path.dirname(__file__))
import campaign_reward

OUT = os.path.join(ROOT, "evidence", "ai", "model", "card-worth.json")
SCALE = 2.0      # worth points per campaign score point
SHRINK = 20      # a card seen n times keeps n / (n + SHRINK) of its measured effect
CLIP = 5.0       # never move a card's worth more than this
RIDGE = 4.0


def events():
    out = []
    for f in glob.glob(os.path.join(ROOT, "evidence", "ai", "*", "decisions.jsonl")):
        try:
            lines = [l for l in open(f, encoding="utf-8") if '"upgradeLog"' in l]
        except OSError:
            continue
        for line in lines:
            try:
                c = json.loads(line)
            except Exception:
                continue
            final = campaign_reward.campaign(c["rec"])
            for e in c.get("upgradeLog") or []:
                before = campaign_reward.campaign(e["before"])
                out.append({"inv": e["inv"], "diff": c.get("diff", "Easy"), "at": e.get("at", 0), "xp": e.get("xp", 0),
                            "so_far": before, "y": final - before, "buy": e.get("buy") or [], "cut": [x for x in e.get("cut") or [] if x]})
    return out


def fit(ev):
    feats = sorted({"buy:" + c for e in ev for c in e["buy"]} | {"cut:" + c for e in ev for c in e["cut"]})
    controls = ["one", "standard", "second", "xp", "so_far"]
    X = np.zeros((len(ev), len(controls) + len(feats))); y = np.array([e["y"] for e in ev], dtype=float)
    idx = {f: len(controls) + i for i, f in enumerate(feats)}
    for r, e in enumerate(ev):
        X[r, :5] = [1, e["diff"] == "Standard", e["at"] > 0, e["xp"], e["so_far"]]
        for c in e["buy"]:
            X[r, idx["buy:" + c]] += 1
        for c in e["cut"]:
            X[r, idx["cut:" + c]] += 1
    pen = np.diag([1e-3] * len(controls) + [RIDGE] * len(feats))   # controls barely held (a column can be constant: all Easy)
    A = X.T @ X + pen
    beta = np.linalg.solve(A, X.T @ y)
    resid = y - X @ beta
    s2 = float(resid @ resid) / max(1, len(ev) - len(controls))
    cov = s2 * np.linalg.inv(A)
    n = (X != 0).sum(axis=0)
    return {f: (float(beta[i]), math.sqrt(max(0.0, cov[i, i])), int(n[i])) for f, i in idx.items()}


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--show", action="store_true")
    ap.add_argument("--collect", type=int, default=0, help="campaigns per difficulty to play first (sim flags rec+upexplore+rotate)")
    ap.add_argument("--tag", default="cwm")
    a = ap.parse_args()
    if a.collect:
        sim = os.path.join(ROOT, "tools", "sim", "bin", "ArkhamSim.exe")
        jobs = [("%s-b%02d-%s" % (a.tag, b, d[0]), d, random.randint(1, 10**6)) for b in range((a.collect + 9) // 10) for d in ("Easy", "Standard")]
        run = lambda j: subprocess.run([sim, "--campaign", "01", "--flags", "rec+upexplore+rotate", "--seed", str(j[2]), "--diff", j[1],
                                        "--games", "10", "--tag", j[0]], cwd=ROOT, capture_output=True, creationflags=0x00004000)
        with concurrent.futures.ThreadPoolExecutor(2) as ex:   # 2 sim workers: the CPU is left for the game
            list(ex.map(run, jobs))
    ev = events()
    memory = {"updated": time.strftime("%Y-%m-%d %H:%M:%S"), "events": len(ev), "scale": SCALE, "shrink": SHRINK, "investigators": {}}
    lines = []
    for inv in sorted({e["inv"] for e in ev}):
        sub = [e for e in ev if e["inv"] == inv]
        if len(sub) < 8:
            continue
        cards = {}
        for f, (b, se, n) in fit(sub).items():
            kind, code = f.split(":")
            w = max(-CLIP, min(CLIP, SCALE * b * n / (n + SHRINK)))
            c = cards.setdefault(code, {"buy": 0.0, "cut": 0.0})
            c[kind] = round(w, 2); c[kind + "Effect"] = round(b, 2); c[kind + "Se"] = round(se, 2); c[kind + "N"] = n
        memory["investigators"][inv] = cards
        lines.append("%s events=%d " % (inv, len(sub)) + " ".join(
            "%s%+.1f(n%d)" % (k, v["buyEffect"], v["buyN"]) for k, v in sorted(cards.items(), key=lambda kv: -kv[1]["buy"]) if "buyN" in v))
    os.makedirs(os.path.dirname(OUT), exist_ok=True)
    tmp = OUT + ".tmp"
    json.dump(memory, open(tmp, "w", encoding="utf-8"), ensure_ascii=False, indent=1)
    os.replace(tmp, OUT)
    print("CARD_WORTH events=%d " % len(ev) + " | ".join(lines))
    if a.show:
        for inv, cards in memory["investigators"].items():
            print(inv)
            for code, v in sorted(cards.items(), key=lambda kv: -(kv[1]["buy"] + kv[1]["cut"])):
                print("  %s buy %+.2f (effect %+.2f ±%.2f n%d)  cut %+.2f (effect %+.2f ±%.2f n%d)" % (
                    code, v["buy"], v.get("buyEffect", 0), v.get("buySe", 0), v.get("buyN", 0),
                    v["cut"], v.get("cutEffect", 0), v.get("cutSe", 0), v.get("cutN", 0)))


if __name__ == "__main__":
    main()
