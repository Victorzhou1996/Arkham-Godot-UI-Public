"""Nightly cleanup of the QQ bot's bug list: move closed reports out of bugs.jsonl so /list stays short.

Runs on the server (systemd qqbot-cleanup.timer, every day at 04:00 server time = Beijing time):
  /opt/qqbot/.venv/bin/python /opt/qqbot/bug_cleanup.py            move closed reports to bugs-archive.jsonl
  /opt/qqbot/.venv/bin/python /opt/qqbot/bug_cleanup.py --dry-run  only print what would move

Closed = status fixed / duplicate / not-a-bug (set by tools/qqbot/bugsync.py mark). Everything else stays.
Nothing is deleted: the rows are appended to data/bugs-archive.jsonl, and a backup of bugs.jsonl is kept
(bugs.jsonl.bak-cleanup-<time>). The bot numbers new reports by the highest number in both files, so numbers never repeat.
Log: data/cleanup.log.
"""
import json
import os
import shutil
import sys
import time
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
from qqbot import BUGS, BUGS_ARCHIVE, DATA, bugs_locked, read_reports  # noqa: E402  (same folder)

CLOSED = {"fixed", "duplicate", "not-a-bug"}
LOG = DATA / "cleanup.log"


def log(msg: str) -> None:
    with LOG.open("a", encoding="utf-8") as f:
        f.write(time.strftime("%Y-%m-%d %H:%M:%S ") + msg + "\n")


def main(argv: list[str]) -> int:
    dry = "--dry-run" in argv
    with bugs_locked():
        rows = read_reports(BUGS)
        closed = [r for r in rows if r.get("status", "new") in CLOSED]
        keep = [r for r in rows if r.get("status", "new") not in CLOSED]
        nos = [r["no"] for r in closed]
        if dry or not closed:
            print(json.dumps({"dry_run": dry, "move": nos, "keep": len(keep)}, ensure_ascii=False))
            if not dry:
                log("nothing to move")
            return 0
        shutil.copy2(BUGS, BUGS.with_name("bugs.jsonl.bak-cleanup-" + time.strftime("%Y%m%d-%H%M%S")))
        stamp = time.strftime("%Y-%m-%dT%H:%M:%S")
        with BUGS_ARCHIVE.open("a", encoding="utf-8") as f:
            for r in closed:
                f.write(json.dumps({**r, "archived_time": stamp}, ensure_ascii=False) + "\n")
        tmp = BUGS.with_suffix(".jsonl.tmp")
        tmp.write_text("".join(json.dumps(r, ensure_ascii=False) + "\n" for r in keep), encoding="utf-8")
        os.replace(tmp, BUGS)
    log(f"moved {len(nos)} closed to archive: {nos}; {len(keep)} left")
    print(json.dumps({"moved": nos, "left": len(keep)}, ensure_ascii=False))
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
