"""Entry point: ``python -m cupboard_pi`` (the ``cupboard-pi`` service). Loads config, logs startup, runs
until stopped (SIGTERM from systemd, or Ctrl-C)."""

from __future__ import annotations

import logging
import os
import signal
import sys
import threading
from pathlib import Path

from cupboard_pi.config import Config, ConfigError

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


def main(argv: list[str] | None = None, stop: threading.Event | None = None) -> int:
    logging.basicConfig(level=logging.INFO, format=log_format())
    try:
        config = Config.from_env()
    except ConfigError as e:
        log.error("%s", e)
        return 2
    logging.getLogger().setLevel(config.log_level)

    if stop is None:  # running as the service: stop on SIGTERM (systemd) or Ctrl-C
        stop = threading.Event()
        for sig in (signal.SIGTERM, signal.SIGINT):
            signal.signal(sig, lambda _signum, _frame: stop.set())

    log.info("cupboard-pi starting: sha=%s ingest=%s", deployed_sha(), config.ingest_url)
    # Scanner reading and heartbeats are added by their features and run until `stop` is set.
    stop.wait()
    log.info("cupboard-pi stopping")
    return 0


if __name__ == "__main__":
    sys.exit(main())
