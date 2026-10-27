#!/usr/bin/env bash
# Register the Foundry models in deploy/ai-models.seed.json in the settings container (#120), and say what each is
# allowed to be used for and whether it takes a temperature. Until the model test suite (#121) does that itself.
#
# Usage: deploy/seed-ai-models.sh [--dry-run] [--yes]
#
# Safe to re-run: it adds missing models and sets temperature mode and jobs on the listed ones. It never changes which
# model each job uses (`active`), and never removes anything. It checks every deployment name exists in Foundry first.
# It writes as YOU (Entra ID; Cosmos keys are off), so your login needs "Cosmos DB Built-in Data Contributor": if it
# doesn't have it, the script stops and prints the one command that grants it (and the one that removes it again).
# Writing is Gary's (live household data): the agent only ever runs it with --dry-run, which reads and writes nothing.
set -euo pipefail

cd "$(dirname "$0")/.."

fail() { echo "ERROR: $*" >&2; exit 1; }
az account show >/dev/null 2>&1 || fail "Not logged in to Azure. Run: az login"
command -v python3 >/dev/null || fail "python3 is needed."

output() { az deployment sub show --name cupboard-tracker --query "properties.outputs.$1.value" -o tsv 2>/dev/null \
  || fail "Infra deployment 'cupboard-tracker' not found. Run deploy/infra.sh first."; }
RG=$(output resourceGroupName)
COSMOS=$(output cosmosAccountName)
ENDPOINT=$(output cosmosEndpoint)
DATABASE=$(output cosmosDatabaseName)
FOUNDRY=$(output aiServicesName)

echo "Subscription: $(az account show --query name -o tsv)"
echo "Cosmos:       $COSMOS / $DATABASE (settings container)"
echo "Foundry:      $FOUNDRY"
az cosmosdb sql container show -g "$RG" -a "$COSMOS" -d "$DATABASE" -n settings >/dev/null 2>&1 \
  || fail "The 'settings' container doesn't exist yet. Run deploy/infra.sh (#120)."

DEPLOYED=$(az cognitiveservices account deployment list -g "$RG" -n "$FOUNDRY" --query "[].name" -o tsv | paste -sd, -)
USER_OID=$(az ad signed-in-user show --query id -o tsv)
USER_NAME=$(az ad signed-in-user show --query displayName -o tsv)

set +e
python3 -I deploy/seed_ai_models.py --endpoint "$ENDPOINT" --database "$DATABASE" --seed deploy/ai-models.seed.json \
  --user-oid "$USER_OID" --user-name "$USER_NAME" --foundry-deployments "$DEPLOYED" "$@"
STATUS=$?
set -e
if [[ $STATUS -eq 3 ]]; then
  echo >&2
  echo "Grant it, run this script again, then take it away:" >&2
  echo "  az cosmosdb sql role assignment create -g $RG -a $COSMOS --scope / --principal-id $USER_OID --role-definition-id 00000000-0000-0000-0000-000000000002" >&2
  echo "  az cosmosdb sql role assignment list -g $RG -a $COSMOS --query \"[?principalId=='$USER_OID' && ends_with(roleDefinitionId,'0002')].name\" -o tsv   # the id to delete" >&2
  echo "  az cosmosdb sql role assignment delete -g $RG -a $COSMOS --role-assignment-id <that id> --yes" >&2
fi
exit $STATUS
