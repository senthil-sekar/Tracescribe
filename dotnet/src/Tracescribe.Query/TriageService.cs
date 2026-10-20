using System.Globalization;
using System.Text;
using Tracescribe.Digest;

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
    private readonly ITraceBackend _backend;
    private readonly TriageDigestOptions _digestOptions;

    /// <summary>Creates the service.</summary>
    public TriageService(ITraceBackend backend, TriageDigestOptions? digestOptions = null)
    {
        _backend = backend ?? throw new ArgumentNullException(nameof(backend));
        _digestOptions = digestOptions ?? new TriageDigestOptions();
    }

    /// <summary>The backend in use.</summary>
    public ITraceBackend Backend => _backend;

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
