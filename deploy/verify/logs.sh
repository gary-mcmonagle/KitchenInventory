#!/usr/bin/env bash
# Read-only: recent requests, traces and exceptions for one Function app, from Application Insights
# (queried in its Log Analytics workspace, which is where workspace-based App Insights stores data).
# Usage: deploy/verify/logs.sh <ingest|api> [minutes=30] [max rows=50]
source "$(dirname "$0")/_common.sh"

APP=${1:-}; MINUTES=${2:-30}; ROWS=${3:-50}
case "$APP" in
  ingest) ROLE=$(infra_output ingestFunctionAppName) ;;
  api) ROLE=$(infra_output apiFunctionAppName) ;;
  *) echo "Usage: $0 <ingest|api> [minutes] [max rows]" >&2; exit 2 ;;
esac
[[ "$MINUTES" =~ ^[0-9]+$ && "$ROWS" =~ ^[0-9]+$ ]] || fail "minutes and rows must be whole numbers."

RG=$(infra_output resourceGroupName)
WS=$(az monitor log-analytics workspace list -g "$RG" --query "[0].customerId" -o tsv)
[[ -n "$WS" ]] || fail "No Log Analytics workspace in $RG."

QUERY="union withsource=tbl AppRequests, AppTraces, AppExceptions
| where TimeGenerated > ago(${MINUTES}m) and AppRoleName == '${ROLE}'
| extend detail = case(tbl == 'AppRequests', strcat(Name, ' ', ResultCode, ' ', tostring(round(DurationMs)), 'ms'),
                       tbl == 'AppTraces', strcat(tostring(SeverityLevel), ' ', Message),
                       strcat(ExceptionType, ': ', OuterMessage))
| project TimeGenerated, type = replace_string(tbl, 'App', ''), OperationId, detail
| order by TimeGenerated desc
| take ${ROWS}"

echo "Logs for $ROLE, last ${MINUTES} min (newest first, max ${ROWS} rows; ingestion can lag 1-3 min):"
az monitor log-analytics query -w "$WS" --analytics-query "$QUERY" -o table 2> >(grep -v "Bicep" >&2) \
  | sed -E 's/(x-functions-key|code)=[^& ]+/\1=<redacted>/gI'
