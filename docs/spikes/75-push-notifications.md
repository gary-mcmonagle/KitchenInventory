# Spike #75: Push notifications from the web app

Card: https://trello.com/c/bRKVQcyr

## Question

Can Cupboard Tracker send **push notifications to household phones** from the web app (no native app)? What's the simplest reliable way on our stack, and which events are worth a push? Gary (2026-10-18): **"We will need Apple"**, so iPhone support is a must.

## Summary

> **Decision (Gary, 2026-10-19): option A.** Send directly from the API Function app with our own Web Push sender, not Notification Hubs. Gary asked to favour Azure; this route is all Azure (Function app, Key Vault, Cosmos). Notification Hubs was ruled out because its web push doesn't reach iOS, and because it only accepts shared access keys (no managed identity), which breaks the no-keys rule.

- **Yes, including iPhone.** It uses the standard **Web Push** API with a **service worker**.
  - **iPhone and iPad:** the app has to be added to the **Home Screen** first, and needs iOS/iPadOS **16.4 or later**. Notifications are allowed from inside that installed app.
  - **Android, Mac and PC:** works straight from the browser.
- **Recommendation: send directly from the API Function app with our own small Web Push sender.** About 150 lines using .NET's built-in crypto: `aes128gcm` encryption (RFC 8291) and VAPID signing (RFC 8292).
  - **Not Azure Notification Hubs:** its browser push **doesn't support Safari or iOS**.
  - **Not the popular `WebPush` NuGet package:** it only sends the old **`aesgcm`** draft encoding, which Apple's push service doesn't accept.
- **Proven on desktop.** A proof of concept (`docs/spikes/75-push-poc/`) subscribed real Google Chrome, and a push sent from .NET was shown as a notification with the right title, body, tag and tap target.
  - The sender matches RFC 8291's test vector **byte for byte**.
  - After the browser unsubscribed, the push service answered **410 Gone**, which is how dead subscriptions get pruned.
  - **Proven on Gary's iPhone too** (Home Screen app, 2026-10-19), once two Apple-specific rules were followed (below).
- **First slice:** a **morning "Use first today" push** at about 07:30 UK time, only when something is due today or tomorrow, at most one a day. Plus the **hub offline** alert as a push alongside the existing email.
- **Cost: nothing extra.** The browser vendors' push services (Apple, Google, Mozilla) are free. There's no new Azure resource, only a Cosmos container (serverless, pennies) and one Key Vault secret.
- **Build order (suggested cards below):**
  1. Make the app installable (manifest, icons, service worker) and **check sign-in inside the iPhone Home Screen app**. This is the biggest unknown left.
  2. The push plumbing (sender, subscriptions, keys).
  3. The morning nudge with the "Turn on notifications" UI. **Draw it on the Cupboard Tracker UI canvas first.**
  4. The hub-offline push.

## Findings

### 1. Platform support

| Device | Push? | What it needs | Sent through |
|---|---|---|---|
| **iPhone / iPad** (Safari engine, any browser) | Yes, iOS/iPadOS **16.4+** | **Added to the Home Screen** (manifest with `display: standalone`) and opened from there; permission asked from a **tap** inside that app [1] | Apple Push Notification service (`web.push.apple.com`) [1] |
| iPhone in a normal Safari tab | **No** | — | — |
| **Android** (Chrome, Edge, Firefox, Samsung Internet) | Yes | Permission from a tap; installing isn't required | Google FCM (Chrome), Mozilla autopush (Firefox) |
| **Mac** (Chrome, Edge, Firefox; Safari 16+ on macOS 13+) | Yes | Permission from a tap | As above; Safari via Apple |
| **Windows PC** | Yes | Permission from a tap | As above |

- **iOS 18.4 and later** also support **Declarative Web Push** [2]. A push in a standard JSON format (`"web_push": 8030`, with `notification.title`, `body` and `navigate`) is shown **by the system itself, even if the service worker fails**. It's backwards compatible: other browsers hand the same JSON to the service worker, which shows it. **Recommendation: always send this format.** It's more reliable on iPhone at no cost elsewhere.
- **Every push must show a notification.** Subscriptions require `userVisibleOnly: true` [3]. Safari **revokes** a subscription after a few pushes that don't show one (reported as 3 in Apple's 2022 WWDC session; developer reports agree) [4]. So there are no silent pushes, and the service worker shows *something* for every push, even when the app is open.
- **iPhone reliability:** there are some developer reports of iOS push stopping until the device re-subscribes [4]. The design copes: the app re-registers its subscription every time it's opened (below), and the 9pm email digest stays as the dependable channel.
- **EU:** Apple briefly removed Home Screen web apps in the EU in an iOS 17.4 beta, then reversed it before release [5]. It doesn't affect a UK household, but it's a reminder that Apple controls this path.
- **Unsupported devices** (an older iPhone, or a Safari tab that isn't installed) just don't offer the switch. The **email digest and offline email carry on** for everyone, so nothing is lost silently. The UI says why (see the UI section).

### 2. Making the app installable (PWA basics)

What the app has today (`FE/`): no manifest, no service worker, a `theme-color` meta, an SVG favicon only, and `staticwebapp.config.json` with a navigation fallback.

What it needs:

- **`FE/public/manifest.webmanifest`** with:
  - `id: "/"`, `name: "Cupboard"`, `short_name: "Cupboard"`, `start_url: "/"`, `scope: "/"`, `display: "standalone"`;
  - `background_color` and `theme_color` `#F4F7F3` (the mockups' ground);
  - PNG icons at 192 and 512 px (`any` and `maskable`), rendered from the favicon (the PoC did this with `qlmanage` + `sips`);
  - in `index.html`: `<link rel="manifest">` and `<link rel="apple-touch-icon" href="/icon-192.png">`.
- **`FE/public/sw.js`**: plain JavaScript in `public/`, so Vite copies it as is. **No plugin or bundling is needed**, because the worker does no offline caching.
  - It handles `push`, `notificationclick` and `pushsubscriptionchange` (the PoC's `sw.js` is the template).
  - **No `fetch` handler**, so it can't interfere with MSAL's sign-in redirects or serve stale app files.
  - It's registered once from `main.tsx` with scope `/`.
- **Static Web Apps (Free)** [6]:
  - Exclude `/sw.js`, `/manifest.webmanifest` and `/icon-*.png` from `navigationFallback` (or they'd be answered with `index.html`).
  - Add a route for `/sw.js` with `Cache-Control: no-cache`, so a new worker is picked up on the next visit.
  - Add `"mimeTypes": { ".webmanifest": "application/manifest+json" }`.
  - SWA serves HTTPS on both the default hostname and `kitchen.garymcmonagle.com`.
  - A Home Screen app is tied to the hostname it was added from. **Use `kitchen.garymcmonagle.com`**: a subscription made on one hostname doesn't exist on the other.
- **Sign-in inside the installed iPhone app** is the biggest unknown left.
  - The Home Screen app has its **own storage** (separate from Safari), so the user signs in once more inside it.
  - MSAL's **redirect** flow is the one that works in standalone mode (pop-ups don't), and we already use it.
  - What still needs checking on Gary's phone is that the Microsoft sign-in page (another origin) comes back into the app properly. That's step 1 of the first follow-up card, before any push work.

### 3. Subscribing

- **Permission is only asked from a tap** on "Turn on notifications", never on load. iOS refuses otherwise [1], and Chrome downranks sites that prompt on load.
- **Flow:**
  1. `Notification.requestPermission()`.
  2. `registration.pushManager.subscribe({ userVisibleOnly: true, applicationServerKey: <VAPID public key> })` [3].
  3. `POST /api/push/subscriptions` with the subscription (`endpoint`, `keys.p256dh`, `keys.auth`) and a device label.
- **Refresh:** every time the app opens, it calls `pushManager.getSubscription()`. If one exists, it posts it again (an idempotent upsert that bumps `lastSeenAt`); if it's gone but permission is still granted, it subscribes again. The service worker also handles `pushsubscriptionchange`, which **isn't Baseline** (limited support), so the on-open check is the real safety net [7].
- **Turn off:** `subscription.unsubscribe()` plus `DELETE /api/push/subscriptions/{id}`. Turning off in iOS Settings is detected on the next open (`Notification.permission !== "granted"`), and the server copy expires anyway (below).
- **Keys:** a VAPID key pair (P-256). Rotating it invalidates every subscription (they're bound to the public key), so **don't rotate casually**. If it ever leaks, rotate, and every device turns notifications back on.

### 4. Sending from Azure: direct vs Notification Hubs

| | **Direct from the API app (recommended)** | Azure Notification Hubs |
|---|---|---|
| iPhone / Safari | **Yes** (`aes128gcm`, VAPID) | **No.** Its browser push covers Chrome, Edge and Firefox; Safari only through legacy certificate-based APNs website push, and iOS is "Not supported" [8] |
| New resource | None | A namespace and hub; Free tier: 1M pushes, 500 devices, no SLA [9] |
| Bicep | Only the Key Vault secret reference and a container | `Microsoft.NotificationHubs` plus VAPID credentials on the hub |
| Code | ~150 lines: `IPushSender` in Application, `WebPushSender` in Infrastructure | An SDK, still needs our own subscription storage |
| Cost | £0 | Free tier £0, but no SLA |
| Auth | Managed identity everywhere; the only secret is our own VAPID private key in Key Vault | **Shared access keys only** (SAS); no managed identity [14] |

**Library check:**

- **`WebPush` (web-push-libs, 1.0.13, MPL-2.0, 8M downloads) [10]:** the PoC's first sender used it. It sent `Content-Encoding: aesgcm`, and the DLL contains no `aes128gcm` at all. Chrome accepted it; Apple doesn't [11].
- **`Rask.WebPush` (0.23, MIT, `aes128gcm`) [12]:** pre-1.0, part of a wider framework, with little use.

**So we write our own:** `aes128gcm` and VAPID with in-box `ECDiffieHellman`, `HKDF`, `AesGcm` and `ECDsa`, unit-tested against **RFC 8291's test vector** (the PoC matches it exactly). It fits the clean architecture (no third-party types) and has no package to keep up to date. The PoC's `send/WebPushSender.cs` is the template.

**How to send:**

- Headers: `TTL` (12 h for the morning nudge, 1 h for an offline alert), `Urgency` (`normal`; `high` for offline), and `Topic` (`use-first`, `hub-offline`: an undelivered older push of the same topic is replaced).
- Handling the push service's answers:
  - **201**: delivered;
  - **404/410**: delete the subscription;
  - **429/5xx**: retry later;
  - **400/403/413**: a bug or a configuration problem, logged at **Error** (it fires the error alert).
- Logs record the outcome, status and push-service host only: never the endpoint (it's a capability URL) or the payload.

### 5. Storing subscriptions (Cosmos)

**New container `pushSubscriptions`, partition key `/userId`** (one partition per person: every read is per user, and the timer fans out across a handful). Two document types:

```json
{
  "id": "<sha256(endpoint), base64url>",
  "type": "subscription",
  "userId": "<Entra oid>",
  "endpoint": "https://web.push.apple.com/…",
  "keys": { "p256dh": "…", "auth": "…" },
  "deviceLabel": "iPhone",
  "pushService": "web.push.apple.com",
  "createdAt": "…",
  "lastSeenAt": "…",
  "lastSuccessAt": "…",
  "lastFailureAt": null,
  "failureCount": 0
}
```

```json
{
  "id": "preferences",
  "type": "preferences",
  "userId": "<Entra oid>",
  "types": { "useFirstMorning": true, "hubOffline": true },
  "updatedAt": "…"
}
```

- The **id is a hash of the endpoint**, so re-posting the same subscription is an idempotent upsert.
- **Removed** on 404/410, on "turn off", and when **`lastSeenAt` is over 30 days old**. The app re-registers on every open, so a phone nobody has opened in a month stops getting pushes. This also matters because closed browser profiles kept answering 201 in the PoC: the push service can't always tell a device is gone.
- **Preferences are per person** (not per device): which types they want. Defaults are on for both first-slice types once they turn notifications on.
- **Sent pushes:** one `sentPushes` record per type per day (`id = "useFirstMorning:2026-10-19"`), as `sentDigests` does for the digest, so a timer that fires twice (or two instances) sends once.

### 6. Which events

| Event | For | Against | Recommendation |
|---|---|---|---|
| **Use first today** (things due today or tomorrow) | The morning counterpart to the 9pm digest: you see it **before** cooking or shopping. Quiet days send nothing | Some days it's the same item as yesterday | **First slice.** About 07:30 UK time, at most once a day, only if anything is due. "Use first today: milk, gnocchi and 1 more". Tap opens the stock list with Use first showing |
| **Hub offline** | It's the one thing that silently breaks the system; a push is quicker to notice than email | Rare; email already covers it | **First slice (small).** Sent alongside the existing offline email, once per outage, `Urgency: high` |
| **Unknown item waiting for a name** | Reminds whoever scanned to name it | The scanner is in the kitchen and the scan feed already shows it live; pushes for every scan would nag | **Not now.** Maybe a once-a-day "3 items still need a name", folded into the morning push |
| **Recipe / shopping ideas** | Could be nice | Not built yet; risk of being "marketing" | **Later**, opt-in only |

**How to avoid nagging:**

- At most **one push per type per day** (sent-push records).
- **Quiet hours 21:30–07:30 UK**: the morning nudge is scheduled after them. An offline alert during quiet hours waits until 07:30, and is dropped if the hub is back by then (the email still goes at once).
- **One `tag` per type**, so a new notification **replaces** the old one on screen instead of stacking. The same value goes in the `Topic` header, so an undelivered push is replaced at the push service too.
- **Grouping**: one push lists up to 2 names and "and n more", never one push per item.

**Timer:** like the digest (`DailyDigestTimer` fires at 20:00 and 21:00 UTC and checks the UK clock), a morning timer fires at 06:30 and 07:30 UTC and sends at 07:30 UK time. That covers BST and GMT without `WEBSITE_TIME_ZONE`, which Flex Consumption on Linux doesn't support.

### 7. Payload and tap

```json
{
  "web_push": 8030,
  "notification": {
    "title": "Use first today",
    "body": "Milk, gnocchi and 1 more need using.",
    "navigate": "https://kitchen.garymcmonagle.com/?filter=use-first",
    "tag": "useFirst",
    "lang": "en-GB"
  }
}
```

- **Icon:** the app icon (from the manifest on iPhone; `icon`/`badge` set by the service worker elsewhere).
- **Tapping:** opens `navigate`. If the app is already open, the service worker **focuses that window and navigates it** instead of opening another (`clients.matchAll` → `focus()` → `navigate()`; proven in the PoC's `sw.js`).
- **Destinations:**
  - Use first: `/?filter=use-first`, the stock list with Use first showing (the stock list would need to read that query; a small change);
  - hub offline: `/scanners`;
  - (later) waiting for a name: `/scanners`.
- **Badge:** `app_badge` (the Badging API, iOS 16.4+) could show the use-first count on the icon. **Not in the first slice**, because it must then be kept in step; it's an easy follow-up.
- **Payload size:** about 170 bytes, well under the ~4 KB limit.

### 8. Privacy and security

- **The payload is minimal:** a title, a short body with at most 2 product names, and a path. No people's names, barcodes, emails or IDs. It's **encrypted end to end** (the push service can't read it). Product names are household data, not personal data. If Gary would rather not show them on a lock screen, use counts only ("3 things to use first today"); it's a one-line choice in the feature.
- **Subscribing needs a signed-in user:** `POST /api/push/subscriptions` and `DELETE` sit behind the existing `BearerAuthMiddleware`, and the subscription is stored under the caller's `oid`. A user can only list or delete their own.
- **The VAPID private key** lives in Key Vault (`vapid-private-key`), reaching the API app as `VAPID_PRIVATE_KEY` through a Key Vault reference, like `jev-api-key`. It's never in the repo, logs, Trello or chat. The **public key** isn't secret: it's an app setting (`VAPID_PUBLIC_KEY`, in `main.bicepparam`), served to the UI by `GET /api/push/config`, so no UI rebuild is needed when it changes.
  - **Setting it by hand:** CLAUDE.md says Gary sets secrets in the portal (2026-10-18, "no script"). The card mentions `deploy/secrets.sh`, which doesn't exist, so **this follows CLAUDE.md**. A helper, `deploy/vapid-keys.sh`, could generate the pair, print only the public key, and copy the private key to the clipboard for pasting into the portal.
- **Removing a household member:** deleting their guest account stops them signing in, but a subscription on their phone would keep receiving pushes until it expires. So:
  - subscriptions expire **30 days after `lastSeenAt`** (the app must be opened, signed in, to refresh it);
  - the feature should add a "remove this person's devices" step (delete their partition) to the "Removing someone" steps in CLAUDE.md, or a 1-line `cosmos` delete Gary runs.
- **Content Security Policy:** none is set today. If one is added later, `connect-src` isn't affected (the browser talks to the push service itself, not the page).

### 9. Testing

**Without a phone (the agent):**

- **Unit tests:** the sender against RFC 8291's vector, VAPID JWT claims (`aud` = the push service origin, `exp` ≤ 24 h, `sub`), and response handling (201, 404/410, 429, 5xx), all through a fake `HttpMessageHandler`.
- **End to end on desktop:** `playwright-core` drives the **Mac's installed Google Chrome with a throwaway profile** (never Gary's), with notifications granted. It subscribes, sends a real push, and reads it back with `registration.getNotifications()`. This is how the PoC was proven.
  - Claude Code's **built-in browser pane can't** do it: notifications are denied, and registering a service worker fails.
- **API:** `deploy/verify/api.sh` checks the subscribe endpoints and their 401s; `cosmos.sh pushSubscriptions` shows the stored documents.

**Needs Gary's phone:**

- iPhone install and sign-in in the Home Screen app;
- allowing notifications;
- delivery while locked and while the app is open;
- tapping into the right screen;
- turning off in iOS Settings.

## Proof of concept

In `docs/spikes/75-push-poc/` (see its README). Results (2026-10-18/19, desktop Google Chrome on macOS 14, throwaway profiles):

| Check | Result |
|---|---|
| Built-in browser pane | Notification permission `denied`; service worker registration fails ("unknown error"). **Not usable** for push testing |
| Subscribe in Chrome (permission granted) | `Subscribed with fcm.googleapis.com`, saved |
| Send with `WebPush` 1.0.13 | Delivered and shown, but sent as **`Content-Encoding: aesgcm`** (not acceptable to Apple) |
| Own sender `--self-test` | **RFC 8291 test vector: MATCH** |
| Send with own sender (`aes128gcm`, VAPID, TTL, Urgency, Topic) | `HTTP 201 → Delivered`; notification shown: title "Use first today", body, tag `poc-use-first`, `navigate` `/?filter=use-first` |
| Send after the browser unsubscribed | **`HTTP 410 → Gone`** (prune) |
| Send to profiles that were closed, not unsubscribed | Still `201`: the push service can't tell. Hence the 30-day `lastSeenAt` expiry |
| iPhone in a Safari tab (not installed) | `PushManager: false`, `Notification` undefined: **no push in a tab**, as Apple documents |
| **iPhone, installed to the Home Screen** (2026-10-19, via a temporary Cloudflare quick tunnel) | Subscribed with `web.push.apple.com`. First send: **403 `BadJwtToken`** with VAPID subject `mailto:…@example.invalid`; with `https://kitchen.garymcmonagle.com`, the next: **400 `BadWebPushTopic`** for `Topic: poc-use-first`; with `pocUseFirst`: **201 Delivered**, and the notification showed on Gary's iPhone with the app icon, "Use first today", "from Push PoC" and the body |

No VAPID private key is in the repo: the PoC reads keys from an env file outside it, and `.gitignore` covers `*.env` and `subscriptions.json`.

### Apple's extra rules (found on the iPhone)

Google accepted all of these; Apple didn't. The feature's sender must follow Apple's stricter rules, and its tests should check them:

- **VAPID subject:** Apple rejects a made-up contact (`403 {"reason":"BadJwtToken"}` for `mailto:…@example.invalid`). Use a real one: `https://kitchen.garymcmonagle.com` works and keeps Gary's email out of config.
- **Topic:** Apple rejects hyphens (`400 {"reason":"BadWebPushTopic"}` for `poc-use-first`), although RFC 8030 allows them. Use **letters and digits only** (`useFirst`, `hubOffline`), at most 32.
  - **Correction (#106, 2026-10-21):** it wasn't the hyphen. Apple reads the topic as URL-safe Base64, and `poc-use-first` is 13 characters: no Base64 string has a length with remainder 1 when divided by 4. The topic `stock` (5) was refused the same way. Letters and digits, at most 32, and not 1, 5, 9, 13… characters long.
- **Error bodies:** push services explain refusals in the response body (Apple: `{"reason":"…"}`). Log that reason (it holds no secrets) with the status, or problems are guesswork.

The iPhone needed iOS 16.4+ and the page **added to the Home Screen and opened from there** (Share → Add to Home Screen, "Open as Web App" on). In a Safari tab, `Notification` doesn't exist.

## Recommended design (for the feature)

- **UI, "Turn on notifications"** (wireframe level; **draw it on the Cupboard Tracker UI canvas before building**):
  - **Where:** a **"Notifications" card on the Scanners screen**, under the devices (the "is everything working?" screen), titled **"This phone"**.
  - **Off, supported:** "Get a nudge in the morning when food needs using up." Then a full-width green **Turn on notifications** button (52 px, as "Get a recipe").
  - **iPhone, not installed:** "To get notifications on iPhone, add Cupboard to your Home Screen: tap **Share**, then **Add to Home Screen**, and open it from there." No button.
  - **Blocked** (permission denied): "Notifications are blocked for Cupboard. Turn them on in Settings → Notifications → Cupboard."
  - **On:** two switch rows (44 px), **Morning use-first nudge** and **Hub offline**, and a text button **Turn off on this phone**. A muted line: "Also by email at 9pm."
  - The same states could appear later as a one-time dismissable hint on the stock list. Not in the first slice.
- **API (behind the bearer token), in `docs/api.md`:**
  - `GET /api/push/config` → `{ vapidPublicKey }`;
  - `PUT /api/push/subscriptions` (upsert, returns `{ id }`);
  - `DELETE /api/push/subscriptions/{id}`;
  - `GET`/`PUT /api/push/preferences`.
- **Application:**
  - `IPushSender` (send one message to one subscription → Delivered / Gone / Retry / Refused);
  - `IPushSubscriptionRepository`;
  - use cases `RegisterPushSubscription`, `RemovePushSubscription`, `SendMorningUseFirst` (timer) and `NotifyHubOffline` (hooked into `CheckHubsOffline` beside the email, behind `INotifier`-style best-effort handling).
- **Infrastructure:** `WebPushSender` (from the PoC), `CosmosPushSubscriptionRepository`.
- **Bicep:**
  - the `pushSubscriptions` container (`/userId`), plus `sentPushes` (`/id`) or a reuse of `sentDigests` with a type prefix;
  - API app settings `VAPID_PUBLIC_KEY` (from `main.bicepparam`), `VAPID_SUBJECT` (`https://kitchen.garymcmonagle.com`: Apple needs a real contact, and this keeps Gary's email out of config) and `VAPID_PRIVATE_KEY` (a Key Vault reference to `vapid-private-key`);
  - **no new Azure resources.**
- **Docs:** `docs/data-model.md` (the container), `docs/domain-language.md` (push notification, push subscription, quiet hours), and CLAUDE.md (the secret, VAPID rotation, removing a member's devices).

## Costs

| Item | Cost |
|---|---|
| Apple, Google and Mozilla push services | Free |
| Notification Hubs | Not used |
| Cosmos (a few documents, a few reads and writes a day) | Pennies a month (serverless) |
| Key Vault (one secret, read at app start-up) | Under a penny a month |
| Function executions (one timer twice a day) | Within the free grant |

## Suggested follow-up cards (not created)

1. **[Task] Make the app installable (PWA)**: manifest, PNG icons, `apple-touch-icon`, a minimal `sw.js` (no caching), SWA config (fallback exclusions, `no-cache` on `sw.js`, `.webmanifest` MIME type). **Verify with Gary:** add `kitchen.garymcmonagle.com` to the iPhone Home Screen, sign in inside it, and use the app as normal. *Enables the push feature.*
2. **[Task] Web Push plumbing**: `WebPushSender` (RFC 8291 vector test), `pushSubscriptions` container, subscribe, unsubscribe and config endpoints, the VAPID key in Key Vault (Gary sets it by hand) and the public key in Bicep, the 30-day expiry and 404/410 pruning. *Enables the features below.*
3. **[Feature] Morning "Use first today" push**: the timer (07:30 UK, at most once a day, quiet days silent), the "This phone" notifications card on the Scanners screen (**draw on the canvas first**), preferences, and tapping into the stock list with Use first showing.
4. **[Feature] Hub offline push**: alongside the existing email, once per outage, quiet hours respected.
5. *(Later)* App badge with the use-first count; a daily "items still need a name" line in the morning push; recipe ideas as an opt-in push.

## Sources

1. WebKit, *Web Push for Web Apps on iOS and iPadOS* (iOS 16.4; Home Screen; `display: standalone`; permission from direct user interaction; `*.push.apple.com`; Focus; Badging): https://webkit.org/blog/13878/web-push-for-web-apps-on-ios-and-ipados/
2. WebKit, *Meet Declarative Web Push* (iOS/iPadOS 18.4; `web_push: 8030`; `notification.title` and `navigate` required; backwards compatible; the server side is unchanged): https://webkit.org/blog/16535/meet-declarative-web-push/
3. MDN, `PushManager.subscribe()` (`userVisibleOnly`, `applicationServerKey` and VAPID): https://developer.mozilla.org/docs/Web/API/PushManager/subscribe
4. Safari revokes subscriptions when pushes show no notification (citing Apple's WWDC 2022 session), and reports of iOS push stopping: https://meta.discourse.org/t/ios-notifications-can-lose-permission-to-push-if-the-user-is-currently-active/290225 , https://github.com/firebase/firebase-js-sdk/issues/8010
5. Apple keeps Home Screen web apps in the EU (iOS 17.4): https://9to5mac.com/2024/03/01/apple-home-screen-web-apps-ios-17-eu/
6. Microsoft Learn, *Configure Azure Static Web Apps* (routes, headers, `navigationFallback.exclude`, `mimeTypes`): https://learn.microsoft.com/azure/static-web-apps/configuration
7. MDN, `pushsubscriptionchange` (limited availability; resubscribe with `oldSubscription.options`): https://developer.mozilla.org/docs/Web/API/ServiceWorkerGlobalScope/pushsubscriptionchange_event
8. Microsoft Learn, *Send browser (web push) notifications with Azure Notification Hubs* ("Apple Safari isn't included"; iOS "Not supported"): https://learn.microsoft.com/azure/notification-hubs/browser-push
9. Azure Notification Hubs pricing (Free: 1M pushes, 500 devices, no SLA): https://azure.microsoft.com/pricing/details/notification-hubs/
10. NuGet, `WebPush` 1.0.13 (web-push-libs, MPL-2.0): https://www.nuget.org/packages/WebPush , https://github.com/web-push-libs/web-push-csharp
11. Apple's push service requires `aes128gcm` and VAPID (third-party, confirmed in a library's release notes; no primary Apple statement found): https://newreleases.io/project/npm/@block65/webcrypto-web-push/release/2.0.0 ; RFC 8291 (encryption, test vector in section 5): https://www.rfc-editor.org/rfc/rfc8291 ; RFC 8292 (VAPID): https://www.rfc-editor.org/rfc/rfc8292
12. NuGet, `Rask.WebPush` 0.23.0: https://www.nuget.org/packages/Rask.WebPush/
13. web.dev, *Push notifications overview* (permission from a gesture, push services, encryption, TTL, urgency, topic): https://web.dev/articles/push-notifications-overview
14. Microsoft Learn, *Notification Hubs security model* (shared access signatures with keys on the hub): https://learn.microsoft.com/azure/notification-hubs/notification-hubs-push-notification-security
