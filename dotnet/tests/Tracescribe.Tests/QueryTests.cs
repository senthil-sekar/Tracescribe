using System.Net;
using System.Text;
using Tracescribe.Query;
using Xunit;

namespace Tracescribe.Tests;

public class QueryTests
{
    private sealed class FakeHandler(Func<Uri, (HttpStatusCode Status, string Body)> respond) : HttpMessageHandler
    {
        public List<Uri> Requests { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request.RequestUri!);
            var (status, body) = respond(request.RequestUri!);
            return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
        }
    }

    private const string TraceId = "5b8efff798038103d269b633813fc60c";

    [Fact]
    public async Task Jaeger_get_trace_and_digest()
    {
        var handler = new FakeHandler(uri => uri.AbsolutePath == $"/api/traces/{TraceId}"
            ? (HttpStatusCode.OK, ReaderTests.JaegerJson)
            : (HttpStatusCode.NotFound, "{}"));
        var triage = new TriageService(new JaegerBackend(new HttpClient(handler), new Uri("http://jaeger:16686")));

        var digest = await triage.GetDigestAsync(TraceId);
        Assert.Contains("ROOT CAUSE CANDIDATES", digest);
        Assert.Contains("execute_tool decode_vin", digest);

        Assert.Contains("was not found in jaeger", await triage.GetDigestAsync("0000000000000000000000000000beef"));
    }

    [Fact]
    public async Task Jaeger_search_queries_each_service_for_errors_and_semantic_failures()
    {
        var handler = new FakeHandler(uri => uri.AbsolutePath switch
        {
            "/api/services" => (HttpStatusCode.OK, """{"data":["claims-api","billing"]}"""),
            "/api/traces" when uri.Query.Contains("claims-api") && uri.Query.Contains("error") => (HttpStatusCode.OK, ReaderTests.JaegerJson),
            "/api/traces" => (HttpStatusCode.OK, """{"data":[]}"""),
            _ => (HttpStatusCode.NotFound, "{}"),
        });
        var triage = new TriageService(new JaegerBackend(new HttpClient(handler), new Uri("http://jaeger:16686/")));

        var text = await triage.FindFailuresTextAsync(new TraceQuery(Lookback: TimeSpan.FromMinutes(30)));

        Assert.Contains("1 failing trace over the last 30m (jaeger, newest first):", text);
        Assert.Contains(TraceId, text);
        Assert.Contains("claim.fnol.submit (claims-api)", text);
        Assert.Contains("likely cause: execute_tool decode_vin", text);

        var searches = handler.Requests.Where(r => r.AbsolutePath == "/api/traces").Select(r => Uri.UnescapeDataString(r.Query)).ToList();
        Assert.Equal(4, searches.Count); // 2 services x (error, semantic)
        Assert.Contains(searches, q => q.Contains("""tags={"error":"true"}""", StringComparison.Ordinal));
        Assert.Contains(searches, q => q.Contains("""tags={"tracescribe.outcome.matched":"false"}""", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(null, FailureKind.Error, "{ status = error }")]
    [InlineData(null, FailureKind.Semantic, "{ span.tracescribe.outcome.matched = false }")]
    [InlineData("claims-api", FailureKind.Any, "{ resource.service.name = \"claims-api\" && (status = error || span.tracescribe.outcome.matched = false) }")]
    public void Tempo_traceql(string? service, FailureKind kind, string expected) =>
        Assert.Equal(expected, TempoBackend.TraceQl(new TraceQuery(service, Kind: kind)));

    [Fact]
    public async Task Tempo_search_then_fetch()
    {
        var handler = new FakeHandler(uri => uri.AbsolutePath switch
        {
            "/api/search" => (HttpStatusCode.OK, $$"""{"traces":[{"traceID":"{{TraceId}}","rootServiceName":"claims-api"}]}"""),
            var p when p == $"/api/traces/{TraceId}" => (HttpStatusCode.OK, ReaderTests.OtlpJson),
            "/api/search/tag/service.name/values" => (HttpStatusCode.OK, """{"tagValues":["vin-service","claims-api"]}"""),
            _ => (HttpStatusCode.NotFound, "{}"),
        });
        var triage = new TriageService(new TempoBackend(new HttpClient(handler), new Uri("http://tempo:3200")));

        var found = await triage.FindFailuresAsync(new TraceQuery("claims-api"));
        var s = Assert.Single(found);
        Assert.Equal("claim.fnol.submit", s.Root);
        Assert.Equal("GET /decode", s.TopRootCause?.Name);
        Assert.Equal("claims-api\nvin-service".ReplaceLineEndings(), await triage.ListServicesTextAsync());
    }

    [Fact]
    public async Task Backend_errors_become_readable_text()
    {
        var handler = new FakeHandler(_ => (HttpStatusCode.Unauthorized, "token expired"));
        var triage = new TriageService(new TempoBackend(new HttpClient(handler), new Uri("http://tempo:3200")));

        Assert.Equal("Backend error: tempo: 401 Unauthorized from /api/traces/abc: token expired", await triage.GetDigestAsync("abc"));
        Assert.StartsWith("Backend error: tempo: 401", await triage.FindFailuresTextAsync(new TraceQuery()));
    }

    [Fact]
    public async Task File_backend_reads_otlp_and_jaeger_files()
    {
        var dir = Directory.CreateTempSubdirectory("tracescribe-tests");
        try
        {
            await File.WriteAllTextAsync(Path.Combine(dir.FullName, "a.jsonl"), ReaderTests.OtlpJson.ReplaceLineEndings(" "));
            await File.WriteAllTextAsync(Path.Combine(dir.FullName, "b.json"), ReaderTests.JaegerJson.Replace(TraceId, "1111111111111111111111111111111a", StringComparison.Ordinal));
            var triage = new TriageService(new FileTraceBackend(dir.FullName));

            var found = await triage.FindFailuresAsync(new TraceQuery());
            Assert.Equal(2, found.Count);
            Assert.Single(await triage.FindFailuresAsync(new TraceQuery("vin-service")));
            Assert.Contains("claims-agent", await triage.ListServicesTextAsync());
            Assert.Contains("svc=vin-service", await triage.GetDigestAsync(TraceId));
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public void Factory_validates_settings()
    {
        Assert.IsType<JaegerBackend>(TraceBackendFactory.Create(new TraceBackendOptions()));
        Assert.IsType<TempoBackend>(TraceBackendFactory.Create(new TraceBackendOptions { Kind = "Tempo", Header = "Authorization: Basic abc" }));
        Assert.Throws<ArgumentException>(() => TraceBackendFactory.Create(new TraceBackendOptions { Kind = "file" }));
        Assert.Throws<ArgumentException>(() => TraceBackendFactory.Create(new TraceBackendOptions { Kind = "zipkin" }));
    }
}
