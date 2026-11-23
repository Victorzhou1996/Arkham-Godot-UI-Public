# Campaign and scenario covers (Yunke 11-18: the campaign picker shows each campaign's cover from the html version, redrawn,
# not the investigator group pictures). Source: the html build's img/arkham/boxes/<id>.jpg (campaigns 01, 02: a square box
# front with the illustration in a band across its middle; scenarios <code>: a portrait pack front with the illustration in
# an arched window). Only the illustration is kept (no English title, no logos), then redrawn toward the painted look of the
# story pictures (same pass as tools/make_story_art.py). Writes new files only: app/assets/art-cover/<id>.webp
# Usage: python -I tools/make_covers.py "<Arkham UI root>" "<html img/arkham folder>"
import sys, os
import numpy as np
import cv2

root, src = sys.argv[1], sys.argv[2]
out_dir = os.path.join(root, "app", "assets", "art-cover"); os.makedirs(out_dir, exist_ok=True)

def read(path):
    return cv2.imdecode(np.fromfile(path, dtype=np.uint8), cv2.IMREAD_COLOR)

def write(path, img):
    ok, enc = cv2.imencode(".webp", img, [cv2.IMWRITE_WEBP_QUALITY, 92]); enc.tofile(path)

def quantize(img, k):
    z = img.reshape(-1, 3).astype(np.float32)
    crit = (cv2.TERM_CRITERIA_EPS + cv2.TERM_CRITERIA_MAX_ITER, 20, 1.0)
    _, lab, cen = cv2.kmeans(z, k, None, crit, 2, cv2.KMEANS_PP_CENTERS)
    return cen[lab.flatten()].reshape(img.shape).astype(np.uint8)

def redraw(img):
    h, w = img.shape[:2]
    if h < 900:
        s = 900 / h; img = cv2.resize(img, (int(w * s), 900), interpolation=cv2.INTER_CUBIC)
    st = cv2.stylization(img, sigma_s=60, sigma_r=0.32)
    q = quantize(cv2.bilateralFilter(st, 9, 36, 9), 40)
    out = cv2.addWeighted(cv2.GaussianBlur(q, (0, 0), 0.9), 0.8, st, 0.2, 0)
    f = out.astype(np.float32) / 255.0
    lum = (0.0722 * f[..., 0] + 0.7152 * f[..., 1] + 0.2126 * f[..., 2])[..., None]
    cool = np.array([0.86, 0.79, 0.64], np.float32); warm = np.array([0.86, 0.98, 1.06], np.float32)
    t = np.clip((lum - 0.08) / 0.4, 0, 1)
    f = f * (cool * (1 - t) + warm * t)
    return np.clip(f * 255, 0, 255).astype(np.uint8)

def crop(img, box):
    h, w = img.shape[:2]; x0, y0, x1, y1 = box
    return img[int(h * y0):int(h * y1), int(w * x0):int(w * x1)]

# the illustration inside each kind of front, as fractions (x0, y0, x1, y1)
CAMPAIGN = {"01": (0.012, 0.447, 0.988, 0.812), "02": (0.012, 0.405, 0.988, 0.825), "03": (0.012, 0.408, 0.988, 0.822)}
SCENARIO = (0.06, 0.25, 0.94, 0.765)
ids = list(CAMPAIGN) + ["01104", "01120", "01142", "02041", "02062", "02118", "02159", "02195", "02236", "02274", "02311",
                       "03043", "03061", "03120", "03159", "03200", "03240", "03274", "03316"]
if len(sys.argv) > 3: ids = [i for i in ids if i in sys.argv[3].split(",")]   # only these (new files; earlier covers stay untouched)
for i in ids:
    p = os.path.join(src, "boxes", i + ".jpg")
    if not os.path.exists(p): print("missing", p); continue
    img = read(p)
    # Yunke 11-18: the original picture, not the filtered redraw (it looked rough) — only the crop to the illustration
    write(os.path.join(out_dir, i + ".webp"), crop(img, CAMPAIGN.get(i, SCENARIO)))
    print("cover", i)
