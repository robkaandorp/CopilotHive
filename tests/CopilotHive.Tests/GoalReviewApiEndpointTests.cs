using System.Net;
using System.Text;
using System.Text.Json;
using CopilotHive.Goals;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;

namespace CopilotHive.Tests;

/// <summary>
/// Integration tests for the POST <c>/api/goals/{id}/review</c> endpoint.
/// The class participates in the shared <c>HiveIntegration</c> collection so it does
/// not run in parallel with other integration tests that mutate the process-level
/// <c>STATE_DIR</c> environment variable. Each test creates its own isolated
/// <see cref="HiveTestFactory"/> with an LLM stub to avoid depending on a real model.
/// </summary>
[Collection("HiveIntegration")]
public sealed class GoalReviewApiEndpointTests
{
    private static readonly JsonSerializerOptions JsonOpts =
        new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    /// <summary>
    /// Required by xUnit to bind the shared collection fixture. The tests do not use the
    /// shared factory directly because each test needs its own isolated application
    /// instance with a stubbed chat client.
    /// </summary>
    public GoalReviewApiEndpointTests(HiveTestFactory _) { }

    private static HiveTestFactory CreateFactory() =>
        new HiveTestFactory
        {
            ReviewChatClientFactory = _ => new StubChatClient(
                """{"verdict":"Approved","issues":[],"verified":[],"recommendation":"Looks good"}"""),
        };

    private static StringContent GoalJson(string id, string description = "Test review goal")
    {
        var goalData = new { id, description };
        return new StringContent(JsonSerializer.Serialize(goalData, JsonOpts), Encoding.UTF8, "application/json");
    }

    [Fact]
    public async Task PostReview_ValidDraftGoal_Returns200WithApprovedVerdict()
    {
        var id = "test-" + Guid.NewGuid().ToString("N")[..16];
        using var factory = CreateFactory();
        using var client = factory.CreateClient();

        using (var scope = factory.Services.CreateScope())
        {
            var store = scope.ServiceProvider.GetRequiredService<IGoalStore>();
            var goal = new Goal { Id = id, Description = "Test review goal" };
            await store.CreateGoalAsync(goal, TestContext.Current.CancellationToken);
            goal.Status = GoalStatus.Draft;
            await store.UpdateGoalAsync(goal, TestContext.Current.CancellationToken);
        }

        var response = await client.PostAsync($"/api/goals/{id}/review", null, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Contains("Approved", body);
    }

    [Fact]
    public async Task PostReview_NonExistentGoal_Returns404()
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient();

        var response = await client.PostAsync("/api/goals/nonexistent-review-goal/review", null, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task PostReview_NonDraftGoal_Returns400()
    {
        var id = "test-" + Guid.NewGuid().ToString("N")[..16];
        using var factory = CreateFactory();
        using var client = factory.CreateClient();

        using (var scope = factory.Services.CreateScope())
        {
            var store = scope.ServiceProvider.GetRequiredService<IGoalStore>();
            var goal = new Goal { Id = id, Description = "Test review goal" };
            await store.CreateGoalAsync(goal, TestContext.Current.CancellationToken);
        }

        var response = await client.PostAsync($"/api/goals/{id}/review", null, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task PostReview_ReviewAlreadyPending_Returns409()
    {
        var id = "test-" + Guid.NewGuid().ToString("N")[..16];
        using var factory = CreateFactory();
        using var client = factory.CreateClient();

        using (var scope = factory.Services.CreateScope())
        {
            var store = scope.ServiceProvider.GetRequiredService<IGoalStore>();
            var goal = new Goal { Id = id, Description = "Test review goal" };
            await store.CreateGoalAsync(goal, TestContext.Current.CancellationToken);
            goal.Status = GoalStatus.Draft;
            goal.ReviewStatus = ReviewStatus.Pending;
            await store.UpdateGoalAsync(goal, TestContext.Current.CancellationToken);
        }

        var response = await client.PostAsync($"/api/goals/{id}/review", null, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    private sealed class StubChatClient(string reply) : IChatClient
    {
        public ChatClientMetadata Metadata => new("stub", null, "stub-model");

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, reply))
            {
                FinishReason = ChatFinishReason.Stop,
            });
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation]
            CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            yield return new ChatResponseUpdate(ChatRole.Assistant, [new TextContent(reply)]);
            yield return new ChatResponseUpdate { FinishReason = ChatFinishReason.Stop, Role = ChatRole.Assistant };
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }
}
