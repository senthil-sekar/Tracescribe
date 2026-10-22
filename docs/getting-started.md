# Getting started

This walks through instrumenting an ASP.NET Core service and pointing an AI agent at its traces. About 15 minutes.

## 1. Add the packages

```bash
dotnet add package Tracescribe.OpenTelemetry --prerelease
dotnet add package OpenTelemetry.Extensions.Hosting
dotnet add package OpenTelemetry.Exporter.OpenTelemetryProtocol
dotnet add package OpenTelemetry.Instrumentation.AspNetCore
dotnet add package OpenTelemetry.Instrumentation.Http
```

`Tracescribe.OpenTelemetry` brings in `Tracescribe` (the `Scribe` API) and `Tracescribe.SemanticConventions`.
It runs on .NET 8, .NET 10 and .NET Framework 4.6.2+ (through .NET Standard 2.0).

## 2. Register Tracescribe with OpenTelemetry

```csharp
builder.Services.AddOpenTelemetry()
    .ConfigureResource(r => r.AddService("claims-api"))
    .WithTracing(t => t
        .AddAspNetCoreInstrumentation()
        .AddHttpClientInstrumentation()
        .AddTracescribe()                 // before exporters
        .AddOtlpExporter())
    .WithMetrics(m => m.AddTracescribe().AddOtlpExporter());

builder.Logging.AddOpenTelemetry(o =>
{
    o.AddTracescribe();                   // tags logs with tracescribe.intent and redacts PII
    o.AddOtlpExporter();
});
```

With only this, every span gets a **failure class inferred** from exceptions and HTTP status codes, and context
values are redacted before export.

## 3. Name your business operations

Wrap the operations you'd want explained during an incident:

```csharp
using var op = Scribe.Begin("claim.fnol.submit", expected: "claim accepted with a claim number")
    .WithContext("claim_id", claimId)
    .WithContext("state", request.State);
try
{
    var number = await claims.SubmitAsync(request);
    op.Succeed($"claim number {number} issued");
}
catch (Exception ex)
{
    op.Fail(ex);
    throw;
}
```

- **Intent** (`claim.fnol.submit`): what kind of operation this is. Lower-case, dotted, no IDs
  ([naming rules](../spec/SPEC.md#31-intent)).
- **Expected outcome**: what success looks like, stated up front.
- **Context**: identifiers an agent can pivot on. Redacted if they look like PII.

When the work finishes but the result is wrong, say so. This is the failure error-rate alerting never sees:

```csharp
if (coverage.Vin != reported.Vin)
{
    op.Mismatch(actual: $"coverage checked on {coverage.Vin}", reason: "fallback VIN used, not the damaged vehicle");
}
```

Record the branches that change what happens next, and leave notes for whoever triages later:

```csharp
op.Decide("vin.fallback", chosen: "first_vehicle_on_policy", alternatives: ["retry_decoder", "ask_claimant"],
    rationale: "decoder timed out");
op.Hint("Mismatch here usually means the VIN fallback picked the wrong vehicle on a multi-vehicle policy");
```

Prefer attributes to boilerplate? Add `Tracescribe.Generators` and declare
`[Scribe("policy.lookup", Expected = "active policy found")]` on a `partial` method (see the
[README](../README.md#or-let-the-generator-write-it)).

## 4. Alert on semantic failures

`tracescribe.operation.count` with `tracescribe.outcome.status = failure` and `tracescribe.failure.class = semantic`
counts operations that finished without an error but did the wrong thing. Put an alert on its rate per intent.

## 5. Let an agent triage

```bash
dotnet tool install -g Tracescribe.Mcp --prerelease
claude mcp add tracescribe -e TRACESCRIBE_BACKEND=tempo -e TRACESCRIBE_URL=http://localhost:3200 -- tracescribe-mcp
```

Then ask the agent: *"What failed in claims-api in the last hour, and why?"* It calls `find_failures`,
`get_trace_digest` and `find_logs`. Other backends and clients: [backends.md](backends.md).

Try it without a backend first:

```bash
git clone https://github.com/senthil-sekar/Tracescribe && cd Tracescribe/dotnet
dotnet run --project samples/ClaimIntake -- --export /tmp/claim.json
tracescribe-mcp failures --path /tmp/claim.json
```
