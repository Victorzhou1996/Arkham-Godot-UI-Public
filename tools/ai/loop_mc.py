# Search-teacher learning loop (docs/AI-PLAY-PLAN-20261119.md §4.3, P2–P4) on the headless simulator:
#   collect: 3 low-priority simulator processes; with a value network the teacher tries the rule AI's 3 best choices in the
#            core and keeps the one V likes best (flag teach); every decision is recorded
#   train:   value + policy networks on the RTX 4080 (train2.py)
#   eval:    fixed seeds, solo Easy + Standard: rules / policy network / search teacher
#   python tools/ai/loop_mc.py --iters 30
# Log: evidence/ai/train/loop-mc.log   Data: evidence/ai/tlrn-*   Models: evidence/ai/model/value.json, policy.json
import argparse, os, random, subprocess, sys, time
from concurrent.futures import ThreadPoolExecutor

ROOT = os.path.abspath(os.path.join(os.path.dirname(__file__), "..", ".."))
sys.path.insert(0, os.path.join(ROOT, "tools", "dev"))
import ai_bench

SIM = os.path.join(ROOT, "tools", "sim", "bin", "ArkhamSim.exe")
LOG = os.path.join(ROOT, "evidence", "ai", "train", "loop-mc.log")
MODEL = os.path.join(ROOT, "evidence", "ai", "model")


def log(text):
    os.makedirs(os.path.dirname(LOG), exist_ok=True)
    line = time.strftime("%Y-%m-%d %H:%M:%S") + "  " + text
    print(line, flush=True); open(LOG, "a", encoding="utf-8").write(line + "\n")


def sim(tag, games, players, diff, seed, flags):
    out = subprocess.run([SIM, "--games", str(games), "--players", str(players), "--diff", diff, "--seed", str(seed), "--tag", tag, "--flags", flags],
                         cwd=ROOT, capture_output=True, text=True, encoding="utf-8", errors="replace", creationflags=0x00004000)   # below normal
    return out.stdout.splitlines()


def summary(prefix):
    rs = ai_bench.rows(prefix)
    if not rs:
        return "no games"
    wins = sum(str(r["result"]).startswith("Won") for r in rs)
    bad, tot = {}, {}
    for r in rs:
        for k, (b, t) in (r.get("checks") or {}).items():
            bad[k] = bad.get(k, 0) + b; tot[k] = tot.get(k, 0) + t
    return "games=%d wins=%d (%.0f%%) checks_ok: %s" % (len(rs), wins, 100.0 * wins / len(rs),
                                                       ", ".join("%s %.0f%%" % (k, 100.0 * (tot[k] - bad[k]) / max(1, tot[k])) for k in sorted(tot)))


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--iters", type=int, default=30)
    ap.add_argument("--start", type=int, default=0)
    ap.add_argument("--solo", type=int, default=30)
    ap.add_argument("--four", type=int, default=3)
    ap.add_argument("--evals", type=int, default=10)
    ap.add_argument("--warmup", type=int, default=600, help="plain rule games before the search teacher starts")
    a = ap.parse_args()
    for it in range(a.start, a.start + a.iters):
        # the teacher starts once V has learned from enough plain rule games (its noise must be below the choices' differences)
        has_v = os.path.exists(os.path.join(MODEL, "value.json")) and len(ai_bench.rows("tlrn")) >= a.warmup
        flags = "rec+explore" + ("+teach" if has_v else "")
        t0 = time.time()
        jobs = [("tlrn-i%03d-1e" % it, a.solo, 1, "Easy", random.randint(1, 10**6), flags),
                ("tlrn-i%03d-1s" % it, a.solo, 1, "Standard", random.randint(1, 10**6), flags),
                ("tlrn-i%03d-4e" % it, a.four, 4, "Easy", random.randint(1, 10**6), flags)]
        with ThreadPoolExecutor(max_workers=3) as ex:
            outs = list(ex.map(lambda j: sim(*j), jobs))
        teach = [l for o in outs for l in o if l.startswith("SIM_TEACH")]
        dec = sum(int(l.split("decisions=")[1].split()[0]) for l in teach); chg = sum(int(l.split("changed=")[1].split()[0]) for l in teach)
        log("iter %d collected (%s) in %ds; teacher changed %d of %d turn decisions; data: %s" % (it, flags, time.time() - t0, chg, dec, summary("tlrn")))
        out = subprocess.run([sys.executable, os.path.join(ROOT, "tools", "ai", "train2.py"), "tlrn-", "150"], cwd=ROOT, capture_output=True, text=True)
        log("iter %d %s" % (it, out.stdout.strip() or out.stderr.strip()[-500:]))
        if os.path.exists(os.path.join(MODEL, "value.json")):
            t1 = time.time(); ev = []
            for diff, seed in (("Easy", 9001), ("Standard", 9101)):
                ev.append(("teval-i%03d-rules-%s" % (it, diff[0]), a.evals, 1, diff, seed, ""))
                ev.append(("teval-i%03d-teach-%s" % (it, diff[0]), a.evals, 1, diff, seed, "teach"))
                if os.path.exists(os.path.join(MODEL, "policy.json")):
                    ev.append(("teval-i%03d-net-%s" % (it, diff[0]), a.evals, 1, diff, seed, "net"))
            with ThreadPoolExecutor(max_workers=3) as ex:
                list(ex.map(lambda j: sim(*j), ev))
            for side in ("rules", "teach", "net"):
                if ai_bench.rows("teval-i%03d-%s" % (it, side)):
                    log("iter %d eval %-5s %s" % (it, side, summary("teval-i%03d-%s" % (it, side))))
            log("iter %d eval took %ds" % (it, time.time() - t1))


if __name__ == "__main__":
    main()
