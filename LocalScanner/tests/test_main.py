import logging
import os
import signal
import subprocess
import sys
import threading
import time

from conftest import PORT_IN, PORT_OUT

from cupboard_pi.__main__ import deployed_sha, log_format, main, make_scan_handler
from cupboard_pi.config import Config
from cupboard_pi.identity import Identity
from cupboard_pi.ingest_client import IngestClient, IngestResponse
from cupboard_pi.scan_queue import ScanQueue

ENV = {
    "INGEST_URL": "https://ingest.example.net",
    "INGEST_AUTH_HEADER_NAME": "x-functions-key",
    "INGEST_AUTH_HEADER_VALUE": "secret-value",
    "HUB_ID": "cupboard-pi",
    "SCANNERS": f"scanner-in=in@{PORT_IN},scanner-out=out@{PORT_OUT}",
}


def fake_client():
    config = Config(ENV["INGEST_URL"], ENV["INGEST_AUTH_HEADER_NAME"], ENV["INGEST_AUTH_HEADER_VALUE"])
    return IngestClient(config, lambda request, timeout: IngestResponse(202, "{}"))


def _set_env(monkeypatch, tmp_path):
    for k, v in ENV.items():
        monkeypatch.setenv(k, v)
    monkeypatch.setenv("SCAN_QUEUE_PATH", str(tmp_path / "q.sqlite3"))


def test_logs_startup_with_sha_and_stops_when_asked(monkeypatch, caplog, tmp_path, by_path):
    _set_env(monkeypatch, tmp_path)
    monkeypatch.setenv("DEPLOYED_SHA", "abc1234")
    stop = threading.Event()
    stop.set()
    with caplog.at_level(logging.INFO):
        assert main(stop=stop, by_path=by_path, client=fake_client()) == 0
    assert "cupboard-pi starting: sha=abc1234" in caplog.text
    assert f"scanner-in=in@{PORT_IN}" in caplog.text
    assert "cupboard-pi stopping" in caplog.text
    assert "secret-value" not in caplog.text


def test_keeps_running_until_stopped(monkeypatch, tmp_path, by_path):
    _set_env(monkeypatch, tmp_path)
    stop = threading.Event()
    result = []
    worker = threading.Thread(
        target=lambda: result.append(main(stop=stop, by_path=by_path, client=fake_client()))
    )
    worker.start()
    time.sleep(0.3)
    assert worker.is_alive()
    stop.set()
    worker.join(timeout=10)
    assert result == [0]


def test_missing_config_exits_with_2(monkeypatch, caplog):
    for k in ENV:
        monkeypatch.delenv(k, raising=False)
    assert main() == 2
    assert "INGEST_URL" in caplog.text


def test_scanners_without_ports_refuse_to_start(monkeypatch, caplog, tmp_path):
    _set_env(monkeypatch, tmp_path)
    monkeypatch.setenv("SCANNERS", "scanner-in=in,scanner-out=out")
    assert main() == 2
    assert "@<scanner port>" in caplog.text


def test_unwritable_queue_refuses_to_start(monkeypatch, caplog, tmp_path):
    _set_env(monkeypatch, tmp_path)
    monkeypatch.setenv("SCAN_QUEUE_PATH", str(tmp_path / "missing-dir" / "q.sqlite3"))
    assert main() == 2
    assert "scan queue" in caplog.text


def handler(tmp_path):
    identity = Identity.from_env(ENV, require_ports=True)
    queue = ScanQueue(tmp_path / "q.sqlite3")
    return make_scan_handler(identity, queue, "abc1234"), queue


def test_a_valid_scan_is_queued_with_the_device_and_role(tmp_path, caplog):
    on_scan, queue = handler(tmp_path)
    with caplog.at_level(logging.INFO):
        on_scan("scanner-out", "5000354922831")
    item = queue.oldest()
    assert {k: item.payload[k] for k in ("barcode", "deviceId", "role", "clientSha")} == {
        "barcode": "5000354922831",
        "deviceId": "scanner-out",
        "role": "out",
        "clientSha": "abc1234",
    }
    assert item.payload["scanId"] == item.scan_id
    assert item.payload["scannedAt"].endswith("Z")
    assert f"scan ID {item.scan_id}" in caplog.text and "queue depth 1" in caplog.text


def test_an_invalid_barcode_is_logged_and_not_queued(tmp_path, caplog):
    on_scan, queue = handler(tmp_path)
    with caplog.at_level(logging.WARNING):
        on_scan("scanner-in", "5000354922832")  # wrong check digit
        on_scan("scanner-in", "Aa<200>")
    assert queue.depth() == 0
    assert "Invalid barcode read on scanner-in: '5000354922832'" in caplog.text


def test_sha_comes_from_the_release_folder_above_the_package(monkeypatch, tmp_path):
    monkeypatch.delenv("DEPLOYED_SHA", raising=False)
    release = tmp_path / "releases" / "abc1234"
    package = release / "venv" / "lib" / "python3.13" / "site-packages" / "cupboard_pi"
    package.mkdir(parents=True)
    (release / "DEPLOYED_SHA").write_text("abc1234\n")
    (tmp_path / "DEPLOYED_SHA").write_text("older\n")  # a file further up doesn't win
    assert deployed_sha(package) == "abc1234"


def test_sha_from_env_wins(monkeypatch, tmp_path):
    monkeypatch.setenv("DEPLOYED_SHA", "fromenv")
    (tmp_path / "DEPLOYED_SHA").write_text("fromfile")
    assert deployed_sha(tmp_path / "pkg") == "fromenv"


def test_sha_is_local_without_a_file(monkeypatch, tmp_path):
    monkeypatch.delenv("DEPLOYED_SHA", raising=False)
    assert deployed_sha(tmp_path / "a" / "b") == "local"


def test_no_timestamp_in_logs_under_systemd():
    assert "asctime" not in log_format({"JOURNAL_STREAM": "8:12345"})
    assert "asctime" in log_format({})


def test_sigterm_stops_the_service_cleanly(tmp_path):
    # A closed local port: the startup heartbeat fails at once, with no network involved.
    env = {
        **os.environ,
        **ENV,
        "INGEST_URL": "http://127.0.0.1:9",
        "DEPLOYED_SHA": "sigtest",
        "SCAN_QUEUE_PATH": str(tmp_path / "q.sqlite3"),
    }
    proc = subprocess.Popen([sys.executable, "-m", "cupboard_pi"], env=env, stderr=subprocess.PIPE, text=True)
    deadline = time.monotonic() + 10
    line = ""
    while time.monotonic() < deadline and "starting" not in line:
        line = proc.stderr.readline()
    assert "sha=sigtest" in line
    proc.send_signal(signal.SIGTERM)
    _, rest = proc.communicate(timeout=10)
    assert proc.returncode == 0
    assert "cupboard-pi stopping" in rest


def test_bad_heartbeat_interval_refuses_to_start(monkeypatch, caplog, tmp_path):
    _set_env(monkeypatch, tmp_path)
    monkeypatch.setenv("HEARTBEAT_INTERVAL_SECONDS", "1")
    assert main() == 2
    assert "HEARTBEAT_INTERVAL_SECONDS" in caplog.text
