namespace Tracescribe.Query;

/// <summary>Settings that select and configure a backend.</summary>
public sealed class TraceBackendOptions
{
    /// <summary><c>jaeger</c>, <c>tempo</c>, <c>azuremonitor</c> or <c>file</c>.</summary>
    public string Kind { get; set; } = "jaeger";

    /// <summary>Base URL for HTTP backends.</summary>
    public string? Url { get; set; }

    /// <summary>File or directory for the <c>file</c> backend.</summary>
    public string? Path { get; set; }

    /// <summary>Optional request header, as <c>Name: value</c>, e.g. <c>Authorization: Basic ...</c>.</summary>
    public string? Header { get; set; }

    /// <summary>Log Analytics workspace id for the <c>azuremonitor</c> backend.</summary>
    public string? Workspace { get; set; }

    /// <summary>
    /// Bearer token source for the <c>azuremonitor</c> backend, called per request. The MCP server plugs in
    /// <c>DefaultAzureCredential</c>; not needed when <see cref="Header"/> carries an <c>Authorization</c> header.
    /// </summary>
    public Func<CancellationToken, ValueTask<string>>? AccessTokenProvider { get; set; }

    /// <summary>Grafana Loki base URL, for log search alongside <c>jaeger</c> or <c>tempo</c>.</summary>
    public string? LogsUrl { get; set; }

    /// <summary>Request header for Loki, as <c>Name: value</c>; defaults to <see cref="Header"/>.</summary>
    public string? LogsHeader { get; set; }

    /// <summary>HTTP timeout.</summary>
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Reads settings from environment variables: <c>TRACESCRIBE_BACKEND</c>, <c>TRACESCRIBE_URL</c>,
    /// <c>TRACESCRIBE_PATH</c>, <c>TRACESCRIBE_HEADER</c>, <c>TRACESCRIBE_WORKSPACE</c>, <c>TRACESCRIBE_LOGS_URL</c>
    /// and <c>TRACESCRIBE_LOGS_HEADER</c>.
    /// </summary>
    public static TraceBackendOptions FromEnvironment() => new()
    {
        Kind = Environment.GetEnvironmentVariable("TRACESCRIBE_BACKEND") ?? "jaeger",
        Url = Environment.GetEnvironmentVariable("TRACESCRIBE_URL"),
        Path = Environment.GetEnvironmentVariable("TRACESCRIBE_PATH"),
        Header = Environment.GetEnvironmentVariable("TRACESCRIBE_HEADER"),
        Workspace = Environment.GetEnvironmentVariable("TRACESCRIBE_WORKSPACE"),
        LogsUrl = Environment.GetEnvironmentVariable("TRACESCRIBE_LOGS_URL"),
        LogsHeader = Environment.GetEnvironmentVariable("TRACESCRIBE_LOGS_HEADER"),
    };
}

/// <summary>Creates backends from <see cref="TraceBackendOptions"/>.</summary>
public static class TraceBackendFactory
{
    /// <summary>Creates the configured backend.</summary>
    /// <exception cref="ArgumentException">The settings are incomplete or name an unknown backend.</exception>
    public static ITraceBackend Create(TraceBackendOptions options, HttpClient? http = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        var kind = options.Kind.Trim().ToLowerInvariant();
        if (kind == "file")
        {
            return new FileTraceBackend(options.Path ?? throw new ArgumentException("The file backend needs a path (TRACESCRIBE_PATH or --path)."));
        }

        var defaultUrl = kind switch
        {
            "jaeger" => "http://localhost:16686",
            "tempo" => "http://localhost:3200",
            "azuremonitor" => AzureMonitorBackend.DefaultUrl,
            _ => throw new ArgumentException($"Unknown backend '{options.Kind}'. Use jaeger, tempo, azuremonitor or file."),
        };

        var client = http ?? new HttpClient { Timeout = options.Timeout };
        AddHeader(client, options.Header);
        var url = new Uri(options.Url ?? defaultUrl);
        if (kind == "azuremonitor")
        {
            var workspace = options.Workspace ?? throw new ArgumentException("The azuremonitor backend needs a workspace id (TRACESCRIBE_WORKSPACE or --workspace).");
            if (options.AccessTokenProvider is null && options.Header is null)
            {
                throw new ArgumentException("The azuremonitor backend needs credentials: an access token provider or an Authorization header.");
            }

            return new AzureMonitorBackend(client, url, workspace, options.AccessTokenProvider);
        }

        return kind == "jaeger" ? new JaegerBackend(client, url) : new TempoBackend(client, url);
    }

    /// <summary>
    /// Creates the log backend that goes with <paramref name="traces"/>: the trace backend itself when it also stores
    /// logs (file, Azure Monitor), otherwise Loki when <see cref="TraceBackendOptions.LogsUrl"/> is set. Returns
    /// <c>null</c> when log search isn't available.
    /// </summary>
    public static ILogBackend? CreateLogs(TraceBackendOptions options, ITraceBackend traces, HttpClient? http = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (traces is ILogBackend logs)
        {
            return logs;
        }

        if (options.LogsUrl is not { Length: > 0 } logsUrl)
        {
            return null;
        }

        var client = http ?? new HttpClient { Timeout = options.Timeout };
        AddHeader(client, options.LogsHeader ?? options.Header);
        return new LokiLogBackend(client, new Uri(logsUrl));
    }

    private static void AddHeader(HttpClient client, string? header)
    {
        if (header is null)
        {
            return;
        }

        var colon = header.IndexOf(':');
        if (colon <= 0)
        {
            throw new ArgumentException("The header must look like 'Name: value'.");
        }

        client.DefaultRequestHeaders.TryAddWithoutValidation(header[..colon].Trim(), header[(colon + 1)..].Trim());
    }
}
