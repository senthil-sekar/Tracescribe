using System.Net;
using System.Text.Json;
using Tracescribe.Digest;
using Tracescribe.Query;
using Xunit;

namespace Tracescribe.Tests;

public class AzureMonitorTests
{
    private const string TraceId = "a619766eb96e96ae355d257cbf8fe474";
    private const string Workspace = "00000000-1111-2222-3333-444444444444";

    private static readonly string[] TraceColumns =
        ["Type", "TimeGenerated", "Id", "ParentId", "OperationId", "Name", "DurationMs", "Success", "ResultCode", "DependencyType", "AppRoleName", "Properties", "Message", "ExceptionType", "OuterMessage"];

    /// <summary>A Log Analytics query response, the way the API encodes it (dynamic columns as JSON strings).</summary>
    private static string Table(string[] columns, params object?[][] rows) => JsonSerializer.Serialize(new
    {
        tables = new[]
        {
            new
            {
                name = "PrimaryResult",
                columns = columns.Select(c => new { name = c, type = "string" }),
                rows,
            },
        },
    });

    private static string Props(params (string Key, string Value)[] props) =>
        JsonSerializer.Serialize(props.ToDictionary(p => p.Key, p => p.Value));

    // What the Azure Monitor exporter writes for the ClaimIntake sample, trimmed.
    private static readonly object?[][] TraceRows =
    [
        ["AppRequests", "2026-10-01T12:00:00.000Z", "1000000000000001", TraceId, TraceId, "claim.fnol.submit", 148.0, false, "0", null, "claims-api",
            Props(("tracescribe.intent", "claim.fnol.submit"), ("tracescribe.outcome.expected", "claim accepted with a claim number"),
                  ("tracescribe.failure.class", "validation"), ("tracescribe.failure.reason", "claim held for manual review"),
                  ("tracescribe.context.reporter_email", "jane.doe@example.com")), null, null, null],
        ["AppDependencies", "2026-10-01T12:00:00.010Z", "2000000000000002", "1000000000000001", TraceId, "execute_tool decode_vin", 52.0, false, "", "InProc", "claims-api",
            Props(("gen_ai.operation.name", "execute_tool"), ("tracescribe.failure.class", "dependency.timeout"), ("tracescribe.failure.inferred", "True"),
                  ("tracescribe.failure.retryable", "True")), null, null, null],
        ["AppDependencies", "2026-10-01T12:00:00.100Z", "3000000000000003", "1000000000000001", TraceId, "coverage.check", 7.0, true, "", "InProc", "claims-api",
            Props(("tracescribe.intent", "coverage.check"), ("tracescribe.outcome.expected", "collision coverage on the reported vehicle"),
                  ("tracescribe.outcome.matched", "False"), ("tracescribe.failure.reason", "coverage evaluated against fallback VIN")), null, null, null],
        ["AppDependencies", "2026-10-01T12:00:00.030Z", "4000000000000004", "1000000000000001", TraceId, "GET /policies", 20.0, true, "200", "HTTP", "policy-api", "{}", null, null, null],
        ["AppExceptions", "2026-10-01T12:00:00.062Z", null, "2000000000000002", TraceId, null, null, null, null, null, "claims-api", "{}", null,
            "System.TimeoutException", "VIN decoder did not respond within 30ms"],
        ["AppTraces", "2026-10-01T12:00:00.070Z", null, "1000000000000001", TraceId, null, null, null, null, null, "claims-api",
            Props(("tracescribe.decision.name", "vin.fallback"), ("tracescribe.decision.chosen", "first_vehicle_on_policy")), "tracescribe.decision", null, null],
    ];

    private static (AzureMonitorBackend Backend, LambdaHandler Handler) Create(Func<string, string> respond)
    {
        var handler = new LambdaHandler((req, body) =>
        {
            if (req.RequestUri!.AbsolutePath != $"/v1/workspaces/{Workspace}/query" || req.Method != HttpMethod.Post)
            {
                return (HttpStatusCode.NotFound, "{}");
            }

            if (req.Headers.Authorization?.ToString() != "Bearer token-1")
            {
                return (HttpStatusCode.Unauthorized, """{"error":{"message":"bad token"}}""");
            }

            using var request = JsonDocument.Parse(body!);
            return (HttpStatusCode.OK, respond(request.RootElement.GetProperty("query").GetString()!));
        });
        var backend = new AzureMonitorBackend(new HttpClient(handler), new Uri(AzureMonitorBackend.DefaultUrl), Workspace, _ => ValueTask.FromResult("token-1"));
        return (backend, handler);
    }

    [Fact]
    public async Task Digest_from_application_insights_tables()
    {
        var (backend, handler) = Create(_ => Table(TraceColumns, TraceRows));
        var digest = await new TriageService(backend).GetDigestAsync(TraceId.ToUpperInvariant());

        Assert.Contains("[FAIL] claim.fnol.submit 148ms svc=claims-api class=validation", digest);
        Assert.Contains("[FAIL] execute_tool decode_vin 52ms", digest);
        Assert.Contains("class=dependency.timeout(inferred)", digest);
        Assert.Contains("exception: System.TimeoutException: VIN decoder did not respond within 30ms", digest);
        Assert.Contains("[WRONG] coverage.check 7ms", digest);
        Assert.Contains("decision: vin.fallback chose=first_vehicle_on_policy", digest);
        Assert.Contains("reporter_email=[REDACTED:email]", digest);
        Assert.Contains("retryable=true", digest);
        Assert.DoesNotContain("jane.doe", digest);

        var (_, body) = Assert.Single(handler.Requests);
        using var request = JsonDocument.Parse(body!);
        Assert.Equal("P7D", request.RootElement.GetProperty("timespan").GetString());
        Assert.Contains($"where OperationId in (\"{TraceId}\")", request.RootElement.GetProperty("query").GetString());
    }

    [Fact]
    public async Task Search_finds_ids_then_loads_all_traces_in_one_query()
    {
        var (backend, handler) = Create(kql => kql.Contains("summarize", StringComparison.Ordinal)
            ? Table(["OperationId", "Start"], [TraceId, "2026-10-01T12:00:00Z"], ["bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", "2026-10-01T11:00:00Z"])
            : Table(TraceColumns, TraceRows.Where(r => kql.Contains((string)r[4]!, StringComparison.Ordinal)).ToArray()));

        var failures = await new TriageService(backend).FindFailuresAsync(new TraceQuery("claims-api", TimeSpan.FromMinutes(30), 5, FailureKind.Semantic));

        var s = Assert.Single(failures); // the second id has no spans in the window
        Assert.Equal("claim.fnol.submit", s.Root);
        Assert.Equal(2, s.FailedCount);
        Assert.Equal(1, s.SemanticCount);
        Assert.Equal("execute_tool decode_vin", s.TopRootCause?.Name);

        Assert.Equal(3, handler.Requests.Count); // search, batch load, then a direct load of the trace that had no spans
        using var search = JsonDocument.Parse(handler.Requests[0].Body!);
        Assert.Equal("PT30M", search.RootElement.GetProperty("timespan").GetString());
        Assert.Contains("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", handler.Requests[1].Body);
    }

    [Fact]
    public void Kql_queries()
    {
        Assert.Equal("""
            union AppRequests, AppDependencies
            | where AppRoleName == "claims-api"
            | where Success == false or tostring(Properties["tracescribe.outcome.matched"]) =~ "false" or tostring(Properties["tracescribe.failure.class"]) == "semantic" or tostring(Properties["tracescribe.failure.class"]) startswith_cs "semantic."
            | summarize Start = min(TimeGenerated) by OperationId
            | top 10 by Start desc
            """.ReplaceLineEndings("\n"), AzureMonitorBackend.SearchKql(new TraceQuery("claims-api")));

        Assert.Contains("| where Success == false\n", AzureMonitorBackend.SearchKql(new TraceQuery(Kind: FailureKind.Error)));

        Assert.Equal("""
            AppTraces
            | where OperationId == "a619766eb96e96ae355d257cbf8fe474"
            | where tostring(Properties["tracescribe.intent"]) startswith_cs "claim."
            | where SeverityLevel >= 2
            | where Message !startswith_cs "tracescribe."
            | top 50 by TimeGenerated desc
            | project TimeGenerated, Message, SeverityLevel, OperationId, ParentId, AppRoleName, Properties
            """.ReplaceLineEndings("\n"), AzureMonitorBackend.LogsKql(new LogQuery("claim.*", TraceId.ToUpperInvariant(), MinSeverity: LogSeverity.Warn)));
    }

    [Fact]
    public void Kql_values_are_quoted()
    {
        var kql = AzureMonitorBackend.SearchKql(new TraceQuery("x\" or 1==1 | take 1 //\\"));
        Assert.Contains("""| where AppRoleName == "x\" or 1==1 | take 1 //\\"\n""".Replace("\\n", "\n", StringComparison.Ordinal), kql);
    }

    [Fact]
    public async Task Logs_and_services()
    {
        var (backend, _) = Create(kql => kql.StartsWith("AppTraces", StringComparison.Ordinal)
            ? Table(["TimeGenerated", "Message", "SeverityLevel", "OperationId", "ParentId", "AppRoleName", "Properties"],
                ["2026-10-01T12:00:00.040Z", "VIN decoder timed out", 2, TraceId, "2000000000000002", "claims-api",
                    Props(("tracescribe.intent", "claim.fnol.submit"), ("attempt", "2"))],
                ["2026-10-01T12:00:00.000Z", "Submitting claim", 1, TraceId, TraceId, "claims-api", Props(("tracescribe.intent", "claim.fnol.submit"))])
            : Table(["AppRoleName"], ["policy-api"], ["claims-api"]));
        var triage = new TriageService(backend);

        var text = await triage.FindLogsTextAsync(new LogQuery("claim.fnol.submit"));
        Assert.Contains("2 log records for intent=claim.fnol.submit (azuremonitor, oldest first):", text);
        Assert.Contains($"12:00:00.000Z INFO claims-api trace={TraceId}  Submitting claim", text);
        Assert.Contains($"12:00:00.040Z WARN claims-api trace={TraceId} span=2000000000000002  VIN decoder timed out | attempt=2", text);

        Assert.Equal("claims-api\npolicy-api".ReplaceLineEndings(), await triage.ListServicesTextAsync());
    }

    [Fact]
    public async Task Errors_become_readable_text()
    {
        var handler = new LambdaHandler((_, _) => (HttpStatusCode.Forbidden, """{"error":{"code":"InsufficientAccessError","message":"no access"}}"""));
        var backend = new AzureMonitorBackend(new HttpClient(handler), new Uri(AzureMonitorBackend.DefaultUrl), Workspace, _ => ValueTask.FromResult("t"));
        Assert.StartsWith($"Backend error: azuremonitor: 403 Forbidden from /v1/workspaces/{Workspace}/query", await new TriageService(backend).GetDigestAsync(TraceId));

        var tokenFailure = new AzureMonitorBackend(new HttpClient(handler), new Uri(AzureMonitorBackend.DefaultUrl), Workspace,
            _ => throw new TraceBackendException("azuremonitor: could not get an Azure token"));
        Assert.Equal("Backend error: azuremonitor: could not get an Azure token", await new TriageService(tokenFailure).ListServicesTextAsync());
    }

    [Fact]
    public void Factory_requires_workspace_and_credentials()
    {
        Assert.IsType<AzureMonitorBackend>(TraceBackendFactory.Create(new TraceBackendOptions { Kind = "azuremonitor", Workspace = Workspace, AccessTokenProvider = _ => ValueTask.FromResult("t") }));
        Assert.IsType<AzureMonitorBackend>(TraceBackendFactory.Create(new TraceBackendOptions { Kind = "AzureMonitor", Workspace = Workspace, Header = "Authorization: Bearer t" }));
        Assert.Throws<ArgumentException>(() => TraceBackendFactory.Create(new TraceBackendOptions { Kind = "azuremonitor", AccessTokenProvider = _ => ValueTask.FromResult("t") }));
        Assert.Throws<ArgumentException>(() => TraceBackendFactory.Create(new TraceBackendOptions { Kind = "azuremonitor", Workspace = Workspace }));
    }
}
