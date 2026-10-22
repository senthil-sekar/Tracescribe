using Microsoft.Extensions.Logging;
using OpenTelemetry.Logs;
using Tracescribe.Digest;

namespace Tracescribe.OpenTelemetry;

/// <summary>Converts OpenTelemetry log records to Tracescribe's backend-neutral <see cref="LogEntry"/>.</summary>
public static class LogRecordExtensions
{
    /// <summary>
    /// Snapshots a log record, e.g. in a processor's <c>OnEnd</c> (records are pooled, so copy before returning).
    /// Pair with <see cref="OtlpJsonWriter.WriteLogs"/> to save logs the Tracescribe CLI and MCP server can search.
    /// </summary>
    /// <param name="record">The record.</param>
    /// <param name="service">Service name to stamp on the entry.</param>
    public static LogEntry ToLogEntry(this LogRecord record, string? service = null)
    {
        ArgumentNullException.ThrowIfNull(record);
        var severity = record.LogLevel switch
        {
            LogLevel.Trace => LogSeverity.Trace,
            LogLevel.Debug => LogSeverity.Debug,
            LogLevel.Information => LogSeverity.Info,
            LogLevel.Warning => LogSeverity.Warn,
            LogLevel.Error => LogSeverity.Error,
            LogLevel.Critical => LogSeverity.Fatal,
            _ => LogSeverity.Unspecified,
        };

        var attributes = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var kv in record.Attributes ?? [])
        {
            if (kv.Key != "{OriginalFormat}")
            {
                attributes[kv.Key] = kv.Value;
            }
        }

        return new LogEntry(
            record.Timestamp,
            severity,
            record.FormattedMessage ?? record.Body ?? string.Empty,
            record.TraceId == default ? null : record.TraceId.ToHexString(),
            record.SpanId == default ? null : record.SpanId.ToHexString(),
            attributes)
        {
            Service = service,
        };
    }
}
