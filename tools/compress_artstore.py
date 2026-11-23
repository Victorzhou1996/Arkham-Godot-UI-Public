"""A smaller copy of the html green package's art store, for shipping (Yunke 11-22: "全部开始压缩").

Reads the store read-only (index.json + objects/), writes <out>/index.json and <out>/objects/:
  - only what the game reads (ArtStore.cs): "cards/<code>.*" and the html table's investigator portraits;
  - card scans at most 640 px tall (the game scales them to 520 px), WebP quality 85; portraits WebP quality 90, no resize;
  - cards the game already ships in app/assets/cards (<code>.webp) are left out: Ui.Card reads those first.
Run:  python -I tools/compress_artstore.py <store root> <out dir> <app/assets/cards dir>
"""
import json
import os
import sys
from concurrent.futures import ProcessPoolExecutor

from PIL import Image

MAX_H = 640
PORTRAITS = "game/frontend/dist/img/arkham/portraits/"


def work(job):
    src, dst, kind = job
    if os.path.exists(dst):
        return dst, os.path.getsize(dst), "kept"
    try:
        im = Image.open(src)
        im.load()
        mode = "RGBA" if im.mode in ("RGBA", "LA", "P") and "transparency" in im.info or im.mode in ("RGBA", "LA") else "RGB"
        im = im.convert(mode)
        if kind == "card" and im.height > MAX_H:
            im = im.resize((round(im.width * MAX_H / im.height), MAX_H), Image.LANCZOS)
        tmp = dst + ".tmp"
        im.save(tmp, "WEBP", quality=85 if kind == "card" else 90, method=5)
        os.replace(tmp, dst)
        return dst, os.path.getsize(dst), "new"
    except Exception as e:  # unreadable here: left out (the game could not read it either)
        return dst, 0, f"skip {type(e).__name__}: {e}"


def main():
    root, out, shipped = sys.argv[1], sys.argv[2], sys.argv[3]
    have = {os.path.splitext(f)[0] for f in os.listdir(shipped) if f.endswith(".webp")}
    index = json.load(open(os.path.join(root, "index.json"), encoding="utf-8"))
    os.makedirs(os.path.join(out, "objects"), exist_ok=True)
    paths, jobs, dup = {}, {}, 0
    for key, v in index["paths"].items():
        if not isinstance(v, dict) or "file" not in v:
            continue
        if key.startswith("cards/"):
            kind = "card"
            if os.path.splitext(os.path.basename(key))[0] in have:
                dup += 1
                continue
        elif key.startswith(PORTRAITS):
            kind = "portrait"
        else:
            continue
        src = os.path.join(root, v["file"])
        if not os.path.exists(src):
            continue
        rel = "objects/" + os.path.splitext(os.path.basename(v["file"]))[0] + ("" if kind == "card" else "-p") + ".webp"
        jobs[rel] = (src, os.path.join(out, rel), kind)
        nv = dict(v)
        nv["file"] = rel
        paths[key] = nv
    done, total, bad = 0, 0, []
    with ProcessPoolExecutor(max_workers=max(2, (os.cpu_count() or 4) - 2)) as ex:
        for dst, size, how in ex.map(work, jobs.values(), chunksize=16):
            done += 1
            total += size
            if how.startswith("skip"):
                bad.append(dst)
            if done % 500 == 0:
                print(f"{done}/{len(jobs)} {total / 2**20:.0f} MB", flush=True)
    bad_rel = {os.path.relpath(b, out).replace(os.sep, "/") for b in bad}
    paths = {k: v for k, v in paths.items() if v["file"] not in bad_rel}
    meta = {k: v for k, v in index.items() if k != "paths"}
    meta["paths"] = paths
    meta["compressedFrom"] = os.path.abspath(root)
    meta["compression"] = f"cards <= {MAX_H}px webp q85, portraits webp q90; cards shipped in app/assets/cards left out"
    with open(os.path.join(out, "index.json"), "w", encoding="utf-8") as f:
        json.dump(meta, f, ensure_ascii=False)
    print(f"done files={len(jobs)} total={total / 2**20:.0f} MB left-out-duplicates={dup} unreadable={len(bad)}")


if __name__ == "__main__":
    main()
