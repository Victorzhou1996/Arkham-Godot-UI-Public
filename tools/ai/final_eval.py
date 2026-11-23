# Final comparison with the latest models (fixed seeds): rules / search teacher / policy network, solo Easy + Standard and 4-player Easy.
#   python tools/ai/final_eval.py <name> [games]
import os, sys, time
from concurrent.futures import ThreadPoolExecutor
sys.path.insert(0, os.path.dirname(__file__))
import loop_mc

name = sys.argv[1] if len(sys.argv) > 1 else "final"
n = int(sys.argv[2]) if len(sys.argv) > 2 else 20
jobs = []
for side, flags in (("rules", ""), ("teach", "teach"), ("net", "net")):
    jobs.append(("%s-%s-1E" % (name, side), n, 1, "Easy", 9001, flags))
    jobs.append(("%s-%s-1S" % (name, side), n, 1, "Standard", 9101, flags))
    jobs.append(("%s-%s-4E" % (name, side), 2, 4, "Easy", 9201, flags))
t0 = time.time()
with ThreadPoolExecutor(max_workers=3) as ex:
    list(ex.map(lambda j: loop_mc.sim(*j), jobs))
for side in ("rules", "teach", "net"):
    for part in ("1E", "1S", "4E"):
        loop_mc.log("FINAL %-5s %s %s" % (side, part, loop_mc.summary("%s-%s-%s" % (name, side, part))))
loop_mc.log("FINAL took %ds" % (time.time() - t0))
