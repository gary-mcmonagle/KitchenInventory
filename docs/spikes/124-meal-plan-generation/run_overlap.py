"""Experiment 2 (spike #124): does the model share ingredients, and does a code score + one retry help?

The household's real stock is so full that a plan rarely needs to buy anything (experiment 1), so overlap is measured on a
'before the shop' stock: the cupboard aisles plus the use-first items, which is when planning matters.
"""
import json, sys, collections
import outline, usually_buys
SP = sys.argv[1]
snap = json.load(open(SP + "/snapshot.json"))
full = outline.build_stock(snap)
prods = {x["id"]: x for x in snap["products"] if x["type"] == "product"}
mem = [x for x in snap["shoppingList"] if x["type"] == "memory"]
usual = [n for n, _, _ in usually_buys.usually_buys(snap["inventoryItems"], prods, mem)]

CUPBOARD = {"pastaRiceGrains", "tinsJars", "saucesOils", "bakingSpices", "breakfastCereal"}
def thin(with_use_first):
    keep = [s for s in full if s["aisleId"] in CUPBOARD or (with_use_first and s["useFirst"] and (s["days"] or 0) >= 0)]
    keep = [dict(s) for s in keep]
    for i, s in enumerate(keep): s["ref"] = f"p{i + 1}"
    return keep

def orphans(plan):
    c = collections.defaultdict(set)
    for m in plan:
        for b in m["toBuy"]: c[outline.norm(b)].add(m["id"])
    return {k: sorted(v) for k, v in c.items() if len(v) == 1}

def retry(stock, counts, puf, plan, orph):
    sysm = outline.system_prompt(overlap=True, prefer_use_first=puf)
    usr = outline.user_prompt(counts, stock, usual, puf, plan=plan,
                              instruction="These items are each bought for only ONE meal: " + ", ".join(sorted(orph)) +
                              ". Revise the plan so each is shared with another meal or replaced by something in stock or already shared. Return the whole plan.")
    return outline.call([{"role": "system", "content": sysm}, {"role": "user", "content": usr}], outline.schema())

results = {}
def run(label, stock, counts, puf, overlap, do_retry=False):
    sysm = outline.system_prompt(overlap=overlap, prefer_use_first=puf)
    usr = outline.user_prompt(counts, stock, usual, puf)
    plan, m = outline.call([{"role": "system", "content": sysm}, {"role": "user", "content": usr}], outline.schema())
    sc = outline.score(plan["meals"], stock)
    print(f"{label:46} {m['secs']:>5}s {m['prompt']}+{m['completion']} £{m['gbp']}  buy {sc['distinctToBuy']} distinct / {sc['toBuyMentions']} mentions, orphans {sc['orphans']}, shared {sc['sharedItems']}, shareRatio {sc['shareRatio']}, overClaimed {sc['overClaimed']}, inStockInToBuy {sc['inStockInToBuy']}")
    results[label] = {"plan": plan, "measure": m, "score": sc}
    if do_retry:
        o = orphans(plan["meals"])
        if o:
            p2, m2 = retry(stock, counts, puf, plan["meals"], o)
            s2 = outline.score(p2["meals"], stock)
            print(f"{label + ' + retry':46} {m2['secs']:>5}s {m2['prompt']}+{m2['completion']} £{m2['gbp']}  buy {s2['distinctToBuy']} distinct / {s2['toBuyMentions']} mentions, orphans {s2['orphans']}, shared {s2['sharedItems']}, shareRatio {s2['shareRatio']}, overClaimed {s2['overClaimed']}")
            results[label + " + retry"] = {"plan": p2, "measure": m2, "score": s2}
    json.dump(results, open(SP + "/overlap.json", "w"), indent=1)

C32 = {"lunch": 3, "dinner": 3}
for puf in (False, True):
    st = thin(puf)
    tag = f"before-the-shop stock ({len(st)} items), 3x2, use first {'on' if puf else 'off'}"
    for rep in (1, 2):
        run(f"A no overlap rule #{rep} | {tag}", st, C32, puf, overlap=False)
    for rep in (1, 2):
        run(f"B overlap rule #{rep} | {tag}", st, C32, puf, overlap=True, do_retry=True)
