# Cupboard Tracker

Home food inventory tracker. Two barcode scanners (check-in / check-out) feed a Raspberry Pi, which sends scan events to Azure. A phone-first React app shows current stock.

This file holds **technical detail and process rules**. The *what* of each piece of work lives on the Trello cards — they are written to be self-contained. If a card and this file disagree, stop and ask in a card comment.

Use the terms defined in `docs/domain-language.md` in code, comments, commits and card comments. If you need a term that isn't defined there, add it as part of your work.

---

## Architecture

| Component | Tech | Notes |
|---|---|---|
| Pi client | Python, Raspberry Pi 3 (headless) | Lean thin client. Reads both scanners, maps each to a logical `deviceId`, POSTs scan events + periodic heartbeat to the ingest Function. Auth header name/value come from env vars and are abstracted in the client. Deployed via git pull over SSH. |
| Ingest Function | .NET Azure Functions | HTTP, function-key auth. Writes append-only scan events to Cosmos. |
| API / BFF Function | .NET Azure Functions | Entra ID bearer-token auth from the UI. Also hosts change-feed processing and timer functions unless a card says otherwise. |
| UI | React on Azure Static Web Apps (free tier) | Phone-first, calm and clean. Entra ID sign-in. Live scan feed via Azure SignalR (free tier). |
| Data | Cosmos DB **serverless** | Containers: scan events (append-only), products (each with its own `productId`; barcodes are optional lookups, so loose items work), inventory items (one per physical unit, partitioned by product), devices (role: in / out), unmatched check-outs, plus the change-feed leases container. |
| External | Open Food Facts, Azure Document Intelligence (Read), Jev (TypeSafe AI System One) | Product lookup → OCR for photo ID → Jev for decisions over extracted text and estimated expiry dates. |
| Infra | Bicep | Single environment. Follow Bicep best practices (modules, parameters, no hard-coded names/secrets). |

The Cosmos data model (containers, partition keys, change-feed flow, idempotency) is documented in [`docs/data-model.md`](docs/data-model.md). The HTTP contracts (request and response shapes, validation rules, status codes) are in [`docs/api.md`](docs/api.md); the Pi's payload models (`cupboard_pi.payloads`) must stay in step with it. Follow it for document shapes; if a card conflicts with it, ask in a card comment.

### Code conventions

- Both Function apps live in **one .NET solution** so types stay aligned; assume they are always deployed together.
- **Clean architecture.** Domain and application layers have no dependency on Azure SDKs, HTTP clients or third-party types. Every external service (Cosmos, Open Food Facts, Jev, Document Intelligence, email, SignalR) sits behind an interface with its implementation in the infrastructure layer.
- Every behaviour change ships with **unit tests**. Tests must pass before merging.
- Configuration via app settings / env vars. Key Vault is not used yet — it's added when the first real secret appears.
- **.NET 10 (LTS)**, Azure Functions **isolated worker** (Functions host v4), on the Flex Consumption plan. Everything .NET lives in `BE/`: pinned in `BE/global.json`, shared build settings in `BE/Directory.Build.props` (nullable, warnings as errors, recommended analysers); package versions only in `BE/Directory.Packages.props` (central package management).
- **Local scanner (Pi client), in `LocalScanner/`: Python 3.11+**, standard library only at runtime (lean thin client); `pytest` and `ruff` for development. The simulator (card #66) imports the `cupboard_pi` package's ingest client and payload models, so keep them free of Pi-only dependencies. The shared ingest contract lives in `cupboard_pi.payloads` (scan event, heartbeat, barcode validation) and `cupboard_pi.identity` (`HUB_ID`, `SCANNERS=deviceId=in|out,...`).

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
  - **Infra (Bicep): `deploy/infra.sh [--yes] [--what-if-only]`.** Deploys `infra/main.bicep` with `infra/main.bicepparam` at subscription scope (it creates the resource group). It shows the signed-in subscription, prints a **what-if**, and deploys only after confirmation (`--yes` skips the prompt; use it only when Gary has approved the what-if). Safe to re-run: it reads each Function app's live `COMMIT_SHA` and `OFF_CONTACT_EMAIL` (the Open Food Facts User-Agent contact: Gary's email, **never committed**) and passes them back, so infra never resets them. On the first deploy it takes `OFF_CONTACT_EMAIL` from the environment, or asks. A re-run with no template changes leaves the live resources **identical** (checked on #31 by snapshotting the group before and after). **What-if still lists some "Modify" entries that are false positives**, not real changes: app settings (what-if can't read them, so they always show as "Create"), role assignments' `principalId` (a managed identity's ID can't be resolved during what-if), and read-only or server-defaulted properties (e.g. Cosmos `sqlEndpoint`, storage blob-service defaults, App Insights `Flow_Type`). Look for anything else. Prints the outputs (app names, hostnames, Cosmos endpoint, App Insights name).
  - **Function apps: `deploy/functions.sh [--force]`.** Deploys **both** apps together from a clean, pushed `main` (or a checkout of exactly `origin/main`'s commit); `--force` overrides those checks and is for emergencies only. It runs `dotnet test` (stops on failure), publishes both apps in Release, zip-deploys them to the apps named in the infra deployment's outputs, sets `COMMIT_SHA` to the short commit, then smoke-checks `/api/ping` on both (ingest with its function key, fetched at runtime and passed to curl on stdin: never echoed, logged or written to disk), retrying for ~2 minutes. It prints the apps, the SHA and the smoke result, and exits non-zero if anything fails.
  - UI (Static Web Apps): _TBD_
  - Pi setup (one-off, root, run by Gary): `deploy/pi-setup.sh`.
  - Pi client: _TBD_ (#33)
- Never delete or overwrite Cosmos data, containers or resources outside what a card asks for. Anything destructive or irreversible: stop and ask in a card comment.

### Verification toolkit

Read-only scripts in `deploy/verify/` for checking cards against the **live** system. They run as your `az login`; deploy/infra.sh grants that account read-only access (Reader on the group, Log Analytics Reader, Cosmos DB Built-in Data Reader). None of them can write or delete anything.

| Script | Use it to |
|---|---|
| `deploy/verify/ping.sh` | See both Function apps answer `/api/ping`, and which commit each runs. |
| `deploy/verify/logs.sh <ingest\|api> [minutes=30] [rows=50]` | Read recent requests, traces and exceptions for one app (newest first). It queries the Log Analytics workspace behind App Insights; ingestion lags 1–3 minutes. |
| `deploy/verify/cosmos.sh <container> ["SELECT ..."]` | Read documents from a container (default `SELECT * FROM c`, at most 100 shown). Only `SELECT` is allowed. Cross-partition `TOP`, `ORDER BY`, `GROUP BY` and aggregates aren't supported: filter on the partition key instead. |

**Scan and heartbeat simulator** (`tools/simulator/`, see its README): sends scan events and heartbeats to the live ingest Function exactly as the Pi does, using the Pi package's own client and payload models. `cupboard-simulator [--dry-run] scan --role in|out --barcode <digits>`, `scans --file <json>` (a list of `{barcode, role}` or the phone scanner app's export), `resend --scan-id <id>`, `heartbeat [--every <s> --for <min>]`. It reads `HUB_ID` and `SCANNERS` like the Pi (`--env-file LocalScanner/.env.example` works for dry runs) and fetches the function key at runtime without ever showing it.

**Rules for simulating against the live system:**
- There's **one live environment**, so simulated scans create **real** stock. Only simulate items Gary **actually has**, with their real barcodes (from Gary, or his phone scanner app), unless a card says otherwise.
- Anything sent only to test must be **undone** (e.g. checked out again) and recorded in the card comment.
- Until [Feature] Pi reads both scanners and sends scans (#67) is Done, a verification step saying "Gary scans X" means: get the barcode from Gary and send it with the simulator.

- When querying App Insights yourself, query the **workspace** (`az monitor log-analytics query`, tables `AppRequests`, `AppTraces`, `AppExceptions`, `AppDependencies`); `az monitor app-insights query` returned nothing here. `last` and `kind` are reserved words in KQL.
- **Evidence pasted into Trello must have keys, tokens and personal data redacted.** The scripts never print the function key, but check anything you paste.
- UI screenshots come with the first UI feature (via a browser tool). Pi logs (`pi-logs.sh`) and the Pi's status in `ping.sh` come with the Pi deployment card (#33).

### Raspberry Pi (the hub)

Headless Raspberry Pi 3, managed over SSH from the MacBook. No secrets here: the env file lives only on the Pi.

| | |
|---|---|
| Host / user | `cupboard-pi.local` / `gary` (SSH key only: password and keyboard-interactive logins are off, root login off) |
| OS | Raspberry Pi OS 13 "trixie" Lite, 32-bit (`armv7l`), Python 3.13, timezone Europe/London, NTP on |
| Packages | `git`, `python3`, `python3-venv`, `python3-pip`, `evtest` (scanner spike), `unattended-upgrades` (security updates, on) |
| App directory | `/opt/cupboard-tracker` (owned by `gary`, 755): the client and its venv, deployed by git |
| Config directory | `/etc/cupboard-tracker/` (root, 750): `pi.env` with the ingest URL and function key. **Never in the repo**, never printed |

`deploy/pi-setup.sh` set this up (idempotent; Gary runs it as root because it needs his sudo password: `scp deploy/pi-setup.sh gary@cupboard-pi.local:/tmp/ && ssh -t gary@cupboard-pi.local sudo bash /tmp/pi-setup.sh`). **The agent never types the Pi's sudo password**; anything needing root is a script Gary runs. `gary` is in the `input` group, so scanner input devices can be read without root.

### Azure resources and naming

Everything lives in **`rg-cupboard-tracker`** in **UK South** (the subscription holds other projects; never touch anything outside this group). Names are `<type>-cupboard-<role>[-<suffix>]`, using the Azure abbreviations, with a 6-character `suffix` (from `uniqueString(subscription, resource group)`) only where a name must be globally unique:

| Resource | Name |
|---|---|
| Resource group | `rg-cupboard-tracker` |
| Log Analytics / Application Insights | `log-cupboard-tracker` / `appi-cupboard-tracker` |
| Cosmos DB (serverless), database `cupboard` | `cosmos-cupboard-<suffix>` |
| Function apps (Flex Consumption, one plan each) | `func-cupboard-ingest-<suffix>`, `func-cupboard-api-<suffix>`; plans `plan-cupboard-ingest`, `plan-cupboard-api` |
| Storage (one per Function app; no dashes allowed) | `stcupboarding<suffix>`, `stcupboardapi<suffix>` |

Tags on everything: `app=cupboard-tracker`, `environment=live`, `managedBy=bicep` (plus `component=ingest|api` on each Function app's resources).

**No keys anywhere:** Cosmos has local (key) auth disabled and both apps use their system-assigned managed identities with the **Cosmos DB Built-in Data Contributor** role on the database. Storage has shared-key access disabled (the apps use **Storage Blob Data Owner** on their own account for host storage and the deployment package). Application Insights accepts only Entra-authenticated telemetry (**Monitoring Metrics Publisher**). App settings hold endpoints and names only (`COSMOS_ENDPOINT`, `COSMOS_DATABASE`, `AzureWebJobsStorage__accountName`, the App Insights connection string, `COMMIT_SHA`).

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
FE/                              # frontend: React app (not yet)
LocalScanner/                    # local scanner: the Python Pi client (package cupboard_pi, src/ layout)
tools/                           # command-line tools, e.g. simulator/ (#66; not yet)
docs/
  domain-language.md
  data-model.md
  spikes/
deploy/                          # deployment scripts (infra.sh)
infra/                           # Bicep: main.bicep, main.bicepparam, modules/
```

Older cards may say `src/`, `pi/` or `ui/`: read those as `BE/src/`, `LocalScanner/` and `FE/`. Command-line tools that aren't part of a component (e.g. the scan and heartbeat simulator, `tools/simulator/`) go in a top-level **`tools/`** folder (Gary, 2026-10-17). For any other new top-level folder, check with Gary first.

Dependencies point inwards: Functions → Infrastructure → Application → Domain.
