"""Remembers sent scan payloads (no secrets) so `resend` can send one again unchanged."""

from __future__ import annotations

import json
import os
from pathlib import Path
from typing import Any


def _path() -> Path:
    home = Path(os.environ.get("CUPBOARD_SIMULATOR_HOME", Path.home() / ".cupboard-simulator"))
    return home / "sent-scans.jsonl"


def record(payload: dict[str, Any]) -> None:
    path = _path()
    path.parent.mkdir(parents=True, exist_ok=True)
    with path.open("a") as f:
        f.write(json.dumps(payload, separators=(",", ":")) + "\n")


def find(scan_id: str) -> dict[str, Any] | None:
    path = _path()
    if not path.exists():
        return None
    for line in reversed(path.read_text().splitlines()):
        payload = json.loads(line)
        if payload.get("scanId") == scan_id:
            return payload
    return None
