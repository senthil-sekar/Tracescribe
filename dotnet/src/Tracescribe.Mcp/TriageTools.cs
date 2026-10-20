using System.ComponentModel;
using ModelContextProtocol.Server;
using Tracescribe.Query;

namespace Tracescribe.Mcp;

/// <summary>MCP tools exposed by the Tracescribe server. Each returns compact, agent-readable text.</summary>
[McpServerToolType]
public sealed class TriageTools
{
    private readonly TriageService _triage;

    /// <summary>Creates the tools.</summary>
    public TriageTools(TriageService triage) => _triage = triage;

    /// <summary>Triage digest for one trace.</summary>
    [McpServerTool(Name = "get_trace_digest", ReadOnly = true)]
    [Description("Returns a compact triage digest of one distributed trace: the span tree with intent, expected vs. actual " +
                 "outcome, failure class and decisions (healthy subtrees collapsed), followed by ranked root-cause candidates. " +
                 "Start here when you have a trace id from an alert, log line or error report.")]
    public Task<string> GetTraceDigest(
        [Description("Trace id, 32 hex characters.")] string traceId,
        [Description("Render healthy subtrees too. Leave false unless the failure may hide in a span marked OK.")] bool expandHealthy = false,
        CancellationToken cancellationToken = default) =>
        _triage.GetDigestAsync(traceId, expandHealthy, cancellationToken);

    /// <summary>Recent failing traces with likely causes.</summary>
    [McpServerTool(Name = "find_failures", ReadOnly = true)]
    [Description("Finds recent traces containing failures and lists each with its most likely root cause, newest first. " +
                 "kind = 'error' (span errors), 'semantic' (completed but wrong result: tracescribe.outcome.matched=false) or 'any'.")]
    public Task<string> FindFailures(
        [Description("Only traces touching this service. Omit for all services.")] string? service = null,
        [Description("How many minutes back to search. Omit for the backend default (60 for Jaeger and Tempo, all data for files).")] int? lookbackMinutes = null,
        [Description("'any', 'error' or 'semantic'.")] string kind = "any",
        [Description("Maximum traces to return (1-50).")] int limit = 10,
        CancellationToken cancellationToken = default)
    {
        if (!Enum.TryParse<FailureKind>(kind, ignoreCase: true, out var failureKind))
        {
            return Task.FromResult($"Unknown kind '{kind}'. Use any, error or semantic.");
        }

        var query = new TraceQuery(
            string.IsNullOrWhiteSpace(service) ? null : service,
            lookbackMinutes is { } minutes ? TimeSpan.FromMinutes(Math.Clamp(minutes, 1, 60 * 24 * 7)) : null,
            limit,
            failureKind);
        return _triage.FindFailuresTextAsync(query, cancellationToken);
    }

    /// <summary>Known services.</summary>
    [McpServerTool(Name = "list_services", ReadOnly = true)]
    [Description("Lists the service names known to the trace backend, for use with find_failures.")]
    public Task<string> ListServices(CancellationToken cancellationToken = default) =>
        _triage.ListServicesTextAsync(cancellationToken);
}
