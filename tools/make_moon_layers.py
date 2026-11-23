# Moving moons for the title paintings (Yunke 11-22: "把窗外的月亮也做成动态的" / "改为远景的月亮移动").
# A mechanical split of the painting itself — nothing new is painted, nothing is generated: for each scene three full-canvas
# PNGs are written next to its base, drawn over it in this order:
#   <id>-moon-hole.png   the sky behind the moon: the disc (a little larger) filled from the sky round it (cv2.inpaint),
#                        so that where the moon moves off, sky shows rather than a second moon
#   <id>-moon.png        the moon disc alone (soft rim; the places where something in front covered it are filled from the
#                        moon's own surface), moved by the shader ("moonmove" in app/Scripts/Screens/HomeBackground.cs)
#   <id>-moon-front.png  what lies in front of the moon (clouds over it, branches, leaves, bats, the window's bar, the red
#                        wisp), cut from the base where the disc is and round it, so these stay where they are
# Moon vs what is in front of it is told apart by colour: the moon is cream (R about G or above, light), the sky and the
# clouds teal (G well above R), branches and bars dark, the wisp red. Inside the disc everything not moon is in front;
# round it (the band the moon can move into) dark things, the wisp and, where asked, the clouds that go on over the moon.
# Thin clouds over the moon (matte) are matted against it, so they do not keep its light where it moves off.
# The shader moves the disc at most along dir (home-scenes.json) — keep that, plus the sway, inside band.
# Usage: python -I tools/make_moon_layers.py "<Arkham UI root>" [H01 H06 ...] [--debug <dir>]
import sys, os
import numpy as np
import cv2

# c, r: centre and rim radius in base-image pixels, fitted to the rim (R-G contrast, 0.5 px steps); band = how far round the
# disc the front layer looks for things in front; cloud / dark / dark_out: R-G and brightness thresholds; band_cloud: how much
# lighter than the sky a cloud round the disc is; dbg_off = the travel (the shader's dir) and half of it, for --debug
SCENES = {
    "H01": dict(c=(1035.5, 126.5), r=83.0, band=18, cloud=-30, dark=62, dark_out=45, band_cloud=20, matte=True, dbg_off=[(2, -11), (1, -5.5)]),
    "H06": dict(c=(1449.5, 172.5), r=140.5, band=20, cloud=-30, dark=55, dark_out=32, band_cloud=20, matte=True, dbg_off=[(3, -13), (1.5, -6.5)]),
    "H07": dict(c=(1417.0, 106.5), r=81.5, band=18, cloud=-34, dark=95, dark_out=56, band_cloud=None, dbg_off=[(-3, -9), (-1.5, -4.5)]),
    "H10": dict(c=(1402.0, 58.0), r=36.5, band=12, cloud=-36, dark=95, dark_out=56, band_cloud=None, dbg_off=[(-3, -5), (-1.5, -2.5)]),
}


def smooth(e0, e1, x):
    t = np.clip((x - e0) / (e1 - e0), 0, 1)
    return t * t * (3 - 2 * t)


def write_png(path, rgba):
    ok, buf = cv2.imencode(".png", cv2.cvtColor(rgba, cv2.COLOR_RGBA2BGRA))
    buf.tofile(path)


def make(root, sid, cfg, debug):
    folder = os.path.join(root, "app", "assets", "bg", "home", sid)
    bgr = cv2.imdecode(np.fromfile(os.path.join(folder, sid + "-base.png"), np.uint8), cv2.IMREAD_COLOR)
    rgb = cv2.cvtColor(bgr, cv2.COLOR_BGR2RGB)
    H, W = rgb.shape[:2]
    a = rgb.astype(np.float32)
    cx, cy = cfg["c"]; R = cfg["r"]; band = cfg["band"]
    yy, xx = np.mgrid[0:H, 0:W].astype(np.float32)
    d = np.hypot(xx - cx, yy - cy)
    rg = cv2.GaussianBlur(a[..., 0] - a[..., 1], (0, 0), 0.8)
    lum = cv2.GaussianBlur(a @ np.float32([0.3, 0.59, 0.11]), (0, 0), 0.8)

    disc = d <= R
    # ── in front, inside the disc: everything that is not moon (teal clouds, dark branches, the red wisp)
    teal = 1 - smooth(cfg["cloud"] - 6, cfg["cloud"] + 6, rg)
    dark = 1 - smooth(cfg["dark"] - 20, cfg["dark"] + 10, lum)
    warm = cv2.GaussianBlur((a[..., 0] - a[..., 1]) - (a[..., 1] - a[..., 2]), (0, 0), 0.8)
    red = smooth(8, 25, warm)                      # the wisp: R over G while G is not over B (the moon's cream has G > B)
    # thin dark strokes on the moon (bat wings, twigs, a leaf's stalk) are lighter than branches: found as darker than the
    # moon right round them (a black-hat on brightness)
    k9 = cv2.getStructuringElement(cv2.MORPH_ELLIPSE, (9, 9))
    thin = smooth(cfg.get("thin", 26) - 8, cfg.get("thin", 26) + 8, cv2.morphologyEx(lum, cv2.MORPH_BLACKHAT, k9)) * (1 - smooth(85, 110, lum))
    occ_in = np.maximum.reduce([teal, dark, red, thin])
    core = d <= R - 2.5                            # the rim's own blend with the sky is not a cloud: judged apart below
    # small teal flecks wholly inside the disc are the moon's own markings, not clouds: drop them
    hard = ((occ_in > 0.5) & core).astype(np.uint8)
    n, lab, stats, _ = cv2.connectedComponentsWithStats(hard, connectivity=8)
    rimin = (d > R - 4.5) & core
    keep = np.zeros(n, bool); keep[np.unique(lab[rimin & (hard > 0)])] = True
    keep[stats[:, cv2.CC_STAT_AREA] >= cfg.get("min_blob", 400)] = True
    keep[0] = False
    darkish = (dark > 0.5) | (red > 0.5) | (thin > 0.5)   # dark, red and thin things are in front, however small (bats)
    occ_in = np.where(keep[lab] | darkish, occ_in, 0) * core
    # ── in front, round the disc (where the moon may move): things darker than the sky there (branches, leaves, bats, the
    # window's frame), the wisp, and — where asked — clouds lighter than the sky at that distance (the moon's glow is lighter
    # near the rim, so "lighter" is measured against the median brightness of the ring at the same distance)
    dark_o = 1 - smooth(cfg["dark_out"] - 8, cfg["dark_out"] + 8, lum)
    occ_out = np.maximum(dark_o, red)
    if cfg["band_cloud"] is not None:
        di = np.clip(np.round(d - R).astype(int), 0, band + 40)
        ringm = (d > R) & (d <= R + band + 40)
        base_l = np.array([np.median(lum[ringm & (di == k)]) if (ringm & (di == k)).any() else 0 for k in range(band + 41)])
        base_l = np.convolve(np.pad(base_l, 2, mode="edge"), np.ones(5) / 5, mode="valid")
        rel = lum - base_l[di]
        cloud_o = smooth(cfg["band_cloud"] - 8, cfg["band_cloud"] + 8, rel) * smooth(-18, -26, rg) * (d >= R + 2.5)
        # only clouds that go on over the moon are in front of it; one that stops at the rim was painted behind the disc
        bridge = ((occ_in > 0.5) | (cloud_o > 0.5) | ((d > R - 2.5) & (d < R + 2.5) & (teal > 0.5))).astype(np.uint8)
        n2, lab2 = cv2.connectedComponents(bridge, connectivity=8)
        over = np.zeros(n2, bool); over[np.unique(lab2[occ_in > 0.5])] = True; over[0] = False
        occ_out = np.maximum(occ_out, cloud_o * over[lab2])
    occ_out = occ_out * (d >= R + 2.5)
    # the rim (R-2.5 … R+2.5), where moon and sky blend: in front only where something plainly in front (not a darker
    # patch of sky) lies just inside or just outside it on the same ray from the centre — wholly when it crosses the rim,
    # else as far as the pixel itself does not look like moon; its colour then comes from that side (see front_rgb), so no
    # light of the old rim stays behind when the moon moves
    solid = ((occ_in > 0.5) | (occ_out > 0.5)).astype(np.float32)
    ang = np.arctan2(yy - cy, xx - cx)
    def ray(img, rr):
        return cv2.remap(img, (cx + rr * np.cos(ang)).astype(np.float32), (cy + rr * np.sin(ang)).astype(np.float32),
                         cv2.INTER_LINEAR, borderMode=cv2.BORDER_REPLICATE)
    in_s, out_s = ray(solid, R - 4), ray(solid, R + 4)
    nonmoon = np.maximum.reduce([teal, dark, red, thin])
    occ_rim = np.where((in_s > 0.5) & (out_s > 0.5), 1.0, np.maximum(in_s, out_s) * nonmoon)
    occ_rim = np.where(d >= R - 0.5, np.maximum(occ_rim, out_s), occ_rim).astype(np.float32)   # outside the disc: no gap
    occ_rim = cv2.GaussianBlur(occ_rim, (0, 0), 1.0)                 # evenly along the rim, not pixel by pixel
    rimz = (d > R - 2.5) & (d < R + 2.5)
    occ = np.where(core, occ_in, np.where(d >= R + 2.5, occ_out, occ_rim)).astype(np.float32)
    occ = occ * (1 - smooth(R + band - 4, R + band, d))
    # a pixel wider than what was found, so no fringe of it stays on the moon that moves
    # but not over what is plainly moon (the moon's outline drawn on through a cloud must move with the moon), nor on the rim
    moonlike = smooth(cfg["cloud"] + 4, cfg["cloud"] + 18, rg) * smooth(135, 170, lum) * (1 - red) * (1 - thin)
    wide = cv2.GaussianBlur(cv2.dilate(occ, np.ones((3, 3), np.uint8)), (0, 0), 0.7) * (1 - moonlike) * ~rimz
    occ = np.clip(np.maximum(cv2.GaussianBlur(occ, (0, 0), 0.6), wide) * 1.1, 0, 1).astype(np.float32)
    solid_out = ((np.maximum(dark_o, red) > 0.3) & (d > R) & (d <= R + band + 4)).astype(np.uint8)

    # ── the moon: its own surface, the covered parts and a margin outside the rim filled from it
    fill = cv2.dilate((occ > 0.08).astype(np.uint8), np.ones((5, 5), np.uint8)) | (d > R - 3)
    fill = fill.astype(np.uint8)
    x0, y0 = int(max(cx - R - 40, 0)), int(max(cy - R - 40, 0))
    x1, y1 = int(min(cx + R + 40, W)), int(min(cy + R + 40, H))
    sub = bgr[y0:y1, x0:x1].copy()
    fsub = fill[y0:y1, x0:x1].copy()
    # outside the margin nothing to keep: give inpaint only moon to work from
    moon_bgr = cv2.inpaint(sub, fsub, 7, cv2.INPAINT_TELEA)
    moon_rgb = np.zeros_like(rgb); moon_rgb[y0:y1, x0:x1] = cv2.cvtColor(moon_bgr, cv2.COLOR_BGR2RGB)
    moon_a = 1 - smooth(R - 1.4, R + 0.6, d)
    moon_a[d > R + 2] = 0

    # ── the hole: the disc (R + 2, past the rim's blend) filled from the sky round it
    # (dark things round it are left out of what the fill draws from, or a branch would smear grey into the sky)
    hole_m = ((d <= R + 2.0) | cv2.dilate(solid_out, np.ones((5, 5), np.uint8)).astype(bool)).astype(np.uint8)
    hsub = cv2.inpaint(bgr[y0:y1, x0:x1], hole_m[y0:y1, x0:x1], 9, cv2.INPAINT_TELEA)
    hole_rgb = rgb.copy(); hole_rgb[y0:y1, x0:x1] = cv2.cvtColor(hsub, cv2.COLOR_BGR2RGB)
    hole_a = 1 - smooth(R + 2.0, R + 4.0, d)

    def rgba(c, al):
        out = np.zeros((H, W, 4), np.uint8)
        out[..., :3] = c; out[..., 3] = np.clip(al * 255 + 0.5, 0, 255).astype(np.uint8)
        out[out[..., 3] == 0, :3] = 0
        return out

    hole = rgba(hole_rgb, hole_a)
    moon = rgba(moon_rgb, moon_a)
    # front colours on the rim come from the thing in front beside it (outside first), unless the pixel is itself dark or red;
    # just outside the rim a front pixel lighter than the cloud a little further out (the moon's light edge, the outline
    # painted on through haze) takes that cloud's colour
    own = (np.maximum(dark, red) > 0.5)[..., None]
    # (filled from the things in front only: everything else near the rim is left out of what inpaint draws from)
    rq = ray(rgb, R + 7)
    src = (solid > 0.5) & ~rimz & (np.abs(d - R) < 14)
    unknown = (~src & (np.abs(d - R) < 16)).astype(np.uint8)
    rim_fill = cv2.cvtColor(cv2.inpaint(bgr[y0:y1, x0:x1], unknown[y0:y1, x0:x1], 4, cv2.INPAINT_TELEA), cv2.COLOR_BGR2RGB)
    rim_full = rgb.copy(); rim_full[y0:y1, x0:x1] = rim_fill
    rim_rgb = np.where(own, rgb, rim_full)
    ql = rq.astype(np.float32) @ np.float32([0.3, 0.59, 0.11])
    edge = (d >= R + 2.5) & (d <= R + 6) & (lum > ql + 6) & (np.maximum(dark, red) < 0.5)
    front_rgb = np.where(rimz[..., None], rim_rgb, np.where(edge[..., None], rq, rgb))
    if cfg.get("matte"):
        # clouds over the moon are thin and lit from behind by it, lighter than the same clouds beside the disc. Over the disc
        # they are matted against what lies under them at rest (moon, or the filled sky past the rim): seen = a·cloud +
        # (1-a)·under, the cloud's own colour taken from the clouds round the disc (blurred). At rest that adds up to the
        # painting again; where the moon moves off, the cloud no longer carries the moon's light (and its old edge) along
        soft = (np.maximum(dark, red) < 0.5) & (thin < 0.5)
        w = (soft & (occ > 0.5) & (d >= R + 2.5) & (d < R + band + 40)).astype(np.float32)
        wb = cv2.GaussianBlur(w, (0, 0), 18)
        hz = cv2.GaussianBlur(a * w[..., None], (0, 0), 18) / np.maximum(wb, 1e-4)[..., None]
        hz[wb < 0.02] = np.median(a[w > 0], axis=0)
        under = moon_a[..., None] * moon_rgb.astype(np.float32) + (1 - moon_a[..., None]) * hole_rgb.astype(np.float32)
        v = hz - under
        al = np.clip(((a - under) * v).sum(2) / np.maximum((v * v).sum(2), 1.0), 0, 1)
        # (and the rim's own pixels where a cloud dims them, even if not found as a cloud: else the moon's edge shines through)
        ul = under @ np.float32([0.3, 0.59, 0.11])
        cloudy = cv2.dilate((occ > 0.3).astype(np.uint8), np.ones((11, 11), np.uint8)) > 0
        m_in = soft & ((occ > 0.02) | ((d > R - 3) & (lum < ul - 15) & cloudy)) & (d < R + 2)
        col = np.where((al >= 0.15)[..., None], np.clip(under + (a - under) / np.maximum(al, 0.15)[..., None], 0, 255), hz)
        front_rgb = np.where(m_in[..., None], col.astype(np.uint8), front_rgb)
        occ = np.where(m_in, al, occ).astype(np.float32)
    front = rgba(front_rgb, occ)
    # keep colour under transparent texels of moon/front from bleeding black when filtered: extend the colour outwards
    for img, src in ((moon, moon_rgb), (front, front_rgb), (hole, hole_rgb)):
        z = img[..., 3] == 0
        img[z, :3] = src[z]
    write_png(os.path.join(folder, sid + "-moon-hole.png"), hole)
    write_png(os.path.join(folder, sid + "-moon.png"), moon)
    write_png(os.path.join(folder, sid + "-moon-front.png"), front)
    print(sid, "written; front px", int((occ > 0.5).sum()), "disc px", int(disc.sum()))

    if debug:
        os.makedirs(debug, exist_ok=True)
        m = int(R + band + 30)
        bx0, by0, bx1, by1 = int(max(cx - m, 0)), int(max(cy - m, 0)), int(min(cx + m, W)), int(min(cy + m, H))
        k = max(2, 900 // (bx1 - bx0))
        def crop(img):
            c = img[by0:by1, bx0:bx1]
            return cv2.resize(c, ((bx1 - bx0) * k, (by1 - by0) * k), interpolation=cv2.INTER_NEAREST)
        base_c = crop(rgb)
        ov = rgb.astype(np.float32).copy()
        ov = ov * (1 - 0.6 * occ[..., None]) + np.float32([255, 0, 255]) * 0.6 * occ[..., None]
        cv2.circle(ov, (int(cx), int(cy)), int(R), (255, 255, 0), 1)
        def comp(off):
            o = np.float32(off)
            M = np.float32([[1, 0, o[0]], [0, 1, o[1]]])
            ma = cv2.warpAffine(moon.astype(np.float32), M, (W, H))
            out = a.copy()
            for layer in (hole.astype(np.float32), ma, front.astype(np.float32)):
                al = layer[..., 3:4] / 255
                out = out * (1 - al) + layer[..., :3] * al
            return out
        offs = cfg.get("dbg_off", [(8, -10), (-8, 8)])
        sheet = np.concatenate([np.concatenate([base_c, crop(ov.astype(np.uint8))], 1),
                                np.concatenate([crop(np.clip(comp(o), 0, 255).astype(np.uint8)) for o in offs], 1)], 0)
        cv2.imwrite(os.path.join(debug, sid + "-sheet.png"), cv2.cvtColor(sheet, cv2.COLOR_RGB2BGR))
        cv2.imwrite(os.path.join(debug, sid + "-moonlayer.png"), crop(np.where(moon[..., 3:4] > 0, moon[..., :3], 0).astype(np.uint8))[..., ::-1])
        cv2.imwrite(os.path.join(debug, sid + "-hole.png"), crop(hole_rgb)[..., ::-1])


if __name__ == "__main__":
    args = sys.argv[1:]
    debug = None
    if "--debug" in args:
        i = args.index("--debug"); debug = args[i + 1]; del args[i:i + 2]
    root = args[0]
    for sid in (args[1:] or list(SCENES)):
        make(root, sid, SCENES[sid], debug)
