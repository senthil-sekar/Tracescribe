using System.Diagnostics;
using Tracescribe;

namespace ClaimIntake;

/// <summary>
/// Shows the source generator: declare a partial method with [Scribe] and write the implementation in
/// LookupCoreAsync. The generated LookupAsync opens the "policy.lookup" operation, records policy_id as
/// context, and marks the outcome (with an inferred failure class if the implementation throws).
/// </summary>
public partial class PolicyService
{
    private static readonly ActivitySource Cache = new("ClaimIntake.Cache");

    [Scribe("policy.lookup", Expected = "active policy found")]
    public partial Task<IReadOnlyList<string>> LookupAsync([ScribeContext("policy_id")] string policyId, CancellationToken cancellationToken);

    private async Task<IReadOnlyList<string>> LookupCoreAsync(string policyId, CancellationToken cancellationToken)
    {
        foreach (var key in new[] { "policy", "drivers", "vehicles", "coverages", "discounts" })
        {
            using var _ = Cache.StartActivity("cache.get")?.SetTag("cache.key", key);
            await Task.Delay(3, cancellationToken);
        }

        return ["1HGCM82633A004352", "2T1BURHE0JC034511"];
    }
}
