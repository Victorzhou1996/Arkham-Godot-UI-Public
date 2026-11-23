"""White edges on printed card scans: straight white bands along an edge, and white rounded-corner triangles (a card scanned
on white paper). Finds, for every card image, how much to trim off each edge and the corner radius to make transparent, so
the cards sit cleanly on the dark table.  Writes app/assets/build-data/card-trim.json {code: [left, top, right, bottom,
radius]} — the four trims as fractions of the image's width / height, the radius as a fraction of the trimmed short side —
only for cards that need it, plus contact sheets (before / after on a dark background) to check by eye.
Detection is per pixel (whitish = bright and unsaturated; a corner counts only when its white patch is a small bounded
rounded-corner shape, so light card frames and white art are left alone).
Usage: python tools/dev/card_trim.py [sheet_dir]
"""
import json, os, sys
from concurrent.futures import ProcessPoolExecutor
import numpy as np
import cv2
from PIL import Image

ROOT = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
GREEN = os.path.join(os.path.dirname(ROOT), 'ArkhamHorrorLocal-WinDeploy-20260712', 'green-packages', 'green-1.0.39-fast-20261118', 'art')
LOCAL = os.path.join(ROOT, 'app', 'assets', 'cards')
OUT = os.path.join(ROOT, 'app', 'assets', 'build-data', 'card-trim.json')
DEFAULT_RAD = 0.045    # a real card's corner radius, as a fraction of its short side (63 mm card, about 3 mm)
MAX_RAD = 0.085


def items():
    idx = json.load(open(os.path.join(GREEN, 'index.json'), encoding='utf-8'))['paths']
    out = {}
    for k, v in idx.items():
        if k.startswith('cards/') and 'file' in v:
            out[k[6:].rsplit('.', 1)[0]] = os.path.join(GREEN, v['file'])
    for f in os.listdir(LOCAL):          # our own core / Dunwich scans win (Ui.Card reads them first)
        if f.endswith('.webp'):
            out[f[:-5]] = os.path.join(LOCAL, f)
    return out


def whitish(a):
    mn = a.min(axis=2).astype(np.int16); mx = a.max(axis=2).astype(np.int16)
    return (mn >= 212) & (mx - mn <= 40)


def analyse(job):
    code, path = job
    try:
        a = np.asarray(Image.open(path).convert('RGB'))
    except Exception:
        return code, None
    H, W = a.shape[:2]
    m = whitish(a)

    def band(rows, cap):
        n = 0
        for r in rows:
            if n >= cap or r.mean() < 0.85: break
            n += 1
        return n
    t = band(m, int(H * 0.05)); b = band(m[::-1], int(H * 0.05))
    l = band(m.T, int(W * 0.05)); r = band(m.T[::-1], int(W * 0.05))
    c = m[t:H - b, l:W - r]
    h, w = c.shape; short = min(w, h)
    # each corner: the largest radius R whose outside-the-arc patch is (nearly) all white — a scan's white corner
    cap = int(short * MAX_RAD)
    yy, xx = np.mgrid[0:cap, 0:cap]
    radii = []
    for flip_y in (False, True):
        for flip_x in (False, True):
            q = c[::-1 if flip_y else 1, ::-1 if flip_x else 1][:cap, :cap]
            best = 0
            for R in range(3, cap):
                out = ((xx[:R, :R] - R) ** 2 + (yy[:R, :R] - R) ** 2) > R * R
                if out.sum() == 0: continue
                if q[:R, :R][out].mean() >= 0.8: best = R
            radii.append(best)
    # a scan is often off-centre, so only one or two corners show white; a corner white right up to the cap is a light
    # frame or white art, not paper, and does not count
    bounded = [R for R in radii if 3 <= R < cap - 2]
    rad = (max(bounded) + 1.5) / short if bounded else 0.0
    # a card with white paper in a corner gets rounded corners at least as round as a real card's
    rad = 0.0 if rad == 0 else round(min(max(rad, DEFAULT_RAD), MAX_RAD), 4)
    if t + b + l + r == 0 and rad == 0: return code, None
    return code, [round(l / W, 4), round(t / H, 4), round(r / W, 4), round(b / H, 4), round(rad, 4)]


def sheet(results, paths, out_dir, per=40):
    os.makedirs(out_dir, exist_ok=True)
    codes = sorted(results)
    for s in range(0, len(codes), per):
        tiles = []
        for code in codes[s:s + per]:
            im = Image.open(paths[code]).convert('RGBA'); W, H = im.size
            l, t, r, b, rad = results[code]
            cut = im.crop((round(l * W), round(t * H), W - round(r * W), H - round(b * H)))
            cw, ch = cut.size; R = int(rad * min(cw, ch))
            if R > 0:
                mask = Image.new('L', cut.size, 0)
                from PIL import ImageDraw
                ImageDraw.Draw(mask).rounded_rectangle((0, 0, cw - 1, ch - 1), R, fill=255)
                cut.putalpha(mask)
            for img in (im, cut):
                img = img.copy(); img.thumbnail((150, 210))
                bg = Image.new('RGBA', (156, 228), (34, 26, 22, 255)); bg.alpha_composite(img, (3, 3))
                tiles.append(bg.convert('RGB'))
        cols = 10
        sh = Image.new('RGB', (156 * cols, 228 * ((len(tiles) + cols - 1) // cols)), (34, 26, 22))
        for i, tl in enumerate(tiles): sh.paste(tl, ((i % cols) * 156, (i // cols) * 228))
        sh.save(os.path.join(out_dir, 'trim-%02d.jpg' % (s // per)), quality=85)


if __name__ == '__main__':
    paths = items()
    with ProcessPoolExecutor() as ex:
        results = {c: v for c, v in ex.map(analyse, paths.items(), chunksize=16) if v}
    json.dump(dict(sorted(results.items())), open(OUT, 'w', encoding='utf-8'), separators=(',', ':'))
    edges = sum(1 for v in results.values() if sum(v[:4]) > 0); corners = sum(1 for v in results.values() if v[4] > 0)
    print('images', len(paths), 'need trim', len(results), 'edge bands', edges, 'white corners', corners)
    if len(sys.argv) > 1: sheet(results, paths, sys.argv[1])


def write_local(results, backup_dir):
    """Re-cut our own scans (app/assets/cards) that need it, in place, after copying the originals to backup_dir."""
    import shutil
    from PIL import ImageDraw
    os.makedirs(backup_dir, exist_ok=True)
    for code, (l, t, r, b, rad) in results.items():
        path = os.path.join(LOCAL, code + '.webp')
        if not os.path.exists(path): continue
        keep = os.path.join(backup_dir, code + '.webp')
        if not os.path.exists(keep): shutil.copy2(path, keep)
        im = Image.open(keep).convert('RGBA'); W, H = im.size
        cut = im.crop((round(l * W), round(t * H), W - round(r * W), H - round(b * H)))
        cw, ch = cut.size; R = int(round(rad * min(cw, ch)))
        if R > 0:
            big = 4; mask = Image.new('L', (cw * big, ch * big), 0)      # drawn large, scaled down: a smooth edge
            ImageDraw.Draw(mask).rounded_rectangle((0, 0, cw * big - 1, ch * big - 1), R * big, fill=255)
            cut.putalpha(mask.resize((cw, ch), Image.LANCZOS))
        cut.save(path, 'WEBP', quality=92, method=6)
        print('re-cut', code, (W, H), '->', cut.size, 'r', R)


if __name__ == '__main__' and '--write-local' in sys.argv:
    write_local(json.load(open(OUT, encoding='utf-8')), os.path.join(ROOT, 'backup', 'card-trim-20261119'))
