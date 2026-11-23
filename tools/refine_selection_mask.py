# Refine the hand-painted selection mask (app/assets/bg/selection-mask.png) against the painting itself.
# For each person colour, GrabCut is seeded with: certain foreground = eroded rough region,
# probable foreground = rough region, probable background = a ring around it, background elsewhere
# (other people's cores are forced to background). Output: selection-mask-refined.png (same palette)
# plus a debug overlay. The original mask is left untouched.
# Usage: python -I tools/refine_selection_mask.py "<Arkham UI root>"
import sys, os
import numpy as np
import cv2

root = sys.argv[1]
bg = os.path.join(root, "app", "assets", "bg")
paint = cv2.imread(os.path.join(bg, "selection.png"), cv2.IMREAD_COLOR)
rough = cv2.imread(os.path.join(bg, "selection-mask.png"), cv2.IMREAD_COLOR)[:, :, ::-1]  # to RGB
H, W = rough.shape[:2]
people = {"01001": (255, 0, 0), "01002": (0, 255, 0), "01003": (0, 0, 255), "01004": (255, 255, 0), "01005": (255, 0, 255)}
regions = {k: np.all(np.abs(rough.astype(int) - np.array(c)) < 60, axis=2).astype(np.uint8) for k, c in people.items()}
k = lambda r: cv2.getStructuringElement(cv2.MORPH_ELLIPSE, (2 * r + 1, 2 * r + 1))

# ── step 0: the hand mask was drawn on a shrunken copy of the painting (people sit ~15-25% closer to the
# centre). Fit x' = sx*x + tx, y' = sy*y + ty so the mask outlines land on strong edges of the painting.
gray = cv2.cvtColor(paint, cv2.COLOR_BGR2GRAY).astype(np.float32)
grad = cv2.GaussianBlur(cv2.magnitude(cv2.Sobel(gray, cv2.CV_32F, 1, 0, ksize=3), cv2.Sobel(gray, cv2.CV_32F, 0, 1, ksize=3)), (0, 0), 3)
pts = []
for reg in regions.values():
    cs, _ = cv2.findContours(reg, cv2.RETR_EXTERNAL, cv2.CHAIN_APPROX_NONE)
    for c in cs: pts.append(c.reshape(-1, 2)[::3])
pts = np.concatenate(pts).astype(np.float32)
def score(sx, tx, sy, ty):
    x = pts[:, 0] * sx + tx; y = pts[:, 1] * sy + ty
    ok = (x >= 0) & (x < W - 1) & (y >= 0) & (y < H - 1)
    if ok.mean() < 0.9: return -1
    return float(grad[y[ok].astype(int), x[ok].astype(int)].mean())
# seed from the people's centres (estimated by eye): x' ≈ 1.18x − 127, y' ≈ 1.30y − 104
best = (score(1.18, -127, 1.30, -104), 1.18, -127, 1.30, -104)
for step_s, step_t, span in [(0.03, 24, 3), (0.01, 8, 3), (0.004, 3, 3)]:
    _, bsx, btx, bsy, bty = best
    for sx in bsx + step_s * np.arange(-span, span + 1):
        for sy in bsy + step_s * np.arange(-span, span + 1):
            # keep the centre of the painting roughly in place while scaling; search the offset around that
            cx0 = btx + (bsx - sx) * W / 2; cy0 = bty + (bsy - sy) * H / 2
            for tx in cx0 + step_t * np.arange(-span, span + 1):
                for ty in cy0 + step_t * np.arange(-span, span + 1):
                    v = score(sx, tx, sy, ty)
                    if v > best[0]: best = (v, sx, tx, sy, ty)
_, sx, tx, sy, ty = best
print(f"alignment: x' = {sx:.3f}x + {tx:.1f}, y' = {sy:.3f}y + {ty:.1f} (edge score {best[0]:.1f}, identity {score(1, 0, 1, 0):.1f})")
M = np.float32([[sx, 0, tx], [0, sy, ty]])
regions = {code: cv2.warpAffine(reg, M, (W, H), flags=cv2.INTER_NEAREST) for code, reg in regions.items()}

# ── step 0b: one global fit cannot suit everyone (Daisy came out shifted onto the table). Nudge each person's
# region on its own: shift ±60 px and scale 0.85–1.2 around its centre, maximising edge strength on its outline.
def contour_pts(reg):
    cs, _ = cv2.findContours(reg, cv2.RETR_EXTERNAL, cv2.CHAIN_APPROX_NONE)
    return np.concatenate([c.reshape(-1, 2) for c in cs]).astype(np.float32) if cs else np.zeros((0, 2), np.float32)
def edge_score(p):
    x, y = p[:, 0], p[:, 1]; ok = (x >= 0) & (x < W - 1) & (y >= 0) & (y < H - 1)
    return float(grad[y[ok].astype(int), x[ok].astype(int)].mean()) if ok.mean() > 0.8 else -1
for code in list(regions):
    reg = regions[code]; P = contour_pts(reg)
    if len(P) == 0: continue
    c = P.mean(axis=0); best_l = (edge_score(P), 0.0, 0.0, 1.0, 1.0)
    for step, span in [(12, 5), (4, 3), (1.5, 3)]:
        _, bx, by, bsx, bsy = best_l
        for dx in bx + step * np.arange(-span, span + 1):
            for dy in by + step * np.arange(-span, span + 1):
                for ks in bsx + 0.02 * np.arange(-2, 3):
                    for kv in bsy + 0.02 * np.arange(-2, 3):
                        if not (0.85 <= ks <= 1.2 and 0.85 <= kv <= 1.2): continue
                        Q = (P - c) * np.array([ks, kv], np.float32) + c + np.array([dx, dy], np.float32)
                        v = edge_score(Q)
                        if v > best_l[0]: best_l = (v, dx, dy, ks, kv)
    v, dx, dy, ks, kv = best_l
    A = np.float32([[ks, 0, c[0] * (1 - ks) + dx], [0, kv, c[1] * (1 - kv) + dy]])
    regions[code] = cv2.warpAffine(reg, A, (W, H), flags=cv2.INTER_NEAREST)
    print(f"  {code}: shift ({dx:+.0f},{dy:+.0f}) scale ({ks:.2f},{kv:.2f}) edge {v:.1f}")

refined = {}
for code, reg in regions.items():
    ys, xs = np.nonzero(reg)
    pad = 90
    x0, x1, y0, y1 = max(0, xs.min() - pad), min(W, xs.max() + pad), max(0, ys.min() - pad), min(H, ys.max() + pad)
    gc = np.full((H, W), cv2.GC_BGD, np.uint8)
    gc[cv2.dilate(reg, k(45)) > 0] = cv2.GC_PR_BGD
    gc[cv2.dilate(reg, k(16)) > 0] = cv2.GC_PR_FGD
    gc[cv2.erode(reg, k(10)) > 0] = cv2.GC_FGD
    for other, oreg in regions.items():
        if other != code: gc[cv2.erode(oreg, k(6)) > 0] = cv2.GC_BGD
    sub = gc[y0:y1, x0:x1].copy()
    bgd, fgd = np.zeros((1, 65), np.float64), np.zeros((1, 65), np.float64)
    cv2.grabCut(paint[y0:y1, x0:x1], sub, None, bgd, fgd, 6, cv2.GC_INIT_WITH_MASK)
    fg = np.zeros((H, W), np.uint8)
    fg[y0:y1, x0:x1] = ((sub == cv2.GC_FGD) | (sub == cv2.GC_PR_FGD)).astype(np.uint8)
    fg |= (cv2.erode(reg, k(3)) > 0).astype(np.uint8)   # never lose the aligned hand-drawn core
    # keep the component(s) touching the certain core, close small gaps, fill holes
    fg = cv2.morphologyEx(fg, cv2.MORPH_CLOSE, k(4))
    n, lab = cv2.connectedComponents(fg)
    core = cv2.erode(reg, k(14)) > 0
    keep = np.zeros_like(fg)
    for i in range(1, n):
        comp = lab == i
        if (comp & core).any() or comp.sum() > 4000: keep[comp] = 1
    cnts, _ = cv2.findContours(keep, cv2.RETR_EXTERNAL, cv2.CHAIN_APPROX_NONE)
    filled = np.zeros_like(keep); cv2.drawContours(filled, cnts, -1, 1, thickness=cv2.FILLED)
    refined[code] = filled
    print(code, "rough px", int(reg.sum()), "refined px", int(filled.sum()))

out = np.zeros((H, W, 3), np.uint8)
claimed = np.zeros((H, W), np.int32)
# where two people claim a pixel, the one whose rough region covers it wins
for code, m in refined.items():
    take = (m > 0) & ((claimed == 0) | (regions[code] > 0))
    out[take] = people[code]; claimed[take] = 1
cv2.imwrite(os.path.join(bg, "selection-mask-refined.png"), out[:, :, ::-1])
dbg = paint.copy()
for code, m in refined.items():
    c = people[code][::-1]
    cnts, _ = cv2.findContours(m, cv2.RETR_EXTERNAL, cv2.CHAIN_APPROX_NONE)
    cv2.drawContours(dbg, cnts, -1, c, 2)
    rc, _ = cv2.findContours(regions[code], cv2.RETR_EXTERNAL, cv2.CHAIN_APPROX_NONE)
    cv2.drawContours(dbg, rc, -1, (200, 200, 200), 1)
os.makedirs(os.path.join(root, "evidence", "p0"), exist_ok=True)
cv2.imwrite(os.path.join(root, "evidence", "p0", "mask-refine-overlay.png"), dbg)
print("ok")
