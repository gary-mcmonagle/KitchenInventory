#!/usr/bin/env bash
# Read-only: run a SELECT query against one Cosmos container, as you (Entra ID; keys are disabled).
# Usage: deploy/verify/cosmos.sh <container> ["SELECT ... "]   (default: SELECT * FROM c; at most 100 items shown)
# Cross-partition queries work for plain filters and projections. TOP, ORDER BY, GROUP BY and aggregates across
# partitions aren't served by the gateway this simple client uses: filter on the partition key instead.
source "$(dirname "$0")/_common.sh"

CONTAINER=${1:-}
QUERY=${2:-"SELECT * FROM c"}
[[ -n "$CONTAINER" ]] || { echo "Usage: $0 <container> [\"SELECT ...\"]" >&2; exit 2; }

ENDPOINT=$(infra_output cosmosEndpoint)
DATABASE=$(infra_output cosmosDatabaseName)
python3 -I "$(dirname "$0")/cosmos_query.py" "$ENDPOINT" "$DATABASE" "$CONTAINER" "$QUERY"
