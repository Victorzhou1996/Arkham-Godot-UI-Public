# Legal test decks for the AI-play functional tests: each investigator's deck options (arkham.build data), the cards
# under test, level-0 fillers of the allowed classes, signatures, one random basic weakness (01000).
import json, sys
root = sys.argv[1]; out = sys.argv[2]
cards = {}
for c in json.load(open(root + '/app/assets/build-data/cards-zh-cn.json', encoding='utf-8'))['data']['all_card']:
    cards.setdefault(c['code'], c)
known = set()
import glob, os
for f in glob.glob(root + '/app/assets/data/*-cards.json') + [root + '/app/assets/data/core-cards.json']:
    for c in json.load(open(f, encoding='utf-8')): known.add(c['code'])

MAIN_PACKS = None  # any pack the catalog knows

def traits(c): return [t.strip().rstrip('.') for t in (c.get('real_traits') or '').split('.') if t.strip()]

def allowed(inv, c, sel):
    lvl = c.get('xp') or 0
    for o in inv.get('deck_options', []):
        if o.get('virtual'): continue
        if 'not' in o: continue
        fac = o.get('faction') or []
        if o.get('faction_select'): fac = [sel.get('faction_selected')] if sel.get('faction_selected') else []
        if 'id' in o and o.get('id') in ('faction_1', 'faction_2'):
            fac = [sel.get(o['id'])] if sel.get(o['id']) else []
        if fac and c.get('faction_code') not in fac: continue
        if 'level' in o and not (o['level']['min'] <= lvl <= o['level']['max']): continue
        if 'trait' in o and not any(t.lower() in [x.lower() for x in traits(c)] for t in o['trait']): continue
        if 'type' in o and c['type_code'] not in o['type']: continue
        if not fac and 'trait' not in o and 'type' not in o and 'level' not in o: continue
        if 'limit' in o: continue
        return True
    return False

def filler_ok(c):
    return (c['code'] in known and c['type_code'] in ('asset', 'event', 'skill') and (c.get('xp') or 0) == 0
            and not c.get('restrictions') and not c.get('subtype_code') and not c.get('permanent')
            and '永久' not in (c.get('text') or '') and '定制' not in (c.get('text') or '') and (c.get('deck_limit') or 2) >= 2
            and not c.get('duplicate_of_code') and not c.get('hidden') and c['faction_code'] != 'mythos'
            and not c['code'].startswith(('6', '9', '8')) or (c['code'] in known and c['code'][:2] in ('60',) and False))

def build(name, inv_code, need, sel=None, budget=0, singleton=False, size_delta=0, prefer_trait=None, prefer_min=0, attach=None, prefer_type=None):
    sel = sel or {}
    inv = cards[inv_code]
    req = inv.get('deck_requirements') or {}
    size = (req.get('size') or 30) + size_delta
    slots = {}
    for code, n in need: slots[code] = n
    counted = lambda: sum(n for k, n in slots.items() if not cards[k].get('permanent') and '永久' not in (cards[k].get('text') or '') and cards[k].get('subtype_code') not in ('weakness', 'basicweakness') and not cards[k].get('restrictions'))
    pool = sorted([c for c in cards.values() if filler_ok(c) and allowed(inv, c, sel)], key=lambda c: (c['code'][:2] not in ('01', '02', '03', '04', '05'), c['code']))
    if prefer_trait or prefer_type:
        hit = (lambda c: prefer_trait in traits(c)) if prefer_trait else (lambda c: c['type_code'] == prefer_type)
        pref = [c for c in pool if hit(c)]
        have = sum(n for k, n in slots.items() if hit(cards[k]))
        for c in pref:
            if have >= prefer_min or counted() >= size: break
            if c['code'] in slots: continue
            k = 1 if singleton else min(2, prefer_min - have); slots[c['code']] = k; have += k
    for c in pool:
        if counted() >= size: break
        if c['code'] in slots: continue
        slots[c['code']] = min(1 if singleton else 2, size - counted())
    for code in (req.get('card') or {}): slots[code] = 1 if code in ('08015', '06008') else (cards[code].get('deck_limit') or 1)   # as the rules core counts them
    slots['01000'] = 1
    meta = dict(sel)
    if budget: meta['gathering_standalone_xp'] = budget
    if attach: meta.update(attach)
    return {'name': name, 'investigator_code': inv_code, 'slots': slots, 'meta': json.dumps(meta) if meta else ''}

decks = {}
decks['qa-forced-learning'] = build('QA · 强制学习 · 黛西', '01002', [('08031', 1)], size_delta=15)
decks['qa-underworld-support'] = build('QA · 地下支持 · 斯基兹', '01003', [('08046', 1)], singleton=True, size_delta=-5)
mkt = build('QA · 地下市场 · 斯基兹', '01003', [('09077', 1)], budget=2, size_delta=10, prefer_trait='Illicit', prefer_min=10)
illicit = [k for k in mkt['slots'] if 'Illicit' in traits(cards[k]) and k != '09077']
decks['qa-underworld-market'] = mkt
decks['qa-ancestral-knowledge'] = build('QA · 先祖知识 · 黛西', '01002', [('07303', 1)], budget=3, size_delta=5, prefer_type='skill', prefer_min=10)
for code, nm, sel in [('05002', '乔·戴蒙德', {}), ('08010', '陈丽丽', {}), ('07001', '玛丽修女', {}), ('11017', '乔治·巴纳比', {}),
                      ('06002', '曼蒂·汤普森', {'deck_size_selected': '30', 'faction_selected': 'mystic'}), ('06003', '托尼·摩尔根', {'faction_selected': 'guardian'}), ('09001', '卡森·辛克莱', {'faction_selected': 'seeker'}),
                      ('09018', '查理·凯恩', {'faction_1': 'guardian', 'faction_2': 'seeker'}), ('11001', '玛利翁·塔瓦雷斯', {'option_selected': 'improvised'}),
                      ('11014', '歌莉娅·戈德堡', {'faction_selected': 'seeker'})]:
    pt = 'Insight' if code == '05002' else None
    decks['qa-inv-' + code] = build('QA · ' + nm, code, [], sel=sel, prefer_trait=pt, prefer_min=11 if pt else 0)
json.dump({'decks': decks, 'illicit': illicit}, open(out, 'w', encoding='utf-8'), ensure_ascii=False, indent=1)
for k, d in decks.items(): print(k, d['investigator_code'], sum(d['slots'].values()), len(d['slots']))
