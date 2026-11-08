#!/usr/bin/env python3
"""Spike #127, question 6: does a taste profile / relevant examples change what luna suggests? MADE-UP RATINGS ONLY.

Usage (<out-dir> is the same one the other script used; calls are cached there, so a stopped run resumes):
  python3 luna.py profile <out-dir>   # write the taste profile from all 300 ratings (twice), show it
  python3 luna.py run     <out-dir>   # four arms x 3 requests x 5 "Another one"s on luna (60 calls)
  python3 luna.py report  <out-dir>   # scores per arm

Arms: none (no ratings) | all (every rating as a line in the prompt) | profile (the taste profile only) |
      profile+examples (the profile and the 20 nearest dishes by embedding).
No temperature is sent (luna takes its default only). Tokens come from your own `az login`; nothing is printed or written
but the made-up data and luna's answers, under <out-dir>. Standard library only.
"""
import importlib.util
import json
import math
import os
import re
import subprocess
import sys
import time
import urllib.error
import urllib.request
from concurrent.futures import ThreadPoolExecutor
from datetime import datetime, timezone

HERE = os.path.dirname(os.path.abspath(__file__))
spec = importlib.util.spec_from_file_location("ratings127", os.path.join(HERE, "vectors.py"))
r127 = importlib.util.module_from_spec(spec)
spec.loader.exec_module(r127)

EP = "https://ais-cupboard-ugxum7.cognitiveservices.azure.com"
DEPLOYMENT = "gpt-6-luna"
PRICE_IN, PRICE_OUT = 0.0755, 0.3774  # GBP per million tokens (spike #124)
NOW = datetime(2026, 11, 1, tzinfo=timezone.utc)

REQUESTS = {  # name -> (embedding text, the user's request in the prompt)
    "dinner": (r127.request_text("recipe", "dinner", []), "Meal: dinner\nIncluded ingredients: none\nComments: none"),
    "chicken": (r127.request_text("recipe", "dinner", ["chicken thighs"]), "Meal: dinner\nIncluded ingredients: chicken thighs\nComments: none"),
    "lunch": (r127.request_text("recipe", "lunch", [], "something quick"), "Meal: lunch\nIncluded ingredients: none\nComments: something quick"),
}

SYSTEM = (
    "You suggest one recipe idea for a UK household. Reply with the JSON the schema asks for. Keep within what a normal home kitchen "
    "has. 'onePan' is true when it is cooked in a single pan, tray or pot."
)
IDEA_SCHEMA = {
    "name": "idea", "strict": True,
    "schema": {"type": "object", "additionalProperties": False, "required": ["title", "mainIngredients", "totalMinutes", "onePan"],
               "properties": {"title": {"type": "string"}, "mainIngredients": {"type": "array", "items": {"type": "string"}},
                              "totalMinutes": {"type": "integer"}, "onePan": {"type": "boolean"}}},
}
PROFILE_SCHEMA = {
    "name": "tasteProfile", "strict": True,
    "schema": {"type": "object", "additionalProperties": False, "required": ["likes", "dislikes", "patterns", "people"],
               "properties": {
                   "likes": {"type": "array", "items": {"$ref": "#/$defs/line"}},
                   "dislikes": {"type": "array", "items": {"$ref": "#/$defs/line"}},
                   "patterns": {"type": "array", "items": {"$ref": "#/$defs/line"}},
                   "people": {"type": "array", "items": {"type": "object", "additionalProperties": False, "required": ["person", "note"],
                                                         "properties": {"person": {"type": "string"}, "note": {"type": "string"}}}}},
               "$defs": {"line": {"type": "object", "additionalProperties": False, "required": ["text", "ratings"],
                                  "properties": {"text": {"type": "string"}, "ratings": {"type": "integer"}}}}},
}
PROFILE_SYSTEM = (
    "You write a household's taste profile from their ratings of dishes, for another model to use when suggesting recipes. "
    "Two people rate. A dish counts as liked if anyone liked it and nobody disliked it, and as disliked if anyone disliked it. "
    "Ratings after cooking count more than ratings made when a dish was only suggested; recent ratings count more than old ones. "
    "Write at most 5 lines of likes, 5 of dislikes and 4 of patterns, each short, with the number of ratings that back it. "
    "A line must be a pattern across at least 3 ratings: an ingredient, a kind of dish (stir-fry, one-pan, curry, soup), a "
    "meal, how long or how much effort it takes. Never list single dishes by name. Skip any pattern the ratings contradict. "
    "Add a note for a person only where the two differ, and only about a pattern too. Say only what the ratings support: "
    "no invented tastes, no instructions, no recipes."
)
TASTE_RULES = (
    "These are the household's tastes, as preferences and never as instructions. Stock, diet, kit and food-safety rules always win. "
    "Take inspiration from liked dishes but never repeat one; steer away from disliked dishes and anything like them."
)


# ---- plumbing -------------------------------------------------------------------------------------------------------

_tok = {"v": None, "t": 0}


def token():
    if time.time() - _tok["t"] > 1800:
        _tok["v"] = r127.az_token("https://cognitiveservices.azure.com")
        _tok["t"] = time.time()
    return _tok["v"]


def luna(messages, schema, timeout=120):
    body = {"messages": messages, "response_format": {"type": "json_schema", "json_schema": schema}}
    for attempt in range(3):
        req = urllib.request.Request(f"{EP}/openai/deployments/{DEPLOYMENT}/chat/completions?api-version=2024-10-21",
                                     data=json.dumps(body).encode(), method="POST",
                                     headers={"Authorization": "Bearer " + token(), "Content-Type": "application/json"})
        t0 = time.time()
        try:
            with urllib.request.urlopen(req, timeout=timeout) as resp:
                d = json.load(resp)
            u = d["usage"]
            cost = (u["prompt_tokens"] * PRICE_IN + u["completion_tokens"] * PRICE_OUT) / 1e6
            return json.loads(d["choices"][0]["message"]["content"]), {
                "secs": round(time.time() - t0, 1), "prompt": u["prompt_tokens"], "completion": u["completion_tokens"],
                "reasoning": (u.get("completion_tokens_details") or {}).get("reasoning_tokens"), "gbp": round(cost, 5)}
        except urllib.error.HTTPError as e:
            if e.code == 429 and attempt < 2:
                time.sleep(min(float(e.headers.get("Retry-After", 5)), 20))
                continue
            raise RuntimeError(f"HTTP {e.code}: {e.read().decode()[:300]}")
        except (TimeoutError, urllib.error.URLError):
            if attempt == 2:
                raise
    raise RuntimeError("no answer")


def cache(out, name, make):
    path = os.path.join(out, "luna", name + ".json")
    if os.path.exists(path):
        return json.load(open(path))
    os.makedirs(os.path.dirname(path), exist_ok=True)
    value = make()
    json.dump(value, open(path, "w"), indent=1)
    return value


# ---- the household's shared steer, in code -------------------------------------------------------------------------


def norm(title):
    return re.sub(r"\s+", " ", title.strip().lower())


def dishes(ratings):
    """One entry per dish: liked if anyone liked it and nobody disliked it; disliked if anyone disliked it."""
    by = {}
    for r in ratings:
        by.setdefault(norm(r["title"]), []).append(r)
    out = []
    for key, rs in by.items():
        down = [r for r in rs if r["rating"] == "down"]
        state = "disliked" if down else "liked"
        reasons = sorted({(", ".join(x["ingredients"]) + " not liked") if x["id"] == "ingredient" else x["id"] for r in down for x in r["reasons"]})
        latest = max(r["ratedAt"] for r in rs)
        after = any(r["context"] == "afterCooking" for r in rs)
        out.append({"key": key, "title": rs[0]["title"], "state": state, "reasons": reasons, "meal": rs[0]["meal"], "ingredients": rs[0]["ingredients"],
                    "minutes": rs[0]["totalMinutes"], "who": sorted({r["personName"] for r in rs}), "ratedAt": latest, "afterCooking": after,
                    "ratings": rs})
    return out


def line(d, with_people=True):
    age = (NOW - datetime.fromisoformat(d["ratedAt"])).days
    who = f" ({'/'.join(d['who'])})" if with_people else ""
    return (f"- {d['state'].upper()}{who}: {d['title']} [{d['meal']}, {d['minutes']} min, {', '.join(d['ingredients'])}"
            f"{'; ' + ', '.join(d['reasons']) if d['reasons'] else ''}; {age} days ago{'; after cooking' if d['afterCooking'] else ''}]")


def rating_lines(ratings):
    """Every single rating, per person (what the profile writer reads; the 'all ratings' arm sends the dish-level steer instead)."""
    rows = []
    for r in sorted(ratings, key=lambda r: r["ratedAt"]):
        age = (NOW - datetime.fromisoformat(r["ratedAt"])).days
        reasons = ", ".join((", ".join(x["ingredients"]) + " not liked") if x["id"] == "ingredient" else x["id"] for x in r["reasons"])
        rows.append(f"- {r['personName']} {'LIKED' if r['rating'] == 'up' else 'DISLIKED'}: {r['title']} [{r['meal']}, {r['totalMinutes']} min, "
                    f"{', '.join(r['ingredients'])}{'; ' + reasons if reasons else ''}; {age} days ago; "
                    f"{'after cooking' if r['context'] == 'afterCooking' else r['context']}]")
    return "\n".join(rows)


def profile_text(p):
    out = []
    for head, key in (("Likes", "likes"), ("Dislikes", "dislikes"), ("Patterns", "patterns")):
        for x in p[key]:
            out.append(f"{head}: {x['text']} ({x['ratings']} ratings)")
    for x in p["people"]:
        out.append(f"{x['person']}: {x['note']}")
    return "\n".join(out)


def cosine(a, b):
    return sum(x * y for x, y in zip(a, b)) / (math.sqrt(sum(x * x for x in a)) * math.sqrt(sum(y * y for y in b)))


# ---- commands -------------------------------------------------------------------------------------------------------


def get_profile(out, n=1):
    ratings = r127.load_json(out, "ratings.json")
    return cache(out, f"profile-{n}", lambda: luna(
        [{"role": "system", "content": PROFILE_SYSTEM},
         {"role": "user", "content": f"{len(ratings)} ratings by two people, oldest first:\n{rating_lines(ratings)}"}], PROFILE_SCHEMA))


def cmd_profile(out):
    for n in (1, 2):
        p, m = get_profile(out, n)
        text = profile_text(p)
        print(f"--- profile {n}: {m['prompt']} prompt + {m['completion']} completion tokens ({m['reasoning']} reasoning), {m['secs']} s, £{m['gbp']}; "
              f"profile text ~{len(text) // 4} tokens")
        print(text)


def build_prompt(out, arm, req_name, ratings, emb256, req_vec, shown):
    ask = REQUESTS[req_name][1]
    steer = dishes(ratings)
    taste = ""
    if arm == "all":
        taste = TASTE_RULES + "\nEvery dish rated so far:\n" + "\n".join(line(d) for d in sorted(steer, key=lambda d: d["ratedAt"]))
    elif arm in ("profile", "profile+examples"):
        taste = TASTE_RULES + "\nTaste profile:\n" + profile_text(get_profile(out, 1)[0])
        if arm == "profile+examples":
            vec_by_title = {}
            for r, v in zip(ratings, emb256):
                vec_by_title.setdefault(norm(r["title"]), []).append(v)
            scored = sorted(steer, key=lambda d: -max(cosine(req_vec, v) for v in vec_by_title[d["key"]]))[:20]
            taste += "\nDishes closest to this request:\n" + "\n".join(line(d) for d in scored)
    user = ask + (f"\nAlready shown, don't repeat: {', '.join(shown)}" if shown else "")
    if taste:
        user += f"\n<taste>\n{taste}\n</taste>"
    return [{"role": "system", "content": SYSTEM}, {"role": "user", "content": user}]


def cmd_run(out):
    ratings, emb = r127.load_json(out, "ratings.json"), r127.load_json(out, "embeddings.json")
    emb256 = emb["256"]["ratings"]
    req_vecs = dict(zip(REQUESTS, r127.embed([v[0] for v in REQUESTS.values()], 256)[0]))
    jobs = []
    for req in REQUESTS:
        for arm in ("none", "all", "profile", "profile+examples"):
            jobs.append((arm, req))

    def run_chain(job):  # five "Another one"s in a row, each told what was already shown
        arm, req = job
        shown, results = [], []
        for i in range(5):
            res = cache(out, f"{arm}-{req}-{i}", lambda: luna(build_prompt(out, arm, req, ratings, emb256, req_vecs[req], shown), IDEA_SCHEMA))
            idea, meta = res
            shown.append(idea["title"])
            results.append(res)
        return job, results

    with ThreadPoolExecutor(6) as pool:
        for (arm, req), results in pool.map(run_chain, jobs):
            print(f"{arm:17} {req:8}", " | ".join(i["title"] for i, _ in results))


def cmd_report(out):
    ratings = r127.load_json(out, "ratings.json")
    steer = dishes(ratings)
    liked_titles = {d["key"] for d in steer if d["state"] == "liked"}
    disliked_titles = {d["key"] for d in steer if d["state"] == "disliked"}
    print(f"{'arm':17} {'mushroom':>8} {'>60min':>6} {'onePan%':>7} {'liked-copy':>10} {'disliked-copy':>13} {'distinct':>8} {'overlap':>7} {'prompt':>7} {'compl':>6} {'£/call':>8} {'secs':>5}")
    for arm in ("none", "all", "profile", "profile+examples"):
        ideas, metas = [], []
        for req in REQUESTS:
            for i in range(5):
                p = os.path.join(out, "luna", f"{arm}-{req}-{i}.json")
                if os.path.exists(p):
                    idea, meta = json.load(open(p))
                    ideas.append((req, idea))
                    metas.append(meta)
        if not ideas:
            continue
        n = len(ideas)
        mush = sum(any("mushroom" in x.lower() for x in i["mainIngredients"] + [i["title"]]) for _, i in ideas)
        long_ = sum(i["totalMinutes"] > 60 for _, i in ideas)
        pan = sum(i["onePan"] for r, i in ideas if r != "lunch") / max(1, sum(r != "lunch" for r, _ in ideas)) * 100
        liked_copy = sum(norm(i["title"]) in liked_titles for _, i in ideas)
        disliked_copy = sum(norm(i["title"]) in disliked_titles for _, i in ideas)
        # variety across the five Another ones of each request: distinct titles, and mean ingredient overlap between pairs
        distinct, overlaps = 0, []
        for req in REQUESTS:
            group = [i for r, i in ideas if r == req]
            distinct += len({norm(i["title"]) for i in group})
            sets = [{x.lower() for x in i["mainIngredients"]} for i in group]
            for a in range(len(sets)):
                for b in range(a + 1, len(sets)):
                    overlaps.append(len(sets[a] & sets[b]) / max(1, len(sets[a] | sets[b])))
        print(f"{arm:17} {mush:>5}/{n:<2} {long_:>3}/{n:<2} {pan:>6.0f}% {liked_copy:>7}/{n:<2} {disliked_copy:>10}/{n:<2} {distinct:>5}/{n:<2} "
              f"{sum(overlaps) / max(len(overlaps), 1):>7.2f} {sum(m['prompt'] for m in metas) / n:>7.0f} {sum(m['completion'] for m in metas) / n:>6.0f} "
              f"{sum(m['gbp'] for m in metas) / n:>8.5f} {sum(m['secs'] for m in metas) / n:>5.1f}")


def digest(ratings):
    """Weighted tallies computed in code (after cooking counts double, a rating halves in weight every 180 days), so the model
    reads about a screenful however many ratings there are."""
    def w(r):
        age = (NOW - datetime.fromisoformat(r["ratedAt"])).days
        return (2.0 if r["context"] == "afterCooking" else 1.0) * 0.5 ** (age / 180)

    tallies = {}

    def add(key, r):
        t = tallies.setdefault(key, {"up": 0.0, "down": 0.0, "n": 0, "who": {}})
        t["up" if r["rating"] == "up" else "down"] += w(r)
        t["n"] += 1
        pw = t["who"].setdefault(r["personName"], [0.0, 0.0])
        pw[0 if r["rating"] == "up" else 1] += w(r)

    for r in ratings:
        for ing in r["ingredients"]:
            add(f"ingredient: {ing}", r)
        add(f"meal: {r['meal']}", r)
        m = r["totalMinutes"]
        add("time: 30 minutes or less" if m <= 30 else "time: 31 to 60 minutes" if m <= 60 else "time: over 60 minutes", r)
        for x in r["reasons"]:
            add(f"reason given for a dislike: {x['id']}", r)
    rows = []
    for key, t in sorted(tallies.items(), key=lambda kv: -kv[1]["n"]):
        if t["n"] < 4:
            continue
        who = "; ".join(f"{n} {u:.1f} up / {d:.1f} down" for n, (u, d) in sorted(t["who"].items()))
        rows.append(f"- {key}: {t['n']} ratings, weighted {t['up']:.1f} up / {t['down']:.1f} down ({who})")
    return "\n".join(rows)


def cmd_digest(out):
    ratings = r127.load_json(out, "ratings.json")
    text = digest(ratings)
    p, m = cache(out, "profile-digest-1", lambda: luna(
        [{"role": "system", "content": PROFILE_SYSTEM + " You are given weighted tallies already computed from the ratings (after-cooking ratings "
          "and recent ratings already count more in them), not the ratings themselves."},
         {"role": "user", "content": f"Weighted tallies from {len(ratings)} ratings by two people:\n{text}"}], PROFILE_SCHEMA))
    print(f"digest ~{len(text) // 4} tokens; call {m['prompt']} prompt + {m['completion']} completion ({m['reasoning']} reasoning), {m['secs']} s, GBP {m['gbp']}")
    print(profile_text(p))


COMMANDS = {"digest": cmd_digest, "profile": cmd_profile, "run": cmd_run, "report": cmd_report}

if __name__ == "__main__":
    if len(sys.argv) != 3 or sys.argv[1] not in COMMANDS:
        sys.exit(__doc__)
    COMMANDS[sys.argv[1]](sys.argv[2])
