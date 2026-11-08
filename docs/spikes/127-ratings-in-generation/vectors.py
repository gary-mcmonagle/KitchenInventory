#!/usr/bin/env python3
"""Spike #127: using ratings in generation (vector search, embeddings, taste profile). MADE-UP DATA ONLY.

Usage (keep <out-dir> outside the repo; every step caches its results there, so a stopped run resumes):
  python3 vectors.py gen    <out-dir>            # ~300 made-up ratings for two people
  python3 vectors.py embed  <out-dir>            # embed ratings + test requests (256 and 512 dims)
  python3 vectors.py load   <out-dir>            # write the ratings into the spike-ratings-* containers
  python3 vectors.py search <out-dir>            # top-20 queries: RU, latency, relevance; and in-code compare
  python3 vectors.py unload <out-dir>            # delete the documents again (containers are deleted by hand)

Environment (never printed or written): none. Tokens come from your own `az login` (no keys anywhere); the Cosmos
account and the Foundry endpoint are the live ones from rg-cupboard-tracker. Standard library only.
Patterns planted in the data (so the spike can check they're found): Sam dislikes mushrooms (👎, reason ingredient:
mushrooms); both like one-pan dinners; a recipe over 60 minutes gets 👎 (reason: took too long); the rest are mostly 👍.
"""
import json
import math
import os
import random
import subprocess
import sys
import threading
from http.client import HTTPException, HTTPSConnection
import time
import urllib.error
import urllib.parse
import urllib.request
import uuid
from concurrent.futures import ThreadPoolExecutor
from datetime import datetime, timedelta, timezone

RG = "rg-cupboard-tracker"
COSMOS = "cosmos-cupboard-ugxum7"
DB = "cupboard"
AI = "ais-cupboard-ugxum7"
EMBED_DEPLOYMENT = "text-embedding-3-small"
CONTAINERS = {256: "spike-ratings-flat256", 512: "spike-ratings-qflat512"}  # + disk512 below
DISK = "spike-ratings-disk512"
PK = "household"
NOW = datetime(2026, 11, 1, tzinfo=timezone.utc)

PEOPLE = [("p-gary", "Gary"), ("p-sam", "Sam")]

# ---- made-up dishes -------------------------------------------------------------------------------------------------

PROTEINS = {
    "chicken": ("meat", ["chicken thighs", "chicken breast"]),
    "salmon": ("fish", ["salmon fillets"]),
    "sausage": ("meat", ["sausages"]),
    "chickpea": ("veg", ["chickpeas"]),
    "lentil": ("veg", ["red lentils"]),
    "beef": ("meat", ["beef mince"]),
    "egg": ("veg", ["eggs"]),
    "tofu": ("veg", ["tofu"]),
    "halloumi": ("veg", ["halloumi"]),
    "prawn": ("fish", ["prawns"]),
}
VEG = ["peppers", "courgettes", "spinach", "leeks", "carrots", "tomatoes", "sweetcorn", "broccoli", "onions", "potatoes", "mushrooms"]
STYLES_DINNER = [  # (style, one-pan, minutes)
    ("one-pan bake", True, 40), ("one-pan traybake", True, 45), ("one-pot stew", True, 55), ("slow-cooked casserole", False, 150),
    ("stir-fry", True, 20), ("curry", False, 50), ("pasta", False, 30), ("roast", False, 90), ("pie", False, 75),
    ("tacos", False, 30), ("risotto", True, 45), ("traybake with rice", True, 50), ("braise", False, 120),
]
BREAKFASTS = [("shakshuka", ["eggs", "tomatoes", "peppers"], 25, True), ("porridge with banana", ["oats", "bananas"], 10, False),
              ("omelette", ["eggs", "cheese"], 10, False), ("mushroom omelette", ["eggs", "mushrooms"], 15, False),
              ("overnight oats", ["oats", "yoghurt"], 5, False), ("pancakes", ["flour", "eggs", "milk"], 25, False),
              ("avocado toast", ["bread", "avocado", "eggs"], 10, False), ("full English", ["eggs", "sausages", "mushrooms", "tomatoes"], 30, False),
              ("granola bowl", ["granola", "yoghurt"], 5, False), ("spinach and feta frittata", ["eggs", "spinach", "feta"], 25, True)]
LUNCHES = [("tomato soup", ["tomatoes", "onions"], 30, True), ("mushroom soup", ["mushrooms", "onions"], 35, True), ("cheese toastie", ["bread", "cheese"], 10, False),
           ("chickpea salad", ["chickpeas", "cucumber", "tomatoes"], 15, False), ("tuna pasta salad", ["tuna", "pasta", "sweetcorn"], 20, False),
           ("falafel wraps", ["chickpeas", "wraps"], 25, False), ("jacket potato with beans", ["potatoes", "baked beans"], 60, False),
           ("quesadillas", ["wraps", "cheese", "peppers"], 15, False), ("egg fried rice", ["rice", "eggs", "peas"], 20, True)]


def dinners():
    out = []
    for prot, (diet, names) in PROTEINS.items():
        for style, one_pan, mins in STYLES_DINNER:
            veg = random.Random(f"{prot}{style}").sample(VEG, 2)
            title = f"{prot.capitalize()} and {veg[0].rstrip('s')} {style}" if prot not in ("egg",) else f"{veg[0].capitalize()} {style}"
            out.append({
                "title": title, "meal": "dinner", "kind": "recipe", "diet": diet, "totalMinutes": mins + random.Random(title).choice([0, 5, 10, 15]),
                "ingredients": [names[0], veg[0], veg[1]] + (["rice"] if "rice" in style or style == "risotto" else []), "onePan": one_pan,
            })
    return out


def catalogue():
    random.seed(127)
    items = dinners()
    for title, ing, mins, one_pan in BREAKFASTS:
        items.append({"title": title.capitalize(), "meal": "breakfast", "kind": "recipe", "diet": "veg", "totalMinutes": mins, "ingredients": ing, "onePan": one_pan})
    for title, ing, mins, one_pan in LUNCHES:
        items.append({"title": title.capitalize(), "meal": "lunch", "kind": "recipe", "diet": "veg" if "tuna" not in ing else "fish", "totalMinutes": mins, "ingredients": ing, "onePan": one_pan})
    return items


def decide(dish, who, rng):
    """The planted patterns. Returns (rating, reasons)."""
    mushrooms = "mushrooms" in dish["ingredients"]
    if mushrooms and who == "Sam":
        return "down", [{"id": "ingredient", "ingredients": ["mushrooms"]}]
    if dish["totalMinutes"] > 60:
        return "down", [{"id": "tookTooLong"}]
    if dish["meal"] == "dinner" and dish["onePan"]:
        return "up", []
    if rng.random() < 0.75:
        return "up", []
    return "down", [{"id": rng.choice(["notFilling", "justNotForUs", "tooMuchEffort"])}]


def gen(out):
    rng = random.Random(1271)
    dishes = catalogue()
    rng.shuffle(dishes)
    ratings = []
    for i in range(300):
        dish = dishes[i % len(dishes)]
        who_id, who = PEOPLE[rng.randrange(2)]
        rating, reasons = decide(dish, who, rng)
        ctx_roll = rng.random()
        context = "afterCooking" if ctx_roll < 0.3 else "suggested" if ctx_roll < 0.85 else "saved"
        when = NOW - timedelta(days=rng.randrange(0, 365), hours=rng.randrange(24))
        ratings.append({
            "id": str(uuid.UUID(int=rng.getrandbits(128))), PK: "household", "personId": who_id, "personName": who, "rating": rating,
            "reasons": reasons, "context": context, "ratedAt": when.isoformat(), "kind": dish["kind"], "title": dish["title"],
            "meal": dish["meal"], "diet": dish["diet"], "totalMinutes": dish["totalMinutes"], "ingredients": dish["ingredients"][:15],
        })
    save(out, "ratings.json", ratings)
    ups = sum(r["rating"] == "up" for r in ratings)
    print(f"{len(ratings)} ratings: {ups} up, {len(ratings) - ups} down; {len(dishes)} different dishes")


def rating_text(r):
    """What's embedded for a rating (question 3): title, meal, kind, main ingredients, diet, reasons."""
    reasons = ", ".join(
        (", ".join(x["ingredients"]) + " disliked") if x["id"] == "ingredient" else x["id"] for x in r["reasons"]
    )
    parts = [f"{r['meal']} {r['kind']}: {r['title']}", "main ingredients: " + ", ".join(r["ingredients"]), f"diet: {r['diet']}"]
    if r["totalMinutes"]:
        parts.append(f"{r['totalMinutes']} minutes")
    if reasons:
        parts.append("reasons: " + reasons)
    return ". ".join(parts)


def request_text(mode, meal, included, comments="", diet=""):
    """What's embedded for a request (question 3): mode, meal, included ingredients, comments, diet."""
    parts = [f"{meal} {mode}"]
    if included:
        parts.append("main ingredients: " + ", ".join(included))
    if diet:
        parts.append(f"diet: {diet}")
    if comments:
        parts.append(comments)
    return ". ".join(parts)


REQUESTS = {
    "breakfast": request_text("recipe", "breakfast", []),
    "chicken": request_text("recipe", "dinner", ["chicken thighs"]),
    "quick-lunch": request_text("recipe", "lunch", [], "something quick"),
    "mushrooms": request_text("recipe", "dinner", ["mushrooms"]),
    "veg-dinner": request_text("recipe", "dinner", [], "", "veg"),
    "fish": request_text("recipe", "dinner", ["salmon fillets"]),
}

# ---- plumbing -------------------------------------------------------------------------------------------------------


def save(out, name, obj):
    os.makedirs(out, exist_ok=True)
    with open(os.path.join(out, name), "w") as f:
        json.dump(obj, f, indent=1)


def load_json(out, name):
    with open(os.path.join(out, name)) as f:
        return json.load(f)


def az_token(resource):
    return subprocess.run(["az", "account", "get-access-token", "--resource", resource, "--query", "accessToken", "-o", "tsv"],
                          check=True, capture_output=True, text=True).stdout.strip()


_local = threading.local()


def http(method, url, headers, body=None):
    """One reused connection per thread (new connections now and then stall for seconds); a stalled one is dropped and retried."""
    data = None if body is None else json.dumps(body).encode()
    parts = urllib.parse.urlsplit(url)
    path = parts.path + ("?" + parts.query if parts.query else "")
    for attempt in range(8):
        conn = getattr(_local, parts.netloc, None)
        if conn is None:
            conn = HTTPSConnection(parts.netloc, timeout=6)
            setattr(_local, parts.netloc, conn)
        t0 = time.time()
        try:
            conn.request(method, path, body=data, headers=headers)
            resp = conn.getresponse()
            raw = resp.read()
            ms = (time.time() - t0) * 1000
            text = raw.decode() if raw else ""
            if resp.status >= 400:
                return resp.status, dict(resp.getheaders()), text[:600], ms
            return resp.status, dict(resp.getheaders()), (json.loads(text) if text else None), ms
        except (OSError, HTTPException):
            conn.close()
            setattr(_local, parts.netloc, None)
            if attempt == 7:
                raise
            time.sleep(1)


def embed(texts, dims):
    ep = f"https://{AI}.cognitiveservices.azure.com/openai/deployments/{EMBED_DEPLOYMENT}/embeddings?api-version=2024-10-21"
    hdr = {"Authorization": "Bearer " + az_token("https://cognitiveservices.azure.com"), "Content-Type": "application/json"}
    vectors, tokens, ms = [], 0, []
    for i in range(0, len(texts), 64):
        for attempt in range(5):
            st, _, body, took = http("POST", ep, hdr, {"input": texts[i:i + 64], "dimensions": dims})
            if st == 429:
                time.sleep(15)
                continue
            break
        if st != 200:
            sys.exit(f"embedding failed {st}: {body}")
        vectors += [d["embedding"] for d in body["data"]]
        tokens += body["usage"]["total_tokens"]
        ms.append(took)
    return vectors, tokens, ms


def cmd_embed(out):
    ratings = load_json(out, "ratings.json")
    texts = [rating_text(r) for r in ratings]
    req_texts = list(REQUESTS.values())
    result = {}
    for dims in (256, 512):
        vs, tok, ms = embed(texts + req_texts, dims)
        # single-text latency (what one request pays): five calls
        single = [embed([req_texts[i % len(req_texts)]], dims)[2][0] for i in range(5)]
        result[str(dims)] = {"ratings": vs[:len(texts)], "requests": dict(zip(REQUESTS, vs[len(texts):])),
                             "tokensTotal": tok, "batchMs": ms, "singleMs": single}
        print(f"{dims} dims: {len(texts)} ratings + {len(req_texts)} requests = {tok} tokens; single-call latency ms {[round(x) for x in single]}")
    save(out, "embeddings.json", result)
    print("sample rating text:", texts[0])
    print("sample request text:", req_texts[1])


class Cosmos:
    def __init__(self):
        self.base = f"https://{COSMOS}.documents.azure.com"
        self.token = az_token("https://cosmos.azure.com")

    def hdr(self, **extra):
        h = {"Authorization": "type=aad&ver=1.0&sig=" + self.token, "x-ms-version": "2018-12-31", "Content-Type": "application/json",
             "x-ms-date": datetime.now(timezone.utc).strftime("%a, %d %b %Y %H:%M:%S GMT")}
        h.update(extra)
        return h

    def upsert(self, coll, doc):
        return http("POST", f"{self.base}/dbs/{DB}/colls/{coll}/docs",
                    self.hdr(**{"x-ms-documentdb-is-upsert": "True", "x-ms-documentdb-partitionkey": json.dumps([doc[PK]])}), doc)

    def query(self, coll, sql, params, pk=None):
        h = self.hdr(**{"x-ms-documentdb-isquery": "True", "Content-Type": "application/query+json", "x-ms-max-item-count": "-1",
                        "x-ms-documentdb-query-enablecrosspartition": "True"})
        if pk:
            h["x-ms-documentdb-partitionkey"] = json.dumps([pk])
        h.pop("x-ms-documentdb-query-enablecrosspartition", None) if pk else None
        st, rh, body, ms = http("POST", f"{self.base}/dbs/{DB}/colls/{coll}/docs", h, {"query": sql, "parameters": params})
        return st, float(rh.get("x-ms-request-charge", 0)), body, ms

    def delete(self, coll, doc_id, pk):
        return http("DELETE", f"{self.base}/dbs/{DB}/colls/{coll}/docs/{doc_id}",
                    self.hdr(**{"x-ms-documentdb-partitionkey": json.dumps([pk])}))


def targets():
    return [(256, "spike-ratings-flat256"), (512, "spike-ratings-qflat512"), (512, DISK)]


def cmd_load(out):
    ratings, emb = load_json(out, "ratings.json"), load_json(out, "embeddings.json")
    c = Cosmos()
    for dims, coll in targets():
        total, n = 0.0, 0
        have = {d["id"] for d in c.query(coll, "SELECT c.id FROM c", [], pk="household")[2]["Documents"]}
        todo = [(r, v) for r, v in zip(ratings, emb[str(dims)]["ratings"]) if r["id"] not in have]  # a re-run only adds the rest

        def write(rv):
            st, rh, body, ms = c.upsert(coll, {**rv[0], "embedding": rv[1]})
            if st not in (200, 201):
                sys.exit(f"{coll}: write failed {st} {body}")
            return float(rh.get("x-ms-request-charge", 0))

        with ThreadPoolExecutor(8) as pool:  # a connection now and then stalls; eight at once hide it
            charges = list(pool.map(write, todo))
        total, n = sum(charges), len(charges)
        print(f"{coll}: {n} written, {total:.0f} RU in total ({total / max(n, 1):.1f} RU per write)")


def cmd_unload(out):
    ratings = load_json(out, "ratings.json")
    c = Cosmos()
    for _, coll in targets():
        for r in ratings:
            c.delete(coll, r["id"], r[PK])
        print(f"{coll}: documents deleted")


def cosine(a, b):
    dot = sum(x * y for x, y in zip(a, b))
    return dot / (math.sqrt(sum(x * x for x in a)) * math.sqrt(sum(y * y for y in b)))


def describe_hits(items):
    return [f"{i['meal'][:3]} {'👍' if i['rating'] == 'up' else '👎'} {i['title']}" for i in items]


def cmd_search(out):
    ratings, emb = load_json(out, "ratings.json"), load_json(out, "embeddings.json")
    c = Cosmos()
    summary = {}
    for dims, coll in targets():
        sql = ("SELECT TOP 20 c.id, c.title, c.meal, c.rating, c.ingredients, VectorDistance(c.embedding, @v) AS score "
               "FROM c ORDER BY VectorDistance(c.embedding, @v)")
        rows = []
        for name, vec in emb[str(dims)]["requests"].items():
            samples = []
            for _ in range(3):
                st, ru, body, ms = c.query(coll, sql, [{"name": "@v", "value": vec}], pk="household")
                if st != 200:
                    sys.exit(f"{coll} query failed {st}: {body}")
                samples.append((ru, ms))
            hits = body["Documents"]
            rows.append((name, samples, hits))
        summary[coll] = rows
        rus = [s[0] for _, ss, _ in rows for s in ss]
        mss = [s[1] for _, ss, _ in rows for s in ss]
        print(f"\n== {coll} ({dims} dims): top-20 RU min/avg/max {min(rus):.1f}/{sum(rus) / len(rus):.1f}/{max(rus):.1f}; "
              f"latency ms min/avg/max {min(mss):.0f}/{sum(mss) / len(mss):.0f}/{max(mss):.0f}")
        for name, _, hits in rows:
            meals = {}
            for h in hits:
                meals[h["meal"]] = meals.get(h["meal"], 0) + 1
            print(f"  {name:12} meals {meals}; mushrooms in {sum('mushrooms' in h['ingredients'] for h in hits)}/20; "
                  f"chicken in {sum(any('chicken' in i for i in h['ingredients']) for h in hits)}/20; "
                  f"down {sum(h['rating'] == 'down' for h in hits)}/20")
    # in-code comparison (the fallback): read everything, rank by cosine
    print("\n== in code (fallback): read all ratings, cosine similarity")
    for dims, coll in targets()[:2]:
        t0 = time.time()
        st, ru, body, ms = c.query(coll, "SELECT c.id, c.title, c.meal, c.rating, c.ingredients, c.embedding FROM c", [], pk="household")
        docs = body["Documents"]
        t1 = time.time()
        for name, vec in emb[str(dims)]["requests"].items():
            sorted(docs, key=lambda d: -cosine(vec, d["embedding"]))[:20]
        print(f"  {coll}: read {len(docs)} docs for {ru:.1f} RU in {ms:.0f} ms; ranking {len(emb[str(dims)]['requests'])} requests in code "
              f"{(time.time() - t1) * 1000:.0f} ms total")
    # same relevance in code, to compare with the index
    print("\n== agreement: in-code top-20 vs the index (qflat512)")
    docs_by_id = {r["id"]: r for r in ratings}
    for name, vec in emb["512"]["requests"].items():
        mine = [r["id"] for r in sorted(ratings, key=lambda r: -cosine(vec, emb["512"]["ratings"][ratings.index(r)]))[:20]]
        theirs = [h["id"] for h in dict((n, h) for n, _, h in summary["spike-ratings-qflat512"])[name]]
        print(f"  {name:12} overlap {len(set(mine) & set(theirs))}/20")
    save(out, "search-results.json", {k: [(n, s, describe_hits(h)) for n, s, h in v] for k, v in summary.items()})


def cmd_scale(out, total=2000):
    """Pad the flat256 and qflat512 containers to `total` documents with noisy copies of the 300 (cost scales with count, not content)."""
    ratings, emb = load_json(out, "ratings.json"), load_json(out, "embeddings.json")
    c = Cosmos()
    rng = random.Random(2000)
    for dims, coll in targets()[:2]:
        have = {d["id"] for d in c.query(coll, "SELECT c.id FROM c", [], pk="household")[2]["Documents"]}
        todo = []
        for i in range(len(have), total):  # ids continue from what's there, so a re-run only adds the rest
            r, v = ratings[i % 300], emb[str(dims)]["ratings"][i % 300]
            todo.append(({**r, "id": f"scale-{coll[-6:]}-{i}"}, [x + rng.gauss(0, 0.01) for x in v]))

        def write(rv):
            st, rh, body, ms = c.upsert(coll, {**rv[0], "embedding": rv[1]})
            return float(rh.get("x-ms-request-charge", 0)) if st in (200, 201) else sys.exit(f"{coll}: {st} {body}")

        with ThreadPoolExecutor(8) as pool:
            charges = list(pool.map(write, todo))
        print(f"{coll}: {len(charges)} added, {sum(charges):.0f} RU")


COMMANDS = {"scale": cmd_scale, "gen": gen, "embed": cmd_embed, "load": cmd_load, "search": cmd_search, "unload": cmd_unload}

if __name__ == "__main__":
    if len(sys.argv) != 3 or sys.argv[1] not in COMMANDS:
        sys.exit(__doc__)
    COMMANDS[sys.argv[1]](sys.argv[2])
