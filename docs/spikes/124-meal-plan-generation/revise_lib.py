"""The 'revise' call shared by experiments 3 and 3b (spike #124): one call that returns only the meals it changed or added."""
import copy, json
import outline, run_overlap_lib as lib

class Ctx:
    def __init__(self, SP, puf=False):
        self.snap, full, self.usual = lib.load(SP)
        self.puf = puf
        self.stock = lib.thin(full, puf)
        res = json.load(open(SP + "/overlap.json"))
        key = [k for k in res if k.startswith("B overlap rule #1") and f"use first {'on' if puf else 'off'}" in k and "retry" not in k][0]
        self.base = res[key]["plan"]["meals"]

    def system(self):
        return outline.system_prompt(overlap=True, prefer_use_first=self.puf) + (
            "\nREVISING A PLAN: you are given the current plan. Return ONLY the meals you changed or added (same ids for changed meals; new ids for added ones), "
            "never a meal marked KEEP. Keep the plan's sharing of ingredients: a changed or new meal should reuse items other meals already buy where it makes sense.")

    def revise(self, plan, keep_ids, instruction, counts, avoid=None):
        p = copy.deepcopy(plan)
        for m in p: m["keep"] = m["id"] in keep_ids
        ins = instruction + (" Avoid these titles: " + "; ".join(avoid) + "." if avoid else "")
        usr = outline.user_prompt(counts, self.stock, self.usual, self.puf, plan=p, instruction=ins)
        out, m = outline.call([{"role": "system", "content": self.system()}, {"role": "user", "content": usr}], outline.schema())
        return out["meals"], m

    def show(self, label, plan, m, extra=""):
        sc = outline.score(plan, self.stock)
        print(f"{label:38} {m['secs']:>5}s {m['prompt']}+{m['completion']} £{m['gbp']}  meals {sc['meals']} buy {sc['distinctToBuy']}/{sc['toBuyMentions']} orphans {sc['orphans']} shareRatio {sc['shareRatio']} {extra}")
        return sc

def merge(plan, changed, keep_ids, delete_ids=()):
    by = {m["id"]: m for m in plan}
    for c in changed:
        if c["id"] in keep_ids: continue           # code never lets a kept meal change
        by[c["id"]] = c
    return [m for m in by.values() if m["id"] not in delete_ids]
