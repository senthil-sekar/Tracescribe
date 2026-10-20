using System.Diagnostics;
using Microsoft.Extensions.Logging;
using OpenTelemetry;
using OpenTelemetry.Logs;
using OpenTelemetry.Trace;
using Tracescribe.OpenTelemetry;
using Tracescribe.SemanticConventions;
using Xunit;

namespace Tracescribe.Integrations.Tests;

public class OpenTelemetryTests
{
    private sealed class CaptureSpans : BaseProcessor<Activity>
    {
        public List<Activity> Spans { get; } = new();

        public override void OnEnd(Activity data)
        {
            lock (Spans)
            {
                Spans.Add(data);
            }
        }
    }

    private sealed class CaptureLogs : BaseProcessor<LogRecord>
    {
        public List<(string? Message, Dictionary<string, object?> Attributes)> Logs { get; } = new();

        public override void OnEnd(LogRecord data)
        {
            var attrs = (data.Attributes ?? []).ToDictionary(kv => kv.Key, kv => kv.Value);
            lock (Logs)
            {
                Logs.Add((data.FormattedMessage, attrs));
            }
        }
    }

    [Fact]
    public void Tracer_provider_enriches_spans_before_later_processors()
    {
        var source = new ActivitySource("Integrations." + Guid.NewGuid().ToString("N"));
        var capture = new CaptureSpans();
        using (Sdk.CreateTracerProviderBuilder()
            .AddSource(source.Name)
            .AddTracescribe()
            .AddProcessor(capture)
            .Build())
        {
            using (var a = source.StartActivity("POST /rate")!)
            {
                a.SetTag("http.response.status_code", 429);
                a.SetStatus(ActivityStatusCode.Error);
            }
        }

        var span = Assert.Single(capture.Spans);
        Assert.Equal(FailureClassValues.Policy, span.GetTagItem(TracescribeAttributes.FailureClass));
        Assert.True(span.GetTagItem(TracescribeAttributes.FailureRetryable) is true);
    }

    [Fact]
    public void Log_records_get_intent_and_redaction()
    {
        var capture = new CaptureLogs();
        using var tracer = Sdk.CreateTracerProviderBuilder().AddTracescribe().Build();
        using var factory = LoggerFactory.Create(b => b.AddOpenTelemetry(o =>
        {
            o.IncludeFormattedMessage = true;
            o.AddTracescribe();
            o.AddProcessor(capture);
        }));
        var logger = factory.CreateLogger("claims");

        using (var op = Scribe.Begin("claim.fnol.submit"))
        {
            logger.LogWarning("Could not reach adjuster {Email} for policy {PolicyId}", "adj@insurer.com", "PA-1");
            op.Succeed();
        }

        logger.LogInformation("Outside any operation, call 555-123-4567");

        Assert.Equal(2, capture.Logs.Count);
        var inside = capture.Logs[0];
        Assert.Equal("Could not reach adjuster [REDACTED:email] for policy PA-1", inside.Message);
        Assert.Equal("claim.fnol.submit", inside.Attributes[TracescribeAttributes.Intent]);
        Assert.Equal("[REDACTED]", inside.Attributes["Email"]);
        Assert.Equal("PA-1", inside.Attributes["PolicyId"]);
        Assert.True(inside.Attributes[TracescribeAttributes.Redacted] is true);

        var outside = capture.Logs[1];
        Assert.Equal("Outside any operation, call [REDACTED:phone]", outside.Message);
        Assert.False(outside.Attributes.ContainsKey(TracescribeAttributes.Intent));
    }
}
