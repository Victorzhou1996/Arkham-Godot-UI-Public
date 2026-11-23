# Offscreen sweep over every campaign / scenario in campaigns-catalog.json (later cycles, 11-22): for each, a fresh data
# directory, the real menu path (cycle:<id> → prepare → add seats → start), then autoplay N steps without screenshots, and a
# couple of screenshots of the table. Runs `parallel` games at a time through tools/dev/rungame.ps1 (offscreen).
# Writes evidence/later/sweep/<id>.png etc. and a summary evidence/later/sweep/summary.json from the logs.
# Usage: python tools/dev/sweep_starts.py [steps] [parallel] [only ids comma-separated]
import json, os, re, subprocess, sys, concurrent.futures, time

root = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
steps = int(sys.argv[1]) if len(sys.argv) > 1 else 150
par = int(sys.argv[2]) if len(sys.argv) > 2 else 4
only = set(sys.argv[3].split(",")) if len(sys.argv) > 3 and sys.argv[3] else None
cat = json.load(open(os.path.join(root, "app", "assets", "data", "campaigns-catalog.json"), encoding="utf-8"))
jobs = []
for c in cat["campaigns"]:
    jobs.append((c["id"], c["id"], ""))
    if c.get("returnTo"): jobs.append((c["returnTo"]["id"], c["id"], "return"))
for s in cat["sideStories"]: jobs.append((s["id"], s["id"], "", s.get("needsInvestigator", "")))
for f in cat["fanCampaigns"] + cat["fanScenarios"]: jobs.append((f["id"], f["id"], ""))
jobs = [j if len(j) == 4 else j + ("",) for j in jobs]
if only: jobs = [j for j in jobs if j[0] in only]
out = os.path.join(root, "evidence", "later", "sweep"); os.makedirs(out, exist_ok=True)
safe = lambda i: re.sub(r"[^A-Za-z0-9_-]", "_", i.strip(":"))

def run(job):
    jid, entry, variant, need = job
    tag = "sweep-" + safe(jid)
    seat2 = need if need else "01001"
    seats = ["01001"] if not need else [need]
    script = ["wait:3", "menu:cycle", "wait:2", "cycle:" + entry, "wait:2"] + (["prepare-variant:return", "wait:2"] if variant else []) + \
             ["prepare-add:" + s for s in seats] + ["wait:1", "capture:evidence/later/sweep/" + safe(jid) + "-prepare.png", "prepare-start", "wait:6",
              "autoplay:" + str(steps) + ",evidence/later/sweep/run-" + safe(jid) + ",fast", "wait:2",
              "capture:evidence/later/sweep/" + safe(jid) + "-table.png", "scan-english:" + safe(jid), "quit"]
    t = time.time()
    subprocess.run(["powershell", "-NoProfile", "-File", os.path.join(root, "tools", "dev", "rungame.ps1"), "-Tag", tag, "-Size", "1920x1080",
                    "-Script", ";".join(script), "-DataRoot", "qa-data/sweep5/" + safe(jid), "-TimeoutSec", str(60 + steps * 6)], cwd=root, capture_output=True)
    return jid, time.time() - t

with concurrent.futures.ThreadPoolExecutor(par) as ex:
    for jid, sec in ex.map(run, jobs): print("done", jid, int(sec), "s", flush=True)

summary = []
for jid, entry, variant, need in jobs:
    log = os.path.join(root, "evidence", "p0", "sweep-" + safe(jid) + ".log")
    text = open(log, encoding="utf-8", errors="replace").read() if os.path.exists(log) else ""
    tags = sorted(set(re.findall(r"AUTOPLAY \w+ (\w+) ->", text)))
    summary.append({"id": jid, "variant": variant, "needs": need,
        "started": "AUTOPLAY_START" in text, "inPlay": bool(re.search(r"AUTOPLAY InvestigationPhase PlayerWindowChooseOne", text)),
        "steps": len(re.findall(r"^AUTOPLAY \w+ ", text, re.M)), "stuck": re.findall(r"AUTOPLAY_STUCK[^\n]*", text)[:3],
        "end": (re.findall(r"AUTOPLAY_END[^\n]*", text) or [""])[0], "questionTags": tags,
        "errors": sorted(set(e[:160] for e in re.findall(r"ERROR: (?!Condition|WASAPI|[0-9]+ RID|Texture with|[0-9]+ resources)[^\n]*", text)))[:6],
        "english": len(re.findall(r"QA_ENGLISH", text)), "timeout": "TIMEOUT" in text})
json.dump(summary, open(os.path.join(out, "summary.json"), "w", encoding="utf-8"), ensure_ascii=False, indent=1)
print("summary", os.path.join(out, "summary.json"))
