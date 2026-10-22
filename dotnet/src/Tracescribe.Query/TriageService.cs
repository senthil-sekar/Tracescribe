using System.Globalization;
using System.Text;
using Tracescribe.Digest;
using Tracescribe.SemanticConventions;

namespace Tracescribe.Query;

/// <summary>One line of a failure search.</summary>
/// <param name="TraceId">Trace id.</param>
/// <param name="Start">Trace start.</param>
/// <param name="Duration">Trace duration.</param>
/// <param name="Root">Root span name.</param>
/// <param name="RootService">Root span service, when known.</param>
/// <param name="FailedCount">Failed spans.</param>
/// <param name="SemanticCount">Semantic failures.</param>
/// <param name="TopRootCause">Most likely root cause, when any.</param>
public sealed record TraceSummary(
    string TraceId,
    DateTimeOffset Start,
    TimeSpan Duration,
    string Root,
    string? RootService,
    int FailedCount,
    int SemanticCount,
    RootCauseCandidate? TopRootCause);

/// <summary>
/// Agent-facing triage operations over an <see cref="ITraceBackend"/>. Every method returns compact text
/// meant to be handed to an LLM. This is what the Tracescribe MCP server exposes as tools.
/// </summary>
public sealed class TriageService
{
    private static readonly HashSet<string> HiddenLogAttributes = new(StringComparer.Ordinal)
    {
        TracescribeAttributes.Intent, TracescribeAttributes.Redacted, "{OriginalFormat}",
    };

    private readonly ITraceBackend _backend;
    private readonly ILogBackend? _logs;
    private readonly TriageDigestOptions _digestOptions;

    /// <summary>Creates the service.</summary>
    /// <param name="backend">Where traces come from.</param>
    /// <param name="digestOptions">Digest rendering and redaction.</param>
    /// <param name="logs">Where logs come from; defaults to <paramref name="backend"/> when it also stores logs.</param>
    public TriageService(ITraceBackend backend, TriageDigestOptions? digestOptions = null, ILogBackend? logs = null)
    {
        _backend = backend ?? throw new ArgumentNullException(nameof(backend));
        _digestOptions = digestOptions ?? new TriageDigestOptions();
        _logs = logs ?? backend as ILogBackend;
    }

    /// <summary>The backend in use.</summary>
    public ITraceBackend Backend => _backend;

    /// <summary>The log backend in use, if log search is available.</summary>
    public ILogBackend? Logs => _logs;

    /// <summary>Digest of one trace, or a short explanation when it cannot be produced.</summary>
    public async Task<string> GetDigestAsync(string traceId, bool expandHealthy = false, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(traceId))
        {
            return "A trace id is required.";
        }

        try
        {
            var spans = await _backend.GetTraceAsync(traceId.Trim(), cancellationToken).ConfigureAwait(false);
            if (spans.Count == 0)
            {
                return $"Trace {traceId} was not found in {_backend.Name}.";
            }

            var options = new TriageDigestOptions
            {
                ExpandHealthy = expandHealthy,
                MaxValueLength = _digestOptions.MaxValueLength,
                IncludeContext = _digestOptions.IncludeContext,
                Redactor = _digestOptions.Redactor,
            };
            return TriageDigest.Render(spans, options);
        }
        catch (TraceBackendException ex)
        {
            return "Backend error: " + ex.Message;
        }
    }

    /// <summary>Finds failing traces and summarizes each with its most likely root cause.</summary>
    public async Task<IReadOnlyList<TraceSummary>> FindFailuresAsync(TraceQuery query, CancellationToken cancellationToken = default)
    {
        var limited = query with { Limit = Math.Clamp(query.Limit, 1, 50) };
        var found = await _backend.SearchAsync(limited, cancellationToken).ConfigureAwait(false);
        var summaries = new List<TraceSummary>(found.Count);
        foreach (var trace in found)
        {
            var spans = trace.Spans ?? await _backend.GetTraceAsync(trace.TraceId, cancellationToken).ConfigureAwait(false);
            if (spans.Count == 0)
            {
                continue;
            }

            var report = TriageDigest.Build(spans, _digestOptions).First();
            var ids = spans.Select(s => s.SpanId).ToHashSet(StringComparer.Ordinal);
            var root = spans.Where(s => s.ParentSpanId is null || !ids.Contains(s.ParentSpanId)).MinBy(s => s.StartTime) ?? spans[0];
            summaries.Add(new TraceSummary(
                trace.TraceId,
                spans.Min(s => s.StartTime),
                report.Duration,
                root.Name,
                root.Service,
                report.FailedCount,
                report.SemanticCount,
                report.RootCauses.FirstOrDefault()));
        }

        return summaries;
    }

    /// <summary>Text rendering of <see cref="FindFailuresAsync"/>.</summary>
    public async Task<string> FindFailuresTextAsync(TraceQuery query, CancellationToken cancellationToken = default)
    {
        IReadOnlyList<TraceSummary> summaries;
        try
        {
            summaries = await FindFailuresAsync(query, cancellationToken).ConfigureAwait(false);
        }
        catch (TraceBackendException ex)
        {
            return "Backend error: " + ex.Message;
        }

        var where = (query.Service is null ? string.Empty : $" in {query.Service}")
            + (query.Lookback is { } lb ? $" over the last {FormatSpan(lb)}" : string.Empty);
        if (summaries.Count == 0)
        {
            return $"No {Describe(query.Kind, 2)}{where} found in {_backend.Name}.";
        }

        var sb = new StringBuilder();
        sb.Append(summaries.Count.ToString(CultureInfo.InvariantCulture)).Append(' ').Append(Describe(query.Kind, summaries.Count))
          .Append(where).Append(" (").Append(_backend.Name).AppendLine(", newest first):");
        foreach (var s in summaries)
        {
            sb.Append("- ").Append(s.TraceId)
              .Append(" | ").Append(s.Start.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss'Z'", CultureInfo.InvariantCulture))
              .Append(" | ").Append(s.Root);
            if (s.RootService is not null)
            {
                sb.Append(" (").Append(s.RootService).Append(')');
            }

            sb.Append(" | ").Append(s.FailedCount).Append(" failed, ").Append(s.SemanticCount).Append(" semantic");
            if (s.TopRootCause is { } c)
            {
                sb.Append(" | likely cause: ").Append(c.Name);
                if (c.FailureClass is not null)
                {
                    sb.Append(" [").Append(c.FailureClass).Append(']');
                }

                if (c.Reason is not null)
                {
                    sb.Append(" ").Append(c.Reason);
                }
            }

            sb.AppendLine();
        }

        sb.Append("Use get_trace_digest with a trace id for the full picture.");
        return sb.ToString();
    }

    /// <summary>Services known to the backend, one per line.</summary>
    public async Task<string> ListServicesTextAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var services = await _backend.ListServicesAsync(cancellationToken).ConfigureAwait(false);
            return services.Count == 0 ? $"No services found in {_backend.Name}." : string.Join(Environment.NewLine, services);
        }
        catch (TraceBackendException ex)
        {
            return "Backend error: " + ex.Message;
        }
    }

    /// <summary>
    /// Log records matching the query, oldest first, one per line, with values redacted and truncated like digests.
    /// </summary>
    public async Task<string> FindLogsTextAsync(LogQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (_logs is null)
        {
            return $"Log search isn't available for {_backend.Name}. Set TRACESCRIBE_LOGS_URL to a Loki URL, or use the azuremonitor or file backend.";
        }

        if (query.Intent is null && query.TraceId is null && query.Service is null)
        {
            return "Give an intent, a trace id or a service to search logs.";
        }

        IReadOnlyList<LogEntry> logs;
        try
        {
            logs = await _logs.SearchLogsAsync(query with { Limit = Math.Clamp(query.Limit, 1, 200) }, cancellationToken).ConfigureAwait(false);
        }
        catch (TraceBackendException ex)
        {
            return "Backend error: " + ex.Message;
        }

        var filters = new List<string>();
        if (query.Intent is not null)
        {
            filters.Add("intent=" + query.Intent);
        }

        if (query.TraceId is not null)
        {
            filters.Add("trace=" + query.TraceId);
        }

        if (query.Service is not null)
        {
            filters.Add("service=" + query.Service);
        }

        if (query.MinSeverity != LogSeverity.Unspecified)
        {
            filters.Add("severity>=" + query.MinSeverity.ToString().ToUpperInvariant());
        }

        var where = string.Join(", ", filters)
            + (query.Lookback is { } lb ? $" over the last {FormatSpan(lb)}" : string.Empty);
        if (logs.Count == 0)
        {
            return $"No log records for {where} found in {_logs.Name}.";
        }

        var sb = new StringBuilder();
        sb.Append(logs.Count.ToString(CultureInfo.InvariantCulture)).Append(logs.Count == 1 ? " log record" : " log records")
          .Append(" for ").Append(where).Append(" (").Append(_logs.Name).AppendLine(", oldest first):");
        foreach (var log in logs.OrderBy(l => l.Timestamp))
        {
            sb.Append(log.Timestamp.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture))
              .Append(' ').Append(log.Severity == LogSeverity.Unspecified ? "-" : log.Severity.ToString().ToUpperInvariant());
            if (log.Service is not null && query.Service is null)
            {
                sb.Append(' ').Append(log.Service);
            }

            if (log.Intent is { } intent && (query.Intent is null || query.IntentIsPrefix))
            {
                sb.Append(" [").Append(intent).Append(']');
            }

            if (log.TraceId is not null && query.TraceId is null)
            {
                sb.Append(" trace=").Append(log.TraceId);
            }

            if (log.SpanId is not null)
            {
                sb.Append(" span=").Append(log.SpanId);
            }

            sb.Append("  ").Append(Clean("body", log.Body));
            var attributes = log.Attributes
                .Where(kv => kv.Value is not null && !HiddenLogAttributes.Contains(kv.Key))
                .Select(kv =>
                {
                    var key = kv.Key.StartsWith(TracescribeAttributes.ContextPrefix, StringComparison.Ordinal)
                        ? kv.Key[TracescribeAttributes.ContextPrefix.Length..]
                        : kv.Key;
                    return key + "=" + Clean(key, SpanRecord.Format(kv.Value) ?? string.Empty);
                })
                .ToList();
            if (attributes.Count > 0)
            {
                sb.Append(" | ").Append(string.Join(", ", attributes));
            }

            sb.AppendLine();
        }

        if (query.TraceId is null && logs.Any(l => l.TraceId is not null))
        {
            sb.Append("Use get_trace_digest with a trace id to see the operation a log line was written in.");
        }

        return sb.ToString().TrimEnd();
    }

    private string Clean(string key, string value)
    {
        var safe = _digestOptions.Redactor is { } redactor ? redactor.Redact(key, value, out _) : value;
        safe = safe.ReplaceLineEndings(" ");
        return safe.Length > _digestOptions.MaxValueLength ? safe[.._digestOptions.MaxValueLength] + "…" : safe;
    }

    private static string Describe(FailureKind kind, int count)
    {
        var noun = count == 1 ? "trace" : "traces";
        return kind switch
        {
            FailureKind.Error => $"{noun} with errors",
            FailureKind.Semantic => $"{noun} with semantic failures",
            _ => $"failing {noun}",
        };
    }

    private static string FormatSpan(TimeSpan t) => t.TotalHours >= 1
        ? t.TotalHours.ToString("0.#", CultureInfo.InvariantCulture) + "h"
        : t.TotalMinutes.ToString("0", CultureInfo.InvariantCulture) + "m";
}
