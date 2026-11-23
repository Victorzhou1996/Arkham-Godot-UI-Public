// Deck API on arkham.build's own store (data.decks + deckEdits), driven by build's own actions — so creating, editing,
// saving, deleting and copying decks follow build exactly. The C# workshop keeps decks.json in step with data.decks.
import type { Card, Deck } from "@arkham-build/shared";
import { useStore } from "@/store";

import { makeDeckCopy } from "@/store/lib/deck-factory";
import { formatDeckImport, parseDeckJson } from "@/store/lib/deck-io";
import { type ChangeStats, getChangeStats } from "@/store/lib/deck-upgrades";
import type { ResolvedDeck } from "@/store/lib/types";
import { getAdditionalDeckOptions } from "@/store/lib/deck-options";
import { filterAlternates, filterCardPool, filterDuplicates, filterInvestigatorAccess, filterInvestigatorWeaknessAccess, filterPlayerCards } from "@/store/lib/filtering";
import { selectDeckCreateCardSets, selectDeckCreateChecked, selectDeckCreateInvestigators } from "@/store/selectors/deck-create";
import { selectDeckValid, selectResolvedDeckById } from "@/store/selectors/decks";
import { selectAvailableUpgrades } from "@/store/selectors/lists";
import { selectLookupTables, selectMetadata, selectStaticBuildQlInterpreter } from "@/store/selectors/shared";
import { createHttpClient } from "@/store/services/http-client";
import { cardSummary, type Json, normalize, state } from "./entry";

// The local provider never touches the network; the client is only there because the actions take one.
const client = createHttpClient({ apiUrl: "http://localhost", onUnauthorized: () => undefined });

function deckOut(id: string) {
  const d = state().data.decks[id];
  return d ? JSON.stringify(d) : "null";
}

function cardBrief(rc: { card: Card } | undefined, quantity?: number) {
  if (!rc) return null;
  return { ...cardSummary(rc.card), quantity };
}

type SlotMap = Record<string, number> | null | undefined;

// ── printings of one card (Yunke 11-22: 智慧古书 (3) upgraded twice showed as two rows) ──
// A reprint names its original in duplicate_of_code (01686 新核心 → 06279); build's validation already counts them as one
// card. A deck that holds two printings of a card keeps them under the original; while editing, a copy added or
// upgraded joins the printing the deck already has, so the deck never splits again.
function baseCode(code: string): string {
  const cards = selectMetadata(state()).cards;
  const d = cards[code]?.duplicate_of_code;
  return d && cards[d] ? d : code;
}
function mergePrintings(map: unknown): unknown {
  if (!map || typeof map !== "object" || Array.isArray(map)) return map;
  const m = map as Record<string, number>;
  const groups: Record<string, number> = {};
  for (const [code, q] of Object.entries(m)) if ((q ?? 0) > 0) groups[baseCode(code)] = (groups[baseCode(code)] ?? 0) + 1;
  if (!Object.values(groups).some((n) => n > 1)) return map;
  const out: Record<string, number> = {};
  for (const [code, q] of Object.entries(m)) {
    if (!((q ?? 0) > 0)) continue;
    const to = groups[baseCode(code)] > 1 ? baseCode(code) : code;
    out[to] = (out[to] ?? 0) + (q ?? 0);
  }
  return out;
}
function mergeDeckPrintings(raw: Record<string, unknown>) {
  return { ...raw, slots: mergePrintings(raw.slots), sideSlots: mergePrintings(raw.sideSlots), ignoreDeckLimitSlots: mergePrintings(raw.ignoreDeckLimitSlots) };
}
// the printing of this card already in a slot map, if the map holds another printing of it; else the code itself
function printingIn(map: SlotMap, code: string): string {
  if (!map || map[code]) return code;
  const b = baseCode(code);
  return Object.keys(map).find((k) => (map[k] ?? 0) > 0 && baseCode(k) === b) ?? code;
}

// An upgraded deck's cost against the version before it (build's getChangeStats: xp, xpSpent, xpAvailable, changes).
function upgradeStats(id: string) {
  const s = state();
  const next = selectResolvedDeckById(s, id, true);
  if (!next?.previous_deck) return null;
  const prev = selectResolvedDeckById(s, next.previous_deck, false);
  if (!prev) return null;
  const stats = getChangeStats(prev, next);
  const notes = upgradeNotes[id];
  const d = notes ? applyNotes(prev, next, stats, notes) : { total: 0, used: [] };
  return {
    xp: stats.xp, xpAdjustment: stats.xpAdjustment,
    xpSpent: (stats.xpSpent ?? 0) - d.total, xpAvailable: (stats.xpAvailable ?? 0) + d.total,
    buildXpSpent: stats.xpSpent, discount: d.total, discounts: d.used, changes: stats.changes,
  };
}

// ── what happened in play that lowers this upgrade's price (Yunke 11-22, docs/INGAME-DECKBUILDING-CARDS-20261122.md) ──
// The table hands these over (the request meta's "arkhamui_upgrade_notes"); build does not know them, so they go on top
// of its price: each discount once, on the purchase where it saves the most. Buying a new card still costs at least 1
// (FAQ 1.8); an upgrade or a customization box has no such floor.
type UpgradeNotes = {
  freeBoxes?: Record<string, number>;                          // 鸦翎羽毛笔: n boxes on its own sheet cost nothing
  assetUpgradeDiscounts?: { code: string; amount: number }[]; // 鸦翎羽毛笔: the attached asset's upgrade −amount (by title)
  newCardDiscounts?: number[];                                // 毛皮货: one new card −x each
  cardDiscounts?: number[];                                   // 绿人奖章: one purchase (new card or upgrade) −x each
};
const upgradeNotes: Record<string, UpgradeNotes> = {};

type Purchase = { code: string; name: string; kind: "new" | "upgrade" | "box"; left: number };
type Used = { source: string; code: string; amount: number };

// the purchases in this upgrade, each copy with what it adds to build's price (build's own price with and without it)
function purchases(prev: ResolvedDeck, next: ResolvedDeck, changes: ChangeStats["changes"]): Purchase[] {
  const spent = (deck: ResolvedDeck) => getChangeStats(prev, deck).xpSpent ?? 0;
  const full = spent(next);
  const out: Purchase[] = [];
  for (const key of ["slots", "extraSlots"] as const) {
    const diffs = changes[key] ?? {};
    for (const [code, diff] of Object.entries(diffs)) {
      if (diff <= 0) continue;
      const card = next.cards[key]?.[code]?.card;
      if (!card) continue;
      // lower versions of the same card taken out: that many copies are upgrades (build prices those first, so the
      // copies taken off last are the upgrades)
      let sources = 0;
      for (const [c2, d2] of Object.entries(diffs)) {
        const pc = d2 < 0 ? prev.cards[key]?.[c2]?.card : undefined;
        if (pc && pc.real_name === card.real_name && (pc.xp ?? 0) < (card.xp ?? 0)) sources += -d2;
      }
      const q = (next[key] as Record<string, number> | undefined)?.[code] ?? 0;
      let before = full;
      for (let k = 1; k <= diff; k++) {
        const fewer = { ...next, [key]: { ...(next[key] as Record<string, number>), [code]: q - k } } as ResolvedDeck;
        const s = spent(fewer);
        out.push({ code, name: card.real_name, kind: k > diff - sources ? "upgrade" : "new", left: Math.max(0, before - s) });
        before = s;
      }
    }
  }
  // boxes checked on a customizable card already in the deck (an upgrade of that card)
  for (const code of Object.keys(changes.customizations ?? {})) {
    if (!prev.slots[code] || next.exileSlots[code]) continue;
    const card = prev.cards.slots[code]?.card;
    if (!card) continue;
    const without = { ...next, customizations: { ...(next.customizations ?? {}), [code]: prev.customizations?.[code] ?? {} } } as ResolvedDeck;
    out.push({ code, name: card.real_name, kind: "box", left: Math.max(0, full - spent(without)) });
  }
  return out;
}

function applyNotes(prev: ResolvedDeck, next: ResolvedDeck, stats: ChangeStats, notes: UpgradeNotes) {
  const used: Used[] = [];
  const any = (notes.freeBoxes && Object.keys(notes.freeBoxes).length) || notes.assetUpgradeDiscounts?.length || notes.newCardDiscounts?.length || notes.cardDiscounts?.length;
  if (!any) return { total: 0, used };
  const list = purchases(prev, next, stats.changes);
  const floor = (p: Purchase) => (p.kind === "new" ? 1 : 0);
  // one discount of up to `amount` on the eligible purchase where it saves the most
  function spend(source: string, amount: number, eligible: (p: Purchase) => boolean) {
    let best: Purchase | undefined; let save = 0;
    for (const p of list) {
      if (!eligible(p)) continue;
      const s = Math.min(amount, p.left - floor(p));
      if (s > save || (s === save && s > 0 && best && p.left > best.left)) { best = p; save = s; }
    }
    if (!best || save <= 0) return;
    best.left -= save;
    used.push({ source, code: best.code, amount: save });
  }
  const meta = selectMetadata(state());
  for (const [code, n] of Object.entries(notes.freeBoxes ?? {})) spend("freeBox:" + code, n, (p) => p.kind === "box" && p.code === code);
  for (const a of notes.assetUpgradeDiscounts ?? []) {
    const title = meta.cards[a.code]?.real_name ?? "";
    spend("asset:" + a.code, a.amount, (p) => p.kind !== "new" && p.name === title && next.cards.slots[p.code]?.card?.type_code === "asset");
  }
  for (const x of notes.newCardDiscounts ?? []) spend("newCard", x, (p) => p.kind === "new");
  for (const x of notes.cardDiscounts ?? []) spend("card", x, () => true);
  return { total: used.reduce((a, u) => a + u.amount, 0), used };
}

// The higher-level versions of each card in the deck that this deck may take (build's selectAvailableUpgrades: same
// card, more xp, allowed by the deck's options and card pool). build offers them only on an upgraded deck (one with a
// previous version); the workshop offers them on any deck, so a deck without one is passed as if it had.
function availableUpgrades(id: string, target: string) {
  const s = state();
  const r = selectResolvedDeckById(s, id, true);
  if (!r) return null;
  const deck = r.previous_deck ? r : ({ ...r, previous_deck: "workshop" } as typeof r);
  const a = selectAvailableUpgrades(s, deck, (target === "extraSlots" ? "extraSlots" : "slots") as never);
  // build keeps one card per level / class / subname, whichever printing it meets first (e.g. the starter-deck reprint
  // 60270 instead of the core set's 01040); offer the earliest printing of that same card instead
  const m = selectMetadata(s);
  const lt = selectLookupTables(s);
  const age = (c: Card) => { const p = m.packs[c.pack_code]; const cy = p ? m.cycles[p.cycle_code] : undefined; return (cy?.position ?? 99) * 1000 + (p?.position ?? 999); };
  // (a reprint build met first, e.g. 01686 新核心 for 06279, is not kept either: the earliest original is offered, and
  // a printing the deck already holds wins over both, so a second copy upgraded joins the first)
  const held = (r as unknown as Record<string, SlotMap>)[target === "extraSlots" ? "extraSlots" : "slots"];
  const upgrades: Record<string, Card[]> = {};
  for (const [code, list] of Object.entries(a.upgrades)) {
    const versions = Object.keys(lt.relations.level[code] ?? {}).map((k) => m.cards[k]).filter(Boolean);
    upgrades[code] = list.map((u) => {
      const same = versions.filter((v) => v.xp === u.xp && v.real_name === u.real_name && v.faction_code === u.faction_code && v.faction2_code === u.faction2_code && v.subname === u.subname && !v.duplicate_of_code);
      const pick = same.length ? same.reduce((best, v) => (age(v) < age(best) ? v : best), same[0]) : u;
      return m.cards[printingIn(held, pick.code)] ?? pick;
    });
  }
  return { ...a, upgrades };
}

const Decks = {
  // Replace the store's decks with ours ({ key: deck }); keys become build ids.
  load(mapText: Json): number {
    const map = JSON.parse(mapText) as Record<string, Record<string, unknown>>;
    const decks: Record<string, Deck> = {};
    const history: Record<string, string[]> = {};
    for (const [key, raw] of Object.entries(map)) {
      if (!raw || typeof raw !== "object" || !raw.investigator_code) continue;
      decks[key] = { ...normalize(mergeDeckPrintings({ ...raw, id: key }) as never), id: key } as Deck;
      history[key] = [];
    }
    useStore.setState((prev) => ({ data: { ...prev.data, decks, history }, deckEdits: {} }) as never);
    return Object.keys(decks).length;
  },

  // Everything the library page shows per deck, with build's validation.
  list(): Json {
    const s = state();
    const out: unknown[] = [];
    for (const id of Object.keys(s.data.decks)) {
      try {
        const r = selectResolvedDeckById(s, id, false);
        if (!r) continue;
        const v = selectDeckValid(s, r);
        out.push({
          id,
          name: r.name,
          investigator: r.investigatorBack.card.code,
          investigator_front: r.investigatorFront.card.code,
          investigator_name: r.investigatorFront.card.name,
          faction: r.investigatorBack.card.faction_code,
          xp: r.stats?.xpRequired ?? 0,
          size: r.stats?.deckSize ?? 0,
          total: r.stats?.deckSizeTotal ?? 0,
          valid: v.valid,
          errors: v.errors,
          date_update: r.date_update,
          tags: r.tags,
          taboo_id: r.taboo_id ?? null,
          previous: r.previous_deck,
          next: r.next_deck,
        });
      } catch (e) {
        out.push({ id, name: s.data.decks[id]?.name, investigator: s.data.decks[id]?.investigator_code, broken: String(e) });
      }
    }
    return JSON.stringify(out);
  },

  get(id: string): Json {
    return deckOut(id);
  },

  // The deck as the editor shows it (pending edits applied): cards per slot, validation, stats, selections.
  view(id: string): Json {
    const s = state();
    const r = selectResolvedDeckById(s, id, true);
    if (!r) return "null";
    const v = selectDeckValid(s, r);
    const list = (slots: SlotMap, cards: Record<string, { card: Card }> | undefined) =>
      Object.entries(slots ?? {})
        .filter(([, q]) => q > 0)
        .map(([code, q]) => cardBrief(cards?.[code], q) ?? { code, quantity: q });
    const rr = r as unknown as Record<string, unknown> & { cards: Record<string, Record<string, { card: Card }>> };
    return JSON.stringify({
      id,
      name: r.name,
      taboo_id: r.taboo_id,
      investigator: r.investigatorBack.card.code,
      investigator_front: r.investigatorFront.card.code,
      investigatorCard: cardSummary(r.investigatorBack.card),
      slots: list(r.slots, rr.cards.slots),
      sideSlots: list(r.sideSlots, rr.cards.sideSlots),
      extraSlots: list(rr.extraSlots as SlotMap, rr.cards.extraSlots),
      bonded: list(rr.bondedSlots as SlotMap, rr.cards.bondedSlots),
      ignoreDeckLimitSlots: r.ignoreDeckLimitSlots,
      selections: r.selections,
      stats: r.stats,
      xp: r.xp,
      xpAdjustment: r.xp_adjustment,
      validation: v,
      edited: !!s.deckEdits[id],

      metaParsed: rr.metaParsed,
      previous: r.previous_deck,
      upgrade: upgradeStats(id),
      tags: r.tags,
      description: r.description_md,
      annotations: rr.annotations,
      cardPool: rr.cardPool ?? [],
      // the investigator's sides (original / parallel) as build's config editor offers them
      sides: (() => {
        const rel = (rr.investigatorBack as unknown as { relations?: { parallel?: { card: Card } } })?.relations
          ?? (rr.investigatorFront as unknown as { relations?: { parallel?: { card: Card } } })?.relations;
        return { original: r.investigator_code, parallel: rel?.parallel?.card.code ?? null,
          front: r.investigatorFront.card.code, back: r.investigatorBack.card.code };
      })(),
      // the investigator's deck-building choices (faction_selected / deck_size_selected / option_selected)
      choices: (r.investigatorBack.card.deck_options ?? [])
        .map((o, i) => ({ index: i, id: o.id, name: o.name, faction_select: o.faction_select, deck_size_select: o.deck_size_select, option_select: o.option_select }))
        .filter((o) => o.faction_select || o.deck_size_select || o.option_select),
    });
  },

  // The player cards this deck (edits applied) may take — build's access filter.
  access(id: string, target: string): Json {
    const s = state();
    const r = selectResolvedDeckById(s, id, true);
    if (!r) return "[]";
    const filter = filterInvestigatorAccess(r.investigatorBack.card, selectStaticBuildQlInterpreter(s), selectLookupTables(s), {
      targetDeck: target === "extraSlots" ? "extraSlots" : "slots",
      // options the deck's own cards add (多才多艺 Versatile, …), as build's own card list passes them (selectors/lists.ts)
      additionalDeckOptions: target === "extraSlots" ? [] : getAdditionalDeckOptions(r),
      selections: r.selections,
      investigatorFront: r.investigatorFront.card,
      customizable: { properties: "all", level: "all" },
    } as never);
    const weakness = filterInvestigatorWeaknessAccess(r.investigatorBack.card, selectLookupTables(s));
    const rp = r as unknown as { cardPool?: string[]; extraSlots?: Record<string, number> };
    const poolFilter = rp.cardPool?.length ? filterCardPool(rp.cardPool as never, selectMetadata(s), selectLookupTables(s)) : undefined;
    const inDeck = (c: Card) => !!(r.slots?.[c.code] || r.sideSlots?.[c.code] || rp.extraSlots?.[c.code]);
    const inPool = (c: Card) => !poolFilter || poolFilter(c) || inDeck(c) || (!c.duplicate_of_code && c.xp == null && (!!c.restrictions || !!c.encounter_code));
    const out: unknown[] = [];
    for (const c of Object.values(selectMetadata(s).cards)) {
      if (!filterPlayerCards(c) || !filterDuplicates(c) || !filterAlternates(c)) continue;
      if (c.type_code === "investigator" || c.encounter_code) continue;
      // weaknesses this investigator may take (build's weakness access), marked so the editor shows them only on request
      if (c.subtype_code) { if (weakness(c)) out.push({ ...cardSummary(c), access: "weakness" }); continue; }
      if (filter && filter(c) && inPool(c)) out.push(cardSummary(c));
    }
    return JSON.stringify(out);
  },

  // ── edits (build's pending edits until save) ──
  quantity(id: string, code: string, quantity: number, limit: number, slot: string, mode: string) {
    // a card the deck holds in another printing (the pool shows only originals): the change goes to that printing
    const r = selectResolvedDeckById(state(), id, true) as unknown as Record<string, SlotMap> | undefined;
    code = printingIn(r?.[slot || "slots"], code);
    state().updateCardQuantity(id, code, quantity, limit, (slot || "slots") as never, (mode || "increment") as never);
    return true;
  },
  meta(id: string, key: string, value: string | null, type: string) { state().updateMetaProperty(id, key, value, (type || undefined) as never); return true; },
  name(id: string, value: string) { state().updateName(id, value); return true; },
  taboo(id: string, value: number | null) { state().updateTabooId(id, value); return true; },
  taboos(): Json { return JSON.stringify(Object.values(selectMetadata(state()).tabooSets)); },
  tags(id: string, value: string) { state().updateTags(id, value); return true; },
  description(id: string, value: string) { state().updateDescription(id, value); return true; },
  customization(id: string, code: string, index: number, editText: Json) { state().updateCustomization(id, code, index, JSON.parse(editText)); return true; },
  side(id: string, side: string, code: string) { state().updateInvestigatorSide(id, side, code); return true; },
  xpAdjustment(id: string, value: number) { state().updateXpAdjustment(id, value); return true; },
  // { code: [upgrade card, …] } for the cards in this part of the deck that have higher-level versions
  upgrades(id: string, target: string): Json {
    const a = availableUpgrades(id, target);
    const out: Record<string, unknown[]> = {};
    for (const [code, list] of Object.entries(a?.upgrades ?? {})) out[code] = list.map((c) => cardSummary(c));
    return JSON.stringify(out);
  },
  // replace copies of a card with a higher-level version (build's upgradeCard; one copy per delta)
  upgradeCard(id: string, code: string, upgradeCode: string, copies: number, target: string) {
    const slots = (target === "extraSlots" ? "extraSlots" : "slots") as "slots" | "extraSlots";
    for (let i = 0; i < Math.max(1, copies); i++) {
      const a = availableUpgrades(id, slots);
      if (!a?.upgrades[code]?.some((c) => c.code === upgradeCode)) break;   // no (more) copies to upgrade
      state().upgradeCard({ availableUpgrades: a, code, deckId: id, delta: 1, slots, upgradeCode });
    }
    return true;
  },
  annotation(id: string, code: string, value: string | null) { state().updateAnnotation(id, code, value || null); return true; },
  // move one copy between the main and the side deck (build's swapDeck)
  swap(id: string, code: string, target: string) {
    const card = selectMetadata(state()).cards[code];
    if (card) state().swapDeck(card, id, (target === "sideSlots" ? "sideSlots" : "slots") as never);
    return true;
  },
  // a customizable card's sheet: its options, their text, and what this deck has bought
  customizable(id: string, code: string): Json {
    const s = state();
    const r = selectResolvedDeckById(s, id, true);
    const c = selectMetadata(s).cards[code];
    if (!r || !c?.customization_options) return "null";
    const rr = r as unknown as { customizations?: Record<string, Record<string, { index: number; xp_spent: number; selections?: string }>> };
    return JSON.stringify({ code, name: c.name, text: c.customization_text, options: c.customization_options, chosen: rr.customizations?.[code] ?? {} });
  },
  // build's draw, kept to the basic weaknesses the rules core can play (`allowed`, the core's own pool: StandaloneDecks.
  // WeaknessPool — no class-restricted ones such as 09124–09128, which the core refuses as unsupportedCardRestriction).
  // build already skips weaknesses of other classes; a draw outside the core's pool is undone and drawn again.
  randomWeakness(id: string, allowed?: Json): Json {
    const ok: Set<string> | null = allowed ? new Set(JSON.parse(String(allowed)) as string[]) : null;
    const fits = (code: string) => !ok || ok.has(code) || ok.has(code.replace(/^c/, ""));
    for (let attempt = 0; attempt < 60; attempt++) {
      const before = useStore.getState().deckEdits[id];
      const card = state().drawRandomBasicWeakness(id) as Card;
      if (fits(card.code)) return JSON.stringify(cardSummary(card));
      useStore.setState((prev) => {
        const deckEdits = { ...prev.deckEdits } as Record<string, unknown>;
        if (before === undefined) delete deckEdits[id]; else deckEdits[id] = before;
        return { deckEdits } as never;
      });
    }
    throw new Error("没有可抽的基础弱点");
  },
  discard(id: string) { state().discardEdits(id); return true; },
  async save(id: string): Promise<Json> { await state().saveDeck(client, id); return deckOut(id); },
  async remove(id: string): Promise<boolean> { await state().deleteDeck(client, id); return true; },

  // Import decks from JSON text (build's importDecks: parseDeckJson + formatDeckImport — a new id, its problem, no
  // upgrade chain or xp). Takes one deck, a list of decks, or a { key: deck } map (our own decks.json). Decks whose
  // investigator or cards this card pool does not know (fan-made content) are refused, with their codes.
  importJson(text: Json): Json {
    const s = state();
    const m = selectMetadata(s);
    const raw: unknown = JSON.parse(String(text).replace(/^﻿/, ""));
    const items: unknown[] = Array.isArray(raw) ? raw
      : raw && typeof raw === "object" && !("investigator_code" in (raw as object)) ? Object.values(raw as object) : [raw];
    const added: unknown[] = [];
    const refused: unknown[] = [];
    for (const item of items) {
      let deck: Deck;
      try { deck = parseDeckJson(JSON.stringify(item)); }
      catch { refused.push({ name: (item as { name?: string })?.name ?? null, reason: "invalid" }); continue; }
      const codes = [deck.investigator_code, ...Object.keys(deck.slots ?? {}), ...Object.keys((deck.sideSlots as SlotMap) ?? {})];
      const unknown = codes.filter((c) => c && !m.cards[c]);
      if (unknown.length) { refused.push({ name: deck.name, reason: "unknown", codes: unknown }); continue; }
      try {
        const f = formatDeckImport(state(), deck, "deck");
        useStore.setState((prev) => ({ data: { ...prev.data, decks: { ...prev.data.decks, [f.id]: f }, history: { ...prev.data.history, [f.id]: [] } } }) as never);
        added.push(f);
      } catch (e) { refused.push({ name: deck.name, reason: "error", detail: String(e) }); }
    }
    return JSON.stringify({ added, refused });
  },

  // A copy under a new id (build's makeDeckCopy), added to the store.
  copy(id: string): Json {
    const d = state().data.decks[id];
    if (!d) return "null";
    const c = makeDeckCopy(d);
    useStore.setState((prev) => ({ data: { ...prev.data, decks: { ...prev.data.decks, [c.id]: c }, history: { ...prev.data.history, [c.id]: [] } } }) as never);
    return JSON.stringify(c);
  },

  // ── campaign upgrade between scenarios (build's upgradeDeck) ──
  // The deck the investigator plays now goes into the store as the previous version; build's upgradeDeck makes the
  // next version with the experience to spend. Edits on it are priced by getChangeStats (see view().upgrade).
  // notesText: the table's notes of what happened in play (UpgradeNotes above), priced on top of build; exileString: the
  // cards exiled last scenario (build's form, one code per copy), for 似曾相识 and the free level-0 replacements.
  async upgradeBegin(prevText: Json, xp: number, notesText?: string, exileString?: string): Promise<Json> {
    const raw = JSON.parse(prevText) as Record<string, unknown>;
    const prevId = `upgrade-prev-${Date.now()}`;
    // two printings of one card (two upgrades that took different printings) go in merged, on both versions, so build
    // prices the merge as no change
    const prevDeck = { ...normalize(mergeDeckPrintings({ ...raw, id: prevId }) as never), id: prevId } as Deck;
    useStore.setState((prev) => ({ data: { ...prev.data, decks: { ...prev.data.decks, [prevId]: prevDeck }, history: { ...prev.data.history, [prevId]: [] } } }) as never);
    const next = await state().upgradeDeck(client, { id: prevId, xp, exileString: exileString ?? "" });
    if (notesText) { try { upgradeNotes[next.id] = JSON.parse(notesText) as UpgradeNotes; } catch { /* no notes */ } }
    return JSON.stringify({ prev: prevId, next: next.id });
  },
  // Finish: keep the edits (save) or drop them, hand the upgraded deck back and take both versions out of the store
  // (campaign decks live in the rules core, not in the deck library).
  async upgradeEnd(id: string, keep: boolean): Promise<Json> {
    if (keep) await state().saveDeck(client, id);
    else state().discardEdits(id);
    const deck = state().data.decks[id];
    const prevId = deck?.previous_deck;
    delete upgradeNotes[id];
    useStore.setState((prev) => {
      const decks = { ...prev.data.decks };
      const history = { ...prev.data.history };
      const deckEdits = { ...prev.deckEdits };
      for (const k of [id, prevId]) if (k) { delete decks[k]; delete history[k]; delete deckEdits[k]; }
      return { data: { ...prev.data, decks, history }, deckEdits } as never;
    });
    return deck ? JSON.stringify(deck) : "null";
  },

  // ── create (build's deck-create slice) ──
  createInit(code: string): Json { state().initCreate(code); return Decks.createInfo(); },
  createInfo(): Json {
    const s = state();
    const dc = selectDeckCreateChecked(s);
    const inv = selectDeckCreateInvestigators(s);
    const sets = selectDeckCreateCardSets(s);
    const back = inv.back.card;
    const options = (back.deck_options ?? [])
      .map((o, i) => ({ index: i, id: o.id, name: o.name, faction_select: o.faction_select, deck_size_select: o.deck_size_select, option_select: o.option_select }))
      .filter((o) => o.faction_select || o.deck_size_select || o.option_select);
    const relations = (inv.investigator as unknown as { relations?: { parallel?: { card: Card } } }).relations;
    return JSON.stringify({
      state: dc,
      investigator: cardSummary(inv.investigator.card),
      front: cardSummary(inv.front.card),
      back: cardSummary(back),
      parallel: relations?.parallel ? cardSummary(relations.parallel.card) : null,
      options,
      sets: sets.map((g) => ({
        id: g.id, title: g.title, canSelect: g.canSelect, selected: g.selected,
        cards: g.cards.map((c) => cardBrief(c, g.quantities?.[c.card.code])),
      })),
      taboos: Object.values(selectMetadata(s).tabooSets),
    });
  },
  createTitle(v: string) { state().deckCreateSetTitle(v); return true; },
  createTaboo(v: number | null) { state().deckCreateSetTabooSet(v ?? undefined); return true; },
  createSelection(key: string, v: string) { state().deckCreateSetSelection(key, v); return true; },
  createSide(code: string, side: string) { state().deckCreateSetInvestigatorCode(code, side as never); return true; },
  createToggleSet(id: string) { state().deckCreateToggleCardSet(id); return true; },
  createStarter(on: boolean) { state().deckCreateSetApplyStarterDeck(on); return true; },
  async create(): Promise<Json> {
    state().deckCreateSetProvider("local" as never);
    const id = await state().createDeck(client);
    state().resetCreate();
    return deckOut(id as string);
  },
};

(globalThis as unknown as { BuildEngine: { decks: typeof Decks } }).BuildEngine.decks = Decks;
