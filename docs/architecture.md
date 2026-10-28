# Architecture

How Tracescribe is put together. For using it, start with [getting-started.md](getting-started.md); for the
attribute names themselves, see [spec/SPEC.md](../spec/SPEC.md).

## Overview

```
 your service                                     telemetry backend              agent
┌──────────────────────────────────────┐        ┌────────────────────┐        ┌──────────────────┐
│ Scribe.Begin / [Scribe] / IChatClient│        │ Jaeger · Tempo     │        │ Claude, Cursor,  │
│        │ intent, outcome, decisions  │  OTLP  │ Loki · App Insights│ query  │ VS Code, ...     │
│        ▼                             │ ─────► │ OTLP/JSON files    │ ◄───── │   ▲              │
│ TracescribeProcessor (spans)         │        └────────────────────┘        │   │ MCP (stdio)   │
│ TracescribeLogProcessor (logs)       │                                      │ tracescribe-mcp  │
│ Tracescribe meter (metrics)          │                                      │ → TriageService  │
└──────────────────────────────────────┘                                      │ → TriageDigest   │
                                                                              └──────────────────┘
```

Two halves share one convention set:

1. **Emitting**, inside your services: the `Scribe` API, the source generator and the chat middleware put
   `tracescribe.*` attributes and events on ordinary OpenTelemetry spans, and processors enrich, redact and count
   them before export. Nothing is sent anywhere OpenTelemetry wouldn't already send it.
2. **Reading**, outside your services: `Tracescribe.Query` loads traces and logs back from a backend and
   `TriageDigest` turns them into compact text. `tracescribe-mcp` exposes that to agents over MCP.

## Packages and layering

```
Tracescribe.SemanticConventions   constants generated from spec/attributes.yaml (no dependencies)
        ▲
Tracescribe                        Scribe API, enricher, failure inference, redaction, metrics,
        ▲                          SpanRecord/LogEntry, OTLP/JSON + Jaeger readers, OTLP/JSON writer, TriageDigest
        ├── Tracescribe.OpenTelemetry   span + log processors, meter registration   (→ OpenTelemetry)
        ├── Tracescribe.Extensions.AI   IChatClient middleware                      (→ Microsoft.Extensions.AI)
        └── Tracescribe.Query           backends, TriageService                     (BCL only)
                    ▲
            Tracescribe.Mcp             MCP server + CLI, a .NET tool               (→ ModelContextProtocol, Azure.Identity)

Tracescribe.Generators             Roslyn source generator (analyzer package, no runtime dependency)
```

| Package | Targets |
|---|---|
| SemanticConventions, Tracescribe, OpenTelemetry, Extensions.AI | netstandard2.0, net8.0, net10.0 |
| Query, Mcp | net8.0, net10.0 |
| Generators | netstandard2.0 (runs inside the compiler) |

The netstandard2.0 builds compile `dotnet/build/Polyfills.cs`, which supplies the few .NET 6+ APIs the code uses as
C# 14 extension members, so the shared source has no `#if` blocks. Redaction uses `[GeneratedRegex]` on .NET 7+
and compiled `Regex` otherwise. `Tracescribe.NetStandard.Tests` runs the core suite against the netstandard2.0
assembly.

## Emitting

### `Scribe` and `ScribeOperation`

`Scribe.Begin(intent, expected, ...)` starts an `Activity` on the `Tracescribe` source, sets
`tracescribe.intent` and the schema version, and returns a `ScribeOperation`. Its methods set attributes and
events on that activity: `WithContext` (`tracescribe.context.*`), `Decide` (`tracescribe.decision` event),
`Hint`, and the outcome methods `Succeed`, `Partial`, `Fail` and `Mismatch` (`outcome.matched = false`, the
semantic failure). `Fail(exception)` classifies the exception and records it as an `exception` event with a
redacted message.

### Source generator

`Tracescribe.Generators` implements `[Scribe]` partial methods: it opens the operation, records
`[ScribeContext]` parameters, calls the `<Name>Core` method, and records success, an inferred failure, or an
unknown outcome on caller cancellation. It handles `void`, values, `Task`, `Task<T>`, `ValueTask` and
`ValueTask<T>`, and reports mistakes as compile errors `TSCR001`–`TSCR007`. It targets Roslyn 4.8 (the .NET 8
SDK) as its minimum.

### Chat middleware

`TracescribeChatClient` wraps an `IChatClient` call in an intent span and maps the outcome: `content_filter` →
`failure` / `policy`, `length` → `partial`, provider exceptions → classified failures, and any other finish
reason → the configured `ChatOutcome` evaluator (`NonEmpty`, `RequireJson`, `RequireToolCall`, `All`). Put
`UseOpenTelemetry()` after it so the GenAI chat span nests under the intent span.

### Enrichment (`TracescribeEnricher`)

`TracescribeProcessor` runs the enricher on every finished span from any instrumentation, not only
Tracescribe's own. It never throws. In order, it:

1. **Infers `failure.class`** on error spans that have none. First match wins: an `exception` event's type,
   `error.type`, an `exception.type` attribute, the HTTP status code, a failing `execute_tool` span
   (→ `dependency`), otherwise `unknown`. It also sets `failure.retryable` when the source implies it, and
   `failure.reason` from the status description. Inferred values set `tracescribe.failure.inferred = true`. Classification walks exception base types and inner exceptions, and reads
   `StatusCode`/`Status` properties such as those on Azure and OpenAI SDK exceptions. The mapping is in
   `FailureClassifier`.
2. **Defaults the outcome**: an intent span that ends without one gets `outcome.status = failure` if it errored,
   otherwise `unknown`.
3. **Redacts** `tracescribe.context.*` values and sets `tracescribe.redacted` / `.redacted.keys`.
4. **Records metrics** for intent spans: `tracescribe.operation.count` and `.duration`.

`TraceCollector` runs the same enricher in-process without the OpenTelemetry SDK, which is what the sample and
tests use.

### Logs

`TracescribeLogProcessor` tags each log record with the `tracescribe.intent` of the nearest enclosing intent
span and redacts the message, body and attribute values. That's what makes `find_logs` by intent possible
without knowing a trace id.

### Redaction

`DefaultRedactor` masks whole values under deny-listed keys (`ssn`, `email`, `date_of_birth`, `card_number`, …)
and masks PII-shaped substrings elsewhere: email addresses, phone numbers, Luhn-valid card numbers and US SSNs.
`TracescribeRedaction.Default` is used for exception messages and by the digest; every component also accepts its
own `IRedactor`. Limits are in [SECURITY.md](../SECURITY.md).

## Reading

### Backends

Each backend implements `ITraceBackend` (`GetTraceAsync`, `SearchAsync`, `ListServicesAsync`) and, if it stores
logs, `ILogBackend` (`SearchLogsAsync`). They convert whatever the backend returns into `SpanRecord` and
`LogEntry`, so everything above them is backend-neutral. HTTP backends share an internal `HttpQueryClient` for
URL handling, error mapping and per-request auth.

| Backend | Search | Notes |
|---|---|---|
| `JaegerBackend` | `/api/traces` with tag filters, per service (first 25 when none is given) | Jaeger stores arrays as JSON strings; the conventions' arrays are restored |
| `TempoBackend` | One TraceQL query, then `/api/traces/{id}` per hit | |
| `LokiLogBackend` | LogQL on OTLP structured metadata (`tracescribe_intent`, `trace_id`, `severity_number`) | Logs only; pairs with Jaeger or Tempo |
| `AzureMonitorBackend` | KQL over `AppRequests`, `AppDependencies`, `AppExceptions`, `AppTraces` | At most two queries per search (search, then one batch load); Entra ID tokens via a callback |
| `FileTraceBackend` | Reads OTLP/JSON (one document or JSON lines) and Jaeger JSON | Also serves OTLP/JSON logs |

Table and column mappings for each backend are in [backends.md](backends.md).

### `TriageService` and `TriageDigest`

`TriageService` is the agent-facing layer: `FindFailuresTextAsync`, `GetDigestAsync`, `FindLogsTextAsync` and
`ListServicesTextAsync`. Each returns compact text and turns backend errors into a readable line instead of
throwing.

`TriageDigest` builds a report per trace:

- **Health per span:** `FAIL` (error status or failure outcome), `WRONG` (semantic: `outcome.matched = false`
  or a `semantic` failure class), `PARTIAL`, `??` (intent span with no outcome), `OK`.
- **Tree:** issues and their ancestors are rendered with intent, outcome, reason, exception, decisions, hints and
  context; healthy subtrees collapse to one line (`+5 healthy spans`), and repeated healthy leaves merge.
- **Root-cause candidates:** the deepest failing spans first, then semantic failures, each with its path and
  the business context inherited from its ancestors.

The digest text is for language models and isn't covered by the stability guarantees; the data types
(`TriageReport`, `RootCauseCandidate`) are.

### MCP server

`tracescribe-mcp` (`Tracescribe.Mcp`) hosts `TriageService` over stdio with the official MCP SDK, exposing
`find_failures`, `get_trace_digest`, `find_logs` and `list_services`, all read-only. Without a command it serves
MCP; with one (`failures`, `digest`, `logs`, `services`) it prints the same text as a CLI. For Azure Monitor it
gets tokens from `DefaultAzureCredential` and caches them until shortly before expiry.

**Untrusted content.** Telemetry can contain text from end users or attackers, and tool results go straight into
a model's context. Two defences:

1. `TextSafety.OneLine` turns line breaks, Unicode line separators and control characters into spaces in every
   rendered value (span and service names, attributes, events, log lines), so content can't forge digest lines.
2. Every tool result is wrapped in `<telemetry>` … `</telemetry>` with a reminder to treat it as data; copies of
   the markers inside the content are neutralised.

## Conventions pipeline

`spec/attributes.yaml` is the source of truth. `spec/tools/generate_dotnet.py` generates
`TracescribeConventions.g.cs` from it and refuses entries without a `stability`. CI fails if the generated file
drifts from the YAML.

## Compatibility checks

Every `dotnet pack` runs package validation: the target frameworks inside each package must expose the same API,
and the API must be compatible with the last release on nuget.org (`PackageValidationBaselineVersion`). See
[versioning.md](versioning.md) and [CONTRIBUTING.md](../CONTRIBUTING.md).

## Repository layout

```
spec/                 conventions: SPEC.md, attributes.yaml, tools/generate_dotnet.py
collector/            OpenTelemetry Collector config: pipeline redaction, OTLP/JSON file export
docs/                 user documentation
dotnet/
  src/                the seven packages
  tests/              unit, netstandard2.0, integration, generator and real-backend tests, MCP smoke test
  samples/ClaimIntake the scenario used throughout the docs
  benchmarks/         BenchmarkDotNet overhead benchmarks
  build/Polyfills.cs  netstandard2.0 compatibility
.github/workflows/    dotnet.yml (CI), release.yml (publish to nuget.org on a v* tag)
```
