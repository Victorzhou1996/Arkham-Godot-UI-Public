"""Read the crop boxes an outside helper picked (inbox/art-crop-task-*/crops*.json, the task in TASK-卡图裁切坐标.md), check each
box against the task's rules and write app/assets/build-data/art-crops.json {code: {"small": [x, y, w, h], "strip": [...]}}
as fractions of the card image (the game reads it in DeckPreview.Picked). Boxes that break a rule are dropped (the card keeps
today's automatic box) and listed.  Usage: python tools/dev/import_crops.py inbox/art-crop-task-20261119
"""
import glob, json, os, sys

ROOT = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
task = sys.argv[1]
manifest = {m['code']: m for m in json.load(open(os.path.join(task, 'manifest.json'), encoding='utf-8'))}
out_path = os.path.join(ROOT, 'app', 'assets', 'build-data', 'art-crops.json')
result = json.load(open(out_path, encoding='utf-8')) if os.path.exists(out_path) else {}
RULES = {'small': (112 / 157, 0.01, 'h', 0.55), 'strip': (4.3, 0.02, 'w', 0.70)}
ok = bad = 0; problems = []
for f in sorted(glob.glob(os.path.join(task, 'crops*.json'))):
    for code, entry in json.load(open(f, encoding='utf-8')).items():
        m = manifest.get(code)
        if m is None or not isinstance(entry, dict): problems.append((code, 'unknown card')); bad += 1; continue
        ax, ay, aw, ah = m['art']
        for kind, (aspect, tol, dim, least) in RULES.items():
            box = entry.get(kind)
            if box is None: continue
            try: x, y, w, h = [float(v) for v in box]
            except Exception: problems.append((code, kind + ' not [x,y,w,h]')); bad += 1; continue
            why = []
            if w <= 0 or h <= 0: why.append('size')
            elif abs((w / h) / aspect - 1) > tol: why.append('aspect %.3f' % (w / h))
            if x < ax - 1 or y < ay - 1 or x + w > ax + aw + 1 or y + h > ay + ah + 1: why.append('outside art')
            if (h < least * ah) if dim == 'h' else (w < least * aw): why.append('too small')
            if why: problems.append((code, kind + ': ' + ', '.join(why))); bad += 1; continue
            result.setdefault(code, {})[kind] = [round(x / m['width'], 4), round(y / m['height'], 4), round(w / m['width'], 4), round(h / m['height'], 4)]
            ok += 1
json.dump(dict(sorted(result.items())), open(out_path, 'w', encoding='utf-8'), separators=(',', ':'))
print('accepted', ok, 'dropped', bad, '->', out_path)
for p in problems[:40]: print('  ', *p)
