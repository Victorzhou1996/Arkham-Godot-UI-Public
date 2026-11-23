"""Server-side bug digest: runs on the bot's server every 2 hours (systemd timer qqbot-digest.timer), needs nothing from
Yunke's PC. It reads data/bugs.jsonl (written by qqbot.py receive), takes the reports after the last digest, sorts them
by keyword into priority groups (crash / save first), flags likely duplicates of still-open reports, writes the digest to
data/digests/<time>.md (and data/digest_latest.md), and pushes it to the owner's private chat.

No model is called; the grouping is keyword rules. Fixing, verifying and marking a report fixed stay on the PC
(scheduled task qq-bug-triage-2h, tools/qqbot/bugsync.py), which catches up when the PC is back.

Usage (in /opt/qqbot, with QQBOT_APPID / QQBOT_APPSECRET from qqbot.env):
  python bug_digest.py            new reports since the last digest -> digest file + private push (nothing new: no push)
  python bug_digest.py --dry-run [N]  print the digest only (of the last N, if given): no file, no push, the position is not moved
  python bug_digest.py --test N   push a digest of the last N reports marked as a test (the position is not moved)
State: data/digest_state.json {"last_no": n}. Owner: data/owner.json {"openid": "..."}. Log: data/digest.log.
The position moves only after the push went through, so a failed push is retried next run.
"""
import difflib
import json
import os
import sys
from datetime import datetime
from pathlib import Path

HERE = Path(__file__).resolve().parent
sys.path.insert(0, str(HERE))
from qqbot import BUGS, BUGS_ARCHIVE, DATA, Bot, read_reports  # noqa: E402  (same folder: the bot's API client and paths)

STATE = DATA / "digest_state.json"
OWNER = DATA / "owner.json"
DIGESTS = DATA / "digests"
LOG = DATA / "digest.log"

# first match wins, so the more urgent groups come first
GROUPS = [
    ("崩溃/坏档", ("崩溃", "闪退", "坏档", "存档", "卡死", "报错", "无法启动", "打不开", "Exception", "error", "continuation")),
    ("操作", ("太难点", "点不到", "点不了", "点不中", "按钮", "拖", "误删", "删掉", "删光", "找不到")),
    ("显示/界面", ("显示", "看不到", "看到", "文字", "界面", "缩放", "名字", "提示", "乱码")),
    ("建议", ("建议", "希望", "能不能", "可以加", "混池")),
    ("规则/效果", ("技能", "能力", "效果", "判定", "检定", "标记", "攻击", "取消", "伤害", "恐惧", "弱点", "触发", "揭示", "结算")),
]


def log(text: str) -> None:
    DATA.mkdir(parents=True, exist_ok=True)
    with LOG.open("a", encoding="utf-8") as f:
        f.write(f"{datetime.now():%Y-%m-%d %H:%M:%S} {text}\n")


def rows() -> list[dict]:
    # live list + the nightly archive of closed reports (bug_cleanup.py), so totals stay right
    return sorted(read_reports(BUGS_ARCHIVE) + read_reports(BUGS), key=lambda r: r["no"])


def group_of(text: str) -> str:
    return next((name for name, words in GROUPS if any(w.lower() in text.lower() for w in words)), "其他")


def duplicate_of(r: dict, earlier: list[dict]) -> int | None:
    t = r.get("text") or ""
    if len(t) < 6:
        return None
    best = max(earlier, key=lambda e: difflib.SequenceMatcher(None, t, e.get("text") or "").ratio(), default=None)
    if best and difflib.SequenceMatcher(None, t, best.get("text") or "").ratio() >= 0.6:
        return best["no"]
    return None


def digest(new: list[dict], all_rows: list[dict], test: bool = False) -> str:
    open_count = sum(1 for r in all_rows if r.get("status", "new") == "new")
    head = ("【测试】" if test else "") + f"新反馈 {len(new)} 条（共 {len(all_rows)} 条，未处理 {open_count} 条）"
    by_group: dict[str, list[str]] = {}
    for r in new:
        text = r.get("text") or "（只有图片）"
        earlier = [e for e in all_rows if e["no"] < r["no"] and e.get("status", "new") == "new"]
        dup = duplicate_of(r, earlier)
        tags = ("[手机]" if "手机" in text else "") + (f"[图{len(r.get('images') or [])}]" if r.get("images") else "") + (f"[疑似重复#{dup}]" if dup else "")
        by_group.setdefault(group_of(text), []).append(f"#{r['no']} {r['time'][5:16].replace('T', ' ')} {tags}{text[:40]}")
    lines = [head]
    for name, _ in GROUPS + [("其他", ())]:
        if name in by_group:
            lines.append(f"■ {name}（{len(by_group[name])}）")
            lines += by_group[name]
    lines.append("修复与验证由电脑上的定时任务处理，修好后会标记为 fixed。")
    text = "\n".join(lines)
    return text if len(text) <= 900 else text[:880] + "\n…（其余见 /list）"


def push(text: str) -> None:
    openid = json.loads(OWNER.read_text(encoding="utf-8"))["openid"]
    Bot(os.environ["QQBOT_APPID"], os.environ["QQBOT_APPSECRET"]).send_c2c(openid, text)


def main(argv: list[str]) -> int:
    all_rows = rows()
    if "--test" in argv:
        n = int(argv[argv.index("--test") + 1]) if len(argv) > argv.index("--test") + 1 else 3
        push(digest(all_rows[-n:], all_rows, test=True)); log(f"test push of the last {n}"); print("test pushed"); return 0
    state = json.loads(STATE.read_text(encoding="utf-8")) if STATE.exists() else {"last_no": max((r["no"] for r in all_rows), default=0)}
    new = [r for r in all_rows if r["no"] > state["last_no"]]
    if "--dry-run" in argv:
        i = argv.index("--dry-run")
        if len(argv) > i + 1 and argv[i + 1].isdigit():   # --dry-run N: show the digest of the last N
            new = all_rows[-int(argv[i + 1]):]
        print(digest(new, all_rows) if new else f"没有新反馈（上次到 #{state['last_no']}）"); return 0
    if not STATE.exists():   # first run: start from now, the existing reports were already summarized by hand
        STATE.write_text(json.dumps(state), encoding="utf-8"); log(f"start at #{state['last_no']}"); return 0
    if not new:
        log("nothing new"); return 0
    text = digest(new, all_rows)
    DIGESTS.mkdir(parents=True, exist_ok=True)
    stamp = datetime.now().strftime("%Y%m%d-%H%M")
    (DIGESTS / f"{stamp}.md").write_text(text + "\n", encoding="utf-8")
    (DATA / "digest_latest.md").write_text(text + "\n", encoding="utf-8")
    try:
        push(text)
    except Exception as e:  # kept for the next run; the reason goes to the log
        log(f"push FAILED ({len(new)} new): {type(e).__name__}: {e}"); return 1
    STATE.write_text(json.dumps({"last_no": max(r["no"] for r in new)}), encoding="utf-8")
    log(f"pushed {len(new)} new (#{new[0]['no']}..#{new[-1]['no']})")
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
