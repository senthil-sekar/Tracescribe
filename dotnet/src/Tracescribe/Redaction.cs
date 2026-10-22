using System.Text.RegularExpressions;

namespace Tracescribe;

/// <summary>Decides whether an attribute value must be masked before export.</summary>
public interface IRedactor
{
    /// <summary>
    /// Returns the value to export. Set <paramref name="redacted"/> to <c>true</c> when the value was changed.
    /// </summary>
    /// <param name="key">Context key without the <c>tracescribe.context.</c> prefix.</param>
    /// <param name="value">Original value rendered as a string.</param>
    /// <param name="redacted">Whether the returned value differs from the original.</param>
    string Redact(string key, string value, out bool redacted);
}

/// <summary>Options for <see cref="DefaultRedactor"/>.</summary>
public sealed class RedactionOptions
{
    /// <summary>Keys whose values are always masked. Matching is case-insensitive.</summary>
    public ISet<string> DenyKeys { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "ssn", "social_security_number", "tax_id", "dob", "date_of_birth", "driver_license",
        "drivers_license_number", "email", "phone", "phone_number", "address", "street_address",
        "card_number", "account_number", "routing_number", "bank_account", "password", "secret",
    };

    /// <summary>Keys that bypass pattern detection. Deny-list still wins.</summary>
    public ISet<string> AllowKeys { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>Detect and mask emails, phone numbers, card numbers and US SSNs inside values.</summary>
    public bool DetectPatterns { get; set; } = true;
}

/// <summary>
/// Default redaction policy: masks deny-listed keys entirely and masks PII-shaped substrings in other values.
/// This is a safety net, not a compliance control. Prefer not to put PII in telemetry at all.
/// </summary>
public sealed partial class DefaultRedactor : IRedactor
{
    private readonly RedactionOptions _options;

    /// <summary>Creates a redactor.</summary>
    public DefaultRedactor(RedactionOptions? options = null) => _options = options ?? new RedactionOptions();

    /// <inheritdoc />
    public string Redact(string key, string value, out bool redacted)
    {
        if (_options.DenyKeys.Contains(key))
        {
            redacted = true;
            return "[REDACTED]";
        }

        if (!_options.DetectPatterns || _options.AllowKeys.Contains(key))
        {
            redacted = false;
            return value;
        }

        var result = Email().Replace(value, "[REDACTED:email]");
        result = Ssn().Replace(result, "[REDACTED:ssn]");
        result = Card().Replace(result, m => LuhnValid(m.Value) ? "[REDACTED:card]" : m.Value);
        result = Phone().Replace(result, "[REDACTED:phone]");
        redacted = !ReferenceEquals(result, value) && result != value;
        return result;
    }

    private static bool LuhnValid(string candidate)
    {
        var digits = candidate.Where(char.IsDigit).Select(c => c - '0').ToArray();
        if (digits.Length is < 13 or > 19)
        {
            return false;
        }

        var sum = 0;
        for (var i = 0; i < digits.Length; i++)
        {
            var d = digits[digits.Length - 1 - i];
            if (i % 2 == 1)
            {
                d *= 2;
                if (d > 9)
                {
                    d -= 9;
                }
            }

            sum += d;
        }

        return sum % 10 == 0;
    }

    private const string EmailPattern = @"[A-Za-z0-9._%+\-]+@[A-Za-z0-9.\-]+\.[A-Za-z]{2,}";
    private const string SsnPattern = @"\b\d{3}-\d{2}-\d{4}\b";
    private const string CardPattern = @"\b(?:\d[ \-]?){12,18}\d\b";
    private const string PhonePattern = @"(?<!\w)(?:\+?1[ .\-]?)?\(?\d{3}\)?[ .\-]\d{3}[ .\-]\d{4}\b";

#if NET7_0_OR_GREATER
    [GeneratedRegex(EmailPattern)]
    private static partial Regex Email();

    [GeneratedRegex(SsnPattern)]
    private static partial Regex Ssn();

    [GeneratedRegex(CardPattern)]
    private static partial Regex Card();

    [GeneratedRegex(PhonePattern)]
    private static partial Regex Phone();
#else
    private static readonly Regex EmailRegex = new(EmailPattern, RegexOptions.Compiled);
    private static readonly Regex SsnRegex = new(SsnPattern, RegexOptions.Compiled);
    private static readonly Regex CardRegex = new(CardPattern, RegexOptions.Compiled);
    private static readonly Regex PhoneRegex = new(PhonePattern, RegexOptions.Compiled);

    private static Regex Email() => EmailRegex;

    private static Regex Ssn() => SsnRegex;

    private static Regex Card() => CardRegex;

    private static Regex Phone() => PhoneRegex;
#endif
}

/// <summary>Process-wide redaction defaults.</summary>
public static class TracescribeRedaction
{
    private static IRedactor _default = new DefaultRedactor();

    /// <summary>
    /// Redactor used for exception messages recorded by Tracescribe, by <see cref="Digest.TriageDigest"/>
    /// and by components that are not given an explicit redactor. Set once at startup.
    /// </summary>
    public static IRedactor Default
    {
        get => Volatile.Read(ref _default);
        set => Volatile.Write(ref _default, value ?? throw new ArgumentNullException(nameof(value)));
    }

    /// <summary>Applies <paramref name="redactor"/> (or <see cref="Default"/>) to free text such as a message.</summary>
    public static string RedactText(string text, string key = "text", IRedactor? redactor = null) =>
        (redactor ?? Default).Redact(key, text, out _);
}
