// Workshop random weakness draw kept to the rules core's pool (no class-restricted basic weaknesses, e.g. 09124–09128).
import fs from "node:fs"; import path from "node:path"; import vm from "node:vm"; import { fileURLToPath } from "node:url";
const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "../..");
const read = (p) => fs.readFileSync(path.join(root, p), "utf8");
const ctx = vm.createContext({ console, setTimeout, clearTimeout, queueMicrotask, structuredClone, Intl, crypto: globalThis.crypto, TextEncoder, TextDecoder, URL });
vm.runInContext(read("app/assets/build-engine/engine.js"), ctx);
const E = ctx.BuildEngine;
await E.init(read("app/assets/build-data/version-zh-cn.json"), read("app/assets/build-data/metadata-zh-cn.json"), read("app/assets/build-data/cards-zh-cn.json"), "zh-cn");
const D = E.decks; D.load(read("userdata/decks.json"));
// the core's pool, as StandaloneDecks.WeaknessPool builds it
// core-cards.json plus every <stem>-cards.json beside it (CoreCatalog), first one of a code wins
const byCode = new Map();
for (const f of ["core-cards.json", ...fs.readdirSync(path.join(root, "app/assets/data")).filter((f) => f.endsWith("-cards.json") && f !== "core-cards.json").sort()])
  for (const c of JSON.parse(read("app/assets/data/" + f))) if (!byCode.has(c.code)) byCode.set(c.code, c);
const cards = [...byCode.values()];
const pool = cards.filter((c) => c.subtype_code === "basicweakness" && c.code !== "01000" && !c.duplicate_of_code
  && !String(c.real_text ?? "").startsWith("Multiplayer only.") && !String(c.real_text ?? "").startsWith("Campaign Mode only.") && c.restrictions == null).map((c) => c.code);
const restricted = cards.filter((c) => c.subtype_code === "basicweakness" && c.restrictions != null).map((c) => c.code);
console.log("core pool", pool.length, "restricted", restricted.join(","));
const ex = JSON.parse(read("userdata/decks.json"))["example-01001"];   // Roland, a Guardian: build would still offer 09124
for (const mode of ["build-only", "core-pool"]) {
  const seen = {}; let bad = 0, placeholderLeft = 0, extra = 0;
  for (let i = 0; i < 400; i++) {
    const w = JSON.parse(await D.upgradeBegin(JSON.stringify({ investigator_code: "01001", name: "w", slots: { ...ex.slots, "01000": 1 } }), 0));
    const before = JSON.parse(D.view(w.next)).slots.reduce((n, c) => n + c.quantity, 0);
    const got = JSON.parse(mode === "core-pool" ? D.randomWeakness(w.next, JSON.stringify(pool)) : D.randomWeakness(w.next));
    const v = JSON.parse(D.view(w.next));
    if (v.slots.some((c) => c.code === "01000")) placeholderLeft++;
    if (v.slots.reduce((n, c) => n + c.quantity, 0) !== before) extra++;
    seen[got.code] = (seen[got.code] ?? 0) + 1; if (!pool.includes(got.code)) bad++;
    await D.upgradeEnd(w.next, false);
  }
  console.log(mode, "draws 400, distinct", Object.keys(seen).length, "outside core pool", bad, "restricted drawn", restricted.filter((c) => seen[c]).map((c) => c + "×" + seen[c]).join(",") || "none", "placeholder left", placeholderLeft, "size changed", extra);
}
