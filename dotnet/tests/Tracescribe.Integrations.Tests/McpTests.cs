using Tracescribe.Digest;
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
    }
}
