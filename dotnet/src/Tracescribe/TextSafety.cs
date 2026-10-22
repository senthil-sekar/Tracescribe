using System.Text;

namespace Tracescribe;

/// <summary>
/// Makes untrusted telemetry text safe to show to an agent: one line, no control characters. A span name or
/// exception message containing a newline could otherwise forge lines that look like the digest's own output.
/// </summary>
internal static class TextSafety
{
    /// <summary>Replaces line breaks, Unicode line/paragraph separators and other control characters with spaces.</summary>
    public static string OneLine(string value)
    {
        var clean = true;
        foreach (var c in value)
        {
            if (IsUnsafe(c))
            {
                clean = false;
                break;
            }
        }

        if (clean)
        {
            return value;
        }

        var sb = new StringBuilder(value.Length);
        var lastWasSpace = false;
        foreach (var c in value)
        {
            if (IsUnsafe(c))
            {
                if (!lastWasSpace)
                {
                    sb.Append(' ');
                }

                lastWasSpace = true;
                continue;
            }

            sb.Append(c);
            lastWasSpace = c == ' ';
        }

        return sb.ToString();
    }

    private static bool IsUnsafe(char c) => char.IsControl(c) || c is '\u2028' or '\u2029';
}
