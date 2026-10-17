#!/usr/bin/env bash
# Deploy Cupboard Tracker's baseline infrastructure (infra/main.bicep) to the live environment.
#
# Usage: deploy/infra.sh [--yes] [--what-if-only]
#   --yes           deploy without the confirmation prompt
#   --what-if-only  show the what-if and stop
#
# Safe to re-run: the template is idempotent, and the live COMMIT_SHA of each Function app is
# read first and passed back in, so a re-run never resets it.
set -euo pipefail

cd "$(dirname "$0")/.."

YES=false
WHAT_IF_ONLY=false
for arg in "$@"; do
  case "$arg" in
    --yes) YES=true ;;
    --what-if-only) WHAT_IF_ONLY=true ;;
    -h|--help) sed -n '2,10p' "$0"; exit 0 ;;
    *) echo "Unknown option: $arg" >&2; exit 2 ;;
  esac
done

LOCATION="uksouth"
DEPLOYMENT_NAME="cupboard-tracker"
TEMPLATE="infra/main.bicep"
PARAMS="infra/main.bicepparam"
RG="rg-cupboard-tracker"

if ! az account show >/dev/null 2>&1; then
  echo "Not logged in to Azure. Run: az login" >&2
  exit 1
fi
echo "Subscription: $(az account show --query '[name, id]' -o tsv | paste -sd ' ' -)"
echo "Signed in as: $(az account show --query user.name -o tsv)"
echo

# Keep each Function app's deployed commit (set by the Function deploy script) across infra re-runs.
current_sha() {
  local component=$1
  [[ "$(az group exists -n "$RG")" == "true" ]] || { echo "not-deployed"; return; }
  local app
  app=$(az functionapp list -g "$RG" --query "[?tags.component=='$component'].name | [0]" -o tsv 2>/dev/null || true)
  [[ -n "$app" ]] || { echo "not-deployed"; return; }
  az functionapp config appsettings list -g "$RG" -n "$app" --query "[?name=='COMMIT_SHA'].value | [0]" -o tsv 2>/dev/null || echo "not-deployed"
}
INGEST_COMMIT_SHA=$(current_sha ingest); export INGEST_COMMIT_SHA
API_COMMIT_SHA=$(current_sha api); export API_COMMIT_SHA
# Read-only verification access goes to whoever runs this (Gary's az login on the MacBook), unless set explicitly.
if [[ -z "${VERIFIER_PRINCIPAL_ID:-}" ]]; then
  VERIFIER_PRINCIPAL_ID=$(az ad signed-in-user show --query id -o tsv 2>/dev/null || true)
fi
export VERIFIER_PRINCIPAL_ID
echo "Verifier (read-only access): ${VERIFIER_PRINCIPAL_ID:-none}"
INGEST_COMMIT_SHA=${INGEST_COMMIT_SHA:-not-deployed}
API_COMMIT_SHA=${API_COMMIT_SHA:-not-deployed}

echo "Building $TEMPLATE..."
az bicep build --file "$TEMPLATE" --stdout >/dev/null

echo "What-if (no changes are made by this step)..."
az deployment sub what-if \
  --name "$DEPLOYMENT_NAME" \
  --location "$LOCATION" \
  --template-file "$TEMPLATE" \
  --parameters "$PARAMS" \
  --result-format FullResourcePayloads

if $WHAT_IF_ONLY; then
  exit 0
fi

if ! $YES; then
  read -r -p "Deploy these changes to the LIVE environment? [y/N] " answer
  [[ "$answer" =~ ^[Yy]$ ]] || { echo "Cancelled."; exit 1; }
fi

echo "Deploying..."
az deployment sub create \
  --name "$DEPLOYMENT_NAME" \
  --location "$LOCATION" \
  --template-file "$TEMPLATE" \
  --parameters "$PARAMS" \
  --query "properties.outputs" -o json > /dev/null

echo
echo "Outputs:"
az deployment sub show --name "$DEPLOYMENT_NAME" \
  --query "properties.outputs | [keys(@), values(@)[].value]" -o json \
  | python3 -c 'import json, sys; keys, values = json.load(sys.stdin); print("\n".join("  %s: %s" % kv for kv in zip(keys, values)))'
