# Campaign learning loop (docs/AI-PLAY-PLAN-20261119.md §8): whole Night of the Zealot campaigns on the headless simulator,
# reward = what each chapter adds to the campaign (tools/ai/campaign_reward.py), 2 low-priority simulator processes (the
# machine's CPU is shared with the game and QA screenshots), the networks trained on the RTX 4080.
#   python tools/ai/loop_camp.py --iters 12 [--start 0]
# Log: evidence/ai/train/loop-camp.log   Data: evidence/ai/tcamp-*   Eval: evidence/ai/tceval-*
import argparse, glob, json, os, random, subprocess, sys, time
from concurrent.futures import ThreadPoolExecutor

ROOT = os.path.abspath(os.path.join(os.path.dirname(__file__), "..", ".."))
sys.path.insert(0, os.path.dirname(__file__))
import campaign_reward

SIM = os.path.join(ROOT, "tools", "sim", "bin", "ArkhamSim.exe")
LOG = os.path.join(ROOT, "evidence", "ai", "train", "loop-camp.log")
MODEL = os.path.join(ROOT, "evidence", "ai", "model")
WORKERS = 2


def log(text):
    os.makedirs(os.path.dirname(LOG), exist_ok=True)
    line = time.strftime("%Y-%m-%d %H:%M:%S") + "  " + text
    print(line, flush=True); open(LOG, "a", encoding="utf-8").write(line + "\n")


def sim(tag, games, players, diff, seed, flags):
    out = subprocess.run([SIM, "--games", str(games), "--players", str(players), "--diff", diff, "--seed", str(seed), "--tag", tag,
                          "--campaign", "01", "--flags", flags],
                         cwd=ROOT, capture_output=True, text=True, encoding="utf-8", errors="replace", creationflags=0x00004000)   # below normal
    return out.stdout.splitlines()


def campaigns(prefix):
    out = []
    for f in glob.glob(os.path.join(ROOT, "evidence", "ai", prefix + "*", "decisions.jsonl")):
        for line in open(f, encoding="utf-8"):
            if '"campaignEnd"' in line:
                try:
                    out.append(json.loads(line))
                except Exception:
                    pass
    return out


def summary(prefix):
    cs = campaigns(prefix)
    if not cs:
        return "no campaigns"
    score = [campaign_reward.campaign(c["rec"]) for c in cs]
    ends = {}
    for c in cs:
        e = next((k for k in c["rec"]["keys"] if "Umordhoth" in k), "未打到第三章结局")
        ends[e] = ends.get(e, 0) + 1
    lost = sum(any(t.get("lost") for t in c["rec"]["trauma"]) for c in cs)
    trauma = sum(t["phys"] + t["ment"] for c in cs for t in c["rec"]["trauma"]) / len(cs)
    inter = sum(next((s["n"] for s in c["rec"]["sets"] if s["key"] == "CultistsWeInterrogated"), 0) for c in cs) / len(cs)
    house = sum("YourHouseIsStillStanding" in c["rec"]["keys"] for c in cs)
    priest = sum("GhoulPriestIsStillAlive" not in c["rec"]["keys"] for c in cs)
    unk = sorted({k for c in cs for k in campaign_reward.unknown(c["rec"])})
    return ("campaigns=%d score=%.2f lost=%d trauma=%.1f interrogated=%.1f house=%d priestKilled=%d ends=%s%s"
            % (len(cs), sum(score) / len(cs), lost, trauma, inter, house, priest, json.dumps(ends, ensure_ascii=False),
               (" unknownKeys=" + ",".join(unk)) if unk else ""))


def fresh(name, since):
    p = os.path.join(MODEL, name)
    return os.path.exists(p) and os.path.getmtime(p) > since


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--iters", type=int, default=12)
    ap.add_argument("--start", type=int, default=0)
    ap.add_argument("--solo", type=int, default=8)
    ap.add_argument("--duo", type=int, default=2)
    ap.add_argument("--evals", type=int, default=6)
    ap.add_argument("--warmup", type=int, default=150, help="plain rule campaigns before the search teacher starts")
    ap.add_argument("--tag", default="t", help="data tcamp-*, eval tceval-*; stage 3 (rule-signal reward): --tag s3; stage 4 (deck upgrades, last chapter: ending only): --tag s4")
    ap.add_argument("--rotate", action="store_true", help="training data: the solo investigator changes with the seed (all five of the core set)")
    a = ap.parse_args()
    D, E = a.tag + "camp-", a.tag + "ceval-"   # data and evaluation run prefixes
    since = time.time() - 1 if a.start == 0 else 0   # a fresh run only trusts models it trained itself
    for it in range(a.start, a.start + a.iters):
        has_v = fresh("value.json", since) and len(campaigns(D)) >= a.warmup
        flags = "rec+explore" + ("+teach" if has_v else "") + ("+rotate" if a.rotate else "")
        t0 = time.time()
        jobs = [(D + "i%03d-1e" % it, a.solo, 1, "Easy", random.randint(1, 10**6), flags),
                (D + "i%03d-1s" % it, a.solo, 1, "Standard", random.randint(1, 10**6), flags),
                (D + "i%03d-2e" % it, a.duo, 2, "Easy", random.randint(1, 10**6), flags)]
        with ThreadPoolExecutor(max_workers=WORKERS) as ex:
            outs = list(ex.map(lambda j: sim(*j), jobs))
        teach = [l for o in outs for l in o if l.startswith("SIM_TEACH")]
        dec = sum(int(l.split("decisions=")[1].split()[0]) for l in teach); chg = sum(int(l.split("changed=")[1].split()[0]) for l in teach)
        errs = [l for o in outs for l in o if l.startswith("SIM_ERROR")]
        log("iter %d collected (%s) in %ds; teacher changed %d of %d; errors %d; data: %s" % (it, flags, time.time() - t0, chg, dec, len(errs), summary(D)))
        for e in errs[:3]:
            log("iter %d SIM_ERROR %s" % (it, e[:300]))
        # the card memory: what each card bought / cut between chapters added to the campaign (AiUpgrade reads it)
        cw = subprocess.run([sys.executable, os.path.join(ROOT, "tools", "ai", "card_worth.py")], cwd=ROOT, capture_output=True, text=True, encoding="utf-8", errors="replace")
        log("iter %d %s" % (it, (cw.stdout.strip() or cw.stderr.strip()[-300:])[:900]))
        out = subprocess.run([sys.executable, os.path.join(ROOT, "tools", "ai", "train2.py"), D, "150"], cwd=ROOT, capture_output=True, text=True)
        log("iter %d %s" % (it, out.stdout.strip() or out.stderr.strip()[-500:]))
        if it % 2 == 1 or it == a.start + a.iters - 1:   # evaluation every second iteration (CPU)
            t1 = time.time(); ev = []
            for diff, seed in (("Easy", 9001), ("Standard", 9101)):
                ev.append((E + "i%03d-rules-%s" % (it, diff[0]), a.evals, 1, diff, seed, "rec"))
                if fresh("value.json", since):
                    ev.append((E + "i%03d-teach-%s" % (it, diff[0]), a.evals, 1, diff, seed, "rec+teach"))
                if fresh("policy.json", since):
                    ev.append((E + "i%03d-net-%s" % (it, diff[0]), a.evals, 1, diff, seed, "rec+net"))
            with ThreadPoolExecutor(max_workers=WORKERS) as ex:
                list(ex.map(lambda j: sim(*j), ev))
            for side in ("rules", "teach", "net"):
                if campaigns(E + "i%03d-%s" % (it, side)):
                    log("iter %d eval %-5s %s" % (it, side, summary(E + "i%03d-%s" % (it, side))))
            log("iter %d eval took %ds" % (it, time.time() - t1))


if __name__ == "__main__":
    main()
