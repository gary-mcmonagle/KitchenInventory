import logging
import os
import signal
import subprocess
import sys
import threading
import time

from cupboard_pi.__main__ import deployed_sha, main

ENV = {
    "INGEST_URL": "https://ingest.example.net",
    "INGEST_AUTH_HEADER_NAME": "x-functions-key",
    "INGEST_AUTH_HEADER_VALUE": "secret-value",
}


def _set_env(monkeypatch):
    for k, v in ENV.items():
        monkeypatch.setenv(k, v)


def test_logs_startup_with_sha_and_stops_when_asked(monkeypatch, caplog):
    _set_env(monkeypatch)
    monkeypatch.setenv("DEPLOYED_SHA", "abc1234")
    stop = threading.Event()
    stop.set()
    with caplog.at_level(logging.INFO):
        assert main(stop=stop) == 0
    assert "cupboard-pi starting: sha=abc1234" in caplog.text
    assert "cupboard-pi stopping" in caplog.text
    assert "secret-value" not in caplog.text


def test_keeps_running_until_stopped(monkeypatch):
    _set_env(monkeypatch)
    stop = threading.Event()
    result = []
    worker = threading.Thread(target=lambda: result.append(main(stop=stop)))
    worker.start()
    time.sleep(0.2)
    assert worker.is_alive()
    stop.set()
    worker.join(timeout=5)
    assert result == [0]


def test_missing_config_exits_with_2(monkeypatch, caplog):
    for k in ENV:
        monkeypatch.delenv(k, raising=False)
    assert main() == 2
    assert "INGEST_URL" in caplog.text


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


def test_sigterm_stops_the_service_cleanly(tmp_path):
    env = {**os.environ, **ENV, "DEPLOYED_SHA": "sigtest"}
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
