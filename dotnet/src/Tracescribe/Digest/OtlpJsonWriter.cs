using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Tracescribe.Digest;

/// <summary>
/// Writes spans as OTLP/JSON (<c>ExportTraceServiceRequest</c>), the format <see cref="OtlpJsonReader"/>,
/// the OpenTelemetry Collector and most backends understand. Handy for saving a trace captured with
/// <see cref="TraceCollector"/> so an agent or the Tracescribe CLI can read it later.
/// </summary>
public static class OtlpJsonWriter
{
    /// <summary>Serializes spans, grouped by service and instrumentation scope.</summary>
    /// <param name="spans">Spans to write.</param>
    /// <param name="defaultService">Service name for spans whose <see cref="SpanRecord.Service"/> is unknown.</param>
    /// <param name="indented">Pretty-print the JSON.</param>
    public static string Write(IEnumerable<SpanRecord> spans, string? defaultService = null, bool indented = false)
    {
        using var stream = new MemoryStream();
        using (var w = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = indented }))
        {
            w.WriteStartObject();
            w.WriteStartArray("resourceSpans");
            foreach (var byService in spans.GroupBy(s => s.Service ?? defaultService))
            {
                w.WriteStartObject();
                w.WriteStartObject("resource");
                w.WriteStartArray("attributes");
                if (byService.Key is { } service)
                {
                    WriteAttribute(w, "service.name", service);
                }

                w.WriteEndArray();
                w.WriteEndObject();

                w.WriteStartArray("scopeSpans");
                foreach (var byScope in byService.GroupBy(s => s.Source))
                {
                    w.WriteStartObject();
                    w.WriteStartObject("scope");
                    w.WriteString("name", byScope.Key);
                    w.WriteEndObject();
                    w.WriteStartArray("spans");
                    foreach (var span in byScope)
                    {
                        WriteSpan(w, span);
                    }

                    w.WriteEndArray();
                    w.WriteEndObject();
                }

                w.WriteEndArray();
                w.WriteEndObject();
            }

            w.WriteEndArray();
            w.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static void WriteSpan(Utf8JsonWriter w, SpanRecord span)
    {
        w.WriteStartObject();
        w.WriteString("traceId", span.TraceId);
        w.WriteString("spanId", span.SpanId);
        w.WriteString("parentSpanId", span.ParentSpanId ?? string.Empty);
        w.WriteString("name", span.Name);
        w.WriteNumber("kind", span.Kind switch
        {
            ActivityKind.Server => 2,
            ActivityKind.Client => 3,
            ActivityKind.Producer => 4,
            ActivityKind.Consumer => 5,
            _ => 1,
        });
        var start = Nanos(span.StartTime);
        w.WriteString("startTimeUnixNano", start.ToString(CultureInfo.InvariantCulture));
        w.WriteString("endTimeUnixNano", (start + (span.Duration.Ticks * 100)).ToString(CultureInfo.InvariantCulture));
        WriteAttributes(w, span.Attributes);

        w.WriteStartArray("events");
        foreach (var e in span.Events)
        {
            w.WriteStartObject();
            w.WriteString("name", e.Name);
            w.WriteString("timeUnixNano", Nanos(e.Timestamp).ToString(CultureInfo.InvariantCulture));
            WriteAttributes(w, e.Attributes);
            w.WriteEndObject();
        }

        w.WriteEndArray();

        w.WriteStartArray("links");
        foreach (var l in span.Links)
        {
            w.WriteStartObject();
            w.WriteString("traceId", l.TraceId);
            w.WriteString("spanId", l.SpanId);
            WriteAttributes(w, l.Attributes);
            w.WriteEndObject();
        }

        w.WriteEndArray();

        w.WriteStartObject("status");
        w.WriteNumber("code", (int)span.Status);
        if (span.StatusDescription is { } message)
        {
            w.WriteString("message", message);
        }

        w.WriteEndObject();
        w.WriteEndObject();
    }

    private static void WriteAttributes(Utf8JsonWriter w, IReadOnlyDictionary<string, object?> attributes)
    {
        w.WriteStartArray("attributes");
        foreach (var kv in attributes)
        {
            if (kv.Value is not null)
            {
                WriteAttribute(w, kv.Key, kv.Value);
            }
        }

        w.WriteEndArray();
    }

    private static void WriteAttribute(Utf8JsonWriter w, string key, object value)
    {
        w.WriteStartObject();
        w.WriteString("key", key);
        w.WritePropertyName("value");
        WriteAnyValue(w, value);
        w.WriteEndObject();
    }

    private static void WriteAnyValue(Utf8JsonWriter w, object? value)
    {
        w.WriteStartObject();
        switch (value)
        {
            case string s:
                w.WriteString("stringValue", s);
                break;
            case bool b:
                w.WriteBoolean("boolValue", b);
                break;
            case sbyte or byte or short or ushort or int or uint or long:
                w.WriteString("intValue", Convert.ToInt64(value, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture));
                break;
            case float or double or decimal:
                w.WriteNumber("doubleValue", Convert.ToDouble(value, CultureInfo.InvariantCulture));
                break;
            case System.Collections.IEnumerable items:
                w.WriteStartObject("arrayValue");
                w.WriteStartArray("values");
                foreach (var item in items)
                {
                    WriteAnyValue(w, item);
                }

                w.WriteEndArray();
                w.WriteEndObject();
                break;
            default:
                w.WriteString("stringValue", SpanRecord.Format(value) ?? string.Empty);
                break;
        }

        w.WriteEndObject();
    }

    private static long Nanos(DateTimeOffset time) => (time.UtcTicks - DateTime.UnixEpoch.Ticks) * 100;
}
