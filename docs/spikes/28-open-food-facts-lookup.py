#!/usr/bin/env python3
"""Spike #28: look up the sample barcodes on Open Food Facts.

Usage: python3 28-open-food-facts-lookup.py [output-dir] [barcode ...]

Standard library only. No auth, no secrets: Open Food Facts reads are anonymous.
Writes one raw JSON response per barcode to output-dir (default: ./off-results)
and prints a tab-separated summary.
"""
import json
import os
import sys
import time
import urllib.error
import urllib.request

# Contact goes in the User-Agent, as Open Food Facts asks: AppName/Version (contact).
USER_AGENT = "CupboardTracker/0.1 (https://github.com/gary-mcmonagle/KitchenInventory)"
URL = "https://world.openfoodfacts.org/api/v2/product/{}.json?fields={}"
FIELDS = ",".join([
    "code", "product_name", "product_name_en", "generic_name", "brands",
    "quantity", "categories_tags", "image_front_url", "image_front_small_url",
    "lang", "countries_tags", "stores", "completeness", "last_modified_t",
])

# From the scan history Gary posted on the card (2026-10-16), duplicates removed.
BARCODES = [
    "049733830119", "5000354922831", "5032457717533", "20590093", "5060040110010",
    "5903034425762", "7640110700075", "20719395", "4335619399235", "5060424707256",
    "5000328084152", "5010029212464", "5020580560379", "4088600478425", "5000232027306",
    "4056489879305", "20055363", "4056489899112", "4335619013537", "4088600060880",
    "5051140150785", "5000184221241", "615357122468", "4056489901105", "4056489825586",
    "8002210128088", "4056489903703",
]


def lookup(barcode):
    req = urllib.request.Request(URL.format(barcode, FIELDS), headers={"User-Agent": USER_AGENT})
    start = time.monotonic()
    try:
        with urllib.request.urlopen(req, timeout=10) as resp:
            status, body = resp.status, resp.read()
    except urllib.error.HTTPError as e:
        status, body = e.code, e.read()
    elapsed_ms = int((time.monotonic() - start) * 1000)
    try:
        data = json.loads(body)
    except ValueError:
        data = {"_raw": body.decode("utf-8", "replace")[:500]}
    return status, elapsed_ms, data


def lookup_with_backoff(barcode, attempts=4):
    # 429s come back as an nginx HTML page with no Retry-After, so back off blindly.
    for attempt in range(attempts):
        status, ms, data = lookup(barcode)
        if status != 429 and status < 500:
            return status, ms, data
        time.sleep(5 * 2 ** attempt)
    return status, ms, data


def main():
    out_dir = sys.argv[1] if len(sys.argv) > 1 else "off-results"
    barcodes = sys.argv[2:] or BARCODES
    os.makedirs(out_dir, exist_ok=True)
    print("barcode\thttp\tms\tstatus\tname\tbrands\tquantity")
    for barcode in barcodes:
        status, ms, data = lookup_with_backoff(barcode)
        with open(os.path.join(out_dir, barcode + ".json"), "w") as f:
            json.dump({"http": status, "ms": ms, "response": data}, f, indent=2)
        p = data.get("product") or {}
        print("\t".join(str(x) for x in [
            barcode, status, ms, data.get("status_verbose", ""),
            p.get("product_name", ""), p.get("brands", ""), p.get("quantity", ""),
        ]))
        time.sleep(6)  # 1/s still drew 429s, so stay at ~10 reads/minute


if __name__ == "__main__":
    main()
