using System.Diagnostics;
using System.Globalization;
using System.Text.Json;

namespace Tracescribe.Digest;

/// <summary>
/// Reads spans from OTLP/JSON: the <c>ExportTraceServiceRequest</c> JSON encoding written by the
/// OpenTelemetry Collector file exporter and returned by Grafana Tempo and Jaeger's OTLP API.
/// </summary>
/// <remarks>
/// Lenient on purpose so it can read what real backends emit: accepts a single document or JSON lines,
/// <c>resourceSpans</c> or <c>batches</c>, <c>scopeSpans</c> or <c>instrumentationLibrarySpans</c>, a
/// top-level <c>trace</c> wrapper, hex or base64 ids, and numeric or string enums and 64-bit integers.
/// </remarks>
public static class OtlpJsonReader
{
    /// <summary>Reads all spans from OTLP/JSON text (one document, or one document per line).</summary>
    public static IReadOnlyList<SpanRecord> Read(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        var spans = new List<SpanRecord>();
        try
        {
            using var doc = JsonDocument.Parse(json);
            ReadDocument(doc.RootElement, spans);
            return spans;
        }
        catch (JsonException) when (json.Contains('\n'))
        {
            // JSON lines: the Collector file exporter writes one request per line.
        }

        spans.Clear();
        foreach (var line in json.Split('\n'))
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            using var doc = JsonDocument.Parse(line);
            ReadDocument(doc.RootElement, spans);
        }

        return spans;
    }

    /// <summary>Reads all spans from a file of OTLP/JSON.</summary>
    public static IReadOnlyList<SpanRecord> ReadFile(string path) => Read(File.ReadAllText(path));

    private static void ReadDocument(JsonElement root, List<SpanRecord> spans)
    {
        if (root.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in root.EnumerateArray())
            {
                ReadDocument(item, spans);
            }

            return;
        }

        if (root.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        if (root.TryGetProperty("trace", out var wrapped))
        {
            ReadDocument(wrapped, spans);
            return;
        }

        foreach (var resourceSpans in ArrayOf(root, "resourceSpans", "batches"))
        {
            string? service = null;
            if (resourceSpans.TryGetProperty("resource", out var resource))
            {
                service = SpanRecord.Format(Attributes(resource).GetValueOrDefault("service.name"));
            }

            foreach (var scopeSpans in ArrayOf(resourceSpans, "scopeSpans", "instrumentationLibrarySpans"))
            {
                var scopeName = string.Empty;
                if ((scopeSpans.TryGetProperty("scope", out var scope) || scopeSpans.TryGetProperty("instrumentationLibrary", out scope))
                    && scope.TryGetProperty("name", out var n))
                {
                    scopeName = n.GetString() ?? string.Empty;
                }

                foreach (var span in ArrayOf(scopeSpans, "spans"))
                {
                    spans.Add(ReadSpan(span, scopeName, service));
                }
            }
        }
    }

    private static SpanRecord ReadSpan(JsonElement span, string scopeName, string? service)
    {
        var start = Nanos(span, "startTimeUnixNano");
        var end = Nanos(span, "endTimeUnixNano");
        var parent = Id(span, "parentSpanId", 16);

        var status = ActivityStatusCode.Unset;
        string? statusMessage = null;
        if (span.TryGetProperty("status", out var s))
        {
            status = Enum(s, "code") switch
            {
                1 or 1001 => ActivityStatusCode.Ok,
                2 or 1002 => ActivityStatusCode.Error,
                _ => ActivityStatusCode.Unset,
            };
            statusMessage = s.TryGetProperty("message", out var m) ? m.GetString() : null;
            if (string.IsNullOrEmpty(statusMessage))
            {
                statusMessage = null;
            }
        }

        var events = ArrayOf(span, "events")
            .Select(e => new SpanEventRecord(
                e.TryGetProperty("name", out var en) ? en.GetString() ?? string.Empty : string.Empty,
                ToTime(Nanos(e, "timeUnixNano")),
                Attributes(e)))
            .ToList();

        var links = ArrayOf(span, "links")
            .Select(l => new SpanLinkRecord(Id(l, "traceId", 32) ?? string.Empty, Id(l, "spanId", 16) ?? string.Empty, Attributes(l)))
            .ToList();

        return new SpanRecord(
            Id(span, "traceId", 32) ?? string.Empty,
            Id(span, "spanId", 16) ?? string.Empty,
            parent,
            span.TryGetProperty("name", out var name) ? name.GetString() ?? string.Empty : string.Empty,
            scopeName,
            Kind(Enum(span, "kind")),
            ToTime(start),
            TimeSpan.FromTicks(Math.Max(0, end - start) / 100),
            status,
            statusMessage,
            Attributes(span),
            events,
            links)
        {
            Service = service,
        };
    }

    private static IEnumerable<JsonElement> ArrayOf(JsonElement obj, params string[] names)
    {
        foreach (var name in names)
        {
            if (obj.TryGetProperty(name, out var arr) && arr.ValueKind == JsonValueKind.Array)
            {
                return arr.EnumerateArray().ToList();
            }
        }

        return [];
    }

    private static Dictionary<string, object?> Attributes(JsonElement obj)
    {
        var d = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var kv in ArrayOf(obj, "attributes"))
        {
            if (kv.TryGetProperty("key", out var key) && key.GetString() is { } k)
            {
                d[k] = kv.TryGetProperty("value", out var v) ? AnyValue(v) : null;
            }
        }

        return d;
    }

    private static object? AnyValue(JsonElement v)
    {
        if (v.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        foreach (var p in v.EnumerateObject())
        {
            switch (p.Name)
            {
                case "stringValue":
                    return p.Value.GetString();
                case "boolValue":
                    return p.Value.ValueKind == JsonValueKind.String ? p.Value.GetString() == "true" : p.Value.GetBoolean();
                case "intValue":
                    return p.Value.ValueKind == JsonValueKind.String
                        ? long.Parse(p.Value.GetString()!, CultureInfo.InvariantCulture)
                        : p.Value.GetInt64();
                case "doubleValue":
                    return p.Value.ValueKind == JsonValueKind.String
                        ? double.Parse(p.Value.GetString()!, CultureInfo.InvariantCulture)
                        : p.Value.GetDouble();
                case "bytesValue":
                    return p.Value.GetString();
                case "arrayValue":
                    var items = ArrayOf(p.Value, "values").Select(AnyValue).ToArray();
                    return items.All(i => i is string) ? items.Cast<string>().ToArray() : items;
                case "kvlistValue":
                    return string.Join(", ", ArrayOf(p.Value, "values").Select(kv =>
                        $"{(kv.TryGetProperty("key", out var k) ? k.GetString() : null)}={SpanRecord.Format(kv.TryGetProperty("value", out var val) ? AnyValue(val) : null)}"));
            }
        }

        return null;
    }

    private static long Nanos(JsonElement obj, string name)
    {
        if (!obj.TryGetProperty(name, out var v))
        {
            return 0;
        }

        return v.ValueKind == JsonValueKind.String
            ? long.Parse(v.GetString()!, CultureInfo.InvariantCulture)
            : v.GetInt64();
    }

    private static DateTimeOffset ToTime(long unixNanos) =>
        new(DateTime.UnixEpoch.Ticks + (unixNanos / 100), TimeSpan.Zero);

    private static int Enum(JsonElement obj, string name)
    {
        if (!obj.TryGetProperty(name, out var v))
        {
            return 0;
        }

        if (v.ValueKind == JsonValueKind.Number)
        {
            return v.GetInt32();
        }

        // Proto enum names, e.g. SPAN_KIND_SERVER / STATUS_CODE_ERROR. Offset by 1000 to keep the two families apart.
        return v.GetString() switch
        {
            "SPAN_KIND_INTERNAL" => 1,
            "SPAN_KIND_SERVER" => 2,
            "SPAN_KIND_CLIENT" => 3,
            "SPAN_KIND_PRODUCER" => 4,
            "SPAN_KIND_CONSUMER" => 5,
            "STATUS_CODE_OK" => 1001,
            "STATUS_CODE_ERROR" => 1002,
            _ => 0,
        };
    }

    private static ActivityKind Kind(int otlpKind) => otlpKind switch
    {
        2 => ActivityKind.Server,
        3 => ActivityKind.Client,
        4 => ActivityKind.Producer,
        5 => ActivityKind.Consumer,
        _ => ActivityKind.Internal,
    };

    /// <summary>Normalizes an id that may be hex or base64 to lower-case hex. Empty ids become <c>null</c>.</summary>
    internal static string? Id(JsonElement obj, string name, int hexLength)
    {
        if (!obj.TryGetProperty(name, out var v) || v.GetString() is not { Length: > 0 } raw)
        {
            return null;
        }

        return NormalizeId(raw, hexLength);
    }

    internal static string NormalizeId(string raw, int hexLength)
    {
        if (raw.Length <= hexLength && raw.All(Uri.IsHexDigit))
        {
            return raw.ToLowerInvariant().PadLeft(hexLength, '0');
        }

        try
        {
            return Convert.ToHexString(Convert.FromBase64String(raw)).ToLowerInvariant();
        }
        catch (FormatException)
        {
            return raw;
        }
    }
}
