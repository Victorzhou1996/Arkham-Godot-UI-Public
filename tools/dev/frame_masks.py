"""Where the printed card's frame is, card by card, so a crop box can be kept off it (Yunke 11-20: the automatic boxes look
better than GPT's, only some take in the frame; GPT's keep off it by zooming far in, and then the picture is hard to read).

The frame of a card is the same print on every card of its type and class; the illustration is different on each. So, over
all the 600×840 scans of one (type, class), a pixel whose colour hardly changes from card to card is frame, and a pixel of
one card that matches that steady colour is that card's frame (a card scanned at another size or offset simply matches less).
Writes <task>/frames/templates.npz (mean and spread per group) and, with --show, overlays of the found frame for a check.
Usage: python tools/dev/frame_masks.py <task> [--show code,code,...]
"""
import json, os, sys
import numpy as np
from PIL import Image

ROOT = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
SIZE = (600, 840)
STEADY = 14.0     # spread (0–255, per channel, the largest) under which a pixel is the same print on every card
MATCH = 26.0      # a card's pixel within this of the steady colour is that print


def card_index():
    d = json.load(open(os.path.join(ROOT, 'app/assets/build-data/cards-zh-cn.json'), encoding='utf-8'))['data']['all_card']
    return {c['code']: c for c in d}


def group_of(c):
    return c['type_code'] + '/' + (c.get('faction_code') or '') + ('+' + c['faction2_code'] if c.get('faction2_code') else '')


def soft(img):
    """The card at the template size, a little blurred: a thin printed line lands a pixel apart from scan to scan, and
    unblurred it reads as changing (the stripes of a neutral event's frame were missed)."""
    import cv2
    img = img if img.size == SIZE else img.resize(SIZE, Image.BILINEAR)
    return cv2.GaussianBlur(np.asarray(img, dtype=np.float32), (0, 0), 1.6)


def load(task, m):
    return soft(Image.open(os.path.join(task, m['image'])).convert('RGB'))


def stats(items):
    n = 0; s1 = s2 = None
    for a in items:
        a = a.astype(np.float64)
        if s1 is None: s1 = np.zeros_like(a); s2 = np.zeros_like(a)
        n += 1; s1 += a; s2 += a * a
    mean = s1 / n; std = np.sqrt(np.maximum(0, s2 / n - mean * mean))
    return n, mean.astype(np.float32), std.max(axis=2).astype(np.float32)


def build(task, manifest, cards):
    """One template per (type, class); and, where a group holds cards printed in more than one frame style (the neutral
    events: gray, striped …), one per style: the group's cards are clustered on the pixels that are frame for the type."""
    import cv2
    by = {}
    for m in manifest:
        if (m['width'], m['height']) == SIZE: by.setdefault(group_of(cards[m['code']]), []).append(m)
    by = {g: ms for g, ms in by.items() if len(ms) >= 8}
    out, thumbs = {}, {}
    for g, ms in by.items():
        def each():
            for m in ms:
                a = load(task, m); thumbs[m['code']] = cv2.resize(a, (60, 84), interpolation=cv2.INTER_AREA); yield a
        n, mean, std = stats(each())
        out[g + ':mean'] = mean; out[g + ':std'] = std; out[g + ':n'] = np.array(n)
    # the type's frame shape: steady in most of its groups
    for t in sorted({g.split('/')[0] for g in by}):
        gs = [g for g in by if g.startswith(t + '/')]
        shape = np.mean([cv2.resize((out[g + ':std'] < STEADY).astype(np.float32), (60, 84), interpolation=cv2.INTER_AREA) for g in gs], axis=0) >= 0.5
        if shape.sum() < 50: continue
        for g in gs:
            ms = by[g]
            if len(ms) < 16: continue
            X = np.stack([thumbs[m['code']][shape].ravel() for m in ms]).astype(np.float32)
            k = 3 if len(ms) >= 30 else 2
            _, lab, _ = cv2.kmeans(X, k, None, (cv2.TERM_CRITERIA_EPS + cv2.TERM_CRITERIA_MAX_ITER, 50, 0.5), 5, cv2.KMEANS_PP_CENTERS)
            for i in range(k):
                part = [m for m, l in zip(ms, lab.ravel()) if l == i]
                if len(part) < 6 or len(part) == len(ms): continue
                n, mean, std = stats(load(task, m) for m in part)
                key = g + '#' + str(i)
                out[key + ':mean'] = mean; out[key + ':std'] = std; out[key + ':n'] = np.array(n)
                print(key, n)
    return out


class Frames:
    def __init__(self, task):
        self.task = task
        self.cards = card_index()
        self.manifest = json.load(open(os.path.join(task, 'manifest.json'), encoding='utf-8'))
        path = os.path.join(task, 'frames', 'templates-styles.npz')
        if not os.path.exists(path):
            os.makedirs(os.path.dirname(path), exist_ok=True)
            np.savez_compressed(path, **build(task, self.manifest, self.cards))
        z = np.load(path)
        self.t = {k: z[k] for k in z.files}
        self.groups = sorted({k.split(':')[0] for k in self.t})

    def templates(self, c):
        """The group's own template, else the other classes of the same type (a two-class card is printed like either class's
        frame in shape; the colour test then picks whichever matches)."""
        g = group_of(c)
        styles = lambda gs: [x for x in self.groups if x.split('#')[0] in gs]   # the group and its frame styles
        if g + ':mean' in self.t: return styles([g])
        own = [x for x in self.groups if '#' not in x and x.startswith(c['type_code'] + '/') and x.split('/')[1].split('+')[0] in
               (c.get('faction_code'), c.get('faction2_code'))]
        return styles(own or [x for x in self.groups if '#' not in x and x.startswith(c['type_code'] + '/')])

    def mask(self, m, img=None):
        """Frame pixels of one card, in the card's own pixels (bool array H×W)."""
        c = self.cards[m['code']]
        img = img or Image.open(os.path.join(self.task, m['image'])).convert('RGB')
        a = soft(img)
        f = np.zeros(a.shape[:2], bool)
        for g in self.templates(c):
            mean, std = self.t[g + ':mean'], self.t[g + ':std']
            f |= (std < STEADY) & (np.abs(a - mean).max(axis=2) < MATCH)
        # textured parts of the print (the gold of a Seeker corner) match only in specks: close them into the area they
        # cover; then drop stray matches inside the picture, and widen by the scan's soft edge
        import cv2
        u = f.astype(np.uint8)
        u = cv2.morphologyEx(u, cv2.MORPH_CLOSE, np.ones((9, 9), np.uint8))
        u = cv2.morphologyEx(u, cv2.MORPH_OPEN, np.ones((5, 5), np.uint8))
        u = cv2.dilate(u, np.ones((5, 5), np.uint8))
        if img.size != SIZE: u = cv2.resize(u, img.size, interpolation=cv2.INTER_NEAREST)
        return u.astype(bool)


def main():
    task = sys.argv[1]
    fr = Frames(task)
    print('groups', len(fr.groups))
    if '--show' in sys.argv:
        codes = sys.argv[sys.argv.index('--show') + 1].split(',')
        out = os.path.join(task, 'frames'); by = {m['code']: m for m in fr.manifest}
        for code in codes:
            m = by[code]; img = Image.open(os.path.join(task, m['image'])).convert('RGB')
            f = fr.mask(m, img)
            a = np.asarray(img).copy(); a[f] = (a[f] * 0.3 + np.array([255, 0, 255]) * 0.7).astype(np.uint8)
            Image.fromarray(a).save(os.path.join(out, 'mask-%s.jpg' % code), quality=85)
            print(code, fr.cards[code]['type_code'], fr.templates(fr.cards[code]), 'frame %.2f' % f.mean())


if __name__ == '__main__':
    main()
