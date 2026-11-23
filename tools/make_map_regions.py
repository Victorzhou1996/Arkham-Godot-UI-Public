# Region mask for a painted map (Yunke 11-18: light the places of the map the way the investigator pickers light people —
# the hovered place stays lit, the rest drops to a silhouette, the places that can be acted on glow along their own outline).
# For each hotspot of the scenario (app/assets/scenarios/<code>.json, map.hotspots, in file order) GrabCut finds the
# building / room inside its box: the box is "probably this place", its middle "surely", the other places' middles
# "surely not". Output: app/assets/bg/<map image>-regions.png, red channel = (index + 1) * 20, 0 = no place
# (+ <map image>-regions-check.jpg to look at). A place GrabCut cannot find falls back to its box with rounded corners.
# map.shapes.<label> (a polygon, fractions of the picture) replaces GrabCut for that place (the house's parlour and cellar).
# Usage: python -I tools/make_map_regions.py "<Arkham UI root>" <scenario code> [<scenario code> ...]
import sys, os, json
import numpy as np
import cv2

root = sys.argv[1]
A = os.path.join(root, "app", "assets")
for code in sys.argv[2:]:
    sc = json.load(open(os.path.join(A, "scenarios", code + ".json"), encoding="utf-8"))
    mp = sc["map"]; name = mp["image"]; spots = list(mp["hotspots"].items())
    img = cv2.imdecode(np.fromfile(os.path.join(A, "bg", name + ".png"), np.uint8), cv2.IMREAD_COLOR)
    H, W = img.shape[:2]
    def rect(f): return (int(f[0] * W), int(f[1] * H), int((f[0] + f[2]) * W), int((f[1] + f[3]) * H))
    def core(f, k=0.28):
        x0, y0, x1, y1 = rect(f); dx, dy = int((x1 - x0) * k), int((y1 - y0) * k)
        return (x0 + dx, y0 + dy, x1 - dx, y1 - dy)
    claims = []
    shapes = mp.get("shapes", {})   # hand-drawn outlines (fractions of the picture) for places GrabCut gets wrong
    for i, (label, f) in enumerate(spots):
        x0, y0, x1, y1 = rect(f)
        if label in shapes:
            a = np.zeros((H, W), np.uint8)
            cv2.fillPoly(a, [np.array([[int(px * W), int(py * H)] for px, py in shapes[label]], np.int32)], 1)
            er = max(3, int(min(W, H) * 0.006)); a = cv2.erode(a, np.ones((er, er), np.uint8))
            claims.append(a); print(code, label, int(a.sum()), "px (shape)")
            continue
        gc = np.full((H, W), cv2.GC_BGD, np.uint8)
        gc[y0:y1, x0:x1] = cv2.GC_PR_FGD
        c = core(f); gc[c[1]:c[3], c[0]:c[2]] = cv2.GC_FGD
        for j, (_, g) in enumerate(spots):
            if j == i: continue
            o = core(g, 0.2); gc[o[1]:o[3], o[0]:o[2]] = cv2.GC_BGD
        # a margin outside the box may still be the building (roofs, steps); beyond that, surely not
        m = 0.03   # Yunke 11-18: a place's mask covers its body only; places need not touch (the lines show the connections)
        mx0, my0, mx1, my1 = rect((f[0] - m, f[1] - m, f[2] + 2 * m, f[3] + 2 * m))
        mx0, my0 = max(0, mx0), max(0, my0)
        band = np.zeros((H, W), bool); band[my0:my1, mx0:mx1] = True; band[y0:y1, x0:x1] = False
        gc[band & (gc == cv2.GC_BGD)] = cv2.GC_PR_BGD
        bgm = np.zeros((1, 65), np.float64); fgm = np.zeros((1, 65), np.float64)
        try: cv2.grabCut(img, gc, None, bgm, fgm, 5, cv2.GC_INIT_WITH_MASK)
        except cv2.error: pass
        a = ((gc == cv2.GC_FGD) | (gc == cv2.GC_PR_FGD)).astype(np.uint8)
        n, lab, stats, _ = cv2.connectedComponentsWithStats(a)
        keep = set(np.unique(lab[c[1]:c[3], c[0]:c[2]])) - {0}
        a = np.isin(lab, list(keep)).astype(np.uint8)
        a = cv2.morphologyEx(a, cv2.MORPH_CLOSE, np.ones((21, 21), np.uint8))
        # never beyond the box and its margin (a dark cut-away backdrop let the attic spread over the whole picture)
        clip = np.zeros((H, W), np.uint8); clip[my0:my1, mx0:mx1] = 1; a = a & clip
        # fill holes inside the place: flood the outside from a blank border (the place may touch the picture's corner)
        big = np.zeros((H + 2, W + 2), np.uint8); big[1:-1, 1:-1] = a
        ff = big.copy(); pad = np.zeros((H + 4, W + 4), np.uint8); cv2.floodFill(ff, pad, (0, 0), 1)
        a = np.maximum(a, (1 - ff)[1:-1, 1:-1])
        # pull the outline in a little, so neighbouring places keep a gap between them
        er = max(3, int(min(W, H) * 0.006)); a = cv2.erode(a, np.ones((er, er), np.uint8))
        box_area = (x1 - x0) * (y1 - y0)
        if a.sum() < 0.35 * box_area:   # not found: the box itself, rounded
            a = np.zeros((H, W), np.uint8); r = int(min(x1 - x0, y1 - y0) * 0.12)
            cv2.rectangle(a, (x0 + r, y0), (x1 - r, y1), 1, -1); cv2.rectangle(a, (x0, y0 + r), (x1, y1 - r), 1, -1)
            for cx, cy in ((x0 + r, y0 + r), (x1 - r, y0 + r), (x0 + r, y1 - r), (x1 - r, y1 - r)): cv2.circle(a, (cx, cy), r, 1, -1)
            print(code, label, "fallback to box")
        claims.append(a)
        print(code, label, int(a.sum()), "px")
    out = np.zeros((H, W), np.uint8)
    # smaller places last so a place inside a bigger one's margin keeps its own pixels
    for i in sorted(range(len(spots)), key=lambda i: -int(claims[i].sum())):
        out[claims[i] > 0] = (i + 1) * 20
    rgb = np.dstack([np.zeros_like(out), np.zeros_like(out), out])   # BGR: red channel carries the index
    ok, e = cv2.imencode(".png", rgb); e.tofile(os.path.join(A, "bg", name + "-regions.png"))
    # Soft glow coverage (Yunke 11-18, the glow-mask rule: the mask fully covers the place, a little larger, and its edge
    # shades off like a glow): each place's own area, grown a few pixels and blurred, one channel per place, four places per
    # RGBA picture: <image>-glow0.png, -glow1.png, -glow2.png. The map shader reads them with linear filtering.
    grow = max(3, int(min(W, H) * 0.008)); sigma = max(4.0, min(W, H) * 0.014)
    covs = []
    for i in range(len(spots)):
        own = (out == (i + 1) * 20).astype(np.uint8)
        own = cv2.dilate(own, np.ones((2 * grow + 1, 2 * grow + 1), np.uint8))
        c = cv2.GaussianBlur(own.astype(np.float32), (0, 0), sigma)
        covs.append(np.clip(c * 255.0, 0, 255).astype(np.uint8))
    while len(covs) % 4: covs.append(np.zeros((H, W), np.uint8))
    for k in range(len(covs) // 4):
        r, g, b, a = covs[4 * k: 4 * k + 4]
        ok, e = cv2.imencode(".png", np.dstack([b, g, r, a])); e.tofile(os.path.join(A, "bg", name + "-glow%d.png" % k))
    pal = np.array([[0, 0, 0]] + [[(37 * k) % 255, (91 * k) % 255, (151 * k) % 255] for k in range(1, 12)], np.uint8)
    vis = pal[(out // 20).astype(int)]
    chk = cv2.addWeighted(img, 0.6, vis, 0.4, 0)
    ok, e = cv2.imencode(".jpg", cv2.resize(chk, (W // 2, H // 2))); e.tofile(os.path.join(A, "bg", name + "-regions-check.jpg"))
    print(code, "regions written", [l for l, _ in spots])
