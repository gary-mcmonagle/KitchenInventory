# Shared helpers for the read-only verification scripts. Source it; don't run it.
set -euo pipefail
set +x  # keys pass through some of these scripts

fail() { echo "ERROR: $*" >&2; exit 1; }
az account show >/dev/null 2>&1 || fail "Not logged in to Azure. Run: az login"

# Values from the infra deployment outputs (deploy/infra.sh), never hard-coded.
infra_output() {
  az deployment sub show --name cupboard-tracker --query "properties.outputs.$1.value" -o tsv 2>/dev/null \
    || fail "Infra deployment 'cupboard-tracker' not found. Run deploy/infra.sh first."
}
