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


PORT_1 = "platform-3f980000.usb-usb-0:1.5:1.0"
PORT_2 = "platform-3f980000.usb-usb-0:1.2:1.0"


def test_reads_scanner_ports():
    env = {**ENV, "SCANNERS": f"scanner-in=in@{PORT_1}, scanner-out=out@{PORT_2}-event-kbd"}
    identity = Identity.from_env(env, require_ports=True)
    assert identity.devices == {"scanner-in": "in", "scanner-out": "out"}
    assert identity.ports == {PORT_1: "scanner-in", PORT_2: "scanner-out"}


def test_the_pi_needs_a_port_for_every_device():
    with pytest.raises(ConfigError) as e:
        Identity.from_env({**ENV, "SCANNERS": f"scanner-in=in@{PORT_1},scanner-out=out"}, require_ports=True)
    assert "scanner-out=out" in str(e.value) and "@<scanner port>" in str(e.value)


def test_the_simulator_ignores_missing_ports():
    assert Identity.from_env(ENV).ports == {}


@pytest.mark.parametrize("scanners", [f"a1=in@{PORT_1},b1=out@{PORT_1}", "a1=in@", "a1=in@bad port"])
def test_rejects_bad_ports(scanners):
    with pytest.raises(ConfigError):
        Identity.from_env({**ENV, "SCANNERS": scanners}, require_ports=True)
