"""Experiment 3 (spike #124): adjust a meal, another one, add a meal and adjust the whole plan, as ONE 'revise' call that returns
only the meals it changed or added. Code merges them by id, restores kept meals, and re-scores overlap."""
import json, sys
from revise_lib import Ctx, merge

SP = sys.argv[1]
c = Ctx(SP); base = c.base; ids = [m["id"] for m in base]; counts = {"lunch": 3, "dinner": 3}
sc0 = c.show("base plan", base, {"secs": 0, "prompt": 0, "completion": 0, "gbp": 0}); print()
res = {}
target = base[2]; keep = set(ids) - {target["id"]}
ch, m = c.revise(base, keep, f"Adjust meal {target['id']}: make it vegetarian.", counts)
c.show("adjust one meal", merge(base, ch, keep), m, f"| changed {[x['id'] for x in ch]}"); res["adjust"] = {"changed": ch, "measure": m}
t2 = base[1]; keep = set(ids) - {t2["id"]}; plain, avoided = [], []
for i in range(3):
    ch, m = c.revise(base, keep, f"Replace meal {t2['id']} with a different {t2['type']}.", counts)
    plain.append(ch[0]["title"]); c.show(f"another one (no avoid) #{i+1}", merge(base, ch, keep), m, f"| {ch[0]['title']}")
shown = [t2["title"]]
for i in range(3):
    ch, m = c.revise(base, keep, f"Replace meal {t2['id']} with a different {t2['type']}.", counts, avoid=shown)
    shown.append(ch[0]["title"]); avoided.append(ch[0]["title"]); c.show(f"another one (avoid shown) #{i+1}", merge(base, ch, keep), m, f"| {ch[0]['title']}")
res["another"] = {"plain": plain, "avoid": avoided}
ch, m = c.revise(base, set(ids), "Add one more dinner.", {"lunch": 3, "dinner": 4})
c.show("add a dinner", merge(base, ch, set(ids)), m, f"| buy {[x['toBuy'] for x in ch]}"); res["add"] = {"changed": ch, "measure": m}
keep = {ids[0], ids[3]}
ch, m = c.revise(base, keep, "Make the whole plan cheaper: fewer things to buy, simpler ingredients.", counts)
new = merge(base, ch, keep); c.show("whole plan: cheaper (keep 2)", new, m, f"| changed {len(ch)} of {len(base) - 2} unkept")
assert all(next(x for x in new if x["id"] == k) == next(x for x in base if x["id"] == k) for k in keep), "a kept meal changed"
res["whole"] = {"changed": ch, "measure": m}
json.dump(res, open(SP + "/adjust.json", "w"), indent=1)
