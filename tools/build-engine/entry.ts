// Headless arkham.build rules engine for the Godot deck workshop (Yunke 11-18: "基底是 build").
// Bundled by tools/build-engine/build.mjs into app/assets/build-engine/engine.js and run in Jint (C#).
// Everything rule-related is arkham.build's own code (vendor/arkham.build, AGPL-3.0): its store is started in
// "refresh" mode from the local card cache, and its selectors / lib functions answer the workshop's questions.
import type { Card, Deck } from "@arkham-build/shared";
import { useStore } from "@/store";

import { getChangeStats } from "@/store/lib/deck-upgrades";
import { getAdditionalDeckOptions } from "@/store/lib/deck-options";
import { validateDeck } from "@/store/lib/deck-validation";
import { makeDeck } from "@/store/lib/deck-factory";
import {
  filterInvestigatorAccess,
  filterInvestigatorWeaknessAccess,
  filterPlayerCards,
  filterDuplicates,
  filterAlternates,
} from "@/store/lib/filtering";
import { resolveDeck } from "@/store/lib/resolve-deck";
import { randomBasicWeaknessForDeck } from "@/store/lib/random-basic-weakness";
import {
  selectLocaleSortingCollator,
  selectLookupTables,
  selectMetadata,
  selectStaticBuildQlInterpreter,
} from "@/store/selectors/shared";
import factions from "@/store/services/data/factions.json";
import subTypes from "@/store/services/data/subtypes.json";
import types from "@/store/services/data/types.json";

export type Json = string;

export function state() {
  return useStore.getState();
}
function deps() {
  const s = state();
  return { metadata: selectMetadata(s), lookupTables: selectLookupTables(s) };
}
function collator() {
  return selectLocaleSortingCollator(state());
}

// Our saved decks are ArkhamDB-style objects ({ name, investigator_code, slots, sideSlots, meta? }); build's Deck needs
// every field present, so missing ones get build's defaults.
export function normalize(raw: Partial<Deck> & Record<string, unknown>): Deck {
  const now = new Date().toISOString();
  return {
    id: (raw.id as string) ?? "local",
    name: raw.name ?? "",
    investigator_code: raw.investigator_code as string,
    investigator_name: (raw.investigator_name as string) ?? "",
    slots: (raw.slots as Record<string, number>) ?? {},
    sideSlots: (raw.sideSlots as Record<string, number>) ?? {},
    ignoreDeckLimitSlots: (raw.ignoreDeckLimitSlots as Record<string, number>) ?? null,
    meta: typeof raw.meta === "string" ? raw.meta : JSON.stringify(raw.meta ?? {}),
    taboo_id: (raw.taboo_id as number) ?? null,
    xp: (raw.xp as number) ?? null,
    xp_adjustment: (raw.xp_adjustment as number) ?? null,
    exile_string: (raw.exile_string as string) ?? null,
    problem: (raw.problem as string) ?? null,
    tags: (raw.tags as string) ?? "",
    description_md: (raw.description_md as string) ?? "",
    date_creation: (raw.date_creation as string) ?? now,
    date_update: (raw.date_update as string) ?? now,
    source: (raw.source as Deck["source"]) ?? "local",
    version: (raw.version as string) ?? "0.1",
    previous_deck: (raw.previous_deck as Deck["previous_deck"]) ?? null,
    next_deck: (raw.next_deck as Deck["next_deck"]) ?? null,
  } as Deck;
}

function resolve(deck: Deck) {
  return resolveDeck(deps(), collator(), normalize(deck as never));
}

export function cardSummary(c: Card) {
  return {
    code: c.code,
    name: c.name,
    real_name: c.real_name,
    subname: c.subname,
    faction_code: c.faction_code,
    faction2_code: c.faction2_code,
    faction3_code: c.faction3_code,
    type_code: c.type_code,
    subtype_code: c.subtype_code,
    pack_code: c.pack_code,
    xp: c.xp,
    cost: c.cost,
    traits: c.traits,
    slot: c.real_slot,
    deck_limit: c.deck_limit,
    exceptional: c.exceptional,
    myriad: c.myriad,
    customization_options: c.customization_options ? true : undefined,
    skill_willpower: c.skill_willpower,
    skill_intellect: c.skill_intellect,
    skill_combat: c.skill_combat,
    skill_agility: c.skill_agility,
    skill_wild: c.skill_wild,
    text: c.text,
    restrictions: c.restrictions,
    deck_requirements: c.deck_requirements,
    duplicate_of_code: c.duplicate_of_code,
    alternate_of_code: c.alternate_of_code,
  };
}

const Engine = {
  version: "arkham.build " + (globalThis as unknown as { __BUILD_COMMIT__?: string }).__BUILD_COMMIT__,

  // Load the local cache (the same JSON the build API serves: version, metadata, all_card) and build the store.
  async init(versionText: Json, metadataText: Json, cardsText: Json, locale: string) {
    const version = JSON.parse(versionText).data.all_card_updated[0];
    const metadataResponse = JSON.parse(metadataText).data;
    const cards = JSON.parse(cardsText).data.all_card;
    useStore.setState(useStore.getInitialState(), true);
    await state().init(
      () =>
        Promise.resolve({
          ...metadataResponse,
          faction: factions,
          type: types,
          subtype: subTypes,
        }),
      () => Promise.resolve(version),
      () => Promise.resolve(cards),
      { refresh: true, locale },
    );
    return Object.keys(selectMetadata(state()).cards).length;
  },

  // Investigators (front cards only, no duplicates / alternates), grouped later by pack in C#.
  investigators(): Json {
    const m = selectMetadata(state());
    const list = Object.values(m.cards).filter(
      (c) =>
        c.type_code === "investigator" &&
        filterPlayerCards(c) &&
        filterDuplicates(c) &&
        filterAlternates(c) &&
        !c.hidden,
    );
    return JSON.stringify(
      list.map((c) => ({
        ...cardSummary(c),
        cycle_code: m.packs[c.pack_code]?.cycle_code,
        pack_position: m.packs[c.pack_code]?.position,
        cycle_position: m.cycles[m.packs[c.pack_code]?.cycle_code ?? ""]?.position,
        position: c.position,
      })),
    );
  },

  // Packs and cycles (names in the cache's language), for grouping.
  packs(): Json {
    const m = selectMetadata(state());
    return JSON.stringify({ packs: Object.values(m.packs), cycles: Object.values(m.cycles) });
  },

  // The player cards a deck (its investigator, options and current slots) may include: build's own access filter.
  // target: "slots" (the deck) | "extraSlots" (Parallel Jim's spirit deck etc.)
  access(deckText: Json, target: string): Json {
    const deck = JSON.parse(deckText) as Deck;
    const resolved = resolve(deck);
    const s = state();
    const lookupTables = selectLookupTables(s);
    const interpreter = selectStaticBuildQlInterpreter(s);
    const filter = filterInvestigatorAccess(resolved.investigatorBack.card, interpreter, lookupTables, {
      targetDeck: target === "extraSlots" ? "extraSlots" : "slots",
      // options the deck's own cards add (多才多艺 Versatile, …), as build's own card list passes them (selectors/lists.ts)
      additionalDeckOptions: target === "extraSlots" ? [] : getAdditionalDeckOptions(resolved),
      selections: resolved.selections,
      investigatorFront: resolved.investigatorFront.card,
      customizable: { properties: "all", level: "all" },
    } as never);
    const weakness = filterInvestigatorWeaknessAccess(resolved.investigatorBack.card, lookupTables);
    const m = selectMetadata(s);
    const out: unknown[] = [];
    for (const c of Object.values(m.cards)) {
      if (!filterPlayerCards(c) || !filterDuplicates(c) || !filterAlternates(c)) continue;
      if (c.type_code === "investigator" || c.encounter_code) continue;
      const ok = filter ? filter(c) : false;
      const weak = c.subtype_code ? weakness(c) : false;
      if (ok || weak) out.push({ ...cardSummary(c), access: ok ? "deck" : "weakness" });
    }
    return JSON.stringify(out);
  },

  // Validation exactly as build does it; the errors keep build's structure (type + details) for the UI to word.
  validate(deckText: Json): Json {
    const deck = JSON.parse(deckText) as Deck;
    const resolved = resolve(deck);
    const s = state();
    const result = validateDeck(resolved, selectMetadata(s), selectLookupTables(s), selectStaticBuildQlInterpreter(s));
    (result as unknown as { investigator: string }).investigator = deck.investigator_code;
    return JSON.stringify(result);
  },

  // A resolved deck in summary: size, xp, counts, the investigator's deck options in words.
  summary(deckText: Json): Json {
    const deck = JSON.parse(deckText) as Deck;
    const r = resolve(deck);
    return JSON.stringify({
      investigator: r.investigatorBack.card.code,
      stats: r.stats,
      xp: r.stats?.xpRequired,
      selections: r.selections,
      cards: Object.fromEntries(
        Object.entries(r.cards.slots).map(([code, rc]) => [code, { name: rc.card.name, quantity: deck.slots[code] }]),
      ),

    });
  },

  // A new deck the way build's "create deck" makes it (signatures and default weaknesses included).
  normalize(deckText: Json): Json {
    return JSON.stringify(normalize(JSON.parse(deckText)));
  },

  newDeck(payloadText: Json): Json {
    return JSON.stringify(makeDeck(JSON.parse(payloadText)));
  },

  // A random basic weakness for the deck (build's pool and rules).
  randomWeakness(deckText: Json): Json {
    const deck = JSON.parse(deckText) as Deck;
    const s = state();
    return JSON.stringify(
      randomBasicWeaknessForDeck(selectMetadata(s), selectLookupTables(s), s.settings, resolve(deck)) ?? null,
    );
  },

  // Upgrade cost between a deck and its previous version (campaign play), as build computes it.
  upgrade(prevText: Json, nextText: Json): Json {
    const prev = resolve(JSON.parse(prevText) as Deck);
    const next = resolve(JSON.parse(nextText) as Deck);
    return JSON.stringify(getChangeStats(prev, next) ?? null);
  },
};

(globalThis as unknown as { BuildEngine: typeof Engine }).BuildEngine = Engine;
