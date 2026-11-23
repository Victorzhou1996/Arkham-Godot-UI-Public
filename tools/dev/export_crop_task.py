"""Export the card-art crop task for an outside helper (Yunke 11-19: GPT picks the crop boxes, we read them back).

For every card the table cuts its art from (the player cards without a redrawn picture and the encounter enemies — the cards in
app/assets/build-data/art-focus.json) writes, under <out>:
  images/<code>.jpg      the printed card (width 600 px, portrait or landscape as printed)
  previews/<code>.jpg    the same card with the art region (cyan), today's small-card box (amber) and strip box (green)
  manifest.json          [{code, name, type, image, width, height, art, small, strip}] — boxes as [x, y, w, h] in the image's pixels
Usage: python tools/dev/export_crop_task.py <out_dir>
"""
import json, os, sys
from PIL import Image, ImageDraw

ROOT = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from art_focus import GREEN, frame_for, cards_needing_crops, encounter_enemies, SMALL, STRIP  # noqa: E402

out = sys.argv[1]
os.makedirs(os.path.join(out, 'images'), exist_ok=True); os.makedirs(os.path.join(out, 'previews'), exist_ok=True)
idx = json.load(open(os.path.join(GREEN, 'index.json'), encoding='utf-8'))['paths']
files = {os.path.splitext(k[6:])[0]: v['file'] for k, v in idx.items() if k.startswith('cards/') and 'file' in v}
focus = json.load(open(os.path.join(ROOT, 'app', 'assets', 'build-data', 'art-focus.json'), encoding='utf-8'))
TYPES = {'asset': '支援', 'event': '事件', 'skill': '技能', 'treachery': '诡计', 'enemy': '敌人'}
manifest = []
for c in cards_needing_crops() + encounter_enemies():
    code = c['code']
    if code not in files or code not in focus: continue
    img = Image.open(os.path.join(GREEN, files[code])).convert('RGB')
    s = 600 / img.size[0]; img = img.resize((600, round(img.size[1] * s)), Image.LANCZOS); W, H = img.size
    fx, fy, fw, fh = frame_for(c)
    ax, ay, aw, ah = round(fx * W), round(fy * H), round(fw * W), round(fh * H)
    # today's boxes: the small card is the full art height at 112:157, centred on fx; the strip the full width at 4.3:1 on fy
    sw = min(aw, round(ah * SMALL)); sx = min(max(ax + focus[code][0] * aw - sw / 2, ax), ax + aw - sw)
    bh = min(ah, round(aw / STRIP)); by = min(max(ay + focus[code][1] * ah - bh / 2, ay), ay + ah - bh)
    small = [round(sx), ay, sw, ah]; strip = [ax, round(by), aw, bh]
    img.save(os.path.join(out, 'images', code + '.jpg'), quality=88)
    pv = img.copy(); d = ImageDraw.Draw(pv)
    d.rectangle([ax, ay, ax + aw, ay + ah], outline=(0, 230, 255), width=3)
    d.rectangle([small[0], small[1], small[0] + small[2], small[1] + small[3]], outline=(255, 180, 0), width=4)
    d.rectangle([strip[0], strip[1], strip[0] + strip[2], strip[1] + strip[3]], outline=(80, 255, 120), width=2)
    pv.thumbnail((400, 400)); pv.save(os.path.join(out, 'previews', code + '.jpg'), quality=80)
    manifest.append({'code': code, 'name': c.get('name', ''), 'type': TYPES.get(c.get('type_code'), c.get('type_code')),
                     'image': 'images/' + code + '.jpg', 'width': W, 'height': H,
                     'art': [ax, ay, aw, ah], 'small': small, 'strip': strip})
json.dump(manifest, open(os.path.join(out, 'manifest.json'), 'w', encoding='utf-8'), ensure_ascii=False, indent=1)
print('cards', len(manifest))
