import json
import outline, usually_buys
CUPBOARD = {"pastaRiceGrains", "tinsJars", "saucesOils", "bakingSpices", "breakfastCereal"}
def load(SP):
    snap = json.load(open(SP + "/snapshot.json"))
    full = outline.build_stock(snap)
    prods = {x["id"]: x for x in snap["products"] if x["type"] == "product"}
    mem = [x for x in snap["shoppingList"] if x["type"] == "memory"]
    usual = [n for n, _, _ in usually_buys.usually_buys(snap["inventoryItems"], prods, mem)]
    return snap, full, usual
def thin(full, with_use_first):
    keep = [dict(s) for s in full if s["aisleId"] in CUPBOARD or (with_use_first and s["useFirst"] and (s["days"] or 0) >= 0)]
    for i, s in enumerate(keep): s["ref"] = f"p{i + 1}"
    return keep
