#!/usr/bin/env bash
# Build, test and deploy BOTH Function apps (ingest and API) to the live environment, then smoke-check them.
#
# Usage: deploy/functions.sh [--force]
#   --force   allow a dirty tree, a branch other than main, or a commit not pushed to origin/main
#
# App names come from the infra deployment's outputs (deploy/infra.sh), never hard-coded.
# The ingest function key is fetched at runtime, passed to curl on stdin, and never echoed or written to disk.
set -euo pipefail
set +x  # never trace: a key passes through this script

cd "$(dirname "$0")/.."
ROOT=$(pwd)

FORCE=false
for arg in "$@"; do
  case "$arg" in
    --force) FORCE=true ;;
    -h|--help) sed -n '2,9p' "$0"; exit 0 ;;
    *) echo "Unknown option: $arg" >&2; exit 2 ;;
  esac
done

fail() { echo "ERROR: $*" >&2; exit 1; }

# 1. Only deploy committed, pushed code from main (unless --force).
if [[ -n "$(git status --porcelain)" ]]; then
  $FORCE || fail "Uncommitted changes. Commit them, or use --force."
  echo "WARNING: deploying with uncommitted changes (--force)."
fi
git fetch -q origin main
BRANCH=$(git rev-parse --abbrev-ref HEAD)
# On main, or exactly origin/main's commit (e.g. a detached worktree checkout of main).
if [[ "$BRANCH" != "main" && "$(git rev-parse HEAD)" != "$(git rev-parse origin/main)" ]]; then
  $FORCE || fail "On '$BRANCH', not main. Merge to main first, or use --force."
  echo "WARNING: deploying from '$BRANCH' (--force)."
fi
if ! git merge-base --is-ancestor HEAD origin/main; then
  $FORCE || fail "HEAD isn't pushed to origin/main. Push first, or use --force."
  echo "WARNING: HEAD isn't on origin/main (--force)."
fi
SHA=$(git rev-parse --short HEAD)

az account show >/dev/null 2>&1 || fail "Not logged in to Azure. Run: az login"

# App names from the infra deployment outputs.
outputs() { az deployment sub show --name cupboard-tracker --query "properties.outputs.$1.value" -o tsv; }
RG=$(outputs resourceGroupName) || fail "Infra deployment 'cupboard-tracker' not found. Run deploy/infra.sh first."
INGEST_APP=$(outputs ingestFunctionAppName)
API_APP=$(outputs apiFunctionAppName)
INGEST_HOST=$(outputs ingestFunctionAppHostname)
API_HOST=$(outputs apiFunctionAppHostname)
[[ -n "$RG" && -n "$INGEST_APP" && -n "$API_APP" ]] || fail "Couldn't read app names from the infra deployment outputs."

echo "Deploying $SHA to $INGEST_APP and $API_APP (resource group $RG)"
echo

# 2. Tests must pass.
echo "== dotnet test"
(cd BE && dotnet test) || fail "Tests failed. Nothing was deployed."
echo

# 3. Publish (Release) and deploy both apps.
WORK=$(mktemp -d)
trap 'rm -rf "$WORK"' EXIT

publish_and_deploy() {
  local project=$1 app=$2
  echo "== $project → $app"
  dotnet publish "BE/src/$project/$project.csproj" -c Release -o "$WORK/$project" --nologo -v quiet \
    || fail "Publish failed for $project."
  (cd "$WORK/$project" && zip -qr "$WORK/$project.zip" .)
  # 4. Record the commit first, so the code deploy rolls out code and setting together
  #    (setting it afterwards causes a second roll-out, briefly serving the old value).
  #    infra.sh preserves COMMIT_SHA on re-runs.
  az functionapp config appsettings set -g "$RG" -n "$app" --settings "COMMIT_SHA=$SHA" -o none \
    || fail "Couldn't set COMMIT_SHA on $app."
  az functionapp deployment source config-zip -g "$RG" -n "$app" --src "$WORK/$project.zip" -o none \
    || fail "Deploying $project to $app failed."
  echo
}
publish_and_deploy CupboardTracker.Functions.Ingest "$INGEST_APP"
publish_and_deploy CupboardTracker.Functions.Api "$API_APP"

# 5. Smoke check: /api/ping must report the deployed SHA 3 times in a row (so a roll-out in progress, with an
#    old instance still answering, doesn't pass). Retries for ~2 minutes to allow for cold start.
ping_until_sha() {
  local name=$1 url=$2 key=${3:-}
  local deadline=$((SECONDS + ${SMOKE_TIMEOUT_SECONDS:-120})) body="" streak=0
  while (( SECONDS < deadline )); do
    if [[ -n "$key" ]]; then
      body=$(printf 'x-functions-key: %s\n' "$key" | curl -s --max-time 20 -H @- "$url" || true)
    else
      body=$(curl -s --max-time 20 "$url" || true)
    fi
    if [[ "$body" == *"\"commit\":\"$SHA\""* ]]; then
      streak=$((streak + 1))
      if (( streak >= 3 )); then
        echo "  $name: OK $body"
        return 0
      fi
      sleep 2
      continue
    fi
    streak=0
    sleep 5
  done
  echo "  $name: FAILED (last response: ${body:-none})"
  return 1
}

echo "== Smoke check"
INGEST_KEY=$(az functionapp keys list -g "$RG" -n "$INGEST_APP" --query "functionKeys.default" -o tsv 2>/dev/null || true)
[[ -n "$INGEST_KEY" ]] || fail "Couldn't fetch the ingest function key."
SMOKE=0
ping_until_sha "ingest" "https://$INGEST_HOST/api/ping" "$INGEST_KEY" || SMOKE=1
unset INGEST_KEY
ping_until_sha "api" "https://$API_HOST/api/ping" || SMOKE=1

# 6. Summary.
echo
echo "== Summary"
echo "  commit:  $SHA"
echo "  ingest:  $INGEST_APP (https://$INGEST_HOST)"
echo "  api:     $API_APP (https://$API_HOST)"
if (( SMOKE == 0 )); then
  echo "  smoke:   passed"
else
  echo "  smoke:   FAILED"
  exit 1
fi
