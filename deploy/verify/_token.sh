# Shared by token.sh and api.sh: an API access token from the signed-in az login (Gary's), via the Azure CLI's
# pre-authorisation on the API registration (#36). Source it; never print the token.
api_scope() {
  local tenant
  tenant=$(az account show --query tenantId -o tsv) || return 1
  echo "api://$tenant/cupboard-tracker-api/access_as_user"
}
api_token() {
  az account get-access-token --scope "$(api_scope)" --query accessToken -o tsv 2>/dev/null \
    || { echo "ERROR: couldn't get an API token. Are the registrations deployed (deploy/entra.sh)? Try: az login --scope \"$(api_scope)\"" >&2; return 1; }
}
