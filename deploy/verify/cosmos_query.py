"""Read-only Cosmos DB query over REST with an Entra token from `az` (used by cosmos.sh).

Only SELECT queries are sent, and the caller's role (Cosmos DB Built-in Data Reader) can't write anyway.
The token is fetched in-process and never printed.
"""

import json
import re
import subprocess
import sys
import urllib.error
import urllib.parse
import urllib.request
from email.utils import formatdate

MAX_ITEMS = 100


def token(resource: str) -> str:
    out = subprocess.run(
        ["az", "account", "get-access-token", "--resource", resource, "--query", "accessToken", "-o", "tsv"],
        check=True, capture_output=True, text=True,
    )
    return out.stdout.strip()


def main() -> int:
    endpoint, database, container, query = sys.argv[1:5]
    if not re.match(r"^\s*SELECT\b", query, re.IGNORECASE):
        print("Only SELECT queries are allowed.", file=sys.stderr)
        return 2
    if not re.fullmatch(r"[A-Za-z0-9_-]+", container):
        print("Invalid container name.", file=sys.stderr)
        return 2

    base = endpoint.rstrip("/").removesuffix(":443")
    auth = urllib.parse.quote(f"type=aad&ver=1.0&sig={token(base)}", safe="")
    url = f"{base}/dbs/{database}/colls/{container}/docs"
    results, continuation = [], None
    while True:
        headers = {
            "Authorization": auth,
            "x-ms-date": formatdate(usegmt=True),
            "x-ms-version": "2018-12-31",
            "Content-Type": "application/query+json",
            "x-ms-documentdb-isquery": "True",
            "x-ms-documentdb-query-enablecrosspartition": "True",
            "x-ms-max-item-count": "50",
        }
        if continuation:
            headers["x-ms-continuation"] = continuation
        body = json.dumps({"query": query, "parameters": []}).encode()
        req = urllib.request.Request(url, data=body, headers=headers, method="POST")  # noqa: S310 (Cosmos https endpoint)
        try:
            with urllib.request.urlopen(req, timeout=30) as resp:  # noqa: S310
                page = json.load(resp)
                continuation = resp.headers.get("x-ms-continuation")
        except urllib.error.HTTPError as e:
            detail = e.read().decode("utf-8", "replace")[:500]
            print(f"Cosmos returned HTTP {e.code}: {detail}", file=sys.stderr)
            return 1
        results.extend(page.get("Documents", []))
        if not continuation or len(results) >= MAX_ITEMS:
            break

    print(json.dumps(results[:MAX_ITEMS], indent=2, ensure_ascii=False))
    print(f"({min(len(results), MAX_ITEMS)} item(s){' , truncated' if len(results) > MAX_ITEMS else ''})", file=sys.stderr)
    return 0


if __name__ == "__main__":
    sys.exit(main())
