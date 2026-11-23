# AI benchmark: runs many autoplay games in parallel (off-screen), each in its own qa-data/ai/<tag> folder with the qa4 decks
# (random basic weakness), a fixed seed (ARKHAM_QA_SEED) and a difficulty, then summarises evidence/ai/results.jsonl.
#
#   python tools/dev/ai_bench.py run  --name cmp1 --ais v2,legacy --players 1,4 --diffs Easy,Standard --seeds 5 --jobs 6
#   python tools/dev/ai_bench.py report --name cmp1
#
# Never touches qa-data/r11play2 (it only reads its decks.json / settings.json as templates).
import argparse, json, os, subprocess, sys, time, itertools
from concurrent.futures import ThreadPoolExecutor

ROOT = os.path.abspath(os.path.join(os.path.dirname(__file__), "..", ".."))
INVESTIGATORS = ["01001", "01002", "01003", "01004"]


def make_data(tag, diff):
    d = os.path.join(ROOT, "qa-data", "ai", tag)
    os.makedirs(d, exist_ok=True)
    src = json.load(open(os.path.join(ROOT, "qa-data", "r11play2", "decks.json"), encoding="utf-8"))
    decks = {k: v for k, v in src.items() if k.startswith("qa4-")}
    for v in decks.values():
        assert v["slots"].get("01000") == 1, "decks must use the random basic weakness"
    json.dump(decks, open(os.path.join(d, "decks.json"), "w", encoding="utf-8"), ensure_ascii=False, indent=1)
    st = json.load(open(os.path.join(ROOT, "qa-data", "r11play2", "settings.json"), encoding="utf-8"))
    st.update({"Difficulty": diff, "ReducedMotion": True, "AutoSkip": True, "MusicVolume": 0, "EffectsVolume": 0})
    json.dump(st, open(os.path.join(d, "settings.json"), "w", encoding="utf-8"), indent=1)
    return "qa-data/ai/" + tag


def run_one(job):
    tag, ai, players, diff, seed, scenario, timeout = job
    data = make_data(tag, diff)
    adds = ";".join("prepare-add:%s;wait:0.5" % c for c in INVESTIGATORS[:players])
    flags = "fair+fast" + ("+legacy" if ai == "legacy" else "")
    start = "prepare-standalone:%s" % scenario if scenario != "campaign" else "prepare-start"
    steps = 3000 * players if scenario != "campaign" else 9000 * players
    script = "wait:3;menu:new;wait:3;%s;%s;wait:10;autoplay:%d,evidence/ai/%s,%s;quit" % (adds, start, steps, tag, flags)
    env = dict(os.environ, ARKHAM_QA_SEED=str(seed))
    t0 = time.time()
    out = subprocess.run(["powershell", "-NoProfile", "-File", os.path.join(ROOT, "tools", "dev", "rungame.ps1"), "-Tag", "ai-" + tag,
                          "-Size", "1280x720", "-DataRoot", data, "-TimeoutSec", str(timeout), "-Script", script],
                         cwd=ROOT, env=env, capture_output=True, text=True)
    print("%-28s %-6s %dp %-8s seed=%-6s %s %ds" % (tag, ai, players, diff, seed, out.stdout.strip(), time.time() - t0), flush=True)
    # a game killed at the timeout writes no AI_SUMMARY: record it as a loss so the win rate stays honest
    if not any(r.get("run") == "evidence/ai/" + tag for r in rows(tag.split("-")[0])):
        record_missing(tag, ai, players, diff, seed, scenario, "Timeout" if "TIMEOUT" in out.stdout else "NoSummary", int(time.time() - t0))


def record_missing(tag, ai, players, diff, seed, scenario, result, seconds):
    row = {"time": time.strftime("%Y-%m-%d %H:%M:%S"), "run": "evidence/ai/" + tag, "ai": ai, "flags": "fair+fast", "scenario": scenario,
           "players": players, "investigators": "/".join(INVESTIGATORS[:players]), "difficulty": diff, "result": result, "rounds": 0,
           "steps": 0, "defeats": 0, "undos": 0, "damage": 0, "horror": 0, "seconds": seconds, "seed": str(seed)}
    with open(os.path.join(ROOT, "evidence", "ai", "results.jsonl"), "a", encoding="utf-8") as f:
        f.write(json.dumps(row, ensure_ascii=False) + "\n")


def rows(name):
    f = os.path.join(ROOT, "evidence", "ai", "results.jsonl")
    if not os.path.exists(f):
        return []
    out = []
    for line in open(f, encoding="utf-8"):
        try:
            r = json.loads(line)
        except Exception:
            continue
        if r.get("run", "").startswith("evidence/ai/" + name + "-"):
            out.append(r)
    return out


def report(name):
    rs = rows(name)
    groups = {}
    for r in rs:
        k = (r["ai"], r["players"], r["difficulty"], r["scenario"])
        groups.setdefault(k, []).append(r)
    print("| AI | 人数 | 难度 | 剧本 | 局数 | 胜 | 胜率 | 平均轮数(胜局) | 平均被击败人次 | 平均伤害+恐惧 | 平均耗时(秒) |")
    print("|---|---|---|---|---|---|---|---|---|---|---|")
    for k in sorted(groups):
        g = groups[k]
        wins = [r for r in g if str(r["result"]).startswith("Won")]
        avg = lambda xs: sum(xs) / len(xs) if xs else 0
        print("| %s | %d | %s | %s | %d | %d | %.0f%% | %s | %.1f | %.1f | %.0f |" % (
            k[0], k[1], k[2], k[3], len(g), len(wins), 100.0 * len(wins) / len(g),
            ("%.1f" % avg([r["rounds"] for r in wins])) if wins else "-",
            avg([r["defeats"] for r in g]), avg([r["damage"] + r["horror"] for r in g]), avg([r["seconds"] for r in g])))


def main():
    try:
        sys.stdout.reconfigure(encoding="utf-8")
    except Exception:
        pass
    ap = argparse.ArgumentParser()
    ap.add_argument("cmd", choices=["run", "report", "missing"])
    ap.add_argument("--name", required=True)
    ap.add_argument("--ais", default="v2,legacy")
    ap.add_argument("--players", default="1,4")
    ap.add_argument("--diffs", default="Easy,Standard")
    ap.add_argument("--seeds", type=int, default=5)
    ap.add_argument("--seed0", type=int, default=7001)
    ap.add_argument("--scenario", default="01104")
    ap.add_argument("--jobs", type=int, default=6)
    ap.add_argument("--timeout", type=int, default=2400)
    a = ap.parse_args()
    if a.cmd == "report":
        report(a.name); return
    if a.cmd == "missing":   # games of a finished batch that left no result line (killed at the timeout): record them as losses
        done = {r["run"] for r in rows(a.name)}
        for players, diff, i, ai in itertools.product([int(p) for p in a.players.split(",")], a.diffs.split(","), range(a.seeds), a.ais.split(",")):
            tag = "%s-%s-%dp-%s-%d" % (a.name, ai, players, diff[:3].lower(), a.seed0 + i)
            if "evidence/ai/" + tag not in done:
                record_missing(tag, ai, players, diff, a.seed0 + i, a.scenario, "Timeout", a.timeout); print("missing", tag)
        return
    jobs = []
    for players, diff, i, ai in itertools.product([int(p) for p in a.players.split(",")], a.diffs.split(","), range(a.seeds), a.ais.split(",")):
        seed = a.seed0 + i
        tag = "%s-%s-%dp-%s-%d" % (a.name, ai, players, diff[:3].lower(), seed)
        jobs.append((tag, ai, players, diff, seed, a.scenario, a.timeout))
    # long (4-player) games first, so the pool ends together
    jobs.sort(key=lambda j: -j[2])
    with ThreadPoolExecutor(max_workers=a.jobs) as ex:
        list(ex.map(run_one, jobs))
    report(a.name)


if __name__ == "__main__":
    main()
