# H10: cut the drawer tentacle out of the ORIGINAL painting (the package's redrawn layer starts in mid air above an empty
# drawer; Yunke 11-20 "末端…感觉是悬空的"). Mask = the red of the tentacle in the original (largest piece, holes closed),
# plus a soft patch of the original's dark drawer inside around the root, so it comes out of the dark again.
# Writes app/assets/bg/home/H10/H10-tentacle-orig.png (the package's H10-tentacle.png stays as it was).
import sys
import numpy as np, cv2
from PIL import Image
root = sys.argv[1]; prev = sys.argv[2] if len(sys.argv) > 2 else None
O = Image.open(root + '/inbox/home-art-v2-20261120/originals/H10-investigation-desk.png').convert('RGB')
o = np.asarray(O)
hsv = np.asarray(O.convert('HSV')).astype(float)
h, s, v = hsv[..., 0] * 360 / 255, hsv[..., 1] / 255, hsv[..., 2] / 255
x0, y0, x1, y1 = 780, 690, 1140, 941
red = np.zeros(h.shape, np.uint8)
sub = ((h > 320) | (h < 8)) & (s > 0.35) & (v > 0.12)
red[y0:y1, x0:x1] = sub[y0:y1, x0:x1]
red = cv2.morphologyEx(red, cv2.MORPH_CLOSE, cv2.getStructuringElement(cv2.MORPH_ELLIPSE, (9, 9)))
n, lab, st, _ = cv2.connectedComponentsWithStats(red)
big = 1 + int(np.argmax(st[1:, cv2.CC_STAT_AREA]))
m = (lab == big).astype(np.uint8)
# fill holes
ff = m.copy() * 255; pad = np.zeros((ff.shape[0] + 2, ff.shape[1] + 2), np.uint8)
cv2.floodFill(ff, pad, (0, 0), 128); m = ((ff != 128)).astype(np.uint8)
m = cv2.dilate(m, cv2.getStructuringElement(cv2.MORPH_ELLIPSE, (5, 5)))
a = cv2.GaussianBlur(m.astype(np.float32), (0, 0), 1.2)
# the root inside the drawer: a soft patch of the original (dark inside, the body going back in)
patch = np.zeros_like(a)
cv2.ellipse(patch, (int(sys.argv[3]) if len(sys.argv) > 3 else 868, int(sys.argv[4]) if len(sys.argv) > 4 else 786), (58, 30), -18, 0, 360, 1.0, -1)
patch = cv2.GaussianBlur(patch, (0, 0), 9)
a = np.maximum(a, patch)
rgba = np.dstack([o, (np.clip(a, 0, 1) * 255).astype(np.uint8)])
Image.fromarray(rgba).save(root + '/app/assets/bg/home/H10/H10-tentacle-orig.png')
print('bbox', Image.fromarray(rgba).getchannel('A').getbbox())
if prev:
    b = Image.open(root + '/app/assets/bg/home/H10/H10-base.png').convert('RGBA')
    c = b.copy(); c.alpha_composite(Image.fromarray(rgba).crop((0, 0) + b.size))
    old = b.copy(); old.alpha_composite(Image.open(root + '/app/assets/bg/home/H10/H10-tentacle.png').convert('RGBA').crop((0, 0) + b.size))
    bx = (790, 700, 1130, 941); w, hh = bx[2] - bx[0], bx[3] - bx[1]
    out = Image.new('RGB', (w * 3, hh))
    out.paste(O.crop(bx), (0, 0)); out.paste(old.crop(bx).convert('RGB'), (w, 0)); out.paste(c.crop(bx).convert('RGB'), (w * 2, 0))
    out.resize((w * 6, hh * 2)).save(prev)
