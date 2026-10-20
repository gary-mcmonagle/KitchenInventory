# Spike #99: Going back from item detail to the stock list without a reload

**Question.** Going back from item detail to the stock list loses your place: the list shows its loading state, refetches, and lands at the top. Why, and what's the best fix on the phone (installed iPhone app and Safari)?

**Answer in one line.** It isn't a page reload. The stock list **unmounts** when item detail opens and **remounts from scratch** on the way back. Keeping the list mounted (hidden) under the screens reached from it, and restoring the window's scroll, fixes it. A prototype on `spike/99-back-to-stock-list` (not merged or deployed) lands exactly where you were.

## How the app navigates today

- **No router library.** `FE/src/router.ts` is a tiny router: `navigate(path)` does `history.pushState` and fires `popstate`. `usePath()` reads `location.pathname` through `useSyncExternalStore`. The Static Web App serves `index.html` for every path. There's no service worker fetch handler (`FE/public/sw.js`).
- **No data library.** Each screen fetches its own data in a `useEffect`, with no cache. `StockScreen` starts in `phase: "loading"` (the placeholder rows) and calls `GET /api/stock`.
- **The window scrolls**, not an inner container (`App.module.css` `.page` has no overflow). So scroll restoration is about `window.scrollY`.

## The causes, with evidence

Measured on the live site (UI `0789c24`, test user, 375 px):

1. Scroll the stock list to `scrollY = 2500`.
2. Tap "White mushrooms".
3. Press the browser back button.

| | Before | Just after back (60 ms) | 3 s later |
|---|---|---|---|
| `scrollY` | 2500 | **0** | **0** |
| Loading placeholders | — | **shown** | gone |
| `GET /api/stock` calls | 1 | | **2** (refetched) |
| SignalR negotiate | 1 | | **2** |
| Tapped item in view | — | | **no** |

1. **Not a full page load.** Every link goes through `navigate()` (`pushState`), and the browser back button only fires `popstate`. `index.html` and the JS bundle aren't fetched again.
2. **The list unmounts.** `Shell.tsx` renders exactly one screen per path. On `/products/{id}` the `StockScreen` is gone, so all of its state goes too:
   - the stock data;
   - **Use first** (deliberately not remembered);
   - its scan feed connection handle;
   - the toast.

   On the way back it mounts fresh: placeholders, a refetch, and a scan feed catch-up.
3. **Scroll goes to the top.** While item detail shows, the page is short, so the browser clamps `scrollY` to 0. The remounted list then starts at 0. It also couldn't restore earlier, because its rows don't exist until the refetch lands.
4. **Aisle open/closed** is read from `localStorage` synchronously on mount, so it already comes back right. The **search** query (#91) is in module memory, so it survives too. **Use first** does not.
5. **"‹ Stock" pushes a new history entry** (`navigate("/")`) instead of going back. So each round trip via ‹ Stock adds two entries, and a later browser back (or iOS swipe) goes to the item again rather than out of the app.

### The back paths

- **Browser back and ‹ Stock:** the same remount, the same result. ‹ Stock also grows the history.
- **iOS edge swipe in the installed app:** an iOS history back, the same `popstate`. **To check on Gary's iPhone:** whether his iOS version offers the edge swipe in the installed app at all, and how it looks with the prototype.

## Options compared

| Option | Lands where you were | No loading flash | Browser back / iOS swipe | Live updates while away | Deep links | Effort | Fits other screens |
|---|---|---|---|---|---|---|---|
| **A. Keep the list mounted under the screens reached from it** (hidden, `display: none`) + save `scrollY` when leaving and restore it on return | **Yes**: exact pixel position, item in view | **Yes**: nothing refetches | Yes: both are `popstate` | **Yes**: the list's SignalR handlers keep running | Yes: a direct item open has no list behind it; ‹ Stock navigates to `/` at the top | **Small**: Shell + router, about 40 lines | Scanners, Add, Name it and Edit sit over the list the same way; Stock check (#98) can too |
| B. A shared stock cache (React Query-style) shown at once on return, refreshed in the background, + scroll restore | Mostly: needs the rows rendered before restoring | Yes, if cached | Yes | Only if the cache gets the SignalR updates (more wiring) | Yes | **Medium**: a new library or a hand-rolled cache, wired into the feed | Each screen opts in |
| C. Item detail as an overlay/sheet route on top of the list | Yes (the list never moves) | Yes | Needs care: the overlay must be a history entry | Yes | A direct item open needs a "no list behind" mode | **Medium–large**: a different layout and transitions | Other screens would need the same treatment |
| D. View Transitions API / CSS slide | — (an add-on, not a fix) | — | Safari 18 (iOS 18) supports same-document view transitions; earlier iOS falls back to no animation. Must not fight the native swipe animation. | — | — | Small, after A | Nice-to-have |
| E. Scroll to the tapped item if the list changed | Complements A | — | — | — | — | Small | — |

## Recommendation

**Option A.** It's the smallest change, it lands exactly where you were, and it keeps everything on the list (data, aisles, search, Use first, live updates) for free. It also matches how the app is built: a few screens, no router or data library.

Pair it with:

- **‹ Stock steps back through history** when we came from the list (`history.state.from === "/"`). Otherwise, for a deep link, it navigates to `/`. So the in-app button, the browser back and the iOS swipe all agree, and history doesn't grow.
- **The scroll position is recorded in `navigate()`** at the moment of leaving, and restored in a layout effect when the list shows again. Don't use a `scroll` listener: the clamp to 0 when the list hides would overwrite it, and background tabs don't fire scroll events. Set `history.scrollRestoration = "manual"`.
- **E later**, if needed: when the list changed while away (e.g. the item was checked out in detail), the restored position may point at a different row. It's a small add-on and was not needed in testing.
- **D (a slide transition) as a separate nice-to-have**, once A is in. Try it on Gary's iPhone first, because of the native swipe animation.

## The prototype (`spike/99-back-to-stock-list`, not merged)

**Changes:**
- **`Shell.tsx`:**
  - The stock list is rendered whenever the path is `/` or a screen reached from it (item detail, Name it, Edit, Add, store-label assign, Scanners), inside `<div hidden>` when it isn't showing.
  - A layout effect restores `savedScroll("/")` when the list shows again, and scrolls other screens to the top.
- **`router.ts`:**
  - `navigate()` records `scrollY` for the path being left, and pushes `{ from }` state.
  - `backTo(path)` steps back through history when we came from there, else navigates.
- **`ItemScreen.tsx`:** ‹ Stock uses `backTo("/")`.

**Measured locally** (Vite dev server against the live API, test user, 375 px):

| | Browser back | In-app ‹ Stock |
|---|---|---|
| `scrollY` before → after | 2500 → **2500** | 2500 → **2500** |
| Tapped item ("White mushrooms") in view | **yes** | **yes** |
| Loading placeholders | **none** | **none** |
| `GET /api/stock` calls before → after | 2 → **2** (no refetch) | 2 → **2** |
| History length | unchanged | **unchanged** (it steps back) |

- **State kept:** Use first on plus the search "milk" both came back as they were, with the same sections shown.
- **Deep link:** opening `/products/{id}` directly, then ‹ Stock, landed at the top of the list (`scrollY 0`). That's a normal navigate, since there was nothing behind it.

**Found by the prototype (the fix must handle it):** the "Added 6 × Bananas" toast after the Add screen is read once when the stock list **mounts** (`takeFlash()` in `StockScreen`). A kept-mounted list has to read it when it becomes **visible** again. One existing test (`AddScreen.test.tsx`, "+ Add in the stock list header … shows the toast after adding") fails on the prototype for exactly this reason.

## Other list → detail → back paths

- **Hungry? → item detail** (the ingredient links in a recipe, and the Ready now "Uses" links). Going back **loses the generated recipe or ideas**, because `RecipesScreen` unmounts. Worse, ‹ Stock goes to the stock list, not back to the recipe. The same approach covers it: keep the tab screens (Stock, List, Hungry?) mounted while a screen reached from them shows, and make ‹ go back through history.
- **Shopping list:** has no item detail links today. The list's state lives in `ShoppingListProvider` (app-wide), so it already survives.
- **Search (#91)** keeps working: the query is in memory, and the kept-mounted list keeps it anyway.
- **Stock check (#98):** its screen would sit over the list like Scanners. The list's live updates while away carry its changes.

## Suggested follow-up cards (not created)

### [Feature] Going back to the stock list lands where you were

Draft description:

> **Value.** Going back from item detail (or Scanners, Add, Name it) to the stock list lands exactly where you were: the same scroll, aisles, search and Use first, with no loading flash and no refetch, whether you tap ‹ Stock, use the browser's back, or swipe back on iPhone.
>
> **Type:** Feature. **Depends on:** nothing. **Components:** UI. Follow spike #99 (`docs/spikes/99-back-to-stock-list.md`), option A.
>
> **What to do:**
> - **Shell:** keep the stock list mounted (hidden with `display: none`) while a screen reached from it is open: item detail, Name it, Edit details, Add, store-label assign, Scanners, and later Stock check.
> - **Router:**
>   - `navigate()` records the scroll position of the screen being left;
>   - the Shell restores it in a layout effect when the list shows again (`history.scrollRestoration = "manual"`);
>   - other screens open at the top.
> - **Back buttons:** ‹ buttons step back through history when we came from that screen (`history.state.from`), and otherwise navigate (deep links).
> - **The Add toast:** the stock list shows it when it becomes visible again, not only on mount.
> - **Hungry?:** apply the same to Hungry? → item detail, so a generated recipe or ideas survive the trip, and its ‹ goes back to the recipe.
>
> **Tests:** scroll restored after back and after ‹; no refetch and no placeholders on return; Use first, search and aisles kept; deep link → ‹ lands at the top; history doesn't grow via ‹; the Add toast shows on return; the recipe survives a trip to item detail.
>
> **Verification:** at 390 px, scroll far down, open an item, go back by ‹ and by the browser back → the same place, no flash, no `/api/stock` call. On Gary's iPhone: the same, plus the edge swipe if his iOS offers it.

### [Feature] A slide between the stock list and item detail (optional)

The View Transitions API (Safari 18+) or a CSS slide, tried on Gary's iPhone so it doesn't fight the native swipe animation.

## Not checked

- On Gary's iPhone: the installed app's edge swipe and how the prototype feels. It needs a preview build or a deploy of the follow-up. Recommend checking it as part of the follow-up's verification.
- Network-throttled behaviour. The agent's browser can't throttle, and option A makes no network calls on back anyway.
