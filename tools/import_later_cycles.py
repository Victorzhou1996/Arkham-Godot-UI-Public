# Every later campaign, return-to, side story and fan-made scenario the rules core starts (11-22, "后续循环接入").
# Read-only sources (0712 html build, classic-fast-20261117):
#   cards  frontend/public/cards/cards_zh-cn.json (official zh-cn) and frontend/public/l10n/native-homebrew-cards-*.json
#   names  frontend/src/locales/zh-cn/campaignNames.json, scenarioNames.json; homebrew/<dir>/locales/zh-cn
#   lists  frontend/src/arkham/data/campaigns.json, <campaign>.json, side-stories.json; homebrew/<dir>/campaign.json, scenarios.json
#   texts  frontend/src/locales/zh-cn/<campaign>/ (base.json + scenarios/*.json), standalone/, top-level json; homebrew locales
#   starts evidence/probe-starts-*/starts.json (StartsProbe: what the core really opens)
# Writes (new files; locale-zh-extra.json only gains keys it lacks, backup first):
#   app/assets/data/<stem>-cards.json, app/assets/data/campaigns-catalog.json, app/assets/data/locale-zh-extra.json
# Usage: python -I tools/import_later_cycles.py "<Arkham UI root>" "<0712 root>" "<starts.json>"
import sys, os, re, json, glob, shutil

root, web, starts_file = sys.argv[1:4]
F = os.path.join(web, "classic-fast-20261117", "frontend")
L = os.path.join(F, "src", "locales", "zh-cn")
D = os.path.join(F, "src", "arkham", "data")
H = os.path.join(F, "homebrew")
out = os.path.join(root, "app", "assets", "data")
load = lambda p: json.load(open(p, encoding="utf-8"))

# ── 1. card texts (official zh-cn) per group; 01-03 are imported already ──
GROUPS = {"tfa": ["04"], "tcu": ["05"], "tde": ["06"], "tic": ["07"], "eoe": ["08"], "tsk": ["09"], "fhv": ["10"], "tdc": ["11"],
          "boa": ["12"], "cob": ["13"], "rtn": ["50", "51", "52", "53", "54"], "inv": ["60"],
          "side": ["70", "71", "72", "73", "81", "82", "83", "84", "85", "86", "87", "88", "89", "90", "98", "99"]}
cards = load(os.path.join(F, "public", "cards", "cards_zh-cn.json"))
for stem, prefixes in GROUPS.items():
    rows = sorted((c for c in cards if c["code"][:2] in prefixes), key=lambda c: c["code"])
    json.dump(rows, open(os.path.join(out, stem + "-cards.json"), "w", encoding="utf-8"), ensure_ascii=False, indent=1)
    print("cards", stem, len(rows))
# fan-made scenarios' cards (the html build's localized native homebrew card list)
hb = []
for p in sorted(glob.glob(os.path.join(F, "public", "l10n", "native-homebrew-cards-*.json"))):
    data = load(p); rows = data if isinstance(data, list) else data.get("cards", data.get("data", []))
    if isinstance(rows, dict): rows = list(rows.values())
    hb += [c for c in rows if isinstance(c, dict) and c.get("code")]
seen = set(); hb = [c for c in hb if not (c["code"] in seen or seen.add(c["code"]))]
json.dump(hb, open(os.path.join(out, "homebrew-cards.json"), "w", encoding="utf-8"), ensure_ascii=False, indent=1)
print("cards homebrew", len(hb))

# ── 2. official texts our locale copy lacks (never overriding a key it has) ──
def camel(dirname):
    parts = dirname.split("-"); return parts[0] + "".join(p[:1].upper() + p[1:] for p in parts[1:])
def campaign_messages(name):
    d = os.path.join(L, name); msg = {}
    if os.path.exists(os.path.join(d, "base.json")): msg.update(load(os.path.join(d, "base.json")))
    for p in sorted(glob.glob(os.path.join(d, "scenarios", "*.json"))): msg[os.path.basename(p)[:-5]] = load(p)
    for p in sorted(glob.glob(os.path.join(d, "*.json"))):
        if os.path.basename(p) != "base.json": msg[os.path.basename(p)[:-5]] = load(p)
    return msg
src = {}
for name in sorted(os.listdir(L)):
    if os.path.isdir(os.path.join(L, name)) and name not in ("standalone", "gameBoard"): src[name] = campaign_messages(name)
src["returnToTheForgottenAge"] = src.get("theForgottenAge", {})
src["standalone"] = {os.path.basename(p)[:-5]: load(p) for p in sorted(glob.glob(os.path.join(L, "standalone", "*.json")))}
for top in ("label", "log", "xp", "cards", "investigators"):
    if os.path.exists(os.path.join(L, top + ".json")): src.setdefault(top, {}).update(load(os.path.join(L, top + ".json")))
for top in ("base", "campaignLog"):
    if os.path.exists(os.path.join(L, top + ".json")):
        for k, v in load(os.path.join(L, top + ".json")).items(): src.setdefault(k, v) if not isinstance(v, dict) else src.setdefault(k, {}).update(v)
for name in sorted(os.listdir(H)):
    loc = os.path.join(H, name, "locales", "zh-cn")
    if not os.path.isdir(loc): continue
    scope = src.setdefault(camel(name), {})
    for p in sorted(glob.glob(os.path.join(loc, "*.json"))): scope.update(load(p))
have = load(os.path.join(out, "locale-zh.json"))
extra_path = os.path.join(out, "locale-zh-extra.json")
os.makedirs(os.path.join(root, "backup"), exist_ok=True)
bak = os.path.join(root, "backup", "locale-zh-extra-20261122-before-later-cycles.json")
if not os.path.exists(bak): shutil.copy(extra_path, bak)   # the copy from before the first run
extra = load(extra_path); added = 0
def merge(s, cur, x):
    global added
    for k, v in s.items():
        if isinstance(v, dict):
            c = cur.get(k) if isinstance(cur, dict) else None
            xx = x.setdefault(k, {}) if isinstance(x.get(k, {}), dict) else None
            if xx is None: continue
            merge(v, c if isinstance(c, dict) else {}, xx)
            if not xx: del x[k]
        elif not (isinstance(cur, dict) and k in cur) and k not in x:
            x[k] = v; added += 1
merge(src, have, extra)
extra["_source"] = extra.get("_source", "") + " Later cycles, side stories, fan-made scenarios (11-22): frontend/src/locales/zh-cn/<campaign>/, standalone/, top-level json, homebrew/<dir>/locales/zh-cn."
json.dump(extra, open(extra_path, "w", encoding="utf-8"), ensure_ascii=False, indent=1)
print("locale keys added", added)

# ── 3. the catalog the campaign picker reads ──
cn = load(os.path.join(L, "campaignNames.json")); cn = cn.get("campaignNames", cn)
sn = load(os.path.join(L, "scenarioNames.json")); sn = sn.get("scenarioNames", sn)
starts = {(s["kind"], s["id"]): s for s in load(starts_file)}
def lower_camel(english):
    words = re.sub(r"[^A-Za-z0-9 ]", " ", english.replace("'", "")).split()
    return (words[0].lower() + "".join(w[:1].upper() + w[1:].lower() if not w.isupper() else w for w in words[1:])) if words else ""
def scope_key(section, english):
    msgs = have.get(section, {}) if isinstance(have.get(section), dict) else {}
    msgs = {**src.get(section, {}), **msgs}
    base = re.sub(r",?\s*Part\s+[IV]+$", "", english).strip()
    for cand in (lower_camel(base), lower_camel(base).replace("The", "the")):
        if cand in msgs: return cand
    low = {k.lower(): k for k in msgs}
    return low.get(lower_camel(base).lower(), "")
def title_of(section, part):
    for m in (have.get(section), src.get(section)):
        t = m.get(part, {}).get("title") if isinstance(m, dict) and isinstance(m.get(part), dict) else None
        if isinstance(t, str) and t: return t
    return ""
FILE = {"01": "nightOfTheZealot", "02": "theDunwichLegacy", "03": "thePathToCarcosa", "04": "theForgottenAge", "05": "theCircleUndone",
        "06": "theDreamEaters", "07": "theInnsmouthConspiracy", "08": "edgeOfTheEarth", "09": "theScarletKeys", "10": "theFeastOfHemlockVale",
        "11": "theDrownedCity", "12": "brethrenOfAsh", "13": "childrenOfBlood"}
CYCLE = {"01": "基础游戏", "02": "第一循环", "03": "第二循环", "04": "第三循环", "05": "第四循环", "06": "第五循环", "07": "第六循环",
         "08": "第七循环", "09": "第八循环", "10": "第九循环", "11": "第十循环", "12": "第二季基础游戏", "13": "小型战役扩展"}
investigators = {}
for c in cards:
    if c.get("type_code") == "investigator" and re.fullmatch(r"\d{5}", c["code"]) and c["code"][2:4] == "00":
        investigators.setdefault(c["code"][:2], []).append(c["code"])
catalog = {"_source": "tools/import_later_cycles.py (html lists, zh-cn names, core starts probe)", "campaigns": [], "sideStories": [], "fanCampaigns": [], "fanScenarios": []}
for c in load(os.path.join(D, "campaigns.json")):
    cid = c["id"]; key = FILE.get(cid, "")
    chapters = []
    sfile = os.path.join(D, key + ".json")
    for s in (load(sfile) if os.path.exists(sfile) else []):
        chapters.append({"code": s["id"], "key": scope_key(key, s["name"]), "name": sn.get(s["id"], s["name"]), "english": s["name"],
                         "returnTo": s.get("returnTo"), "returnName": sn.get(s.get("returnTo") or "", "")})
    ret = (c.get("returnTo") or {}).get("id")
    st = starts.get(("campaign", cid), {}); rst = starts.get(("campaign", ret), {}) if ret else {}
    catalog["campaigns"].append({"id": cid, "key": key, "name": cn.get(cid, c["name"]), "english": c["name"], "cycle": CYCLE.get(cid, ""),
        "beta": bool(c.get("beta")), "chapters": chapters, "investigators": investigators.get(cid, []), "bags": c.get("difficultyLevels", {}),
        "returnTo": {"id": ret, "name": cn.get(ret, ""), "bags": (c.get("returnTo") or {}).get("difficultyLevels", c.get("difficultyLevels", {})),
                     "starts": rst.get("verdict") == "ok", "reason": rst.get("reason", "")} if ret else None,
        "starts": st.get("verdict") == "ok", "reason": st.get("reason", ""),
        "hasStoryTexts": bool(src.get(key) or have.get(key)),
        # official step titles (prologue / epilogue) when the campaign has them
        "prologue": title_of(key, "prologue"), "epilogue": title_of(key, "epilogue")})
for s in load(os.path.join(D, "side-stories.json")):
    st = starts.get(("scenario", s["id"]), {}); reason = st.get("reason", "")
    m = re.search(r"without (.+?)\"?\}?$", reason)
    need = m.group(1).replace('\\"', '"').strip('"') if m else ""
    need_code = next((x["code"] for x in cards if x.get("type_code") == "investigator" and x.get("real_name") == need.replace('"', '').replace("Skids O'Toole", '"Skids" O\'Toole') or x.get("real_name") == need), "") if need else ""
    catalog["sideStories"].append({"id": s["id"], "name": sn.get(s["id"], s["name"]), "english": s["name"], "key": scope_key("standalone", s["name"]),
        "difficulties": s.get("standaloneDifficulties", ["Easy", "Standard", "Hard", "Expert"]), "bags": s.get("difficultyLevels", {}),
        "beta": bool(s.get("beta")), "starts": st.get("verdict") == "ok", "needsInvestigator": need_code, "reason": reason})
for name in sorted(os.listdir(H)):
    d = os.path.join(H, name)
    if not os.path.exists(os.path.join(d, "scenarios.json")): continue
    scope = camel(name); msgs = src.get(scope, {})
    title = msgs.get("name") or msgs.get("title") or ""
    scen = [{"code": x["id"], "key": x.get("i18n", ""), "name": sn.get(x["id"], ""), "english": x.get("name", "")} for x in load(os.path.join(d, "scenarios.json"))]
    if os.path.exists(os.path.join(d, "campaign.json")):
        c = load(os.path.join(d, "campaign.json")); st = starts.get(("campaign", c["id"]), {})
        catalog["fanCampaigns"].append({"id": c["id"], "key": scope, "name": cn.get(c["id"], title), "english": c.get("name", ""), "designer": c.get("designer", ""),
            "beta": bool(c.get("beta")), "chapters": scen, "bags": c.get("difficultyLevels", {}), "starts": st.get("verdict") == "ok", "reason": st.get("reason", "")})
    else:
        for x in scen:
            st = starts.get(("scenario", x["code"]), {})
            catalog["fanScenarios"].append({"id": x["code"], "key": scope, "name": x["name"] or title, "english": x["english"], "starts": st.get("verdict") == "ok", "reason": st.get("reason", "")})
json.dump(catalog, open(os.path.join(out, "campaigns-catalog.json"), "w", encoding="utf-8"), ensure_ascii=False, indent=1)
print("catalog", len(catalog["campaigns"]), "campaigns,", len(catalog["sideStories"]), "side,", len(catalog["fanCampaigns"]), "fan campaigns,", len(catalog["fanScenarios"]), "fan scenarios")
