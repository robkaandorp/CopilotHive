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

    // ── POST/PUT /api/config/repositories — CI monitoring fields ─────────────────
    // These tests use CustomEndpointFactory (registered ConfigModelService + fake
    // config repo) so the endpoints perform real CRUD and the config is observable.

    [Fact]
    public async Task PostRepository_WithMonitorCiAndTimeout_Returns200AndPersists()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"copilothive-repo-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        using var baseFactory = new CustomEndpointFactory(tempDir);
        using var factory = baseFactory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureServices(services =>
            {
                var existing = services.SingleOrDefault(d => d.ServiceType == typeof(IBrainRepoManager));
                if (existing is not null) services.Remove(existing);
                services.AddSingleton<IBrainRepoManager>(new ConfigurableFakeBranchRepoManager(["main"]));
            });
        });
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync(
            "/api/config/repositories",
            new { name = "ci-repo", url = "https://github.com/org/ci-repo.git", defaultBranch = "main", monitorCi = true, ciTimeoutMinutes = 45 },
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var repo = Assert.Single(baseFactory.Config.Repositories);
        Assert.True(repo.MonitorCi);
        Assert.Equal(45, repo.CiTimeoutMinutes);
    }

    [Fact]
    public async Task PostRepository_ZeroCiTimeout_Returns400BadRequest()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"copilothive-repo-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        using var baseFactory = new CustomEndpointFactory(tempDir);
        using var factory = baseFactory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureServices(services =>
            {
                var existing = services.SingleOrDefault(d => d.ServiceType == typeof(IBrainRepoManager));
                if (existing is not null) services.Remove(existing);
                services.AddSingleton<IBrainRepoManager>(new ConfigurableFakeBranchRepoManager(["main"]));
            });
        });
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync(
            "/api/config/repositories",
            new { name = "bad-repo", url = "https://github.com/org/bad-repo.git", defaultBranch = "main", monitorCi = true, ciTimeoutMinutes = 0 },
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(baseFactory.Config.Repositories);
    }

    [Fact]
    public async Task PutRepository_WithMonitorCi_Returns200AndUpdates()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"copilothive-repo-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        using var baseFactory = new CustomEndpointFactory(tempDir);
        using var factory = baseFactory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureServices(services =>
            {
                var existing = services.SingleOrDefault(d => d.ServiceType == typeof(IBrainRepoManager));
                if (existing is not null) services.Remove(existing);
                services.AddSingleton<IBrainRepoManager>(new ConfigurableFakeBranchRepoManager(["main"]));
            });
        });
        using var client = factory.CreateClient();

        // Seed a repository first.
        var postResponse = await client.PostAsJsonAsync(
            "/api/config/repositories",
            new { name = "ci-repo", url = "https://github.com/org/ci-repo.git", defaultBranch = "main" },
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, postResponse.StatusCode);

        var putResponse = await client.PutAsJsonAsync(
            "/api/config/repositories/ci-repo",
            new { name = "ci-repo", url = "https://github.com/org/ci-repo.git", defaultBranch = "main", monitorCi = true },
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, putResponse.StatusCode);
        var repo = Assert.Single(baseFactory.Config.Repositories);
        Assert.True(repo.MonitorCi);
        Assert.Equal(30, repo.CiTimeoutMinutes);
    }

    [Fact]
    public async Task PutRepository_WithCiTimeout_Returns200AndPersists()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"copilothive-repo-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        using var baseFactory = new CustomEndpointFactory(tempDir);
        using var factory = baseFactory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureServices(services =>
            {
                var existing = services.SingleOrDefault(d => d.ServiceType == typeof(IBrainRepoManager));
                if (existing is not null) services.Remove(existing);
                services.AddSingleton<IBrainRepoManager>(new ConfigurableFakeBranchRepoManager(["main"]));
            });
        });
        using var client = factory.CreateClient();

        // Seed a repository first.
        var postResponse = await client.PostAsJsonAsync(
            "/api/config/repositories",
            new { name = "ci-repo", url = "https://github.com/org/ci-repo.git", defaultBranch = "main", monitorCi = true, ciTimeoutMinutes = 30 },
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, postResponse.StatusCode);

        var putResponse = await client.PutAsJsonAsync(
            "/api/config/repositories/ci-repo",
            new { name = "ci-repo", url = "https://github.com/org/ci-repo.git", defaultBranch = "main", monitorCi = true, ciTimeoutMinutes = 90 },
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, putResponse.StatusCode);
        var repo = Assert.Single(baseFactory.Config.Repositories);
        Assert.True(repo.MonitorCi);
        Assert.Equal(90, repo.CiTimeoutMinutes);
    }

    [Fact]
    public async Task PutRepository_OutOfRangeCiTimeout_Returns400BadRequest()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"copilothive-repo-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        using var baseFactory = new CustomEndpointFactory(tempDir);
        using var factory = baseFactory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureServices(services =>
            {
                var existing = services.SingleOrDefault(d => d.ServiceType == typeof(IBrainRepoManager));
                if (existing is not null) services.Remove(existing);
                services.AddSingleton<IBrainRepoManager>(new ConfigurableFakeBranchRepoManager(["main"]));
            });
        });
        using var client = factory.CreateClient();

        // Seed a repository first.
        var postResponse = await client.PostAsJsonAsync(
            "/api/config/repositories",
            new { name = "ci-repo", url = "https://github.com/org/ci-repo.git", defaultBranch = "main" },
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, postResponse.StatusCode);

        var putResponse = await client.PutAsJsonAsync(
            "/api/config/repositories/ci-repo",
            new { name = "ci-repo", url = "https://github.com/org/ci-repo.git", defaultBranch = "main", ciTimeoutMinutes = 121 },
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, putResponse.StatusCode);
        // The original value must be unchanged.
        var repo = Assert.Single(baseFactory.Config.Repositories);
        Assert.Equal(30, repo.CiTimeoutMinutes);
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

    [Fact]
    public async Task GetRepositoryBranches_InvalidName_Returns400()
    {
        var ct = TestContext.Current.CancellationToken;
        var fake = new ConfigurableFakeBranchRepoManager([]) { ValidateNames = true };
        using var factory = CreateBranchFactory(fake);
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/api/config/repositories/..%2Fescape/branches", ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync(ct);
        Assert.Contains("error", body);
    }

    [Fact]
    public async Task GetRepositoryBranches_NoRepoManager_Returns503()
    {
        var ct = TestContext.Current.CancellationToken;

        // The endpoint parameter is [FromServices] IBrainRepoManager? — nullable. When the
        // service is not registered, ASP.NET passes null and the endpoint returns 503. To reach
        // this path we remove IBrainRepoManager plus every service that requires it during
        // startup (GoalDispatcher singleton + its hosted-service registration), so the host can
        // still boot. StaleWorkerCleanupService (which does not depend on IBrainRepoManager) is
        // re-added so the app retains a hosted service.
        using var factory = new HiveTestFactory().WithWebHostBuilder(builder =>
        {
            builder.ConfigureServices(services =>
            {
                // Remove IBrainRepoManager so the endpoint receives null → 503.
                var repoMgr = services.SingleOrDefault(d => d.ServiceType == typeof(IBrainRepoManager));
                if (repoMgr is not null)
                    services.Remove(repoMgr);

                // Remove GoalDispatcher singleton (constructor requires IBrainRepoManager).
                var dispatcher = services.SingleOrDefault(d => d.ServiceType == typeof(GoalDispatcher));
                if (dispatcher is not null)
                    services.Remove(dispatcher);

                // Remove all IHostedService registrations (GoalDispatcher's factory resolves
                // GoalDispatcher which is now gone) and re-add the one that is independent.
                var hostedServices = services
                    .Where(d => d.ServiceType == typeof(Microsoft.Extensions.Hosting.IHostedService))
                    .ToList();
                foreach (var hs in hostedServices)
                    services.Remove(hs);
                services.AddHostedService<StaleWorkerCleanupService>();
            });
        });
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/api/config/repositories/test-repo/branches", ct);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
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

    // ── PATCH /api/config/composer — event notifications (real service) ────────

    [Fact]
    public async Task PatchComposer_EventNotifications_Returns200AndPersists()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"copilothive-composer-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        using var baseFactory = new CustomEndpointFactory(tempDir);
        using var factory = baseFactory.WithWebHostBuilder(builder => { });
        using var client = factory.CreateClient();

        var response = await client.PatchAsJsonAsync(
            "/api/config/composer",
            new
            {
                maxSteps = 75,
                eventNotificationsMode = "active",
                eventNotificationsActiveEvents = new[] { "goal_completed", "ci_failed" },
                eventNotificationsThrottleSeconds = 60,
            },
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.NotNull(baseFactory.Config.Composer);
        Assert.Equal(75, baseFactory.Config.Composer!.MaxSteps);
        Assert.NotNull(baseFactory.Config.Composer.EventNotifications);
        Assert.Equal("active", baseFactory.Config.Composer.EventNotifications!.Mode);
        Assert.Equal(["goal_completed", "ci_failed"], baseFactory.Config.Composer.EventNotifications.ActiveEvents);
        Assert.Equal(60, baseFactory.Config.Composer.EventNotifications.ThrottleSeconds);
    }

    [Fact]
    public async Task PatchComposer_InvalidMode_Returns400()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"copilothive-composer-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        using var baseFactory = new CustomEndpointFactory(tempDir);
        using var factory = baseFactory.WithWebHostBuilder(builder => { });
        using var client = factory.CreateClient();

        var response = await client.PatchAsJsonAsync(
            "/api/config/composer",
            new { eventNotificationsMode = "bogus" },
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Null(baseFactory.Config.Composer);
    }

    [Fact]
    public async Task PatchComposer_InvalidEvents_Returns400()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"copilothive-composer-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        using var baseFactory = new CustomEndpointFactory(tempDir);
        using var factory = baseFactory.WithWebHostBuilder(builder => { });
        using var client = factory.CreateClient();

        var response = await client.PatchAsJsonAsync(
            "/api/config/composer",
            new { eventNotificationsActiveEvents = new[] { "not_an_event" } },
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Null(baseFactory.Config.Composer);
    }

    [Fact]
    public async Task PatchComposer_EmptyEvents_Returns400()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"copilothive-composer-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        using var baseFactory = new CustomEndpointFactory(tempDir);
        using var factory = baseFactory.WithWebHostBuilder(builder => { });
        using var client = factory.CreateClient();

        var response = await client.PatchAsJsonAsync(
            "/api/config/composer",
            new { eventNotificationsActiveEvents = Array.Empty<string>() },
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Null(baseFactory.Config.Composer);
    }

    /// <summary>
    /// Regression: names that differ from a canonical whitelist entry only in underscore
    /// structure must be rejected with a 400, not silently canonicalized and persisted.
    /// </summary>
    [Theory]
    [InlineData("goal__completed")]
    [InlineData("_goal_completed")]
    [InlineData("goal_completed_")]
    [InlineData("ci__failed")]
    [InlineData(" goal_completed ")]
    public async Task PatchComposer_MalformedNearWhitelistEvent_Returns400(string malformed)
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"copilothive-composer-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        using var baseFactory = new CustomEndpointFactory(tempDir);
        using var factory = baseFactory.WithWebHostBuilder(builder => { });
        using var client = factory.CreateClient();

        var response = await client.PatchAsJsonAsync(
            "/api/config/composer",
            new { eventNotificationsActiveEvents = new[] { malformed } },
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Contains("Invalid active event", body, StringComparison.Ordinal);
        // Nothing was persisted — validation runs before any mutation.
        Assert.Null(baseFactory.Config.Composer);
    }

    /// <summary>
    /// The counterpart to the malformed cases: both canonical spellings (snake_case and
    /// PascalCase) are accepted case-insensitively and persist the canonical snake_case form.
    /// </summary>
    [Theory]
    [InlineData("goal_completed")]
    [InlineData("GoalCompleted")]
    [InlineData("GOAL_COMPLETED")]
    [InlineData("goalcompleted")]
    public async Task PatchComposer_CanonicalEventSpelling_Returns200AndCanonicalizes(string spelling)
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"copilothive-composer-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        using var baseFactory = new CustomEndpointFactory(tempDir);
        using var factory = baseFactory.WithWebHostBuilder(builder => { });
        using var client = factory.CreateClient();

        var response = await client.PatchAsJsonAsync(
            "/api/config/composer",
            new { eventNotificationsActiveEvents = new[] { spelling } },
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(["goal_completed"], baseFactory.Config.Composer!.EventNotifications!.ActiveEvents);
    }

    [Fact]
    public async Task PatchComposer_PartialUpdate_OnlyChangedFields()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"copilothive-composer-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        using var baseFactory = new CustomEndpointFactory(tempDir);
        using var factory = baseFactory.WithWebHostBuilder(builder => { });
        using var client = factory.CreateClient();

        // Seed a composer with existing event notification settings.
        baseFactory.Config.Composer = new ComposerConfig
        {
            MaxSteps = 50,
            EventNotifications = new EventNotificationsConfig
            {
                Mode = "active",
                ActiveEvents = ["goal_completed", "goal_failed"],
                ThrottleSeconds = 45
            }
        };

        // Only change the mode; everything else must stay.
        var response = await client.PatchAsJsonAsync(
            "/api/config/composer",
            new { eventNotificationsMode = "off" },
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(50, baseFactory.Config.Composer!.MaxSteps);
        Assert.Equal("off", baseFactory.Config.Composer.EventNotifications!.Mode);
        Assert.Equal(["goal_completed", "goal_failed"], baseFactory.Config.Composer.EventNotifications.ActiveEvents);
        Assert.Equal(45, baseFactory.Config.Composer.EventNotifications.ThrottleSeconds);
    }

    [Fact]
    public async Task PatchComposer_ThrottleOutOfRange_ClampedAndReturns200()
    {
        // Out-of-range throttle is clamped, not rejected — PATCH must return 200.
        var tempDir = Path.Combine(Path.GetTempPath(), $"copilothive-composer-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        using var baseFactory = new CustomEndpointFactory(tempDir);
        using var factory = baseFactory.WithWebHostBuilder(builder => { });
        using var client = factory.CreateClient();

        baseFactory.Config.Composer = new ComposerConfig
        {
            MaxSteps = 50,
            EventNotifications = new EventNotificationsConfig
            {
                Mode = "active",
                ActiveEvents = ["goal_completed"],
                ThrottleSeconds = 30
            }
        };

        var response = await client.PatchAsJsonAsync(
            "/api/config/composer",
            new { eventNotificationsThrottleSeconds = 9999 },
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(300, baseFactory.Config.Composer!.EventNotifications!.ThrottleSeconds);
    }

    // ── GET /api/config/composer — effective values ────────────────────────────

    [Fact]
    public async Task GetComposer_NullComposer_ReturnsDefaults()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"copilothive-composer-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        using var baseFactory = new CustomEndpointFactory(tempDir);
        using var factory = baseFactory.WithWebHostBuilder(builder => { });
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/api/config/composer", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        Assert.Equal(JsonValueKind.Null, body.GetProperty("model").ValueKind);
        Assert.Equal(0, body.GetProperty("models").GetArrayLength());
        Assert.Equal(50, body.GetProperty("maxSteps").GetInt32());
        Assert.Equal(JsonValueKind.Null, body.GetProperty("reasoningEffort").ValueKind);
        Assert.Equal("passive", body.GetProperty("eventNotifications").GetProperty("mode").GetString());
        var activeEvents = body.GetProperty("eventNotifications").GetProperty("activeEvents")
            .EnumerateArray().Select(e => e.GetString()).ToList();
        Assert.Equal(["goal_completed", "goal_failed", "ci_failed", "issue_raised"], activeEvents);
        Assert.Equal(30, body.GetProperty("eventNotifications").GetProperty("throttleSeconds").GetInt32());
    }

    [Fact]
    public async Task GetComposer_EmptyActiveEvents_ReturnsAllFour()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"copilothive-composer-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        using var baseFactory = new CustomEndpointFactory(tempDir);
        using var factory = baseFactory.WithWebHostBuilder(builder => { });
        using var client = factory.CreateClient();

        baseFactory.Config.Composer = new ComposerConfig
        {
            MaxSteps = 50,
            EventNotifications = new EventNotificationsConfig
            {
                Mode = "active",
                ActiveEvents = [],
                ThrottleSeconds = 30
            }
        };

        var response = await client.GetAsync("/api/config/composer", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        var activeEvents = body.GetProperty("eventNotifications").GetProperty("activeEvents")
            .EnumerateArray().Select(e => e.GetString()).ToList();
        Assert.Equal(["goal_completed", "goal_failed", "ci_failed", "issue_raised"], activeEvents);
    }

    [Fact]
    public async Task GetComposer_InvalidMode_ReturnsPassive()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"copilothive-composer-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        using var baseFactory = new CustomEndpointFactory(tempDir);
        using var factory = baseFactory.WithWebHostBuilder(builder => { });
        using var client = factory.CreateClient();

        baseFactory.Config.Composer = new ComposerConfig
        {
            MaxSteps = 50,
            EventNotifications = new EventNotificationsConfig
            {
                Mode = "bogus",
                ActiveEvents = ["goal_completed"],
                ThrottleSeconds = 30
            }
        };

        var response = await client.GetAsync("/api/config/composer", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        Assert.Equal("passive", body.GetProperty("eventNotifications").GetProperty("mode").GetString());
    }

    [Fact]
    public async Task GetComposer_OutOfRangeThrottle_ClampedToRange()
    {
        // Seed a config whose raw ThrottleSeconds exceeds the [1, 300] window. The GET
        // endpoint must return the clamped effective value (300), not the raw 999.
        // This verifies the "throttle clamped" effective-value requirement.
        var tempDir = Path.Combine(Path.GetTempPath(), $"copilothive-composer-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        using var baseFactory = new CustomEndpointFactory(tempDir);
        using var factory = baseFactory.WithWebHostBuilder(builder => { });
        using var client = factory.CreateClient();

        baseFactory.Config.Composer = new ComposerConfig
        {
            MaxSteps = 50,
            EventNotifications = new EventNotificationsConfig
            {
                Mode = "active",
                ActiveEvents = ["goal_completed"],
                ThrottleSeconds = 999
            }
        };

        var response = await client.GetAsync("/api/config/composer", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        Assert.Equal(300, body.GetProperty("eventNotifications").GetProperty("throttleSeconds").GetInt32());
    }

    [Fact]
    public async Task GetComposer_NullThrottle_ReturnsDefault30()
    {
        // When ThrottleSeconds is null the effective value defaults to 30.
        var tempDir = Path.Combine(Path.GetTempPath(), $"copilothive-composer-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        using var baseFactory = new CustomEndpointFactory(tempDir);
        using var factory = baseFactory.WithWebHostBuilder(builder => { });
        using var client = factory.CreateClient();

        baseFactory.Config.Composer = new ComposerConfig
        {
            MaxSteps = 50,
            EventNotifications = new EventNotificationsConfig
            {
                Mode = "active",
                ActiveEvents = ["goal_completed"],
                ThrottleSeconds = null
            }
        };

        var response = await client.GetAsync("/api/config/composer", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        Assert.Equal(30, body.GetProperty("eventNotifications").GetProperty("throttleSeconds").GetInt32());
    }
}
