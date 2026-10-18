#!/usr/bin/env python3
"""Spike #60: pick an Iconify icon for each product, four ways, and compare.

Usage:
  set -a; . ./local.env; set +a; python3 docs/spikes/60-iconify-icons.py <products.json> <out-dir>

products.json: a list of {displayName, brand, size, categoryTags, storage, barcodes, sample?} (exported read-only
with deploy/verify/cosmos.sh). Standard library only. The Jev key comes from JEV_API_KEY and is never printed or
written. Only product names and category tags go to Iconify's public search API (from this machine). Nothing changes
live data. Writes <out-dir>/results.json.

Approaches:
  1 search   Search the set's icon names (the set's own data from npm, searched locally): the name, then the last
             category tag, then single words.
  2 search+jev   Candidates from those searches (up to 255), then Jev's choice over the icon names.
  3 curated+jev  Jev's choice over the set's own food category (fluent-emoji-flat "Food & Drink", 136 icons;
                 icon-park-outline "Foods", 121).
  4 aisle   Jev's choice of a draft aisle (from card #71), each with one icon: the floor when nothing fits.
"""
import json
import os
import re
import sys
import time
import urllib.error
import urllib.parse
import urllib.request

ICONIFY = "https://api.iconify.design"
JEV = "https://api.typesafe.ai/v1/systemone"
MODEL = "jev-1.13.0"
SETS = ["fluent-emoji-flat", "noto", "icon-park-outline", "tabler"]
JEV_SETS = ["fluent-emoji-flat", "icon-park-outline"]  # approaches 2 and 3
FOOD_CATEGORY = {"fluent-emoji-flat": "Food & Drink", "icon-park-outline": "Foods"}

# Card #71's draft aisles, with a Fluent icon each (approach 4).
AISLES = {
    "fruitVeg": ("Fruit & veg", "leafy-green"),
    "bakery": ("Bakery", "bread"),
    "meatFish": ("Meat & fish", "cut-of-meat"),
    "dairyEggs": ("Dairy & eggs", "cheese-wedge"),
    "chilledDeli": ("Chilled & deli (ready meals, dips, cooked meats)", "sandwich"),
    "frozen": ("Frozen", "ice"),
    "pastaRiceGrains": ("Pasta, rice & grains", "cooked-rice"),
    "tinsJars": ("Tins & jars", "canned-food"),
    "saucesOils": ("Sauces, oils & condiments", "salt"),
    "bakingSpices": ("Baking & spices", "hot-pepper"),
    "breakfastCereal": ("Breakfast & cereal", "bowl-with-spoon"),
    "snacksSweets": ("Snacks & sweets", "chocolate-bar"),
    "drinks": ("Drinks", "hot-beverage"),
    "other": ("Other", "shopping-cart"),
}

STOP = {"and", "with", "in", "a", "of", "the", "flavour", "flavoured", "style", "mini", "original", "rich", "plain",
        "authentic", "british", "whole", "pdo", "less", "mild", "hot", "spicy", "free", "range", "fat"}


CACHE = {}
CACHE_FILE = None
PAUSE = 0.6  # seconds between Iconify requests: its public API rate-limits bursts (HTTP 429)


def get(url, attempts=6):
    """A cached, paced GET. Iconify's public API answers 429 to bursts: back off and retry, and stop loudly rather
    than carry on with empty results (which would look like poor accuracy)."""
    if url in CACHE:
        return CACHE[url]
    for attempt in range(attempts):
        time.sleep(PAUSE)
        try:
            with urllib.request.urlopen(url, timeout=20) as r:
                CACHE[url] = json.loads(r.read())
                if CACHE_FILE and "icons.json" not in url:
                    json.dump(CACHE, open(CACHE_FILE, "w"))
                return CACHE[url]
        except urllib.error.HTTPError as e:
            if e.code != 429 and e.code < 500:
                raise
            wait = int(e.headers.get("Retry-After") or 0) or 30 * (attempt + 1)
            print(f"  Iconify {e.code}: waiting {wait}s", flush=True)
            time.sleep(wait)
        except (urllib.error.URLError, TimeoutError):
            time.sleep(5 * (attempt + 1))
    sys.exit(f"Iconify kept failing for {url}")


SET_DATA = {}


def set_data(prefix):
    """The whole set's data from npm (@iconify-json/<prefix>), as a bundled build would use: icon names, aliases and
    categories. Searching it here avoids Iconify's public search API, which blocked bursts for ~30 minutes (HTTP 429)."""
    if prefix not in SET_DATA:
        SET_DATA[prefix] = get(f"https://cdn.jsdelivr.net/npm/@iconify-json/{prefix}/icons.json")
    return SET_DATA[prefix]


def singular(word):
    if word.endswith("ies"):
        return word[:-3] + "y"
    if word.endswith(("oes", "ches", "shes")):
        return word[:-2]
    return word[:-1] if word.endswith("s") and not word.endswith("ss") else word


def search(query, prefix, limit=32):
    """Local search over the set's icon names (and aliases), like Iconify's: icons whose name contains every query word
    (singular or plural) rank first, then those with the most query words; shorter names first."""
    data = set_data(prefix)
    names = list(data.get("icons", {})) + list(data.get("aliases", {}))
    terms = {singular(w) for w in re.findall(r"[a-z]+", query.lower()) if len(w) > 2}
    if not terms:
        return []
    scored = []
    for name in names:
        tokens = {singular(t) for t in name.split("-")}
        hits = len(terms & tokens)
        if hits:
            scored.append((-(hits == len(terms)), -hits, len(tokens), name))
    scored.sort()
    return [f"{prefix}:{name}" for *_, name in scored[:limit]]


def words(product):
    text = product["displayName"].lower()
    for noise in [(product.get("brand") or "").lower(), (product.get("size") or "").lower()]:
        if noise:
            text = text.replace(noise, " ")
    found = [w for w in re.findall(r"[a-z]+", text) if len(w) > 2 and w not in STOP]
    return found


def queries(product):
    """Search terms, best first: the cleaned name, the most specific category tag, then single words (longest first)."""
    name = " ".join(words(product))
    tags = [t.split(":", 1)[-1].replace("-", " ") for t in (product.get("categoryTags") or [])]
    out = [name] if name else []
    if tags:
        out.append(tags[-1])
    out += sorted(set(words(product)), key=len, reverse=True)
    seen, result = set(), []
    for q in out:
        if q and q not in seen:
            seen.add(q)
            result.append(q)
    return result


def approach1(product, prefix):
    for q in queries(product):
        hits = search(q, prefix, limit=40)
        if hits:
            return {"icon": hits[0], "query": q}
    return {"icon": None, "query": None}


def candidates(product, prefix):
    found = []
    for q in queries(product):
        for icon in search(q, prefix, limit=40):
            if icon not in found:
                found.append(icon)
    return found[:255]


def jev(state, question):
    key = os.environ.get("JEV_API_KEY")
    if not key:
        sys.exit("Set JEV_API_KEY in the environment.")
    body = json.dumps({"state": state, "model": MODEL, "questions": {"q": question}}).encode()
    req = urllib.request.Request(JEV, data=body, method="POST",
                                 headers={"Authorization": "Bearer " + key, "Content-Type": "application/json"})
    for attempt in range(4):
        try:
            with urllib.request.urlopen(req, timeout=30) as r:
                answer = json.loads(r.read())["answers"]["q"]
                return answer.get("choice"), answer.get("confidence")
        except urllib.error.HTTPError as e:
            if e.code not in (429, 529) and e.code < 500:
                return None, f"HTTP {e.code}"
        except (urllib.error.URLError, TimeoutError):
            pass
        time.sleep(2 * 2 ** attempt)
    return None, "unavailable"


def state(product):
    s = {"name": product["displayName"]}
    for key, field in [("brand", "brand"), ("size", "size"), ("categories", "categoryTags"), ("storage", "storage")]:
        if product.get(field):
            s[key] = product[field]
    return s


def describe(icon):
    return icon.split(":", 1)[-1].replace("-", " ")


def jev_pick(product, icons, instructions):
    if not icons:
        return {"icon": None, "confidence": None}
    criteria = {icon.split(":", 1)[-1]: describe(icon) for icon in icons}
    choice, confidence = jev(state(product), {"type": "choice", "instructions": instructions, "criteria": criteria})
    prefix = icons[0].split(":", 1)[0]
    return {"icon": f"{prefix}:{choice}" if choice else None, "confidence": confidence, "options": len(criteria)}


def food_icons(prefix):
    """The set's own food category (from the package's metadata.json): the curated list for approach 3."""
    meta = get(f"https://cdn.jsdelivr.net/npm/@iconify-json/{prefix}/metadata.json")
    return [f"{prefix}:{name}" for name in meta.get("categories", {}).get(FOOD_CATEGORY[prefix], [])]


PICK = ("Pick the icon that best shows what this food or drink product is, so someone can recognise it at a glance "
        "in a list of kitchen stock. Choose the closest kind of food, not the brand.")


def main():
    products = json.load(open(sys.argv[1]))
    out_dir = sys.argv[2]
    os.makedirs(out_dir, exist_ok=True)
    global CACHE_FILE
    CACHE_FILE = os.path.join(out_dir, "iconify-cache.json")
    if os.path.exists(CACHE_FILE):
        CACHE.update(json.load(open(CACHE_FILE)))
    curated = {prefix: food_icons(prefix) for prefix in JEV_SETS}
    print("curated sizes:", {k: len(v) for k, v in curated.items()}, flush=True)
    aisle_criteria = {k: v[0] for k, v in AISLES.items()}
    results = []
    for i, product in enumerate(products):
        row = {"product": product["displayName"], "brand": product.get("brand"), "sample": product.get("sample", False),
               "loose": not product.get("barcodes")}
        row["a1"] = {prefix: approach1(product, prefix) for prefix in SETS}
        row["a2"] = {prefix: jev_pick(product, candidates(product, prefix), PICK) for prefix in JEV_SETS}
        row["a3"] = {prefix: jev_pick(product, curated[prefix], PICK) for prefix in JEV_SETS}
        aisle, conf = jev(state(product), {
            "type": "choice", "criteria": aisle_criteria,
            "instructions": "Which supermarket aisle this product would be found in."})
        row["a4"] = {"aisle": aisle, "icon": f"fluent-emoji-flat:{AISLES[aisle][1]}" if aisle in AISLES else None,
                     "confidence": conf}
        results.append(row)
        print(f"{i + 1:2}/{len(products)} {product['displayName'][:30]:30} a1={row['a1']['fluent-emoji-flat']['icon']} "
              f"a2={row['a2']['fluent-emoji-flat']['icon']} a3={row['a3']['fluent-emoji-flat']['icon']} a4={aisle}", flush=True)
    json.dump({"curated": curated, "aisles": AISLES, "results": results}, open(os.path.join(out_dir, "results.json"), "w"),
              indent=1)


if __name__ == "__main__":
    main()
