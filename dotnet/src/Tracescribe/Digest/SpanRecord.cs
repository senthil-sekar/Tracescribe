using System.Diagnostics;

namespace Tracescribe.Digest;

/// <summary>An event on a span.</summary>
/// <param name="Name">Event name.</param>
/// <param name="Timestamp">When it happened.</param>
/// <param name="Attributes">Event attributes.</param>
public sealed record SpanEventRecord(string Name, DateTimeOffset Timestamp, IReadOnlyDictionary<string, object?> Attributes);

/// <summary>A link from a span to another span.</summary>
/// <param name="TraceId">Linked trace id (hex).</param>
/// <param name="SpanId">Linked span id (hex).</param>
/// <param name="Attributes">Link attributes.</param>
public sealed record SpanLinkRecord(string TraceId, string SpanId, IReadOnlyDictionary<string, object?> Attributes);

/// <summary>
/// Backend-neutral snapshot of a finished span. The digest works on these, so spans can come from
/// in-process <see cref="Activity"/> objects today and from OTLP/JSON exports or a trace backend later.
/// </summary>
/// <param name="TraceId">Trace id (hex).</param>
/// <param name="SpanId">Span id (hex).</param>
/// <param name="ParentSpanId">Parent span id (hex), or <c>null</c> for a root.</param>
/// <param name="Name">Span name.</param>
/// <param name="Source">Instrumentation scope / activity source name.</param>
/// <param name="Kind">Span kind.</param>
/// <param name="StartTime">Start time.</param>
/// <param name="Duration">Duration.</param>
/// <param name="Status">Span status.</param>
/// <param name="StatusDescription">Status description.</param>
/// <param name="Attributes">Span attributes.</param>
/// <param name="Events">Span events.</param>
/// <param name="Links">Span links.</param>
public sealed record SpanRecord(
    string TraceId,
    string SpanId,
    string? ParentSpanId,
    string Name,
    string Source,
    ActivityKind Kind,
    DateTimeOffset StartTime,
    TimeSpan Duration,
    ActivityStatusCode Status,
    string? StatusDescription,
    IReadOnlyDictionary<string, object?> Attributes,
    IReadOnlyList<SpanEventRecord> Events,
    IReadOnlyList<SpanLinkRecord> Links)
{
    /// <summary>Snapshots a finished <see cref="Activity"/>.</summary>
    public static SpanRecord FromActivity(Activity activity)
    {
        var parent = activity.ParentSpanId == default ? null : activity.ParentSpanId.ToHexString();
        return new SpanRecord(
            activity.TraceId.ToHexString(),
            activity.SpanId.ToHexString(),
            parent,
            activity.DisplayName,
            activity.Source.Name,
            activity.Kind,
            activity.StartTimeUtc,
            activity.Duration,
            activity.Status,
            activity.StatusDescription,
            ToDictionary(activity.TagObjects),
            activity.Events.Select(e => new SpanEventRecord(e.Name, e.Timestamp, ToDictionary(e.Tags))).ToList(),
            activity.Links.Select(l => new SpanLinkRecord(
                l.Context.TraceId.ToHexString(),
                l.Context.SpanId.ToHexString(),
                ToDictionary(l.Tags ?? Enumerable.Empty<KeyValuePair<string, object?>>()))).ToList());
    }

    /// <summary>Returns an attribute rendered as a string, or <c>null</c>.</summary>
    public string? Get(string key) => Attributes.TryGetValue(key, out var v) ? Format(v) : null;

    internal static string? Format(object? value) => value switch
    {
        null => null,
        string s => s,
        bool b => b ? "true" : "false",
        IEnumerable<string> list => string.Join(", ", list),
        IFormattable f => f.ToString(null, System.Globalization.CultureInfo.InvariantCulture),
        _ => value.ToString(),
    };

    private static Dictionary<string, object?> ToDictionary(IEnumerable<KeyValuePair<string, object?>> tags)
    {
        var d = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var t in tags)
        {
            d[t.Key] = t.Value;
        }

        return d;
    }
}
