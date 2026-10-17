# Domain language

The shared vocabulary for Cupboard Tracker. Cards, code, UI copy, commits and comments all use these words for these things.

If you need a term that isn't here, add it as part of the card that introduces it. Terms are alphabetical within each group. Where a term has a type or field in code, the name is given as **In code**.

---

## Products & stock

**Barcode** – the EAN/UPC number printed on the pack. Identifies a product, not a unit.
- Not to be confused with: **Unit**. Many units share one barcode.
- In code: `barcode` (string)

**Display name** – the name the UI and digest show for a product. From product lookup (Open Food Facts), naming or photo identification; `Unknown item (<barcode>)` for a no-match.
- Not to be confused with: the brand and size, which are shown separately.
- In code: `displayName`

**Loose item** – a product with no barcode, such as fruit, bakery bread or deli food. Added from the app, not scanned.
- Not to be confused with: **Store label**. A store-labelled item has a barcode, but one printed in the shop.

**Product** – a kind of item, identified by its own product ID (e.g. "Heinz Baked Beans 415g"). It can have no barcode (a loose item), one barcode, or several.
- Not to be confused with: **Unit**. A product is the kind of thing; a unit is one physical tin.
- In code: `Product`, `productId`

**Product cache** – our stored products and their details, found by barcode through barcode lookups, so we only look each barcode up on Open Food Facts once.

**Stock** – all units currently in the house (checked in and not checked out).

**Stock list** – the UI's first screen, showing current stock grouped by product.

**Stock-take** – a one-off bulk check-in of everything already in the house. Expiry dates for these units are approximate.

**Unit** (inventory item) – one physical item in the house (one tin). Stock is made of units.
- Not to be confused with: **Product**. Three tins of the same beans are one product and three units.
- In code: `Unit` (stored in the inventory items container)

---

## Scanning & devices

**Check-in** – adding a unit to stock: a scan on the `in` device, or a manual add in the UI.
- Not to be confused with: **Scan event**. A scan event is the raw record of a scan; a check-in is the stock change it causes.

**Check-out** – removing a unit from stock: a scan on the `out` device, or the check-out action in the stock list.

**Device** – the logical identity the Pi gives a scanner, and its role.
- In code: `Device`, `deviceId`

**Heartbeat** – a periodic "I'm alive" message from the Pi. The interval is configurable.

**Last seen** – when a device last sent a heartbeat or scan.
- In code: `lastSeen`

**Role** – what a device's scans mean: `in` (check-in) or `out` (check-out).
- In code: `Role`

**Scan event** – a single raw scan sent from the Pi: barcode, deviceId, timestamp. Append-only; never edited or deleted.
- Not to be confused with: **Check-in** / **Check-out**, which are the stock changes a scan event leads to.
- In code: `ScanEvent`

**Scanner** – a physical barcode scanner. There are two of the same model, told apart by a configured prefix/suffix.
- Not to be confused with: **Device**, the logical identity the Pi assigns to a scanner.

**Undo check-out** – reversing a check-out, returning the unit to stock.

**Unmatched check-out** – a check-out scan with no unit in stock to remove: either a barcode we've never seen, or a known product with nothing in stock. Shown on the scanners screen until dismissed. Replaces the older term "no-match check-out".
- Not to be confused with: **No-match**, which is about product lookup, not stock.
- In code: `UnmatchedCheckOut` (stored in the unmatched check-outs container)

---

## Identification

**Barcode lookup** – the stored link from one barcode, exactly as scanned, to its product. Lets a scan find its product with one read, and stops two scans of a new barcode creating two products.
- Not to be confused with: **Product lookup**, which resolves a barcode, including asking Open Food Facts.
- In code: `BarcodeLookup`

**Dismiss** – clearing an unmatched check-out from the scanners screen.

**Lookup failed** – product lookup couldn't reach Open Food Facts (down, rate-limited or timing out). Not a final answer: it's retried until it becomes a match or a no-match, or the product is named. Shown as `Unknown item (<barcode>)` meanwhile.
- Not to be confused with: **No-match**, which is final.
- In code: product status `lookupFailed`

**Match** – a barcode that product lookup resolved to a product.
- In code: product status `known` (with `source: openFoodFacts`)

**Name a product** – manually entering product details for a no-match or lookup-failed product.

**No-match** – a scanned barcode that Open Food Facts doesn't know. Final: it's never looked up again, and needs naming or photo identification.
- In code: product status `noMatch`

**Photo identification** – identifying a no-match product from a photo of the pack: OCR (Document Intelligence) extracts the text, then Jev decides the product from that text.

**Product lookup** – resolving a barcode to product details: product cache first (through its barcode lookup), then Open Food Facts.

**Store label** – a barcode printed in the shop for deli or weighed items: an **EAN-13 starting 20–29**. Not looked up on Open Food Facts. 8-digit barcodes starting with 2 (e.g. Lidl's own-brand) are normal product barcodes, not store labels.

---

## Expiry

**Bought date** – the date a unit was checked in.

**Estimated expiry date** – shelf life plus the bought date (the check-in date). Always an estimate; never scanned or read from the pack.

**Shelf life** – the typical duration a product lasts, estimated by Jev.

---

## Monitoring & notifications

**Daily digest** – the 9pm email summarising current stock and that day's activity.

**Error alert** – an email sent when errors occur in Azure.

**Offline alert** – an email sent when a device's last seen is older than the configured threshold.

---

## Users & UI

**Scan feed** – the live list of toasts in the UI, newest on top, one per scan event.

**Scan feed event** – a live message pushed to every open app about one scan event, keyed by its scan ID: received (grey toast), identified (green), needs identification (yellow), unmatched check-out, or skipped. A hint only; the API is the source of truth.
- Not to be confused with: **Scan event**, the stored record of the scan itself.
- In code: `ScanFeedEvent` (`ScanReceived`, `ScanIdentified`, `ScanNeedsIdentification`, `CheckOutUnmatched`, `ScanSkipped`), sent through `IScanFeedPublisher`

**Scanners screen** – the UI screen showing devices, no-match check-ins and unmatched check-outs.

**Toast states** – loading (grey), needs identification (yellow, tap to open photo identification), identified (green, shows the product name).
