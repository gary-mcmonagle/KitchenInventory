#!/usr/bin/env bash
# Build, test and deploy the React app (FE/) to the Static Web App, then smoke-check it.
#
# Usage: deploy/ui.sh [--force]
#   --force   allow a dirty tree, a branch other than main, or a commit not pushed to origin/main
#
# Config comes from the deployments, never the repo: the API hostname and Static Web App from 'cupboard-tracker'
# (deploy/infra.sh), the tenant, SPA client ID and API scope from 'cupboard-entra' (deploy/entra.sh). None of
# those are secrets. The SWA deployment token is fetched at runtime, passed to the SWA CLI in its environment,
# and never echoed or written to disk.
set -euo pipefail
set +x  # never trace: a token passes through this script

cd "$(dirname "$0")/.."
RG=rg-cupboard-tracker
SWA_CLI="@azure/static-web-apps-cli@2.0.10"

FORCE=false
for arg in "$@"; do
  case "$arg" in
    --force) FORCE=true ;;
    -h|--help) sed -n '2,10p' "$0"; exit 0 ;;
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
infra() { az deployment sub show --name cupboard-tracker --query "properties.outputs.$1.value" -o tsv; }
entra() { az deployment group show -g "$RG" -n cupboard-entra --query "properties.outputs.$1.value" -o tsv; }
SWA_NAME=$(infra staticWebAppName) || fail "No Static Web App in the infra outputs. Run deploy/infra.sh first."
SWA_HOST=$(infra staticWebAppHostname)
API_HOST=$(infra apiFunctionAppHostname)
TENANT_ID=$(entra tenantId) || fail "No Entra registrations. Run deploy/entra.sh (as cupboard-deployer) first."
SPA_CLIENT_ID=$(entra spaClientId)
API_SCOPE=$(entra apiScope)
[[ -n "$SWA_NAME" && -n "$SWA_HOST" && -n "$API_HOST" && -n "$SPA_CLIENT_ID" && -n "$API_SCOPE" ]] \
  || fail "Couldn't read the config from the deployment outputs."

echo "Deploying UI $SHA to $SWA_NAME (https://$SWA_HOST), API https://$API_HOST"
echo

# 2. Install, lint and test.
echo "== npm ci, lint, test"
(cd FE && npm ci --no-audit --no-fund --silent && npm run lint --silent && npm test --silent) \
  || fail "Lint or tests failed. Nothing was deployed."
echo

# 3. Build with the deployed config, and record the commit.
echo "== Build"
(cd FE && VITE_TENANT_ID="$TENANT_ID" VITE_SPA_CLIENT_ID="$SPA_CLIENT_ID" VITE_API_SCOPE="$API_SCOPE" \
  VITE_API_BASE_URL="https://$API_HOST" VITE_BUILD_SHA="$SHA" npm run build --silent) || fail "Build failed."
printf '{"commit":"%s"}\n' "$SHA" > FE/dist/version.json
echo

# 4. Deploy with the SWA CLI. The token goes in its environment only.
echo "== Deploy"
SWA_CLI_DEPLOYMENT_TOKEN=$(az staticwebapp secrets list -g "$RG" -n "$SWA_NAME" --query properties.apiKey -o tsv) \
  || fail "Couldn't fetch the Static Web App deployment token."
[[ -n "$SWA_CLI_DEPLOYMENT_TOKEN" ]] || fail "Empty Static Web App deployment token."
export SWA_CLI_DEPLOYMENT_TOKEN
npx --yes "$SWA_CLI" deploy FE/dist --env production --no-use-keychain >/dev/null \
  || { unset SWA_CLI_DEPLOYMENT_TOKEN; fail "SWA deploy failed."; }
unset SWA_CLI_DEPLOYMENT_TOKEN
echo "  uploaded"
echo

# 5. Smoke check: the site serves this build's version.json, and the app page loads.
echo "== Smoke check"
deadline=$((SECONDS + ${SMOKE_TIMEOUT_SECONDS:-120}))
body=""
while (( SECONDS < deadline )); do
  body=$(curl -s --max-time 15 "https://$SWA_HOST/version.json?nocache=$RANDOM" || true)
  [[ "$body" == *"\"commit\":\"$SHA\""* ]] && break
  sleep 5
done
[[ "$body" == *"\"commit\":\"$SHA\""* ]] || fail "version.json didn't report $SHA (last: ${body:-none})."
page=$(curl -s -o /dev/null -w '%{http_code}' "https://$SWA_HOST/")
[[ "$page" == 200 ]] || fail "The app page returned HTTP $page."
echo "  version.json: $body"
echo "  https://$SWA_HOST/ → HTTP 200"
echo
echo "== Summary"
echo "  commit: $SHA"
echo "  site:   https://$SWA_HOST/"
