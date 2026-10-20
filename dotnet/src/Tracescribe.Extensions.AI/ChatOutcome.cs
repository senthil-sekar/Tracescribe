using System.Text.Json;
using Microsoft.Extensions.AI;

namespace Tracescribe.Extensions.AI;

/// <summary>Result of checking a chat response against what the caller expected.</summary>
/// <param name="Matched">Whether the response is acceptable.</param>
/// <param name="Actual">Short, PII-free description of what came back.</param>
/// <param name="Reason">Why it does not match, when it doesn't.</param>
public sealed record ChatOutcome(bool Matched, string? Actual = null, string? Reason = null)
{
    /// <summary>An acceptable response.</summary>
    public static ChatOutcome Ok(string? actual = null) => new(true, actual);

    /// <summary>A semantically wrong response.</summary>
    public static ChatOutcome Wrong(string actual, string reason) => new(false, actual, reason);
}

/// <summary>Checks a completed chat response. Must not throw; return <see cref="ChatOutcome.Wrong"/> instead.</summary>
public delegate ChatOutcome ChatOutcomeEvaluator(ChatResponse response);

/// <summary>Ready-made <see cref="ChatOutcomeEvaluator"/>s. Combine them with <see cref="All"/>.</summary>
public static class ChatOutcomeEvaluators
{
    /// <summary>The response must contain text or a tool call.</summary>
    public static ChatOutcomeEvaluator NonEmpty() => response =>
        string.IsNullOrWhiteSpace(response.Text) && !ToolCalls(response).Any()
            ? ChatOutcome.Wrong("empty response", "model returned no text and no tool call")
            : ChatOutcome.Ok();

    /// <summary>The response text must be valid JSON (a surrounding Markdown code fence is tolerated).</summary>
    public static ChatOutcomeEvaluator RequireJson() => response =>
    {
        var text = StripFence(response.Text);
        try
        {
            using var _ = JsonDocument.Parse(text);
            return ChatOutcome.Ok("valid JSON");
        }
        catch (JsonException ex)
        {
            return ChatOutcome.Wrong($"non-JSON response ({text.Length} chars)", $"response is not valid JSON: {ex.Message}");
        }
    };

    /// <summary>The model must call a tool, optionally a specific one.</summary>
    public static ChatOutcomeEvaluator RequireToolCall(string? toolName = null) => response =>
    {
        var calls = ToolCalls(response).Select(c => c.Name).ToList();
        if (toolName is null ? calls.Count > 0 : calls.Contains(toolName))
        {
            return ChatOutcome.Ok("called " + string.Join(", ", calls));
        }

        var actual = calls.Count == 0 ? "no tool call" : "called " + string.Join(", ", calls);
        return ChatOutcome.Wrong(actual, toolName is null ? "expected a tool call" : $"expected a call to {toolName}");
    };

    /// <summary>All evaluators must match; the first mismatch wins.</summary>
    public static ChatOutcomeEvaluator All(params ChatOutcomeEvaluator[] evaluators) => response =>
    {
        ChatOutcome? last = null;
        foreach (var evaluate in evaluators)
        {
            last = evaluate(response);
            if (!last.Matched)
            {
                return last;
            }
        }

        return last ?? ChatOutcome.Ok();
    };

    private static IEnumerable<FunctionCallContent> ToolCalls(ChatResponse response) =>
        response.Messages.SelectMany(m => m.Contents).OfType<FunctionCallContent>();

    private static string StripFence(string text)
    {
        var t = text.Trim();
        if (!t.StartsWith("```", StringComparison.Ordinal))
        {
            return t;
        }

        var firstNewline = t.IndexOf('\n');
        var lastFence = t.LastIndexOf("```", StringComparison.Ordinal);
        return firstNewline > 0 && lastFence > firstNewline ? t[(firstNewline + 1)..lastFence].Trim() : t;
    }
}
