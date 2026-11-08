# Spike #116: the same product shown twice with different casing

Run on 2026-11-08, read-only against the live system (nothing written, merged or renamed). The scan is
[`116-name-casing-duplicates.py`](116-name-casing-duplicates.py) (`dump` then `report`; the dump has ids and barcodes, so it stays
outside the repo). Figures are from 352 products (340 known), 493 units, 411 of them in stock, in 296 in-stock products.

## 1. The question

Gary saw the same thing in stock under names that differ in casing, and suspected image identification or manual input needing
Jev matching. Why does it happen, how widespread is it, and what is the fix?

## 2. Answer in short

- **It is mostly not duplicate products. It is a display rule.** The stock list shows one row per **product**, and a row's title is
  the product's generic name **unless the generic name equals the display name ignoring case**, in which case it shows the display
  name **as stored** (`stockRowText`, `FE/src/format.ts`). Generic names are always in one spelling (sentence case, "Baked beans"),
  but display names come as the source gave them ("Baked Beans", "UDON NOODLES", "potatoes"). So two products with the same generic
  name show as "Baked beans" and "Baked Beans", "Udon Noodles" and "UDON NOODLES", "carrots" and "Carrots".
- **How widespread:** 119 of 296 in-stock rows (40%) take their title from the display name as stored. 35 groups (76 rows) have the
  same title ignoring case, and **14 groups (31 rows) show it with different casing**.
- **Real duplicate products exist but are few:** 12 groups (24 products) have the same display name ignoring case. They are
  separate products by design (a different barcode, or loose against barcoded), and all of them share one generic name.
- **Jev matching would add almost nothing.** The generic name (#90) **is** that Jev match, and it already puts every one of these
  under one name. The one near-duplicate the exact scan misses is "apples" / "Apple".
- **Recommendation:** fix the display rule (a one-line change plus tests), so a product with a generic name always leads with the
  generic name. Optionally then group rows that share a generic name. Don't build more matching, and don't merge anything yet.

## 3. What the photo shows

Gary's photo (the stock list) shows two rows: **"carrots"** (x3, "Opened 18 Oct") and **"Carrots"** ("British carrots · 1kg", bought 19 Oct).
The live data matches it exactly, and it is the display rule in section 2, **not a pair of same-named products**:

- "carrots" is a **loose product added from a photo**. Its display name is "carrots", the generic name (Jev, sentence case) is "Carrots", and
  those are equal ignoring case, so the row leads with the display name **as stored**: "carrots".
- "Carrots" is a **barcoded Open Food Facts product** named "British carrots" (1 kg). Its generic name is also "Carrots", but its display name differs
  from that, so the row leads with the **generic name**: "Carrots", with "British carrots" underneath.

So the same generic name shows as two spellings depending on which rule each product falls under. The two products are not in the
same-name group of section 5.1 (their display names differ: "carrots" and "British carrots"); they are one of the 14 casing groups in
section 5.4. Nothing was duplicated by image identification or manual input, and Jev matching wouldn't change this: the Jev generic
name already is the same. The loose product's photo-recognised name "carrots" is correct and is kept.

## 4. How names get into the data

| Path | Name stored | Goes through `ProductMatcher`? |
|---|---|---|
| Scan check-in, then Open Food Facts lookup (`ProductResolver`) | `product_name_en`, else `product_name`, **exactly as returned** (Gary, 2026-10-17), so often in capitals | **No.** A barcode is its own identity, so it never links to an existing name |
| Photo recognition, then Add (`RecogniseProductPhoto`, `AddToStock`) | the model's name | **Yes**: any known product, barcoded or loose, with the same name ignoring case and spaces (loose first) |
| Typed add (`AddToStock`) | what was typed | **Yes**, the same way |
| Name a barcoded product (`NameProduct`) | what was typed | No, it names the product the scan made |
| Edit details (`EditProductDetails`) | what was typed | No |
| Generic name (#90, `AssignGenericNames`, Edit details) | sentence case, one spelling per generic name id (checked: 284 ids, no id has two spellings) | n/a: it is the grouping |

`Product.NameKeyOf` (lower case, single spaces) is used by the matcher and by the generic name id. Nothing else compares names.
There is no `Jev` step before a product is created.

## 5. The data

### 5.1 Same name ignoring case: 12 groups, 24 products

| Kind | Groups | Examples |
|---|---|---|
| Two barcoded Open Food Facts products (two barcodes, same name) | 8 | "Chickpeas in Water" x2, "Dark Soy Sauce" x2, "Semi-Skimmed Milk" x2, "Tomato & Mascarpone Stir Through Sauce" x2; and with different casing "Baby Spinach" / "BABY SPINACH", "Cannellini beans in water" / "Cannellini Beans in Water", "Pak choi" / "Pak Choi", "Udon Noodles" / "UDON NOODLES" |
| A loose product and a barcoded one | 4 | "celery" / "Celery", "Garlic" / "Garlic", "Pears" / "Pears", "potatoes" / "Potatoes" |

- **6 of the 12 differ in casing** (the four above, plus "celery" and "potatoes"); the other 6 are identical text.
- **All 12 groups share one generic name**, so the generic name already says they are the same thing.
- **How the loose and barcoded pairs happened:** in all four, the loose product (photo or typed add) came **first** (18 to 19 Oct) and
  a barcode scan of produce created its own Open Food Facts product 1 to 2 weeks later (26 Oct, 2 Nov). The scan path never
  consults the matcher, so nothing linked them. The matcher itself works: when the barcoded product comes first, a photo add finds it.
- **No race or stale cache:** no pair was created within seconds of each other, and no two loose products share a name.

### 5.2 Near duplicates (no brand, size, punctuation or plural s): 13 groups

The same 12 plus **"apples" (loose) / "Apple" (barcoded)**. So an extra Jev step "is this one of the existing products?" would have
found one more pair in 340 products, and the generic name already joins that one too.

### 5.3 Capitals

15 known products are in all capitals, **all from Open Food Facts** (for example "UDON NOODLES", "COCONUT MILK CLASSIC", "BARISTA OAT",
"SEMI-SKIMMED BRITISH MILK"); no photo or typed name is. Casing styles of the 340 known names: Title Case 203, sentence case or
mixed 87, lower case 34, all capitals 16 (one is non-Latin). Display names have no stray spaces.

### 5.4 What the stock list shows

296 rows, one per in-stock product. **35 groups (76 rows) have the same title ignoring case.** 14 of those groups (31 rows) show it with
different casing, for example "Baked beans" / "Baked Beans" / "Baked beans", "Basmati Rice" / "Basmati rice", "Dark chocolate" / "Dark
Chocolate", "carrots" / "Carrots", "apples" / "Apples", "Udon Noodles" / "UDON NOODLES". The other 21 groups have identical titles
(for example two rows both called "Chickpeas", told apart only by the muted line). If every row led with its generic name, **no group
would show different casing** (checked on the live data), because there is one spelling per generic name.

## 6. Options

| | Fixes | Effort | Risk |
|---|---|---|---|
| **(c) Display rule: a product with a generic name always leads with it**; the display name goes in the muted line when it differs from the generic name ignoring case | All 14 casing groups. Applies to every screen that uses `stockRowText` | Very small: change `stockRowText` and its tests; check other screens that show a product name (item detail, search, check-out toast, shopping list) | Very low. Every known product has a generic name today; a product without one keeps its display name |
| **(e) Group rows that share a generic name** on the stock list (one row "Baked beans x5", expands to the products) | The other 21 groups too, and 35 groups overall; 76 rows become 35, so the list is about 41 rows shorter | Medium: a design question (counts, expiry, check-out, which product to check out from), so it needs a mockup first | Medium: changes how check-out and "use first" work |
| **(a) Match across all creation paths** (a barcode scan reuses a loose product with the same name) | The 4 loose/barcoded pairs, going forward | Small to medium | Medium: "Garlic" from a shop's produce barcode is the same thing, but a brand named like a product ("Birds Eye" peas) is not, and linking changes barcode identity |
| **(b) A Jev "is this an existing product?" step** | One extra pair ("apples"/"Apple") | Medium, plus a Jev call per add | Low, but little to gain: the generic name is that match already |
| **(d) A one-off merge** of existing duplicates | The 12 groups | Small script, Gary approves each | Medium: it rewrites unit and barcode links; the generic name already groups them, so (e) removes the need |

## 7. Recommendation

1. **Do (c) now.** It is the actual cause, it removes every casing difference at once, and it needs no data change.
2. **Consider (e) next**, as a design question for Gary (the 35 groups, 76 rows). It is the real fix for "the same thing twice" and is
   built on generic names, which are already done and consistent.
3. **Don't do (b)**: the generic name is already the Jev match, and the exact scan finds one pair it would add.
4. **Don't do (a) or (d) yet.** The 4 loose/barcoded pairs and 8 barcode pairs are correct as separate products and are joined by (c) and
   (e). Revisit if (e) is not wanted.
5. **Leave Open Food Facts names as returned** (Gary's 2026-10-17 decision): with (c) a capitalised name only shows in the muted line.

## 8. Suggested follow-up cards (not created)

1. **[Task] Stock rows always lead with the generic name** (option c): `stockRowText`, its tests, and a check of every screen that shows a
   product name, so "Baked Beans" and "UDON NOODLES" never lead a row. FE only; deploy `deploy/ui.sh`.
2. **[Feature] Group stock rows that share a generic name** (option e), after a mockup: one row per generic name with a count, expanding
   to its products. Needs Gary's design decision on check-out and "use first".
3. **[Task] (Optional) Show the display name in capitals as sentence case in the muted line**, if Gary dislikes "UDON NOODLES" there.

## 9. What was and wasn't changed

Nothing live was changed: queries only (`SELECT` on `products` and `inventoryItems`), as the signed-in developer. The dump with ids and
barcodes stays in the scratchpad, outside the repo. Product names above are household groceries.
