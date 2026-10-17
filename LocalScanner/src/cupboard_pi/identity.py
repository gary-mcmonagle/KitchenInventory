"""The hub's identity and its scanners' devices, from env (shared by the Pi and the simulator).

HUB_ID    the Pi's ID, e.g. cupboard-pi
SCANNERS  deviceId=role pairs, comma-separated, each optionally followed by @<scanner port>:
            scanner-in=in@platform-3f980000.usb-usb-0:1.5:1.0,scanner-out=out@platform-3f980000.usb-usb-0:1.2:1.0
          The scanner port is the dongle's /dev/input/by-path name without "-event-kbd" (spike #27).
          The Pi needs a port for every device; the simulator doesn't use them.
"""

from __future__ import annotations

import os
import re
from collections.abc import Mapping
from dataclasses import dataclass, field

from cupboard_pi.config import ConfigError
from cupboard_pi.payloads import ROLES

_DEVICE_ID = re.compile(r"[a-z0-9][a-z0-9-]{1,62}")
_SCANNER_PORT = re.compile(r"[A-Za-z0-9][A-Za-z0-9._:-]{0,200}")
EXAMPLE = (
    "scanner-in=in@platform-3f980000.usb-usb-0:1.5:1.0,scanner-out=out@platform-3f980000.usb-usb-0:1.2:1.0"
)


@dataclass(frozen=True)
class Identity:
    hub_id: str
    devices: dict[str, str]  # deviceId -> role
    ports: dict[str, str] = field(default_factory=dict)  # scanner port -> deviceId

    def device_for_role(self, role: str) -> str:
        matches = [d for d, r in self.devices.items() if r == role]
        if len(matches) != 1:
            raise ConfigError(f"SCANNERS must have exactly one device with role {role!r}.")
        return matches[0]

    @classmethod
    def from_env(cls, env: Mapping[str, str] | None = None, require_ports: bool = False) -> Identity:
        env = os.environ if env is None else env
        hub_id = env.get("HUB_ID", "").strip()
        raw = env.get("SCANNERS", "").strip()
        missing = [n for n, v in (("HUB_ID", hub_id), ("SCANNERS", raw)) if not v]
        if missing:
            raise ConfigError("Missing environment variables: " + ", ".join(missing))
        if not _DEVICE_ID.fullmatch(hub_id):
            raise ConfigError("HUB_ID must be lowercase letters, digits and dashes.")
        devices: dict[str, str] = {}
        ports: dict[str, str] = {}
        for pair in raw.split(","):
            device_id, sep, rest = (p.strip() for p in pair.partition("="))
            role, at, port = (p.strip() for p in rest.partition("@"))
            if not sep or not _DEVICE_ID.fullmatch(device_id) or role not in ROLES:
                raise ConfigError(
                    f"Bad SCANNERS entry {pair.strip()!r}: expected deviceId=in or deviceId=out."
                )
            if device_id in devices:
                raise ConfigError(f"Duplicate device {device_id!r} in SCANNERS.")
            if at:
                port = port.removesuffix("-event-kbd")
                if not _SCANNER_PORT.fullmatch(port):
                    raise ConfigError(f"Bad scanner port in SCANNERS entry {pair.strip()!r}.")
                if port in ports:
                    raise ConfigError(f"Scanner port {port!r} is mapped to two devices in SCANNERS.")
                ports[port] = device_id
            elif require_ports:
                raise ConfigError(
                    f"SCANNERS entry {pair.strip()!r} has no scanner port. Each entry needs "
                    f"deviceId=role@<scanner port>, e.g. SCANNERS={EXAMPLE}"
                )
            devices[device_id] = role
        return cls(hub_id=hub_id, devices=devices, ports=ports)
