#!/usr/bin/env bash
# Set or rotate a secret in Cupboard Tracker's Key Vault (#42). Gary runs this (Key Vault Secrets Officer).
#
# Usage: deploy/secrets.sh <secret-name>
#   jev-api-key   the Jev (TypeSafe System One) API key, read by the API app as JEV_API_KEY
#
# The value comes from the matching environment variable (e.g. JEV_API_KEY), or a hidden prompt. Never from an
# argument (shell history) or a file in the repo. It's passed to az on stdin and never echoed, logged or written to
# disk. Afterwards the apps that read it are told to refresh their Key Vault references, so the new value is live
# within a minute or so instead of within 24 hours.
set -euo pipefail

cd "$(dirname "$0")/.."

RG="rg-cupboard-tracker"

name=${1:-}
case "$name" in
  jev-api-key) env_name=JEV_API_KEY; component=api ;;
  -h|--help|"") sed -n '2,10p' "$0"; exit 2 ;;
  *) echo "Unknown secret: $name (known: jev-api-key)" >&2; exit 2 ;;
esac

if ! az account show >/dev/null 2>&1; then
  echo "Not logged in to Azure. Run: az login" >&2
  exit 1
fi

vault=$(az deployment sub show --name cupboard-tracker --query properties.outputs.keyVaultName.value -o tsv 2>/dev/null || true)
[[ -n "$vault" ]] || { echo "No Key Vault in the infra deployment's outputs. Run deploy/infra.sh first." >&2; exit 1; }

value=${!env_name:-}
if [[ -z "$value" ]]; then
  read -r -s -p "Value for $name (hidden; or set $env_name): " value
  echo
fi
[[ -n "$value" ]] || { echo "No value given." >&2; exit 1; }

echo "Setting $name in $vault (value not shown, ${#value} characters)..."
version=$(printf '%s' "$value" | az keyvault secret set --vault-name "$vault" --name "$name" --file /dev/stdin \
  --encoding utf-8 --content-type text/plain --query "id" -o tsv | sed 's|.*/||')
unset value
echo "Stored as version $version."

app=$(az functionapp list -g "$RG" --query "[?tags.component=='$component'].name | [0]" -o tsv)
app_id=$(az functionapp show -g "$RG" -n "$app" --query id -o tsv)
echo "Refreshing Key Vault references on $app..."
if az rest --method post --url "https://management.azure.com${app_id}/config/configreferences/appsettings/refresh?api-version=2022-03-01" >/dev/null 2>&1; then
  echo "Done. Check: az rest --method get --url \"https://management.azure.com${app_id}/config/configreferences/appsettings/${env_name}?api-version=2022-03-01\" --query properties.status"
else
  echo "Couldn't trigger a refresh; the app picks up the new version within 24 hours, or on: az functionapp restart -g $RG -n $app" >&2
fi
