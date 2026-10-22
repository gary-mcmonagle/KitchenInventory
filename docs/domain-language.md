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

**Add to stock** – putting units in stock from the app rather than a scanner (#58): photograph what you're putting away (**photo add**), pick a loose item, or type a name, then tick and fix the list. Each row is matched to an existing product by name (ignoring case and extra spaces) or becomes a new loose item.
- In the app: the **Add** pill in the bottom nav, the "Add to stock" screen.
- In code: `AddToStock`, `POST /api/stock/add`, `AddScreen`

**Loose item** – a product with no barcode, such as fruit, veg or bakery bread. Added from the app, not scanned.
- Not to be confused with: **Store label**. A store-labelled item has a barcode, but one printed in the shop.
- In code: `Product.IsLoose` (no barcodes), `GET /api/products/loose`

**Photo add** – adding items by photographing them: gpt-4o lists what it sees (several items per photo), and you confirm with checkboxes. The photo is never stored. Brand and size are optional extras; what the item is matters most.
- Not to be confused with: **Photo identification**, which names an unknown barcode.
- In code: `IProductPhotoRecogniser`, `POST /api/photos/recognise`

**Product** – a kind of item, identified by its own product ID (e.g. "Heinz Baked Beans 415g"). It can have no barcode (a loose item), one barcode, or several.
- Not to be confused with: **Unit**. A product is the kind of thing; a unit is one physical tin.
- In code: `Product`, `productId`

**Product cache** – our stored products and their details, found by barcode through barcode lookups, so we only look each barcode up on Open Food Facts once.

**Stock** – all units currently in the house (checked in and not checked out).

**Stock list** – the UI's first screen, showing current stock grouped by product in three sections: **Use first**, **Later** and **Waiting for a name**. Reloads when opened, when you return to the app, and on pull-to-refresh.
- In code: `GetStock`, `GET /api/stock`, `StockScreen`

**Waiting for a name** – the stock list section for in-stock products that have no name yet (a no-match or a lookup failure), each shown as **Unknown item** with its barcode in groups of four and the date it was scanned.
- Not to be confused with: **No-match**, the lookup outcome; a lookup failure also waits for a name until a retry finds it.

**Stock-take** – a one-off bulk check-in of everything already in the house. Expiry dates for these units are approximate.

**Unit** (inventory item) – one physical item in the house (one tin). Stock is made of units.
- Not to be confused with: **Product**. Three tins of the same beans are one product and three units.
- In code: `Unit` (stored in the inventory items container)

---

## Scanning & devices

**Check-in** – adding a unit to stock: a scan on the `in` device, or a manual add in the UI.
- Not to be confused with: **Scan event**. A scan event is the raw record of a scan; a check-in is the stock change it causes.

**Check-out** – removing a unit from stock: a scan on the `out` device, or the check-out action in the stock list. Both take the same unit (soonest estimated expiry, then oldest check-in) and are recorded on it as `checkOutSource` `scanner` or `ui`.
- In code: `CheckOutScan`, `CheckOutFromApp`

**Device** – the logical identity the Pi gives a scanner, and its role.
- In code: `Device`, `deviceId`

**Heartbeat** – a periodic "I'm alive" message from the hub. The interval is configurable (2 minutes by default).
- In code: `Heartbeat` (Pi), `RecordHeartbeat` (ingest), `POST /api/heartbeat`

**Hub** – the Raspberry Pi that the scanners connect to. It sends scan events and heartbeats. Stored with the devices, as `kind: "hub"`.
- Not to be confused with: **Device**, a scanner's logical identity.
- In code: `Hub`, `hubId` (the Pi's `HUB_ID`, e.g. `cupboard-pi`)

**Last seen** – when the server last heard from a device (a scan) or the hub (a heartbeat). Always the server's receive time, never the Pi's clock.
- In code: `lastSeenAt`

**Role** – what a device's scans mean: `in` (check-in) or `out` (check-out).
- In code: `Role`

**Scan ID** – the UUID the hub mints for a scan the moment it reads the barcode. It stays the same when the scan is resent, so ingest stores each scan once.
- Not to be confused with: **Barcode**. Scanning the same tin twice gives two scan IDs and two units.
- In code: `scanId` (wire), the scan event's `id` (stored), `ScanEvent.ScanId`

**Scan event** – a single raw scan sent from the Pi: barcode, deviceId, timestamp. Append-only; never edited or deleted.
- Not to be confused with: **Check-in** / **Check-out**, which are the stock changes a scan event leads to.
- In code: `ScanEvent`

**Dongle** – the USB wireless receiver a scanner is paired to. The Pi sees the dongle (as a USB keyboard), not the scanner. Both dongles are identical (same USB ID, no serial number).

**Scanner** – a physical barcode scanner. There are two of the same model, told apart by the **scanner port** their dongle is plugged into.
- Not to be confused with: **Device**, the logical identity the Pi assigns to a scanner.

**Scan queue** – the Pi's local, durable list of scans waiting to be sent to ingest (SQLite under `/var/lib/cupboard-tracker/`). Every scan is written here before it's sent, and removed only once ingest accepts it, so an outage or a reboot loses nothing.
- In code: `ScanQueue`

**Scanner port** – the Pi USB port a scanner's dongle is plugged into, named by its `/dev/input/by-path` link (e.g. `platform-3f980000.usb-usb-0:1.5:1.0`). The Pi maps each scanner port to a device. Moving a dongle to another port moves its scans to another device.
- In code: `scanner_port` (Pi client config)

**Still got this?** – the question a stock list row asks once a unit reaches its estimated expiry date (#94), with **Still here** and **Gone**. Asked from the estimated day itself; a product row asks about its oldest such unit. The aisle's pill then reads "n to check".

**Still here** – the answer that the unit is still in the house. It records **last confirmed** and stops asking for 2 days (`STILL_HERE_ASK_AGAIN_DAYS`); the date stays as it was. The row shows **Checked** for the rest of the day.

**Last confirmed** – when someone last said a unit is still here (`lastConfirmedAt`, with how: the prompt, or later a stock check by place).

**Stock-check check-out** – **Gone** on a "Still got this?" prompt: that unit is checked out as a correction (`checkOutReason: "stockCheck"`), not as something used just now. It counts as running out (add when we run out) but isn't pushed as a stock change.

**Expired** (for "Still got this?") – an in-stock, named unit whose estimated expiry date is **before** today and that nobody has said is still here lately. A unit on its estimated day asks on its row but isn't expired yet.

**Still got these?** – the sheet listing the expired units, each with Still here and Gone, opened by the **expired check push**: at 18:00 on Tuesdays, Thursdays and Sundays, if anything is expired ("3 expired items are still in stock. Still got these?"). Turned off per phone with the **Still got this?** switch on the "This phone" card.

**Undo check-out** – reversing a check-out, returning the unit to stock with its check-out details cleared. Offered for 6 seconds after a check-out in the stock list (the toast's Undo).
- In code: `UndoCheckOut`, `POST /api/units/{unitId}/undo-check-out`

**Unmatched check-out** – a check-out scan with no unit in stock to remove: either a barcode we've never seen, or a known product with nothing in stock. Shown on the scanners screen until dismissed. Replaces the older term "no-match check-out".
- Not to be confused with: **No-match**, which is about product lookup, not stock.
- In code: `UnmatchedCheckOut` (stored in the unmatched check-outs container)

---

## Identification

**Barcode lookup** – the stored link from one barcode, exactly as scanned, to its product. Lets a scan find its product with one read, and stops two scans of a new barcode creating two products.
- Not to be confused with: **Product lookup**, which resolves a barcode, including asking Open Food Facts.
- In code: `BarcodeLookup`

**Dismiss** – hiding something without changing stock. In the scan feed, dismissing an amber "No match" toast hides it for the whole household (the product stays under Waiting for a name, and a newer check-in shows it again); a "Not in stock" toast is dismissed on that device only. On the scanners screen, dismissing clears an unmatched check-out.

**Lookup failed** – product lookup couldn't reach Open Food Facts (down, rate-limited or timing out). Not a final answer: it's retried until it becomes a match or a no-match, or the product is named. Shown as `Unknown item (<barcode>)` meanwhile.
- Not to be confused with: **No-match**, which is final.
- In code: product status `lookupFailed`

**Match** – a barcode that product lookup resolved to a product.
- In code: product status `known` (with `source: openFoodFacts`)

**Name a product** – manually entering product details for a no-match or lookup-failed product.
- In the app: the **Name it** button on a "Waiting for a name" row opens the "Name this product" screen.
- In code: `NameProduct`, `PUT /api/products/{productId}/name`, `NameScreen`

**No-match** – a scanned barcode that Open Food Facts doesn't know. Final: it's never looked up again, and needs naming or photo identification.
- In code: product status `noMatch`

**Photo identification** – identifying a no-match product from a photo of the pack: OCR (Document Intelligence) extracts the text, then Jev decides the product from that text.

**Product lookup** – resolving a barcode to product details: product cache first (through its barcode lookup), then Open Food Facts.

**Store label** – a barcode printed in the shop for deli or weighed items: an **EAN-13 starting 20–29**. Those are GS1 **Restricted Circulation Numbers** for use "within a geographic region" (GS1 General Specifications change notice GSCN 23-006), so they can't identify a product. Not looked up on Open Food Facts. 8-digit barcodes starting with 2 (e.g. Lidl's own-brand) and 12-digit UPC-As are normal product barcodes, not store labels.
- On check-in: a **store-label check-in** is recorded and the app asks "Store label: what is it?". **Assigning** it (Pick, in "What is this?") puts one unit in stock at the scan time.
- On check-out: an unmatched check-out, "Store label: check it out from the app".
- In code: `StoreLabels.IsStoreLabel`, `StoreLabelCheckIn`, `AssignStoreLabel`

---

## Expiry

**Bought date** – the date a unit was checked in, as a UK calendar date.

**Estimated expiry date** – shelf life plus the bought date (the check-in date). Always an estimate; never scanned or read from the pack. Shown as a badge: "Est. tomorrow", "Est. 4 days", "Est. 2 Nov".

**Item detail** – the screen for one known product, opened from its name in the stock list: each unit in the house with its bought and estimated dates, check-out of a particular unit, correcting a unit's date or the product's shelf life, and editing its details.

**Current storage** – where a unit should be kept now it's been opened, frozen or defrosted (e.g. a tin moves to the fridge once opened). Shown as a hint under the unit: "Keep in the fridge".
- In code: `Unit.CurrentStorage`, `currentStorage`

**Defrosted** – a frozen unit taken out of the freezer. Its estimate becomes the defrost date plus how long the product keeps once defrosted, kept where Jev says (fridge or cupboard).

**Edit details** – correcting a known product's name, brand or size in item detail. Like naming, it makes the name manual, so nothing overwrites it.

**Estimate source** – what a unit's estimated expiry date is from: the shelf life (bought date + shelf life), set by you, or the unit's state (opened, frozen, defrosted). The latest state decides.
- In code: `ExpirySource` (`shelfLife`, `manual`, `opened`, `frozen`, `defrosted`), `expirySource`

**Frozen** – a unit put in the freezer. Its estimate becomes the freeze date plus how long the product keeps in the freezer, and it's checked out last. Shown with a **Frozen** label.

**Aisle** – where a product sits on the stock list, like a supermarket (#71). A fixed list in shop order: Fruit & veg, Bakery, Meat & fish, Dairy & eggs, Chilled & deli, Frozen, Pasta, rice & grains, Tins & jars, Sauces, oils & condiments, Baking & spices, Breakfast & cereal, Snacks & sweets, Drinks, Other. Jev picks each product's aisle once; Gary can change it in Edit details. Aisles start open (closing one is remembered on that device) and list use-first items first.
- In code: `Aisles`, `aisleId`

**Use first view** – the stock list's **Use first** toggle (#71): only the products that need using, grouped by aisle, every aisle open. Off by default, never remembered.

**Animal content** – whether a product is or contains **meat** or **fish**, or is made from them (stock cubes, meat gravy, lard, gelatine, Worcestershire sauce), or **neither** (#112). Cheese, eggs, milk and honey are neither. Fish counts as meat for veg purposes. Worked out once per product (the Meat & fish aisle, then Open Food Facts categories, then Jev), **unknown** when Jev isn't sure, and correctable in Edit details ("Meat / fish / neither").

**Generic name** – a product's shopping-list name (#90): what you'd say to someone going to the shop, at shopper level ("Semi-skimmed milk", "Caster sugar", "Smoked paprika"), shared by every brand and size of the same thing so they count as one item. Jev matches a product to an existing generic name first (in its aisle, then by shared words across aisles); only when none fits does Foundry write a new one. Gary can pick another, or type a new one, in Edit details. On the stock list it's the row's title when it differs from the product's name, with the full name and size underneath (#92); item detail keeps the full name and says "Listed as …".
- Not to be confused with: **Display name**, the product's full name as scanned or named ("SEMI-SKIMMED BRITISH MILK").
- In code: `GenericName`, `ProductGenericName`, `AssignGenericNames`, `genericName` documents

**Product icon** – the picture in a product's tile instead of its first letter (#72): an IconPark Outline icon drawn in the tile's green, picked by Jev from a curated food set, or by Gary in Edit details. When Jev isn't sure, the tile shows the product's aisle icon; the letter is the last resort.
- In code: `ProductIcons`, `ProductTile`, `icon`

**Later** – the stock list section for named products that aren't in Use first, including those with no estimate yet.

**Shelf life** – how long a product usually keeps after it's bought, unopened and in its usual storage. Estimated once per product by Jev; a cautious estimate is used when Jev isn't confident. It can be set by hand in item detail ("set by you"), which applies to units bought from then on and is never overwritten by Jev.

**Opened** – a unit that's been opened. Its estimate becomes the **earlier** of the open date plus how long the product keeps once opened, and the estimate it had: opening never makes something last longer. Opened units are checked out first and shown with an **Opened** label. Marked from a unit's **More** button in item detail, or from the **Opened** button on a stock list row (opens the next one; greyed out while one's open). How long a product keeps once opened can be set by hand in item detail.
- In code: `UnitState.Opened`, `openedOn`, `POST /api/units/{id}/opened`

**Set by you** – a date or shelf life a person set in item detail (`manual`), as opposed to an estimate. Never recalculated.

**Usual storage** – where a product is normally kept before it's opened: cupboard, fridge or freezer. Jev chooses it with the shelf life, and the estimate is for that storage.

**Unit state** – opened, frozen or defrosted: what happened to a unit after it was bought (#65), marked from the **More** button on a unit in item detail, today or on a different day, with Undo. The product's estimates for each state come from Jev once per product, so marking is instant; until they arrive, the new estimate is "on its way".
- In code: `UnitState`, `UnitStates`, `ProductStateEstimates`

**Stock search** – the box at the top of the stock list (#91). It filters the list in the app as you type: every word must be part of a product's name, brand, generic name, aisle name or barcode, ignoring case and accents. Matching aisles show open; the rest are hidden. The search is kept while the app is open, never stored on the device.
- In code: `matchesSearch`, `stockSearch.ts`

**Use first** – the stock list section (and the digest's first section) for products whose soonest unit is estimated to be expired or due within 3 days (`USE_FIRST_DAYS`).

---

## Monitoring & notifications

**Daily digest** – the email sent at 21:00 UK time every day, quiet days included: what to use first (when there's anything), that day's check-ins and check-outs, everything in stock, and unknown items by barcode.
- In code: `SendDailyDigest`, `BuildDailyDigest`, `NotificationKind.DailyDigest`

**Daily digest window** – the period a digest covers: 21:00 UK time the day before to 21:00 UK time on the digest's date (start inclusive, end exclusive). 23 or 25 hours on clock-change days, so nothing is counted twice or missed.
- In code: `DigestWindow`

**Error alert** – an email sent when errors occur in Azure.

**Offline alert** – the one email sent when the hub's last seen is older than the threshold (`OFFLINE_THRESHOLD_MINUTES`, 20 by default). One per outage: the next heartbeat ends the outage, with no "back online" email.
- In code: `CheckHubsOffline`, `offlineAlertSentAt`, `NotificationKind.OfflineAlert`

---

## Users & UI

**Scan feed** – the live list of toasts in the UI, newest on top, one per scan event.

**Scan feed event** – a live message pushed to every open app (Azure SignalR), keyed by scan ID: `scanReceived` (grey toast), `scanIdentified` (green), `scanNeedsIdentification` (amber), `checkOutUnmatched` (amber "Not in stock"), or `scanSkipped`; plus `productNamed` and `feedDismissed`, which remove a product's amber toast everywhere. A hint only: the API is the source of truth, and the app catches up from `GET /api/scan-feed` after every reconnect.

**Catch-up** – the app asking the API what the scan feed missed (`GET /api/scan-feed`), on load, after every reconnect and when it comes back to the foreground. It also brings back amber toasts still waiting for a name.
- Not to be confused with: **Scan event**, the stored record of the scan itself.
- In code: `ScanFeedEvent` (`ScanReceived`, `ScanIdentified`, `ScanNeedsIdentification`, `CheckOutUnmatched`, `ScanSkipped`), sent through `IScanFeedPublisher`

**Scanners screen** – the UI screen answering "is everything working?" and "what needs sorting?": each scanner's receiver presence and the Pi's health, check-ins with no match (to identify), check-outs with no match (to dismiss), and recently checked out (to undo).

**Receiver presence** – whether a scanner's receiver (dongle) is plugged into the Pi, as the Pi reports in its heartbeats: **Connected**, **Not connected** ("Unplugged since …"), or **Unknown** until reported. It says nothing about the scanner itself: the Pi can't tell whether a wireless scanner is switched on or in range.

**Recently checked out** – units checked out in the last 48 hours, from a scanner or the app, listed on the scanners screen so a mistake (e.g. scanning out the wrong thing) can be undone.

**Recipe suggestion** – a recipe the app suggests from what's in stock (#73), for a chosen meal (breakfast, lunch, dinner or snack), optionally built around the use-first items, with any picked ingredients included and the household's one-off comments followed where they fit. Generated by gpt-4o on Foundry and never stored. **Another one** asks for a different recipe with the same choices; **Start again** goes back to the form with the choices kept.
- Not to be confused with: **Use first**, which says what needs using; a recipe suggestion only proposes how.
- In code: `SuggestRecipe`, `IRecipeGenerator`, `RecipeRequest`, `Recipe`, `RecipesScreen`

**Recipe adjustment** – changing a suggested recipe instead of asking for another (#104): **Adjust** opens a sheet to **add** (from the cupboard, or something to buy), **remove** ingredients, and **tweak** ("spicier", "for 4"). gpt-4o returns the same dish updated, with "Adjusted: + Chorizo · − Onion · "spicier"" under the meta and **Undo adjustment**, which steps back one version at a time. Another one and Start again drop the adjustments.
- In code: `AdjustRecipe`, `RecipeChanges`, `AdjustAsync`, `AdjustSheet`, `AdjustmentSummary`

**Adjusted idea** – a Ready now idea changed in place with its card's **Adjust** button (#108): the same Adjust sheet as a recipe ("Adjust this idea"; Remove lists what it uses; room for one thing to buy). It stays a quick idea (toaster, microwave, kettle or no cooking, possibly a different one of those), shows "Adjusted: …" with **Undo** at the top of the card, and tags what it added. Each card keeps its own versions.

**Suggested addition** – one of up to 3 in-stock things (by generic name) the Adjust sheet offers as chips: Jev's pick of what would improve the recipe most, with use-first items nudged ahead of close ones (#104). It's only a suggestion until tapped.
- In code: `SuggestRecipeAdditions`, `IRecipeAdditionRanker`, `JevRecipeAdditionRanker`, `RecipeAddition`

**Hungry?** – the tab and screen for ideas of what to eat from what's in stock (#88; called Recipes until then). It asks **Cook something** (a recipe suggestion) or **Ready now**, and which meal: breakfast, lunch, dinner or **snack**.
- In code: `RecipesScreen` (route `/recipes`), `RecipeMode`

**Ready now** – 3 to 5 quick ideas from what's in stock that need only a toaster, microwave or kettle, or no cooking, and take about 10 minutes (#88). **More ideas** asks for different ones; each idea says which **appliance** it needs.
- In code: `RecipeMode.ReadyNow`, `QuickIdea`, `SuggestQuickIdeasAsync`, `IdeasView`

**Appliance** – the only kit a Ready now idea may use: toaster, microwave, kettle, air fryer (#111), or none ("No cooking").

**Equipment** – the kitchen kit a Cook something recipe may use (#111), chosen with the **What can you use?** chips: Hob, Oven & grill, Microwave, Toaster, Air fryer, Slow cooker, Rice cooker, Soup maker, Blender. All ticked each time the form opens; nothing is remembered. A kettle is always fine. The recipe's meta line ends with the kit it uses; if its method still mentions something unticked after one retry, it shows **Check before cooking**.
- In code: `Appliance`

**Basic staples** – the few things a recipe suggestion may assume without them being in stock: salt, pepper, cooking oil and water. Anything else not in stock is listed under "You'll also need" (at most 3).

**Meat or veg?** – the Hungry? switch for who's eating (#110): **Meat & veg** (anything goes), **Veggie** (no meat or fish, and nothing cooked with them) or **Half & half** (Cook something only). Meat & veg each time the form opens. In code, `Diet` (`any`, `veg`, `half`).

**Veggie** – vegetarian as Hungry? means it: no meat or fish, and nothing made from them (stock cubes, gravy, lard, gelatine, Worcestershire sauce, fish sauce); cheese, eggs, milk and honey are fine. See **Animal content**.

**Half & half** – one meal where some plates are veggie, cooked alongside the meat (#110). The recipe says how many plates of each ("Serves 2 (1 meat, 1 veg)"), and tags anything for one side only with a **plate tag**.

**Plate tag** – **Meat plates** or **Veggie plates** on a half & half recipe's ingredient, extra item or method step (tinted steps); untagged means everyone. In code, `PlateFor` (`all`, `meat`, `veg`).

**Saved recipe** – a recipe or Ready now idea someone liked and kept (#105), in the household's one shared collection: a **snapshot** of what was shown (nothing is regenerated), with a name anyone can change and a shared **note**. Found in the **Saved** box at the top of Hungry?, newest first, filtered by meal, cook time and words in the name. **Remove from saved** has Undo.

**Stock status (of a saved recipe)** – whether each ingredient is in today's stock: **have** (the same product, or any product with the same generic name, or a matching name), **use first** (had, and due soon), or **missing**. Basic staples always count as had. The recipe shows "You've got everything" (with "Uses up …") or "Missing: …", and **Add missing to the list** puts the missing ones on the shopping list by generic name.

**Push notification** – a message that appears on a household member's phone or computer from Cupboard Tracker, even when the app is closed (#77). Only on devices where they turned notifications on; on iPhone, only in the app added to the Home Screen.
- In code: `SendPush`, `PushMessage`, `IPushSender`

**Push subscription** – one device's permission and address for push notifications, stored per person and device, with which notification types it wants (preferences). Removed when the device turns notifications off, when the push service says it's gone, or after 30 days without the app being opened.
- In code: `PushSubscription`, `pushSubscriptions` container

**Shopping list** – the household's one shared list of things to buy (#84), on the List tab. Items are added by **generic name**; anyone can tick them off, and every open phone sees changes within a couple of seconds.
- In code: `ShoppingListItem`, `AddToShoppingList`, `ListScreen`, `useShoppingList`, `shoppingList` container

**Add from an empty search** – when a stock search finds nothing, the stock list offers **Add "<what you typed>" to the shopping list** (#97): one tap puts it on the list (`source: stockSearch`), named as typed with a capital first letter. Not offered for barcodes (digits only) or a single character, and it says so when the name is already on the list.

**Outbox** – the changes to the shopping list made on a phone but not yet sent (#85): each add, tick, put-back, removal and Clear ticked, with its own change ID and the time it was made, kept in order on the phone (IndexedDB, with the list's last copy). Rows with a change waiting show **Not synced** while offline.
- In code: `OutboxEntry`, `listStore.ts`, `ShoppingList.waiting`

**Sync** – sending the outbox, oldest first, through the list's normal idempotent endpoints, then catching up with `since`. Happens on open, when the connection comes back, on returning to the app, after each change, and every 30 s while something waits. Shown as **Offline · n changes waiting to sync**, then **Syncing…**, then a brief **Synced**; **Sign in to sync your changes** if the sign-in has expired (the outbox is kept).
- In code: `ShoppingListProvider` (`flush`), `SyncBanner`

**App shell** – the app's own files (the page, scripts, styles, fonts, icons) that the service worker keeps so the installed app opens with no signal (#85). One cache per deploy (`cupboard-shell-<version>`).
- In code: `FE/public/sw.js`, `FE/sw-shell.mjs`

**List aisle** – the aisle a shopping list item is grouped under with **Sort by aisle**: its product's aisle, or Jev's answer for a typed item (shown as "Finding its aisle…" until it arrives), remembered for that name.
- In code: `ShoppingListItem.Aisle`, `AssignListAisle`, `ListMemory`

**Seen receipt** – a note from the app that a person saw an event (e.g. a scan's toast) on a visible screen, so a push about it leaves them out. Kept for about a day. For stock changes, a green check-in or check-out toast counts once it's shown on a visible page; nothing else does.
- In code: `RecordPushSeen`, `markSeen`, `pushSeen` container

**Run out** – the house has none of something left (#89): no in-stock units across **every product with the same generic name** (the Asda and the Tesco semi-skimmed milk count together). A product without a generic name yet counts by its display name. Any check-out can cause it, including a stock-check one.
- In code: `RunOutGroups`, `RunOutGroup`

**Add when we run out** – the setting on a generic name (#89) that puts it on the shopping list, with the **Auto** tag ("Added when the last one was used"), when the house runs out. Opt-in, household-wide, and it belongs to the name, so it covers every brand. Turned on in item detail, or with **Yes, always** on the "Add to the shopping list?" sheet. If a product's generic name is changed by hand, it follows the setting of its new name. Undoing the check-out takes the automatic item off again, unless someone has ticked it or added a note.
- In code: `GenericName.AddWhenOut`, `AddWhenRunOut`, `SetAddWhenOut`, `RunOutCard`, `source: "autoRunOut"`

**"Add to the shopping list?" sheet** – the bottom sheet after a check-out (#89), opened by tapping a stock change push that says something was removed, or a green "Checked out" toast. One row per removed generic name with how many are left, **Add** or "On the list", then the offer to do it automatically.
- In code: `RunOutSheet`, `GetRunOutChoices`, `/?removed=<batchId>`

**List push** – one "Shopping list" push telling a person what others have added to the shopping list since they last looked (#86): sent 3 minutes after someone stops adding (at most 15 after they started), at most one per person every 30 minutes, and replacing the previous one. Only adds by a person start one (not "Add when we run out"). Turned on or off per device ("Shopping list" on the "This phone" card).

**Last viewed (the List)** – when a person last had the List on screen (`listLastViewedAt`, server time), told by the app when the List opens, when the app comes back to it, and at most once a minute while live changes arrive. A list push only mentions items added after it.

**Stock change push** – one "Cupboard updated" push saying what was added or removed (#80), sent to each person who didn't see those changes' green toasts in the app. Turned on or off per device ("Stock changes" on the "This phone" card).
- In code: `SendStockChangePush`, `AddStockChangeToPush`, `StockChange`, preference `stockChanges`

**App stock change** – a stock change made in the app rather than at a scanner (#81): a check-out, an add from the Add tab, a store label picked, or an undone check-out. It joins the same batch, and everyone but the person who made it hears about it.
- In code: `StockChange.ActorId`, `StockChangeKeys.ForApp`

**Netting** – before a stock change push is written, each person's changes are summed per product: a check-out and its undo cancel out, so a mistake fixed straight away isn't pushed (#81).
- In code: `SendStockChangePush.Net`

**Batch (stock change batch)** – the household's stock changes waiting to be pushed together, so a big shop is one push: sent a minute after the last change, at most five minutes after the first.
- In code: `StockChangeBatch`, `IStockChangeBatchRepository`, the `stock-push` queue

**This phone** – the card on the Scanners screen where a person turns notifications on or off for the device they're holding, chooses what it gets, and sends a test.
- In code: `ThisPhoneCard`, `phoneNotifications`

**Quiet hours** – 21:30 to 07:30 UK time, when no pushes are sent.
- In code: `QuietHours`

**Toast states** – loading (grey), needs identification (yellow, tap to open photo identification), identified (green, shows the product name).
