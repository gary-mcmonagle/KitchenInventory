"""Reads barcodes from the scanners' dongles (USB HID keyboards), standard library only (spike #27).

Each dongle is found by its scanner port: its /dev/input/by-path link, which is set by the physical
USB port. The two dongles are identical, so by-id can't tell them apart. Each one is grabbed exclusively
(EVIOCGRAB), so scans never reach the console. A scan is the keys typed before Enter.
"""

from __future__ import annotations

import contextlib
import fcntl
import logging
import os
import select
import struct
import threading
import time
from collections.abc import Callable, Iterable
from dataclasses import dataclass
from pathlib import Path

from cupboard_pi.payloads import ScannerPresence

log = logging.getLogger(__name__)

BY_PATH = Path("/dev/input/by-path")
LINK_SUFFIX = "-event-kbd"
EVENT = struct.Struct("@llHHi")  # struct input_event: time (sec, usec as C longs), type, code, value
EV_KEY = 0x01
EVIOCGRAB = 0x40044590  # _IOW('E', 0x90, int)
RESCAN_SECONDS = 2.0
STALE_SECONDS = (
    2.0  # a partial scan with no key for this long is dropped (a scanner types ~4 ms per character)
)
MAX_SCAN_LENGTH = 64

# Linux key codes -> (unshifted, shifted). Scanners send US key codes for the characters they type.
_KEYS = {
    2: ("1", "!"), 3: ("2", "@"), 4: ("3", "#"), 5: ("4", "$"), 6: ("5", "%"), 7: ("6", "^"), 8: ("7", "&"),
    9: ("8", "*"), 10: ("9", "("), 11: ("0", ")"), 12: ("-", "_"), 13: ("=", "+"),
    16: ("q", "Q"), 17: ("w", "W"), 18: ("e", "E"), 19: ("r", "R"), 20: ("t", "T"), 21: ("y", "Y"),
    22: ("u", "U"), 23: ("i", "I"), 24: ("o", "O"), 25: ("p", "P"), 26: ("[", "{"), 27: ("]", "}"),
    30: ("a", "A"), 31: ("s", "S"), 32: ("d", "D"), 33: ("f", "F"), 34: ("g", "G"), 35: ("h", "H"),
    36: ("j", "J"), 37: ("k", "K"), 38: ("l", "L"), 39: (";", ":"), 40: ("'", '"'), 41: ("`", "~"),
    43: ("\\", "|"), 44: ("z", "Z"), 45: ("x", "X"), 46: ("c", "C"), 47: ("v", "V"), 48: ("b", "B"),
    49: ("n", "N"), 50: ("m", "M"), 51: (",", "<"), 52: (".", ">"), 53: ("/", "?"), 57: (" ", " "),
    # Keypad digits (some scanners use them)
    71: ("7", "7"), 72: ("8", "8"), 73: ("9", "9"), 75: ("4", "4"), 76: ("5", "5"), 77: ("6", "6"),
    79: ("1", "1"), 80: ("2", "2"), 81: ("3", "3"), 82: ("0", "0"),
}  # fmt: skip
_DIGIT_KEYS = frozenset(range(2, 12))  # KEY_1..KEY_0
_ENTER = frozenset({28, 96})  # KEY_ENTER, KEY_KPENTER
_SHIFT = frozenset({42, 54})  # KEY_LEFTSHIFT, KEY_RIGHTSHIFT


class KeyDecoder:
    """Turns one dongle's key events into scans. ``feed`` returns the scan when Enter is pressed."""

    def __init__(self, clock: Callable[[], float] = time.monotonic):
        self._clock = clock
        self._chars: list[str] = []
        self._shift = False
        self._last_key = 0.0

    def feed(self, code: int, value: int) -> str | None:
        if code in _SHIFT:
            self._shift = value != 0
            return None
        if value != 1:  # only key-down; ignore release (0) and autorepeat (2)
            return None
        now = self._clock()
        if self._chars and now - self._last_key > STALE_SECONDS:
            log.warning("Dropped a partial scan (%d characters, no Enter)", len(self._chars))
            self._chars = []
        self._last_key = now
        if code in _ENTER:
            scan, self._chars = "".join(self._chars), []
            return scan or None
        if len(self._chars) >= MAX_SCAN_LENGTH:
            return None
        if code in _DIGIT_KEYS:
            # Digits regardless of Shift: a scanner on a French (AZERTY) layout holds Shift for every digit
            # (spike #27). Barcodes are digits, so this is always right.
            self._chars.append(_KEYS[code][0])
        elif code in _KEYS:
            self._chars.append(_KEYS[code][1] if self._shift else _KEYS[code][0])
        else:
            self._chars.append(f"<{code}>")
        return None


def scanner_ports(by_path: Path = BY_PATH) -> set[str]:
    """Scanner ports of the USB keyboards plugged in now (udev's duplicate "usbv2" links are skipped)."""
    return {
        p.name.removesuffix(LINK_SUFFIX)
        for p in by_path.glob("*" + LINK_SUFFIX)
        if ".usb-" in p.name and "usbv2" not in p.name
    }


def scanner_presence(ports: dict[str, str], by_path: Path = BY_PATH) -> list[ScannerPresence]:
    """Whether each mapped scanner's dongle is plugged in, for the heartbeat. ``ports``: port -> deviceId."""
    plugged = scanner_ports(by_path)
    return [
        ScannerPresence(device_id=d, present=p in plugged)
        for p, d in sorted(ports.items(), key=lambda i: i[1])
    ]


@dataclass
class _Dongle:
    port: str
    device_id: str
    fd: int
    decoder: KeyDecoder


OnScan = Callable[[str, str], None]  # (device_id, barcode)


class ScannerReader:
    """Reads every mapped dongle until ``stop`` is set. Dongles plugged in or out are noticed within 2 s.

    A USB keyboard on a port that isn't in SCANNERS is logged as an error and left alone (not grabbed).
    ``on_scan`` must be quick (it writes to the local queue); sending happens elsewhere.
    """

    def __init__(
        self,
        ports: dict[str, str],
        on_scan: OnScan,
        by_path: Path = BY_PATH,
        grab: Callable[[int], None] = lambda fd: fcntl.ioctl(fd, EVIOCGRAB, 1),
    ):
        self._ports = dict(ports)
        self._grab = grab
        self._on_scan = on_scan
        self._by_path = by_path
        self._open: dict[int, _Dongle] = {}
        self._reported_unknown: set[str] = set()
        self._reported_missing: set[str] = set()

    def run(self, stop: threading.Event) -> None:
        next_rescan = 0.0
        try:
            while not stop.is_set():
                if time.monotonic() >= next_rescan:
                    self._rescan()
                    next_rescan = time.monotonic() + RESCAN_SECONDS
                if not self._open:
                    stop.wait(0.5)
                    continue
                ready, _, _ = select.select(list(self._open), [], [], 0.5)
                for fd in ready:
                    self._read(fd)
        finally:
            for fd in list(self._open):
                self._close(fd)

    def _rescan(self) -> None:
        plugged = scanner_ports(self._by_path)
        open_ports = {d.port for d in self._open.values()}
        for port in sorted(plugged - open_ports):
            device_id = self._ports.get(port)
            if device_id is None:
                if port not in self._reported_unknown:
                    log.error(
                        "Ignoring a USB keyboard on an unmapped scanner port %s (not in SCANNERS)", port
                    )
                    self._reported_unknown.add(port)
                continue
            self._open_dongle(port, device_id)
        self._reported_unknown &= plugged
        for fd, dongle in list(self._open.items()):
            if dongle.port not in plugged:
                log.warning("Scanner %s disconnected (%s)", dongle.device_id, dongle.port)
                self._close(fd)
        for port, device_id in sorted(self._ports.items()):
            if port not in plugged and port not in self._reported_missing:
                log.warning("Scanner %s's dongle isn't plugged in (expected on %s)", device_id, port)
        self._reported_missing = set(self._ports) - plugged

    def _open_dongle(self, port: str, device_id: str) -> None:
        try:
            fd = os.open(self._by_path / (port + LINK_SUFFIX), os.O_RDONLY | os.O_NONBLOCK)
        except OSError as e:
            log.error("Can't open scanner %s (%s): %s", device_id, port, e)
            return
        try:
            self._grab(fd)  # exclusive: nothing else sees these keystrokes
        except OSError as e:
            log.error("Can't grab scanner %s (%s): %s", device_id, port, e)
            os.close(fd)
            return
        self._open[fd] = _Dongle(port, device_id, fd, KeyDecoder())
        log.info("Scanner %s connected (%s)", device_id, port)

    def _read(self, fd: int) -> None:
        dongle = self._open[fd]
        try:
            data = os.read(fd, EVENT.size * 64)
        except BlockingIOError:
            return
        except OSError:  # unplugged
            log.warning("Scanner %s disconnected (%s)", dongle.device_id, dongle.port)
            self._close(fd)
            return
        if not data:  # a real evdev device never returns 0 bytes; a test FIFO does before its writer connects
            return
        for _sec, _usec, etype, code, value in iter_events(data):
            if etype != EV_KEY:
                continue
            scan = dongle.decoder.feed(code, value)
            if scan is not None:
                self._on_scan(dongle.device_id, scan)

    def _close(self, fd: int) -> None:
        self._open.pop(fd, None)
        with contextlib.suppress(OSError):
            os.close(fd)


def iter_events(data: bytes) -> Iterable[tuple[int, int, int, int, int]]:
    for offset in range(0, len(data) - EVENT.size + 1, EVENT.size):
        yield EVENT.unpack_from(data, offset)
