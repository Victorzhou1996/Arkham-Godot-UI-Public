# Local learning loop (runs for hours on its own; Claude only reads the summary):
#   collect games in parallel (off-screen, CPU)  →  train Q network on the RTX 4080 (train.py)  →  evaluate  →  repeat
#   python tools/ai/loop.py --iters 12 --jobs 6
# Summary per iteration: evidence/ai/train/loop.log   Games: evidence/ai/lrn-*/decisions.jsonl   Models: evidence/ai/model/
# Disk: a 1-player game keeps ~8 MB of save history in its qa-data folder, a 4-player game ~90 MB (nothing is deleted).
import argparse, json, os, random, subprocess, sys, time
from concurrent.futures import ThreadPoolExecutor

ROOT = os.path.abspath(os.path.join(os.path.dirname(__file__), "..", ".."))
sys.path.insert(0, os.path.join(ROOT, "tools", "dev"))
import ai_bench  # make_data, rows

LOG = os.path.join(ROOT, "evidence", "ai", "train", "loop.log")


def log(text):
    os.makedirs(os.path.dirname(LOG), exist_ok=True)
    line = time.strftime("%Y-%m-%d %H:%M:%S") + "  " + text
    print(line, flush=True)
    open(LOG, "a", encoding="utf-8").write(line + "\n")


def play(tag, players, diff, seed, flags, timeout):
    data = ai_bench.make_data(tag, diff)
    adds = ";".join("prepare-add:%s;wait:0.5" % c for c in ai_bench.INVESTIGATORS[:players])
    script = "wait:3;menu:new;wait:3;%s;prepare-standalone:01104;wait:10;autoplay:%d,evidence/ai/%s,%s;quit" % (adds, 3000 * players, tag, flags)
    env = dict(os.environ, ARKHAM_QA_SEED=str(seed))
    out = subprocess.run(["powershell", "-NoProfile", "-File", os.path.join(ROOT, "tools", "dev", "rungame.ps1"), "-Tag", "ai-" + tag,
                          "-Size", "1280x720", "-DataRoot", data, "-TimeoutSec", str(timeout), "-Script", script],
                         cwd=ROOT, env=env, capture_output=True, text=True)
    return out.stdout.strip()


def summary(prefix):
    rs = ai_bench.rows(prefix)
    if not rs:
        return "no games"
    wins = sum(str(r["result"]).startswith("Won") for r in rs)
    bad = {}; tot = {}
    for r in rs:
        for k, (b, t) in (r.get("checks") or {}).items():
            bad[k] = bad.get(k, 0) + b; tot[k] = tot.get(k, 0) + t
    checks = ", ".join("%s %d/%d" % (k, tot[k] - bad[k], tot[k]) for k in sorted(tot))
    return "games=%d wins=%d (%.0f%%) checks_ok: %s" % (len(rs), wins, 100.0 * wins / len(rs), checks)


LOWER = ("Get-CimInstance Win32_Process | Where-Object { $_.Name -like 'Godot*' -and ($_.CommandLine -like '*qa-data/ai/lrn*') } | "
         "ForEach-Object { try { (Get-Process -Id $_.ProcessId).PriorityClass = 'BelowNormal' } catch {} }")


def keep_low(stop):
    # Godot raises its own priority to AboveNormal; the training games must not compete with the foreground game / AI
    while not stop.is_set():
        subprocess.run(["powershell", "-NoProfile", "-Command", LOWER], capture_output=True)
        stop.wait(10)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--iters", type=int, default=12)
    ap.add_argument("--jobs", type=int, default=6)
    ap.add_argument("--solo", type=int, default=10, help="1-player training games per iteration")
    ap.add_argument("--four", type=int, default=2, help="4-player training games per iteration")
    ap.add_argument("--evals", type=int, default=4, help="1-player evaluation games per iteration (fixed seeds, no exploration)")
    ap.add_argument("--start", type=int, default=0)
    a = ap.parse_args()
    import threading
    stop = threading.Event(); threading.Thread(target=keep_low, args=(stop,), daemon=True).start()
    for it in range(a.start, a.start + a.iters):
        have_model = os.path.exists(os.path.join(ROOT, "evidence", "ai", "model", "policy.json"))
        jobs = []
        for k in range(a.four):
            jobs.append(("lrn-i%02d-4p-%d" % (it, k), 4, "Easy", random.randint(1, 10**6), "fair+fast+rec+explore" + ("+net" if have_model and k % 2 else ""), 2400))
        for k in range(a.solo):
            diff = "Easy" if k % 2 == 0 else "Standard"
            jobs.append(("lrn-i%02d-1p-%d" % (it, k), 1, diff, random.randint(1, 10**6), "fair+fast+rec+explore" + ("+net" if have_model and k % 2 else ""), 900))
        # evaluation on fixed seeds: the rule AI and the model side by side (same seeds every iteration)
        for k in range(a.evals):
            diff = "Easy" if k % 2 == 0 else "Standard"
            seed = 9001 + k
            jobs.append(("lrneval-i%02d-rules-%d" % (it, k), 1, diff, seed, "fair+fast", 900))
            if have_model:
                jobs.append(("lrneval-i%02d-net-%d" % (it, k), 1, diff, seed, "fair+fast+net", 900))
        t0 = time.time()
        with ThreadPoolExecutor(max_workers=a.jobs) as ex:
            list(ex.map(lambda j: play(*j), jobs))
        log("iter %d played %d games in %ds" % (it, len(jobs), time.time() - t0))
        log("iter %d training data so far: %s" % (it, summary("lrn")))
        log("iter %d eval rules: %s" % (it, summary("lrneval-i%02d-rules" % it)))
        if have_model:
            log("iter %d eval net:   %s" % (it, summary("lrneval-i%02d-net" % it)))
        out = subprocess.run([sys.executable, os.path.join(ROOT, "tools", "ai", "train.py"), "lrn-", "80"], cwd=ROOT, capture_output=True, text=True)
        log("iter %d %s" % (it, (out.stdout.strip() or out.stderr.strip()[-400:])))


if __name__ == "__main__":
    main()
