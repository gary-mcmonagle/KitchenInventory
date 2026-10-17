import pytest


@pytest.fixture(autouse=True)
def isolated_sent_log(tmp_path, monkeypatch):
    monkeypatch.setenv("CUPBOARD_SIMULATOR_HOME", str(tmp_path / "sim-home"))
