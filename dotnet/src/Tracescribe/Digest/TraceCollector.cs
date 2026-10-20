using System.Collections.Concurrent;
using System.Diagnostics;

namespace Tracescribe.Digest;

/// <summary>
/// In-memory span collector built on <see cref="ActivityListener"/>. Useful for tests, local
/// development and producing digests without an OpenTelemetry pipeline. Not intended for production
/// export; use the OpenTelemetry SDK with <c>Tracescribe.OpenTelemetry</c> for that.
/// </summary>
public sealed class TraceCollector : IDisposable
{
    private readonly ActivityListener _listener;
    private readonly ConcurrentQueue<SpanRecord> _spans = new();

    /// <summary>Starts collecting.</summary>
    /// <param name="sourceFilter">Which sources to listen to; defaults to all sources.</param>
    /// <param name="enricher">Optional enricher applied when each span ends; defaults to a new <see cref="TracescribeEnricher"/>.</param>
    public TraceCollector(Func<ActivitySource, bool>? sourceFilter = null, TracescribeEnricher? enricher = null)
    {
        var e = enricher ?? new TracescribeEnricher();
        _listener = new ActivityListener
        {
            ShouldListenTo = sourceFilter ?? (_ => true),
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activity =>
            {
                e.OnEnd(activity);
                _spans.Enqueue(SpanRecord.FromActivity(activity));
            },
        };
        ActivitySource.AddActivityListener(_listener);
    }

    /// <summary>All spans collected so far, in end order.</summary>
    public IReadOnlyList<SpanRecord> Spans => _spans.ToArray();

    /// <summary>Spans belonging to one trace.</summary>
    public IReadOnlyList<SpanRecord> GetTrace(ActivityTraceId traceId)
    {
        var id = traceId.ToHexString();
        return _spans.Where(s => s.TraceId == id).ToArray();
    }

    /// <summary>Stops collecting.</summary>
    public void Dispose() => _listener.Dispose();
}
