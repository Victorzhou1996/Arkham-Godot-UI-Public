// 多才多艺 (Versatile 06167): with it in the deck, any other class's level-0 card (强梦血清 06159 for Skids, who has no
// seeker access of his own) is in the card pool, and the deck allows only one such card (Yunke 11-23 player report).
// Usage: node tools/build-engine/test-versatile.mjs [engine.js]
import fs from "node:fs";
import path from "node:path";
import vm from "node:vm";
import { fileURLToPath } from "node:url";

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "../..");
const read = (p) => fs.readFileSync(path.join(root, p), "utf8");
const ctx = vm.createContext({ console, setTimeout, clearTimeout, queueMicrotask, structuredClone, Intl, crypto: globalThis.crypto, TextEncoder, TextDecoder, URL });
vm.runInContext(read(process.argv[2] ?? "app/assets/build-engine/engine.js"), ctx);
const E = ctx.BuildEngine;
await E.init(read("app/assets/build-data/version-zh-cn.json"), read("app/assets/build-data/metadata-zh-cn.json"), read("app/assets/build-data/cards-zh-cn.json"), "zh-cn");
const base = { ...JSON.parse(read("tools/build-engine/test-deck.json")), investigator_code: "01003", investigator_name: "斯奇兹" };
const has = (deck, code) => JSON.parse(E.access(JSON.stringify(deck), "slots")).some((c) => c.code === code);
const without = { ...base };
const withV = { ...base, slots: { ...base.slots, "06167": 1 } };
console.log("engine.access  06159 强梦血清: without Versatile", has(without, "06159"), "| with", has(withV, "06159"));
console.log("engine.access  01042 百科全书 (seeker level 2) with Versatile:", has(withV, "01042"));
// the workshop's own path (decks.access on a stored deck)
const D = E.decks;
D.load(JSON.stringify({ v0: { ...without, id: "v0" }, v1: { ...withV, id: "v1" } }));
const pool = (id) => JSON.parse(D.access(id, "slots")).map((c) => c.code);
console.log("decks.access   06159: without", pool("v0").includes("06159"), "| with", pool("v1").includes("06159"));
// one off-class card is fine, two are not: Skids' example deck (qa-data/p0) with Versatile
const ex = JSON.parse(read("qa-data/p0/decks.json"))["example-01003"];
const exV = { ...base, ...ex, investigator_code: "01003", slots: { ...ex.slots, "06167": 1 } };
const one = { ...exV, slots: { ...exV.slots, "06159": 1 } };
const two = { ...one, slots: { ...one.slots, "01039": 1 } };   // 01039 推理: another seeker level 0 card
const val = (d) => { const v = JSON.parse(E.validate(JSON.stringify(d))); return (v.errors ?? []).map((e) => e.type + (e.type === "TOO_FEW_CARDS" || e.type === "TOO_MANY_CARDS" ? "" : JSON.stringify(e.details).slice(0, 160))).join(", ") || "valid"; };
console.log("validate Versatile only:", val(exV));
console.log("validate one off-class:", val(one));
console.log("validate two off-class:", val(two));
