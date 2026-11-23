"""Where the face is in each investigator's portrait picture, so the player area's portrait (a wide strip, about 2.17 : 1, at the
lower left of the table) is centred on it instead of on the picture's top middle (Yunke 11-19: parallel Agnes's face fell out
of the frame).

The picture is the one the table uses (TableScreen.Layout PlayerArea): the redrawn art (app/assets/ref/art/cards/<code>-*.webp,
app/assets/art-fallback) if there is one, else the html table's portrait from the green package (portraits/<code>.jpg).
Faces: OpenCV cascades; people: torchvision Faster R-CNN v2 on the GPU (same weights as art_focus.py). The focus is the face
(the largest, then the highest), else the head of the largest person (the top quarter of the box), else the picture's centre.
Writes app/assets/build-data/portrait-focus.json {code: [fx, fy]} and a contact sheet with the visible window drawn.
Usage: python tools/dev/portrait_focus.py <sheet_dir>
"""
import glob, json, os, sys
import numpy as np
import cv2
from PIL import Image, ImageDraw

ROOT = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from crop_sheets import GREEN  # noqa: E402
import art_focus  # noqa: E402  (its detector)

WINDOW = 426 / 196   # the player area's portrait window, width / height

face_front = cv2.CascadeClassifier(os.path.join(cv2.data.haarcascades, 'haarcascade_frontalface_alt2.xml'))
face_side = cv2.CascadeClassifier(os.path.join(cv2.data.haarcascades, 'haarcascade_profileface.xml'))


def picture_for(code, entries):
    """The picture the table shows for this investigator (mirrors Ui.Art → ArtStore.Portrait, including ArtStore's cut of the
    printed card for parallel / reprint investigators whose html portrait is only another card's)."""
    for folder in ('app/assets/ref/art/cards', 'app/assets/art-fallback'):
        hits = sorted(f for f in glob.glob(os.path.join(ROOT, folder, code + '-*.webp')))
        if hits: return Image.open(hits[0]).convert('RGB'), 'art'
    e = entries.get('game/frontend/dist/img/arkham/portraits/' + code + '.jpg')
    if e is None: return None, None
    alias = os.path.splitext(os.path.basename(e.get('aliasOf', '')))[0] if e.get('aliasOf') else ''
    same_art = code.startswith('015') or code == '11008'
    if not code.endswith('b') and alias and alias != code and alias.rstrip('ab') != code.rstrip('ab') and not same_art:
        card = entries.get('cards/' + code + '.avif')
        if not card or 'file' not in card: return None, None
        img = Image.open(os.path.join(GREEN, card['file'])).convert('RGB')
        W, H = img.size
        if W < H: return None, None
        y0, h = 0.165 * H, 0.785 * H; w = 0.652 * h
        x = min(max(0.27 * W - w / 2, 0), 0.545 * W - w)
        return img.crop((int(x), int(y0), int(x + w), int(y0 + h))), 'cut'
    if 'file' not in e: return None, None
    return Image.open(os.path.join(GREEN, e['file'])).convert('RGB'), 'portrait'


def focus_of(img):
    w, h = img.size
    gray = cv2.cvtColor(np.asarray(img.convert('RGB'))[:, :, ::-1].copy(), cv2.COLOR_BGR2GRAY)
    faces = []
    for casc, flip in ((face_front, False), (face_side, False), (face_side, True)):
        g = cv2.flip(gray, 1) if flip else gray
        for (x, y, fw, fh) in casc.detectMultiScale(g, 1.08, 5, minSize=(int(w * 0.06), int(w * 0.06))):
            if flip: x = w - x - fw
            faces.append((x, y, fw, fh))
    people = [d for d in art_focus.detections(img) if d[5] == 1 and d[4] >= 3.0 * 0.6]
    # a portrait's face is in its upper part, and in the upper half of the main figure: a "face" low in the picture is a hand,
    # a trumpet or a fold of cloth (90024, 90049, 90078 on the first pass)
    faces = [f for f in faces if (f[1] + f[3] / 2) < 0.6 * h]
    if people:
        px0, py0, px1, py1, _, _ = max(people, key=lambda d: (d[2] - d[0]) * (d[3] - d[1]))
        inside = [f for f in faces if px0 <= f[0] + f[2] / 2 <= px1 and (f[1] + f[3] / 2) <= py0 + 0.5 * (py1 - py0)]
        if inside or faces: faces = inside
    if faces:
        # the largest face; among faces of similar size, the highest one (a portrait's subject, not a figure behind)
        big = max(f[2] * f[3] for f in faces)
        cand = sorted((f for f in faces if f[2] * f[3] >= 0.5 * big), key=lambda f: f[1])
        x, y, fw, fh = cand[0]
        return (x + fw / 2) / w, (y + fh * 0.6) / h, fh / h, 'face'
    if people:
        x0, y0, x1, y1, _, _ = max(people, key=lambda d: (d[2] - d[0]) * (d[3] - d[1]))
        # no face found: a head is about a third of the figure's width tall
        return (x0 + x1) / 2 / w, (y0 + (y1 - y0) * 0.18) / h, 0.33 * (x1 - x0) / h, 'person'
    return 0.5, 0.35, 0.0, 'none'


def main():
    out = sys.argv[1]; os.makedirs(out, exist_ok=True)
    entries = json.load(open(os.path.join(GREEN, 'index.json'), encoding='utf-8'))['paths']
    cards = json.load(open(os.path.join(ROOT, 'app/assets/build-data/cards-zh-cn.json'), encoding='utf-8'))['data']['all_card']
    codes = sorted({c['code'] for c in cards if c.get('type_code') == 'investigator' and not c.get('encounter_code')})
    result, cells = {}, []
    for code in codes:
        img, kind = picture_for(code, entries)
        if img is None: continue
        fx, fy, fsize, how = focus_of(img)
        # [fx, fy, face height] as fractions of the picture (face height 0 = unknown)
        result[code] = [round(fx, 3), round(fy, 3), round(fsize, 3)]
        cells.append((code, img, fx, fy, how, kind))
    # hand-checked corrections (tools/dev/portrait-focus-overrides.json) win over the detector
    ov_file = os.path.join(os.path.dirname(os.path.abspath(__file__)), 'portrait-focus-overrides.json')
    if os.path.exists(ov_file):
        for k, v in json.load(open(ov_file, encoding='utf-8')).items():
            if not k.startswith('_') and k in result: result[k] = v
    dst = os.path.join(ROOT, 'app', 'assets', 'build-data', 'portrait-focus.json')
    json.dump(result, open(dst, 'w', encoding='utf-8'), indent=0)
    print('wrote', dst, len(result), 'faces', sum(1 for c in cells if c[4] == 'face'), 'people', sum(1 for c in cells if c[4] == 'person'))
    # contact sheet: the picture with the window the table shows (cover-scaled to the window's width, centred on the focus)
    CW, CH, COLS = 150, 200, 10
    for s in range(0, len(cells), 60):
        chunk = cells[s:s + 60]; rows = (len(chunk) + COLS - 1) // COLS
        sheet = Image.new('RGB', (COLS * (CW + 4), rows * (CH + 16)), (18, 16, 14)); dr = ImageDraw.Draw(sheet)
        for i, (code, img, fx, fy, how, kind) in enumerate(chunk):
            r = img.copy(); r.thumbnail((CW, CH)); d = ImageDraw.Draw(r)
            w, h = r.size
            if w / h > WINDOW: ww, wh = h * WINDOW, h
            else: ww, wh = w, w / WINDOW
            x0 = min(max(fx * w - ww / 2, 0), w - ww); y0 = min(max(fy * h - wh * 0.45, 0), h - wh)
            d.rectangle([x0, y0, x0 + ww - 1, y0 + wh - 1], outline=(255, 190, 60), width=2)
            d.ellipse([fx * w - 3, fy * h - 3, fx * w + 3, fy * h + 3], fill=(80, 220, 255))
            X, Y = (i % COLS) * (CW + 4), (i // COLS) * (CH + 16)
            sheet.paste(r, (X, Y)); dr.text((X + 2, Y + CH + 2), code + ' ' + how + ' ' + kind[0], fill=(255, 220, 120))
        sheet.save(os.path.join(out, 'portraits-%02d.png' % (s // 60)))


if __name__ == '__main__':
    main()
