using System.Diagnostics;
using System.Globalization;
using OpenTelemetry;
using OpenTelemetry.Logs;
using Tracescribe.SemanticConventions;

namespace Tracescribe.OpenTelemetry;

/// <summary>Options for <see cref="TracescribeLogProcessor"/>.</summary>
public sealed class TracescribeLogOptions
{
    /// <summary>
    /// Redactor for log bodies and attribute values. Defaults to <see cref="TracescribeRedaction.Default"/>;
    /// <c>null</c> disables redaction.
    /// </summary>
    public IRedactor? Redactor { get; set; } = TracescribeRedaction.Default;

    /// <summary>Add <c>tracescribe.intent</c> from the enclosing intent span to each log record.</summary>
    public bool AddIntent { get; set; } = true;
}

/// <summary>
/// Log processor that (1) tags each log record with the <c>tracescribe.intent</c> of the operation it was
/// written in, so an agent can pull every log for a failing intent, and (2) redacts PII-shaped values from
/// the formatted message, body and attribute values before export. Register it before your exporter.
/// </summary>
public sealed class TracescribeLogProcessor : BaseProcessor<LogRecord>
{
    private const string OriginalFormat = "{OriginalFormat}";
    private readonly TracescribeLogOptions _options;

    /// <summary>Creates the processor.</summary>
    public TracescribeLogProcessor(TracescribeLogOptions? options = null) => _options = options ?? new TracescribeLogOptions();

    /// <inheritdoc />
    public override void OnEnd(LogRecord data)
    {
        try
        {
            Process(data);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Debug.WriteLine($"Tracescribe log processing failed: {ex}");
        }
    }

    private void Process(LogRecord record)
    {
        var redactor = _options.Redactor;
        var redacted = false;

        if (redactor is not null)
        {
            if (record.FormattedMessage is { } message)
            {
                record.FormattedMessage = redactor.Redact("log.message", message, out var changed);
                redacted |= changed;
            }

            if (record.Body is { } body)
            {
                record.Body = redactor.Redact("log.body", body, out var changed);
                redacted |= changed;
            }
        }

        var intent = _options.AddIntent ? FindIntent(Activity.Current) : null;
        var original = record.Attributes;
        if (redactor is null && intent is null)
        {
            return;
        }

        var attributes = new List<KeyValuePair<string, object?>>((original?.Count ?? 0) + 2);
        var hasIntent = false;
        if (original is not null)
        {
            foreach (var kv in original)
            {
                hasIntent |= kv.Key == TracescribeAttributes.Intent;
                if (redactor is null || kv.Key == OriginalFormat || kv.Value is null or bool or IFormattable)
                {
                    // Numbers, dates and booleans can't carry PII patterns worth masking.
                    attributes.Add(kv);
                    continue;
                }

                var key = kv.Key.StartsWith(TracescribeAttributes.ContextPrefix, StringComparison.Ordinal)
                    ? kv.Key[TracescribeAttributes.ContextPrefix.Length..]
                    : kv.Key;
                var text = Convert.ToString(kv.Value, CultureInfo.InvariantCulture) ?? string.Empty;
                var masked = redactor.Redact(key, text, out var changed);
                redacted |= changed;
                attributes.Add(changed ? new(kv.Key, masked) : kv);
            }
        }

        if (intent is not null && !hasIntent)
        {
            attributes.Add(new(TracescribeAttributes.Intent, intent));
        }

        if (redacted)
        {
            attributes.Add(new(TracescribeAttributes.Redacted, true));
        }

        record.Attributes = attributes;
    }

    private static string? FindIntent(Activity? activity)
    {
        for (var a = activity; a is not null; a = a.Parent)
        {
            if (a.GetTagItem(TracescribeAttributes.Intent) is string intent)
            {
                return intent;
            }
        }

        return null;
    }
}
