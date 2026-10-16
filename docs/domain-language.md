# Domain language

The shared vocabulary for Cupboard Tracker. Cards, code, UI copy, commits and comments all use these words for these things.

If you need a term that isn't here, add it as part of the card that introduces it. Terms are alphabetical within each group. Where a term has a type or field in code, the name is given as **In code**.

---

## Products & stock

**Barcode** – the EAN/UPC number printed on the pack. Identifies a product, not a unit.
- Not to be confused with: **Unit**. Many units share one barcode.
- In code: `barcode` (string)

**Product** – a kind of item, identified by its barcode (e.g. "Heinz Baked Beans 415g").
- Not to be confused with: **Unit**. A product is the kind of thing; a unit is one physical tin.
- In code: `Product`

**Product cache** – our stored copy of product details, keyed by barcode, so we only look each barcode up once.

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

---

## Identification

**Dismiss** – clearing a no-match check-out from the scanners screen without identifying it.

**Match** – a barcode that product lookup resolved to a product.

**Name a product** – manually entering product details for a no-match barcode.

**No-match** – a scanned barcode that product lookup couldn't resolve. It needs naming or photo identification.

**Photo identification** – identifying a no-match product from a photo of the pack: OCR (Document Intelligence) extracts the text, then Jev decides the product from that text.

**Product lookup** – resolving a barcode to product details: product cache first, then Open Food Facts.

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

**Scanners screen** – the UI screen showing devices and no-match check-ins and check-outs.

**Toast states** – loading (grey), needs identification (yellow, tap to open photo identification), identified (green, shows the product name).
