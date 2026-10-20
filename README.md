# Tracescribe

**OpenTelemetry, written for agents to read.**

Tracescribe extends the OpenTelemetry GenAI semantic conventions with triage-ready signals
(intent, expected vs. actual outcome, failure class, decisions and business context) so AI agents
can debug traces, logs and metrics faster. .NET first.

> Status: **v0.1 alpha, Development.** Attribute names may change before 1.0.

## Why

OTel tells you *what happened*. An agent triaging an incident also needs to know:

| Question | Tracescribe signal |
|---|---|
| What was the code trying to do? | `tracescribe.intent` |
| What should have happened, and did it? | `tracescribe.outcome.expected` / `.actual` / `.matched` |
| What kind of failure is this? | `tracescribe.failure.class` (set or inferred) |
| What did the code decide along the way? | `tracescribe.decision` events |
| Which claim, policy or customer is involved? | `tracescribe.context.*` (redacted) |

It also catches **semantic failures**: work that finished without an exception but produced the
wrong result. Normal error alerting never sees those.

Everything is plain OTel attributes, events and links, so it works with any OTLP backend
(Jaeger, Tempo, Application Insights, Honeycomb, Datadog). See [`spec/SPEC.md`](spec/SPEC.md).

## Quick look

```csharp
using var op = Scribe.Begin("claim.fnol.submit", expected: "claim accepted with a claim number")
    .WithContext("claim_id", claimId)
    .WithContext("state", "CA");

try
{
    var number = await claims.SubmitAsync(request);
    op.Succeed($"claim number {number} issued");
}
catch (TimeoutException ex)
{
    op.Fail(FailureClassValues.DependencyTimeout, "rating service timed out", retryable: true, ex);
    throw;
}
```

Spans from other instrumentation (ASP.NET Core, HttpClient, Microsoft Agent Framework `execute_tool`
spans) get a failure class inferred from exception types, `error.type` and HTTP status codes.

### Register with OpenTelemetry

```csharp
builder.Services.AddOpenTelemetry().WithTracing(t => t
    .AddAspNetCoreInstrumentation()
    .AddHttpClientInstrumentation()
    .AddTracescribe()        // before exporters
    .AddOtlpExporter());
```

### The triage digest

`TriageDigest` turns a trace into compact text an agent can reason over. Healthy subtrees collapse,
and root-cause candidates are ranked: deepest failure first, then semantic failures. This is the
output of the [ClaimIntake sample](dotnet/samples/ClaimIntake/Program.cs):

```
TRACE 3a9ec5f22067a96ccbf957e467577945 | 10 spans | 2 failed, 1 semantic | 108ms | root: claim.fnol.submit
[FAIL] claim.fnol.submit 108ms class=validation
│    expected: claim accepted with a claim number
│    actual: claim held, no claim number issued
│    reason: claim held for manual review: vehicle could not be verified
│    decision: vin.fallback chose=first_vehicle_on_policy alts=[retry_decoder, ask_claimant] why: decoder timed out; avoid blocking claimant
│    context: claim_id=CLM-2026-004211, policy_id=PA-7781203, reporter_email=[REDACTED:email], state=CA
├─ [OK] policy.lookup 33ms (+5 healthy spans)
├─ [OK] invoke_agent claims-assistant 47ms gen_ai=invoke_agent
│  └─ [FAIL] execute_tool decode_vin 41ms gen_ai=execute_tool:decode_vin class=dependency.timeout(inferred)
│          reason: VIN decoder did not respond within 30ms
│          exception: System.TimeoutException: VIN decoder did not respond within 30ms
└─ [WRONG] coverage.check 8ms class=semantic
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

## Packages

| Package | Depends on | Purpose |
|---|---|---|
| `Tracescribe.SemanticConventions` | nothing | Attribute and enum constants mirroring [`spec/attributes.yaml`](spec/attributes.yaml) |
| `Tracescribe` | BCL only (`System.Diagnostics`) | `Scribe` API, `Activity` extensions, failure inference, redaction, `TriageDigest` |
| `Tracescribe.OpenTelemetry` | `OpenTelemetry` | `TracescribeProcessor` and `AddTracescribe()` |

## Repo layout

```
spec/        language-neutral conventions (SPEC.md, attributes.yaml)
dotnet/      Tracescribe.sln: src/, tests/, samples/
```

Other languages will live beside `dotnet/` and share `spec/`.

## Build

```bash
cd dotnet
dotnet test
dotnet run --project samples/ClaimIntake
```

Requires the .NET 8 SDK.

## Roadmap

- [x] v0.1 conventions: intent, outcome, failure class, decisions, hints, context, links
- [x] .NET core SDK, failure inference, redaction, triage digest
- [x] OpenTelemetry processor
- [ ] `Tracescribe.Mcp`: MCP server exposing `get_trace_digest` / `find_failures` over OTLP backends
- [ ] OTLP/JSON reader so digests work on exported traces, not only in-process spans
- [ ] `IChatClient` middleware (Microsoft.Extensions.AI) for intent and outcome on agent calls
- [ ] Redaction for exception messages and log bodies
- [ ] Logs and metrics conventions (`tracescribe.intent` on log records, outcome counters)
- [ ] Source generator for `[Scribe("claim.fnol.submit")]` method attributes
- [ ] Constants generated from `spec/attributes.yaml`

## Prior art

- [OpenTelemetry GenAI semantic conventions](https://opentelemetry.io/docs/specs/semconv/gen-ai/): Tracescribe builds on these.
- *AgentTrace: A Structured Logging Framework for Agent System Observability* (AlSayyad, Huang, Pal; arXiv:2602.10133): operational, cognitive and contextual surfaces for agent logs.
- OpenInference, OpenLLMetry and OpenLIT: LLM tracing instrumentation, mostly Python and TypeScript.

## License

[MIT](LICENSE)
