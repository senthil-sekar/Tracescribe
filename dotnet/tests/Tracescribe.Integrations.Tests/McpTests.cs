using Microsoft.Extensions.Logging;
using OpenTelemetry;
using OpenTelemetry.Logs;
using OpenTelemetry.Trace;
using Tracescribe.Digest;
using Tracescribe.OpenTelemetry;
using Tracescribe.Mcp;
using Tracescribe.Query;
using Xunit;

namespace Tracescribe.Integrations.Tests;

public class McpTests
{
    private static string WriteSampleTrace(out string traceId)
    {
        var source = new System.Diagnostics.ActivitySource("Mcp.Tests." + Guid.NewGuid().ToString("N"));
        using var collector = new TraceCollector(s => s.Name == source.Name);
        using (var root = Scribe.Begin("claim.fnol.submit", source: source))
        {
            traceId = root.Activity!.TraceId.ToHexString();
            using (var child = Scribe.Begin("coverage.check", expected: "coverage on reported vehicle", source: source))
            {
                child.Mismatch("coverage on another vehicle", "fallback VIN used");
            }

            root.Succeed();
        }

        var path = Path.Combine(Path.GetTempPath(), $"tracescribe-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, OtlpJsonWriter.Write(collector.Spans, "claims-api"));
        return path;
    }

    [Fact]
    public async Task Tools_work_over_a_file_backend()
    {
        var path = WriteSampleTrace(out var traceId);
        try
        {
            var tools = new TriageTools(new TriageService(new FileTraceBackend(path)));

            var digest = await tools.GetTraceDigest(traceId);
            Assert.Contains("[WRONG] coverage.check", digest);
            Assert.Contains("svc=claims-api", digest);

            var failures = await tools.FindFailures(kind: "semantic");
            Assert.Contains(traceId, failures);
            Assert.Contains("likely cause: coverage.check [semantic] fallback VIN used", failures);

            Assert.Contains("No traces with errors", await tools.FindFailures(kind: "error"));
            Assert.StartsWith("Unknown kind", await tools.FindFailures(kind: "bogus"));
            Assert.Equal("claims-api", await tools.ListServices());
        }
        finally
        {
            File.Delete(path);
        }
    }

    private sealed class CaptureLogEntries : BaseProcessor<LogRecord>
    {
        public List<LogEntry> Entries { get; } = new();

        public override void OnEnd(LogRecord data)
        {
            lock (Entries)
            {
                Entries.Add(data.ToLogEntry("claims-api"));
            }
        }
    }

    [Fact]
    public async Task Find_logs_by_intent_end_to_end()
    {
        // ILogger -> TracescribeLogProcessor (intent + redaction) -> OTLP/JSON file -> find_logs.
        var capture = new CaptureLogEntries();
        using var tracer = Sdk.CreateTracerProviderBuilder().AddTracescribe().Build();
        using (var factory = LoggerFactory.Create(b => b.AddOpenTelemetry(o =>
        {
            o.IncludeFormattedMessage = true;
            o.AddTracescribe();
            o.AddProcessor(capture);
        })))
        {
            var logger = factory.CreateLogger("claims");
            using (var op = Scribe.Begin("claim.fnol.submit"))
            {
                logger.LogInformation("Claim received from {Reporter}", "jane.doe@example.com");
                using (Scribe.Begin("coverage.check"))
                {
                    logger.LogWarning("Coverage evaluated against fallback VIN {Vin}", "1HGCM82633A004352");
                }

                op.Succeed();
            }

            logger.LogError("Unrelated failure outside any operation");
        }

        var path = Path.Combine(Path.GetTempPath(), $"tracescribe-logs-{Guid.NewGuid():N}.json");
        await File.WriteAllTextAsync(path, OtlpJsonWriter.WriteLogs(capture.Entries));
        try
        {
            var tools = new TriageTools(new TriageService(new FileTraceBackend(path)));

            var submit = await tools.FindLogs(intent: "claim.fnol.submit");
            Assert.StartsWith("1 log record for intent=claim.fnol.submit (file, oldest first):", submit);
            Assert.Contains("INFO claims-api trace=", submit);
            Assert.Contains("Claim received from [REDACTED:email] | Reporter=[REDACTED:email]", submit);
            Assert.DoesNotContain("jane.doe", submit);

            var all = await tools.FindLogs(service: "claims-api", minSeverity: "warn");
            Assert.Contains("WARN [coverage.check]", all);
            Assert.Contains("ERROR  Unrelated failure outside any operation", all);
            Assert.DoesNotContain("Claim received", all);

            Assert.StartsWith("Unknown severity", await tools.FindLogs(intent: "x", minSeverity: "loud"));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Command_line_parses_commands_and_backend_flags()
    {
        var cli = CommandLine.Parse(["failures", "--backend", "tempo", "--url", "http://tempo:3200", "--service", "claims-api", "--minutes", "15", "--kind", "semantic", "--limit", "5"]);
        Assert.Null(cli.Error);
        Assert.Equal("failures", cli.Command);
        Assert.Equal("tempo", cli.Backend.Kind);
        Assert.Equal("http://tempo:3200", cli.Backend.Url);
        Assert.Equal(new TraceQuery("claims-api", TimeSpan.FromMinutes(15), 5, FailureKind.Semantic), cli.Query);

        var digest = CommandLine.Parse(["digest", "abc", "--path", "traces.json", "--expand"]);
        Assert.Equal("abc", digest.Argument);
        Assert.Equal("file", digest.Backend.Kind);
        Assert.True(digest.ExpandHealthy);

        Assert.Equal("serve", CommandLine.Parse([]).Command);
        Assert.NotNull(CommandLine.Parse(["digest"]).Error);
        Assert.NotNull(CommandLine.Parse(["frobnicate"]).Error);
        Assert.NotNull(CommandLine.Parse(["failures", "--minutes"]).Error);

        var logs = CommandLine.Parse(["logs", "--intent", "claim.*", "--trace", "abc", "--service", "claims-api", "--severity", "warn",
            "--minutes", "30", "--limit", "20", "--backend", "tempo", "--logs-url", "http://loki:3100", "--logs-header", "X-Scope-OrgID: t1"]);
        Assert.Null(logs.Error);
        Assert.Equal(new LogQuery("claim.*", "abc", "claims-api", TimeSpan.FromMinutes(30), LogSeverity.Warn, 20), logs.LogQuery);
        Assert.Equal("http://loki:3100", logs.Backend.LogsUrl);
        Assert.Equal("X-Scope-OrgID: t1", logs.Backend.LogsHeader);
        Assert.Equal(50, CommandLine.Parse(["logs", "--intent", "x"]).LogQuery.Limit);
        Assert.NotNull(CommandLine.Parse(["logs", "--severity", "loud"]).Error);

        var azure = CommandLine.Parse(["failures", "--workspace", "ws-1"]);
        Assert.Equal("azuremonitor", azure.Backend.Kind);
        Assert.Equal("ws-1", azure.Backend.Workspace);
    }
}
