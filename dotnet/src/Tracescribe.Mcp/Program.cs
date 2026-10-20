using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Tracescribe.Mcp;
using Tracescribe.Query;

// tracescribe-mcp                         run the MCP server on stdio (default)
// tracescribe-mcp digest <trace-id>       print a digest
// tracescribe-mcp failures [--service s] [--minutes n] [--kind any|error|semantic] [--limit n]
// tracescribe-mcp services
// Backend options (or environment): --backend jaeger|tempo|file (TRACESCRIBE_BACKEND), --url (TRACESCRIBE_URL),
//                                   --path (TRACESCRIBE_PATH), --header "Name: value" (TRACESCRIBE_HEADER)

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
    triage = new TriageService(TraceBackendFactory.Create(cli.Backend));
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
