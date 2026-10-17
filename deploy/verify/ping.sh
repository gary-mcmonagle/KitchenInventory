#!/usr/bin/env bash
# Read-only: call /api/ping on both Function apps, and check the Pi client service, showing each one's deployed commit.
# Usage: deploy/verify/ping.sh
# The ingest function key is fetched at runtime and passed to curl on stdin; it's never shown or saved.
source "$(dirname "$0")/_common.sh"

RG=$(infra_output resourceGroupName)
INGEST_APP=$(infra_output ingestFunctionAppName)
for pair in "ingest:$(infra_output ingestFunctionAppHostname)" "api:$(infra_output apiFunctionAppHostname)"; do
  name=${pair%%:*}; host=${pair#*:}
  if [[ $name == ingest ]]; then
    key=$(az functionapp keys list -g "$RG" -n "$INGEST_APP" --query functionKeys.default -o tsv)
    out=$(printf 'x-functions-key: %s\n' "$key" | curl -s --max-time 30 -w ' HTTP %{http_code}' -H @- "https://$host/api/ping" || true)
    unset key
  else
    out=$(curl -s --max-time 30 -w ' HTTP %{http_code}' "https://$host/api/ping" || true)
  fi
  printf '%-7s %s\n' "$name" "${out:-no response}"
done

# The Pi client: service state and the live release's commit, over SSH (PI_HOST, default gary@cupboard-pi.local).
PI_HOST=${PI_HOST:-gary@cupboard-pi.local}
out=$(ssh -o BatchMode=yes -o ConnectTimeout=10 "$PI_HOST" \
  'echo "$(systemctl is-active cupboard-pi.service) commit $(cat /opt/cupboard-tracker/current/DEPLOYED_SHA 2>/dev/null || echo none)"' \
  2>/dev/null || true)
printf '%-7s %s\n' pi "${out:-unreachable ($PI_HOST)}"
