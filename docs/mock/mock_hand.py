# Mock-up only: small hand cards (art + name, cost on frame edge) and a hover text box, at 1:1 on a 1080p table shot.
import json, re, sys, glob
from PIL import Image, ImageDraw, ImageFont, ImageFilter

ROOT = sys.argv[1]           # G:/Python Project/Arkham UI
OUT = sys.argv[2]            # scratchpad dir
A = ROOT + "/app/assets/"
F = A + "fonts/"
sans = lambda s: ImageFont.truetype(F + "SourceHanSansCN-Medium.otf", s)
serif = lambda s: ImageFont.truetype(F + "SourceHanSerifCN-Regular.otf", s)
kai = lambda s: ImageFont.truetype(F + "LXGWWenKai-Medium.ttf", s)
icon = lambda s: ImageFont.truetype(F + "arkham.ttf", s)
GLYPH = {"action": "I", "combat": "D", "intellect": "F", "willpower": "A", "agility": "S", "wild": "G", "fast": "u", "reaction": "y", "free": "u"}

cards = {c["code"]: c for c in json.load(open(A + "data/core-cards.json", encoding="utf-8"))}
# cost / type from the rules source (CardDef); skills have no cost
HAND = [("01020", 3), ("01039", None), ("01016", 4), ("01030", 1), ("01037", 2)]

BRASS = (165, 138, 84); AMBER = (230, 178, 92); PAPER = (234, 219, 189); INK = (24, 22, 18)

def cover(img, w, h):
    r = max(w / img.width, h / img.height)
    img = img.resize((int(img.width * r + 1), int(img.height * r + 1)), Image.LANCZOS)
    x, y = (img.width - w) // 2, int((img.height - h) * 0.35)
    return img.crop((x, y, x + w, y + h))

def small_card(code, cost, W=150, H=210):
    c = Image.new("RGBA", (W + 20, H + 20), (0, 0, 0, 0))
    art_p = glob.glob(A + "ref/art/cards/" + code + "-*.webp")
    art = Image.open(art_p[0]).convert("RGB") if art_p else Image.new("RGB", (W, H), (60, 55, 45))
    # art crop: its vertical extent equals the full card height (bleeds under the frame; no inner dark margin)
    inner = cover(art, W, H)
    face = Image.new("RGBA", (W, H), (30, 28, 24, 255))
    face.paste(inner, (0, 0))
    d = ImageDraw.Draw(face)
    # name band: dark at the bottom, fading to transparent upward (no hard top edge)
    BH = 58
    band = Image.new("RGBA", (W, BH), (18, 16, 13, 0))
    bd = ImageDraw.Draw(band)
    for yy in range(BH):
        t = yy / (BH - 1)                      # 0 at top, 1 at bottom
        a = int(225 * min(1.0, t / 0.55) ** 1.6)  # smooth ramp, solid in the lower ~45%
        bd.line((0, yy, W, yy), fill=(18, 16, 13, a))
    face.alpha_composite(band, (0, H - BH))
    name = cards[code]["name"]
    f = kai(18)
    while d.textlength(name, font=f) > W - 16 and f.size > 15: f = kai(f.size - 1)
    d.text((W / 2, H - 20), name, font=f, fill=PAPER, anchor="mm")
    # frame: a single brass rim drawn over the art
    d.rounded_rectangle((1, 1, W - 2, H - 2), radius=10, outline=BRASS, width=3)
    mask = Image.new("L", (W, H), 0); ImageDraw.Draw(mask).rounded_rectangle((0, 0, W - 1, H - 1), radius=10, fill=255)
    c.paste(face, (10, 10), mask)
    # cost medallion on the frame edge (template element)
    if cost is not None:
        cd = ImageDraw.Draw(c)
        cx, cy, r = 22, 22, 18
        cd.ellipse((cx - r, cy - r, cx + r, cy + r), fill=(35, 52, 92), outline=AMBER, width=3)
        cd.text((cx, cy - 1), str(cost), font=sans(22), fill=(255, 255, 255), anchor="mm")
    return c

def rich_lines(text, width, f_txt, f_ico):
    """Wrap Chinese text with [icon] tokens and <b> tags; returns list of lines of (kind, str, bold)."""
    text = re.sub(r"</?i>", "", text)
    tokens = []
    bold = False
    for part in re.split(r"(\[[a-z_]+\]|<b>|</b>|\n)", text):
        if not part: continue
        if part == "<b>": bold = True; continue
        if part == "</b>": bold = False; continue
        if part == "\n": tokens.append(("nl", "", False)); continue
        m = re.fullmatch(r"\[([a-z_]+)\]", part)
        if m: tokens.append(("ico", GLYPH.get(m.group(1), "?"), False)); continue
        for ch in part: tokens.append(("ch", ch, bold))
    lines, cur, x = [], [], 0
    tmp = ImageDraw.Draw(Image.new("RGB", (1, 1)))
    for kind, s, b in tokens:
        if kind == "nl": lines.append(cur); cur, x = [], 0; continue
        w = tmp.textlength(s, font=f_ico if kind == "ico" else f_txt) + (2 if kind == "ico" else 0)
        if x + w > width and cur: lines.append(cur); cur, x = [], 0
        cur.append((kind, s, b)); x += w
    if cur: lines.append(cur)
    return lines

def text_box(code, cost, W=400):
    c = cards[code]
    f_t, f_s, f_b, f_i = kai(26), sans(17), serif(20), icon(22)
    pad = 18
    lines = rich_lines(c.get("text", ""), W - 2 * pad, f_b, f_i)
    H = pad + 34 + 26 + 10 + len(lines) * 31 + pad
    img = Image.new("RGBA", (W, H), (0, 0, 0, 0))
    d = ImageDraw.Draw(img)
    d.rounded_rectangle((0, 0, W - 1, H - 1), radius=10, fill=(26, 24, 20, 240), outline=BRASS, width=2)
    d.text((pad, pad), c["name"], font=f_t, fill=(255, 241, 213))
    sub = c.get("type_name", "") + ("  ·  " + c["traits"] if c.get("traits") else "")
    d.text((pad, pad + 36), sub, font=f_s, fill=(182, 170, 145))
    y = pad + 36 + 26 + 10
    d.line((pad, y - 6, W - pad, y - 6), fill=(90, 74, 46), width=1)
    for ln in lines:
        x = pad
        for kind, s, b in ln:
            if kind == "ico":
                d.text((x, y + 2), s, font=f_i, fill=AMBER); x += d.textlength(s, font=f_i) + 2
            else:
                d.text((x, y), s, font=f_b, fill=(255, 241, 213) if b else PAPER, stroke_width=1 if b else 0, stroke_fill=(255, 241, 213))
                x += d.textlength(s, font=f_b)
        y += 31
    return img

shot = Image.open(ROOT + "/evidence/m4/immersive2/02-phase-InvestigationPhase.png").convert("RGBA")
W, H = 136, 190
# Fan layout: cards on an arc, each tilted; the hovered card straightens, lifts, and its neighbours make room.
step = 130                     # spacing between card centres; shrinks only when the hand is large
tilt = 5.5                     # degrees per position from the middle
sag = 6                        # px drop per (offset^2) along the arc
push = 18                      # neighbours slide away from the hovered card
n = len(HAND); mid = (n - 1) / 2
cx = (580 + (1920 - 440)) // 2  # centre of the free area between the portrait and the action panel
base_cy = 1080 - H // 2 - 48   # centre y of the middle card (outer cards stay fully on screen)
hover = 2   # the .45 Automatic is hovered

def place(img, centre):
    shot.alpha_composite(img, (int(centre[0] - img.width / 2), int(centre[1] - img.height / 2)))

for i, (code, cost) in enumerate(HAND):
    if i == hover: continue
    off = i - mid
    x = cx + off * step + (push if i > hover else -push if i < hover else 0) * (1 if abs(i - hover) == 1 else 0.5)
    y = base_cy + off * off * sag
    card = small_card(code, cost, W, H).rotate(-off * tilt, resample=Image.BICUBIC, expand=True)
    place(card, (x, y))

code, cost = HAND[hover]
lift = 40
hx, hy = cx + (hover - mid) * step, base_cy - lift
card = small_card(code, cost, W, H)
glow = Image.new("RGBA", card.size, (0, 0, 0, 0)); ImageDraw.Draw(glow).rounded_rectangle((6, 6, card.width - 6, card.height - 6), radius=12, outline=AMBER, width=6)
place(glow.filter(ImageFilter.GaussianBlur(5)), (hx, hy))
place(card, (hx, hy))
box = text_box(code, cost)
bx = int(hx - box.width / 2)
by = int(hy - card.height / 2 - box.height - 10)
shot.alpha_composite(box, (bx, by))
shot.convert("RGB").save(OUT + "/mock-hand-1080p.png")
shot.crop((bx - 260, by - 20, bx + box.width + 260, 1080)).convert("RGB").save(OUT + "/mock-hand-crop-1x.png")
print("ok", box.size, (bx, by))
