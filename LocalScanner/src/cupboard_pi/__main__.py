"""Entry point: ``python -m cupboard_pi`` (the ``cupboard-pi`` service).

Reads both scanners and queues each valid scan durably, while a separate sender posts the queue to
ingest, so reading never waits on the network. Runs until stopped (SIGTERM from systemd, or Ctrl-C).
If either loop crashes, it exits non-zero and systemd restarts it.
"""

from __future__ import annotations

import logging
import os
import signal
import sys
import threading
from pathlib import Path

from cupboard_pi.config import Config, ConfigError
from cupboard_pi.identity import Identity
from cupboard_pi.ingest_client import IngestClient
from cupboard_pi.payloads import ScanEvent, is_valid_barcode
from cupboard_pi.scan_queue import DEFAULT_PATH, WARN_DEPTH, ScanQueue
from cupboard_pi.scanners import BY_PATH, ScannerReader
from cupboard_pi.sender import Sender

DEPLOYED_SHA_FILENAME = "DEPLOYED_SHA"

log = logging.getLogger("cupboard_pi")


def deployed_sha(package_dir: Path | None = None) -> str:
    """The commit this release was built from.

    ``DEPLOYED_SHA`` in the environment wins. Otherwise deploy/pi.sh puts a ``DEPLOYED_SHA`` file in the
    release folder (``/opt/cupboard-tracker/releases/<sha>/``), above the venv this package is installed in,
    so the nearest one up from the package is this release's own SHA. ``local`` when there's neither.
    """
    sha = os.environ.get("DEPLOYED_SHA", "").strip()
    if sha:
        return sha
    package_dir = package_dir or Path(__file__).resolve().parent
    for folder in package_dir.parents:
        try:
            return (folder / DEPLOYED_SHA_FILENAME).read_text().strip() or "local"
        except OSError:
            continue
    return "local"


def log_format(env: dict[str, str] | None = None) -> str:
    """Under systemd (JOURNAL_STREAM is set) the journal adds the timestamp, so don't log a second one."""
    env = os.environ if env is None else env
    if env.get("JOURNAL_STREAM"):
        return "%(levelname)s %(name)s %(message)s"
    return "%(asctime)s %(levelname)s %(name)s %(message)s"


def make_scan_handler(identity: Identity, queue: ScanQueue, sha: str):
    """Validates and queues one barcode read. Quick: it only writes to the local queue."""

    def on_scan(device_id: str, barcode: str) -> None:
        if not is_valid_barcode(barcode):
            log.warning("Invalid barcode read on %s: %r (not sent)", device_id, barcode[:64])
            return
        event = ScanEvent.new(barcode, device_id, identity.devices[device_id], sha)
        depth = queue.add(event.scan_id, event.to_json())
        log.info(
            "Scan %s on %s (%s), scan ID %s; queue depth %d",
            barcode,
            device_id,
            event.role,
            event.scan_id,
            depth,
        )
        if depth > WARN_DEPTH and depth % 100 == 1:
            log.warning(
                "The scan queue has %d scans waiting (over %d): is ingest reachable?", depth, WARN_DEPTH
            )

    return on_scan


def main(
    argv: list[str] | None = None,
    stop: threading.Event | None = None,
    by_path: Path = BY_PATH,
    grab=None,
) -> int:
    logging.basicConfig(level=logging.INFO, format=log_format())
    try:
        config = Config.from_env()
        identity = Identity.from_env(require_ports=True)
    except ConfigError as e:
        log.error("%s", e)
        return 2
    logging.getLogger().setLevel(config.log_level)
    queue_path = Path(os.environ.get("SCAN_QUEUE_PATH", "").strip() or DEFAULT_PATH)
    try:
        queue = ScanQueue(queue_path)
    except Exception as e:  # e.g. the directory is missing or not writable
        log.error("Can't open the scan queue at %s: %s", queue_path, e)
        return 2

    if stop is None:  # running as the service: stop on SIGTERM (systemd) or Ctrl-C
        stop = threading.Event()
        for sig in (signal.SIGTERM, signal.SIGINT):
            signal.signal(sig, lambda _signum, _frame: stop.set())

    sha = deployed_sha()
    log.info("cupboard-pi starting: sha=%s ingest=%s", sha, config.ingest_url)
    log.info(
        "Scanners: %s; queue %s (%d waiting)",
        ", ".join(
            f"{d}={identity.devices[d]}@{p}" for p, d in sorted(identity.ports.items(), key=lambda i: i[1])
        ),
        queue_path,
        queue.depth(),
    )
    crashed: list[str] = []

    def guard(name: str, target) -> threading.Thread:
        def run() -> None:
            try:
                target(stop)
            except Exception:
                log.exception("The %s loop crashed; stopping so systemd restarts the client", name)
                crashed.append(name)
                stop.set()

        return threading.Thread(target=run, name=name)

    handler = make_scan_handler(identity, queue, sha)
    reader = ScannerReader(identity.ports, handler, by_path, **({"grab": grab} if grab else {}))
    threads = [guard("scanner", reader.run), guard("sender", Sender(queue, IngestClient(config)).run)]
    for t in threads:
        t.start()
    stop.wait()
    for t in threads:
        t.join(timeout=30)
    queue.close()
    log.info("cupboard-pi stopping")
    return 1 if crashed else 0


if __name__ == "__main__":
    sys.exit(main())
