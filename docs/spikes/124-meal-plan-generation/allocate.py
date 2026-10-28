"""Stock allocation (spike #124): who gets what is in stock, in code, with no quantities beyond 'units'.

Rule: one UNIT of a product serves ONE meal, except bulk products, which serve any number of meals.
  - A product's capacity is its in-stock unit count (a tin, a pack, a bag), or unlimited if it is BULK.
  - BULK = the aisles where one pack lasts many meals (pasta, rice & grains; baking & spices; sauces, oils & condiments;
    breakfast & cereal; drinks), or a name that says so (a short list: salt, pepper, oil, flour, sugar, rice, pasta, spices).
  - Meals claim in plan order, kept meals first (a meal the person kept never loses its stock to a new one).
  - A claim beyond capacity is SHORT: that meal needs another one, so its generic name joins the meal's things to buy.
Names only, never amounts: the person works out quantities. The model's 'uses' are claims; the model does no arithmetic.
"""
import collections, re, unittest

BULK_AISLES = {"pastaRiceGrains", "bakingSpices", "saucesOils", "breakfastCereal", "drinks"}
BULK_WORDS = re.compile(r"\b(salt|pepper|oil|flour|sugar|rice|pasta|spice|spices|stock cube|vinegar)\b", re.I)

def is_bulk(item):
    return item["aisleId"] in BULK_AISLES or bool(BULK_WORDS.search(item["name"]))

def allocate(meals, stock, keep_ids=()):
    """meals: [{'id','uses':[ref]}]; stock: [{'ref','qty','aisleId','name','generic'}].
    Returns {'claims': {ref: n}, 'short': {meal_id: [generic name]}, 'unknown': [(meal_id, ref)]}."""
    by_ref = {s["ref"]: s for s in stock}
    used = collections.Counter()
    short, unknown = collections.defaultdict(list), []
    for m in sorted(meals, key=lambda m: m["id"] not in keep_ids):      # stable: kept first, then plan order
        for ref in m["uses"]:
            item = by_ref.get(ref)
            if item is None:
                unknown.append((m["id"], ref)); continue
            if is_bulk(item) or used[ref] < item["qty"]:
                used[ref] += 1
            else:
                short[m["id"]].append(item["generic"])
    return {"claims": dict(used), "short": dict(short), "unknown": unknown}

def to_buy(meals, stock, open_on_list=(), keep_ids=()):
    """What's missing for the plan: each meal's own toBuy plus what allocation left short, merged by name, with the meals that
    need it, minus anything already open on the shopping list (#125)."""
    a = allocate(meals, stock, keep_ids)
    need = collections.defaultdict(list)
    for m in meals:
        for name in list(m["toBuy"]) + a["short"].get(m["id"], []):
            need[name.strip().lower()].append(m["id"])
    skip = {n.strip().lower() for n in open_on_list}
    return {n: sorted(set(ids)) for n, ids in sorted(need.items()) if n not in skip}

class Tests(unittest.TestCase):
    S = [{"ref": "p1", "qty": 1, "aisleId": "tinsJars", "name": "Chopped tomatoes", "generic": "chopped tomatoes"},
         {"ref": "p2", "qty": 1, "aisleId": "pastaRiceGrains", "name": "Fusilli", "generic": "pasta"},
         {"ref": "p3", "qty": 2, "aisleId": "fruitVeg", "name": "Peppers", "generic": "peppers"}]
    def meals(self, *uses): return [{"id": f"m{i + 1}", "uses": u, "toBuy": []} for i, u in enumerate(uses)]

    def test_one_tin_serves_one_meal(self):
        a = allocate(self.meals(["p1"], ["p1"]), self.S)
        self.assertEqual(a["short"], {"m2": ["chopped tomatoes"]})

    def test_bulk_serves_every_meal(self):
        self.assertEqual(allocate(self.meals(["p2"], ["p2"], ["p2"]), self.S)["short"], {})

    def test_quantity_two_serves_two_meals_not_three(self):
        a = allocate(self.meals(["p3"], ["p3"], ["p3"]), self.S)
        self.assertEqual(a["short"], {"m3": ["peppers"]})

    def test_a_kept_meal_keeps_its_stock(self):
        a = allocate(self.meals(["p1"], ["p1"]), self.S, keep_ids={"m2"})
        self.assertEqual(a["short"], {"m1": ["chopped tomatoes"]})

    def test_unknown_refs_are_reported_not_claimed(self):
        a = allocate(self.meals(["p9"]), self.S)
        self.assertEqual(a["unknown"], [("m1", "p9")]); self.assertEqual(a["claims"], {})

    def test_to_buy_merges_by_name_and_leaves_out_what_is_on_the_list(self):
        meals = [{"id": "m1", "uses": ["p1"], "toBuy": ["Cabbage"]}, {"id": "m2", "uses": ["p1"], "toBuy": ["cabbage", "Pak choi"]}]
        self.assertEqual(to_buy(meals, self.S), {"cabbage": ["m1", "m2"], "chopped tomatoes": ["m2"], "pak choi": ["m2"]})
        self.assertEqual(to_buy(meals, self.S, open_on_list=["Pak choi"]), {"cabbage": ["m1", "m2"], "chopped tomatoes": ["m2"]})

if __name__ == "__main__":
    import json, sys
    if len(sys.argv) > 1:       # run the rule over the plans the experiments produced
        import outline
        from revise_lib import Ctx
        SP = sys.argv[1]; full = outline.build_stock(Ctx(SP).snap)
        plans = {**{k: v["plan"]["meals"] for k, v in json.load(open(SP + "/matrix.json")).items()}}
        for k, meals in plans.items():
            a = allocate(meals, full)
            print(f"{k:42} meals {len(meals):>2}  claims {sum(a['claims'].values()):>3}  short {sum(len(v) for v in a['short'].values())}  unknown refs {len(a['unknown'])}")
    else:
        unittest.main()
