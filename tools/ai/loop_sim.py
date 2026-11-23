# Learning loop on the headless simulator (tools/sim): no game windows, little CPU, GPU training.
#   collect (3 low-priority sim processes)  →  train on the RTX 4080 (train.py)  →  evaluate rules vs net on fixed seeds  →  repeat
#   python tools/ai/loop_sim.py --iters 20
# Summary: evidence/ai/train/loop-sim.log   Data: evidence/ai/slrn-*/decisions.jsonl   Models: evidence/ai/model/
import argparse, os, random, subprocess, sys, time
from concurrent.futures import ThreadPoolExecutor

ROOT = os.path.abspath(os.path.join(os.path.dirname(__file__), "..", ".."))
sys.path.insert(0, os.path.join(ROOT, "tools", "dev"))
import ai_bench  # rows

SIM = os.path.join(ROOT, "tools", "sim", "bin", "ArkhamSim.exe")
LOG = os.path.join(ROOT, "evidence", "ai", "train", "loop-sim.log")


def log(text):
    os.makedirs(os.path.dirname(LOG), exist_ok=True)
    line = time.strftime("%Y-%m-%d %H:%M:%S") + "  " + text
    print(line, flush=True)
    open(LOG, "a", encoding="utf-8").write(line + "\n")


def sim(tag, games, players, diff, seed, flags):
    # BELOW_NORMAL_PRIORITY_CLASS: the foreground game and AI keep the CPU
    out = subprocess.run([SIM, "--games", str(games), "--players", str(players), "--diff", diff, "--seed", str(seed), "--tag", tag, "--flags", flags],
                         cwd=ROOT, capture_output=True, text=True, encoding="utf-8", errors="replace", creationflags=0x00004000)
    return [l for l in out.stdout.splitlines() if l.startswith("SIM_GAME") or l.startswith("SIM_ERROR")]


def summary(prefix):
    rs = ai_bench.rows(prefix)
    if not rs:
        return "no games"
    wins = sum(str(r["result"]).startswith("Won") for r in rs)
    bad = {}; tot = {}
    for r in rs:
        for k, (b, t) in (r.get("checks") or {}).items():
            bad[k] = bad.get(k, 0) + b; tot[k] = tot.get(k, 0) + t
    checks = ", ".join("%s %.0f%%" % (k, 100.0 * (tot[k] - bad[k]) / max(1, tot[k])) for k in sorted(tot))
    return "games=%d wins=%d (%.0f%%) checks_ok: %s" % (len(rs), wins, 100.0 * wins / len(rs), checks)


def wins(prefix):
    return sum(str(r["result"]).startswith("Won") for r in ai_bench.rows(prefix))


def net_ok(it):
    return wins("seval-i%03d-net" % it) >= wins("seval-i%03d-rules" % it) and ai_bench.rows("seval-i%03d-net" % it)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--iters", type=int, default=20)
    ap.add_argument("--start", type=int, default=0)
    ap.add_argument("--solo", type=int, default=40, help="1-player games per collector per iteration (two collectors: Easy, Standard)")
    ap.add_argument("--four", type=int, default=4, help="4-player Easy games per iteration")
    ap.add_argument("--evals", type=int, default=10, help="fixed-seed evaluation games per side and difficulty")
    a = ap.parse_args()
    model = os.path.join(ROOT, "evidence", "ai", "model", "policy.json")
    for it in range(a.start, a.start + a.iters):
        have = os.path.exists(model)
        # the network plays in collection only once it does not lose to the rules on the fixed seeds
        net = "+net" if have and it > a.start and net_ok(it - 1) else ""
        t0 = time.time()
        jobs = [("slrn-i%03d-1e" % it, a.solo, 1, "Easy", random.randint(1, 10**6), "rec+explore" + net),
                ("slrn-i%03d-1s" % it, a.solo, 1, "Standard", random.randint(1, 10**6), "rec+explore"),
                ("slrn-i%03d-4e" % it, a.four, 4, "Easy", random.randint(1, 10**6), "rec+explore" + net)]
        with ThreadPoolExecutor(max_workers=3) as ex:
            outs = list(ex.map(lambda j: sim(*j), jobs))
        errors = sum(1 for o in outs for l in o if l.startswith("SIM_ERROR"))
        log("iter %d collected %d games in %ds (errors %d); all data: %s" % (it, sum(j[1] for j in jobs), time.time() - t0, errors, summary("slrn")))
        out = subprocess.run([sys.executable, os.path.join(ROOT, "tools", "ai", "train.py"), "slrn-", "80"], cwd=ROOT, capture_output=True, text=True)
        log("iter %d %s" % (it, (out.stdout.strip() or out.stderr.strip()[-400:])))
        if os.path.exists(model):
            t1 = time.time()
            ev = [("seval-i%03d-rules-e" % it, a.evals, 1, "Easy", 9001, ""), ("seval-i%03d-net-e" % it, a.evals, 1, "Easy", 9001, "net"),
                  ("seval-i%03d-rules-s" % it, a.evals, 1, "Standard", 9101, ""), ("seval-i%03d-net-s" % it, a.evals, 1, "Standard", 9101, "net")]
            with ThreadPoolExecutor(max_workers=3) as ex:
                list(ex.map(lambda j: sim(*j), ev))
            log("iter %d eval (%ds) rules: %s" % (it, time.time() - t1, summary("seval-i%03d-rules" % it)))
            log("iter %d eval        net: %s" % (it, summary("seval-i%03d-net" % it)))


if __name__ == "__main__":
    main()
