using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

using CopilotHive.Configuration;
using CopilotHive.Dashboard;
using CopilotHive.Goals;
using CopilotHive.Git;
using CopilotHive.Knowledge;
using CopilotHive.Orchestration;
using CopilotHive.Persistence;
using CopilotHive.Services;

using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CopilotHive.Tests.Services;

/// <summary>
/// Wire-contract tests for the seven release endpoints after the
/// <see cref="IReleaseFacade"/> refactor, plus facade-level side-effect tests.
/// These pin the EXACT wire behaviour of the pre-refactor handlers so the facade
/// migration cannot silently change it:
/// <list type="bullet">
///   <item>Enums serialize as snake_case STRINGS via <see cref="Program.GlobalStringEnumConverter"/>
///   (<c>"failure":"none"</c>, <c>"status":"released"</c>, <c>"execution_state":"completed"</c>) —
///   never as numbers.</item>
///   <item>The 500 <c>{detail, results}</c> execution-failure body and the 503
///   <c>{detail}</c> missing-service body keep their exact shapes.</item>
///   <item>Create/notes/tag/repositories keep their status codes, bodies and side effects.</item>
///   <item>The status-success orchestration side effects (event publication,
///   <c>LaunchNuGetMonitors</c>, knowledge-document cleanup, release-timestamp update,
///   dashboard notification) run EXACTLY once via the facade — and a cancelled
///   knowledge-document cleanup does not fail the release.</item>
/// </list>
/// </summary>
[Collection("HiveIntegration")]
public sealed class ReleaseFacadeWireContractTests
{
    private static string UniqueId() => "test-" + Guid.NewGuid().ToString("N")[..16];

    private static HiveConfigFile CreateConfig(
        string repoName = "repo1",
        NuGetPublishConfig? publishNuGet = null) => new()
    {
        Repositories =
        [
            new RepositoryConfig
            {
                Name = repoName,
                Url = $"https://github.com/test/{repoName}",
                DefaultBranch = "main",
                Release = new ReleaseRepoConfig { MergeTo = "main", TagBranch = "main" },
                PublishNuGet = publishNuGet,
            },
        ],
    };

    /// <summary>
    /// Creates a test factory with <see cref="ReleaseExecutionService"/>, a
    /// <see cref="HiveConfigFile"/> and a fake <see cref="IBrainRepoManager"/> registered —
    /// the setup the Planning→Released execution path needs.
    /// </summary>
    private static WebApplicationFactory<Program> CreateExecutionFactory(
        ConfigurableFakeRepoManager fake,
        HiveConfigFile? config = null)
    {
        config ??= CreateConfig();
        var baseFactory = new HiveTestFactory { MockRepoManager = fake };
        return baseFactory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureServices(services =>
            {
                var existingConfig = services.SingleOrDefault(d => d.ServiceType == typeof(HiveConfigFile));
                if (existingConfig is not null)
                    services.Remove(existingConfig);
                services.AddSingleton(config);

                services.AddSingleton(sp => new ReleaseExecutionService(
                    sp.GetRequiredService<IGoalStore>(),
                    config,
                    sp.GetRequiredService<IBrainRepoManager>(),
                    sp.GetRequiredService<ILogger<ReleaseExecutionService>>()));
            });
        });
    }

    private static async Task SeedAsync(
        WebApplicationFactory<Program> factory, string releaseId, string tag,
        ReleaseStatus status = ReleaseStatus.Planning, List<string>? repos = null)
    {
        using var scope = factory.Services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IGoalStore>();
        await store.CreateReleaseAsync(new Release
        {
            Id = releaseId,
            Tag = tag,
            Status = status,
            RepositoryNames = repos ?? [],
        }, TestContext.Current.CancellationToken);
    }

    // ── Wire contract: snake_case enum serialization (the global converter) ───

    [Fact]
    public async Task StatusExecutionSuccess_FailureSerializesAsNoneString()
    {
        var ct = TestContext.Current.CancellationToken;
        var fake = new ConfigurableFakeRepoManager { CreateTagResult = true };
        using var factory = CreateExecutionFactory(fake);
        using var client = factory.CreateClient();

        var releaseId = UniqueId();
        await SeedAsync(factory, releaseId, "v1.0.0", repos: ["repo1"]);
        using (var scope = factory.Services.CreateScope())
        {
            var store = scope.ServiceProvider.GetRequiredService<IGoalStore>();
            await store.CreateGoalAsync(
                new Goal { Id = UniqueId(), Description = "Test", ReleaseId = releaseId, Status = GoalStatus.Completed }, ct);
        }

        var response = await client.PatchAsync(
            $"/api/releases/{releaseId}/status",
            JsonContent.Create(new { status = "Released" }), ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(body);

        // The { release, result } envelope shape.
        Assert.Equal(2, doc.RootElement.EnumerateObject().Count());
        Assert.Equal(JsonValueKind.Object, doc.RootElement.GetProperty("release").ValueKind);
        Assert.Equal(JsonValueKind.Object, doc.RootElement.GetProperty("result").ValueKind);

        // Enum wire forms: snake_case STRINGS from the global converter — NOT numbers.
        var release = doc.RootElement.GetProperty("release");
        Assert.Equal(JsonValueKind.String, release.GetProperty("status").ValueKind);
        Assert.Equal("released", release.GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.String, release.GetProperty("executionState").ValueKind);
        Assert.Equal("completed", release.GetProperty("executionState").GetString());
        Assert.Equal(JsonValueKind.Array, release.GetProperty("repositoryNames").ValueKind);
        Assert.Equal("repo1", release.GetProperty("repositoryNames")[0].GetString());
        Assert.NotNull(release.GetProperty("releasedAt").GetString());

        // The execution result's Failure enum serializes as the string "none" on success.
        var result = doc.RootElement.GetProperty("result");
        Assert.True(result.GetProperty("success").GetBoolean());
        Assert.Equal(JsonValueKind.String, result.GetProperty("failure").ValueKind);
        Assert.Equal("none", result.GetProperty("failure").GetString());

        // The per-repo result mirrors RepoReleaseResult exactly.
        var results = result.GetProperty("results");
        Assert.Equal(1, results.GetArrayLength());
        Assert.Equal("repo1", results[0].GetProperty("repoName").GetString());
        Assert.True(results[0].GetProperty("success").GetBoolean());

        // The release is persisted as Released with the release timestamp set.
        using (var scope = factory.Services.CreateScope())
        {
            var stored = await scope.ServiceProvider.GetRequiredService<IGoalStore>()
                .GetReleaseAsync(releaseId, ct);
            Assert.NotNull(stored);
            Assert.Equal(ReleaseStatus.Released, stored!.Status);
            Assert.NotNull(stored.ReleasedAt);
        }
    }

    [Fact]
    public async Task StatusPlanningNoOp_BareReleaseWithSnakeCaseEnums()
    {
        var ct = TestContext.Current.CancellationToken;
        var fake = new ConfigurableFakeRepoManager { CreateTagResult = true };
        using var factory = CreateExecutionFactory(fake);
        using var client = factory.CreateClient();

        var releaseId = UniqueId();
        await SeedAsync(factory, releaseId, "v1.0.0", repos: ["repo1"]);

        var response = await client.PatchAsync(
            $"/api/releases/{releaseId}/status",
            JsonContent.Create(new { status = "Planning" }), ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(body);

        // The no-op success is the BARE release JSON — no {release, result} envelope.
        Assert.Equal(releaseId, doc.RootElement.GetProperty("id").GetString());
        Assert.Equal("v1.0.0", doc.RootElement.GetProperty("tag").GetString());
        Assert.Equal(JsonValueKind.String, doc.RootElement.GetProperty("status").ValueKind);
        Assert.Equal("planning", doc.RootElement.GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.String, doc.RootElement.GetProperty("executionState").ValueKind);
        Assert.Equal("none", doc.RootElement.GetProperty("executionState").GetString());
        Assert.False(doc.RootElement.TryGetProperty("result", out _));
        Assert.False(doc.RootElement.TryGetProperty("release", out _));

        // No git operations ran for the no-op.
        Assert.Empty(fake.MergeCalls);
        Assert.Empty(fake.CreateTagCalls);
    }

    // ── Wire contract: failure bodies ─────────────────────────────────────────

    [Fact]
    public async Task StatusExecutionFailure500_DetailAndResultsOnly()
    {
        var ct = TestContext.Current.CancellationToken;
        var fake = new ConfigurableFakeRepoManager
        {
            MergeCallback = (_, _, _) => throw new InvalidOperationException("merge blew up"),
        };
        using var factory = CreateExecutionFactory(fake);
        using var client = factory.CreateClient();

        var releaseId = UniqueId();
        await SeedAsync(factory, releaseId, "v1.0.0", repos: ["repo1"]);
        using (var scope = factory.Services.CreateScope())
        {
            var store = scope.ServiceProvider.GetRequiredService<IGoalStore>();
            await store.CreateGoalAsync(
                new Goal { Id = UniqueId(), Description = "Test", ReleaseId = releaseId, Status = GoalStatus.Completed }, ct);
        }

        var response = await client.PatchAsync(
            $"/api/releases/{releaseId}/status",
            JsonContent.Create(new { status = "Released" }), ct);

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(body);

        // The 500 body is EXACTLY {detail, results} — no {error} / {errors} members leak in.
        Assert.Equal(2, doc.RootElement.EnumerateObject().Count());
        var detail = doc.RootElement.GetProperty("detail").GetString();
        Assert.False(string.IsNullOrWhiteSpace(detail));
        Assert.Contains("repo1", detail);
        var results = doc.RootElement.GetProperty("results");
        Assert.Equal(1, results.GetArrayLength());
        Assert.Equal("repo1", results[0].GetProperty("repoName").GetString());
        Assert.False(results[0].GetProperty("success").GetBoolean());
        Assert.False(string.IsNullOrWhiteSpace(results[0].GetProperty("error").GetString()));
        Assert.False(doc.RootElement.TryGetProperty("error", out _));
        Assert.False(doc.RootElement.TryGetProperty("errors", out _));

        // The failed execution must not have marked the release Released.
        using (var scope = factory.Services.CreateScope())
        {
            var stored = await scope.ServiceProvider.GetRequiredService<IGoalStore>()
                .GetReleaseAsync(releaseId, ct);
            Assert.NotNull(stored);
            Assert.Equal(ReleaseStatus.Planning, stored!.Status);
        }
    }

    [Fact]
    public async Task StatusMissingExecutionService503_ExactDetailBody()
    {
        var ct = TestContext.Current.CancellationToken;
        // A plain factory (no ReleaseExecutionService registered) → the 503 branch.
        using var factory = new HiveTestFactory();
        using var client = factory.CreateClient();

        var releaseId = UniqueId();
        await SeedAsync(factory, releaseId, "v1.0.0", repos: ["repo1"]);

        var response = await client.PatchAsync(
            $"/api/releases/{releaseId}/status",
            JsonContent.Create(new { status = "Released" }), ct);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(body);

        // The 503 body carries EXACTLY one detail member with the frozen message.
        Assert.Single(doc.RootElement.EnumerateObject());
        Assert.Equal(
            "Release execution service is not available.",
            doc.RootElement.GetProperty("detail").GetString());
    }

    [Fact]
    public async Task StatusValidationFailure400_ErrorsArrayOnly()
    {
        var ct = TestContext.Current.CancellationToken;
        var fake = new ConfigurableFakeRepoManager { CreateTagResult = true };
        using var factory = CreateExecutionFactory(fake);
        using var client = factory.CreateClient();

        var releaseId = UniqueId();
        await SeedAsync(factory, releaseId, "v1.0.0", repos: ["repo1"]);
        using (var scope = factory.Services.CreateScope())
        {
            var store = scope.ServiceProvider.GetRequiredService<IGoalStore>();
            await store.CreateGoalAsync(
                new Goal { Id = UniqueId(), Description = "Test", ReleaseId = releaseId, Status = GoalStatus.InProgress }, ct);
        }

        var response = await client.PatchAsync(
            $"/api/releases/{releaseId}/status",
            JsonContent.Create(new { status = "Released" }), ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(body);

        // The validation body is {errors:[...]} — no {error} member.
        Assert.Single(doc.RootElement.EnumerateObject());
        var errors = doc.RootElement.GetProperty("errors");
        Assert.Equal(JsonValueKind.Array, errors.ValueKind);
        Assert.NotEqual(0, errors.GetArrayLength());
        Assert.False(doc.RootElement.TryGetProperty("error", out _));

        // No git operations ran for a validation failure.
        Assert.Empty(fake.MergeCalls);
    }

    [Fact]
    public async Task StatusNotFound404_ErrorBody()
    {
        var ct = TestContext.Current.CancellationToken;
        using var factory = new HiveTestFactory();
        using var client = factory.CreateClient();

        var response = await client.PatchAsync(
            $"/api/releases/{UniqueId()}/status",
            JsonContent.Create(new { status = "Released" }), ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(body);
        Assert.False(string.IsNullOrWhiteSpace(doc.RootElement.GetProperty("error").GetString()));
        Assert.False(doc.RootElement.TryGetProperty("errors", out _));
    }

    [Fact]
    public async Task StatusReleasedToReleased409_ErrorBody()
    {
        var ct = TestContext.Current.CancellationToken;
        using var factory = new HiveTestFactory();
        using var client = factory.CreateClient();

        var releaseId = UniqueId();
        await SeedAsync(factory, releaseId, "v1.0.0", status: ReleaseStatus.Released);

        var response = await client.PatchAsync(
            $"/api/releases/{releaseId}/status",
            JsonContent.Create(new { status = "Released" }), ct);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(body);
        Assert.Contains("already in 'Released'", doc.RootElement.GetProperty("error").GetString());
    }

    [Theory]
    [InlineData("", "Status is required.")]
    [InlineData("7", "Invalid status '7'. Valid values: Planning, Released.")]
    [InlineData("Bogus", "Invalid status 'Bogus'. Valid values: Planning, Released.")]
    [InlineData("Released,Planning", "Invalid status 'Released,Planning'. Valid values: Planning, Released.")]
    public async Task StatusInvalidValues400_ExactErrorBody(string status, string expectedError)
    {
        var ct = TestContext.Current.CancellationToken;
        using var factory = new HiveTestFactory();
        using var client = factory.CreateClient();

        var releaseId = UniqueId();
        await SeedAsync(factory, releaseId, "v1.0.0");

        var response = await client.PatchAsync(
            $"/api/releases/{releaseId}/status",
            JsonContent.Create(new { status }), ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(body);
        Assert.Single(doc.RootElement.EnumerateObject());
        Assert.Equal(expectedError, doc.RootElement.GetProperty("error").GetString());
    }

    [Fact]
    public async Task StatusReleasedToPlanning409_ExactErrorBody()
    {
        var ct = TestContext.Current.CancellationToken;
        using var factory = new HiveTestFactory();
        using var client = factory.CreateClient();

        var releaseId = UniqueId();
        await SeedAsync(factory, releaseId, "v1.0.0", status: ReleaseStatus.Released);

        var response = await client.PatchAsync(
            $"/api/releases/{releaseId}/status",
            JsonContent.Create(new { status = "Planning" }), ct);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(body);
        Assert.Equal(
            "Cannot revert a Released release back to Planning.",
            doc.RootElement.GetProperty("error").GetString());
    }

    // ── Wire contract: create ─────────────────────────────────────────────────

    [Fact]
    public async Task CreateRelease_Created201WithLocationAndSnakeCaseEnums()
    {
        var ct = TestContext.Current.CancellationToken;
        using var factory = new HiveTestFactory();
        using var client = factory.CreateClient();

        var version = "v-" + UniqueId();
        var response = await client.PostAsync(
            "/api/releases",
            JsonContent.Create(new { version, repository = "repo1" }), ct);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal($"/api/releases/{version}", response.Headers.Location?.ToString());

        var body = await response.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(body);
        Assert.Equal(version, doc.RootElement.GetProperty("id").GetString());
        Assert.Equal(version, doc.RootElement.GetProperty("tag").GetString());
        Assert.Equal("planning", doc.RootElement.GetProperty("status").GetString());
        Assert.Equal("none", doc.RootElement.GetProperty("executionState").GetString());
        Assert.Equal("repo1", doc.RootElement.GetProperty("repositoryNames")[0].GetString());
    }

    [Fact]
    public async Task CreateRelease_BlankVersion_Returns400ErrorBody()
    {
        using var factory = new HiveTestFactory();
        using var client = factory.CreateClient();

        var response = await client.PostAsync(
            "/api/releases",
            JsonContent.Create(new { version = "  " }), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        using var doc = JsonDocument.Parse(body);
        Assert.Equal("Version is required.", doc.RootElement.GetProperty("error").GetString());
    }

    [Fact]
    public async Task CreateRelease_DuplicateId_Returns409ErrorBody()
    {
        var ct = TestContext.Current.CancellationToken;
        using var factory = new HiveTestFactory();
        using var client = factory.CreateClient();

        var releaseId = UniqueId();
        await SeedAsync(factory, releaseId, releaseId);

        var response = await client.PostAsync(
            "/api/releases",
            JsonContent.Create(new { version = releaseId }), ct);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(body);
        Assert.Contains("already exist", doc.RootElement.GetProperty("error").GetString(), StringComparison.OrdinalIgnoreCase);
    }

    // ── Wire contract: notes ──────────────────────────────────────────────────

    [Fact]
    public async Task Notes_NullValue_ClearsStoredNotes()
    {
        var ct = TestContext.Current.CancellationToken;
        using var factory = new HiveTestFactory();
        using var client = factory.CreateClient();

        var releaseId = UniqueId();
        await SeedAsync(factory, releaseId, "v1.0.0");

        // Set notes first, then clear them with a null value.
        var set = await client.PatchAsync(
            $"/api/releases/{releaseId}/notes",
            JsonContent.Create(new { notes = "Old notes" }), ct);
        Assert.Equal(HttpStatusCode.OK, set.StatusCode);

        var clear = await client.PatchAsync(
            $"/api/releases/{releaseId}/notes",
            JsonContent.Create(new { notes = (string?)null }), ct);
        Assert.Equal(HttpStatusCode.OK, clear.StatusCode);

        // Verify via a STORE RE-READ, not just the response body — the null must have
        // been persisted as a clear.
        using var scope = factory.Services.CreateScope();
        var stored = await scope.ServiceProvider.GetRequiredService<IGoalStore>()
            .GetReleaseAsync(releaseId, ct);
        Assert.NotNull(stored);
        Assert.Null(stored!.Notes);
    }

    [Fact]
    public async Task Notes_UnknownId_Returns404ErrorBody()
    {
        using var factory = new HiveTestFactory();
        using var client = factory.CreateClient();

        var response = await client.PatchAsync(
            "/api/releases/nonexistent-xyz/notes",
            JsonContent.Create(new { notes = "n" }), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        using var doc = JsonDocument.Parse(body);
        Assert.Contains("not found", doc.RootElement.GetProperty("error").GetString(), StringComparison.OrdinalIgnoreCase);
    }

    // ── Wire contract: tag ────────────────────────────────────────────────────

    [Fact]
    public async Task Tag_BlankValue_Returns400ErrorBody()
    {
        var ct = TestContext.Current.CancellationToken;
        using var factory = new HiveTestFactory();
        using var client = factory.CreateClient();

        var releaseId = UniqueId();
        await SeedAsync(factory, releaseId, "v1.0.0");

        var response = await client.PatchAsync(
            $"/api/releases/{releaseId}/tag",
            JsonContent.Create(new { tag = "  " }), ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(body);
        Assert.Equal("Tag is required.", doc.RootElement.GetProperty("error").GetString());
    }

    [Fact]
    public async Task Tag_UnknownId_Returns404ErrorBody()
    {
        using var factory = new HiveTestFactory();
        using var client = factory.CreateClient();

        var response = await client.PatchAsync(
            "/api/releases/nonexistent-xyz/tag",
            JsonContent.Create(new { tag = "v2.0.0" }), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        using var doc = JsonDocument.Parse(body);
        Assert.Contains("not found", doc.RootElement.GetProperty("error").GetString(), StringComparison.OrdinalIgnoreCase);
    }

    // ── Wire contract: repositories ───────────────────────────────────────────

    [Fact]
    public async Task Repositories_NullValue_NoOpUpdateWith200()
    {
        var ct = TestContext.Current.CancellationToken;
        using var factory = new HiveTestFactory();
        using var client = factory.CreateClient();

        var releaseId = UniqueId();
        await SeedAsync(factory, releaseId, "v1.0.0", repos: ["repo1"]);

        var response = await client.PatchAsync(
            $"/api/releases/{releaseId}/repositories",
            JsonContent.Create(new { repositories = (string[]?)null }), ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        // The stored repository list is UNCHANGED (the null list is a no-op update, not a clear).
        using var scope = factory.Services.CreateScope();
        var stored = await scope.ServiceProvider.GetRequiredService<IGoalStore>()
            .GetReleaseAsync(releaseId, ct);
        Assert.NotNull(stored);
        Assert.Equal(["repo1"], stored!.RepositoryNames);
    }

    [Fact]
    public async Task Repositories_UnknownId_Returns404ErrorBody()
    {
        using var factory = new HiveTestFactory();
        using var client = factory.CreateClient();

        var response = await client.PatchAsync(
            "/api/releases/nonexistent-xyz/repositories",
            JsonContent.Create(new { repositories = new[] { "repo1" } }), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        using var doc = JsonDocument.Parse(body);
        Assert.Contains("not found", doc.RootElement.GetProperty("error").GetString(), StringComparison.OrdinalIgnoreCase);
    }

    // ── Wire contract: delete preconditions ───────────────────────────────────

    [Fact]
    public async Task DeleteRelease_PlanningNoGoals_Returns204NoBody()
    {
        var ct = TestContext.Current.CancellationToken;
        using var factory = new HiveTestFactory();
        using var client = factory.CreateClient();

        var releaseId = UniqueId();
        await SeedAsync(factory, releaseId, "v1.0.0");

        var response = await client.DeleteAsync($"/api/releases/{releaseId}", ct);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(string.Empty, await response.Content.ReadAsStringAsync(ct));

        using var scope = factory.Services.CreateScope();
        Assert.Null(await scope.ServiceProvider.GetRequiredService<IGoalStore>().GetReleaseAsync(releaseId, ct));
    }

    [Fact]
    public async Task DeleteRelease_NonPlanning_Returns400ErrorBody()
    {
        var ct = TestContext.Current.CancellationToken;
        using var factory = new HiveTestFactory();
        using var client = factory.CreateClient();

        var releaseId = UniqueId();
        await SeedAsync(factory, releaseId, "v1.0.0", status: ReleaseStatus.Released);

        var response = await client.DeleteAsync($"/api/releases/{releaseId}", ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(body);
        Assert.Equal("Only Planning releases can be deleted.", doc.RootElement.GetProperty("error").GetString());
    }

    [Fact]
    public async Task DeleteRelease_Executing_Returns409ErrorBody()
    {
        var ct = TestContext.Current.CancellationToken;
        using var factory = new HiveTestFactory();
        using var client = factory.CreateClient();

        var releaseId = UniqueId();
        using (var scope = factory.Services.CreateScope())
        {
            var store = scope.ServiceProvider.GetRequiredService<IGoalStore>();
            await store.CreateReleaseAsync(new Release
            {
                Id = releaseId, Tag = "v1.0.0", ExecutionState = ReleaseExecutionState.Executing,
            }, ct);
        }

        var response = await client.DeleteAsync($"/api/releases/{releaseId}", ct);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(body);
        Assert.Equal("Release is currently executing — cannot delete.", doc.RootElement.GetProperty("error").GetString());
    }

    [Fact]
    public async Task DeleteRelease_GoalsAttached_Returns400ErrorBody()
    {
        var ct = TestContext.Current.CancellationToken;
        using var factory = new HiveTestFactory();
        using var client = factory.CreateClient();

        var releaseId = UniqueId();
        await SeedAsync(factory, releaseId, "v1.0.0");
        using (var scope = factory.Services.CreateScope())
        {
            var store = scope.ServiceProvider.GetRequiredService<IGoalStore>();
            await store.CreateGoalAsync(
                new Goal { Id = UniqueId(), Description = "Attached", ReleaseId = releaseId }, ct);
        }

        var response = await client.DeleteAsync($"/api/releases/{releaseId}", ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(body);
        Assert.Contains("1 goal(s)", doc.RootElement.GetProperty("error").GetString());
    }

    [Fact]
    public async Task DeleteRelease_NonExistent_Returns404ErrorBody()
    {
        using var factory = new HiveTestFactory();
        using var client = factory.CreateClient();

        var response = await client.DeleteAsync(
            $"/api/releases/{UniqueId()}", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        using var doc = JsonDocument.Parse(body);
        Assert.Contains("not found", doc.RootElement.GetProperty("error").GetString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task DeleteRelease_ConcurrentStateChange_Returns409ErrorBody()
    {
        var ct = TestContext.Current.CancellationToken;
        var fakeStore = new ConcurrentChangeFakeStore
        {
            Release = new Release { Id = "rel", Tag = "v1.0.0", RepositoryNames = [] },
        };
        var baseFactory = new HiveTestFactory();
        using var factory = baseFactory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureServices(services =>
            {
                var existing = services.SingleOrDefault(d => d.ServiceType == typeof(IGoalStore));
                if (existing is not null)
                    services.Remove(existing);
                services.AddSingleton<IGoalStore>(fakeStore);
            });
        });
        using var client = factory.CreateClient();

        var response = await client.DeleteAsync("/api/releases/rel", ct);

        // Every precondition pre-check passed, but the atomic delete reported false → 409.
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(body);
        Assert.Contains("concurrent state change", doc.RootElement.GetProperty("error").GetString(), StringComparison.OrdinalIgnoreCase);
        // The dashboard was NOT notified for this failure.
        Assert.Equal(1, fakeStore.GetReleaseCalls);
        Assert.Equal(1, fakeStore.DeleteCalls);
    }

    // ── Wire contract: validate ───────────────────────────────────────────────

    [Fact]
    public async Task ValidateRelease_NoService_Returns200WithValidTrueAndEmptyErrors()
    {
        var ct = TestContext.Current.CancellationToken;
        using var factory = new HiveTestFactory();
        using var client = factory.CreateClient();

        var releaseId = UniqueId();
        await SeedAsync(factory, releaseId, "v1.0.0", repos: ["repo1"]);

        var response = await client.GetAsync($"/api/releases/{releaseId}/validate", ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(body);
        Assert.Equal(2, doc.RootElement.EnumerateObject().Count());
        Assert.True(doc.RootElement.GetProperty("valid").GetBoolean());
        Assert.Equal(JsonValueKind.Array, doc.RootElement.GetProperty("errors").ValueKind);
        Assert.Equal(0, doc.RootElement.GetProperty("errors").GetArrayLength());
    }

    [Fact]
    public async Task ValidateRelease_UnknownId_Returns404ErrorBody()
    {
        using var factory = new HiveTestFactory();
        using var client = factory.CreateClient();

        var response = await client.GetAsync(
            "/api/releases/nonexistent-xyz/validate", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        using var doc = JsonDocument.Parse(body);
        Assert.Contains("not found", doc.RootElement.GetProperty("error").GetString(), StringComparison.OrdinalIgnoreCase);
    }

    // ── Facade side effects: exactly once via the facade ──────────────────────

    [Fact]
    public async Task Facade_SuccessSideEffects_RunExactlyOnce()
    {
        var ct = TestContext.Current.CancellationToken;
        var dbContext = CopilotHiveDbContext.CreateInMemory();
        try
        {
            var store = new GoalStore(dbContext, NullLogger<GoalStore>.Instance);
            await store.CreateReleaseAsync(new Release
            {
                Id = "v1.0.0", Tag = "v1.0.0", RepositoryNames = ["pkg-repo"],
            }, ct);
            await store.CreateGoalAsync(
                new Goal { Id = "goal-1", Description = "Test", ReleaseId = "v1.0.0", Status = GoalStatus.Completed }, ct);

            // Seed the transient progress/review knowledge documents the cleanup must delete.
            var graph = new KnowledgeGraph();
            await graph.CreateDocumentAsync("progress-goal-1", "Doc", DocumentType.Scratch, "content", ct: ct);
            await graph.CreateDocumentAsync("review-goal-1", "Doc", DocumentType.Scratch, "content", ct: ct);
            var docCleanup = new KnowledgeDocumentCleanupService(graph, NullLogger<KnowledgeDocumentCleanupService>.Instance);

            var monitor = new RecordingMonitorService();
            var config = CreateConfig("pkg-repo", publishNuGet: new NuGetPublishConfig
            {
                Packages = [new NuGetPackageEntry { PackageId = "My.Package" }],
            });
            var eventBus = new RecordingEventBus();
            var notifier = new DashboardNotifier();
            var notifyCount = 0;
            notifier.OnStateChanged += () => Interlocked.Increment(ref notifyCount);

            var fake = new ConfigurableFakeRepoManager { CreateTagResult = true };
            var execService = new ReleaseExecutionService(
                store, config, fake, NullLogger<ReleaseExecutionService>.Instance);
            var facade = new ReleaseFacade(
                store, notifier, NullLogger<ReleaseFacade>.Instance,
                execService, eventBus, monitor, config, appLifetime: null, docCleanup);

            var outcome = await facade.UpdateReleaseStatusAsync(
                "v1.0.0", new UpdateReleaseStatusRequest("Released"), ct);

            Assert.IsType<ExecutionSuccessOutcome>(outcome);

            // The release timestamp was updated on the persisted release.
            var stored = await store.GetReleaseAsync("v1.0.0", ct);
            Assert.NotNull(stored);
            Assert.Equal(ReleaseStatus.Released, stored!.Status);
            Assert.NotNull(stored.ReleasedAt);

            // The event was published exactly once.
            var evt = Assert.Single(eventBus.Published);
            Assert.Equal(EventType.ReleaseCompleted, evt.Type);
            Assert.Equal("v1.0.0", evt.ReleaseId);

            // LaunchNuGetMonitors was invoked exactly once for the PublishNuGet repo
            // (via TCS gate — no polling).
            await monitor.Completed.Task.WaitAsync(TimeSpan.FromSeconds(10), ct);
            var call = Assert.Single(monitor.Calls);
            Assert.Equal(("pkg-repo", "v1.0.0"), (call.Repo, call.Tag));

            // The knowledge documents were cleaned up (both transient docs deleted).
            Assert.Null(graph.GetDocument("progress-goal-1"));
            Assert.Null(graph.GetDocument("review-goal-1"));

            // The dashboard was notified exactly once for the whole status change.
            Assert.Equal(1, notifyCount);

            // The git operations ran exactly once.
            _ = Assert.Single(fake.MergeCalls);
            _ = Assert.Single(fake.CreateTagCalls);
        }
        finally
        {
            await dbContext.DisposeAsync();
        }
    }

    [Fact]
    public async Task Facade_CleanupCancelled_DoesNotFailTheRelease()
    {
        var ct = TestContext.Current.CancellationToken;
        var dbContext = CopilotHiveDbContext.CreateInMemory();
        try
        {
            var store = new GoalStore(dbContext, NullLogger<GoalStore>.Instance);
            await store.CreateReleaseAsync(new Release
            {
                Id = "v1.0.0", Tag = "v1.0.0", RepositoryNames = ["repo1"],
            }, ct);
            await store.CreateGoalAsync(
                new Goal { Id = "goal-1", Description = "Test", ReleaseId = "v1.0.0", Status = GoalStatus.Completed }, ct);

            var graph = new KnowledgeGraph();
            await graph.CreateDocumentAsync("progress-goal-1", "Doc", DocumentType.Scratch, "content", ct: ct);
            var docCleanup = new KnowledgeDocumentCleanupService(graph, NullLogger<KnowledgeDocumentCleanupService>.Instance);

            var eventBus = new RecordingEventBus();
            var notifier = new DashboardNotifier();
            var notifyCount = 0;
            notifier.OnStateChanged += () => Interlocked.Increment(ref notifyCount);

            var fake = new ConfigurableFakeRepoManager { CreateTagResult = true };
            var execService = new ReleaseExecutionService(
                store, CreateConfig(), fake, NullLogger<ReleaseExecutionService>.Instance);

            // Cancel the token AFTER execution succeeds but BEFORE the cleanup runs: the event
            // publication is the in-between hook (it fires after the success re-read + timestamp
            // update, both of which use default tokens, and before the knowledge-document
            // cleanup, which receives ct). The cleanup's cancellation must be contained
            // (caught + logged) and must not fail the release.
            using var cts = new CancellationTokenSource();

            var facade = new ReleaseFacade(
                store, notifier, NullLogger<ReleaseFacade>.Instance,
                execService, eventBus, nuGetMonitor: null, hiveConfig: null, appLifetime: null, docCleanup);

            // The event publication fires AFTER the success re-read + timestamp update (default
            // tokens) and BEFORE the knowledge-document cleanup (which receives ct).
            eventBus.OnPublish = _ => cts.Cancel();

            var outcome = await facade.UpdateReleaseStatusAsync(
                "v1.0.0", new UpdateReleaseStatusRequest("Released"), cts.Token);

            var success = Assert.IsType<ExecutionSuccessOutcome>(outcome);
            Assert.Equal(ReleaseStatus.Released, success.Release.Status);
            Assert.Equal(ReleaseExecutionState.Completed, success.Release.ExecutionState);

            // The event was still published and the dashboard still notified.
            _ = Assert.Single(eventBus.Published);
            Assert.Equal(1, notifyCount);

            // The cancellation was contained in the cleanup: the transient doc is intact.
            Assert.NotNull(graph.GetDocument("progress-goal-1"));
        }
        finally
        {
            await dbContext.DisposeAsync();
        }
    }

    // ── Fakes ─────────────────────────────────────────────────────────────────

    /// <summary>A recording <see cref="IEventBus"/> capturing published events.</summary>
    private sealed class RecordingEventBus : IEventBus
    {
        public List<SystemEvent> Published { get; } = [];

        /// <summary>Optional callback invoked on every publish (e.g. to cancel a token
        /// between the execution success and the knowledge-document cleanup).</summary>
        public Action<SystemEvent>? OnPublish { get; set; }

        public event Action<SystemEvent>? OnEvent
        {
            add { }
            remove { }
        }

        public void Publish(SystemEvent evt)
        {
            Published.Add(evt);
            OnPublish?.Invoke(evt);
        }
    }

    /// <summary>
    /// A <see cref="NuGetPublishMonitorService"/> whose <c>MonitorReleaseAsync</c> records each
    /// call and signals a <see cref="TaskCompletionSource"/> so tests can await it without
    /// polling or sleeps.
    /// </summary>
    private sealed class RecordingMonitorService : NuGetPublishMonitorService
    {
        public List<(string Repo, string Tag)> Calls { get; } = [];

        public TaskCompletionSource Completed { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override Task MonitorReleaseAsync(string repoName, string releaseTag, CancellationToken ct)
        {
            Calls.Add((repoName, releaseTag));
            Completed.TrySetResult();
            return Task.CompletedTask;
        }
    }

    /// <summary>
    /// An in-memory <see cref="IGoalStore"/> for the concurrent-state-change delete test:
    /// every precondition pre-check passes but the atomic delete returns <c>false</c>,
    /// exactly as the real store's <c>ExecuteDeleteAsync</c> does when a concurrent change
    /// invalidates a precondition.
    /// </summary>
    private sealed class ConcurrentChangeFakeStore : IGoalStore
    {
        public required Release Release { get; set; }

        public int GetReleaseCalls { get; private set; }

        public int DeleteCalls { get; private set; }

        public string Name => "concurrent-change-fake-store";

        public Task<Release?> GetReleaseAsync(string releaseId, CancellationToken ct = default)
        {
            GetReleaseCalls++;
            return Task.FromResult<Release?>(new Release
            {
                Id = Release.Id,
                Tag = Release.Tag,
                Status = Release.Status,
                RepositoryNames = [.. Release.RepositoryNames],
                ExecutionState = Release.ExecutionState,
            });
        }

        public Task<IReadOnlyList<Goal>> GetGoalsByReleaseAsync(string releaseId, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<Goal>>([]);

        public Task<bool> DeleteReleaseAsync(string releaseId, CancellationToken ct = default)
        {
            DeleteCalls++;
            return Task.FromResult(false);
        }

        // ── Unused IGoalStore members ─────────────────────────────────────────

        public Task<IReadOnlyList<Goal>> GetAllGoalsAsync(CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<Goal>>([]);

        public Task<Goal?> GetGoalAsync(string goalId, CancellationToken ct = default)
            => Task.FromResult<Goal?>(null);

        public Task<Goal> CreateGoalAsync(Goal goal, CancellationToken ct = default)
            => Task.FromResult(goal);

        public Task UpdateGoalAsync(Goal goal, CancellationToken ct = default)
            => Task.CompletedTask;

        public Task<bool> DeleteGoalAsync(string goalId, CancellationToken ct = default)
            => Task.FromResult(false);

        public Task<IReadOnlyList<Goal>> SearchGoalsAsync(
            string query, GoalStatus? statusFilter = null, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<Goal>>([]);

        public Task<IReadOnlyList<Goal>> GetGoalsByStatusAsync(GoalStatus status, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<Goal>>([]);

        public Task AddIterationAsync(string goalId, IterationSummary summary, CancellationToken ct = default)
            => Task.CompletedTask;

        public Task<IReadOnlyList<IterationSummary>> GetIterationsAsync(string goalId, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<IterationSummary>>([]);

        public Task<IReadOnlyList<Goal>> GetPendingGoalsAsync(CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<Goal>>([]);

        public Task UpdateGoalStatusAsync(
            string goalId, GoalStatus status, GoalUpdateMetadata? metadata = null, CancellationToken ct = default)
            => Task.CompletedTask;

        public Task<Release> CreateReleaseAsync(Release release, CancellationToken ct = default)
            => Task.FromResult(release);

        public Task UpdateReleaseAsync(Release release, CancellationToken ct = default)
            => Task.CompletedTask;

        public Task UpdateReleaseAsync(string releaseId, ReleaseUpdateData update, CancellationToken ct = default)
            => Task.CompletedTask;

        public Task<IReadOnlyList<Release>> GetReleasesAsync(CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<Release>>([Release]);

        public Task<IReadOnlyList<ConversationEntry>> GetPipelineConversationAsync(
            string goalId, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<ConversationEntry>>([]);

        public Task ResetGoalIterationDataAsync(string goalId, CancellationToken ct = default)
            => Task.CompletedTask;

        public Task<IReadOnlyList<(string GoalId, PersistedClarification Clarification)>> GetAllClarificationsAsync(
            int? limit = null, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<(string, PersistedClarification)>>([]);
    }
}