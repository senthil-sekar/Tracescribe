using System.Diagnostics;
using System.Globalization;
using System.Text.Json;

namespace Tracescribe.Digest;

/// <summary>
/// Reads spans from the Jaeger query API JSON format (<c>GET /api/traces/{id}</c> and
/// <c>GET /api/traces?service=...</c>), mapping Jaeger's representation of OTel data back to
/// <see cref="SpanRecord"/>: <c>otel.status_code</c>, <c>span.kind</c>, <c>otel.scope.name</c>,
/// logs as events and <c>FOLLOWS_FROM</c> references as links.
/// </summary>
public static class JaegerJsonReader
{
    private static readonly HashSet<string> ConsumedTags = new(StringComparer.Ordinal)
    {
        "span.kind", "otel.status_code", "otel.status_description", "otel.scope.name", "otel.library.name",
        "otel.scope.version", "otel.library.version", "internal.span.format",
    };

    // Jaeger has no array type: it stores array attributes as a JSON string such as ["a","b"]. Restore the
    // arrays the conventions define; other strings are left alone, since one may legitimately start with '['.
    private static readonly HashSet<string> ArrayTags = new(StringComparer.Ordinal)
    {
        Tracescribe.SemanticConventions.TracescribeEvents.DecisionAlternatives,
        Tracescribe.SemanticConventions.TracescribeAttributes.RedactedKeys,
    };

    /// <summary>Reads every span of every trace in a Jaeger API response.</summary>
    public static IReadOnlyList<SpanRecord> Read(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var spans = new List<SpanRecord>();
        var root = doc.RootElement;
        List<JsonElement> traces = root.ValueKind switch
        {
            JsonValueKind.Object when root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array => data.EnumerateArray().ToList(),
            JsonValueKind.Array => root.EnumerateArray().ToList(),
            JsonValueKind.Object => [root],
            _ => [],
        };

        foreach (var trace in traces)
        {
            var services = new Dictionary<string, string>(StringComparer.Ordinal);
            if (trace.TryGetProperty("processes", out var processes) && processes.ValueKind == JsonValueKind.Object)
            {
                foreach (var p in processes.EnumerateObject())
                {
                    if (p.Value.TryGetProperty("serviceName", out var sn) && sn.GetString() is { } name)
                    {
                        services[p.Name] = name;
                    }
                }
            }

            if (!trace.TryGetProperty("spans", out var spanArray) || spanArray.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (var span in spanArray.EnumerateArray())
            {
                spans.Add(ReadSpan(span, services));
            }
        }

        return spans;
    }

    private static SpanRecord ReadSpan(JsonElement span, Dictionary<string, string> services)
    {
        var traceId = OtlpJsonReader.NormalizeId(Str(span, "traceID") ?? string.Empty, 32);
        var tags = Tags(span, "tags");

        string? parent = null;
        var links = new List<SpanLinkRecord>();
        if (span.TryGetProperty("references", out var refs) && refs.ValueKind == JsonValueKind.Array)
        {
            foreach (var r in refs.EnumerateArray())
            {
                var refTrace = OtlpJsonReader.NormalizeId(Str(r, "traceID") ?? string.Empty, 32);
                var refSpan = OtlpJsonReader.NormalizeId(Str(r, "spanID") ?? string.Empty, 16);
                if (parent is null && Str(r, "refType") == "CHILD_OF" && refTrace == traceId)
                {
                    parent = refSpan;
                }
                else
                {
                    links.Add(new SpanLinkRecord(refTrace, refSpan, new Dictionary<string, object?>()));
                }
            }
        }

        var status = ActivityStatusCode.Unset;
        var statusCode = SpanRecord.Format(tags.GetValueOrDefault("otel.status_code"));
        if (statusCode == "ERROR" || tags.GetValueOrDefault("error") is true || SpanRecord.Format(tags.GetValueOrDefault("error")) == "true")
        {
            status = ActivityStatusCode.Error;
        }
        else if (statusCode == "OK")
        {
            status = ActivityStatusCode.Ok;
        }

        var kind = SpanRecord.Format(tags.GetValueOrDefault("span.kind")) switch
        {
            "server" => ActivityKind.Server,
            "client" => ActivityKind.Client,
            "producer" => ActivityKind.Producer,
            "consumer" => ActivityKind.Consumer,
            _ => ActivityKind.Internal,
        };

        var scope = SpanRecord.Format(tags.GetValueOrDefault("otel.scope.name") ?? tags.GetValueOrDefault("otel.library.name")) ?? string.Empty;
        var statusDescription = SpanRecord.Format(tags.GetValueOrDefault("otel.status_description"));

        var events = new List<SpanEventRecord>();
        if (span.TryGetProperty("logs", out var logs) && logs.ValueKind == JsonValueKind.Array)
        {
            foreach (var log in logs.EnumerateArray())
            {
                var fields = Tags(log, "fields");
                var name = SpanRecord.Format(fields.GetValueOrDefault("event")) ?? "log";
                fields.Remove("event");
                events.Add(new SpanEventRecord(name, Micros(Long(log, "timestamp")), fields));
            }
        }

        foreach (var consumed in ConsumedTags)
        {
            tags.Remove(consumed);
        }

        var start = Long(span, "startTime");
        var processId = Str(span, "processID");
        return new SpanRecord(
            traceId,
            OtlpJsonReader.NormalizeId(Str(span, "spanID") ?? string.Empty, 16),
            parent,
            Str(span, "operationName") ?? string.Empty,
            scope,
            kind,
            Micros(start),
            TimeSpan.FromTicks(Long(span, "duration") * 10),
            status,
            statusDescription,
            tags,
            events,
            links)
        {
            Service = processId is not null && services.TryGetValue(processId, out var svc) ? svc : null,
        };
    }

    private static Dictionary<string, object?> Tags(JsonElement obj, string name)
    {
        var d = new Dictionary<string, object?>(StringComparer.Ordinal);
        if (!obj.TryGetProperty(name, out var arr) || arr.ValueKind != JsonValueKind.Array)
        {
            return d;
        }

        foreach (var t in arr.EnumerateArray())
        {
            if (Str(t, "key") is not { } key || !t.TryGetProperty("value", out var v))
            {
                continue;
            }

            d[key] = v.ValueKind switch
            {
                JsonValueKind.String when ArrayTags.Contains(key) => StringArray(v.GetString()),
                JsonValueKind.String => v.GetString(),
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                JsonValueKind.Number when v.TryGetInt64(out var l) => l,
                JsonValueKind.Number => v.GetDouble(),
                _ => v.GetRawText(),
            };
        }

        return d;
    }

    private static object? StringArray(string? json)
    {
        if (json is not { Length: > 1 } || json[0] != '[')
        {
            return json;
        }

        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.ValueKind == JsonValueKind.Array
                ? doc.RootElement.EnumerateArray().Select(e => e.ValueKind == JsonValueKind.String ? e.GetString() ?? string.Empty : e.GetRawText()).ToArray()
                : json;
        }
        catch (JsonException)
        {
            return json;
        }
    }

    private static string? Str(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static long Long(JsonElement obj, string name)
    {
        if (!obj.TryGetProperty(name, out var v))
        {
            return 0;
        }

        return v.ValueKind == JsonValueKind.String ? long.Parse(v.GetString()!, CultureInfo.InvariantCulture) : v.GetInt64();
    }

    private static DateTimeOffset Micros(long unixMicros) => new(DateTime.UnixEpoch.Ticks + (unixMicros * 10), TimeSpan.Zero);
}
