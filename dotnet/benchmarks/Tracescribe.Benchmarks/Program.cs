// Measures what Tracescribe adds on top of plain OpenTelemetry tracing.
// Every benchmark runs with a TracerProvider that samples everything and exports nothing, so the numbers are the
// cost of creating and processing spans in-process, not of exporting them.
using System.Diagnostics;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Running;
using OpenTelemetry;
using OpenTelemetry.Trace;
using Tracescribe;
using Tracescribe.Digest;
using Tracescribe.OpenTelemetry;
using Tracescribe.SemanticConventions;

BenchmarkSwitcher.FromAssembly(typeof(SpanBenchmarks).Assembly).Run(args);

/// <summary>Cost of one operation: a plain span versus Tracescribe operations, all through the OpenTelemetry SDK.</summary>
[MemoryDiagnoser]
public class SpanBenchmarks
{
    private static readonly ActivitySource Plain = new("Tracescribe.Benchmarks.Plain");
    private static readonly TimeoutException Timeout = new("VIN decoder did not respond within 30ms");
    private TracerProvider? _provider;

    [GlobalSetup]
    public void Setup() => _provider = Sdk.CreateTracerProviderBuilder()
        .AddSource(Plain.Name)
        .AddTracescribe()
        .Build();

    [GlobalCleanup]
    public void Cleanup() => _provider?.Dispose();

    [Benchmark(Baseline = true, Description = "Plain OTel span + 1 tag")]
    public void PlainSpan()
    {
        using var activity = Plain.StartActivity("claim.fnol.submit");
        activity?.SetTag("claim_id", "CLM-1");
    }

    [Benchmark(Description = "Scribe: intent + expected + success")]
    public void ScribeSucceed()
    {
        using var op = Scribe.Begin("claim.fnol.submit", expected: "claim accepted with a claim number");
        op.Succeed();
    }

    [Benchmark(Description = "Scribe: + 3 context values, a decision, a mismatch")]
    public void ScribeRich()
    {
        using var op = Scribe.Begin("coverage.check", expected: "collision coverage on the reported vehicle")
            .WithContext("claim_id", "CLM-2026-004211")
            .WithContext("policy_id", "PA-7781203")
            .WithContext("state", "CA");
        op.Decide("vin.fallback", "first_vehicle_on_policy", ["retry_decoder", "ask_claimant"], "decoder timed out");
        op.Mismatch("coverage on the first vehicle", "fallback VIN used");
    }

    [Benchmark(Description = "Scribe: failure from exception (class inferred)")]
    public void ScribeFail()
    {
        using var op = Scribe.Begin("vin.decode");
        op.Fail(Timeout);
    }

    [Benchmark(Description = "Plain span + error status, enriched by TracescribeProcessor")]
    public void PlainSpanErrorEnriched()
    {
        using var activity = Plain.StartActivity("GET /decode");
        activity?.SetTag("http.response.status_code", 504);
        activity?.SetStatus(ActivityStatusCode.Error);
    }
}

/// <summary>Redaction of one value: the default policy on text with and without PII.</summary>
[MemoryDiagnoser]
public class RedactionBenchmarks
{
    private readonly DefaultRedactor _redactor = new();

    [Benchmark(Baseline = true, Description = "No PII (40 chars)")]
    public string Clean() => _redactor.Redact("note", "claim held for manual review in region 7", out _);

    [Benchmark(Description = "Email inside (60 chars)")]
    public string WithEmail() => _redactor.Redact("note", "could not reach adjuster jane.doe@example.com for policy PA-1", out _);

    [Benchmark(Description = "Deny-listed key")]
    public string DenyKey() => _redactor.Redact("ssn", "123-45-6789", out _);
}

/// <summary>Rendering a triage digest, which the MCP server does per request (not on the hot path of your service).</summary>
[MemoryDiagnoser]
public class DigestBenchmarks
{
    private IReadOnlyList<SpanRecord> _small = [];
    private IReadOnlyList<SpanRecord> _large = [];

    [GlobalSetup]
    public void Setup()
    {
        _small = Trace(10);
        _large = Trace(200);
    }

    [Benchmark(Baseline = true, Description = "10-span trace")]
    public string Small() => TriageDigest.Render(_small);

    [Benchmark(Description = "200-span trace")]
    public string Large() => TriageDigest.Render(_large);

    private static List<SpanRecord> Trace(int count)
    {
        const string traceId = "5b8efff798038103d269b633813fc60c";
        var start = DateTimeOffset.UtcNow;
        var spans = new List<SpanRecord>(count);
        for (var i = 0; i < count; i++)
        {
            var failed = i == count - 1;
            var attributes = new Dictionary<string, object?>
            {
                [TracescribeAttributes.Intent] = i == 0 ? "claim.fnol.submit" : $"step.{i % 7}",
                [TracescribeAttributes.OutcomeStatus] = failed ? OutcomeStatusValues.Failure : OutcomeStatusValues.Success,
            };
            if (failed)
            {
                attributes[TracescribeAttributes.FailureClass] = FailureClassValues.DependencyTimeout;
                attributes[TracescribeAttributes.FailureReason] = "VIN decoder did not respond";
            }

            spans.Add(new SpanRecord(traceId, (i + 1).ToString("x16"), i == 0 ? null : ((i - 1) / 4 + 1).ToString("x16"),
                i == 0 ? "claim.fnol.submit" : $"step {i}", "bench", ActivityKind.Internal, start.AddMilliseconds(i), TimeSpan.FromMilliseconds(3),
                failed ? ActivityStatusCode.Error : ActivityStatusCode.Unset, null, attributes, [], [])
            {
                Service = "claims-api",
            });
        }

        return spans;
    }
}
