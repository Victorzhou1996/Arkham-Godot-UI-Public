# Mock-up only: four candidate styles for every numbered token on the table (skills, health/sanity/resources/clues,
# enemy stats), shown in context: engaged enemy sitting above the left player area (§17).
# Usage: python -I mock_tokens.py "<Arkham UI root>" "<out dir>"
import sys
from PIL import Image, ImageDraw, ImageFilter, ImageFont

ROOT, OUT = sys.argv[1], sys.argv[2]
src = open(ROOT + "/docs/mock/mock_table.py", encoding="utf-8").read().split("HAND5 = [")[0]
T = {"__name__": "mock_table"}; exec(src, T)
A = T["A"]; sans, kai, icon, cards = T["sans"], T["kai"], T["icon"], T["cards"]
S = 4  # supersampling factor
F = A + "fonts/"
NUM = lambda px: ImageFont.truetype(F + "arkham.ttf", px)      # the card-game numerals (0-9) in arkham.ttf
GLYPH = {"willpower": "A", "intellect": "F", "combat": "D", "agility": "S"}
SKC = {"willpower": (98, 140, 205), "intellect": (160, 110, 200), "combat": (205, 85, 65), "agility": (95, 165, 90)}
STC = {"health": (178, 58, 48), "sanity": (58, 110, 160), "resource": (160, 116, 60), "clue": (52, 132, 110)}
ICON = {"health": "health", "sanity": "sanity", "clue": "clues", "resource": "resources"}
IVORY = (240, 228, 204); INK = (44, 32, 20)

def ref_icon(kind, px, col=None, alpha=255):
    im = Image.open(A + "ref/ui/investigator-v10/" + ICON[kind] + ".webp").convert("RGBA").resize((px, px), Image.LANCZOS)
    if col:
        out = Image.new("RGBA", im.size, col + (0,)); a = im.getchannel("A").point(lambda v: v * alpha // 255); out.putalpha(a); return out
    if alpha < 255: im.putalpha(im.getchannel("A").point(lambda v: v * alpha // 255))
    return im

def canvas(w, h): return Image.new("RGBA", (w * S, h * S), (0, 0, 0, 0))
def down(im, w, h): return im.resize((w, h), Image.LANCZOS)
def lerp(a, b, t): return tuple(int(a[i] + (b[i] - a[i]) * t) for i in range(3))

def num(d, xy, s, px, fill, shadow=(0, 0, 0, 170), sh=2):
    f = NUM(px)
    if shadow: d.text((xy[0], xy[1] + sh * S), s, font=f, fill=shadow, anchor="mm")
    d.text(xy, s, font=f, fill=fill, anchor="mm")

def paste_glyph(im, ch, px, col, cx, cy, alpha=255):
    g = Image.new("RGBA", im.size, (0, 0, 0, 0)); ImageDraw.Draw(g).text((cx, cy), ch, font=icon(px), fill=col + (alpha,), anchor="mm"); im.alpha_composite(g)

def paste_icon(im, ic, cx, cy):
    im.alpha_composite(ic, (int(cx - ic.width / 2), int(cy - ic.height / 2)))

# ─────────────── style A: brass coin, faint emblem behind an engraved numeral
def brass_disc(d, box, rim):
    x0, y0, x1, y1 = box; n = int(rim)
    for i in range(n):
        t = i / max(1, n - 1); c = lerp((96, 72, 38), (236, 202, 132), 1 - abs(t - 0.45) * 1.8 if abs(t - 0.45) < 0.55 else 0)
        d.ellipse((x0 + i, y0 + i, x1 - i, y1 - i), outline=c, width=2)
    d.ellipse((x0 + n, y0 + n, x1 - n, y1 - n), fill=(30, 26, 21))

def A_skill(key, v, D=52):
    im = canvas(D, D); d = ImageDraw.Draw(im); W = D * S
    brass_disc(d, (0, 0, W - 1, W - 1), W * 0.11)
    paste_glyph(im, GLYPH[key], int(W * 0.66), SKC[key], W / 2, W / 2, 165)
    d = ImageDraw.Draw(im); d.ellipse((W * 0.11, W * 0.11, W * 0.89, W * 0.89), outline=SKC[key] + (200,), width=S * 2)
    num(d, (W / 2, W / 2), str(v), int(W * 0.5), (246, 232, 204))
    return down(im, D, D)

def A_stat(kind, v, D=52):
    im = canvas(D, D); d = ImageDraw.Draw(im); W = D * S
    brass_disc(d, (0, 0, W - 1, W - 1), W * 0.11)
    paste_icon(im, ref_icon(kind, int(W * 0.64), STC[kind], 200), W / 2, W / 2)
    d = ImageDraw.Draw(im); num(d, (W / 2, W / 2), str(v), int(W * 0.5), (246, 232, 204))
    return down(im, D, D)

# ─────────────── style B: ivory seal, coloured emblem tab on the rim, ink numeral
def B_base(D, ring_col):
    im = canvas(D, D); d = ImageDraw.Draw(im); W = D * S
    d.ellipse((0, 0, W - 1, W - 1), fill=(58, 42, 26))
    d.ellipse((S * 2, S * 2, W - 1 - S * 2, W - 1 - S * 2), fill=IVORY)
    for i in range(int(W * 0.2)):  # soft paper shading toward the bottom
        a = int(40 * (i / (W * 0.2)))
        d.arc((S * 2 + i * 0.1, S * 2 + i * 0.1, W - S * 2 - i * 0.1, W - S * 2 - i * 0.1), 20, 160, fill=(150, 120, 80, a), width=1)
    d.ellipse((W * 0.12, W * 0.12, W * 0.88, W * 0.88), outline=ring_col + (230,), width=S * 2)
    return im, d, W

def B_skill(key, v, D=52):
    im, d, W = B_base(D, SKC[key])
    num(d, (W / 2, W * 0.53), str(v), int(W * 0.5), INK, shadow=None)
    out = Image.new("RGBA", ((D + 10) * S, (D + 10) * S), (0, 0, 0, 0)); out.alpha_composite(im, (10 * S, 10 * S))
    od = ImageDraw.Draw(out); r = 11 * S
    od.ellipse((2 * S, 2 * S, 2 * S + 2 * r, 2 * S + 2 * r), fill=SKC[key], outline=(58, 42, 26), width=S * 2)
    paste_glyph(out, GLYPH[key], int(r * 1.3), (255, 255, 255), 2 * S + r, 2 * S + r + S)
    return down(out, D + 10, D + 10)

def B_stat(kind, v, D=52):
    im, d, W = B_base(D, STC[kind])
    num(d, (W / 2, W * 0.53), str(v), int(W * 0.5), INK, shadow=None)
    out = Image.new("RGBA", ((D + 10) * S, (D + 10) * S), (0, 0, 0, 0)); out.alpha_composite(im, (10 * S, 10 * S))
    paste_icon(out, ref_icon(kind, 26 * S), 13 * S, 13 * S)
    return down(out, D + 10, D + 10)

# ─────────────── style C: no container — coloured emblem + large numeral with a soft dark halo
def C_any(emblem, v, col, D=52):
    W = (D + 22) * S; im = Image.new("RGBA", (W, D * S), (0, 0, 0, 0))
    halo = Image.new("RGBA", im.size, (0, 0, 0, 0)); hd = ImageDraw.Draw(halo)
    hd.rounded_rectangle((S * 4, S * 6, W - S * 4, D * S - S * 6), radius=D * S // 2, fill=(0, 0, 0, 150))
    im.alpha_composite(halo.filter(ImageFilter.GaussianBlur(S * 5)))
    if isinstance(emblem, str): paste_glyph(im, emblem, int(D * S * 0.5), col, D * S * 0.36, D * S / 2)
    else: paste_icon(im, emblem, D * S * 0.36, D * S / 2)
    d = ImageDraw.Draw(im); num(d, (D * S * 0.95, D * S * 0.52), str(v), int(D * S * 0.62), (248, 236, 210), shadow=(0, 0, 0, 220), sh=2)
    return down(im, D + 22, D)

def C_skill(key, v, D=52): return C_any(GLYPH[key], v, SKC[key], D)
def C_stat(kind, v, D=52): return C_any(ref_icon(kind, int(D * S * 0.52)), v, STC[kind], D)

# ─────────────── style D: enamel disc — colour gradient, gold rim, gloss, white numeral, emblem on top
def D_base(D, col):
    im = canvas(D, D); W = D * S
    g = Image.new("RGBA", im.size, (0, 0, 0, 0)); gd = ImageDraw.Draw(g)
    for i in range(int(W / 2)):
        t = i / (W / 2); c = lerp(tuple(min(255, int(x * 1.35)) for x in col), tuple(int(x * 0.55) for x in col), 1 - t)
        gd.ellipse((W / 2 - (W / 2 - i), W / 2 - (W / 2 - i) - W * 0.06 * t, W / 2 + (W / 2 - i), W / 2 + (W / 2 - i) - W * 0.06 * t), fill=c)
    m = Image.new("L", im.size, 0); ImageDraw.Draw(m).ellipse((0, 0, W - 1, W - 1), fill=255); g.putalpha(m)
    im.alpha_composite(g); d = ImageDraw.Draw(im)
    d.ellipse((0, 0, W - 1, W - 1), outline=(214, 178, 108), width=S * 2)
    gl = Image.new("RGBA", im.size, (0, 0, 0, 0)); ImageDraw.Draw(gl).ellipse((W * 0.18, W * 0.08, W * 0.82, W * 0.42), fill=(255, 255, 255, 46)); im.alpha_composite(gl.filter(ImageFilter.GaussianBlur(S * 2)))
    return im, ImageDraw.Draw(im), W

def D_skill(key, v, D=52):
    im, d, W = D_base(D, SKC[key])
    paste_glyph(im, GLYPH[key], int(W * 0.26), (255, 255, 255), W / 2, W * 0.24, 230)
    d = ImageDraw.Draw(im); num(d, (W / 2, W * 0.6), str(v), int(W * 0.46), (255, 255, 255), shadow=(0, 0, 0, 150))
    return down(im, D, D)

def D_stat(kind, v, D=52):
    im, d, W = D_base(D, STC[kind])
    paste_icon(im, ref_icon(kind, int(W * 0.26), (255, 255, 255), 235), W / 2, W * 0.24)
    d = ImageDraw.Draw(im); num(d, (W / 2, W * 0.6), str(v), int(W * 0.46), (255, 255, 255), shadow=(0, 0, 0, 150))
    return down(im, D, D)

STYLES = {
    "A": ("黄铜徽章", "黄铜圈＋深色底，图标做浅浮雕，衬线数字", A_skill, A_stat),
    "B": ("象牙纸印", "象牙纸面＋墨色数字，彩色小徽记压在边上", B_skill, B_stat),
    "C": ("极简", "不要底座：彩色图标＋大号数字，靠柔和暗影衬底", C_skill, C_stat),
    "D": ("珐琅彩", "按属性上色的珐琅圆片，金边高光，白色数字", D_skill, D_stat),
}

def place_c(base, im, cx, cy): base.alpha_composite(im, (int(cx - im.width / 2), int(cy - im.height / 2)))

def install(style):
    _, _, sk, st = STYLES[style]
    def skill_unit(base, cx, cy, key, v, d=48): place_c(base, sk(key, v, 52), cx, cy)
    def token_num(base, kind, n, cx, cy, h=46, small_max=None):
        k = {"horror": "sanity"}.get(kind, kind); im = st(k, n, 52); place_c(base, im, cx, cy)
        if small_max is not None: T["text"](base, (cx + 26, cy + 20), "/" + str(small_max), sans(14), (182, 170, 145), "lm", False, stroke=2)
    T["skill_unit"] = skill_unit; T["token_num"] = token_num
    return sk, st

def engaged_enemy(base, style, cx, cy, w=196, h=147):
    sk, st = STYLES[style][2], STYLES[style][3]
    T["glow"](base, (cx - w / 2 - 8, cy - h / 2 - 8, cx + w / 2 + 8, cy + h / 2 + 8), (230, 178, 92), 18, 7, 8)
    p = T["plate"](Image.open(A + "ref/art/cards/01159-swarm-of-rats.webp"), w, h, (192, 84, 62), 5, 14)
    base.alpha_composite(p, (int(cx - p.width / 2), int(cy - p.height / 2)))
    T["text"](base, (cx, cy - h / 2 - 22), "与你交战 · 鼠群", sans(18), (232, 120, 96), "mm")
    L, R, Tp, B = cx - w / 2, cx + w / 2, cy - h / 2, cy + h / 2
    place_c(base, sk("combat", 1, 40), L + 2, Tp + 26)
    place_c(base, sk("agility", 3, 40), R - 2, Tp + 26)
    place_c(base, st("health", 1, 40), L + 2, B - 22)
    place_c(base, st("health", 1, 34), R - 30, B + 2)
    place_c(base, st("sanity", 0, 34), R + 8, B + 2)
    T["text"](base, (R - 11, B + 32), "攻击", sans(13), (182, 170, 145), "mm", False, stroke=2)

def frame(style):
    install(style)
    b = T["background"]("hallway.png")
    T["top_bar"](b)
    T["text"](b, (960, 118), "走廊", kai(46), (255, 241, 213), "mm")
    T["text"](b, (960, 160), "隐藏值 1　·　线索 0", sans(20), (234, 219, 189), "mm")
    T["exits"](b)
    T["player_area"](b)
    engaged_enemy(b, style, 227, 470)
    T["right_zone"](b); T["piles"](b)
    T["hand"](b, [("01020", 3), ("01039", None), ("01016", 4), ("01030", 1), ("01037", 2)])
    T["next_step"](b, "你的回合 · 剩余 3 次行动：拖动手牌到中央打出，或点击高亮的目标")
    return b

crops = []
for key in STYLES:
    f = frame(key)
    f.convert("RGB").save(OUT + f"/mock-tokens-{key}-1080p.png")
    crops.append((key, f.crop((0, 360, 470, 1080))))
CW, CH = 470, 720
sheet = Image.new("RGB", (CW * 4 + 30, CH + 70), (14, 12, 10))
d = ImageDraw.Draw(sheet)
for i, (key, c) in enumerate(crops):
    x = i * (CW + 10)
    sheet.paste(c.convert("RGB"), (x, 70))
    d.text((x + 12, 10), f"方案 {key} · {STYLES[key][0]}", font=kai(26), fill=(255, 241, 213))
    d.text((x + 12, 44), STYLES[key][1], font=sans(15), fill=(182, 170, 145))
sheet.save(OUT + "/mock-tokens-sheet-1x.png")
print("ok")
