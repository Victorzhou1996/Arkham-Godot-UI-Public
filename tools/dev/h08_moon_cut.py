# H08: the moon behind the glass roof as its own layer (Yunke 11-20 "看看能不能让外面的月亮动起来").
# Takes the disc from H08-base.png (centre 604,44 r 41), fills the glazing bars inside it from the moon around them
# (cv2.inpaint, thin lines only) so the surface can turn under them, and leaves the bars out of the alpha: the base's own
# bars stay on top of the moving moon. Writes app/assets/bg/home/H08/H08-moon.png (full 1672x941 canvas).
import sys
import numpy as np, cv2
from PIL import Image
root = sys.argv[1]; prev = sys.argv[2] if len(sys.argv) > 2 else None
B = Image.open(root + '/app/assets/bg/home/H08/H08-base.png').convert('RGB')
b = np.asarray(B).copy()
cx, cy, r = 604, 44, 41
H, W = b.shape[:2]
yy, xx = np.mgrid[0:H, 0:W]
dist = np.hypot(xx - cx, yy - cy)
disc = np.clip(r - dist + 0.5, 0, 1).astype(np.float32)
L = b.astype(np.float32).mean(2)
bars = ((L < 88) & (dist < r + 2)).astype(np.uint8)
bars = cv2.dilate(bars, np.ones((3, 3), np.uint8))
fill = cv2.inpaint(b, bars, 4, cv2.INPAINT_TELEA)
keep = 1 - cv2.GaussianBlur(bars.astype(np.float32), (0, 0), 0.7)
a = np.clip(disc * keep, 0, 1)
rgba = np.dstack([fill, (a * 255).astype(np.uint8)])
Image.fromarray(rgba).save(root + '/app/assets/bg/home/H08/H08-moon.png')
print('bars px', int(bars.sum()), 'bbox', Image.fromarray(rgba).getchannel('A').getbbox())
if prev:
    c = Image.fromarray(np.dstack([fill, np.full((H, W), 255, np.uint8)])).crop((550, 0, 660, 95)).convert('RGB')
    m = Image.fromarray((a * 255).astype(np.uint8)).crop((550, 0, 660, 95)).convert('RGB')
    o = B.crop((550, 0, 660, 95))
    out = Image.new('RGB', (330, 95)); out.paste(o, (0, 0)); out.paste(c, (110, 0)); out.paste(m, (220, 0))
    out.resize((1320, 380), Image.NEAREST).save(prev)
