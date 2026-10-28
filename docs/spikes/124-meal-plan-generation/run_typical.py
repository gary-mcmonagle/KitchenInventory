"""Experiment 6 (spike #124): the 'typical plan' (3 days x dinner) on the full real stock, use first off and on, and veggie."""
import sys
import outline
from revise_lib import Ctx
c = Ctx(sys.argv[1])
full = c.snap and outline.build_stock(c.snap)       # the household's whole stock, not the before-the-shop subset
for label, puf, diet in [("3 dinners, use first off", False, "any"), ("3 dinners, use first on", True, "any"), ("3 dinners, veggie, use first off", False, "veg")]:
    msgs = [{"role": "system", "content": outline.system_prompt(True, diet=diet, prefer_use_first=puf)}, {"role": "user", "content": outline.user_prompt({"dinner": 3}, full, c.usual, puf, diet=diet)}]
    plan, m = outline.call(msgs, outline.schema())
    refs = {s["ref"]: s for s in full}
    meat = [r for x in plan["meals"] for r in x["uses"] if r in refs and refs[r]["animal"] in ("meat", "fish")]
    sc = outline.score(plan["meals"], full)
    print(f"{label:34} {m['secs']:>5}s {m['prompt']}+{m['completion']} (reasoning {m['reasoning']}) £{m['gbp']} | buy {sc['distinctToBuy']} | use-first covered {sc['useFirstCovered']} | meat/fish refs used {len(meat)}")
