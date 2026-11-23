#!/usr/bin/env python3
"""Spike #135: can Jev judge what's due on the shopping list ("usuals")?

Usage (read-only against the live data; keep the dump OUTSIDE the repo, it has product ids):
  python3 135-usuals-jev.py dump <dir>                       # products + units to <dir> (SELECT only, your az login)
  python3 135-usuals-jev.py backtest <dir> [--asof 28,21,14,7] [--no-jev] [--variants v1,v2,v3] [--mode single,batch]

For each "as of" date (N days ago) it builds, from the check-ins before that date only, each generic name's purchase
history, asks "is it due?" with a plain rule and with Jev, and compares both with what was actually checked in over the
following 7 days. Stock is never used (spike #135: check-outs are missed, check-ins are dependable).

The Jev key is read from the git-ignored local.env (JEV_API_KEY) and is never printed, logged or put in an argument.
Jev responses are cached under <dir>/jev-cache so a re-run costs nothing. Standard library only.
"""
import hashlib
import importlib.util
import json
import os
import statistics
import sys
import threading
import time
import urllib.error
import urllib.request
from collections import defaultdict
from concurrent.futures import ThreadPoolExecutor
from datetime import date, datetime, timedelta, timezone
from pathlib import Path
from zoneinfo import ZoneInfo

UK = ZoneInfo("Europe/London")
HISTORY_START = date(2026, 10, 19)  # the day after the stock take (USUALLY_BUYS_HISTORY_START)
WINDOW_DAYS = 7  # "due" means it was checked in again within this many days
JEV_URL = "https://api.typesafe.ai/v1/systemone"
JEV_MODEL = "jev-1.13.0"
PRICE_PER_MTOK_INPUT = 0.042  # USD, output is free (spike #40)


def load_module_116():
    spec = importlib.util.spec_from_file_location("s116", Path(__file__).with_name("116-name-casing-duplicates.py"))
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


# ---- dump -------------------------------------------------------------------------------------------------------------

def dump(out_dir: Path) -> None:
    m = load_module_116()
    outputs = json.loads(m.az("deployment", "sub", "show", "--name", "cupboard-tracker", "--query", "properties.outputs"))
    endpoint, database = outputs["cosmosEndpoint"]["value"], outputs["cosmosDatabaseName"]["value"]
    out_dir.mkdir(parents=True, exist_ok=True)
    products = m.query(endpoint, database, "products", "SELECT * FROM c WHERE c.type = 'product'")
    units = m.query(
        endpoint, database, "inventoryItems",
        "SELECT c.productId, c.status, c.checkedInAt, c.checkedOutAt, c.checkOutReason, c.estimatedExpiryDate FROM c")
    (out_dir / "products.json").write_text(json.dumps(products))
    (out_dir / "units.json").write_text(json.dumps(units))
    print(f"{len(products)} products, {len(units)} units")


# ---- facts ------------------------------------------------------------------------------------------------------------

def uk_day(iso: str) -> date:
    return datetime.fromisoformat(iso.replace("Z", "+00:00")).astimezone(UK).date()


def generic_key(p: dict) -> str:
    g = p.get("genericName")
    g = g.get("name") if isinstance(g, dict) else g
    return " ".join((g or p.get("displayName") or "?").split()).lower()


class Data:
    def __init__(self, directory: Path):
        products = json.loads((directory / "products.json").read_text())
        units = json.loads((directory / "units.json").read_text())
        self.key_of = {p["id"]: generic_key(p) for p in products}
        self.aisle = {}
        self.shelf = {}
        votes = defaultdict(lambda: defaultdict(int))
        for p in products:
            k = generic_key(p)
            votes[k][p.get("aisle") or "other"] += 1
            if p.get("shelfLifeDays"):
                self.shelf.setdefault(k, p["shelfLifeDays"])
        for k, v in votes.items():
            self.aisle[k] = max(v, key=v.get)
        # purchases: check-ins since the stock take, undone camera check-ins left out
        self.purchases = defaultdict(list)  # key -> list of (date, checkedOut date or None, reason)
        for u in units:
            if u.get("checkOutReason") == "undoneCheckIn":
                continue
            key = self.key_of.get(u["productId"], "?")
            d_in = uk_day(u["checkedInAt"])
            d_out = uk_day(u["checkedOutAt"]) if u.get("checkedOutAt") else None
            self.purchases[key].append((d_in, d_out, u.get("checkOutReason")))
        self.last_day = max(d for v in self.purchases.values() for d, _, _ in v)

    def facts(self, key: str, asof: date) -> dict | None:
        """Everything known about one name before `asof` (check-ins on earlier days only). None if under two purchase days."""
        days = sorted({d for d, _, _ in self.purchases[key] if HISTORY_START <= d < asof})
        if len(days) < 2:
            return None
        gaps = [(b - a).days for a, b in zip(days, days[1:])]
        lags = [(o - i).days for i, o, reason in self.purchases[key]
                if o is not None and o < asof and i >= HISTORY_START and reason != "stockCheck"]
        return {
            "key": key,
            "aisle": self.aisle.get(key, "other"),
            "purchase_days": len(days),
            "gaps": gaps,
            "median_gap": statistics.median(gaps),
            "days_since_last": (asof - days[-1]).days,
            "median_lasted": statistics.median(lags) if len(lags) >= 2 else None,
            "shelf_life_days": self.shelf.get(key),
        }

    def bought_within(self, key: str, asof: date) -> bool:
        end = asof + timedelta(days=WINDOW_DAYS)
        return any(asof <= d < end for d, _, _ in self.purchases[key])

    def candidates(self, asof: date) -> list[dict]:
        out = [f for k in self.purchases if k != "?" and (f := self.facts(k, asof))]
        for f in out:
            f["truth"] = self.bought_within(f["key"], asof)
            f["asof"] = asof
        return sorted(out, key=lambda f: f["key"])


def ratio(f: dict) -> float:
    return f["days_since_last"] / max(f["median_gap"], 1)


def per_week(f: dict) -> float:
    """How many different days a week it is bought on, since the stock take (a staples score)."""
    return f["purchase_days"] / max((f["asof"] - HISTORY_START).days, 7) * 7


def freq_and_due(f: dict) -> float:
    """Staples first, but a recently bought one is less likely to be needed (the ratio, capped)."""
    return per_week(f) * min(ratio(f), 1.5)


# ---- metrics ----------------------------------------------------------------------------------------------------------

def auc(scored: list[tuple[float, bool]]) -> float:
    pos = [s for s, t in scored if t]
    neg = [s for s, t in scored if not t]
    if not pos or not neg:
        return float("nan")
    wins = sum((p > n) + 0.5 * (p == n) for p in pos for n in neg)
    return wins / (len(pos) * len(neg))


def at_threshold(scored: list[tuple[float, bool]], cut: float) -> dict:
    chosen = [t for s, t in scored if s >= cut]
    positives = sum(1 for _, t in scored if t)
    tp = sum(chosen)
    precision = tp / len(chosen) if chosen else float("nan")
    recall = tp / positives if positives else float("nan")
    f1 = 2 * precision * recall / (precision + recall) if tp else 0.0
    return {"cut": cut, "listed": len(chosen), "precision": precision, "recall": recall, "f1": f1}


def show(title: str, scored: list[tuple[float, bool]], cuts: list[float]) -> None:
    base = sum(t for _, t in scored) / len(scored)
    print(f"\n  {title}: n={len(scored)}, bought again within {WINDOW_DAYS} days: {base:.0%}, AUC={auc(scored):.2f}")
    for c in cuts:
        r = at_threshold(scored, c)
        print(f"    cut {c:>5}: list {r['listed']:>3} ({r['listed'] / len(scored):.0%})  precision {r['precision']:.2f}  "
              f"recall {r['recall']:.2f}  F1 {r['f1']:.2f}")


# ---- Jev --------------------------------------------------------------------------------------------------------------

def jev_key() -> str:
    for candidate in (Path(os.environ.get("LOCAL_ENV", "")), Path(__file__).resolve().parents[2] / "local.env",
                      *[p / "local.env" for p in Path(__file__).resolve().parents]):
        if candidate.is_file():
            for line in candidate.read_text().splitlines():
                if line.startswith("JEV_API_KEY="):
                    return line.split("=", 1)[1].strip().strip("'\"")
    sys.exit("No JEV_API_KEY in local.env")


class Jev:
    def __init__(self, cache_dir: Path):
        self.key = jev_key()
        self.cache = cache_dir
        self.cache.mkdir(parents=True, exist_ok=True)
        self.lock = threading.Lock()
        self.tokens = 0
        self.calls = 0
        self.seconds: list[float] = []

    def ask(self, state, questions: dict) -> dict:
        body = json.dumps({"state": state, "model": JEV_MODEL, "questions": questions}, sort_keys=True)
        path = self.cache / (hashlib.sha256(body.encode()).hexdigest() + ".json")
        if path.exists():
            return json.loads(path.read_text())
        for attempt in range(5):
            started = time.time()
            req = urllib.request.Request(JEV_URL, data=body.encode(), method="POST", headers={
                "Authorization": "Bearer " + self.key, "Content-Type": "application/json"})
            try:
                with urllib.request.urlopen(req, timeout=60) as resp:
                    out = json.load(resp)
                with self.lock:
                    self.calls += 1
                    self.seconds.append(time.time() - started)
                    self.tokens += out.get("usage", {}).get("input_tokens", 0)
                path.write_text(json.dumps(out))
                return out
            except urllib.error.HTTPError as e:
                if e.code in (429, 529) or e.code >= 500:
                    time.sleep(2 * (attempt + 1))
                    continue
                sys.exit(f"Jev refused the request: HTTP {e.code}")  # the key is never in the message
            except (urllib.error.URLError, TimeoutError):
                time.sleep(2 * (attempt + 1))
        sys.exit("Jev unavailable after retries")


def aisle_name(aisle_id: str) -> str:
    return {"fruitVeg": "fruit and veg", "dairyEggs": "dairy and eggs", "chilledDeli": "chilled and deli",
            "tinsJars": "tins and jars", "bakery": "bakery", "meatFish": "meat and fish", "frozen": "frozen",
            "pastaRiceGrains": "pasta, rice and grains", "saucesOils": "sauces, oils and condiments",
            "bakingSpices": "baking and spices", "breakfastCereal": "breakfast and cereal",
            "snacksSweets": "snacks and sweets", "drinks": "drinks"}.get(aisle_id, "other")


# Variants: how the facts are given, and how the question is worded.
INSTRUCTION = {
    "q1": "This household usually buys this item. It is time to buy it again this week.",
    "q2": ("The household shops about once a week. Given how often they usually buy this item and how long it is since "
           "they last bought it, they will probably need to buy it again in the next 7 days."),
}


def state_for(variant: str, f: dict) -> dict:
    gaps = f["gaps"]
    if variant == "v1":  # numbers as they are
        return {"item": f["key"], "aisle": aisle_name(f["aisle"]), "times_bought_on_different_days": f["purchase_days"],
                "days_between_purchases": gaps, "days_since_last_bought": f["days_since_last"]}
    words = (f"Bought on {f['purchase_days']} different days. Usually about every {round(f['median_gap'])} days "
             f"(gaps in days: {', '.join(str(g) for g in gaps)}). Last bought {f['days_since_last']} days ago.")
    if variant == "v2":  # the same facts, in words
        return {"item": f["key"], "aisle": aisle_name(f["aisle"]), "history": words}
    if variant == "v3":  # plus how it keeps and how fast it goes
        extra = []
        if f["shelf_life_days"]:
            extra.append(f"Unopened it keeps for about {f['shelf_life_days']} days.")
        if f["median_lasted"] is not None:
            extra.append(f"A pack is usually used up about {round(f['median_lasted'])} days after it is bought.")
        return {"item": f["key"], "aisle": aisle_name(f["aisle"]), "history": words + (" " + " ".join(extra) if extra else "")}
    raise SystemExit(f"unknown variant {variant}")


def jev_single(jev: Jev, variant: str, question: str, cands: list[dict]) -> list[float]:
    """One request per candidate (its own state), a few in parallel."""
    def one(f):
        out = jev.ask(state_for(variant, f), {"due": {"type": "noul", "instructions": INSTRUCTION[question]}})
        return out["answers"]["due"]["noul"]

    with ThreadPoolExecutor(max_workers=4) as pool:
        return list(pool.map(one, cands))


def jev_batch(jev: Jev, variant: str, question: str, cands: list[dict]) -> list[float]:
    """One request: every candidate's facts in the state, one question per candidate."""
    state = {"items": [{"id": f"i{n}", **state_for(variant, f)} for n, f in enumerate(cands)]}
    questions = {f"i{n}": {"type": "noul", "instructions": f"For item i{n} ({f['key']}): " + INSTRUCTION[question]}
                 for n, f in enumerate(cands)}
    out = jev.ask(state, questions)["answers"]
    return [out[f"i{n}"]["noul"] for n in range(len(cands))]


# ---- the backtest -----------------------------------------------------------------------------------------------------

def backtest(directory: Path, asofs: list[int], use_jev: bool, variants: list[str], modes: list[str]) -> None:
    data = Data(directory)
    today = data.last_day
    print(f"history to {today}; purchases counted since {HISTORY_START} (UK days); 'due' = bought again within {WINDOW_DAYS} days")
    jev = Jev(directory / "jev-cache") if use_jev else None
    pooled = defaultdict(list)  # name -> [(score, truth)]
    detail = []
    for back in asofs:
        asof = today - timedelta(days=back)
        if asof + timedelta(days=WINDOW_DAYS) > today + timedelta(days=1):
            print(f"\nas of {asof}: the following {WINDOW_DAYS} days aren't complete yet, skipped")
            continue
        cands = data.candidates(asof)
        print(f"\n== as of {asof}: {len(cands)} candidates (2+ purchase days before then), "
              f"{sum(c['truth'] for c in cands)} bought again within {WINDOW_DAYS} days")
        pooled["plain rule: days since last / median gap"] += [(ratio(c), c["truth"]) for c in cands]
        pooled["plain rule: how often (purchase days a week)"] += [(per_week(c), c["truth"]) for c in cands]
        pooled["plain rule: how often x how overdue"] += [(freq_and_due(c), c["truth"]) for c in cands]
        if jev:
            for mode in modes:
                for variant in variants:
                    for question in INSTRUCTION:
                        runner = jev_single if mode == "single" else jev_batch
                        probs = runner(jev, variant, question, cands)
                        name = f"Jev {mode} {variant} {question}"
                        pooled[name] += [(p, c["truth"]) for p, c in zip(probs, cands)]
                        detail.append((asof, name, [(c["key"], round(p, 2), c["truth"]) for p, c in zip(probs, cands)]))
    print("\n=== pooled over all as-of dates ===")
    show("plain rule: days since last / median gap", pooled.pop("plain rule: days since last / median gap"),
         [0.5, 0.7, 0.8, 0.9, 1.0, 1.2, 1.5])
    show("plain rule: how often (purchase days a week)", pooled.pop("plain rule: how often (purchase days a week)"),
         [0.25, 0.5, 0.75, 1.0, 1.5])
    show("plain rule: how often x how overdue", pooled.pop("plain rule: how often x how overdue"),
         [0.25, 0.5, 0.75, 1.0, 1.5])
    for name, scored in pooled.items():
        show(name, scored, [0.3, 0.4, 0.5, 0.6, 0.7, 0.8])
    if jev:
        print(f"\nJev: {jev.calls} calls this run (cached ones not counted), {jev.tokens} input tokens "
              f"(~${jev.tokens * PRICE_PER_MTOK_INPUT / 1e6:.4f}); latency per call "
              + (f"median {statistics.median(jev.seconds):.2f}s, max {max(jev.seconds):.2f}s" if jev.seconds else "n/a"))
    (directory / "backtest-detail.json").write_text(json.dumps(detail, default=str))


# ---- by shop week (the household does one big shop a week) ------------------------------------------------------------

MAIN_SHOP_MIN_ITEMS = 25  # a day with at least this many check-ins is the big weekly shop; top-ups belong to its week


def shop_weeks(data: Data) -> list[date]:
    per = defaultdict(int)
    for v in data.purchases.values():
        for d_in, _, _ in v:
            if d_in >= HISTORY_START:
                per[d_in] += 1
    return sorted(d for d, n in per.items() if n >= MAIN_SHOP_MIN_ITEMS)


def bought_by_week(data: Data, key: str, shops: list[date], upto: int) -> list[bool]:
    """For each shop week before index `upto`: was it checked in at any time from that shop up to the next one."""
    days = {d for d, _, _ in data.purchases[key]}
    out = []
    for i in range(upto):
        end = shops[i + 1] if i + 1 < len(shops) else date.max
        out.append(any(shops[i] <= d < end for d in days))
    return out


def week_state(variant: str, key: str, aisle: str, seq: list[bool], shelf: int | None) -> dict:
    n = len(seq)
    k = sum(seq)
    yes_no = ", ".join("yes" if b else "no" for b in seq)
    ago = next((n - 1 - i for i in range(n - 1, -1, -1) if seq[i]), None)
    since = "bought in the most recent shop" if ago == 0 else f"last bought {ago} shops ago"
    text = (f"The household does one big shop a week. In the last {n} weekly shops (oldest first), was this bought that "
            f"week? {yes_no}. Bought in {k} of {n} weeks; {since}.")
    if variant == "w2" and shelf:
        text += f" Unopened it keeps for about {shelf} days."
    return {"item": key, "aisle": aisle_name(aisle), "history": text}


WEEK_QUESTIONS = {
    "w1": "This household usually buys this item. They will want it on the shopping list for the next weekly shop.",
    "w2": ("They are about to write the list for the next weekly shop. Given how often this item has been in their weekly "
           "shops, it will probably be needed on this one."),
}


def calendar_weeks(data: Data) -> list[date]:
    """Mondays (UK) from the stock take's week to the latest check-in: plain Monday-to-Sunday weeks."""
    monday = HISTORY_START - timedelta(days=HISTORY_START.weekday())
    out = []
    while monday <= data.last_day:
        out.append(monday)
        monday += timedelta(days=7)
    return out


def weekly(directory: Path, use_jev: bool, variants: list[str], iso: bool = False) -> None:
    data = Data(directory)
    shops = calendar_weeks(data) if iso else shop_weeks(data)
    print("big shops:", ", ".join(str(d) for d in shops), f"(a day with {MAIN_SHOP_MIN_ITEMS}+ check-ins)")
    jev = Jev(directory / "jev-cache") if use_jev else None
    pooled = defaultdict(list)
    per_shop = defaultdict(lambda: defaultdict(list))  # name -> shop index -> [(score, truth)]
    for i in range(2, len(shops)):  # predicting week i from weeks 0..i-1 (needs two weeks of history)
        rows = []
        for key in data.purchases:
            if key == "?":
                continue
            seq = bought_by_week(data, key, shops, i)
            if sum(seq) < 2:
                continue
            truth = bought_by_week(data, key, shops, i + 1)[i]
            rows.append((key, seq, truth))
        rows.sort()
        print(f"\n== predicting the shop of {shops[i]}: {len(rows)} candidates (bought in 2+ earlier weeks), "
              f"{sum(t for _, _, t in rows)} were bought that week")
        pooled["rule: share of weeks bought"] += [(sum(q) / len(q), t) for _, q, t in rows]
        pooled["rule: share of weeks, recent weeks count more"] += [
            (sum((0.6 ** (len(q) - 1 - j)) * b for j, b in enumerate(q)) / sum(0.6 ** j for j in range(len(q))), t)
            for _, q, t in rows]
        pooled["rule: bought last week"] += [(1.0 if q[-1] else 0.0, t) for _, q, t in rows]
        if jev:
            for variant in variants:
                for qid, text in WEEK_QUESTIONS.items():
                    def one(row, variant=variant, text=text):
                        key, seq, _ = row
                        state = week_state(variant, key, data.aisle.get(key, "other"), seq, data.shelf.get(key))
                        return jev.ask(state, {"due": {"type": "noul", "instructions": text}})["answers"]["due"]["noul"]
                    with ThreadPoolExecutor(max_workers=4) as pool:
                        probs = list(pool.map(one, rows))
                    pooled[f"Jev {variant} {qid}"] += [(p, t) for p, (_, _, t) in zip(probs, rows)]
                    per_shop[f"Jev {variant} {qid}"][i] = [(p, t) for p, (_, _, t) in zip(probs, rows)]
                    share = [sum(q) / len(q) for _, q, _ in rows]
                    pooled[f"Jev {variant} {qid} + share of weeks"] += [
                        (0.5 * p + 0.5 * sh, t) for p, sh, (_, _, t) in zip(probs, share, rows)]
                    per_shop[f"Jev {variant} {qid} + share of weeks"][i] = [
                        (0.5 * p + 0.5 * sh, t) for p, sh, (_, _, t) in zip(probs, share, rows)]
        per_shop["rule: share of weeks bought"][i] = [(sum(q) / len(q), t) for _, q, t in rows]
    print("\n=== precision of the top of the list (what the preview shows), averaged over the shops ===")
    for name, shops_scored in per_shop.items():
        cells = []
        for k in (10, 15, 20, 30):
            ps = [sum(t for _, t in sorted(sc, key=lambda x: -x[0])[:k]) / min(k, len(sc)) for sc in shops_scored.values()]
            cells.append(f"top {k}: {sum(ps) / len(ps):.2f}")
        print(f"  {name:<44} " + "  ".join(cells))
    print("\n=== pooled over the shops ===")
    for name, scored in pooled.items():
        show(name, scored, [0.3, 0.4, 0.5, 0.6, 0.7, 0.8] if name.startswith("Jev") else [0.25, 0.5, 0.75, 1.0])
    if jev:
        print(f"\nJev: {jev.calls} calls this run, {jev.tokens} input tokens (~${jev.tokens * PRICE_PER_MTOK_INPUT / 1e6:.4f}); "
              + (f"latency median {statistics.median(jev.seconds):.2f}s, max {max(jev.seconds):.2f}s" if jev.seconds else ""))


if __name__ == "__main__":
    import argparse

    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    sub = parser.add_subparsers(dest="command", required=True)
    sub.add_parser("dump").add_argument("dir", type=Path)
    bt = sub.add_parser("backtest")
    bt.add_argument("dir", type=Path)
    bt.add_argument("--asof", default="28,21,14,7", help="days ago to pretend it is (at least 6, so the next 7 days are known)")
    bt.add_argument("--no-jev", action="store_true")
    bt.add_argument("--variants", default="v1,v2,v3")
    bt.add_argument("--mode", default="single,batch")
    wk = sub.add_parser("weekly")
    wk.add_argument("dir", type=Path)
    wk.add_argument("--no-jev", action="store_true")
    wk.add_argument("--variants", default="w1,w2")
    wk.add_argument("--iso-weeks", action="store_true", help="plain Monday-to-Sunday weeks instead of detecting the big shop")
    args = parser.parse_args()
    if args.command == "weekly":
        weekly(args.dir, not args.no_jev, args.variants.split(","), args.iso_weeks)
    elif args.command == "dump":
        dump(args.dir)
    else:
        backtest(args.dir, [int(x) for x in args.asof.split(",")], not args.no_jev, args.variants.split(","), args.mode.split(","))
