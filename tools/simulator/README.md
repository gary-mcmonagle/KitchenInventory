# Scan and heartbeat simulator

Sends **scan events** and **heartbeats** to the live ingest Function exactly as the Pi does. It imports the Pi package's ingest client, payload models and identity config (`LocalScanner/`, package `cupboard_pi`), so the two can't drift apart.

> **The live environment is the real household system: simulated scans create real stock.** Only simulate items Gary actually has, with their real barcodes. Undo anything sent only to test (e.g. check it out again) and record it in the card comment.

## Set up

```bash
cd tools/simulator
python3.11 -m venv .venv && . .venv/bin/activate
pip install -e ../../LocalScanner -e '.[dev]'
```

It needs `HUB_ID` and `SCANNERS`: the **same variables and values as the Pi** (see `LocalScanner/.env.example`). Export them, or pass `--env-file` (only those two keys are read).

The ingest URL and function key come from Azure at runtime (`az login`, infra deployment outputs). The key is sent only as a request header: it's never printed, logged or saved.

## Use

```bash
cupboard-simulator --env-file ../../LocalScanner/.env.example --dry-run scan --role in --barcode 5000354922831
cupboard-simulator scan --role in --barcode 5000354922831 [--at 2026-10-17T14:30:00Z]
cupboard-simulator scans --file export.json [--role in] [--keep-timestamps]   # list of {barcode, role}, or the phone app's export
cupboard-simulator resend --scan-id <uuid>        # same payload again (idempotency); uses ~/.cupboard-simulator/sent-scans.jsonl
cupboard-simulator heartbeat [--every 60 --for 10]
```

- `--dry-run` prints each payload and sends nothing (no Azure calls at all).
- Every scan gets a fresh scan ID (UUID) and `source: "simulator"`; `clientSha` is `simulator-<git sha>`.
- Batches skip anything that isn't a product barcode (QR codes etc.) with a message, and any invalid barcode.
- Exit code: 0 all sent with 2xx, 1 some failed, 2 bad input or config.

## Test

```bash
pytest && ruff check . && ruff format --check .
```
