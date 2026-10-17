from cupboard_pi.scan_queue import ScanQueue


def test_survives_a_restart_in_order(tmp_path):
    path = tmp_path / "q.sqlite3"
    q = ScanQueue(path)
    q.add("id-1", {"scanId": "id-1", "barcode": "1"})
    q.add("id-2", {"scanId": "id-2", "barcode": "2"})
    q.close()
    q = ScanQueue(path)  # "restart"
    assert q.depth() == 2
    first = q.oldest()
    assert (first.scan_id, first.payload["barcode"]) == ("id-1", "1")
    assert q.remove(first.seq) == 1
    assert q.oldest().scan_id == "id-2"


def test_add_wakes_the_sender(tmp_path):
    q = ScanQueue(tmp_path / "q.sqlite3")
    q.added.clear()
    q.add("id-1", {})
    assert q.added.is_set()


def test_reject_moves_the_scan_aside(tmp_path):
    q = ScanQueue(tmp_path / "q.sqlite3")
    q.add("id-1", {"scanId": "id-1"})
    q.add("id-2", {"scanId": "id-2"})
    assert q.reject(q.oldest().seq, 400, '{"errors":["bad"]}') == 1
    assert q.rejected_count() == 1
    assert q.oldest().scan_id == "id-2"
