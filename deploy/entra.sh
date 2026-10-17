#!/usr/bin/env bash
# Deploy the Entra ID app registrations (infra/entra.bicep, #36) as cupboard-deployer, from its own Azure CLI profile.
#
# Usage:
#   AZURE_CONFIG_DIR=~/.azure-cupboard-deployer az login --tenant <tenantId>      # once in a while, interactive with MFA
#   AZURE_CONFIG_DIR=~/.azure-cupboard-deployer deploy/entra.sh [--swa-host <hostname>] [--yes]
#
#   --swa-host   the Static Web App's hostname, added as an SPA redirect URI (http://localhost:5173/ is always there)
#   --yes        don't ask for confirmation
#
# Safe to re-run: the registrations are matched by uniqueName and updated in place. Never run it with Gary's own
# profile: it refuses unless the signed-in account is cupboard-deployer and AZURE_CONFIG_DIR is a separate profile.
set -euo pipefail

cd "$(dirname "$0")/.."
RG=rg-cupboard-tracker
DEPLOYMENT=cupboard-entra
DEPLOYER_PREFIX=cupboard-deployer@

SWA_HOST=""
YES=false
while [[ $# -gt 0 ]]; do
  case "$1" in
    --swa-host) SWA_HOST=${2:?--swa-host needs a hostname}; shift 2 ;;
    --yes) YES=true; shift ;;
    -h|--help) sed -n '2,13p' "$0"; exit 0 ;;
    *) echo "Unknown option: $1" >&2; exit 2 ;;
  esac
done

fail() { echo "ERROR: $*" >&2; exit 1; }

[[ -n "${AZURE_CONFIG_DIR:-}" && "${AZURE_CONFIG_DIR%/}" != "$HOME/.azure" ]] \
  || fail "Run this with the deployer's own CLI profile: AZURE_CONFIG_DIR=~/.azure-cupboard-deployer $0"
USER_NAME=$(az account show --query user.name -o tsv 2>/dev/null) \
  || fail "Not signed in. Run: AZURE_CONFIG_DIR=$AZURE_CONFIG_DIR az login --tenant <tenantId>"
[[ "$USER_NAME" == "$DEPLOYER_PREFIX"* ]] || fail "Signed in as $USER_NAME, not cupboard-deployer. Sign in as the deployer."

URIS='"http://localhost:5173/"'
if [[ -n "$SWA_HOST" ]]; then
  [[ "$SWA_HOST" =~ ^[a-z0-9.-]+$ ]] || fail "--swa-host must be a bare hostname, e.g. nice-sea-0123.azurestaticapps.net"
  URIS="$URIS,\"https://$SWA_HOST/\""
fi

echo "Signed in as: $USER_NAME (profile $AZURE_CONFIG_DIR)"
echo "Deploying infra/entra.bicep to $RG as deployment '$DEPLOYMENT'"
echo "SPA redirect URIs: [$URIS]"
if ! $YES; then
  read -r -p "Create or update the Cupboard Tracker app registrations? [y/N] " answer
  [[ "$answer" =~ ^[Yy]$ ]] || { echo "Cancelled."; exit 1; }
fi

az deployment group create -g "$RG" -n "$DEPLOYMENT" --template-file infra/entra.bicep \
  --parameters spaRedirectUris="[$URIS]" -o none

echo
echo "Outputs (not secrets; deploy/infra.sh reads them from this deployment):"
az deployment group show -g "$RG" -n "$DEPLOYMENT" \
  --query "properties.outputs | [keys(@), values(@)[].value]" -o json \
  | python3 -c 'import json, sys; keys, values = json.load(sys.stdin); print("\n".join("  %s: %s" % kv for kv in zip(keys, values)))'
echo
echo "Next: Gary assigns users in the Entra admin center (Enterprise applications → Cupboard Tracker → Users and groups),"
echo "then runs deploy/infra.sh so the API app gets the client IDs."
