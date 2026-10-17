#!/usr/bin/env python3
"""Spike #27: read barcodes from USB HID barcode scanners on a headless Pi, standard library only.

Prints one line per scan:  <scanner-id> <barcode>
where <scanner-id> is the device's /dev/input/by-path name (stable per physical USB port).

Usage (on the Pi, as a user in the `input` group):
  python3 27-scanner-read.py [--seconds N] [--raw]

- Finds keyboard devices under /dev/input/by-path/*-event-kbd that are on USB.
- Grabs each one exclusively (EVIOCGRAB), so scans never reach the console or anything else.
- Decodes key presses to characters (US key codes; a scan ends with Enter), with Shift for letters/symbols.
- Re-scans for devices every 2 s, so an unplugged and replugged dongle is picked up without a restart.
- --raw also prints every key event (code, value) to show exactly what the scanner sends.
"""

import argparse
import fcntl
import os
import select
import struct
import sys
import time
from pathlib import Path

BY_PATH = Path("/dev/input/by-path")
EVENT = struct.Struct("LLHHi")  # struct input_event: time (sec, usec as kernel ulong), type, code, value
EV_KEY = 0x01
EVIOCGRAB = 0x40044590  # _IOW('E', 0x90, int)

# Linux input key codes → (unshifted, shifted). Barcode scanners send US key codes for the characters they type.
KEYS = {
    2: ("1", "!"), 3: ("2", "@"), 4: ("3", "#"), 5: ("4", "$"), 6: ("5", "%"), 7: ("6", "^"), 8: ("7", "&"),
    9: ("8", "*"), 10: ("9", "("), 11: ("0", ")"), 12: ("-", "_"), 13: ("=", "+"),
    16: ("q", "Q"), 17: ("w", "W"), 18: ("e", "E"), 19: ("r", "R"), 20: ("t", "T"), 21: ("y", "Y"), 22: ("u", "U"),
    23: ("i", "I"), 24: ("o", "O"), 25: ("p", "P"), 26: ("[", "{"), 27: ("]", "}"),
    30: ("a", "A"), 31: ("s", "S"), 32: ("d", "D"), 33: ("f", "F"), 34: ("g", "G"), 35: ("h", "H"), 36: ("j", "J"),
    37: ("k", "K"), 38: ("l", "L"), 39: (";", ":"), 40: ("'", '"'), 41: ("`", "~"), 43: ("\\", "|"),
    44: ("z", "Z"), 45: ("x", "X"), 46: ("c", "C"), 47: ("v", "V"), 48: ("b", "B"), 49: ("n", "N"), 50: ("m", "M"),
    51: (",", "<"), 52: (".", ">"), 53: ("/", "?"), 57: (" ", " "),
    # Keypad digits (some scanners use them)
    71: ("7", "7"), 72: ("8", "8"), 73: ("9", "9"), 75: ("4", "4"), 76: ("5", "5"), 77: ("6", "6"),
    79: ("1", "1"), 80: ("2", "2"), 81: ("3", "3"), 82: ("0", "0"),
}
DIGIT_KEYS = set(range(2, 12))  # KEY_1..KEY_0
ENTER = {28, 96}  # KEY_ENTER, KEY_KPENTER
SHIFT = {42, 54}  # KEY_LEFTSHIFT, KEY_RIGHTSHIFT


class Scanner:
    def __init__(self, link: Path):
        self.id = link.name.removesuffix("-event-kbd")
        self.fd = os.open(link, os.O_RDONLY | os.O_NONBLOCK)
        fcntl.ioctl(self.fd, EVIOCGRAB, 1)  # exclusive: nothing else sees these keystrokes
        self.buffer = []
        self.shift = False
        self.saw_shift = False
        self.started = None

    def feed(self, code: int, value: int, raw: bool) -> str | None:
        if raw:
            print(f"  raw {self.id} code={code} value={value}", flush=True)
        if code in SHIFT:
            self.shift = value != 0
            self.saw_shift = self.saw_shift or self.shift
            return None
        if value != 1:  # only key-down (1); ignore release (0) and autorepeat (2)
            return None
        if code in ENTER:
            scan, self.buffer = "".join(self.buffer), []
            return scan
        if self.started is None or not self.buffer:
            self.started = time.monotonic()
        char = KEYS.get(code)
        if code in DIGIT_KEYS:
            # Top-row digit keys are digits whether or not Shift is held: a scanner set to a French (AZERTY) layout
            # holds Shift for every digit (found in this spike). Barcodes are digits, so this is always right.
            self.buffer.append(char[0])
        else:
            self.buffer.append(char[1] if self.shift else char[0] if char else f"<{code}>")
        return None

    def close(self):
        try:
            os.close(self.fd)
        except OSError:
            pass


def usb_keyboards() -> list[Path]:
    return sorted(p for p in BY_PATH.glob("*-event-kbd") if ".usb-" in p.name and "usbv2" not in p.name)


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--seconds", type=float, default=0, help="stop after N seconds (0 = run until Ctrl-C)")
    parser.add_argument("--raw", action="store_true", help="also print every key event")
    args = parser.parse_args()

    scanners: dict[int, Scanner] = {}
    deadline = time.monotonic() + args.seconds if args.seconds else None
    next_rescan = 0.0
    print(f"listening (pid {os.getpid()})", flush=True)
    while deadline is None or time.monotonic() < deadline:
        if time.monotonic() >= next_rescan:
            known = {s.id for s in scanners.values()}
            for link in usb_keyboards():
                if link.name.removesuffix("-event-kbd") not in known:
                    try:
                        scanner = Scanner(link)
                    except OSError as e:
                        print(f"! can't open {link.name}: {e}", flush=True)
                        continue
                    scanners[scanner.fd] = scanner
                    print(f"+ scanner {scanner.id} ({os.path.realpath(link)})", flush=True)
            next_rescan = time.monotonic() + 2

        ready, _, _ = select.select(list(scanners), [], [], 0.5)
        for fd in ready:
            scanner = scanners[fd]
            try:
                data = os.read(fd, EVENT.size * 64)
            except OSError:  # unplugged / out of range dongle removed
                print(f"- scanner {scanner.id} gone", flush=True)
                scanner.close()
                del scanners[fd]
                continue
            for offset in range(0, len(data) - EVENT.size + 1, EVENT.size):
                _sec, _usec, etype, code, value = EVENT.unpack_from(data, offset)
                if etype != EV_KEY:
                    continue
                scan = scanner.feed(code, value, args.raw)
                if scan is not None:
                    took = (time.monotonic() - scanner.started) * 1000 if scanner.started else 0
                    shifted = " (shift held)" if scanner.saw_shift else ""
                    print(f"{scanner.id} {scan}   [{len(scan)} chars, {took:.0f} ms{shifted}]", flush=True)
                    scanner.saw_shift = False

    for scanner in scanners.values():
        scanner.close()
    return 0


if __name__ == "__main__":
    sys.exit(main())
