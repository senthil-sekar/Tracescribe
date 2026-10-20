using Tracescribe.SemanticConventions;

namespace Tracescribe;

/// <summary>Result of failure inference.</summary>
/// <param name="FailureClass">One of <see cref="FailureClassValues"/>.</param>
/// <param name="Retryable">Whether a retry could plausibly succeed, when known.</param>
public readonly record struct FailureClassification(string FailureClass, bool? Retryable);

/// <summary>
/// Infers <c>tracescribe.failure.class</c> from signals other instrumentation already emits:
/// exception types, <c>error.type</c>, HTTP status codes and GenAI operation names.
/// </summary>
public static class FailureClassifier
{
    private static readonly Dictionary<string, FailureClassification> ByExceptionName = new(StringComparer.Ordinal)
    {
        ["TimeoutException"] = new(FailureClassValues.DependencyTimeout, true),
        ["TaskCanceledException"] = new(FailureClassValues.DependencyTimeout, true),
        ["OperationCanceledException"] = new(FailureClassValues.DependencyTimeout, true),
        ["TimeoutRejectedException"] = new(FailureClassValues.DependencyTimeout, true),
        ["HttpRequestException"] = new(FailureClassValues.Dependency, true),
        ["SocketException"] = new(FailureClassValues.Dependency, true),
        ["SqlException"] = new(FailureClassValues.Dependency, null),
        ["DbException"] = new(FailureClassValues.Dependency, null),
        ["RequestFailedException"] = new(FailureClassValues.Dependency, null),
        ["CosmosException"] = new(FailureClassValues.Dependency, null),
        ["BrokenCircuitException"] = new(FailureClassValues.Dependency, true),
        ["FormatException"] = new(FailureClassValues.Data, false),
        ["JsonException"] = new(FailureClassValues.Data, false),
        ["InvalidDataException"] = new(FailureClassValues.Data, false),
        ["KeyNotFoundException"] = new(FailureClassValues.Data, false),
        ["ValidationException"] = new(FailureClassValues.Validation, false),
        ["UnauthorizedAccessException"] = new(FailureClassValues.Policy, false),
        ["SecurityException"] = new(FailureClassValues.Policy, false),
        ["AuthenticationException"] = new(FailureClassValues.Policy, false),
        ["NullReferenceException"] = new(FailureClassValues.Internal, false),
        ["InvalidOperationException"] = new(FailureClassValues.Internal, false),
        ["IndexOutOfRangeException"] = new(FailureClassValues.Internal, false),
        ["NotImplementedException"] = new(FailureClassValues.Internal, false),
        ["InvalidCastException"] = new(FailureClassValues.Internal, false),
    };

    /// <summary>Classifies from a full or short exception type name.</summary>
    public static FailureClassification? FromExceptionType(string? exceptionType)
    {
        if (string.IsNullOrWhiteSpace(exceptionType))
        {
            return null;
        }

        var shortName = exceptionType[(exceptionType.LastIndexOf('.') + 1)..];
        if (ByExceptionName.TryGetValue(shortName, out var c))
        {
            return c;
        }

        if (shortName.StartsWith("Argument", StringComparison.Ordinal))
        {
            return new(FailureClassValues.Data, false);
        }

        if (shortName.Contains("Timeout", StringComparison.Ordinal))
        {
            return new(FailureClassValues.DependencyTimeout, true);
        }

        return null;
    }

    /// <summary>Classifies from an HTTP response status code.</summary>
    public static FailureClassification? FromHttpStatus(int statusCode) => statusCode switch
    {
        401 or 403 => new(FailureClassValues.Policy, false),
        429 => new(FailureClassValues.Policy, true),
        408 or 504 => new(FailureClassValues.DependencyTimeout, true),
        400 or 409 or 422 => new(FailureClassValues.Validation, false),
        404 => new(FailureClassValues.Data, false),
        >= 500 and <= 599 => new(FailureClassValues.Dependency, statusCode is 502 or 503),
        _ => null,
    };

    /// <summary>Classifies from the OTel <c>error.type</c> attribute.</summary>
    public static FailureClassification? FromErrorType(string? errorType)
    {
        if (string.IsNullOrWhiteSpace(errorType))
        {
            return null;
        }

        if (int.TryParse(errorType, out var code))
        {
            return FromHttpStatus(code);
        }

        return errorType switch
        {
            "timeout" => new(FailureClassValues.DependencyTimeout, true),
            "connection_error" or "name_resolution_error" or "secure_connection_error" or "http_protocol_error"
                => new(FailureClassValues.Dependency, true),
            _ => FromExceptionType(errorType),
        };
    }
}
