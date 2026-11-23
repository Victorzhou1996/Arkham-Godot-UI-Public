# Mock-up only: player area v4 (§18) — skills across the top of the portrait on a dark fade,
# 0712-web-style stat tokens packed tight with the action arrows beside them, assets below.
# Usage: python -I mock_player.py "<Arkham UI root>" "<out dir>"
import sys
from PIL import Image, ImageDraw

ROOT, OUT = sys.argv[1], sys.argv[2]
src = open(ROOT + "/docs/mock/mock_tokens.py", encoding="utf-8").read().split("crops = []")[0]
K = {"__name__": "mock_tokens"}; exec(src, K)
T = K["T"]; A = K["A"]; sans, kai, icon, NUM = K["sans"], K["kai"], K["icon"], K["NUM"]
text, panel, pill, plate, feathered, tint, fit, img = T["text"], T["panel"], T["pill"], T["plate"], T["feathered"], T["tint"], T["fit"], T["img"]
HOVER, MUTED, PAPER, AMBER, BRASS = (255, 241, 213), (182, 170, 145), (234, 219, 189), (230, 178, 92), (165, 138, 84)
C_skill, place_c = K["C_skill"], K["place_c"]

PX0, PY0, PX1, PY1 = 14, 610, 440, 1066

def web_token(base, kind, n, x, cy, h=46):
    """0712 web token: the round/hex token picture with a bold white numeral on top (black outline)."""
    t = fit(img(A + "ui/tokens/" + kind + ".png"), h)
    base.alpha_composite(t, (int(x), int(cy - t.height / 2)))
    d = ImageDraw.Draw(base)
    d.text((x + t.width / 2, cy + 1), str(n), font=NUM(30), fill=(255, 255, 255), anchor="mm", stroke_width=3, stroke_fill=(12, 10, 8))
    return t.width

def player_area(base, actions_left=3, actions_total=3, damage=0, horror=0, res=6, clues=2, ammo=4):
    panel(base, (PX0, PY0, PX1, PY1), radius=12, fill=(16, 14, 11, 236), outline=(110, 92, 60), width=1)
    W = PX1 - PX0 - 4
    art = feathered(Image.open(A + "ref/art/cards/01001-roland-banks.webp"), W, 222, 0.0, edges=(30, 4, 30, 96))   # crop from the very top so the face sits lower, below the skills
    base.alpha_composite(art, (PX0 + 2, PY0 + 2))
    # dark fade across the top of the portrait, skills sit on it (style C: emblem + numeral, no base)
    BH = 64
    band = Image.new("RGBA", (W, BH), (0, 0, 0, 0)); bd = ImageDraw.Draw(band)
    for y in range(BH): bd.line((0, y, W, y), fill=(10, 8, 6, int(230 * (1 - y / BH) ** 1.3)))
    m = Image.new("L", (W, BH), 0); ImageDraw.Draw(m).rounded_rectangle((0, 0, W - 1, 120), radius=11, fill=255)
    band.putalpha(Image.composite(band.getchannel("A"), Image.new("L", (W, BH), 0), m))
    base.alpha_composite(band, (PX0 + 2, PY0 + 2))
    for i, (k, v) in enumerate([("willpower", 3), ("intellect", 3), ("combat", 4), ("agility", 2)]):
        place_c(base, C_skill(k, v, 40), PX0 + 58 + i * 102, PY0 + 26)
    text(base, (PX0 + 20, PY0 + 196), "罗兰·班克斯", kai(30), HOVER, "lm", stroke=2)
    # stat tokens, packed tight in the 0712 order: resources, damage, horror, clues
    x = PX0 + 18; cy = PY0 + 256
    for kind, n in [("resource", res), ("health", damage), ("horror", horror), ("clue", clues)]:
        x += web_token(base, kind, n, x, cy) + 2
    # action arrows use the freed space on the same row
    d = ImageDraw.Draw(base)
    for i in range(actions_total):
        ax = PX1 - 150 + i * 46
        d.text((ax + 1, cy - 17), "I", font=icon(40), fill=(0, 0, 0))
        d.text((ax, cy - 19), "I", font=icon(40), fill=AMBER if i < actions_left else (95, 86, 66))
    d.line((PX0 + 18, PY0 + 294, PX1 - 18, PY0 + 294), fill=(90, 76, 50))
    text(base, (PX0 + 20, PY0 + 302), "资产", kai(20), HOVER, shadow=False)
    a = plate(Image.open(A + "ref/art/cards/01006-rolands-38-special.webp"), 96, 70, BRASS, 3, 8)
    base.alpha_composite(a, (PX0 + 18, PY0 + 332))
    pill(base, PX0 + 104, PY0 + 336, "弹药 " + str(ammo), (150, 110, 40), 14)
    base.alpha_composite(tint(fit(img(T["SLOT_ICON"]["hand"]), 20), PAPER), (PX0 + 24, PY0 + 378))
    for i in range(3):
        bx = PX0 + 130 + i * 72
        panel(base, (bx, PY0 + 332, bx + 62, PY0 + 408), radius=8, fill=(30, 28, 24, 150), outline=(80, 70, 50), width=1)
    x = PX0 + 20
    for k, free, total in [("hand", 1, 2), ("arcane", 2, 2), ("accessory", 1, 1), ("body", 1, 1), ("ally", 1, 1)]:
        s = tint(fit(img(T["SLOT_ICON"][k]), 24), MUTED if free else (110, 70, 60)); base.alpha_composite(s, (x, PY0 + 420))
        text(base, (x + s.width + 4, PY0 + 432), str(free), sans(18), HOVER if free else (140, 80, 70), "lm", False)
        text(base, (x + s.width + 16, PY0 + 435), "/" + str(total), sans(12), MUTED, "lm", False)
        x += s.width + 50

def frame():
    K["install"]("C")
    b = T["background"]("hallway.png")
    T["top_bar"](b)
    text(b, (960, 118), "走廊", kai(46), HOVER, "mm")
    text(b, (960, 160), "隐藏值 1　·　线索 0", sans(20), PAPER, "mm")
    T["exits"](b)
    player_area(b)
    K["engaged_enemy"](b, "C", 227, 470)
    T["right_zone"](b); T["piles"](b)
    T["hand"](b, [("01020", 3), ("01039", None), ("01016", 4), ("01030", 1), ("01037", 2)])
    T["next_step"](b, "你的回合 · 剩余 3 次行动：拖动手牌到中央打出，或点击高亮的目标")
    return b

f = frame()
f.convert("RGB").save(OUT + "/mock-player-v4-1080p.png")
f.crop((0, 360, 470, 1080)).convert("RGB").save(OUT + "/mock-player-v4-crop-1x.png")
print("ok")
