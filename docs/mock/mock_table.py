# Mock-up only: the table screen after the interaction/UI plan (docs/PLAN-INTERACTION-20261117.md §2–§13, §15).
# Usage: python -I mock_table.py "<Arkham UI root>" "<out dir>"
import sys, math
from PIL import Image, ImageDraw, ImageFilter

ROOT, OUT = sys.argv[1], sys.argv[2]
# reuse card / text-box drawing from mock_hand.py (only its function definitions)
src = open(ROOT + "/docs/mock/mock_hand.py", encoding="utf-8").read().split("shot = Image.open(")[0]
ns = {"__name__": "mock_hand"}; exec(src, ns)
small_card, sans, kai, serif, icon, cards, rich_lines = ns["small_card"], ns["sans"], ns["kai"], ns["serif"], ns["icon"], ns["cards"], ns["rich_lines"]
A = ROOT + "/app/assets/"
W1, H1 = 1920, 1080
BRASS = (165, 138, 84); AMBER = (230, 178, 92); PAPER = (234, 219, 189); HOVER = (255, 241, 213); MUTED = (182, 170, 145)
GOOD = (120, 190, 110); RED = (192, 84, 62)
SK = {"willpower": ("A", (98, 140, 205)), "intellect": ("F", (160, 110, 200)), "combat": ("D", (205, 85, 65)), "agility": ("S", (95, 165, 90))}
PORTRAIT = {"01001": A + "ref/art/cards/01001-roland-banks.webp", "01002": A + "ref/art/cards/01002-daisy-walker.webp"}
SLOT_ICON = {k: A + "ref/ui/icons/slot-" + k + ".webp" for k in ["hand", "arcane", "accessory", "body", "ally"]}

def img(p): return Image.open(p).convert("RGBA")

def cover(im, w, h, fy=0.5):
    r = max(w / im.width, h / im.height)
    im = im.resize((int(im.width * r + 1), int(im.height * r + 1)), Image.LANCZOS)
    x, y = (im.width - w) // 2, int((im.height - h) * fy)
    return im.crop((x, y, x + w, y + h))

def tint(im, col):
    out = Image.new("RGBA", im.size, col + (0,)); out.putalpha(im.getchannel("A")); return out

def fit(im, h):
    return im.resize((max(1, int(im.width * h / im.height)), h), Image.LANCZOS)

def layer(): return Image.new("RGBA", (W1, H1), (0, 0, 0, 0))

def panel(base, box, radius=10, fill=(26, 24, 20, 232), outline=BRASS, width=2):
    l = layer(); ImageDraw.Draw(l).rounded_rectangle(box, radius=radius, fill=fill, outline=outline, width=width); base.alpha_composite(l)

def text(base, xy, s, font, fill=PAPER, anchor="la", shadow=True, stroke=0):
    d = ImageDraw.Draw(base)
    if shadow: d.text((xy[0] + 1, xy[1] + 2), s, font=font, fill=(0, 0, 0, 200), anchor=anchor)
    d.text(xy, s, font=font, fill=fill, anchor=anchor, stroke_width=stroke, stroke_fill=(10, 8, 6))

def disc(im, size, ring, ring_w=6, fy=0.4):
    a = cover(im.convert("RGB"), size, size, fy)
    out = Image.new("RGBA", (size + 2 * ring_w, size + 2 * ring_w), (0, 0, 0, 0))
    m = Image.new("L", (size, size), 0); ImageDraw.Draw(m).ellipse((0, 0, size - 1, size - 1), fill=255)
    out.paste(a, (ring_w, ring_w), m)
    ImageDraw.Draw(out).ellipse((ring_w // 2, ring_w // 2, size + ring_w * 1.5, size + ring_w * 1.5), outline=ring, width=ring_w)
    return out

def plate(im, w, h, ring, ring_w=5, r=14):
    """Rounded-rectangle token that shows (almost) the whole illustration."""
    a = cover(im.convert("RGB"), w, h, 0.45)
    out = Image.new("RGBA", (w + 2 * ring_w, h + 2 * ring_w), (0, 0, 0, 0))
    m = Image.new("L", (w, h), 0); ImageDraw.Draw(m).rounded_rectangle((0, 0, w - 1, h - 1), radius=r, fill=255)
    out.paste(a, (ring_w, ring_w), m)
    ImageDraw.Draw(out).rounded_rectangle((ring_w // 2, ring_w // 2, w + ring_w * 1.5, h + ring_w * 1.5), radius=r + 2, outline=ring, width=ring_w)
    return out

def glow(base, box, color=AMBER, r=12, w=7, blur=7, ellipse=False):
    l = layer(); d = ImageDraw.Draw(l)
    if ellipse: d.ellipse(box, outline=color + (230,), width=w)
    else: d.rounded_rectangle(box, radius=r, outline=color + (230,), width=w)
    base.alpha_composite(l.filter(ImageFilter.GaussianBlur(blur)))

def pill(base, cx, cy, s, fill, size=18):
    f = sans(size); d = ImageDraw.Draw(base); tw = d.textlength(s, font=f)
    panel(base, (cx - tw / 2 - 12, cy - 15, cx + tw / 2 + 12, cy + 15), radius=15, fill=fill + (240,), outline=(30, 26, 20), width=2)
    d.text((cx, cy - 1), s, font=f, fill=(255, 255, 255), anchor="mm")

# ── token with a number on top (0712 web style)
def token_num(base, kind, n, cx, cy, h=46, small_max=None):
    t = fit(img(A + "ui/tokens/" + kind + ".png"), h)
    base.alpha_composite(t, (int(cx - t.width / 2), int(cy - t.height / 2)))
    text(base, (cx, cy + 1), str(n), sans(int(h * 0.5)), (255, 255, 255), "mm", False, stroke=3)
    if small_max is not None:
        text(base, (cx + t.width / 2 - 2, cy + h / 2 - 4), "/" + str(small_max), sans(14), MUTED, "lm", False, stroke=2)

# ── skill: icon + circle + number, one unit
def skill_unit(base, cx, cy, key, v, d=48):
    g, col = SK[key]; dr = ImageDraw.Draw(base)
    dr.ellipse((cx - d / 2, cy - d / 2, cx + d / 2, cy + d / 2), fill=(20, 18, 15), outline=col, width=4)
    text(base, (cx, cy + 1), str(v), sans(26), (255, 255, 255), "mm", False)
    b = 24; bx, by = cx - d / 2 + 2, cy - d / 2 + 2
    dr.ellipse((bx - b / 2, by - b / 2, bx + b / 2, by + b / 2), fill=col, outline=(20, 18, 15), width=2)
    dr.text((bx, by + 1), g, font=icon(17), fill=(255, 255, 255), anchor="mm")

# ── enemy stat badge on the token edge
def stat_badge(base, cx, cy, glyph, col, v, tok=None, d=44, path=None):
    dr = ImageDraw.Draw(base)
    dr.ellipse((cx - d / 2, cy - d / 2, cx + d / 2, cy + d / 2), fill=(20, 18, 15, 245), outline=col, width=3)
    if tok or path:
        t = fit(img(path or (A + "ui/tokens/" + tok + ".png")), d - 8); base.alpha_composite(t, (int(cx - t.width / 2), int(cy - t.height / 2)))
        text(base, (cx, cy + 1), str(v), sans(19), (255, 255, 255), "mm", False, stroke=3)
    else:
        dr.text((cx - 9, cy + 1), glyph, font=icon(21), fill=col, anchor="mm")
        text(base, (cx + 9, cy + 1), str(v), sans(20), (255, 255, 255), "mm", False)

def background(name, dim=0.0, blur=9):
    bg = cover(Image.open(A + "bg/" + name).convert("RGB"), W1, H1).filter(ImageFilter.GaussianBlur(blur)).convert("RGBA")
    v = layer(); vd = ImageDraw.Draw(v)
    for i in range(280):
        vd.line((0, H1 - i, W1, H1 - i), fill=(8, 7, 5, int(185 * (1 - i / 280) ** 1.8)))
    for i in range(120):
        vd.line((0, i, W1, i), fill=(8, 7, 5, int(120 * (1 - i / 120) ** 1.8)))
    bg.alpha_composite(v)
    bg.alpha_composite(Image.new("RGBA", (W1, H1), (0, 0, 0, int(255 * (0.12 + dim)))))
    return bg

def top_bar(base, phase="调查阶段 · 第 2 轮"):
    text(base, (28, 22), phase, sans(20), MUTED)
    base.alpha_composite(disc(Image.open(A + "bg/mansion-cutaway.png"), 64, BRASS, 3, 0.5), (26, 56)); text(base, (104, 76), "宅邸", sans(18), MUTED)
    panel(base, (700, 16, 1220, 70), radius=27, fill=(22, 20, 17, 225))
    text(base, (735, 43), "目标 · 灼热屏障", kai(24), HOVER, "lm")
    d = ImageDraw.Draw(base); text(base, (1035, 43), "毁灭 1/3", sans(19), (232, 120, 96), "lm")
    for i in range(3):
        x = 1130 + i * 26; d.ellipse((x, 34, x + 18, 52), fill=(200, 70, 55) if i < 1 else None, outline=(200, 90, 70), width=2)
    for i, code in enumerate(["01001", "01002"]):
        base.alpha_composite(disc(Image.open(PORTRAIT[code]), 50, AMBER if i == 0 else BRASS, 3, 0.12), (1300 + i * 64, 16))
    x = 1450
    for label in ["混沌袋", "记录", "撤回", "菜单", "?"]:
        w = 46 if label == "?" else 84
        panel(base, (x, 18, x + w, 70), radius=6, fill=(35, 34, 29, 235), outline=(120, 100, 64), width=1)
        text(base, (x + w / 2, 44), label, sans(19), (140, 132, 112) if label == "撤回" else PAPER, "mm", False)
        x += w + 10

# ── investigator module (bottom left)
def investigator(base, actions_left=3, actions_total=3, hp=9, hp_max=9, san=5, san_max=5, res=3, clues=0):
    panel(base, (14, 858, 636, 1066), radius=12, fill=(22, 20, 17, 228))
    base.alpha_composite(disc(Image.open(PORTRAIT["01001"]), 128, AMBER, 5, 0.12), (28, 888))
    text(base, (184, 872), "罗兰·班克斯", kai(28), HOVER)
    d = ImageDraw.Draw(base)
    for i in range(actions_total):  # action = right-pointing arrow glyph, as on the 0712 web table
        d.text((392 + i * 34, 873), "I", font=icon(30), fill=AMBER if i < actions_left else (95, 86, 66))
    for i, (k, v) in enumerate([("willpower", 3), ("intellect", 3), ("combat", 4), ("agility", 2)]):
        skill_unit(base, 212 + i * 66, 942, k, v)
    for i, (k, v, mx) in enumerate([("health", hp, hp_max), ("horror", san, san_max), ("resource", res, None), ("clue", clues, None)]):
        token_num(base, k, v, 210 + i * 78, 1006, 46, mx)
    text(base, (480, 932), "牌库 27", sans(17), MUTED, shadow=False)
    text(base, (480, 958), "弃牌 2", sans(17), MUTED, shadow=False)

# ── asset area with free-slot counters (reference-version style)
def assets(base, ammo=4):
    panel(base, (1372, 858, 1906, 1066), radius=12, fill=(22, 20, 17, 228))
    text(base, (1390, 868), "资产", kai(22), HOVER, shadow=False)
    # tile: .38 Special in a hand slot, uses badge
    a = plate(Image.open(A + "ref/art/cards/01006-rolands-38-special.webp"), 120, 86, BRASS, 3, 8)
    base.alpha_composite(a, (1390, 902))
    pill(base, 1490, 908, "弹药 " + str(ammo), (150, 110, 40), 15)
    s = tint(fit(img(SLOT_ICON["hand"]), 22), PAPER); base.alpha_composite(s, (1396, 968))
    text(base, (1453, 1006), "罗兰的.38特制手枪", sans(15), PAPER, "mm", False)
    # empty-slot placeholders
    for i in range(2):
        x = 1530 + i * 70
        panel(base, (x, 902, x + 60, 994), radius=8, fill=(30, 28, 24, 160), outline=(90, 78, 56), width=1)
    # free-slot counters: icon + free/total
    text(base, (1390, 1040), "空余槽位", sans(15), MUTED, "lm", False)
    x = 1470
    for k, free, total in [("hand", 1, 2), ("arcane", 2, 2), ("accessory", 1, 1), ("body", 1, 1), ("ally", 1, 1)]:
        s = tint(fit(img(SLOT_ICON[k]), 28), MUTED if free else (110, 70, 60)); base.alpha_composite(s, (x, 1026))
        text(base, (x + s.width + 4, 1040), str(free), sans(20), HOVER if free else (140, 80, 70), "lm", False)
        text(base, (x + s.width + 17, 1043), "/" + str(total), sans(13), MUTED, "lm", False)
        x += s.width + 50

def feathered(im, w, h, fy=0.2, edges=(36, 30, 36, 110)):
    """Crop to w×h and fade every edge to transparent (left, top, right, bottom widths), so it melts into a dark panel."""
    from PIL import ImageChops
    a = cover(im.convert("RGB"), w, h, fy).convert("RGBA")
    l, t, r, bt = edges
    hx = Image.new("L", (w, 1)); hy = Image.new("L", (1, h))
    for x in range(w): hx.putpixel((x, 0), int(255 * min(1, x / l, (w - 1 - x) / r) ** 1.3))
    for y in range(h): hy.putpixel((0, y), int(255 * min(1, y / t, (h - 1 - y) / bt) ** 1.3))
    a.putalpha(ImageChops.multiply(hx.resize((w, h)), hy.resize((w, h))))
    return a

# ── left player area: investigator + assets in one tall, narrow panel (§16)
PX0, PY0, PX1, PY1 = 14, 580, 440, 1066
def player_area(base, actions_left=3, actions_total=3, hp=9, san=5, res=3, clues=0, ammo=4):
    panel(base, (PX0, PY0, PX1, PY1), radius=12, fill=(16, 14, 11, 236), outline=(110, 92, 60), width=1)
    art = feathered(Image.open(PORTRAIT["01001"]), PX1 - PX0 - 4, 196, 0.18)
    base.alpha_composite(art, (PX0 + 2, PY0 + 2))
    text(base, (PX0 + 20, PY0 + 168), "罗兰·班克斯", kai(30), HOVER, "lm", stroke=2)
    d = ImageDraw.Draw(base)
    for i in range(actions_total):
        x = PX1 - 132 + i * 38
        d.text((x + 1, PY0 + 154), "I", font=icon(32), fill=(0, 0, 0)); d.text((x, PY0 + 152), "I", font=icon(32), fill=AMBER if i < actions_left else (95, 86, 66))
    xs = [PX0 + 66 + i * 98 for i in range(4)]
    for x, (k, v) in zip(xs, [("willpower", 3), ("intellect", 3), ("combat", 4), ("agility", 2)]):
        skill_unit(base, x, PY0 + 232, k, v)
    for x, (k, v, mx) in zip(xs, [("health", hp, 9), ("horror", san, 5), ("resource", res, None), ("clue", clues, None)]):
        token_num(base, k, v, x, PY0 + 294, 48, mx)
    d.line((PX0 + 18, PY0 + 330, PX1 - 18, PY0 + 330), fill=(90, 76, 50))
    text(base, (PX0 + 20, PY0 + 340), "资产", kai(20), HOVER, shadow=False)
    # asset tile with uses badge and its slot icon; name shows on hover
    a = plate(Image.open(A + "ref/art/cards/01006-rolands-38-special.webp"), 96, 70, BRASS, 3, 8)
    base.alpha_composite(a, (PX0 + 18, PY0 + 368))
    pill(base, PX0 + 104, PY0 + 372, "弹药 " + str(ammo), (150, 110, 40), 14)
    base.alpha_composite(tint(fit(img(SLOT_ICON["hand"]), 20), PAPER), (PX0 + 24, PY0 + 414))
    for i in range(3):
        x = PX0 + 130 + i * 72
        panel(base, (x, PY0 + 368, x + 62, PY0 + 444), radius=8, fill=(30, 28, 24, 150), outline=(80, 70, 50), width=1)
    x = PX0 + 20
    for k, free, total in [("hand", 1, 2), ("arcane", 2, 2), ("accessory", 1, 1), ("body", 1, 1), ("ally", 1, 1)]:
        s = tint(fit(img(SLOT_ICON[k]), 24), MUTED if free else (110, 70, 60)); base.alpha_composite(s, (x, PY0 + 448 - 0))
        text(base, (x + s.width + 4, PY0 + 460), str(free), sans(18), HOVER if free else (140, 80, 70), "lm", False)
        text(base, (x + s.width + 16, PY0 + 463), "/" + str(total), sans(12), MUTED, "lm", False)
        x += s.width + 50

# ── deck / discard as two small piles left and right of the hand (§16)
def piles(base, deck=27, discard=2, top_discard="01088"):
    back = cover(Image.open(A + "ref/ui/board-v6-materials/piles/personal-blue-back.webp").convert("RGB"), 66, 92).convert("RGBA")
    bx, by = 482, 950
    for k in range(3, -1, -1): base.alpha_composite(back, (bx + k * 3, by - k * 3))
    pill(base, bx + 36, by + 108, "牌库 " + str(deck), (40, 50, 80), 15)
    c = small_card(top_discard, None, 66, 92).rotate(-4, resample=Image.BICUBIC, expand=True)
    dx, dy = 1376, 940
    for k in range(min(discard, 2) - 1, 0, -1):
        base.alpha_composite(Image.new("RGBA", (66, 92), (40, 36, 30, 255)), (dx + 10 + k * 3, dy + 10 - k * 3))
    base.alpha_composite(c, (dx, dy))
    pill(base, dx + 42, by + 108, "弃牌 " + str(discard), (60, 52, 40), 15)

# ── right side: player-specific zone + public zone (threats, other cards) (§16)
RX0, RX1 = 1480, 1906
def right_zone(base):
    panel(base, (RX0, 600, RX1, 742), radius=12, fill=(16, 14, 11, 200), outline=(90, 78, 56), width=1)
    text(base, (RX0 + 18, 612), "专属区", kai(20), HOVER, shadow=False)
    l = layer(); dl = ImageDraw.Draw(l)
    for x in range(RX0 + 18, RX1 - 18, 14): dl.line((x, 648, x + 7, 648), fill=(110, 96, 70, 200)); dl.line((x, 726, x + 7, 726), fill=(110, 96, 70, 200))
    for y in range(648, 726, 14): dl.line((RX0 + 18, y, RX0 + 18, y + 7), fill=(110, 96, 70, 200)); dl.line((RX1 - 18, y, RX1 - 18, y + 7), fill=(110, 96, 70, 200))
    base.alpha_composite(l)
    text(base, ((RX0 + RX1) / 2, 687), "调查员专属的牌堆或区域（例如特殊牌库）", sans(16), (130, 120, 100), "mm", False)
    panel(base, (RX0, 754, RX1, 986), radius=12, fill=(16, 14, 11, 200), outline=(90, 78, 56), width=1)
    text(base, (RX0 + 18, 766), "公共区 · 威胁", kai(20), HOVER, shadow=False)
    t = plate(Image.open(A + "ref/art/cards/01164-frozen-in-fear.webp"), 120, 86, (150, 80, 160), 3, 8)
    base.alpha_composite(t, (RX0 + 18, 802))
    text(base, (RX0 + 84, 912), cards["01164"]["name"], sans(16), PAPER, "mm", False)
    text(base, (RX0 + 84, 936), "罗兰 · 诡计", sans(13), MUTED, "mm", False)
    for i in range(2):
        x = RX0 + 162 + i * 128
        panel(base, (x, 802, x + 116, 896), radius=8, fill=(30, 28, 24, 130), outline=(70, 62, 46), width=1)
    panel(base, (RX1 - 160, 1004, RX1, 1060), radius=8, fill=(35, 34, 29, 240), outline=BRASS, width=2)
    text(base, (RX1 - 80, 1032), "结束回合", kai(24), HOVER, "mm", False)

def hand(base, codes):
    W, H = 136, 190; step, tilt, sag = 124, 5.5, 6
    n = len(codes); mid = (n - 1) / 2; cx = 960; cy = H1 - H // 2 - 34
    for i, (code, cost) in enumerate(codes):
        off = i - mid
        c = small_card(code, cost, W, H).rotate(-off * tilt, resample=Image.BICUBIC, expand=True)
        base.alpha_composite(c, (int(cx + off * step - c.width / 2), int(cy + off * off * sag - c.height / 2)))

def next_step(base, msg):
    f = sans(21); d = ImageDraw.Draw(base); w = d.textlength(msg, font=f) + 60
    panel(base, (960 - w / 2, 802, 960 + w / 2, 844), radius=21, fill=(22, 20, 17, 215), outline=(150, 120, 70), width=1)
    d.ellipse((960 - w / 2 + 18, 817, 960 - w / 2 + 30, 829), fill=AMBER)
    text(base, (960 - w / 2 + 40, 823), msg, f, HOVER, "lm", False)

def float_text(base, items, x, y):
    for i, (s, col) in enumerate(items):
        f = sans(22); d = ImageDraw.Draw(base); w = d.textlength(s, font=f) + 28
        yy = y - i * 40
        panel(base, (x, yy, x + w, yy + 34), radius=17, fill=(14, 12, 10, 200), outline=(80, 66, 44), width=1)
        text(base, (x + 14, yy + 17), s, f, col, "lm", False)

def toast(base, art, title, body, y=92):
    panel(base, (1500, y, 1894, y + 74), radius=10, fill=(26, 24, 20, 236), outline=RED, width=2)
    base.alpha_composite(disc(Image.open(art), 50, RED, 3, 0.4), (1510, y + 9))
    text(base, (1576, y + 12), title, sans(18), (232, 120, 96), shadow=False)
    text(base, (1576, y + 40), body, sans(19), PAPER, shadow=False)

def exits(base):
    for (x, y, s, act) in [(480, 470, "← 书房", True), (1640, 470, "客厅 →", True), (860, 690, "↓ 地窖（未揭示）", False)]:
        f = kai(24); d = ImageDraw.Draw(base); w = d.textlength(s, font=f) + 40
        if act: glow(base, (x - 4, y - 4, x + w + 4, y + 50), AMBER, 25, 6, 6)
        panel(base, (x, y, x + w, y + 46), radius=23, fill=(22, 20, 17, 215), outline=AMBER if act else (110, 96, 70), width=2)
        text(base, (x + w / 2, y + 23), s, f, HOVER if act else MUTED, "mm", False)

def enemy(base, ex, ey, hovered=False, w=232, h=174):
    if not hovered: glow(base, (ex - w / 2 - 8, ey - h / 2 - 8, ex + w / 2 + 8, ey + h / 2 + 8), AMBER, 18, 7, 8)
    p = plate(Image.open(A + "ref/art/cards/01159-swarm-of-rats.webp"), w, h, RED, 5, 14)
    base.alpha_composite(p, (int(ex - p.width / 2), int(ey - p.height / 2)))
    text(base, (ex, ey - h / 2 - 22), "与你交战", sans(18), (232, 120, 96), "mm")
    L, R, T, B = ex - w / 2, ex + w / 2, ey - h / 2, ey + h / 2
    stat_badge(base, L + 4, T + 34, "D", SK["combat"][1], 1)            # fight
    stat_badge(base, R - 4, T + 34, "S", SK["agility"][1], 3)           # evade
    stat_badge(base, L + 4, B - 30, None, RED, 1, path=A + "ref/ui/icons/damage.webp")   # health
    # attack capsule: damage + horror dealt by this enemy
    cx0, cy0 = R - 6, B - 4
    panel(base, (cx0 - 58, cy0 - 20, cx0 + 58, cy0 + 20), radius=20, fill=(20, 18, 15, 245), outline=RED, width=2)
    base.alpha_composite(fit(img(A + "ui/tokens/health.png"), 28), (int(cx0 - 50), int(cy0 - 14)))
    text(base, (cx0 - 14, cy0 + 1), "1", sans(20), (255, 255, 255), "mm", False)
    base.alpha_composite(fit(img(A + "ui/tokens/horror.png"), 26), (int(cx0 + 2), int(cy0 - 13)))
    text(base, (cx0 + 42, cy0 + 1), "0", sans(20), (255, 255, 255), "mm", False)
    pill(base, ex, B + 22, "鼠群", (34, 30, 24), 20)
    return (L, T, R, B)

def enemy_info(base, x, y):
    c = cards["01159"]; Wd = 380; pad = 18
    lines = rich_lines(c.get("text", ""), Wd - 2 * pad, serif(20), icon(22))
    rows = [("战斗", "D", SK["combat"][1], "1"), ("躲避", "S", SK["agility"][1], "3"), ("生命", None, RED, "1")]
    H = pad + 34 + 26 + 12 + 40 + 32 + 10 + len(lines) * 30 + pad
    panel(base, (x, y, x + Wd, y + H), radius=10, fill=(26, 24, 20, 242))
    d = ImageDraw.Draw(base)
    text(base, (x + pad, y + pad), c["name"], kai(26), HOVER, shadow=False)
    text(base, (x + pad, y + pad + 36), "敌人 · " + c.get("traits", ""), sans(17), MUTED, shadow=False)
    yy = y + pad + 36 + 26 + 12
    d.line((x + pad, yy - 6, x + Wd - pad, yy - 6), fill=(90, 74, 46))
    xx = x + pad
    for name, g, col, v in rows:
        if g: d.text((xx, yy + 4), g, font=icon(22), fill=col)
        else: base.alpha_composite(fit(img(A + "ui/tokens/health.png"), 24), (int(xx), int(yy + 4)))
        d.text((xx + 30, yy + 2), name + " " + v, font=sans(20), fill=PAPER); xx += 116
    yy += 40
    d.text((x + pad, yy), "攻击：", font=sans(20), fill=PAPER)
    base.alpha_composite(fit(img(A + "ui/tokens/health.png"), 24), (x + pad + 62, yy + 2)); d.text((x + pad + 90, yy), "伤害 1", font=sans(20), fill=PAPER)
    base.alpha_composite(fit(img(A + "ui/tokens/horror.png"), 24), (x + pad + 170, yy + 2)); d.text((x + pad + 198, yy), "恐惧 0", font=sans(20), fill=PAPER)
    yy += 42
    for ln in lines:
        xx = x + pad
        for kind, s, b in ln:
            if kind == "ico": d.text((xx, yy + 2), s, font=icon(22), fill=AMBER); xx += d.textlength(s, font=icon(22)) + 2
            else:
                d.text((xx, yy), s, font=serif(20), fill=HOVER if b else PAPER, stroke_width=1 if b else 0, stroke_fill=HOVER); xx += d.textlength(s, font=serif(20))
        yy += 30

HAND5 = [("01020", 3), ("01039", None), ("01016", 4), ("01030", 1), ("01037", 2)]

# ───────────── mock 1: investigation phase; the enemy is hovered, its info box is open
b = background("hallway.png")
top_bar(b)
text(b, (W1 / 2, 118), "走廊", kai(46), HOVER, "mm")
text(b, (W1 / 2, 160), "隐藏值 1　·　线索 0", sans(20), PAPER, "mm")
exits(b)
L, T, R, B = enemy(b, 960, 400, hovered=True)
enemy_info(b, int(R + 46), int(T - 20))
float_text(b, [("+1 资源", (240, 205, 120)), ("黛西·沃克  +1 资源", (240, 205, 120))], 456, 730)
toast(b, A + "ref/art/cards/01159-swarm-of-rats.webp", "强制 · 已自动结算", "鼠群 现身，并与你交战")
player_area(b); right_zone(b); piles(b); hand(b, HAND5)
next_step(b, "你的回合 · 剩余 3 次行动：拖动手牌到中央打出，或点击高亮的目标")
b.convert("RGB").save(OUT + "/mock-table-investigation.png")

# ───────────── mock 2: skill test reveal as a floating overlay (13.4)
b = background("hallway.png", dim=0.3)
top_bar(b)
text(b, (W1 / 2, 118), "走廊", kai(46), (150, 140, 120), "mm")
p = plate(Image.open(A + "ref/art/cards/01159-swarm-of-rats.webp"), 160, 120, RED, 4, 10)
b.alpha_composite(p, (int(W1 / 2 - p.width / 2), 240))
text(b, (W1 / 2, 395), "攻击 · 鼠群", kai(30), HOVER, "mm")
t = img(A + "tokens/ct_minus1.png").resize((150, 150))
glow(b, (W1 / 2 - 82, 425, W1 / 2 + 82, 589), AMBER, 0, 8, 10, True)
b.alpha_composite(t, (int(W1 / 2 - 75), 432))
text(b, (W1 / 2, 640), "战斗 4  +1（罗兰的.38特制手枪）  −1  =  4　对抗　1", sans(28), PAPER, "mm")
text(b, (W1 / 2, 700), "成功 · 超过 3 点", kai(44), GOOD, "mm")
text(b, (W1 / 2, 750), "自动应用中…", sans(18), MUTED, "mm")
float_text(b, [("鼠群  −2 生命 · 被击败", (240, 150, 120))], 1080, 300)
player_area(b, actions_left=2, ammo=3); right_zone(b); piles(b); hand(b, HAND5)
b.convert("RGB").save(OUT + "/mock-table-skilltest.png")

# ───────────── mock 3: phase banner (13.9)
b = background("hallway.png", dim=0.15)
top_bar(b, "神话阶段 · 第 2 轮"); player_area(b, actions_left=0); right_zone(b); piles(b); hand(b, HAND5)
band = layer(); bd = ImageDraw.Draw(band)
for i in range(160):
    bd.line((0, 380 + i, W1, 380 + i), fill=(10, 8, 6, int(215 * math.sin(math.pi * i / 159))))
b.alpha_composite(band)
text(b, (W1 / 2, 440), "神话阶段", kai(64), HOVER, "mm")
text(b, (W1 / 2, 500), "密谋放置毁灭 · 每位调查员抽一张遭遇卡", sans(22), MUTED, "mm")
b.convert("RGB").save(OUT + "/mock-table-phase.png")
print("ok")
