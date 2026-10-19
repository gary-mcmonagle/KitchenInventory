# Spike #83: Offline shopping list on iPhone

Card: https://trello.com/c/hRPLFylw

## Question

Can the installed Cupboard Tracker web app **open and work with no signal** (say, in a shop basement), let people **tick items off and add items offline**, and **sync those changes** when the connection comes back, reliably on **iPhone**? What should [Feature] Shopping list works offline be built on?

## Summary

> **Decision (Gary, 2026-10-19): test on the real app, not the proof of concept.** The iPhone Airplane Mode check (open offline, tick and add, sync on reopening) becomes part of verifying [Feature] Shopping list works offline, on the installed app at `kitchen.garymcmonagle.com`. No tunnel was opened for this spike.

- **Yes, with a standard "offline-first" design.** The proof of concept (`docs/spikes/83-offline-poc/`) does all of it in desktop Chrome:
  - the service worker caches the app shell, so the app opens offline;
  - the list lives in IndexedDB on the phone;
  - every change goes into an **outbox** first and is replayed in order when the network is back;
  - the server applies each change **once**, by change ID.
- **iPhone:** not run here. Gary chose to test it on the real app, as part of [Feature] Shopping list works offline (see the decision above).
- **Sign-in isn't a blocker.** MSAL's start-up (`initialize`, `handleRedirectPromise`, the cached account) needs **no network** (measured: 2 ms with Microsoft unreachable), so the installed app opens signed-in offline and shows the local list. Only API calls need a token. If the token can't be renewed offline, changes simply wait in the outbox. If the 24-hour refresh token has expired, they wait until the user signs in again online.
- **iPhone storage is the main risk, and it's manageable:**
  - Safari's "delete script data after 7 days without use" rule applies to Safari, and a **Home Screen app keeps its own counter that resets each time it's used** [1][2].
  - iOS 17+ gives an installed app the **same large quota** as Safari [3].
  - `navigator.storage.persist()` exists, but WebKit grants it by heuristics ("whether the website is opened as a Home Screen Web App") [3], with an open bug where it always returns `false` [4]. So the design **never relies on the phone being the only copy for long**: it syncs at every chance, and the server is the source of truth.
- **No Background Sync on iPhone** (Safari doesn't support it at all [5]), so sync runs **when the app is open**:
  - when it opens;
  - when it comes back online;
  - when it returns to the foreground;
  - every 30 s while visible.

  Changes made offline go up the next time the app is opened with signal.
- **Conflict rules** (proposed; worked examples below):
  - adds of the same name **merge** while the item is open;
  - a tick only beats an **older** untick;
  - **removal wins**;
  - re-adding something already ticked off makes a **new** item, because it's needed again.
- **This changes [Task] Make the app installable (PWA):** its service worker must **cache the app shell** (it says "no offline caching" today), with a versioned cache and network-first pages, so updates still arrive promptly. API data is never cached by the worker: the list's offline copy lives in IndexedDB, managed by the page.

## Findings

### 1. Opening offline (app shell caching on Static Web Apps)

- **How:** on `install`, the service worker caches the shell into a **versioned cache**: `index.html`, the built JS and CSS, the manifest, icons, and the Figtree font files. On `activate`, it deletes old caches and claims open pages.
- **Fetch strategy (PoC `sw.js`):**
  - Pages and shell files are **network first with a 3-second timeout**, falling back to the cache. A basement often has "one bar that never answers"; the timeout stops the app hanging on it. Good responses refresh the cache.
  - `/api/*` and other origins (the API Function app, Microsoft sign-in, SignalR) are **never handled by the worker**, so stock data is never served stale from a cache. The list's offline data is the page's IndexedDB copy, not cached responses.
  - **Vite's hashed files** (`/assets/index-abc123.js`) never change content, so caching them is safe. `index.html` points at the current hashes. The worker's file list should come from the Vite build manifest, so a deploy caches exactly the files it shipped.
- **Updates:** `sw.js` served with `Cache-Control: no-cache` (Static Web Apps route header [6]), so the browser checks for a new worker on every open.
  - A new deploy changes the cache version, installs alongside the old one, then takes over (`skipWaiting` + `clients.claim`).
  - The next navigation gets the new shell. Worst case, the user sees the previous version for **one open**.
  - The feature could show "Updated, tap to reload" instead of switching silently; not needed for a household.
- **SWA config:** the navigation fallback must exclude `sw.js`, the manifest, icons and `/assets/*` (it already excludes `/assets/*`). The worker's scope is `/`.
- **Fonts:** Figtree comes from Google Fonts (another origin). Either self-host the font files under `/assets` (simplest, and cached with the shell), or let the worker cache those requests. **Recommendation: self-host.** Offline, the system font would otherwise show.

### 2. Signed in, offline (MSAL)

- **Measured in the PoC:** with a made-up client ID, `initialize()` and `handleRedirectPromise()` complete in milliseconds **with `login.microsoftonline.com` unreachable**, and the cached-account calls work. So `main.tsx` as it is today starts offline, and `AuthenticatedTemplate` renders for anyone with a cached account (MSAL's cache is in `localStorage`).
- **Tokens:** `acquireTokenSilent` needs the network when the access token has expired (about 1 hour). Offline, it fails with a network/browser error, **not** `InteractionRequiredAuthError`. MSAL reports some offline failures with a misleading code (`post_request_failed`) [7], so check the error code, not just its type.
  - `ApiClient` today only sends people to sign-in on `InteractionRequiredAuthError` or a 401, so going offline **doesn't** bounce anyone to Microsoft. That's the right behaviour.
- **What the feature needs:**
  - the list screen reads **IndexedDB first**, and treats any API or token failure while `!navigator.onLine` (or a network error) as "offline", not an error;
  - the outbox sender gets a token only when it's about to send, and on a token failure just leaves the changes queued;
  - if the refresh token has expired (24 h for SPAs), `acquireTokenSilent` gives `InteractionRequiredAuthError` once **online**. The app shows "Sign in to sync your changes" with the outbox kept (it survives the redirect, being in IndexedDB), and syncs after sign-in.
- **Edge case:** **one user per phone.** If a different person signs in on the same installed app with unsynced changes, send them as the person who made them (store the user's `oid` on each change) or drop them with a warning. Recommended: tag each change with the signed-in user's `oid` and replay only that user's changes.

### 3. Local storage on iPhone

| Question | Answer | Source |
|---|---|---|
| Is IndexedDB available in an installed web app? | Yes | [3] |
| How much space? | The same origin quota as Safari: up to ~60% of disk on iOS 17+ (a shopping list is a few KB) | [3][8] |
| The 7-day "no interaction" deletion? | It's Safari's rule for sites in the browser (with tracking prevention on). **Home Screen apps keep their own count of days of use, which resets when the app is used.** A 2022 bug report says data still went after 7 days on iOS 15.3 despite use, unresolved | [1][2][8] |
| `navigator.storage.persist()`? | Supported in Safari 17. WebKit grants it by heuristics such as "opened as a Home Screen Web App". An open WebKit bug says it always returns `false` in Safari 17. Desktop Chrome (headless, not installed) returned `false` in the PoC | [3][4] |
| If it's cleared with unsynced changes? | Those changes are lost; the list itself is safe on the server. The window is small, because the app syncs whenever it's opened with signal | – |

**What the design does about it:** call `persist()` once after installing (harmless if refused). Sync on every open. Show the outbox count on screen, so "3 changes waiting" is visible rather than silent. Keep **nothing only on the phone** for longer than it takes to find signal. IndexedDB is a cache of the server's list plus a short outbox, never the only copy.

### 4. Outbox and sync

- **Outbox format** (IndexedDB store, auto-increment `seq` keeps the order):

  ```json
  { "seq": 7, "changeId": "uuid", "op": "add | tick | untick | remove", "itemId": "uuid", "name": "Bananas", "at": "2026-10-19T07:05:12Z", "userId": "<oid>" }
  ```

  The change and its local effect are written **in one IndexedDB transaction**, so the screen and the outbox never disagree. New items get their `itemId` on the phone (a UUID), so later ticks and removes can refer to an item the server hasn't seen yet.
- **Sending:** the whole outbox (or batches of up to 200) in one `POST`, in `seq` order. On success, delete the sent rows and replace the local list with the server's. Rows added while the request was in flight stay queued.
- **Triggers:** app open, the `online` event, `visibilitychange` to visible, after each change if online, and every 30 s while visible. **Background Sync isn't available on iOS** [5], so changes can't go up while the app is closed. Feature-detect `SyncManager` and use it on Android/Chrome as a bonus, if wanted.
- **Server:** applies changes **idempotently by `changeId`**. The PoC showed the same change sent twice gives `applied` then `duplicate`. Applied change IDs are kept (in Cosmos, a few weeks with TTL), so a retried request after a timeout is safe.

### 5. Conflict rules for a shared list (two people, both offline)

Rules (implemented in the PoC's `apply`):

1. **add**: if an **open** (not ticked, not removed) item has the same name (case and spaces ignored; with generic names from #82, the generic name), the add **merges** into it. Otherwise it's a new item.
2. **tick / untick** apply only if their `at` is **newer** than the item's last tick or untick, so a stale untick can't undo a later tick.
3. **remove wins**: a removed item stays removed, and later ticks or unticks for it are ignored.
4. **notes** (if added later): the newest `at` wins.

Worked examples, with A offline and B online. The Bananas, Bread, Milk and replay rows are from the PoC run; the Eggs row follows from rule 2 and wasn't run:

| What happened | Result |
|---|---|
| A (offline) adds "Bananas"; B (online) adds "bananas" | One item: B's reached the server first, and A's merged into it (`merged`) |
| B removes "Bread" while A (offline) still sees it | Bread is gone for both after A syncs |
| A ticks "Milk" offline, then adds "milk" again | "Milk ✓" (bought) and a new open "milk" (needed again), which is intended: a ticked item isn't "open" |
| A ticks "Eggs" at 10:00 offline; B unticks "Eggs" at 10:05 online; A syncs at 10:20 | Unticked: B's change is newer (by `at`, the time the change was made, not when it arrived) |
| The same change replayed after a timeout | Applied once, then `duplicate` |

Clocks: `at` comes from the phone's clock. Phones set their clocks automatically, so this is fine for a household. The server should clamp any `at` in the future to "now".

### 6. Live updates after reconnecting

- The app already reconnects SignalR and calls a catch-up endpoint (`GET /api/scan-feed?since=`) after every (re)connect. The list does the same: after the outbox is sent, `GET /api/shopping-list` (or `?since=<version>`) replaces the local copy. A list-changed SignalR event (`shoppingListChanged`) tells other open phones to refetch.
- **UI states** (calm, as the scan feed):
  - **"Offline"** (amber banner) when `navigator.onLine` is false;
  - **"Offline · 3 changes waiting to sync"**;
  - **"Syncing…"**, then **"Synced"** (fading after a few seconds);
  - **"Sign in to sync your changes"** when the token can't be renewed online.

  The PoC banner does the first three.

### 7. Testing

- **The agent, desktop Chrome:** `playwright-core` with throwaway Chrome profiles and `context.setOffline(true)`, as the PoC test did. Two profiles act as two phones. Covered: opening offline from the cache, outbox and banner, replay order, merges and removals, idempotency, and MSAL start-up with Microsoft blocked (`page.route` aborting its hosts).
- **Unit tests in the feature:** the server's `apply` rules (each row of the table above), the outbox (one transaction, order kept, rows added in flight kept), and the banner states.
- **Gary's iPhone:** opening the installed app in Airplane Mode, ticking and adding offline, closing it, then syncing on reopening. Plus storage surviving a few days unopened, which only time can test.

## Proof of concept

`docs/spikes/83-offline-poc/` (see its README). Desktop results (2026-10-19, Chrome on macOS 14, throwaway profiles, `context.setOffline`):

| Check | Result |
|---|---|
| Online, A adds Milk, Bread, Eggs | Server: Bread, Eggs, Milk |
| A closes the app, goes offline, reopens it | **Opens from the cached shell**, showing Bread, Eggs, Milk from IndexedDB |
| A (offline) ticks Milk, adds Bananas, adds "milk " | Banner: **"Offline · 3 changes waiting to sync"** |
| B (online) adds "bananas", removes Bread | Server: Eggs, Milk, bananas |
| A back online | Banner **"Synced"**. Server: Eggs, Milk ✓, bananas, milk. A's Bananas **merged** into B's; Bread stayed **removed**; each change applied once |
| The same change sent twice | `applied`, then **`duplicate`** |
| MSAL start-up, Microsoft unreachable | `initialize` 2 ms, `handleRedirectPromise` 0 ms, no error. Start-up needs no network |
| `navigator.storage.persist()` (desktop, not installed) | `false` |
| **iPhone (Home Screen app, Airplane Mode)** | **Deferred to the real app** (Gary's decision): part of verifying [Feature] Shopping list works offline |

## Recommended design (for [Feature] Shopping list works offline)

- **Service worker** (from [Task] Make the app installable): a versioned shell cache, the file list from the Vite build manifest, network-first pages with a 3 s timeout, cache-first hashed assets, nothing for `/api` or other origins, and `no-cache` on `sw.js`. Self-host Figtree.
- **Local store:** IndexedDB `shoppingList` (items as last synced plus local changes) and `outbox` (ordered changes). A small module (`listStore.ts`) owns both; screens never call the API for the list directly.
- **Sync module:** triggers as above. Gets a token only to send; treats network and token failures as "still offline". Sends batches in order, then refetches.
- **API (Entra bearer token):**
  - `POST /api/shopping-list/changes` with `{ changes: [...] }`: applies them in order, idempotent by `changeId` (applied IDs kept with a TTL), using the conflict rules above. Returns the list and a version.
  - `GET /api/shopping-list?since=<version>`.
  - A `shoppingListChanged` SignalR event.
- **UI states:** Offline, n changes waiting, Syncing…, Synced, Sign in to sync your changes. They should be drawn on the Cupboard Tracker UI canvas with the list screen, before the feature is built.
- **Storage:** `persist()` once installed, and the outbox count always visible. Accept that an evicted phone loses only unsent changes.

## Changes needed to [Task] Make the app installable (PWA)

- Its "minimal service worker, no offline caching" becomes **"cache the app shell"** (versioned cache, network-first pages with a timeout, hashed assets cache-first, never `/api`). Recommended inside that task, so the installed app opens offline from day one, and the push handlers from [Task] Web Push plumbing slot into the same worker.
- **Self-host the Figtree font files**, so they're in the cache.
- Add `/assets/*` (already there), `sw.js`, the manifest and icons to the SWA navigation-fallback exclusions, and `Cache-Control: no-cache` on `sw.js`.

## Risks

- **iPhone storage eviction:** unsent changes could be lost if iOS clears the app's data (rare for an app in use). Mitigated by syncing at every chance and showing the outbox count.
- **The 24-hour refresh token:** offline for more than a day plus the app not opened online means signing in again before syncing. The changes are kept meanwhile.
- **Stale shell:** a user may see the previous version for one open after a deploy. Acceptable; the API stays backwards compatible within a deploy.
- **Clock skew:** `at` from the phone decides tick-versus-untick. Phones set the time automatically, and the server clamps future times.
- **iOS changes:** Apple controls Home Screen web apps (e.g. the EU episode in iOS 17.4, see spike #75). The list still works online in Safari if anything regresses.

## Suggested follow-up cards (not created)

1. **[Task] Offline app shell in the service worker**: or fold it into [Task] Make the app installable (PWA), which this spike recommends. Shell caching, versioning, self-hosted Figtree, SWA config, plus a Playwright offline test.
2. **[Feature] Shopping list works offline**: IndexedDB store, outbox, sync triggers, the idempotent changes endpoint with the conflict rules, the UI states (drawn on the canvas first), and the iPhone Airplane Mode verification.
3. *(Optional)* **[Task] Background Sync on Android/Chrome**: a bonus where supported; not needed on iPhone.

## Sources

1. WebKit Bugzilla 237350 (Home Screen web apps and the 7-day cap; WebKit's statement that Home Screen apps have their own counter of days of use; a user report of data loss on iOS 15.3): https://bugs.webkit.org/show_bug.cgi?id=237350
2. Michael Tsai, *Safari 13.1: Third-Party Cookie Blocking and 7-Day Script-Writeable Storage* (quotes John Wilander: Home Screen web apps' counter "resets with actual use"): https://mjtsai.com/blog/2020/03/26/safari-13-1-third-party-cookie-blocking-and-7-day-script-writeable-storage/
3. WebKit, *Updates to Storage Policy* (Safari 17 / iOS 17 quotas; standalone web apps get the same quota as the browser; `persist()` granted by heuristics such as being a Home Screen web app; LRU eviction): https://webkit.org/blog/14403/updates-to-storage-policy/
4. WebKit Bugzilla 271401, `navigator.storage.persist()` always returns false (Safari 17, open): https://bugs.webkit.org/show_bug.cgi?id=271401
5. Can I use, Background Sync API (Safari and Safari on iOS: not supported): https://caniuse.com/background-sync
6. Microsoft Learn, *Configure Azure Static Web Apps* (route headers, navigation fallback exclusions, MIME types): https://learn.microsoft.com/azure/static-web-apps/configuration
7. MSAL.js issue 4114 (offline `acquireTokenSilent` reports `post_request_failed`) and *Handle errors and exceptions in MSAL.js*: https://github.com/AzureAD/microsoft-authentication-library-for-js/issues/4114 , https://learn.microsoft.com/entra/identity-platform/msal-error-handling-js
8. MDN, *Storage quotas and eviction criteria* (Safari: Home Screen apps use the browser's quota; the 7-day proactive eviction applies in Safari with tracking prevention; server-set cookies are exempt): https://developer.mozilla.org/docs/Web/API/Storage_API/Storage_quotas_and_eviction_criteria
