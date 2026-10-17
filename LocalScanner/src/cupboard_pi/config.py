"""Configuration from environment variables. Values are never logged."""

from __future__ import annotations

import os
from collections.abc import Mapping
from dataclasses import dataclass, field

REQUIRED = ("INGEST_URL", "INGEST_AUTH_HEADER_NAME", "INGEST_AUTH_HEADER_VALUE")
# The auth header carries a function key, so only HTTPS, except a local `func start`.
ALLOWED_URL_PREFIXES = ("https://", "http://localhost:", "http://127.0.0.1:")


class ConfigError(Exception):
    """Raised when required settings are missing. The message names variables, never values."""


@dataclass(frozen=True)
class Config:
    ingest_url: str
    ingest_auth_header_name: str
    ingest_auth_header_value: str = field(repr=False)  # a secret: kept out of repr and logs
    log_level: str = "INFO"

    @classmethod
    def from_env(cls, env: Mapping[str, str] | None = None) -> Config:
        env = os.environ if env is None else env
        missing = [name for name in REQUIRED if not env.get(name, "").strip()]
        if missing:
            raise ConfigError("Missing environment variables: " + ", ".join(missing))
        ingest_url = env["INGEST_URL"].strip().rstrip("/")
        if not ingest_url.startswith(ALLOWED_URL_PREFIXES):
            raise ConfigError(
                "INGEST_URL must start with https:// (or http://localhost for local development)"
            )
        return cls(
            ingest_url=ingest_url,
            ingest_auth_header_name=env["INGEST_AUTH_HEADER_NAME"].strip(),
            ingest_auth_header_value=env["INGEST_AUTH_HEADER_VALUE"].strip(),
            log_level=env.get("LOG_LEVEL", "INFO").strip().upper() or "INFO",
        )
