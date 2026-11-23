# Trial: push the painted backgrounds toward the flatter, concept-art look of the reference version
# (fewer textures, broad flat planes, limited palette). Writes new files only:
#   assets-source/stylized-trial/<name>-<variant>.png  and  evidence/p0/stylize-sheet-<name>.jpg
# Usage: python -I tools/stylize_trial.py "<Arkham UI root>" selection hallway ...
import sys, os
import numpy as np
import cv2

root = sys.argv[1]
names = sys.argv[2:] or ["selection", "hallway"]
src_dir = os.path.join(root, "app", "assets", "bg")
out_dir = os.path.join(root, "assets-source", "stylized-trial"); os.makedirs(out_dir, exist_ok=True)
ev_dir = os.path.join(root, "evidence", "p0"); os.makedirs(ev_dir, exist_ok=True)

def quantize(img, k):
    z = img.reshape(-1, 3).astype(np.float32)
    crit = (cv2.TERM_CRITERIA_EPS + cv2.TERM_CRITERIA_MAX_ITER, 20, 1.0)
    _, lab, cen = cv2.kmeans(z, k, None, crit, 2, cv2.KMEANS_PP_CENTERS)
    return cen[lab.flatten()].reshape(img.shape).astype(np.uint8)

def variant_a(img):   # "flat planes": mean-shift + palette + soft re-blend
    ms = cv2.pyrMeanShiftFiltering(img, 14, 28)
    q = quantize(cv2.bilateralFilter(ms, 9, 40, 9), 28)
    return cv2.addWeighted(cv2.GaussianBlur(q, (0, 0), 1.2), 0.85, ms, 0.15, 0)

def variant_b(img):   # "concept sketch": OpenCV stylization (edge-aware smoothing) + gentle palette
    st = cv2.stylization(img, sigma_s=80, sigma_r=0.35)
    return quantize(st, 40)

def variant_c(img):   # "graphic": strong flattening, dark linework from the luminance edges
    ms = cv2.pyrMeanShiftFiltering(img, 20, 36)
    q = quantize(ms, 18)
    g = cv2.cvtColor(cv2.GaussianBlur(img, (0, 0), 2), cv2.COLOR_BGR2GRAY)
    edges = cv2.Canny(g, 40, 110)
    edges = cv2.dilate(edges, np.ones((2, 2), np.uint8))
    out = q.copy(); out[edges > 0] = (out[edges > 0] * 0.45).astype(np.uint8)
    return cv2.GaussianBlur(out, (0, 0), 0.6)

for name in names:
    img = cv2.imread(os.path.join(src_dir, name + ".png"), cv2.IMREAD_COLOR)
    if img is None: print("missing", name); continue
    tiles = [("原图", img)]
    for key, fn, label in [("a", variant_a, "A 平涂色块"), ("b", variant_b, "B 概念速写"), ("c", variant_c, "C 图形化描边")]:
        out = fn(img)
        cv2.imwrite(os.path.join(out_dir, f"{name}-{key}.png"), out)
        tiles.append((label, out))
    # 2x2 comparison sheet at half size, plus a 1:1 detail crop row
    h, w = img.shape[:2]; hw, hh = w // 2, h // 2
    sheet = np.zeros((hh * 2, hw * 2, 3), np.uint8)
    for i, (_, t) in enumerate(tiles):
        sheet[(i // 2) * hh:(i // 2 + 1) * hh, (i % 2) * hw:(i % 2 + 1) * hw] = cv2.resize(t, (hw, hh), interpolation=cv2.INTER_AREA)
    cv2.imwrite(os.path.join(ev_dir, f"stylize-sheet-{name}.jpg"), sheet, [cv2.IMWRITE_JPEG_QUALITY, 90])
    cx, cy, cw, ch = int(w * 0.55), int(h * 0.12), 420, 420
    crop = np.concatenate([t[cy:cy + ch, cx:cx + cw] for _, t in tiles], axis=1)
    cv2.imwrite(os.path.join(ev_dir, f"stylize-detail-{name}.jpg"), crop, [cv2.IMWRITE_JPEG_QUALITY, 92])
    print("done", name)
