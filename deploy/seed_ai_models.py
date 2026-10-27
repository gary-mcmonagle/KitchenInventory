"""Register Foundry models in the settings container's `aiModels` document (#120), as the signed-in person.

Used by deploy/seed-ai-models.sh, until the model test suite (#121) sets each model's `temperatureMode` and `jobs`
itself. It adds the models in the seed file that aren't registered yet, and sets `temperatureMode` and `jobs` on the
ones that are. It never touches `active` (which model each job uses), never removes or renames anything, and skips a
model that already has test results (the test suite owns those). Cosmos has local auth off, so it writes over REST
with an Entra token from `az` (needs the Cosmos DB Built-in Data Contributor role; the token is never printed).
"""

import argparse
import json
import re
import subprocess
import sys
import urllib.error
import urllib.parse
import urllib.request
from datetime import datetime, timezone
from email.utils import formatdate
from pathlib import Path

JOBS = ("recipes", "photos", "genericNames")
MODES = ("usual", "default")
MAX_MODELS = 20
ID = re.compile(r"^[A-Za-z0-9_-]{1,40}$")
DEPLOYMENT = re.compile(r"^[A-Za-z0-9._-]{1,64}$")
DOCUMENT_ID = "aiModels"
CONTAINER = "settings"


def timestamp(at: datetime) -> str:
    return at.astimezone(timezone.utc).strftime("%Y-%m-%dT%H:%M:%S.") + f"{at.microsecond // 1000:03d}Z"


def check_seed(seed: dict) -> list[str]:
    """What's wrong with the seed file, as messages (empty when it's fine)."""
    problems: list[str] = []
    models = seed.get("models")
    if not isinstance(models, list) or not models:
        return ["The seed file needs a non-empty 'models' list."]
    ids, deployments = set(), set()
    for m in models:
        name = m.get("id", "?") if isinstance(m, dict) else "?"
        if not isinstance(m, dict):
            problems.append("Every model must be an object.")
            continue
        if not ID.match(str(m.get("id", ""))):
            problems.append(f"{name}: id must be 1 to 40 letters, digits, - or _.")
        if not 1 <= len(str(m.get("label", "")).strip()) <= 60:
            problems.append(f"{name}: label must be 1 to 60 characters.")
        if not DEPLOYMENT.match(str(m.get("deployment", ""))):
            problems.append(f"{name}: deployment must be 1 to 64 letters, digits, '.', '_' or '-'.")
        if m.get("temperatureMode") not in MODES:
            problems.append(f"{name}: temperatureMode must be one of {MODES}.")
        jobs = m.get("jobs")
        if not isinstance(jobs, list) or any(j not in JOBS for j in jobs) or len(set(jobs)) != len(jobs):
            problems.append(f"{name}: jobs must be a list without repeats from {JOBS}.")
        if m.get("id") in ids:
            problems.append(f"{name}: id repeated.")
        if str(m.get("deployment", "")).lower() in deployments:
            problems.append(f"{name}: deployment repeated.")
        ids.add(m.get("id"))
        deployments.add(str(m.get("deployment", "")).lower())
    return problems


def merge(existing: dict | None, seed: dict, user_oid: str, user_name: str, now: datetime) -> tuple[dict, list[str]]:
    """The document with the seed models added or updated, and a line per change. Raises ValueError if it can't."""
    doc = json.loads(json.dumps(existing)) if existing else {"id": DOCUMENT_ID, "type": "aiModels", "models": [], "active": {}}
    doc.setdefault("models", [])
    doc.setdefault("active", {})
    changes: list[str] = []
    by_id = {m["id"]: m for m in doc["models"]}
    for want in seed["models"]:
        have = by_id.get(want["id"])
        if have is None:
            other = next((m for m in doc["models"] if m["deployment"].lower() == want["deployment"].lower()), None)
            if other is not None:
                raise ValueError(f"{want['deployment']} is already registered as '{other['id']}' ({other['label']}).")
            if len(doc["models"]) >= MAX_MODELS:
                raise ValueError(f"At most {MAX_MODELS} models can be registered.")
            added = {
                "id": want["id"], "label": want["label"].strip(), "deployment": want["deployment"],
                "temperatureMode": want["temperatureMode"], "jobs": list(want["jobs"]),
                "addedBy": user_oid, "addedByName": user_name, "addedAt": timestamp(now), "tests": None,
            }
            doc["models"].append(added)
            by_id[want["id"]] = added
            changes.append(f"add {want['id']} ({want['deployment']}): temperature {want['temperatureMode']}, jobs {', '.join(want['jobs']) or 'none'}")
            continue
        if have["deployment"] != want["deployment"]:
            raise ValueError(f"{want['id']} is registered for deployment {have['deployment']}; a deployment name can't change.")
        if have.get("tests"):
            changes.append(f"skip {want['id']}: it has test results, which now own its temperature and jobs")
            continue
        if have.get("temperatureMode") != want["temperatureMode"]:
            changes.append(f"set {want['id']} temperature {have.get('temperatureMode')} -> {want['temperatureMode']}")
            have["temperatureMode"] = want["temperatureMode"]
        if sorted(have.get("jobs", [])) != sorted(want["jobs"]):
            changes.append(f"set {want['id']} jobs {', '.join(have.get('jobs', [])) or 'none'} -> {', '.join(want['jobs']) or 'none'}")
            have["jobs"] = list(want["jobs"])
    if not any(c.startswith(("add", "set")) for c in changes):
        return existing or doc, [c for c in changes if c.startswith("skip")]
    return doc, changes


class Cosmos:
    """Just enough of the Cosmos REST API to read, create and replace one document."""

    def __init__(self, endpoint: str, database: str):
        self.base = endpoint.rstrip("/").removesuffix(":443")
        self.database = database
        self.auth = urllib.parse.quote(f"type=aad&ver=1.0&sig={self._token()}", safe="")

    def _token(self) -> str:
        out = subprocess.run(
            ["az", "account", "get-access-token", "--resource", self.base, "--query", "accessToken", "-o", "tsv"],
            check=True, capture_output=True, text=True)
        return out.stdout.strip()

    def _send(self, method: str, path: str, body: dict | None = None, etag: str | None = None) -> tuple[int, dict | None, str | None]:
        headers = {
            "Authorization": self.auth, "x-ms-date": formatdate(usegmt=True), "x-ms-version": "2018-12-31",
            "Content-Type": "application/json", "x-ms-documentdb-partitionkey": json.dumps([DOCUMENT_ID]),
        }
        if etag:
            headers["If-Match"] = etag
        data = json.dumps(body).encode() if body is not None else None
        request = urllib.request.Request(f"{self.base}/dbs/{self.database}/colls/{CONTAINER}/docs{path}", data=data, headers=headers, method=method)  # noqa: S310
        try:
            with urllib.request.urlopen(request, timeout=30) as response:  # noqa: S310
                text = response.read().decode()
                return response.status, (json.loads(text) if text else None), response.headers.get("etag")
        except urllib.error.HTTPError as e:
            detail = e.read().decode("utf-8", "replace")[:400]
            return e.code, {"error": detail}, None

    def read(self) -> tuple[dict | None, str | None]:
        status, body, etag = self._send("GET", f"/{DOCUMENT_ID}")
        if status == 404:
            return None, None
        if status != 200:
            raise RuntimeError(f"Cosmos returned HTTP {status} reading the settings: {body}")
        return body, etag

    def write(self, doc: dict, etag: str | None) -> None:
        status, body, _ = self._send("PUT", f"/{DOCUMENT_ID}", doc, etag) if etag else self._send("POST", "", doc)
        if status == 403:
            raise PermissionError("Cosmos refused the write (HTTP 403): this account isn't a Cosmos DB Built-in Data Contributor.")
        if status in (409, 412):
            raise RuntimeError("The settings changed while this ran. Run it again.")
        if status not in (200, 201):
            raise RuntimeError(f"Cosmos returned HTTP {status} writing the settings: {body}")


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument("--endpoint", required=True)
    parser.add_argument("--database", required=True)
    parser.add_argument("--seed", required=True, type=Path)
    parser.add_argument("--user-oid", required=True)
    parser.add_argument("--user-name", required=True)
    parser.add_argument("--foundry-deployments", required=True, help="Comma-separated names deployed in Foundry.")
    parser.add_argument("--yes", action="store_true", help="Don't ask first.")
    parser.add_argument("--dry-run", action="store_true", help="Show what would change; write nothing.")
    args = parser.parse_args()

    seed = json.loads(args.seed.read_text())
    if problems := check_seed(seed):
        print("The seed file has problems:\n  " + "\n  ".join(problems), file=sys.stderr)
        return 2
    deployed = {d.strip() for d in args.foundry_deployments.split(",") if d.strip()}
    missing = [m["deployment"] for m in seed["models"] if m["deployment"] not in deployed]
    if missing:
        print(f"Not deployed in Foundry (deployment names are case-sensitive): {', '.join(missing)}\nDeployed: {', '.join(sorted(deployed))}", file=sys.stderr)
        return 2

    cosmos = Cosmos(args.endpoint, args.database)
    existing, etag = cosmos.read()
    try:
        doc, changes = merge(existing, seed, args.user_oid, args.user_name, datetime.now(timezone.utc))
    except ValueError as e:
        print(f"Can't apply the seed: {e}", file=sys.stderr)
        return 2
    print("settings/aiModels:", "not there yet" if existing is None else f"{len(existing.get('models', []))} model(s) registered")
    active = {job: sel["modelId"] for job, sel in (existing or {}).get("active", {}).items()}
    print("In use now:      ", ", ".join(f"{j}={m}" for j, m in active.items()) or "nothing chosen: every job uses its default")
    for line in changes or ["nothing to change"]:
        print("  -", line)
    if not any(c.startswith(("add", "set")) for c in changes):
        return 0
    if args.dry_run:
        print("Dry run: nothing written.")
        return 0
    if not args.yes and input("Write this? [y/N] ").strip().lower() not in ("y", "yes"):
        print("Nothing written.")
        return 1
    try:
        cosmos.write(doc, etag)
    except PermissionError as e:
        print(f"{e}\nTo allow your login to write for now (Owner needed; remove it afterwards):\n"
              f"  az cosmosdb sql role assignment create -g <rg> -a <cosmos account> --scope / "
              f"--principal-id {args.user_oid} --role-definition-id 00000000-0000-0000-0000-000000000002\n"
              "(deploy/seed-ai-models.sh prints it with the names filled in.)", file=sys.stderr)
        return 3
    print("Written. Which model each job uses is unchanged; a change in use takes effect within about a minute.")
    return 0


if __name__ == "__main__":
    sys.exit(main())
