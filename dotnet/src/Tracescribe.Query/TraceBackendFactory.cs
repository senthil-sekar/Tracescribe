namespace Tracescribe.Query;

/// <summary>Settings that select and configure a backend.</summary>
public sealed class TraceBackendOptions
{
    /// <summary><c>jaeger</c>, <c>tempo</c> or <c>file</c>.</summary>
    public string Kind { get; set; } = "jaeger";

    /// <summary>Base URL for HTTP backends.</summary>
    public string? Url { get; set; }

    /// <summary>File or directory for the <c>file</c> backend.</summary>
    public string? Path { get; set; }

    /// <summary>Optional request header, as <c>Name: value</c>, e.g. <c>Authorization: Basic ...</c>.</summary>
    public string? Header { get; set; }

    /// <summary>HTTP timeout.</summary>
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Reads settings from environment variables: <c>TRACESCRIBE_BACKEND</c>, <c>TRACESCRIBE_URL</c>,
    /// <c>TRACESCRIBE_PATH</c> and <c>TRACESCRIBE_HEADER</c>.
    /// </summary>
    public static TraceBackendOptions FromEnvironment() => new()
    {
        Kind = Environment.GetEnvironmentVariable("TRACESCRIBE_BACKEND") ?? "jaeger",
        Url = Environment.GetEnvironmentVariable("TRACESCRIBE_URL"),
        Path = Environment.GetEnvironmentVariable("TRACESCRIBE_PATH"),
        Header = Environment.GetEnvironmentVariable("TRACESCRIBE_HEADER"),
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
            _ => throw new ArgumentException($"Unknown backend '{options.Kind}'. Use jaeger, tempo or file."),
        };

        var client = http ?? new HttpClient { Timeout = options.Timeout };
        if (options.Header is { } header)
        {
            var colon = header.IndexOf(':');
            if (colon <= 0)
            {
                throw new ArgumentException("The header must look like 'Name: value'.");
            }

            client.DefaultRequestHeaders.TryAddWithoutValidation(header[..colon].Trim(), header[(colon + 1)..].Trim());
        }

        var url = new Uri(options.Url ?? defaultUrl);
        return kind == "jaeger" ? new JaegerBackend(client, url) : new TempoBackend(client, url);
    }
}
