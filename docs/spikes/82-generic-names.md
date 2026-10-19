# Spike #82: Generic product names with Foundry

Card: https://trello.com/c/yPUtfuSK

## Question

Can every product get a short, consistent **generic name**, what you'd say to someone going to the shop (e.g. "Kellogg's Bran Flakes 200 g" → "Bran flakes"), using **Foundry gpt-4o**? What rules, prompt and safeguards make it reliable and consistent enough to drive the shopping list?

## Summary

- **Yes.** gpt-4o gives good generic names from the product's name, brand, size, categories and aisle. It drops brands, ranges, sizes and marketing words, and translates foreign packs: Kreuzkümmel → Cumin seeds, Mais groß → Sweetcorn, Oignon jaune → Onions, Abacaxi em Caldas → Pineapple in syrup, горчица → Mustard. It flags non-food (the vitamin C tablets).
- **Consistency needs help, and we know what works.** Measured on 77 real products, including 9 groups of "the same thing from different brands":

  | Approach | Same-thing groups under one name (level A) |
  |---|---|
  | Each product on its own | 3 of 9 |
  | **Told the names already in use, asked to reuse one** | **8 of 9** (6 of 9 in the reverse order) |
  | Plus a **tidy-up pass** (one call over all names, merging synonyms) | +4 of the 6 that free naming missed, in one 1,389-token call |

  So: **name each product with the existing names in the prompt** (in real use, products arrive one at a time and see everything named before them), **check "reused" in code** (the model's own flag can't be trusted: it claimed to reuse names that only appeared in the prompt's examples), and keep **a manual override**. A tidy-up pass is an optional extra for the one-off fill.
- **Level, Gary to choose** (both columns in the table below). **Recommendation: level A (shopper level).** It keeps what changes what you'd pick up: semi-skimmed milk, caster vs icing sugar, smoked paprika, decaf coffee, Dijon vs English mustard. Level B merges those ("Sugar" for caster, icing and demerara), which would buy the wrong thing. Level B is more consistent on paper (9 of 9 groups) because it's coarser.
- **Cost and speed:** about 700 prompt + 19 completion tokens per product, **about $0.002 each**; the one-off fill of all 181 named products costs about **$0.35**. Median latency 0.5 s.
- **Shared capacity:** the `gpt-4o` deployment is small (10,000 tokens a minute, 10 requests per 10 s) and shared with photo recognition and recipes. The spike's first full run would have taken over an hour and starved them, so it was cut to a throttled sample. **The one-off fill must be throttled too** (about 6,000 tokens a minute, a few minutes for 181 products), and day to day it's one call per newly named product, which is negligible.

## How it was tested

- **Script:** `docs/spikes/82-generic-names.py` (standard library only; Entra token from `az login`, no keys). Read-only: the products came from `deploy/verify/cosmos.sh products` (181 named products), and nothing was written to live data. Results are cached outside the repo.
- **Sample:** 77 products: every product in 9 "same thing" groups and 8 "family" groups (sugars, coffees, paprikas, ...), every loose or hand-named item, and awkward ones (foreign names, brand-as-name, multipacks, ready meals, a mocktail, vitamin tablets, a garbled Open Food Facts name).
- **Variants:**
  - **A-free-1/2:** level A, each product alone, run twice.
  - **B-free-1:** level B, the same.
  - **A-reuse-1/2:** level A, one after another with the names so far in the prompt, then again in reverse order.
  - **B-reuse-1:** level B with reuse.
  - **A-min-1:** level A with the name and brand only.
- **Model:** gpt-4o, temperature 0, strict JSON schema `{ genericName, reusedExisting, isFood }`, one call per product, throttled to about 6,000 tokens a minute (the run took about an hour).

## Findings

### 1. How generic (levels A and B)

Both prompts share the rules below (section 4). Level A keeps descriptors that change what you'd buy; level B keeps only the basic food. The full table is at the end. Some contrasts:

| Product | A (shopper) | B (plain) |
|---|---|---|
| SEMI-SKIMMED BRITISH MILK | Semi-skimmed milk | Milk |
| Caster Sugar / Icing Sugar / Demerara | Caster sugar / Icing sugar / Demerara sugar | Sugar / Icing sugar / Sugar |
| Decaf Blend (coffee) | Decaf ground coffee | Decaf coffee |
| Smoked Sweet Paprika | Smoked paprika | Paprika |
| Beef stock cubes / Vegetable stock cubes | (kept apart) | Stock cubes |
| Pasta 'N' Sauce Cheese & Broccoli | Cheese and broccoli pasta | Pasta and sauce |
| Viennetta Vanille | Layered ice cream | Ice cream |

**Recommendation: A.** B loses distinctions that matter in a shop, and when B is wrong it's wrong in the shop, not just on the list.

### 2. Consistency

| Group | A-free-1 | A-reuse-1 | A-reuse-2 | B-reuse-1 |
|---|---|---|---|---|
| Udon noodles | ✅ Udon noodles | ✅ Udon noodles | ✅ Udon noodles | ✅ Udon noodles |
| Garden peas | ❌ Frozen peas / Garden peas | ✅ Peas | ✅ Garden peas | ✅ Peas |
| Coconut milk | ✅ Coconut milk | ✅ Coconut milk | ✅ Coconut milk | ✅ Coconut milk |
| Cumin seeds | ❌ Cumin / Cumin seeds | ✅ Cumin seeds | ❌ Cumin / Cumin seeds | ✅ Cumin seeds |
| Chopped tomatoes | ✅ Chopped tomatoes | ✅ Chopped tomatoes | ✅ Chopped tomatoes | ✅ Chopped tomatoes |
| Greek yogurt | ❌ Greek style natural yogurt / Greek yogurt | ✅ Greek style yogurt | ✅ Greek yogurt | ✅ Greek yogurt |
| Taco mixed beans | ❌ Mixed beans / Mixed beans in tomato sauce | ✅ Mixed beans | ❌ Mixed beans / Mixed beans in tomato sauce | ✅ Mixed beans |
| Pizza subs | ❌ Cheese and tomato pizza subs / Pizza subs | ✅ Pizza subs | ✅ Cheese and tomato pizza subs | ✅ Pizza subs |
| Gherkins | ❌ Pickled gherkins / Sliced gherkins | ❌ Pickled gherkins / Sliced gherkins | ✅ Pickled gherkins | ✅ Gherkins |
| Sugars (family) | Caster sugar / Demerara sugar / Icing sugar / Light brown muscovado sugar / White sugar | Caster sugar / Demerara sugar / Icing sugar / Light brown sugar / White sugar | Caster sugar / Demerara sugar / Icing sugar / Light brown sugar / White sugar | Brown sugar / Icing sugar / Sugar |
| Coffee (family) | Coffee bags / Decaf ground coffee / Ground coffee / Whole coffee beans | Coffee bags / Coffee beans / Decaf ground coffee / Ground coffee | Coffee bags / Coffee beans / Decaf ground coffee / Ground coffee | Coffee / Coffee bags / Decaf coffee |
| Paprika (family) | Paprika / Smoked paprika | Paprika / Smoked paprika | Paprika / Smoked paprika | ✅ Paprika |
| Microwave rice (family) | Long grain microwave rice / Microwave basmati rice / Microwave rice | Basmati rice / Microwave rice | ✅ Microwave rice | Basmati rice / Microwave rice |
| Instant noodles (family) | Curry noodles / Instant noodles / Ramen noodles | ✅ Instant noodles | Instant noodles / Ramen noodles | ✅ Instant noodles |
| Mustard (family) | Dijon mustard / English mustard / Mustard | Dijon mustard / English mustard / Mustard | Dijon mustard / English mustard / Mustard | Dijon mustard / English mustard / Mustard |
| Stock cubes (family) | Beef stock cubes / Vegetable stock cubes | Beef stock cubes / Vegetable stock cubes | Beef stock cubes / Vegetable stock cubes | ✅ Stock cubes |
| Muesli and granola (family) | Fruit and nut muesli / Granola / Swiss style muesli | Granola / Muesli | Granola / Muesli | Granola / Muesli |

Figures (77 products):

| Variant | Distinct names | Same in a second run | Reused an existing name |
|---|---|---|---|
| A, each product alone | 73 | 75 of 77 (97%) | – |
| A, with existing names | 63 | 56 of 77 (72%) in the **reverse** order | 17 |

- **Reusing existing names is what makes same-thing products merge** (8 of 9 groups, against 3 of 9 alone).
- **The order matters:** whichever product comes first sets the name ("Greek style yogurt" one way round, "Greek yogurt" the other). In real use that's fine: the first one named sets the name, and later ones follow it. For the one-off fill, process products in a sensible order (most recently bought first) and accept the result, or run the tidy-up pass.
- **The model's `reusedExisting` flag isn't reliable:** it said "reused" for Bran flakes, Apples and Carrots, which were only in the prompt's examples. Decide it in code: the name is reused if it equals (ignoring case) a name already in use.
- **Tidy-up pass (tested once):** one call with all 73 distinct level-A names merged Cumin seeds → Cumin, Garden peas → Frozen peas, Greek style natural yogurt → Greek yogurt, and Pizza subs → Cheese and tomato pizza subs. It kept every real difference. It's cheap (1,389 tokens). Its merges should be **suggestions** Gary accepts, not automatic, because it can pick the less natural canonical name (e.g. "Frozen peas").

### 3. Edge cases

| Case | Result | Note |
|---|---|---|
| Loose items | Apples, Carrots, Celery, Garlic, Leeks; Cherry tomatoes; Red kuri squash | Good; plural for things you count |
| Foreign packs | Cumin seeds, Sweetcorn, Onions, Pineapple in syrup, Mustard | Translated into UK English |
| Brand is the name | Viennetta → "Layered ice cream"; Starburst → "Fruit chews"; PG Tips → "Tea bags"; Quorn fillet → "Quorn fillets" | Gary to judge Viennetta and Starburst; the prompt's "keep the brand when it IS the product" rule was used for Quorn but not Viennetta |
| Different products with similar names | Hot Honey → "Honey" | ❌ Hot honey is a different thing to buy: a rule ("keep the flavour when it makes a different product") or a manual fix |
| Multipacks, ready meals | Pizza subs, Instant noodles, Cheese and broccoli pasta | Good |
| Garbled source names | "Sweet &quot" (Maggi) → Seasoning mix / Sweet sauce (unstable) | Garbage in: these need a manual name anyway |
| Not food | Effervescent vitamin C → flagged `isFood: false` | Use the flag to keep non-food off food features (e.g. recipes) |

### 4. Contract and wording rules

- **Schema** (`json_schema`, `strict: true`): `{ "genericName": string, "reusedExisting": boolean, "isFood": boolean }`. Keep `reusedExisting` only as a hint; code decides.
- **Rules** (in the prompt; see the script):
  - English, UK spelling.
  - Sentence case, keeping proper nouns (Dijon, Parmigiano Reggiano).
  - No brand, shop, range, size, count, pack format or marketing words.
  - **Plural for countable things** (Apples, Eggs, Udon noodles), **singular for uncountable** (Milk, Rice).
  - The brand only when it is the product's everyday name.
- **Post-processing in code:** trim; collapse spaces; cap at 40 characters; if it equals an existing name ignoring case, use the existing spelling (this is the merge).

### 5. Input

- **Name, brand, size, the 4 most specific English category tags, and the aisle.** With name and brand only, 11 of 77 names changed (66 of 77 the same).
- The extra input mostly made names **more generic and so more mergeable**: "Instant noodles" not "Beef and tomato instant noodles", "Microwave rice" not "Golden vegetable microwave rice", "Whole coffee beans" not "Colombian coffee beans", "Onions" not "Yellow onion".
- It made a few more specific: "Frozen peas" (from the categories) and "Decaf ground coffee".
- **Keep the full input.** It's cheap, and it pulls names towards the shopper level.

### 6. Cost and latency

| | Tokens | Cost (gpt-4o Standard) |
|---|---|---|
| One product, with the existing-names list | about 700 in + 19 out | about $0.002 |
| One-off fill, 181 products | about 130,000 | about $0.35 |
| Tidy-up pass over all names | about 3,000 | under $0.01 |

Median latency 521 ms (95th percentile 3.1 s, including throttling).

### 7. Control and the manual override

- **Expected corrections:** about 1 in 20 on this sample (Hot honey, possibly Viennetta and Starburst, the garbled Maggi item). That's rarely enough to be acceptable, as long as fixing one is easy.
- **Override:** a **"Generic name"** field in Edit details. Saving it sets `genericNameSource: "manual"`, and the model never overwrites it (as with aisle and shelf life).
- **Renaming:** changing a product's generic name updates **open** shopping-list items for that product (they reference the product, and show its generic name). Ticked or removed items keep their old text as history. If the new name equals another existing name, the list merges them, the same as two adds of the same thing.

### 8. Where else it helps

| Use | Worth it? |
|---|---|
| Shopping list items, autocomplete, merging | **Yes**: the reason for this spike |
| Push notifications ("Added Bran flakes") | **Yes**: shorter and clearer than "Special Flakes Bran Flakes" |
| Recipe prompt (#73) | **Yes**: cleaner ingredient names, fewer tokens |
| Icon search (#72) | Maybe: Jev already picks icons well; a later tweak |
| The stock list | No: keep the full product name there (brand and size matter at home) |

## Recommendation

- **Store `genericName` (level A), `genericNameSource` (`foundry` | `manual`), and `genericNameEstimatedAt` on each product.** Fill it **when a product becomes known** (looked up, named or identified), with the generic names already in use in the prompt and the reuse decided in code. Add a throttled **one-off fill** for existing products (most recently bought first).
- **Prompt:** level A from `docs/spikes/82-generic-names.py` (`LEVELS["A"] + REUSE`), plus one rule for flavours that make a different product (Hot honey).
- **Safeguards:** code-side reuse, length cap, `isFood`, and the manual override.
- **Optional:** a "tidy up names" suggestion (the consolidation pass) for Gary to accept or reject. Not needed at first.
- **Capacity:** keep it on the shared `gpt-4o` deployment, with the fill throttled. Day to day it's one call per new product.

## Every product (Gary: please mark any you'd change, and say A or B)

Level A and level B columns come from the "with existing names" runs. "A stable?" shows every level-A name the product got across the four level-A runs.

| Product (brand) | Level A | Level B | A stable? | Not food |
|---|---|---|---|---|
| apples | Apples | Apples | yes |  |
| Microwave Basmati Rice (Taste of) | Basmati rice | Basmati rice | Basmati rice / Microwave basmati rice / Microwave rice |  |
| Beef stock cubes (Newgate) | Beef stock cubes | Stock cubes | yes |  |
| Special Flakes Bran Flakes (Crownfield) | Bran flakes | Bran flakes | yes |  |
| carrots | Carrots | Carrots | yes |  |
| Caster Sugar (Belbake) | Caster sugar | Sugar | yes |  |
| celery | Celery | Celery | yes |  |
| Pasta 'N' Sauce Cheese & Broccoli (Batchelors) | Cheese and broccoli pasta | Pasta and sauce | yes |  |
| cherry tomatoes | Cherry tomatoes | Cherry tomatoes | yes |  |
| chopped kale | Chopped kale | Kale | Chopped kale / Kale |  |
| Chopped Tomatoes in a Rich Tomato Juice (Cucina) | Chopped tomatoes | Chopped tomatoes | yes |  |
| Chopped Tomatoes (Freshona) | Chopped tomatoes | Chopped tomatoes | yes |  |
| COCONUT MILK CLASSIC (Asia Green Garden) | Coconut milk | Coconut milk | yes |  |
| Coconut milk (Frashona) | Coconut milk | Coconut milk | yes |  |
| Rich Italian Coffee Bags (Taylors of Harrogate) | Coffee bags | Coffee bags | yes |  |
| Colombian Supremo Whole Coffee Beans (Deluxe) | Coffee beans | Coffee | Coffee beans / Whole coffee beans |  |
| Cumin Seeds (Asda) | Cumin seeds | Cumin seeds | yes |  |
| Kreuzkümmel (Kania) | Cumin seeds | Cumin seeds | Cumin / Cumin seeds |  |
| Decaf Blend (Bellarom) | Decaf ground coffee | Decaf coffee | yes |  |
| Demerara natural unrefined cane sugar (Billington's) | Demerara sugar | Sugar | yes |  |
| Dijon Mustard (Batts) | Dijon mustard | Dijon mustard | yes |  |
| Effervescent Vitamin C - Orange Flavour (MinaVit) | Effervescent vitamin C | Vitamin C tablets | yes | yes |
| 12 Medium Eggs (Merevale) | Eggs | Eggs | Eggs / Medium eggs |  |
| English Mustard (BATTS) | English mustard | English mustard | yes |  |
| Dairy Milk Fruit & Nut (Cadbury) | Fruit and nut chocolate | Chocolate | yes |  |
| Starburst Original FRUIT CHEWS BURSTING WITH FRUIT | Fruit chews | Sweets | yes |  |
| Garlic | Garlic | Garlic | yes |  |
| Raisin and Almond Granola (Crownfield) | Granola | Granola | yes |  |
| Creamy Greek Style Natural Yogurt (Milbona) | Greek style yogurt | Greek yogurt | Greek style natural yogurt / Greek style yogurt / Greek yogurt |  |
| Greek 10% fat authentic yogurt (Brooklea) | Greek style yogurt | Greek yogurt | Greek style yogurt / Greek yogurt |  |
| Deluxe Roast and Ground Coffee (Lidl) | Ground coffee | Coffee | yes |  |
| Hot Honey (Evie’s) | Honey | Honey | Honey / Hot honey |  |
| Icing Sugar (Belbake) | Icing sugar | Icing sugar | yes |  |
| Speedy Noodle Beef & Tomato Flavour (Newgate) | Instant noodles | Instant noodles | yes |  |
| NEWGATE SPEEDY noodle CURRY FLAVOUR (Newgate) | Instant noodles | Instant noodles | Curry noodles / Instant noodles |  |
| Classic Soy Flavour Ramen Noodles (Newgate) | Instant noodles | Instant noodles | Instant noodles / Ramen noodles |  |
| Temato ketchup Ingredients 72% temato pures single (Lidl) | Ketchup | Ketchup | yes |  |
| Viennetta Vanille (Viennetta) | Layered ice cream | Ice cream | Layered ice cream / Vanilla Viennetta / Vanilla ice cream |  |
| leek | Leeks | Leek | Leek / Leeks |  |
| light brown muscovado cane sugar (Belbake) | Light brown sugar | Brown sugar | Light brown muscovado sugar / Light brown sugar |  |
| Golden vegetable microwave rice (Taste of) | Microwave rice | Microwave rice | yes |  |
| Long Grain Microwave Rice (Lidl) | Microwave rice | Microwave rice | Long grain microwave rice / Microwave rice |  |
| Taco Mixed Beans (Freshona) | Mixed beans | Mixed beans | yes |  |
| Taco Mixed Beans in a Tomato Sauce (Four Seasons) | Mixed beans | Mixed beans | Mixed beans / Mixed beans in tomato sauce |  |
| Mocktail Pina Colada (Freeway) | Mocktail | Mocktail | Mocktail / Mocktail pina colada / Pina colada mocktail |  |
| Swiss style museli (Brownfield) | Muesli | Muesli | Muesli / Swiss style muesli |  |
| Fruit & Nut Muesli (Crownfield) | Muesli | Muesli | Fruit and nut muesli / Muesli |  |
| brown mushrooms | Mushrooms | Mushrooms | Brown mushrooms / Mushrooms |  |
| горчица (taste of deutschland) | Mustard | Mustard | yes |  |
| Organic nettle | Nettle | Nettle | Dried nettle / Nettle |  |
| Oignon jaune | Onions | Onion | Onion / Onions |  |
| Paprika | Paprika | Paprika | yes |  |
| Parmigiano Reggiano PDO (Deluxe) | Parmigiano Reggiano | Parmigiano Reggiano | yes |  |
| Levering pasta sauce | Pasta sauce | Pasta sauce | yes |  |
| British Garden Peas (Freshona) | Peas | Peas | Frozen peas / Garden peas / Peas |  |
| garden peas (Birds Eye) | Peas | Peas | Garden peas / Peas |  |
| Pickled Gherkins (Freshona) | Pickled gherkins | Gherkins | yes |  |
| Abacaxi em Caldas Lidl (Freshona) | Pineapple in syrup | Pineapple in syrup | yes |  |
| 2 Pizza Subs (Trattoria Alfredo) | Pizza subs | Pizza subs | Cheese and tomato pizza subs / Pizza subs |  |
| 2 Pizza Subs Cheese & Tomato (Pizza Afredo) | Pizza subs | Pizza subs | Cheese and tomato pizza subs / Pizza subs |  |
| fillet (Quorn) | Quorn fillets | Quorn fillets | yes |  |
| Quorn Pieces (Quorn) | Quorn pieces | Quorn pieces | yes |  |
| red kuri squash | Red kuri squash | Squash | yes |  |
| rolled oats | Rolled oats | Oats | yes |  |
| Sweet &quot (Maggi) | Seasoning mix | Seasoning | All purpose seasoning / Seasoning mix / Sweet sauce |  |
| All Purpose Seasoning | Seasoning mix | Seasoning | All purpose seasoning / Seasoning mix |  |
| SEMI-SKIMMED BRITISH MILK (Dairy Manor) | Semi-skimmed milk | Milk | yes |  |
| Sliced Gherkins (Freshona) | Sliced gherkins | Gherkins | Pickled gherkins / Sliced gherkins |  |
| Smoked Paprika | Smoked paprika | Paprika | yes |  |
| Smoked Sweet Paprika (Schwartz) | Smoked paprika | Paprika | yes |  |
| Mais groß (Freshona) | Sweetcorn | Sweetcorn | yes |  |
| PG Tips 210 Tea Bags (PG Tips) | Tea bags | Tea bags | yes |  |
| Udon Noodles (Taste of) | Udon noodles | Udon noodles | yes |  |
| UDON NOODLES (GREEN GARDEN) | Udon noodles | Udon noodles | yes |  |
| Vegetable Stock Cubes (Newgate) | Vegetable stock cubes | Stock cubes | yes |  |
| White Sugar (Sainsbury's) | White sugar | Sugar | yes |  |
| yellow courgette | Yellow courgette | Courgette | Yellow courgette / Yellow courgettes |  |

## Suggested follow-up cards (not created)

1. **[Feature] Generic product names** (draft description):
   > **Value:** every product gets a short shopping-list name ("Bran flakes"), so the shopping list, pushes and recipes read naturally and the same thing from different brands merges.
   > **Do:**
   > - Add `genericName` (level A), `genericNameSource` (`foundry` | `manual`) and `genericNameEstimatedAt` to products (`docs/data-model.md`).
   > - When a product becomes known (Open Food Facts, naming, photo), call gpt-4o (`IGenericNamer` in Application, Foundry in Infrastructure, managed identity, strict schema) with its name, brand, size, the most specific category tags and aisle, plus the generic names already in use. Decide "reused" in code (case-insensitive match takes the existing spelling); trim, cap at 40.
   > - Add a "Generic name" field to Edit details: saving sets `manual`, never overwritten.
   > - Add a one-off throttled fill of existing products (most recently bought first, about 6,000 tokens a minute, so as not to starve recipes and photos).
   > - Show the generic name on item detail under the full name.
   > **Tests:** prompt and schema, code-side reuse, caps, manual not overwritten, the throttle, a failure leaves the product without a generic name (retried by the timer).
   > **Verification:** run the fill, spot-check 20 products with Gary, fix one by hand.
2. **[Feature] Shared shopping list with autocomplete** (exists): use `genericName` for auto-adds, suggestions and merging.
3. *(Optional)* **[Task] Tidy up generic names:** the consolidation pass as suggestions Gary accepts.
4. *(Optional)* Use generic names in pushes ("Added Bran flakes") and in the recipe prompt.
