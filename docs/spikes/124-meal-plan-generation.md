# Spike #124: meal plan generation (outline, overlap, stock allocation and cost)

**Question.** How should Plan meals (#119, #125, #126) generate a pool of meals that uses what's in stock, favours what the household usually buys, and shares ingredients, on the `gpt-6-luna` deployment (default temperature only)? Nine sub-questions below.

**Answer in one paragraph.** One **outline** call per plan (a strict JSON schema, no method and no amounts) plus **bookkeeping in code**: stock allocation, overlap scoring with one retry, "what's missing" and "usually buys". Every change (Adjust a meal, Another one, Add a meal, Adjust the whole plan) is the **same "revise" call**: the current plan goes in, only the changed or added meals come back, and code merges them. Full recipes reuse the existing recipe generation, one meal at a time when opened. The existing **`recipes` job** is enough: no new model job. The one thing the evidence changes is **plan creation should be asynchronous** (202 and poll, as in #121), because luna's reasoning makes latency 3 to 46 seconds and, in about 3 calls in 100, it simply didn't answer within 100 s.

All measurements are on the live luna deployment (default temperature), with the household's real stock and history read **read-only** (nothing was written to Cosmos or the list; about 70 paid calls, under 15p in total). Scripts are in [`124-meal-plan-generation/`](124-meal-plan-generation/); the household snapshot they use is kept outside the repo.

## 1. The outline call

**Schema** (strict `json_schema`, the same mechanism as recipes; the model returns ids, code owns everything else):

```json
{ "meals": [ {
    "id": "dinner-1", "type": "breakfast|lunch|dinner", "title": "…", "summary": "one sentence", "servings": 2,
    "uses":   ["p12", "p40"],          // stock refs (p1, p2, … as in recipes): claims on stock
    "usesUp": ["p12"],                 // the use-first refs it uses (always [] when prefer use first is off)
    "toBuy":  ["peppers", "minced beef"] // short generic shopping names, not in stock; at most 4 per meal
} ] }
```

**Prompt.** The recipe prompt's shape: system rules, then a user message with the meals wanted, `Prefer use first: yes|no`, the diet, the stock table (the same columns as recipes, so the same ~6k tokens), the **usually buys** names, and the comments inside `<preferences>` tags. With prefer use first **off** the stock table has no dates or use-first flags (only "expired"), exactly as `preferUseFirst: false` works for recipes. Rules are the recipe rules (staples only, stock refs, veggie/half and half text reused as is) plus: share ingredients (below), prefer usually-buys names when something must be bought, and never put an in-stock product in `toBuy`.

**Measurements** (luna, the household's whole stock of 274 products, the overlap rule on; `run_matrix.py`):

| Plan | Use first | Latency | Tokens in + out (reasoning) | Cost |
|---|---|---|---|---|
| 1 day × 1 meal | off | 3.1 s | 6,031 + 308 (205) | £0.0006 |
| 1 day × 1 meal | on | 10.1 s | 7,488 + 1,130 (1,024) | £0.0010 |
| 3 days × 2 meals (6) | off | 11.5 s | 6,035 + 1,463 (1,024) | £0.0010 |
| 3 days × 2 meals (6) | on | 36.3 s | 7,492 + 4,514 (3,914) | £0.0023 |
| 7 days × 3 meals (21) | off | 24.5 s | 6,039 + 3,768 (2,018) | £0.0019 |
| 7 days × 3 meals (21) | on | 46.1 s | 7,496 + 6,702 (4,704) | £0.0031 |

* **The prompt is ~6.0k tokens (7.5k with use-by dates)**, dominated by the stock, so it barely grows with the plan. The usually-buys list adds ~100 tokens.
* **Prefer use first costs time**: the reasoning triples. My first rule ("every use-first product should appear in some meal") made the model squeeze all 30 use-first items into 3 dinners (28.6 s, 3.0k reasoning tokens). **Softening it** ("use as many as fit naturally, soonest expiry first, never force a product into a dish where it doesn't belong") gave 3 dinners in 11.0 s (4 of 30 used), 3 × 2 in 21.4 s (16 of 30), 7 × 3 in 42.2 s (28 of 30). Use the softened rule (in `outline.py`).
* **One outline call per plan is workable, even for 21 meals** (≤ 46 s). Splitting by meal type isn't needed, and would lose sharing across types (the toBuy list is what needs sharing). 
* **The real stock is so full that plans rarely need to buy anything** (every row above has an empty `toBuy`). That's right and good: the plan must work with no toBuy at all, and overlap only matters when stock is thin (below).
* **Tail latency.** In ~70 calls, **two never answered within 100 to 180 s** (about 3%) (a read timeout), and under 10 concurrent calls one timed out. That is luna's reasoning model, not our quota (no 429 was seen). This drives the async recommendation.

## 2. "Usually buys", with little history

**What the data looks like today** (`usually_buys.py`):

* Scan history: 396 events since 18 Oct. Check-ins per day: 18 Oct **231**, 19 Oct **76**, then 17, 1, 18, 5, 57. So **the stock take is 18 and 19 Oct** (the big evening bursts on 18 Oct 18:00–21:00 and 19 Oct 18:00), and real shops are 23 Oct (18) and 26 Oct (57). *The 19 Oct evening is my reading; Gary to confirm in a comment.* Bursts alone can't tell a stock take from a big shop (a 57-item shop looks like part of one), so **use a date, not burst detection**: an app setting `USUALLY_BUYS_HISTORY_START` (today `2026-10-20`, the day after the stock take).
* Check-ins since 20 Oct: 98 units of 88 products. **Only 0 products were checked in on two different days**, and only 5 generic names. So "3 check-ins in 90 days" gives nothing; "2 in 90 days" almost nothing.
* Shopping list: the typed items and their tombstones **expire after 7 days** (`ttl`), so ticked-off items aren't a usable history. But the list keeps one **`memory` document per name ever added** (no ttl; 63 names today, with `timesAdded`). That is the durable list signal.
* Check-outs since 20 Oct: 42 units. A product being **used up** is evidence it's one we use, and it needs no extra history.

**The rule** (a pure function, `usually_buys.py`; names are the product's **generic name** where it has one, else its display name; all signals since the history start):

```
score(name) = trips (distinct check-in days, capped at 4)
            + list adds (the memory document's timesAdded, capped at 3)
            + 1 if any unit was used up (checked out)
include if score >= 2 (the floor), best first, at most 30. Leave out names in the "Other" aisle (kitchen foil).
```

**On today's data:** floor 2 gives **30 names** (15 without the list signal); floor 1 would give 150, floor 3 gives 5. Counting the stock take as history (start 18 Oct) would give 72 names, mostly things bought once, which is why the date matters. Examples at the top: semi-skimmed milk (score 7), baby spinach, dark chocolate. **It improves on its own**: as shops accumulate, "trips" counts rise and the top 30 sharpen; the floor and N stay as settings.

**Empty is fine.** No history (or none yet since the start date) gives an empty list; the prompt then omits the usually-buys line and the plan is made without it. Nothing fails.

**Queries and RU** (all single-partition or small, projections only; measured live):

| Query | Rows | RU |
|---|---|---|
| `inventoryItems`: `SELECT productId, checkedInAt, checkedOutAt, status WHERE checkedInAt >= @start` | 98 | 5.8 |
| `inventoryItems`: `SELECT productId, checkedInAt, checkedOutAt WHERE checkedOutAt >= @start` | 42 | 4.0 |
| `shoppingList` (partition `household`): `SELECT normalizedName, name, timesAdded, lastAddedAt WHERE type = 'memory'` | 63 | 4.9 |

About **15 RU** plus the product names, which the plan already reads for the stock (the existing ~265 RU stock read, so no extra). Compute it per plan creation (it's cheap); no cache needed. Send **generic names**: the list's free-text names (`Milk`, `Yoghurt`) are kept as they are, normalised, and matched to the generic name where the same.

## 3. Overlap

**Measure** (code, `outline.score`): collect every `toBuy` mention by normalised generic name; **shared** = a name in two or more meals; **orphans** = a name needed by exactly one meal. Overlap is good when **orphans = 0** (`shareRatio` = the share of mentions that are shared).

**Asking the model is mostly enough; code scoring plus one retry closes the gap.** Measured on a "before the shop" stock (the cupboard aisles plus use-first items, 145 and 150 items) with 3 days × lunch and dinner, twice each (`run_overlap.py`):

| Variant | Use first | Distinct things to buy / mentions | Orphans | Share ratio |
|---|---|---|---|---|
| A: no overlap rule | off | 6 / 6 and 5 / 5 | **6 and 5** | 0.0 and 0.0 |
| B: overlap rule in the prompt | off | 3 / 6 and 3 / 7 | **0 and 1** | 1.0 and 0.86 |
| B + one code retry | off | 2 / 6 (the run that had an orphan) | **0** | 1.0 |
| A: no overlap rule | on | 4 / 4 and 4 / 4 | **4 and 4** | 0.0 and 0.0 |
| B: overlap rule in the prompt | on | 4 / 6, and 0 / 0 | **2**, and 0 | 0.67, and none |
| B + one code retry | on | 4 / 8 (the run that had 2 orphans) | **0** | 1.0 |

* Without the rule the model shops for each meal separately (0% shared). With one sentence of rule, 3 of 4 runs had ≤ 1 orphan; **one retry fixed every remaining orphan**, at ~8 to 12 s and ~£0.0009.
* Examples: "Chickpea and coconut curry with spinach rice" and "Mushroom and spinach pesto fusilli" both buy baby spinach; the model also reuses names from the usually-buys list (brown mushrooms, pak choi, baby spinach).
* **Recommendation:** put the sharing rule in the prompt; after the call, **score in code; if there are orphans, make one retry** with the revise call (below), telling the model the orphan names ("each is bought for only one meal: share it with another meal or replace it"). Accept the result after one retry (never loop). Show "Shares X with 2 other meals" from the scored plan, not from the model's words.

## 4. Stock allocation (`allocate.py`, six unit tests)

The model proposes `uses` (claims on stock refs); **code** decides who gets what, with no quantities:

* A product's capacity is its **in-stock unit count** (a tin, a pack, a bag): **one unit serves one meal**.
* **Bulk products serve any number of meals**: the aisles where one pack lasts (Pasta, rice & grains; Baking & spices; Sauces, oils & condiments; Breakfast & cereal; Drinks) or a short list of names (salt, pepper, oil, flour, sugar, rice, pasta, spice, stock cube, vinegar).
* Meals claim in plan order, **kept meals first**, so a meal the person kept never loses its stock to a new one.
* A claim beyond capacity is **short**: that meal needs another one, so its generic name joins what's to buy. Unknown refs are reported and dropped.
* "What's missing" for #125 = each meal's `toBuy` ∪ short claims, merged by name (with the meals that need each), minus names open on the list.

On the plans from the experiments it flagged **1 short claim in 6 plans** (70 claims), so it is simple and rarely wrong. A wrong guess costs one list item the person can untick. Pros of "one unit = one meal": explainable ("you have one tin and two meals want it"). Weakness: a big multi-serve pack counts once; that's acceptable because names only is the contract.

## 5. Adjust, Another one, Add a meal, Adjust the whole plan

**All four are one `revise` call** (`revise_lib.py`): the same system prompt plus a revising paragraph; the user message has the meals wanted, the **current plan** (each meal: id, type, title, uses, toBuy; kept meals marked `KEEP`), the `Prefer use first` choice and the stock table as before, and an **instruction** line. The model returns **only the meals it changed or added** (same ids for changed ones); code merges them, **drops any change to a kept meal**, and re-scores overlap.

| Operation | Instruction | Latency | Tokens in + out | Cost |
|---|---|---|---|---|
| Adjust a meal | "Adjust meal X: make it vegetarian." (the rest kept) | 2.3 s | 3,944 + 205 | £0.0004 |
| Another one | "Replace meal X with a different dinner." (+ avoid titles) | 3 to 6 s | 3,944 + 280 to 590 | £0.0004 to £0.0005 |
| Add a meal | "Add one more dinner." | 3.3 s | 3,940 + 304 | £0.0004 |
| Adjust the whole plan | "Cheaper: fewer things to buy." (two meals kept) | 9.0 s | 3,945 + 1,009 | £0.0007 |

(Prompt sizes are ~3.9k here because these ran on the 145-item before-the-shop stock; on the full stock add ~2k.)

* **Returning only changed meals is cheap and fast** (200 to 1,000 tokens, not the whole plan's thousands), and kept meals can't drift because code owns them.
* **Another one stays varied only if code varies it.** At the default temperature, three identical "Another one" calls gave three near-identical meals (butter bean and tomato, every time); adding `avoidTitles` helped a little. **What worked:** code picks a **different "angle" per call** from a short list (a rice dish, a pasta dish, a soup or stew, a noodle dish, a wrap, a curry, a bake, a salad or bowl, an egg dish, a one-pan dish) and passes the **titles to avoid** (the plan's other titles plus earlier replacements): 4 of 4 distinct dishes, all of the right kind.
* **Replacing or deleting a meal can strand what the other meals share** (a replacement with no regard for the plan left 1 to 2 orphans in all 12 tries, two runs of six). Fix, again in code: before the call, work out which `toBuy` names the meal **shares with exactly one other meal** and put them in the instruction ("The other meals buy baby spinach and brown mushrooms in sharing with it: use those in the new meal"). Result: **0 orphans in 4 of 4**, ~3 s each. Delete needs no call, but code should warn "X is now bought for one meal only" or offer the same instruction through Adjust.
* **Prefer use first** is just the stock table and the one rule line, as in creation; a revise call keeps the plan's choice and never changes it.

## 6. Full recipes

**Yes, reuse the existing recipe generation per meal** (`run_recipe.py` takes the real recipe system prompt and schema straight from `FoundryRecipeGenerator.cs`). Request: `Meal` = the outline's type; `Must include` = the outline's `uses` refs (that's what the recipe prompt already does for "Include ingredients"); the title, summary and the `toBuy` names go in the preferences text ("Cook this planned meal: …. It may also use these items, which will be bought: …"). Three meals from a plan: each used **every must-include** ref, produced the right dish, and its `alsoNeeded` matched the outline's `toBuy` (e.g. spinach, mushrooms); **4.4 to 6.5 s, ~4.0k + 0.5 to 0.9k tokens, £0.0005 to £0.0006 each** on luna. Notes for #126: store the recipe on the plan; the recipe stays valid unless the meal is adjusted; **`alsoNeeded` is capped at 3 today, while an outline allows 4**: either cap the outline at 3 or raise the cap to 5 for planned meals (suggest the latter, as buying is expected). Writing recipes **only when opened, one at a time** is confirmed as right: it is the 6.5k-token call and nobody needs all 21.

## 7. Model job

**No new job: the existing `recipes` job is good enough** for the outline and the revise calls. Luna handled the strict schema first time on every call (no schema or JSON failures in ~70 calls), obeys the rules, and the outline's schema is a subset of what #121's strict-schema and recipe checks already exercise. No new job means no change to #120's settings, #121's tests or #123's UI. **Optional, low priority follow-up** (suggested, not created): a **"plan outline" check** in the model tests (#121), so a model that can write a recipe but not an outline can't slip in; it's not needed to ship #119.

## 8. Cost (luna list price: £0.0755 per million input tokens, £0.3774 per million output; reasoning counts as output)

| | Calls | Cost |
|---|---|---|
| **Typical plan**: 3 days × dinner, adjusted twice, all three recipes opened | create 11.0 s, £0.0010 (use first on; £0.0007 off) · adjust ×2 £0.0008 · open ×3 £0.0018 | **≈ £0.0036 (0.4p)** |
| **Big plan**: 7 × 3 meals, opened all (and 5 adjustments) | create 42.2 s, £0.0027 (use first on; £0.0019 off) · open ×21 £0.0126 · adjust ×5 ~£0.0020 | **≈ £0.0173 (1.7p)** |

For comparison, a #121 model test costs about the same as one big plan. Cost is not a constraint at any realistic use.

## 9. Rate limits

* **The luna deployment** (`az cognitiveservices account deployment list`, read-only): **GlobalStandard, 125,000 tokens per minute and 125 requests per minute**. It is **its own quota** (gpt-4o has 50k, DeepSeek 20k, gpt-4.1-mini 10k, each separate). It's shared only by what's set to use luna (recipes, Ready now, Adjust, and generic names or photos if switched to it; luna can't read images). It is global, so processing isn't confined to the UK.
* **Headroom.** The outline is 7.5k to 15k tokens; a recipe ~4.5k; a revise ~4.5k. **10 outlines at once** (3 × 2, use first on, `run_burst.py`) used **64k tokens (51% of a minute's quota)**; 9 of 10 finished in 11 to 27 s, **no 429**, one timed out. So **two people planning at once** (2 × ≤ 15k = 30k, 24%) is comfortable, and a household can't realistically reach the quota. **Writing recipes only when opened** keeps it far inside.
* **Limits per person per hour.** Plan calls get **their own bucket**, separate from the 30 recipe calls an hour: **30 plan calls per person per hour** (create, adjust, another one, add, adjust the whole plan, plus the overlap retry). A session is ~10 calls; a call costs ≤ £0.003, so even the limit is ≤ 9p an hour. **Opening a planned meal's recipe counts towards the recipe limit** (it's a recipe call), so opening many recipes of a big plan in one go is the only way to meet it, and opening is naturally spread over a week.
* **429 and timeouts.** A 429 wasn't provoked at these volumes, so `Retry-After` wasn't observed. Handle it as the card says: **honour `Retry-After` once if it's ≤ 10 s, else a friendly "Busy right now, try again in a minute"; nothing changes on a failed call.** Separately, because of luna's tail latency: **attempt timeout 90 s for create and 45 s for revise and recipes, one retry on timeout** (revise calls normally take 2 to 9 s; recipes 4 to 7 s).
* **The quota is not too low**; no change needed.

## Recommendation (what #119, #125 and #126 can be built from)

1. **Generate in two stages**: an outline call (the schema above, strict, `recipes` job, default temperature handled by #120's temperature mode) and, later, one recipe per opened meal with the existing generator (`Must include` = the meal's `uses`; allow 5 `alsoNeeded`).
2. **Do the bookkeeping in code**: `allocate` (one unit = one meal, bulk unlimited, kept meals first), the overlap score with **one** retry, "what's missing" for #125 (toBuy ∪ short claims, merged by name, minus the open list), and the "stranded items" hint for Another one and Delete.
3. **One revise call** for Adjust, Another one, Add a meal and Adjust the whole plan, returning changed meals only; code merges, protects kept meals, and adds a different **angle** and the **titles to avoid** for Another one and Add.
4. **Create the plan asynchronously** (POST returns 202 with the plan id and `status: generating`; the UI polls, as for model tests in #121), because outline latency is 3 to 46 s and luna sometimes doesn't answer for 100 s or more. Revise calls (2 to 9 s) can stay synchronous with a 45 s timeout and one retry.
5. **Usually buys** by the rule in section 2, from `USUALLY_BUYS_HISTORY_START` (an app setting), empty-safe.
6. **Prefer use first off** gives the model no dates, use-first flags or the use-first rule; on, use the **softened** rule (not "use all").
7. **Limits**: plan calls 30 an hour per person (own bucket); recipe opens count in the recipe 30; 429 and timeout handling as above.
8. **No new model job.**

## Suggested follow-up cards (not created)

* **#119 additions** (not new cards): async creation (queue + poll, the #121 pattern); the plan limit setting; `USUALLY_BUYS_HISTORY_START`, `USUALLY_BUYS_MIN_SCORE` and `USUALLY_BUYS_TOP_N` settings.
* **[Task] Recipe from a planned meal**: allow `alsoNeeded` up to 5 and a "planned meal" request (must-include from the outline; the title and toBuy in the preferences) in `FoundryRecipeGenerator`. Could be part of #126.
* **[Task] Model test: a plan outline check** (#121, optional, low priority): the same check list with a tiny outline request, so a model that can't write an outline can't be made active for `recipes`.
* **[Task, optional] Tail-latency guard for luna**: the existing recipe calls have a 45 s timeout and 3 attempts, which already copes; note for Gary that ~3% of luna calls didn't answer within 100 s in this spike.

## Re-running the experiments

`python3 usually_buys.py <dir>`, `run_matrix.py`, `run_overlap.py`, `run_adjust.py`, `run_adjust2.py`, `run_recipe.py`, `run_burst.py <dir> <n>`, `run_typical.py` (each takes the folder holding `snapshot.json`: the household's `scanEvents`, `shoppingList`, `inventoryItems` and `products`, pulled with `cosmos.py`; keep it **outside the repo**), and `python3 -m unittest allocate`. They sign in through your `az login`, send no temperature, and write nothing to Cosmos or the list.
