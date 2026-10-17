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

The Cosmos data model (containers, partition keys, change-feed flow, idempotency) is documented in [`docs/data-model.md`](docs/data-model.md). Follow it for document shapes; if a card conflicts with it, ask in a card comment.

### Code conventions

- Both Function apps live in **one .NET solution** so types stay aligned; assume they are always deployed together.
- **Clean architecture.** Domain and application layers have no dependency on Azure SDKs, HTTP clients or third-party types. Every external service (Cosmos, Open Food Facts, Jev, Document Intelligence, email, SignalR) sits behind an interface with its implementation in the infrastructure layer.
- Every behaviour change ships with **unit tests**. Tests must pass before merging.
- Configuration via app settings / env vars. Key Vault is not used yet — it's added when the first real secret appears.
- **.NET 10 (LTS)**, Azure Functions **isolated worker** (Functions host v4), on the Flex Consumption plan. Everything .NET lives in `BE/`: pinned in `BE/global.json`, shared build settings in `BE/Directory.Build.props` (nullable, warnings as errors, recommended analysers); package versions only in `BE/Directory.Packages.props` (central package management).
- **Local scanner (Pi client), in `LocalScanner/`: Python 3.11+**, standard library only at runtime (lean thin client); `pytest` and `ruff` for development. The simulator (card #66) imports the `cupboard_pi` package's ingest client and payload models, so keep them free of Pi-only dependencies.

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
  - **Infra (Bicep): `deploy/infra.sh [--yes] [--what-if-only]`.** Deploys `infra/main.bicep` with `infra/main.bicepparam` at subscription scope (it creates the resource group). It shows the signed-in subscription, prints a **what-if**, and deploys only after confirmation (`--yes` skips the prompt; use it only when Gary has approved the what-if). Safe to re-run: it reads each Function app's live `COMMIT_SHA` and passes it back, so infra never resets the deployed commit. A re-run with no template changes should show **no changes** in what-if. Prints the outputs (app names, hostnames, Cosmos endpoint, App Insights name).
  - Function apps: _TBD_
  - UI (Static Web Apps): _TBD_
  - Pi client: _TBD_
- Never delete or overwrite Cosmos data, containers or resources outside what a card asks for. Anything destructive or irreversible: stop and ask in a card comment.

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
docs/
  domain-language.md
  data-model.md
  spikes/
deploy/                          # deployment scripts (infra.sh)
infra/                           # Bicep: main.bicep, main.bicepparam, modules/
```

Older cards may say `src/`, `pi/` or `ui/`: read those as `BE/src/`, `LocalScanner/` and `FE/`. Where a card names a new top-level folder (e.g. `tools/simulator/`), check with Gary which of the three it belongs in.

Dependencies point inwards: Functions → Infrastructure → Application → Domain.
