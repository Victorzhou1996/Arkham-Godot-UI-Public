# Per-person colour mask for an investigator group picture (the picker's hover spotlight and camera push read it).
# Yunke supplies the painting (11-18: the Dunwich Legacy group at the station café; the core set's new one later).
# Each person gets a seed: a "core" box that is surely them and a looser "body" box that may be them. GrabCut runs once per
# person with every other person's core forced to background; pixels claimed by several people go to the one standing in
# front (the order below). Output: <out>-mask.png in the picker's palette + <out>-mask-check.jpg (overlay, for a look).
# Usage: python -I tools/make_lineup_mask.py <painting> <out base path> <scene: dunwich|core>
import sys, os, json
import numpy as np
import cv2

src, out, scene = sys.argv[1], sys.argv[2], (sys.argv[3] if len(sys.argv) > 3 else "dunwich")
img = cv2.imdecode(np.fromfile(src, np.uint8), cv2.IMREAD_COLOR)
H, W = img.shape[:2]
# fractions of the picture: core (x0,y0,x1,y1), body (x0,y0,x1,y1)
SCENES = {
    # The Dunwich Legacy, station café (Yunke's painting, 11-18), left to right: Rex at the table, Zoey behind him in her
    # apron, Jenny in the middle front in the chair, Jim standing at the back with the trumpet, Pete with guitar and dog.
    "dunwich": {
        "order": ["02003", "02002", "02005", "02004", "02001"],   # front to back
        "people": {
            "02002": ((0.13, 0.27, 0.23, 0.62), (0.00, 0.19, 0.30, 0.76)),
            "02001": ((0.31, 0.08, 0.40, 0.52), (0.27, 0.03, 0.45, 0.64)),
            "02003": ((0.49, 0.28, 0.60, 0.88), (0.42, 0.21, 0.68, 1.00)),
            "02004": ((0.60, 0.05, 0.70, 0.45), (0.56, 0.00, 0.80, 0.80)),
            "02005": ((0.81, 0.34, 0.95, 0.88), (0.71, 0.27, 1.00, 1.00)),
        },
        # surely background: the table top and its things, the lantern, the window and wall above Pete's hat
        "background": [(0.00, 0.78, 0.47, 1.00), (0.00, 0.72, 0.16, 1.00), (0.35, 0.58, 0.45, 0.90), (0.85, 0.00, 1.00, 0.24), (0.70, 0.00, 0.78, 0.25)],
    },
    # The core set, the study (Yunke's painting, 11-18), left to right: Agnes in her apron at the desk, Daisy seated behind
    # her with a book, Roland in his trench coat at the wall, Wendy crouched on the floor, Skids on the chair at the right.
    "core": {
        "order": ["01005", "01001", "01003", "01004", "01002"],   # front to back
        "people": {
            "01004": ((0.06, 0.14, 0.17, 0.45), (0.02, 0.09, 0.23, 0.62)),
            "01002": ((0.24, 0.03, 0.32, 0.40), (0.19, 0.00, 0.36, 0.57)),
            "01001": ((0.40, 0.16, 0.51, 0.64), (0.27, 0.10, 0.64, 0.77)),
            "01005": ((0.53, 0.40, 0.61, 0.70), (0.42, 0.30, 0.70, 0.80)),
            "01003": ((0.78, 0.18, 0.90, 0.78), (0.71, 0.11, 0.99, 0.99)),
        },
        # surely background: the desk and armchair, the lamp, the rug and floor in front, the wall above Skids
        "background": [(0.00, 0.60, 0.26, 1.00), (0.00, 0.30, 0.05, 0.62), (0.28, 0.80, 0.70, 1.00), (0.66, 0.00, 1.00, 0.10),
                       (0.00, 0.00, 0.19, 0.09), (0.69, 0.10, 0.76, 0.72), (0.33, 0.00, 0.40, 0.14)],
        # hand-drawn outlines in picture pixels (1672×941) — Yunke 11-18: GrabCut took in the window, bookshelf and wall
        # around them, so the lit area was a block and the silhouette did not match the people
        "shapes": {
            "01004": [(200,110),(260,95),(330,115),(360,165),(355,235),(330,265),(345,330),(330,420),(290,520),(250,535),(120,535),(70,470),(70,390),(170,362),(168,252),(150,215),(175,150)],
            "01002": [(410,40),(470,20),(535,45),(550,110),(540,165),(560,215),(565,265),(520,300),(500,380),(470,470),(400,525),(360,470),(355,300),(360,230),(390,175),(405,120)],
            "01001": [(700,150),(730,118),(790,120),(855,160),(860,200),(960,180),(1040,150),(1070,165),(1060,210),(990,225),(900,230),(860,250),(800,330),(780,400),(760,470),(700,560),(660,650),(640,710),(560,700),(470,720),(430,700),(440,560),(470,420),(510,330),(560,250),(640,215),(700,205)],
            "01005": [(900,270),(950,250),(1030,265),(1050,320),(1030,370),(1050,420),(1085,480),(1100,560),(1160,640),(1170,690),(1135,695),(1080,640),(1040,620),(1000,690),(930,705),(870,690),(820,700),(760,690),(680,665),(670,620),(720,520),(800,440),(870,400),(900,355),(880,310)],
            "01003": [(1270,120),(1330,100),(1420,115),(1450,175),(1440,215),(1530,220),(1600,240),(1650,285),(1620,320),(1520,320),(1480,360),(1470,430),(1480,520),(1440,560),(1380,580),(1330,640),(1360,800),(1370,870),(1320,895),(1185,890),(1180,850),(1250,800),(1260,700),(1250,600),(1300,540),(1330,480),(1350,400),(1360,320),(1350,250),(1300,240),(1270,200)],
        },
    },
    # The Path to Carcosa, a theatre dressing room (img2 ensemble C02-A, Yunke 11-20), left to right: Mark at the back with
    # his rifle, Lola in front in the armchair, William Yorick with the lantern, Minh with her book, Akachi with her staff at
    # the back, Sefina at the table with her palette knife. Outlines only (no GrabCut).
    "carcosa": {
        "order": ["03006", "03003", "03005", "03002", "03004", "03001"],   # front to back
        "people": {c: ((0, 0, 0, 0), (0, 0, 0, 0)) for c in ["03001", "03002", "03003", "03004", "03005", "03006"]},
        "shapes": {
            "03001": [(255,40),(300,10),(360,12),(390,45),(385,100),(360,140),(395,190),(410,260),(412,340),(400,430),(380,470),(240,470),(160,470),(155,500),(60,500),(50,440),(25,430),(20,330),(40,250),(90,200),(120,150),(150,60),(170,70),(165,130),(220,120)],
            "03006": [(250,330),(330,315),(400,330),(440,380),(445,440),(420,500),(410,530),(440,570),(445,690),(520,730),(640,740),(650,770),(520,780),(445,800),(520,830),(600,880),(640,941),(330,941),(300,860),(250,840),(190,790),(130,770),(40,770),(10,750),(30,690),(120,610),(200,560),(270,520),(240,470),(225,420),(230,370)],
            "03005": [(660,125),(710,98),(790,98),(825,150),(810,250),(836,300),(836,540),(790,580),(780,660),(690,660),(660,690),(560,690),(470,690),(440,600),(450,400),(470,320),(520,230),(600,180),(650,150)],
            "03002": [(970,125),(1035,112),(1100,135),(1118,200),(1105,262),(1140,300),(1150,420),(1165,500),(1110,520),(1110,620),(1090,700),(1005,700),(1000,615),(900,612),(880,560),(856,470),(870,340),(915,290),(965,262),(958,200)],
            "03004": [(1275,40),(1345,25),(1420,45),(1445,60),(1495,55),(1500,200),(1540,330),(1545,450),(1520,520),(1440,520),(1340,450),(1290,540),(1270,620),(1180,620),(1160,440),(1175,320),(1210,230),(1260,190),(1270,120)],
            "03003": [(1250,345),(1340,325),(1420,340),(1470,400),(1530,470),(1600,540),(1650,610),(1672,680),(1672,941),(1490,941),(1495,800),(1450,760),(1300,740),(1200,730),(1110,660),(1120,600),(1170,600),(1230,560),(1250,500),(1240,420)],
        },
    },
}
# a scene file (tools/lineups/<id>.json, 11-22: every later box's group picture): {"people": [codes in palette order],
# "order": [codes front to back], "shapes": {code: [[x, y], ...] in 1672×941 picture pixels}}
PALETTE = [(0, 0, 255), (0, 255, 0), (255, 0, 0), (0, 255, 255), (255, 0, 255), (255, 255, 0)]   # BGR of red, green, blue, yellow, magenta, cyan
if scene.endswith(".json"):
    spec = json.load(open(scene, encoding="utf-8"))
    SCENES["file"] = {"order": spec["order"], "people": {c: ((0, 0, 0, 0), (0, 0, 0, 0)) for c in spec["people"]},
                      "shapes": {c: [tuple(p) for p in pts] for c, pts in spec["shapes"].items()}}
    scene = "file"
cfg = SCENES[scene]
colors = {"02001": (0, 0, 255), "02002": (0, 255, 0), "02003": (255, 0, 0), "02004": (0, 255, 255), "02005": (255, 0, 255),
          "01001": (0, 0, 255), "01002": (0, 255, 0), "01003": (255, 0, 0), "01004": (0, 255, 255), "01005": (255, 0, 255),
          "03001": (0, 0, 255), "03002": (0, 255, 0), "03003": (255, 0, 0), "03004": (0, 255, 255), "03005": (255, 0, 255), "03006": (255, 255, 0)}  # BGR
if scene == "file": colors = {c: PALETTE[i] for i, c in enumerate(spec["people"])}

def box(f): return (int(f[0] * W), int(f[1] * H), int(f[2] * W), int(f[3] * H))
claims = {}
for code, (core, body) in cfg["people"].items():
    if code in cfg.get("shapes", {}):
        # a hand-drawn outline: that is the person (scaled if the picture is not 1672 wide), edges smoothed a little
        sx = W / 1672.0
        a = np.zeros((H, W), np.uint8)
        cv2.fillPoly(a, [np.array([[int(x * sx), int(y * sx)] for x, y in cfg["shapes"][code]], np.int32)], 1)
        claims[code] = a; print(code, "shape", int(a.sum())); continue
    gc = np.full((H, W), cv2.GC_BGD, np.uint8)
    bx = box(body); cx = box(core)
    gc[bx[1]:bx[3], bx[0]:bx[2]] = cv2.GC_PR_BGD
    # an ellipse inside the body box is probably them (people are not rectangles)
    ell = np.zeros((H, W), np.uint8)
    cv2.ellipse(ell, ((bx[0] + bx[2]) // 2, (bx[1] + bx[3]) // 2), ((bx[2] - bx[0]) // 2, (bx[3] - bx[1]) // 2), 0, 0, 360, 1, -1)
    gc[(ell > 0) & (gc == cv2.GC_PR_BGD)] = cv2.GC_PR_FGD
    gc[cx[1]:cx[3], cx[0]:cx[2]] = cv2.GC_FGD
    for z in cfg.get("background", []):
        b = box(z); gc[b[1]:b[3], b[0]:b[2]] = cv2.GC_BGD
    for other, (ocore, _) in cfg["people"].items():
        if other == code: continue
        o = box(ocore); gc[o[1]:o[3], o[0]:o[2]] = cv2.GC_BGD
    bgm = np.zeros((1, 65), np.float64); fgm = np.zeros((1, 65), np.float64)
    cv2.grabCut(img, gc, None, bgm, fgm, 6, cv2.GC_INIT_WITH_MASK)
    a = ((gc == cv2.GC_FGD) | (gc == cv2.GC_PR_FGD)).astype(np.uint8)
    # keep the blob touching the core, close small holes
    n, lab, stats, _ = cv2.connectedComponentsWithStats(a)
    core_labels = set(np.unique(lab[cx[1]:cx[3], cx[0]:cx[2]])) - {0}
    a = np.isin(lab, list(core_labels)).astype(np.uint8)
    a = cv2.morphologyEx(a, cv2.MORPH_CLOSE, np.ones((15, 15), np.uint8))
    filled = a.copy(); pad = np.zeros((H + 2, W + 2), np.uint8); cv2.floodFill(filled, pad, (0, 0), 1)
    a = np.maximum(a, 1 - filled)
    claims[code] = a
    print(code, "pixels", int(a.sum()))

# grow every person a little (Yunke 11-18: several masks did not cover the whole figure — the rule is that the mask covers
# the person completely and may be a bit larger); overlaps still go to whoever stands in front
grow = max(6, int(W * 0.012))
for code in claims: claims[code] = cv2.dilate(claims[code], cv2.getStructuringElement(cv2.MORPH_ELLIPSE, (2 * grow + 1, 2 * grow + 1)))
mask = np.zeros((H, W, 3), np.uint8)
for code in reversed(cfg["order"]):          # back first, front last (the front person wins shared pixels)
    mask[claims[code] > 0] = colors[code]
ok, enc = cv2.imencode(".png", mask); enc.tofile(out + "-mask.png")
check = cv2.addWeighted(img, 0.55, mask, 0.45, 0)
ok, enc = cv2.imencode(".jpg", cv2.resize(check, (W // 2, H // 2))); enc.tofile(out + "-mask-check.jpg")
print("mask written", out + "-mask.png")
