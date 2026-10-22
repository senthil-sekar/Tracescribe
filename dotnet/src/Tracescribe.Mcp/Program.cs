using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Tracescribe.Mcp;
using Tracescribe.Query;

// tracescribe-mcp                         run the MCP server on stdio (default)
// tracescribe-mcp digest <trace-id>       print a digest
// tracescribe-mcp failures [--service s] [--minutes n] [--kind any|error|semantic] [--limit n]
// tracescribe-mcp logs [--intent i] [--trace id] [--service s] [--severity warn] [--minutes n] [--limit n]
// tracescribe-mcp services
// Backend options (or environment): see CommandLine.Usage.

var cli = CommandLine.Parse(args);
if (cli.Error is { } parseError)
{
    Console.Error.WriteLine(parseError);
    Console.Error.WriteLine(CommandLine.Usage);
    return 2;
}

if (cli.Command == "help")
{
    Console.WriteLine(CommandLine.Usage);
    return 0;
}

TriageService triage;
try
{
    if (cli.Backend.Kind.Equals("azuremonitor", StringComparison.OrdinalIgnoreCase) && cli.Backend.Header is null)
    {
        cli.Backend.AccessTokenProvider = new AzureTokenProvider().GetTokenAsync;
    }

    var traces = TraceBackendFactory.Create(cli.Backend);
    triage = new TriageService(traces, logs: TraceBackendFactory.CreateLogs(cli.Backend, traces));
}
catch (ArgumentException ex)
{
    Console.Error.WriteLine(ex.Message);
    return 2;
}

switch (cli.Command)
{
    case "digest":
        Console.WriteLine(await triage.GetDigestAsync(cli.Argument ?? string.Empty, cli.ExpandHealthy));
        return 0;
    case "failures":
        Console.WriteLine(await triage.FindFailuresTextAsync(cli.Query));
        return 0;
    case "logs":
        Console.WriteLine(await triage.FindLogsTextAsync(cli.LogQuery));
        return 0;
    case "services":
        Console.WriteLine(await triage.ListServicesTextAsync());
        return 0;
}

var builder = Host.CreateApplicationBuilder();
builder.Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace); // stdout belongs to MCP
builder.Services.AddSingleton(triage);
builder.Services
    .AddMcpServer(o => o.ServerInfo = new() { Name = "tracescribe", Version = typeof(TriageTools).Assembly.GetName().Version?.ToString() ?? "0.0.0" })
    .WithStdioServerTransport()
    .WithTools<TriageTools>();
await builder.Build().RunAsync();
return 0;
