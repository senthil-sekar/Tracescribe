using System.Diagnostics;
using OpenTelemetry;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;
using Tracescribe.SemanticConventions;

namespace Tracescribe.OpenTelemetry;

/// <summary>
/// Span processor that applies <see cref="TracescribeEnricher"/> to every span before export.
/// Register it before your exporter so exported spans carry the enrichment.
/// </summary>
public sealed class TracescribeProcessor : BaseProcessor<Activity>
{
    private readonly TracescribeEnricher _enricher;

    /// <summary>Creates the processor.</summary>
    public TracescribeProcessor(TracescribeEnricherOptions? options = null) =>
        _enricher = new TracescribeEnricher(options);

    /// <inheritdoc />
    public override void OnEnd(Activity data) => _enricher.OnEnd(data);
}

/// <summary>Registration helpers for traces, metrics and logs.</summary>
public static class TracescribeOpenTelemetryExtensions
{
    /// <summary>
    /// Listens to the Tracescribe source and adds <see cref="TracescribeProcessor"/>.
    /// Call this before adding exporters.
    /// </summary>
    /// <example>
    /// <code>
    /// builder.Services.AddOpenTelemetry()
    ///     .WithTracing(t => t.AddAspNetCoreInstrumentation().AddTracescribe().AddOtlpExporter())
    ///     .WithMetrics(m => m.AddTracescribe().AddOtlpExporter());
    /// builder.Logging.AddOpenTelemetry(o => { o.AddTracescribe(); o.AddOtlpExporter(); });
    /// </code>
    /// </example>
    public static TracerProviderBuilder AddTracescribe(
        this TracerProviderBuilder builder,
        Action<TracescribeEnricherOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        var options = new TracescribeEnricherOptions();
        configure?.Invoke(options);
        return builder.AddSource(Scribe.SourceName).AddProcessor(new TracescribeProcessor(options));
    }

    /// <summary>Adds the Tracescribe meter (<c>tracescribe.operation.count</c>, <c>tracescribe.operation.duration</c>).</summary>
    public static MeterProviderBuilder AddTracescribe(this MeterProviderBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        return builder.AddMeter(TracescribeMetrics.MeterName);
    }

    /// <summary>Adds <see cref="TracescribeLogProcessor"/>. Call this before adding exporters.</summary>
    public static OpenTelemetryLoggerOptions AddTracescribe(
        this OpenTelemetryLoggerOptions options,
        Action<TracescribeLogOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        var logOptions = new TracescribeLogOptions();
        configure?.Invoke(logOptions);
        return options.AddProcessor(new TracescribeLogProcessor(logOptions));
    }
}
