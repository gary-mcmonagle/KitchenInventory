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
