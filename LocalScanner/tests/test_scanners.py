import logging
import threading
import time

from conftest import PORT_IN, PORT_OUT, FakeDongle, no_grab

from cupboard_pi.scanners import KeyDecoder, ScannerReader, scanner_ports, scanner_presence

SHIFT, ENTER = 42, 28


def feed_all(decoder, events):
    out = [decoder.feed(code, value) for code, value in events]
    return [s for s in out if s is not None]


def press(code):
    return [(code, 1), (code, 0)]


def test_decodes_digits_then_enter():
    events = [e for c in (6, 11, 11, 11) for e in press(c)] + press(ENTER)
    assert feed_all(KeyDecoder(), events) == ["5000"]


def test_digits_ignore_shift_azerty_layout():
    events = [(SHIFT, 1), *press(6), (SHIFT, 0), (SHIFT, 1), *press(11), (SHIFT, 0), *press(ENTER)]
    assert feed_all(KeyDecoder(), events) == ["50"]


def test_letters_follow_shift_and_unknown_keys_are_marked():
    events = [(SHIFT, 1), *press(30), (SHIFT, 0), *press(30), *press(200), *press(ENTER)]
    assert feed_all(KeyDecoder(), events) == ["Aa<200>"]


def test_autorepeat_and_empty_enter_give_nothing():
    assert feed_all(KeyDecoder(), [(2, 2), *press(ENTER)]) == []


def test_a_stale_partial_scan_is_dropped(caplog):
    now = [0.0]
    decoder = KeyDecoder(clock=lambda: now[0])
    feed_all(decoder, press(2) + press(3))
    now[0] = 10.0
    with caplog.at_level(logging.WARNING):
        assert feed_all(decoder, press(4) + press(ENTER)) == ["3"]
    assert "partial scan" in caplog.text


def test_scanner_ports_skip_duplicates_and_non_usb(by_path):
    for name in (
        f"{PORT_IN}-event-kbd",
        "platform-3f980000.usb-usbv2-0:1.5:1.0-event-kbd",
        "platform-3f902000.hdmi-event",
        "platform-3f902000.gpio-event-kbd",
    ):
        (by_path / name).touch()
    assert scanner_ports(by_path) == {PORT_IN}


def test_presence_true_and_false(by_path):
    (by_path / f"{PORT_IN}-event-kbd").touch()
    presence = scanner_presence({PORT_IN: "scanner-in", PORT_OUT: "scanner-out"}, by_path)
    assert [(p.device_id, p.present) for p in presence] == [("scanner-in", True), ("scanner-out", False)]
    assert presence[1].to_json() == {"deviceId": "scanner-out", "present": False}


def run_reader(by_path, ports, scans):
    stop = threading.Event()
    reader = ScannerReader(ports, lambda d, b: scans.append((d, b)), by_path, grab=no_grab)
    thread = threading.Thread(target=reader.run, args=(stop,))
    thread.start()
    return stop, thread


def wait_for(cond, timeout=5.0):
    deadline = time.monotonic() + timeout
    while time.monotonic() < deadline:
        if cond():
            return True
        time.sleep(0.02)
    return False


def test_reads_each_dongle_as_its_own_device(by_path):
    dongle_in, dongle_out = FakeDongle(by_path, PORT_IN), FakeDongle(by_path, PORT_OUT)
    scans = []
    stop, thread = run_reader(by_path, {PORT_IN: "scanner-in", PORT_OUT: "scanner-out"}, scans)
    try:
        dongle_in.type("5000354922831")
        dongle_out.type("20055363", shift_digits=True)
        assert wait_for(lambda: len(scans) == 2)
        assert sorted(scans) == [("scanner-in", "5000354922831"), ("scanner-out", "20055363")]
    finally:
        stop.set()
        thread.join(5)


def test_unmapped_port_is_logged_and_ignored(by_path, caplog):
    FakeDongle(by_path, "platform-3f980000.usb-usb-0:1.3:1.0")
    scans = []
    with caplog.at_level(logging.ERROR):
        stop, thread = run_reader(by_path, {PORT_IN: "scanner-in"}, scans)
        try:
            assert wait_for(lambda: "unmapped scanner port" in caplog.text)
        finally:
            stop.set()
            thread.join(5)
    assert scans == []


def test_unplug_and_replug_resumes(by_path, caplog):
    dongle = FakeDongle(by_path, PORT_IN)
    scans = []
    with caplog.at_level(logging.INFO):
        stop, thread = run_reader(by_path, {PORT_IN: "scanner-in"}, scans)
        try:
            dongle.type("20055363")
            assert wait_for(lambda: len(scans) == 1)
            dongle.unplug()
            assert wait_for(lambda: "disconnected" in caplog.text)
            dongle.fifo.unlink()
            dongle = FakeDongle(by_path, PORT_IN)
            dongle.type("5000354922831")
            assert wait_for(lambda: len(scans) == 2)
        finally:
            stop.set()
            thread.join(5)
    assert scans[1] == ("scanner-in", "5000354922831")
