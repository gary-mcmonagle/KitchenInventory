#!/usr/bin/env python3
"""Spike #61: can Jev pick the most readable English name from Open Food Facts' name fields, and flag unreadable ones?

Usage:
  python3 61-jev-name-choice.py fetch   <out-dir>    # Open Food Facts records for the 27 sample barcodes (cached)
  python3 61-jev-name-choice.py report  <out-dir>    # candidates, Jev answers (normal + reversed order), summary
  python3 61-jev-name-choice.py probe   <out-dir>    # the readability flag on made-up foreign and noisy names

Standard library only. The Jev key comes from JEV_API_KEY in the environment (e.g. `set -a; . ./local.env`); it's
never printed or written. Raw Jev responses are saved to <out-dir>/jev-*.json (git-ignored when <out-dir> is
jev-results/), so the report can be re-read without new calls.
"""
import json
import os
import re
import sys
import time
import unicodedata
import urllib.error
import urllib.request

OFF_URL = "https://world.openfoodfacts.org/api/v2/product/{}.json"
USER_AGENT = "CupboardTracker/0.1 (https://github.com/gary-mcmonagle/KitchenInventory)"
JEV_URL = "https://api.typesafe.ai/v1/systemone"
MODEL = "jev-1.13.0"  # pinned, as spike #40 recommends

# The 27 sample barcodes from spike #28 (Gary's scan history, duplicates removed).
BARCODES = [
    "049733830119", "5000354922831", "5032457717533", "20590093", "5060040110010",
    "5903034425762", "7640110700075", "20719395", "4335619399235", "5060424707256",
    "5000328084152", "5010029212464", "5020580560379", "4088600478425", "5000232027306",
    "4056489879305", "20055363", "4056489899112", "4335619013537", "4088600060880",
    "5051140150785", "5000184221241", "615357122468", "4056489901105", "4056489825586",
    "8002210128088", "4056489903703",
]

NAME_FIELDS = re.compile(r"^(product_name|generic_name|abbreviated_product_name)(_[a-z]{2})?$")


# ---------------------------------------------------------------- Open Food Facts

def fetch(out_dir):
    os.makedirs(out_dir, exist_ok=True)
    for barcode in BARCODES:
        path = os.path.join(out_dir, f"off-{barcode}.json")
        if os.path.exists(path):
            continue
        for attempt in range(4):
            req = urllib.request.Request(OFF_URL.format(barcode), headers={"User-Agent": USER_AGENT})
            try:
                with urllib.request.urlopen(req, timeout=15) as resp:
                    status, body = resp.status, resp.read()
            except urllib.error.HTTPError as e:
                status, body = e.code, e.read()
            if status == 429 or status >= 500:
                time.sleep(10 * 2 ** attempt)
                continue
            break
        with open(path, "wb") as f:
            f.write(body)
        print(f"{barcode}\tHTTP {status}", flush=True)
        time.sleep(7.5)  # about 8 a minute, well inside Open Food Facts' 15


def product_of(out_dir, barcode):
    with open(os.path.join(out_dir, f"off-{barcode}.json"), encoding="utf-8") as f:
        data = json.load(f)
    return data.get("product") or {}


def current_name(p):
    """What the app shows today: product_name_en, else product_name, trimmed (Gary, 2026-10-17)."""
    for field in ("product_name_en", "product_name"):
        value = (p.get(field) or "").strip()
        if value:
            return value
    return ""


def candidates(p):
    """Distinct non-empty name-like fields: [(field, text)], current name first."""
    seen, out = set(), []
    fields = ["product_name_en", "product_name"] + sorted(k for k in p if NAME_FIELDS.match(k))
    for field in fields:
        text = (p.get(field) or "").strip() if isinstance(p.get(field), str) else ""
        key = text.casefold()
        if text and key not in seen:
            seen.add(key)
            out.append((field, text))
    return out


# ---------------------------------------------------------------- cheap checks (no Jev)

def is_all_caps(text):
    letters = [c for c in text if c.isalpha()]
    return len(letters) >= 3 and all(c.isupper() for c in letters)


def non_latin(text):
    return any(c.isalpha() and "LATIN" not in unicodedata.name(c, "") for c in text)


def suspicious(text):
    reasons = []
    if not text:
        reasons.append("empty")
    if non_latin(text):
        reasons.append("non-Latin")
    if is_all_caps(text):
        reasons.append("all caps")
    return reasons


# ---------------------------------------------------------------- Jev

def jev(state, questions):
    key = os.environ.get("JEV_API_KEY")
    if not key:
        sys.exit("Set JEV_API_KEY in the environment.")
    body = json.dumps({"state": state, "model": MODEL, "questions": questions}).encode()
    req = urllib.request.Request(JEV_URL, data=body, method="POST", headers={
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
        return status, ms, json.loads(raw)
    except ValueError:
        return status, ms, {"_raw": raw.decode("utf-8", "replace")[:300]}


def context(p):
    cats = [c.split(":", 1)[-1].replace("-", " ") for c in (p.get("categories_tags") or [])][-4:]
    return {"brand": (p.get("brands") or "").split(",")[0].strip() or None,
            "quantity": (p.get("quantity") or "").strip() or None,
            "categories": cats}


NAME_INSTRUCTIONS = ("Which option is the clearest name for this product in English, the way a UK shopper would "
                     "write it on a shopping list? Prefer English over other languages. Ignore capital letters.")


def name_questions(cands, current, reverse=False):
    options = {f"C{i + 1}": text for i, (_, text) in enumerate(cands)}
    options["none"] = "None of these is a usable English name for this product"
    if reverse:
        options = dict(reversed(list(options.items())))
    return {
        "name": {"type": "choice", "instructions": NAME_INSTRUCTIONS, "criteria": options},
        "readable": {"type": "noul",
                     "instructions": f'The product name "{current}" is in English and a UK shopper would understand '
                                     "what the product is from it. Capital letters don't matter."},
    }


SHELF_LIFE = {  # from spike #40, to test riding along in one request (question 6)
    "shelf_life": {"type": "score",
                   "instructions": "How long this product usually stays good to eat after it is bought, unopened "
                                   "and stored the usual way for this kind of product.",
                   "criteria": ["1-2 days", "3-4 days", "About a week", "About 2 weeks", "About a month",
                                "About 3 months", "About 6 months", "About a year", "About 2 years",
                                "3 years or more"]},
}


def cached_jev(out_dir, name, state, questions):
    path = os.path.join(out_dir, f"jev-{name}.json")
    if os.path.exists(path):
        with open(path, encoding="utf-8") as f:
            return json.load(f)
    status, ms, data = jev(state, questions)
    record = {"http": status, "ms": ms, "response": data}
    with open(path, "w", encoding="utf-8") as f:
        json.dump(record, f, indent=2, ensure_ascii=False)
    return record


def chosen(record, cands):
    a = record["response"].get("answers", {}).get("name", {})
    c = a.get("choice", "")
    text = cands[int(c[1:]) - 1][1] if c.startswith("C") and c[1:].isdigit() else c
    return text, round(a.get("confidence", 0), 2)


def report(out_dir):
    rows, combined = [], None
    for barcode in BARCODES:
        p = product_of(out_dir, barcode)
        cands = candidates(p)
        current = current_name(p)
        row = {"barcode": barcode, "current": current, "candidates": cands, "suspicious": suspicious(current)}
        if cands:
            state = {**context(p), "names": [t for _, t in cands]}
            normal = cached_jev(out_dir, f"{barcode}", state, name_questions(cands, current))
            rev = cached_jev(out_dir, f"{barcode}-rev", state, name_questions(cands, current, reverse=True))
            row["choice"], row["confidence"] = chosen(normal, cands)
            row["choice_rev"], _ = chosen(rev, cands)
            row["readable"] = round(normal["response"].get("answers", {}).get("readable", {}).get("noul", -1), 2)
            row["ms"] = normal["ms"]
        rows.append(row)
    # Question 6: one request with the name and shelf-life questions together (a suspicious and a clean product).
    for barcode in ("4056489879305", "5000354922831"):
        p = product_of(out_dir, barcode)
        cands = candidates(p)
        state = {**context(p), "names": [t for _, t in cands]}
        rec = cached_jev(out_dir, f"{barcode}-combined", state, {**name_questions(cands, current_name(p)), **SHELF_LIFE})
        a = rec["response"].get("answers", {})
        combined = combined or []
        combined.append((barcode, rec["http"], rec["ms"], chosen(rec, cands)[0],
                         round(a.get("readable", {}).get("noul", -1), 2),
                         round(a.get("shelf_life", {}).get("score", -1), 2)))

    print("barcode\tcurrent\t#cands\tsuspicious\tjev choice\tconf\tsame reversed\treadable(current)\tms")
    for r in rows:
        print("\t".join(str(x) for x in [
            r["barcode"], r["current"] or "—", len(r["candidates"]), ",".join(r["suspicious"]) or "-",
            r.get("choice", "—"), r.get("confidence", "—"),
            "yes" if r.get("choice") == r.get("choice_rev") else "NO", r.get("readable", "—"), r.get("ms", "—"),
        ]))
    print("\ncandidates:")
    for r in rows:
        print(r["barcode"], json.dumps(r["candidates"], ensure_ascii=False))
    print("\ncombined (name + shelf life in one request):")
    for c in combined or []:
        print("\t".join(str(x) for x in c))
    multi = sum(1 for r in rows if len(r["candidates"]) > 1)
    caps = sum(1 for r in rows if "all caps" in r["suspicious"])
    print(f"\n{len(rows)} products; {multi} with more than one candidate; {caps} all caps; "
          f"{sum(1 for r in rows if r['suspicious'])} suspicious")


# Names not in the sample, to test the readability flag on Latin-script foreign names and noise (question 3).
PROBES = [
    ("Weizenkleingebäck nach indischer Art", "20055363"), ("Pains naan à l'indienne", "20055363"),
    ("Naanbrood op indische wijze", "20055363"), ("Taste of", "5000354922831"), ("Gravy", "5000354922831"),
    ("Moutarde de Dijon", "4056489879305"), ("SPC 400G", "4056489903703"), ("горчица", "4056489879305"),
]


def probe(out_dir):
    for i, (name, barcode) in enumerate(PROBES):
        p = product_of(out_dir, barcode)
        q = name_questions([("probe", name)], name)
        rec = cached_jev(out_dir, f"probe-{i}", {**context(p), "names": [name]}, {"readable": q["readable"]})
        print(f"{rec['response'].get('answers', {}).get('readable', {}).get('noul', -1):.2f}\t{name}")


if __name__ == "__main__":
    commands = {"fetch": fetch, "report": report, "probe": probe}
    if len(sys.argv) < 3 or sys.argv[1] not in commands:
        sys.exit(__doc__)
    commands[sys.argv[1]](sys.argv[2])
