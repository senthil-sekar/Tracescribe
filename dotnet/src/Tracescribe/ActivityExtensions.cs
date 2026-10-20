using System.Diagnostics;
using Tracescribe.SemanticConventions;

namespace Tracescribe;

/// <summary>
/// Tracescribe extensions for any <see cref="Activity"/>, including spans created by other
/// instrumentation (ASP.NET Core, HttpClient, Microsoft Agent Framework).
/// </summary>
public static class ActivityExtensions
{
    /// <summary>Sets <c>tracescribe.intent</c> and the schema version.</summary>
    public static Activity SetIntent(this Activity activity, string intent, string? description = null)
    {
        activity.SetTag(TracescribeAttributes.Intent, intent);
        activity.SetTag(TracescribeAttributes.SchemaVersion, TracescribeAttributes.SchemaVersionValue);
        if (description is not null)
        {
            activity.SetTag(TracescribeAttributes.IntentDescription, description);
        }

        return activity;
    }

    /// <summary>Sets <c>tracescribe.outcome.expected</c>.</summary>
    public static Activity ExpectOutcome(this Activity activity, string expected) =>
        activity.SetTag(TracescribeAttributes.OutcomeExpected, expected);

    /// <summary>Sets a <c>tracescribe.context.&lt;key&gt;</c> attribute. Values are redacted at export by the enricher.</summary>
    public static Activity SetContext(this Activity activity, string key, object? value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        return activity.SetTag(TracescribeAttributes.Context(key), value);
    }

    /// <summary>Records success.</summary>
    public static Activity RecordSuccess(this Activity activity, string? actual = null)
    {
        activity.SetTag(TracescribeAttributes.OutcomeStatus, OutcomeStatusValues.Success);
        activity.SetTag(TracescribeAttributes.OutcomeMatched, true);
        if (actual is not null)
        {
            activity.SetTag(TracescribeAttributes.OutcomeActual, actual);
        }

        return activity;
    }

    /// <summary>Records partial success.</summary>
    public static Activity RecordPartial(this Activity activity, string actual, string? reason = null)
    {
        activity.SetTag(TracescribeAttributes.OutcomeStatus, OutcomeStatusValues.Partial);
        activity.SetTag(TracescribeAttributes.OutcomeMatched, false);
        activity.SetTag(TracescribeAttributes.OutcomeActual, actual);
        if (reason is not null)
        {
            activity.SetTag(TracescribeAttributes.FailureReason, reason);
        }

        return activity;
    }

    /// <summary>Records a failure, sets error status and, when given, an OTel <c>exception</c> event.</summary>
    public static Activity RecordFailure(
        this Activity activity,
        string failureClass,
        string reason,
        bool? retryable = null,
        Exception? exception = null,
        string? actual = null)
    {
        activity.SetTag(TracescribeAttributes.OutcomeStatus, OutcomeStatusValues.Failure);
        activity.SetTag(TracescribeAttributes.OutcomeMatched, false);
        activity.SetTag(TracescribeAttributes.FailureClass, failureClass);
        activity.SetTag(TracescribeAttributes.FailureReason, reason);
        if (retryable is { } r)
        {
            activity.SetTag(TracescribeAttributes.FailureRetryable, r);
        }

        if (actual is not null)
        {
            activity.SetTag(TracescribeAttributes.OutcomeActual, actual);
        }

        if (exception is not null)
        {
            activity.RecordExceptionEvent(exception);
        }

        activity.SetStatus(ActivityStatusCode.Error, reason);
        return activity;
    }

    /// <summary>Records a semantic failure (completed without error, wrong result). Span status is unchanged.</summary>
    public static Activity RecordMismatch(this Activity activity, string actual, string? reason = null)
    {
        activity.SetTag(TracescribeAttributes.OutcomeStatus, OutcomeStatusValues.Failure);
        activity.SetTag(TracescribeAttributes.OutcomeMatched, false);
        activity.SetTag(TracescribeAttributes.OutcomeActual, actual);
        activity.SetTag(TracescribeAttributes.FailureClass, FailureClassValues.Semantic);
        if (reason is not null)
        {
            activity.SetTag(TracescribeAttributes.FailureReason, reason);
        }

        return activity;
    }

    /// <summary>Adds a <c>tracescribe.decision</c> event.</summary>
    public static Activity AddDecision(
        this Activity activity,
        string name,
        string chosen,
        IEnumerable<string>? alternatives = null,
        string? rationale = null)
    {
        var tags = new ActivityTagsCollection
        {
            [TracescribeEvents.DecisionName] = name,
            [TracescribeEvents.DecisionChosen] = chosen,
        };
        if (alternatives is not null)
        {
            tags[TracescribeEvents.DecisionAlternatives] = alternatives.ToArray();
        }

        if (rationale is not null)
        {
            tags[TracescribeEvents.DecisionRationale] = rationale;
        }

        return activity.AddEvent(new ActivityEvent(TracescribeEvents.Decision, tags: tags));
    }

    /// <summary>Adds a <c>tracescribe.hint</c> event.</summary>
    public static Activity AddHint(this Activity activity, string text) =>
        activity.AddEvent(new ActivityEvent(TracescribeEvents.Hint,
            tags: new ActivityTagsCollection { [TracescribeEvents.HintText] = text }));

    /// <summary>Adds an OTel-conventional <c>exception</c> event (type, message, stack trace).</summary>
    public static Activity RecordExceptionEvent(this Activity activity, Exception exception)
    {
        var tags = new ActivityTagsCollection
        {
            ["exception.type"] = exception.GetType().FullName,
            ["exception.message"] = exception.Message,
        };
        if (exception.StackTrace is { } stack)
        {
            tags["exception.stacktrace"] = stack;
        }

        return activity.AddEvent(new ActivityEvent("exception", tags: tags));
    }
}
