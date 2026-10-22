using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Tracescribe.Digest;
using Tracescribe.SemanticConventions;

namespace Tracescribe.Query;

/// <summary>
/// Grafana Loki (<c>/loki/api/v1/query_range</c>), default port 3100. Pairs with Jaeger or Tempo, which don't store
/// logs. Expects logs ingested over OTLP (Loki 3+), where attributes become structured metadata with dots replaced by
/// underscores: <c>tracescribe_intent</c>, <c>trace_id</c>, <c>severity_number</c>.
/// </summary>
public sealed class LokiLogBackend : ILogBackend
{
    private readonly HttpQueryClient _client;

    private static readonly HashSet<string> Reserved = new(StringComparer.Ordinal)
    {
        "service_name", "trace_id", "span_id", "severity_text", "severity_number", "detected_level", "observed_timestamp", "flags",
    };

    /// <summary>Creates the backend.</summary>
    public LokiLogBackend(HttpClient http, Uri baseUrl) => _client = new HttpQueryClient(http, baseUrl, "loki");

    /// <inheritdoc />
    public string Name => "loki";

    /// <summary>OTLP attribute name as Loki stores it.</summary>
    internal static string Label(string attribute) => attribute.Replace('.', '_');

    /// <summary>Builds the LogQL query used for a search.</summary>
    internal static string LogQl(LogQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);
        var sb = new StringBuilder();
        sb.Append(query.Service is { } service ? $"{{service_name=\"{Escape(service)}\"}}" : "{service_name=~\".+\"}");
        if (query.IntentValue is { } intent)
        {
            sb.Append(" | ").Append(Label(TracescribeAttributes.Intent))
              .Append(query.IntentIsPrefix ? $"=~\"{Escape(Regex.Escape(intent))}.*\"" : $"=\"{Escape(intent)}\"");
        }

        if (query.TraceId is { } traceId)
        {
            sb.Append(" | trace_id=\"").Append(Escape(traceId.ToLowerInvariant())).Append('"');
        }

        if (query.MinSeverity != LogSeverity.Unspecified)
        {
            sb.Append(" | severity_number >= ").Append((int)query.MinSeverity);
        }

        return sb.ToString();
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<LogEntry>> SearchLogsAsync(LogQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        // A trace id pins the search, so look further back than the default hour when no window is given.
        var (start, end) = HttpQueryClient.Window(query.Lookback ?? (query.TraceId is null ? null : TimeSpan.FromDays(7)));
        var path = string.Create(CultureInfo.InvariantCulture,
            $"loki/api/v1/query_range?query={Uri.EscapeDataString(LogQl(query))}&start={start.ToUnixTimeMilliseconds() * 1_000_000}&end={end.ToUnixTimeMilliseconds() * 1_000_000}&limit={query.Limit}&direction=backward");
        var body = await _client.GetAsync(path, cancellationToken).ConfigureAwait(false);
        return body is null ? [] : Read(body).Where(query.Matches).OrderByDescending(l => l.Timestamp).Take(query.Limit).ToList();
    }

    /// <summary>Reads log records from a Loki <c>streams</c> response.</summary>
    internal static IReadOnlyList<LogEntry> Read(string json)
    {
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("data", out var data)
            || !data.TryGetProperty("result", out var result)
            || result.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var logs = new List<LogEntry>();
        foreach (var stream in result.EnumerateArray())
        {
            var labels = new Dictionary<string, string>(StringComparer.Ordinal);
            if (stream.TryGetProperty("stream", out var s) && s.ValueKind == JsonValueKind.Object)
            {
                foreach (var p in s.EnumerateObject())
                {
                    labels[p.Name] = p.Value.ToString();
                }
            }

            var attributes = new Dictionary<string, object?>(StringComparer.Ordinal);
            foreach (var (k, v) in labels)
            {
                if (!Reserved.Contains(k))
                {
                    attributes[k == Label(TracescribeAttributes.Intent) ? TracescribeAttributes.Intent : k] = v;
                }
            }

            var severity = labels.TryGetValue("severity_number", out var n) && int.TryParse(n, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number)
                ? LogEntry.FromSeverityNumber(number)
                : LogEntry.ParseSeverity(labels.GetValueOrDefault("severity_text") ?? labels.GetValueOrDefault("detected_level"));

            if (!stream.TryGetProperty("values", out var values) || values.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (var value in values.EnumerateArray())
            {
                if (value.ValueKind != JsonValueKind.Array || value.GetArrayLength() < 2
                    || !long.TryParse(value[0].GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var nanos))
                {
                    continue;
                }

                logs.Add(new LogEntry(
                    new DateTimeOffset(DateTime.UnixEpoch.Ticks + (nanos / 100), TimeSpan.Zero),
                    severity,
                    value[1].GetString() ?? string.Empty,
                    labels.GetValueOrDefault("trace_id") is { Length: > 0 } t ? t.ToLowerInvariant() : null,
                    labels.GetValueOrDefault("span_id") is { Length: > 0 } sp ? sp.ToLowerInvariant() : null,
                    attributes)
                {
                    Service = labels.GetValueOrDefault("service_name"),
                });
            }
        }

        return logs;
    }

    private static string Escape(string value) => value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal);
}
