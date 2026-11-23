"""Keep today's automatic crop boxes (the manifest's small / strip, from art_focus.py) and only move them off the card frame
(Yunke 11-20: the automatic ones look better than GPT's, except that some take in the frame; GPT's keep off it by zooming far
in, and then the picture is hard to read).

The frame comes from frame_masks.py. A box is fine when the frame it shows is under LIMIT of what is seen; a strip is weighed
by the game's fade (DeckPreview.Fade: alpha = smoothstep(0, 0.62, u)), so frame in its faded left part hardly counts. When a
box shows frame, the nearest box at the same proportions that does not is taken, inside the art region: slide it first, and
make it smaller only as much as needed (cost = zoom + half the slide + PENALTY × the frame still seen: a sliver of frame is
better than blowing a card up by a third, which hides what it shows — an enemy's art band, whose top edge is the text box's
curve). SMALLEST bounds the zoom.  Writes <task>/deframed/crops.json (pixel boxes for every card), report.json and side-by-side sheets of
the moved boxes (left = automatic, right = moved).  Usage: python tools/dev/deframe_crops.py <task> [code,code,...]
"""
import json, os, sys
import numpy as np
from PIL import Image, ImageDraw, ImageFont

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from frame_masks import Frames  # noqa: E402

LIMIT = 0.0002
SMALLEST = 0.70   # import_crops.py takes no strip under 70 % of the art width
PENALTY = 25
ASPECT = {'small': 112 / 157, 'strip': 4.3}


def fade(n):
    u = (np.arange(n) + 0.5) / n; t = np.clip(u / 0.62, 0, 1)
    return t * t * (3 - 2 * t)


class Scorer:
    def __init__(self, f):
        self.f = f.astype(np.float32)
        self.ii = np.pad(self.f.cumsum(0).cumsum(1), ((1, 0), (1, 0)))
        self.col = np.pad(self.f.cumsum(0), ((1, 0), (0, 0)))   # per column, down the rows

    def small(self, x, y, w, h):
        x0, y0, x1, y1 = int(round(x)), int(round(y)), int(round(x + w)), int(round(y + h))
        ii = self.ii
        return float(ii[y1, x1] - ii[y0, x1] - ii[y1, x0] + ii[y0, x0]) / max(1, (x1 - x0) * (y1 - y0))

    def strip(self, x, y, w, h):
        x0, y0, x1, y1 = int(round(x)), int(round(y)), int(round(x + w)), int(round(y + h))
        c = self.col[y1, x0:x1] - self.col[y0, x0:x1]
        wt = fade(x1 - x0)
        return float((c * wt).sum() / max(1.0, wt.sum() * (y1 - y0)))


def best(kind, box, art, score):
    ax, ay, aw, ah = art; x, y, w, h = box
    cx, cy = x + w / 2, y + h / 2
    first = score(x, y, w, h)
    if first <= LIMIT: return None, first
    found = None
    for s in np.arange(1.0, SMALLEST - 1e-6, -0.02):
        bw, bh = w * s, h * s
        if bw > aw + 0.5 or bh > ah + 0.5: continue
        xs = np.linspace(ax, ax + aw - bw, max(2, int((aw - bw) / 3) + 1)) if aw - bw > 1 else [max(ax, min(x, ax + aw - bw))]
        ys = np.linspace(ay, ay + ah - bh, max(2, int((ah - bh) / 3) + 1)) if ah - bh > 1 else [max(ay, min(y, ay + ah - bh))]
        for by in ys:
            for bx in xs:
                sc = score(bx, by, bw, bh)
                if sc > 0.02: continue
                slide = abs(bx + bw / 2 - cx) / w + abs(by + bh / 2 - cy) / h
                cost = (1 - s) + 0.5 * slide + PENALTY * max(0.0, sc - LIMIT)
                if found is None or cost < found[0]: found = (cost, [bx, by, bw, bh], s)
        if found is not None and (1 - s) > found[0]: break   # a smaller box can no longer be cheaper
    if found is None: return False, first
    return found[1], first


def sheet(rows, kind, task, out, font):
    per = 12 if kind == 'small' else 10
    for s in range(0, len(rows), per):
        part = rows[s:s + per]
        cell_w, cell_h, cols = (330, 250, 4) if kind == 'small' else (1080, 165, 1)
        img_s = Image.new('RGB', (cell_w * cols, cell_h * ((len(part) + cols - 1) // cols)), (24, 22, 20)); d = ImageDraw.Draw(img_s)
        for i, (m, old, new, why) in enumerate(part):
            img = Image.open(os.path.join(task, m['image'])).convert('RGB')
            cx, cy = (i % cols) * cell_w, (i // cols) * cell_h
            a = cut(img, old, kind); b = cut(img, new, kind)
            img_s.paste(a, (cx + 8, cy + 26)); img_s.paste(b, (cx + 16 + a.size[0], cy + 26))
            d.text((cx + 8, cy + 4), '%s %s  左=原 右=去边框  %s' % (m['code'], m['name'], why), fill=(240, 220, 170), font=font)
        img_s.save(os.path.join(out, 'sheet-%s-%02d.jpg' % (kind, s // per + 1)), quality=88)


def cut(img, box, kind, h=200):
    x, y, w, hh = box
    c = img.crop((int(round(x)), int(round(y)), int(round(x + w)), int(round(y + hh))))
    if kind == 'small': return c.resize((int(h * w / hh), h))
    c = c.resize((520, int(520 * hh / w)))
    a = np.asarray(c).astype(np.float32); f = fade(a.shape[1])[None, :, None]
    return Image.fromarray((a * f + np.array([15, 13, 11]) * (1 - f)).astype(np.uint8))


def main():
    task = sys.argv[1]; only = set(sys.argv[2].split(',')) if len(sys.argv) > 2 else None
    fr = Frames(task)
    out = os.path.join(task, 'deframed'); os.makedirs(out, exist_ok=True)
    try: font = ImageFont.truetype('C:/Windows/Fonts/msyh.ttc', 15)
    except Exception: font = ImageFont.load_default()
    crops, report, moved = {}, {}, {'small': [], 'strip': []}
    items = [m for m in fr.manifest if only is None or m['code'] in only]
    for n, m in enumerate(items):
        img = Image.open(os.path.join(task, m['image'])).convert('RGB')
        sc = Scorer(fr.mask(m, img))
        entry = {}
        for kind in ('small', 'strip'):
            old = m[kind]
            new, first = best(kind, old, m['art'], getattr(sc, kind))
            if new is None: entry[kind] = old; continue
            if new is False:
                entry[kind] = old; report.setdefault(m['code'], {})[kind] = 'left: frame %.3f, no clean box' % first; continue
            entry[kind] = [round(v, 1) for v in new]
            why = 'frame %.1f%%>%.2f%%  x%.2f' % (first * 100, getattr(sc, kind)(*new) * 100, new[2] / old[2])
            report.setdefault(m['code'], {})[kind] = why
            moved[kind].append((m, old, new, why))
        crops[m['code']] = entry
        if n % 200 == 0: print(n, '/', len(items), flush=True)
    json.dump(crops, open(os.path.join(out, 'crops.json'), 'w', encoding='utf-8'), ensure_ascii=False, indent=0)
    json.dump(report, open(os.path.join(out, 'report.json'), 'w', encoding='utf-8'), ensure_ascii=False, indent=1)
    for kind in ('small', 'strip'): sheet(moved[kind], kind, task, out, font)
    zoom = {k: [r[2][2] / r[1][2] for r in moved[k]] for k in moved}
    print('cards', len(items), {k: len(v) for k, v in moved.items()},
          'left', sum(1 for r in report.values() for v in r.values() if v.startswith('left')),
          {k: ('min %.2f median %.2f' % (min(v), float(np.median(v)))) if v else '-' for k, v in zoom.items()})


if __name__ == '__main__':
    main()
