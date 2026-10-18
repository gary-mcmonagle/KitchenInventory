#!/usr/bin/env bash
# Clear the live household data: empty stock, ready for a stock take (or for release). IRREVERSIBLE.
#
# Usage: deploy/reset-data.sh
#
# Deletes these Cosmos containers, then runs deploy/infra.sh, which recreates them empty exactly as infra/modules/
# cosmos.bicep defines them (you approve its what-if as usual), then restarts both Function apps so the change-feed
# processor starts afresh:
#   scanEvents, products, inventoryItems, unmatchedCheckOuts, sentDigests, leases (the change feed's bookmarks)
# Keeps:
#   devices (the scanners' roles and the Pi's heartbeat state: setup, not stock), and every other resource.
# Data-plane role assignments are on the database, so they survive. Run it as Gary (needs Owner/Contributor on the
# resource group); it asks you to type the resource group's name before deleting anything.
set -euo pipefail

cd "$(dirname "$0")/.."

fail() { echo "ERROR: $*" >&2; exit 1; }
az account show >/dev/null 2>&1 || fail "Not logged in to Azure. Run: az login"

output() { az deployment sub show --name cupboard-tracker --query "properties.outputs.$1.value" -o tsv; }
RG=$(output resourceGroupName)
ACCOUNT=$(output cosmosAccountName)
DATABASE=$(output cosmosDatabaseName)
API=$(output apiFunctionAppName)
INGEST=$(output ingestFunctionAppName)
CONTAINERS=(scanEvents products inventoryItems unmatchedCheckOuts sentDigests leases)

echo "Subscription:  $(az account show --query name -o tsv)"
echo "Cosmos:        $ACCOUNT / $DATABASE in $RG"
echo "Will DELETE:   ${CONTAINERS[*]}"
echo "Will keep:     devices"
echo
echo "This permanently deletes all stock, products, scans, unmatched check-outs and sent digests."
read -r -p "Type the resource group's name ($RG) to go ahead: " answer
[[ "$answer" == "$RG" ]] || fail "Not confirmed. Nothing was deleted."

for container in "${CONTAINERS[@]}"; do
  if az cosmosdb sql container show -g "$RG" -a "$ACCOUNT" -d "$DATABASE" -n "$container" >/dev/null 2>&1; then
    echo "Deleting $container..."
    az cosmosdb sql container delete -g "$RG" -a "$ACCOUNT" -d "$DATABASE" -n "$container" --yes
  else
    echo "$container already gone."
  fi
done

echo
echo "Recreating the containers with deploy/infra.sh (check the what-if: it should create these ${#CONTAINERS[@]})."
deploy/infra.sh

echo
echo "Restarting both Function apps..."
az functionapp restart -g "$RG" -n "$API"
az functionapp restart -g "$RG" -n "$INGEST"
echo
echo "Done: stock is empty. Check with: deploy/verify/cosmos.sh products   (expect 0 items)"
