extern alias WorkerAssembly;

using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using WorkerAssembly::CopilotHive.SDK;
using CopilotChoiceMergingHandler = WorkerAssembly::CopilotHive.SDK.ChatClientFactory.CopilotChoiceMergingHandler;

namespace CopilotHive.Tests.Worker;

/// <summary>
/// Tests for the CopilotChoiceMergingHandler in ChatClientFactory.
/// Validates that the handler correctly handles edge cases in the Copilot API responses.
/// </summary>
public sealed class CopilotChoiceMergingHandlerTests
{
    /// <summary>
    /// When the Copilot API returns an empty choices array, the handler must synthesize
    /// a minimal stop choice so the OpenAI SDK doesn't crash with "Index was out of range".
    /// </summary>
    [Fact]
    public async Task EmptyChoicesArray_SynthesizesStopChoice()
    {
        var responseBody = """{"choices":[],"created":1774365638,"id":"test","model":"claude-opus-4.6"}""";
        var handler = CreateHandler(responseBody);
        var client = new HttpClient(handler);

        var response = await client.SendAsync(CreatePostRequest(), TestContext.Current.CancellationToken);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        var json = JsonNode.Parse(body);
        var choices = json?["choices"]?.AsArray();
        Assert.NotNull(choices);
        Assert.Single(choices);
        Assert.Equal("stop", choices[0]?["finish_reason"]?.GetValue<string>());
        Assert.Equal("assistant", choices[0]?["message"]?["role"]?.GetValue<string>());
    }

    /// <summary>
    /// When there's exactly one choice, the handler passes it through unchanged.
    /// </summary>
    [Fact]
    public async Task SingleChoice_PassesThrough()
    {
        var responseBody = """{"choices":[{"index":0,"message":{"role":"assistant","content":"hello"},"finish_reason":"stop"}],"model":"test"}""";
        var handler = CreateHandler(responseBody);
        var client = new HttpClient(handler);

        var response = await client.SendAsync(CreatePostRequest(), TestContext.Current.CancellationToken);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        var json = JsonNode.Parse(body);
        var choices = json?["choices"]?.AsArray();
        Assert.NotNull(choices);
        Assert.Single(choices);
        Assert.Equal("hello", choices[0]?["message"]?["content"]?.GetValue<string>());
    }

    /// <summary>
    /// When there are two choices (text + tool_calls), they get merged into one.
    /// </summary>
    [Fact]
    public async Task TwoChoices_TextAndToolCalls_MergesIntoOne()
    {
        var responseBody = """
        {
            "choices": [
                {"index":0,"message":{"role":"assistant","content":"I'll do that"},"finish_reason":"tool_calls"},
                {"index":1,"message":{"role":"assistant","tool_calls":[{"id":"tc1","type":"function","function":{"name":"test","arguments":"{}"}}]},"finish_reason":"tool_calls"}
            ],
            "model":"test"
        }
        """;
        var handler = CreateHandler(responseBody);
        var client = new HttpClient(handler);

        var response = await client.SendAsync(CreatePostRequest(), TestContext.Current.CancellationToken);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        var json = JsonNode.Parse(body);
        var choices = json?["choices"]?.AsArray();
        Assert.NotNull(choices);
        Assert.Single(choices);

        var merged = choices[0]?["message"];
        Assert.NotNull(merged);
        Assert.Equal("I'll do that", merged["content"]?.GetValue<string>());
        Assert.NotNull(merged["tool_calls"]);
    }

    private static CopilotChoiceMergingHandler CreateHandler(string responseBody) =>
        new(new FakeResponseHandler(responseBody));

    private static HttpRequestMessage CreatePostRequest()
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "https://api.test.com/chat/completions");
        request.Content = new StringContent("{}", Encoding.UTF8, "application/json");
        return request;
    }

    /// <summary>Fake inner handler that returns a canned response.</summary>
    private sealed class FakeResponseHandler(string responseBody) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken ct)
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(responseBody, Encoding.UTF8, "application/json"),
            };
            return Task.FromResult(response);
        }
    }
}
