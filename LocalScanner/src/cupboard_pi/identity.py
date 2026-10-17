"""The hub's identity and its scanners' devices, from env (shared by the Pi and the simulator).

HUB_ID    the Pi's ID, e.g. cupboard-pi
SCANNERS  deviceId=role pairs, comma-separated, e.g. scanner-in=in,scanner-out=out
          (how a physical scanner is matched to its device comes with the scanner spike, #27)
"""

from __future__ import annotations

import os
import re
from collections.abc import Mapping
from dataclasses import dataclass

from cupboard_pi.config import ConfigError
from cupboard_pi.payloads import ROLES

_DEVICE_ID = re.compile(r"[a-z0-9][a-z0-9-]{1,62}")


@dataclass(frozen=True)
class Identity:
    hub_id: str
    devices: dict[str, str]  # deviceId -> role

    def device_for_role(self, role: str) -> str:
        matches = [d for d, r in self.devices.items() if r == role]
        if len(matches) != 1:
            raise ConfigError(f"SCANNERS must have exactly one device with role {role!r}.")
        return matches[0]

    @classmethod
    def from_env(cls, env: Mapping[str, str] | None = None) -> Identity:
        env = os.environ if env is None else env
        hub_id = env.get("HUB_ID", "").strip()
        raw = env.get("SCANNERS", "").strip()
        missing = [n for n, v in (("HUB_ID", hub_id), ("SCANNERS", raw)) if not v]
        if missing:
            raise ConfigError("Missing environment variables: " + ", ".join(missing))
        if not _DEVICE_ID.fullmatch(hub_id):
            raise ConfigError("HUB_ID must be lowercase letters, digits and dashes.")
        devices: dict[str, str] = {}
        for pair in raw.split(","):
            device_id, sep, role = (p.strip() for p in pair.partition("="))
            if not sep or not _DEVICE_ID.fullmatch(device_id) or role not in ROLES:
                raise ConfigError(
                    f"Bad SCANNERS entry {pair.strip()!r}: expected deviceId=in or deviceId=out."
                )
            if device_id in devices:
                raise ConfigError(f"Duplicate device {device_id!r} in SCANNERS.")
            devices[device_id] = role
        return cls(hub_id=hub_id, devices=devices)
