# Tracescribe

**The scribe that writes your traces for AI agents to read.**

Tracescribe extends the OpenTelemetry GenAI semantic conventions with triage-ready signals (intent, expected vs.
actual outcome, failure class, decisions and business context) so AI agents can debug traces, logs and metrics
faster. It also catches *semantic failures*: work that finished without an exception but produced the wrong result.

| Package | What it does |
|---|---|
| `Tracescribe.SemanticConventions` | Attribute, event and metric name constants |
| `Tracescribe` | `Scribe` API, failure inference, redaction, metrics, triage digests, OTLP/JSON and Jaeger readers and writers |
| `Tracescribe.Generators` | Source generator for `[Scribe]` partial methods |
| `Tracescribe.OpenTelemetry` | Span processor, log processor (intent + redaction), meter registration |
| `Tracescribe.Extensions.AI` | `IChatClient` middleware for Microsoft.Extensions.AI |
| `Tracescribe.Query` | Jaeger, Tempo, Loki, Azure Monitor and file backends, `TriageService` |
| `Tracescribe.Mcp` | MCP server and CLI: `dotnet tool install -g Tracescribe.Mcp --prerelease` |

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
    op.Fail(ex);   // failure class and retryability inferred
    throw;
}
```

Docs, the MCP setup for Claude Code, Claude Desktop, Cursor and VS Code, and the convention spec:
https://github.com/senthil-sekar/tracescribe

Status: alpha. Attribute names may change before 1.0.
