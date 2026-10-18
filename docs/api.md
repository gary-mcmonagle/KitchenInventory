# API

The HTTP contracts of the Function apps. Terms follow [domain-language.md](domain-language.md); stored shapes follow [data-model.md](data-model.md).

The client-side models for these payloads live in the Pi package, `LocalScanner/src/cupboard_pi/payloads.py`, and the simulator (`tools/simulator/`) uses them too. **Keep the two sides in step:** a change here changes `payloads.py` and its tests, and the .NET validation, in the same commit.

---

## Ingest Function app

Auth: **function key** in the `x-functions-key` header (the Pi reads the header name and value from `INGEST_AUTH_HEADER_NAME` / `INGEST_AUTH_HEADER_VALUE`). A missing or wrong key gets **401** from the platform before any code runs.

### `POST /api/scans`: record a scan event

Sent by the hub for every barcode read (and by the simulator). Resends are expected (the Pi queues and retries), so this is **idempotent on the scan ID**.

```json
{
  "scanId": "0b1c3f1e-6d0e-4c0a-9e0a-3a4d7c1f9a21",
  "barcode": "5000354922831",
  "deviceId": "scanner-in",
  "role": "in",
  "scannedAt": "2026-10-16T18:42:07.123Z",
  "clientSha": "e1130b1",
  "source": "simulator"
}
```

| Field | Rule |
|---|---|
| `scanId` | **Required.** A UUID, minted by the client when the barcode is read (never derived from barcode + time), and kept unchanged across resends. Becomes the scan event's `id`. |
| `barcode` | **Required.** Digits only, length 8, 12 or 13 (EAN-8, UPC-A, EAN-13), valid GS1 check digit. Exactly as scanned: no padding or normalising. |
| `deviceId` | **Required.** Lowercase letters, digits and dashes (e.g. `scanner-in`). From the hub's `SCANNERS` config. |
| `role` | **Required.** `in` (check-in) or `out` (check-out). |
| `scannedAt` | **Required.** ISO 8601 with a timezone (`Z` or an offset); stored in UTC. The client's clock. |
| `clientSha` | **Required.** The client's deployed commit (at most 64 characters). |
| anything else | **Optional extra fields, stored as-is** (e.g. `source`). Names can't be `id`, `receivedAt`, a contract field, or start with `_`. |

All string fields must be JSON strings. The body must be a JSON object of at most 16 KB.

**Responses**

| Status | When | Body |
|---|---|---|
| **202 Accepted** | Stored, **or already stored** (a resend: same status and body, nothing new stored) | `{ "scanId": "…" }` |
| **400 Bad Request** | Not a JSON object, a field of the wrong type, or any rule above broken | `{ "errors": ["barcode check digit is wrong.", …] }` (all problems at once) |
| **401 Unauthorized** | Missing or wrong function key | (from the platform) |
| 5xx | Cosmos or the host failed | Retry: the client's queue resends, and the scan ID makes that safe. |

**What it does:** stores the scan event in `scanEvents` (partition `/deviceId`, id = scan ID, adding the server's `receivedAt`), then creates or updates the device in `devices` with its role, `lastSeenAt` (server time) and `lastScanAt` (never moved backwards by an older, queued scan). A device whose role changes is updated and a warning is logged.

Idempotency relies on the resend being identical: the same scan ID sent with a **different** `deviceId` would land in another partition and be stored again. The Pi resends its queued payload unchanged, so this doesn't happen in practice.

### `GET /api/ping`

`200 { "status": "ok", "commit": "<sha>" }` (both Function apps; ingest needs the function key).

### `POST /api/heartbeat`

The hub reporting in (every 2 minutes by default; #44, #68). Sent by the Pi and by the simulator, using `cupboard_pi.payloads.Heartbeat`. Not queued on the Pi: a lost heartbeat is simply followed by the next.

```json
{
  "hubId": "cupboard-pi",
  "sentAt": "2026-10-17T17:59:59.870Z",
  "clientSha": "24d5e5b",
  "uptimeSeconds": 3600,
  "usbInputDevices": ["USB Adapter USB Device"],
  "scanners": [{ "deviceId": "scanner-in", "present": true }, { "deviceId": "scanner-out", "present": false }]
}
```

| Field | Rule |
|---|---|
| `hubId` | **Required.** Lowercase letters, digits and dashes. The Pi's `HUB_ID`. |
| `sentAt` | **Required.** ISO 8601 with a timezone. The Pi's clock: stored for information only. |
| `clientSha` | **Required.** The client's deployed commit (at most 64 characters). |
| `uptimeSeconds` | **Required.** A whole number, 0 or more. |
| `usbInputDevices` | **Required** (may be empty). Names of the USB input devices the Pi sees, for information: at most 32, each at most 200 characters. |
| `scanners` | **Optional** (absent or `null` is fine). Each scanner's dongle as present or not: `[{ "deviceId", "present": true/false }]`, at most 16. Stored when sent. |
| anything else | Ignored. |

The body must be a JSON object of at most 16 KB.

**Responses:** **202** `{ "hubId": "…" }` when recorded; **400** `{ "errors": [ … ] }` (all problems at once) otherwise; **401** without the function key.

**What it does:** creates or replaces the hub document (`kind: "hub"` in `devices`; see [data-model.md](data-model.md)) with `lastSeenAt` = the **server's** receive time and the heartbeat's details. If an offline alert had been sent for the current outage, it clears `offlineAlertSentAt` and logs "Hub … back online after N minutes" (no email).

---

## API Function app (the BFF)

Called by the React app (`FE/`) directly from the browser; CORS allows the Static Web App and `http://localhost:5173`.

Auth: an **Entra ID bearer token** for the API registration (`aud` = the API's client ID, scope `access_as_user`, from the SPA or, for verification, the Azure CLI). Every endpoint needs one unless it's listed as public. **401** for a missing or invalid token, **403** for the wrong client or scope; both have a JSON body `{ "error": "…" }` and a `WWW-Authenticate: Bearer` header. See CLAUDE.md, Sign-in.

### `GET /api/ping` (public)

`200 { "status": "ok", "commit": "<sha>" }`. No token: the deploy smoke check and the UI footer use it.

### `GET /api/me`

`200 { "name": "Cupboard test", "objectId": "<oid>" }`: who the token belongs to.

### `GET /api/stock`

The stock list (#49, #52): every product with units in stock, grouped, put in sections and sorted so the UI does no aggregation. Only `inStock` units count.

```json
{
  "generatedAt": "2026-10-17T22:00:00.000Z",
  "totalUnits": 30,
  "useFirstCount": 2,
  "products": [
    {
      "productId": "bddb7749-2e2b-4ddd-937f-f44bb613eae3",
      "barcode": "5000354922831",
      "displayName": "Beef Gravy",
      "brand": "Bisto",
      "size": "230g",
      "count": 2,
      "latestCheckedInAt": "2026-10-17T18:16:15.743Z",
      "status": "known",
      "section": "later",
      "soonestEstimatedExpiryDate": "2027-10-17",
      "daysUntilSoonest": 365,
      "shelfLifeDays": 365
    }
  ]
}
```

| Field | Notes |
|---|---|
| `totalUnits` | All in-stock units (the header's "n items"). |
| `useFirstCount` | Products in `useFirst` (the header's "m to use first"). |
| `products` | By section: `useFirst`, then `later`, then `waitingForName`. Within Use first and Later: soonest estimated expiry first (no estimate last), then name ignoring case. Waiting for a name: most recently scanned first. |
| `barcode` | The product's first barcode, else the unit's. `null` for a product without one. |
| `displayName` | From the product cache; `Unknown item (<barcode>)` for unnamed products (the UI shows "Unknown item"). |
| `brand`, `size` | From the cached product; `null` when missing. |
| `count` | In-stock units of this product. |
| `latestCheckedInAt` | The newest of those units' check-in times (UTC). |
| `status` | `known`, `noMatch` or `lookupFailed`. |
| `section` | `useFirst`: a known product whose soonest unit is estimated expired or due within `USE_FIRST_DAYS` (app setting, default **3**) days. `later`: every other known product, including those with no estimate yet. `waitingForName`: `noMatch` and `lookupFailed`. |
| `soonestEstimatedExpiryDate` | `YYYY-MM-DD`: the earliest `estimatedExpiryDate` of its in-stock units. `null` until it has an estimate. |
| `daysUntilSoonest` | Days from today (UK calendar date) to that date; negative once it's passed, `0` on the day. `null` with it. |
| `shelfLifeDays` | The product's shelf life, for the "keeps about …" note (the soonest unit was bought on `soonestEstimatedExpiryDate − shelfLifeDays`). `null` until estimated. |

The UI's badge: `Est. expired` (red), `Est. today` / `Est. tomorrow` / `Est. n days` up to 14 days (amber at 3 days or fewer, green above), then `Est. 2 Nov`, or `Est. Oct 2028` when it's over a year away. The digest's Use first section uses the same text (`ExpiryBadge`).

`Cache-Control: no-store`.

### `POST /api/products/{productId}/check-out`

Check out **one** unit of the product from the stock list (#50): the same unit a check-out scan would take (soonest estimated expiry, then oldest check-in), ETag-guarded so the app and a scanner never take the same unit. No body. The unit gets `status: checkedOut`, `checkedOutAt` = now, `checkOutSource: "ui"` and `checkedOutBy` = the caller's object ID.

- **200** `{ "unitId": "…", "productId": "…", "displayName": "Beef Gravy", "remainingCount": 1 }`
- **409** `{ "error": "Nothing of that product is in stock." }`: nothing changed.

### `POST /api/units/{unitId}/undo-check-out`

Put a checked-out unit back in stock, clearing its check-out details. **Idempotent:** a unit already in stock is left alone. No body.

- **200** `{ "unitId": "…", "productId": "…", "count": 2, "restored": true }`: `count` is the product's in-stock units now; `restored` is `false` if it was already in stock.
- **404** `{ "error": "No such unit." }`

### `GET /api/products/{productId}`

A product, for naming (#51) and item detail (#53).

```json
{
  "productId": "…",
  "barcode": "4056489899112",
  "status": "known",
  "displayName": "SEMI-SKIMMED BRITISH MILK",
  "brand": "Dairy Manor",
  "size": "2.272 L",
  "inStockCount": 2,
  "latestCheckedInAt": "2026-10-17T17:42:00.000Z",
  "latestDeviceRole": "in",
  "shelfLifeDays": 7,
  "shelfLifeSource": "jev",
  "units": [
    { "unitId": "…", "checkedInAt": "2026-10-13T09:00:00.000Z", "estimatedExpiryDate": "2026-10-20", "expirySource": "shelfLife", "daysUntilExpiry": 2 },
    { "unitId": "…", "checkedInAt": "2026-10-17T17:42:00.000Z", "estimatedExpiryDate": "2026-10-25", "expirySource": "manual", "daysUntilExpiry": 7 }
  ]
}
```

| Field | Notes |
|---|---|
| `latestCheckedInAt`, `latestDeviceRole` | `in` / `out`, from the device that checked in the newest in-stock unit; `null` when nothing's in stock. |
| `shelfLifeDays`, `shelfLifeSource` | The product's shelf life and who set it (`jev` or `manual`); `null` until estimated. |
| `units` | In-stock units in **check-out order**: soonest estimated expiry first (none last), then oldest check-in. Item detail labels them Unit 1…n. |
| `units[].expirySource` | `shelfLife` (bought date + shelf life) or `manual` (set in item detail); `null` with no date yet. |
| `units[].daysUntilExpiry` | Days from today (UK) to its date; negative once passed. `null` with no date. |

**404** `{ "error": "No such product." }`.

### `POST /api/units/{unitId}/check-out`

Check out **that** unit, from item detail (#53). No body. Recorded like a stock-list check-out: `checkOutSource: "ui"`, `checkedOutBy` = the caller. ETag-guarded. Undo with `POST /api/units/{unitId}/undo-check-out`.

- **200** `{ "unitId": "…", "productId": "…", "displayName": "…", "remainingCount": 1 }`
- **404** `{ "error": "No such unit." }`
- **409** `{ "error": "That unit isn't in stock." }`: already checked out (or a scanner took it meanwhile). Nothing changed.

### `PUT /api/units/{unitId}/estimated-expiry`

Correct **one** unit's estimated expiry date (#53): `{ "date": "2026-10-25" }`. Sets `estimatedExpiryDate` and `expirySource: "manual"`. A manual date is never recalculated (no shelf-life fill or change touches it). Other units are unchanged.

- **200** `{ "unitId": "…", "productId": "…", "estimatedExpiryDate": "2026-10-25", "expirySource": "manual" }`
- **400** `{ "error": "…" }`: not a real `YYYY-MM-DD` date between 2000-01-01 and 2100-12-31.
- **404** `{ "error": "No such unit." }`; **409** `{ "error": "…" }`: not in stock, or changed while saving.

### `PUT /api/products/{productId}/shelf-life`

Set a product's shelf life by hand (#53): `{ "days": 14 }` (whole number, 1–3650). Sets `shelfLifeDays` and `shelfLifeSource: "manual"`; Jev never overwrites it. It applies to **units bought from now on**: units that already have a date keep it (in-stock units with no date yet get one from it on the next shelf-life run). Only for `known` products.

- **200** `{ "productId": "…", "shelfLifeDays": 14, "shelfLifeSource": "manual" }`
- **400** `{ "error": "…" }`; **404** `{ "error": "No such product." }`; **409** `{ "error": "…" }`: not known yet (name it first), or changed while saving.

### `PUT /api/products/{productId}/details`

Correct a **known** product's details (#53): `{ "name": "Semi-skimmed milk", "brand": "Dairy Manor", "size": "2 pint" }`, with the same rules as naming (below). Sets `source: manual`, `displayName`, `brand`, `size`, `editedAt` and `editedBy`. Known products are never looked up again, so nothing overwrites it. Its shelf life is kept.

- **200** `{ "productId": "…", "displayName": "…", "brand": "…", "size": null }`
- **400** `{ "error": "Check the details.", "errors": { "name": "Enter a name." } }`
- **404** `{ "error": "No such product." }`; **409** `{ "error": "…" }`: still unknown (use the name endpoint), or changed while saving.

### `PUT /api/products/{productId}/name`

Name an unknown item (#51): `{ "name": "Smoked paprika", "brand": "Schwartz", "size": "48 g" }` (brand and size optional). Only for `noMatch` or `lookupFailed` products. It sets `status: known`, `source: manual`, `displayName`, `brand`, `size`, `namedAt` and `namedBy` (the caller's object ID), and future check-ins of the barcode use the name with no Open Food Facts call.

| Field | Rule |
|---|---|
| `name` | Required; trimmed, 1–100 characters. |
| `brand`, `size` | Optional; trimmed, at most 60 characters. Empty means none. |

- **200** `{ "productId": "…", "displayName": "Smoked paprika" }`
- **400** `{ "error": "Check the details.", "errors": { "name": "Enter a name." } }` (per field)
- **404** `{ "error": "No such product." }`
- **409** `{ "error": "This product already has a name.", "displayName": "…" }`: it's known (or was named or looked up meanwhile). Renaming known products isn't this endpoint.

### `GET /api/scanners`

The scanners screen (#55): is everything working, and what needs sorting.

```json
{
  "generatedAt": "2026-10-18T08:00:00.000Z",
  "hub": { "hubId": "cupboard-pi", "lastSeenAt": "…", "heartbeatIntervalSeconds": 120, "offlineThresholdMinutes": 20, "online": true },
  "devices": [{ "deviceId": "scanner-in", "role": "in", "present": true, "presentSince": "…", "absentSince": null, "lastScanAt": "…" }],
  "noMatchCheckIns": [
    { "productId": "…", "barcode": "501001234567", "status": "noMatch", "scannedAt": "…", "unitId": "…", "inStockCount": 1, "scanId": null },
    { "productId": null, "barcode": "2012345678903", "status": "storeLabel", "scannedAt": "…", "unitId": null, "inStockCount": 1, "scanId": "…" }
  ],
  "unmatchedCheckOuts": [{ "scanId": "…", "barcode": "20055363", "productId": null, "displayName": null, "scannedAt": "…", "storeLabel": false }],
  "recentlyCheckedOut": [{ "unitId": "…", "productId": "…", "displayName": "Beef Gravy", "checkedOutAt": "…", "source": "scanner" }]
}
```

| Field | Notes |
|---|---|
| `hub` | The most recently heard hub; `null` before the first heartbeat. `online` is false once it's been silent for longer than `OFFLINE_THRESHOLD_MINUTES` (the offline alert's rule). `heartbeatIntervalSeconds` is the API's copy of the Pi's `HEARTBEAT_INTERVAL_SECONDS` (default 120), for display. |
| `devices` | Each scanner, check-in first. `present` is whether its **receiver** is plugged into the hub, per the latest heartbeats: `null` until reported. `presentSince` / `absentSince` say since when. |
| `noMatchCheckIns` | Every product still `noMatch` / `lookupFailed` with something in stock, newest check-in first (its newest unit), then every unassigned **store-label check-in** (#58: `status: "storeLabel"`, `productId` and `unitId` `null`, and the `scanId` to assign with `POST /api/store-label-check-ins/{scanId}/assign`). Includes ones dismissed from the scan feed. |
| `unmatchedCheckOuts` | Every **undismissed** unmatched check-out, newest first. `displayName` only for a named product; otherwise the app shows `Unknown item <barcode>`. `storeLabel` is true for an in-store label (#58), which can never match a unit: the app shows "Store label: check it out from the app". |
| `recentlyCheckedOut` | Units checked out in the last **48 hours**, newest first; `source` is `scanner` or `ui`. Undo with `POST /api/units/{unitId}/undo-check-out`, which works the same for scanner check-outs. |

`Cache-Control: no-store`.

### `POST /api/unmatched-check-outs/{scanId}/dismiss`

Dismisses an unmatched check-out (reviewed, nothing to do). No body. **Idempotent**: the first dismissal's time and user are kept. Stock is never changed.

- **200** `{ "scanId": "…", "dismissed": true }`
- **404** `{ "error": "No such unmatched check-out." }`

## Live scan feed (#54)

Every open app gets scan feed events through **Azure SignalR** (Free, Serverless; hub `ScanFeed`; [spike #38](spikes/38-signalr-scan-feed.md)). The feed is a hint: the API is the source of truth, and the app catches up after every (re)connect. Events are broadcast to every connection (all signed-in household members). Sending is best-effort: a SignalR failure is a warning in the logs and never fails a scan, its processing, naming or dismissal.

### Events

The client method name is the event name (`connection.on("scanReceived", …)`). The contract: **add fields, never rename**. Times are UTC ISO strings. There's no personal data.

Scan events all have `scanId`, `deviceId`, `role` (`in` / `out`) and `occurredAt`, and are keyed by `scanId`. The app never moves a toast backwards: a late `scanReceived` never replaces an outcome.

| Event | Sent by | Extra fields | Toast |
|---|---|---|---|
| `scanReceived` | Ingest, once the scan is stored (not for a resend) | `barcode`, `scannedAt` | Grey: `Reading <barcode>` |
| `scanIdentified` | Processor: a known product checked in or out | `outcome` (`checkedIn` / `checkedOut`), `productId`, `displayName`, `brand`, `size`, `unitId` | Green: `Added …` / `Checked out …`, 10 s |
| `scanNeedsIdentification` | Processor: a check-in of a `noMatch` / `lookupFailed` product | `productId`, `barcode`, `displayName`, `productStatus`, `unitId` | Amber: `No match for <barcode>`, Identify and dismiss, until named or dismissed |
| `checkOutUnmatched` | Processor: a check-out with nothing in stock | `barcode`, `productId` (or `null`), `displayName` (known products only, else `null`), `storeLabel` (#58) | Amber: `Not in stock: …` (a store label: `Store label: check it out from the app`), dismiss, 10 s |
| `storeLabelCheckIn` | Processor: a check-in of a store label (#58) | `barcode` | Amber: `Store label: what is it?`, Pick and dismiss, until assigned or dismissed |
| `storeLabelResolved` | `POST /api/store-label-check-ins/{scanId}/assign` | `scanId`, `productId`, `displayName`, `occurredAt` (not a scan event: no `deviceId` / `role`) | That label's toast turns green: `Added …` |
| `scanSkipped` | No longer sent (store labels were skipped before #58); kept in the contract | `barcode`, `reason` | The grey toast goes |
| `productNamed` | `PUT /api/products/{id}/name` | `productId`, `displayName`, `occurredAt` | That product's amber toasts go |
| `feedDismissed` | `POST /api/scan-feed/{id}/dismiss`, or `…/store-labels/{scanId}/dismiss` | `productId` (`null` for a store label), `scanId` (store labels only), `occurredAt` | That product's (or store label's) amber toast goes on every app |

Processor events are sent again on a change-feed replay (harmless: the app updates the toast in place).

### `POST /api/negotiate`

Called by `@microsoft/signalr` (hub URL `<api>/api`), with the user's bearer token. It returns where to connect and a SignalR access token for that user (`UserId` = their object ID), **200** `{ "url": "https://sigr-…service.signalr.net/client/?hub=scanfeed", "accessToken": "…" }`. No token: **401**.

### `GET /api/scan-feed?since=<ISO time>`

Catch-up, called on load, after every (re)connect and when the app comes back to the foreground. `since` is the last event time the app saw: default now − 10 minutes, at most 24 hours back. **400** if it isn't an ISO time with a timezone.

```json
{
  "generatedAt": "2026-10-18T07:30:00.000Z",
  "events": [
    { "type": "scanIdentified", "scanId": "…", "deviceId": "scanner-in", "role": "in", "occurredAt": "…", "outcome": "checkedIn", "productId": "…", "displayName": "Beef Gravy", "brand": "Bisto", "size": "230g", "unitId": "…" },
    { "type": "scanNeedsIdentification", "scanId": "…", "deviceId": "scanner-in", "role": "in", "occurredAt": "…", "productId": "…", "barcode": "501001234567", "displayName": "Unknown item (501001234567)", "productStatus": "noMatch", "unitId": "…" }
  ]
}
```

`events` (oldest first, the same shapes as the live events plus `type`) has two parts:

- **Each scan received since `since`**, at most 200, with its outcome now: `scanReceived` if not processed yet, `scanIdentified` (a product named since shows as identified), `scanNeedsIdentification` or `checkOutUnmatched`. `occurredAt` is when ingest received it. A store-label check-in appears in the next part instead (or not at all once assigned).
- **Every product still waiting for a name** (`noMatch` / `lookupFailed`) that has something in stock and whose newest check-in is after its `feedDismissedAt`, as one `scanNeedsIdentification` for that newest unit. This is how amber toasts survive a reload. A recent scan of a product dismissed since then isn't reported as amber.
- **Every unassigned store-label check-in** not dismissed from the feed, as `storeLabelCheckIn` (#58).

`Cache-Control: no-store`.

### `POST /api/scan-feed/{productId}/dismiss`

Hides a product's amber "No match" toast for the whole household. No body. It sets the product's `feedDismissedAt` (ETag-guarded) and sends `feedDismissed`. **It changes nothing else**: stock, status and "Waiting for a name" stay as they are. A check-in of the product after this shows a fresh toast. (Dismissing a "Not in stock" toast is client-side only.)

- **200** `{ "productId": "…", "feedDismissedAt": "…" }`
- **404** `{ "error": "No such product." }`

### `POST /api/scan-feed/store-labels/{scanId}/dismiss`

Hides a store label's amber toast for the household (#58). No body. Sets the store-label check-in's `feedDismissedAt` and sends `feedDismissed` with the `scanId`. It stays on the scanners screen to pick later.

- **200** `{ "scanId": "…", "dismissed": true }`
- **404** `{ "error": "No such store-label check-in." }`

## Adding items from the app (#58)

Loose items (no barcode) and store-labelled items are added here; see [domain language](domain-language.md#products--stock). All need a signed-in user (**401** without a token). The photo is **never stored or logged**.

### `POST /api/photos/recognise`

What a photo shows ([spike #41](spikes/41-photo-recognition.md): gpt-4o on Azure AI Foundry, called with the API app's managed identity). The body is the photo itself, `Content-Type: image/jpeg`, **at most 1 MB** (the app resizes to 1024 px on the long edge first). Nothing is stored.

```json
{
  "readable": true,
  "items": [
    { "name": "Leeks", "brand": null, "size": null, "kind": "loose", "quantity": 2, "match": null },
    { "name": "Bananas", "brand": null, "size": null, "kind": "loose", "quantity": 6, "match": { "productId": "…", "displayName": "Bananas" } }
  ]
}
```

| Field | Notes |
|---|---|
| `readable` | `false` when the photo was too blurry or dark to tell anything. |
| `items[].name` | What the item is, specific enough to buy again (≤ 100 characters). |
| `items[].brand`, `size` | Only when legible on that item's own pack (≤ 60 characters), else `null`. |
| `items[].kind` | `packaged`, `loose` or `storeLabelled`. Non-food is left out. |
| `items[].quantity` | How many identical items, 1–20. |
| `items[].match` | An existing product with the same display name (ignoring case and extra spaces), loose ones first; else `null`. |

- **400** `{ "error": "…" }`: not a JPEG, or empty.
- **413**: over 1 MB.
- **503** `{ "error": "Photo recognition isn't available right now. …" }`: the model failed after retries (429 and 5xx are retried). The app falls back to picking and typing.

### `GET /api/products/loose`

Loose products (known, no barcode) for the quick pick, **most recently added first**, then A–Z. `Cache-Control: no-store`.

```json
{ "products": [{ "productId": "…", "displayName": "Bananas", "shelfLifeDays": 7, "lastAddedAt": "…" }] }
```

### `POST /api/stock/add`

Adds units to stock from the app.

```json
{
  "requestId": "6c1f…",
  "items": [
    { "productId": "…", "quantity": 2, "source": "pick" },
    { "name": "Leeks", "brand": null, "size": null, "quantity": 3, "source": "photo" }
  ]
}
```

- `requestId`: a new UUID per add (required). **Idempotent**: new products' and units' ids are derived from it, so sending the same request again creates nothing twice.
- `items`: 1–20 rows. Each is an existing **known** product (`productId`) or a `name` (≤ 100; `brand` and `size` ≤ 60 optional). A name is matched to an existing product (ignoring case and extra spaces, loose ones first) or becomes a new **loose product** (`source: photo` for `photo` rows, else `manual`).
- `quantity`: 1–20 (default 1). `source`: `photo`, `pick` or `typed` (default `pick` with a `productId`, else `typed`).
- Each unit: `checkedInAt` now, `checkInSource: "ui"`, `checkedInBy` the user, no barcode, and an estimated expiry straight away if the product has a shelf life (otherwise the shelf-life timer fills it in).

- **200** `{ "units": 5, "items": [{ "productId": "…", "displayName": "Leeks", "quantity": 3, "createdProduct": true }] }`
- **400** `{ "error": "…", "errors": { "items[0].name": "Enter a name." } }`: bad shape, no `requestId`, a bad row, or a `productId` that isn't a known product (`items[n].productId`).

### `POST /api/store-label-check-ins/{scanId}/assign`

"What is this?" for a store-label check-in: `{ "productId": "…" }` or `{ "name": "Ham hock terrine", "brand": null, "size": null, "source": "typed" }` (matched or created as in `stock/add`; quantity is always 1). Adds **one unit at the original scan time** (its id is the scan ID, with the label as its barcode), resolves the check-in and sends `storeLabelResolved`. **Idempotent**: assigning again to the same product changes nothing.

- **200** `{ "scanId": "…", "unitId": "…", "productId": "…", "displayName": "Ham hock terrine" }`
- **400** as for `stock/add` (field names without the `items[n].` prefix).
- **404** `{ "error": "No such store-label check-in." }`
- **409** `{ "error": "That check-in has already been sorted out.", "productId": "…" }`: already assigned to a different product.
