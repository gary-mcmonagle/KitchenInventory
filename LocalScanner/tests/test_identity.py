import pytest

from cupboard_pi.config import ConfigError
from cupboard_pi.identity import Identity

ENV = {"HUB_ID": "cupboard-pi", "SCANNERS": "scanner-in=in, scanner-out=out"}


def test_reads_hub_and_devices():
    identity = Identity.from_env(ENV)
    assert identity.hub_id == "cupboard-pi"
    assert identity.devices == {"scanner-in": "in", "scanner-out": "out"}
    assert identity.device_for_role("out") == "scanner-out"


@pytest.mark.parametrize(
    "scanners",
    ["scanner-in", "scanner-in=sideways", "Scanner In=in", "scanner-in=in,scanner-in=out", "=in"],
)
def test_rejects_bad_scanner_config(scanners):
    with pytest.raises(ConfigError):
        Identity.from_env({**ENV, "SCANNERS": scanners})


def test_missing_settings_are_named():
    with pytest.raises(ConfigError) as e:
        Identity.from_env({})
    assert "HUB_ID" in str(e.value) and "SCANNERS" in str(e.value)


def test_role_needs_exactly_one_device():
    identity = Identity.from_env({**ENV, "SCANNERS": "scanner-in=in"})
    with pytest.raises(ConfigError):
        identity.device_for_role("out")
