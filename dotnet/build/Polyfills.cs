// Compiled into netstandard2.0 builds only (see Directory.Build.props). Supplies the handful of .NET 6+ APIs the
// libraries use, as C# 14 extension members, so the shared source needs no #if blocks. Internal to each assembly.
#if NETSTANDARD2_0
using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Runtime.CompilerServices;
using System.Text;

namespace Tracescribe;

internal static class Polyfills
{
    extension(ArgumentNullException)
    {
        public static void ThrowIfNull([NotNull] object? argument, [CallerArgumentExpression(nameof(argument))] string? paramName = null)
        {
            if (argument is null)
            {
                throw new ArgumentNullException(paramName);
            }
        }
    }

    extension(ArgumentException)
    {
        public static void ThrowIfNullOrEmpty([NotNull] string? argument, [CallerArgumentExpression(nameof(argument))] string? paramName = null)
        {
            ArgumentNullException.ThrowIfNull(argument, paramName);
            if (argument.Length == 0)
            {
                throw new ArgumentException("The value cannot be an empty string.", paramName);
            }
        }

        public static void ThrowIfNullOrWhiteSpace([NotNull] string? argument, [CallerArgumentExpression(nameof(argument))] string? paramName = null)
        {
            ArgumentNullException.ThrowIfNull(argument, paramName);
            if (string.IsNullOrWhiteSpace(argument))
            {
                throw new ArgumentException("The value cannot be an empty string or composed entirely of whitespace.", paramName);
            }
        }
    }

    extension(DateTime)
    {
        public static DateTime UnixEpoch => new(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    }

    extension(Convert)
    {
        public static string ToHexString(byte[] bytes)
        {
            var sb = new StringBuilder(bytes.Length * 2);
            foreach (var b in bytes)
            {
                sb.Append(b.ToString("X2", System.Globalization.CultureInfo.InvariantCulture));
            }

            return sb.ToString();
        }
    }

    extension<TKey, TValue>(IReadOnlyDictionary<TKey, TValue> dictionary)
    {
        public TValue? GetValueOrDefault(TKey key) => dictionary.TryGetValue(key, out var value) ? value : default;
    }

    extension<TKey, TValue>(Dictionary<TKey, TValue> dictionary)
        where TKey : notnull
    {
        public TValue? GetValueOrDefault(TKey key) => dictionary.TryGetValue(key, out var value) ? value : default;

        public bool TryAdd(TKey key, TValue value)
        {
            if (dictionary.ContainsKey(key))
            {
                return false;
            }

            dictionary.Add(key, value);
            return true;
        }
    }

    extension(string text)
    {
        public string ReplaceLineEndings(string replacement) =>
            text.Replace("\r\n", "\n").Replace("\r", "\n").Replace("\n", replacement);

        public string ReplaceLineEndings() => text.ReplaceLineEndings(Environment.NewLine);

        public string Replace(string oldValue, string? newValue, StringComparison comparison)
        {
            if (comparison != StringComparison.Ordinal)
            {
                throw new NotSupportedException("Only ordinal replacement is polyfilled.");
            }

            return text.Replace(oldValue, newValue);
        }

        public bool Contains(char value) => text.IndexOf(value) >= 0;

        public bool Contains(string value, StringComparison comparison) => text.IndexOf(value, comparison) >= 0;

        public bool StartsWith(char value) => text.Length > 0 && text[0] == value;

        public bool EndsWith(char value) => text.Length > 0 && text[text.Length - 1] == value;

        public string[] Split(char separator, StringSplitOptions options) => text.Split([separator], options);
    }

    extension(HttpRequestException exception)
    {
        /// <summary>Not available before .NET 5; the status code is unknown.</summary>
        public HttpStatusCode? StatusCode => null;
    }
}
#endif
