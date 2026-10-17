import logging
import threading
import time

import pytest
from conftest import PORT_IN, PORT_OUT

from cupboard_pi.config import Config, ConfigError
from cupboard_pi.heartbeat import HeartbeatLoop, heartbeat_interval, usb_input_device_names
from cupboard_pi.identity import Identity
from cupboard_pi.ingest_client import IngestClient, IngestError, IngestResponse
from cupboard_pi.payloads import ScannerPresence

CONFIG = Config("https://ingest.example.net", "x-functions-key", "secret-value")
IDENTITY = Identity.from_env(
    {"HUB_ID": "cupboard-pi", "SCANNERS": f"scanner-in=in@{PORT_IN},scanner-out=out@{PORT_OUT}"},
    require_ports=True,
)


class Recorder:
    def __init__(self, fail=None):
        self.requests = []
        self.fail = fail  # an exception or a status code

    def __call__(self, request, timeout):
        self.requests.append(request)
        if isinstance(self.fail, Exception):
            raise self.fail
        return IngestResponse(self.fail or 202, '{"hubId":"cupboard-pi"}')


def loop(transport, **kw):
    kw.setdefault(
        "presence", lambda: [ScannerPresence("scanner-in", True), ScannerPresence("scanner-out", False)]
    )
    kw.setdefault("usb_devices", lambda: ["USB Adapter USB Device"])
    return HeartbeatLoop(IDENTITY, IngestClient(CONFIG, transport), "abc1234", **kw)


@pytest.mark.parametrize(("env", "expected"), [({}, 120), ({"HEARTBEAT_INTERVAL_SECONDS": "30"}, 30)])
def test_interval_defaults_to_2_minutes(env, expected):
    assert heartbeat_interval(env) == expected


@pytest.mark.parametrize("value", ["9", "0", "-5", "two minutes", "1.5"])
def test_bad_interval_refuses_to_start(value):
    with pytest.raises(ConfigError, match="HEARTBEAT_INTERVAL_SECONDS"):
        heartbeat_interval({"HEARTBEAT_INTERVAL_SECONDS": value})


def test_payload_matches_the_shared_contract():
    t = Recorder()
    assert loop(t, started=time.monotonic() - 3600).send_once()
    request = t.requests[0]
    assert request.url == "https://ingest.example.net/api/heartbeat"
    import json

    body = json.loads(request.body)
    assert set(body) == {"hubId", "sentAt", "clientSha", "uptimeSeconds", "usbInputDevices", "scanners"}
    assert body["hubId"] == "cupboard-pi" and body["clientSha"] == "abc1234"
    assert body["sentAt"].endswith("Z") and 3599 <= body["uptimeSeconds"] <= 3601
    assert body["usbInputDevices"] == ["USB Adapter USB Device"]
    assert body["scanners"] == [
        {"deviceId": "scanner-in", "present": True},
        {"deviceId": "scanner-out", "present": False},
    ]


def test_presence_comes_from_the_scanner_ports_by_default(by_path):
    (by_path / f"{PORT_OUT}-event-kbd").touch()
    t = Recorder()
    from cupboard_pi.scanners import scanner_presence

    loop(t, presence=lambda: scanner_presence(IDENTITY.ports, by_path)).send_once()
    import json

    assert json.loads(t.requests[0].body)["scanners"] == [
        {"deviceId": "scanner-in", "present": False},
        {"deviceId": "scanner-out", "present": True},
    ]


@pytest.mark.parametrize("fail", [IngestError("Could not reach ingest: URLError"), 503])
def test_a_failure_is_a_warning_and_not_retried(fail, caplog):
    t = Recorder(fail=fail)
    with caplog.at_level(logging.WARNING):
        assert loop(t).send_once() is False
    assert len(t.requests) == 1
    assert "Heartbeat failed" in caplog.text and "secret-value" not in caplog.text


def test_an_empty_usb_list_is_fine():
    t = Recorder()
    assert loop(t, usb_devices=lambda: []).send_once()


def test_sends_on_start_then_on_schedule_and_stops_promptly():
    t = Recorder()
    stop = threading.Event()
    thread = threading.Thread(target=loop(t, interval_seconds=0.1).run, args=(stop,))
    thread.start()
    time.sleep(0.35)
    stop.set()
    thread.join(2)
    assert not thread.is_alive()
    assert 3 <= len(t.requests) <= 5


def test_a_slow_heartbeat_never_blocks_scan_queueing(tmp_path):
    from cupboard_pi.scan_queue import ScanQueue

    release = threading.Event()

    def stuck(request, timeout):
        release.wait(5)
        return IngestResponse(202, "{}")

    stop = threading.Event()
    thread = threading.Thread(target=loop(stuck).run, args=(stop,))
    thread.start()
    q = ScanQueue(tmp_path / "q.sqlite3")
    try:
        started = time.monotonic()
        q.add("id-1", {})
        assert time.monotonic() - started < 1 and q.depth() == 1
    finally:
        release.set()
        stop.set()
        thread.join(5)


def test_usb_device_names_from_the_kernel_list(tmp_path):
    devices = tmp_path / "devices"
    devices.write_text(
        'I: Bus=0003\nN: Name="USB Adapter USB Device"\nP: Phys=usb-3f980000.usb-1.2/input0\n\n'
        'I: Bus=0019\nN: Name="vc4-hdmi"\nP: Phys=vc4-hdmi/input0\n\n'
        'N: Name="USB Adapter USB Device"\nP: Phys=usb-3f980000.usb-1.5/input0\n'
    )
    assert usb_input_device_names(devices) == ["USB Adapter USB Device", "USB Adapter USB Device"]
    assert usb_input_device_names(tmp_path / "missing") == []
