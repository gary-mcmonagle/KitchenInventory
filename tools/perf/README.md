# Measuring Cosmos (#115)

KQL for the Cosmos telemetry both Function apps send, for [Faster back end (#96)](../../docs/data-model.md#measuring-cosmos)
and the cost guard thresholds (#107). Read-only: run them against the Log Analytics workspace behind App Insights.

```bash
WS=$(az monitor log-analytics workspace show -g rg-cupboard-tracker -n log-cupboard-tracker --query customerId -o tsv)
# one block (they're separated by blank lines), e.g. the N+1 view:
az monitor log-analytics query -w "$WS" -o table \
  --analytics-query "$(awk -v n='// 6.' 'index($0,n)==1{p=1} p&&/^$/{exit} p' tools/perf/queries.kql)"
```

| # | View |
|---|---|
| 1 | RUs per day by container |
| 2, 3 | Top 20 operations by total RU, and by RU per call |
| 4 | Cross-partition operations (RU, pages and items per call) |
| 5 | p50 and p95 duration per endpoint, warm and cold |
| 6 | RUs and Cosmos calls per request, by endpoint (the N+1 view) |
| 7 | RUs per timer, queue and change-feed run |
| 8 | 429s |
| 9 | The latest index recommendations per operation |
| 10 | What the measuring adds to ingestion per day |

What's recorded (no document content, ids or query parameters):

- **Metrics** `Cosmos RU`, `Cosmos duration ms`, `Cosmos items`, `Cosmos pages`, pre-aggregated per minute by
  `operation`, `container`, `partition` (`single` / `cross`) and `status`. `ItemCount` is the number of calls.
- **Metrics** `Cosmos RU per invocation` and `Cosmos calls per invocation` by `function` and `trigger`.
- **A trace per invocation that touched Cosmos**: `Cosmos usage: <function> (<trigger>) made n Cosmos calls costing x RU`,
  with the request's operation id, so it joins to `AppRequests`.
- **Index metrics** (only while `COSMOS_INDEX_METRICS` is `true`): `Cosmos index metrics for <operation>: …`, at most
  once an hour per operation.
