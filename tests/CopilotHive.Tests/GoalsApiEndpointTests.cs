using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using CopilotHive.Goals;
using Microsoft.Extensions.DependencyInjection;

namespace CopilotHive.Tests;

/// <summary>
/// Integration tests for the Goals REST API endpoints (<c>/api/goals</c>).
/// Verifies correct HTTP status codes and response bodies for all CRUD operations,
/// including error cases that previously returned 500 Internal Server Error.
/// </summary>
public class GoalsApiEndpointTests : IClassFixture<HiveTestFactory>
{
    private static readonly JsonSerializerOptions JsonOpts =
        new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private readonly HttpClient _client;
    private readonly HiveTestFactory _factory;

    /// <summary>Initialises the test with a shared <see cref="HiveTestFactory"/> fixture.</summary>
    /// <param name="factory">The shared test factory.</param>
    public GoalsApiEndpointTests(HiveTestFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static string UniqueId() =>
        "test-" + Guid.NewGuid().ToString("N")[..16];

    private static StringContent GoalJson(string id, string description = "Test goal") =>
        new(JsonSerializer.Serialize(new { id, description }, JsonOpts), Encoding.UTF8, "application/json");

    // ── POST /api/goals ───────────────────────────────────────────────────

    [Fact]
    public async Task PostGoal_ValidGoal_Returns201Created()
    {
        var id = UniqueId();
        var response = await _client.PostAsync("/api/goals", GoalJson(id),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        // Location header may be absolute or relative depending on the test host
        var location = response.Headers.Location?.ToString();
        Assert.NotNull(location);
        Assert.Contains(id, location);
    }

    [Fact]
    public async Task PostGoal_ValidGoal_ResponseBodyContainsId()
    {
        var id = UniqueId();
        var response = await _client.PostAsync("/api/goals", GoalJson(id),
            TestContext.Current.CancellationToken);

        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        using var doc = JsonDocument.Parse(body);
        Assert.Equal(id, doc.RootElement.GetProperty("id").GetString());
    }

    [Fact]
    public async Task PostGoal_InvalidId_UppercaseLetters_Returns400BadRequest()
    {
        // GoalId.Validate throws ArgumentException for uppercase IDs.
        // The endpoint must catch this and return 400, not 500.
        var response = await _client.PostAsync("/api/goals",
            GoalJson("Invalid-Id-WithUppercase"),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task PostGoal_InvalidId_StartsWithHyphen_Returns400BadRequest()
    {
        var response = await _client.PostAsync("/api/goals",
            GoalJson("-starts-with-hyphen"),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task PostGoal_InvalidId_EndsWithHyphen_Returns400BadRequest()
    {
        var response = await _client.PostAsync("/api/goals",
            GoalJson("ends-with-hyphen-"),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task PostGoal_InvalidId_ContainsSpecialChars_Returns400BadRequest()
    {
        var response = await _client.PostAsync("/api/goals",
            GoalJson("invalid_id!"),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task PostGoal_InvalidId_Returns400WithErrorField()
    {
        var response = await _client.PostAsync("/api/goals",
            GoalJson("HasUpperCase"),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        using var doc = JsonDocument.Parse(body);
        Assert.True(doc.RootElement.TryGetProperty("error", out var errVal));
        Assert.False(string.IsNullOrEmpty(errVal.GetString()));
    }

    [Fact]
    public async Task PostGoal_DuplicateId_Returns409Conflict()
    {
        // Duplicate IDs previously caused 500 via SqliteException.
        // The endpoint must catch this and return 409.
        var id = UniqueId();
        var first = await _client.PostAsync("/api/goals", GoalJson(id),
            TestContext.Current.CancellationToken);
        first.EnsureSuccessStatusCode();

        var second = await _client.PostAsync("/api/goals", GoalJson(id),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
    }

    [Fact]
    public async Task PostGoal_DuplicateId_Returns409WithErrorField()
    {
        var id = UniqueId();
        await _client.PostAsync("/api/goals", GoalJson(id), TestContext.Current.CancellationToken);

        var response = await _client.PostAsync("/api/goals", GoalJson(id),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        using var doc = JsonDocument.Parse(body);
        Assert.True(doc.RootElement.TryGetProperty("error", out var errVal));
        Assert.False(string.IsNullOrEmpty(errVal.GetString()));
    }

    // ── GET /api/goals ────────────────────────────────────────────────────

    [Fact]
    public async Task GetAllGoals_Returns200()
    {
        var response = await _client.GetAsync("/api/goals",
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task GetAllGoals_ReturnsJsonArray()
    {
        var response = await _client.GetAsync("/api/goals",
            TestContext.Current.CancellationToken);

        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        using var doc = JsonDocument.Parse(body);
        Assert.Equal(JsonValueKind.Array, doc.RootElement.ValueKind);
    }

    // ── GET /api/goals/{id} ───────────────────────────────────────────────

    [Fact]
    public async Task GetGoalById_ExistingGoal_Returns200()
    {
        var id = UniqueId();
        await _client.PostAsync("/api/goals", GoalJson(id), TestContext.Current.CancellationToken);

        var response = await _client.GetAsync($"/api/goals/{id}",
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task GetGoalById_NonExistentGoal_Returns404()
    {
        var response = await _client.GetAsync("/api/goals/nonexistent-goal-id-xyz",
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // ── PATCH /api/goals/{id}/status ──────────────────────────────────────

    [Fact]
    public async Task PatchGoalStatus_ValidTransition_Returns200()
    {
        var id = UniqueId();
        await _client.PostAsync("/api/goals", GoalJson(id), TestContext.Current.CancellationToken);

        var response = await _client.PatchAsync(
            $"/api/goals/{id}/status",
            new StringContent(JsonSerializer.Serialize(new { status = "completed" }, JsonOpts),
                Encoding.UTF8, "application/json"),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task PatchGoalStatus_InvalidStatus_Returns400()
    {
        var id = UniqueId();
        await _client.PostAsync("/api/goals", GoalJson(id), TestContext.Current.CancellationToken);

        var response = await _client.PatchAsync(
            $"/api/goals/{id}/status",
            new StringContent(JsonSerializer.Serialize(new { status = "notastatus" }, JsonOpts),
                Encoding.UTF8, "application/json"),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task UpdateGoalStatus_DraftGoal_ReturnsPendingStatus()
    {
        // Arrange: create a goal (starts in Pending) then move it to Draft
        var id = UniqueId();
        await _client.PostAsync("/api/goals", GoalJson(id), TestContext.Current.CancellationToken);
        await _client.PatchAsync(
            $"/api/goals/{id}/status",
            new StringContent(JsonSerializer.Serialize(new { status = "Draft" }, JsonOpts),
                Encoding.UTF8, "application/json"),
            TestContext.Current.CancellationToken);

        // Act: transition Draft → Pending
        var response = await _client.PatchAsync(
            $"/api/goals/{id}/status",
            new StringContent(JsonSerializer.Serialize(new { status = "Pending" }, JsonOpts),
                Encoding.UTF8, "application/json"),
            TestContext.Current.CancellationToken);

        // Assert: endpoint returns success
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        // Assert: goal status is now Pending (1 as integer, or "Pending" as string)
        var getResponse = await _client.GetAsync($"/api/goals/{id}",
            TestContext.Current.CancellationToken);
        getResponse.EnsureSuccessStatusCode();
        var body = await getResponse.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        using var doc = JsonDocument.Parse(body);
        var statusElement = doc.RootElement.GetProperty("status");
        // GoalStatus.Pending = 1 when serialized as integer; may also be "Pending" as string
        var isPending = statusElement.ValueKind == JsonValueKind.Number
            ? statusElement.GetInt32() == (int)GoalStatus.Pending
            : string.Equals(statusElement.GetString(), "Pending", StringComparison.OrdinalIgnoreCase);
        Assert.True(isPending, $"Expected status to be Pending but got: {statusElement}");
    }

    [Fact]
    public async Task PatchGoalStatus_NonExistentGoal_Returns404()
    {
        var response = await _client.PatchAsync(
            "/api/goals/nonexistent-goal-xyz/status",
            new StringContent(JsonSerializer.Serialize(new { status = "completed" }, JsonOpts),
                Encoding.UTF8, "application/json"),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // ── DELETE /api/goals/{id} ────────────────────────────────────────────

    [Fact]
    public async Task DeleteGoal_ExistingGoal_Returns204NoContent()
    {
        var id = UniqueId();
        await _client.PostAsync("/api/goals", GoalJson(id), TestContext.Current.CancellationToken);
        // Patch to Draft so deletion is allowed
        await _client.PatchAsync($"/api/goals/{id}/status",
            new StringContent(JsonSerializer.Serialize(new { status = "Draft" }, JsonOpts), Encoding.UTF8, "application/json"),
            TestContext.Current.CancellationToken);

        var response = await _client.DeleteAsync($"/api/goals/{id}",
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    [Fact]
    public async Task DeleteGoal_NonExistentGoal_Returns404()
    {
        var response = await _client.DeleteAsync("/api/goals/nonexistent-goal-xyz",
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task DeleteGoal_PendingGoal_Returns400BadRequest()
    {
        var id = UniqueId();
        // Default status is Pending
        await _client.PostAsync("/api/goals", GoalJson(id), TestContext.Current.CancellationToken);

        var response = await _client.DeleteAsync($"/api/goals/{id}",
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Contains("Only Draft or Failed goals can be deleted", body);
    }

    // ── GET /api/goals/search ─────────────────────────────────────────────

    [Fact]
    public async Task SearchGoals_ReturnsMatchingGoals()
    {
        var id = UniqueId();
        var desc = "unique-description-" + id;
        await _client.PostAsync("/api/goals",
            GoalJson(id, desc), TestContext.Current.CancellationToken);

        var response = await _client.GetAsync(
            $"/api/goals/search?q={id}",
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        using var doc = JsonDocument.Parse(body);
        Assert.Equal(JsonValueKind.Array, doc.RootElement.ValueKind);
        Assert.True(doc.RootElement.GetArrayLength() >= 1);
    }

    [Fact]
    public async Task SearchGoals_NoMatches_ReturnsEmptyArray()
    {
        var response = await _client.GetAsync(
            "/api/goals/search?q=completely-unique-no-matches-xyz123abc",
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        using var doc = JsonDocument.Parse(body);
        Assert.Equal(JsonValueKind.Array, doc.RootElement.ValueKind);
        Assert.Equal(0, doc.RootElement.GetArrayLength());
    }
}
