# Coach report (docs/AI-PLAY-PLAN-20261119.md §19): read verbose sim logs (--verbose: AI_TURN / AI_FIGHT lines) and find
# where the actions of each chapter go, which ones move nothing, and what separates the good campaigns from the bad.
#   python tools/ai/coach_report.py scratch/coach1-E.log scratch/coach1-S.log
import collections, json, os, re, statistics as st, sys

ROOT = os.path.join(os.path.dirname(__file__), "..", "..")
NAMES = {}
for f in ("core-cards.json",):
    try:
        for c in json.load(open(os.path.join(ROOT, "app", "assets", "data", f), encoding="utf-8")): NAMES[c["code"]] = c["name"]
    except OSError: pass
NAMES.update({"01113": "阁楼", "01114": "地窖"})


def name(code):
    return NAMES.get(code, code)


def pain_cause(p):
    """the cause of one SIM_PAIN line (sim --verbose): who attacked, which treachery, which place, which chaos token"""
    why, test = p.get("why", ""), p.get("test", "")
    for part in why.split("+"):
        if part.startswith("EnemyAttackSource:") or part.startswith("attack:"): return "敌人攻击 " + name(part.split(":", 1)[1])
    for part in why.split("+") + [test]:
        if part.startswith("TreacherySource:"): return "诡计 " + name(part.split(":", 1)[1])
    if why.startswith("ChaosTokenEffectSource:"): return "混沌标记 " + why.split(":", 1)[1]
    if why.startswith("LocationSource:"): return "地点 " + name(why.split(":", 1)[1])
    if why.startswith("AssetSource:") or why.startswith("EventSource:"): return "自己的牌 " + name(why.split(":", 1)[1])
    if p.get("new"): return "诡计 " + name(p["new"].split(",")[0])
    if p["phase"] == "InvestigationPhase" and p.get("loc") in ("01113", "01114"): return "进入" + name(p["loc"])
    if why: return "其他 " + why.split(":")[0]
    return "其他(%s)" % p["phase"].replace("Phase", "")

TURN = re.compile(r'^AI_TURN (\S+) act=(\S+) need=(\d+) team=(\d+)(.*?) doom=(\d+)/(\d+) ag=(\S+) hp=(-?\d+) san=(-?\d+) act=(\d) res=(\d+) → (.*?) \((-?[\d.]+)\)')


def kind(choice):
    for k, words in (("调查", ["调查"]), ("移动", ["移动"]), ("攻击", ["攻击"]), ("躲避", ["躲避"]), ("资源", ["获取1资源"]), ("抽牌", ["抽牌"]),
                     ("打出", ["打出"]), ("结束回合", ["结束回合"]), ("推进/能力", ["发动能力", "推进"]), ("谈判", ["谈判", "交涉"]), ("交战", ["交战"]),
                     ("撤退", ["撤退"])):
        if any(w in choice for w in words):
            return k
    return "其他"


def campaigns(path):
    """yield one campaign at a time: {'chapters': {code: [turns]}, 'end': campaignEnd json}"""
    cur = {"chapters": collections.OrderedDict(), "fights": collections.defaultdict(list), "pain": collections.defaultdict(list)}; ch = None; fights = []
    for line in open(path, encoding="utf-8", errors="replace"):
        if '"ScenarioStep"' in line:
            m = re.search(r'c(\d{5})', line); ch = m.group(1) if m else None
            if ch: cur["chapters"].setdefault(ch, [])
        elif line.startswith("SIM_PAIN") and ch:
            p = dict(kv.split("=", 1) for kv in line.split()[1:] if "=" in kv)
            cur["pain"][ch].append(p)
        elif line.startswith("AI_FIGHT"):
            fights.append(line)
        elif line.startswith("AI_TURN") and ch:
            m = TURN.match(line)
            if not m: continue
            g = m.groups()
            t = dict(name=g[0], act=g[1], need=int(g[2]), team=int(g[3]), extra=g[4], doom=int(g[5]), thr=int(g[6]), ag=g[7], hp=int(g[8]),
                     san=int(g[9]), left=int(g[10]), res=int(g[11]), choice=g[12], v=float(g[13]), kind=kind(g[12]))
            # the AI_FIGHT line of the chosen attack: same value
            if t["kind"] == "攻击":
                for f in reversed(fights):
                    fm = re.search(r'p=([\d.]+) dmg=(\d+) left=(\d+).* v=(-?[\d.]+)', f)
                    if fm and abs(float(fm.group(4)) - t["v"]) < 0.06:
                        t["p"] = float(fm.group(1)); t["dmg"] = int(fm.group(2)); t["eleft"] = int(fm.group(3)); break
            fights = []
            cur["chapters"][ch].append(t)
        elif line.startswith("SIM_CAMPAIGN_END"):
            cur["end"] = json.loads(line.split(" ", 2)[2]); yield cur
            cur = {"chapters": collections.OrderedDict(), "fights": collections.defaultdict(list), "pain": collections.defaultdict(list)}; ch = None


def rounds(turns):
    """split one chapter's turns into rounds (a turn with more actions left than the one before starts a new round)"""
    out = []; prev = -1
    for t in turns:
        if t["left"] > prev or not out: out.append([])
        out[-1].append(t); prev = t["left"]
    return out


def chapter_stats(turns):
    rs = rounds(turns)
    s = collections.Counter(t["kind"] for t in turns if t["left"] > 0)
    s["结束回合(还剩行动)"] = sum(1 for t in turns if t["kind"] == "结束回合" and t["left"] > 0)
    s["结束回合"] -= s["结束回合(还剩行动)"]
    inv_gain = []
    for a, b in zip(turns, turns[1:]):
        if a["kind"] == "调查" and a["act"] == b["act"]: inv_gain.append(1 if b["team"] > a["team"] else 0)
    # damage + horror from the start of one round to the start of the next (the enemy and mythos phases included)
    hp_loss = [max(0, a[0]["hp"] - b[0]["hp"]) + max(0, a[0]["san"] - b[0]["san"]) for a, b in zip(rs, rs[1:])]
    # rounds where nothing moved: same act, same team clues, doom went up
    stuck = 0
    for r1, r2 in zip(rs, rs[1:]):
        if r1[0]["act"] == r2[0]["act"] and r2[0]["team"] <= r1[0]["team"] and r2[0]["doom"] + (100 if r2[0]["ag"] != r1[0]["ag"] else 0) > r1[0]["doom"]:
            stuck += 1
    atk = [t for t in turns if t["kind"] == "攻击" and "p" in t]
    return dict(rounds=len(rs), actions=sum(s.values()), mix=s, inv_rate=(sum(inv_gain) / len(inv_gain)) if inv_gain else None, n_inv=len(inv_gain),
                pain_per_round=st.mean(hp_loss) if hp_loss else 0, stuck=stuck, atk=len(atk), atk_low=sum(1 for t in atk if t["p"] < 0.5),
                atk_p=st.mean([t["p"] for t in atk]) if atk else None, last=turns[-1] if turns else None)


def setn(end, key):
    return next((s["n"] for s in end["rec"]["sets"] if s["key"] == key), 0)


def main(paths):
    camps = [c for p in paths for c in campaigns(p)]
    print("战役", len(camps))
    names = {"01104": "第一章 聚会", "01120": "第二章 午夜假面", "01142": "第三章 吞噬者"}
    for ch in ("01104", "01120", "01142"):
        rows = []
        for c in camps:
            if ch not in c["chapters"] or not c["chapters"][ch]: continue
            s = chapter_stats(c["chapters"][ch]); e = c["end"]
            good = {"01104": "GhoulPriestIsStillAlive" not in e["rec"]["keys"],
                    "01120": setn(e, "CultistsWeInterrogated") >= 2,
                    "01142": any(k in x for x in e["rec"]["keys"] for k in ("Broken", "Repelled", "Sacrificed"))}[ch]
            rows.append((good, s))
        if not rows: continue
        print("\n==== %s  %d 局（好局 = %s：%d）" % (names[ch], len(rows), {"01104": "打死祭司", "01120": "审问≥2", "01142": "第三章赢"}[ch], sum(g for g, _ in rows)))
        for label, sel in (("全部", rows), ("好局", [r for r in rows if r[0]]), ("差局", [r for r in rows if not r[0]])):
            if not sel: continue
            ss = [s for _, s in sel]; n = len(ss)
            mix = collections.Counter()
            for s in ss: mix.update(s["mix"])
            tot = sum(mix.values())
            inv = [s["inv_rate"] for s in ss if s["inv_rate"] is not None]
            atkp = [s["atk_p"] for s in ss if s["atk_p"] is not None]
            print("  %s %d局: 回合 %.1f  每回合掉血+理智 %.2f  卡住的回合 %.2f  调查成功率 %s  攻击 %.1f次/局 平均成功率 %s 低于50%%的 %.1f次" % (
                label, n, st.mean(s["rounds"] for s in ss), st.mean(s["pain_per_round"] for s in ss), st.mean(s["stuck"] for s in ss),
                "%.2f" % st.mean(inv) if inv else "-", st.mean(s["atk"] for s in ss), "%.2f" % st.mean(atkp) if atkp else "-", st.mean(s["atk_low"] for s in ss)))
            print("     行动去向: " + "  ".join("%s %.0f%%" % (k, 100 * v / tot) for k, v in mix.most_common()))
        # where the chapter ended: act / agenda of the last turn
        ends = collections.Counter((s["last"]["act"], s["last"]["ag"], s["last"]["kind"]) for _, s in rows)
        print("  结束时（幕/密谋/最后一个行动）: " + ", ".join("%s/%s/%s×%d" % (a, g, k, n) for (a, g, k), n in ends.most_common(8)))
        # where the horror and damage of this chapter came from (SIM_PAIN lines)
        pains = [p for c in camps for p in c["pain"].get(ch, [])]
        if pains:
            n = len([c for c in camps if ch in c["chapters"]])
            for key, label in (("dsan", "恐惧"), ("dhp", "伤害")):
                by = collections.Counter()
                for p in pains: by[pain_cause(p)] += int(p[key])
                tot = sum(by.values())
                print("  %s来源（每局 %.2f）: " % (label, tot / max(1, n)) + "  ".join("%s %.2f" % (k, v / n) for k, v in by.most_common(10) if v))
        last = [s["last"] for _, s in rows]
        print("  最后一回合: 体力 %.1f 理智 %.1f 毁灭 %.1f/%.1f" % (st.mean(t["hp"] for t in last), st.mean(t["san"] for t in last), st.mean(t["doom"] for t in last), st.mean(t["thr"] for t in last)))


if __name__ == "__main__":
    main(sys.argv[1:])
