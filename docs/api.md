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

Not built yet: [Feature] Email alert when the Pi goes offline (#44) adds it. The client model is already in `cupboard_pi.payloads.Heartbeat` (`hubId`, `sentAt`, `clientSha`, `uptimeSeconds`, `usbInputDevices`, optional `scanners: [{deviceId, present}]`); #44 documents it here.
