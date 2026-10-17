"""Ingest URL and function key from Azure via the az CLI. The key is returned to the caller only:
never printed, logged or written anywhere."""

from __future__ import annotations

import json
import subprocess

DEPLOYMENT = "cupboard-tracker"


class AzureError(RuntimeError):
    pass


def _az(*args: str) -> str:
    try:
        out = subprocess.run(["az", *args], check=True, capture_output=True, text=True)
    except FileNotFoundError as e:
        raise AzureError("The az CLI isn't installed.") from e
    except subprocess.CalledProcessError as e:
        # stderr from az never contains the key (the key only appears on stdout of `keys list`).
        raise AzureError(f"az {args[0]} {args[1]} failed: {e.stderr.strip()[:300]}") from e
    return out.stdout.strip()


def ingest_target() -> tuple[str, str]:
    """(ingest base URL, function key) from the infra deployment outputs."""
    outputs = json.loads(
        _az("deployment", "sub", "show", "--name", DEPLOYMENT, "--query", "properties.outputs", "-o", "json")
    )
    try:
        rg = outputs["resourceGroupName"]["value"]
        app = outputs["ingestFunctionAppName"]["value"]
        host = outputs["ingestFunctionAppHostname"]["value"]
    except (KeyError, TypeError) as e:
        raise AzureError("Infra deployment outputs are missing. Run deploy/infra.sh first.") from e
    key = _az(
        "functionapp", "keys", "list", "-g", rg, "-n", app, "--query", "functionKeys.default", "-o", "tsv"
    )
    if not key:
        raise AzureError("Couldn't fetch the ingest function key.")
    return f"https://{host}", key
