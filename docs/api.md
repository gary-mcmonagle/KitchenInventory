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
| `openedCount`, `frozenCount` | In-stock units opened, and in the freezer now (#65): the row's **Opened** / **n frozen** pills. |
| `aisleId`, `icon`, `useFirst` | Its aisle (#71; `other` until Jev has chosen), its icon (#72; `null`: show the aisle's icon, or the letter for `other`) and whether it's in Use first. |
| `genericName` | Its generic name (#90), or `null`. Added for the stock search (#91), which filters in the app. |
| `aisles` | Known products **by aisle in shop order** (#71): `[{ aisleId, name, icon, itemCount, useFirstCount, anyExpired, products: [...] }]`, empty aisles left out. Within an aisle: use first (soonest first), then the rest by soonest estimated expiry, then name. The UI's Use first view filters this in the app. |
| `waitingForName` | Unknown items (`noMatch` / `lookupFailed`), newest first, apart from the aisles. |
| `soonestState`, `soonestStateOn` | When the soonest unit's date comes from being `opened`, `frozen` or `defrosted` (#65): which, and since when (`YYYY-MM-DD`). The row then says "Opened 12 Oct" instead of the bought note. `null` otherwise. |

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
  "usualStorage": "fridge",
  "opened": { "days": 4, "storage": "fridge", "source": "jev" },
  "frozen": { "days": 90, "storage": "freezer", "source": "jev" },
  "defrosted": { "days": 2, "storage": "fridge", "source": "jev" },
  "openedCount": 1,
  "frozenCount": 0,
  "units": [
    { "unitId": "…", "checkedInAt": "2026-10-13T09:00:00.000Z", "estimatedExpiryDate": "2026-10-19", "expirySource": "opened", "daysUntilExpiry": 1,
      "openedOn": "2026-10-15", "frozenOn": null, "defrostedOn": null, "currentStorage": "fridge", "estimatePending": false, "canUndoStateChange": true },
    { "unitId": "…", "checkedInAt": "2026-10-17T17:42:00.000Z", "estimatedExpiryDate": "2026-10-25", "expirySource": "manual", "daysUntilExpiry": 7,
      "openedOn": null, "frozenOn": null, "defrostedOn": null, "currentStorage": null, "estimatePending": false, "canUndoStateChange": false }
  ]
}
```

| Field | Notes |
|---|---|
| `latestCheckedInAt`, `latestDeviceRole` | `in` / `out`, from the device that checked in the newest in-stock unit; `null` when nothing's in stock. |
| `shelfLifeDays`, `shelfLifeSource` | The product's shelf life and who set it (`jev` or `manual`); `null` until estimated. |
| `usualStorage` | Where it's kept sealed (`cupboard`, `fridge`, `freezer`), from its shelf life; `null` until estimated. |
| `opened`, `frozen`, `defrosted` | How long it keeps once opened, in the freezer, and once defrosted, and where to keep it then (#65): `{ days, storage, source }`, `source` `jev` or `manual` (only `opened` can be set by hand). `null` until the shelf-life timer has asked Jev. |
| `openedCount`, `frozenCount` | In-stock units opened, and in the freezer now. |
| `aisleId`, `aisleSource` | Its aisle (#71) and who chose it (`jev` / `manual`); `null` until the shelf-life timer has asked Jev. |
| `icon`, `iconSource` | Its icon name (#72, IconPark Outline) and who chose it; `icon` is `null` when Jev wasn't sure (below 0.3), so the UI shows the aisle's icon. |
| `genericName` | `{ id, name, source }` (#90): the shopping-list name and where it came from (`jevMatch`, `foundry`, `manual`, `fallback`); `null` until the timer has worked it out. |
| `units` | In-stock units in **check-out order**: **opened first, in the freezer last** (#65), then soonest estimated expiry (none last), then oldest check-in. Item detail labels them Unit 1…n. |
| `units[].expirySource` | `shelfLife` (bought date + shelf life), `manual` (set in item detail), or `opened` / `frozen` / `defrosted` (#65: what its estimate is from now); `null` with no date yet. |
| `units[].openedOn`, `frozenOn`, `defrostedOn` | `YYYY-MM-DD` (UK) when marked, or `null`. Frozen now = `frozenOn` set and `defrostedOn` not. |
| `units[].currentStorage` | Where to keep it now it's opened, frozen or defrosted; `null` otherwise. |
| `units[].estimatePending` | Marked before the product had that estimate: the date shown is the old one, and the shelf-life timer fills in the new one. |
| `units[].canUndoStateChange` | Its last state change can be undone. |
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

### `POST /api/units/{unitId}/opened`, `/frozen`, `/defrosted`

Marks a unit opened, frozen or defrosted (#65), from item detail's **More** sheet. Body: optional `{ "date": "YYYY-MM-DD" }` (default today, UK; not in the future, not before it was bought). It re-estimates the unit at once from the product's estimates, **without calling Jev**:

- **Opened:** the earlier of open date + `opened.days` and the date it had (opening never makes something last longer; no date yet: open date + `opened.days`). Keep it in `opened.storage`.
- **Frozen:** freeze date + `frozen.days`. In the freezer.
- **Defrosted** (frozen units only): defrost date + `defrosted.days`. Keep it in `defrosted.storage`.

The latest action decides, and a manual date is replaced (Undo brings it back; each change can be undone in turn). With no product estimate yet, the state is saved with the old date and `estimatePending: true`, and the timer fills it in. **Idempotent:** marking it again changes nothing.

- **200** `{ "productId": "…", "unit": { …as in GET /api/products/{productId} } }`
- **400** `{ "error": "…" }`: a bad date, in the future, or before it was bought.
- **404** `{ "error": "No such unit." }`
- **409** `{ "error": "…" }`: not in stock, Defrosted on a unit that isn't frozen, or changed while saving.

### `PUT /api/products/{productId}/aisle`

Puts a known product in an aisle by hand (#71): `{ "aisle": "bakery" }`, one of the 14 aisle IDs (`fruitVeg`, `bakery`, `meatFish`, `dairyEggs`, `chilledDeli`, `frozen`, `pastaRiceGrains`, `tinsJars`, `saucesOils`, `bakingSpices`, `breakfastCereal`, `snacksSweets`, `drinks`, `other`). Sets `aisleSource: manual`; Jev never overwrites it.

- **200** `{ "productId": "…", "aisle": "bakery" }`; **400** not an aisle; **404**; **409** not known yet (name it first), or changed while saving.

### `GET /api/generic-names`

Every generic name (#90), A–Z, for Edit details' picker: `{ "genericNames": [{ "id": "semi-skimmed milk", "name": "Semi-skimmed milk", "aisleId": "dairyEggs", "aisleName": "Dairy & eggs", "productCount": 3 }] }`.

### `PUT /api/products/{productId}/generic-name`

Sets a known product's generic name by hand (#90): `{ "genericNameId": "semi-skimmed milk" }` (an existing name, the usual path) or `{ "name": "Whole milk" }` (a new one, created in the product's aisle; a name that normalizes to an existing record uses it). Saved as `genericNameSource: manual`, never overwritten.

- **200** `{ "productId": "…", "genericName": { "id": "…", "name": "…", "source": "manual" }, "created": false }`
- **400** neither or both fields, no such name, or a bad new name (1–40 characters, no size, not the brand); **404** not a known product; **409** changed meanwhile.

### Add when we run out (#89)

The setting lives on the **generic name**, so turning it on from one brand covers every product with that name. After **every** check-out (scanner, stock list or item detail, whatever its reason), if the setting is on and nothing is left in stock across every product with that generic name, the name is added to the shopping list with `source: "autoRunOut"` and the product's id. Its item id and change id come from the unit and its check-out time, so a replayed check-out never adds twice. **Undoing** that check-out removes the item again, unless it has been ticked (even if put back) or given a note since. A name already open on the list isn't added again. Best-effort: a failure is a warning and never fails the check-out. A product without a generic name counts by its display name, but the setting can't be turned on for it.

#### `GET /api/products/{productId}/run-out`

**200** `{ "genericNameId": "semi-skimmed milk", "name": "Semi-skimmed milk", "addWhenOut": false, "inStock": 3, "canSet": true }`: `inStock` counts every product with that generic name; `canSet` is false while the product has no generic name. **404** not a known product.

#### `PUT /api/generic-names/{id}/add-when-out`

`{ "addWhenOut": true }` (or `false`). **200** `{ "genericNameId": "…", "name": "…", "addWhenOut": true }`; **400** a body that isn't a boolean; **404** no such generic name. The id is URL-encoded (it can contain spaces).

#### `GET /api/run-out/batches/{batchId}` and `GET /api/run-out/products/{productId}`

The "Add to the shopping list?" sheet: for a tapped stock change push (its batch), or for a tapped green "Checked out" toast (one product). **200**:

```json
{
  "checkedOutBy": "Sam",
  "at": "2026-10-20T17:42:00+00:00",
  "items": [{ "productId": "…", "genericNameId": "semi-skimmed milk", "name": "Semi-skimmed milk", "left": 0, "onList": false, "addWhenOut": false, "canSet": true }]
}
```

- **Batches:** the batch's removed items **netted per product** (a check-out and its undo cancel out), leaving out the caller's own app check-outs. One row per generic name. `checkedOutBy` is the person's name, `"the scanner"` for scanner check-outs, or null when several people did. **404** once the batch has expired (3 days) or for an unknown id.
- **Products:** `checkedOutBy` and `at` are null.

### `PUT /api/products/{productId}/icon`

Sets a known product's icon by hand (#72): `{ "icon": "bread" }`, one of the curated IconPark names (`ProductIcons.Names`), sets `iconSource: manual`, never overwritten. `{ "icon": null }` clears it ("Let Jev choose"): the next timer run asks Jev again.

- **200** `{ "productId": "…", "icon": "bread" }`; **400** not one of the icons; **404**; **409** as above.

### `POST /api/products/{productId}/opened`

The stock list's **Opened** button (Gary, 2026-10-18): opens the unit that would be used next (check-out order, skipping units in the freezer), today, exactly as `POST /api/units/{unitId}/opened`. No body. **200** with the opened unit (Undo with its `unitId`); **404** with nothing in stock; **409** when one's already open (the button is greyed out then) or every unit is in the freezer.

### `POST /api/units/{unitId}/undo-state-change`

Puts the unit's estimate fields back exactly as before its last state change. Call it again to undo the change before that (up to 5 back). No body. **200** as above; **404**; **409** when there's nothing to undo or it isn't in stock.

### `PUT /api/products/{productId}/opened-shelf-life`

How long it keeps once opened, by hand (#65): `{ "days": 5 }` (1–3650). Sets `opened.source: "manual"`, which Jev never overwrites. It applies to units **opened from now on**. Only for `known` products. Responses as `PUT /api/products/{productId}/shelf-life`.

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
  "available": true,
  "readable": true,
  "items": [
    { "name": "Leeks", "brand": null, "size": null, "kind": "loose", "quantity": 2, "match": null },
    { "name": "Bananas", "brand": null, "size": null, "kind": "loose", "quantity": 6, "match": { "productId": "…", "displayName": "Bananas" } }
  ]
}
```

| Field | Notes |
|---|---|
| `available` | `false` when the model couldn't be reached after retries (429 and 5xx are retried, or it isn't configured): `readable` is false, `items` empty, and `error` says to pick or type instead. A **200**, not a 5xx, so an outage doesn't fire the error alert (#48); a misconfiguration (the model refusing the app) is logged at Error. |
| `readable` | `false` when the photo was too blurry or dark to tell anything. |
| `items[].name` | What the item is, specific enough to buy again (≤ 100 characters). |
| `items[].brand`, `size` | Only when legible on that item's own pack (≤ 60 characters), else `null`. |
| `items[].kind` | `packaged`, `loose` or `storeLabelled`. Non-food is left out. |
| `items[].quantity` | How many identical items, 1–20. |
| `items[].match` | An existing product with the same display name (ignoring case and extra spaces), loose ones first; else `null`. |

- **400** `{ "error": "…" }`: not a JPEG, or empty.
- **413**: over 1 MB.

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

## Hungry? recipes and Ready now ideas (#73, #88)

### `POST /api/recipes/suggest`

A **recipe suggestion** built from what's in stock: gpt-4o on Azure AI Foundry (the deployment in `RECIPE_MODEL_DEPLOYMENT`, default `gpt-4o`), called with the API app's managed identity and a strict JSON schema. Needs a signed-in user (**401** without a token). **Stateless: nothing is stored.** Only metadata is logged (latency, token counts, deployment, outcome, and whether comments were given): never the prompt, comments, stock or recipe.

```json
{
  "mode": "cook",
  "meal": "dinner",
  "preferUseFirst": true,
  "includeProductIds": ["…"],
  "comments": "something spicy, no oven",
  "avoidTitles": ["Creamy cheddar penne"]
}
```

| Field | Notes |
|---|---|
| `mode` | `cook` (default: one recipe, below) or `readyNow` (3–5 quick ideas, see **Ready now**) (#88). |
| `meal` | Required: `breakfast`, `lunch`, `dinner` or `snack` (#88). |
| `preferUseFirst` | Build around the use-first items (default `false`). |
| `includeProductIds` | Products that must be in it (default none, at most 20). Each must be a **known** product in stock. |
| `comments` | One-off preferences, or `null`. Trimmed; empty means none; **at most 300 characters**. Sent to the model in their own delimited section as wishes, never instructions: they can't change the stock-only rule, the 3-item limit, food safety or the output format. |
| `avoidTitles` | Titles already shown this visit (Another one, More ideas): the model suggests something different, a little more freely. The last 20 are used, each cut to 80 characters. |

The **stock is read on the server from Cosmos**, never taken from the app (in the prompt each product is a short ref, `p1`, `p2`, …, mapped back to its ID on the server, so a prompt stays around 7k tokens; the deployment allows 50k tokens a minute since 2026-10-21, 10k before): every in-stock known product (items waiting for a name are left out) with its name, quantity, days to its soonest estimated expiry, the use-first flag, its aisle and how many are opened.

```json
{
  "available": true,
  "recipe": {
    "title": "Creamy cheddar penne",
    "summary": "A quick one-pan pasta that uses up the milk.",
    "meal": "dinner",
    "servings": 2,
    "totalMinutes": 25,
    "ingredients": [
      { "name": "Semi-skimmed milk", "amount": "300 ml", "productId": "…", "useFirst": true },
      { "name": "Oil, salt and pepper", "amount": "1 tbsp", "productId": null, "useFirst": false }
    ],
    "steps": ["Cook the penne…", "…"],
    "usesUp": [{ "productId": "…", "displayName": "Semi-skimmed milk", "daysUntilSoonest": 2, "soonestEstimatedExpiryDate": "2026-10-20" }],
    "alsoNeeded": ["1 onion"],
    "couldNotFitIn": []
  }
}
```

| Field | Notes |
|---|---|
| `ingredients[].productId` | An in-stock product (its `name` is the product's **current** display name), or `null` for a basic staple. Product IDs the model made up are dropped (the ingredient becomes plain text). |
| `ingredients[].useFirst` | The product is in Use first: the app shows the amber dot. |
| `usesUp` | Use-first products the recipe uses, with what the badge needs. |
| `alsoNeeded` | Things not in stock beyond basic staples (salt, pepper, oil, water), **at most 3**. |
| `couldNotFitIn` | Names of included products the recipe still leaves out after one retry. |
| Limits | `title` ≤ 80 characters, `summary` ≤ 300, `steps` ≤ 15 of ≤ 300 each, `servings` 1–12, `totalMinutes` 1–600; `meal` is always the one asked for. |

In cook mode the model is told simple dishes are welcome (a cheese toastie or beans on toast is fine), and a snack is small.

#### Ready now (`"mode": "readyNow"`, #88)

Quick things using **only a toaster, microwave or kettle, or no cooking** (never a hob, oven, grill or air fryer), ready in about 10 minutes, from the stock (basic staples allowed; butter, spreads and sauces only if in stock). Same model, identity, rate limit, comments rules and logging (the mode and meal, never text).

```json
{
  "available": true,
  "mode": "readyNow",
  "meal": "snack",
  "ideas": [
    {
      "title": "Hummus and toasted pitta",
      "how": "Toast 2 pittas, slice into strips and dip.",
      "appliance": "toaster",
      "minutes": 4,
      "uses": [{ "productId": "…", "displayName": "Hummus", "useFirst": true, "daysUntilSoonest": -1, "soonestEstimatedExpiryDate": "2026-10-19" }],
      "alsoNeeded": null
    }
  ]
}
```

| Field | Notes |
|---|---|
| `ideas` | 1–5 (the model is asked for 3–5). An idea with an empty title or how, an appliance outside the four, a repeated title or one in `avoidTitles` is dropped. None left → `available: false`. |
| `appliance` | `toaster`, `microwave`, `kettle` or `none` (no cooking). |
| `uses` | In-stock products it uses (made-up IDs dropped), with the product's current name, use-first flag and expiry for the app's dots and "check it's still good" line. |
| `alsoNeeded` | At most **one** thing not in stock, or `null`. |
| Order | With `preferUseFirst`, ideas that use a use-first product come first; otherwise the model's order. |
| Limits | `title` ≤ 60 characters, `how` ≤ 240, `minutes` 1–30. |

- **200** `{ "available": false, "message": "Couldn't come up with a recipe just now. Try again." }`: the model timed out, couldn't be reached (429 and 5xx are retried), gave an unreadable answer, or refused (its content filter, which the comments can trigger). A 200, so it doesn't fire the error alert; a misconfiguration (the model refusing the app) is logged at Error.
- **400** `{ "error": "…" }`: bad shape, meal or mode, comments over 300 characters, more than 20 included, or an included product that isn't a known product in stock.
- **422** `{ "error": "There's nothing in stock to cook with." }`: no known products in stock.
- **429** `{ "error": "That's a lot of recipes for one hour. Try again a bit later." }`: over `RECIPE_REQUESTS_PER_HOUR` (default 30) for this user in the last hour. Counted per API app instance, in memory: a cost guard, not an exact quota.

### Adjust a recipe (#104)

#### `POST /api/recipes/adjust`

```json
{
  "recipe": { "title": "…", "summary": "…", "meal": "dinner", "servings": 2, "totalMinutes": 25,
              "ingredients": [{ "name": "…", "amount": "…", "productId": "…" | null }], "steps": ["…"], "alsoNeeded": ["…"] },
  "add": { "productIds": ["…"], "typed": ["chilli flakes"] },
  "remove": ["Onion"],
  "tweak": "spicier" | null
}
```

The same recipe, updated by gpt-4o on Foundry (same deployment, managed identity, schema and validation as suggest).
- **The recipe** is the one the app last showed. It's only the starting point: the stock is rebuilt from Cosmos. Up to 40 ingredients, 20 steps and 5 `alsoNeeded`; other fields (e.g. `useFirst`, `usesUp`) are ignored.
- **Changes:**
  - `add.productIds` must be in stock (400 otherwise), at most 10.
  - `add.typed`: things to buy, at most 3 of up to 40 characters. They go under "You'll also need" and count towards its 3.
  - `remove`: ingredient names from the recipe; unknown names are ignored.
  - `tweak`: at most 300 characters (trimmed). Like the form's comments, it's a preference in its own delimited section and can't override the rules.
  - At least one change is needed (400 "Choose something to add, remove or tweak.").
- **Checked:** added items must appear and removed ones mustn't. If they don't, the model is asked once more; anything still wrong comes back as `couldNotAdd` / `couldNotRemove` on the recipe ("Couldn't add: …").
- **200:** `{ "available": true, "recipe": { …as suggest…, "couldNotAdd": [], "couldNotRemove": [] }, "adjustmentSummary": { "added": ["Chorizo", "chilli flakes"], "removed": ["Onion"], "tweak": "spicier" } }`. Added products are named by their generic name. When the model can't help: `{ "available": false, "message": "Couldn't update the recipe just now. Try again." }`.
- **Other statuses:** **422** nothing in stock; **429** over the hourly limit, which **adjustments share with suggest**; **401** without a token.
- Nothing is stored. Logs record counts and whether a tweak was given, never the recipe or the tweak.

#### `POST /api/recipes/suggest-additions`

`{ "recipe": { …as above… } }` → **200** `{ "suggestions": [{ "name": "Chorizo", "productIds": ["…"], "useFirst": false, "confidence": 0.41 }] }`, 0 to 3, best first.
- **Candidates:** every in-stock known product, grouped by **generic name** (display name when it has none), less anything already in the recipe (the same product, or a name an ingredient contains or is contained in).
- **Ranking:** one Jev `choice` question ("which would improve this recipe most as an addition?") over the candidates plus "Nothing would improve it". Jev gives a **probability for every option**, so one question ranks them all; there's no asking again.
- **Picking:** up to 3 that reach `RECIPE_ADDITION_MIN_CONFIDENCE` (default 0.1) **and** beat "Nothing would improve it".
- **More than 254 candidates:** asked in batches; each batch's top picks meet in a **run-off** question, which decides.
- **Use first:** a use-first suggestion moves ahead of ones within 0.1 of it.
- No Foundry call and no rate limit. If Jev fails it returns `[]`, and the sheet hides the row. Logs record counts and latency only.

## Shopping list (#84)

One list for the household. Every endpoint needs a signed-in user (**401** without a token). Item ids and change ids come from the app (UUIDs), so a write sent twice is applied once (ready for offline use). People are shown by name, never by object ID.

**Item:**

```json
{
  "id": "…", "name": "Semi-skimmed milk", "note": "2", "productId": "…" | null,
  "aisleId": "dairyEggs" | null, "aisleName": "Dairy & eggs" | null, "findingAisle": false,
  "source": "typed" | "suggestion" | "itemDetail", "status": "open" | "done",
  "addedByName": "Gary", "addedAt": "…", "doneByName": null, "doneAt": null,
  "updatedAt": "…", "version": 3, "removed": false
}
```

`version` goes up on every change: **apps keep the higher one**, so a late or repeated event never undoes a newer change. `findingAisle` is true while Jev works out a typed item's aisle (the row shows a spinner).

### `GET /api/shopping-list[?since=<asOf>]`

**200** `{ "asOf": "…", "items": [...] }`: open items (oldest first) and items ticked off in the last 24 hours. With `since` (an earlier `asOf`): every item changed since then (looking back 5 s more, for clock differences), removed ones as tombstones (`removed: true`, kept a week). Apps call it after every SignalR (re)connect and when they come back to the foreground. **400** for a bad `since`.

### `PUT /api/shopping-list/items/{id}`

`{ "changeId": "…", "name": "Kitchen foil", "note": "the big roll", "productId": null, "source": "typed" }` adds the item at once. **200** `{ "item": {…}, "merged": false }`.

- **Idempotent:** an id that exists returns the item as it is.
- **No duplicates:** a name already open (lower case, trimmed, single spaces) returns that item with `merged: true`, adding the note if one was given.
- **Aisle:** with `productId`, the product's aisle (and, when `name` is left out, its generic name or display name). A typed item reuses the aisle that name had before, or is queued for Jev (the aisles feature's choice question) on the API app's `list-aisles` queue; until then `aisleId` is null. Jev unreachable → `other` for now, retried by a timer every 15 minutes.
- **400** for a missing id or changeId, a name of 0 or over 80 characters, a note over 60, or an unknown product. `source` is one of `typed`, `suggestion`, `itemDetail` or `checkOutPrompt` (the sheet after a check-out, #89); `autoRunOut` is refused (only the server adds those).

### `POST /api/shopping-list/items/{id}/done`, `/undone` and `DELETE /api/shopping-list/items/{id}`

Body `{ "changeId": "…" }`. Tick off, put back, or remove (a tombstone). The same `changeId` again changes nothing. **200** `{ "item": {…} }`; **404** for an unknown id.

`/done` and `/undone` also take an optional **`at`** (ISO 8601 with offset; **400** if it isn't one): when the change was really made on the phone, for changes sent late from the **outbox** (#85). A tick or put-back made **at or before** the item's last tick or put-back changes nothing (200 with the item as it is), so a late offline change never undoes a newer one. A time in the future counts as now. Without `at`, the change counts as made now. The item stores it as `statusChangedAt`.

### `POST /api/shopping-list/clear-done`

"Clear ticked": every ticked item becomes a tombstone. **200** `{ "cleared": ["…"] }`. A timer does the same for items ticked off more than a day ago.

### `GET /api/shopping-list/suggestions?q=`

**200** `{ "suggestions": [{ "name": "Semi-skimmed milk", "inStock": 3, "productId": "…" | null }] }`, at most 8: names containing `q` from products' **generic names** (display names when they have none) and past list items. Ranked by names starting with `q`, then generic names, then how often and how recently they were used. `inStock` sums units across **every product with that generic name**. Names already open are left out.

### Live updates (SignalR)

On the scan feed's hub (one connection per open app carries both), sent after each successful write; **best-effort** (a failure is a warning; apps catch up with `since`):

- `shoppingListItemChanged`: the full item (adds, ticks, put-backs, note merges, **an aisle arriving**).
- `shoppingListItemRemoved`: `{ "id": "…", "version": 4 }`.
- `shoppingListCleared`: `{ "ids": ["…"] }`.

## Push notifications (#77)

The plumbing for push notifications ([Task] Web Push plumbing, spike #75). Every endpoint needs a signed-in user (**401** without a token) and only touches the **caller's own** subscriptions. The features decide what to send and when; nothing here sends a push except the test.

### `GET /api/push/config`

`{ "publicKey": "<VAPID public key, base64url>" }`, or `null` until the keys are set. The app subscribes with it.

### `PUT /api/push/subscriptions`

This device's subscription, as `PushSubscription.toJSON()` gives it: `{ "endpoint": "https://…", "keys": { "p256dh": "…", "auth": "…" } }`. Saves it for the caller, or refreshes it (the app sends it on every open, which keeps `lastSeenAt` fresh); preferences already set are kept. Optional `deviceLabel` (`iPhone`, `iPad`, `Android`, `Mac`, `Windows`, `Linux` or `Browser`), worked out by the app; otherwise it comes from the `User-Agent` (which didn't reach the API from an installed iPhone app).

- **200** `{ "id": "…", "deviceLabel": "iPhone", "preferences": { "stockChanges": true } }` (the endpoint is never sent back).
- **400** `{ "error": "…" }`: the endpoint isn't on a known push service (Apple `web.push.apple.com`, Google `fcm.googleapis.com`, Mozilla `updates.push.services.mozilla.com`, Microsoft `*.notify.windows.com`, HTTPS, default port), or the keys aren't a P-256 key and a 16-byte secret (base64url).

### `DELETE /api/push/subscriptions`

`{ "endpoint": "…" }`: removes the caller's subscription for that endpoint. **200** `{ "removed": true }`; **404** if the caller has none for it; **400** without an endpoint.

### `PATCH /api/push/subscriptions/preferences`

`{ "endpoint": "…", "preferences": { "stockChanges": true } }`: merges these types into the caller's subscription. Types are letters and digits (at most 20). **200** as for `PUT`; **404** if the caller has no such subscription; **400** for a bad body or type.

### `POST /api/push/seen`

`{ "eventKeys": ["scan:<scanId>", …] }`: the caller saw these events on a visible screen, so a delayed push about them leaves the caller out (on all their devices). Keys are 1–100 characters without `/ \ ? #`, at most 50 per call; the user comes from the token. **204**; **400** for a bad body. Fire-and-forget: a lost receipt only means an extra push.

### `POST /api/push/test`

Sends "Test notification" to the caller's own devices only. **200** `{ "targeted": 2, "delivered": 2, "removed": 0, "failed": 0 }`; **409** if the caller has no devices with notifications on; **429** after 5 in an hour (`PUSH_TEST_PER_HOUR`); **503** until the VAPID keys are set.

### What a push looks like

Declarative Web Push JSON (WebKit; iOS 18.4+ shows it without the service worker, and the app's worker shows it everywhere else):

```json
{ "web_push": 8030, "notification": { "title": "…", "body": "…", "navigate": "https://kitchen.garymcmonagle.com/…", "tag": "stock", "lang": "en-GB" } }
```

Sent with `Content-Encoding: aes128gcm` (RFC 8291), VAPID (RFC 8292; subject `https://kitchen.garymcmonagle.com`), `TTL`, `Urgency` and a letters-and-digits `Topic` equal to the tag, so a newer push of the same kind replaces an older one. Delayed pushes are jobs on the API app's `push-jobs` Storage queue; each runs once.

### Stock change pushes (#80)

No endpoint of their own: they use the subscription, preferences and seen receipt endpoints above.

- **Which changes:** scanner check-ins of a named product (a new unit, not a replay) and scanner check-outs that took a unit, the ones that show a green toast (`scanIdentified`). Their key is `scan:<scanId>`. Not amber toasts or unmatched check-outs.
- **App changes (#81):** check-outs from the stock list or item detail (removed), units added from the Add tab and store labels assigned with Pick (added; not on a retry), and undone check-outs (added back). Each carries its **actor** (the caller's `oid`) and a key `app:<unitId>:<out|undo|added>:<ms>`. Renames, estimates, aisles, opened/frozen and dismissals aren't stock changes.
- **Seen:** the app sends `scan:<scanId>` to `POST /api/push/seen` once a green check-in or check-out toast is **shown** (one of the three on screen) while the page is visible, live or from catch-up.
- **Batching:** changes join the household's open batch; it's sent `STOCK_PUSH_DELAY_SECONDS` (default 60) after the last change, but no later than `STOCK_PUSH_MAX_WAIT_SECONDS` (default 300) after the first. Each change queues a `{ "batchId": "…" }` message on the API app's `stock-push` queue, hidden until the batch is due; a message for a batch that was pushed back or already sent does nothing.
- **Who:** each person with a device whose `preferences.stockChanges` is `true` gets **one** push for what they missed, on each such device: scanner changes they have **no** seen receipt for (from any of their devices), and app changes made by **someone else**. What's left is **netted per product** (a check-out and its undo cancel out; 2 added and 1 removed is 1 added). Nothing left → nothing.
- **What:** title `Cupboard updated`, body like `Added Milk, Bread, Eggs and 4 more · Removed Hummus` (up to 3 names a side, repeats as `2 × Milk`, an empty side left out), topic and tag `stockChanges` (was `stock`, which Apple refused, #106), opens `/` (or `/?removed=<batchId>` when something was removed, #89), TTL 3 hours.
- **Quiet hours** (21:30–07:30 UK): changes then aren't batched, and a batch falling due then is closed without sending, so night-time changes are never pushed later.
