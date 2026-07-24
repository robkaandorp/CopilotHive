using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using CopilotHive.Configuration;
using CopilotHive.Git;
using CopilotHive.Services;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace CopilotHive.Tests;

/// <summary>
/// Integration tests for the available-models configuration REST endpoints registered by
/// <c>ConfigHub.MapConfigEndpoints</c>. Uses <see cref="HiveTestFactory"/> to boot the real
/// application from <c>Program.cs</c>.
/// </summary>
/// <remarks>
/// The test factory boots the app without a <c>--config-repo</c> argument, so
/// <c>ConfigModelService</c> and <c>ModelDiscoveryService</c> are not registered. These tests
/// therefore exercise the service-null (<c>Results.Problem</c>) path, verifying the endpoints are
/// wired up and respond gracefully instead of throwing.
/// </remarks>
[Collection("HiveIntegration")]
public class ConfigEndpointsTests
{
    private readonly HttpClient _client;

    /// <summary>Receives the shared factory and creates an <see cref="HttpClient"/> backed by the test server.</summary>
    /// <param name="factory">The shared <see cref="HiveTestFactory"/> fixture for this test class.</param>
    public ConfigEndpointsTests(HiveTestFactory factory)
    {
        _client = factory.CreateClient();
    }

    // ── GET /api/config/models/discover ──────────────────────────────────────

    [Fact]
    public async Task GetDiscover_Endpoint_IsRouted()
    {
        var response = await _client.GetAsync("/api/config/models/discover", TestContext.Current.CancellationToken);

        // The route exists; it must not 404. Without a registered discovery service it returns Problem (500).
        Assert.NotEqual(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
    }

    // ── POST /api/config/available-models ────────────────────────────────────

    [Fact]
    public async Task PostAvailableModel_Endpoint_IsRouted()
    {
        var response = await _client.PostAsJsonAsync(
            "/api/config/available-models",
            new { name = "copilot/test-model", contextWindow = 128000, reasoningEffort = "high" },
            TestContext.Current.CancellationToken);

        Assert.NotEqual(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
    }

    // ── PUT /api/config/available-models/{name} ──────────────────────────────

    [Fact]
    public async Task PutAvailableModel_Endpoint_IsRouted()
    {
        var response = await _client.PutAsJsonAsync(
            "/api/config/available-models/copilot%2Ftest-model",
            new { name = "copilot/test-model", contextWindow = 256000, reasoningEffort = "medium" },
            TestContext.Current.CancellationToken);

        Assert.NotEqual(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
    }

    // ── DELETE /api/config/available-models/{name} ───────────────────────────

    [Fact]
    public async Task DeleteAvailableModel_Endpoint_IsRouted()
    {
        var response = await _client.DeleteAsync(
            "/api/config/available-models/copilot%2Ftest-model",
            TestContext.Current.CancellationToken);

        Assert.NotEqual(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
    }

    // ── GET /api/config/repositories ─────────────────────────────────────────────

    [Fact]
    public async Task GetRepositories_Endpoint_IsRouted()
    {
        var response = await _client.GetAsync("/api/config/repositories", TestContext.Current.CancellationToken);

        // Route exists; returns NotFound (404) when HiveConfigFile is not registered (no config repo).
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // ── POST /api/config/repositories ───────────────────────────────────────────

    [Fact]
    public async Task PostRepository_Endpoint_IsRouted()
    {
        var response = await _client.PostAsJsonAsync(
            "/api/config/repositories",
            new { name = "test-repo", url = "https://github.com/org/repo.git", defaultBranch = "main" },
            TestContext.Current.CancellationToken);

        Assert.NotEqual(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
    }

    // ── PUT /api/config/repositories/{name} ──────────────────────────────────────

    [Fact]
    public async Task PutRepository_Endpoint_IsRouted()
    {
        var response = await _client.PutAsJsonAsync(
            "/api/config/repositories/test-repo",
            new { name = "test-repo", url = "https://github.com/org/repo.git", defaultBranch = "main" },
            TestContext.Current.CancellationToken);

        Assert.NotEqual(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
    }

    // ── DELETE /api/config/repositories/{name} ───────────────────────────────────

    [Fact]
    public async Task DeleteRepository_Endpoint_IsRouted()
    {
        var response = await _client.DeleteAsync(
            "/api/config/repositories/test-repo",
            TestContext.Current.CancellationToken);

        Assert.NotEqual(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
    }

    // ── RepositoryRequest.Release JSON binding (System.Text.Json) ────────────────
    // These tests deserialize JSON directly into RepositoryRequest to prove the
    // Release field binds via camelCase property names. They FAIL if the Release
    // parameter is removed from RepositoryRequest or if the JSON property names
    // (release / mergeTo / tagBranch) no longer map — coverage the route-availability
    // tests above cannot provide (those hit the null-service guard before binding).
    // Uses JsonSerializerDefaults.Web to mirror the ASP.NET minimal-API binding pipeline
    // (case-insensitive, camelCase) that actually deserializes RepositoryRequest at runtime.

    private static readonly System.Text.Json.JsonSerializerOptions WebJsonOptions =
        new(System.Text.Json.JsonSerializerDefaults.Web);

    [Fact]
    public void RepositoryRequest_DeserializesReleaseWithBothFields()
    {
        const string json =
            "{\"name\":\"test\",\"url\":\"https://github.com/org/repo.git\",\"defaultBranch\":\"main\"," +
            "\"release\":{\"mergeTo\":\"main\",\"tagBranch\":\"develop\"}}";

        var req = System.Text.Json.JsonSerializer.Deserialize<RepositoryRequest>(json, WebJsonOptions);

        Assert.NotNull(req);
        Assert.Equal("test", req!.Name);
        Assert.Equal("main", req.DefaultBranch);
        Assert.NotNull(req.Release);
        Assert.Equal("main", req.Release!.MergeTo);
        Assert.Equal("develop", req.Release!.TagBranch);
    }

    [Fact]
    public void RepositoryRequest_MissingReleaseField_DefaultsToNull()
    {
        const string json =
            "{\"name\":\"test\",\"url\":\"https://github.com/org/repo.git\",\"defaultBranch\":\"main\"}";

        var req = System.Text.Json.JsonSerializer.Deserialize<RepositoryRequest>(json, WebJsonOptions);

        Assert.NotNull(req);
        Assert.Null(req!.Release);
    }

    [Fact]
    public void RepositoryRequest_ExplicitNullRelease_IsNull()
    {
        const string json =
            "{\"name\":\"test\",\"url\":\"https://github.com/org/repo.git\",\"defaultBranch\":\"main\",\"release\":null}";

        var req = System.Text.Json.JsonSerializer.Deserialize<RepositoryRequest>(json, WebJsonOptions);

        Assert.NotNull(req);
        Assert.Null(req!.Release);
    }

    [Fact]
    public void RepositoryRequest_EmptyReleaseObject_HasNullFields()
    {
        const string json =
            "{\"name\":\"test\",\"url\":\"https://github.com/org/repo.git\",\"defaultBranch\":\"main\"," +
            "\"release\":{\"mergeTo\":null,\"tagBranch\":null}}";

        var req = System.Text.Json.JsonSerializer.Deserialize<RepositoryRequest>(json, WebJsonOptions);

        Assert.NotNull(req);
        Assert.NotNull(req!.Release);
        Assert.Null(req.Release!.MergeTo);
        Assert.Null(req.Release!.TagBranch);
    }

    // ── GET /api/config/orchestrator ─────────────────────────────────────────────

    [Fact]
    public async Task GetOrchestrator_Endpoint_IsRouted()
    {
        var response = await _client.GetAsync("/api/config/orchestrator", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // ── PATCH /api/config/orchestrator ───────────────────────────────────────────

    [Fact]
    public async Task PatchOrchestrator_Endpoint_IsRouted()
    {
        var content = new StringContent("{\"maxIterations\":10}", System.Text.Encoding.UTF8, "application/json");
        var response = await _client.PatchAsync("/api/config/orchestrator", content, TestContext.Current.CancellationToken);

        Assert.NotEqual(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
    }

    // ── GET /api/config/workers ─────────────────────────────────────────────────

    [Fact]
    public async Task GetWorkers_Endpoint_IsRouted()
    {
        var response = await _client.GetAsync("/api/config/workers", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // ── PATCH /api/config/workers ───────────────────────────────────────────────

    [Fact]
    public async Task PatchWorkers_Endpoint_IsRouted()
    {
        var content = new StringContent("{\"coder\":50000}", System.Text.Encoding.UTF8, "application/json");
        var response = await _client.PatchAsync("/api/config/workers", content, TestContext.Current.CancellationToken);

        Assert.NotEqual(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
    }

    // ── GET /api/config/repositories/{name}/branches ──────────────────────────────

    [Fact]
    public async Task GetRepositoryBranches_ClonedRepo_ReturnsBranches()
    {
        var ct = TestContext.Current.CancellationToken;
        var fake = new ConfigurableFakeBranchRepoManager(["main", "develop"]);
        using var factory = CreateBranchFactory(fake);
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/api/config/repositories/test-repo/branches", ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var branches = await response.Content.ReadFromJsonAsync<List<string>>(JsonSerializerOptions.Web, ct);
        Assert.NotNull(branches);
        Assert.Equal(["main", "develop"], branches!);
    }

    [Fact]
    public async Task GetRepositoryBranches_NotCloned_Returns404WithSafeMessage()
    {
        var ct = TestContext.Current.CancellationToken;
        var fake = new ConfigurableFakeBranchRepoManager([]) { ThrowOnList = new InvalidOperationException("Repository 'test-repo' is not cloned.") };
        using var factory = CreateBranchFactory(fake);
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/api/config/repositories/test-repo/branches", ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync(ct);
        Assert.Contains("is not cloned", body);
    }



    [Fact]
    public async Task GetRepositoryBranches_GitFailure_Returns500WithoutRawUrl()
    {
        var ct = TestContext.Current.CancellationToken;
        var sensitive = "https://user:token@github.com/org/repo.git";
        var fake = new ConfigurableFakeBranchRepoManager([]) { ThrowOnList = new InvalidOperationException($"fatal: {sensitive}") };
        using var factory = CreateBranchFactory(fake);
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/api/config/repositories/test-repo/branches", ct);

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync(ct);
        Assert.DoesNotContain(sensitive, body);
        Assert.Contains("Failed to list branches for this repository.", body);
    }

    private static WebApplicationFactory<Program> CreateBranchFactory(ConfigurableFakeBranchRepoManager fake)
    {
        var config = new HiveConfigFile
        {
            Repositories =
            [
                new RepositoryConfig
                {
                    Name = "test-repo",
                    Url = "https://github.com/org/repo.git",
                    DefaultBranch = "main",
                },
            ],
        };
        var baseFactory = new HiveTestFactory { MockRepoManager = fake };
        return baseFactory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureServices(services =>
            {
                var existingConfig = services.SingleOrDefault(d => d.ServiceType == typeof(HiveConfigFile));
                if (existingConfig is not null)
                    services.Remove(existingConfig);
                services.AddSingleton(config);
            });
        });
    }

    internal sealed class ConfigurableFakeBranchRepoManager : IBrainRepoManager
    {
        private readonly List<string> _branches;

        public ConfigurableFakeBranchRepoManager(List<string> branches)
        {
            _branches = branches;
        }

        public Exception? ThrowOnList { get; set; }
        public bool ValidateNames { get; set; }

        public string WorkDirectory => "/fake/work";

        public Task<List<string>> ListRemoteBranchesAsync(string repoName, CancellationToken ct = default)
        {
            if (ValidateNames && (string.IsNullOrWhiteSpace(repoName) || repoName.Contains('/') || repoName.Contains("\\") || repoName.Contains("..")))
                return Task.FromException<List<string>>(new ArgumentException($"Repository name '{repoName}' is invalid."));
            if (ThrowOnList is not null)
                return Task.FromException<List<string>>(ThrowOnList);
            return Task.FromResult(_branches);
        }

        public Task<string> EnsureCloneAsync(string repoName, string repoUrl, string defaultBranch, CancellationToken ct = default) =>
            Task.FromResult($"/fake/work/{repoName}");
        public Task<string> MergeFeatureBranchAsync(string repoName, string featureBranch, string defaultBranch, string commitMessage, CancellationToken ct = default) =>
            Task.FromResult("fake-sha");
        public Task<BranchDeleteResult> DeleteRemoteBranchAsync(string repoName, string branchName, CancellationToken ct = default) =>
            Task.FromResult(BranchDeleteResult.Success);
        public string GetClonePath(string repoName) => $"/fake/work/{repoName}";
        public Task<string?> GetHeadShaAsync(string repoName, CancellationToken ct = default) =>
            Task.FromResult<string?>(null);
        public Task<string?> MergeBranchAsync(string repoName, string sourceBranch, string targetBranch, CancellationToken ct = default) =>
            Task.FromResult<string?>(null);
        public Task<bool> CreateTagAsync(string repoName, string tag, string branch, string message, CancellationToken ct = default) =>
            Task.FromResult(false);
        public Task<bool> DeleteTagAsync(string repoName, string tag, CancellationToken ct = default) =>
            Task.FromResult(false);
    }

    // ── GET /api/config/composer ────────────────────────────────────────────────

    [Fact]
    public async Task GetComposer_Endpoint_IsRouted()
    {
        var response = await _client.GetAsync("/api/config/composer", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // ── PATCH /api/config/composer ──────────────────────────────────────────────

    [Fact]
    public async Task PatchComposer_Endpoint_IsRouted()
    {
        var content = new StringContent("{\"contextWindow\":200000,\"maxSteps\":50}", System.Text.Encoding.UTF8, "application/json");
        var response = await _client.PatchAsync("/api/config/composer", content, TestContext.Current.CancellationToken);

        Assert.NotEqual(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
    }
}
