using Tracescribe.SemanticConventions;

namespace Tracescribe.Digest;

/// <summary>Log severity, following the OpenTelemetry severity ranges.</summary>
public enum LogSeverity
{
    /// <summary>Severity not recorded.</summary>
    Unspecified = 0,
    /// <summary>OTel severity 1-4.</summary>
    Trace = 1,
    /// <summary>OTel severity 5-8.</summary>
    Debug = 5,
    /// <summary>OTel severity 9-12.</summary>
    Info = 9,
    /// <summary>OTel severity 13-16.</summary>
    Warn = 13,
    /// <summary>OTel severity 17-20.</summary>
    Error = 17,
    /// <summary>OTel severity 21-24.</summary>
    Fatal = 21,
}

/// <summary>
/// Backend-neutral snapshot of a log record, the log counterpart of <see cref="SpanRecord"/>.
/// </summary>
/// <param name="Timestamp">When the record was written.</param>
/// <param name="Severity">Severity range.</param>
/// <param name="Body">Log message.</param>
/// <param name="TraceId">Trace id (hex) of the span the record was written in, when known.</param>
/// <param name="SpanId">Span id (hex) of the span the record was written in, when known.</param>
/// <param name="Attributes">Log attributes.</param>
public sealed record LogEntry(
    DateTimeOffset Timestamp,
    LogSeverity Severity,
    string Body,
    string? TraceId,
    string? SpanId,
    IReadOnlyDictionary<string, object?> Attributes)
{
    /// <summary>Service that wrote the record (OTel resource <c>service.name</c>), when known.</summary>
    public string? Service { get; init; }

    /// <summary>The <c>tracescribe.intent</c> of the operation the record was written in, when known.</summary>
    public string? Intent => Get(TracescribeAttributes.Intent);

    /// <summary>Returns an attribute rendered as a string, or <c>null</c>.</summary>
    public string? Get(string key) => Attributes.TryGetValue(key, out var v) ? SpanRecord.Format(v) : null;

    /// <summary>Maps an OpenTelemetry severity number (1-24) to its range.</summary>
    public static LogSeverity FromSeverityNumber(int number) => number switch
    {
        >= 21 => LogSeverity.Fatal,
        >= 17 => LogSeverity.Error,
        >= 13 => LogSeverity.Warn,
        >= 9 => LogSeverity.Info,
        >= 5 => LogSeverity.Debug,
        >= 1 => LogSeverity.Trace,
        _ => LogSeverity.Unspecified,
    };

    /// <summary>Parses a severity name such as <c>WARN</c>, <c>Warning</c>, <c>err</c> or <c>Critical</c>.</summary>
    public static LogSeverity ParseSeverity(string? text) => text?.Trim().ToUpperInvariant() switch
    {
        null or "" => LogSeverity.Unspecified,
        var t when t.StartsWith("TRACE", StringComparison.Ordinal) || t.StartsWith("VERBOSE", StringComparison.Ordinal) => LogSeverity.Trace,
        var t when t.StartsWith("DEBUG", StringComparison.Ordinal) => LogSeverity.Debug,
        var t when t.StartsWith("INFO", StringComparison.Ordinal) => LogSeverity.Info,
        var t when t.StartsWith("WARN", StringComparison.Ordinal) => LogSeverity.Warn,
        var t when t.StartsWith("ERR", StringComparison.Ordinal) => LogSeverity.Error,
        var t when t.StartsWith("FATAL", StringComparison.Ordinal) || t.StartsWith("CRIT", StringComparison.Ordinal) => LogSeverity.Fatal,
        _ => LogSeverity.Unspecified,
    };
}
