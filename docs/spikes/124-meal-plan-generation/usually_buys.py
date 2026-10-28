"""'Usually buys' (spike #124): the rule, as a pure function over the three signals, plus the live queries with their RU.

Signals (all read-only): check-ins of units since the stock take (inventoryItems), check-outs (used up) since then, and what
people put on the shopping list (the `memory` documents: one per name ever added, with timesAdded, kept for good).
"""
import collections, datetime as dt

HISTORY_START = "2026-10-20T00:00:00"   # the day after the stock take (18-19 Oct): an app setting in the real thing
MIN_SCORE = 2                            # the floor: one signal alone isn't "usually"
TOP_N = 30

def key_of(product):                     # the generic name where there is one, else the display name
    g = product.get("genericName")
    name = g.get("name") if isinstance(g, dict) else g
    return (name or product.get("displayName") or "").strip().lower()

def usually_buys(units, products, memory, start=HISTORY_START, top_n=TOP_N, min_score=MIN_SCORE):
    """units: inventory docs; products: {productId: product doc}; memory: shopping-list memory docs.
    Returns [(name, score, why)] best first. Empty is fine: the plan is made without it."""
    trips, used, listed, last = collections.defaultdict(set), set(), collections.Counter(), {}
    for u in units:
        p = products.get(u["productId"])
        if not p: continue
        k = key_of(p)
        if u["checkedInAt"] >= start:
            trips[k].add(u["checkedInAt"][:10]); last[k] = max(last.get(k, ""), u["checkedInAt"])
        if u.get("checkedOutAt") and u["checkedOutAt"] >= start:
            used.add(k)
    for m in memory:
        listed[m["normalizedName"].strip().lower()] = min(m.get("timesAdded", 1), 3)
    out = []
    for k in set(trips) | used | set(listed):
        t, l, e = min(len(trips.get(k, ())), 4), listed.get(k, 0), 1 if k in used else 0
        score = t + l + e
        if score >= min_score:
            out.append((k, score, f"{t} trip(s), {l} list add(s), {'used' if e else 'not used'}"))
    out.sort(key=lambda x: (-x[1], x[0]))
    return out[:top_n]

QUERIES = {   # what the server would run: partition-scoped or small, with a projection
    "units": "SELECT c.productId, c.checkedInAt, c.checkedOutAt, c.status FROM c WHERE c.checkedInAt >= @start",
    "memory": "SELECT c.normalizedName, c.name, c.timesAdded, c.lastAddedAt FROM c WHERE c.type = 'memory'",
}
if __name__ == "__main__":
    import json, sys
    from cosmos import query
    SP = sys.argv[1]
    snap = json.load(open(SP + "/snapshot.json"))
    prods = {x["id"]: x for x in snap["products"] if x["type"] == "product"}
    for name, sql in QUERIES.items():
        container, part = ("inventoryItems", None) if name == "units" else ("shoppingList", "household")
        docs, ru, pages = query(container, sql, [{"name": "@start", "value": HISTORY_START + "Z"}] if name == "units" else None, partition=part)
        print(f"{name}: {len(docs)} rows, {ru} RU, {pages} page(s)")
        if name == "units": units = docs
        else: memory = docs
    # the units query above only has units checked in since the start; check-outs of older units need the second signal
    all_units, ru, _ = query("inventoryItems", "SELECT c.productId, c.checkedInAt, c.checkedOutAt FROM c WHERE c.checkedOutAt >= @start", [{"name": "@start", "value": HISTORY_START + "Z"}])
    print(f"check-outs since start: {len(all_units)} rows, {ru} RU")
    merged = {(u['productId'], u['checkedInAt']): u for u in units + all_units}.values()
    res = usually_buys(merged, prods, memory)
    print(f"usually buys today (score >= {MIN_SCORE}): {len(res)}")
    for n, s, w in res: print(f"  {s}  {n}  ({w})")
    base = usually_buys(snap["inventoryItems"], prods, [], start="2026-10-18T00:00:00", min_score=2)
    print("for contrast, counting the stock take as history (start 18 Oct, no list):", len(base), "names")
    only1 = usually_buys(merged, prods, memory, min_score=1)
    print("with floor 1:", len(only1))
