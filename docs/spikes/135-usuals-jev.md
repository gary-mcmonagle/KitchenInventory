# Spike #135: can Jev judge what's due on the shopping list?

Run on 2026-11-23 with `jev-1.13.0`, read-only against the live data (nothing written, merged or deployed). The scripts are
[`135-usuals-jev.py`](135-usuals-jev.py) (`dump`, `backtest`, `weekly`); the dump has product ids, so it stays outside the repo. The Jev key is
read from the git-ignored `local.env` by the script and is never printed or logged. Card: https://trello.com/c/VEDluGKp

## 1. The question

Gary wants an **Add usuals** button on the shopping list that offers what the household usually buys and is due again, with **Jev from
day one** deciding "is this due?" (mockup: https://claude.ai/artifact/HrZHuCEUkig3BpHKQsZeeQ). Stock must not be an input. Before building:

1. Does Jev make good due-or-not judgements from a few weeks of purchase history, and how does it compare with a plain rule?
2. Which facts, question wording, cut-offs and batching?
3. What are the real latency, token use and cost?

## 2. Answer in short

- **Jev works, and costs nothing, but on five weeks of data it is only a little better than a simple "how often is it bought" rule, and
  nothing predicts well.** Best figures: **AUC 0.66 to 0.69 for Jev, 0.62 to 0.66 for the best plain rule, 0.54 for a "days since last /
  usual gap" rule** (0.5 is a coin toss). The Jev advantage (+0.02 to +0.04) is **inside the noise** (about ±0.05 on about 120 decisions).
- **What actually predicted best is "how many of the recent weeks was it bought in".** The household does **one big shop a week** (a
  Monday, once a Tuesday: 76, 57, 70, 70 and 49 items, against 1 to 18 on every other day), so the history is cleaner in weeks than in
  calendar-day gaps. Gaps in days are noisy and barely beat chance.
- **Stock does not help; it hurts.** Items recorded as holding 4 or more units were bought that week **51%** of the time (1 to 2 units: 28
  to 36%), stock alone scores AUC 0.47, and subtracting a penalty for stock drops Jev from 0.66 to 0.58. This supports Gary's decision to
  leave stock out (my own hypothesis, that the false positives were things already in the cupboard, was wrong).
- **Speed and cost are not a constraint:** median 0.19 s a call, 476 calls cost about **$0.0075**; a run for 60 candidates is about 3 s
  and a thousandth of a cent.
- **Recommendation: build it with Jev, as one yes/no (`noul`) question per candidate, with the history given as a plain sentence per
  Monday-to-Sunday week; keep the plain share-of-weeks rule as the fallback; pre-tick only the strongest band; tell Gary to expect a
  preview that is right about half to 60% of the time at first.** Re-run these scripts when there are eight to ten weeks of history.

## 3. How it was measured

For each "as of" date the script uses **only the check-ins before that date**, asks "is each candidate due?", and compares with what was
actually **checked in over the following 7 days** ("bought again"). Check-ins since the stock take (19 Oct) count, by UK day; units whose
check-in was undone and "Gone" check-outs are left out. Candidates are generic names (display name when none) bought on two or more days
(or in two or more weeks). Stock is never used by the rules or sent to Jev.

| Framing | As-of dates | Decisions | Base rate (bought again) |
|---|---|---|---|
| Calendar days | 25 Oct, 1 Nov, 8 Nov, 15 Nov | 131 (5, 23, 41, 62 candidates) | 43% |
| Weeks (predict week 3, 4, 5 from the weeks before) | 2 Nov, 9 Nov, 16/17 Nov | 119 (20, 40, 59) | 41% to 42% |

AUC is the chance that a bought item scores higher than a not-bought one, so it doesn't depend on a cut-off. It's the number to compare.
It's a **proxy**: "not bought this week" can mean "not needed" or "still got plenty", and the household varies its basket.

## 4. Findings

### 4.1 The plain rules (no Jev)

| Rule | AUC | Notes |
|---|---|---|
| Days since last purchase / usual gap in days | **0.54** | Cut 0.5 lists 78% of candidates at 48% precision: useless |
| Purchase days a week since the stock take | 0.66 | Cut 0.75: lists 48%, precision 0.59, recall 0.66 |
| That times how overdue (gap ratio capped at 1.5) | 0.65 | |
| **Share of weeks it was bought in** | **0.62 to 0.63** | Cut 0.75: lists 43%, precision 0.53, recall 0.55 |
| The same, recent weeks counting more | 0.62 | No better |
| Bought last week | 0.55 | |

So **"usual gap in days" is the wrong idea for this household**: it shops on one day a week and buys a varying basket, so what matters is
"how often is it in the weekly shop", not "how many days since".

### 4.2 Jev

One `noul` (yes/no probability) question per candidate, three ways of giving the facts (v1 numbers, v2 the same in words, v3 plus shelf life
and how long a pack lasts) and two wordings:

- `q1` (days) "This household usually buys this item. It is time to buy it again this week."
- `q2` (days) "The household shops about once a week. Given how often they usually buy this item and how long it is since they last bought it, they will probably need to buy it again in the next 7 days."
- `w1` / `w2` (weeks) the same ideas for "the next weekly shop".

| Framing and setup | AUC |
|---|---|
| Days, one request per candidate: v1 q1 / v1 q2 / v2 q1 / v2 q2 / v3 q1 / v3 q2 | 0.68 / 0.67 / 0.67 / 0.68 / **0.69** / 0.67 |
| Days, **one request for all candidates**: v1 q1 / v1 q2 / v2 q1 / v2 q2 / v3 q1 / v3 q2 | 0.65 / 0.65 / 0.63 / 0.65 / 0.66 / 0.67 |
| Weeks, one request per candidate: w1 w1 / w1 w2 / w2 w1 / w2 w2 | 0.64 / 0.65 / 0.65 / **0.66** |
| Weeks, calendar weeks Monday to Sunday (no shop detection): the same four | 0.65 / 0.65 / 0.66 / **0.66** |
| Jev averaged with the share of weeks | 0.64 to 0.65 (no gain) |

- **One request per candidate beats putting everyone in one request** by about 0.02 to 0.04 AUC. Use one request each, four at a time.
- **The wording and the extra facts hardly matter** (all within 0.03). Shelf life and "how long a pack lasts" added nothing. Numbers against
  words made no difference either, in spite of Jev 1.13's noted weakness with numbers.
- **Jev's probabilities run high**: most candidates score 0.6 or more, so a cut-off of 0.5 lists nearly everything. Cut-offs that mean
  something (weeks framing, `w2 w2`):

| Cut-off | Lists | Precision | Recall |
|---|---|---|---|
| 0.6 | 67% of candidates | 0.51 | 0.84 |
| 0.7 | 47% | 0.50 | 0.57 |
| **0.8** | **28%** (about 15 to 20 items from 60 candidates) | **0.58** | 0.39 |

  The base rate is 0.41, so the "Due" band (0.8 and above) is right about **58%** of the time, and the 0.6 to 0.8 band (about 47 items at
  about 0.47) is barely better than picking at random.
- **The top of the list is where Jev is best:** precision in the top 10 (averaged over the shops) is **0.57 to 0.60** for Jev against 0.53
  for the share-of-weeks rule, and about the same (0.48 to 0.52) at the top 20.

### 4.3 Hits and misses (the shop of 17 Nov, `w2 w2`, weeks oldest first, Y = bought)

- Right: medium eggs (YYYY) 0.93, Greek yogurt (YYYY) 0.92, broccoli (YYYY) 0.90, chicken breast fillets (-YYY) 0.80, orange juice (-YYY) 0.78.
- Wrong, scored high but not bought that week: unsweetened soya milk (YYYY) 0.92, pears (YYYY) 0.89, dark chocolate (YYYY) 0.89, chickpeas
  (YYYY) 0.89. They were bought every week before, so they look like the surest staples; this week they weren't.
- Missed, bought but scored low: potatoes (YY--) 0.59, chicken thigh (Y--Y) 0.59, celery (--YY) 0.55, curly kale (Y-Y-) 0.52, soy sauces (Y--Y)
  0.50, ginger (-YY-) 0.50: irregular things that happen to be bought.

Nothing here is an error in the arithmetic: with four weeks of history, "bought every week so far" and "bought irregularly" can't be told
apart from a single quiet week.

### 4.4 Stock (measured, not used)

Recorded units held just before each shop against whether the item was bought that week (119 decisions):

| Held | Items | Bought that week |
|---|---|---|
| 0 | 5 | 80% |
| 1 | 22 | 36% |
| 2 | 36 | 28% |
| 3 | 21 | 43% |
| 4 or more | 35 | 51% |

| Score | AUC | Top 10 | Top 20 |
|---|---|---|---|
| Jev `w2 w2` | 0.66 | 0.57 | 0.48 |
| Jev minus 0.3 if 2 or more held | 0.64 | 0.53 | 0.52 |
| Jev minus 0.3 if 3 or more held | 0.58 | 0.53 | 0.50 |
| Jev minus 0.15 a unit held | 0.59 | 0.53 | 0.50 |
| Stock alone (fewer held = more likely) | 0.47 | 0.40 | 0.43 |

Recorded stock carries no useful signal: staples pile up in the records because their check-outs are missed (a check on 2026-11-23: 250 of
the 499 units "in stock" were checked in over 30 days earlier, and 106 were past their estimated dates).

### 4.5 Latency, tokens, cost

476 single calls (weeks framing, four variants over three shops): **179,528 input tokens, about $0.0075**, median **0.19 s**, max 0.67 s, four
in parallel. That is about 380 tokens a candidate. A run for 60 to 100 candidates is about 25 to 40k tokens (a tenth of a cent), 2 to 5 s
wall time, far inside the "a minute" Gary allowed. A year of weekly runs is under 10 cents.

## 5. Recommendation

Build **[Feature] Usuals** with Jev as Gary asked. Specifically:

1. **Facts (code):** per generic name (the display name when it has none), count check-ins by **Monday-to-Sunday UK week** since
   `USUALLY_BUYS_HISTORY_START`, ignoring undone camera check-ins; the current, unfinished week is not part of the history. Candidates are names bought in **two or more weeks**. Calendar weeks matched shop detection exactly
   here, so no "big shop" detection is needed.
2. **Question to Jev:** `noul`, one request per candidate, four in parallel, the `w2` wording, with the state

   ```json
   { "item": "medium eggs", "aisle": "dairy and eggs",
     "history": "The household does one big shop a week. In the last 4 weekly shops (oldest first), was this bought that week? yes, yes, yes, yes. Bought in 4 of 4 weeks; bought in the most recent shop." }
   ```

   and the instruction "They are about to write the list for the next weekly shop. Given how often this item has been in their weekly shops,
   it will probably be needed on this one." Pin `jev-1.13.0`. No stock, no shelf life, no names of people.
3. **Cut-offs (settings, defaults from this data):** **Due** at probability 0.8 or more (pre-ticked); **Maybe** from 0.6 up to 0.8 (shown, not
   ticked), capped so the whole preview stays near 30 items (best first). Expect Due to be right about 55 to 60% of the time and Maybe about
   half that again: say so on the card and in the sheet's wording ("looks due", not "needed").
4. **Why line (code, not the model):** "Bought in 4 of the last 4 weeks".
5. **Fallback when Jev is off or fails:** share of weeks bought, **Due at 0.75 or more** (about 43% of candidates, precision about 0.53 to
   0.59), **Maybe** from 0.5. The sheet says "Based on your buying pattern only."
6. **Timeouts:** the existing `JevOptions` (10 s a call, three attempts) per candidate, four at a time, and an overall budget of about 60 s
   for the job; on exceeding it, finish with the fallback.
7. **Keep this script as the benchmark.** Re-run `weekly --iso-weeks` at eight to ten weeks of history and again after any change to the
   facts or wording: the gap between Jev and the plain rule, and the whole level, should improve as the history grows (more weeks per
   item). Nothing in the app needs to remember what people untick for that.

The honest expectation for day one: the preview will be useful for the dozen or so surest staples and a coin toss beyond that. The
design (tick to add, nothing remembered, "Maybe" unticked) suits that.

## 6. Suggested follow-ups (not created)

1. **Update [Feature] Usuals (#134)** to this recommendation: weeks not days, `noul` per candidate, cut-offs 0.8 / 0.6, fallback share of weeks
   at 0.75 and 0.5, and an honest expectation for the sheet's wording. (The card already says the spike's write-up wins.)
2. **[Task] Re-run the usuals benchmark** after eight to ten weeks of history (or when a person asks for better results), and consider adding
   a feedback signal then (what was ticked and what was later bought) if the numbers are still flat.
3. **The in-stock indicator and "mark out of stock"** (parked): worth doing for the list's own sake, but on this evidence not as an input to
   what gets suggested. Its usefulness depends on cleaning the check-out data first (Clear out and the camera help).

## 7. What was and wasn't changed

Queries only (`SELECT` on `products` and `inventoryItems`) as the signed-in developer, and about 1,300 Jev calls (cached on disk outside the
repo) at a total cost of well under a cent. Nothing was written to Cosmos, deployed or changed in the app; the dump and the Jev cache stay in
the scratchpad, outside the repo, and the key was only read from `local.env` by the script.
