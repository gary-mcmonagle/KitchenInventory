"""Experiment 3b (spike #124): make 'Another one' keep the plan's sharing and stay varied, with code doing the bookkeeping:
 - code works out which toBuy items the replaced meal shares with exactly one other meal (they'd be stranded) and says so;
 - code picks a different 'angle' (a main ingredient or cuisine) per call from a short list: the default temperature can't vary it."""
import json, sys, collections
import outline
from revise_lib import Ctx, merge

ANGLES = ["a rice dish", "a pasta dish", "a soup or stew", "a noodle dish", "a wrap, taco or sandwich", "a curry", "a bake or roast", "a salad or bowl", "an egg dish", "a one-pan dish"]

def stranded(plan, replace_id):
    c = collections.defaultdict(set)
    for m in plan:
        for b in m["toBuy"]: c[outline.norm(b)].add(m["id"])
    mine = {outline.norm(b) for m in plan if m["id"] == replace_id for b in m["toBuy"]}
    return sorted(k for k in mine if len(c[k] - {replace_id}) == 1)       # still bought, but for only one other meal

if __name__ == "__main__":
    SP = sys.argv[1]
    c = Ctx(SP); base = c.base; ids = [m["id"] for m in base]; counts = {"lunch": 3, "dinner": 3}
    # the meal whose removal strands the most
    pick = max(base, key=lambda m: len(stranded(base, m["id"])))
    keep = set(ids) - {pick["id"]}; st = stranded(base, pick["id"])
    print(f"replacing {pick['id']}; it shares {st} with exactly one other meal\n")
    shown = [pick["title"]]; titles = []
    for i in range(4):
        angle = ANGLES[(i * 3) % len(ANGLES)]
        ins = (f"Replace meal {pick['id']} with a different {pick['type']}, {angle}." +
               (f" The other meals buy {', '.join(st)} in sharing with it: use those in the new meal so they stay shared." if st else ""))
        ch, m = c.revise(base, keep, ins, counts, avoid=shown)
        shown.append(ch[0]["title"]); titles.append(ch[0]["title"])
        c.show(f"another one (angle+stranded) #{i+1}", merge(base, ch, keep), m, f"| {angle}: {ch[0]['title']} | buy {ch[0]['toBuy']}")
