using System.Net;
using System.Text;
using Tracescribe.Digest;
using Tracescribe.Query;
using Tracescribe.SemanticConventions;
using Xunit;

namespace Tracescribe.Tests;

public class LogTests
{
    private const string TraceId = "5b8efff798038103d269b633813fc60c";
    private static readonly DateTimeOffset T0 = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);

    internal static IReadOnlyList<LogEntry> SampleLogs() =>
    [
        new(T0, LogSeverity.Info, "Submitting claim for jane.doe@example.com", TraceId, "eee19b7ec3c1b174",
            new Dictionary<string, object?> { [TracescribeAttributes.Intent] = "claim.fnol.submit", ["claim_id"] = "CLM-1" }) { Service = "claims-api" },
        new(T0.AddMilliseconds(40), LogSeverity.Warn, "VIN decoder timed out, falling back to first vehicle", TraceId, "aaa19b7ec3c1b174",
            new Dictionary<string, object?> { [TracescribeAttributes.Intent] = "claim.fnol.submit", ["attempt"] = 2L }) { Service = "claims-api" },
        new(T0.AddMilliseconds(50), LogSeverity.Error, "Coverage check used fallback VIN", TraceId, "ccc19b7ec3c1b174",
            new Dictionary<string, object?> { [TracescribeAttributes.Intent] = "claim.coverage.check" }) { Service = "policy-api" },
        new(T0.AddMinutes(-5), LogSeverity.Error, "unrelated failure", "1111111111111111111111111111111a", null,
            new Dictionary<string, object?> { [TracescribeAttributes.Intent] = "billing.invoice.send" }) { Service = "billing" },
    ];

    [Fact]
    public void Otlp_logs_round_trip()
    {
        var logs = OtlpJsonReader.ReadLogs(OtlpJsonWriter.WriteLogs(SampleLogs()));

        Assert.Equal(4, logs.Count);
        var warn = logs.Single(l => l.Severity == LogSeverity.Warn);
        Assert.Equal(T0.AddMilliseconds(40), warn.Timestamp);
        Assert.Equal("claims-api", warn.Service);
        Assert.Equal("claim.fnol.submit", warn.Intent);
        Assert.Equal(TraceId, warn.TraceId);
        Assert.Equal("aaa19b7ec3c1b174", warn.SpanId);
        Assert.Equal(2L, warn.Attributes["attempt"]);
        Assert.Null(logs.Single(l => l.Service == "billing").SpanId);

        Assert.Empty(OtlpJsonReader.ReadLogs(ReaderTests.OtlpJson)); // spans only
    }

    [Fact]
    public void Reads_collector_style_logs()
    {
        // JSON lines, enum names, base64 ids, severity text only, observed time only.
        var json = """
            {"resourceLogs":[{"resource":{"attributes":[{"key":"service.name","value":{"stringValue":"claims-api"}}]},"scopeLogs":[{"logRecords":[{"timeUnixNano":"1760000000000000000","severityNumber":"SEVERITY_NUMBER_WARN2","body":{"stringValue":"slow"},"traceId":"W47/95gDgQPSabYzgT/GDA==","spanId":"7uGbfsPBsXQ="}]}]}]}
            {"resourceLogs":[{"scopeLogs":[{"logRecords":[{"observedTimeUnixNano":"1760000000000000000","severityText":"Critical","body":{"stringValue":"down"}}]}]}]}
            """;
        var logs = OtlpJsonReader.ReadLogs(json);

        Assert.Equal(2, logs.Count);
        Assert.Equal(LogSeverity.Warn, logs[0].Severity);
        Assert.Equal(TraceId, logs[0].TraceId);
        Assert.Equal("eee19b7ec3c1b174", logs[0].SpanId);
        Assert.Equal(LogSeverity.Fatal, logs[1].Severity);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1760000000), logs[1].Timestamp);
    }

    [Theory]
    [InlineData("claim.fnol.submit", null, LogSeverity.Unspecified, 2)]
    [InlineData("claim.*", null, LogSeverity.Unspecified, 3)]
    [InlineData("claim.", null, LogSeverity.Unspecified, 0)]
    [InlineData(null, TraceId, LogSeverity.Warn, 2)]
    [InlineData(null, "1111111111111111111111111111111A", LogSeverity.Unspecified, 1)]
    public async Task File_backend_searches_logs(string? intent, string? traceId, LogSeverity severity, int expected)
    {
        var path = Path.Combine(Path.GetTempPath(), $"tracescribe-logs-{Guid.NewGuid():N}.json");
        try
        {
            await File.WriteAllTextAsync(path, OtlpJsonWriter.WriteLogs(SampleLogs()));
            var logs = await new FileTraceBackend(path).SearchLogsAsync(new LogQuery(intent, traceId, MinSeverity: severity));
            Assert.Equal(expected, logs.Count);
            Assert.True(logs.SequenceEqual(logs.OrderByDescending(l => l.Timestamp)));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task Find_logs_text_is_compact_redacted_and_chronological()
    {
        var dir = Directory.CreateTempSubdirectory("tracescribe-tests");
        try
        {
            await File.WriteAllTextAsync(Path.Combine(dir.FullName, "traces.json"), ReaderTests.OtlpJson);
            await File.WriteAllTextAsync(Path.Combine(dir.FullName, "logs.json"), OtlpJsonWriter.WriteLogs(SampleLogs()));
            var triage = new TriageService(new FileTraceBackend(dir.FullName));

            var text = await triage.FindLogsTextAsync(new LogQuery("claim.fnol.submit"));
            var lines = text.Split('\n').Select(l => l.TrimEnd('\r')).ToArray();
            Assert.Equal("2 log records for intent=claim.fnol.submit (file, oldest first):", lines[0]);
            Assert.Equal("2026-10-01 12:00:00.000Z INFO claims-api trace=5b8efff798038103d269b633813fc60c span=eee19b7ec3c1b174  Submitting claim for [REDACTED:email] | claim_id=CLM-1", lines[1]);
            Assert.Equal("2026-10-01 12:00:00.040Z WARN claims-api trace=5b8efff798038103d269b633813fc60c span=aaa19b7ec3c1b174  VIN decoder timed out, falling back to first vehicle | attempt=2", lines[2]);
            Assert.StartsWith("Use get_trace_digest", lines[3]);

            var byTrace = await triage.FindLogsTextAsync(new LogQuery("claim.*", TraceId, MinSeverity: LogSeverity.Error));
            Assert.Contains("1 log record for intent=claim.*, trace=5b8efff798038103d269b633813fc60c, severity>=ERROR", byTrace);
            Assert.Contains("ERROR policy-api [claim.coverage.check] span=ccc19b7ec3c1b174  Coverage check used fallback VIN", byTrace);
            Assert.DoesNotContain("get_trace_digest", byTrace);

            Assert.Equal("No log records for intent=nope found in file.", await triage.FindLogsTextAsync(new LogQuery("nope")));
            Assert.StartsWith("Give an intent", await triage.FindLogsTextAsync(new LogQuery()));

            // Spans and logs can share a directory.
            Assert.Contains("ROOT CAUSE CANDIDATES", await triage.GetDigestAsync(TraceId));
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task Find_logs_explains_when_logs_are_not_configured()
    {
        var triage = new TriageService(new JaegerBackend(new HttpClient(), new Uri("http://jaeger:16686")));
        Assert.StartsWith("Log search isn't available for jaeger. Set TRACESCRIBE_LOGS_URL", await triage.FindLogsTextAsync(new LogQuery("x")));
    }

    [Theory]
    [InlineData(null, null, null, LogSeverity.Unspecified, "{service_name=~\".+\"}")]
    [InlineData("claim.fnol.submit", null, "claims-api", LogSeverity.Unspecified, "{service_name=\"claims-api\"} | tracescribe_intent=\"claim.fnol.submit\"")]
    [InlineData("claim.*", TraceId, null, LogSeverity.Warn, "{service_name=~\".+\"} | tracescribe_intent=~\"claim\\\\..*\" | trace_id=\"5b8efff798038103d269b633813fc60c\" | severity_number >= 13")]
    [InlineData("say \"hi\"", null, null, LogSeverity.Unspecified, "{service_name=~\".+\"} | tracescribe_intent=\"say \\\"hi\\\"\"")]
    public void Loki_logql(string? intent, string? traceId, string? service, LogSeverity severity, string expected) =>
        Assert.Equal(expected, LokiLogBackend.LogQl(new LogQuery(intent, traceId, service, MinSeverity: severity)));

    [Fact]
    public async Task Loki_search_reads_streams()
    {
        Uri? requested = null;
        var handler = new LambdaHandler((req, _) =>
        {
            requested = req.RequestUri;
            return (HttpStatusCode.OK, """
                {"status":"success","data":{"resultType":"streams","result":[
                  {"stream":{"service_name":"claims-api","tracescribe_intent":"claim.fnol.submit","trace_id":"5B8EFFF798038103D269B633813FC60C","span_id":"aaa19b7ec3c1b174","severity_number":"13","severity_text":"Warning","claim_id":"CLM-1"},
                   "values":[["1760000000040000000","VIN decoder timed out"],["1760000000000000000","Submitting claim"]]},
                  {"stream":{"service_name":"claims-api","tracescribe_intent":"claim.fnol.submit","detected_level":"error"},
                   "values":[["1760000000050000000","held for review"]]}]}}
                """);
        });
        var loki = new LokiLogBackend(new HttpClient(handler), new Uri("http://loki:3100"));

        var logs = await loki.SearchLogsAsync(new LogQuery("claim.fnol.submit", Limit: 2));

        Assert.Equal(2, logs.Count);
        Assert.Equal("held for review", logs[0].Body);
        Assert.Equal(LogSeverity.Error, logs[0].Severity);
        Assert.Equal("VIN decoder timed out", logs[1].Body);
        Assert.Equal(LogSeverity.Warn, logs[1].Severity);
        Assert.Equal(TraceId, logs[1].TraceId);
        Assert.Equal("claim.fnol.submit", logs[1].Intent);
        Assert.Equal("CLM-1", logs[1].Get("claim_id"));
        Assert.False(logs[1].Attributes.ContainsKey("severity_text"));

        Assert.Equal("/loki/api/v1/query_range", requested!.AbsolutePath);
        var query = Uri.UnescapeDataString(requested.Query);
        Assert.Contains("query={service_name=~\".+\"} | tracescribe_intent=\"claim.fnol.submit\"", query);
        Assert.Contains("limit=2", query);
        Assert.Contains("direction=backward", query);
    }

    [Fact]
    public void Factory_picks_log_backend()
    {
        var file = new FileTraceBackend("x");
        Assert.Same(file, TraceBackendFactory.CreateLogs(new TraceBackendOptions(), file));

        var tempo = TraceBackendFactory.Create(new TraceBackendOptions { Kind = "tempo" });
        Assert.Null(TraceBackendFactory.CreateLogs(new TraceBackendOptions(), tempo));
        Assert.IsType<LokiLogBackend>(TraceBackendFactory.CreateLogs(new TraceBackendOptions { LogsUrl = "http://loki:3100", LogsHeader = "X-Scope-OrgID: tenant1" }, tempo));
        Assert.Throws<ArgumentException>(() => TraceBackendFactory.CreateLogs(new TraceBackendOptions { LogsUrl = "http://loki:3100", LogsHeader = "bad" }, tempo));
    }

    [Theory]
    [InlineData("WARN", LogSeverity.Warn)]
    [InlineData("Warning", LogSeverity.Warn)]
    [InlineData("err", LogSeverity.Error)]
    [InlineData("Critical", LogSeverity.Fatal)]
    [InlineData("verbose", LogSeverity.Trace)]
    [InlineData("loud", LogSeverity.Unspecified)]
    public void Parses_severity(string text, LogSeverity expected) => Assert.Equal(expected, LogEntry.ParseSeverity(text));
}

/// <summary>HTTP handler that answers every request with a callback. Captures request bodies before they are disposed.</summary>
internal sealed class LambdaHandler(Func<HttpRequestMessage, string?, (HttpStatusCode Status, string Body)> respond) : HttpMessageHandler
{
    public List<(HttpRequestMessage Request, string? Body)> Requests { get; } = new();

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
        Requests.Add((request, body));
        var (status, text) = respond(request, body);
        return new HttpResponseMessage(status) { Content = new StringContent(text, Encoding.UTF8, "application/json") };
    }
}
