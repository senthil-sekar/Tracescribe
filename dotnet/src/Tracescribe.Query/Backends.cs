using System.Globalization;
using System.Net;
using System.Text.Json;
using Tracescribe.Digest;
using Tracescribe.SemanticConventions;

namespace Tracescribe.Query;

/// <summary>Shared HTTP plumbing for backends.</summary>
public abstract class HttpTraceBackend : ITraceBackend
{
    /// <summary>Creates the backend.</summary>
    /// <param name="http">Client to use; configure auth headers and timeouts on it.</param>
    /// <param name="baseUrl">Base URL of the query API, e.g. <c>http://localhost:16686</c>.</param>
    protected HttpTraceBackend(HttpClient http, Uri baseUrl)
    {
        Http = http ?? throw new ArgumentNullException(nameof(http));
        BaseUrl = baseUrl.AbsoluteUri.EndsWith('/') ? baseUrl : new Uri(baseUrl.AbsoluteUri + "/");
    }

    /// <summary>The HTTP client.</summary>
    protected HttpClient Http { get; }

    /// <summary>Base URL, always ending in a slash.</summary>
    protected Uri BaseUrl { get; }

    /// <inheritdoc />
    public abstract string Name { get; }

    /// <inheritdoc />
    public abstract Task<IReadOnlyList<SpanRecord>> GetTraceAsync(string traceId, CancellationToken cancellationToken = default);

    /// <inheritdoc />
    public abstract Task<IReadOnlyList<FoundTrace>> SearchAsync(TraceQuery query, CancellationToken cancellationToken = default);

    /// <inheritdoc />
    public abstract Task<IReadOnlyList<string>> ListServicesAsync(CancellationToken cancellationToken = default);

    /// <summary>GETs a relative path. Returns <c>null</c> on 404; throws <see cref="TraceBackendException"/> on other failures.</summary>
    protected async Task<string?> GetAsync(string relative, CancellationToken cancellationToken)
    {
        var uri = new Uri(BaseUrl, relative);
        HttpResponseMessage response;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.Accept.ParseAdd("application/json");
            response = await Http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            throw new TraceBackendException($"{Name}: cannot reach {uri.GetLeftPart(UriPartial.Authority)}: {ex.Message}", ex);
        }

        using (response)
        {
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                return null;
            }

            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                var snippet = body.Length > 300 ? body[..300] : body;
                throw new TraceBackendException($"{Name}: {(int)response.StatusCode} {response.ReasonPhrase} from {uri.AbsolutePath}: {snippet}");
            }

            return body;
        }
    }

    /// <summary>Resolves the search window.</summary>
    protected static (DateTimeOffset Start, DateTimeOffset End) Window(TraceQuery query)
    {
        var end = DateTimeOffset.UtcNow;
        return (end - (query.Lookback ?? TimeSpan.FromHours(1)), end);
    }
}

/// <summary>Jaeger query API (<c>/api/traces</c>, <c>/api/services</c>), default port 16686.</summary>
public sealed class JaegerBackend : HttpTraceBackend
{
    /// <summary>Creates the backend.</summary>
    public JaegerBackend(HttpClient http, Uri baseUrl) : base(http, baseUrl)
    {
    }

    /// <inheritdoc />
    public override string Name => "jaeger";

    /// <inheritdoc />
    public override async Task<IReadOnlyList<SpanRecord>> GetTraceAsync(string traceId, CancellationToken cancellationToken = default)
    {
        var body = await GetAsync("api/traces/" + Uri.EscapeDataString(traceId), cancellationToken).ConfigureAwait(false);
        return body is null ? [] : JaegerJsonReader.Read(body);
    }

    /// <inheritdoc />
    public override async Task<IReadOnlyList<FoundTrace>> SearchAsync(TraceQuery query, CancellationToken cancellationToken = default)
    {
        // Jaeger search requires a service, so search each service when none is given.
        var services = query.Service is { } s ? [s] : (await ListServicesAsync(cancellationToken).ConfigureAwait(false)).Take(25).ToList();
        var tagFilters = query.Kind switch
        {
            FailureKind.Error => new[] { """{"error":"true"}""" },
            FailureKind.Semantic => new[] { $$"""{"{{TracescribeAttributes.OutcomeMatched}}":"false"}""" },
            _ => new[] { """{"error":"true"}""", $$"""{"{{TracescribeAttributes.OutcomeMatched}}":"false"}""" },
        };

        var (start, end) = Window(query);
        var found = new Dictionary<string, (DateTimeOffset Start, List<SpanRecord> Spans)>(StringComparer.Ordinal);
        foreach (var service in services)
        {
            foreach (var tags in tagFilters)
            {
                var path = string.Create(CultureInfo.InvariantCulture,
                    $"api/traces?service={Uri.EscapeDataString(service)}&tags={Uri.EscapeDataString(tags)}&start={start.ToUnixTimeMilliseconds() * 1000}&end={end.ToUnixTimeMilliseconds() * 1000}&limit={query.Limit}");
                var body = await GetAsync(path, cancellationToken).ConfigureAwait(false);
                if (body is null)
                {
                    continue;
                }

                foreach (var trace in JaegerJsonReader.Read(body).GroupBy(x => x.TraceId))
                {
                    found.TryAdd(trace.Key, (trace.Min(x => x.StartTime), trace.ToList()));
                }
            }
        }

        return found
            .OrderByDescending(kv => kv.Value.Start)
            .Take(query.Limit)
            .Select(kv => new FoundTrace(kv.Key, kv.Value.Spans))
            .ToList();
    }

    /// <inheritdoc />
    public override async Task<IReadOnlyList<string>> ListServicesAsync(CancellationToken cancellationToken = default)
    {
        var body = await GetAsync("api/services", cancellationToken).ConfigureAwait(false);
        if (body is null)
        {
            return [];
        }

        using var doc = JsonDocument.Parse(body);
        return doc.RootElement.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array
            ? data.EnumerateArray().Select(e => e.GetString()).OfType<string>().Order(StringComparer.Ordinal).ToList()
            : [];
    }
}

/// <summary>Grafana Tempo HTTP API (<c>/api/traces</c>, TraceQL <c>/api/search</c>), default port 3200.</summary>
public sealed class TempoBackend : HttpTraceBackend
{
    /// <summary>Creates the backend.</summary>
    public TempoBackend(HttpClient http, Uri baseUrl) : base(http, baseUrl)
    {
    }

    /// <inheritdoc />
    public override string Name => "tempo";

    /// <inheritdoc />
    public override async Task<IReadOnlyList<SpanRecord>> GetTraceAsync(string traceId, CancellationToken cancellationToken = default)
    {
        var body = await GetAsync("api/traces/" + Uri.EscapeDataString(traceId), cancellationToken).ConfigureAwait(false);
        return body is null ? [] : OtlpJsonReader.Read(body);
    }

    /// <summary>Builds the TraceQL query used for a search. Public for transparency and testing.</summary>
    public static string TraceQl(TraceQuery query)
    {
        var failure = query.Kind switch
        {
            FailureKind.Error => "status = error",
            FailureKind.Semantic => $"span.{TracescribeAttributes.OutcomeMatched} = false",
            _ => $"(status = error || span.{TracescribeAttributes.OutcomeMatched} = false)",
        };
        return query.Service is { } service
            ? $"{{ resource.service.name = \"{service.Replace("\\", "\\\\").Replace("\"", "\\\"")}\" && {failure} }}"
            : $"{{ {failure} }}";
    }

    /// <inheritdoc />
    public override async Task<IReadOnlyList<FoundTrace>> SearchAsync(TraceQuery query, CancellationToken cancellationToken = default)
    {
        var (start, end) = Window(query);
        var path = string.Create(CultureInfo.InvariantCulture,
            $"api/search?q={Uri.EscapeDataString(TraceQl(query))}&start={start.ToUnixTimeSeconds()}&end={end.ToUnixTimeSeconds()}&limit={query.Limit}");
        var body = await GetAsync(path, cancellationToken).ConfigureAwait(false);
        if (body is null)
        {
            return [];
        }

        using var doc = JsonDocument.Parse(body);
        if (!doc.RootElement.TryGetProperty("traces", out var traces) || traces.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return traces.EnumerateArray()
            .Select(t => t.TryGetProperty("traceID", out var id) ? id.GetString() : null)
            .OfType<string>()
            .Select(id => new FoundTrace(id.ToLowerInvariant().PadLeft(32, '0')))
            .Take(query.Limit)
            .ToList();
    }

    /// <inheritdoc />
    public override async Task<IReadOnlyList<string>> ListServicesAsync(CancellationToken cancellationToken = default)
    {
        var body = await GetAsync("api/search/tag/service.name/values", cancellationToken).ConfigureAwait(false);
        if (body is null)
        {
            return [];
        }

        using var doc = JsonDocument.Parse(body);
        return doc.RootElement.TryGetProperty("tagValues", out var values) && values.ValueKind == JsonValueKind.Array
            ? values.EnumerateArray().Select(e => e.ValueKind == JsonValueKind.String ? e.GetString() : e.TryGetProperty("value", out var v) ? v.GetString() : null)
                .OfType<string>().Order(StringComparer.Ordinal).ToList()
            : [];
    }
}

/// <summary>
/// Traces exported to files: OTLP/JSON (Collector file exporter, one request per line) or Jaeger JSON.
/// Point it at a file or a directory of <c>.json</c>/<c>.jsonl</c> files.
/// </summary>
public sealed class FileTraceBackend : ITraceBackend
{
    private readonly string _path;

    /// <summary>Creates the backend.</summary>
    public FileTraceBackend(string path) => _path = path;

    /// <inheritdoc />
    public string Name => "file";

    /// <summary>Reads spans from text in either supported format.</summary>
    public static IReadOnlyList<SpanRecord> ReadText(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return [];
        }

        var otlp = text.Contains("\"resourceSpans\"", StringComparison.Ordinal) || text.Contains("\"batches\"", StringComparison.Ordinal);
        return otlp || !text.Contains("\"traceID\"", StringComparison.Ordinal) ? OtlpJsonReader.Read(text) : JaegerJsonReader.Read(text);
    }

    private IReadOnlyList<SpanRecord> LoadAll()
    {
        IEnumerable<string> files = Directory.Exists(_path)
            ? Directory.EnumerateFiles(_path, "*.json*", SearchOption.AllDirectories)
            : File.Exists(_path) ? [_path] : throw new TraceBackendException($"file: '{_path}' does not exist");
        return files.SelectMany(f => ReadText(File.ReadAllText(f))).ToList();
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<SpanRecord>> GetTraceAsync(string traceId, CancellationToken cancellationToken = default)
    {
        var id = traceId.ToLowerInvariant();
        return Task.FromResult<IReadOnlyList<SpanRecord>>(LoadAll().Where(s => s.TraceId == id).ToList());
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<FoundTrace>> SearchAsync(TraceQuery query, CancellationToken cancellationToken = default)
    {
        var since = query.Lookback is { } lb ? DateTimeOffset.UtcNow - lb : DateTimeOffset.MinValue;
        var result = LoadAll()
            .GroupBy(s => s.TraceId)
            .Where(g => g.Min(s => s.StartTime) >= since)
            .Where(g => query.Service is null || g.Any(s => s.Service == query.Service))
            .Where(g => g.Any(s => Matches(s, query.Kind)))
            .OrderByDescending(g => g.Min(s => s.StartTime))
            .Take(query.Limit)
            .Select(g => new FoundTrace(g.Key, g.ToList()))
            .ToList();
        return Task.FromResult<IReadOnlyList<FoundTrace>>(result);
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<string>> ListServicesAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<string>>(LoadAll().Select(s => s.Service).OfType<string>().Distinct().Order(StringComparer.Ordinal).ToList());

    private static bool Matches(SpanRecord span, FailureKind kind)
    {
        var health = TriageDigest.HealthOf(span);
        return kind switch
        {
            FailureKind.Error => span.Status == System.Diagnostics.ActivityStatusCode.Error,
            FailureKind.Semantic => health == SpanHealth.Semantic,
            _ => health is SpanHealth.Failed or SpanHealth.Semantic or SpanHealth.Partial,
        };
    }
}
