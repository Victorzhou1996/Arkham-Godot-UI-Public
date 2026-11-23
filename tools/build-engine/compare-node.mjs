// Validates every deck in a decks.json with the engine under Node (V8); prints one line per deck.
import fs from "node:fs"; import path from "node:path"; import vm from "node:vm"; import { fileURLToPath } from "node:url";
const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "../..");
const read = (p) => fs.readFileSync(path.join(root, p), "utf8");
const ctx = vm.createContext({ console, setTimeout, clearTimeout, queueMicrotask, structuredClone, crypto: globalThis.crypto, URL });
vm.runInContext(read("app/assets/build-engine/engine.js"), ctx);
const E = ctx.BuildEngine;
await E.init(read("app/assets/build-data/version-zh-cn.json"), read("app/assets/build-data/metadata-zh-cn.json"), read("app/assets/build-data/cards-zh-cn.json"), "zh-cn");
const decks = JSON.parse(read(process.argv[2] || "userdata/decks.json"));
for (const [k, d] of Object.entries(decks)) { try { const r = JSON.parse(E.validate(JSON.stringify(d))); console.log("DECK " + k + " " + (r.valid ? "valid" : "invalid " + r.errors.map((e) => e.type).join(","))); } catch (e) { console.log("DECK " + k + " error"); } }
