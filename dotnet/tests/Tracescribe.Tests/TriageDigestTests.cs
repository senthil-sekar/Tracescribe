using System.Diagnostics;
using Tracescribe.Digest;
using Tracescribe.SemanticConventions;
using Xunit;

namespace Tracescribe.Tests;

public class TriageDigestTests
{
    [Fact]
    public void Ranks_deepest_failure_first_then_semantic_failures()
    {
        using var t = new TestTrace();
        using (var root = Scribe.Begin("claim.fnol.submit", source: t.Source))
        {
            using (var tool = t.Source.StartActivity("execute_tool decode_vin")!)
            {
                tool.RecordExceptionEvent(new TimeoutException("decoder slow"));
                tool.SetStatus(ActivityStatusCode.Error, "decoder slow");
            }

            using (var cov = Scribe.Begin("coverage.check", source: t.Source))
            {
                cov.Mismatch("wrong vehicle");
            }

            root.Fail(FailureClassValues.Validation, "held for review");
        }

        var report = Assert.Single(TriageDigest.Build(t.Collector.Spans));
        Assert.Equal(3, report.SpanCount);
        Assert.Equal(2, report.FailedCount);
        Assert.Equal(1, report.SemanticCount);

        Assert.Equal(2, report.RootCauses.Count);
        Assert.Equal("execute_tool decode_vin", report.RootCauses[0].Name);
        Assert.Equal(FailureClassValues.DependencyTimeout, report.RootCauses[0].FailureClass);
        Assert.True(report.RootCauses[0].Inferred);
        Assert.Equal("claim.fnol.submit > execute_tool decode_vin", report.RootCauses[0].Path);
        Assert.Equal("coverage.check", report.RootCauses[1].Name);
        Assert.Equal(SpanHealth.Semantic, report.RootCauses[1].Health);
    }

    [Fact]
    public void Collapses_healthy_subtrees_and_repeated_leaves()
    {
        using var t = new TestTrace();
        using (var root = Scribe.Begin("root", source: t.Source))
        {
            using (var healthy = Scribe.Begin("policy.lookup", source: t.Source))
            {
                for (var i = 0; i < 4; i++)
                {
                    using var _ = t.Source.StartActivity("cache.get");
                }

                healthy.Succeed();
            }

            for (var i = 0; i < 3; i++)
            {
                using var _ = t.Source.StartActivity("db.query");
            }

            using (var bad = Scribe.Begin("rate", source: t.Source))
            {
                bad.Fail(FailureClassValues.Data, "missing garaging zip");
            }

            root.Fail(FailureClassValues.Data, "quote failed");
        }

        var text = TriageDigest.Render(t.Collector.Spans);
        Assert.Contains("[OK] policy.lookup", text);
        Assert.Contains("(+4 healthy spans)", text);
        Assert.DoesNotContain("cache.get", text);
        Assert.Contains("[OK] db.query x3", text);
        Assert.Contains("[FAIL] rate", text);
        Assert.Contains("reason: missing garaging zip", text);
    }

    [Fact]
    public void Expand_healthy_renders_everything()
    {
        using var t = new TestTrace();
        using (var root = Scribe.Begin("root", source: t.Source))
        {
            using (t.Source.StartActivity("child"))
            {
            }

            root.Succeed();
        }

        var text = TriageDigest.Render(t.Collector.Spans, new TriageDigestOptions { ExpandHealthy = true });
        Assert.Contains("child", text);
        Assert.Contains("ROOT CAUSE CANDIDATES: none", text);
    }

    [Fact]
    public void Root_cause_context_includes_inherited_business_context()
    {
        using var t = new TestTrace();
        using (var root = Scribe.Begin("root", source: t.Source).WithContext("claim_id", "CLM-9"))
        {
            using (var child = Scribe.Begin("child", source: t.Source).WithContext("vin", "V1"))
            {
                child.Fail(FailureClassValues.Data, "bad vin");
            }

            root.Fail(FailureClassValues.Data, "failed");
        }

        var cause = Assert.Single(TriageDigest.Build(t.Collector.Spans)).RootCauses[0];
        Assert.Equal("CLM-9", cause.Context["claim_id"]);
        Assert.Equal("V1", cause.Context["vin"]);
    }

    [Fact]
    public void Orphan_spans_are_rendered_as_roots()
    {
        using var t = new TestTrace();
        var remoteParent = new ActivityContext(ActivityTraceId.CreateRandom(), ActivitySpanId.CreateRandom(), ActivityTraceFlags.Recorded);
        using (var a = t.Source.StartActivity("consumer.process", ActivityKind.Consumer, remoteParent)!)
        {
            a.SetStatus(ActivityStatusCode.Error, "poison message");
        }

        var text = TriageDigest.Render(t.Collector.Spans);
        Assert.Contains("[FAIL] consumer.process", text);
        Assert.Contains("(parent not captured)", text);
    }
}
