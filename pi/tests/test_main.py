import logging

from cupboard_pi.__main__ import main

ENV = {
    "INGEST_URL": "https://ingest.example.net",
    "INGEST_AUTH_HEADER_NAME": "x-functions-key",
    "INGEST_AUTH_HEADER_VALUE": "secret-value",
}


def test_starts_and_exits_cleanly(monkeypatch, caplog):
    for k, v in ENV.items():
        monkeypatch.setenv(k, v)
    monkeypatch.setenv("DEPLOYED_SHA", "abc1234")
    with caplog.at_level(logging.INFO):
        assert main() == 0
    assert "sha=abc1234" in caplog.text
    assert "secret-value" not in caplog.text


def test_missing_config_exits_with_2(monkeypatch, caplog):
    for k in ENV:
        monkeypatch.delenv(k, raising=False)
    assert main() == 2
    assert "INGEST_URL" in caplog.text
