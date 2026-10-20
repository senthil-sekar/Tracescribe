using System.Diagnostics;
using Tracescribe.SemanticConventions;
using Xunit;

namespace Tracescribe.Tests;

public class ScribeTests
{
    [Fact]
    public void Succeed_records_intent_expected_and_success()
    {
        using var t = new TestTrace();
        using (var op = Scribe.Begin("claim.fnol.submit", expected: "claim accepted", source: t.Source))
        {
            op.Succeed("claim CLM-1 issued");
        }

        var s = t.Span("claim.fnol.submit");
        Assert.Equal("claim.fnol.submit", s.Get(TracescribeAttributes.Intent));
        Assert.Equal(TracescribeAttributes.SchemaVersionValue, s.Get(TracescribeAttributes.SchemaVersion));
        Assert.Equal("claim accepted", s.Get(TracescribeAttributes.OutcomeExpected));
        Assert.Equal("claim CLM-1 issued", s.Get(TracescribeAttributes.OutcomeActual));
        Assert.Equal(OutcomeStatusValues.Success, s.Get(TracescribeAttributes.OutcomeStatus));
        Assert.Equal("true", s.Get(TracescribeAttributes.OutcomeMatched));
        Assert.Equal(ActivityStatusCode.Unset, s.Status);
    }

    [Fact]
    public void Dispose_without_outcome_marks_unknown()
    {
        using var t = new TestTrace();
        using (Scribe.Begin("policy.quote.rate", source: t.Source))
        {
        }

        Assert.Equal(OutcomeStatusValues.Unknown, t.Span("policy.quote.rate").Get(TracescribeAttributes.OutcomeStatus));
    }

    [Fact]
    public void Fail_sets_error_status_class_and_exception_event()
    {
        using var t = new TestTrace();
        using (var op = Scribe.Begin("payment.refund.issue", source: t.Source))
        {
            op.Fail(FailureClassValues.Dependency, "bank API returned 503", retryable: true, new InvalidOperationException("boom"));
        }

        var s = t.Span("payment.refund.issue");
        Assert.Equal(ActivityStatusCode.Error, s.Status);
        Assert.Equal(FailureClassValues.Dependency, s.Get(TracescribeAttributes.FailureClass));
        Assert.Equal("bank API returned 503", s.Get(TracescribeAttributes.FailureReason));
        Assert.Equal("true", s.Get(TracescribeAttributes.FailureRetryable));
        Assert.Null(s.Get(TracescribeAttributes.FailureInferred));
        Assert.Equal(OutcomeStatusValues.Failure, s.Get(TracescribeAttributes.OutcomeStatus));
        var ex = Assert.Single(s.Events, e => e.Name == "exception");
        Assert.Equal("System.InvalidOperationException", ex.Attributes["exception.type"]);
    }

    [Fact]
    public void Mismatch_is_a_semantic_failure_without_error_status()
    {
        using var t = new TestTrace();
        using (var op = Scribe.Begin("coverage.check", expected: "coverage on reported vehicle", source: t.Source))
        {
            op.Mismatch("coverage on a different vehicle");
        }

        var s = t.Span("coverage.check");
        Assert.Equal(ActivityStatusCode.Unset, s.Status);
        Assert.Equal(FailureClassValues.Semantic, s.Get(TracescribeAttributes.FailureClass));
        Assert.Equal("false", s.Get(TracescribeAttributes.OutcomeMatched));
        Assert.Equal(Digest.SpanHealth.Semantic, Digest.TriageDigest.HealthOf(s));
    }

    [Fact]
    public void Decisions_and_hints_are_events()
    {
        using var t = new TestTrace();
        using (var op = Scribe.Begin("claim.route", source: t.Source))
        {
            op.Decide("adjuster.assignment", "desk", ["field", "desk"], "low severity")
              .Hint("Check severity model version if routing looks wrong");
            op.Succeed();
        }

        var s = t.Span("claim.route");
        var decision = Assert.Single(s.Events, e => e.Name == TracescribeEvents.Decision);
        Assert.Equal("desk", decision.Attributes[TracescribeEvents.DecisionChosen]);
        Assert.Single(s.Events, e => e.Name == TracescribeEvents.Hint);
    }

    [Fact]
    public void Nested_operations_share_the_trace()
    {
        using var t = new TestTrace();
        using (var outer = Scribe.Begin("outer", source: t.Source))
        {
            using (var inner = Scribe.Begin("inner", source: t.Source))
            {
                inner.Succeed();
            }

            outer.Succeed();
        }

        var outerSpan = t.Span("outer");
        var innerSpan = t.Span("inner");
        Assert.Equal(outerSpan.TraceId, innerSpan.TraceId);
        Assert.Equal(outerSpan.SpanId, innerSpan.ParentSpanId);
    }

    [Fact]
    public void Links_carry_a_reason()
    {
        var context = new ActivityContext(ActivityTraceId.CreateRandom(), ActivitySpanId.CreateRandom(), ActivityTraceFlags.Recorded);
        var link = TracescribeLinks.Create(context, LinkReasonValues.RetryOf);
        Assert.Contains(link.Tags!, kv => kv.Key == TracescribeAttributes.LinkReason && (string?)kv.Value == LinkReasonValues.RetryOf);
    }
}
