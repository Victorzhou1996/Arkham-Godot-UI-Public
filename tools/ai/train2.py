# Train the two networks on the local RTX 4080 from simulator games (evidence/ai/<prefix>*/decisions.jsonl):
#   value.json   V(team state) → how well the game ends from here (final progress + a win bonus), used by the search teacher
#   policy.json  π(state, choice) → which choice the search teacher picked (softmax over the offered choices), used to play fast
# Rows come from tools/sim with flags rec(+teach): t = team features, s/c = deciding seat and choice features, a = played, k = teacher.
import glob, json, os, sys, time
import torch, torch.nn as nn
sys.path.insert(0, os.path.dirname(__file__))
import campaign_reward

ROOT = os.path.abspath(os.path.join(os.path.dirname(__file__), "..", ".."))
STATE, CHOICE, TEAM, HEUR = 26, 22, 50, 11


def value(p):
    """the chapter's progress by the rules core's own state (docs/AI-PLAY-PLAN-20261119.md §10)"""
    old = len(p) < 13
    p = list(p) + [0] * 13
    act, clues, hp, san, down, doom, players, enemies = p[:8]
    n = max(1, players)
    if old or p[10] == 0:   # an old record (8 fields): the old formula
        return 12 * act + 0.7 * clues / n + 0.6 * (hp + san) / n - 10 * down - 0.8 * doom - 1.0 * enemies
    threshold, agenda, maxhp, maxsan, victory = p[8:13]
    margin = threshold - doom if threshold > 0 else 9
    return (12 * act - 6 * max(0, agenda - 1)            # the act moves the story on, each agenda that advanced is a step lost
            + 0.7 * clues / n                             # clues in hand for the act
            + 4 * (hp / max(1, maxhp) + san / max(1, maxsan))   # how far from defeat (damage / horror as a share of what defeats)
            - 10 * down - 1.5 * max(0, 3 - margin) - 1.0 * enemies + 2 * victory
            - 4 * max(0, 2.5 - san / n) - 3 * max(0, 2.5 - hp / n))   # one treachery from insanity / defeat


def load(prefix):
    V, P, games, wins = [], [], 0, 0
    for f in glob.glob(os.path.join(ROOT, "evidence", "ai", prefix + "*", "decisions.jsonl")):
        rows = []
        for line in open(f, encoding="utf-8"):
            try:
                rows.append(json.loads(line))
            except Exception:
                pass
        if any(r.get("campaignEnd") for r in rows):
            # a whole campaign: each chapter's decisions learn what that chapter added to the campaign (log records, trauma,
            # investigators lost), plus a little of the chapter's own progress so V can tell good play inside a chapter
            games += 1; prev, seg = campaign_reward.EMPTY, []
            for r in rows:
                if r.get("chapterEnd") or r.get("campaignEnd"):
                    cur = r.get("rec") or prev
                    if seg:
                        last = list(seg[-1]["p"]) + [0] * 8
                        last[0] = max(x["p"][0] for x in seg)
                        target = campaign_reward.chapter(prev, cur) / 4.0 + 0.25 * value(last) / 20.0
                        for x in seg:
                            V.append((x["t"], target))
                            if x.get("k", -1) >= 0 and len(x["c"]) >= 2:
                                P.append(([x["s"] + c for c in x["c"]], x["k"]))
                    prev, seg = cur, []
                    if r.get("campaignEnd"):
                        wins += campaign_reward.campaign(cur) > 0
                elif "t" in r and not r.get("end"):
                    seg.append(r)
            continue
        end = next((r for r in reversed(rows) if r.get("end")), None)
        if end is None or not str(end.get("result", "")).startswith(("Won", "Lost")):
            continue
        games += 1
        won = str(end["result"]).startswith("Won"); wins += won
        last = list(end["p"]) + [0] * 8
        last[0] = max((r["p"][0] for r in rows if not r.get("end")), default=0) + (1 if won else 0)
        target = (value(last) + (40 if won else 0)) / 20.0
        for r in rows:
            if r.get("end") or "t" not in r:
                continue
            V.append((r["t"], target))
            if r.get("k", -1) >= 0 and len(r["c"]) >= 2:
                P.append(([r["s"] + c for c in r["c"]], r["k"]))
    return V, P, games, wins


def mlp(inp, hidden):
    layers, last = [], inp
    for h in hidden:
        layers += [nn.Linear(last, h), nn.ReLU()]; last = h
    return nn.Sequential(*layers, nn.Linear(last, 1))


def export(model, name, inputDim, info):
    lin = [m for m in model if isinstance(m, nn.Linear)]
    out = {"inputDim": inputDim, "info": info, "time": time.strftime("%Y-%m-%d %H:%M:%S"),
           "layers": [{"w": l.weight.detach().cpu().tolist(), "b": l.bias.detach().cpu().tolist()} for l in lin]}
    d = os.path.join(ROOT, "evidence", "ai", "model"); os.makedirs(d, exist_ok=True)
    stamp = time.strftime("%Y%m%d-%H%M%S")
    json.dump(out, open(os.path.join(d, name.replace(".json", "-" + stamp + ".json")), "w"))   # every model kept
    tmp = os.path.join(d, name + ".tmp"); json.dump(out, open(tmp, "w")); os.replace(tmp, os.path.join(d, name))


def train_value(V, dev, epochs):
    X = torch.tensor([v[0] for v in V], dtype=torch.float32, device=dev); Y = torch.tensor([[v[1]] for v in V], dtype=torch.float32, device=dev)
    idx = torch.randperm(len(X), device=dev); cut = int(len(X) * 0.9); tr, va = idx[:cut], idx[cut:]
    model = mlp(TEAM, [512, 512, 256]).to(dev)
    opt = torch.optim.AdamW(model.parameters(), lr=1e-3, weight_decay=1e-4); lossf = nn.MSELoss()
    sched = torch.optim.lr_scheduler.CosineAnnealingLR(opt, epochs)
    for ep in range(epochs):
        model.train(); perm = tr[torch.randperm(len(tr), device=dev)]
        for i in range(0, len(perm), 2048):
            b = perm[i:i + 2048]; opt.zero_grad(); loss = lossf(model(X[b]), Y[b]); loss.backward(); opt.step()
        sched.step()
    model.eval()
    with torch.no_grad():
        vl = lossf(model(X[va]), Y[va]).item(); base = lossf(torch.full_like(Y[va], Y[tr].mean().item()), Y[va]).item()
    return model, "value samples=%d val_mse=%.3f baseline=%.3f" % (len(X), vl, base)


def train_policy(P, dev, epochs):
    n = max(len(p[0]) for p in P)
    X = torch.zeros(len(P), n, STATE + CHOICE, device=dev); M = torch.zeros(len(P), n, dtype=torch.bool, device=dev)
    T = torch.tensor([p[1] for p in P], device=dev)
    for i, (cs, _) in enumerate(P):
        X[i, :len(cs)] = torch.tensor(cs, device=dev); M[i, :len(cs)] = True
    idx = torch.randperm(len(P), device=dev); cut = int(len(P) * 0.9); tr, va = idx[:cut], idx[cut:]
    model = mlp(STATE + CHOICE, [256, 256]).to(dev)
    opt = torch.optim.AdamW(model.parameters(), lr=1e-3, weight_decay=1e-4); lossf = nn.CrossEntropyLoss()
    def logits(b):
        z = model(X[b]).squeeze(-1); return z.masked_fill(~M[b], -1e9)
    for ep in range(epochs):
        model.train(); perm = tr[torch.randperm(len(tr), device=dev)]
        for i in range(0, len(perm), 512):
            b = perm[i:i + 512]; opt.zero_grad(); loss = lossf(logits(b), T[b]); loss.backward(); opt.step()
    model.eval()
    with torch.no_grad():
        acc = (logits(va).argmax(-1) == T[va]).float().mean().item()
        heur = X[va][:, :, STATE + HEUR].masked_fill(~M[va], -1e9).argmax(-1)
        rules = (heur == T[va]).float().mean().item()
    return model, "policy samples=%d val_acc=%.2f (rules alone %.2f)" % (len(P), acc, rules)


def main():
    prefix = sys.argv[1] if len(sys.argv) > 1 else "tlrn-"
    epochs = int(sys.argv[2]) if len(sys.argv) > 2 else 150
    t0 = time.time()
    V, P, games, wins = load(prefix)
    dev = "cuda" if torch.cuda.is_available() else "cpu"
    gpu = torch.cuda.get_device_name(0) if dev == "cuda" else "cpu"
    if len(V) < 500:
        print("TRAIN_SKIP value samples=%d games=%d" % (len(V), games)); return
    vm, vinfo = train_value(V, dev, epochs)
    export(vm, "value.json", TEAM, "games=%d wins=%d %s" % (games, wins, vinfo))
    pinfo = "policy skipped (no teacher rows yet)"
    if len(P) >= 300:
        pm, pinfo = train_policy(P, dev, max(40, epochs // 2))
        export(pm, "policy.json", STATE + CHOICE, "games=%d %s" % (games, pinfo))
    print("TRAIN_DONE games=%d wins=%d %s | %s | %s %ds" % (games, wins, vinfo, pinfo, gpu, time.time() - t0), flush=True)


if __name__ == "__main__":
    main()
