# Train the AI's Q(state, choice) network on the local GPU (RTX 4080) from recorded games.
# Data: evidence/ai/<run>/decisions.jsonl written by the game with autoplay flag "rec" (see app/Scripts/AI/AiNet.cs).
# Target (deep Monte-Carlo, as in DouZero): for each recorded turn decision, how much the game's progress improved from
# that moment to the end (act steps, clues, health/sanity kept, defeats, doom, enemies) plus a bonus for a win.
# Output: evidence/ai/model/policy.json (read by the game; plain JSON weights, no extra packages).
import glob, json, os, random, sys, time
import torch, torch.nn as nn

ROOT = os.path.abspath(os.path.join(os.path.dirname(__file__), "..", ".."))
STATE, CHOICE = 26, 22
HEUR = 11  # index of the rule AI's own score inside the choice features


def value(p):
    act, clues, hp, san, down, doom, players, enemies = (p + [0] * 8)[:8]
    return 12 * act + 0.7 * clues / max(1, players) + 0.6 * (hp + san) / max(1, players) - 10 * down - 0.8 * doom - 1.0 * enemies


def load(prefix):
    xs, ys, games, wins = [], [], 0, 0
    for f in glob.glob(os.path.join(ROOT, "evidence", "ai", prefix + "*", "decisions.jsonl")):
        rows = []
        for line in open(f, encoding="utf-8"):
            try:
                rows.append(json.loads(line))
            except Exception:
                pass
        end = next((r for r in reversed(rows) if r.get("end")), None)
        if end is None:
            continue   # a game cut off without its end: the target is unknown
        if not str(end.get("result", "")).startswith(("Won", "Lost")):
            continue   # cut short by an error (e.g. the core's time limit under a busy CPU): the outcome is unknown
        games += 1
        won = str(end.get("result", "")).startswith("Won")
        wins += won
        # after the end there is no act on the table: count the furthest act reached (one more for a win)
        last = list(end["p"]) + [0] * 8
        last[0] = max((r["p"][0] for r in rows if not r.get("end")), default=0) + (1 if won else 0)
        vend = value(last) + (40 if won else 0)
        for r in rows:
            if r.get("end") or not r.get("c"):
                continue
            a = r["a"]
            if a >= len(r["c"]):
                continue
            xs.append(r["s"] + r["c"][a])
            ys.append((vend - value(r["p"])) / 20.0)
    return xs, ys, games, wins


def agreement(model, prefix, dev, limit=4000):
    # how often the network's best choice equals the rule AI's best choice (a sanity measure, not a goal)
    same = total = 0
    for f in glob.glob(os.path.join(ROOT, "evidence", "ai", prefix + "*", "decisions.jsonl")):
        for line in open(f, encoding="utf-8"):
            r = json.loads(line)
            if r.get("end") or len(r.get("c", [])) < 2:
                continue
            x = torch.tensor([r["s"] + c for c in r["c"]], dtype=torch.float32, device=dev)
            q = model(x).squeeze(-1)
            same += int(q.argmax().item() == max(range(len(r["c"])), key=lambda i: r["c"][i][HEUR]))
            total += 1
            if total >= limit:
                return same / total
    return same / max(1, total)


def main():
    prefix = sys.argv[1] if len(sys.argv) > 1 else "lrn-"
    epochs = int(sys.argv[2]) if len(sys.argv) > 2 else 40
    t0 = time.time()
    xs, ys, games, wins = load(prefix)
    if len(xs) < 200:
        print("TRAIN_SKIP samples=%d games=%d" % (len(xs), games)); return
    dev = "cuda" if torch.cuda.is_available() else "cpu"
    X = torch.tensor(xs, dtype=torch.float32, device=dev); Y = torch.tensor(ys, dtype=torch.float32, device=dev).unsqueeze(-1)
    idx = torch.randperm(len(X), device=dev); cut = int(len(X) * 0.9)
    tr, va = idx[:cut], idx[cut:]
    model = nn.Sequential(nn.Linear(STATE + CHOICE, 256), nn.ReLU(), nn.Linear(256, 256), nn.ReLU(), nn.Linear(256, 1)).to(dev)
    opt = torch.optim.Adam(model.parameters(), lr=1e-3, weight_decay=1e-5)
    lossf = nn.MSELoss()
    for ep in range(epochs):
        model.train()
        perm = tr[torch.randperm(len(tr), device=dev)]
        for i in range(0, len(perm), 1024):
            b = perm[i:i + 1024]
            opt.zero_grad(); loss = lossf(model(X[b]), Y[b]); loss.backward(); opt.step()
    model.eval()
    with torch.no_grad():
        vl = lossf(model(X[va]), Y[va]).item()
        base = lossf(torch.full_like(Y[va], Y[tr].mean().item()), Y[va]).item()
        agree = agreement(model, prefix, dev)
    info = "games=%d wins=%d samples=%d val_mse=%.4f baseline_mse=%.4f agree_rules=%.2f device=%s %ds" % (
        games, wins, len(xs), vl, base, agree, torch.cuda.get_device_name(0) if dev == "cuda" else "cpu", time.time() - t0)
    layers = [m for m in model if isinstance(m, nn.Linear)]
    out = {"stateDim": STATE, "choiceDim": CHOICE, "info": info, "time": time.strftime("%Y-%m-%d %H:%M:%S"),
           "layers": [{"w": l.weight.detach().cpu().tolist(), "b": l.bias.detach().cpu().tolist()} for l in layers]}
    d = os.path.join(ROOT, "evidence", "ai", "model"); os.makedirs(d, exist_ok=True)
    stamp = time.strftime("%Y%m%d-%H%M%S")
    json.dump(out, open(os.path.join(d, "policy-" + stamp + ".json"), "w"))       # every model kept (nothing is deleted)
    tmp = os.path.join(d, "policy.json.tmp"); json.dump(out, open(tmp, "w")); os.replace(tmp, os.path.join(d, "policy.json"))
    print("TRAIN_DONE " + info, flush=True)


if __name__ == "__main__":
    main()
