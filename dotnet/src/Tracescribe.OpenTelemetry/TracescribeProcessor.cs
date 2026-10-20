using System.Diagnostics;
using OpenTelemetry;
using OpenTelemetry.Trace;

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

/// <summary>Registration helpers.</summary>
public static class TracerProviderBuilderExtensions
{
    /// <summary>
    /// Listens to the Tracescribe source and adds <see cref="TracescribeProcessor"/>.
    /// Call this before adding exporters.
    /// </summary>
    /// <example>
    /// <code>
    /// builder.Services.AddOpenTelemetry().WithTracing(t => t
    ///     .AddAspNetCoreInstrumentation()
    ///     .AddHttpClientInstrumentation()
    ///     .AddTracescribe()
    ///     .AddOtlpExporter());
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
}
