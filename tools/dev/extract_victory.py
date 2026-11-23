"""Victory points of every card, from the html version's card data (official ArkhamDB fields; the 0712 workspace is only
read), into app/assets/build-data/victory.json ({code: points}). The rules core's catalog (core-cards.json) has no victory
field, and the table needs it for the star badge and "胜利X" (Yunke 11-19).
Usage: python tools/dev/extract_victory.py"""
import json, os
ROOT = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
DIST = os.path.join(os.path.dirname(ROOT), 'ArkhamHorrorLocal-WinDeploy-20260712', 'game', 'frontend', 'dist')
out = {}
# cards.json lacks some later packs; cards_zh.json (same fields, Chinese text) has them
for name in ('cards.json', 'cards_zh.json'):
    for c in json.load(open(os.path.join(DIST, name), encoding='utf-8')):
        v = c.get('victory')
        if isinstance(v, int) and v > 0: out.setdefault(c['code'], v)
json.dump(dict(sorted(out.items())), open(os.path.join(ROOT, 'app', 'assets', 'build-data', 'victory.json'), 'w', encoding='utf-8'), ensure_ascii=False, indent=0)
print(len(out), 'cards with victory points')
