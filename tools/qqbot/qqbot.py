"""QQ official bot: a WebSocket receiver (so the bot has a connected service) and a sender.

Usage:
  python tools/qqbot/qqbot.py receive
      Connects to the QQ Open Platform gateway and keeps running. Private messages (C2C) and group @-messages
      are logged to tools/qqbot/data/receiver.log, and their senders' openid are saved in data/openids.json /
      data/groups.json so later sends know where to go.
  python tools/qqbot/qqbot.py push "text"          (to the latest group the receiver has seen)
  python tools/qqbot/qqbot.py send c2c <user_openid> "text"
  python tools/qqbot/qqbot.py send group <group_openid> "text"

Credentials come only from the environment: QQBOT_APPID and QQBOT_APPSECRET. Nothing secret is written to disk or printed.
Endpoints and the intent bit follow the QQ Open Platform bot API (v2); verify against the official docs if they change.
"""
import asyncio
import contextlib
import json
import os
import sys
import time
import urllib.error
import urllib.request
from datetime import datetime
from pathlib import Path

import websockets

try:
    import fcntl   # the bot runs on Linux; the lock is skipped where fcntl does not exist
except ImportError:
    fcntl = None

API = "https://api.sgroup.qq.com"
TOKEN_URL = "https://bots.qq.com/app/getAppAccessToken"
INTENTS = 1 << 25   # GROUP_AND_C2C_EVENT: group @-messages and private messages
DATA = Path(__file__).resolve().parent / "data"
LOG = DATA / "receiver.log"


def log(text: str) -> None:
    DATA.mkdir(parents=True, exist_ok=True)
    line = f"{datetime.now():%Y-%m-%d %H:%M:%S} {text}"
    print(line, flush=True)
    with LOG.open("a", encoding="utf-8") as f:
        f.write(line + "\n")


def http(method: str, url: str, body=None, headers=None) -> dict:
    data = json.dumps(body).encode("utf-8") if body is not None else None
    req = urllib.request.Request(url, data=data, headers={"Content-Type": "application/json", **(headers or {})}, method=method)
    try:
        with urllib.request.urlopen(req, timeout=20) as r:
            raw = r.read().decode("utf-8")
            return json.loads(raw) if raw else {}
    except urllib.error.HTTPError as e:
        # the platform's error text is useful and carries no secret
        raise RuntimeError(f"HTTP {e.code} {url}: {e.read().decode('utf-8', 'replace')[:300]}") from None


class Bot:
    def __init__(self, app_id: str, secret: str):
        self.app_id = app_id
        self.secret = secret
        self.token = None
        self.token_exp = 0.0

    def access_token(self) -> str:
        if self.token and time.time() < self.token_exp - 300:
            return self.token
        r = http("POST", TOKEN_URL, {"appId": self.app_id, "clientSecret": self.secret})
        self.token = r["access_token"]
        self.token_exp = time.time() + int(r.get("expires_in", 7200))
        return self.token

    def auth(self) -> dict:
        return {"Authorization": f"QQBot {self.access_token()}", "X-Union-Appid": self.app_id}

    def send_c2c(self, openid: str, text: str, msg_id: str | None = None) -> dict:
        body = {"content": text, "msg_type": 0}
        if msg_id:   # a reply to a message the user just sent (passive reply)
            body.update(msg_id=msg_id, msg_seq=1)
        return http("POST", f"{API}/v2/users/{openid}/messages", body, self.auth())

    def send_group(self, group_openid: str, text: str, msg_id: str | None = None) -> dict:
        body = {"content": text, "msg_type": 0}
        if msg_id:
            body.update(msg_id=msg_id, msg_seq=1)
        return http("POST", f"{API}/v2/groups/{group_openid}/messages", body, self.auth())


def remember(name: str, key: str, info: dict) -> None:
    path = DATA / f"{name}.json"
    seen = json.loads(path.read_text(encoding="utf-8")) if path.exists() else {}
    seen[key] = {**info, "last_seen": datetime.now().isoformat(timespec="seconds")}
    path.write_text(json.dumps(seen, ensure_ascii=False, indent=2), encoding="utf-8")


BUGS = DATA / "bugs.jsonl"
# closed reports (fixed / duplicate / not-a-bug) are moved here every night by bug_cleanup.py, so /list stays short;
# numbers keep counting across both files
BUGS_ARCHIVE = DATA / "bugs-archive.jsonl"
BUGS_LOCK = DATA / "bugs.lock"


def read_reports(path: Path) -> list[dict]:
    return [json.loads(line) for line in path.open(encoding="utf-8") if line.strip()] if path.exists() else []


@contextlib.contextmanager
def bugs_locked():
    """Hold the lock that bug_cleanup.py and bugsync's remote mark also take before rewriting bugs.jsonl."""
    DATA.mkdir(parents=True, exist_ok=True)
    with open(BUGS_LOCK, "a") as f:
        if fcntl:
            fcntl.flock(f, fcntl.LOCK_EX)
        try:
            yield
        finally:
            if fcntl:
                fcntl.flock(f, fcntl.LOCK_UN)


BUG_IMAGES = DATA / "bug_images"
IMAGE_EXT = {"image/jpeg": ".jpg", "image/png": ".png", "image/gif": ".gif", "image/webp": ".webp"}
MAX_IMAGE_BYTES = 10 * 1024 * 1024


def fetch_image(att: dict, stem: Path) -> str:
    """Download one image attachment as stem + extension; returns its path relative to DATA."""
    url = att["url"]
    if url.startswith("//"):
        url = "https:" + url
    ext = IMAGE_EXT.get((att.get("content_type") or "").lower(), ".jpg")
    req = urllib.request.Request(url, headers={"User-Agent": "qqbot"})
    with urllib.request.urlopen(req, timeout=30) as r:
        data = r.read(MAX_IMAGE_BYTES + 1)
    if len(data) > MAX_IMAGE_BYTES:
        raise RuntimeError("image larger than 10 MB")
    path = stem.with_suffix(ext)
    path.write_bytes(data)
    return path.relative_to(DATA).as_posix()


def record_bug(source: str, who: str, text: str, attachments: list[dict]) -> int:
    """Append one feedback line to bugs.jsonl and save its images under data/bug_images; returns its number.
    Nothing is sent back to the group except a short ack."""
    DATA.mkdir(parents=True, exist_ok=True)
    # under the lock from numbering to append, so the nightly cleanup or a remote mark never rewrites the file in between
    with bugs_locked():
        number = max((r["no"] for r in read_reports(BUGS) + read_reports(BUGS_ARCHIVE)), default=0) + 1
        images, skipped = [], []
        for i, att in enumerate(attachments, 1):
            url = att.get("url", "")
            if not (att.get("content_type") or "").lower().startswith("image/"):
                skipped.append({"url": url, "reason": "not an image"})
                continue
            try:
                BUG_IMAGES.mkdir(parents=True, exist_ok=True)
                images.append(fetch_image(att, BUG_IMAGES / f"{number:04d}-{i}"))
            except Exception as e:  # keep the URL so the report is not lost; the reason goes to the log
                skipped.append({"url": url, "reason": f"download failed: {e}"})
                log(f"bug #{number}: image {i} not saved: {e}")
        row = {"no": number, "time": datetime.now().isoformat(timespec="seconds"), "source": source,
               "reporter_openid": who, "text": text, "images": images, "skipped": skipped, "status": "new"}
        with BUGS.open("a", encoding="utf-8") as f:
            f.write(json.dumps(row, ensure_ascii=False) + "\n")
    return number


def bug_text(content: str) -> str | None:
    """'/bug xxx' (or '/bug' alone) -> 'xxx'; anything else -> None."""
    c = content.strip()
    if c.lower() == "/bug":
        return ""
    if c.lower().startswith("/bug ") or c.lower().startswith("/bug\n"):
        return c[4:].strip()
    return None


def list_bugs(arg: str) -> str:
    """'/list' -> the 10 newest reports still in bugs.jsonl; '/list N' -> the N newest (1-20). Closed reports are moved
    to the archive every night at 04:00, so the list shows what is open (plus what was closed since then)."""
    rows, archived = read_reports(BUGS), len(read_reports(BUGS_ARCHIVE))
    if not rows and not archived:
        return "还没有反馈记录。"
    n = int(arg) if arg.isdigit() else 10
    n = max(1, min(n, 20))
    new_count = sum(1 for r in rows if r.get("status") == "new")
    lines = [f"共 {len(rows) + archived} 条，未处理 {new_count} 条，已处理归档 {archived} 条。最近 {min(n, len(rows))} 条："]
    for r in reversed(rows[-n:]):
        text = (r.get("text") or "（只有图片）")[:20]
        lines.append(f"#{r['no']} [{r.get('status', 'new')}] {r['time'][5:16]} {text}")
    return "\n".join(lines)


UPDATES = Path(__file__).resolve().parent / "updates.md"


def latest_updates(limit: int = 6) -> str:
    """'/update' -> the newest entries of updates.md, grouped by batch with 'MM-DD' only, trimmed to fit one QQ message."""
    if not UPDATES.exists():
        return "还没有更新记录。"
    import re
    stamp = re.compile(r"^- \[(\d{4}-\d{2}-\d{2} \d{2}:\d{2})\] (.*)$")
    rows = []
    for line in UPDATES.read_text(encoding="utf-8").splitlines():
        m = stamp.match(line.strip())
        if m:
            rows.append((m.group(1), m.group(2)))
    lines, current = ["近期更新："], None
    for when, text in rows[:limit]:
        if when != current:
            current = when
            lines.append(f"{when[5:10]}：")
        lines.append(f"- {text}")
    return "\n".join(lines)[:600]


TRAIN = Path(__file__).resolve().parent / "train.md"

# keyword rules for /summary: a rough sort by module, no model call; the first matching module wins
BUG_MODULES = [
    ("地图/连线", ("地图", "连线", "箭头", "地点", "缩放")),
    ("卡牌/手牌", ("卡", "手牌", "牌库", "牌组")),
    ("战斗/检定", ("伤害", "检定", "分配", "攻击", "战斗", "恐惧")),
    ("音乐/音效", ("音乐", "音效", "声音", "切歌")),
    ("文字/翻译", ("字体", "文字", "英文", "翻译", "汉化")),
    ("界面/动画", ("动画", "按钮", "界面", "显示", "弹窗", "提示")),
]


def train_status() -> str:
    """'/train' -> the training notes in train.md (written by hand, so not live)."""
    if not TRAIN.exists():
        return "还没有训练进度记录。"
    return TRAIN.read_text(encoding="utf-8").strip()[:600]


def bug_progress() -> str:
    """Plain @ reply: '收到。目前bug列表a/b', a = reports marked fixed, b = all reports."""
    rows = read_reports(BUGS) + read_reports(BUGS_ARCHIVE)
    fixed = sum(1 for r in rows if r.get("status") == "fixed")
    return f"收到。目前bug列表{fixed}/{len(rows)}"


def summarize_bugs() -> str:
    """'/summary' -> counts by module (keyword rules) and the newest open reports."""
    rows = read_reports(BUGS) + read_reports(BUGS_ARCHIVE)
    if not rows:
        return "还没有反馈记录。"
    open_rows = sorted((r for r in rows if r.get("status", "new") == "new"), key=lambda r: r["no"])
    counts: dict[str, int] = {}
    for r in open_rows:
        text = r.get("text") or ""
        module = next((name for name, words in BUG_MODULES if any(w in text for w in words)), "其他")
        counts[module] = counts.get(module, 0) + 1
    lines = [f"反馈共 {len(rows)} 条，未处理 {len(open_rows)} 条。", "未处理，按关键词分类："]
    lines += [f"- {name}：{n} 条" for name, n in sorted(counts.items(), key=lambda kv: -kv[1])] or ["- 无"]
    lines.append("最近 5 条未处理：")
    for r in open_rows[-5:][::-1]:
        lines.append(f"#{r['no']} {(r.get('text') or '（只有图片）')[:20]}")
    return "\n".join(lines)[:600]


async def on_dispatch(bot: Bot, event: str, d: dict) -> None:
    if event == "READY":
        log("gateway ready (identified)")
    elif event == "C2C_MESSAGE_CREATE":
        openid = d["author"]["user_openid"]
        content = (d.get("content") or "").strip()
        remember("openids", openid, {"text": content[:80]})
        log(f"private message from openid={openid} text={content[:80]!r}")
        try:
            reply = await asyncio.to_thread(bot.send_c2c, openid, "收到。进度推送已连上，之后会在这里发项目进度。", d.get("id"))
            log(f"ack sent: {reply.get('id', reply)}")
        except Exception as e:
            log(f"ack failed: {e}")
    elif event == "GROUP_AT_MESSAGE_CREATE":
        group = d["group_openid"]
        member = d["author"].get("member_openid", "")
        content = (d.get("content") or "").strip()
        remember("groups", group, {"last_member": member, "text": content[:80]})
        log(f"group @ message group={group} member={member} text={content[:80]!r}")
        reply_text = bug_progress()
        body = bug_text(content)
        if content.strip().lower().startswith("/list"):
            reply_text = list_bugs(content.strip()[5:].strip())
            log("list requested")
            body = None
        elif content.strip().lower().startswith("/update"):
            reply_text = latest_updates()
            log("update requested")
            body = None
        elif content.strip().lower().startswith("/train"):
            reply_text = train_status()
            log("train requested")
            body = None
        elif content.strip().lower().startswith("/summary"):
            reply_text = summarize_bugs()
            log("summary requested")
            body = None
        if body is not None:
            atts = [a for a in d.get("attachments", []) if a.get("url")]
            if not body and not atts:
                reply_text = "请在 /bug 后面写上问题描述，例如：/bug 地图缩放后地点卡重叠"
            else:
                n = await asyncio.to_thread(record_bug, "group:" + group, member, body, atts)
                reply_text = f"已记录第 {n} 条反馈，谢谢！"
                log(f"bug #{n} recorded from member={member}")
        try:
            reply = await asyncio.to_thread(bot.send_group, group, reply_text, d.get("id"))
            log(f"group ack sent: {reply.get('id', reply)}")
        except Exception as e:
            log(f"group ack failed: {e}")


async def receive(bot: Bot) -> None:
    while True:
        try:
            gateway = http("GET", f"{API}/gateway", None, bot.auth())["url"]
            async with websockets.connect(gateway, max_size=None) as ws:
                seq = None
                heartbeat = None
                async for raw in ws:
                    msg = json.loads(raw)
                    if msg.get("s") is not None:
                        seq = msg["s"]
                    op = msg.get("op")
                    if op == 10:   # hello: start heartbeats, then identify
                        interval = msg["d"]["heartbeat_interval"] / 1000

                        async def beat():
                            while True:
                                await asyncio.sleep(interval)
                                await ws.send(json.dumps({"op": 1, "d": seq}))
                        heartbeat = asyncio.create_task(beat())
                        await ws.send(json.dumps({"op": 2, "d": {"token": f"QQBot {bot.access_token()}", "intents": INTENTS, "shard": [0, 1]}}))
                    elif op == 0:
                        await on_dispatch(bot, msg["t"], msg["d"])
                    elif op in (7, 9):   # reconnect requested / invalid session: reconnect from the top
                        log(f"gateway asked to reconnect (op {op})")
                        break
                    # op 11: heartbeat ack, nothing to do
                if heartbeat:
                    heartbeat.cancel()
        except Exception as e:
            log(f"connection error: {type(e).__name__}: {e}")
        await asyncio.sleep(5)


def main(argv: list[str]) -> int:
    app_id = os.environ.get("QQBOT_APPID", "")
    secret = os.environ.get("QQBOT_APPSECRET", "")
    if not app_id or not secret:
        print("QQBOT_APPID and QQBOT_APPSECRET must be set in the environment", file=sys.stderr)
        return 2
    bot = Bot(app_id, secret)
    if argv[:1] == ["receive"]:
        asyncio.run(receive(bot))
        return 0
    if argv[:1] == ["push"] and len(argv) == 2:
        # push <text>: to the most recently seen group (groups.json, written by the receiver)
        groups_file = DATA / "groups.json"
        if not groups_file.exists():
            print("no group seen yet: @ the bot once in the group first", file=sys.stderr)
            return 2
        groups = json.loads(groups_file.read_text(encoding="utf-8"))
        latest = max(groups.items(), key=lambda kv: kv[1].get("last_seen", ""))[0]
        print(json.dumps(bot.send_group(latest, argv[1]), ensure_ascii=False))
        return 0
    if len(argv) == 4 and argv[0] == "send":
        kind, target, text = argv[1], argv[2], argv[3]
        fn = bot.send_c2c if kind == "c2c" else bot.send_group
        print(json.dumps(fn(target, text), ensure_ascii=False))
        return 0
    print(__doc__)
    return 2


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
