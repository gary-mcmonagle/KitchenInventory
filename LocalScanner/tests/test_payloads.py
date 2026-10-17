import uuid
from datetime import UTC, datetime, timedelta, timezone

import pytest

from cupboard_pi.payloads import (
    Heartbeat,
    PayloadError,
    ScanEvent,
    ScannerPresence,
    format_utc,
    is_valid_barcode,
)

# Real barcodes from the Open Food Facts spike sample (#28).
VALID = ["5000354922831", "4056489899112", "20590093", "049733830119", "615357122468"]


@pytest.mark.parametrize("barcode", VALID)
def test_real_barcodes_are_valid(barcode):
    assert is_valid_barcode(barcode)


@pytest.mark.parametrize(
    "barcode",
    [
        "5000354922832",
        "20590094",
        "049733830110",
        "123",
        "50003549228311",
        "50003549228a1",
        "",
        "\uff15000354922831",  # full-width 5: isdigit() is True, but it isn't a barcode
    ],
)
def test_invalid_barcodes_are_rejected(barcode):
    assert not is_valid_barcode(barcode)


def test_scan_event_json_shape():
    at = datetime(2026, 10, 16, 18, 42, 7, 123000, tzinfo=UTC)
    event = ScanEvent.new("5000354922831", "scanner-in", "in", "abc1234", at, {"source": "simulator"})
    body = event.to_json()
    assert body == {
        "scanId": body["scanId"],
        "barcode": "5000354922831",
        "deviceId": "scanner-in",
        "role": "in",
        "scannedAt": "2026-10-16T18:42:07.123Z",
        "clientSha": "abc1234",
        "source": "simulator",
    }
    assert uuid.UUID(body["scanId"]).version == 4


def test_each_new_scan_gets_its_own_id():
    a = ScanEvent.new("5000354922831", "scanner-in", "in", "x")
    b = ScanEvent.new("5000354922831", "scanner-in", "in", "x")
    assert a.scan_id != b.scan_id


def test_bad_role_and_barcode_are_rejected():
    with pytest.raises(PayloadError):
        ScanEvent.new("5000354922831", "scanner-in", "both", "x")
    with pytest.raises(PayloadError):
        ScanEvent.new("5000354922832", "scanner-in", "in", "x")


def test_extra_fields_cant_override_contract_fields():
    event = ScanEvent.new("5000354922831", "scanner-in", "in", "x", extra={"role": "out"})
    with pytest.raises(PayloadError):
        event.to_json()


def test_timestamps_are_utc_with_z():
    bst = timezone(timedelta(hours=1))
    assert format_utc(datetime(2026, 10, 16, 19, 42, 7, tzinfo=bst)) == "2026-10-16T18:42:07.000Z"
    with pytest.raises(PayloadError):
        format_utc(datetime(2026, 10, 16, 19, 42, 7))


def test_heartbeat_json_shape_without_and_with_scanners():
    at = datetime(2026, 10, 16, 18, 0, tzinfo=UTC)
    hb = Heartbeat("cupboard-pi", at, "abc1234", 3600)
    assert hb.to_json() == {
        "hubId": "cupboard-pi",
        "sentAt": "2026-10-16T18:00:00.000Z",
        "clientSha": "abc1234",
        "uptimeSeconds": 3600,
        "usbInputDevices": [],
    }
    hb2 = Heartbeat(
        "cupboard-pi", at, "abc1234", 1, ["Barcode Scanner"], [ScannerPresence("scanner-in", True)]
    )
    assert hb2.to_json()["scanners"] == [{"deviceId": "scanner-in", "present": True}]
    assert hb2.to_json()["usbInputDevices"] == ["Barcode Scanner"]
