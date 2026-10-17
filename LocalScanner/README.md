# LocalScanner: the Cupboard Tracker Pi client

Python package `cupboard_pi`: the thin client on the Raspberry Pi. For now it loads its config, logs startup (with the deployed commit) and runs until stopped; scanner reading and heartbeats come with their features.

Deployed to the Pi by `deploy/pi.sh` (see CLAUDE.md, Deployment). `deploy/` here holds the systemd unit, the deploy's sudoers rule and `install-service.sh`, which Gary runs as root when the unit changes.

```bash
cd LocalScanner
python3.11 -m venv .venv && . .venv/bin/activate
pip install -e '.[dev]'
pytest
ruff check . && ruff format --check .
python -m cupboard_pi        # needs the variables in .env.example set in the environment
```

The ingest client (`ingest_client`), payload models (`payloads`: scan event, heartbeat, barcode validation) and identity config (`identity`: `HUB_ID`, `SCANNERS`) are imported by the scan and heartbeat simulator (card #66), so keep them importable and free of Pi-only dependencies.
