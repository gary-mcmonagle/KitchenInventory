"""Entry point: ``python -m cupboard_pi``. Loads config, logs startup and exits (for now)."""

from __future__ import annotations

import logging
import os
import sys
from pathlib import Path

from cupboard_pi.config import Config, ConfigError

DEPLOYED_SHA_FILE = Path("/opt/cupboard-tracker/DEPLOYED_SHA")

log = logging.getLogger("cupboard_pi")


def deployed_sha() -> str:
    sha = os.environ.get("DEPLOYED_SHA", "").strip()
    if sha:
        return sha
    try:
        return DEPLOYED_SHA_FILE.read_text().strip() or "local"
    except OSError:
        return "local"


def main(argv: list[str] | None = None) -> int:
    logging.basicConfig(level=logging.INFO, format="%(asctime)s %(levelname)s %(name)s %(message)s")
    try:
        config = Config.from_env()
    except ConfigError as e:
        log.error("%s", e)
        return 2
    logging.getLogger().setLevel(config.log_level)
    log.info("cupboard-pi starting: sha=%s ingest=%s", deployed_sha(), config.ingest_url)
    # Scanner reading and heartbeats are added by their features.
    log.info("cupboard-pi stopping")
    return 0


if __name__ == "__main__":
    sys.exit(main())
