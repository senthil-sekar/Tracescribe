using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Net;
using Tracescribe.SemanticConventions;
using Xunit;

namespace Tracescribe.Tests;

public class ExceptionInferenceTests
{
    private sealed class ProviderException(int status) : Exception("provider said no")
    {
        public int Status { get; } = status;
    }

    private sealed class ClaimInputException() : ArgumentException("bad claim input");

    [Fact]
    public void Http_request_exception_uses_status_code()
    {
        var c = FailureClassifier.FromException(new HttpRequestException("rate limited", null, HttpStatusCode.TooManyRequests));
        Assert.Equal(FailureClassValues.Policy, c?.FailureClass);
        Assert.True(c?.Retryable == true);
    }

    [Fact]
    public void Sdk_exception_with_status_property_uses_it()
    {
        Assert.Equal(FailureClassValues.Dependency, FailureClassifier.FromException(new ProviderException(503))?.FailureClass);
        Assert.Equal(FailureClassValues.Policy, FailureClassifier.FromException(new ProviderException(403))?.FailureClass);
    }

    [Fact]
    public void Base_types_are_considered()
    {
        Assert.Equal(FailureClassValues.Data, FailureClassifier.FromException(new ClaimInputException())?.FailureClass);
    }

    [Fact]
    public void HttpClient_timeout_is_a_dependency_timeout()
    {
        var ex = new TaskCanceledException("The request was canceled", new TimeoutException());
        Assert.Equal(FailureClassValues.DependencyTimeout, FailureClassifier.FromException(ex)?.FailureClass);
    }

    [Fact]
    public void Inner_and_aggregate_exceptions_are_unwrapped()
    {
        var wrapped = new Exception("outer", new HttpRequestException("down", null, HttpStatusCode.BadGateway));
        Assert.Equal(FailureClassValues.Dependency, FailureClassifier.FromException(wrapped)?.FailureClass);
        var agg = new AggregateException(new TimeoutException());
        Assert.Equal(FailureClassValues.DependencyTimeout, FailureClassifier.FromException(agg)?.FailureClass);
    }

    [Fact]
    public void Unknown_exception_is_unclassified() =>
        Assert.Null(FailureClassifier.FromException(new Exception("?")));

    [Fact]
    public void Fail_with_exception_infers_class_and_redacts_message()
    {
        using var t = new TestTrace();
        using (var op = Scribe.Begin("customer.notify", source: t.Source))
        {
            op.Fail(new HttpRequestException("send to jane@example.com failed", null, HttpStatusCode.ServiceUnavailable));
        }

        var s = t.Span("customer.notify");
        Assert.Equal(FailureClassValues.Dependency, s.Get(TracescribeAttributes.FailureClass));
        Assert.Equal("true", s.Get(TracescribeAttributes.FailureInferred));
        Assert.Equal("true", s.Get(TracescribeAttributes.FailureRetryable));
        Assert.Equal("send to [REDACTED:email] failed", s.Get(TracescribeAttributes.FailureReason));
        var ex = Assert.Single(s.Events, e => e.Name == "exception");
        Assert.Equal("send to [REDACTED:email] failed", ex.Attributes["exception.message"]);
        Assert.Equal("true", s.Get(TracescribeAttributes.Redacted));
    }

    [Fact]
    public void Metrics_are_recorded_once_per_intent_span()
    {
        var intent = "metrics.test." + Guid.NewGuid().ToString("N");
        var counts = new List<(long Value, Dictionary<string, object?> Tags)>();
        var durations = new List<double>();
        using var listener = new MeterListener
        {
            InstrumentPublished = (inst, l) =>
            {
                if (inst.Meter.Name == TracescribeMetrics.MeterName)
                {
                    l.EnableMeasurementEvents(inst);
                }
            },
        };
        listener.SetMeasurementEventCallback<long>((inst, value, tags, _) =>
        {
            var d = tags.ToArray().ToDictionary(k => k.Key, k => k.Value);
            if (inst.Name == TracescribeMetrics.OperationCount && (string?)d.GetValueOrDefault(TracescribeAttributes.Intent) == intent)
            {
                lock (counts)
                {
                    counts.Add((value, d));
                }
            }
        });
        listener.SetMeasurementEventCallback<double>((inst, value, tags, _) =>
        {
            var d = tags.ToArray().ToDictionary(k => k.Key, k => k.Value);
            if (inst.Name == TracescribeMetrics.OperationDuration && (string?)d.GetValueOrDefault(TracescribeAttributes.Intent) == intent)
            {
                lock (durations)
                {
                    durations.Add(value);
                }
            }
        });
        listener.Start();

        using var t = new TestTrace();
        Activity? captured;
        using (var op = Scribe.Begin(intent, source: t.Source))
        {
            captured = op.Activity;
            op.Fail(FailureClassValues.Validation, "nope");
        }

        new TracescribeEnricher().OnEnd(captured!); // a second enricher must not double count

        var count = Assert.Single(counts);
        Assert.Equal(1, count.Value);
        Assert.Equal(OutcomeStatusValues.Failure, count.Tags[TracescribeAttributes.OutcomeStatus]);
        Assert.Equal(FailureClassValues.Validation, count.Tags[TracescribeAttributes.FailureClass]);
        Assert.Single(durations);
    }
}
