"""Batch files: a JSON list of {barcode, role}, or the barcode-scanner phone app's export
(objects with value, format and timestamp in ms, as in the Open Food Facts spike's sample)."""

from __future__ import annotations

import json
from dataclasses import dataclass
from datetime import UTC, datetime
from pathlib import Path

PRODUCT_FORMATS = {"ean_13", "ean_8", "upc_a"}


@dataclass(frozen=True)
class BatchItem:
    barcode: str
    role: str
    scanned_at: datetime | None  # from the phone export, when kept


@dataclass(frozen=True)
class Batch:
    items: list[BatchItem]
    skipped: list[str]  # human-readable reasons


def load_batch(path: Path, default_role: str, keep_timestamps: bool = False) -> Batch:
    data = json.loads(path.read_text())
    if not isinstance(data, list):
        raise ValueError("The batch file must be a JSON list.")
    items: list[BatchItem] = []
    skipped: list[str] = []
    for i, entry in enumerate(data):
        if not isinstance(entry, dict):
            skipped.append(f"#{i}: not an object")
        elif "barcode" in entry:
            items.append(BatchItem(str(entry["barcode"]), str(entry.get("role", default_role)), None))
        elif "value" in entry:
            fmt = str(entry.get("format", "")).lower()
            if fmt not in PRODUCT_FORMATS:
                skipped.append(
                    f"#{i}: {entry.get('formatName', fmt) or 'unknown format'} isn't a product barcode"
                )
                continue
            at = None
            if keep_timestamps and isinstance(entry.get("timestamp"), int | float):
                at = datetime.fromtimestamp(entry["timestamp"] / 1000, UTC)
            items.append(BatchItem(str(entry["value"]), default_role, at))
        else:
            skipped.append(f"#{i}: no barcode or value")
    return Batch(items, skipped)
