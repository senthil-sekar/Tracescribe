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

/// <summary>Raised when a backend cannot be reached or returns an unexpected response.</summary>
public sealed class TraceBackendException : Exception
{
    /// <summary>Creates the exception.</summary>
    public TraceBackendException(string message, Exception? inner = null) : base(message, inner)
    {
    }
}
