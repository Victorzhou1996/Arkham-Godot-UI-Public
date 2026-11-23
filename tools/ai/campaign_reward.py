# Campaign reward for Night of the Zealot (docs/AI-PLAY-PLAN-20261119.md §8): the goal is the whole campaign, not one chapter.
# A chapter is worth what it adds to the campaign log (favourable / unfavourable records), the trauma it leaves and any
# investigator lost (killed or driven insane = gone for the campaign).
#   rec = {"keys": [...], "sets": [{"key", "n"}], "trauma": [{"code", "phys", "ment", "lost"}]}   (tools/sim CampRecord)
KEYS = {
    # 1 the Gathering
    "YourHouseIsStillStanding": 2, "YourHouseHasBurnedToTheGround": 0, "GhoulPriestIsStillAlive": -2,
    "LitaWasForcedToFindOthersToHelpHerCause": 0,
    # 2 the Midnight Masks
    "ItIsPastMidnight": -1,
    # 3 the Devourer Below (endings)
    "ArkhamSuccumbedToUmordhothsTerribleVengeance": -6, "TheRitualToSummonUmordhothWasBroken": 6,
    "TheInvestigatorsRepelledUmordhoth": 6, "TheInvestigatorsSacrificedLitaChantlerToUmordhoth": 3,
}
SETS = {"CultistsWeInterrogated": 1, "CultistsWhoGotAway": -2}   # got away: spawns at the Main Path in 地底噬魔 (InvestigatingTheTrail)
TRAUMA, LOST = -1, -8 - 6   # a lost investigator: -8, and the campaign is as good as failed (it ends here: the -6 ending it skips)
EMPTY = {"keys": [], "sets": [], "trauma": []}


def key_value(k):
    if k in KEYS:
        return KEYS[k]
    if "Succumbed" in k or "Devoured" in k:
        return -6
    if "Broken" in k or "Repelled" in k:
        return 6
    return 0


def unknown(rec):
    return [k for k in rec.get("keys", []) if k not in KEYS]


FINAL_ENDINGS = ("Succumbed", "Devoured", "Broken", "Repelled", "Sacrificed")   # 地底噬魔's resolutions: the campaign's last chapter


def chapter(prev, cur, final_free=True):
    """what one chapter added to the campaign (cur and prev are campaign records at its end and start)"""
    new = set(cur.get("keys", [])) - set(prev.get("keys", []))
    r = sum(key_value(k) for k in new)
    # the last chapter: the campaign ends with it, so the trauma it leaves and an investigator lost in it change nothing
    # later — only the ending counts (else resigning at once, to save trauma, looks as good as trying; 11-20 stage 3)
    final = final_free and any(e in k for k in new for e in FINAL_ENDINGS)
    pn = {s["key"]: s["n"] for s in prev.get("sets", [])}
    r += sum(SETS.get(s["key"], 0) * (s["n"] - pn.get(s["key"], 0)) for s in cur.get("sets", []))
    pt = {t["code"]: t for t in prev.get("trauma", [])}
    for t in ([] if final else cur.get("trauma", [])):
        p = pt.get(t["code"], {"phys": 0, "ment": 0, "lost": False})
        r += TRAUMA * (t["phys"] + t["ment"] - p["phys"] - p["ment"])
        if t.get("lost") and not p.get("lost"):
            r += LOST
    return r


def campaign(rec):
    """the whole campaign's score (all trauma counts, as in the earlier comparisons); training targets use chapter()"""
    return chapter(EMPTY, rec, final_free=False)
