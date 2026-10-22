# Backends

`tracescribe-mcp` (and `Tracescribe.Query`, which it's built on) reads traces and logs from one backend at a time.

| Backend | Traces | Logs (`find_logs`) | Tested against |
|---|---|---|---|
| `jaeger` | Jaeger query API (`/api/traces`) | Loki, if `TRACESCRIBE_LOGS_URL` is set | Jaeger 1.76 (CI, real container) |
| `tempo` | Grafana Tempo (`/api/traces`, TraceQL `/api/search`) | Loki, if `TRACESCRIBE_LOGS_URL` is set | Tempo 2.9 (CI, real container) |
| `azuremonitor` | Application Insights tables in a Log Analytics workspace (KQL) | `AppTraces` | Fake endpoint with real response shapes |
| `file` | OTLP/JSON or Jaeger JSON files | OTLP/JSON log files in the same place | CI |
| (Loki, logs only) | — | Loki 3 query API with OTLP ingestion | Loki 3.5 (CI, real container) |

## Settings

| Setting | Env var | Flag |
|---|---|---|
| Backend: `jaeger` (default), `tempo`, `azuremonitor`, `file` | `TRACESCRIBE_BACKEND` | `--backend` |
| Base URL (defaults: `http://localhost:16686`, `http://localhost:3200`, `https://api.loganalytics.azure.com`) | `TRACESCRIBE_URL` | `--url` |
| File or directory for `file` | `TRACESCRIBE_PATH` | `--path` |
| Log Analytics workspace id for `azuremonitor` | `TRACESCRIBE_WORKSPACE` | `--workspace` |
| Request header, e.g. `Authorization: Basic …` | `TRACESCRIBE_HEADER` | `--header` |
| Loki base URL, for logs with `jaeger` or `tempo` | `TRACESCRIBE_LOGS_URL` | `--logs-url` |
| Header for Loki, e.g. `X-Scope-OrgID: tenant1` (defaults to the header above) | `TRACESCRIBE_LOGS_HEADER` | `--logs-header` |
| Token scope for a sovereign Azure cloud | `TRACESCRIBE_AZURE_SCOPE` | |

## MCP clients

**Claude Code**

```bash
claude mcp add tracescribe -e TRACESCRIBE_BACKEND=jaeger -e TRACESCRIBE_URL=http://localhost:16686 -- tracescribe-mcp
```

**Claude Desktop, Cursor, VS Code** (`mcpServers`)

```json
{
  "mcpServers": {
    "tracescribe": {
      "command": "tracescribe-mcp",
      "env": { "TRACESCRIBE_BACKEND": "tempo", "TRACESCRIBE_URL": "http://localhost:3200",
               "TRACESCRIBE_LOGS_URL": "http://localhost:3100" }
    }
  }
}
```

## Jaeger

Search needs a service, so `find_failures` without one searches the first 25 services Jaeger lists. Semantic
failures are found with the tag filter `tracescribe.outcome.matched=false`; errors with `error=true`.
Jaeger has no array type and stores array attributes as JSON strings; Tracescribe turns the conventions' arrays
(`tracescribe.decision.alternatives`, `tracescribe.redacted.keys`) back into arrays.

## Grafana Tempo

`find_failures` runs one TraceQL search, e.g.
`{ resource.service.name = "claims-api" && (status = error || span.tracescribe.outcome.matched = false) }`,
then loads each trace. Recent traces are searchable as soon as the ingester has them.

## Grafana Loki

Send logs over OTLP (Loki 3+). Loki stores OTLP attributes as structured metadata with dots replaced by
underscores, so `find_logs` filters on `tracescribe_intent`, `trace_id` and `severity_number`:
`{service_name="claims-api"} | tracescribe_intent="claim.fnol.submit" | severity_number >= 13`.
Multi-tenant Loki needs `TRACESCRIBE_LOGS_HEADER="X-Scope-OrgID: <tenant>"`.

## Azure Monitor / Application Insights

For services exporting with the Azure Monitor OpenTelemetry distro or exporter, where Tracescribe attributes land
in `customDimensions` (`Properties` in the workspace tables).

| Table | Becomes |
|---|---|
| `AppRequests` | server spans |
| `AppDependencies` | client and internal spans (`DependencyType`) |
| `AppExceptions` | `exception` events on the span in `ParentId` |
| `AppTraces` | logs, and span events whose message starts with `tracescribe.` |

```bash
az login
tracescribe-mcp failures --workspace <workspace-guid> --minutes 60
```

- Signs in with `DefaultAzureCredential`: Azure CLI, managed identity, workload identity or an environment
  service principal. Grant **Log Analytics Reader** on the workspace.
- Or pass a token yourself: `TRACESCRIBE_HEADER="Authorization: Bearer <token>"`.
- `find_failures` uses at most two queries (search, then one batch load) to stay inside the query API's limits.
  Trace lookups search the last 7 days.
- Sovereign clouds: set `TRACESCRIBE_URL` (e.g. `https://api.loganalytics.us`) and `TRACESCRIBE_AZURE_SCOPE`.

## Files

Point `--path` at a file or a directory of `.json`/`.jsonl` files: OTLP/JSON (one request per line, as the
Collector file exporter writes) or Jaeger JSON. [`collector/tracescribe-collector.yaml`](../collector/tracescribe-collector.yaml)
writes both traces and logs this way, with pipeline redaction.

## Writing your own backend

Implement `ITraceBackend` (and `ILogBackend` if it stores logs) from `Tracescribe.Query`, convert results to
`SpanRecord` / `LogEntry`, and pass it to `TriageService`.
