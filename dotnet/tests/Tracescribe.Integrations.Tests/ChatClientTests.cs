using System.Net;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;
using Tracescribe.Digest;
using Tracescribe.Extensions.AI;
using Tracescribe.SemanticConventions;
using Xunit;

namespace Tracescribe.Integrations.Tests;

public class ChatClientTests
{
    private sealed class FakeChatClient(Func<ChatResponse> respond) : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(respond());

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var response = respond();
            foreach (var update in response.ToChatResponseUpdates())
            {
                await Task.Yield();
                yield return update;
            }
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }

    private static ChatResponse Text(string text, ChatFinishReason? finish = null) =>
        new(new ChatMessage(ChatRole.Assistant, text)) { FinishReason = finish ?? ChatFinishReason.Stop };

    private static (IChatClient Client, TraceCollector Collector, string Intent) Setup(Func<ChatResponse> respond, Action<TracescribeChatOptions>? configure = null)
    {
        var intent = "chat.test." + Guid.NewGuid().ToString("N");
        var client = new ChatClientBuilder(new FakeChatClient(respond)).UseTracescribe(intent, configure).Build();
        return (client, new TraceCollector(s => s.Name == Scribe.SourceName), intent);
    }

    private static SpanRecord SpanFor(TraceCollector collector, string intent) =>
        Assert.Single(collector.Spans, s => s.Name == intent);

    [Fact]
    public async Task Successful_response_is_a_success()
    {
        var (client, collector, intent) = Setup(() => Text("Claim summary"));
        using (collector)
        {
            await client.GetResponseAsync("summarize");
            var s = SpanFor(collector, intent);
            Assert.Equal(OutcomeStatusValues.Success, s.Get(TracescribeAttributes.OutcomeStatus));
            Assert.Equal("stop", s.Get(TracescribeAttributes.ChatFinishReason));
        }
    }

    [Fact]
    public async Task Evaluator_mismatch_is_a_semantic_failure()
    {
        var (client, collector, intent) = Setup(() => Text("Sure! Here is the claim summary."), o => o.Evaluator = ChatOutcomeEvaluators.RequireJson());
        using (collector)
        {
            await client.GetResponseAsync("summarize as JSON");
            var s = SpanFor(collector, intent);
            Assert.Equal(FailureClassValues.Semantic, s.Get(TracescribeAttributes.FailureClass));
            Assert.Equal("false", s.Get(TracescribeAttributes.OutcomeMatched));
            Assert.StartsWith("response is not valid JSON", s.Get(TracescribeAttributes.FailureReason));
            Assert.Equal(System.Diagnostics.ActivityStatusCode.Unset, s.Status);
        }
    }

    [Fact]
    public async Task Fenced_json_passes_the_json_check()
    {
        var (client, collector, intent) = Setup(() => Text("```json\n{\"severity\":\"low\"}\n```"), o => o.Evaluator = ChatOutcomeEvaluators.RequireJson());
        using (collector)
        {
            await client.GetResponseAsync("x");
            Assert.Equal(OutcomeStatusValues.Success, SpanFor(collector, intent).Get(TracescribeAttributes.OutcomeStatus));
        }
    }

    [Fact]
    public async Task Content_filter_is_a_policy_failure_and_length_is_partial()
    {
        var (filtered, c1, i1) = Setup(() => Text("", ChatFinishReason.ContentFilter));
        var (truncated, c2, i2) = Setup(() => Text("The claim was", ChatFinishReason.Length));
        using (c1)
        using (c2)
        {
            await filtered.GetResponseAsync("x");
            await truncated.GetResponseAsync("x");
            Assert.Equal(FailureClassValues.Policy, SpanFor(c1, i1).Get(TracescribeAttributes.FailureClass));
            Assert.Equal(OutcomeStatusValues.Partial, SpanFor(c2, i2).Get(TracescribeAttributes.OutcomeStatus));
        }
    }

    [Fact]
    public async Task Provider_errors_are_classified()
    {
        var (client, collector, intent) = Setup(() => throw new HttpRequestException("slow down", null, HttpStatusCode.TooManyRequests));
        using (collector)
        {
            await Assert.ThrowsAsync<HttpRequestException>(() => client.GetResponseAsync("x"));
            var s = SpanFor(collector, intent);
            Assert.Equal(FailureClassValues.Policy, s.Get(TracescribeAttributes.FailureClass));
            Assert.Equal("true", s.Get(TracescribeAttributes.FailureRetryable));
        }
    }

    [Fact]
    public async Task Streaming_is_evaluated_when_the_stream_completes()
    {
        var (client, collector, intent) = Setup(
            () => new ChatResponse(new ChatMessage(ChatRole.Assistant, [new FunctionCallContent("call-1", "lookup_policy")])) { FinishReason = ChatFinishReason.ToolCalls },
            o => o.Evaluator = ChatOutcomeEvaluators.RequireToolCall("decode_vin"));
        using (collector)
        {
            await foreach (var _ in client.GetStreamingResponseAsync("x"))
            {
            }

            var s = SpanFor(collector, intent);
            Assert.Equal(FailureClassValues.Semantic, s.Get(TracescribeAttributes.FailureClass));
            Assert.Equal("called lookup_policy", s.Get(TracescribeAttributes.OutcomeActual));
            Assert.Equal("tool_calls", s.Get(TracescribeAttributes.ChatFinishReason));
        }
    }

    [Fact]
    public async Task Intent_can_be_overridden_per_call()
    {
        var (client, collector, _) = Setup(() => Text("ok"));
        var perCall = "chat.override." + Guid.NewGuid().ToString("N");
        using (collector)
        {
            var options = new ChatOptions { AdditionalProperties = new() { [TracescribeChatOptions.IntentProperty] = perCall } };
            await client.GetResponseAsync("x", options);
            Assert.Equal(perCall, SpanFor(collector, perCall).Get(TracescribeAttributes.Intent));
        }
    }
}
