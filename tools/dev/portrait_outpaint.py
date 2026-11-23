"""Widen each investigator's portrait to the player area's strip (426 : 196) with AI outpainting, so the face fits the strip's
height and the sides are real picture rather than a blurred copy (Yunke 11-19: "先保证高度容纳人脸，然后横向的边缘用ai补图").

For every investigator in app/assets/build-data/portrait-focus.json: take the picture the table shows (portrait_focus.picture_for),
scale it so the head (about 1.7 face boxes) takes ~88% of the strip's height (the same rule as the table), place it on a canvas
of the strip's proportions centred on the face, and let SDXL inpainting (diffusers/stable-diffusion-xl-1.0-inpainting-0.1, fp16,
on the GPU) paint the empty left and right parts. The original pixels are pasted back over the result (a soft seam), so the
investigator is untouched; only the new sides are generated.

Output: app/assets/art-portrait-wide/<code>.png (new files; nothing is overwritten) and a sheet in <sheet_dir>.
Usage: python tools/dev/portrait_outpaint.py <sheet_dir> [code,code,...]
"""
import json, os, sys
import numpy as np
from PIL import Image, ImageDraw, ImageFilter

ROOT = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import portrait_focus as pfm  # noqa: E402
from crop_sheets import GREEN  # noqa: E402

OUT_W, OUT_H = 1280, 592            # the strip (426 × 196) at about 3×, both multiples of 8
STRIP_H = 196
PROMPT = ("1920s Lovecraftian investigator portrait, painterly card illustration, continuation of the same scene, "
          "same lighting and colours, detailed background")
NEGATIVE = "text, letters, watermark, frame, border, card frame, extra person, extra face, blurry, deformed"


def layout(img, f):
    """The picture's size and position on the output canvas (the table's rule, at the output's scale)."""
    fx, fy, fs = f
    pw, ph = img.size
    k = OUT_H / STRIP_H
    cover = max(OUT_W / pw, OUT_H / ph)
    sc = cover
    if fs > 0: sc = min(max(0.88 * STRIP_H * k / (1.7 * fs * ph), OUT_H / ph), cover)
    aw, ah = pw * sc, ph * sc
    ax = OUT_W / 2 - fx * aw
    ay = min(max(OUT_H * 0.45 - fy * ah, OUT_H - ah), 0)
    ax = min(max(ax, OUT_W - aw), 0) if aw >= OUT_W else min(max(ax, 0), OUT_W - aw)
    return sc, int(round(ax)), int(round(ay)), int(round(aw)), int(round(ah))


def main():
    sheet_dir = sys.argv[1]; only = set(sys.argv[2].split(',')) if len(sys.argv) > 2 else None
    os.makedirs(sheet_dir, exist_ok=True)
    out_dir = os.path.join(ROOT, 'app', 'assets', 'art-portrait-wide'); os.makedirs(out_dir, exist_ok=True)
    focus = json.load(open(os.path.join(ROOT, 'app/assets/build-data/portrait-focus.json'), encoding='utf-8'))
    entries = json.load(open(os.path.join(GREEN, 'index.json'), encoding='utf-8'))['paths']

    import torch
    from diffusers import StableDiffusionXLInpaintPipeline
    pipe = StableDiffusionXLInpaintPipeline.from_pretrained(
        'diffusers/stable-diffusion-xl-1.0-inpainting-0.1', torch_dtype=torch.float16, variant='fp16').to('cuda')
    pipe.set_progress_bar_config(disable=True)

    done = []
    for code, f in focus.items():
        if only and code not in only: continue
        img, kind = pfm.picture_for(code, entries)
        if img is None: continue
        sc, ax, ay, aw, ah = layout(img, f)
        if aw >= OUT_W - 2:
            # already as wide as the strip: nothing to paint
            continue
        canvas = Image.new('RGB', (OUT_W, OUT_H), (0, 0, 0))
        small = img.resize((aw, ah), Image.LANCZOS)
        # the sides start from a blurred stretch of the picture's own edges, so the model keeps its colours
        base = img.resize((OUT_W, int(OUT_W * img.size[1] / img.size[0])), Image.LANCZOS).filter(ImageFilter.GaussianBlur(24))
        canvas.paste(base, (0, int(min(max(OUT_H * 0.45 - f[1] * base.size[1], OUT_H - base.size[1]), 0))))
        canvas.paste(small, (ax, ay))
        # mask: white where to paint (the sides), with a 24 px overlap into the picture so the seam is painted too
        mask = Image.new('L', (OUT_W, OUT_H), 255)
        ImageDraw.Draw(mask).rectangle([ax + 24, 0, ax + aw - 24, OUT_H], fill=0)
        mask = mask.filter(ImageFilter.GaussianBlur(8))
        # SEED_OFFSET (environment) gives a picture another try when its first sides came out wrong
        g = torch.Generator('cuda').manual_seed((int(code.strip('ab')[-5:]) if code.strip('ab')[-5:].isdigit() else 7) + int(os.environ.get('SEED_OFFSET', '0')))
        res = pipe(prompt=PROMPT, negative_prompt=NEGATIVE, image=canvas, mask_image=mask, width=OUT_W, height=OUT_H,
                   strength=0.99, guidance_scale=7.0, num_inference_steps=30, generator=g).images[0]
        res = res.resize((OUT_W, OUT_H))
        # the investigator's own pixels back on top, blended over the overlap
        keep = Image.new('L', (OUT_W, OUT_H), 0)
        ImageDraw.Draw(keep).rectangle([ax + 20, 0, ax + aw - 20, OUT_H], fill=255)
        keep = keep.filter(ImageFilter.GaussianBlur(10))
        orig = Image.new('RGB', (OUT_W, OUT_H)); orig.paste(small, (ax, ay))
        final = Image.composite(orig, res, keep)
        final.save(os.path.join(out_dir, code + '.png'))
        done.append((code, final))
        print('done', code, flush=True)
    # sheet
    cols = 3
    for s in range(0, len(done), 24):
        ch = done[s:s + 24]; rows = (len(ch) + cols - 1) // cols
        sheet = Image.new('RGB', (cols * 430, rows * 214), (18, 16, 14)); d = ImageDraw.Draw(sheet)
        for i, (code, im) in enumerate(ch):
            X, Y = (i % cols) * 430, (i // cols) * 214
            sheet.paste(im.resize((426, 196)), (X, Y)); d.text((X + 3, Y + 198), code, fill=(255, 220, 120))
        sheet.save(os.path.join(sheet_dir, 'wide-%02d.png' % (s // 24)))
    print('total', len(done))


if __name__ == '__main__':
    main()
