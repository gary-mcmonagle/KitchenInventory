# Spike #28: Open Food Facts coverage for UK products

Card: https://trello.com/c/QHfaL3Iw

## Question

How well does Open Food Facts identify the products in our house, and what exactly can we get from it? If coverage is poor, the no-match path becomes the main path and feature priorities change.

## Summary

- **Coverage is excellent.** 27 of 27 sample barcodes are a match; **26 of 27 (96%)** have a name we can show. The no-match path is an edge case, not the main path. No alternative source is needed.
- **Use the v3 product endpoint**, look up the barcode exactly as scanned, and build the display name from `product_name_en`, falling back to `product_name`.
- **The rate limit is tight: 15 product reads a minute per IP.** Normal use (tens of scans a day) is fine, but a stock-take burst will hit it. Treat 429 as transient and back off.
- A 404 with `product_not_found` is a **no-match**. Everything else that isn't a 200 (429, 5xx, timeouts, non-JSON) is **transient** and should end as `lookupFailed` after retries.

## Sample

Gary's scan history on the card (2026-10-16): 30 scans, which are **27 unique product barcodes**, 1 repeat (`4056489899112`, scanned twice) and 2 QR codes (URLs, not product barcodes). The repeat and the QR codes are left out of the hit rate.

The category for each barcode is my call from the brand. "Fresh" here means chilled or short-life packaged food. The sample has **no loose or weighed items, store-labelled items or obvious freezer items**, so coverage for those is untested (see Limitations).

## Results

Looked up on 2026-10-16 with [`28-open-food-facts-lookup.py`](28-open-food-facts-lookup.py).

| Barcode | Category | Match? | Display name (proposed) | Brand | Quantity | Notes |
|---|---|---|---|---|---|---|
| 049733830119 | Branded | Yes | Chipotle Hot Sauce | McCormick & Company Inc. | 150 ml | UPC-A; OFF returns the code as `0049733830119`. All-caps name. |
| 5000354922831 | Branded | Yes | Beef Gravy | Bisto | 230g | |
| 5032457717533 | Branded | Yes | Guinness Spicy Chipotle Rub | Guinness | – | No categories. |
| 5060040110010 | Branded | Yes | Clarks Original Maple Syrup 180Ml | Clarks | 180 ml | Main language `fr`, but `product_name_en` present. Size is repeated in the name. |
| 5903034425762 | Branded | Yes | Polish Honey multiflower | Mazurskie Miody | 900 g | |
| 7640110700075 | Branded | Yes | Granular low calorie sweetener | Canderel | 75g | |
| 5060424707256 | Branded | Yes | Warrior Rage Pre Workout | – | – | Bare entry: no brand, image or categories. |
| 5000328084152 | Branded | Yes | Salt & Pepper Chicken Flavour Potato Crisps | Walkers | – | No categories. |
| 5010029212464 | Branded | Yes | Weetabix Chocolate | Weetabix | `N.24 -` | Junk quantity. |
| 5020580560379 | Branded | Yes | Topokki Hot And Spicy Pot | Sun Hee | – | No categories. |
| 5000232027306 | Branded | Yes | Lasagna sheets | Napolina | 375 g | |
| 5000184221241 | Branded | Yes | Knorr All Purpose Seasoning Aromat | Knorr | 90g | Main language `sv`; `product_name_en` present. |
| 615357122468 | Branded | Yes | Rich Italian Coffee Bags | Taylors of Harrogate | 75 g | UPC-A; returned as `0615357122468`. |
| 8002210128088 | Branded | Yes | Garlic flavoured olive oil | Filippo Berio | 250 ml | |
| 20719395 | Own-brand (Lidl) | Yes | Whole Chia Seeds | Alesto | 200g | EAN-8. |
| 4335619399235 | Own-brand (Lidl) | Yes | Walnut halves | Alesto | 200g | |
| 4088600478425 | Own-brand (Aldi) | Yes | Chopped Tomatoes in a Rich Tomato Juice | Cucina | 400 g | |
| 4056489879305 | Own-brand (Lidl) | Yes, **not usable** | – | taste of deutschland | 250 | Only name is `горчица` (Bulgarian for "mustard"), with no English name. |
| 4335619013537 | Own-brand (Lidl) | Yes | Mild Peri Peri Mayo V | BATTS | – | All-caps name with a stray "V". |
| 5051140150785 | Own-brand (Tesco) | Yes | 50% Less Sodium Salt | Tesco | – | |
| 4056489901105 | Own-brand (Lidl) | Yes | Crunchy Peanut Butter | Maribel | 1 kg | |
| 4056489825586 | Own-brand (Lidl) | Yes | Swiss style museli | Brownfield | 1 kg | Typo in OFF data. |
| 4056489903703 | Own-brand (Lidl) | Yes | Chickpeas in Water | Freshona | 400g | |
| 20590093 | Fresh (Lidl) | Yes | Easy Peelers | Deluxe | – | EAN-8. Packed fruit. |
| 20055363 | Fresh (Lidl) | Yes | Plain Naan Breads | Taste of | 2 x 130 g | EAN-8. All-caps name; brand truncated in OFF. |
| 4056489899112 | Fresh (Lidl) | Yes | Semi-Skimmed British Milk | Dairy Manor | – | All-caps name; no image or categories. Scanned twice. |
| 4088600060880 | Fresh (Aldi) | Yes | Greek 10% fat authentic yogurt | Brooklea | 500g | |
| `https://lwvk.st4.ch/…` | – | n/a | – | – | – | QR code, not a product barcode. Not looked up. |
| `https://1nk.uk/…` | – | n/a | – | – | – | QR code, not a product barcode. Not looked up. |

## Hit rate

| Category | Sample | Match | Usable name |
|---|---|---|---|
| Branded | 14 | 14 (100%) | 14 (100%) |
| Own-brand | 9 | 9 (100%) | 8 (89%) |
| Fresh (packaged) | 4 | 4 (100%) | 4 (100%) |
| **Total** | **27** | **27 (100%)** | **26 (96%)** |

Well above the ~70% threshold, so alternatives weren't checked in depth (see Alternatives).

## Fields

Present across the 27 matches:

| Field | Present | Verdict |
|---|---|---|
| `product_name` | 27 | Always there, but it's in the product's **main language** (`lang`), which isn't always English (`bg`, `fr`, `sv` in this sample). |
| `product_name_en` | 26 | The best source for the display name. |
| `brands` | 26 | Comma-separated, free text. Usually one brand. Occasionally truncated ("Taste of"). |
| `quantity` | 19 (70%) | Free text, inconsistent ("230g", "2 x 130 g", "250", "N.24 -"). Display only; never parse it. |
| `image_front_url` / `image_front_small_url` | 25 | Reliable. CC BY-SA. |
| `categories_tags` | 22 | Normalised tags (e.g. `en:gravy`, `en:naans`). Useful later as a hint for shelf life. |
| `generic_name` | 5 | Too sparse to rely on. |
| `stores` | 10 | Sparse and messy ("Lidil"). Not useful. |
| `lang`, `countries_tags` | 27 | Not needed in the cache. |

### Recommended fields to cache

Match the field names already reserved in [`docs/data-model.md`](../data-model.md), plus one:

| Cache field | From | Notes |
|---|---|---|
| `displayName` | built (below) | |
| `brand` | first entry of `brands`, trimmed | Optional. |
| `quantity` | `quantity`, trimmed | Optional, shown as-is. |
| `imageUrl` | `image_front_small_url` (200 px), else `image_front_url` | Optional. The mockups use an initial tile, so this is for later. |
| `categoryTags` | `categories_tags` | **New.** Optional; a cheap hint for the shelf-life estimate later. |

### Building the display name

1. Take the first non-empty of `product_name_en`, then `product_name`.
2. Trim it and collapse runs of whitespace.
3. If it's **all capitals**, convert it to title case ("SEMI-SKIMMED BRITISH MILK" → "Semi-Skimmed British Milk"). Otherwise keep OFF's casing.
4. If the result isn't mostly **Latin script** (as with "горчица"), or step 1 finds nothing, treat the barcode as a **no-match** and use `Unknown item (<barcode>)`. Still cache brand, quantity and image, since they help with naming. *Needs Gary's confirmation; see Decisions.*
5. **Don't** add the brand or size to the display name. The mockups show them separately (`brand · size`). Some OFF names already include the brand ("Knorr All Purpose…") or size ("…180Ml"); leave those alone rather than guessing.

## API usage

- **Endpoint:** `GET https://world.openfoodfacts.org/api/v3/product/{barcode}?fields=product_name,product_name_en,brands,quantity,image_front_url,image_front_small_url,categories_tags`
  - v3 is recommended for new integrations; v2 (`/api/v2/product/{barcode}.json`) is deprecated but still works and returns the same product fields.
  - Always pass `fields`. It keeps responses to a few hundred bytes.
  - Typical response time from here: ~100 ms.
- **Barcode format:** send it **exactly as scanned**. OFF accepts 8-digit EAN-8, 12-digit UPC-A and 13-digit EAN-13. For UPC-A it returns the code zero-padded to 13 digits (`049733830119` → `0049733830119`), so **key the product cache on the scanned barcode, not on OFF's `code`**.
- **Auth:** none for reads, only a custom `User-Agent`.
- **User-Agent:** `AppName/Version (ContactEmail)`, e.g. `CupboardTracker/1.0 (<contact email>)`. The contact must be an **app setting**, never committed, because the repo is public. The spike script used the repo URL instead.
- **Rate limits (from OFF's API docs):** **15 product reads a minute per IP**, 10 searches a minute. There's also a global limit that returns **503**. Abuse can lead to an IP ban. At 1 read a second, the script was getting 429s after 14 reads.
  - Function apps share outbound IPs with other tenants, so we may see 429s even at low volume. Retry with back-off rather than assuming our own traffic caused them.
  - A **stock-take** (dozens of check-ins in a few minutes) will exceed 15 a minute. The retry policy plus the `lookupFailed` retry on the next check-in covers it, but it's better to spread the lookups out (see Follow-ups).
- **Terms:** data under ODbL (attribution required if we display it), images under CC BY-SA, and no accuracy guarantee. OFF also asks integrators to fill in their API usage form and to send *test* traffic to staging (`world.openfoodfacts.net`, basic auth `off`/`off`). Unit tests should mock the HTTP client. An integration test, if we add one, should point the base URL setting at staging.

## Errors

What each case looked like on 2026-10-16:

| Case | HTTP | Body | Treat as |
|---|---|---|---|
| Found | 200 | v3: `"status":"success"`, `result.id: "product_found"`, `product: {…}` | **Match** (if the display name rule gives a name; otherwise no-match). |
| Not found | 404 | v3: `"status":"failure"`, `result.id: "product_not_found"`. v2: `"status":0,"status_verbose":"product not found"` | **No-match** (final). |
| Exists, but not food (e.g. a beauty product) | 404 | v2: `"status_verbose":"product found with a different product type: beauty"` | **No-match**. |
| Invalid code (`abc`, all zeros) | 200 | `"status":0,"status_verbose":"no code or invalid code"` | **No-match**. The Pi and ingest already validate barcodes, so this shouldn't happen. |
| Rate limited | 429 | **nginx HTML page**, not JSON, and no `Retry-After` header | **Transient**: retry with exponential back-off. |
| Global limit / server error | 503 / 5xx | Not observed in this spike | **Transient**. |
| Timeout, connection error, non-JSON 200 | – | – | **Transient**. |

Rule of thumb: **only a 404 (or a 200 without a usable name) is a no-match. Anything else that isn't a successful 200 is a retry**, and `lookupFailed` after the retry policy gives up. Check the HTTP status before parsing the body, because 429s aren't JSON.

## Alternatives

Not needed, since the hit rate is 96%. For the record, the UK options worth a look if that changes would be a paid UPC database (e.g. a GS1 UK-backed lookup) or a supermarket's own API. Neither was evaluated.

## Limitations

- The sample is weighted towards ambient groceries. It has no **loose/weighed** or **store-labelled** items, and nothing obviously from the freezer. Store-labelled barcodes are excluded from lookup anyway ([Feature] Check-in adds to stock).
- **EAN-8 barcodes starting with `2` are not store labels.** Lidl prints its own-brand products with 8-digit codes like `20719395`, and OFF resolves them. Store-label detection must only match the **EAN-13 in-store ranges (prefixes 20–29)**, not every barcode that starts with 2.
- OFF data is crowd-sourced and can be wrong (typos, junk quantities, truncated brands). The product cache never re-fetches, so mistakes stick until renaming known products exists ([Feature] Item detail: edit details).

## Recommendation

Go ahead with the agreed product lookup: product cache first, then Open Food Facts v3. Build the display name as above, cache `displayName`, `brand`, `quantity`, `imageUrl` and `categoryTags`, and classify responses using the Errors table. Keep the no-match path (naming, photo identification) as planned, but it's an edge case: about 1 in 25 products in this sample.

## Decisions for Gary

1. **Non-English-only names.** When OFF only has a name in a non-Latin script (e.g. "горчица"), treat the product as a **no-match** so it gets named? Or show the foreign name as it is?
2. **User-Agent contact.** OFF wants a contact email in the User-Agent. Which address should the app setting hold? It won't be committed.
3. **OFF API usage form.** OFF asks integrators to register their usage. Do you want to fill it in?

## Suggested follow-up cards (not created)

- **[Task] Amend the data model with the Open Food Facts fields:** add `categoryTags`, confirm `brand` / `quantity` / `imageUrl`, and record the display name rule and the non-English rule. This could fold into the existing "[Task] Amend the data model" card.
- **[Task] Spread out Open Food Facts lookups during a stock-take:** a small throttle (e.g. at most 10 lookups a minute) in the lookup client, so a burst of check-ins doesn't trip the 15/min limit or risk an IP ban. This could also be a note on [Feature] Check-in adds to stock.
- **[Task] Open Food Facts attribution in the UI:** a "Product data from Open Food Facts (ODbL)" line, e.g. on item detail or a settings/about screen, to meet the licence.
