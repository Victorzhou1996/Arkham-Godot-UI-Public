# Read-only extraction of core-set player card costs and skill icons from the rules backend source
# (Arkham/{Asset,Event}/Cards/NightOfTheZealot.hs and Arkham/Skill/Cards.hs), for the hand-card UI.
# Writes app/assets/data/core-costs.json: { "01016": {"type": "asset", "cost": 4, "skills": ["agility"]}, ... }
# Usage: python -I tools/extract_core_costs.py "<Arkham UI root>" "<ArkhamHorror repo root>"
import sys, os, re, json

root, repo = sys.argv[1], sys.argv[2]
lib = os.path.join(repo, "backend", "arkham-api", "library", "Arkham")
files = [os.path.join(lib, "Asset", "Cards", "NightOfTheZealot.hs"), os.path.join(lib, "Event", "Cards", "NightOfTheZealot.hs"), os.path.join(lib, "Skill", "Cards.hs")]
cards = {c["code"]: c for c in json.load(open(os.path.join(root, "app", "assets", "data", "core-cards.json"), encoding="utf-8"))}
player = {k for k, c in cards.items() if c.get("type_code") in ("asset", "event", "skill")}

NAME = r'(?:"(?:\\.|[^"\\])*"|\((?:[^()]|\([^()]*\))*\))'
head = re.compile(r'\(?(asset|event|storyAsset)\s+"(\d{5}[a-z]?)"\s+' + NAME + r'\s+(\d+)')
skillh = re.compile(r'\(skill\s+"(\d{5}[a-z]?)"\s+' + NAME + r'\s+\[([^\]]*)\]')
weak = re.compile(r'(weakness|basicWeakness)\s+"(\d{5}[a-z]?)"')
icons = lambda s: [w.strip().lstrip("#").lower() for w in s.split(",") if w.strip()]

out = {}
for f in files:
    text = open(f, encoding="utf-8").read()
    for block in re.split(r'\n(?=[a-z][A-Za-z0-9_\']*\s*::\s*CardDef)', text):
        m = head.search(block); s = skillh.search(block); w = weak.search(block)
        if m: kind, code, cost = m.group(1), m.group(2), int(m.group(3))
        elif s: kind, code, cost = "skill", s.group(1), None
        elif w: kind, code, cost = "weakness", w.group(2), None
        else: continue
        if code not in player: continue
        sk = icons(s.group(2)) if s else []
        cs = re.search(r'(?:cdSkills\s*=|&\s*skills)\s*\[([^\]]*)\]', block)
        if cs: sk = icons(cs.group(1))
        cc = re.search(r'cdCost\s*=\s*(Just\s*\(StaticCost\s+(\d+)\)|Nothing)', block)
        if cc: cost = int(cc.group(2)) if cc.group(2) else None
        out[code] = {"type": cards[code]["type_code"], "cost": cost, "skills": sk}

missing = sorted(player - set(out))
dest = os.path.join(root, "app", "assets", "data", "core-costs.json")
json.dump({"_source": "rules backend source (read-only): " + ", ".join(os.path.relpath(f, repo) for f in files), **dict(sorted(out.items()))},
          open(dest, "w", encoding="utf-8"), ensure_ascii=False, indent=1)
print(f"extracted {len(out)} of {len(player)} core player cards -> {dest}")
print("missing:", missing)
for code in ["01016", "01006", "01039", "01020", "01030", "01037", "01088"]:
    print(code, cards.get(code, {}).get("real_name"), out.get(code))
