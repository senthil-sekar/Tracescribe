using Tracescribe.Digest;

namespace Tracescribe.Query;

/// <summary>Which traces <see cref="ITraceBackend.SearchAsync"/> should return.</summary>
public enum FailureKind
{
    /// <summary>Traces with an error span or a semantic failure.</summary>
    Any,
    /// <summary>Traces with at least one span in error status.</summary>
    Error,
    /// <summary>Traces with at least one span where <c>tracescribe.outcome.matched = false</c>.</summary>
    Semantic,
}

/// <summary>A search for failing traces.</summary>
/// <param name="Service">Only traces touching this service; <c>null</c> for all services.</param>
/// <param name="Lookback">How far back to search from now; <c>null</c> lets the backend decide (1 hour for Jaeger and Tempo, all data for files).</param>
/// <param name="Limit">Maximum traces to return.</param>
/// <param name="Kind">Which failures to look for.</param>
public sealed record TraceQuery(string? Service = null, TimeSpan? Lookback = null, int Limit = 10, FailureKind Kind = FailureKind.Any);

/// <summary>A trace found by a search. <see cref="Spans"/> is set when the backend returned them with the search.</summary>
/// <param name="TraceId">Trace id (hex).</param>
/// <param name="Spans">The trace's spans, if already loaded.</param>
public sealed record FoundTrace(string TraceId, IReadOnlyList<SpanRecord>? Spans = null);

/// <summary>A source of traces: a tracing backend or exported files.</summary>
public interface ITraceBackend
{
    /// <summary>Short name for messages, e.g. <c>jaeger</c>.</summary>
    string Name { get; }

    /// <summary>Loads every span of a trace. Returns an empty list when the trace does not exist.</summary>
    Task<IReadOnlyList<SpanRecord>> GetTraceAsync(string traceId, CancellationToken cancellationToken = default);

    /// <summary>Finds traces that contain failures, newest first.</summary>
    Task<IReadOnlyList<FoundTrace>> SearchAsync(TraceQuery query, CancellationToken cancellationToken = default);

    /// <summary>Lists service names known to the backend.</summary>
    Task<IReadOnlyList<string>> ListServicesAsync(CancellationToken cancellationToken = default);
}

/// <summary>A search for log records.</summary>
/// <param name="Intent">Only records carrying this <c>tracescribe.intent</c>. A trailing <c>*</c> matches by prefix, e.g. <c>claim.*</c>.</param>
/// <param name="TraceId">Only records written in this trace.</param>
/// <param name="Service">Only records from this service.</param>
/// <param name="Lookback">How far back to search from now; <c>null</c> lets the backend decide (1 hour for HTTP backends, all data for files).</param>
/// <param name="MinSeverity">Only records at or above this severity.</param>
/// <param name="Limit">Maximum records to return; the newest are kept.</param>
public sealed record LogQuery(
    string? Intent = null,
    string? TraceId = null,
    string? Service = null,
    TimeSpan? Lookback = null,
    LogSeverity MinSeverity = LogSeverity.Unspecified,
    int Limit = 50)
{
    /// <summary>Whether <see cref="Intent"/> is a prefix pattern (ends with <c>*</c>).</summary>
    public bool IntentIsPrefix => Intent is { Length: > 0 } i && i[^1] == '*';

    /// <summary><see cref="Intent"/> without a trailing <c>*</c>.</summary>
    public string? IntentValue => IntentIsPrefix ? Intent![..^1] : Intent;

    /// <summary>Whether a record satisfies this query. Backends that filter server-side use it as a final check.</summary>
    public bool Matches(LogEntry log)
    {
        ArgumentNullException.ThrowIfNull(log);
        if (IntentValue is { } intent)
        {
            var actual = log.Intent;
            if (actual is null || !(IntentIsPrefix ? actual.StartsWith(intent, StringComparison.Ordinal) : actual == intent))
            {
                return false;
            }
        }

        return (TraceId is null || string.Equals(log.TraceId, TraceId, StringComparison.OrdinalIgnoreCase))
            && (Service is null || log.Service == Service)
            && (MinSeverity == LogSeverity.Unspecified || log.Severity >= MinSeverity);
    }
}

/// <summary>A source of log records. Backends that store both traces and logs implement this and <see cref="ITraceBackend"/>.</summary>
public interface ILogBackend
{
    /// <summary>Short name for messages, e.g. <c>loki</c>.</summary>
    string Name { get; }

    /// <summary>Finds log records matching the query, newest first.</summary>
    Task<IReadOnlyList<LogEntry>> SearchLogsAsync(LogQuery query, CancellationToken cancellationToken = default);
}

/// <summary>Raised when a backend cannot be reached or returns an unexpected response.</summary>
public sealed class TraceBackendException : Exception
{
    /// <summary>Creates the exception.</summary>
    public TraceBackendException(string message, Exception? inner = null) : base(message, inner)
    {
    }
}
