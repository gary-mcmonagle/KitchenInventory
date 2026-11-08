#!/usr/bin/env python3
"""Spike #116: the same product shown twice under names that differ in case (or nearly).

Usage (read-only; keep the dump outside the repo, it has product ids and barcodes):
  python3 116-name-casing-duplicates.py dump <out-dir>     # reads products and inventoryItems with your az login
  python3 116-name-casing-duplicates.py report <out-dir>   # the figures and examples, names only (no ids, no barcodes)

`dump` reads every document (no 100-item cap like deploy/verify/cosmos.sh) with SELECT queries only: nothing is
written to Cosmos. Standard library only. The token is fetched in-process and never printed.
"""
import json
import re
import subprocess
import sys
import urllib.error
import urllib.parse
import urllib.request
from collections import Counter, defaultdict
from email.utils import formatdate
from pathlib import Path


def az(*args: str) -> str:
    return subprocess.run(["az", *args], check=True, capture_output=True, text=True).stdout.strip()


def query(endpoint: str, database: str, container: str, sql: str) -> list[dict]:
    base = endpoint.rstrip("/").removesuffix(":443")
    token = az("account", "get-access-token", "--resource", base, "--query", "accessToken", "-o", "tsv")
    auth = urllib.parse.quote(f"type=aad&ver=1.0&sig={token}", safe="")
    url = f"{base}/dbs/{database}/colls/{container}/docs"
    out, continuation = [], None
    while True:
        headers = {
            "Authorization": auth,
            "x-ms-date": formatdate(usegmt=True),
            "x-ms-version": "2018-12-31",
            "Content-Type": "application/query+json",
            "x-ms-documentdb-isquery": "True",
            "x-ms-documentdb-query-enablecrosspartition": "True",
            "x-ms-max-item-count": "200",
        }
        if continuation:
            headers["x-ms-continuation"] = continuation
        req = urllib.request.Request(  # noqa: S310
            url, data=json.dumps({"query": sql, "parameters": []}).encode(), headers=headers, method="POST"
        )
        with urllib.request.urlopen(req, timeout=60) as resp:  # noqa: S310
            out.extend(json.load(resp).get("Documents", []))
            continuation = resp.headers.get("x-ms-continuation")
        if not continuation:
            return out


def dump(out_dir: Path) -> None:
    outputs = json.loads(az("deployment", "sub", "show", "--name", "cupboard-tracker", "--query", "properties.outputs"))
    endpoint, database = outputs["cosmosEndpoint"]["value"], outputs["cosmosDatabaseName"]["value"]
    out_dir.mkdir(parents=True, exist_ok=True)
    products = query(endpoint, database, "products", "SELECT * FROM c WHERE c.type = 'product'")
    units = query(
        endpoint, database, "inventoryItems", "SELECT c.productId, c.status, c.checkedInAt FROM c"
    )
    (out_dir / "products.json").write_text(json.dumps(products))
    (out_dir / "units.json").write_text(json.dumps(units))
    print(f"{len(products)} products, {len(units)} units")


# ---- report ---------------------------------------------------------------------------------------------------------

def name_key(name: str) -> str:
    """Product.NameKeyOf: single spaces, trimmed, lower case."""
    return " ".join(name.split()).lower()


SIZE = re.compile(r"\b\d+([.,]\d+)?\s*(x\s*\d+\s*)?(g|kg|ml|l|cl|litres?|pack|pk|pint|pints)\b", re.IGNORECASE)


def loose_key(name: str, brand: str | None) -> str:
    """The near-duplicate key: no brand, no pack size, no punctuation, no plural s, lower case."""
    n = name.lower()
    if brand:
        n = n.replace(brand.lower(), " ")
    n = SIZE.sub(" ", n)
    n = re.sub(r"[^\w\s]", " ", n)
    words = [re.sub(r"(?<=[a-z])s$", "", w) for w in n.split() if w not in {"the", "of", "and", "with"}]
    return " ".join(sorted(words))


def kind(p: dict) -> str:
    source = p.get("source") or ("noMatch" if p.get("status") != "known" else "?")
    barcoded = bool(p.get("barcodes"))
    return f"{source}{'+barcode' if barcoded else ',loose'}"


def row_title(p: dict) -> tuple[str, str]:
    """FE/src/format.ts stockRowText: the generic name leads unless it is the display name ignoring case."""
    generic = p.get("genericName")
    if generic and name_key(generic) != name_key(p["displayName"]):
        return generic, p["displayName"]
    return p["displayName"], p.get("brand") or ""


def stock_rows(known: list[dict], in_stock: Counter) -> None:
    """What the stock list shows: one row per in-stock product. Which rows would look like the same thing?"""
    rows = [p for p in known if in_stock.get(p["id"], 0) > 0]
    print(f"\n== stock list: {len(rows)} rows (one per in-stock product)")
    by_title = defaultdict(list)
    for p in rows:
        by_title[name_key(row_title(p)[0])].append(p)
    dupes = [g for g in by_title.values() if len(g) > 1]
    spelled = [g for g in dupes if len({row_title(p)[0] for p in g}) > 1]
    print(f"rows whose title is the same ignoring case: {len(dupes)} groups, {sum(len(g) for g in dupes)} rows;"
          f" shown with different casing: {len(spelled)} groups, {sum(len(g) for g in spelled)} rows")
    for g in sorted(dupes, key=lambda g: row_title(g[0])[0].lower()):
        shown = " | ".join(f"{row_title(p)[0]!r} ({row_title(p)[1]!r}, x{in_stock[p['id']]}, {kind(p)})" for p in g)
        print(("  [case differs] " if len({row_title(p)[0] for p in g}) > 1 else "  [same text]    ") + shown)
    by_generic = defaultdict(list)
    for p in rows:
        if p.get("genericName"):
            by_generic[name_key(p["genericName"])].append(p)
    multi = [g for g in by_generic.values() if len(g) > 1]
    print(f"in-stock products sharing a generic name: {len(multi)} groups, {sum(len(g) for g in multi)} rows"
          f" (of which looking identical by title: counted above)")


def report(out_dir: Path) -> None:
    products = json.loads((out_dir / "products.json").read_text())
    units = json.loads((out_dir / "units.json").read_text())
    in_stock = Counter(u["productId"] for u in units if u.get("status") == "inStock")
    known = [p for p in products if p.get("status") == "known"]
    print(f"products {len(products)} (known {len(known)}), units {len(units)}, in stock {sum(in_stock.values())}")
    print("by source:", dict(Counter(kind(p) for p in known)))

    def line(p: dict) -> str:
        generic = p.get("genericName")
        return (
            f"    {p['displayName']!r:50} {kind(p):24} named={str(p.get('namedAt') or '')[:10] or '-':10} "
            f"stock={in_stock.get(p['id'], 0)} generic={generic!r} aisle={p.get('aisle')!r}"
        )

    def group(label: str, key) -> list[list[dict]]:
        groups = defaultdict(list)
        for p in known:
            groups[key(p)].append(p)
        dupes = [g for g in groups.values() if len(g) > 1]
        print(f"\n== {label}: {len(dupes)} groups, {sum(len(g) for g in dupes)} products")
        for g in sorted(dupes, key=lambda g: g[0]["displayName"].lower()):
            print(f"  [{len(g)}]")
            for p in g:
                print(line(p))
        return dupes

    exact = group("same name ignoring case and spaces (NameKey)", lambda p: name_key(p["displayName"]))
    sizes = Counter()
    for g in exact:
        spelled = {p["displayName"] for p in g}
        sizes["different spelling (case or spaces)" if len(spelled) > 1 else "identical text"] += 1
    print("\nexact groups by text:", dict(sizes))
    group("near duplicates (no brand, size, punctuation, plural s)",
          lambda p: loose_key(p["displayName"], (p.get("brand") or None)))

    stock_rows(known, in_stock)

    caps = [p for p in known if p["displayName"] == p["displayName"].upper() and re.search(r"[A-Z]", p["displayName"])]
    print(f"\n== all-capitals names: {len(caps)}", dict(Counter(kind(p) for p in caps)))
    for p in caps[:15]:
        print(line(p))
    odd = [p for p in known if p["displayName"] != " ".join(p["displayName"].split())]
    print(f"\n== names with stray spaces: {len(odd)}")
    print("\n== casing styles of known names:",
          dict(Counter(
              "ALL CAPS" if p["displayName"] == p["displayName"].upper()
              else "lower" if p["displayName"] == p["displayName"].lower()
              else "Title Case" if p["displayName"] == p["displayName"].title()
              else "Sentence/mixed"
              for p in known)))


if __name__ == "__main__":
    if len(sys.argv) != 3 or sys.argv[1] not in {"dump", "report"}:
        sys.exit(__doc__)
    {"dump": dump, "report": report}[sys.argv[1]](Path(sys.argv[2]))
