# Story pictures for the act / agenda interludes (Yunke 11-18 #4): the illustration on the left of an agenda card, on the
# right of an act card (the printed landscape card), redrawn toward the painted look of the reference version's art
# (edge-aware smoothing, a limited palette, cool shadows and warm highlights). A card the reference version already
# redrew is not reused: those files are whole redrawn card faces with their text. Writes new files only: app/assets/art-story/<code>[b].webp
# Usage: python -I tools/make_story_art.py "<Arkham UI root>"
import sys, os, json, glob
import numpy as np
import cv2

root = sys.argv[1]
assets = os.path.join(root, "app", "assets")
out_dir = os.path.join(assets, "art-story"); os.makedirs(out_dir, exist_ok=True)

def load_cards():
    out = {}
    for f in ("core-cards.json", "dwl-cards.json"):
        data = json.load(open(os.path.join(assets, "data", f), encoding="utf-8"))
        for c in (data.values() if isinstance(data, dict) else data):
            out[str(c.get("code", ""))] = c
    return out

def read(path):
    buf = np.fromfile(path, dtype=np.uint8)
    return cv2.imdecode(buf, cv2.IMREAD_COLOR)

def write(path, img):
    ok, enc = cv2.imencode(".webp", img, [cv2.IMWRITE_WEBP_QUALITY, 90])
    enc.tofile(path)

def quantize(img, k):
    z = img.reshape(-1, 3).astype(np.float32)
    crit = (cv2.TERM_CRITERIA_EPS + cv2.TERM_CRITERIA_MAX_ITER, 20, 1.0)
    _, lab, cen = cv2.kmeans(z, k, None, crit, 2, cv2.KMEANS_PP_CENTERS)
    return cen[lab.flatten()].reshape(img.shape).astype(np.uint8)

def redraw(img):
    # upscale small crops first so the painterly pass works on enough pixels
    h, w = img.shape[:2]
    if h < 900:
        s = 900 / h; img = cv2.resize(img, (int(w * s), 900), interpolation=cv2.INTER_CUBIC)
    st = cv2.stylization(img, sigma_s=60, sigma_r=0.32)
    q = quantize(cv2.bilateralFilter(st, 9, 36, 9), 36)
    out = cv2.addWeighted(cv2.GaussianBlur(q, (0, 0), 0.9), 0.82, st, 0.18, 0)
    # grade: cool shadows, warm highlights (the table's film look)
    f = out.astype(np.float32) / 255.0
    lum = (0.0722 * f[..., 0] + 0.7152 * f[..., 1] + 0.2126 * f[..., 2])[..., None]
    cool = np.array([0.84, 0.77, 0.62], np.float32)   # BGR
    warm = np.array([0.85, 0.98, 1.07], np.float32)
    t = np.clip((lum - 0.08) / 0.4, 0, 1)
    f = f * (cool * (1 - t) + warm * t)
    return np.clip(f * 255, 0, 255).astype(np.uint8)

def crop(img, kind):
    h, w = img.shape[:2]
    if w < h:  # a portrait-oriented scan: use its upper picture area
        return img[int(h * 0.06):int(h * 0.5), int(w * 0.06):int(w * 0.94)]
    if kind == "agenda":
        return img[0:int(h * 0.93), 0:int(w * 0.39)]
    return img[0:int(h * 0.93), int(w * 0.62):w]

cards = load_cards()
made = kept = 0
for code, c in sorted(cards.items()):
    kind = c.get("type_code")
    if kind not in ("act", "agenda") or not code.startswith(("01", "02")):
        continue
    # the illustration is on the card's front; the back is mostly story text, so both names get the front's picture
    src = os.path.join(assets, "cards", code + ".webp")
    if not os.path.exists(src):
        continue
    img = read(src)
    if img is None:
        continue
    pic = crop(img, kind)   # Yunke 11-18: the card's own picture as printed (the filtered redraw looked rough)
    for side in ("", "b"):
        write(os.path.join(out_dir, code + side + ".webp"), pic)
    made += 1
print("story art: redrawn", made, "reference kept", kept, "->", out_dir)
