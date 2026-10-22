using System.Diagnostics;
using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Xml;
using Tracescribe.Digest;
using Tracescribe.SemanticConventions;

namespace Tracescribe.Query;

/// <summary>
/// Azure Monitor / Application Insights through the Log Analytics query API (KQL over a workspace). Reads the
/// workspace-based tables the Azure Monitor OpenTelemetry exporter writes: <c>AppRequests</c> and
/// <c>AppDependencies</c> (spans), <c>AppExceptions</c> (exception events) and <c>AppTraces</c> (logs, and span
/// events such as <c>tracescribe.decision</c>). Span attributes are read from <c>Properties</c>.
/// </summary>
/// <remarks>
/// Authenticates with a Microsoft Entra bearer token for <see cref="DefaultScope"/>. The caller needs the
/// Log Analytics Reader role (or <c>Microsoft.OperationalInsights/workspaces/query/read</c>) on the workspace.
/// </remarks>
public sealed class AzureMonitorBackend : HttpTraceBackend, ILogBackend
{
    /// <summary>Public cloud Log Analytics query endpoint.</summary>
    public const string DefaultUrl = "https://api.loganalytics.azure.com";

    /// <summary>Token scope for the Log Analytics query API.</summary>
    public const string DefaultScope = "https://api.loganalytics.io/.default";

    private const string SpanTables = "AppRequests, AppDependencies";
    private readonly string _workspaceId;
    private readonly Func<CancellationToken, ValueTask<string>>? _accessToken;

    /// <summary>Creates the backend.</summary>
    /// <param name="http">Client to use.</param>
    /// <param name="baseUrl">Query endpoint, e.g. <see cref="DefaultUrl"/> or a sovereign cloud's.</param>
    /// <param name="workspaceId">Log Analytics workspace id (GUID).</param>
    /// <param name="accessToken">Returns a bearer token for <see cref="DefaultScope"/>; called per request so tokens can refresh.
    /// <c>null</c> when <paramref name="http"/> already sends an <c>Authorization</c> header.</param>
    public AzureMonitorBackend(HttpClient http, Uri baseUrl, string workspaceId, Func<CancellationToken, ValueTask<string>>? accessToken = null)
        : base(http, baseUrl)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceId);
        _workspaceId = workspaceId.Trim();
        _accessToken = accessToken;
    }

    /// <inheritdoc />
    public override string Name => "azuremonitor";

    /// <summary>How far back <see cref="GetTraceAsync"/> and trace-id log searches look. Default 7 days.</summary>
    public TimeSpan TraceWindow { get; init; } = TimeSpan.FromDays(7);

    /// <summary>KQL that finds failing operation ids. Public for transparency and testing.</summary>
    public static string SearchKql(TraceQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);
        var semantic = $"tostring(Properties[{Quote(TracescribeAttributes.OutcomeMatched)}]) =~ \"false\" or tostring(Properties[{Quote(TracescribeAttributes.FailureClass)}]) == \"semantic\"";
        var failure = query.Kind switch
        {
            FailureKind.Error => "Success == false",
            FailureKind.Semantic => semantic,
            _ => $"Success == false or {semantic}",
        };

        var sb = new StringBuilder();
        sb.Append("union ").Append(SpanTables).Append('\n');
        if (query.Service is { } service)
        {
            sb.Append("| where AppRoleName == ").Append(Quote(service)).Append('\n');
        }

        sb.Append("| where ").Append(failure).Append('\n')
          .Append("| summarize Start = min(TimeGenerated) by OperationId\n")
          .Append("| top ").Append(query.Limit.ToString(CultureInfo.InvariantCulture)).Append(" by Start desc");
        return sb.ToString();
    }

    /// <summary>KQL that loads every span, exception and span event of the given traces. Public for transparency and testing.</summary>
    public static string TraceKql(IEnumerable<string> traceIds)
    {
        var ids = string.Join(", ", traceIds.Select(id => Quote(id.Trim().ToLowerInvariant())));
        return $"""
            union {SpanTables}, AppExceptions, AppTraces
            | where OperationId in ({ids})
            | where Type != "AppTraces" or Message startswith_cs "{TracescribeAttributes.Prefix}"
            | project Type, TimeGenerated, Id, ParentId, OperationId, Name, DurationMs, Success, ResultCode, DependencyType, AppRoleName, Properties, Message, ExceptionType, OuterMessage
            """.ReplaceLineEndings("\n");
    }

    /// <summary>KQL that searches logs in <c>AppTraces</c>. Public for transparency and testing.</summary>
    public static string LogsKql(LogQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);
        var sb = new StringBuilder("AppTraces\n");
        if (query.Service is { } service)
        {
            sb.Append("| where AppRoleName == ").Append(Quote(service)).Append('\n');
        }

        if (query.TraceId is { } traceId)
        {
            sb.Append("| where OperationId == ").Append(Quote(traceId.Trim().ToLowerInvariant())).Append('\n');
        }

        if (query.IntentValue is { } intent)
        {
            sb.Append("| where tostring(Properties[").Append(Quote(TracescribeAttributes.Intent)).Append("]) ")
              .Append(query.IntentIsPrefix ? "startswith_cs " : "== ").Append(Quote(intent)).Append('\n');
        }

        if (query.MinSeverity != LogSeverity.Unspecified)
        {
            sb.Append("| where SeverityLevel >= ").Append(ToSeverityLevel(query.MinSeverity).ToString(CultureInfo.InvariantCulture)).Append('\n');
        }

        // Span events (tracescribe.decision, tracescribe.hint) also land in AppTraces; they belong to the digest.
        sb.Append("| where Message !startswith_cs ").Append(Quote(TracescribeAttributes.Prefix)).Append('\n')
          .Append("| top ").Append(query.Limit.ToString(CultureInfo.InvariantCulture)).Append(" by TimeGenerated desc\n")
          .Append("| project TimeGenerated, Message, SeverityLevel, OperationId, ParentId, AppRoleName, Properties");
        return sb.ToString();
    }

    /// <inheritdoc />
    public override async Task<IReadOnlyList<SpanRecord>> GetTraceAsync(string traceId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(traceId);
        var rows = await QueryAsync(TraceKql([traceId]), TraceWindow, cancellationToken).ConfigureAwait(false);
        return ToSpans(rows);
    }

    /// <inheritdoc />
    public override async Task<IReadOnlyList<FoundTrace>> SearchAsync(TraceQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        var lookback = query.Lookback ?? TimeSpan.FromHours(1);
        var found = await QueryAsync(SearchKql(query), lookback, cancellationToken).ConfigureAwait(false);
        var ids = found.Select(r => r.String("OperationId")).OfType<string>().Select(id => id.ToLowerInvariant()).ToList();
        if (ids.Count == 0)
        {
            return [];
        }

        // One more query loads all of them, rather than one per trace: the query API is rate limited.
        var spans = ToSpans(await QueryAsync(TraceKql(ids), lookback + TimeSpan.FromHours(1), cancellationToken).ConfigureAwait(false))
            .ToLookup(s => s.TraceId, StringComparer.Ordinal);
        return ids.Select(id => new FoundTrace(id, spans[id].ToList() is { Count: > 0 } list ? list : null)).ToList();
    }

    /// <inheritdoc />
    public override async Task<IReadOnlyList<string>> ListServicesAsync(CancellationToken cancellationToken = default)
    {
        var rows = await QueryAsync($"union {SpanTables} | where isnotempty(AppRoleName) | distinct AppRoleName", TraceWindow, cancellationToken).ConfigureAwait(false);
        return rows.Select(r => r.String("AppRoleName")).OfType<string>().Order(StringComparer.Ordinal).ToList();
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<LogEntry>> SearchLogsAsync(LogQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        var lookback = query.Lookback ?? (query.TraceId is null ? TimeSpan.FromHours(1) : TraceWindow);
        var rows = await QueryAsync(LogsKql(query), lookback, cancellationToken).ConfigureAwait(false);
        return rows.Select(r => new LogEntry(
                r.Time("TimeGenerated"),
                FromSeverityLevel(r.Int("SeverityLevel")),
                r.String("Message") ?? string.Empty,
                r.String("OperationId")?.ToLowerInvariant(),
                ParentSpan(r),
                r.Properties())
            {
                Service = r.String("AppRoleName"),
            })
            .ToList();
    }

    /// <inheritdoc />
    protected override async ValueTask AuthorizeAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (_accessToken is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await _accessToken(cancellationToken).ConfigureAwait(false));
        }
    }

    private async Task<IReadOnlyList<Row>> QueryAsync(string kql, TimeSpan timespan, CancellationToken cancellationToken)
    {
        var request = JsonSerializer.Serialize(new Dictionary<string, string>
        {
            ["query"] = kql,
            ["timespan"] = XmlConvert.ToString(timespan),
        });
        var body = await PostJsonAsync($"v1/workspaces/{Uri.EscapeDataString(_workspaceId)}/query", request, cancellationToken).ConfigureAwait(false)
            ?? throw new TraceBackendException($"{Name}: workspace '{_workspaceId}' was not found");
        return Row.ReadTable(body, Name);
    }

    private static List<SpanRecord> ToSpans(IReadOnlyList<Row> rows)
    {
        var spanRows = rows.Where(r => r.String("Type") is "AppRequests" or "AppDependencies").ToList();
        var events = new Dictionary<string, List<SpanEventRecord>>(StringComparer.Ordinal);
        foreach (var r in rows)
        {
            if (ParentSpan(r) is not { } spanId)
            {
                continue;
            }

            SpanEventRecord? e = r.String("Type") switch
            {
                "AppExceptions" => new SpanEventRecord("exception", r.Time("TimeGenerated"), new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["exception.type"] = r.String("ExceptionType"),
                    ["exception.message"] = r.String("OuterMessage") ?? r.String("Message"),
                }),
                "AppTraces" => new SpanEventRecord(r.String("Message") ?? string.Empty, r.Time("TimeGenerated"), r.Properties()),
                _ => null,
            };
            if (e is not null)
            {
                (events.TryGetValue(spanId, out var list) ? list : events[spanId] = []).Add(e);
            }
        }

        return spanRows.Select(r =>
        {
            var spanId = r.String("Id")?.ToLowerInvariant() ?? string.Empty;
            var success = r.Bool("Success") ?? true;
            var resultCode = r.String("ResultCode");
            var type = r.String("DependencyType");
            var isRequest = r.String("Type") == "AppRequests";
            return new SpanRecord(
                r.String("OperationId")?.ToLowerInvariant() ?? string.Empty,
                spanId,
                ParentSpan(r),
                r.String("Name") ?? string.Empty,
                isRequest ? "request" : type ?? string.Empty,
                isRequest ? ActivityKind.Server
                    : type == "InProc" ? ActivityKind.Internal
                    : type?.StartsWith("Queue Message", StringComparison.Ordinal) == true ? ActivityKind.Producer
                    : ActivityKind.Client,
                r.Time("TimeGenerated"),
                TimeSpan.FromMilliseconds(r.Double("DurationMs") ?? 0),
                success ? ActivityStatusCode.Unset : ActivityStatusCode.Error,
                !success && !string.IsNullOrEmpty(resultCode) && resultCode != "0" ? "result code " + resultCode : null,
                r.Properties(),
                events.TryGetValue(spanId, out var spanEvents) ? spanEvents.OrderBy(e => e.Timestamp).ToList() : [],
                [])
            {
                Service = r.String("AppRoleName"),
            };
        }).ToList();
    }

    /// <summary>The span a row belongs to. A root's <c>ParentId</c> is empty or equal to its operation id.</summary>
    private static string? ParentSpan(Row r)
    {
        var parent = r.String("ParentId");
        return string.IsNullOrEmpty(parent) || string.Equals(parent, r.String("OperationId"), StringComparison.OrdinalIgnoreCase)
            ? null
            : parent.ToLowerInvariant();
    }

    private static int ToSeverityLevel(LogSeverity severity) => severity switch
    {
        >= LogSeverity.Fatal => 4,
        >= LogSeverity.Error => 3,
        >= LogSeverity.Warn => 2,
        >= LogSeverity.Info => 1,
        _ => 0,
    };

    private static LogSeverity FromSeverityLevel(int? level) => level switch
    {
        0 => LogSeverity.Debug,
        1 => LogSeverity.Info,
        2 => LogSeverity.Warn,
        3 => LogSeverity.Error,
        4 => LogSeverity.Fatal,
        _ => LogSeverity.Unspecified,
    };

    /// <summary>A KQL string literal.</summary>
    internal static string Quote(string value) =>
        "\"" + value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal)
            .Replace("\n", "\\n", StringComparison.Ordinal).Replace("\r", "\\r", StringComparison.Ordinal) + "\"";

    /// <summary>One result row, keyed by column name.</summary>
    private sealed class Row(Dictionary<string, JsonElement> cells)
    {
        public static IReadOnlyList<Row> ReadTable(string body, string backend)
        {
            using var doc = JsonDocument.Parse(body);
            if (!doc.RootElement.TryGetProperty("tables", out var tables) || tables.ValueKind != JsonValueKind.Array || tables.GetArrayLength() == 0)
            {
                var error = doc.RootElement.TryGetProperty("error", out var e) && e.TryGetProperty("message", out var m) ? m.GetString() : "no result table";
                throw new TraceBackendException($"{backend}: query failed: {error}");
            }

            var table = tables[0];
            var columns = table.GetProperty("columns").EnumerateArray().Select(c => c.GetProperty("name").GetString() ?? string.Empty).ToList();
            var rows = new List<Row>();
            foreach (var row in table.GetProperty("rows").EnumerateArray())
            {
                var cells = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
                var i = 0;
                foreach (var cell in row.EnumerateArray())
                {
                    if (i < columns.Count)
                    {
                        cells[columns[i++]] = cell.Clone();
                    }
                }

                rows.Add(new Row(cells));
            }

            return rows;
        }

        public string? String(string column) => cells.TryGetValue(column, out var v)
            ? v.ValueKind switch
            {
                JsonValueKind.String => v.GetString() is { Length: > 0 } s ? s : null,
                JsonValueKind.Null or JsonValueKind.Undefined => null,
                _ => v.ToString(),
            }
            : null;

        public DateTimeOffset Time(string column) =>
            String(column) is { } s && DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var t)
                ? t.ToUniversalTime()
                : DateTimeOffset.MinValue;

        public double? Double(string column) =>
            String(column) is { } s && double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : null;

        public int? Int(string column) => Double(column) is { } d ? (int)d : null;

        public bool? Bool(string column) => String(column) switch
        {
            var s when string.Equals(s, "true", StringComparison.OrdinalIgnoreCase) => true,
            var s when string.Equals(s, "false", StringComparison.OrdinalIgnoreCase) => false,
            _ => null,
        };

        /// <summary>
        /// The <c>Properties</c> column (dynamic, returned as a JSON string). Values are strings; the exporter writes
        /// booleans as <c>True</c>/<c>False</c>, so those are normalized to <see cref="bool"/>.
        /// </summary>
        public Dictionary<string, object?> Properties()
        {
            var d = new Dictionary<string, object?>(StringComparer.Ordinal);
            if (!cells.TryGetValue("Properties", out var cell))
            {
                return d;
            }

            JsonDocument? owned = null;
            try
            {
                var obj = cell;
                if (cell.ValueKind == JsonValueKind.String)
                {
                    if (cell.GetString() is not { Length: > 0 } text)
                    {
                        return d;
                    }

                    owned = JsonDocument.Parse(text);
                    obj = owned.RootElement;
                }

                if (obj.ValueKind != JsonValueKind.Object)
                {
                    return d;
                }

                foreach (var p in obj.EnumerateObject())
                {
                    var value = p.Value.ValueKind == JsonValueKind.String ? p.Value.GetString() : p.Value.ToString();
                    d[p.Name] = value switch
                    {
                        _ when string.Equals(value, "true", StringComparison.OrdinalIgnoreCase) => true,
                        _ when string.Equals(value, "false", StringComparison.OrdinalIgnoreCase) => false,
                        _ => value,
                    };
                }
            }
            catch (JsonException)
            {
                // Not JSON: leave properties empty rather than fail the whole trace.
            }
            finally
            {
                owned?.Dispose();
            }

            return d;
        }
    }
}
