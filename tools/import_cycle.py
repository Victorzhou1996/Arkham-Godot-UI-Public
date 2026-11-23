# Imports one cycle's cards into the app, read-only from its sources (Yunke: html version texts and art; rules source only as reference).
#   cards:  0712 html build  frontend/public/cards/cards_zh-cn.json (the official zh-cn file; its core cards match core-cards.json exactly)
#   images: 0712 cards/<code>.avif  -> app/assets/cards/<code>.webp
#   mini:   0712 html portraits img/arkham/portraits/<code>.jpg -> app/assets/ui/mini/ (investigators only)
#   costs:  rules source CardDef (Asset/Event Cards/<Cycle>.hs, Skill/Cards.hs) -> app/assets/data/<prefix>-costs.json
# Usage: python -I tools/import_cycle.py <Arkham UI root> <0712 root> <ArkhamHorror repo root> <code prefix, e.g. 02> <file prefix, e.g. dwl> <Haskell file stem, e.g. TheDunwichLegacy>
import sys, os, re, json
from PIL import Image

root, web, repo, prefix, stem, hs = sys.argv[1:7]
pub = os.path.join(web, "classic-fast-20261117", "frontend", "public")
cards = [c for c in json.load(open(os.path.join(pub, "cards", "cards_zh-cn.json"), encoding="utf-8")) if c["code"].startswith(prefix)]
cards.sort(key=lambda c: c["code"])
data = os.path.join(root, "app", "assets", "data")
json.dump(cards, open(os.path.join(data, stem + "-cards.json"), "w", encoding="utf-8"), ensure_ascii=False, indent=1)
print("cards", len(cards))

# printed images
src = os.path.join(web, "cards"); dst = os.path.join(root, "app", "assets", "cards"); n = 0
for f in sorted(os.listdir(src)):
    if not re.fullmatch(prefix + r"\d{3}[a-z]?\.avif", f): continue
    out = os.path.join(dst, f[:-5] + ".webp")
    if os.path.exists(out): continue
    Image.open(os.path.join(src, f)).convert("RGB").save(out, "WEBP", quality=82, method=4); n += 1
print("images", n)

# investigator minis (html version portraits)
por = None
for base, dirs, files in os.walk(os.path.join(web, "backend-localization-20261017")):
    if base.replace("\\", "/").endswith("frontend/img/arkham/portraits") and (prefix + "001.jpg") in files: por = base
mini = os.path.join(root, "app", "assets", "ui", "mini")
for c in cards:
    if c.get("type_code") == "investigator" and por:
        p = os.path.join(por, c["code"] + ".jpg")
        if os.path.exists(p): open(os.path.join(mini, c["code"] + ".jpg"), "wb").write(open(p, "rb").read())
print("portraits from", por)

# costs and skill icons (same rules as tools/extract_core_costs.py)
lib = os.path.join(repo, "backend", "arkham-api", "library", "Arkham")
if not os.path.isdir(lib): lib = repo   # the rules library itself (…/arkham-core/library/Arkham), read-only
files = [os.path.join(lib, "Asset", "Cards", hs + ".hs"), os.path.join(lib, "Event", "Cards", hs + ".hs"), os.path.join(lib, "Skill", "Cards.hs")]
if os.path.exists(os.path.join(lib, "Skill", "CardDefs", hs + ".hs")): files.append(os.path.join(lib, "Skill", "CardDefs", hs + ".hs"))   # newer rules sources split skills per cycle
player = {c["code"]: c for c in cards if c.get("type_code") in ("asset", "event", "skill")}
NAME = r'(?:"(?:\\.|[^"\\])*"|\((?:[^()]|\([^()]*\))*\))'
head = re.compile(r'\(?(asset|event|storyAsset)\s+"(\d{5}[a-z]?)"\s+' + NAME + r'\s+(\d+)')
skillh = re.compile(r'\(skill\s+"(\d{5}[a-z]?)"\s+' + NAME + r'\s+\[([^\]]*)\]')
skillr = re.compile(r'\(skill\s+"(\d{5}[a-z]?)"\s+' + NAME + r'\s+\(replicate\s+(\d+)\s+#(\w+)\)')   # (replicate 4 #combat)
weak = re.compile(r'(weakness|basicWeakness|storyWeakness)\s+"(\d{5}[a-z]?)"')
icons = lambda s: [w.strip().lstrip("#").lower() for w in s.split(",") if w.strip()]
out = {}
for f in files:
    text = open(f, encoding="utf-8").read()
    for block in re.split(r'\n(?=[a-z][A-Za-z0-9_\']*\s*::\s*CardDef)', text):
        m = head.search(block); s = skillh.search(block); w = weak.search(block)
        if m: kind, code, cost = m.group(1), m.group(2), int(m.group(3))
        elif s: kind, code, cost = "skill", s.group(1), None
        elif skillr.search(block): r = skillr.search(block); kind, code, cost = "skill", r.group(1), None
        elif w: kind, code, cost = "weakness", w.group(2), None
        else: continue
        if code not in player: continue
        sk = icons(s.group(2)) if s else ([r.group(3).lower()] * int(r.group(2)) if kind == "skill" and not s else [])
        cs = re.search(r'(?:cdSkills\s*=|&\s*skills)\s*\[([^\]]*)\]', block)
        if cs: sk = icons(cs.group(1))
        cc = re.search(r'cdCost\s*=\s*(Just\s*\(StaticCost\s+(\d+)\)|Nothing)', block)
        if cc: cost = int(cc.group(2)) if cc.group(2) else None
        slots = re.search(r'cdSlots\s*=\s*\[([^\]]*)\]', block)
        out[code] = {"type": player[code]["type_code"], "cost": cost, "skills": sk}
missing = sorted(set(player) - set(out))
json.dump({"_source": "rules source (read-only): " + ", ".join(os.path.relpath(f, repo) for f in files), **dict(sorted(out.items()))},
          open(os.path.join(data, stem + "-costs.json"), "w", encoding="utf-8"), ensure_ascii=False, indent=1)
print("costs", len(out), "of", len(player), "missing", missing)

# art crops of the printed cards (the redrawn card frame needs a picture, and the reference-version
# illustrations cover only the core set) -> app/assets/art-fallback/<code>-fallback.webp, picked up by Ui.Art
BOX = {  # in 750 x 1050 portrait pixels (investigator: 1050 x 750 landscape)
    "asset": (130, 95, 730, 463), "event": (100, 45, 700, 470), "skill": (60, 105, 700, 500),
    "treachery": (110, 60, 640, 480), "location": (45, 100, 705, 500), "enemy": (40, 600, 710, 1000),
    "investigator": (20, 110, 560, 750),
}
fb = os.path.join(root, "app", "assets", "art-fallback"); made = 0
for c in cards:
    kind = c.get("type_code")
    if kind not in BOX: continue
    for side in ("", "b"):
        if side == "b" and kind != "location": continue
        img = os.path.join(dst, c["code"] + side + ".webp")
        if not os.path.exists(img): continue
        im = Image.open(img).convert("RGB")
        if (im.size[0] > im.size[1]) != (kind == "investigator"): continue   # an odd orientation: leave it to the full card
        sx, sy = im.size[0] / (1050 if kind == "investigator" else 750), im.size[1] / (750 if kind == "investigator" else 1050)
        x0, y0, x1, y1 = BOX[kind]
        if kind == "asset" and c.get("subname"): y0 = 140   # unique assets carry a subtitle band under the name
        im.crop((int(x0 * sx), int(y0 * sy), int(x1 * sx), int(y1 * sy))).save(os.path.join(fb, c["code"] + side + "-fallback.webp"), "WEBP", quality=85); made += 1
print("art crops", made)
