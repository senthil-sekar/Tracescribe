using Tracescribe.Digest;
using Tracescribe.Query;
using Xunit;

namespace Tracescribe.Backends.Tests;

[Collection(BackendCollection.Name)]
public sealed class BackendTests(BackendFixture fixture)
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(30) };

    [BackendFact]
    public Task Jaeger_finds_and_digests_the_trace() =>
        FindsAndDigests(new TriageService(new JaegerBackend(Http, fixture.JaegerUrl)));

    [BackendFact]
    public Task Tempo_finds_and_digests_the_trace() =>
        FindsAndDigests(new TriageService(new TempoBackend(Http, fixture.TempoUrl)));

    [BackendFact]
    public async Task Loki_finds_logs_by_intent_and_trace()
    {
        var loki = new LokiLogBackend(Http, fixture.LokiUrl);

        var byIntent = await BackendFixture.Eventually(
            () => loki.SearchLogsAsync(new LogQuery("claim.fnol.submit", Service: fixture.Service, Lookback: TimeSpan.FromMinutes(15))),
            logs => logs.Count >= 2,
            "Loki logs for intent claim.fnol.submit");
        var warn = Assert.Single(byIntent, l => l.Severity == LogSeverity.Warn);
        Assert.Equal("VIN decoder timed out, using first vehicle on policy", warn.Body);
        Assert.Equal(fixture.TraceId, warn.TraceId);
        Assert.NotNull(warn.SpanId);
        Assert.Equal(fixture.Service, warn.Service);

        // Redacted in-process by TracescribeLogProcessor before export.
        var info = Assert.Single(byIntent, l => l.Severity == LogSeverity.Info);
        Assert.Equal("Claim received from [REDACTED:email]", info.Body);

        var prefix = await loki.SearchLogsAsync(new LogQuery("claim.*", Service: fixture.Service, Lookback: TimeSpan.FromMinutes(15)));
        Assert.Equal(byIntent.Count, prefix.Count);

        var byTrace = await loki.SearchLogsAsync(new LogQuery(TraceId: fixture.TraceId, Service: fixture.Service, MinSeverity: LogSeverity.Warn));
        Assert.Equal("VIN decoder timed out, using first vehicle on policy", Assert.Single(byTrace).Body);

        Assert.Empty(await loki.SearchLogsAsync(new LogQuery("billing.invoice.send", Service: fixture.Service, Lookback: TimeSpan.FromMinutes(15))));
    }

    private async Task FindsAndDigests(TriageService triage)
    {
        var name = triage.Backend.Name;
        foreach (var kind in new[] { FailureKind.Semantic, FailureKind.Error, FailureKind.Any })
        {
            var found = await BackendFixture.Eventually(
                () => triage.FindFailuresAsync(new TraceQuery(fixture.Service, TimeSpan.FromMinutes(15), 10, kind)),
                summaries => summaries.Any(s => s.TraceId == fixture.TraceId),
                $"{name} {kind} search");
            var summary = found.Single(s => s.TraceId == fixture.TraceId);
            Assert.Equal("claim.fnol.submit", summary.Root);
            Assert.Equal(fixture.Service, summary.RootService);
            Assert.Equal(2, summary.FailedCount);
            Assert.Equal(1, summary.SemanticCount);
            Assert.Equal("execute_tool decode_vin", summary.TopRootCause?.Name);
        }

        var digest = await triage.GetDigestAsync(fixture.TraceId);
        Assert.Contains("[FAIL] claim.fnol.submit", digest);
        Assert.Contains("class=validation", digest);
        Assert.Contains("class=dependency.timeout(inferred)", digest);
        Assert.Contains("exception: System.TimeoutException: VIN decoder did not respond within 30ms", digest);
        Assert.Contains("[WRONG] coverage.check", digest);
        Assert.Contains("decision: vin.fallback chose=first_vehicle_on_policy alts=[retry_decoder, ask_claimant]", digest);
        Assert.Contains("reporter_email=[REDACTED:email]", digest);
        Assert.DoesNotContain("jane.doe", digest);

        Assert.Contains(fixture.Service, await triage.ListServicesTextAsync());
        Assert.Contains("was not found", await triage.GetDigestAsync("00000000000000000000000000000001"));
    }
}
