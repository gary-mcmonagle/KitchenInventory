#!/usr/bin/env bash
# Read-only: get an API token with your own az login and print its CLAIMS only (never the token itself).
# Usage: deploy/verify/token.sh
source "$(dirname "$0")/_common.sh"
source "$(dirname "$0")/_token.sh"
api_token | python3 -c '
import base64, json, sys
payload = sys.stdin.read().strip().split(".")[1]
claims = json.loads(base64.urlsafe_b64decode(payload + "=" * (-len(payload) % 4)))
for k in ("aud", "iss", "tid", "azp", "scp", "oid", "name", "preferred_username", "ver", "exp"):
    if k in claims:
        print(f"{k:20} {claims[k]}")
'
