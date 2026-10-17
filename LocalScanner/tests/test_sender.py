import threading
import time

from conftest import PORT_IN, FakeDongle, no_grab

from cupboard_pi.config import Config
from cupboard_pi.ingest_client import IngestClient, IngestError, IngestResponse
from cupboard_pi.scan_queue import ScanQueue
from cupboard_pi.scanners import ScannerReader
from cupboard_pi.sender import MAX_RETRY_SECONDS, Sender, next_delay

CONFIG = Config("https://ingest.example.net", "x-functions-key", "secret-value")


class FakeTransport:
    def __init__(self, statuses):
        self.statuses = list(statuses)
        self.sent = []

    def __call__(self, request, timeout):
        self.sent.append(request)
        status = self.statuses.pop(0) if self.statuses else 202
        if status == 0:
            raise IngestError("Could not reach ingest: URLError")
        return IngestResponse(
            status, '{"errors":["barcode check digit is wrong."]}' if status == 400 else "{}"
        )


def run_sender(queue, transport, until, waits=None):
    stop = threading.Event()
    if waits is not None:  # record back-off waits instead of sleeping
        real_wait = stop.wait

        def fake_wait(timeout=None):
            waits.append(timeout)
            return real_wait(0)

        stop.wait = fake_wait
    thread = threading.Thread(target=Sender(queue, IngestClient(CONFIG, transport)).run, args=(stop,))
    thread.start()
    deadline = time.monotonic() + 5
    while time.monotonic() < deadline and not until():
        time.sleep(0.01)
    stop.set()
    queue.added.set()
    thread.join(5)
    return stop


def bodies(transport):
    return [r.body.decode() for r in transport.sent]


def test_sends_in_order_and_deletes_after_2xx(tmp_path):
    q = ScanQueue(tmp_path / "q.sqlite3")
    for i in range(3):
        q.add(f"id-{i}", {"scanId": f"id-{i}"})
    t = FakeTransport([202, 202, 202])
    run_sender(q, t, lambda: q.depth() == 0)
    assert [b for b in bodies(t)] == [f'{{"scanId":"id-{i}"}}' for i in range(3)]
    assert t.sent[0].url == "https://ingest.example.net/api/scans"


def test_failures_keep_the_scan_and_back_off(tmp_path):
    q = ScanQueue(tmp_path / "q.sqlite3")
    q.add("id-1", {"scanId": "id-1"})
    q.add("id-2", {"scanId": "id-2"})
    t = FakeTransport([0, 503, 401, 202, 202])
    waits = []
    run_sender(q, t, lambda: q.depth() == 0, waits)
    assert bodies(t) == ['{"scanId":"id-1"}'] * 4 + ['{"scanId":"id-2"}']  # same payload resent, in order
    assert waits[:3] == [2.0, 4.0, 8.0]


def test_backoff_is_capped_at_5_minutes():
    delay, seen = 0.0, []
    for _ in range(12):
        delay = next_delay(delay)
        seen.append(delay)
    assert seen[:4] == [2, 4, 8, 16] and max(seen) == MAX_RETRY_SECONDS == 300


def test_an_invalid_scan_is_set_aside_and_the_next_is_sent(tmp_path):
    q = ScanQueue(tmp_path / "q.sqlite3")
    q.add("id-1", {"scanId": "id-1"})
    q.add("id-2", {"scanId": "id-2"})
    t = FakeTransport([400, 202])
    run_sender(q, t, lambda: q.depth() == 0)
    assert q.rejected_count() == 1 and len(t.sent) == 2


def test_a_stuck_send_never_blocks_the_scanner(tmp_path, by_path):
    """Reading and queueing carry on while the sender is stuck in a request."""
    q = ScanQueue(tmp_path / "q.sqlite3")
    release = threading.Event()

    def stuck_transport(request, timeout):
        release.wait(10)
        return IngestResponse(202, "{}")

    stop = threading.Event()
    sender = threading.Thread(target=Sender(q, IngestClient(CONFIG, stuck_transport)).run, args=(stop,))
    reader = ScannerReader(
        {PORT_IN: "scanner-in"}, lambda d, b: q.add(b + str(q.depth()), {}), by_path, no_grab
    )
    reader_thread = threading.Thread(target=reader.run, args=(stop,))
    dongle = FakeDongle(by_path, PORT_IN)
    sender.start()
    reader_thread.start()
    try:
        for code in ("20055363", "5000354922831", "20719395"):
            dongle.type(code)
        deadline = time.monotonic() + 5
        while time.monotonic() < deadline and q.depth() < 3:
            time.sleep(0.02)
        assert q.depth() == 3  # all three queued although the first send hasn't returned
    finally:
        release.set()
        stop.set()
        q.added.set()
        sender.join(5)
        reader_thread.join(5)
