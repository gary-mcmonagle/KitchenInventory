"""cupboard-simulator: send scan events and heartbeats to ingest exactly as the Pi does.

The live environment is the real household system: simulated scans create real stock.
See tools/simulator/README.md and CLAUDE.md for the rules.
"""

from __future__ import annotations

import argparse
import json
import subprocess
import sys
import time
from collections.abc import Callable
from datetime import UTC, datetime
from pathlib import Path
from typing import Any

from cupboard_pi.config import Config, ConfigError
from cupboard_pi.identity import Identity
from cupboard_pi.ingest_client import IngestClient, IngestError, IngestResponse
from cupboard_pi.payloads import HEARTBEAT_PATH, SCANS_PATH, Heartbeat, PayloadError, ScanEvent

from cupboard_simulator import azure, sentlog
from cupboard_simulator.exports import load_batch

SOURCE = "simulator"
STARTED = time.monotonic()

# Swappable in tests: (base URL, key) provider and transport.
TargetProvider = Callable[[], tuple[str, str]]


def client_sha() -> str:
    try:
        sha = subprocess.run(
            ["git", "rev-parse", "--short", "HEAD"],  # noqa: S607
            capture_output=True,
            text=True,
            check=True,
            cwd=Path(__file__).parent,
        ).stdout.strip()
    except (OSError, subprocess.CalledProcessError):
        sha = "unknown"
    return f"simulator-{sha}"


def load_env_file(path: Path) -> dict[str, str]:
    """KEY=VALUE lines; only HUB_ID and SCANNERS are used. Values are never printed."""
    values: dict[str, str] = {}
    for line in path.read_text().splitlines():
        line = line.strip()
        if line and not line.startswith("#") and "=" in line:
            key, _, value = line.partition("=")
            if key.strip() in ("HUB_ID", "SCANNERS"):
                values[key.strip()] = value.strip()
    return values


class Sender:
    """Prints (dry run) or sends payloads with the Pi's ingest client."""

    def __init__(self, dry_run: bool, target: TargetProvider, transport: Any = None, out=None):
        self.dry_run = dry_run
        self._target = target
        self._transport = transport
        self._client: IngestClient | None = None
        self._out = out  # None means whatever sys.stdout is at print time
        self.failures = 0

    @property
    def out(self):
        return self._out or sys.stdout

    def _ingest(self) -> IngestClient:
        if self._client is None:
            url, key = self._target()
            config = Config(
                ingest_url=url, ingest_auth_header_name="x-functions-key", ingest_auth_header_value=key
            )
            kwargs = {"transport": self._transport} if self._transport else {}
            self._client = IngestClient(config, **kwargs)
        return self._client

    def send(self, path: str, payload: dict[str, Any], label: str) -> IngestResponse | None:
        if self.dry_run:
            print(f"[dry run] POST {path} {json.dumps(payload)}", file=self.out)
            return None
        try:
            response = self._ingest().post_json(path, payload)
        except IngestError as e:
            self.failures += 1
            print(f"{label}: FAILED ({e})", file=self.out)
            return None
        if not response.ok:
            self.failures += 1
        body = response.body.strip().replace("\n", " ")[:200]
        print(f"{label} → HTTP {response.status}{' ' + body if body else ''}", file=self.out)
        return response


def _send_scan(sender: Sender, event: ScanEvent) -> None:
    payload = event.to_json()
    label = f"{event.role} {event.barcode} scanId={event.scan_id}"
    response = sender.send(SCANS_PATH, payload, label)
    if response is not None:
        sentlog.record(payload)


def _parse_at(value: str | None) -> datetime | None:
    if not value:
        return None
    at = datetime.fromisoformat(value.replace("Z", "+00:00"))
    if at.tzinfo is None:
        raise argparse.ArgumentTypeError("--at needs a timezone, e.g. 2026-10-17T14:30:00Z")
    return at


def cmd_scan(args, identity: Identity, sender: Sender) -> None:
    event = ScanEvent.new(
        args.barcode,
        identity.device_for_role(args.role),
        args.role,
        client_sha(),
        _parse_at(args.at),
        {"source": SOURCE},
    )
    _send_scan(sender, event)


def cmd_scans(args, identity: Identity, sender: Sender) -> None:
    batch = load_batch(Path(args.file), args.role, args.keep_timestamps)
    for reason in batch.skipped:
        print(f"skipped {reason}", file=sender.out)
    sha = client_sha()
    for item in batch.items:
        try:
            event = ScanEvent.new(
                item.barcode,
                identity.device_for_role(item.role),
                item.role,
                sha,
                item.scanned_at,
                {"source": SOURCE},
            )
        except (PayloadError, ConfigError) as e:
            print(f"skipped {item.barcode}: {e}", file=sender.out)
            continue
        _send_scan(sender, event)


def cmd_resend(args, _identity: Identity, sender: Sender) -> None:
    payload = sentlog.find(args.scan_id)
    if payload is None:
        raise SystemExit(f"No sent scan with scanId {args.scan_id} in the simulator's log.")
    sender.send(SCANS_PATH, payload, f"resend {payload['role']} {payload['barcode']} scanId={args.scan_id}")


def cmd_heartbeat(args, identity: Identity, sender: Sender, sleep=time.sleep) -> None:
    deadline = time.monotonic() + args.for_minutes * 60 if args.every else None
    sha = client_sha()
    while True:
        heartbeat = Heartbeat(identity.hub_id, datetime.now(UTC), sha, int(time.monotonic() - STARTED))
        sender.send(HEARTBEAT_PATH, heartbeat.to_json(), f"heartbeat {identity.hub_id}")
        if deadline is None or time.monotonic() + args.every > deadline:
            return
        sleep(args.every)


def build_parser() -> argparse.ArgumentParser:
    p = argparse.ArgumentParser(prog="cupboard-simulator", description=__doc__.splitlines()[0])
    p.add_argument("--dry-run", action="store_true", help="print the payloads; send nothing")
    p.add_argument(
        "--env-file", type=Path, help="read HUB_ID and SCANNERS from this file (default: environment)"
    )
    sub = p.add_subparsers(dest="command", required=True)

    s = sub.add_parser("scan", help="send one scan event")
    s.add_argument("--role", choices=("in", "out"), required=True)
    s.add_argument("--barcode", required=True)
    s.add_argument("--at", help="scannedAt as ISO 8601 with a timezone (default: now)")
    s.set_defaults(func=cmd_scan)

    b = sub.add_parser("scans", help="send a batch from a JSON file")
    b.add_argument("--file", required=True)
    b.add_argument(
        "--role", choices=("in", "out"), default="in", help="role for entries without one (default: in)"
    )
    b.add_argument("--keep-timestamps", action="store_true", help="use the phone export's timestamps")
    b.set_defaults(func=cmd_scans)

    r = sub.add_parser("resend", help="resend an earlier scan unchanged (tests idempotency)")
    r.add_argument("--scan-id", required=True)
    r.set_defaults(func=cmd_resend)

    h = sub.add_parser("heartbeat", help="send heartbeats (once, or on a loop)")
    h.add_argument("--every", type=int, help="seconds between heartbeats (loop)")
    h.add_argument(
        "--for", dest="for_minutes", type=float, default=10, help="loop duration in minutes (default 10)"
    )
    h.set_defaults(func=cmd_heartbeat)
    return p


def main(
    argv: list[str] | None = None,
    target: TargetProvider = azure.ingest_target,
    transport: Any = None,
    env: dict[str, str] | None = None,
) -> int:
    args = build_parser().parse_args(argv)
    try:
        values = load_env_file(args.env_file) if args.env_file else env
        identity = Identity.from_env(values)
        sender = Sender(args.dry_run, target, transport)
        args.func(args, identity, sender)
    except (ConfigError, PayloadError, azure.AzureError, ValueError, OSError) as e:
        print(f"ERROR: {e}", file=sys.stderr)
        return 2
    return 1 if sender.failures else 0
