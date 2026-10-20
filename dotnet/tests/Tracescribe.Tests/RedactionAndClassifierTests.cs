using Tracescribe.SemanticConventions;
using Xunit;

namespace Tracescribe.Tests;

public class RedactionAndClassifierTests
{
    [Theory]
    [InlineData("card", "paid with 4111 1111 1111 1111", "paid with [REDACTED:card]")]
    [InlineData("card", "ref 1234567890123456", "ref 1234567890123456")] // fails Luhn, kept
    [InlineData("note", "ssn 123-45-6789", "ssn [REDACTED:ssn]")]
    [InlineData("claim_id", "CLM-2026-004211", "CLM-2026-004211")]
    [InlineData("vin", "1HGCM82633A004352", "1HGCM82633A004352")]
    public void Default_redactor_masks_pii_shapes(string key, string input, string expected)
    {
        var r = new DefaultRedactor();
        Assert.Equal(expected, r.Redact(key, input, out var redacted));
        Assert.Equal(input != expected, redacted);
    }

    [Fact]
    public void Allow_keys_bypass_pattern_detection_but_not_deny_list()
    {
        var options = new RedactionOptions();
        options.AllowKeys.Add("adjuster_email");
        options.AllowKeys.Add("ssn");
        var r = new DefaultRedactor(options);

        Assert.Equal("adj@insurer.com", r.Redact("adjuster_email", "adj@insurer.com", out _));
        Assert.Equal("[REDACTED]", r.Redact("ssn", "123-45-6789", out _));
    }

    [Theory]
    [InlineData("System.TimeoutException", FailureClassValues.DependencyTimeout)]
    [InlineData("System.Net.Http.HttpRequestException", FailureClassValues.Dependency)]
    [InlineData("System.ArgumentNullException", FailureClassValues.Data)]
    [InlineData("System.UnauthorizedAccessException", FailureClassValues.Policy)]
    [InlineData("System.NullReferenceException", FailureClassValues.Internal)]
    [InlineData("Polly.Timeout.TimeoutRejectedException", FailureClassValues.DependencyTimeout)]
    [InlineData("My.Custom.GatewayTimeoutError", FailureClassValues.DependencyTimeout)]
    public void Classifies_exception_types(string type, string expected) =>
        Assert.Equal(expected, FailureClassifier.FromExceptionType(type)?.FailureClass);

    [Fact]
    public void Unknown_exception_type_is_unclassified() =>
        Assert.Null(FailureClassifier.FromExceptionType("My.DomainException"));

    [Theory]
    [InlineData("timeout", FailureClassValues.DependencyTimeout)]
    [InlineData("connection_error", FailureClassValues.Dependency)]
    [InlineData("503", FailureClassValues.Dependency)]
    public void Classifies_error_type(string errorType, string expected) =>
        Assert.Equal(expected, FailureClassifier.FromErrorType(errorType)?.FailureClass);
}
