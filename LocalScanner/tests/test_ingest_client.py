import json

import pytest

from cupboard_pi.config import Config
from cupboard_pi.ingest_client import HttpRequest, IngestClient, IngestError, IngestResponse

CONFIG = Config("https://ingest.example.net", "x-custom-auth", "the-key")


class FakeTransport:
    def __init__(self, response=None):
        self.response = response or IngestResponse(202, "")
        self.calls: list[tuple[HttpRequest, float]] = []

    def __call__(self, request, timeout):
        self.calls.append((request, timeout))
        if isinstance(self.response, Exception):
            raise self.response
        return self.response


def test_posts_json_with_the_configured_auth_header():
    transport = FakeTransport()
    client = IngestClient(CONFIG, transport, timeout=5)
    response = client.post_json("/api/scans", {"barcode": "5000354922831"})

    request, timeout = transport.calls[0]
    assert request.url == "https://ingest.example.net/api/scans"
    assert request.headers["x-custom-auth"] == "the-key"
    assert request.headers["Content-Type"] == "application/json"
    assert json.loads(request.body) == {"barcode": "5000354922831"}
    assert timeout == 5
    assert response.ok


def test_http_error_statuses_are_returned():
    transport = FakeTransport(IngestResponse(400, '{"error":"bad barcode"}'))
    response = IngestClient(CONFIG, transport).post_json("api/scans", {})
    assert response.status == 400
    assert not response.ok


def test_network_failures_raise_ingest_error():
    transport = FakeTransport(IngestError("Could not reach ingest: URLError"))
    with pytest.raises(IngestError):
        IngestClient(CONFIG, transport).post_json("api/scans", {})
