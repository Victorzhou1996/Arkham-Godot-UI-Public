# How chapter 3 (地底噬魔) ends for each run prefix: defeated by horror (mental trauma added) or damage (physical trauma added).
#   python tools/ai/ch3_defeats.py tceval-s3c-rules ...
import glob, json, os, re, sys
ROOT = os.path.abspath(os.path.join(os.path.dirname(__file__), "..", ".."))
for rx in sys.argv[1:]:
    mad = hurt = n = 0
    for d in glob.glob(os.path.join(ROOT, "evidence", "ai", "*")):
        if not re.match(rx, os.path.basename(d)) or not os.path.exists(os.path.join(d, "decisions.jsonl")):
            continue
        prev = None
        for line in open(os.path.join(d, "decisions.jsonl"), encoding="utf-8"):
            if '"chapterEnd"' in line:
                prev = json.loads(line)["rec"]
            elif '"campaignEnd"' in line:
                c = json.loads(line)
                if prev is None or not any("Umordhoth" in k for k in c["rec"]["keys"]):
                    prev = None; continue
                p = {t["code"]: t for t in prev["trauma"]}
                n += 1
                for t in c["rec"]["trauma"]:
                    q = p.get(t["code"], {"phys": 0, "ment": 0})
                    mad += t["ment"] > q["ment"]; hurt += t["phys"] > q["phys"]
                prev = None
    print("%-28s 第三章%3d局 发疯%3d 受伤倒下%3d" % (rx, n, mad, hurt))
