"""Where the subject of each card's illustration is, so the small cards and strips cut around it instead of the middle.

For every player card without a redrawn picture: take the art region of the printed card (the same frames as
DeckPreview.ArtFrame), find what draws the eye — faces (OpenCV cascades, frontal and profile) weigh most, then a fine-grained
saliency map and local contrast — and pick
  · fx: the centre of the best window at the small card's proportions (width / height = 112 / 157) across the region;
  · fy: the centre of the best band at the strip's proportions (about 4.3 : 1) down the region.
Writes app/assets/build-data/art-focus.json {code: [fx, fy]} (fractions of the region) and contact sheets that draw both
windows over the region, to check by eye.  Usage: python tools/dev/art_focus.py <sheet_dir> [limit]
Objects: torchvision's Faster R-CNN v2 (COCO weights, downloaded once to the torch cache) on the GPU — people, animals, things.
"""
import json, os, sys
import numpy as np
import cv2
from PIL import Image, ImageDraw

ROOT = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from crop_sheets import GREEN, FRAMES, OTHER, cards_needing_crops  # noqa: E402

SMALL = 112 / 157       # small card width / height
STRIP = 4.3             # strip band width / height
W = 360                 # working width of a region

face_front = cv2.CascadeClassifier(os.path.join(cv2.data.haarcascades, 'haarcascade_frontalface_alt2.xml'))
face_side = cv2.CascadeClassifier(os.path.join(cv2.data.haarcascades, 'haarcascade_profileface.xml'))


def spectral_residual(gray):
    """Spectral-residual saliency (Hou & Zhang 2007), on a 96-px-wide copy, back at full size."""
    h, w = gray.shape; sw = 96; sh = max(8, int(sw * h / w))
    g = cv2.resize(gray, (sw, sh)).astype(np.float32)
    f = np.fft.fft2(g); amp = np.log(np.abs(f) + 1e-6); ph = np.angle(f)
    res = amp - cv2.blur(amp, (3, 3))
    sal = np.abs(np.fft.ifft2(np.exp(res + 1j * ph))) ** 2
    sal = cv2.GaussianBlur(sal.astype(np.float32), (0, 0), 2.5)
    return cv2.resize(sal, (w, h))


# ── the object detector (torchvision Faster R-CNN v2, COCO; on the GPU when CUDA is there) ──
_det = None
def detector():
    global _det
    if _det is None:
        try:
            import torch
            from torchvision.models.detection import fasterrcnn_resnet50_fpn_v2, FasterRCNN_ResNet50_FPN_V2_Weights
            dev = 'cuda' if torch.cuda.is_available() else 'cpu'
            m = fasterrcnn_resnet50_fpn_v2(weights=FasterRCNN_ResNet50_FPN_V2_Weights.COCO_V1, box_score_thresh=0.35).eval().to(dev)
            _det = (torch, m, dev)
            print('detector on', dev)
        except Exception as e:
            print('no detector:', e); _det = False
    return _det or None


# COCO labels: 1 person; 16–25 animals; the rest objects (a knife, a book, a clock… often the subject of an item card)
def class_weight(label):
    if label == 1: return 3.0
    if 16 <= label <= 25: return 2.6
    return 1.3


def detections(region):
    """[(x0, y0, x1, y1, weight)] in the region's own pixels."""
    d = detector()
    if d is None: return []
    torch, m, dev = d
    a = np.asarray(region.convert('RGB'), dtype=np.float32) / 255.0
    t = torch.from_numpy(a).permute(2, 0, 1).to(dev)
    with torch.no_grad():
        out = m([t])[0]
    res = []
    for box, label, score in zip(out['boxes'].tolist(), out['labels'].tolist(), out['scores'].tolist()):
        res.append((*box, class_weight(label) * score, label))
    return res


def encounter_enemies():
    """Every encounter enemy (all cycles) without a redrawn picture; the art is in the lower part, as on weakness enemies."""
    d = json.load(open(os.path.join(ROOT, 'app/assets/build-data/cards-zh-cn.json'), encoding='utf-8'))['data']['all_card']
    drawn = set()
    for folder in ('app/assets/ref/art/cards', 'app/assets/art-fallback'):
        drawn |= {f[:5] for f in os.listdir(os.path.join(ROOT, folder)) if not f.endswith('.import')}
    seen, out = set(), []
    for c in sorted(d, key=lambda c: c['code']):
        if c.get('type_code') != 'enemy' or not c.get('encounter_code') or c.get('duplicate_of_code') or c.get('hidden'): continue
        if c['code'] in drawn or c['code'] in seen: continue
        seen.add(c['code']); out.append(c)
    return out


def frame_for(c):
    f = FRAMES.get(c['type_code'], OTHER)
    if c['code'].startswith('98') or c['code'].startswith('90'):
        f = (f[0], f[1] + 0.05, f[2], f[3] - 0.05)
    return f


def region_of(img, f):
    x, y, w, h = f; Wi, Hi = img.size
    return img.crop((int(x * Wi), int(y * Hi), int((x + w) * Wi), int((y + h) * Hi)))


def interest(region):
    """A weight map of what draws the eye in the region (float32, W x H)."""
    r = region.resize((W, max(8, int(W * region.size[1] / region.size[0]))))
    a = np.asarray(r.convert('RGB'))
    bgr = a[:, :, ::-1].copy()
    gray = cv2.cvtColor(bgr, cv2.COLOR_BGR2GRAY)
    sal = cv2.GaussianBlur(spectral_residual(gray), (0, 0), 6)
    # local contrast: detail and edges (painted subjects are drawn with more detail than their backgrounds)
    lap = np.abs(cv2.Laplacian(cv2.GaussianBlur(gray, (0, 0), 1.2), cv2.CV_32F))
    lap = cv2.GaussianBlur(lap, (0, 0), 8)
    m = sal / (sal.max() + 1e-6) + 0.6 * lap / (lap.max() + 1e-6)
    # faces weigh most: a soft blob over each face, a little below it too (the body under a head)
    faces = []
    for casc, flip in ((face_front, False), (face_side, False), (face_side, True)):
        g = cv2.flip(gray, 1) if flip else gray
        for (x, y, w, h) in casc.detectMultiScale(g, 1.1, 4, minSize=(18, 18)):
            if flip: x = gray.shape[1] - x - w
            faces.append((x, y, w, h))
    fm = np.zeros_like(m)
    for (x, y, w, h) in faces:
        cx, cy = x + w / 2, y + h * 0.9
        yy, xx = np.mgrid[0:m.shape[0], 0:m.shape[1]]
        fm += np.exp(-(((xx - cx) / (w * 1.1)) ** 2 + ((yy - cy) / (h * 1.6)) ** 2))
    if faces: m = m + 3.0 * fm / (fm.max() + 1e-6)
    # what the detector finds: people (their upper part most: the head and shoulders), animals, then objects
    sx, sy = m.shape[1] / region.size[0], m.shape[0] / region.size[1]
    dm = np.zeros_like(m); found = 0
    for (x0, y0, x1, y1, wgt, label) in detections(region):
        x0, x1, y0, y1 = x0 * sx, x1 * sx, y0 * sy, y1 * sy
        bw, bh = max(1.0, x1 - x0), max(1.0, y1 - y0)
        # a box covering most of the picture tells nothing about where to look — except a person filling the picture: the
        # figure itself is the subject (Hunter's Armor: the armoured body, not the bright grave cross beside it; Yunke 11-19)
        if bw * bh > 0.8 * m.shape[0] * m.shape[1] and label != 1: continue
        yy, xx = np.mgrid[0:m.shape[0], 0:m.shape[1]]
        cy = y0 + (0.28 if label == 1 else 0.5) * bh
        dm += wgt * np.exp(-(((xx - (x0 + x1) / 2) / (bw * 0.55)) ** 2 + ((yy - cy) / (bh * (0.45 if label == 1 else 0.6))) ** 2))
        found += 1
    if found: m = m + 2.5 * dm / (dm.max() + 1e-6)
    # the card frame's ornaments reach into the region's corners and edges (arches, corner flourishes): damp the outer strip
    xs = np.linspace(0, 1, m.shape[1])[None, :]
    m *= np.clip(np.minimum(xs, 1 - xs) / 0.08, 0.15, 1.0)
    # a slight pull to the middle: an even picture stays centred
    yy, xx = np.mgrid[0:m.shape[0], 0:m.shape[1]]
    m *= 1.0 - 0.18 * (((xx / m.shape[1]) - 0.5) ** 2 * 4)
    return m.astype(np.float32), len(faces) + 100 * found


def best_window(profile, win):
    """Centre (fraction) of the window of length `win` with the largest sum along a 1-D profile."""
    n = len(profile)
    if win >= n: return 0.5
    cs = np.concatenate([[0], np.cumsum(profile)])
    sums = cs[win:] - cs[:-win]
    i = int(np.argmax(sums))
    return (i + win / 2) / n


def main():
    # --enemies: the encounter enemies without a redrawn picture (Yunke 11-19: "在所有怪物卡上也做一遍"), merged into the file
    enemies = '--enemies' in sys.argv; argv = [a for a in sys.argv[1:] if a != '--enemies']
    out_dir = argv[0]; limit = int(argv[1]) if len(argv) > 1 else 0
    os.makedirs(out_dir, exist_ok=True)
    idx = json.load(open(os.path.join(GREEN, 'index.json'), encoding='utf-8'))['paths']
    files = {os.path.splitext(k[6:])[0]: v['file'] for k, v in idx.items() if k.startswith('cards/') and 'file' in v}
    cards = [c for c in (encounter_enemies() if enemies else cards_needing_crops()) if c['code'] in files]
    if limit: cards = cards[:limit]
    focus, cells = {}, []
    for n, c in enumerate(cards):
        img = Image.open(os.path.join(GREEN, files[c['code']])).convert('RGB')
        reg = region_of(img, frame_for(c))
        m, nf = interest(reg)
        h, w = m.shape
        fx = best_window(m.sum(axis=0), max(1, int(round(h * SMALL))))
        fy = best_window(m.sum(axis=1), max(1, int(round(w / STRIP)))) if w / STRIP < h else 0.5
        focus[c['code']] = [round(fx, 3), round(fy, 3)]
        cells.append((c['code'], reg, fx, fy, nf))
        if n % 200 == 0: print('…', n, '/', len(cards))
    path = os.path.join(ROOT, 'app', 'assets', 'build-data', 'art-focus.json')
    if enemies and os.path.exists(path):       # keep the player cards' results, add / replace the enemies'
        old = json.load(open(path, encoding='utf-8')); old.update(focus); focus = old
    json.dump(focus, open(path, 'w', encoding='utf-8'), indent=0)
    print('wrote', path, len(focus))
    # contact sheets: the region, the small-card window (amber) and the strip band (cyan)
    CW, CH, COLS, ROWS = 180, 107, 8, 8
    per = COLS * ROWS
    for s in range(0, len(cells), per):
        sheet = Image.new('RGB', (COLS * (CW + 4), ROWS * (CH + 16)), (18, 16, 14)); dr = ImageDraw.Draw(sheet)
        for i, (code, reg, fx, fy, nf) in enumerate(cells[s:s + per]):
            r = reg.resize((CW, CH)); d = ImageDraw.Draw(r)
            ww = CH * SMALL; x0 = fx * CW - ww / 2; x0 = max(0, min(CW - ww, x0))
            d.rectangle([x0, 0, x0 + ww, CH - 1], outline=(255, 190, 60), width=2)
            bh = CW / STRIP; y0 = fy * CH - bh / 2; y0 = max(0, min(CH - bh, y0))
            d.rectangle([0, y0, CW - 1, y0 + bh], outline=(80, 220, 255), width=1)
            X, Y = (i % COLS) * (CW + 4), (i // COLS) * (CH + 16)
            tag = (' face' if nf % 100 else '') + (' det' if nf >= 100 else '')
            sheet.paste(r, (X, Y)); dr.text((X + 2, Y + CH + 2), code + tag, fill=(255, 220, 120))
        sheet.save(os.path.join(out_dir, 'focus-%02d.png' % (s // per)))


if __name__ == '__main__':
    main()
