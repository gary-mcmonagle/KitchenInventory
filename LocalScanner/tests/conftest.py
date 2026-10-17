import os
import struct
import threading
from pathlib import Path

import pytest

from cupboard_pi.scanners import EV_KEY, EVENT, LINK_SUFFIX

PORT_IN = "platform-3f980000.usb-usb-0:1.5:1.0"
PORT_OUT = "platform-3f980000.usb-usb-0:1.2:1.0"
DIGIT_CODES = {str(d): d + 1 for d in range(1, 10)} | {"0": 11}
ENTER, SHIFT = 28, 42


def key_events(text: str, shift_digits: bool = False) -> bytes:
    """Kernel input events for typing ``text`` then Enter, the way a scanner does."""
    out = b""

    def press(code: int) -> bytes:
        return EVENT.pack(0, 0, EV_KEY, code, 1) + EVENT.pack(0, 0, EV_KEY, code, 0)

    for ch in text:
        if ch.isdigit():
            if shift_digits:
                out += EVENT.pack(0, 0, EV_KEY, SHIFT, 1)
            out += press(DIGIT_CODES[ch])
            if shift_digits:
                out += EVENT.pack(0, 0, EV_KEY, SHIFT, 0)
        else:
            out += press({"a": 30, "b": 48}[ch])
    out += press(ENTER)
    out += EVENT.pack(0, 0, 0, 0, 0)  # EV_SYN, ignored
    return out


class FakeDongle:
    """A by-path link backed by a FIFO, written to like a dongle's event device."""

    def __init__(self, by_path: Path, port: str):
        self.link = by_path / (port + LINK_SUFFIX)
        self.fifo = by_path.parent / ("fifo-" + port.replace(":", "_"))
        os.mkfifo(self.fifo)
        self.link.symlink_to(self.fifo)
        self._fd: int | None = None
        self._ready = threading.Event()
        # Opening a FIFO for writing blocks until the reader opens it, so do it in the background.
        threading.Thread(target=self._open, daemon=True).start()

    def _open(self) -> None:
        self._fd = os.open(self.fifo, os.O_WRONLY)
        self._ready.set()

    def type(self, text: str, **kw) -> None:
        assert self._ready.wait(5), "the reader never opened this dongle"
        os.write(self._fd, key_events(text, **kw))

    def unplug(self) -> None:
        self.link.unlink()
        if self._fd is not None:
            os.close(self._fd)


@pytest.fixture
def by_path(tmp_path):
    d = tmp_path / "by-path"
    d.mkdir()
    return d


def no_grab(_fd: int) -> None:
    pass


__all__ = ["PORT_IN", "PORT_OUT", "FakeDongle", "key_events", "no_grab", "struct"]
