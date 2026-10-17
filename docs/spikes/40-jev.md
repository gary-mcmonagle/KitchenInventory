# Spike #40: Jev API for shelf life and product decisions

Card: https://trello.com/c/QbyAeSZS

## Question

How do we use **Jev** (TypeSafe AI's System One model) for two jobs, and is it reliable and affordable enough?

1. **Shelf life:** given a product (name, brand, categories), estimate its usual shelf life.
2. **Product decision:** given OCR text from a photo of a pack, decide the product's name, brand and size for a no-match barcode.

## Summary

- **Jev doesn't write text. It answers typed questions about the input you give it** (the "state"): **choice** (pick one of up to 255 options), **score** (a position on an ordered scale of 2–10 levels) and **noul** (a yes/no probability). Each answer comes with probabilities and a confidence. That shapes both designs.
- **Shelf life works well as a score** on a 10-level scale of durations ("1–2 days" … "3 years or more"). 16 of 18 test products got a sensible estimate. One was an overestimate (crisps), and one came back with very low confidence (naan), which the fallback below handles. Telling it the storage (e.g. "freezer") changes the estimate sensibly.
- **The product decision works as a set of choices over the OCR lines:** "which line is the name / brand / size", plus yes/no questions for "is this food" and "is it readable". It picked the right lines in 5 of 5 samples after one prompt fix. It's stable when the option order is reversed, and it correctly spotted a non-food pack. **It can't fix OCR errors** ("Ch1ckpeas"), because it chooses lines and can't rewrite them. So low-confidence or messy fields are shown as "Check this one".
- **Fast and nearly free:** 230–400 ms per call, about 500 input tokens, at $0.042 per million input tokens (output is free). That's under **$0.01 a year** at household volume.
- **No .NET SDK** (Python and JavaScript only), so we write a small typed `HttpClient` in Infrastructure. Retry on 429 and 529.
- Both jobs go behind Application interfaces (`IShelfLifeEstimator`, `IProductTextResolver`). Shelf life is **cached per product**. Pin the model version (`jev-1.13.0`), not the `jev-latest` alias.

## 1. API basics

| | |
|---|---|
| Endpoint | `POST https://api.typesafe.ai/v1/systemone` |
| Auth | `Authorization: Bearer <key>`. Our key lives in `JEV_API_KEY`: locally in a git-ignored env file, in Azure in Key Vault (#42). The SDKs' default name is `TYPESAFE_API_KEY`. |
| Request | `{ "state": <string or JSON>, "model": "jev-1.13.0", "questions": { "<id>": { "type": "choice" \| "score" \| "noul", "instructions": "...", "criteria": ... } } }` |
| Response | `{ "model": "jev-1.13.0", "answers": { "<id>": { "type", "choice" / "score" / "noul", "confidence", "probabilities", "legend" } }, "usage": { "input_tokens", "output_tokens" } }` |
| Limits | Choice up to 255 options; score 2–10 levels; 64k tokens per request and 32k per question; body up to 32 MiB |
| SDKs | Python (`typesafe-sdk`) and JavaScript/TypeScript (`@typesafe-ai/sdk`). **No .NET SDK**, so use `HttpClient`. |
| Models | `jev-1.13.0` (stable; `jev-latest` points to it); `jev-1.14.0` (from 14 Oct 2026; `jev-preview`). Aliases move when a release ships. |
| Rate limits | Per organisation, in tokens and requests per second (see the console). Over the limit → `429`, with `retry-after` when present. |
| Latency | Not documented. **Measured: 230–400 ms** per call from the UK (40 calls). |
| Pricing | **$0.042 per million input tokens; output tokens are free.** |
| Data | TypeSafe commits not to train on user data. Zero data retention is enterprise-only, and data location isn't stated. We only send product names and pack text, never personal data. |

**Errors** (docs, plus what we saw):

| Status | Meaning | Our handling |
|---|---|---|
| 200 | Answers | Use them, subject to confidence |
| 400 | Request breaks a rule, or input over the context limit | Log an **error**, don't retry (it's a bug) |
| 401 | `{"detail":{"error_type":"authentication_error",…}}` | Log an **error**, don't retry (bad or missing key) |
| 413 / 415 | Body too big / bad compression | Log an **error**, don't retry |
| 422 | `{"detail":[{"type":"missing","loc":[…,"criteria"],…}]}`: names the bad field | Log an **error**, don't retry |
| 429 / 529 | Rate limited / overloaded | **Retry** with exponential back-off, honouring `retry-after` |
| Timeout, 5xx, network | Transient | **Retry**, then give up and fall back (see section 5) |

## 2. Structured output and contracts

Jev's output is structured by design, so there's no JSON parsing of generated text. The contracts are **the questions we send**.

### Shelf life

- **State:** the product as JSON, for example `{ "name": "Beef Gravy", "brand": "Bisto", "size": "230g", "categories": ["en:gravy"] }`. Add `"storage": "freezer"` when we know it.
- **Questions:**

```json
{
  "shelf_life": {
    "type": "score",
    "instructions": "How long this product usually stays good to eat after it is bought, unopened and stored the usual way for this kind of product.",
    "criteria": ["1-2 days", "3-4 days", "About a week", "About 2 weeks", "About a month",
                 "About 3 months", "About 6 months", "About a year", "About 2 years", "3 years or more"]
  },
  "storage": {
    "type": "choice",
    "instructions": "Where this product is normally stored at home before it is opened.",
    "criteria": { "cupboard": "Cupboard or worktop, at room temperature", "fridge": "Fridge", "freezer": "Freezer" }
  }
}
```

- **Levels → days:** 2, 4, 7, 14, 30, 90, 180, 365, 730, 1095.
- **Which level to use:** the `score` field is the *average* level (e.g. 3.06), which can fall between two peaks. Instead, read `probabilities` and take the **median level**: the lowest level where the running total reaches 0.5. When confidence is low, take the **25th-percentile level** instead (see section 5).
- Durations are given as **words**, not numbers, on purpose: TypeSafe's notes on Jev 1.13 say it "reads dates as text, not as ordered quantities" and "is not a calculator". The tests confirm worded levels work.

### Product decision

- **State:** `{ "ocrLines": ["Heinz", "Beanz", "Baked Beans in a rich tomato sauce", "415g", …] }`.
- **Questions:** `name`, `brand` and `size` are **choices whose options are the OCR lines** (`L1`…`Ln`, each described by its text), plus `"none"`. `is_food` and `readable` are yes/no (`noul`) questions.
- The prompt that tested best ("v2"):
  - `name`: "Which line is the product's name: what the product is, as you would write it on a shopping list. Not the brand, the size, ingredients, or storage advice. **If the name is printed in several languages, choose the English line.**"
  - `brand`: "Which line is the brand or manufacturer name only. **The brand is the product's own brand printed on the front of the pack. A shop's or retailer's name, address or country line is not the brand.**"
  - `size`: "Which line gives the pack size, weight or volume of the product."
  - `is_food`: "This text is from the packaging of a food or drink product."
  - `readable`: "The text is clear enough to tell what the product is."
- **The result we build from it:** `{ name, nameConfidence, brand, brandConfidence, size, sizeConfidence, isFood, readable }`. Each field is the chosen line's text (trimmed), or null for `none`.

## 3. Shelf life quality

Run on 2026-10-17 with `jev-1.13.0` and [`40-jev-test.py`](40-jev-test.py) (`shelf-life` mode). "Verdict" is my sanity check against typical UK best-before and use-by dates. **Gary still checks a sample in the estimated expiry feature (#52).**

| Group | Product | Estimate (median level) | Confidence | Storage (p) | Verdict |
|---|---|---|---|---|---|
| Cupboard | Bisto Beef Gravy 230g | About a year | 0.65 | cupboard (0.97) | Sensible |
| Cupboard | Napolina Lasagna sheets 375 g | About a year | 0.78 | cupboard (0.98) | Sensible (dried pasta often longer, so slightly cautious) |
| Cupboard | Freshona Chickpeas in Water 400g | About a year | 0.76 | cupboard (1.0) | Cautious: tins usually keep 2+ years |
| Cupboard | Filippo Berio Garlic olive oil 250 ml | About 2 years | 0.82 | cupboard (1.0) | Sensible |
| Cupboard | Weetabix Chocolate | About a year | 0.79 | cupboard (1.0) | Sensible |
| Cupboard | Maribel Crunchy Peanut Butter 1 kg | About a year | 0.81 | cupboard (1.0) | Sensible |
| Cupboard | Walkers Salt & Pepper Chicken crisps | About 6 months | 0.71 | cupboard (1.0) | **Too long**: crisps usually 2–3 months |
| Cupboard | Mazurskie Miody honey 900 g | About 2 years | 0.80 | cupboard (1.0) | Sensible |
| Fridge | Dairy Manor semi-skimmed milk | About a week | 0.83 | fridge (0.97) | Sensible |
| Fridge | Brooklea Greek yogurt 500g | About 2 weeks | 0.80 | fridge (1.0) | Sensible |
| Fridge | Lidl Mozzarella 125 g | About 2 weeks | 0.71 | fridge (1.0) | Sensible |
| Fridge | Tesco chicken breast fillets 650 g | 3–4 days | 0.78 | fridge (0.87) | Sensible |
| Freezer | Birds Eye Garden Peas 800 g | About a year | 0.82 | freezer (1.0) | Sensible |
| Freezer | Birds Eye Fish Fingers | About 6 months | 0.76 | freezer (1.0) | Cautious: often a year |
| Fresh | Bananas | About a week | 0.92 | cupboard (0.99) | Sensible |
| Fresh | Deluxe Easy Peelers | About 2 weeks | 0.80 | cupboard (0.80) | Sensible |
| Fresh | Taste of Plain Naan Breads 2 × 130 g | **Low confidence** (spread from 1–2 days to 3 months) | **0.08** | freezer (0.66) | Unusable as the median; the fallback gives 3–4 days, which is sensible |
| Fresh | Lidl Bakery White Sourdough Loaf | 3–4 days | 0.79 | cupboard (0.98) | Sensible |

**With storage stated** (same products, `"storage"` added to the state):

| Product | Storage given | Estimate | Confidence | Verdict |
|---|---|---|---|---|
| White Sourdough Loaf | freezer | About 3 months | 0.70 | Sensible (3–4 days → 3 months) |
| Mozzarella | fridge | About 2 weeks | 0.75 | Same as without, as expected |
| Chicken breast fillets | freezer | About 6 months | 0.68 | Sensible (3–4 days → 6 months) |
| Semi-skimmed milk | freezer | About 3 months | 0.50 | Plausible; the storage answer still said fridge (0.58) |

**Findings**

- **16/18 sensible** without storage. One overestimate (crisps) and one low-confidence answer (naan). Where it's off, it's usually **cautious** (tins, pasta, fish fingers), which is the safe direction for "use first".
- **Storage matters** for bread, meat and milk, and Jev uses it when given. **Is it worth asking for?** Not at check-in: a scan doesn't know where the item goes, and asking Gary every time would be a chore. **Recommendation:** ask Jev for the *usual* storage (the `storage` question) and estimate for that. Store `storage` on the product. A per-unit "I froze this" is a sensible later feature (a follow-up below), and with storage in the state Jev already gives sensible frozen estimates.

## 4. Product decision quality

Hand-written OCR samples (the Document Intelligence spike isn't done), `product` and `order-check` modes. Prompt v2.

| Sample | Name (confidence) | Brand (confidence) | Size (confidence) | is food | readable | Verdict |
|---|---|---|---|---|---|---|
| Clean: Heinz Baked Beans | "Baked Beans in a rich tomato sauce" (0.52) | Heinz (1.0) | 415g (1.0) | 0.98 | 0.98 | Right. The name is long-winded, and its moderate confidence flags it as "Check this one". |
| Noisy: Freshona chickpeas with OCR errors | "Ch1ckpeas" (0.92) | "FRESH0NA" (0.93) | "400g e" (0.97) | 0.97 | 0.95 | Right lines, **OCR errors kept**. Gary fixes them on the naming screen. |
| Multi-language: Lidl mustard | "Medium hot mustard" (1.0) | none (0.77) | 250 ml (1.0) | 0.97 | 0.96 | Name right (English chosen). Brand missed ("taste of DEUTSCHLAND"), so the field is left blank. |
| Deli label: honey roast ham | "Wafer Thin Honey Roast Ham" (1.0) | "LIDL DELI" (0.55) | "NET 0.212kg" (1.0) | 0.99 | 0.98 | Name right. The brand is debatable, and its moderate confidence flags it. |
| Not food: washing-up liquid | "Washing Up Liquid" (1.0) | Fairy (1.0) | 433ml (1.0) | **0.05** | 0.97 | Correctly flagged as not food |

- **Prompt v1 → v2:** v1 chose the German name for the mustard ("Mittelscharfer Senf") and "LIDL GB" as the chickpeas brand. The v2 wording above fixed both, without making the other samples worse.
- **Option order:** TypeSafe warns that Jev 1.13 "leans toward the option that comes first". Reversing the options gave **the same choices for every field**, with similar confidences (brand confidences moved by ±0.2).
- **Noisy text:** it picks the right line, but it **can't correct OCR errors**: choices only. Fixing "Ch1ckpeas" needs either Gary (the "Check this one" hint) or a different approach, such as the vision model being considered in [Spike] Photo recognition (#41).
- **Multi-language packs:** handled once the prompt asks for English. Non-Latin lines (Bulgarian) were correctly ignored.
- **Not tested:** names split across two OCR lines. If Document Intelligence often splits names, offer adjacent line pairs as extra options (`L2+L3`).

## 5. Failure modes and fallbacks

| Failure | Shelf life | Product decision |
|---|---|---|
| 429 / 529 / timeout / 5xx | Retry with back-off (3 tries, ~10 s timeout each). If it still fails: **leave the product unestimated**, log a **warning**, retry on the next relevant event (as #52 says). Check-in never waits on Jev. | Retry, then return "couldn't work it out" and fall back to manual naming with empty fields. |
| 400 / 401 / 413 / 415 / 422 | Log an **error** (configuration or code bug; the error alert emails Gary), leave unestimated | Log an **error**, fall back to manual naming |
| Low confidence | `confidence < 0.5` → use the **25th-percentile level** (a cautious estimate: naan → 3–4 days), with `shelfLifeSource: jevLowConfidence` so the UI can call it a rough estimate. No per-category default table is needed. | Each field: confidence ≥ 0.8 → "Looks right"; below → "Check this one"; `none` → empty. `readable < 0.5` → "couldn't work it out" (manual). `is_food < 0.5` → a warning ("This doesn't look like food"), but still allow saving. |
| Refusals | Not applicable: Jev doesn't generate text, so it can't refuse. It always returns a distribution. | Same |

Thresholds (0.5 and 0.8) are starting points, as TypeSafe's confidence guide advises ("start with conservative thresholds, test with your own data"). They're tuned to `jev-1.13.0`, which is one reason to **pin the model version** in config (`JEV_MODEL`) and re-check before moving.

## 6. Caching

**Confirmed: shelf life is cached per product,** on the product document (`shelfLifeDays`, `shelfLifeSource`, `shelfLifeEstimatedAt`, plus the new `storage` and `shelfLifeConfidence`). Jev is called once per product, never per unit, as #52 already specifies. Re-estimate only when Gary renames a product. A manual shelf life (#53) is never overwritten.

The product decision is not cached: it runs once per photo, on demand.

## 7. Clean architecture

```csharp
// Application
public interface IShelfLifeEstimator
{
    // null when Jev couldn't be reached after retries (caller leaves the product unestimated)
    Task<ShelfLifeEstimate?> EstimateAsync(ProductDescription product, CancellationToken cancellationToken);
}

public sealed record ProductDescription(string Name, string? Brand, string? Size,
    IReadOnlyList<string> CategoryTags, Storage? Storage);

public sealed record ShelfLifeEstimate(int Days, ShelfLifeConfidence Confidence,   // High | Low
    Storage UsualStorage, double RawConfidence);

public enum Storage { Cupboard, Fridge, Freezer }

public interface IProductTextResolver
{
    Task<ProductTextResolution> ResolveAsync(IReadOnlyList<string> ocrLines, CancellationToken cancellationToken);
}

public sealed record ProductTextResolution(
    ResolvedField? Name, ResolvedField? Brand, ResolvedField? Size,
    double IsFoodProbability, double ReadableProbability, bool Recognised);

public sealed record ResolvedField(string Text, double Confidence);
```

- **Infrastructure:** a `JevClient` (typed `HttpClient`, base URL and model from config, key from config backed by Key Vault, a ~10 s timeout, and retry with back-off on 429/529/5xx/timeouts honouring `retry-after`). `JevShelfLifeEstimator` and `JevProductTextResolver` build the questions and map the answers: median or 25th-percentile level → days, thresholds → `High` / `Low`.
- **The key is never logged:** log the status, latency, model and token counts, and nothing from the headers.
- **Tests:** the mappers are pure, so unit test the level/percentile maths, the thresholds and `none` handling against canned responses taken from this spike's results. Test the client with a fake `HttpMessageHandler` (retry on 429/529, no retry on 401/422).

## 8. Cost

- About **500 input tokens** per call (both jobs). **$0.042 per million input tokens, output free** → about **$0.00002 per call**.
- Household volume: a one-off stock-take of a few hundred products plus a few new products a week, and a handful of photos. That's under **1,000 calls a year → under $0.03 a year**. Effectively free; rate limits, not cost, are the only constraint.

## Recommendation

1. **Shelf life:** one Jev call per product, as soon as it's known: a `score` on the 10 worded duration levels plus a `storage` choice. Use the median level, or the 25th percentile below confidence 0.5. Cache it on the product. Pin `jev-1.13.0`.
2. **Product decision:** choices over the OCR lines for name, brand and size, plus `is_food` and `readable`, with the v2 prompt. Per-field confidence drives "Looks right" / "Check this one". Jev can't correct OCR errors, so weigh this against the vision approach in #41.
3. **Integration:** a typed `HttpClient` behind `IShelfLifeEstimator` / `IProductTextResolver`. Retry 429/529 and transient errors; no retry for 4xx bugs. The key goes in Key Vault (#42). Never block check-in or naming.
4. **Cost:** negligible.

## Suggested follow-up cards (not created)

- **Notes for [Feature] Estimated expiry (#52)** (no new card): use this spike's question set, the median / 25th-percentile rule, `shelfLifeSource: jev | jevLowConfidence`, and store `storage` and `shelfLifeConfidence`. Its "default per category" fallback isn't needed.
- **Notes for [Spike] Photo recognition (#41)** (no new card): Jev's text-only approach picks the right lines but can't fix OCR errors; include this in the comparison.
- **[Feature] "I froze this" for a unit** (later): a per-unit storage override that re-estimates that unit's expiry with `storage: freezer`. Jev handles it well (bread 3–4 days → 3 months, chicken → 6 months).
- **[Spike] Jev validation of Open Food Facts names (#61)** can now go ahead. Note that Jev can *judge* a name (e.g. "is this name English and readable?" as a noul) but can't *rewrite* one, so #61 should be re-scoped to checking names, not tidying them.

## Sources

- TypeSafe quick start (endpoint, auth, request and response shape, question types, SDKs): https://docs.typesafe.ai/introduction/quickstart
- TypeSafe API reference (fields, 255 options per choice, 2–10 score levels, errors, retries): https://docs.typesafe.ai/api
- TypeSafe models (versions, aliases, pricing $42 per Btok input with output free, context limits, rate limits): https://docs.typesafe.ai/models
- Confidence guidance (thresholds by risk, route low confidence to a human or fallback): https://docs.typesafe.ai/confidence
- Jev 1.13 known weaknesses (literal reading, numbers, dates as text, option-order bias, not a generator): https://docs.typesafe.ai/model-jaggedness/jev-1.13
- Legal (no training on user data; zero data retention for enterprise): https://docs.typesafe.ai/legal
- Test runs: [`40-jev-test.py`](40-jev-test.py), 2026-10-17, `jev-1.13.0`.
