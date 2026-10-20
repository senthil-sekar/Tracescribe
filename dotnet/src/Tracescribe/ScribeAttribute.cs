namespace Tracescribe;

/// <summary>
/// Marks a <c>partial</c> method whose implementation the Tracescribe source generator writes. The generated
/// body opens a <see cref="Scribe"/> operation named by <see cref="Intent"/>, adds parameters marked with
/// <see cref="ScribeContextAttribute"/> as business context, calls your implementation method, and records
/// success, or failure with an inferred failure class when it throws.
/// </summary>
/// <example>
/// <code>
/// public partial class ClaimService
/// {
///     [Scribe("claim.fnol.submit", Expected = "claim accepted with a claim number")]
///     public partial Task&lt;string&gt; SubmitAsync([ScribeContext("policy_id")] string policyId, ClaimRequest request);
///
///     // Your code. Default name: method name + "Core" (before an "Async" suffix).
///     private async Task&lt;string&gt; SubmitCoreAsync(string policyId, ClaimRequest request) { ... }
/// }
/// </code>
/// </example>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false, Inherited = false)]
public sealed class ScribeAttribute : Attribute
{
    /// <summary>Creates the attribute.</summary>
    /// <param name="intent">Stable, dotted, low-cardinality operation name.</param>
    public ScribeAttribute(string intent) => Intent = intent;

    /// <summary>The <c>tracescribe.intent</c> value.</summary>
    public string Intent { get; }

    /// <summary>The <c>tracescribe.outcome.expected</c> value.</summary>
    public string? Expected { get; set; }

    /// <summary>The <c>tracescribe.intent.description</c> value.</summary>
    public string? Description { get; set; }

    /// <summary>
    /// Name of the method that holds your implementation. Defaults to the method name with <c>Core</c>
    /// inserted before an <c>Async</c> suffix (<c>SubmitAsync</c> → <c>SubmitCoreAsync</c>), or appended
    /// otherwise (<c>Rate</c> → <c>RateCore</c>).
    /// </summary>
    public string? Implementation { get; set; }
}

/// <summary>
/// On a parameter of a <see cref="ScribeAttribute"/> method: records the argument as
/// <c>tracescribe.context.&lt;key&gt;</c>. Values are redacted at export like any other context value.
/// </summary>
[AttributeUsage(AttributeTargets.Parameter, AllowMultiple = false, Inherited = false)]
public sealed class ScribeContextAttribute : Attribute
{
    /// <summary>Creates the attribute.</summary>
    /// <param name="key">Context key in <c>snake_case</c>, e.g. <c>claim_id</c>.</param>
    public ScribeContextAttribute(string key) => Key = key;

    /// <summary>The context key.</summary>
    public string Key { get; }
}
