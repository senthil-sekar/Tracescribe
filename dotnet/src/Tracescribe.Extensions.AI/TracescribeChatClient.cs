using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;
using Tracescribe.SemanticConventions;

namespace Tracescribe.Extensions.AI;

/// <summary>Options for <see cref="TracescribeChatClient"/>.</summary>
public sealed class TracescribeChatOptions
{
    /// <summary>Key in <see cref="ChatOptions.AdditionalProperties"/> that overrides <see cref="Intent"/> per call.</summary>
    public const string IntentProperty = "tracescribe.intent";

    /// <summary>Key in <see cref="ChatOptions.AdditionalProperties"/> that overrides <see cref="Expected"/> per call.</summary>
    public const string ExpectedProperty = "tracescribe.expected";

    /// <summary>Default <c>tracescribe.intent</c> for calls through this client, e.g. <c>claim.summary.generate</c>.</summary>
    public string Intent { get; set; } = "chat.respond";

    /// <summary>Default <c>tracescribe.outcome.expected</c>.</summary>
    public string? Expected { get; set; }

    /// <summary>Checks each completed response. Defaults to <see cref="ChatOutcomeEvaluators.NonEmpty"/>.</summary>
    public ChatOutcomeEvaluator? Evaluator { get; set; } = ChatOutcomeEvaluators.NonEmpty();
}

/// <summary>
/// Chat client middleware that wraps each call in a Tracescribe operation. It records the model's finish
/// reason, treats content-filter stops as <c>policy</c> failures and length stops as <c>partial</c>, runs an
/// outcome evaluator to catch responses that are well-formed but wrong, and classifies exceptions (rate
/// limits, timeouts, provider errors). Put it outside <c>UseOpenTelemetry()</c> so the GenAI <c>chat</c>
/// span nests under the intent span.
/// </summary>
public sealed class TracescribeChatClient : DelegatingChatClient
{
    private readonly TracescribeChatOptions _options;

    /// <summary>Creates the client.</summary>
    public TracescribeChatClient(IChatClient innerClient, TracescribeChatOptions? options = null)
        : base(innerClient) => _options = options ?? new TracescribeChatOptions();

    /// <inheritdoc />
    public override async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        using var op = Begin(options);
        ChatResponse response;
        try
        {
            response = await base.GetResponseAsync(messages, options, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            op.Hint("canceled by caller");
            throw;
        }
        catch (Exception ex)
        {
            op.Fail(ex);
            throw;
        }

        Complete(op, response);
        return response;
    }

    /// <inheritdoc />
    public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        using var op = Begin(options);
        var updates = new List<ChatResponseUpdate>();
        var enumerator = base.GetStreamingResponseAsync(messages, options, cancellationToken).GetAsyncEnumerator(cancellationToken);
        try
        {
            while (true)
            {
                try
                {
                    if (!await enumerator.MoveNextAsync().ConfigureAwait(false))
                    {
                        break;
                    }
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    op.Hint("canceled by caller");
                    throw;
                }
                catch (Exception ex)
                {
                    op.Fail(ex);
                    throw;
                }

                updates.Add(enumerator.Current);
                yield return enumerator.Current;
            }
        }
        finally
        {
            await enumerator.DisposeAsync().ConfigureAwait(false);
        }

        Complete(op, updates.ToChatResponse());
    }

    private ScribeOperation Begin(ChatOptions? options)
    {
        var intent = _options.Intent;
        var expected = _options.Expected;
        if (options?.AdditionalProperties is { } props)
        {
            if (props.TryGetValue(TracescribeChatOptions.IntentProperty, out string? i) && !string.IsNullOrWhiteSpace(i))
            {
                intent = i;
            }

            if (props.TryGetValue(TracescribeChatOptions.ExpectedProperty, out string? e) && !string.IsNullOrWhiteSpace(e))
            {
                expected = e;
            }
        }

        return Scribe.Begin(intent, expected);
    }

    private void Complete(ScribeOperation op, ChatResponse response)
    {
        var finish = response.FinishReason;
        if (finish is { } f)
        {
            op.Activity?.SetTag(TracescribeAttributes.ChatFinishReason, f.Value);
        }

        if (finish == ChatFinishReason.ContentFilter)
        {
            op.Fail(FailureClassValues.Policy, "response stopped by the content filter", retryable: false, actual: "content filtered");
            return;
        }

        if (finish == ChatFinishReason.Length)
        {
            op.Partial("response truncated at the token limit", "finish_reason=length; raise max output tokens or shorten the prompt");
            return;
        }

        ChatOutcome outcome;
        try
        {
            outcome = _options.Evaluator?.Invoke(response) ?? ChatOutcome.Ok();
        }
        catch (Exception ex)
        {
            outcome = ChatOutcome.Wrong("evaluator failed", "outcome evaluator threw " + ex.GetType().Name);
        }

        if (outcome.Matched)
        {
            op.Succeed(outcome.Actual);
        }
        else
        {
            op.Mismatch(outcome.Actual ?? "unexpected response", outcome.Reason);
        }
    }
}

/// <summary>Registration helpers.</summary>
public static class TracescribeChatClientBuilderExtensions
{
    /// <summary>
    /// Adds <see cref="TracescribeChatClient"/> to the pipeline. Call it before <c>UseOpenTelemetry()</c> so
    /// the GenAI chat span becomes a child of the intent span.
    /// </summary>
    /// <example>
    /// <code>
    /// IChatClient client = new ChatClientBuilder(inner)
    ///     .UseTracescribe("claim.summary.generate", o => o.Evaluator = ChatOutcomeEvaluators.RequireJson())
    ///     .UseOpenTelemetry()
    ///     .Build();
    /// </code>
    /// </example>
    public static ChatClientBuilder UseTracescribe(this ChatClientBuilder builder, string intent, Action<TracescribeChatOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(intent);
        var options = new TracescribeChatOptions { Intent = intent };
        configure?.Invoke(options);
        return builder.Use(inner => new TracescribeChatClient(inner, options));
    }
}
