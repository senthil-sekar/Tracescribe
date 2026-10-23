using System.Diagnostics;
using Tracescribe.Digest;
using Tracescribe.SemanticConventions;
using Xunit;

namespace Tracescribe.Tests;

public class ReaderTests
{
    internal const string OtlpJson = """
    {"resourceSpans":[{"resource":{"attributes":[{"key":"service.name","value":{"stringValue":"claims-api"}}]},
      "scopeSpans":[{"scope":{"name":"Tracescribe"},"spans":[
        {"traceId":"5b8efff798038103d269b633813fc60c","spanId":"eee19b7ec3c1b174","parentSpanId":"","name":"claim.fnol.submit",
         "kind":2,"startTimeUnixNano":"1760000000000000000","endTimeUnixNano":"1760000000500000000",
         "attributes":[{"key":"tracescribe.intent","value":{"stringValue":"claim.fnol.submit"}},
                       {"key":"tracescribe.context.claim_id","value":{"stringValue":"CLM-1"}},
                       {"key":"tracescribe.context.reporter_email","value":{"stringValue":"jane@example.com"}},
                       {"key":"retries","value":{"intValue":"3"}},
                       {"key":"tags","value":{"arrayValue":{"values":[{"stringValue":"a"},{"stringValue":"b"}]}}}],
         "status":{"code":2,"message":"held for review"}}]}]},
     {"resource":{"attributes":[{"key":"service.name","value":{"stringValue":"vin-service"}}]},
      "scopeSpans":[{"scope":{"name":"System.Net.Http"},"spans":[
        {"traceId":"5b8efff798038103d269b633813fc60c","spanId":"aaa19b7ec3c1b174","parentSpanId":"eee19b7ec3c1b174","name":"GET /decode",
         "kind":"SPAN_KIND_CLIENT","startTimeUnixNano":1760000000100000000,"endTimeUnixNano":1760000000400000000,
         "attributes":[{"key":"http.response.status_code","value":{"intValue":504}}],
         "events":[{"name":"exception","timeUnixNano":"1760000000400000000","attributes":[
            {"key":"exception.type","value":{"stringValue":"System.Net.Http.HttpRequestException"}},
            {"key":"exception.message","value":{"stringValue":"gateway timeout"}}]}],
         "status":{"code":"STATUS_CODE_ERROR"}}]}]}]}
    """;

    [Fact]
    public void Reads_otlp_json()
    {
        var spans = OtlpJsonReader.Read(OtlpJson);
        Assert.Equal(2, spans.Count);

        var root = spans.Single(s => s.Name == "claim.fnol.submit");
        Assert.Null(root.ParentSpanId);
        Assert.Equal("claims-api", root.Service);
        Assert.Equal("Tracescribe", root.Source);
        Assert.Equal(ActivityKind.Server, root.Kind);
        Assert.Equal(ActivityStatusCode.Error, root.Status);
        Assert.Equal("held for review", root.StatusDescription);
        Assert.Equal(TimeSpan.FromMilliseconds(500), root.Duration);
        Assert.Equal(3L, root.Attributes["retries"]);
        Assert.Equal("a, b", root.Get("tags"));

        var child = spans.Single(s => s.Name == "GET /decode");
        Assert.Equal("eee19b7ec3c1b174", child.ParentSpanId);
        Assert.Equal(ActivityKind.Client, child.Kind);
        Assert.Equal(ActivityStatusCode.Error, child.Status);
        Assert.Equal("vin-service", child.Service);
        Assert.Single(child.Events);
    }

    [Fact]
    public void Digest_of_exported_trace_shows_services_and_redacts()
    {
        var report = Assert.Single(TriageDigest.Build(OtlpJsonReader.Read(OtlpJson)));
        var cause = report.RootCauses[0];
        Assert.Equal("GET /decode", cause.Name);
        Assert.Equal("vin-service", cause.Service);
        Assert.Contains("svc=claims-api", report.Text);
        Assert.Contains("svc=vin-service", report.Text);
        Assert.Contains("reporter_email=[REDACTED", report.Text);
        Assert.DoesNotContain("jane@example.com", report.Text);
    }

    [Fact]
    public void Reads_tempo_style_batches_with_base64_ids_and_json_lines()
    {
        // Tempo returns "batches" and base64 ids; the Collector file exporter writes one request per line.
        const string line1 = """{"batches":[{"instrumentationLibrarySpans":[{"instrumentationLibrary":{"name":"lib"},"spans":[{"traceId":"W47/95gDgQPSabYzgT/GDA==","spanId":"7uGbfsPBsXQ=","name":"a","kind":"SPAN_KIND_SERVER","startTimeUnixNano":"1","endTimeUnixNano":"2"}]}]}]}""";
        const string line2 = """{"resourceSpans":[{"scopeSpans":[{"spans":[{"traceId":"5b8efff798038103d269b633813fc60c","spanId":"0000000000000001","parentSpanId":"eee19b7ec3c1b174","name":"b"}]}]}]}""";
        var spans = OtlpJsonReader.Read(line1 + "\n" + line2 + "\n");

        Assert.Equal(2, spans.Count);
        Assert.Equal("5b8efff798038103d269b633813fc60c", spans[0].TraceId);
        Assert.Equal("eee19b7ec3c1b174", spans[0].SpanId);
        Assert.Equal("lib", spans[0].Source);
        Assert.Equal(ActivityKind.Server, spans[0].Kind);
        Assert.Equal(spans[0].SpanId, spans[1].ParentSpanId);
    }

    internal const string JaegerJson = """
    {"data":[{"traceID":"5b8efff798038103d269b633813fc60c","spans":[
      {"traceID":"5b8efff798038103d269b633813fc60c","spanID":"eee19b7ec3c1b174","operationName":"claim.fnol.submit","references":[],
       "startTime":1760000000000000,"duration":500000,"processID":"p1",
       "tags":[{"key":"span.kind","type":"string","value":"server"},
               {"key":"otel.scope.name","type":"string","value":"Tracescribe"},
               {"key":"tracescribe.intent","type":"string","value":"claim.fnol.submit"},
               {"key":"tracescribe.outcome.status","type":"string","value":"failure"}],
       "logs":[]},
      {"traceID":"5b8efff798038103d269b633813fc60c","spanID":"aaa19b7ec3c1b174","operationName":"execute_tool decode_vin",
       "references":[{"refType":"CHILD_OF","traceID":"5b8efff798038103d269b633813fc60c","spanID":"eee19b7ec3c1b174"}],
       "startTime":1760000000100000,"duration":300000,"processID":"p2",
       "tags":[{"key":"otel.status_code","type":"string","value":"ERROR"},
               {"key":"otel.status_description","type":"string","value":"decoder timeout"},
               {"key":"gen_ai.operation.name","type":"string","value":"execute_tool"},
               {"key":"error","type":"bool","value":true}],
       "logs":[{"timestamp":1760000000400000,"fields":[{"key":"event","type":"string","value":"exception"},
               {"key":"exception.type","type":"string","value":"System.TimeoutException"},
               {"key":"exception.message","type":"string","value":"decoder timeout"}]}]}],
     "processes":{"p1":{"serviceName":"claims-api","tags":[]},"p2":{"serviceName":"claims-agent","tags":[]}}}]}
    """;

    [Fact]
    public void Jaeger_array_attributes_come_back_as_arrays()
    {
        // Jaeger stores array attributes as JSON strings; the conventions' arrays are restored, other strings aren't.
        const string json = """
            {"data":[{"traceID":"5b8efff798038103d269b633813fc60c","spans":[
              {"traceID":"5b8efff798038103d269b633813fc60c","spanID":"eee19b7ec3c1b174","operationName":"claim.fnol.submit","references":[],
               "startTime":1760000000000000,"duration":500000,"processID":"p1",
               "tags":[{"key":"tracescribe.redacted.keys","type":"string","value":"[\"tracescribe.context.reporter_email\"]"},
                       {"key":"note","type":"string","value":"[not json"}],
               "logs":[{"timestamp":1760000000100000,"fields":[{"key":"event","type":"string","value":"tracescribe.decision"},
                       {"key":"tracescribe.decision.name","type":"string","value":"vin.fallback"},
                       {"key":"tracescribe.decision.alternatives","type":"string","value":"[\"retry_decoder\",\"ask_claimant\"]"}]}]}],
             "processes":{"p1":{"serviceName":"claims-api","tags":[]}}}]}
            """;
        var span = Assert.Single(JaegerJsonReader.Read(json));

        Assert.Equal(new[] { "tracescribe.context.reporter_email" }, span.Attributes[TracescribeAttributes.RedactedKeys]);
        Assert.Equal("[not json", span.Attributes["note"]);
        var decision = Assert.Single(span.Events);
        Assert.Equal(new[] { "retry_decoder", "ask_claimant" }, decision.Attributes[TracescribeEvents.DecisionAlternatives]);
        Assert.Contains("alts=[retry_decoder, ask_claimant]", TriageDigest.Render([span]));
    }

    [Fact]
    public void Reads_jaeger_json()
    {
        var spans = JaegerJsonReader.Read(JaegerJson);
        Assert.Equal(2, spans.Count);

        var root = spans.Single(s => s.Name == "claim.fnol.submit");
        Assert.Null(root.ParentSpanId);
        Assert.Equal(ActivityKind.Server, root.Kind);
        Assert.Equal("Tracescribe", root.Source);
        Assert.Equal("claims-api", root.Service);
        Assert.Equal(TimeSpan.FromMilliseconds(500), root.Duration);
        Assert.False(root.Attributes.ContainsKey("span.kind"));

        var tool = spans.Single(s => s.Name == "execute_tool decode_vin");
        Assert.Equal("eee19b7ec3c1b174", tool.ParentSpanId);
        Assert.Equal(ActivityStatusCode.Error, tool.Status);
        Assert.Equal("decoder timeout", tool.StatusDescription);
        Assert.Equal("claims-agent", tool.Service);
        var ex = Assert.Single(tool.Events);
        Assert.Equal("exception", ex.Name);
        Assert.Equal("System.TimeoutException", ex.Attributes["exception.type"]);
    }

    [Fact]
    public void Digest_of_jaeger_trace_ranks_the_tool_failure()
    {
        var report = Assert.Single(TriageDigest.Build(JaegerJsonReader.Read(JaegerJson)));
        Assert.Equal("execute_tool decode_vin", report.RootCauses[0].Name);
        Assert.Contains("exception: System.TimeoutException: decoder timeout", report.Text);
        Assert.Contains("[FAIL] claim.fnol.submit", report.Text);
    }

    [Fact]
    public void Otlp_writer_round_trips_through_the_reader()
    {
        using var t = new TestTrace();
        var cause = new ActivityContext(ActivityTraceId.CreateRandom(), ActivitySpanId.CreateRandom(), ActivityTraceFlags.Recorded);
        using (var root = Scribe.Begin("claim.fnol.submit", expected: "accepted", kind: ActivityKind.Server, source: t.Source,
            links: [TracescribeLinks.Create(cause, LinkReasonValues.CausedBy)]).WithContext("claim_id", "CLM-1"))
        {
            root.Activity!.SetTag("retries", 2).SetTag("ratio", 0.5);
            root.Decide("vin.fallback", "first_vehicle", ["retry", "ask"], "decoder slow");
            using (var child = Scribe.Begin("coverage.check", source: t.Source))
            {
                child.Fail(new TimeoutException("slow"));
            }

            root.Mismatch("held", "vehicle unverified");
        }

        var original = t.Collector.Spans;
        var json = OtlpJsonWriter.Write(original, defaultService: "claims-api", indented: true);
        var read = OtlpJsonReader.Read(json);

        Assert.Equal(original.Count, read.Count);
        foreach (var o in original)
        {
            var r = read.Single(x => x.SpanId == o.SpanId);
            Assert.Equal(o.TraceId, r.TraceId);
            Assert.Equal(o.ParentSpanId, r.ParentSpanId);
            Assert.Equal(o.Name, r.Name);
            Assert.Equal(o.Kind, r.Kind);
            Assert.Equal(o.Status, r.Status);
            Assert.Equal(o.StatusDescription, r.StatusDescription);
            Assert.Equal(o.StartTime, r.StartTime);
            Assert.Equal(o.Duration, r.Duration);
            Assert.Equal("claims-api", r.Service);
            Assert.Equal(o.Events.Count, r.Events.Count);
            Assert.Equal(o.Links.Count, r.Links.Count);
            foreach (var kv in o.Attributes)
            {
                Assert.Equal(SpanRecord.Format(kv.Value), r.Get(kv.Key));
            }
        }

        Assert.Equal(TriageDigest.Render(original).Replace(" svc=claims-api", string.Empty), TriageDigest.Render(read).Replace(" svc=claims-api", string.Empty));
    }

    [Fact]
    public void Digest_redaction_can_be_disabled()
    {
        var spans = OtlpJsonReader.Read(OtlpJson);
        var text = TriageDigest.Render(spans, new TriageDigestOptions { Redactor = null });
        Assert.Contains("jane@example.com", text);
    }
}
