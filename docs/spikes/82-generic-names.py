#!/usr/bin/env python3
"""Spike #82: generic product names ("Kellogg's Bran Flakes 200 g" -> "Bran flakes") with gpt-4o on Foundry.

Usage (products come from deploy/verify/cosmos.sh, read-only; keep inputs and outputs outside the repo):
  python3 82-generic-names.py run <products.json> <out-dir>      # every variant below; responses cached in <out-dir>
  python3 82-generic-names.py report <products.json> <out-dir>   # tables and consistency figures from the cache

The run uses a SAMPLE of the products (every product in the groups below, the loose and hand-named ones, a few awkward
ones, then others up to SAMPLE_SIZE), one call at a time, kept under TOKENS_PER_MINUTE: the live gpt-4o deployment is
shared with photo recognition and recipes (10k tokens a minute), and the spike must not starve them. Results are
saved after every call, so a stopped run resumes where it left off.

Variants, each over every product:
  A-free-1, A-free-2     level A (shopper level), no list of existing names, two runs
  B-free-1               level B (plain level), one run
  A-reuse-1, A-reuse-2   level A, products named one after another, each call told the names given so far and asked to
                         reuse one when it fits (run 2 in reverse order, to see if the order matters)
  B-reuse-1              level B, the same (one run)
  A-min-1                level A with the name and brand only (no size, categories or aisle): does the extra input help?

Environment (never printed or written):
  AZURE_AI_ENDPOINT  https://<account>.cognitiveservices.azure.com/
  AZURE_AI_TOKEN     an Entra token for https://cognitiveservices.azure.com (az account get-access-token); no keys
Standard library only. Nothing is written anywhere except <out-dir>.
"""
import json
import os
import sys
import time
import urllib.error
import urllib.request

API = "2024-10-21"
DEPLOYMENT = "gpt-4o"

COMMON = (
    "You give a product in a UK household's food cupboard a short generic name for their shopping list: what you'd say "
    "to someone going to the shop, so the same kind of thing always gets the same name whatever the brand. Rules: "
    "English, UK spelling, even if the product name is in another language. Sentence case (first word capitalised, "
    "the rest lower case except proper nouns such as Dijon, Parmigiano Reggiano, Weetabix). No brand, shop, range "
    "(Deluxe, Taste of, Finest), size, weight, count, pack format (bag, tin, jar, pot, multipack) or marketing words "
    "(classic, original, authentic, rich, British). Plural for things you count (Apples, Eggs, Carrots, Udon noodles); "
    "singular for things you don't (Milk, Rice, Flour). Keep a brand only when it IS what people call the product and "
    "there's no everyday generic word for it (e.g. Weetabix, Marmite). isFood: false for things that aren't food or "
    "drink (kitchen roll, soap). "
)

LEVELS = {
    "A": COMMON
    + "Level: SHOPPER. Keep the descriptors that change what you'd pick up in the shop: the variety or type (semi-"
    "skimmed, mature, wholewheat, smoked, caster, Greek, decaf, ground vs beans, chopped vs plum tomatoes, beef vs "
    "vegetable stock), and what the dish is for ready-made food (Tikka masala paste, Macaroni cheese). Drop flavour "
    "and variety detail only when any of that kind would do. Examples: 'Kellogg's Bran Flakes 500 g' -> 'Bran flakes'; "
    "'Cathedral City Mature Cheddar 350g' -> 'Mature cheddar'; 'Tesco Semi Skimmed Milk 4 pints' -> 'Semi-skimmed "
    "milk'; 'Heinz Baked Beanz' -> 'Baked beans'; 'Batts Smoked Paprika 40g' -> 'Smoked paprika'.",
    "B": COMMON
    + "Level: PLAIN. Only the basic food, as short as possible, dropping variety and descriptors unless the thing would "
    "be a different food without them (keep 'Coconut milk', 'Soy sauce', 'Peanut butter', 'Stock cubes'). Examples: "
    "'Kellogg's Bran Flakes 500 g' -> 'Cereal' is TOO generic, use 'Bran flakes'; 'Cathedral City Mature Cheddar' -> "
    "'Cheddar'; 'Tesco Semi Skimmed Milk' -> 'Milk'; 'Batts Smoked Paprika' -> 'Paprika'; 'Caster sugar' -> 'Sugar'.",
}

REUSE = (
    " Names already used for other products are listed under 'Existing names'. If one of them names the same kind of "
    "product AT THIS LEVEL, reuse it exactly (same spelling and case) and set reusedExisting to true. Only invent a new "
    "name when none fits; never stretch an existing name to cover something different."
)

SCHEMA = {
    "name": "generic_name",
    "strict": True,
    "schema": {
        "type": "object",
        "additionalProperties": False,
        "required": ["genericName", "reusedExisting", "isFood"],
        "properties": {
            "genericName": {"type": "string"},
            "reusedExisting": {"type": "boolean"},
            "isFood": {"type": "boolean"},
        },
    },
}

VARIANTS = ["A-reuse-1", "B-reuse-1", "A-free-1", "B-free-1", "A-reuse-2", "A-free-2", "A-min-1"]
SAMPLE_SIZE = 50
TOKENS_PER_MINUTE = 6000
AWKWARD = ["Viennetta Vanille", "Starburst Original FRUIT CHEWS BURSTING WITH FRUIT", "PG Tips 210 Tea Bags",
           "Effervescent Vitamin C - Orange Flavour", "fillet", "Mocktail Pina Colada", "Mais groß", "Oignon jaune",
           "Abacaxi em Caldas Lidl", "Temato ketchup Ingredients 72% temato pures single", "Sweet &quot",
           "Dairy Milk Fruit & Nut", "12 Medium Eggs", "Pasta 'N' Sauce Cheese & Broccoli", "SEMI-SKIMMED BRITISH MILK",
           "Special Flakes Bran Flakes", "Parmigiano Reggiano PDO", "Hot Honey", "Quorn Pieces"]

# Products that are the same kind of thing at level A: they should get one name. (Matched on display name.)
SAME_AT_A = {
    "Udon noodles": ["Udon Noodles", "UDON NOODLES"],
    "Garden peas": ["British Garden Peas", "garden peas"],
    "Coconut milk": ["COCONUT MILK CLASSIC", "Coconut milk"],
    "Cumin seeds": ["Cumin Seeds (Asda)", "Kreuzkümmel"],
    "Chopped tomatoes": ["Chopped Tomatoes in a Rich Tomato Juice", "Chopped Tomatoes"],
    "Greek yogurt": ["Creamy Greek Style Natural Yogurt", "Greek 10% fat authentic yogurt"],
    "Taco mixed beans": ["Taco Mixed Beans", "Taco Mixed Beans in a Tomato Sauce"],
    "Pizza subs": ["2 Pizza Subs", "2 Pizza Subs Cheese & Tomato"],
    "Gherkins": ["Sliced Gherkins", "Pickled Gherkins"],
}
# Families that should collapse further at level B (shown for Gary; not scored as errors at level A).
FAMILIES = {
    "Sugars": ["White Sugar", "Caster Sugar", "Icing Sugar", "Demerara natural unrefined cane sugar",
               "light brown muscovado cane sugar"],
    "Coffee": ["Deluxe Roast and Ground Coffee", "Colombian Supremo Whole Coffee Beans", "Decaf Blend",
               "Rich Italian Coffee Bags"],
    "Paprika": ["Paprika", "Smoked Paprika", "Smoked Sweet Paprika"],
    "Microwave rice": ["Microwave Basmati Rice", "Golden vegetable microwave rice", "Long Grain Microwave Rice"],
    "Instant noodles": ["Speedy Noodle Beef & Tomato Flavour", "NEWGATE SPEEDY noodle CURRY FLAVOUR",
                        "Classic Soy Flavour Ramen Noodles"],
    "Mustard": ["English Mustard", "Dijon Mustard", "горчица"],
    "Stock cubes": ["Beef stock cubes", "Vegetable Stock Cubes"],
    "Muesli and granola": ["Swiss style museli", "Fruit & Nut Muesli", "Raisin and Almond Granola"],
}


def describe(p, minimal=False):
    parts = [f"Product name: {p['displayName']}"]
    if p.get("brand"):
        parts.append(f"Brand: {p['brand']}")
    if not minimal:
        if p.get("size"):
            parts.append(f"Size: {p['size']}")
        tags = [t.split(":", 1)[-1] for t in (p.get("categoryTags") or []) if t.startswith("en:")]
        if tags:
            parts.append("Categories: " + ", ".join(tags[-4:]))  # the most specific few
        if p.get("aisle"):
            parts.append(f"Aisle: {p['aisle']}")
    return "\n".join(parts)


def call(system, user):
    body = json.dumps({
        "messages": [{"role": "system", "content": system}, {"role": "user", "content": user}],
        "response_format": {"type": "json_schema", "json_schema": SCHEMA},
        "temperature": 0,
    }).encode()
    url = f"{os.environ['AZURE_AI_ENDPOINT'].rstrip('/')}/openai/deployments/{DEPLOYMENT}/chat/completions?api-version={API}"
    for attempt in range(5):
        req = urllib.request.Request(url, body, {
            "Content-Type": "application/json", "Authorization": f"Bearer {os.environ['AZURE_AI_TOKEN']}"})
        started = time.time()
        try:
            with urllib.request.urlopen(req, timeout=60) as resp:
                raw = json.load(resp)
            raw["_ms"] = int((time.time() - started) * 1000)
            return raw
        except urllib.error.HTTPError as e:
            if e.code in (429, 500, 502, 503, 504) and attempt < 4:
                time.sleep(2 ** attempt * 2)
                continue
            raise SystemExit(f"HTTP {e.code} from the model")
    raise SystemExit("gave up")


def parse(raw):
    return json.loads(raw["choices"][0]["message"]["content"])


def sample(products):
    by_name = {p["displayName"]: p for p in products}
    wanted = [m for group in {**SAME_AT_A, **FAMILIES}.values() for m in group] + AWKWARD
    picked = [by_name[m] for m in dict.fromkeys(wanted) if m in by_name]
    picked += [p for p in products if not p.get("brand") and p not in picked]  # loose and hand-named items
    rest = sorted((p for p in products if p not in picked), key=lambda p: p["id"])  # stable, arbitrary
    return (picked + rest)[:SAMPLE_SIZE] if len(picked) < SAMPLE_SIZE else picked


class Budget:
    """Keeps the run under TOKENS_PER_MINUTE (and the deployment's 10 requests per 10 s)."""

    def __init__(self):
        self.window = []

    def wait(self):
        while True:
            now = time.time()
            self.window = [(t, n) for t, n in self.window if now - t < 60]
            if sum(n for _, n in self.window) < TOKENS_PER_MINUTE and sum(1 for t, _ in self.window if now - t < 10) < 6:
                return
            time.sleep(1)

    def spent(self, tokens):
        self.window.append((time.time(), tokens))


def run(products, out):
    os.makedirs(out, exist_ok=True)
    chosen = sample(products)
    json.dump([p["id"] for p in chosen], open(os.path.join(out, "sample.json"), "w"))
    budget = Budget()
    for variant in VARIANTS:
        level, kind = variant[0], variant.split("-")[1]
        path = os.path.join(out, f"{variant}.json")
        results = json.load(open(path)) if os.path.exists(path) else {}
        order = list(reversed(chosen)) if variant == "A-reuse-2" else chosen
        for p in order:
            if p["id"] in results:
                continue
            system, user = LEVELS[level], describe(p, minimal=kind == "min")
            if kind == "reuse":
                names = sorted({parse(results[q["id"]])["genericName"].strip() for q in order if q["id"] in results})
                system += REUSE
                user = "Existing names: " + (", ".join(names) if names else "(none yet)") + "\n\n" + user
            budget.wait()
            raw = call(system, user)
            budget.spent(raw["usage"]["total_tokens"])
            results[p["id"]] = raw
            json.dump(results, open(path, "w"), ensure_ascii=False)
        print(f"{variant}: done ({len(results)})", flush=True)


def report(products, out):
    ids = set(json.load(open(os.path.join(out, "sample.json"))))
    products = [p for p in products if p["id"] in ids]
    data = {v: {k: parse(r) for k, r in json.load(open(os.path.join(out, f"{v}.json"))).items()} for v in VARIANTS}
    raw = {v: json.load(open(os.path.join(out, f"{v}.json"))) for v in VARIANTS}
    by_name = {p["displayName"]: p["id"] for p in products}
    name = lambda v, pid: data[v][pid]["genericName"].strip()
    n = len(products)

    print(f"## Figures ({n} products)\n")
    print("| Variant | Distinct names | Same as run 2 | Reused an existing name |")
    print("|---|---|---|---|")
    for base in ["A-free", "A-reuse"]:
        v1, v2 = f"{base}-1", f"{base}-2"
        same = sum(name(v1, p["id"]) == name(v2, p["id"]) for p in products)
        reused = sum(1 for p in products if data[v1][p["id"]]["reusedExisting"]) if "reuse" in base else "–"
        print(f"| {base} | {len({name(v1, p['id']) for p in products})} | {same}/{n} ({same * 100 // n}%) | {reused} |")
    same_min = sum(name("A-min-1", p["id"]) == name("A-free-1", p["id"]) for p in products)
    print(f"\nA with name and brand only vs full input: {same_min}/{n} the same.\n")

    print("## Same kind of thing at level A (should be one name)\n")
    print("| Group | A-free-1 | A-reuse-1 | A-reuse-2 | B-reuse-1 |")
    print("|---|---|---|---|---|")
    for group, members in {**SAME_AT_A, **FAMILIES}.items():
        ids = [by_name[m] for m in members if m in by_name]
        if not ids:
            continue
        cells = []
        for v in ["A-free-1", "A-reuse-1", "A-reuse-2", "B-reuse-1"]:
            names = sorted({name(v, i) for i in ids})
            cells.append(("✅ " if len(names) == 1 else "❌ " if group in SAME_AT_A else "") + " / ".join(names))
        print(f"| {group}{'' if group in SAME_AT_A else ' (family)'} | " + " | ".join(cells) + " |")

    print("\n## Every product\n")
    print("| Product (brand) | Level A | Level B | A stable? | Not food |")
    print("|---|---|---|---|---|")
    for p in sorted(products, key=lambda p: name("A-reuse-1", p["id"]).lower()):
        a = {name(v, p["id"]) for v in ["A-free-1", "A-free-2", "A-reuse-1", "A-reuse-2"]}
        brand = f" ({p['brand']})" if p.get("brand") else ""
        print(f"| {p['displayName']}{brand} | {name('A-reuse-1', p['id'])} | {name('B-reuse-1', p['id'])} | "
              f"{'yes' if len(a) == 1 else ' / '.join(sorted(a))} | {'' if data['A-reuse-1'][p['id']]['isFood'] else 'yes'} |")

    usage = [r["usage"] for v in VARIANTS for r in raw[v].values()]
    ms = sorted(r["_ms"] for v in VARIANTS for r in raw[v].values())
    free_usage = [r["usage"] for r in raw["A-free-1"].values()]
    reuse_usage = [r["usage"] for r in raw["A-reuse-1"].values()]
    avg = lambda xs, k: sum(x[k] for x in xs) / len(xs)
    print("\n## Cost and latency\n")
    print(f"- Calls: {len(usage)}; median latency {ms[len(ms) // 2]} ms, 95th percentile {ms[int(len(ms) * 0.95)]} ms.")
    print(f"- Free: {avg(free_usage, 'prompt_tokens'):.0f} prompt + {avg(free_usage, 'completion_tokens'):.0f} completion tokens per product.")
    print(f"- Reuse (list grows to {n}): {avg(reuse_usage, 'prompt_tokens'):.0f} prompt + {avg(reuse_usage, 'completion_tokens'):.0f} completion tokens per product on average.")


if __name__ == "__main__":
    if len(sys.argv) != 4 or sys.argv[1] not in ("run", "report"):
        sys.exit(__doc__)
    products = json.load(open(sys.argv[2]))
    (run if sys.argv[1] == "run" else report)(products, sys.argv[3])
