// Sanity test of app/assets/build-engine/engine.js in Node (the same calls the C# host makes).
import fs from "node:fs";
import path from "node:path";
import vm from "node:vm";
import { fileURLToPath } from "node:url";

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "../..");
const read = (p) => fs.readFileSync(path.join(root, p), "utf8");
const ctx = vm.createContext({ console, setTimeout, clearTimeout, queueMicrotask, structuredClone, Intl, crypto: globalThis.crypto, TextEncoder, TextDecoder, URL });
vm.runInContext(read("app/assets/build-engine/engine.js"), ctx);
const E = ctx.BuildEngine;
let t = Date.now();
const n = await E.init(read("app/assets/build-data/version-zh-cn.json"), read("app/assets/build-data/metadata-zh-cn.json"), read("app/assets/build-data/cards-zh-cn.json"), "zh-cn");
console.log("init", n, "cards in", Date.now() - t, "ms");
const invs = JSON.parse(E.investigators());
console.log("investigators", invs.length, invs.slice(0, 3).map((i) => i.name + " " + i.code));
const deck = { id: "t1", name: "测试", investigator_code: "01001", investigator_name: "罗兰", slots: { "01006": 1, "01007": 1, "01016": 2, "01017": 2, "01018": 2, "01019": 2, "01020": 2, "01021": 2, "01022": 2, "01023": 2, "01024": 2, "01025": 2, "01037": 2, "01039": 2, "01086": 2, "01088": 2, "01089": 2, "01091": 2 }, sideSlots: {}, ignoreDeckLimitSlots: null, meta: "{}", taboo_id: null, xp: null, xp_adjustment: null, problem: null, tags: "", description_md: "", date_creation: "2026-11-18", date_update: "2026-11-18", source: "local", version: "0.1", exile_string: null, previous_deck: null, next_deck: null };
t = Date.now();
console.log("validate", E.validate(JSON.stringify(deck)).slice(0, 400), Date.now() - t, "ms");
t = Date.now();
const acc = JSON.parse(E.access(JSON.stringify(deck), "slots"));
console.log("access", acc.length, "cards", Date.now() - t, "ms", acc.slice(0, 4).map((c) => c.name + c.code + "/" + c.xp));
// deck API
const D = E.decks;
console.log("load", D.load(read("userdata/decks.json")));
const list = JSON.parse(D.list()); console.log("list", list.length, list.slice(0, 2).map((d) => d.name + " " + d.size + "/" + d.total + " xp" + d.xp + " " + d.valid));
const info = JSON.parse(D.createInit("02002")); console.log("create", info.investigator.name, "options", info.options.length, "sets", info.sets.map((s) => s.id).join(","));
D.createTitle("测试新牌组"); const nd = JSON.parse(await D.create()); console.log("created", nd.id, nd.name, Object.keys(nd.slots).length, "codes");
D.quantity(nd.id, "01030", 1, 2, "slots", "increment"); const v = JSON.parse(D.view(nd.id)); console.log("view", v.slots.length, "valid", v.validation.valid, v.validation.errors.map((e) => e.type).join(","), "edited", v.edited);
const saved = JSON.parse(await D.save(nd.id)); console.log("saved", saved.slots["01030"]);
const acc2 = JSON.parse(D.access(nd.id, "slots")); console.log("access", acc2.length);
console.log("copy", JSON.parse(D.copy(nd.id)).id !== nd.id); await D.remove(nd.id); console.log("after remove", JSON.parse(D.list()).length);
// campaign upgrade: Roland's example deck with 5 xp; swap .45 Automatic for two level-2 cards
{
  const ex = JSON.parse(read("userdata/decks.json"))["example-01001"];
  const up = JSON.parse(await D.upgradeBegin(JSON.stringify({ investigator_code: ex.investigator_code, name: ex.name, slots: ex.slots, meta: "{}" }), 5));
  let v = JSON.parse(D.view(up.next)); console.log("upgrade begin", up.prev !== up.next, "xp", v.upgrade?.xp, "spent", v.upgrade?.xpSpent, "valid", v.validation.valid);
  D.quantity(up.next, "01016", -2, 2, "slots", "increment"); D.quantity(up.next, "01029", 2, 2, "slots", "increment");
  v = JSON.parse(D.view(up.next)); console.log("upgrade edit", "spent", v.upgrade?.xpSpent, "available", v.upgrade?.xpAvailable, "valid", v.validation.valid, JSON.stringify(v.validation.errors).slice(0, 200));
  const done = JSON.parse(await D.upgradeEnd(up.next, true)); console.log("upgrade end", done.slots["01029"], done.slots["01016"] ?? 0, "store has", JSON.parse(D.list()).filter((d) => d.id === up.next || d.id === up.prev).length);
  // the random weakness placeholder
  const w = JSON.parse(await D.upgradeBegin(JSON.stringify({ investigator_code: "01001", name: "w", slots: { ...ex.slots, "01000": 1 } }), 0));
  console.log("weakness", JSON.parse(D.randomWeakness(w.next)).name, JSON.parse(D.view(w.next)).slots.some((c) => c.code === "01000"));
  await D.upgradeEnd(w.next, false);
}
// card upgrades on an ordinary deck: Roland's example has Magnifying Glass (0)? use .45 Automatic 01016 → 01016's higher versions
{
  const ex = JSON.parse(read("userdata/decks.json"))["example-01001"];
  D.load(JSON.stringify({ "t-up": { ...ex, id: "t-up" } }));
  const ups = JSON.parse(D.upgrades("t-up", "slots"));
  console.log("upgrades for", Object.keys(ups).length, "cards;", Object.entries(ups).slice(0, 3).map(([k, v]) => k + "→" + v.map((c) => c.code + "(" + c.xp + ")").join("/")).join(" "));
  const [code, list] = Object.entries(ups)[0];
  D.upgradeCard("t-up", code, list[0].code, 2, "slots");
  const v = JSON.parse(D.view("t-up"));
  console.log("after upgrade", code, v.slots.find((c) => c.code === code)?.quantity ?? 0, list[0].code, v.slots.find((c) => c.code === list[0].code)?.quantity, "xp", v.stats.xpRequired);
}
// round 10: sides, choices, customizable sheet, annotation, swap, card pool
{
  const ex = JSON.parse(read("userdata/decks.json"))["example-01001"];
  D.load(JSON.stringify({ r10: { ...ex, id: "r10", slots: { ...ex.slots, "09021": 1 } } }));
  let v = JSON.parse(D.view("r10"));
  console.log("sides", JSON.stringify(v.sides), "choices", v.choices.length);
  console.log("customizable", JSON.parse(D.customizable("r10", "09021")).options.length);
  D.customization("r10", "09021", 2, JSON.stringify({ xp_spent: 2 }));
  D.annotation("r10", "01020", "主力武器");
  D.swap("r10", "01020", "sideSlots");
  D.side("r10", "front", "90024");
  v = JSON.parse(D.view("r10"));
  console.log("after: xp", v.stats.xpRequired, "annot", JSON.stringify(v.annotations), "side", v.sideSlots.map((c) => c.code + "x" + c.quantity).join(","), "front", v.investigator_front);
  console.log("access before pool", JSON.parse(D.access("r10", "slots")).length);
  D.meta("r10", "card_pool", "core", null);
  console.log("access core pool", JSON.parse(D.access("r10", "slots")).length, "pool", JSON.stringify(JSON.parse(D.view("r10")).cardPool));
  console.log("saved meta", JSON.parse(await D.save("r10")).meta);
}
// round 11: import decks from JSON (ArkhamDB export, build export list with a fan-made deck)
{
  D.load("{}");
  for (const f of ["evidence/build/r11/import/arkhamdb-wendy.json", "evidence/build/r11/import/two-decks.json"]) {
    const r = JSON.parse(D.importJson(read(f)));
    console.log("import", f.split("/").pop(), "added", r.added.map((d) => d.id.slice(0, 8) + " " + d.name + " taboo" + d.taboo_id + " meta" + d.meta).join(" | "), "refused", JSON.stringify(r.refused));
  }
  const l = JSON.parse(D.list()); console.log("list after import", l.map((d) => d.name + " " + d.investigator_front + " " + d.size + " valid" + d.valid + " " + (d.errors ?? []).map((e) => e.type).join(",")).join(" | "));
  try { D.importJson("not json"); } catch (e) { console.log("bad text ->", String(e).slice(0, 80)); }
}
