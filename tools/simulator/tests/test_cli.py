import io
import json
import uuid
from contextlib import redirect_stdout

import cupboard_pi.payloads as pi_payloads
import pytest
from cupboard_pi.ingest_client import IngestResponse

from cupboard_simulator import cli

ENV = {"HUB_ID": "cupboard-pi", "SCANNERS": "scanner-in=in,scanner-out=out"}
SECRET = "super-secret-function-key"


class Recorder:
    def __init__(self, status=202):
        self.requests = []
        self.status = status

    def __call__(self, request, timeout):
        self.requests.append(request)
        return IngestResponse(self.status, "")


def run(argv, transport=None, target=None):
    transport = transport or Recorder()
    calls = []

    def provider():
        calls.append(1)
        return "https://ingest.example.net", SECRET

    out = io.StringIO()
    with redirect_stdout(out):
        code = cli.main(argv, target=target or provider, transport=transport, env=ENV)
    return code, out.getvalue(), transport, calls


def test_scan_payload_comes_from_the_pi_models():
    code, _out, transport, _ = run(["scan", "--role", "in", "--barcode", "5000354922831"])
    assert code == 0
    request = transport.requests[0]
    assert request.url == "https://ingest.example.net" + pi_payloads.SCANS_PATH
    body = json.loads(request.body)
    assert set(body) == {"scanId", "barcode", "deviceId", "role", "scannedAt", "clientSha", "source"}
    assert body["deviceId"] == "scanner-in" and body["role"] == "in" and body["source"] == "simulator"
    assert uuid.UUID(body["scanId"])
    # The Pi's own model accepts it unchanged: same contract, no copy.
    pi_payloads.ScanEvent(
        body["scanId"],
        body["barcode"],
        body["deviceId"],
        body["role"],
        pi_payloads.ScanEvent.new("5000354922831", "d", "in", "x").scanned_at,
        body["clientSha"],
    )


def test_out_role_uses_the_out_device():
    _, _, transport, _ = run(["scan", "--role", "out", "--barcode", "20590093"])
    assert json.loads(transport.requests[0].body)["deviceId"] == "scanner-out"


def test_dry_run_sends_nothing_and_never_asks_for_the_key():
    code, out, transport, calls = run(["--dry-run", "scan", "--role", "in", "--barcode", "5000354922831"])
    assert code == 0
    assert transport.requests == []
    assert calls == []
    assert '"barcode": "5000354922831"' in out


def test_the_key_never_appears_in_output():
    _, out, transport, _ = run(["scan", "--role", "in", "--barcode", "5000354922831"])
    assert SECRET not in out
    assert transport.requests[0].headers["x-functions-key"] == SECRET  # sent, but only as the header


def test_invalid_barcode_is_refused_before_sending():
    code, _, transport, _ = run(["scan", "--role", "in", "--barcode", "5000354922832"])
    assert code == 2
    assert transport.requests == []


def test_phone_export_is_parsed_and_qr_codes_skipped(tmp_path):
    export = [
        {"value": "049733830119", "format": "upc_a", "formatName": "UPC-A", "timestamp": 1792188059156},
        {"value": "https://example.test/x", "format": "qr_code", "formatName": "QR Code", "timestamp": 1},
        {"value": "20590093", "format": "ean_8", "formatName": "EAN-8", "timestamp": 1792187995325},
    ]
    path = tmp_path / "export.json"
    path.write_text(json.dumps(export))
    code, out, transport, _ = run(["scans", "--file", str(path)])
    assert code == 0
    assert [json.loads(r.body)["barcode"] for r in transport.requests] == ["049733830119", "20590093"]
    assert "QR Code isn't a product barcode" in out


def test_simple_batch_with_roles(tmp_path):
    path = tmp_path / "batch.json"
    path.write_text(
        json.dumps([{"barcode": "5000354922831", "role": "in"}, {"barcode": "20590093", "role": "out"}])
    )
    _, _, transport, _ = run(["scans", "--file", str(path)])
    assert [json.loads(r.body)["role"] for r in transport.requests] == ["in", "out"]


def test_resend_sends_the_same_payload_again():
    _, _out, transport, _ = run(["scan", "--role", "in", "--barcode", "5000354922831"])
    first = transport.requests[0].body
    scan_id = json.loads(first)["scanId"]
    _, _, transport2, _ = run(["resend", "--scan-id", scan_id])
    assert transport2.requests[0].body == first


def test_heartbeat_payload_comes_from_the_pi_model():
    _, _, transport, _ = run(["heartbeat"])
    request = transport.requests[0]
    assert request.url.endswith(pi_payloads.HEARTBEAT_PATH)
    body = json.loads(request.body)
    assert set(body) == {"hubId", "sentAt", "clientSha", "uptimeSeconds", "usbInputDevices"}
    assert body["hubId"] == "cupboard-pi"


def test_non_2xx_makes_the_exit_code_fail():
    code, out, _, _ = run(["scan", "--role", "in", "--barcode", "5000354922831"], transport=Recorder(400))
    assert code == 1
    assert "HTTP 400" in out


@pytest.mark.parametrize("argv", [["scan", "--role", "in", "--barcode", "5000354922831"], ["heartbeat"]])
def test_missing_identity_config_is_an_error(argv):
    out = io.StringIO()
    with redirect_stdout(out):
        code = cli.main(argv, target=lambda: ("https://x", SECRET), transport=Recorder(), env={})
    assert code == 2
