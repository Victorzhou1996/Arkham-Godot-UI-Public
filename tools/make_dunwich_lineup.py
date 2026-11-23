# The Dunwich Legacy's group picture for the investigator picker (Yunke 11-18 #2): like the core set's painted group scene,
# the five investigators stand together in one picture, with a per-person colour mask so the picker's hover spotlight and
# camera push work the same way. Built from each investigator's own card art (assets/art-fallback, the html scans): the
# figure is cut out (OpenCV GrabCut), stood in a row over a blurred Dunwich landscape (act 02277's picture), and the whole
# picture gets one grade and a ground fog so it reads as one scene. Writes new files only:
#   app/assets/bg/selection-dunwich.png and app/assets/bg/selection-dunwich-mask.png
# Usage: python -I tools/make_dunwich_lineup.py "<Arkham UI root>"
import sys, os
import numpy as np
import cv2

root = sys.argv[1]
A = os.path.join(root, "app", "assets")
W, H = 1672, 941
people = ["02001", "02002", "02003", "02004", "02005"]
colors = {"02001": (0, 0, 255), "02002": (0, 255, 0), "02003": (255, 0, 0), "02004": (0, 255, 255), "02005": (255, 0, 255)}  # BGR of red, green, blue, yellow, magenta

def read(p):
    return cv2.imdecode(np.fromfile(p, np.uint8), cv2.IMREAD_COLOR)

def write(p, img):
    ok, enc = cv2.imencode(".png", img); enc.tofile(p)

# ── backdrop: the landscape of act 02277, covering the frame, blurred and darkened (a misty Dunwich night)
card = read(os.path.join(A, "cards", "02277.webp"))
h, w = card.shape[:2]
land = card[0:int(h * 0.93), int(w * 0.62):w] if w > h else card[int(h * .06):int(h * .5), :]
s = max(W / land.shape[1], H / land.shape[0])
land = cv2.resize(land, (int(land.shape[1] * s) + 1, int(land.shape[0] * s) + 1), interpolation=cv2.INTER_CUBIC)
y0 = max(0, int((land.shape[0] - H) * 0.35)); x0 = (land.shape[1] - W) // 2
bg = land[y0:y0 + H, x0:x0 + W].astype(np.float32)
bg = cv2.GaussianBlur(bg, (0, 0), 9) * 0.55
# cool night tone
bg = bg * np.array([1.08, 0.95, 0.82], np.float32)

canvas = bg.copy()
mask = np.zeros((H, W, 3), np.uint8)

def cutout(img):
    h, w = img.shape[:2]
    gc = np.zeros((h, w), np.uint8); gc[:] = cv2.GC_PR_BGD
    # the figure stands in the middle; the card frame's top band and the outer edges are background
    gc[int(h * 0.10):h, int(w * 0.18):int(w * 0.82)] = cv2.GC_PR_FGD
    gc[int(h * 0.25):int(h * 0.9), int(w * 0.38):int(w * 0.62)] = cv2.GC_FGD
    gc[0:int(h * 0.05), :] = cv2.GC_BGD; gc[:, 0:int(w * 0.04)] = cv2.GC_BGD; gc[:, int(w * 0.96):] = cv2.GC_BGD
    # the class symbol glowing behind a figure (bright, pale) is background, away from the figure's middle column
    hsv = cv2.cvtColor(img, cv2.COLOR_BGR2HSV)
    pale = (hsv[..., 2] > 215) & (hsv[..., 1] < 60)
    side = np.ones((h, w), bool); side[:, int(w * 0.28):int(w * 0.72)] = False
    gc[pale & side] = cv2.GC_BGD
    bgm = np.zeros((1, 65), np.float64); fgm = np.zeros((1, 65), np.float64)
    cv2.grabCut(img, gc, None, bgm, fgm, 6, cv2.GC_INIT_WITH_MASK)
    a = np.where((gc == cv2.GC_FGD) | (gc == cv2.GC_PR_FGD), 1.0, 0.0).astype(np.float32)
    # keep the largest blob, close holes, soften the edge
    n, lab, stats, _ = cv2.connectedComponentsWithStats((a > 0).astype(np.uint8))
    if n > 1:
        big = 1 + np.argmax(stats[1:, cv2.CC_STAT_AREA]); a = (lab == big).astype(np.float32)
    a = cv2.morphologyEx(a, cv2.MORPH_CLOSE, np.ones((25, 25), np.uint8))
    # fill holes inside the figure (light clothing GrabCut took for background)
    filled = (a > 0.5).astype(np.uint8); ff = filled.copy(); pad = np.zeros((h + 2, w + 2), np.uint8)
    cv2.floodFill(ff, pad, (0, 0), 1); a = np.maximum(a, ((1 - ff) | filled).astype(np.float32))
    a = cv2.GaussianBlur(a, (0, 0), 2.2)
    # fade the bottom (legs melt into the ground fog)
    ramp = np.clip((h * 0.98 - np.arange(h)) / (h * 0.18), 0, 1)[:, None]
    return a * ramp

# ── the five figures, side by side with a little overlap, the middle ones a step forward
# centre x, foot y, scale: the middle one stands a step back, the outer ones a step forward (a loose arc, as in a group shot)
slots = [(0.11, 1.00, 0.86), (0.30, 0.97, 0.80), (0.50, 0.93, 0.74), (0.70, 0.97, 0.80), (0.89, 1.00, 0.86)]
order = [2, 1, 3, 0, 4]   # back to front
for i in order:
    code = people[i]
    img = read(os.path.join(A, "art-fallback", code + "-fallback.webp"))
    a = cutout(img)
    cx, foot, sc = slots[i]
    th = int(H * sc); tw = int(img.shape[1] * th / img.shape[0])
    img = cv2.resize(img, (tw, th), interpolation=cv2.INTER_CUBIC).astype(np.float32)
    a = cv2.resize(a, (tw, th), interpolation=cv2.INTER_LINEAR)
    x = int(cx * W - tw / 2); y = int(foot * H - th)
    sx0, sy0 = max(0, -x), max(0, -y); dx0, dy0 = max(0, x), max(0, y)
    ww = min(tw - sx0, W - dx0); hh = min(th - sy0, H - dy0)
    reg = (slice(dy0, dy0 + hh), slice(dx0, dx0 + ww)); src = (slice(sy0, sy0 + hh), slice(sx0, sx0 + ww))
    al = a[src][..., None]
    canvas[reg] = canvas[reg] * (1 - al) + img[src] * al
    m = (a[src] > 0.5)
    mask[reg][m] = colors[code]
    # a soft shadow / rim on the ground under each figure
canvas = np.clip(canvas, 0, 255)

# ── one grade for the whole picture (cool shadows, warm highlights) and a ground fog
f = canvas / 255.0
lum = (0.0722 * f[..., 0] + 0.7152 * f[..., 1] + 0.2126 * f[..., 2])[..., None]
t = np.clip((lum - 0.08) / 0.45, 0, 1)
f = f * (np.array([0.86, 0.80, 0.68], np.float32) * (1 - t) + np.array([0.88, 0.98, 1.06], np.float32) * t)
fog = np.clip((np.arange(H) - H * 0.70) / (H * 0.30), 0, 1)[:, None, None] ** 1.6
f = f * (1 - fog * 0.55) + np.array([0.20, 0.19, 0.17], np.float32) * fog * 0.55
# painterly pass so the five card scans read as one painting
out = np.clip(f * 255, 0, 255).astype(np.uint8)
out = cv2.addWeighted(cv2.stylization(out, sigma_s=45, sigma_r=0.25), 0.55, out, 0.45, 0)
# vignette
yy, xx = np.mgrid[0:H, 0:W]
v = 1 - 0.45 * np.clip(((xx - W / 2) / (W * 0.62)) ** 2 + ((yy - H * 0.45) / (H * 0.75)) ** 2, 0, 1)
out = np.clip(out * v[..., None], 0, 255).astype(np.uint8)

write(os.path.join(A, "bg", "selection-dunwich.png"), out)
write(os.path.join(A, "bg", "selection-dunwich-mask.png"), mask)
print("lineup written", out.shape)
