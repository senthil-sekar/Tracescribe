using System.Diagnostics;
using System.Globalization;
using Tracescribe.SemanticConventions;

namespace Tracescribe;

/// <summary>Options for <see cref="TracescribeEnricher"/>.</summary>
public sealed class TracescribeEnricherOptions
{
    /// <summary>Redaction policy for <c>tracescribe.context.*</c> values. Set to <c>null</c> to disable.</summary>
    public IRedactor? Redactor { get; set; } = TracescribeRedaction.Default;

    /// <summary>Infer <c>tracescribe.failure.class</c> on error spans that lack one.</summary>
    public bool InferFailureClass { get; set; } = true;

    /// <summary>Set <c>tracescribe.outcome.status</c> on intent spans that end without one.</summary>
    public bool DefaultOutcome { get; set; } = true;

    /// <summary>
    /// Record <c>tracescribe.operation.count</c> and <c>tracescribe.operation.duration</c> for intent spans
    /// on the <c>Tracescribe</c> meter.
    /// </summary>
    public bool RecordMetrics { get; set; } = true;
}

/// <summary>
/// Applies Tracescribe conventions to a finished span: failure-class inference, default outcomes and
/// redaction. Called from the OpenTelemetry processor (<c>Tracescribe.OpenTelemetry</c>) or from
/// <see cref="Digest.TraceCollector"/>. Works on spans from any instrumentation, not only Tracescribe's own.
/// </summary>
public sealed class TracescribeEnricher
{
    private readonly TracescribeEnricherOptions _options;

    /// <summary>Creates an enricher.</summary>
    public TracescribeEnricher(TracescribeEnricherOptions? options = null) =>
        _options = options ?? new TracescribeEnricherOptions();

    /// <summary>Enriches a span that has just ended. Never throws.</summary>
    public void OnEnd(Activity activity)
    {
        try
        {
            Enrich(activity);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Telemetry must never break the application.
            Debug.WriteLine($"Tracescribe enrichment failed: {ex}");
        }
    }

    private const string EnrichedMarker = "Tracescribe.Enriched";

    private void Enrich(Activity activity)
    {
        // Idempotent: a span seen by more than one enricher (e.g. a collector and an OTel processor)
        // is enriched and counted once.
        if (activity.GetCustomProperty(EnrichedMarker) is not null)
        {
            return;
        }

        activity.SetCustomProperty(EnrichedMarker, true);

        var isError = activity.Status == ActivityStatusCode.Error;
        var hasIntent = activity.GetTagItem(TracescribeAttributes.Intent) is not null;

        if (isError && _options.InferFailureClass && activity.GetTagItem(TracescribeAttributes.FailureClass) is null)
        {
            var c = Infer(activity);
            activity.SetTag(TracescribeAttributes.FailureClass, c.FailureClass);
            activity.SetTag(TracescribeAttributes.FailureInferred, true);
            if (c.Retryable is { } r && activity.GetTagItem(TracescribeAttributes.FailureRetryable) is null)
            {
                activity.SetTag(TracescribeAttributes.FailureRetryable, r);
            }

            if (activity.GetTagItem(TracescribeAttributes.FailureReason) is null && !string.IsNullOrEmpty(activity.StatusDescription))
            {
                activity.SetTag(TracescribeAttributes.FailureReason, activity.StatusDescription);
            }
        }

        if (hasIntent && _options.DefaultOutcome && activity.GetTagItem(TracescribeAttributes.OutcomeStatus) is null)
        {
            activity.SetTag(TracescribeAttributes.OutcomeStatus, isError ? OutcomeStatusValues.Failure : OutcomeStatusValues.Unknown);
        }

        if (_options.Redactor is { } redactor)
        {
            Redact(activity, redactor);
        }

        if (hasIntent && _options.RecordMetrics)
        {
            TracescribeInstruments.Record(activity);
        }
    }

    internal static FailureClassification Infer(Activity activity)
    {
        foreach (var e in activity.Events)
        {
            if (e.Name != "exception")
            {
                continue;
            }

            foreach (var tag in e.Tags)
            {
                if (tag.Key == "exception.type" && FailureClassifier.FromExceptionType(tag.Value as string) is { } fromEx)
                {
                    return fromEx;
                }
            }
        }

        if (FailureClassifier.FromErrorType(activity.GetTagItem("error.type") as string) is { } fromErrorType)
        {
            return fromErrorType;
        }

        if (activity.GetTagItem("exception.type") is string exType && FailureClassifier.FromExceptionType(exType) is { } fromTag)
        {
            return fromTag;
        }

        var status = activity.GetTagItem("http.response.status_code") ?? activity.GetTagItem("http.status_code");
        if (status is not null
            && int.TryParse(Convert.ToString(status, CultureInfo.InvariantCulture), out var code)
            && FailureClassifier.FromHttpStatus(code) is { } fromHttp)
        {
            return fromHttp;
        }

        if (activity.GetTagItem(GenAiAttributes.OperationName) as string == "execute_tool")
        {
            return new(FailureClassValues.Dependency, null);
        }

        return new(FailureClassValues.Unknown, null);
    }

    private static void Redact(Activity activity, IRedactor redactor)
    {
        List<string>? redactedKeys = null;
        foreach (var tag in activity.TagObjects.ToList())
        {
            if (!tag.Key.StartsWith(TracescribeAttributes.ContextPrefix, StringComparison.Ordinal) || tag.Value is null)
            {
                continue;
            }

            var key = tag.Key[TracescribeAttributes.ContextPrefix.Length..];
            var original = Convert.ToString(tag.Value, CultureInfo.InvariantCulture) ?? string.Empty;
            var masked = redactor.Redact(key, original, out var changed);
            if (changed)
            {
                activity.SetTag(tag.Key, masked);
                (redactedKeys ??= new()).Add(tag.Key);
            }
        }

        if (redactedKeys is not null)
        {
            activity.SetTag(TracescribeAttributes.Redacted, true);
            activity.SetTag(TracescribeAttributes.RedactedKeys, redactedKeys.ToArray());
        }
    }
}
