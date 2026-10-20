using System.Diagnostics;
using Tracescribe.SemanticConventions;
using Xunit;

namespace Tracescribe.Tests;

public class EnricherTests
{
    [Fact]
    public void Infers_timeout_from_exception_event()
    {
        using var t = new TestTrace();
        using (var a = t.Source.StartActivity("execute_tool decode_vin")!)
        {
            a.RecordExceptionEvent(new TimeoutException("slow"));
            a.SetStatus(ActivityStatusCode.Error, "slow");
        }

        var s = t.Span("execute_tool decode_vin");
        Assert.Equal(FailureClassValues.DependencyTimeout, s.Get(TracescribeAttributes.FailureClass));
        Assert.Equal("true", s.Get(TracescribeAttributes.FailureInferred));
        Assert.Equal("true", s.Get(TracescribeAttributes.FailureRetryable));
        Assert.Equal("slow", s.Get(TracescribeAttributes.FailureReason));
    }

    [Theory]
    [InlineData(503, FailureClassValues.Dependency, "true")]
    [InlineData(500, FailureClassValues.Dependency, "false")]
    [InlineData(429, FailureClassValues.Policy, "true")]
    [InlineData(403, FailureClassValues.Policy, "false")]
    [InlineData(422, FailureClassValues.Validation, "false")]
    public void Infers_from_http_status(int status, string expectedClass, string expectedRetryable)
    {
        using var t = new TestTrace();
        using (var a = t.Source.StartActivity("POST /rate")!)
        {
            a.SetTag("http.response.status_code", status);
            a.SetStatus(ActivityStatusCode.Error);
        }

        var s = t.Span("POST /rate");
        Assert.Equal(expectedClass, s.Get(TracescribeAttributes.FailureClass));
        Assert.Equal(expectedRetryable, s.Get(TracescribeAttributes.FailureRetryable));
    }

    [Fact]
    public void Failing_tool_call_without_other_signals_is_a_dependency_failure()
    {
        using var t = new TestTrace();
        using (var a = t.Source.StartActivity("execute_tool lookup_policy")!)
        {
            a.SetTag(GenAiAttributes.OperationName, "execute_tool");
            a.SetStatus(ActivityStatusCode.Error);
        }

        Assert.Equal(FailureClassValues.Dependency, t.Span("execute_tool lookup_policy").Get(TracescribeAttributes.FailureClass));
    }

    [Fact]
    public void Does_not_override_explicit_failure_class()
    {
        using var t = new TestTrace();
        using (var a = t.Source.StartActivity("op")!)
        {
            a.RecordFailure(FailureClassValues.Configuration, "flag off", exception: new TimeoutException());
        }

        var s = t.Span("op");
        Assert.Equal(FailureClassValues.Configuration, s.Get(TracescribeAttributes.FailureClass));
        Assert.Null(s.Get(TracescribeAttributes.FailureInferred));
    }

    [Fact]
    public void Healthy_spans_are_left_alone()
    {
        using var t = new TestTrace();
        using (t.Source.StartActivity("cache.get"))
        {
        }

        var s = t.Span("cache.get");
        Assert.Null(s.Get(TracescribeAttributes.FailureClass));
        Assert.Null(s.Get(TracescribeAttributes.OutcomeStatus));
    }

    [Fact]
    public void Redacts_context_values()
    {
        using var t = new TestTrace();
        using (var a = t.Source.StartActivity("op")!)
        {
            a.SetContext("claim_id", "CLM-1");
            a.SetContext("ssn", "anything");
            a.SetContext("note", "call 555-123-4567 or mail a@b.com");
        }

        var s = t.Span("op");
        Assert.Equal("CLM-1", s.Get(TracescribeAttributes.Context("claim_id")));
        Assert.Equal("[REDACTED]", s.Get(TracescribeAttributes.Context("ssn")));
        Assert.Equal("call [REDACTED:phone] or mail [REDACTED:email]", s.Get(TracescribeAttributes.Context("note")));
        Assert.Equal("true", s.Get(TracescribeAttributes.Redacted));
        Assert.DoesNotContain("claim_id", s.Get(TracescribeAttributes.RedactedKeys));
    }

    [Fact]
    public void Redaction_can_be_disabled()
    {
        using var t = new TestTrace(new TracescribeEnricher(new TracescribeEnricherOptions { Redactor = null }));
        using (var a = t.Source.StartActivity("op")!)
        {
            a.SetContext("email", "a@b.com");
        }

        Assert.Equal("a@b.com", t.Span("op").Get(TracescribeAttributes.Context("email")));
    }
}
