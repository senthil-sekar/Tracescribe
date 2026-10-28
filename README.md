# Tracescribe

**The scribe that writes your traces for AI agents to read.**

[![NuGet](https://img.shields.io/nuget/vpre/Tracescribe?label=Tracescribe)](https://www.nuget.org/packages/Tracescribe)
[![NuGet](https://img.shields.io/nuget/vpre/Tracescribe.Mcp?label=Tracescribe.Mcp)](https://www.nuget.org/packages/Tracescribe.Mcp)
[![dotnet](https://github.com/senthil-sekar/Tracescribe/actions/workflows/dotnet.yml/badge.svg)](https://github.com/senthil-sekar/Tracescribe/actions/workflows/dotnet.yml)

Tracescribe adds triage-ready signals to OpenTelemetry (what the code was trying to do, what should have happened,
what kind of failure it was, and which decisions it made), and an MCP server that hands an AI agent a compact
digest of any trace. .NET first.

> **Status: 1.0.0 release candidate.** The API and the stable conventions are what 1.0 will ship
> ([versioning](docs/versioning.md)).

**Docs:** [Getting started](docs/getting-started.md) · [Backends](docs/backends.md) ·
[Conventions](spec/SPEC.md) · [Architecture](docs/architecture.md) · [Performance](docs/performance.md) ·
[Security](SECURITY.md) · [Changelog](CHANGELOG.md) · [Contributing](CONTRIBUTING.md)

## Why

OpenTelemetry tells you *what happened*. An agent triaging an incident also needs to know:

| Question | Tracescribe signal |
|---|---|
| What was the code trying to do? | `tracescribe.intent` (on spans and logs) |
| What should have happened, and did it? | `tracescribe.outcome.expected` / `.actual` / `.matched` |
| What kind of failure is this? | `tracescribe.failure.class` (set, or inferred from exceptions and status codes) |
| What did the code decide along the way? | `tracescribe.decision` events |
| Which claim, policy or customer is involved? | `tracescribe.context.*` (redacted) |

It also catches **semantic failures**: work that finished without an error but produced the wrong result, which
error-rate alerting never sees. Everything is plain OpenTelemetry data, so it works with any OTLP backend.

## Quick start

```bash
dotnet add package Tracescribe.OpenTelemetry --prerelease
```

Register it with OpenTelemetry. This alone infers failure classes on every span and redacts PII:

```csharp
builder.Services.AddOpenTelemetry()
    .WithTracing(t => t.AddAspNetCoreInstrumentation().AddTracescribe().AddOtlpExporter())
    .WithMetrics(m => m.AddTracescribe().AddOtlpExporter());
builder.Logging.AddOpenTelemetry(o => { o.AddTracescribe(); o.AddOtlpExporter(); });
```

Then name the business operations you'd want explained during an incident:

```csharp
using var op = Scribe.Begin("claim.fnol.submit", expected: "claim accepted with a claim number")
    .WithContext("claim_id", claimId);
try
{
    var number = await claims.SubmitAsync(request);
    op.Succeed($"claim number {number} issued");
}
catch (Exception ex)
{
    op.Fail(ex);   // failure class inferred: timeout → dependency.timeout, 429 → policy, ...
    throw;
}
```

Use `op.Mismatch(actual, reason)` when the work finished but the result is wrong, `op.Decide(...)` to record a
branch, and `op.Hint(...)` to leave a note for whoever triages later. There's also a
[source generator](docs/getting-started.md#let-the-generator-write-it) and
[chat-client middleware](docs/getting-started.md#chat-calls) for `Microsoft.Extensions.AI`.

The libraries run on .NET 8, .NET 10 and .NET Framework 4.6.2+.

## Triage with an agent

```bash
dotnet tool install -g Tracescribe.Mcp --prerelease
claude mcp add tracescribe -e TRACESCRIBE_BACKEND=tempo -e TRACESCRIBE_URL=http://localhost:3200 -- tracescribe-mcp
```

Then ask your agent: *"What failed in claims-api in the last hour, and why?"*

| Tool | What the agent gets |
|---|---|
| `find_failures` | Recent failing traces (errors, semantic failures or both), each with its likely root cause |
| `get_trace_digest` | The span tree with intent, outcomes, failure classes and decisions, then ranked root causes |
| `find_logs` | Log lines for an intent (such as `claim.fnol.submit`), a trace or a service |
| `list_services` | Service names |

It reads **Jaeger, Grafana Tempo, Grafana Loki, Azure Monitor / Application Insights, or OTLP/JSON files**, and
works with Claude Code, Claude Desktop, Cursor and VS Code. Setup for each: [docs/backends.md](docs/backends.md).
The same binary is a CLI (`tracescribe-mcp failures`, `digest`, `logs`, `services`). All tools are read-only.

### What the agent reads

From the [ClaimIntake sample](dotnet/samples/ClaimIntake/Program.cs): an AI agent's VIN-decoding tool times
out, the code falls back to the wrong vehicle, and the coverage check silently matches it (abridged):

```
TRACE a619766eb96e96ae355d257cbf8fe474 | 10 spans | 2 failed, 1 semantic | 148ms | root: claim.fnol.submit
[FAIL] claim.fnol.submit 148ms svc=claims-api class=validation
│    expected: claim accepted with a claim number
│    actual: claim held, no claim number issued
│    decision: vin.fallback chose=first_vehicle_on_policy alts=[retry_decoder, ask_claimant] why: decoder timed out
│    context: claim_id=CLM-2026-004211, policy_id=PA-7781203, reporter_email=[REDACTED:email], state=CA
├─ [OK] policy.lookup 44ms (+5 healthy spans)
├─ [OK] invoke_agent claims-assistant 67ms gen_ai=invoke_agent
│  └─ [FAIL] execute_tool decode_vin 52ms gen_ai=execute_tool:decode_vin class=dependency.timeout(inferred)
│          exception: System.TimeoutException: VIN decoder did not respond within 30ms
└─ [WRONG] coverage.check 7ms class=semantic
        expected: collision coverage active on the reported vehicle
        actual: collision coverage active on 1HGCM82633A004352; reported vehicle was the second car

ROOT CAUSE CANDIDATES (most likely first):
1. execute_tool decode_vin [FAIL] class=dependency.timeout(inferred) retryable=true
2. coverage.check [WRONG] class=semantic
```

Try it without a backend:

```bash
cd dotnet
dotnet run --project samples/ClaimIntake -- --export /tmp/claim.json
tracescribe-mcp failures --path /tmp/claim.json
```

## Packages

| Package | Use it for |
|---|---|
| `Tracescribe.OpenTelemetry` | Most services: the `Scribe` API plus span, log and metric registration |
| `Tracescribe` | The `Scribe` API without the OpenTelemetry SDK |
| `Tracescribe.Generators` | `[Scribe]` attributes instead of hand-written operations |
| `Tracescribe.Extensions.AI` | Intent, outcome and failure tracking for `IChatClient` calls |
| `Tracescribe.SemanticConventions` | Attribute name constants only |
| `Tracescribe.Query` | Reading traces and logs from backends in your own tools |
| `Tracescribe.Mcp` | The MCP server and CLI (`dotnet tool install`) |

## Roadmap

- [ ] 1.0.0, after the release candidate runs in a real service
- [ ] Azure Monitor checked against a live workspace
- [ ] Java SDK sharing [`spec/`](spec/SPEC.md)

Past releases: [CHANGELOG.md](CHANGELOG.md).

## Prior art

Tracescribe builds on the [OpenTelemetry GenAI semantic conventions](https://opentelemetry.io/docs/specs/semconv/gen-ai/).
Related work: *AgentTrace* (AlSayyad, Huang, Pal; arXiv:2602.10133) on structured agent logs; OpenInference,
OpenLLMetry and OpenLIT for LLM tracing; the Grafana, Honeycomb, Datadog and Sentry MCP servers, which give agents
raw telemetry. Tracescribe adds the semantics and a digest built for triage.

## License

[MIT](LICENSE)
