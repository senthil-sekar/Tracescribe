using System.Diagnostics;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using Microsoft.Extensions.Logging;
using OpenTelemetry;
using OpenTelemetry.Exporter;
using OpenTelemetry.Logs;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Tracescribe.OpenTelemetry;
using Tracescribe.SemanticConventions;
using Xunit;

namespace Tracescribe.Backends.Tests;

/// <summary>
/// Starts Jaeger, Tempo and Loki once for the test run, then sends them one claim-intake scenario over OTLP:
/// traces to Jaeger and Tempo, logs to Loki. The scenario matches the ClaimIntake sample: a VIN-decoder tool
/// times out, the code falls back to the wrong vehicle and the coverage check silently matches it.
/// </summary>
public sealed class BackendFixture : IAsyncLifetime
{
    private const string AgentSourceName = "Tracescribe.Backends.Tests.Agent";
    private IContainer? _jaeger;
    private IContainer? _tempo;
    private IContainer? _loki;

    /// <summary>Unique per run so searches only see this run's data.</summary>
    public string Service { get; } = "claims-api-it-" + Guid.NewGuid().ToString("N")[..8];

    public string TraceId { get; private set; } = string.Empty;

    public Uri JaegerUrl { get; private set; } = null!;

    public Uri TempoUrl { get; private set; } = null!;

    public Uri LokiUrl { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        if (!BackendFactAttribute.DockerAvailable)
        {
            return;
        }

        _jaeger = new ContainerBuilder("jaegertracing/all-in-one:1.76.0")
            .WithPortBinding(16686, true)
            .WithPortBinding(4318, true)
            .WithWaitStrategy(Wait.ForUnixContainer().UntilHttpRequestIsSucceeded(r => r.ForPort(16686).ForPath("/")))
            .Build();

        _tempo = new ContainerBuilder("grafana/tempo:2.9.0")
            .WithResourceMapping(new FileInfo(Path.Combine(AppContext.BaseDirectory, "tempo.yaml")), "/etc/tempo/")
            .WithCommand("-config.file=/etc/tempo/tempo.yaml")
            .WithPortBinding(3200, true)
            .WithPortBinding(4318, true)
            .WithWaitStrategy(Wait.ForUnixContainer().UntilHttpRequestIsSucceeded(r => r.ForPort(3200).ForPath("/ready")))
            .Build();

        _loki = new ContainerBuilder("grafana/loki:3.5.7")
            .WithPortBinding(3100, true)
            .WithWaitStrategy(Wait.ForUnixContainer().UntilHttpRequestIsSucceeded(r => r.ForPort(3100).ForPath("/ready")))
            .Build();

        await Task.WhenAll(_jaeger.StartAsync(), _tempo.StartAsync(), _loki.StartAsync());

        JaegerUrl = Url(_jaeger, 16686);
        TempoUrl = Url(_tempo, 3200);
        LokiUrl = Url(_loki, 3100);
        TraceId = EmitScenario();
    }

    public async Task DisposeAsync()
    {
        foreach (var container in new[] { _jaeger, _tempo, _loki })
        {
            if (container is not null)
            {
                await container.DisposeAsync();
            }
        }
    }

    private static Uri Url(IContainer container, int port) => new($"http://{container.Hostname}:{container.GetMappedPublicPort(port)}");

    private string EmitScenario()
    {
        var resource = ResourceBuilder.CreateEmpty().AddService(Service);
        using var agentSource = new ActivitySource(AgentSourceName);
        var tracer = Sdk.CreateTracerProviderBuilder()
            .SetResourceBuilder(resource)
            .AddSource(AgentSourceName)
            .AddTracescribe()
            .AddOtlpExporter("jaeger", o => Otlp(o, new Uri(Url(_jaeger!, 4318), "v1/traces")))
            .AddOtlpExporter("tempo", o => Otlp(o, new Uri(Url(_tempo!, 4318), "v1/traces")))
            .Build();
        var loggerFactory = LoggerFactory.Create(b => b.AddOpenTelemetry(o =>
        {
            o.SetResourceBuilder(resource);
            o.IncludeFormattedMessage = true;
            o.AddTracescribe();
            o.AddOtlpExporter(e => Otlp(e, new Uri(LokiUrl, "otlp/v1/logs")));
        }));
        var logger = loggerFactory.CreateLogger("claims");

        string traceId;
        using (var submit = Scribe.Begin("claim.fnol.submit", expected: "claim accepted with a claim number", kind: ActivityKind.Server)
            .WithContext("claim_id", "CLM-2026-004211")
            .WithContext("policy_id", "PA-7781203")
            .WithContext("reporter_email", "jane.doe@example.com"))
        {
            traceId = submit.Activity!.TraceId.ToHexString();
            logger.LogInformation("Claim received from {Reporter}", "jane.doe@example.com");

            using (var agent = agentSource.StartActivity("invoke_agent claims-assistant"))
            {
                agent?.SetTag(GenAiAttributes.OperationName, "invoke_agent");
                using var tool = agentSource.StartActivity("execute_tool decode_vin");
                tool?.SetTag(GenAiAttributes.OperationName, "execute_tool");
                var ex = new TimeoutException("VIN decoder did not respond within 30ms");
                tool?.AddException(ex);
                tool?.SetStatus(ActivityStatusCode.Error, ex.Message);
                logger.LogWarning("VIN decoder timed out, using first vehicle on policy");
            }

            submit.Decide("vin.fallback", chosen: "first_vehicle_on_policy", alternatives: ["retry_decoder", "ask_claimant"],
                rationale: "decoder timed out; avoid blocking claimant");

            using (var coverage = Scribe.Begin("coverage.check", expected: "collision coverage active on the reported vehicle"))
            {
                coverage.Mismatch("collision coverage active on the first vehicle; reported vehicle was the second car",
                    "coverage evaluated against fallback VIN, not the damaged vehicle");
            }

            submit.Fail(FailureClassValues.Validation, "claim held for manual review: vehicle could not be verified", retryable: false);
        }

        tracer.ForceFlush();
        tracer.Dispose();
        loggerFactory.Dispose(); // flushes the batched log export
        return traceId;
    }

    private static void Otlp(OtlpExporterOptions options, Uri endpoint)
    {
        options.Endpoint = endpoint;
        options.Protocol = OtlpExportProtocol.HttpProtobuf;
        options.ExportProcessorType = ExportProcessorType.Simple;
    }

    /// <summary>Retries until the backend has indexed the data, or fails with the last observation.</summary>
    public static async Task<T> Eventually<T>(Func<Task<T>> probe, Func<T, bool> done, string what)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(90);
        T last;
        while (true)
        {
            last = await probe();
            if (done(last) || DateTime.UtcNow > deadline)
            {
                break;
            }

            await Task.Delay(TimeSpan.FromSeconds(2));
        }

        Assert.True(done(last), $"{what} did not show up within 90s. Last result:\n{last}");
        return last;
    }
}

[CollectionDefinition(Name)]
public sealed class BackendCollection : ICollectionFixture<BackendFixture>
{
    public const string Name = "backends";
}

/// <summary>
/// A fact that needs Docker. Skipped when Docker isn't reachable, unless TRACESCRIBE_REQUIRE_BACKENDS=1, in which
/// case it runs and fails, so CI can't silently skip it.
/// </summary>
public sealed class BackendFactAttribute : FactAttribute
{
    public BackendFactAttribute()
    {
        if (!DockerAvailable && Environment.GetEnvironmentVariable("TRACESCRIBE_REQUIRE_BACKENDS") != "1")
        {
            Skip = "Docker is not available. Set TRACESCRIBE_REQUIRE_BACKENDS=1 to fail instead of skipping.";
        }
    }

    public static bool DockerAvailable =>
        Environment.GetEnvironmentVariable("DOCKER_HOST") is { Length: > 0 }
        || File.Exists("/var/run/docker.sock")
        || (OperatingSystem.IsWindows() && File.Exists(@"\\.\pipe\docker_engine"));
}
