"""Contact sheets of the small-card art crops (DeckPreview.ArtFrame), for checking every player card without redrawn art.

Usage: python tools/dev/crop_sheets.py <out_dir> [overrides.json]
Each cell shows the art region cut from the printed card the way the table's small card shows it (cover-cropped to the small
card's proportions), with the card code under it. Read-only on the html green package.
"""
import json, os, sys
from PIL import Image, ImageDraw

ROOT = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
GREEN = os.path.join(ROOT, '..', 'ArkhamHorrorLocal-WinDeploy-20260712', 'green-packages', 'green-1.0.39-fast-20261118', 'art')
FRAMES = {'asset': (0.13, 0.11, 0.84, 0.355), 'event': (0.15, 0.03, 0.83, 0.44), 'skill': (0.13, 0.085, 0.85, 0.46),
          'enemy': (0.04, 0.64, 0.92, 0.30)}
OTHER = (0.10, 0.08, 0.85, 0.40)
CELL_W, CELL_H, COLS, ROWS = 100, 140, 10, 6


def cards_needing_crops():
    d = json.load(open(os.path.join(ROOT, 'app/assets/build-data/cards-zh-cn.json'), encoding='utf-8'))['data']['all_card']
    pl = [c for c in d if not c.get('encounter_code') and c.get('type_code') in ('asset', 'event', 'skill', 'treachery', 'enemy')
          and not c.get('duplicate_of_code') and not c.get('hidden')]
    drawn = set()
    for folder in ('app/assets/ref/art/cards', 'app/assets/art-fallback'):
        drawn |= {f[:5] for f in os.listdir(os.path.join(ROOT, folder)) if not f.endswith('.import')}
    seen, out = set(), []
    for c in sorted(pl, key=lambda c: c['code']):
        if c['code'] in drawn or c['code'] in seen: continue
        seen.add(c['code']); out.append(c)
    return out


def crop(img, frame):
    x, y, w, h = frame
    W, H = img.size
    region = img.crop((int(x * W), int(y * H), int((x + w) * W), int((y + h) * H)))
    rw, rh = region.size
    target = CELL_W / CELL_H
    if rw / rh > target:   # wider than the small card: keep the middle
        nw = int(rh * target); region = region.crop(((rw - nw) // 2, 0, (rw - nw) // 2 + nw, rh))
    else:
        nh = int(rw / target); region = region.crop((0, (rh - nh) // 2, rw, (rh - nh) // 2 + nh))
    return region.resize((CELL_W, CELL_H))


def main():
    out = sys.argv[1]
    overrides = json.load(open(sys.argv[2], encoding='utf-8')) if len(sys.argv) > 2 and os.path.exists(sys.argv[2]) else {}
    os.makedirs(out, exist_ok=True)
    idx = json.load(open(os.path.join(GREEN, 'index.json'), encoding='utf-8'))['paths']
    files = {os.path.splitext(k[6:])[0]: v['file'] for k, v in idx.items() if k.startswith('cards/') and 'file' in v}
    cards = [c for c in cards_needing_crops() if c['code'] in files]
    per = COLS * ROWS
    for s in range(0, len(cards), per):
        chunk = cards[s:s + per]
        sheet = Image.new('RGB', (COLS * (CELL_W + 4), ROWS * (CELL_H + 18)), (18, 16, 14))
        dr = ImageDraw.Draw(sheet)
        for i, c in enumerate(chunk):
            img = Image.open(os.path.join(GREEN, files[c['code']])).convert('RGB')
            frame = tuple(overrides.get(c['code'], FRAMES.get(c['type_code'], OTHER)))
            if c['code'].startswith('98') or c['code'].startswith('90'):   # the novella / promo layout: a title band over the art
                frame = (frame[0], frame[1] + 0.05, frame[2], frame[3] - 0.05)
            x0, y0 = (i % COLS) * (CELL_W + 4), (i // COLS) * (CELL_H + 18)
            sheet.paste(crop(img, frame), (x0, y0))
            dr.text((x0 + 2, y0 + CELL_H + 2), c['code'] + ' ' + c['type_code'][:2], fill=(255, 220, 120))
        name = os.path.join(out, 'sheet-%02d-%s-%s.png' % (s // per, chunk[0]['code'], chunk[-1]['code']))
        sheet.save(name)
        print(name)
    print('cards', len(cards))


if __name__ == '__main__':
    main()
