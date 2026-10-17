# LocalScanner: the Cupboard Tracker Pi client

Python package `cupboard_pi`: the thin client on the Raspberry Pi. For now it loads its config, logs startup and exits; scanner reading and heartbeats come with their features.

```bash
cd LocalScanner
python3.11 -m venv .venv && . .venv/bin/activate
pip install -e '.[dev]'
pytest
ruff check . && ruff format --check .
python -m cupboard_pi        # needs the variables in .env.example set in the environment
```

The ingest client and (later) the payload models are imported by the scan and heartbeat simulator (card #66), so keep them importable and free of Pi-only dependencies.
