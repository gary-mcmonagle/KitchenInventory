# Spike #61: Jev — pick the best Open Food Facts name, or flag it for naming

Card: https://trello.com/c/UYc6A2Wr

## Question

Jev can't rewrite names. It only answers typed questions ([spike #40](40-jev.md)). The question:

- Can Jev **choose** the most readable English name from the name fields Open Food Facts already returns?
- Can it **flag** products whose name is unreadable, so they go to "Waiting for a name"?
- Is either worth doing?

## Summary

- **Choosing a name isn't worth it.**
  - 20 of the 27 sample products have only one name field, so there's nothing to choose from.
  - Of the 7 with more than one:
    - it kept the current name 5 times;
    - it picked a worse name once (the brand "Canderel" instead of "Granular low calorie sweetener");
    - it picked an equally good name once (the yogurt).
  - Two of those answers changed when the option order was reversed.
- **Flagging works well.** We ask one `noul` (yes/no) question: "this name is in English and a UK shopper would understand it". Flag the product if the answer is below **0.5**. There's a wide gap on both sides of that threshold:
  - Every real English name scored **0.69 or more**, including all four all-caps names (0.87–0.97).
  - `горчица` scored **0.05**.
  - Made-up foreign and noisy names scored 0.05–0.27:
    - Latin-script foreign names, which a code check couldn't catch: German, French, Dutch and "Moutarde de Dijon";
    - truncated or code-like names: "Taste of" and "SPC 400G".
- **It fits in the shelf-life request.** The flag question can share one Jev call with the shelf-life score, so each new product still needs only one call. Each call took about 230–350 ms. The two combined calls gave the same answers as the separate ones.
- **Recommendation:**
  - Leave the name choice as it is: `product_name_en`, else `product_name`, exactly as returned.
  - Add the **readability flag to the shelf-life Jev request, for every new Open Food Facts product**, when shelf life is built.
  - A flagged product gets a new status, **`needsName`**. It shows under "Waiting for a name", with Open Food Facts' name as a hint.
  - If Jev fails, a code check (empty or non-Latin script) still catches the obvious cases.
  - Don't build it as a separate feature before shelf life. In the sample it only matters for one product (`горчица`), and naming by hand (#51) already handles that.

## How it was tested

`docs/spikes/61-jev-name-choice.py` (standard library only):

- `fetch <dir>` saves the Open Food Facts record for each of the 27 sample barcodes from [spike #28](28-open-food-facts.md), one request every 7.5 s.
- `report <dir>` lists each product's candidate names and asks Jev the questions below twice: once in normal option order and once reversed. It also sends two combined requests that include the shelf-life question.
- `probe <dir>` asks the readability question about eight made-up names.

Raw responses are cached in `<dir>`, so the report can be re-run without new calls. The key comes from `JEV_API_KEY` in the environment and is never printed or written.

- **Calls:** 64, all `200`, 230–350 ms each.
- **Model:** `jev-1.13.0`.
- **Date:** 2026-10-18.

## 1. Candidate names

Fields checked: `product_name_en`, `product_name`, every `product_name_<lang>`, `generic_name`, `generic_name_<lang>`, `abbreviated_product_name` and `abbreviated_product_name_<lang>`. Values are trimmed. Duplicates are removed ignoring case.

- **20 of 27** have exactly one candidate.
- **7 of 27** have more than one:

| Barcode | Candidates (field: text) |
|---|---|
| 7640110700075 | `product_name_en`: Granular low calorie sweetener · `generic_name`: Aspartame and acesulfame-k based table-top sweetener · `product_name_fr`: Canderel |
| 5010029212464 | `product_name_en`: Weetabix Chocolate · `product_name_fr`: Weetabix Chocolate 24s |
| 5000232027306 | `product_name_en`: Lasagna sheets · `product_name_fi`: Lasagne Pasta |
| 20055363 | `product_name_en`: PLAIN NAAN BREADS · `generic_name`: Naan Bread · `generic_name_de`: Weizenkleingebäck nach indischer Art · `generic_name_fr`: Pains naan à l'indienne · `generic_name_nl`: Naanbrood op indische wijze · `product_name_nl`: Plain naans |
| 4088600060880 | `product_name_en`: Greek 10% fat authentic yogurt · `generic_name`: Natural Greek (strained) yogurt |
| 5051140150785 | `product_name_en`: 50% Less Sodium Salt · `product_name_fr`: 50percent Less Sodium Salt |
| 5000184221241 | `product_name_en`: Knorr All Purpose Seasoning Aromat · `product_name_fr`: All Purpose Seasoning Aromat |

What the extra candidates are:

- **`generic_name`** is the most useful extra field. It's a plain description ("Naan Bread", "Natural Greek (strained) yogurt").
- **The `_<lang>` names** are mostly the same English text, or a translation.
- **`abbreviated_product_name`** never appeared.
- **`4056489879305`** has only `product_name` (`горчица`), so there's no English alternative to choose.

When the name is bad, Open Food Facts usually has nothing better to offer.

## 2. Choosing (Jev `choice`)

- **Before:** today's rule, `product_name_en`, else `product_name`.
- **After:** Jev's choice from the candidates, plus a "none" option.
- **Verdicts:** "same" means the same name. "same (n/a)" means only one candidate, so the answer is forced. **Better** and **worse** mean the name changed.
- **Readable** is the `noul` probability for the *current* name (section 3). Readable below 0.5 means flagged.

| # | Barcode | Before (current name) | Jev choice | Conf. | Stable reversed | Verdict | Readable | Flagged |
|---|---|---|---|---|---|---|---|---|
| 1 | 049733830119 | CHIPOTLE HOT SAUCE | CHIPOTLE HOT SAUCE | 1.00 | yes | same (n/a) | 0.95 | no |
| 2 | 5000354922831 | Beef Gravy | Beef Gravy | 1.00 | yes | same (n/a) | 0.97 | no |
| 3 | 5032457717533 | Guinness Spicy Chipotle Rub | Guinness Spicy Chipotle Rub | 0.98 | yes | same (n/a) | 0.87 | no |
| 4 | 20590093 | Easy Peelers | Easy Peelers | 0.96 | yes | same (n/a) | 0.84 | no |
| 5 | 5060040110010 | Clarks Original Maple Syrup 180Ml | Clarks Original Maple Syrup 180Ml | 0.98 | yes | same (n/a) | 0.96 | no |
| 6 | 5903034425762 | Polish Honey multiflower | Polish Honey multiflower | 0.86 | yes | same (n/a) | 0.91 | no |
| 7 | 7640110700075 | Granular low calorie sweetener | Canderel | 0.67 | yes | **worse** (brand only) | 0.94 | no |
| 8 | 20719395 | Whole Chia Seeds | Whole Chia Seeds | 1.00 | yes | same (n/a) | 0.96 | no |
| 9 | 4335619399235 | Walnut halves | Walnut halves | 1.00 | yes | same (n/a) | 0.96 | no |
| 10 | 5060424707256 | Warrior Rage Pre Workout | Warrior Rage Pre Workout | 1.00 | yes | same (n/a) | 0.94 | no |
| 11 | 5000328084152 | Salt & Pepper Chicken Flavour Potato Crisps | Salt & Pepper Chicken Flavour Potato Crisps | 0.98 | yes | same (n/a) | 0.94 | no |
| 12 | 5010029212464 | Weetabix Chocolate | Weetabix Chocolate | 0.35 | **no** | same | 0.93 | no |
| 13 | 5020580560379 | Topokki Hot And Spicy Pot | Topokki Hot And Spicy Pot | 0.90 | yes | same (n/a) | 0.69 | no |
| 14 | 4088600478425 | Chopped Tomatoes in a Rich Tomato Juice | Chopped Tomatoes in a Rich Tomato Juice | 0.98 | yes | same (n/a) | 0.96 | no |
| 15 | 5000232027306 | Lasagna sheets | Lasagna sheets | 0.76 | yes | same | 0.93 | no |
| 16 | 4056489879305 | горчица | none | 0.68 | yes | **better** (correctly "none") | 0.05 | **yes** |
| 17 | 20055363 | PLAIN NAAN BREADS | PLAIN NAAN BREADS | 0.36 | **no** | same | 0.94 | no |
| 18 | 4056489899112 | SEMI-SKIMMED BRITISH MILK | SEMI-SKIMMED BRITISH MILK | 0.98 | yes | same (n/a) | 0.97 | no |
| 19 | 4335619013537 | MILD PERI PERI MAYO V | MILD PERI PERI MAYO V | 0.80 | yes | same (n/a) | 0.87 | no |
| 20 | 4088600060880 | Greek 10% fat authentic yogurt | Natural Greek (strained) yogurt | 0.35 | yes | same (both fine) | 0.93 | no |
| 21 | 5051140150785 | 50% Less Sodium Salt | 50% Less Sodium Salt | 1.00 | yes | same | 0.95 | no |
| 22 | 5000184221241 | Knorr All Purpose Seasoning Aromat | Knorr All Purpose Seasoning Aromat | 0.79 | yes | same | 0.85 | no |
| 23 | 615357122468 | Rich Italian Coffee Bags | Rich Italian Coffee Bags | 0.98 | yes | same (n/a) | 0.92 | no |
| 24 | 4056489901105 | Crunchy Peanut Butter | Crunchy Peanut Butter | 1.00 | yes | same (n/a) | 0.96 | no |
| 25 | 4056489825586 | Swiss style museli | Swiss style museli | 0.94 | yes | same (n/a) | 0.87 | no |
| 26 | 8002210128088 | Garlic flavoured olive oil | Garlic flavoured olive oil | 1.00 | yes | same (n/a) | 0.96 | no |
| 27 | 4056489903703 | Chickpeas in Water | Chickpeas in Water | 0.94 | yes | same (n/a) | 0.96 | no |

**Result** for the 7 products with a real choice:

- **Same name: 5.** Two of those answers (Weetabix and the naan) had low confidence and changed when the option order was reversed.
- **Worse: 1.** Canderel.
- **Equally good: 1.** The yogurt.

The only useful answer was "none" for `горчица`, and the flag question gives that more clearly. **Choosing doesn't make names better, so don't build it.** The typos and noise listed on the card ("museli", "MAYO V", "180Ml") are single-candidate names. Jev can't fix those, because it can only choose, not rewrite.

## 3. Flagging (Jev `noul`)

**Question:**

> The product name "<current name>" is in English and a UK shopper would understand what the product is from it. Capital letters don't matter.

**Sample results:**

- **Real names:** 0.69–0.97. The lowest are Topokki (0.69), Easy Peelers (0.84), Knorr Aromat (0.85), and "MILD PERI PERI MAYO V" / "Swiss style museli" / the Guinness rub (0.87).
- **`горчица`:** 0.05.

**Probes** (made-up names, with real products' brand and categories as context):

| Name | Readable |
|---|---|
| Weizenkleingebäck nach indischer Art | 0.05 |
| горчица | 0.05 |
| Pains naan à l'indienne | 0.07 |
| Naanbrood op indische wijze | 0.07 |
| Moutarde de Dijon | 0.15 |
| Taste of | 0.27 |
| SPC 400G | 0.27 |
| Gravy | 0.96 |

**Threshold: flag when readable < 0.5.** Every bad name scored 0.27 or less, and every good name scored 0.69 or more, so 0.5 sits in a wide gap. It flags `горчица` and leaves "SEMI-SKIMMED BRITISH MILK" (0.97) alone.

The probes show why Jev beats a code check: a code check catches `горчица` (Cyrillic), but not German, French or Dutch names, which use the Latin alphabet.

### How it fits the data model (proposal: nothing overwritten)

**New product status: `needsName`.** Open Food Facts found the product, but the name check says its name isn't readable.

- **Kept as fetched:** `source: openFoodFacts` and all Open Food Facts fields.
- **`displayName` stays Open Food Facts' name exactly**, so it's the hint, not a replacement.
- **New field:** `nameCheck: { "readable": 0.05, "model": "jev-1.13.0", "checkedAt": "…" }`. It's recorded for every checked product (`null` if never checked), so the threshold can be revisited later.

How the app treats a `needsName` product:

| Where | Behaviour |
|---|---|
| Stock list | Shows it like `noMatch` / `lookupFailed`: under "Waiting for a name", with the Name it button. |
| Name screen (#51) | Opens with `displayName` shown as "Open Food Facts calls it …". |
| `PUT /api/products/{id}/name` | Accepts `needsName` as well as `noMatch` / `lookupFailed`, with the same ETag-guarded replace. Naming sets `status: known`, `source: manual`, `namedAt`, `namedBy`. |
| Product lookup | Like `known`, it's **final**: never re-fetched, never re-checked. |
| Manual or photo names | Never checked. A manual name always wins. |

The check only ever moves `known` to `needsName`, at product creation. It never touches a named product.

## 4. Simple formatting without Jev (all caps)

**4 of 27** names are all capitals:

- CHIPOTLE HOT SAUCE
- PLAIN NAAN BREADS
- SEMI-SKIMMED BRITISH MILK
- MILD PERI PERI MAYO V

Sentence-casing in code would change these four. Per Gary's decision, nothing is title-cased for now. Jev rated all four readable (0.87–0.97), so capitals aren't a reason to flag. That needs the instruction "Capital letters don't matter".

## 5. When to call it

**Every new Open Food Facts product**, in the same request as the shelf-life estimate.

- **Cost:** negligible ([spike #40](40-jev.md)).
- **Latency:** about 250 ms, once per product, off the check-in path. It runs where shelf life runs: after the product is created, not before the unit is saved.
- **Noise:** none in the sample. No false flags at 0.5.

A **cheap check only** (gate the call on empty / non-Latin / all caps) would miss Latin-script foreign names, as the probes show. All caps also isn't a problem. So don't use the cheap check as the gate. Use it as the fallback when Jev fails (section 7):

- **Empty:** already `noMatch` today.
- **Any letter outside the Latin script:** `needsName`.

## 6. Combined with shelf life

**Yes.** One request with three questions (`name` choice, `readable` noul, `shelf_life` score) returned all three answers in about 250 ms. The answers matched the separate calls:

| Product | `readable` | `shelf_life` score |
|---|---|---|
| `горчица` | 0.05 | 7.92 (about 1–2 years, sensible for mustard) |
| Beef Gravy | 0.97 | 7.68 (about 1–2 years, sensible for gravy granules) |

Because choosing isn't recommended, the production request is just **`readable` + `shelf_life`**:

```json
{
  "model": "jev-1.13.0",
  "state": { "brand": "…", "quantity": "…", "categories": ["…"], "names": ["<displayName>"] },
  "questions": {
    "readable": {
      "type": "noul",
      "instructions": "The product name \"<displayName>\" is in English and a UK shopper would understand what the product is from it. Capital letters don't matter."
    },
    "shelf_life": { "type": "score", "instructions": "…as in spike #40…", "criteria": ["1-2 days", "…", "3 years or more"] }
  }
}
```

**Interface:** `IProductEnricher` (or an extended `IShelfLifeEstimator`) returns `{ shelfLife?, readable? }`. Keep Jev's types in Infrastructure.

For reference, the choice question tested here was a `choice` with options `C1..Cn` = candidates, plus `none` = "None of these is a usable English name for this product". Instructions: "Which option is the clearest name for this product in English, the way a UK shopper would write it on a shopping list? Prefer English over other languages. Ignore capital letters."

## 7. Failure

**Jev failures** (timeout, 429/529 after retries, 4xx/5xx, or a missing `readable` answer):

- Keep `status: known` with Open Food Facts' name as-is and `nameCheck: null`.
- Apply the code fallback: non-Latin script → `needsName`.
- Log a warning.
- **Never block check-in.** The unit and product are saved before any Jev call.

**Other rules:**

- **Low confidence:** `noul` gives a probability, not a choice, so the 0.5 threshold is the whole rule. No separate confidence cut-off is needed.
- **Never overwrite:**
  - The check only runs on a freshly created `known` product with `source: openFoodFacts`.
  - It writes with an ETag-guarded replace, so a name typed in the meantime always wins. The replace fails and the check is dropped.
  - It never runs on `manual` or `photo` names.

## Recommendation

| Option | Recommendation |
|---|---|
| Choosing a name from Open Food Facts' fields | **Don't build it.** |
| Readability flag on new Open Food Facts products | **Build it, always, in the same Jev request as the shelf-life estimate.** New `needsName` status, threshold 0.5, code fallback for non-Latin script. |
| Sentence-casing all caps | Not now (Gary's decision). It would affect 4 of 27. |

## Suggested follow-up cards (not created)

1. **[Feature] Flag unreadable product names for naming.** Depends on the shelf-life feature, or ships with it.
   - Add the `readable` question to the shelf-life Jev request.
   - Add the `needsName` status and the `nameCheck` field (data model and `docs/api.md`).
   - Show `needsName` products under "Waiting for a name", with Open Food Facts' name as the hint on the Name screen.
   - Let `PUT /api/products/{id}/name` accept `needsName`.
   - Add the non-Latin code fallback.
2. **[Task] One Jev request per new product.** If shelf life is already built, refactor its client to send several questions in one request and return typed answers, so the name check rides along.
