"""Read-only Cosmos SELECTs with the request charge (RU), for spike #124. Entra token from `az`; nothing is written.

Cross-partition TOP / ORDER BY / aggregates aren't served by the gateway, so select rows and aggregate in Python.
"""
import json, re, subprocess, urllib.error, urllib.parse, urllib.request
from email.utils import formatdate

def _sh(*a): return subprocess.run(a, check=True, capture_output=True, text=True).stdout.strip()

def _outputs():
    q = "properties.outputs"
    o = json.loads(_sh("az", "deployment", "sub", "show", "--name", "cupboard-tracker", "--query", q, "-o", "json"))
    return o["cosmosEndpoint"]["value"], o["cosmosDatabaseName"]["value"]

ENDPOINT, DATABASE = _outputs()

def query(container: str, sql: str, params=None, partition=None):
    """Returns (documents, total RU, pages)."""
    assert re.match(r"^\s*SELECT\b", sql, re.I) and re.fullmatch(r"[A-Za-z0-9_-]+", container)
    base = ENDPOINT.rstrip("/").removesuffix(":443")
    tok = _sh("az", "account", "get-access-token", "--resource", base, "--query", "accessToken", "-o", "tsv")
    auth = urllib.parse.quote(f"type=aad&ver=1.0&sig={tok}", safe="")
    url = f"{base}/dbs/{DATABASE}/colls/{container}/docs"
    docs, ru, pages, cont = [], 0.0, 0, None
    while True:
        h = {"Authorization": auth, "x-ms-date": formatdate(usegmt=True), "x-ms-version": "2018-12-31",
             "Content-Type": "application/query+json", "x-ms-documentdb-isquery": "True", "x-ms-max-item-count": "200"}
        if partition is None: h["x-ms-documentdb-query-enablecrosspartition"] = "True"
        else: h["x-ms-documentdb-partitionkey"] = json.dumps([partition])
        if cont: h["x-ms-continuation"] = cont
        body = json.dumps({"query": sql, "parameters": params or []}).encode()
        req = urllib.request.Request(url, data=body, headers=h, method="POST")
        for attempt in range(4):  # the connection occasionally stalls: retry
            try:
                with urllib.request.urlopen(req, timeout=20) as r:
                    page = json.load(r); cont = r.headers.get("x-ms-continuation"); ru += float(r.headers.get("x-ms-request-charge", 0))
                break
            except urllib.error.HTTPError as e:
                raise RuntimeError(f"HTTP {e.code}: {e.read().decode()[:300]}")
            except (urllib.error.URLError, TimeoutError):
                if attempt == 3: raise
        docs += page.get("Documents", []); pages += 1
        if not cont: break
    return docs, round(ru, 1), pages
