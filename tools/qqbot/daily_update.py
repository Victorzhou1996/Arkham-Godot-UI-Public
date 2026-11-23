"""Daily notice + upload. Runs every day at 12:00 (Windows scheduled task "QQBot-DailyUpdate").

1. Moves the items queued in pending.md into updates.md, each stamped with its time: "- [2026-11-20 14:30] text".
2. Builds the notice for the window "yesterday 12:00 .. today 12:00":
   - if there are updates in the window: lists them;
   - otherwise: "昨天没有更新内容，已经X天没有更新了" and the last update's date with its items.
3. Sends the notice to your private chat (private pushes work; group pushes are refused by the platform).
4. Uploads updates.md to the server (/opt/qqbot/updates.md), where the /update command reads it.

Needs the environment variables QQBOT_APPID and QQBOT_APPSECRET (set as user environment variables, not in files).
Log: tools/qqbot/data/daily_update.log. No model is called: the text is what was written in the files.
"""
import json
import re
import subprocess
import sys
from datetime import datetime, timedelta
from pathlib import Path

HERE = Path(__file__).resolve().parent
sys.path.insert(0, str(HERE))
from qqbot import DATA, Bot  # noqa: E402  (same folder: the bot's API client)

PENDING = HERE / "pending.md"
UPDATES = HERE / "updates.md"
LOG = DATA / "daily_update.log"
OPENIDS = DATA / "openids.json"
KEY = r"C:\Users\victo\Desktop\kaho.pem"
HOST = "ubuntu@<OVERSEAS_SERVER_IP>"
SSH_OPTS = ["-i", KEY, "-o", "BatchMode=yes", "-o", "StrictHostKeyChecking=accept-new", "-o", "ConnectTimeout=20"]
LEGACY = datetime(2026, 11, 20, 10, 50)   # entries written before stamps existed
HEADER = "# 近期更新（最新在上，每条以 \"- [时间] \" 开头；/update 和每日通知读取这里）"
PENDING_HEADER = "# 待发布（每行以 \"- \" 开头，可以不写时间；每天 12:00 会盖上时间、移入 updates.md 并清空本文件）"
STAMP = re.compile(r"^- \[(\d{4}-\d{2}-\d{2})(?: (\d{2}:\d{2}))?\] (.*)$")
BARE = re.compile(r"^- (.*)$")


def log(text: str) -> None:
    LOG.parent.mkdir(parents=True, exist_ok=True)
    line = f"{datetime.now():%Y-%m-%d %H:%M:%S} {text}"
    print(line, flush=True)
    with LOG.open("a", encoding="utf-8") as f:
        f.write(line + "\n")


def read_entries(path: Path, now: datetime) -> list[tuple[datetime, str]]:
    """Stamped lines keep their time; bare '- text' lines get `now` (pending) or LEGACY (old updates.md)."""
    if not path.exists():
        return []
    out = []
    for line in path.read_text(encoding="utf-8").splitlines():
        m = STAMP.match(line.strip())
        if m:
            dt = datetime.strptime(f"{m.group(1)} {m.group(2) or '00:00'}", "%Y-%m-%d %H:%M")
            out.append((dt, m.group(3)))
            continue
        b = BARE.match(line.strip())
        if b:
            out.append((now if path == PENDING else LEGACY, b.group(1)))
    return out


def write_updates(entries: list[tuple[datetime, str]]) -> None:
    entries = sorted(entries, key=lambda e: e[0], reverse=True)[:60]
    lines = [HEADER] + [f"- [{dt:%Y-%m-%d %H:%M}] {text}" for dt, text in entries]
    UPDATES.write_text("\n".join(lines) + "\n", encoding="utf-8")


def publish_pending(now: datetime) -> int:
    pending = read_entries(PENDING, now)
    if not pending:
        log("nothing pending")
        return 0
    write_updates(read_entries(UPDATES, now) + pending)
    PENDING.write_text(PENDING_HEADER + "\n", encoding="utf-8")
    log(f"published {len(pending)} pending item(s)")
    return len(pending)


def bullets_by_batch(items: list[tuple[datetime, str]]) -> list[str]:
    """Items published together share one time; they are listed under one 'MM-DD' line (no time, no year)."""
    lines, current = [], None
    for dt, text in sorted(items, key=lambda e: e[0], reverse=True):
        if dt != current:
            current = dt
            lines.append(f"{dt:%m-%d}：")
        lines.append(f"- {text}")
    return lines


def window_day(dt: datetime):
    """The 12:00-to-12:00 window an update falls in is labelled by the day it ends (12:00 of that day)."""
    from datetime import time as clock
    return dt.date() if dt.time() < clock(12, 0) else dt.date() + timedelta(days=1)


def build_notice(now: datetime, entries: list[tuple[datetime, str]]) -> str:
    start = (now - timedelta(days=1)).replace(hour=12, minute=0, second=0, microsecond=0)
    end = now.replace(hour=12, minute=0, second=0, microsecond=0)
    in_window = [e for e in entries if start < e[0] <= end]
    if in_window:
        lines = [f"【每日更新】{start:%m-%d} 至 {end:%m-%d}"] + bullets_by_batch(in_window)
    elif entries:
        last_dt = max(dt for dt, _ in entries)
        # days stopped = empty 12:00-to-12:00 windows since the one the last update fell in (that window ends at 12:00 of window_day)
        days = (end.date() - window_day(last_dt)).days
        last_batch = [e for e in entries if e[0] == last_dt]
        lines = [f"昨天没有更新内容，已经{days}天没有更新了", f"上次的更新在{last_dt:%m-%d}："] + [f"- {t}" for _, t in last_batch]
    else:
        lines = ["昨天没有更新内容，目前还没有任何更新记录。"]
    text = "\n".join(lines)
    return text if len(text) <= 900 else text[:880] + "\n…（其余见 /update）"


def owner_openid() -> str:
    ids = json.loads(OPENIDS.read_text(encoding="utf-8")) if OPENIDS.exists() else {}
    if len(ids) != 1:
        raise RuntimeError(f"expected exactly one private chat openid in openids.json, found {len(ids)}")
    return next(iter(ids))


def user_env(name: str) -> str:
    """The process environment, or else the Windows user environment (set with setx; a process started before setx misses it)."""
    import os
    value = os.environ.get(name)
    if value:
        return value
    import winreg
    with winreg.OpenKey(winreg.HKEY_CURRENT_USER, "Environment") as key:
        return winreg.QueryValueEx(key, name)[0]


def send_notice(text: str) -> None:
    bot = Bot(user_env("QQBOT_APPID"), user_env("QQBOT_APPSECRET"))
    bot.send_c2c(owner_openid(), text)
    log("notice sent to private chat")


def upload() -> None:
    subprocess.run(["scp", *SSH_OPTS, str(UPDATES), f"{HOST}:/tmp/updates.md"], check=True, timeout=120)
    subprocess.run(["ssh", *SSH_OPTS, HOST, "sudo mv /tmp/updates.md /opt/qqbot/updates.md"], check=True, timeout=120)
    log("uploaded updates.md to the server")


def main() -> int:
    now = datetime.now()
    try:
        publish_pending(now)
        entries = read_entries(UPDATES, now)
        send_notice(build_notice(now, entries))
        upload()
        return 0
    except Exception as e:  # the reason goes to the log; the scheduled task shows a non-zero exit
        log(f"FAILED: {type(e).__name__}: {e}")
        return 1


if __name__ == "__main__":
    sys.exit(main())
