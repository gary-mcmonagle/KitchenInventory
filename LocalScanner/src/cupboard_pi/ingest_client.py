"""Sends JSON to the ingest Function app with the configured auth header."""

from __future__ import annotations

import json
import urllib.error
import urllib.request
from collections.abc import Callable, Mapping
from dataclasses import dataclass
from typing import Any

from cupboard_pi.config import Config


@dataclass(frozen=True)
class HttpRequest:
    url: str
    headers: Mapping[str, str]
    body: bytes


@dataclass(frozen=True)
class IngestResponse:
    status: int
    body: str

    @property
    def ok(self) -> bool:
        return 200 <= self.status < 300


class IngestError(Exception):
    """The request didn't get an HTTP response (network down, DNS, timeout)."""


Transport = Callable[[HttpRequest, float], IngestResponse]


def urllib_transport(request: HttpRequest, timeout: float) -> IngestResponse:
    """POST with the standard library. HTTP error statuses are returned, not raised."""
    # The URL's scheme is checked in Config (https, or http://localhost), so S310 doesn't apply.
    req = urllib.request.Request(  # noqa: S310
        request.url, data=request.body, headers=dict(request.headers), method="POST"
    )
    try:
        with urllib.request.urlopen(req, timeout=timeout) as resp:  # noqa: S310
            return IngestResponse(resp.status, resp.read().decode("utf-8", "replace"))
    except urllib.error.HTTPError as e:
        return IngestResponse(e.code, e.read().decode("utf-8", "replace"))
    except (urllib.error.URLError, TimeoutError, OSError) as e:
        raise IngestError(f"Could not reach ingest: {type(e).__name__}") from e


class IngestClient:
    """POSTs JSON payloads to ingest paths (e.g. ``/api/scans``) with the auth header from config."""

    def __init__(self, config: Config, transport: Transport = urllib_transport, timeout: float = 10.0):
        self._config = config
        self._transport = transport
        self._timeout = timeout

    def post_json(self, path: str, payload: Any) -> IngestResponse:
        request = HttpRequest(
            url=self._config.ingest_url + "/" + path.lstrip("/"),
            headers={
                "Content-Type": "application/json",
                self._config.ingest_auth_header_name: self._config.ingest_auth_header_value,
            },
            body=json.dumps(payload, separators=(",", ":")).encode("utf-8"),
        )
        return self._transport(request, self._timeout)
