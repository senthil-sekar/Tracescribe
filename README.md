# Tracescribe

**The scribe that writes your traces for AI agents to read.**

Tracescribe extends the OpenTelemetry GenAI semantic conventions with triage-ready signals (intent,
expected vs. actual outcome, failure class, decisions and business context) so AI agents can debug
traces, logs and metrics faster. It ships an MCP server that hands agents a compact digest of any trace,
and the logs written inside any business operation, from Jaeger, Grafana Tempo and Loki, Azure Monitor /
Application Insights, or OTLP/JSON files. .NET first.

> Status: **v0.3 alpha, Development.** Attribute names may change before 1.0. NuGet publishing is wired up
> (see [Releasing](#releasing)); until the first release, build from source.

## Why

OTel tells you *what happened*. An agent triaging an incident also needs to know:

| Question | Tracescribe signal |
|---|---|
| What was the code trying to do? | `tracescribe.intent` (on spans and logs) |
| What should have happened, and did it? | `tracescribe.outcome.expected` / `.actual` / `.matched` |
| What kind of failure is this? | `tracescribe.failure.class` (set or inferred) |
| What did the code decide along the way? | `tracescribe.decision` events |
| Which claim, policy or customer is involved? | `tracescribe.context.*` (redacted) |

It also catches **semantic failures**: work that finished without an exception but produced the wrong
result. Error-rate alerting never sees those.

Everything is plain OTel attributes, events, links, log attributes and metrics, so it works with any
OTLP backend. See [`spec/SPEC.md`](spec/SPEC.md).

## Packages

| Package | Depends on | What it does |
|---|---|---|
| `Tracescribe.SemanticConventions` | nothing | Constants generated from [`spec/attributes.yaml`](spec/attributes.yaml) |
| `Tracescribe` | BCL only | `Scribe` API, failure inference, redaction, metrics, `TriageDigest`, OTLP/JSON and Jaeger readers, OTLP/JSON writer |
| `Tracescribe.Generators` | analyzer | Source generator for `[Scribe]` partial methods |
| `Tracescribe.OpenTelemetry` | `OpenTelemetry` | Span processor, log processor (intent + redaction), meter registration, `LogRecord.ToLogEntry()` |
| `Tracescribe.Extensions.AI` | `Microsoft.Extensions.AI` | `IChatClient` middleware: intent spans, finish-reason handling, outcome checks |
| `Tracescribe.Query` | BCL only | Jaeger, Tempo, Loki, Azure Monitor and file backends, `TriageService` |
| `Tracescribe.Mcp` | `ModelContextProtocol`, `Azure.Identity` | MCP server and CLI (`tracescribe-mcp`), a .NET tool |

## Instrument your code

### Wrap an operation

```csharp
using var op = Scribe.Begin("claim.fnol.submit", expected: "claim accepted with a claim number")
    .WithContext("claim_id", claimId)
    .WithContext("state", "CA");
try
{
    var number = await claims.SubmitAsync(request);
    op.Succeed($"claim number {number} issued");
}
catch (Exception ex)
{
    op.Fail(ex);   // failure class and retryability inferred: 429 → policy, timeout → dependency.timeout, ...
    throw;
}
```

Use `op.Mismatch(actual, reason)` when the work completed but the result is wrong, `op.Decide(...)` to
record a branch, and `op.Hint(...)` to leave a note for whoever triages this later.

### Or let the generator write it

```csharp
public partial class PolicyService
{
    [Scribe("policy.lookup", Expected = "active policy found")]
    public partial Task<Policy> LookupAsync([ScribeContext("policy_id")] string policyId, CancellationToken ct);

    private async Task<Policy> LookupCoreAsync(string policyId, CancellationToken ct) { /* your code */ }
}
```

`Tracescribe.Generators` writes `LookupAsync`: it opens the operation, records `policy_id`, calls
`LookupCoreAsync`, and records success, an inferred failure, or (on caller cancellation) an unknown
outcome. Supports `void`, values, `Task`, `Task<T>`, `ValueTask` and `ValueTask<T>`. Mistakes are
compile errors (`TSCR001`–`TSCR007`).

### Chat calls (Microsoft.Extensions.AI)

```csharp
IChatClient client = new ChatClientBuilder(inner)
    .UseTracescribe("claim.summary.generate", o => o.Evaluator = ChatOutcomeEvaluators.RequireJson())
    .UseOpenTelemetry()     // GenAI chat span nests under the intent span
    .Build();
```

`content_filter` stops become `policy` failures, `length` stops become `partial`, provider exceptions are
classified, and the evaluator turns well-formed-but-wrong responses into semantic failures. Built-in
evaluators: `NonEmpty`, `RequireJson`, `RequireToolCall(name)`, and `All(...)` to combine them.

### Register with OpenTelemetry

```csharp
builder.Services.AddOpenTelemetry()
    .WithTracing(t => t
        .AddAspNetCoreInstrumentation()
        .AddHttpClientInstrumentation()
        .AddTracescribe()                 // before exporters
        .AddOtlpExporter())
    .WithMetrics(m => m.AddTracescribe().AddOtlpExporter());

builder.Logging.AddOpenTelemetry(o =>
{
    o.AddTracescribe();                   // adds tracescribe.intent to logs, redacts PII
    o.AddOtlpExporter();
});
```

- **Spans** from any instrumentation get a failure class inferred from exception types, `error.type`
  and HTTP status codes.
- **Logs** written inside an operation carry its `tracescribe.intent`, and PII-shaped values are masked.
- **Metrics** `tracescribe.operation.count` and `tracescribe.operation.duration` are broken down by
  intent, outcome and failure class, which gives you an alert on semantic failures.

For PII in data Tracescribe doesn't record itself (third-party exception messages, log bodies), use
[`collector/tracescribe-collector.yaml`](collector/tracescribe-collector.yaml). It is an OTel Collector
config that redacts in the pipeline and also writes OTLP/JSON trace and log files the MCP server can read.

## Triage with an agent (MCP)

`tracescribe-mcp` is an MCP server with four tools:

| Tool | What the agent gets |
|---|---|
| `find_failures` | Recent failing traces (`error`, `semantic` or `any`), newest first, each with its likely root cause |
| `get_trace_digest` | The span tree with intent, outcomes, failure classes and decisions, healthy subtrees collapsed, then ranked root-cause candidates |
| `find_logs` | Log records for a `tracescribe.intent` (exact, or a prefix like `claim.*`), a trace or a service, oldest first and redacted |
| `list_services` | Service names, for filtering |

| Backend | Traces | Logs (`find_logs`) |
|---|---|---|
| `jaeger` | Jaeger query API | Loki, if `TRACESCRIBE_LOGS_URL` is set |
| `tempo` | Grafana Tempo (TraceQL search) | Loki, if `TRACESCRIBE_LOGS_URL` is set |
| `azuremonitor` | Application Insights tables in a Log Analytics workspace (KQL) | `AppTraces` |
| `file` | OTLP/JSON or Jaeger JSON files (Collector file exporter, Tempo exports, `OtlpJsonWriter`) | OTLP/JSON log files in the same place |

```bash
cd dotnet
dotnet build src/Tracescribe.Mcp -c Release
# or, once published: dotnet tool install -g Tracescribe.Mcp --prerelease
```

**Claude Code**

```bash
claude mcp add tracescribe -e TRACESCRIBE_BACKEND=jaeger -e TRACESCRIBE_URL=http://localhost:16686 \
  -- dotnet /path/to/tracescribe/dotnet/src/Tracescribe.Mcp/bin/Release/net8.0/Tracescribe.Mcp.dll
```

**Claude Desktop, Cursor, VS Code** (`mcpServers` config)

```json
{
  "mcpServers": {
    "tracescribe": {
      "command": "dotnet",
      "args": ["/path/to/tracescribe/dotnet/src/Tracescribe.Mcp/bin/Release/net8.0/Tracescribe.Mcp.dll"],
      "env": { "TRACESCRIBE_BACKEND": "tempo", "TRACESCRIBE_URL": "http://localhost:3200" }
    }
  }
}
```

| Setting | Env var | Flag |
|---|---|---|
| Backend: `jaeger` (default), `tempo`, `azuremonitor`, `file` | `TRACESCRIBE_BACKEND` | `--backend` |
| Base URL (defaults: `:16686` Jaeger, `:3200` Tempo, `https://api.loganalytics.azure.com`) | `TRACESCRIBE_URL` | `--url` |
| File or directory for `file` | `TRACESCRIBE_PATH` | `--path` |
| Log Analytics workspace id for `azuremonitor` | `TRACESCRIBE_WORKSPACE` | `--workspace` |
| Extra header, e.g. `Authorization: Basic …` | `TRACESCRIBE_HEADER` | `--header` |
| Loki base URL, for logs with `jaeger` or `tempo` | `TRACESCRIBE_LOGS_URL` | `--logs-url` |
| Header for Loki, e.g. `X-Scope-OrgID: tenant1` (defaults to the header above) | `TRACESCRIBE_LOGS_HEADER` | `--logs-header` |

The same binary is a CLI:

```bash
tracescribe-mcp failures --service claims-api --minutes 30 --kind semantic
tracescribe-mcp digest 3a9ec5f22067a96ccbf957e467577945
tracescribe-mcp logs --intent claim.fnol.submit --severity warn --minutes 120
tracescribe-mcp services --path ./traces
```

### Azure Monitor / Application Insights

For services exporting with the Azure Monitor OpenTelemetry distro or exporter. Tracescribe attributes land in
`customDimensions` (`Properties` in the workspace tables) and the backend reads them back with KQL:
`AppRequests` and `AppDependencies` become spans, `AppExceptions` become exception events, and `AppTraces` hold
both logs and span events such as `tracescribe.decision`.

```bash
az login   # or a managed identity / workload identity / service principal in the environment
claude mcp add tracescribe -e TRACESCRIBE_BACKEND=azuremonitor -e TRACESCRIBE_WORKSPACE=<workspace-guid> \
  -- tracescribe-mcp
```

It signs in with `DefaultAzureCredential` and needs **Log Analytics Reader** on the workspace. Searches are KQL
over the query API (at most two queries per `find_failures` call, to stay inside its rate limits); traces are
looked up over the last 7 days. For a sovereign cloud, set `TRACESCRIBE_URL` (e.g. `https://api.loganalytics.us`)
and `TRACESCRIBE_AZURE_SCOPE`.

### Logs by intent

`TracescribeLogProcessor` stamps every log record written inside an operation with its `tracescribe.intent`, so an
agent can ask for "all warnings from `claim.fnol.submit` in the last two hours" without knowing a trace id:

```
2 log records for intent=claim.fnol.submit, severity>=WARN over the last 2h (loki, oldest first):
2026-10-22 09:14:03.212Z WARN claims-api trace=a619766eb96e96ae355d257cbf8fe474 span=1d2c3b4a5e6f7081  VIN decoder timed out, using first vehicle on policy | policy_id=PA-7781203
2026-10-22 09:41:57.008Z WARN claims-api trace=0c4e1f22b8d94a6b8f0b1a3c5d7e9f10 span=9a8b7c6d5e4f3021  VIN decoder timed out, using first vehicle on policy | policy_id=PA-1002934
Use get_trace_digest with a trace id to see the operation a log line was written in.
```

Loki stores OTLP attributes as structured metadata with dots turned into underscores (`tracescribe_intent`), which
is what the Loki backend queries. To save in-process logs for the file backend, convert them with
`LogRecord.ToLogEntry()` and write them with `OtlpJsonWriter.WriteLogs`, or use the Collector config below.

### What the agent reads

Output of the [ClaimIntake sample](dotnet/samples/ClaimIntake/Program.cs). An AI agent's VIN-decoding
tool times out, the code falls back to the wrong vehicle, and the coverage check silently matches it:

```
TRACE a619766eb96e96ae355d257cbf8fe474 | 10 spans | 2 failed, 1 semantic | 148ms | root: claim.fnol.submit
[FAIL] claim.fnol.submit 148ms svc=claims-api class=validation
│    expected: claim accepted with a claim number
│    actual: claim held, no claim number issued
│    reason: claim held for manual review: vehicle could not be verified
│    decision: vin.fallback chose=first_vehicle_on_policy alts=[retry_decoder, ask_claimant] why: decoder timed out; avoid blocking claimant
│    context: claim_id=CLM-2026-004211, policy_id=PA-7781203, reporter_email=[REDACTED:email], state=CA
├─ [OK] policy.lookup 44ms (+5 healthy spans)
├─ [OK] invoke_agent claims-assistant 67ms gen_ai=invoke_agent
│  └─ [FAIL] execute_tool decode_vin 52ms gen_ai=execute_tool:decode_vin class=dependency.timeout(inferred)
│          reason: VIN decoder did not respond within 30ms
│          exception: System.TimeoutException: VIN decoder did not respond within 30ms
└─ [WRONG] coverage.check 7ms class=semantic
        expected: collision coverage active on the reported vehicle
        actual: collision coverage active on 1HGCM82633A004352; reported vehicle was the second car
        reason: coverage evaluated against fallback VIN, not the damaged vehicle
        hint: Mismatch here usually means the VIN fallback picked the wrong vehicle on a multi-vehicle policy

ROOT CAUSE CANDIDATES (most likely first):
1. execute_tool decode_vin [FAIL] class=dependency.timeout(inferred) retryable=true
   reason: VIN decoder did not respond within 30ms
   exception: System.TimeoutException: VIN decoder did not respond within 30ms
   path: claim.fnol.submit > invoke_agent claims-assistant > execute_tool decode_vin
   context: claim_id=CLM-2026-004211, policy_id=PA-7781203, reporter_email=[REDACTED:email], state=CA
2. coverage.check [WRONG] class=semantic
   reason: coverage evaluated against fallback VIN, not the damaged vehicle
   path: claim.fnol.submit > coverage.check
   context: vin=1HGCM82633A004352, claim_id=CLM-2026-004211, policy_id=PA-7781203, reporter_email=[REDACTED:email], state=CA
```

Try it locally:

```bash
cd dotnet
dotnet run --project samples/ClaimIntake -- --export /tmp/claim.json
dotnet run --project src/Tracescribe.Mcp -- failures --path /tmp/claim.json
```

## Repo layout

```
spec/        conventions: SPEC.md, attributes.yaml, tools/generate_dotnet.py
collector/   OTel Collector config: pipeline redaction + OTLP/JSON file export
dotnet/      Tracescribe.sln: src/, tests/, samples/
```

## Build and test

```bash
cd dotnet
dotnet test
python3 ../spec/tools/generate_dotnet.py   # after editing spec/attributes.yaml
```

Requires the .NET 8 SDK. CI also runs the sample, the CLI and an MCP stdio smoke test, packs all
packages and installs the MCP tool from its package.

## Releasing

[`release.yml`](.github/workflows/release.yml) publishes every package (with symbols and SourceLink) to nuget.org
when a `v*` tag is pushed. It uses NuGet [trusted publishing](https://learn.microsoft.com/nuget/nuget-org/trusted-publishing),
so no API key is stored. One-time setup:

1. On nuget.org, **Trusted Publishing** → add a policy: owner `senthil-sekar`, repository `tracescribe`,
   workflow file `release.yml`, environment `nuget`.
2. Add a repository secret `NUGET_USER` with your nuget.org profile name.
3. Optionally, add required reviewers to the `nuget` environment.

Then `git tag v0.3.0-alpha && git push origin v0.3.0-alpha`. The tag sets the package version.

## Roadmap

Done in v0.3:

- [x] `find_logs` MCP tool and `logs` CLI command keyed by `tracescribe.intent`, over Loki, Azure Monitor and OTLP/JSON log files
- [x] Azure Monitor / Application Insights backend (KQL over a Log Analytics workspace, Entra ID sign-in)
- [x] NuGet packaging: package READMEs, symbols, SourceLink, tag-driven release with trusted publishing; `Tracescribe.Mcp` as a .NET tool

Done in v0.2:

- [x] `Tracescribe.Mcp`: MCP server and CLI over Jaeger, Tempo and OTLP/JSON files
- [x] OTLP/JSON and Jaeger JSON readers; OTLP/JSON writer
- [x] `IChatClient` middleware (Microsoft.Extensions.AI)
- [x] Redaction for exception messages, log bodies and digests; Collector config for pipeline redaction
- [x] Logs and metrics conventions
- [x] Source generator for `[Scribe]` methods
- [x] Constants generated from `spec/attributes.yaml`

Next:

- [ ] First nuget.org release (needs the trusted publishing policy above), then `dotnet tool install -g Tracescribe.Mcp`
- [ ] Java SDK sharing `spec/`

## Prior art

- [OpenTelemetry GenAI semantic conventions](https://opentelemetry.io/docs/specs/semconv/gen-ai/): Tracescribe builds on these.
- *AgentTrace: A Structured Logging Framework for Agent System Observability* (AlSayyad, Huang, Pal; arXiv:2602.10133): operational, cognitive and contextual surfaces for agent logs.
- OpenInference, OpenLLMetry and OpenLIT: LLM tracing instrumentation, mostly Python and TypeScript.
- Grafana, Honeycomb, Datadog and Sentry MCP servers: these let agents query raw telemetry. Tracescribe adds the semantics and a digest built for triage.

## License

[MIT](LICENSE)
