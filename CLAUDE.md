# Cupboard Tracker

Home food inventory tracker. Two barcode scanners (check-in / check-out) feed a Raspberry Pi, which sends scan events to Azure. A phone-first React app shows current stock.

This file holds **technical detail and process rules**. The *what* of each piece of work lives on the Trello cards — they are written to be self-contained. If a card and this file disagree, stop and ask in a card comment.

Use the terms defined in `docs/domain-language.md` in code, comments, commits and card comments. If you need a term that isn't defined there, add it as part of your work.

---

## Architecture

| Component | Tech | Notes |
|---|---|---|
| Pi client | Python, Raspberry Pi 3 (headless) | Lean thin client. Reads both scanners, maps each to a logical `deviceId`, POSTs scan events + periodic heartbeat to the ingest Function. Auth header name/value come from env vars and are abstracted in the client. Deployed by `deploy/pi.sh` copying a built wheel over SSH (#33), not git pull. |
| Ingest Function | .NET Azure Functions | HTTP, function-key auth. Writes append-only scan events to Cosmos. |
| API / BFF Function | .NET Azure Functions | Entra ID bearer-token auth from the UI. Also hosts change-feed processing and timer functions unless a card says otherwise. |
| UI | React on Azure Static Web Apps (free tier) | Phone-first, calm and clean. Entra ID sign-in. Live scan feed via Azure SignalR (free tier). |
| Data | Cosmos DB **serverless** | Containers: scan events (append-only), products (each with its own `productId`; barcodes are optional lookups, so loose items work), inventory items (one per physical unit, partitioned by product), devices (role: in / out; and the hub), unmatched check-outs, sent digests (one per day), push subscriptions and receipts, the shopping list (#84), plus the change-feed leases container. |
| External | Open Food Facts, Azure Document Intelligence (Read), Jev (TypeSafe AI System One), Azure Communication Services Email | Product lookup → OCR for photo ID → Jev for decisions over extracted text and estimated expiry dates. Email (offline alert, later the digest) through ACS on an Azure-managed domain, behind `INotifier` (spike #39; ACS Email retires 30 Sep 2028). |
| Infra | Bicep | Single environment. Follow Bicep best practices (modules, parameters, no hard-coded names/secrets). |

The Cosmos data model (containers, partition keys, change-feed flow, idempotency) is documented in [`docs/data-model.md`](docs/data-model.md). The HTTP contracts (request and response shapes, validation rules, status codes) are in [`docs/api.md`](docs/api.md); the Pi's payload models (`cupboard_pi.payloads`) must stay in step with it. Follow it for document shapes; if a card conflicts with it, ask in a card comment.

### Code conventions

- Both Function apps live in **one .NET solution** so types stay aligned; assume they are always deployed together.
- **Clean architecture.** Domain and application layers have no dependency on Azure SDKs, HTTP clients or third-party types. Every external service (Cosmos, Open Food Facts, Jev, Document Intelligence, email, SignalR) sits behind an interface with its implementation in the infrastructure layer.
- Every behaviour change ships with **unit tests**. Tests must pass before merging.
- Configuration via app settings / env vars. **Secrets** (only true third-party keys; Azure services use managed identities) live in Key Vault and reach the code as ordinary app settings through Key Vault references (see [Secrets](#secrets-key-vault-42)).
- **.NET 10 (LTS)**, Azure Functions **isolated worker** (Functions host v4), on the Flex Consumption plan. Everything .NET lives in `BE/`: pinned in `BE/global.json`, shared build settings in `BE/Directory.Build.props` (nullable, warnings as errors, recommended analysers); package versions only in `BE/Directory.Packages.props` (central package management).
- **Local scanner (Pi client), in `LocalScanner/`: Python 3.11+**, standard library only at runtime (lean thin client); `pytest` and `ruff` for development. The simulator (card #66) imports the `cupboard_pi` package's ingest client and payload models, so keep them free of Pi-only dependencies. The shared ingest contract lives in `cupboard_pi.payloads` (scan event, heartbeat, barcode validation) and `cupboard_pi.identity` (`HUB_ID`, `SCANNERS=deviceId=in|out[@<scanner port>],...`; the Pi needs the scanner port on every entry, the simulator ignores it). The Pi client reads the scanners (`cupboard_pi.scanners`), writes each valid scan to a SQLite **scan queue** (`cupboard_pi.scan_queue`) and a separate sender posts it to ingest (`cupboard_pi.sender`), so reading never waits on the network. A third thread (`cupboard_pi.heartbeat`) sends a heartbeat on startup and every `HEARTBEAT_INTERVAL_SECONDS` (default 120) with scanner presence; failures are dropped, not queued.

### Build and test

```bash
# Backend (.NET), from BE/
cd BE
dotnet build                      # 0 warnings expected: warnings are errors
dotnet test                       # xUnit v3 on Microsoft.Testing.Platform (opted in via BE/global.json)

# Run a Function app locally (needs Azure Functions Core Tools v4), from BE/
cp src/CupboardTracker.Functions.Api/local.settings.example.json src/CupboardTracker.Functions.Api/local.settings.json
cd src/CupboardTracker.Functions.Api && func start   # GET http://localhost:7071/api/ping

# Local scanner (Pi client), from LocalScanner/
cd LocalScanner
python3.11 -m venv .venv && . .venv/bin/activate && pip install -e '.[dev]'
pytest && ruff check . && ruff format --check .
```

`local.settings.json` is git-ignored; only the `.example.json` files are committed.

### The repo is public

- **Never commit secrets**: function keys, connection strings, API keys (Jev, Document Intelligence), tokens, `.env` files, `local.settings.json`. Check `git diff --staged` before every commit.
- If a secret is ever committed, stop, do not push, and comment on the card.

### UI mockups

The agreed screen designs live on the **Cupboard Tracker UI** design canvas: https://claude.ai/code/artifact/590d2d48-3f59-4821-92a2-39950d1204e2

Read them with the Artifact tool (`read`, with `path` set to one artboard file). They aren't attached to Trello.

| Artboard file | Screen |
|---|---|
| `project/Main.dc.html` | Stock list (first screen): search, filter chips, scan feed, sections, check-out with undo, bottom nav |
| `project/Item.dc.html` | Item detail |
| `project/Scanners.dc.html` | Scanners (devices, no-match check-ins and check-outs) |
| `project/Capture.dc.html` | Photo of the pack |
| `project/NameIt.dc.html` | Name a product |

- The mockups show the **finished** screens. Build only what your card's scope covers, and match the mockup's look for that part (font, colours, spacing, wording, components). Don't build mocked-up parts that belong to other cards.
- The look: **Figtree** (Google Fonts); ground `#F4F7F3`, ink `#1C2A24`, muted text `#5B6B63`, borders `#D5DED8` / `#E1E8E3`, accent green `#2F6B4F` (pressed/dark `#1F5238`, tint `#E4F0E9`), amber warning `#FFE9C7` / `#7A4300`, red `#F9E3E1` / `#8E1F17`. 14–16 px corner radii on cards and inputs, 44 px minimum touch targets. Take exact values from the artboards rather than this summary if they differ.
- The mockup's sample data (products, dates, barcodes) is illustrative. Never ship it.
- If a card and the mockup disagree, the card wins for scope, and the mockup wins for look and wording. Raise anything unclear in a card comment.

---

## Trello

Board: **Cupboard Tracker**

| List | Meaning |
|---|---|
| Gathering Requirements | Waiting on Gary: a decision, an input (barcodes, photos, a key) or a physical step. **Never pick up work from here.** Gary moves cards to Backlog when ready. |
| Backlog | Refined work ready to pick up, in priority order (top first). |
| In Progress | Being worked on, or sent back from verification. |
| In Verification | Deployed; waiting to be tested against the live system. |
| Done | Verified. |

### Card types

Every card title starts with its type:

- **[Spike]** — solves and documents an unknown. Output is a write-up in `docs/spikes/<card-number>-<slug>.md` with the question, findings, recommendation and any follow-up cards *suggested* (not created).
- **[Feature]** — a small vertical slice of user-visible value, working end to end.
- **[Task]** — enabling work that a feature needs but which has no value by itself. The description names the feature(s) it enables.

### What a card contains

Cards should include: context, acceptance criteria, affected component(s), how to verify (or "No verification needed" with a reason), and any links to docs.

**Spike or Gathering Requirements?** A technical unknown you can investigate yourself is a Spike. An unknown only Gary can resolve (a decision, an input, a physical action) belongs in Gathering Requirements.

If a card is missing something only Gary can provide, **don't guess**. Comment with exactly what you need and move it to **Gathering Requirements**, then tell the user. If a dependency card isn't in Done yet, leave the card in Backlog, skip it, and take the next one down.

**Do not create, rename or archive cards** unless the user asks. Suggest new cards in a comment instead.

---

## Workflow

When asked to pick up work, choose **one** card in this order:

1. **In Verification** — top card. Verify it (see below).
2. **In Progress** — a card sent back from verification. Read the failure comment first.
3. **Backlog** — the top card whose **Depends on** cards are all in Done.

Never pick from Gathering Requirements. Tell the user which card you picked and why before starting, and list any Backlog cards you skipped as blocked.

### Implementing (Backlog / In Progress)

1. Move the card to **In Progress** and comment that you've started.
2. Create a branch from up-to-date `main`: `<type>/<card-number>-<short-slug>` (e.g. `task/25-domain-language-doc`). The card number is the number in the card's URL.
3. Do the work, meeting every acceptance criterion. Add or update unit tests.
4. Run the full build and test suite. All must pass.
5. Commit with messages that reference the card (`#25: ...`). Merge into `main` (no PR) and push.
6. Deploy the affected component(s) using that component's deployment script (see Deployment). If the component has no automated deployment yet, **don't deploy by hand** — comment that a deployment task is needed and stop.
7. Comment on the card with:
   - commit SHA(s) and branch
   - what was deployed, where, and when
   - anything the verifier should know (test data, known limitations)
8. Move the card:
   - to **In Verification**, or
   - straight to **Done** if the card says verification isn't needed — and comment why it wasn't needed.

### Verifying (In Verification)

1. Read the card's acceptance criteria and verification steps, plus the implementation comment.
2. Test against the **deployed** system, not local code. Exercise each acceptance criterion.
3. **Verification runs against live household data.** Anything you change to test it (a check-out, a name, a dismissal) must be undone before you finish, unless Gary did it for real. Record what you changed and how you restored it in your comment. **Never type a password or other credential** (password, one-time code, temporary access pass, key or token) into any sign-in page or script. The test user signs in **passwordless**: if its session has expired, enter its **username only**, then ask Gary to approve on his phone, giving him the number shown on screen. If a password field appears, stop and ask Gary.
4. Comment with evidence: the steps you ran, relevant log excerpts (Application Insights / Function logs / Pi logs), and screenshots of the UI where the Trello tools allow attaching them — otherwise describe what you saw.
5. Outcome:
   - **All criteria pass** → move to **Done**.
   - **Anything fails** → move back to **In Progress** with a comment saying exactly what failed, how to reproduce it, and the evidence.

Spikes are verified by checking the write-up answers the card's question and its recommendation is clear.

---

## Deployment

There is **one environment, and it's the live household system**. Treat it with care.

- Each component gets its own deployment script under `deploy/` once its deployment task is done. Document each script here as it lands:
  - **Infra (Bicep): `deploy/infra.sh [--yes] [--what-if-only]`.** Deploys `infra/main.bicep` with `infra/main.bicepparam` at subscription scope (it creates the resource group). It shows the signed-in subscription, prints a **what-if**, and deploys only after confirmation (`--yes` skips the prompt; use it only when Gary has approved the what-if). Safe to re-run: it reads each Function app's live `COMMIT_SHA`, `OFF_CONTACT_EMAIL` (the Open Food Facts User-Agent contact), `ALERT_EMAIL_TO` (who gets the offline alert, #44) and `DIGEST_EMAIL_TO` (who gets the 9pm digest, #46) and passes them back, so infra never resets them. All three addresses are Gary's and are **never committed**: on the first deploy the script takes them from the environment, or asks. `OFFLINE_THRESHOLD_MINUTES` comes from the template (default 20), so a re-run **does** reset a temporary change to it. A re-run with no template changes leaves the live resources **identical** (checked on #31 by snapshotting the group before and after). **What-if still lists some "Modify" entries that are false positives**, not real changes: app settings (what-if can't read them, so they always show as "Create"), role assignments' `principalId` (a managed identity's ID can't be resolved during what-if), the action group's `emailAddress` (a secure parameter, shown as `[parameters('alertEmailTo')]`), and read-only or server-defaulted properties (e.g. Cosmos `sqlEndpoint`, storage blob-service defaults, App Insights `Flow_Type`). Look for anything else. Prints the outputs (app names, hostnames, Cosmos endpoint, App Insights name).
  - **Function apps: `deploy/functions.sh [--force]`.** Deploys **both** apps together from a clean, pushed `main` (or a checkout of exactly `origin/main`'s commit); `--force` overrides those checks and is for emergencies only. It runs `dotnet test` (stops on failure), publishes both apps in Release, zip-deploys them to the apps named in the infra deployment's outputs, sets `COMMIT_SHA` to the short commit, then smoke-checks `/api/ping` on both (ingest with its function key, fetched at runtime and passed to curl on stdin: never echoed, logged or written to disk), retrying for ~2 minutes. It prints the apps, the SHA and the smoke result, and exits non-zero if anything fails.
  - **Entra registrations: `deploy/entra.sh [--swa-host <hostname>] [--yes]`** (#36). Deploys `infra/entra.bicep` (Microsoft Graph Bicep extension: the SPA and API registrations and their service principals) as deployment `cupboard-entra` in `rg-cupboard-tracker`. **Run as `cupboard-deployer` from its own CLI profile, never Gary's**: `AZURE_CONFIG_DIR=~/.azure-cupboard-deployer az login --tenant <tenantId>` (interactive, MFA; Gary does this), then `AZURE_CONFIG_DIR=~/.azure-cupboard-deployer deploy/entra.sh`. The script refuses any other account or profile. Safe to re-run (registrations are matched by `uniqueName`). Only needed when the registrations change (e.g. adding the Static Web App's redirect URI). **`--swa-host` replaces the redirect list, so pass every hostname each time:** `--swa-host icy-smoke-0a456d503.6.azurestaticapps.net --swa-host kitchen.garymcmonagle.com` (Gary added the custom domain by hand in the portal, 2026-10-18; a run without it would remove it). Afterwards run `deploy/infra.sh`, which reads the client IDs from the `cupboard-entra` deployment's outputs into the API app settings.
  - **UI: `deploy/ui.sh [--force]`** (#37). From a clean, pushed `main` (as `functions.sh`), it runs `npm ci`, lint and tests in `FE/`, builds with config read from the deployments (API hostname and Static Web App from `cupboard-tracker`; tenant, SPA client ID and API scope from `cupboard-entra`), writes `version.json` with the commit, deploys to the Static Web App with the SWA CLI (deployment token fetched at runtime into the CLI's environment, never shown), and smoke-checks that the site serves this commit's `version.json` and the page loads.
  - **Clear the live data: `deploy/reset-data.sh`** (Gary runs it; irreversible). Deletes the `scanEvents`, `products`, `inventoryItems`, `unmatchedCheckOuts`, `sentDigests` and `leases` containers, runs `deploy/infra.sh` to recreate them empty, and restarts both Function apps. Keeps `devices` (scanner roles, the hub). Asks you to type the resource group's name first. Used before the initial stock take (2026-10-18) and for the pre-release wipe.
  - Pi setup (one-off, root, run by Gary): `deploy/pi-setup.sh`.
  - **Pi client: `deploy/pi.sh [--force] [--rollback]`** (#33). From a clean, pushed `main` (as `functions.sh`), it runs the `LocalScanner` tests, builds a wheel from a clean copy, and `rsync`s it over SSH with the systemd unit and a `DEPLOYED_SHA` file to `/opt/cupboard-tracker/releases/<sha>/`, where it gets its own venv. It then checks the unit, the sudoers rule and `pi.env` are in place, points `current` at the new release (atomic symlink swap; `previous` points at the old one), restarts `cupboard-pi`, and smoke-checks that this start logged `cupboard-pi starting: sha=<sha>` and is still running 10 s later. **If the check fails, it puts the previous release back** and exits non-zero with the journal. It keeps the newest 3 releases. `--rollback` swaps `current` and `previous` and restarts. `PI_HOST` overrides the SSH target.
    - **No password:** the deploy's only root commands are `systemctl restart cupboard-pi.service` and `test -f /etc/cupboard-tracker/pi.env`, allowed by `LocalScanner/deploy/cupboard-pi.sudoers`. **Installing the unit and that rule is Gary's** (a unit the deploy user could write would run anything as root): when the unit is missing or changed, `pi.sh` stops before switching and prints `ssh -t gary@cupboard-pi.local sudo bash /opt/cupboard-tracker/releases/<sha>/install-service.sh` for Gary to run, then you re-run `pi.sh`.
    - **It never reads, writes or prints `pi.env`.** If it's missing, it stops and lists the variable names from `LocalScanner/.env.example` for Gary to add.
- Never delete or overwrite Cosmos data, containers or resources outside what a card asks for. Anything destructive or irreversible: stop and ask in a card comment.

### Secrets (Key Vault, #42)

- **Vault:** `kv-cupboard-<suffix>` (output `keyVaultName`): RBAC only (no access policies), soft delete (90 days) and **purge protection** (a deleted vault or secret can't be purged early, so the name stays taken for 90 days). Templates never contain secret values; **Gary sets them by hand** in the portal (no script, Gary 2026-10-18).
- **Secrets (names only):**

  | Secret | App setting | Read by | Used for |
  |---|---|---|---|
  | `jev-api-key` | `JEV_API_KEY` | API app | Jev (TypeSafe System One): shelf life (#52) |
  | `vapid-private-key` | `VAPID_PRIVATE_KEY` | API app | Signing push notifications (#77). Made by `deploy/vapid-keys.sh` (the private key goes straight to the clipboard, never shown) |

- **Access:** the API app's managed identity has **Key Vault Secrets User** on the vault (ingest has no access: it needs no secret). Whoever runs `deploy/infra.sh` (Gary) gets **Key Vault Secrets Officer** (`VAULT_OFFICER_PRINCIPAL_ID` overrides it), so he can set them.
- **How code gets them:** the app setting is a Key Vault reference, `@Microsoft.KeyVault(VaultName=…;SecretName=…)` with no version, so it follows the latest version. Code reads `JEV_API_KEY` like any setting. Until the secret exists, the setting holds the reference text itself; code must treat that as "not configured".
- **Set or rotate:** Gary adds a new version in the portal (Key vaults → `kv-cupboard-<suffix>` → Secrets → `jev-api-key`). The app picks it up within 24 hours, or at once on its next restart or deploy (`deploy/functions.sh`). Check: `az rest --method get --url "https://management.azure.com<api app id>/config/configreferences/appsettings/JEV_API_KEY?api-version=2022-03-01" --query properties.status` should say `Resolved`.
- **Rules:** secret values never go in the repo, Trello, logs, command arguments or chat. The agent never types, prints or sets one. Locally the Jev key lives only in Gary's git-ignored `local.env` (for spike scripts).

### Live scan feed (SignalR, #54)

- **Resource:** `sigr-cupboard-<suffix>` (output `signalRName`): Azure SignalR **Free_F1**, **Serverless**, **local auth disabled** (no keys or connection strings), CORS for the Static Web App and `http://localhost:5173`. Both Function apps have **SignalR Service Owner** through their managed identities.
- **App settings (both apps):** `AzureSignalRConnectionString__serviceUri`, `AzureSignalRConnectionString__credential=managedidentity`, `AzureSignalRServiceTransportType=Transient`. Transient means REST calls with no server connections, so only phones and browsers count against the Free tier's 20 connections and 20,000 messages a day.
- **Shopping list (#84):** the same hub carries `shoppingListItemChanged`, `shoppingListItemRemoved` and `shoppingListCleared`; the app shares **one** connection per open app between the feed and the list (`signalRConnection` is reference-counted), so a phone counts once against the 20.
- **How it works:** ingest sends `scanReceived` once a scan is stored. The change-feed processor sends the outcome (`scanIdentified`, `scanNeedsIdentification`, `checkOutUnmatched`, `scanSkipped`). Naming sends `productNamed`, and dismissing sends `feedDismissed`. Sending is in `SignalRScanFeed` (Infrastructure, SignalR Management SDK) behind `IScanFeedPublisher`, and always goes through `ScanFeedNotifier`, which is **best-effort**: a failure is a **warning** and never fails a scan. The UI connects through `POST /api/negotiate` (bearer token) and catches up from `GET /api/scan-feed` after every (re)connect. Contract: `docs/api.md` (Live scan feed).
- **Check it's sending:** `deploy/verify/logs.sh ingest` (or `api`) shows `Scan feed: sent <event> for scan <id>`, or a warning when it couldn't.
- **Local development:** without SignalR settings, publishing does nothing, and the UI still fills the feed from catch-up. To try the live feed locally, use the **SignalR emulator**, so local scans stay off the live feed: `dotnet tool install -g Microsoft.Azure.SignalR.Emulator`, `asrs-emulator start`, then put its connection string in `AzureSignalRConnectionString` in your git-ignored `local.settings.json` for both apps. Never point a local app at the live SignalR resource.

### Push notifications (#77, spike #75)

- **Sending:** our own Web Push sender in the API app (`WebPushSender`, Infrastructure): `aes128gcm` (RFC 8291, a unit test reproduces its test vector) and VAPID (RFC 8292), in-box crypto. **Not** Notification Hubs (no iOS web push, keys only) and **not** the `WebPush` NuGet package (old `aesgcm`, which Apple rejects).
- **Apple's rules** (Google accepts what Apple rejects, so test on an iPhone): the VAPID subject is a real URL (`PUSH_APP_ORIGIN`, `https://kitchen.garymcmonagle.com`; a fake `mailto:` gets 403 `BadJwtToken`); `Topic` is **letters and digits only**, at most 32, and **never 1, 5, 9, 13… characters long**: Apple reads it as Base64, and no Base64 string has a length with remainder 1 when divided by 4, so `stock` got 400 `BadWebPushTopic` (#106). `PushTopic.IsValid` enforces it; **every push must show a notification** (iOS revokes silent ones). On iPhone, push only works in the app added to the Home Screen.
- **Keys:** `VAPID_PUBLIC_KEY` from `vapidPublicKey` in `infra/main.bicepparam` (not secret); the private key only in Key Vault (`vapid-private-key`, made by `deploy/vapid-keys.sh`). Until both are set, nothing is sent and `/api/push/test` says 503. Rotating them makes every device turn notifications on again.
- **Storage:** `pushSubscriptions` (per person and device; pruned on 404/410 and after 30 days unseen by a daily timer) and `pushSeen` (seen receipts and job claims, each with a `ttl`). Delayed pushes are messages on the API app's `push-jobs` Storage queue (managed identity, Storage Queue Data Contributor), run once by the `pushJob` queue trigger.
- **Allowed push services only:** Apple, Google FCM, Mozilla and Microsoft WNS endpoints; anything else is refused.
- **Quiet hours:** 21:30–07:30 UK (`QuietHours`), for features to use.
- **Stock change pushes (#80):** scanner check-ins and check-outs (green toasts) join one open batch (a `batch:stock` document in `pushSeen`), sent a minute after the last change and at most five after the first (`STOCK_PUSH_DELAY_SECONDS`, `STOCK_PUSH_MAX_WAIT_SECONDS`; defaults in code, no app setting needed). Each change queues a message on the `stock-push` queue (created on first use) for the `stockPush` trigger. Each person opted in (`preferences.stockChanges`) gets one "Cupboard updated" push for what they didn't see as a toast, plus app changes (#81: check-outs, adds, store labels, undos) made by someone else, netted per product. Turned on per device on the Scanners screen's **This phone** card. Logs: `Stock change push: …`. When something was removed, the push opens `/?removed=<batchId>`: the "Add to the shopping list?" sheet (#89), from a copy of the sent batch kept 3 days (`batchSent:<batchId>`).
- **Add when we run out (#89):** `AddWhenRunOut` runs after every check-out (scanner, stock list, item detail) and every undo, best-effort. It reads `addWhenOut` on the generic name record and counts in-stock units across every product with that generic name. Logs: `Run out: …`. No new container, setting or infra.
- **Testing:** Claude Code's built-in browser can't do push. Use `playwright-core` with the Mac's Google Chrome and a **throwaway profile** (never Gary's), grant notifications, subscribe, and read `registration.getNotifications()`. iPhone checks need Gary.
- **Check it's sending:** `deploy/verify/logs.sh api` shows `Push <topic>: n targeted, n delivered, …`. Refusals are logged at Error with the push service's reason (e.g. `BadJwtToken`), which fires the error alert.

### Error alert ("Cupboard Tracker errors", #48)

Azure Monitor emails `ALERT_EMAIL_TO` when either Function app has an **exception**, a trace at **Error or above**, or a **5xx** request. 4xx validation failures and warnings don't count, so log real failures that need a person at Error, and anything retried or expected at Warning or below. It's in Bicep (`infra/modules/errorAlert.bicep`): the log search rule `alert-cupboard-errors` on the Log Analytics workspace, and the action group `ag-cupboard-alerts`. No app code is involved.

- **When:** every 15 minutes over the last 15 minutes; fires on 1 or more. Allow up to about 20 minutes, because ingestion lags.
- **New addresses must be verified:** when the action group is created, or `ALERT_EMAIL_TO` changes, Azure emails "Action required: Verify your email" to the address. **Alerts aren't delivered there until Gary clicks it** (found on #48: the first alert fired before verification and its email was dropped).
- **Noise:** the rule is stateless (no auto-resolve, so no "resolved" emails) and **mutes its actions for 1 hour** after firing. A continuing problem emails at most once an hour.
- **What failed:** the email has the rule's description. Then run `deploy/verify/logs.sh ingest 30` (or `api`).
- **Mute it temporarily** (e.g. during planned work): `az monitor scheduled-query update -g rg-cupboard-tracker -n alert-cupboard-errors --disabled true`, then `--disabled false` afterwards. A re-run of `deploy/infra.sh` re-enables it.
- **Test it:** `POST /api/diagnostics/test-error` on either app, with its function key. It logs one Error tagged `diagnosticTest=true` and changes nothing. It's an Azure call and sends Gary an email, so only do it when a card says so.

### UI (the React app, #37)

- **Where:** `FE/`, served by the Static Web App **`stapp-cupboard-tracker`** (Free plan, **West Europe**: Free isn't offered in UK South; it's static files only) at `https://icy-smoke-0a456d503.6.azurestaticapps.net/` and the custom domain **`https://kitchen.garymcmonagle.com/`** (set up by Gary in the portal, not in Bicep; listed in `customDomains` in `infra/main.bicepparam` for CORS). SWA's built-in auth is **not** used: the app signs in with MSAL and calls the API Function app directly (Free has no linked backends). The API and SignalR allow the site, its custom domains and `http://localhost:5173` in CORS (Bicep, `appOrigins`).
- **Stack:** React 19 + TypeScript, Vite 6 (Vite 7+ needs a newer Node than the MacBook's 20.18), Vitest + Testing Library, ESLint, Prettier. `npm test`, `npm run lint`, `npm run build`.
- **Look:** design tokens in `FE/src/tokens.css`, from the mockups (a test checks them). Phone-first, 390 px design width.
- **Sign-in:** MSAL React, auth code + PKCE, redirect flow, single-tenant authority, `localStorage` cache, `acquireTokenSilent` for the API scope. When silent renewal fails (about daily: an `InteractionRequiredAuthError`, or on iPhone a `BrowserAuthError` timeout because the hidden-frame renewal is blocked) or the API says 401, the user signs in again and returns to the page they were on (`needsSignIn` in `api.ts`); a 403 shows "no access".
- **Navigation (#87):** three bottom tabs, **Stock · List · Hungry?** (`BottomNav`; Hungry? was Recipes until #88, still at `/recipes`; List shows a count badge when its count is above 0). Add and Scanners are in the stock list's header: **+ Add**, and a round Scanners button whose dot is red for a problem (hub offline, a scanner unplugged) or amber for things to sort out (`scannerStatus.ts`). Scanners opens with Stock's tab active and a "‹ Stock" back link. `/list` is the shared shopping list (#84; `ShoppingListProvider` in the Shell keeps it live on every screen for the badge).
- **Footer:** "Signed in as <name> · API <commit> · UI <commit>", for verification.
- **Going back (#103, spike #99):** screens reached from the stock list (item detail, Name it, Edit, Add, store-label assign, Scanners) open **over** it: `Shell` keeps the list mounted but hidden, so its data, scroll, aisles, search and Use first survive. The same for Hungry? under item detail opened from a recipe. `navigate()` records each screen's `scrollY` and pushes `{ from }`; ‹ buttons use `goBack()` (a real history step when opened in the app, else navigate).
- **Installable app (PWA, #76):** `FE/public/manifest.webmanifest` ("Cupboard Tracker", short name "Cupboard", standalone, theme `#2F6B4F`), icons in `FE/public/icons/` (rendered from `FE/icons/*.svg` by `FE/scripts/render-icons.mjs`), iOS tags in `index.html`, and safe-area padding that applies **only when installed** (`@media (display-mode: standalone)`). `FE/public/sw.js` is registered after load; it holds the push handlers and the offline shell cache (below). `staticwebapp.config.json` keeps the worker, manifest and icons out of the navigation fallback and serves the worker and manifest `no-cache`, so a deploy's worker takes over on the next open. **Install from `https://kitchen.garymcmonagle.com`** (Safari → Share → Add to Home Screen on iPhone): push subscriptions belong to the address the app was installed from. On iPhone the installed app has its own storage, so people sign in once more inside it.
- **Offline (#85, spike #83). Caching rules:** the service worker caches **only the app shell** (this site's own files: `index.html`, Vite's hashed JS and CSS, the self-hosted Figtree fonts (`@fontsource/figtree`, no Google Fonts), icons and manifest), never the API, sign-in, SignalR, `/version.json` or `/sw.js`. The build (`FE/sw-shell.mjs`, a Vite plugin) writes the file list and a content hash into `dist/sw.js`, so every deploy has its own cache `cupboard-shell-<hash>`; the new worker takes over at once (`skipWaiting`, `clients.claim`) and deletes the old cache. Pages are network first with a 3 s timeout, then the cached `index.html`; shell files come from the cache. Unbuilt (`npm run dev`) it caches nothing. **Only the shopping list works offline:** its copy and the **outbox** live in IndexedDB (`cupboard` → `shoppingList`, `FE/src/listStore.ts`), and the provider syncs them through the idempotent endpoints, sending each tick's real time as `at` so the server ignores a stale one. Offline, the app never sends anyone to sign-in (`needsSignIn` is false while `navigator.onLine` is false). To test offline: `playwright-core` with a throwaway Chrome profile and `context.setOffline(true)`; the iPhone check (airplane mode, installed app) needs Gary.
- **Run locally against the deployed API:** `cd FE && cp .env.example .env.local`, fill it in (`az deployment group show -g rg-cupboard-tracker -n cupboard-entra --query properties.outputs` and the API hostname), `npm run dev`. In Claude Code, `.claude/launch.json` has a `ui` configuration for the Browser pane's preview (`preview_start` with name `ui`). The test user signs in on `localhost:5173` from the same browser session without a new phone approval; Microsoft first asks "Are you trying to sign in to Cupboard Tracker?" (its standard check for localhost redirects): choose Continue.

### Sign-in (Entra ID, #36, spike #35)

- **Tenant:** Gary's "Default Directory" (`CottonWonderhotmail.onmicrosoft.com`). Single-tenant registrations: **Cupboard Tracker** (`cupboard-tracker-spa`, SPA redirect URIs `http://localhost:5173/` plus the Static Web App) and **Cupboard Tracker API** (`cupboard-tracker-api`, identifier `api://<tenantId>/cupboard-tracker-api`, delegated scope `access_as_user`, v2 tokens). The API pre-authorises the SPA (no consent prompt) and the Azure CLI (`04b07795-8ddb-461a-bbee-02f9e1bf7b46`, for verification). No client secrets anywhere.
- **Who can sign in:** users in the tenant **and assigned** to the SPA's enterprise app (assignment is required). To add a household member, Gary: **Entra ID → Users → Invite external user** (they sign in with their own Microsoft account, or a one-time code by email), then **Enterprise applications → Cupboard Tracker → Users and groups → Add user** (assign people directly; groups need P1). Removing someone = deleting their guest account.
- **Deployer:** `cupboard-deployer@CottonWonderhotmail.onmicrosoft.com` (cloud-only, MFA, no licence, not assigned to the app). Its only Entra role is **Application Developer**; its only Azure role is the custom **Cupboard Tracker deployment runner** (`infra/roles/deployment-runner.json`: run and read deployments in `rg-cupboard-tracker`, nothing else). Gary created both by hand; never create users or assign roles from code.
- **API auth (in code, not Easy Auth):** `BearerAuthMiddleware` checks the bearer token's signature, issuer, audience, tenant, client (`azp`: the SPA or the Azure CLI) and scope. 401 for a missing or invalid token, 403 for the wrong client or scope. **Every API HTTP function at platform level Anonymous needs a signed-in user, unless it's marked `[PublicEndpoint]`** (only `/api/ping`). Function-key endpoints (`/api/diagnostics/test-error`) are checked by the platform. Use cases receive a `CurrentUser(ObjectId, DisplayName)` (`context.GetCurrentUser()`), never token types. `GET /api/me` returns the caller.
- **App settings** (non-secret, from `deploy/infra.sh`): `ENTRA_TENANT_ID`, `ENTRA_API_CLIENT_ID`, `ENTRA_ALLOWED_CLIENT_IDS` (SPA, Azure CLI). Until they're set, every user endpoint answers 401.
- **Verification without credentials:** for the **API**, `deploy/verify/token.sh` (your az login's API token, claims only) and `deploy/verify/api.sh /api/me` (read-only GETs). For the **UI**, the test user **`cupboard-test@CottonWonderhotmail.onmicrosoft.com`**, which signs in **passwordless**: enter the username only, and Gary approves on his phone (number matching). It's a household-level account against live stock. Never type a password or code for it.

### Verification toolkit

Read-only scripts in `deploy/verify/` for checking cards against the **live** system. They run as your `az login`; deploy/infra.sh grants that account read-only access (Reader on the group, Log Analytics Reader, Cosmos DB Built-in Data Reader). None of them can write or delete anything.

| Script | Use it to |
|---|---|
| `deploy/verify/ping.sh` | See both Function apps answer `/api/ping`, and which commit each runs; plus the Pi client's service state and live commit (over SSH). |
| `deploy/verify/token.sh` | Get an API token from your own `az login` (Azure CLI pre-authorisation) and print its **claims** only. |
| `deploy/verify/api.sh /api/<path>` | GET an API path as yourself, with that token (never shown). |
| `deploy/verify/pi-logs.sh [minutes=30]` | Read the Pi client's journal (`journalctl -u cupboard-pi`) over SSH. |
| `deploy/verify/logs.sh <ingest\|api> [minutes=30] [rows=50]` | Read recent requests, traces and exceptions for one app (newest first). It queries the Log Analytics workspace behind App Insights; ingestion lags 1–3 minutes. |
| `deploy/verify/cosmos.sh <container> ["SELECT ..."]` | Read documents from a container (default `SELECT * FROM c`, at most 100 shown). Only `SELECT` is allowed. Cross-partition `TOP`, `ORDER BY`, `GROUP BY` and aggregates aren't supported: filter on the partition key instead. |

**Scan and heartbeat simulator** (`tools/simulator/`, see its README): sends scan events and heartbeats to the live ingest Function exactly as the Pi does, using the Pi package's own client and payload models. `cupboard-simulator [--dry-run] scan --role in|out --barcode <digits>`, `scans --file <json>` (a list of `{barcode, role}` or the phone scanner app's export), `resend --scan-id <id>`, `heartbeat [--every <s> --for <min>]`. It reads `HUB_ID` and `SCANNERS` like the Pi (`--env-file LocalScanner/.env.example` works for dry runs) and fetches the function key at runtime without ever showing it.

**Rules for simulating against the live system:**
- There's **one live environment**, so simulated scans create **real** stock. Only simulate items Gary **actually has**, with their real barcodes (from Gary, or his phone scanner app), unless a card says otherwise.
- Anything sent only to test must be **undone** (e.g. checked out again) and recorded in the card comment.
- **"Gary scans X" now means Gary scans it on the real scanner** (the Pi sends it, #67). The simulator is no longer the default; use it only when a card says so, or when Gary isn't at the scanners and agrees.

- **UI verification uses the test user's session in Claude Code's built-in browser** (the Browser pane, `mcp__Claude_Browser__*`), signed in to the deployed site as `cupboard-test@CottonWonderhotmail.onmicrosoft.com` (first done on #37). Reuse it; the footer shows who's signed in. Expect to re-sign-in about daily (SPA refresh tokens last 24 hours, or the pane's storage was cleared): open the site, press **Sign in**, type the **username only**, press Next, and ask Gary in the chat to approve on his phone, **giving him the number shown**; choose **Yes** to "Stay signed in?". If a password field appears, stop and ask Gary; never type into it. Passwordless needs "Enable phone sign-in" done for the account in Authenticator on Gary's phone (it is).
- When querying App Insights yourself, query the **workspace** (`az monitor log-analytics query`, tables `AppRequests`, `AppTraces`, `AppExceptions`, `AppDependencies`); `az monitor app-insights query` returned nothing here. `last` and `kind` are reserved words in KQL.
- **Evidence pasted into Trello must have keys, tokens and personal data redacted.** The scripts never print the function key, but check anything you paste.
- UI screenshots come with the first UI feature (via a browser tool). Pi logs (`pi-logs.sh`) and the Pi's status in `ping.sh` need only SSH to the Pi (no Azure login for `pi-logs.sh`).

### Raspberry Pi (the hub)

Headless Raspberry Pi 3, managed over SSH from the MacBook. No secrets here: the env file lives only on the Pi.

| | |
|---|---|
| Host / user | `cupboard-pi.local` / `gary` (SSH key only: password and keyboard-interactive logins are off, root login off) |
| OS | Raspberry Pi OS 13 "trixie" Lite, 32-bit (`armv7l`), Python 3.13, timezone Europe/London, NTP on |
| Packages | `git` (installed but unused: deployment is by file copy), `python3`, `python3-venv`, `python3-pip`, `evtest` (scanner spike), `unattended-upgrades` (security updates, on) |
| App directory | `/opt/cupboard-tracker` (owned by `gary`, 755): releases of the client, each with its own venv, **copied over SSH** by `deploy/pi.sh` (#33; no git clone on the Pi) |
| Data directory | `/var/lib/cupboard-tracker` (owned by `gary`, 750): the scan queue, `scan-queue.sqlite3` (table `scans`: waiting to send; `rejected`: scans ingest refused as invalid, kept for a person to look at). Outside the releases, so deploys never wipe it. **Never delete it**: queued scans exist nowhere else |
| Scanners | Two Tera 5100s, told apart by **scanner port** (spike #27): scanner 1's dongle in the **bottom-right** USB port is `platform-3f980000.usb-usb-0:1.5:1.0`, scanner 2's in the **top-left** is `platform-3f980000.usb-usb-0:1.2:1.0`. `SCANNERS` in `pi.env` maps each port to its device and role. Both scanners are set to the UK keyboard layout |
| Config directory | `/etc/cupboard-tracker/` (root, 750): `pi.env` (root, 600) with the ingest URL and function key. **Never in the repo**, never printed. Only systemd (as root) reads it |
| Service | `cupboard-pi` (systemd, unit in `LocalScanner/deploy/cupboard-pi.service`): runs `/opt/cupboard-tracker/current/venv/bin/cupboard-pi` as `gary` (with the `input` group), `Restart=always`, enabled at boot, logs to the journal. `/opt/cupboard-tracker/current` → `releases/<sha>/`, `previous` → the one before |

`deploy/pi-setup.sh` set this up (idempotent; Gary runs it as root because it needs his sudo password: `scp deploy/pi-setup.sh gary@cupboard-pi.local:/tmp/ && ssh -t gary@cupboard-pi.local sudo bash /tmp/pi-setup.sh`). **The agent never types the Pi's sudo password**; anything needing root is a script Gary runs. `gary` is in the `input` group, so scanner input devices can be read without root. Scanners are told apart by the USB port their dongle is in (spike #27, `docs/spikes/27-scanner-input.md`).

### Azure resources and naming

Everything lives in **`rg-cupboard-tracker`** in **UK South** (the subscription holds other projects; never touch anything outside this group). Names are `<type>-cupboard-<role>[-<suffix>]`, using the Azure abbreviations, with a 6-character `suffix` (from `uniqueString(subscription, resource group)`) only where a name must be globally unique:

| Resource | Name |
|---|---|
| Resource group | `rg-cupboard-tracker` |
| Log Analytics / Application Insights | `log-cupboard-tracker` / `appi-cupboard-tracker` |
| Cosmos DB (serverless), database `cupboard` | `cosmos-cupboard-<suffix>` |
| Function apps (Flex Consumption, one plan each) | `func-cupboard-ingest-<suffix>`, `func-cupboard-api-<suffix>`; plans `plan-cupboard-ingest`, `plan-cupboard-api` |
| Error alert (log search rule) / action group | `alert-cupboard-errors` (display name "Cupboard Tracker errors") / `ag-cupboard-alerts` |
| Communication Services / Email Communication Service (location `global`, data in the UK) | `acs-cupboard-<suffix>` / `ecs-cupboard-<suffix>`, domain `AzureManagedDomain` (sender `DoNotReply@<random>.azurecomm.net`, the `emailSender` output) |
| Key Vault (#42) | `kv-cupboard-<suffix>` |
| SignalR Service (#54, Free, Serverless) | `sigr-cupboard-<suffix>` |
| Storage (one per Function app; no dashes allowed) | `stcupboarding<suffix>`, `stcupboardapi<suffix>` |

Tags on everything: `app=cupboard-tracker`, `environment=live`, `managedBy=bicep` (plus `component=ingest|api` on each Function app's resources).

**No keys anywhere** (except true third-party secrets, in Key Vault): Cosmos has local (key) auth disabled and both apps use their system-assigned managed identities with the **Cosmos DB Built-in Data Contributor** role on the database. Storage has shared-key access disabled (the apps use **Storage Blob Data Owner** on their own account for host storage and the deployment package). Application Insights accepts only Entra-authenticated telemetry (**Monitoring Metrics Publisher**). SignalR has local auth disabled; both apps use **SignalR Service Owner** (#54). The API app sends email with its identity too, with **Communication and Email Service Owner** on the one Communication Services resource (Microsoft documents no smaller role for sending, and custom roles are reported not to work for ACS). App settings hold endpoints and names only (`COSMOS_ENDPOINT`, `COSMOS_DATABASE`, `AzureWebJobsStorage__accountName`, the App Insights connection string, `COMMIT_SHA`, `ACS_ENDPOINT`, `EMAIL_SENDER`), plus non-secret config (`ALERT_EMAIL_TO`, `DIGEST_EMAIL_TO`, `OFFLINE_THRESHOLD_MINUTES`, the Open Food Facts settings, `AI_SERVICES_ENDPOINT` with `PHOTO_MODEL_DEPLOYMENT` and `RECIPE_MODEL_DEPLOYMENT` (#73; both `gpt-4o`, the one Foundry deployment used: Standard, regional, **50k tokens a minute**, the subscription's UK South quota, set by `gpt4oCapacity` in `infra/modules/aiServices.bicep`; Gary, 2026-10-21), and optionally `RECIPE_REQUESTS_PER_HOUR`, the per-user cost guard on recipe suggestions and adjustments (#104), default 30; and `RECIPE_ADDITION_MIN_CONFIDENCE`, the Jev probability a suggested addition needs, default 0.1). Generic names (#90) take optional settings that default in code: `GENERIC_NAME_MATCH_MIN_CONFIDENCE` (0.9, calibrated on 77 real products: below it Jev's wrong merges outnumber its right ones), `GENERIC_NAME_BATCH_SIZE` (254, Jev's 255 options less "None of these"), `GENERIC_NAME_MODEL_DEPLOYMENT` (`gpt-4o`) and `GENERIC_NAME_FOUNDRY_MIN_INTERVAL_SECONDS` (6, so the shared gpt-4o deployment keeps room for recipes and photos).

---

## Repo layout

_Update as the structure lands._

```
BE/                              # backend: the one .NET solution (build and test from here)
  CupboardTracker.sln
  global.json                    # .NET SDK pin, test runner
  Directory.Build.props          # shared .NET build settings
  Directory.Packages.props       # all NuGet package versions
  src/
    CupboardTracker.Domain/              # entities and value objects; no package references
    CupboardTracker.Application/         # use cases and interfaces for external services
    CupboardTracker.Infrastructure/      # implementations (Cosmos, HTTP clients, ...) and DI wiring
    CupboardTracker.Functions.Ingest/    # Function app: ingest (function-key auth)
    CupboardTracker.Functions.Api/       # Function app: API/BFF, change feed, timers
  tests/
    CupboardTracker.Domain.Tests/
    CupboardTracker.Application.Tests/
    CupboardTracker.Infrastructure.Tests/
    CupboardTracker.Functions.Ingest.Tests/
    CupboardTracker.Functions.Api.Tests/          # auth middleware, validator
FE/                              # frontend: the React app (Vite; src/tokens.css, src/api.ts; deploy/ui.sh)
LocalScanner/                    # local scanner: the Python Pi client (package cupboard_pi, src/ layout)
  deploy/                        # systemd unit, sudoers rule and install-service.sh (Gary, root) for the Pi
tools/                           # command-line tools, e.g. simulator/ (#66)
docs/
  domain-language.md
  data-model.md
  spikes/
deploy/                          # deployment scripts (infra.sh, functions.sh, pi.sh, pi-setup.sh) and verify/ (read-only checks)
infra/                           # Bicep: main.bicep, main.bicepparam, modules/; entra.bicep (Graph, deploy/entra.sh); roles/
```

Older cards may say `src/`, `pi/` or `ui/`: read those as `BE/src/`, `LocalScanner/` and `FE/`. Command-line tools that aren't part of a component (e.g. the scan and heartbeat simulator, `tools/simulator/`) go in a top-level **`tools/`** folder (Gary, 2026-10-17). For any other new top-level folder, check with Gary first.

Dependencies point inwards: Functions → Infrastructure → Application → Domain.
