"""The hub's heartbeat (#68): one on startup, then every HEARTBEAT_INTERVAL_SECONDS (default 120).

Heartbeats are never queued or retried: a failed one is logged as a warning and the next goes on schedule (the
offline alert only needs *some* heartbeat within its threshold). The loop runs in its own thread, so it never
blocks, or is blocked by, scanner reading or scan sending.
"""

from __future__ import annotations

import logging
import os
import threading
import time
from collections.abc import Callable, Mapping
from datetime import UTC, datetime
from pathlib import Path

from cupboard_pi.config import ConfigError
from cupboard_pi.identity import Identity
from cupboard_pi.ingest_client import IngestClient
from cupboard_pi.payloads import HEARTBEAT_PATH, Heartbeat, ScannerPresence
from cupboard_pi.scanners import scanner_presence

log = logging.getLogger(__name__)

DEFAULT_INTERVAL_SECONDS = 120
MIN_INTERVAL_SECONDS = 10
INPUT_DEVICES = Path("/proc/bus/input/devices")


def heartbeat_interval(env: Mapping[str, str] | None = None) -> int:
    env = os.environ if env is None else env
    raw = env.get("HEARTBEAT_INTERVAL_SECONDS", "").strip()
    if not raw:
        return DEFAULT_INTERVAL_SECONDS
    if not raw.isdigit() or int(raw) < MIN_INTERVAL_SECONDS:
        raise ConfigError(
            f"HEARTBEAT_INTERVAL_SECONDS must be a whole number of seconds, {MIN_INTERVAL_SECONDS} or more."
        )
    return int(raw)


def usb_input_device_names(path: Path = INPUT_DEVICES) -> list[str]:
    """Names of the USB input devices the kernel sees (for information). Empty if it can't tell."""
    try:
        text = path.read_text()
    except OSError:
        return []
    names = []
    for block in text.split("\n\n"):
        name, phys = "", ""
        for line in block.splitlines():
            if line.startswith("N: Name="):
                name = line.removeprefix("N: Name=").strip('"')
            elif line.startswith("P: Phys="):
                phys = line.removeprefix("P: Phys=")
        if phys.startswith("usb-"):
            names.append(name)
    return sorted(names)


class HeartbeatLoop:
    def __init__(
        self,
        identity: Identity,
        client: IngestClient,
        sha: str,
        interval_seconds: int = DEFAULT_INTERVAL_SECONDS,
        presence: Callable[[], list[ScannerPresence]] | None = None,
        usb_devices: Callable[[], list[str]] = usb_input_device_names,
        started: float | None = None,
    ):
        self._identity = identity
        self._client = client
        self._sha = sha
        self._interval = interval_seconds
        self._presence = presence or (lambda: scanner_presence(identity.ports))
        self._usb_devices = usb_devices
        self._started = time.monotonic() if started is None else started
        self._failing = True  # so the first success is logged at INFO

    def run(self, stop: threading.Event) -> None:
        self.send_once()
        while not stop.wait(self._interval):
            self.send_once()

    def send_once(self) -> bool:
        """Sends one heartbeat. Never raises: a failure is a warning, and the next one goes on schedule."""
        try:
            heartbeat = Heartbeat(
                hub_id=self._identity.hub_id,
                sent_at=datetime.now(UTC),
                client_sha=self._sha,
                uptime_seconds=int(time.monotonic() - self._started),
                usb_input_devices=self._usb_devices(),
                scanners=self._presence(),
            )
            response = self._client.post_json(HEARTBEAT_PATH, heartbeat.to_json())
            ok = response.ok
            failure = None if ok else f"HTTP {response.status}: {response.body[:300]}"
        except Exception as e:  # network down, timeout, a bug building it: never stop the loop
            ok, failure = False, f"{type(e).__name__}: {e}"
        if ok:
            level = logging.INFO if self._failing else logging.DEBUG
            present = ", ".join(
                f"{s.device_id}={'yes' if s.present else 'no'}" for s in heartbeat.scanners or []
            )
            log.log(level, "Heartbeat sent (scanners present: %s)", present or "none mapped")
            self._failing = False
        else:
            log.warning(
                "Heartbeat failed (%s); not retried, the next is due in %d s", failure, self._interval
            )
            self._failing = True
        return ok
