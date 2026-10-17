"""The durable scan queue: every scan is written here before it's sent, so none is lost to a network or Azure
outage, a crash, a restart or a reboot.

SQLite under /var/lib/cupboard-tracker/ (outside the release folders, so deploys and rollbacks keep
it). Scans are sent oldest first and deleted only after ingest accepts them (2xx). A scan ingest
rejects as invalid (400) is moved to the ``rejected`` table rather than deleted, so it's kept for a
person to look at and doesn't block the scans behind it.
"""

from __future__ import annotations

import json
import sqlite3
import threading
from dataclasses import dataclass
from datetime import UTC, datetime
from pathlib import Path
from typing import Any

DEFAULT_PATH = Path("/var/lib/cupboard-tracker/scan-queue.sqlite3")
WARN_DEPTH = 1000


@dataclass(frozen=True)
class QueuedScan:
    seq: int
    scan_id: str
    payload: dict[str, Any]


class ScanQueue:
    """Thread-safe: the scanner reader adds, the sender takes. Each call is its own transaction."""

    def __init__(self, path: Path = DEFAULT_PATH):
        self.path = path
        # One connection shared under a lock: simple, and SQLite calls here take well under a millisecond.
        self._db = sqlite3.connect(path, check_same_thread=False, isolation_level=None)
        self._lock = threading.Lock()
        self.added = threading.Event()  # set when a scan is added, so an idle sender wakes straight away
        with self._lock:
            self._db.execute("PRAGMA journal_mode=WAL")
            self._db.execute("PRAGMA synchronous=FULL")  # survive a power cut, not just a crash
            self._db.executescript(
                """
                CREATE TABLE IF NOT EXISTS scans (
                    seq INTEGER PRIMARY KEY AUTOINCREMENT,
                    scan_id TEXT NOT NULL UNIQUE,
                    payload TEXT NOT NULL,
                    queued_at TEXT NOT NULL
                );
                CREATE TABLE IF NOT EXISTS rejected (
                    seq INTEGER PRIMARY KEY,
                    scan_id TEXT NOT NULL,
                    payload TEXT NOT NULL,
                    queued_at TEXT NOT NULL,
                    rejected_at TEXT NOT NULL,
                    status INTEGER NOT NULL,
                    response TEXT NOT NULL
                );
                """
            )

    def add(self, scan_id: str, payload: dict[str, Any]) -> int:
        """Stores a scan's exact payload (resent unchanged, for ingest's idempotency). Returns the depth."""
        with self._lock:
            self._db.execute(
                "INSERT INTO scans (scan_id, payload, queued_at) VALUES (?, ?, ?)",
                (scan_id, json.dumps(payload, separators=(",", ":")), _now()),
            )
            depth = self._depth()
        self.added.set()
        return depth

    def oldest(self) -> QueuedScan | None:
        with self._lock:
            row = self._db.execute("SELECT seq, scan_id, payload FROM scans ORDER BY seq LIMIT 1").fetchone()
        return QueuedScan(row[0], row[1], json.loads(row[2])) if row else None

    def remove(self, seq: int) -> int:
        """After ingest accepted it. Returns the depth."""
        with self._lock:
            self._db.execute("DELETE FROM scans WHERE seq = ?", (seq,))
            return self._depth()

    def reject(self, seq: int, status: int, response: str) -> int:
        """Moves a scan ingest refused as invalid to ``rejected``, in one transaction. Returns the depth."""
        with self._lock:
            self._db.execute("BEGIN IMMEDIATE")
            try:
                self._db.execute(
                    "INSERT INTO rejected (seq, scan_id, payload, queued_at, rejected_at, status, response) "
                    "SELECT seq, scan_id, payload, queued_at, ?, ?, ? FROM scans WHERE seq = ?",
                    (_now(), status, response[:2000], seq),
                )
                self._db.execute("DELETE FROM scans WHERE seq = ?", (seq,))
                self._db.execute("COMMIT")
            except BaseException:
                self._db.execute("ROLLBACK")
                raise
            return self._depth()

    def depth(self) -> int:
        with self._lock:
            return self._depth()

    def rejected_count(self) -> int:
        with self._lock:
            return self._db.execute("SELECT COUNT(*) FROM rejected").fetchone()[0]

    def close(self) -> None:
        with self._lock:
            self._db.close()

    def _depth(self) -> int:
        return self._db.execute("SELECT COUNT(*) FROM scans").fetchone()[0]


def _now() -> str:
    return datetime.now(UTC).isoformat(timespec="milliseconds").replace("+00:00", "Z")
