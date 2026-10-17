"""Sends queued scans to ingest, oldest first, deleting each only after a 2xx.

A failure (no network, a timeout, 401, 429, 5xx) leaves the scan queued and retries with exponential
back-off, 2 s doubling to at most 5 minutes; a success resets it. A 400 means ingest found the scan
invalid, which a resend can't fix, so it's moved to the queue's ``rejected`` table and the next scan
is sent.
"""

from __future__ import annotations

import logging
import threading
import time

from cupboard_pi.ingest_client import IngestClient, IngestError
from cupboard_pi.payloads import SCANS_PATH
from cupboard_pi.scan_queue import ScanQueue

log = logging.getLogger(__name__)

FIRST_RETRY_SECONDS = 2.0
MAX_RETRY_SECONDS = 300.0
IDLE_POLL_SECONDS = 1.0


def next_delay(previous: float) -> float:
    return min(MAX_RETRY_SECONDS, previous * 2 if previous else FIRST_RETRY_SECONDS)


class Sender:
    def __init__(self, queue: ScanQueue, client: IngestClient):
        self._queue = queue
        self._client = client

    def run(self, stop: threading.Event) -> None:
        delay = 0.0
        while not stop.is_set():
            self._queue.added.clear()
            item = self._queue.oldest()
            if item is None:
                self._queue.added.wait(IDLE_POLL_SECONDS)  # wakes as soon as a scan is added
                continue
            started = time.monotonic()
            try:
                response = self._client.post_json(SCANS_PATH, item.payload)
                failure = None if response.ok or response.status == 400 else f"HTTP {response.status}"
            except IngestError as e:
                response, failure = None, str(e)
            if response is not None and response.ok:
                depth = self._queue.remove(item.seq)
                log.info(
                    "Sent scan %s (HTTP %d in %.2f s); queue depth %d",
                    item.scan_id,
                    response.status,
                    time.monotonic() - started,
                    depth,
                )
                delay = 0.0
            elif response is not None and response.status == 400:
                depth = self._queue.reject(item.seq, response.status, response.body)
                log.error(
                    "Ingest rejected scan %s as invalid (kept in the rejected table, not resent): %s; "
                    "queue depth %d",
                    item.scan_id,
                    response.body[:500],
                    depth,
                )
            else:
                delay = next_delay(delay)
                log.warning(
                    "Sending scan %s failed (%s after %.2f s); retrying in %.0f s; queue depth %d",
                    item.scan_id,
                    failure,
                    time.monotonic() - started,
                    delay,
                    self._queue.depth(),
                )
                stop.wait(delay)
