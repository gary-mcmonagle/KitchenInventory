"""Payloads sent to ingest: the shared contract between the Pi, the simulator and the ingest Function.

Scan events follow [Feature] Scans are recorded in Azure (#43); heartbeats follow
[Feature] Email alert when the Pi goes offline (#44). Those cards document the contract in docs/api.md.
Field names are camelCase on the wire.
"""

from __future__ import annotations

import uuid
from dataclasses import dataclass, field
from datetime import UTC, datetime
from typing import Any

SCANS_PATH = "/api/scans"
HEARTBEAT_PATH = "/api/heartbeat"
ROLES = ("in", "out")
BARCODE_LENGTHS = (8, 12, 13)  # EAN-8, UPC-A, EAN-13


class PayloadError(ValueError):
    """A payload would break the contract (bad barcode, role, etc.)."""


def is_valid_barcode(barcode: str) -> bool:
    """Digits only, length 8, 12 or 13, and a valid GS1 (mod 10) check digit."""
    if not barcode.isascii() or not barcode.isdigit() or len(barcode) not in BARCODE_LENGTHS:
        return False
    digits = [int(d) for d in barcode]
    body, check = digits[:-1], digits[-1]
    # From the right of the body, weights alternate 3, 1, 3, 1...
    total = sum(d * (3 if i % 2 == 0 else 1) for i, d in enumerate(reversed(body)))
    return (10 - total % 10) % 10 == check


def format_utc(moment: datetime) -> str:
    """ISO 8601 in UTC with milliseconds and a Z, e.g. 2026-10-16T18:42:07.123Z."""
    if moment.tzinfo is None:
        raise PayloadError("Timestamps must be timezone-aware.")
    return moment.astimezone(UTC).isoformat(timespec="milliseconds").replace("+00:00", "Z")


@dataclass(frozen=True)
class ScanEvent:
    scan_id: str
    barcode: str
    device_id: str
    role: str
    scanned_at: datetime
    client_sha: str
    extra: dict[str, Any] = field(default_factory=dict)  # stored as-is by ingest (e.g. source)

    def __post_init__(self) -> None:
        if not is_valid_barcode(self.barcode):
            raise PayloadError(
                f"Invalid barcode {self.barcode!r}: need 8, 12 or 13 digits with a valid check digit."
            )
        if self.role not in ROLES:
            raise PayloadError(f"Invalid role {self.role!r}: must be 'in' or 'out'.")
        if not self.device_id:
            raise PayloadError("deviceId is required.")
        uuid.UUID(self.scan_id)  # raises ValueError if it isn't a UUID

    @classmethod
    def new(
        cls,
        barcode: str,
        device_id: str,
        role: str,
        client_sha: str,
        scanned_at: datetime | None = None,
        extra: dict[str, Any] | None = None,
    ) -> ScanEvent:
        """A new scan with a fresh scan ID (UUID v4), minted when the barcode is read."""
        return cls(
            scan_id=str(uuid.uuid4()),
            barcode=barcode,
            device_id=device_id,
            role=role,
            scanned_at=scanned_at or datetime.now(UTC),
            client_sha=client_sha,
            extra=dict(extra or {}),
        )

    def to_json(self) -> dict[str, Any]:
        reserved = {"scanId", "barcode", "deviceId", "role", "scannedAt", "clientSha"}
        clash = reserved & self.extra.keys()
        if clash:
            raise PayloadError(f"Extra fields can't override contract fields: {sorted(clash)}")
        return {
            "scanId": self.scan_id,
            "barcode": self.barcode,
            "deviceId": self.device_id,
            "role": self.role,
            "scannedAt": format_utc(self.scanned_at),
            "clientSha": self.client_sha,
            **self.extra,
        }


@dataclass(frozen=True)
class ScannerPresence:
    device_id: str
    present: bool

    def to_json(self) -> dict[str, Any]:
        return {"deviceId": self.device_id, "present": self.present}


@dataclass(frozen=True)
class Heartbeat:
    hub_id: str
    sent_at: datetime
    client_sha: str
    uptime_seconds: int
    usb_input_devices: list[str] = field(default_factory=list)  # informational; may be empty
    scanners: list[ScannerPresence] | None = None  # optional; the real Pi adds it later (#67)

    def __post_init__(self) -> None:
        if not self.hub_id:
            raise PayloadError("hubId is required.")
        if self.uptime_seconds < 0:
            raise PayloadError("uptimeSeconds can't be negative.")

    def to_json(self) -> dict[str, Any]:
        body: dict[str, Any] = {
            "hubId": self.hub_id,
            "sentAt": format_utc(self.sent_at),
            "clientSha": self.client_sha,
            "uptimeSeconds": self.uptime_seconds,
            "usbInputDevices": list(self.usb_input_devices),
        }
        if self.scanners is not None:
            body["scanners"] = [s.to_json() for s in self.scanners]
        return body
