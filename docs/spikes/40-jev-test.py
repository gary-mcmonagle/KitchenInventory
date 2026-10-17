#!/usr/bin/env python3
"""Spike #40: try Jev (TypeSafe System One) for shelf life and product decisions.

Usage:
  JEV_API_KEY=... python3 40-jev-test.py <shelf-life|product|order-check> [output-dir]

Reads the key from JEV_API_KEY (or TYPESAFE_API_KEY). Never prints or writes it.
Standard library only. Writes each raw response to output-dir (default: ./jev-results).
"""
import json
import os
import sys
import time
import urllib.error
import urllib.request

URL = "https://api.typesafe.ai/v1/systemone"
MODEL = "jev-1.13.0"  # pinned, so results aren't moved by the jev-latest alias

# Ordered shelf-life levels (a Score rubric; Jev allows up to 10 levels).
SHELF_LIFE_LEVELS = [
    ("1-2 days", 2),
    ("3-4 days", 4),
    ("About a week", 7),
    ("About 2 weeks", 14),
    ("About a month", 30),
    ("About 3 months", 90),
    ("About 6 months", 180),
    ("About a year", 365),
    ("About 2 years", 730),
    ("3 years or more", 1095),
]

SHELF_LIFE_QUESTIONS = {
    "shelf_life": {
        "type": "score",
        "instructions": "How long this product usually stays good to eat after it is bought, "
                        "unopened and stored the usual way for this kind of product.",
        "criteria": [label for label, _ in SHELF_LIFE_LEVELS],
    },
    "storage": {
        "type": "choice",
        "instructions": "Where this product is normally stored at home before it is opened.",
        "criteria": {
            "cupboard": "Cupboard or worktop, at room temperature",
            "fridge": "Fridge",
            "freezer": "Freezer",
        },
    },
}

# Real household products (spike #28 barcodes) plus typical fridge, freezer and fresh items.
# group: what we expect, for the results table.
PRODUCTS = [
    ("pantry", {"name": "Beef Gravy", "brand": "Bisto", "size": "230g", "categories": ["en:gravy"]}),
    ("pantry", {"name": "Lasagna sheets", "brand": "Napolina", "size": "375 g", "categories": ["en:lasagne"]}),
    ("pantry", {"name": "Chickpeas in Water", "brand": "Freshona", "size": "400g", "categories": ["en:tinned-beans"]}),
    ("pantry", {"name": "Garlic flavoured olive oil", "brand": "Filippo Berio", "size": "250 ml", "categories": ["en:flavoured-olive-oils"]}),
    ("pantry", {"name": "Weetabix Chocolate", "brand": "Weetabix", "categories": ["en:breakfast-cereals"]}),
    ("pantry", {"name": "Crunchy Peanut Butter", "brand": "Maribel", "size": "1 kg", "categories": ["en:crunchy-peanut-butters"]}),
    ("pantry", {"name": "Salt & Pepper Chicken Flavour Potato Crisps", "brand": "Walkers"}),
    ("pantry", {"name": "Polish Honey multiflower", "brand": "Mazurskie Miody", "size": "900 g", "categories": ["en:flower-honeys"]}),
    ("fridge", {"name": "SEMI-SKIMMED BRITISH MILK", "brand": "Dairy Manor"}),
    ("fridge", {"name": "Greek 10% fat authentic yogurt", "brand": "Brooklea", "size": "500g", "categories": ["en:plain-cow-milk-yogurts"]}),
    ("fridge", {"name": "Mozzarella", "brand": "Lidl", "size": "125 g", "categories": ["en:mozzarella"]}),
    ("fridge", {"name": "British Chicken Breast Fillets", "brand": "Tesco", "size": "650 g", "categories": ["en:chicken-breasts"]}),
    ("freezer", {"name": "Garden Peas", "brand": "Birds Eye", "size": "800 g", "categories": ["en:frozen-peas"]}),
    ("freezer", {"name": "Fish Fingers", "brand": "Birds Eye", "size": "10 pack", "categories": ["en:frozen-fish-fingers"]}),
    ("fresh", {"name": "Bananas", "brand": None, "size": "5 pack", "categories": ["en:bananas"]}),
    ("fresh", {"name": "Easy Peelers", "brand": "Deluxe", "categories": ["en:clementines"]}),
    ("fresh", {"name": "PLAIN NAAN BREADS", "brand": "Taste of", "size": "2 x 130 g", "categories": ["en:naans"]}),
    ("fresh", {"name": "White Sourdough Loaf", "brand": "Lidl Bakery", "size": "400 g", "categories": ["en:sourdough-breads"]}),
]

# Same products with the storage stated, to see whether storage changes the answer.
STORAGE_VARIANTS = [
    ("storage: freezer", {"name": "White Sourdough Loaf", "brand": "Lidl Bakery", "size": "400 g", "storage": "freezer"}),
    ("storage: fridge", {"name": "Mozzarella", "brand": "Lidl", "size": "125 g", "storage": "fridge"}),
    ("storage: freezer", {"name": "British Chicken Breast Fillets", "brand": "Tesco", "size": "650 g", "storage": "freezer"}),
    ("storage: freezer", {"name": "SEMI-SKIMMED BRITISH MILK", "brand": "Dairy Manor", "storage": "freezer"}),
]

# Hand-written OCR text, the way Document Intelligence's Read model returns lines.
OCR_SAMPLES = [
    ("clean", "Heinz Baked Beans", [
        "Heinz", "Beanz", "Baked Beans in a rich tomato sauce", "415g", "1 of your 5 a day",
        "Suitable for vegetarians", "Best before end: see top of can",
    ]),
    ("noisy", "Lidl Freshona chickpeas (OCR errors)", [
        "FRESH0NA", "Ch1ckpeas", "in water", "400g e", "Drained weight 240g", "lngredients: Chickpeas, Water, Salt",
        "Store in a cool dry place", "LIDL GB",
    ]),
    ("multi-language", "Lidl mustard (German/Bulgarian/English)", [
        "taste of DEUTSCHLAND", "Mittelscharfer Senf", "Medium hot mustard", "Горчица средно люта",
        "250 ml", "Zutaten: Wasser, Senfsaat, Branntweinessig", "Ingredients: water, mustard seeds",
    ]),
    ("deli label", "Deli counter label", [
        "LIDL DELI", "PACKED 16/10/26", "USE BY 19/10/26", "Wafer Thin Honey Roast Ham", "NET 0.212kg",
        "£/kg 8.49", "PRICE £1.80", "Keep refrigerated 0-5C",
    ]),
    ("not food", "Washing-up liquid", [
        "Fairy", "Original", "Washing Up Liquid", "433ml", "Keep out of reach of children", "Causes serious eye irritation",
    ]),
]


def call(state, questions):
    key = os.environ.get("JEV_API_KEY") or os.environ.get("TYPESAFE_API_KEY")
    if not key:
        sys.exit("Set JEV_API_KEY in the environment.")
    body = json.dumps({"state": state, "model": MODEL, "questions": questions}).encode()
    req = urllib.request.Request(URL, data=body, method="POST", headers={
        "Authorization": "Bearer " + key, "Content-Type": "application/json",
    })
    for attempt in range(4):
        start = time.monotonic()
        try:
            with urllib.request.urlopen(req, timeout=30) as resp:
                status, raw = resp.status, resp.read()
        except urllib.error.HTTPError as e:
            status, raw = e.code, e.read()
        except (urllib.error.URLError, TimeoutError) as e:
            status, raw = 0, str(e).encode()
        ms = int((time.monotonic() - start) * 1000)
        if status in (429, 529) or status >= 500 or status == 0:
            time.sleep(2 * 2 ** attempt)
            continue
        break
    try:
        data = json.loads(raw)
    except ValueError:
        data = {"_raw": raw.decode("utf-8", "replace")[:500]}
    return status, ms, data


def save(out_dir, name, payload):
    os.makedirs(out_dir, exist_ok=True)
    with open(os.path.join(out_dir, name + ".json"), "w") as f:
        json.dump(payload, f, indent=2, ensure_ascii=False)


def shelf_life(out_dir):
    print("group\tproduct\thttp\tms\tlevel\tdays\tconfidence\tstorage\tstorage_p")
    for i, (group, product) in enumerate(PRODUCTS + STORAGE_VARIANTS):
        status, ms, data = call(product, SHELF_LIFE_QUESTIONS)
        save(out_dir, f"shelf-{i:02d}", {"product": product, "http": status, "ms": ms, "response": data})
        a = data.get("answers", {})
        sl, st = a.get("shelf_life", {}), a.get("storage", {})
        level = round(sl.get("score", -1)) if "score" in sl else -1
        label, days = SHELF_LIFE_LEVELS[level] if 0 <= level < len(SHELF_LIFE_LEVELS) else ("?", "?")
        print("\t".join(str(x) for x in [
            group, product["name"], status, ms, label, days, sl.get("confidence", ""),
            st.get("choice", ""), (st.get("probabilities") or {}).get(st.get("choice", ""), ""),
        ]))


PROMPT_VERSION = os.environ.get("PROMPT_VERSION", "v2")


def product_questions(lines, reverse=False):
    labels = [f"L{i + 1}" for i in range(len(lines))]
    options = {lab: line for lab, line in zip(labels, lines)}
    options["none"] = "None of these lines"
    if reverse:
        options = dict(reversed(list(options.items())))
    return {
        "name": {"type": "choice",
                 "instructions": "Which line is the product's name: what the product is, as you would write it on a "
                                 "shopping list. Not the brand, the size, ingredients, or storage advice."
                                 + (" If the name is printed in several languages, choose the English line."
                                    if PROMPT_VERSION == "v2" else ""),
                 "criteria": options},
        "brand": {"type": "choice",
                  "instructions": "Which line is the brand or manufacturer name only."
                                  + (" The brand is the product's own brand printed on the front of the pack. "
                                     "A shop's or retailer's name, address or country line is not the brand."
                                     if PROMPT_VERSION == "v2" else ""),
                  "criteria": options},
        "size": {"type": "choice",
                 "instructions": "Which line gives the pack size, weight or volume of the product.",
                 "criteria": options},
        "is_food": {"type": "noul",
                    "instructions": "This text is from the packaging of a food or drink product."},
        "readable": {"type": "noul",
                     "instructions": "The text is clear enough to tell what the product is."},
    }


def product(out_dir, reverse=False):
    print("sample\tdescription\thttp\tms\tname\tname_conf\tbrand\tbrand_conf\tsize\tsize_conf\tis_food\treadable")
    for i, (kind, desc, lines) in enumerate(OCR_SAMPLES):
        state = {"ocrLines": lines}
        status, ms, data = call(state, product_questions(lines, reverse))
        save(out_dir, f"product-{PROMPT_VERSION}{'-rev' if reverse else ''}-{i:02d}",
             {"lines": lines, "http": status, "ms": ms, "response": data})
        a = data.get("answers", {})

        def pick(q):
            c = a.get(q, {}).get("choice", "")
            text = lines[int(c[1:]) - 1] if c.startswith("L") else c
            return text, round(a.get(q, {}).get("confidence", 0), 2)

        n, b, s = pick("name"), pick("brand"), pick("size")
        print("\t".join(str(x) for x in [
            kind, desc, status, ms, n[0], n[1], b[0], b[1], s[0], s[1],
            round(a.get("is_food", {}).get("noul", -1), 2), round(a.get("readable", {}).get("noul", -1), 2),
        ]))


if __name__ == "__main__":
    mode = sys.argv[1] if len(sys.argv) > 1 else "shelf-life"
    out = sys.argv[2] if len(sys.argv) > 2 else "jev-results"
    if mode == "shelf-life":
        shelf_life(out)
    elif mode == "product":
        product(out)
    elif mode == "order-check":
        product(out, reverse=True)
    else:
        sys.exit("mode: shelf-life | product | order-check")
