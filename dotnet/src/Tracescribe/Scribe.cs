using System.Diagnostics;
using Tracescribe.SemanticConventions;

namespace Tracescribe;

/// <summary>
/// Entry point for triage-ready instrumentation. Wraps a business operation in a span that
/// carries intent, expected outcome, decisions and the final outcome.
/// </summary>
/// <example>
/// <code>
/// using var op = Scribe.Begin("claim.fnol.submit", expected: "claim accepted with claim number")
///     .WithContext("claim_id", claimId);
/// try
/// {
///     var number = await SubmitAsync();
///     op.Succeed($"claim number {number} issued");
/// }
/// catch (TimeoutException ex)
/// {
///     op.Fail(FailureClassValues.DependencyTimeout, "rating service timed out", retryable: true, ex);
///     throw;
/// }
/// </code>
/// </example>
public static class Scribe
{
    /// <summary>Name of the <see cref="ActivitySource"/> used by <see cref="Begin"/>.</summary>
    public const string SourceName = "Tracescribe";

    /// <summary>The <see cref="ActivitySource"/> used by <see cref="Begin"/>. Register it with your tracer provider.</summary>
    public static ActivitySource Source { get; } = new(SourceName, TracescribeAttributes.SchemaVersionValue);

    /// <summary>
    /// Starts a span for a business operation. The span name is the intent.
    /// Returns an operation that is safe to use even when no listener is sampling the span.
    /// </summary>
    /// <param name="intent">Stable, dotted, low-cardinality operation name, e.g. <c>claim.fnol.submit</c>.</param>
    /// <param name="expected">What a correct result looks like.</param>
    /// <param name="description">Optional one-sentence description.</param>
    /// <param name="kind">Span kind.</param>
    /// <param name="links">Optional causal links, see <see cref="TracescribeLinks"/>.</param>
    /// <param name="source">Optional custom source; defaults to <see cref="Source"/>.</param>
    public static ScribeOperation Begin(
        string intent,
        string? expected = null,
        string? description = null,
        ActivityKind kind = ActivityKind.Internal,
        IEnumerable<ActivityLink>? links = null,
        ActivitySource? source = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(intent);
        var activity = (source ?? Source).StartActivity(intent, kind, parentContext: default, links: links);
        activity?.SetIntent(intent, description);
        if (expected is not null)
        {
            activity?.ExpectOutcome(expected);
        }

        return new ScribeOperation(activity);
    }
}

/// <summary>
/// A business operation in flight. All members are no-ops when the underlying span is not sampled.
/// Disposing ends the span; an operation that ends without an outcome is marked <c>unknown</c>.
/// </summary>
public sealed class ScribeOperation : IDisposable
{
    private bool _outcomeRecorded;

    internal ScribeOperation(Activity? activity) => Activity = activity;

    /// <summary>The underlying span, or <c>null</c> when nothing is listening.</summary>
    public Activity? Activity { get; }

    /// <summary>Adds a <c>tracescribe.context.&lt;key&gt;</c> attribute.</summary>
    public ScribeOperation WithContext(string key, object? value)
    {
        Activity?.SetContext(key, value);
        return this;
    }

    /// <summary>Records a <c>tracescribe.decision</c> event.</summary>
    public ScribeOperation Decide(string name, string chosen, IEnumerable<string>? alternatives = null, string? rationale = null)
    {
        Activity?.AddDecision(name, chosen, alternatives, rationale);
        return this;
    }

    /// <summary>Records a <c>tracescribe.hint</c> event for future triagers.</summary>
    public ScribeOperation Hint(string text)
    {
        Activity?.AddHint(text);
        return this;
    }

    /// <summary>Marks the operation successful.</summary>
    public void Succeed(string? actual = null)
    {
        Activity?.RecordSuccess(actual);
        _outcomeRecorded = true;
    }

    /// <summary>Marks the operation as partially successful.</summary>
    public void Partial(string actual, string? reason = null)
    {
        Activity?.RecordPartial(actual, reason);
        _outcomeRecorded = true;
    }

    /// <summary>Marks the operation failed and sets span status to error.</summary>
    public void Fail(string failureClass, string reason, bool? retryable = null, Exception? exception = null, string? actual = null)
    {
        Activity?.RecordFailure(failureClass, reason, retryable, exception, actual);
        _outcomeRecorded = true;
    }

    /// <summary>
    /// Records a semantic failure: the work completed without error but the result does not match
    /// the expected outcome. Span status is left unchanged.
    /// </summary>
    public void Mismatch(string actual, string? reason = null)
    {
        Activity?.RecordMismatch(actual, reason);
        _outcomeRecorded = true;
    }

    /// <summary>Ends the span.</summary>
    public void Dispose()
    {
        if (Activity is null)
        {
            return;
        }

        if (!_outcomeRecorded && Activity.GetTagItem(TracescribeAttributes.OutcomeStatus) is null)
        {
            Activity.SetTag(TracescribeAttributes.OutcomeStatus,
                Activity.Status == ActivityStatusCode.Error ? OutcomeStatusValues.Failure : OutcomeStatusValues.Unknown);
        }

        Activity.Dispose();
    }
}

/// <summary>Helpers for causal span links.</summary>
public static class TracescribeLinks
{
    /// <summary>Creates a link annotated with <c>tracescribe.link.reason</c>; see <see cref="LinkReasonValues"/>.</summary>
    public static ActivityLink Create(ActivityContext context, string reason) =>
        new(context, new ActivityTagsCollection { [TracescribeAttributes.LinkReason] = reason });
}
