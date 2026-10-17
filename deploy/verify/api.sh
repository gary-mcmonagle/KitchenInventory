#!/usr/bin/env bash
# Read-only: GET an API path as yourself (bearer token from your az login, passed to curl on stdin, never shown).
# Usage: deploy/verify/api.sh /api/me
source "$(dirname "$0")/_common.sh"
source "$(dirname "$0")/_token.sh"
PATH_ARG=${1:?Usage: $0 /api/<path>}
[[ "$PATH_ARG" == /api/* ]] || fail "Path must start with /api/"
HOST=$(infra_output apiFunctionAppHostname)
token=$(api_token) || exit 1
printf 'Authorization: Bearer %s\n' "$token" \
  | curl -s -w '\nHTTP %{http_code}\n' -H @- "https://$HOST$PATH_ARG"
unset token
