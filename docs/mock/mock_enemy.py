# Mock-up only: engaged enemy as a tall card like the hand cards (name on a dark fade at the bottom),
# fight / health / evade inside the top — four layout options to choose from (§19).
# Usage: python -I mock_enemy.py "<Arkham UI root>" "<out dir>"
import sys
from PIL import Image, ImageDraw, ImageFilter

ROOT, OUT = sys.argv[1], sys.argv[2]
src = open(ROOT + "/docs/mock/mock_player.py", encoding="utf-8").read().split("def frame():")[0]
P = {"__name__": "mock_player"}; exec(src, P)
K, T = P["K"], P["T"]; A = P["A"]
sans, kai, icon, NUM = P["sans"], P["kai"], P["icon"], P["NUM"]
cover, cards = T["cover"], T["cards"]
HOVER, MUTED, PAPER, AMBER = (255, 241, 213), (182, 170, 145), (234, 219, 189), (230, 178, 92)
RED = (192, 84, 62); COMBAT = (205, 85, 65); AGILITY = (95, 165, 90); HEALTH = (196, 60, 52); BRASS = (165, 138, 84)
s = 3   # supersample

def heart(px, col):
    im = Image.open(A + "ref/ui/investigator-v10/health.webp").convert("RGBA").resize((px, px), Image.LANCZOS)
    out = Image.new("RGBA", im.size, col + (0,)); out.putalpha(im.getchannel("A")); return out

def glyph(d, xy, ch, px, col): d.text(xy, ch, font=icon(px), fill=col, anchor="mm")
def number(d, xy, n, px, col=(255, 255, 255), stroke=0):
    d.text((xy[0], xy[1] + s), str(n), font=NUM(px), fill=(0, 0, 0, 190), anchor="mm")
    d.text(xy, str(n), font=NUM(px), fill=col, anchor="mm", stroke_width=stroke, stroke_fill=(12, 10, 8))

def top_fade(card, h, alpha=225):
    W = card.width; band = Image.new("RGBA", (W, h), (0, 0, 0, 0)); bd = ImageDraw.Draw(band)
    for y in range(h): bd.line((0, y, W, y), fill=(10, 8, 6, int(alpha * (1 - y / h) ** 1.3)))
    card.alpha_composite(band)

# ── option 1: dark fade across the top; three stats spaced evenly (emblem + numeral, no base)
def opt1(card, W, H):
    top_fade(card, int(H * 0.27)); d = ImageDraw.Draw(card); y = int(H * 0.085)
    glyph(d, (W * 0.13, y), "D", 19 * s, COMBAT); number(d, (W * 0.27, y + s), 1, 22 * s)
    card.alpha_composite(heart(19 * s, HEALTH), (int(W * 0.41), int(y - 9.5 * s))); number(d, (W * 0.6, y + s), 1, 22 * s)
    glyph(d, (W * 0.74, y), "S", 19 * s, AGILITY); number(d, (W * 0.88, y + s), 3, 22 * s)

# ── option 2: like the printed card — fight top-left, health heart in the middle, evade top-right
def opt2(card, W, H):
    top_fade(card, int(H * 0.2), 170); d = ImageDraw.Draw(card); y = int(H * 0.1); r = 15 * s
    for cx, col, ch, n in [(W * 0.17, COMBAT, "D", 1), (W * 0.83, AGILITY, "S", 3)]:
        d.ellipse((cx - r, y - r, cx + r, y + r), fill=(20, 18, 15, 235), outline=col, width=2 * s)
        number(d, (cx, y + s), n, 21 * s)
        bx, by = cx + (-r if cx < W / 2 else r) * 0.75, y + r * 0.8
        d.ellipse((bx - 7 * s, by - 7 * s, bx + 7 * s, by + 7 * s), fill=col, outline=(20, 18, 15), width=s)
        glyph(d, (bx, by + s / 2), ch, 9 * s, (255, 255, 255))
    hh = heart(34 * s, HEALTH); card.alpha_composite(hh, (int(W / 2 - hh.width / 2), int(y - hh.height / 2)))
    d = ImageDraw.Draw(card); number(d, (W / 2, y + s), 1, 20 * s, stroke=s)

# ── option 3: one dark plaque inside the top edge, split into three cells
def opt3(card, W, H):
    d = ImageDraw.Draw(card); m = 7 * s; h = 30 * s; y0 = m
    pl = Image.new("RGBA", card.size, (0, 0, 0, 0)); pd = ImageDraw.Draw(pl)
    pd.rounded_rectangle((m, y0, W - m, y0 + h), radius=8 * s, fill=(18, 16, 13, 225), outline=BRASS + (230,), width=s)
    for i in (1, 2): x = m + (W - 2 * m) * i / 3; pd.line((x, y0 + 6 * s, x, y0 + h - 6 * s), fill=(110, 92, 60, 230), width=s)
    card.alpha_composite(pl); d = ImageDraw.Draw(card); cy = y0 + h / 2
    cw = (W - 2 * m) / 3
    for i, (kind, col, n) in enumerate([("D", COMBAT, 1), ("H", HEALTH, 1), ("S", AGILITY, 3)]):
        cx = m + cw * i + cw / 2
        if kind == "H": hh = heart(16 * s, col); card.alpha_composite(hh, (int(cx - 15 * s), int(cy - 7 * s))); d = ImageDraw.Draw(card)
        else: glyph(d, (cx - 8 * s, cy), kind, 16 * s, col)
        number(d, (cx + 8 * s, cy + s), n, 19 * s)

# ── option 4: enamel discs hanging just inside the top edge (colour-coded), health as a red heart token
def opt4(card, W, H):
    top_fade(card, int(H * 0.18), 140); d = ImageDraw.Draw(card); y = 20 * s; r = 15 * s
    for cx, col, ch, n in [(W * 0.2, COMBAT, "D", 1), (W * 0.5, HEALTH, None, 1), (W * 0.8, AGILITY, "S", 3)]:
        g = Image.new("RGBA", card.size, (0, 0, 0, 0)); gd = ImageDraw.Draw(g)
        for i in range(int(r)):
            t = i / r; c = tuple(int(v * (0.55 + 0.6 * t)) if int(v * (0.55 + 0.6 * t)) < 255 else 255 for v in col)
            gd.ellipse((cx - r + i, y - r + i - t * 2 * s, cx + r - i, y + r - i - t * 2 * s), fill=c)
        card.alpha_composite(g); d = ImageDraw.Draw(card)
        d.ellipse((cx - r, y - r, cx + r, y + r), outline=(214, 178, 108), width=s)
        if ch: glyph(d, (cx, y - 7 * s), ch, 9 * s, (255, 255, 255))
        else:
            hh = heart(9 * s, (255, 255, 255)); card.alpha_composite(hh, (int(cx - hh.width / 2), int(y - 12 * s))); d = ImageDraw.Draw(card)
        number(d, (cx, y + 5 * s), n, 17 * s)

OPTIONS = [("1", "顶部暗带", "顶部加黑色渐变，三项用图标＋数字均匀排开", opt1),
           ("2", "仿原卡", "战斗、躲避做成左右两枚圆章，生命做成中间的红心", opt2),
           ("3", "顶部铭牌", "顶部一块深色铭牌，分成三格", opt3),
           ("4", "珐琅圆片", "三枚按属性上色的小圆片挂在顶部内侧", opt4)]

def enemy_card(option, W=156, H=218, name="鼠群", ring=RED):
    Ws, Hs = W * s, H * s
    art = cover(Image.open(A + "ref/art/cards/01159-swarm-of-rats.webp").convert("RGB"), Ws, Hs, 0.45).convert("RGBA")
    card = Image.new("RGBA", (Ws, Hs), (0, 0, 0, 0)); card.alpha_composite(art)
    # name on a dark fade at the bottom, like the hand cards
    BH = int(Hs * 0.3); band = Image.new("RGBA", (Ws, BH), (0, 0, 0, 0)); bd = ImageDraw.Draw(band)
    for y in range(BH): bd.line((0, y, Ws, y), fill=(18, 16, 13, int(230 * min(1.0, (y / (BH - 1)) / 0.55) ** 1.6)))
    card.alpha_composite(band, (0, Hs - BH))
    d = ImageDraw.Draw(card); d.text((Ws / 2, Hs - 21 * s), name, font=kai(21 * s), fill=PAPER, anchor="mm")
    option(card, Ws, Hs)
    d = ImageDraw.Draw(card); d.rounded_rectangle((s, s, Ws - 2 * s, Hs - 2 * s), radius=11 * s, outline=ring, width=4 * s)
    m = Image.new("L", (Ws, Hs), 0); ImageDraw.Draw(m).rounded_rectangle((0, 0, Ws - 1, Hs - 1), radius=11 * s, fill=255)
    out = Image.new("RGBA", (Ws, Hs), (0, 0, 0, 0)); out.paste(card, (0, 0), m)
    return out.resize((W, H), Image.LANCZOS)

def frame(option):
    K["install"]("C")
    b = T["background"]("hallway.png")
    T["top_bar"](b)
    T["text"](b, (960, 118), "走廊", kai(46), HOVER, "mm")
    T["text"](b, (960, 160), "隐藏值 1　·　线索 0", sans(20), PAPER, "mm")
    T["exits"](b)
    P["player_area"](b)
    c = enemy_card(option); cx, cy = 227, 488
    T["glow"](b, (cx - c.width / 2 - 8, cy - c.height / 2 - 8, cx + c.width / 2 + 8, cy + c.height / 2 + 8), AMBER, 16, 7, 8)
    b.alpha_composite(c, (int(cx - c.width / 2), int(cy - c.height / 2)))
    T["text"](b, (cx, cy - c.height / 2 - 20), "与你交战", sans(18), (232, 120, 96), "mm")
    T["right_zone"](b); T["piles"](b)
    T["hand"](b, [("01020", 3), ("01039", None), ("01016", 4), ("01030", 1), ("01037", 2)])
    T["next_step"](b, "你的回合 · 剩余 3 次行动：拖动手牌到中央打出，或点击高亮的目标")
    return b

CW = 470; top, bottom = 330, 900
sheet = Image.new("RGB", (CW * 4 + 30, (bottom - top) + 80 + 500), (14, 12, 10)); d = ImageDraw.Draw(sheet)
for i, (key, title, desc, fn) in enumerate(OPTIONS):
    f = frame(fn); f.convert("RGB").save(OUT + f"/mock-enemy-{key}-1080p.png")
    x = i * (CW + 10)
    d.text((x + 12, 10), f"方案 {key} · {title}", font=kai(26), fill=HOVER)
    d.text((x + 12, 46), desc, font=sans(15), fill=MUTED)
    sheet.paste(f.crop((0, top, CW, bottom)).convert("RGB"), (x, 80))
    big = enemy_card(fn).resize((156 * 2, 218 * 2), Image.LANCZOS)   # 2× zoom of the on-screen card
    bg = Image.new("RGBA", big.size, (14, 12, 10, 255)); bg.alpha_composite(big)
    sheet.paste(bg.convert("RGB"), (x + (CW - big.width) // 2, 80 + (bottom - top) + 40))
d.text((12, 80 + (bottom - top) + 8), "上：实际大小（1:1）　下：放大 2 倍看细节", font=sans(16), fill=MUTED)
sheet.save(OUT + "/mock-enemy-sheet.png")
print("ok")

# final reference frame: player area v4 + enemy option 3 (Yunke's pick) + heavier background blur
frame(opt3).convert("RGB").save(OUT + "/final-table-1080p.png")
print("final ok")
