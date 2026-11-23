"""QQ official bot: a WebSocket receiver (so the bot has a connected service) and a sender.

Usage:
  python tools/qqbot/qqbot.py receive
      Connects to the QQ Open Platform gateway and keeps running. Private messages (C2C) and group @-messages
      are logged to tools/qqbot/data/receiver.log, and their senders' openid are saved in data/openids.json /
      data/groups.json so later sends know where to go.
  python tools/qqbot/qqbot.py send c2c <user_openid> "text"
  python tools/qqbot/qqbot.py send group <group_openid> "text"

Credentials come only from the environment: QQBOT_APPID and QQBOT_APPSECRET. Nothing secret is written to disk or printed.
Endpoints and the intent bit follow the QQ Open Platform bot API (v2); verify against the official docs if they change.
"""
import asyncio
import json
import os
import sys
import time
import urllib.error
import urllib.request
from datetime import datetime
from pathlib import Path

import websockets

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
        try:
            reply = await asyncio.to_thread(bot.send_group, group, "收到，进度推送机器人已在线。", d.get("id"))
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
    if len(argv) == 4 and argv[0] == "send":
        kind, target, text = argv[1], argv[2], argv[3]
        fn = bot.send_c2c if kind == "c2c" else bot.send_group
        print(json.dumps(fn(target, text), ensure_ascii=False))
        return 0
    print(__doc__)
    return 2


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
