using System.Globalization;
using Tracescribe.Digest;
using Tracescribe.Query;

namespace Tracescribe.Mcp;

/// <summary>Minimal command-line parser for the CLI mode.</summary>
public sealed class CommandLine
{
    /// <summary>Usage text.</summary>
    public const string Usage = """
        Usage:
          tracescribe-mcp                    Run the MCP server on stdio
          tracescribe-mcp digest <trace-id> [--expand]
          tracescribe-mcp failures [--service <name>] [--minutes <n>] [--kind any|error|semantic] [--limit <n>]
                                     (default window: 60 minutes for Jaeger/Tempo, all data for files)
          tracescribe-mcp logs [--intent <name or prefix*>] [--trace <trace-id>] [--service <name>]
                               [--severity trace|debug|info|warn|error|fatal] [--minutes <n>] [--limit <n>]
          tracescribe-mcp services

        Backend (flags override environment variables):
          --backend jaeger|tempo|azuremonitor|file   TRACESCRIBE_BACKEND (default jaeger)
          --url <base url>              TRACESCRIBE_URL (defaults: http://localhost:16686, :3200,
                                        https://api.loganalytics.azure.com)
          --path <file or directory>    TRACESCRIBE_PATH (file backend)
          --workspace <id>              TRACESCRIBE_WORKSPACE (azuremonitor: Log Analytics workspace id;
                                        signs in with DefaultAzureCredential, e.g. after 'az login')
          --header "Name: value"        TRACESCRIBE_HEADER (e.g. Authorization)
          --logs-url <loki url>         TRACESCRIBE_LOGS_URL (log search with jaeger or tempo)
          --logs-header "Name: value"   TRACESCRIBE_LOGS_HEADER (defaults to --header)
        """;

    /// <summary><c>serve</c>, <c>digest</c>, <c>failures</c>, <c>logs</c>, <c>services</c> or <c>help</c>.</summary>
    public string Command { get; private set; } = "serve";

    /// <summary>Positional argument (trace id for <c>digest</c>).</summary>
    public string? Argument { get; private set; }

    /// <summary>Whether <c>--expand</c> was given.</summary>
    public bool ExpandHealthy { get; private set; }

    /// <summary>Backend settings.</summary>
    public TraceBackendOptions Backend { get; } = TraceBackendOptions.FromEnvironment();

    /// <summary>Failure search settings.</summary>
    public TraceQuery Query { get; private set; } = new();

    /// <summary>Log search settings for <c>logs</c>. Shares <c>--service</c>, <c>--minutes</c> and <c>--limit</c> with <see cref="Query"/>.</summary>
    public LogQuery LogQuery { get; private set; } = new();

    /// <summary>Parse error, if any.</summary>
    public string? Error { get; private set; }

    /// <summary>Parses arguments.</summary>
    public static CommandLine Parse(IReadOnlyList<string> args)
    {
        var cli = new CommandLine();
        var i = 0;
        if (args.Count > 0 && !args[0].StartsWith('-'))
        {
            cli.Command = args[0].ToLowerInvariant();
            i = 1;
            if (cli.Command is not ("digest" or "failures" or "logs" or "services" or "serve" or "help"))
            {
                cli.Error = $"Unknown command '{args[0]}'.";
                return cli;
            }
        }

        int? limit = null;
        for (; i < args.Count; i++)
        {
            var a = args[i];
            string Next() => i + 1 < args.Count ? args[++i] : throw new FormatException($"{a} needs a value.");
            try
            {
                switch (a)
                {
                    case "-h" or "--help":
                        cli.Command = "help";
                        break;
                    case "--backend":
                        cli.Backend.Kind = Next();
                        break;
                    case "--url":
                        cli.Backend.Url = Next();
                        break;
                    case "--path":
                        cli.Backend.Path = Next();
                        cli.Backend.Kind = "file";
                        break;
                    case "--header":
                        cli.Backend.Header = Next();
                        break;
                    case "--workspace":
                        cli.Backend.Workspace = Next();
                        cli.Backend.Kind = "azuremonitor";
                        break;
                    case "--logs-url":
                        cli.Backend.LogsUrl = Next();
                        break;
                    case "--logs-header":
                        cli.Backend.LogsHeader = Next();
                        break;
                    case "--intent":
                        cli.LogQuery = cli.LogQuery with { Intent = Next() };
                        break;
                    case "--trace":
                        cli.LogQuery = cli.LogQuery with { TraceId = Next() };
                        break;
                    case "--severity":
                        var severityText = Next();
                        var severity = LogEntry.ParseSeverity(severityText);
                        if (severity == LogSeverity.Unspecified)
                        {
                            throw new FormatException($"Unknown severity '{severityText}'.");
                        }

                        cli.LogQuery = cli.LogQuery with { MinSeverity = severity };
                        break;
                    case "--expand":
                        cli.ExpandHealthy = true;
                        break;
                    case "--service":
                        cli.Query = cli.Query with { Service = Next() };
                        break;
                    case "--minutes":
                        cli.Query = cli.Query with { Lookback = TimeSpan.FromMinutes(int.Parse(Next(), CultureInfo.InvariantCulture)) };
                        break;
                    case "--all-time":
                        cli.Query = cli.Query with { Lookback = null };
                        break;
                    case "--limit":
                        limit = int.Parse(Next(), CultureInfo.InvariantCulture);
                        cli.Query = cli.Query with { Limit = limit.Value };
                        break;
                    case "--kind":
                        cli.Query = cli.Query with { Kind = Enum.Parse<FailureKind>(Next(), ignoreCase: true) };
                        break;
                    default:
                        if (!a.StartsWith('-') && cli.Argument is null)
                        {
                            cli.Argument = a;
                            break;
                        }

                        cli.Error = $"Unknown option '{a}'.";
                        return cli;
                }
            }
            catch (Exception ex) when (ex is FormatException or ArgumentException or OverflowException)
            {
                cli.Error = ex.Message;
                return cli;
            }
        }

        cli.LogQuery = cli.LogQuery with { Service = cli.Query.Service, Lookback = cli.Query.Lookback, Limit = limit ?? cli.LogQuery.Limit };
        if (cli.Command == "digest" && cli.Argument is null)
        {
            cli.Error = "digest needs a trace id.";
        }

        return cli;
    }
}
