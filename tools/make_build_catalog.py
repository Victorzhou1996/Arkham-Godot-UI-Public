# Player cards of every cycle for the table's catalog (Yunke 11-18: decks are built with arkham.build over all cycles and
# may be played in the core / Dunwich campaigns). Source: arkham.build's local card cache (app/assets/build-data,
# zh-cn), the same data the workshop's rules engine uses. Writes app/assets/data/zz-build-cards.json and
# zz-build-costs.json in the catalog's own format; cards already in core-/dwl-cards.json are left to those files.
# Usage: python -I tools/make_build_catalog.py "<Arkham UI root>"
import json, os, sys

root = sys.argv[1]
data = os.path.join(root, "app", "assets", "data")
build = os.path.join(root, "app", "assets", "build-data")
cards = json.load(open(os.path.join(build, "cards-zh-cn.json"), encoding="utf-8"))["data"]["all_card"]
meta = json.load(open(os.path.join(build, "metadata-zh-cn.json"), encoding="utf-8"))["data"]
packs = {p["code"]: p for p in meta["pack"]}
have = set()
for f in ("core-cards.json", "dwl-cards.json"):
    for c in json.load(open(os.path.join(data, f), encoding="utf-8")):
        have.add(c["code"])

FACTION = {"guardian": "守护者", "seeker": "探求者", "rogue": "流浪者", "mystic": "神秘学家", "survivor": "生存者", "neutral": "中立", "mythos": "神话"}
TYPE = {"asset": "支援", "event": "事件", "skill": "技能", "investigator": "调查员", "treachery": "诡计", "enemy": "敌人", "story": "故事", "location": "地点"}
KEEP = ["code", "name", "real_name", "subname", "text", "real_text", "traits", "real_traits", "flavor", "back_text", "back_flavor", "real_back_text",
        "type_code", "faction_code", "faction2_code", "faction3_code", "subtype_code", "pack_code", "is_unique", "double_sided", "xp", "cost",
        "deck_limit", "deck_requirements", "deck_options", "health", "sanity", "slot", "real_slot", "exceptional", "myriad", "permanent",
        "skill_willpower", "skill_intellect", "skill_combat", "skill_agility", "skill_wild", "restrictions", "bonded_to", "bonded_count",
        "customization_options", "customization_text", "back_link", "hidden", "duplicate_of_code", "alternate_of_code"]

out, costs = [], {}
for c in cards:
    code = c.get("code", "")
    if not code or code in have or c.get("encounter_code"):
        continue
    if c.get("type_code") not in ("asset", "event", "skill", "investigator", "treachery", "enemy", "story"):
        continue
    if c.get("type_code") in ("treachery", "enemy", "story") and not c.get("subtype_code") and not c.get("restrictions"):
        continue  # only weaknesses and signature cards among those
    if c.get("taboo_set_id"):
        continue
    e = {k: c[k] for k in KEEP if k in c and c[k] is not None}
    e["faction_name"] = FACTION.get(c.get("faction_code", ""), "")
    e["type_name"] = TYPE.get(c.get("type_code", ""), "")
    e["pack_name"] = packs.get(c.get("pack_code", ""), {}).get("name", "")
    out.append(e)
    if c.get("type_code") in ("asset", "event", "skill"):
        skills = []
        for s in ("willpower", "intellect", "combat", "agility", "wild"):
            skills += [s] * int(c.get("skill_" + s) or 0)
        costs[code] = {"type": c["type_code"], "cost": c.get("cost"), "skills": skills}

out.sort(key=lambda x: x["code"])
json.dump(out, open(os.path.join(data, "zz-build-cards.json"), "w", encoding="utf-8"), ensure_ascii=False)
costs["_source"] = {"type": "note", "cost": None, "skills": [], "from": "arkham.build card cache (zh-cn)"} if False else None
costs.pop("_source", None)
json.dump(costs, open(os.path.join(data, "zz-build-costs.json"), "w", encoding="utf-8"), ensure_ascii=False)
print("cards", len(out), "costs", len(costs))
