#!/usr/bin/env python3
"""Spike #41: identify products (several per photo) from phone photos.

Usage (photos are JPEGs, already resized to about 1024 px; never commit them):
  python3 41-photo-recognition.py vision <photo-dir> <out-dir> <deployment>   # gpt-4o or gpt-4.1-mini: items as JSON
  python3 41-photo-recognition.py ocr    <photo-dir> <out-dir>                # Document Intelligence Read: text lines
  python3 41-photo-recognition.py hybrid <photo-dir> <out-dir> <deployment>   # OCR lines + the image to the vision model
  python3 41-photo-recognition.py ocrjev <out-dir>                            # Jev picks name/brand/size from the OCR lines
  python3 41-photo-recognition.py report <out-dir>                            # one table across approaches

Environment (never printed or written):
  AZURE_AI_ENDPOINT  https://<account>.cognitiveservices.azure.com/
  AZURE_AI_TOKEN     an Entra token for https://cognitiveservices.azure.com (az account get-access-token); no keys
  JEV_API_KEY        for ocrjev only
Raw responses are cached in <out-dir> (keep it outside the repo), so reports can be re-run without new calls.
Standard library only.
"""
import base64
import json
import os
import sys
import time
import urllib.error
import urllib.request

OPENAI_API = "2024-10-21"
DI_API = "2024-11-30"

INSTRUCTIONS = (
    "You identify the food and drink items in a photo taken in a UK home kitchen, for a household stock tracker. "
    "List every distinct product you can see. Count identical items together in quantity. "
    "name: what the product is, in English, the way a UK shopper writes it on a shopping list (not the brand, size "
    "or marketing text). brand: the brand printed on the pack (a supermarket's own brand counts); null for loose items "
    "or if you can't see one. size: the pack size or weight as printed (e.g. '415g', '2 pint'); null if not visible. "
    "kind: packaged (a branded or own-brand pack), loose (fruit, veg, bakery or anything without a pack or label), "
    "storeLabelled (a pack with a label printed in the shop, e.g. deli or weighed items: read the product name from the "
    "label), or notFood. Confidences are 0 to 1 and honest: lower when you are guessing from shape or colour, or the "
    "text is cut off. shelfLifeDays: how long it usually keeps after buying, unopened and stored the usual way. "
    "readable: false if the photo is too blurry or dark to tell what anything is."
)

# v2 (after Gary's review: what the food is matters most; brand and size are a bonus). Picked with PROMPT=v2
# (PROMPT=v2b repeats it, to check the answers are repeatable).
INSTRUCTIONS_V2 = (
    "You identify the food and drink in a photo taken in a UK home kitchen, for a household stock tracker. "
    "The most important thing is WHAT each item is, specifically enough to shop for it again: e.g. 'milk chocolate "
    "fruit and nut bar', 'pitted green olives', 'cabernet sauvignon red wine', 'soured cream', 'leeks', 'baguette'. "
    "List each distinct item you can clearly identify. Leave out anything you can only guess at from a blurry shape "
    "or colour in the background. Count identical items together in quantity. "
    "brand and size are optional extras: give them ONLY if you can read them on that item's own pack in this photo. "
    "Never take a brand or size from a neighbouring item, and never fill one in from what's typical: use null instead. "
    "Use real JSON null, never the text 'null' or 'unknown'. "
    "kind: packaged (a branded or own-brand pack), loose (fruit, veg, or bakery bread in a plain bag), storeLabelled "
    "(ONLY when you can see a label printed in the shop with a price or weight, e.g. deli or weighed items), or notFood. "
    "Confidences are 0 to 1: 1 only when it's printed clearly on the item; lower when you're inferring. "
    "shelfLifeDays: how long it usually keeps after buying, unopened and stored the usual way. "
    "readable: false if the photo is too blurry or dark to tell what anything is."
)

PROMPT = os.environ.get("PROMPT", "v1")

NULLABLE_STRING = {"type": ["string", "null"]}
SCHEMA = {
    "name": "kitchen_photo",
    "strict": True,
    "schema": {
        "type": "object",
        "additionalProperties": False,
        "required": ["readable", "items"],
        "properties": {
            "readable": {"type": "boolean"},
            "items": {
                "type": "array",
                "items": {
                    "type": "object",
                    "additionalProperties": False,
                    "required": ["name", "brand", "size", "kind", "quantity", "nameConfidence", "brandConfidence",
                                 "sizeConfidence", "shelfLifeDays"],
                    "properties": {
                        "name": {"type": "string"},
                        "brand": NULLABLE_STRING,
                        "size": NULLABLE_STRING,
                        "kind": {"type": "string", "enum": ["packaged", "loose", "storeLabelled", "notFood"]},
                        "quantity": {"type": "integer"},
                        "nameConfidence": {"type": "number"},
                        "brandConfidence": {"type": "number"},
                        "sizeConfidence": {"type": "number"},
                        "shelfLifeDays": {"type": ["integer", "null"]},
                    },
                },
            },
        },
    },
}


def env(name):
    value = os.environ.get(name)
    if not value:
        sys.exit(f"Set {name} in the environment.")
    return value


def post(url, body, headers, timeout=60):
    req = urllib.request.Request(url, data=json.dumps(body).encode(), method="POST",
                                 headers={"Content-Type": "application/json", **headers})
    start = time.monotonic()
    try:
        with urllib.request.urlopen(req, timeout=timeout) as resp:
            return resp.status, dict(resp.headers), resp.read(), int((time.monotonic() - start) * 1000)
    except urllib.error.HTTPError as e:
        return e.code, dict(e.headers), e.read(), int((time.monotonic() - start) * 1000)
    except (urllib.error.URLError, TimeoutError) as e:
        return 0, {}, json.dumps({"error": str(e)}).encode(), int((time.monotonic() - start) * 1000)


def photos(photo_dir):
    return sorted(f for f in os.listdir(photo_dir) if f.lower().endswith(".jpg"))


def image_url(path):
    with open(path, "rb") as f:
        return "data:image/jpeg;base64," + base64.b64encode(f.read()).decode()


def cache(out_dir, name, produce):
    path = os.path.join(out_dir, name)
    if os.path.exists(path):
        with open(path, encoding="utf-8") as f:
            return json.load(f)
    record = produce()
    with open(path, "w", encoding="utf-8") as f:
        json.dump(record, f, indent=2, ensure_ascii=False)
    return record


def chat(deployment, content):
    endpoint, token = env("AZURE_AI_ENDPOINT"), env("AZURE_AI_TOKEN")
    url = f"{endpoint}openai/deployments/{deployment}/chat/completions?api-version={OPENAI_API}"
    body = {
        "messages": [{"role": "system", "content": INSTRUCTIONS_V2 if PROMPT.startswith("v2") else INSTRUCTIONS},
                     {"role": "user", "content": content}],
        "response_format": {"type": "json_schema", "json_schema": SCHEMA},
        "temperature": 0,
    }
    for attempt in range(4):
        status, _, raw, ms = post(url, body, {"Authorization": "Bearer " + token})
        if status in (0, 429) or status >= 500:
            time.sleep(5 * 2 ** attempt)
            continue
        break
    data = json.loads(raw)
    if status != 200:
        return {"http": status, "ms": ms, "error": data.get("error", data)}
    return {"http": status, "ms": ms, "usage": data.get("usage"),
            "result": json.loads(data["choices"][0]["message"]["content"])}


def vision(photo_dir, out_dir, deployment):
    os.makedirs(out_dir, exist_ok=True)
    for p in photos(photo_dir):
        prefix = deployment if PROMPT == "v1" else f"{PROMPT}-{deployment}"
        rec = cache(out_dir, f"{prefix}-{p}.json", lambda: chat(deployment, [
            {"type": "text", "text": "What's in this photo?"},
            {"type": "image_url", "image_url": {"url": image_url(os.path.join(photo_dir, p)), "detail": "high"}},
        ]))
        print(p, rec["http"], rec["ms"], "ms", json.dumps(rec.get("result", rec.get("error")), ensure_ascii=False)[:300])


def read_ocr(path):
    endpoint, token = env("AZURE_AI_ENDPOINT"), env("AZURE_AI_TOKEN")
    with open(path, "rb") as f:
        b64 = base64.b64encode(f.read()).decode()
    url = f"{endpoint}documentintelligence/documentModels/prebuilt-read:analyze?api-version={DI_API}"
    start = time.monotonic()
    status, headers, raw, _ = post(url, {"base64Source": b64}, {"Authorization": "Bearer " + token})
    if status != 202:
        return {"http": status, "error": raw.decode("utf-8", "replace")[:300]}
    location = headers.get("Operation-Location") or headers.get("operation-location")
    while True:
        time.sleep(1)
        req = urllib.request.Request(location, headers={"Authorization": "Bearer " + token})
        with urllib.request.urlopen(req, timeout=30) as resp:
            data = json.loads(resp.read())
        if data.get("status") in ("succeeded", "failed"):
            break
    ms = int((time.monotonic() - start) * 1000)
    lines = [l["content"] for page in data.get("analyzeResult", {}).get("pages", []) for l in page.get("lines", [])]
    return {"http": 200, "ms": ms, "status": data.get("status"), "lines": lines}


def ocr(photo_dir, out_dir):
    os.makedirs(out_dir, exist_ok=True)
    for p in photos(photo_dir):
        rec = cache(out_dir, f"ocr-{p}.json", lambda: read_ocr(os.path.join(photo_dir, p)))
        print(p, rec.get("ms"), "ms", rec.get("lines", rec.get("error")))


def hybrid(photo_dir, out_dir, deployment):
    os.makedirs(out_dir, exist_ok=True)
    for p in photos(photo_dir):
        lines = cache(out_dir, f"ocr-{p}.json", lambda: read_ocr(os.path.join(photo_dir, p))).get("lines", [])
        rec = cache(out_dir, f"hybrid-{deployment}-{p}.json", lambda: chat(deployment, [
            {"type": "text", "text": "What's in this photo? Text read from it by OCR (may contain errors): "
                                     + json.dumps(lines, ensure_ascii=False)},
            {"type": "image_url", "image_url": {"url": image_url(os.path.join(photo_dir, p)), "detail": "high"}},
        ]))
        print(p, rec["http"], rec["ms"], "ms", json.dumps(rec.get("result", rec.get("error")), ensure_ascii=False)[:300])


def jev(state, questions):
    body = {"state": state, "model": "jev-1.13.0", "questions": questions}
    for attempt in range(4):
        status, _, raw, ms = post("https://api.typesafe.ai/v1/systemone", body,
                                  {"Authorization": "Bearer " + env("JEV_API_KEY")}, timeout=30)
        if status in (429, 529) or status >= 500:
            time.sleep(2 * 2 ** attempt)
            continue
        break
    return {"http": status, "ms": ms, "response": json.loads(raw)}


def ocrjev(out_dir):
    """Approach 1: Jev picks the name, brand and size lines from the OCR text (spike #40's v2 prompt)."""
    for f in sorted(os.listdir(out_dir)):
        if not f.startswith("ocr-"):
            continue
        lines = json.load(open(os.path.join(out_dir, f), encoding="utf-8")).get("lines", [])[:250]
        if not lines:
            continue
        options = {f"L{i + 1}": l for i, l in enumerate(lines)}
        options["none"] = "None of these lines"

        def q(instructions):
            return {"type": "choice", "instructions": instructions, "criteria": options}

        rec = cache(out_dir, f"jev-{f[4:]}", lambda: jev({"ocrLines": lines}, {
            "name": q("Which line is the product's name: what the product is, as you would write it on a shopping "
                      "list. Not the brand, the size, ingredients, or storage advice. If the name is printed in "
                      "several languages, choose the English line."),
            "brand": q("Which line is the brand or manufacturer name only. The brand is the product's own brand "
                       "printed on the front of the pack. A shop's or retailer's name, address or country line is "
                       "not the brand."),
            "size": q("Which line gives the pack size, weight or volume of the product."),
        }))
        answers = rec["response"].get("answers", {})
        pick = {k: (options.get(answers.get(k, {}).get("choice"), None), round(answers.get(k, {}).get("confidence", 0), 2))
                for k in ("name", "brand", "size")}
        print(f[4:], rec["ms"], "ms", pick)


def report(out_dir):
    names = sorted(f[len("gpt-4o-"):] for f in os.listdir(out_dir) if f.startswith("gpt-4o-IMG"))
    for p in names:
        print(f"\n=== {p}")
        for prefix in ("gpt-4o", "gpt-4.1-mini", "hybrid-gpt-4o", "v2-gpt-4o", "v2-gpt-4.1-mini", "v2b-gpt-4o"):
            path = os.path.join(out_dir, f"{prefix}-{p}")
            if not os.path.exists(path):
                continue
            rec = json.load(open(path, encoding="utf-8"))
            items = rec.get("result", {}).get("items", [])
            u = rec.get("usage") or {}
            print(f"  {prefix:14} {rec['ms']:>5} ms  in {u.get('prompt_tokens')} out {u.get('completion_tokens')}")
            for i in items:
                print(f"     {i['quantity']}× {i['name']} | {i['brand']} | {i['size']} | {i['kind']} | "
                      f"conf {i['nameConfidence']:.2f}/{i['brandConfidence']:.2f}/{i['sizeConfidence']:.2f} | "
                      f"{i['shelfLifeDays']} d")
        jpath = os.path.join(out_dir, f"jev-{p}")
        if os.path.exists(jpath):
            ocr_lines = json.load(open(os.path.join(out_dir, f"ocr-{p}"), encoding="utf-8")).get("lines", [])
            ans = json.load(open(jpath, encoding="utf-8"))["response"].get("answers", {})
            opts = {f"L{i + 1}": l for i, l in enumerate(ocr_lines[:250])}
            print("  ocr+jev        " + " | ".join(
                f"{k}: {opts.get(ans.get(k, {}).get('choice'), '—')} ({ans.get(k, {}).get('confidence', 0):.2f})"
                for k in ("name", "brand", "size")))


if __name__ == "__main__":
    mode = sys.argv[1] if len(sys.argv) > 1 else ""
    args = sys.argv[2:]
    commands = {"vision": vision, "ocr": ocr, "hybrid": hybrid, "ocrjev": ocrjev, "report": report}
    if mode not in commands:
        sys.exit(__doc__)
    commands[mode](*args)
