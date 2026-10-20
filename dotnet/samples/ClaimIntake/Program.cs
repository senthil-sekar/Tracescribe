// Tracescribe sample: first notice of loss (FNOL) claim intake.
//
// Simulates a claim submission where an AI agent's VIN-decoding tool times out, the code falls
// back to the VIN on the policy, and the coverage check then silently matches the wrong vehicle.
// No exception reaches the caller for that last step, so ordinary error alerting would miss it.
//
// The program collects the spans in memory and prints the Tracescribe triage digest that an AI
// agent would read. Run: dotnet run --project samples/ClaimIntake

using System.Diagnostics;
using Tracescribe;
using Tracescribe.Digest;
using Tracescribe.SemanticConventions;

var agentSource = new ActivitySource("ClaimIntake.Agent");
var cacheSource = new ActivitySource("ClaimIntake.Cache");

using var collector = new TraceCollector(s => s.Name is Scribe.SourceName || s.Name.StartsWith("ClaimIntake", StringComparison.Ordinal));

ActivityTraceId traceId;
using (var submit = Scribe.Begin("claim.fnol.submit", expected: "claim accepted with a claim number", kind: ActivityKind.Server)
    .WithContext("claim_id", "CLM-2026-004211")
    .WithContext("policy_id", "PA-7781203")
    .WithContext("state", "CA")
    .WithContext("reporter_email", "jane.doe@example.com"))
{
    traceId = submit.Activity!.TraceId;

    using (var policy = Scribe.Begin("policy.lookup", expected: "active policy found"))
    {
        foreach (var key in new[] { "policy", "drivers", "vehicles", "coverages", "discounts" })
        {
            using var _ = cacheSource.StartActivity("cache.get")?.SetTag("cache.key", key);
            await Task.Delay(3);
        }

        policy.Succeed("policy active, 2 vehicles");
    }

    string vin;
    using (var agent = agentSource.StartActivity("invoke_agent claims-assistant"))
    {
        agent?.SetTag(GenAiAttributes.OperationName, "invoke_agent");
        agent?.SetTag(GenAiAttributes.AgentName, "claims-assistant");

        using (var tool = agentSource.StartActivity("execute_tool decode_vin"))
        {
            tool?.SetTag(GenAiAttributes.OperationName, "execute_tool");
            tool?.SetTag(GenAiAttributes.ToolName, "decode_vin");
            await Task.Delay(40);
            var ex = new TimeoutException("VIN decoder did not respond within 30ms");
            tool?.RecordExceptionEvent(ex);
            tool?.SetStatus(ActivityStatusCode.Error, ex.Message);
            // Note: no failure class set here. The enricher infers dependency.timeout from the exception.
        }

        vin = "1HGCM82633A004352"; // first vehicle on the policy
        submit.Decide("vin.fallback", chosen: "first_vehicle_on_policy",
            alternatives: ["retry_decoder", "ask_claimant"],
            rationale: "decoder timed out; avoid blocking claimant");
    }

    using (var coverage = Scribe.Begin("coverage.check", expected: "collision coverage active on the reported vehicle")
        .WithContext("vin", vin)
        .Hint("Mismatch here usually means the VIN fallback picked the wrong vehicle on a multi-vehicle policy"))
    {
        await Task.Delay(5);
        coverage.Mismatch(actual: "collision coverage active on 1HGCM82633A004352; reported vehicle was the second car",
            reason: "coverage evaluated against fallback VIN, not the damaged vehicle");
    }

    submit.Fail(FailureClassValues.Validation, "claim held for manual review: vehicle could not be verified", retryable: false,
        actual: "claim held, no claim number issued");
}

var trace = collector.GetTrace(traceId);
Console.WriteLine(TriageDigest.Render(trace));
