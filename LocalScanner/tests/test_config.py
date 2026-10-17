import pytest

from cupboard_pi.config import Config, ConfigError

ENV = {
    "INGEST_URL": "https://ingest.example.net/",
    "INGEST_AUTH_HEADER_NAME": "x-functions-key",
    "INGEST_AUTH_HEADER_VALUE": "secret-value",
}


def test_reads_settings_from_env():
    config = Config.from_env(ENV)
    assert config.ingest_url == "https://ingest.example.net"
    assert config.ingest_auth_header_name == "x-functions-key"
    assert config.ingest_auth_header_value == "secret-value"
    assert config.log_level == "INFO"


def test_missing_settings_are_named_without_values():
    with pytest.raises(ConfigError) as e:
        Config.from_env({"INGEST_URL": "https://x", "INGEST_AUTH_HEADER_VALUE": "  "})
    assert "INGEST_AUTH_HEADER_NAME" in str(e.value)
    assert "INGEST_AUTH_HEADER_VALUE" in str(e.value)
    assert "https://x" not in str(e.value)


@pytest.mark.parametrize("url", ["http://ingest.example.net", "file:///etc/passwd", "ftp://x"])
def test_rejects_non_https_urls(url):
    with pytest.raises(ConfigError):
        Config.from_env({**ENV, "INGEST_URL": url})


def test_allows_local_func_start():
    assert (
        Config.from_env({**ENV, "INGEST_URL": "http://localhost:7071"}).ingest_url == "http://localhost:7071"
    )


def test_secret_is_not_in_repr():
    assert "secret-value" not in repr(Config.from_env(ENV))
