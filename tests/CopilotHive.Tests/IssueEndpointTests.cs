using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

using CopilotHive.Goals;
using CopilotHive.Persistence;

using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace CopilotHive.Tests;

/// <summary>
/// Integration tests for the issues REST API endpoints (<c>/api/issues</c>).
/// Boots the real application with an in-memory SQLite database (via
/// <see cref="CopilotHiveDbContext.CreateInMemory"/>) and exercises create, read,
/// update, and delete operations including validation error cases.
/// </summary>
[Collection("HiveIntegration")]
public class IssueEndpointTests
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new Program.GlobalStringEnumConverter() },
    };

    private readonly HiveTestFactory _baseFactory;

    /// <summary>Receives the shared <see cref="HiveTestFactory"/> fixture for this collection.</summary>
    /// <param name="factory">The shared test factory.</param>
    public IssueEndpointTests(HiveTestFactory factory)
    {
        _baseFactory = factory;
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Creates a <see cref="WebApplicationFactory{TEntryPoint}"/> derived from the shared fixture
    /// whose <see cref="IIssueStore"/> is backed by a fresh in-memory SQLite database, so every test
    /// gets an isolated, throwaway issue store. Uses <c>WithWebHostBuilder</c> so it does NOT create
    /// a new <see cref="HiveTestFactory"/> or touch the process-wide <c>STATE_DIR</c>.
    /// </summary>
    private WebApplicationFactory<Program> CreateFactory()
    {
        var factory = _baseFactory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureServices(services =>
            {
                var descriptors = services
                    .Where(d => d.ServiceType == typeof(IIssueStore))
                    .ToList();
                foreach (var d in descriptors)
                    services.Remove(d);

                services.AddSingleton<IIssueStore>(_ =>
                    new IssueStore(
                        CopilotHiveDbContext.CreateInMemory(),
                        NullLogger<IssueStore>.Instance));
            });
        });
        return factory;
    }

    private static StringContent JsonBody(object body) =>
        new(JsonSerializer.Serialize(body, JsonOpts), Encoding.UTF8, "application/json");

    private static async Task<JsonElement> ReadJsonAsync(HttpResponseMessage response)
    {
        response.EnsureSuccessStatusCode();
        var stream = await response.Content.ReadAsStreamAsync(TestContext.Current.CancellationToken);
        using var doc = await JsonDocument.ParseAsync(stream,
            cancellationToken: TestContext.Current.CancellationToken);
        return doc.RootElement.Clone();
    }

    private static async Task<string> ReadBodyAsync(HttpResponseMessage response) =>
        await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

    private static object CreateBody(
        string? id = null,
        IssueType? type = IssueType.Bug,
        string? title = "Test issue",
        string? description = "Test description",
        IssueSeverity? severity = null,
        string[]? repositoryNames = null,
        string? sourceGoalId = null,
        string? sourceRole = null,
        int? sourceIteration = null)
        => new
        {
            id,
            type,
            title,
            description,
            severity,
            repositoryNames,
            sourceGoalId,
            sourceRole,
            sourceIteration,
        };

    private static object PatchBody(
        IssueType? type = null,
        string? title = null,
        string? description = null,
        IssueSeverity? severity = null,
        IssueStatus? status = null,
        string[]? repositoryNames = null,
        string? linkedGoalId = null)
        => new
        {
            type,
            title,
            description,
            severity,
            status,
            repositoryNames,
            linkedGoalId,
        };

    // ── GET /api/issues (list with filters) ─────────────────────────────────

    [Fact]
    public async Task GetIssues_AllFiveFilters_ReturnsMatchingIssues()
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient();

        // Matching issue: code_quality, in_progress, medium, CopilotHive, goal-1.
        var matchResponse = await client.PostAsync("/api/issues", JsonBody(CreateBody(
            id: "quality-issue-1",
            type: IssueType.CodeQuality,
            title: "Naming inconsistency",
            description: "Method names are inconsistent.",
            severity: IssueSeverity.Medium,
            repositoryNames: ["CopilotHive"],
            sourceGoalId: "goal-1")), TestContext.Current.CancellationToken);
        var match = await ReadJsonAsync(matchResponse);
        var matchId = match.GetProperty("id").GetString()!;

        var patchResponse = await client.PatchAsync(
            $"/api/issues/{matchId}",
            JsonBody(PatchBody(status: IssueStatus.InProgress)),
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, patchResponse.StatusCode);

        // Non-matching issue (different type, severity, repository, source goal).
        await client.PostAsync("/api/issues", JsonBody(CreateBody(
            id: "other-issue",
            type: IssueType.Bug,
            title: "A bug",
            description: "Something broke.",
            severity: IssueSeverity.High,
            repositoryNames: ["OtherRepo"],
            sourceGoalId: "goal-2")), TestContext.Current.CancellationToken);

        var response = await client.GetAsync(
            "/api/issues?type=code_quality&status=in_progress&severity=medium&repository=CopilotHive&source_goal_id=goal-1",
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await ReadJsonAsync(response);
        Assert.Equal(JsonValueKind.Array, body.ValueKind);
        Assert.Equal(1, body.GetArrayLength());
        Assert.Equal("quality-issue-1", body[0].GetProperty("id").GetString());
        Assert.Equal("code_quality", body[0].GetProperty("type").GetString());
        Assert.Equal("in_progress", body[0].GetProperty("status").GetString());
        Assert.Equal("medium", body[0].GetProperty("severity").GetString());
    }

    [Fact]
    public async Task GetIssues_WithoutFilters_ReturnsAll()
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient();

        await client.PostAsync("/api/issues", JsonBody(CreateBody(id: "issue-a")),
            TestContext.Current.CancellationToken);
        await client.PostAsync("/api/issues", JsonBody(CreateBody(id: "issue-b")),
            TestContext.Current.CancellationToken);

        var response = await client.GetAsync("/api/issues", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await ReadJsonAsync(response);
        Assert.Equal(JsonValueKind.Array, body.ValueKind);
        Assert.Equal(2, body.GetArrayLength());
    }

    [Fact]
    public async Task GetIssues_FilterByTypeOnly_ExcludesOtherType()
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient();

        // Two issues identical in every filter dimension except Type.
        await client.PostAsync("/api/issues", JsonBody(CreateBody(
            id: "type-bug",
            type: IssueType.Bug,
            title: "Same title",
            description: "Same description",
            severity: IssueSeverity.Low,
            repositoryNames: ["CopilotHive"],
            sourceGoalId: "goal-1")), TestContext.Current.CancellationToken);
        await client.PostAsync("/api/issues", JsonBody(CreateBody(
            id: "type-suggestion",
            type: IssueType.Suggestion,
            title: "Same title",
            description: "Same description",
            severity: IssueSeverity.Low,
            repositoryNames: ["CopilotHive"],
            sourceGoalId: "goal-1")), TestContext.Current.CancellationToken);

        var response = await client.GetAsync("/api/issues?type=bug",
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await ReadJsonAsync(response);
        Assert.Equal(JsonValueKind.Array, body.ValueKind);
        Assert.Equal(1, body.GetArrayLength());
        Assert.Equal("type-bug", body[0].GetProperty("id").GetString());
    }

    [Fact]
    public async Task GetIssues_FilterByStatusOnly_ExcludesOtherStatus()
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient();

        // Two issues identical in every filter dimension except Status.
        await client.PostAsync("/api/issues", JsonBody(CreateBody(
            id: "status-open",
            type: IssueType.Bug,
            title: "Same title",
            description: "Same description",
            severity: IssueSeverity.Low,
            repositoryNames: ["CopilotHive"],
            sourceGoalId: "goal-1")), TestContext.Current.CancellationToken);
        var resolvedResponse = await client.PostAsync("/api/issues", JsonBody(CreateBody(
            id: "status-resolved",
            type: IssueType.Bug,
            title: "Same title",
            description: "Same description",
            severity: IssueSeverity.Low,
            repositoryNames: ["CopilotHive"],
            sourceGoalId: "goal-1")), TestContext.Current.CancellationToken);
        var resolved = await ReadJsonAsync(resolvedResponse);
        var resolvedId = resolved.GetProperty("id").GetString()!;
        var patchResponse = await client.PatchAsync($"/api/issues/{resolvedId}",
            JsonBody(PatchBody(status: IssueStatus.Resolved)), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, patchResponse.StatusCode);

        var response = await client.GetAsync("/api/issues?status=open",
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await ReadJsonAsync(response);
        Assert.Equal(JsonValueKind.Array, body.ValueKind);
        Assert.Equal(1, body.GetArrayLength());
        Assert.Equal("status-open", body[0].GetProperty("id").GetString());
    }

    [Fact]
    public async Task GetIssues_FilterBySeverityOnly_ExcludesOtherSeverity()
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient();

        // Two issues identical in every filter dimension except Severity.
        await client.PostAsync("/api/issues", JsonBody(CreateBody(
            id: "severity-high",
            type: IssueType.Bug,
            title: "Same title",
            description: "Same description",
            severity: IssueSeverity.High,
            repositoryNames: ["CopilotHive"],
            sourceGoalId: "goal-1")), TestContext.Current.CancellationToken);
        await client.PostAsync("/api/issues", JsonBody(CreateBody(
            id: "severity-low",
            type: IssueType.Bug,
            title: "Same title",
            description: "Same description",
            severity: IssueSeverity.Low,
            repositoryNames: ["CopilotHive"],
            sourceGoalId: "goal-1")), TestContext.Current.CancellationToken);

        var response = await client.GetAsync("/api/issues?severity=high",
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await ReadJsonAsync(response);
        Assert.Equal(JsonValueKind.Array, body.ValueKind);
        Assert.Equal(1, body.GetArrayLength());
        Assert.Equal("severity-high", body[0].GetProperty("id").GetString());
    }

    [Fact]
    public async Task GetIssues_FilterByRepositoryOnly_ExcludesOtherRepository()
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient();

        // Two issues identical in every filter dimension except RepositoryNames.
        await client.PostAsync("/api/issues", JsonBody(CreateBody(
            id: "repo-match",
            type: IssueType.Bug,
            title: "Same title",
            description: "Same description",
            severity: IssueSeverity.Low,
            repositoryNames: ["CopilotHive"],
            sourceGoalId: "goal-1")), TestContext.Current.CancellationToken);
        await client.PostAsync("/api/issues", JsonBody(CreateBody(
            id: "repo-other",
            type: IssueType.Bug,
            title: "Same title",
            description: "Same description",
            severity: IssueSeverity.Low,
            repositoryNames: ["OtherRepo"],
            sourceGoalId: "goal-1")), TestContext.Current.CancellationToken);

        var response = await client.GetAsync("/api/issues?repository=CopilotHive",
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await ReadJsonAsync(response);
        Assert.Equal(JsonValueKind.Array, body.ValueKind);
        Assert.Equal(1, body.GetArrayLength());
        Assert.Equal("repo-match", body[0].GetProperty("id").GetString());
    }

    [Fact]
    public async Task GetIssues_FilterByRepository_CaseInsensitiveMatch()
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient();

        // Issue whose RepositoryNames contains "CopilotHive"; query uses lowercase.
        await client.PostAsync("/api/issues", JsonBody(CreateBody(
            id: "case-match",
            type: IssueType.Bug,
            title: "Same title",
            description: "Same description",
            severity: IssueSeverity.Low,
            repositoryNames: ["CopilotHive"],
            sourceGoalId: "goal-1")), TestContext.Current.CancellationToken);
        await client.PostAsync("/api/issues", JsonBody(CreateBody(
            id: "case-other",
            type: IssueType.Bug,
            title: "Same title",
            description: "Same description",
            severity: IssueSeverity.Low,
            repositoryNames: ["OtherRepo"],
            sourceGoalId: "goal-1")), TestContext.Current.CancellationToken);

        var response = await client.GetAsync("/api/issues?repository=copilothive",
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await ReadJsonAsync(response);
        Assert.Equal(JsonValueKind.Array, body.ValueKind);
        Assert.Equal(1, body.GetArrayLength());
        Assert.Equal("case-match", body[0].GetProperty("id").GetString());
    }

    [Fact]
    public async Task GetIssues_FilterBySourceGoalIdOnly_ExcludesOtherGoal()
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient();

        // Two issues identical in every filter dimension except SourceGoalId.
        await client.PostAsync("/api/issues", JsonBody(CreateBody(
            id: "goal-1-issue",
            type: IssueType.Bug,
            title: "Same title",
            description: "Same description",
            severity: IssueSeverity.Low,
            repositoryNames: ["CopilotHive"],
            sourceGoalId: "goal-1")), TestContext.Current.CancellationToken);
        await client.PostAsync("/api/issues", JsonBody(CreateBody(
            id: "goal-2-issue",
            type: IssueType.Bug,
            title: "Same title",
            description: "Same description",
            severity: IssueSeverity.Low,
            repositoryNames: ["CopilotHive"],
            sourceGoalId: "goal-2")), TestContext.Current.CancellationToken);

        var response = await client.GetAsync("/api/issues?source_goal_id=goal-1",
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await ReadJsonAsync(response);
        Assert.Equal(JsonValueKind.Array, body.ValueKind);
        Assert.Equal(1, body.GetArrayLength());
        Assert.Equal("goal-1-issue", body[0].GetProperty("id").GetString());
    }

    [Fact]
    public async Task GetIssues_FilterByLinkedGoalIdOnly_ExcludesOtherGoal()
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient();

        // Two issues identical in every filter dimension except LinkedGoalId.
        await client.PostAsync("/api/issues", JsonBody(CreateBody(
            id: "linked-1-issue",
            type: IssueType.Bug,
            title: "Same title",
            description: "Same description",
            severity: IssueSeverity.Low,
            repositoryNames: ["CopilotHive"])), TestContext.Current.CancellationToken);
        await client.PostAsync("/api/issues", JsonBody(CreateBody(
            id: "linked-2-issue",
            type: IssueType.Bug,
            title: "Same title",
            description: "Same description",
            severity: IssueSeverity.Low,
            repositoryNames: ["CopilotHive"])), TestContext.Current.CancellationToken);

        // Set LinkedGoalId via PATCH (the create request has no linkedGoalId field).
        var patch1 = await client.PatchAsync("/api/issues/linked-1-issue",
            JsonBody(PatchBody(linkedGoalId: "goal-1")), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, patch1.StatusCode);
        var patch2 = await client.PatchAsync("/api/issues/linked-2-issue",
            JsonBody(PatchBody(linkedGoalId: "goal-2")), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, patch2.StatusCode);

        var response = await client.GetAsync("/api/issues?linked_goal_id=goal-1",
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await ReadJsonAsync(response);
        Assert.Equal(JsonValueKind.Array, body.ValueKind);
        Assert.Equal(1, body.GetArrayLength());
        Assert.Equal("linked-1-issue", body[0].GetProperty("id").GetString());
    }

    [Fact]
    public async Task GetIssues_InvalidType_Returns400()
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/api/issues?type=not-a-type",
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task GetIssues_CommaCombinedType_Returns400()
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/api/issues?type=bug,suggestion",
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task GetIssues_NumericType_Returns400()
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient();

        // "1" is the integer value of IssueType.Bug — the int.TryParse guard must reject it.
        var response = await client.GetAsync("/api/issues?type=1",
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task GetIssues_InvalidStatus_Returns400()
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/api/issues?status=invalid",
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task GetIssues_CommaCombinedStatus_Returns400()
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient();

        // Enum.TryParse accepts "open,closed" via bitwise OR — the comma guard must reject it.
        var response = await client.GetAsync("/api/issues?status=open,closed",
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task GetIssues_NumericStatus_Returns400()
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient();

        // "1" is the integer value of IssueStatus.Triaged — the int.TryParse guard must reject it.
        var response = await client.GetAsync("/api/issues?status=1",
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task GetIssues_InvalidSeverity_Returns400()
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/api/issues?severity=invalid",
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task GetIssues_CommaCombinedSeverity_Returns400()
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient();

        // Enum.TryParse accepts "low,high" via bitwise OR — the comma guard must reject it.
        var response = await client.GetAsync("/api/issues?severity=low,high",
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task GetIssues_NumericSeverity_Returns400()
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient();

        // "1" is the integer value of IssueSeverity.Medium — the int.TryParse guard must reject it.
        var response = await client.GetAsync("/api/issues?severity=1",
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task GetIssues_NumericStringWithUnderscoreType_Returns400()
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient();

        // "99_" passes the int.TryParse guard (not a pure numeric string because of the
        // trailing underscore), normalizes to "99", passes Enum.TryParse (numeric strings
        // are accepted), and is rejected ONLY by the Enum.IsDefined guard (99 is not a
        // defined IssueType member 0-4). Fails if Enum.IsDefined is removed from the
        // type parsing path.
        var response = await client.GetAsync("/api/issues?type=99_",
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task GetIssues_NumericStringWithUnderscoreStatus_Returns400()
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient();

        // Same chain as the type variant: passes int.TryParse, normalizes to "99",
        // passes Enum.TryParse, and is rejected ONLY by Enum.IsDefined (99 is not a
        // defined IssueStatus member 0-5). Fails if Enum.IsDefined is removed from the
        // status parsing path.
        var response = await client.GetAsync("/api/issues?status=99_",
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task GetIssues_NumericStringWithUnderscoreSeverity_Returns400()
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient();

        // Same chain as the type variant: passes int.TryParse, normalizes to "99",
        // passes Enum.TryParse, and is rejected ONLY by Enum.IsDefined (99 is not a
        // defined IssueSeverity member 0-2). Fails if Enum.IsDefined is removed from the
        // severity parsing path.
        var response = await client.GetAsync("/api/issues?severity=99_",
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task GetIssues_CodeQualityAlias_Codequality_ReturnsMatching()
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient();

        // Create a code_quality issue so the alias query can find it.
        await client.PostAsync("/api/issues", JsonBody(CreateBody(
            id: "alias-issue",
            type: IssueType.CodeQuality,
            title: "Naming issue",
            description: "Inconsistent names.",
            severity: IssueSeverity.Low,
            repositoryNames: ["CopilotHive"],
            sourceGoalId: "goal-1")), TestContext.Current.CancellationToken);

        // A bug issue that must be excluded by the type filter (same other dimensions).
        await client.PostAsync("/api/issues", JsonBody(CreateBody(
            id: "alias-bug",
            type: IssueType.Bug,
            title: "Naming issue",
            description: "Inconsistent names.",
            severity: IssueSeverity.Low,
            repositoryNames: ["CopilotHive"],
            sourceGoalId: "goal-1")), TestContext.Current.CancellationToken);

        var response = await client.GetAsync("/api/issues?type=codequality",
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await ReadJsonAsync(response);
        Assert.Equal(JsonValueKind.Array, body.ValueKind);
        Assert.Equal(1, body.GetArrayLength());
        Assert.Equal("alias-issue", body[0].GetProperty("id").GetString());
        Assert.Equal("code_quality", body[0].GetProperty("type").GetString());
    }

    // ── GET /api/issues/{id} (single) ───────────────────────────────────────

    [Fact]
    public async Task GetIssue_Existing_ReturnsIssueResponse()
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient();

        await client.PostAsync("/api/issues", JsonBody(CreateBody(
            id: "found-issue",
            type: IssueType.Suggestion,
            title: "Improve docs",
            description: "Document the API.",
            severity: IssueSeverity.Medium,
            repositoryNames: ["CopilotHive"],
            sourceGoalId: "goal-1",
            sourceRole: "reviewer",
            sourceIteration: 2)), TestContext.Current.CancellationToken);

        var response = await client.GetAsync("/api/issues/found-issue",
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await ReadJsonAsync(response);
        Assert.Equal("found-issue", body.GetProperty("id").GetString());
        Assert.Equal("suggestion", body.GetProperty("type").GetString());
        Assert.Equal("Improve docs", body.GetProperty("title").GetString());
        Assert.Equal("Document the API.", body.GetProperty("description").GetString());
        Assert.Equal("medium", body.GetProperty("severity").GetString());
        Assert.Equal("open", body.GetProperty("status").GetString());
        Assert.Equal("goal-1", body.GetProperty("sourceGoalId").GetString());
        Assert.Equal("reviewer", body.GetProperty("sourceRole").GetString());
        Assert.Equal(2, body.GetProperty("sourceIteration").GetInt32());
        Assert.Equal(JsonValueKind.String, body.GetProperty("createdAt").ValueKind);
    }

    [Fact]
    public async Task GetIssue_Missing_Returns404()
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/api/issues/does-not-exist",
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task GetIssue_NonAsciiGeneratedId_EscapedUrl_ReturnsFound()
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient();

        // Non-ASCII title + no explicit ID → generated ID contains Unicode letters.
        var createResponse = await client.PostAsync("/api/issues", JsonBody(CreateBody(
            id: null,
            type: IssueType.Bug,
            title: "Café problem",
            description: "Unicode in the title")), TestContext.Current.CancellationToken);
        var created = await ReadJsonAsync(createResponse);
        var id = created.GetProperty("id").GetString()!;
        Assert.Equal("café-problem", id);

        var response = await client.GetAsync($"/api/issues/{Uri.EscapeDataString(id)}",
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await ReadJsonAsync(response);
        Assert.Equal(id, body.GetProperty("id").GetString());
    }

    // ── POST /api/issues (create) ───────────────────────────────────────────

    [Fact]
    public async Task PostIssue_ValidKebabCaseId_Returns201WithBodyAndLocation()
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient();

        var response = await client.PostAsync("/api/issues", JsonBody(CreateBody(
            id: "valid-issue-id",
            type: IssueType.Bug,
            title: "Something broke",
            description: "Details here.",
            severity: IssueSeverity.High)), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var location = response.Headers.Location?.OriginalString;
        Assert.NotNull(location);
        Assert.Equal("/api/issues/valid-issue-id", location);

        var body = await ReadJsonAsync(response);
        Assert.Equal("valid-issue-id", body.GetProperty("id").GetString());
        Assert.Equal("bug", body.GetProperty("type").GetString());
        Assert.Equal("Something broke", body.GetProperty("title").GetString());
        Assert.Equal("Details here.", body.GetProperty("description").GetString());
        Assert.Equal("high", body.GetProperty("severity").GetString());
        Assert.Equal("open", body.GetProperty("status").GetString());
    }

    [Fact]
    public async Task PostIssue_WithoutId_GeneratesId_Returns201()
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient();

        // No "id" property at all in the body.
        var response = await client.PostAsync("/api/issues", JsonBody(new
        {
            type = IssueType.Suggestion,
            title = "Improve error handling",
            description = "Add better messages.",
        }), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await ReadJsonAsync(response);
        var id = body.GetProperty("id").GetString()!;
        Assert.Equal("improve-error-handling", id);

        var location = response.Headers.Location?.OriginalString;
        Assert.NotNull(location);
        Assert.Contains(Uri.EscapeDataString(id), location);
    }

    [Fact]
    public async Task PostIssue_NullId_GeneratesId_Returns201()
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient();

        var response = await client.PostAsync("/api/issues",
            JsonBody(CreateBody(id: null, type: IssueType.Concern, title: "Potential risk",
                description: "Risk details.")), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await ReadJsonAsync(response);
        Assert.Equal("potential-risk", body.GetProperty("id").GetString());
    }

    [Fact]
    public async Task PostIssue_WhitespaceId_GeneratesId_Returns201()
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient();

        var response = await client.PostAsync("/api/issues",
            JsonBody(CreateBody(id: "   ", type: IssueType.Workflow, title: "Slow pipeline",
                description: "The pipeline takes too long.")), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await ReadJsonAsync(response);
        Assert.Equal("slow-pipeline", body.GetProperty("id").GetString());
    }

    [Fact]
    public async Task PostIssue_UppercaseId_Returns400()
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient();

        var response = await client.PostAsync("/api/issues",
            JsonBody(CreateBody(id: "MyIssue")), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task PostIssue_IdWithSlash_Returns400()
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient();

        var response = await client.PostAsync("/api/issues",
            JsonBody(CreateBody(id: "issue/1")), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task PostIssue_IdWithSpecialChars_Returns400()
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient();

        var response = await client.PostAsync("/api/issues",
            JsonBody(CreateBody(id: "issue!")), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task PostIssue_NonAsciiId_Returns400()
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient();

        var response = await client.PostAsync("/api/issues",
            JsonBody(CreateBody(id: "café-problem")), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task PostIssue_MissingType_Returns400()
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient();

        var response = await client.PostAsync("/api/issues",
            JsonBody(CreateBody(type: null)), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await ReadBodyAsync(response);
        Assert.Contains("Type is required.", body);
    }

    [Fact]
    public async Task PostIssue_MissingTitle_Returns400()
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient();

        var response = await client.PostAsync("/api/issues",
            JsonBody(CreateBody(title: null)), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await ReadBodyAsync(response);
        Assert.Contains("Title is required.", body);
    }

    [Fact]
    public async Task PostIssue_MissingDescription_Returns400()
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient();

        var response = await client.PostAsync("/api/issues",
            JsonBody(CreateBody(description: null)), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await ReadBodyAsync(response);
        Assert.Contains("Description is required.", body);
    }

    [Fact]
    public async Task PostIssue_SeverityDefaultsToLow_WhenOmitted()
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient();

        // No severity property at all in the body.
        var response = await client.PostAsync("/api/issues", JsonBody(new
        {
            id = "default-severity",
            type = IssueType.Bug,
            title = "Default severity",
            description = "Severity should default to low.",
        }), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await ReadJsonAsync(response);
        Assert.Equal("low", body.GetProperty("severity").GetString());
    }

    [Fact]
    public async Task PostIssue_SeveritySetExplicitly_ReturnsHigh()
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient();

        var response = await client.PostAsync("/api/issues",
            JsonBody(CreateBody(
                id: "explicit-severity",
                type: IssueType.Bug,
                title: "Explicit severity",
                description: "Severity is high.",
                severity: IssueSeverity.High)), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await ReadJsonAsync(response);
        Assert.Equal("high", body.GetProperty("severity").GetString());
    }

    [Fact]
    public async Task PostIssue_EmptyStringTitle_Returns400()
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient();

        var response = await client.PostAsync("/api/issues",
            JsonBody(CreateBody(id: "empty-title", title: "")), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await ReadBodyAsync(response);
        Assert.Contains("Title is required.", body);
    }

    [Fact]
    public async Task PostIssue_WhitespaceTitle_Returns400()
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient();

        var response = await client.PostAsync("/api/issues",
            JsonBody(CreateBody(id: "ws-title", title: "   ")), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await ReadBodyAsync(response);
        Assert.Contains("Title is required.", body);
    }

    [Fact]
    public async Task PostIssue_EmptyStringDescription_Returns400()
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient();

        var response = await client.PostAsync("/api/issues",
            JsonBody(CreateBody(id: "empty-desc", description: "")), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await ReadBodyAsync(response);
        Assert.Contains("Description is required.", body);
    }

    [Fact]
    public async Task PostIssue_WhitespaceDescription_Returns400()
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient();

        var response = await client.PostAsync("/api/issues",
            JsonBody(CreateBody(id: "ws-desc", description: "   ")), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await ReadBodyAsync(response);
        Assert.Contains("Description is required.", body);
    }

    [Fact]
    public async Task PostIssue_AllOptionalFieldsPopulated_ReturnedInBody()
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient();

        var response = await client.PostAsync("/api/issues", JsonBody(CreateBody(
            id: "all-optional",
            type: IssueType.Concern,
            title: "Optional fields",
            description: "Every optional field is set.",
            severity: IssueSeverity.Medium,
            repositoryNames: ["repo1", "repo2"],
            sourceGoalId: "goal-x",
            sourceRole: "coder",
            sourceIteration: 3)), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await ReadJsonAsync(response);
        Assert.Equal("all-optional", body.GetProperty("id").GetString());
        Assert.Equal("concern", body.GetProperty("type").GetString());
        Assert.Equal("medium", body.GetProperty("severity").GetString());
        var repos = body.GetProperty("repositoryNames").EnumerateArray().Select(e => e.GetString()).ToList();
        Assert.Equal(2, repos.Count);
        Assert.Contains("repo1", repos);
        Assert.Contains("repo2", repos);
        Assert.Equal("goal-x", body.GetProperty("sourceGoalId").GetString());
        Assert.Equal("coder", body.GetProperty("sourceRole").GetString());
        Assert.Equal(3, body.GetProperty("sourceIteration").GetInt32());
    }

    [Fact]
    public async Task PostIssue_DuplicateId_Returns409()
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient();

        const string id = "duplicate-issue";
        var first = await client.PostAsync("/api/issues",
            JsonBody(CreateBody(id: id)), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);

        var second = await client.PostAsync("/api/issues",
            JsonBody(CreateBody(id: id)), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
        var body = await ReadBodyAsync(second);
        // Exact error text with the actual ID interpolated.
        Assert.Equal("{\"error\":\"Issue 'duplicate-issue' already exists.\"}", body);
    }

    [Fact]
    public async Task PostIssue_NonAsciiTitle_GeneratedId_Returns201WithEscapedLocation()
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient();

        var response = await client.PostAsync("/api/issues", JsonBody(CreateBody(
            id: null,
            type: IssueType.Bug,
            title: "Café problem",
            description: "Unicode in the title")), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        // Body carries the original (unescaped) ID.
        var body = await ReadJsonAsync(response);
        var id = body.GetProperty("id").GetString()!;
        Assert.Equal("café-problem", id);

        // Location header must be EXACTLY /api/issues/{escaped id} — computed from the
        // response body's ID, not a substring check.
        var escaped = Uri.EscapeDataString(id);
        var expectedLocation = $"/api/issues/{escaped}";
        var location = response.Headers.Location?.OriginalString;
        Assert.Equal(expectedLocation, location);
        Assert.DoesNotContain("café-problem", location);

        // GET via the escaped URL must retrieve the issue.
        var getResponse = await client.GetAsync($"/api/issues/{escaped}",
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, getResponse.StatusCode);
        var fetched = await ReadJsonAsync(getResponse);
        Assert.Equal(id, fetched.GetProperty("id").GetString());
    }

    // ── PATCH /api/issues/{id} (partial update) ─────────────────────────────

    [Fact]
    public async Task PatchIssue_PartialUpdate_Returns200()
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient();

        var createResponse = await client.PostAsync("/api/issues",
            JsonBody(CreateBody(id: "patch-status")), TestContext.Current.CancellationToken);
        var created = await ReadJsonAsync(createResponse);
        Assert.Equal("open", created.GetProperty("status").GetString());

        var patchResponse = await client.PatchAsync("/api/issues/patch-status",
            JsonBody(PatchBody(status: IssueStatus.Triaged)), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, patchResponse.StatusCode);
        var body = await ReadJsonAsync(patchResponse);
        Assert.Equal("triaged", body.GetProperty("status").GetString());

        // Re-fetch confirms the change was persisted.
        var getResponse = await client.GetAsync("/api/issues/patch-status",
            TestContext.Current.CancellationToken);
        var fetched = await ReadJsonAsync(getResponse);
        Assert.Equal("triaged", fetched.GetProperty("status").GetString());
    }

    [Fact]
    public async Task PatchIssue_AllMutableFields_UpdatedAndPersisted()
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient();

        // Create with known initial values for every mutable field.
        var createResponse = await client.PostAsync("/api/issues", JsonBody(CreateBody(
            id: "patch-all-fields",
            type: IssueType.Bug,
            title: "Original",
            description: "Original desc",
            severity: IssueSeverity.Low,
            repositoryNames: ["repo-a", "repo-b"],
            sourceGoalId: "goal-src",
            sourceRole: "reviewer",
            sourceIteration: 1)), TestContext.Current.CancellationToken);
        var created = await ReadJsonAsync(createResponse);
        Assert.Equal("bug", created.GetProperty("type").GetString());
        Assert.Equal("Original", created.GetProperty("title").GetString());
        Assert.Equal("Original desc", created.GetProperty("description").GetString());
        Assert.Equal("low", created.GetProperty("severity").GetString());
        Assert.Equal("open", created.GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Null, created.GetProperty("linkedGoalId").ValueKind);

        // PATCH every mutable field at once.
        var patchResponse = await client.PatchAsync("/api/issues/patch-all-fields",
            JsonBody(PatchBody(
                type: IssueType.Suggestion,
                title: "Updated",
                description: "Updated desc",
                severity: IssueSeverity.High,
                status: IssueStatus.InProgress,
                repositoryNames: ["repo-c", "repo-d", "repo-e"],
                linkedGoalId: "goal-linked")), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, patchResponse.StatusCode);
        var body = await ReadJsonAsync(patchResponse);
        Assert.Equal("suggestion", body.GetProperty("type").GetString());
        Assert.Equal("Updated", body.GetProperty("title").GetString());
        Assert.Equal("Updated desc", body.GetProperty("description").GetString());
        Assert.Equal("high", body.GetProperty("severity").GetString());
        Assert.Equal("in_progress", body.GetProperty("status").GetString());
        Assert.Equal("goal-linked", body.GetProperty("linkedGoalId").GetString());

        // Re-fetch proves persistence and that RepositoryNames were REPLACED (not appended).
        var getResponse = await client.GetAsync("/api/issues/patch-all-fields",
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, getResponse.StatusCode);
        var fetched = await ReadJsonAsync(getResponse);
        Assert.Equal("suggestion", fetched.GetProperty("type").GetString());
        Assert.Equal("Updated", fetched.GetProperty("title").GetString());
        Assert.Equal("Updated desc", fetched.GetProperty("description").GetString());
        Assert.Equal("high", fetched.GetProperty("severity").GetString());
        Assert.Equal("in_progress", fetched.GetProperty("status").GetString());
        Assert.Equal("goal-linked", fetched.GetProperty("linkedGoalId").GetString());

        // Old repository entries are gone; only the new list is present.
        var repos = fetched.GetProperty("repositoryNames").EnumerateArray().Select(e => e.GetString()).ToList();
        Assert.Equal(3, repos.Count);
        Assert.Contains("repo-c", repos);
        Assert.Contains("repo-d", repos);
        Assert.Contains("repo-e", repos);
        Assert.DoesNotContain("repo-a", repos);
        Assert.DoesNotContain("repo-b", repos);

        // Immutable fields are preserved.
        Assert.Equal("goal-src", fetched.GetProperty("sourceGoalId").GetString());
        Assert.Equal("reviewer", fetched.GetProperty("sourceRole").GetString());
        Assert.Equal(1, fetched.GetProperty("sourceIteration").GetInt32());
    }

    [Fact]
    public async Task PatchIssue_UnspecifiedFields_RemainUnchanged()
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient();

        // Create with known values for every mutable field.
        await client.PostAsync("/api/issues", JsonBody(CreateBody(
            id: "patch-unspecified",
            type: IssueType.Bug,
            title: "Keep title",
            description: "Keep desc",
            severity: IssueSeverity.Medium,
            repositoryNames: ["repo-a"],
            sourceGoalId: "goal-src")), TestContext.Current.CancellationToken);

        // PATCH only Status — every other field must keep its pre-PATCH value.
        var patchResponse = await client.PatchAsync("/api/issues/patch-unspecified",
            JsonBody(PatchBody(status: IssueStatus.Closed)), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, patchResponse.StatusCode);
        var body = await ReadJsonAsync(patchResponse);
        Assert.Equal("closed", body.GetProperty("status").GetString());
        Assert.Equal("bug", body.GetProperty("type").GetString());
        Assert.Equal("Keep title", body.GetProperty("title").GetString());
        Assert.Equal("Keep desc", body.GetProperty("description").GetString());
        Assert.Equal("medium", body.GetProperty("severity").GetString());
        Assert.Equal(JsonValueKind.Null, body.GetProperty("linkedGoalId").ValueKind);
        var repos = body.GetProperty("repositoryNames").EnumerateArray().Select(e => e.GetString()).ToList();
        Assert.Single(repos);
        Assert.Equal("repo-a", repos[0]);
    }

    [Fact]
    public async Task PatchIssue_StatusTransitionToResolved_ResolvedAtSet()
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient();

        await client.PostAsync("/api/issues",
            JsonBody(CreateBody(id: "resolve-me")), TestContext.Current.CancellationToken);

        var patchResponse = await client.PatchAsync("/api/issues/resolve-me",
            JsonBody(PatchBody(status: IssueStatus.Resolved)), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, patchResponse.StatusCode);
        var body = await ReadJsonAsync(patchResponse);
        Assert.Equal("resolved", body.GetProperty("status").GetString());
        Assert.True(body.TryGetProperty("resolvedAt", out var resolvedAt));
        Assert.Equal(JsonValueKind.String, resolvedAt.ValueKind);
    }

    [Fact]
    public async Task PatchIssue_LinkedGoalId_Null_NoChange()
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient();

        await client.PostAsync("/api/issues",
            JsonBody(CreateBody(id: "link-null")), TestContext.Current.CancellationToken);

        // Set the linked goal first.
        var setResponse = await client.PatchAsync("/api/issues/link-null",
            JsonBody(PatchBody(linkedGoalId: "goal-1")), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, setResponse.StatusCode);
        var setBody = await ReadJsonAsync(setResponse);
        Assert.Equal("goal-1", setBody.GetProperty("linkedGoalId").GetString());

        // PATCH without linkedGoalId (null) → no change.
        var noChangeResponse = await client.PatchAsync("/api/issues/link-null",
            JsonBody(PatchBody(severity: IssueSeverity.High)), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, noChangeResponse.StatusCode);
        var noChangeBody = await ReadJsonAsync(noChangeResponse);
        Assert.Equal("goal-1", noChangeBody.GetProperty("linkedGoalId").GetString());
    }

    [Fact]
    public async Task PatchIssue_LinkedGoalId_EmptyString_Clears()
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient();

        await client.PostAsync("/api/issues",
            JsonBody(CreateBody(id: "link-clear")), TestContext.Current.CancellationToken);

        var setResponse = await client.PatchAsync("/api/issues/link-clear",
            JsonBody(PatchBody(linkedGoalId: "goal-1")), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, setResponse.StatusCode);

        var clearResponse = await client.PatchAsync("/api/issues/link-clear",
            JsonBody(PatchBody(linkedGoalId: "")), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, clearResponse.StatusCode);
        var body = await ReadJsonAsync(clearResponse);
        Assert.Equal(JsonValueKind.Null, body.GetProperty("linkedGoalId").ValueKind);
    }

    [Fact]
    public async Task PatchIssue_LinkedGoalId_NonEmpty_Sets()
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient();

        await client.PostAsync("/api/issues",
            JsonBody(CreateBody(id: "link-set")), TestContext.Current.CancellationToken);

        var setResponse = await client.PatchAsync("/api/issues/link-set",
            JsonBody(PatchBody(linkedGoalId: "goal-42")), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, setResponse.StatusCode);
        var body = await ReadJsonAsync(setResponse);
        Assert.Equal("goal-42", body.GetProperty("linkedGoalId").GetString());
    }

    [Fact]
    public async Task PatchIssue_BlankTitle_Returns400()
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient();

        await client.PostAsync("/api/issues",
            JsonBody(CreateBody(id: "blank-title")), TestContext.Current.CancellationToken);

        var response = await client.PatchAsync("/api/issues/blank-title",
            JsonBody(PatchBody(title: "   ")), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await ReadBodyAsync(response);
        Assert.Contains("Title is required.", body);
    }

    [Fact]
    public async Task PatchIssue_BlankDescription_Returns400()
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient();

        await client.PostAsync("/api/issues",
            JsonBody(CreateBody(id: "blank-desc")), TestContext.Current.CancellationToken);

        var response = await client.PatchAsync("/api/issues/blank-desc",
            JsonBody(PatchBody(description: "   ")), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await ReadBodyAsync(response);
        Assert.Contains("Description is required.", body);
    }

    [Fact]
    public async Task PatchIssue_Missing_Returns404()
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient();

        var response = await client.PatchAsync("/api/issues/does-not-exist",
            JsonBody(PatchBody(status: IssueStatus.Triaged)), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // ── DELETE /api/issues/{id} ─────────────────────────────────────────────

    [Fact]
    public async Task DeleteIssue_Existing_Returns204()
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient();

        await client.PostAsync("/api/issues",
            JsonBody(CreateBody(id: "delete-me")), TestContext.Current.CancellationToken);

        var response = await client.DeleteAsync("/api/issues/delete-me",
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        // Confirm the issue is gone.
        var getResponse = await client.GetAsync("/api/issues/delete-me",
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NotFound, getResponse.StatusCode);
    }

    [Fact]
    public async Task DeleteIssue_Missing_Returns404()
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient();

        var response = await client.DeleteAsync("/api/issues/does-not-exist",
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }
}
