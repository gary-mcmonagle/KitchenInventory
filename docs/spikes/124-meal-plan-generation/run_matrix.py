"""Experiment 1 (spike #124): outline latency, tokens and overlap on luna for 1x1, 3x2 and 7x3 meals, prefer use first off and on."""
import json, sys
import outline, usually_buys
SP = sys.argv[1]
snap = json.load(open(SP + "/snapshot.json"))
stock = outline.build_stock(snap)
prods = {x["id"]: x for x in snap["products"] if x["type"] == "product"}
mem = [x for x in snap["shoppingList"] if x["type"] == "memory"]
usual = [n for n, _, _ in usually_buys.usually_buys(snap["inventoryItems"], prods, mem)]
SIZES = {"1 day x 1 meal": {"dinner": 1}, "3 days x 2 meals": {"lunch": 3, "dinner": 3}, "7 days x 3 meals": {"breakfast": 7, "lunch": 7, "dinner": 7}}
out = {}
for label, counts in SIZES.items():
    for puf in (False, True):
        sysm = outline.system_prompt(overlap=True, prefer_use_first=puf)
        usr = outline.user_prompt(counts, stock, usual, puf)
        plan, m = outline.call([{"role": "system", "content": sysm}, {"role": "user", "content": usr}], outline.schema())
        sc = outline.score(plan["meals"], stock)
        key = f"{label} | use first {'on' if puf else 'off'}"
        out[key] = {"plan": plan, "measure": m, "score": sc}
        print(f"{key:40} {m['secs']:>5}s  {m['prompt']}+{m['completion']} tok (reasoning {m['reasoning']})  £{m['gbp']}  {sc}")
        json.dump(out, open(SP + "/matrix.json", "w"), indent=1)
