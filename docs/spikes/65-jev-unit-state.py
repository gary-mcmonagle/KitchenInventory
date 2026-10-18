#!/usr/bin/env python3
"""Card #65: check Jev's answers to the unit-state questions (opened, frozen, defrosted) before building.

Usage:
  set -a; . ./local.env; set +a; python3 docs/spikes/65-jev-unit-state.py [output-dir]

Standard library only. The Jev key comes from JEV_API_KEY in the environment; it's never printed or written.
Asks all five questions in one request per product (as the feature will), picks levels like the shelf-life estimator
(median, or the 25th percentile when confidence < 0.5), prints a table and saves each raw response to output-dir.
"""
import json
import os
import sys
import time
import urllib.error
import urllib.request

URL = "https://api.typesafe.ai/v1/systemone"
MODEL = "jev-1.13.0"
LOW_CONFIDENCE = 0.5

# The shelf-life levels from spike #40 (the feature reuses them).
LEVELS = [
    ("1-2 days", 2), ("3-4 days", 4), ("About a week", 7), ("About 2 weeks", 14), ("About a month", 30),
    ("About 3 months", 90), ("About 6 months", 180), ("About a year", 365), ("About 2 years", 730),
    ("3 years or more", 1095),
]


def duration(instructions):
    return {"type": "score", "instructions": instructions, "criteria": [label for label, _ in LEVELS]}


QUESTIONS = {
    "opened_storage": {
        "type": "choice",
        "instructions": "Where this product should be kept at home once it has been opened.",
        "criteria": {"cupboard": "Cupboard or worktop, at room temperature", "fridge": "Fridge", "freezer": "Freezer"},
    },
    "opened_shelf_life": duration(
        "How long this product stays good to eat once it has been opened, kept where it should be once opened."),
    "frozen_shelf_life": duration(
        "How long this product keeps in a home freezer if it is frozen on the day it was bought, before the quality "
        "suffers."),
    "defrosted_storage": {
        "type": "choice",
        "instructions": "Where this product should be kept once it has been taken out of the freezer and defrosted.",
        "criteria": {"cupboard": "Cupboard or worktop, at room temperature", "fridge": "Fridge"},
    },
    "defrosted_shelf_life": duration(
        "How long this product stays good to eat once it has been frozen and then defrosted, kept where it should be "
        "once defrosted."),
}

# Real products in Gary's stock (live Cosmos, 2026-10-18), with the usual storage Jev gave them for shelf life.
PRODUCTS = [
    {"name": "Garlic flavoured olive oil", "brand": "Filippo Berio", "size": "250 ml", "categories": ["en:flavoured-olive-oils"], "storage": "cupboard"},
    {"name": "Crunchy Peanut Butter", "brand": "Maribel", "size": "1 kg", "categories": ["en:crunchy-peanut-butters"], "storage": "cupboard"},
    {"name": "Chickpeas in Water", "brand": "Freshona", "size": "400g", "categories": ["en:tinned-beans"], "storage": "cupboard"},
    {"name": "CHIPOTLE HOT SAUCE", "brand": "McCormick & Company Inc.", "size": "150 ml", "storage": "cupboard"},
    {"name": "Clarks Original Maple Syrup 180Ml", "brand": "Clarks", "size": "180 ml", "storage": "cupboard"},
    {"name": "Beef Gravy", "brand": "Bisto", "size": "230g", "categories": ["en:gravy"], "storage": "cupboard"},
    {"name": "Greek 10% fat authentic yogurt", "brand": "Brooklea", "size": "500g", "categories": ["en:plain-cow-milk-yogurts"], "storage": "fridge"},
    {"name": "Soured Cream", "brand": "Dairy Manor", "size": "300 ml", "storage": "fridge"},
    {"name": "Parmigiano Reggiano PDO", "brand": "Deluxe", "size": "200g", "storage": "fridge"},
    {"name": "PLAIN NAAN BREADS", "brand": "Taste of", "size": "2 x 130 g", "categories": ["en:naans"], "storage": "cupboard"},
    {"name": "Leeks", "storage": "fridge"},
    {"name": "Ham hock terrine", "storage": "fridge"},
]


def call(state):
    key = os.environ.get("JEV_API_KEY")
    if not key:
        sys.exit("Set JEV_API_KEY in the environment.")
    body = json.dumps({"state": state, "model": MODEL, "questions": QUESTIONS}).encode()
    req = urllib.request.Request(URL, data=body, method="POST", headers={
        "Authorization": "Bearer " + key, "Content-Type": "application/json"})
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
        if status in (0, 429, 529) or status >= 500:
            time.sleep(2 * 2 ** attempt)
            continue
        break
    try:
        return status, ms, json.loads(raw)
    except ValueError:
        return status, ms, {"_raw": raw.decode("utf-8", "replace")[:300]}


def pick(answer):
    """The level's days and whether it was low confidence: median, or 25th percentile below 0.5 confidence."""
    probabilities = answer.get("probabilities") or {}
    confidence = answer.get("confidence", 0)
    low = confidence < LOW_CONFIDENCE
    target, total = (0.25 if low else 0.5), 0.0
    for level in range(len(LEVELS)):
        total += probabilities.get(str(level), 0)
        if total >= target:
            return LEVELS[level][1], low, confidence
    return None, low, confidence


def days_text(days):
    if days is None:
        return "?"
    return f"{days // 365}y" if days >= 365 else f"{days // 30}mo" if days >= 30 else f"{days}d"


def main():
    out_dir = sys.argv[1] if len(sys.argv) > 1 else "jev-65-results"
    os.makedirs(out_dir, exist_ok=True)
    print("product | usual | opened: where, keeps (conf) | frozen keeps (conf) | defrosted: where, keeps (conf) | ms")
    for i, product in enumerate(PRODUCTS):
        status, ms, data = call(product)
        with open(os.path.join(out_dir, f"{i:02d}.json"), "w") as f:
            json.dump({"product": product, "http": status, "ms": ms, "response": data}, f, indent=2)
        if status != 200:
            print(f"{product['name']} | HTTP {status}")
            continue
        a = data.get("answers", {})
        opened, opened_low, opened_c = pick(a.get("opened_shelf_life", {}))
        frozen, frozen_low, frozen_c = pick(a.get("frozen_shelf_life", {}))
        defrosted, defrosted_low, defrosted_c = pick(a.get("defrosted_shelf_life", {}))
        mark = lambda low: "*" if low else ""
        print(" | ".join([
            product["name"][:32], product.get("storage", ""),
            f"{a.get('opened_storage', {}).get('choice')}, {days_text(opened)}{mark(opened_low)} ({opened_c:.2f})",
            f"{days_text(frozen)}{mark(frozen_low)} ({frozen_c:.2f})",
            f"{a.get('defrosted_storage', {}).get('choice')}, {days_text(defrosted)}{mark(defrosted_low)} ({defrosted_c:.2f})",
            str(ms),
        ]))
    print("* = low confidence: the cautious 25th-percentile level was used")


if __name__ == "__main__":
    main()
