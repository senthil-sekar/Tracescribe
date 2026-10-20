using System.Diagnostics;
using System.Diagnostics.Metrics;
using Tracescribe.SemanticConventions;

namespace Tracescribe;

/// <summary>
/// Metric instruments on the <c>Tracescribe</c> meter. Register the meter with your metrics pipeline
/// (<c>.AddMeter("Tracescribe")</c>, or <c>AddTracescribe()</c> from Tracescribe.OpenTelemetry).
/// </summary>
public static class TracescribeInstruments
{
    /// <summary>The meter all Tracescribe instruments are created on.</summary>
    public static Meter Meter { get; } = new(TracescribeMetrics.MeterName, TracescribeAttributes.SchemaVersionValue);

    /// <summary><c>tracescribe.operation.count</c></summary>
    public static Counter<long> OperationCount { get; } = Meter.CreateCounter<long>(
        TracescribeMetrics.OperationCount, TracescribeMetrics.OperationCountUnit, "Number of completed intent operations.");

    /// <summary><c>tracescribe.operation.duration</c></summary>
    public static Histogram<double> OperationDuration { get; } = Meter.CreateHistogram<double>(
        TracescribeMetrics.OperationDuration, TracescribeMetrics.OperationDurationUnit, "Duration of completed intent operations.");

    internal static void Record(Activity activity)
    {
        var intent = activity.GetTagItem(TracescribeAttributes.Intent) as string;
        if (intent is null)
        {
            return;
        }

        var status = activity.GetTagItem(TracescribeAttributes.OutcomeStatus) as string ?? OutcomeStatusValues.Unknown;
        var durationTags = new TagList
        {
            { TracescribeAttributes.Intent, intent },
            { TracescribeAttributes.OutcomeStatus, status },
        };
        var countTags = durationTags;
        if (activity.GetTagItem(TracescribeAttributes.FailureClass) is string failureClass)
        {
            countTags.Add(TracescribeAttributes.FailureClass, failureClass);
        }

        OperationCount.Add(1, countTags);
        OperationDuration.Record(activity.Duration.TotalSeconds, durationTags);
    }
}
