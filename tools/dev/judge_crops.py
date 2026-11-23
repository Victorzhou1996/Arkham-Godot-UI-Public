"""Weigh the outside helper's crop boxes (inbox/art-crop-task-*/crops.json, GPT) against today's automatic ones (the
manifest's small / strip, from art_focus.py) card by card, instead of taking either wholesale (Yunke 11-20: "评判他的版本和你的版本
的优劣，最好结合一下，不要无脑用").

For each card and each box kind it measures, on the printed card's art region:
  · detail — mean local contrast (blurred Laplacian) inside the box, against the art region's mean: a box on an empty sky or a
    flat wash scores low (07094: GPT framed a bare teal patch);
  · subject — how much of what the GPU detector (Faster R-CNN, COCO) and the face cascades find falls inside the box:
    people by their head and shoulders, animals, then objects; weights as in art_focus.py.
A strip is judged on its right 60 % only (the left 40 % is faded out in the game).
GPT's box is taken unless it is clearly worse on these measures; those cards are flagged with the reason and drawn side by side
(automatic | GPT) on contact sheets, to be decided by eye.  Writes <task>/judge/judge.json and <task>/judge/sheet-<kind>-NN.jpg.
Usage: python tools/dev/judge_crops.py inbox/art-crop-task-20261119 [limit]
"""
import json, os, sys
import numpy as np
import cv2
from PIL import Image, ImageDraw, ImageFont

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from art_focus import detections, face_front, face_side  # noqa: E402

task = sys.argv[1]; arg = sys.argv[2] if len(sys.argv) > 2 else ''
limit = int(arg) if arg.isdigit() else 0; only = set(arg.split(',')) if arg and not arg.isdigit() else None
manifest = json.load(open(os.path.join(task, 'manifest.json'), encoding='utf-8'))
gpt = json.load(open(os.path.join(task, 'crops.json'), encoding='utf-8'))
out_dir = os.path.join(task, 'judge'); os.makedirs(out_dir, exist_ok=True)
try: font = ImageFont.truetype('C:/Windows/Fonts/msyh.ttc', 15)
except Exception: font = ImageFont.load_default()


def subjects(img, art):
    """[(x0, y0, x1, y1, weight, kind)] in card pixels: detector boxes (people cut to head and shoulders) and faces."""
    ax, ay, aw, ah = art
    region = img.crop((ax, ay, ax + aw, ay + ah))
    res = []
    for (x0, y0, x1, y1, wgt, label) in detections(region):
        bw, bh = x1 - x0, y1 - y0
        if bw * bh > 0.8 * aw * ah and label != 1: continue           # a box over the whole picture says nothing
        if label == 1: y1 = y0 + max(bh * 0.45, min(bh, bw * 0.9))     # a person: the head and shoulders
        kind = 'person' if label == 1 else 'animal' if 16 <= label <= 25 else 'object'
        res.append((ax + x0, ay + y0, ax + x1, ay + y1, wgt, kind))
    gray = cv2.cvtColor(np.asarray(region.convert('RGB'))[:, :, ::-1].copy(), cv2.COLOR_BGR2GRAY)
    for casc, flip in ((face_front, False), (face_side, False), (face_side, True)):
        g = cv2.flip(gray, 1) if flip else gray
        for (x, y, w, h) in casc.detectMultiScale(g, 1.1, 4, minSize=(18, 18)):
            if flip: x = gray.shape[1] - x - w
            res.append((ax + x, ay + y, ax + x + w, ay + y + h, 3.0, 'face'))
    return res


def detail_map(img):
    g = cv2.cvtColor(np.asarray(img.convert('RGB'))[:, :, ::-1].copy(), cv2.COLOR_BGR2GRAY)
    lap = np.abs(cv2.Laplacian(cv2.GaussianBlur(g, (0, 0), 1.2), cv2.CV_32F))
    return cv2.GaussianBlur(lap, (0, 0), 3)


def visible(box, kind):
    x, y, w, h = box
    return (x + 0.4 * w, y, 0.6 * w, h) if kind == 'strip' else (x, y, w, h)


def detail(dm, box, art_mean):
    x, y, w, h = [int(round(v)) for v in box]
    patch = dm[max(0, y):y + h, max(0, x):x + w]
    return float(patch.mean() / (art_mean + 1e-6)) if patch.size else 0.0


def cover(subs, box):
    """Weighted share of each subject inside the box; and the share of the strongest person / animal / face."""
    x, y, w, h = box; tot = 0.0; best = (0.0, None)
    for (x0, y0, x1, y1, wgt, kind) in subs:
        ix = max(0.0, min(x + w, x1) - max(x, x0)); iy = max(0.0, min(y + h, y1) - max(y, y0))
        share = ix * iy / max(1.0, (x1 - x0) * (y1 - y0))
        tot += wgt * share
        if kind != 'object' and wgt > best[0]: best = (wgt, share)
    return tot, best[1]


def judge(m, g, img, dm, subs):
    ax, ay, aw, ah = m['art']
    art_mean = float(dm[ay:ay + ah, ax:ax + aw].mean())
    out = {}
    for kind in ('small', 'strip'):
        mine, theirs = m[kind], g.get(kind) or m[kind]
        dv = [detail(dm, visible(b, kind), art_mean) for b in (mine, theirs)]
        cv_ = [cover(subs, visible(b, kind)) for b in (mine, theirs)]
        why = []
        # What a first pass taught (sheets of 40 cards): GPT is mostly the better one — it frames what the card's name says
        # (the lantern, the ghoul, the claws), where ours framed a person beside it; and our strips often took in the card
        # frame's ornaments, whose sharp lines read as "detail". So a plain "less detail" or "missed the figure" is no reason.
        # Two cases are worth a look by eye:
        # · GPT zoomed far in (a small box under 70 % of the art's height): the card is blown up and easily lands on nothing
        #   (07094: a bare patch of fog);
        rel = theirs[3] / ah if kind == 'small' else theirs[2] / aw
        if kind == 'small' and rel < 0.7: why.append('zoom %.2f' % rel)
        # · GPT's box holds nothing the detector or the face finder sees, ours does, and GPT's is the plainer one.
        if cv_[1][0] < 0.3 and cv_[0][0] >= 1.0 and dv[1] < dv[0]: why.append('empty %.1f>%.1f' % (cv_[0][0], cv_[1][0]))
        out[kind] = {'pick': 'look' if why else 'gpt', 'why': why, 'detail': [round(v, 2) for v in dv],
                     'cover': [round(c[0], 2) for c in cv_]}
    return out


def crop_img(img, box, kind, h=200):
    x, y, w, hh = box
    c = img.crop((int(x), int(y), int(x + w), int(y + hh)))
    c = c.resize((int(h * w / hh), h)) if kind == 'small' else c.resize((520, int(520 * hh / w)))
    if kind == 'strip':   # the game fades the left 40 %
        a = np.asarray(c.convert('RGB')).astype(np.float32); n = a.shape[1]
        ramp = np.clip((np.arange(n) / n) / 0.4, 0, 1)[None, :, None]
        c = Image.fromarray((a * (0.2 + 0.8 * ramp)).astype(np.uint8))
    return c


def sheets(flagged, kind, imgs):
    per = 12 if kind == 'small' else 10
    for s in range(0, len(flagged), per):
        part = flagged[s:s + per]
        if kind == 'small': cell_w, cell_h, cols = 330, 250, 4
        else: cell_w, cell_h, cols = 1080, 165, 1
        rows = (len(part) + cols - 1) // cols
        sheet = Image.new('RGB', (cell_w * cols, cell_h * rows), (24, 22, 20)); d = ImageDraw.Draw(sheet)
        for i, (m, j) in enumerate(part):
            img = imgs(m)
            cx, cy = (i % cols) * cell_w, (i // cols) * cell_h
            a = crop_img(img, m[kind], kind); b = crop_img(img, gpt[m['code']][kind], kind)
            sheet.paste(a, (cx + 8, cy + 26)); sheet.paste(b, (cx + 16 + a.size[0], cy + 26))
            d.text((cx + 8, cy + 4), '%s %s  左=原 右=GPT  %s' % (m['code'], m['name'], ' '.join(j[kind]['why'])), fill=(240, 220, 170), font=font)
        sheet.save(os.path.join(out_dir, 'sheet-%s-%02d.jpg' % (kind, s // per + 1)), quality=88)


def main():
    result = {}; flagged = {'small': [], 'strip': []}
    items = [m for m in manifest if m['code'] in gpt and (only is None or m['code'] in only)][:limit or None]
    for n, m in enumerate(items):
        img = Image.open(os.path.join(task, m['image'])).convert('RGB')
        j = judge(m, gpt[m['code']], img, detail_map(img), subjects(img, m['art']))
        result[m['code']] = j
        if only: print(m['code'], m['name'], json.dumps(j, ensure_ascii=False))
        for kind in ('small', 'strip'):
            if j[kind]['pick'] == 'look': flagged[kind].append((m, j))
        if n % 200 == 0: print(n, '/', len(items), flush=True)
    json.dump(result, open(os.path.join(out_dir, 'judge.json'), 'w', encoding='utf-8'), ensure_ascii=False, indent=0)
    cache = {}
    def imgs(m):
        if m['code'] not in cache: cache.clear(); cache[m['code']] = Image.open(os.path.join(task, m['image'])).convert('RGB')
        return cache[m['code']]
    for kind in ('small', 'strip'): sheets(flagged[kind], kind, imgs)
    print('cards', len(items), 'flagged small', len(flagged['small']), 'strip', len(flagged['strip']))


if __name__ == '__main__':
    main()
