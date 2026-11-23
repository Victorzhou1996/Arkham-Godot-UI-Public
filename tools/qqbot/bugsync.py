"""Bug feedback from the QQ bot's server: fetch it, and mark reports once they are fixed.

The bot (qqbot.service on the server, /opt/qqbot/qqbot.py) appends each "/bug ..." to /opt/qqbot/data/bugs.jsonl with
status "new"; its /list and /summary count only "new" rows as open. Closing a report = setting its status here
(fixed / duplicate / not-a-bug). Every night at 04:00 the server's bug_cleanup.py (qqbot-cleanup.timer) moves closed
rows to bugs-archive.jsonl so /list stays short; the bot numbers new reports by the highest number in both files.

Usage:
  python tools/qqbot/bugsync.py fetch
      Copies bugs.jsonl, bugs-archive.jsonl and the images (read-only) into inbox/bugs-server/ (bugs.jsonl there = both
      merged, sorted by number), and prints the reports not yet in inbox/bugs-server/ledger.json (the triage ledger).
      A report already moved to the archive can no longer be re-marked (mark reports it as missing).
  python tools/qqbot/bugsync.py mark <no>[,<no>...] <status> [note] [--apply]
      Sets those reports' status on the server (fixed / duplicate / not-a-bug / need-info …), with an optional note.
      Without --apply it only shows what would change. With --apply it keeps a backup next to the file
      (bugs.jsonl.bak-<time>) and replaces the file only if the bot did not append meanwhile (else it retries).

SSH as in daily_update.py: the key on the desktop, BatchMode. Nothing secret is printed.
"""
import json
import subprocess
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
OUT = ROOT / "inbox" / "bugs-server"
LEDGER = OUT / "ledger.json"
KEY = r"C:\Users\victo\Desktop\kaho.pem"
HOST = "ubuntu@<OVERSEAS_SERVER_IP>"
REMOTE = "/opt/qqbot/data"
SSH_OPTS = ["-i", KEY, "-o", "BatchMode=yes", "-o", "StrictHostKeyChecking=accept-new", "-o", "ConnectTimeout=20"]

# runs on the server (python3, as ubuntu): change the status of the given rows, keep a backup, replace atomically
REMOTE_MARK = r'''
import json, os, sys, time, shutil, base64, fcntl
p = "/opt/qqbot/data/bugs.jsonl"
marks, apply = json.loads(base64.b64decode(sys.argv[1]).decode("utf-8")), sys.argv[2] == "1"
lock = open("/opt/qqbot/data/bugs.lock", "a"); fcntl.flock(lock, fcntl.LOCK_EX)   # same lock as the bot and the cleanup
for attempt in range(10):
    size = os.stat(p).st_size
    lines = open(p, encoding="utf-8").read().splitlines(True)
    out, changed = [], []
    for ln in lines:
        if ln.strip():
            r = json.loads(ln); m = marks.get(str(r["no"]))
            if m and (r.get("status") != m["status"] or (m.get("note") and r.get("note") != m["note"])):
                changed.append([r["no"], r.get("status"), m["status"]])
                r["status"] = m["status"]; r["status_time"] = time.strftime("%Y-%m-%dT%H:%M:%S")
                if m.get("note"): r["note"] = m["note"]
                ln = json.dumps(r, ensure_ascii=False) + "\n"
        out.append(ln)
    missing = sorted(set(marks) - {str(json.loads(l)["no"]) for l in lines if l.strip()})
    if not apply or not changed:
        print(json.dumps({"apply": False, "changed": changed, "missing": missing}, ensure_ascii=False)); break
    tmp = p + ".tmp"
    with open(tmp, "w", encoding="utf-8") as f: f.write("".join(out))
    if os.stat(p).st_size != size:        # the bot appended a report meanwhile: start over
        os.remove(tmp); time.sleep(0.5); continue
    shutil.copy2(p, p + ".bak-" + time.strftime("%Y%m%d-%H%M%S"))
    os.replace(tmp, p)
    print(json.dumps({"apply": True, "changed": changed, "missing": missing}, ensure_ascii=False)); break
else:
    print(json.dumps({"error": "file kept changing, nothing written"}))
'''


def run(args: list[str], **kw) -> subprocess.CompletedProcess:
    return subprocess.run(args, check=True, timeout=180, **kw)


def fetch() -> int:
    (OUT / "bug_images").mkdir(parents=True, exist_ok=True)
    live, archive = OUT / "bugs-live.jsonl", OUT / "bugs-archive.jsonl"
    run(["scp", *SSH_OPTS, f"{HOST}:{REMOTE}/bugs.jsonl", str(live)])
    have_archive = run(["ssh", *SSH_OPTS, HOST, f"test -f {REMOTE}/bugs-archive.jsonl && echo yes || echo no"],
                       capture_output=True, text=True).stdout.strip() == "yes"
    if have_archive:
        run(["scp", *SSH_OPTS, f"{HOST}:{REMOTE}/bugs-archive.jsonl", str(archive)])
    have = {p.name for p in (OUT / "bug_images").iterdir()}
    names = run(["ssh", *SSH_OPTS, HOST, f"ls {REMOTE}/bug_images"], capture_output=True, text=True).stdout.split()
    want = [n for n in names if n not in have]
    if want:
        run(["scp", *SSH_OPTS, *[f"{HOST}:{REMOTE}/bug_images/{n}" for n in want], str(OUT / "bug_images")])
    read = lambda p: [json.loads(l) for l in p.read_text(encoding="utf-8").splitlines() if l.strip()] if p.exists() else []
    merged = {r["no"]: r for r in read(archive) if have_archive}
    merged.update({r["no"]: r for r in read(live)})   # a row in both (cleanup interrupted) counts as the live one
    rows = [merged[k] for k in sorted(merged)]
    (OUT / "bugs.jsonl").write_text("".join(json.dumps(r, ensure_ascii=False) + "\n" for r in rows), encoding="utf-8")
    seen = json.loads(LEDGER.read_text(encoding="utf-8")) if LEDGER.exists() else {}
    fresh = [r for r in rows if str(r["no"]) not in seen]
    by_status: dict[str, int] = {}
    for r in rows:
        by_status[r.get("status", "new")] = by_status.get(r.get("status", "new"), 0) + 1
    print(json.dumps({"total": len(rows), "by_status": by_status, "new_images": want,
                      "untriaged": [{"no": r["no"], "time": r["time"], "text": r.get("text", ""), "images": r.get("images", [])} for r in fresh]},
                     ensure_ascii=False, indent=1))
    return 0


def mark(nos: str, status: str, note: str, apply: bool) -> int:
    marks = {n.strip(): {"status": status, **({"note": note} if note else {})} for n in nos.split(",") if n.strip()}
    if not marks or not all(n.isdigit() for n in marks):
        print("numbers like 12 or 12,13", file=sys.stderr); return 2
    import base64   # the marks travel base64-encoded, so no shell quoting on the remote side touches the note
    arg = base64.b64encode(json.dumps(marks, ensure_ascii=False).encode("utf-8")).decode("ascii")
    r = run(["ssh", *SSH_OPTS, HOST, "python3", "-", arg, "1" if apply else "0"],
            input=REMOTE_MARK, capture_output=True, text=True, encoding="utf-8")
    print(r.stdout.strip() or r.stderr.strip())
    return 0


def main(argv: list[str]) -> int:
    sys.stdout.reconfigure(encoding="utf-8")
    if argv[:1] == ["fetch"]:
        return fetch()
    if argv[:1] == ["mark"] and len(argv) >= 3:
        apply = "--apply" in argv
        rest = [a for a in argv[1:] if a != "--apply"]
        return mark(rest[0], rest[1], rest[2] if len(rest) > 2 else "", apply)
    print(__doc__); return 2


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
