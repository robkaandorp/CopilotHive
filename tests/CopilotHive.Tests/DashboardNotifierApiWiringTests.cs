using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

using CopilotHive.Dashboard;
using CopilotHive.Goals;
using CopilotHive.Git;
using CopilotHive.Services;
using CopilotHive.Configuration;

using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace CopilotHive.Tests;

/// <summary>
/// Integration tests verifying <see cref="DashboardNotifier.NotifyStateChanged()"/> is wired
/// into every mutating API endpoint. Uses <see cref="WebApplicationFactory{Program}"/> with a
/// custom DashboardNotifier singleton so the notification count can be asserted precisely.
/// </summary>
[Collection("HiveIntegration")]
public sealed class DashboardNotifierApiWiringTests
{
    private static readonly JsonSerializerOptions JsonOpts =
        new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private static string UniqueId() => "test-" + Guid.NewGuid().ToString("N")[..16];

    private static StringContent JsonContent(object data) =>
        new(JsonSerializer.Serialize(data, JsonOpts), Encoding.UTF8, "application/json");

    /// <summary>
    /// Creates a factory with a shared <see cref="DashboardNotifier"/> singleton that tests
    /// can subscribe to. Returns the factory, the notifier, and a thread-safe counter.
    /// </summary>
    private static (WebApplicationFactory<Program> factory, DashboardNotifier notifier, int[] count)
        CreateFactoryWithNotifier(HiveTestFactory? existingFactory = null)
    {
        var notifier = new DashboardNotifier();
        var counter = new int[1];
        notifier.OnStateChanged += () => Interlocked.Increment(ref counter[0]);

        var baseFactory = existingFactory ?? new HiveTestFactory();
        var factory = baseFactory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureServices(services =>
            {
                // Replace the existing DashboardNotifier singleton with our test instance
                var existing = services.SingleOrDefault(d => d.ServiceType == typeof(DashboardNotifier));
                if (existing is not null)
                    services.Remove(existing);
                services.AddSingleton(notifier);
            });
        });

        return (factory, notifier, counter);
    }

    // ── API goal create → 1 (criterion 32) ─────────────────────────────────

    [Fact]
    public async Task ApiGoalCreate_NotifiesOnce()
    {
        var (factory, _, count) = CreateFactoryWithNotifier();
        using var client = factory.CreateClient();
        var id = UniqueId();

        var response = await client.PostAsync("/api/goals",
            JsonContent(new { id, description = "Test goal" }),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal(1, count[0]);
    }

    // ── API goal status (valid) → 1 (criterion 33) ──────────────────────────

    [Fact]
    public async Task ApiGoalStatus_ValidTransition_NotifiesOnce()
    {
        var (factory, _, count) = CreateFactoryWithNotifier();
        using var client = factory.CreateClient();
        var id = UniqueId();

        // Create goal (notifies 1)
        await client.PostAsync("/api/goals",
            JsonContent(new { id, description = "Test goal" }),
            TestContext.Current.CancellationToken);
        count[0] = 0;

        // Pending → Draft is a valid transition
        var response = await client.PatchAsync(
            $"/api/goals/{id}/status",
            JsonContent(new { status = "Draft" }),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(1, count[0]);
    }

    // ── API goal status (same, rejected) → 0 (criterion 34) ─────────────────

    [Fact]
    public async Task ApiGoalStatus_InvalidTransition_DoesNotNotify()
    {
        var (factory, _, count) = CreateFactoryWithNotifier();
        using var client = factory.CreateClient();
        var id = UniqueId();

        // Create goal (Pending status)
        await client.PostAsync("/api/goals",
            JsonContent(new { id, description = "Test goal" }),
            TestContext.Current.CancellationToken);
        count[0] = 0;

        // Pending → Completed is an invalid transition → 400, no notification
        var response = await client.PatchAsync(
            $"/api/goals/{id}/status",
            JsonContent(new { status = "Completed" }),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(0, count[0]);
    }

    // ── API goal delete → 1 (criterion 35) ─────────────────────────────────

    [Fact]
    public async Task ApiGoalDelete_NotifiesOnce()
    {
        var (factory, _, count) = CreateFactoryWithNotifier();
        using var client = factory.CreateClient();
        var id = UniqueId();

        // Create goal and move to Draft so deletion is allowed
        await client.PostAsync("/api/goals",
            JsonContent(new { id, description = "Test goal" }),
            TestContext.Current.CancellationToken);
        await client.PatchAsync(
            $"/api/goals/{id}/status",
            JsonContent(new { status = "Draft" }),
            TestContext.Current.CancellationToken);
        count[0] = 0;

        var response = await client.DeleteAsync($"/api/goals/{id}",
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(1, count[0]);
    }

    // ── API goal release assign → 1 (criterion 36) ──────────────────────────

    [Fact]
    public async Task ApiGoalReleaseAssign_NotifiesOnce()
    {
        var (factory, _, count) = CreateFactoryWithNotifier();
        using var client = factory.CreateClient();
        var goalId = UniqueId();
        var releaseId = UniqueId();

        // Create goal and release
        await client.PostAsync("/api/goals",
            JsonContent(new { id = goalId, description = "Test goal" }),
            TestContext.Current.CancellationToken);

        using (var scope = factory.Services.CreateScope())
        {
            var store = scope.ServiceProvider.GetRequiredService<IGoalStore>();
            await store.CreateReleaseAsync(new Release
            {
                Id = releaseId,
                Tag = "v1.0.0",
                RepositoryNames = [],
            }, TestContext.Current.CancellationToken);
        }
        count[0] = 0;

        var response = await client.PatchAsync(
            $"/api/goals/{goalId}/release",
            JsonContent(new { releaseId }),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(1, count[0]);
    }

    // ── API goal review status → 1 (criterion 37) ───────────────────────────

    [Fact]
    public async Task ApiGoalReviewStatus_NotifiesOnce()
    {
        var (factory, _, count) = CreateFactoryWithNotifier();
        using var client = factory.CreateClient();
        var id = UniqueId();

        await client.PostAsync("/api/goals",
            JsonContent(new { id, description = "Test goal" }),
            TestContext.Current.CancellationToken);
        count[0] = 0;

        var response = await client.PatchAsync(
            $"/api/goals/{id}/review-status",
            JsonContent(new { reviewStatus = "Approved" }),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(1, count[0]);
    }

    // ── API release create → 1 (criterion 38) ──────────────────────────────

    [Fact]
    public async Task ApiReleaseCreate_NotifiesOnce()
    {
        var (factory, _, count) = CreateFactoryWithNotifier();
        using var client = factory.CreateClient();
        var version = "v-" + UniqueId();

        var response = await client.PostAsync("/api/releases",
            JsonContent(new { version }),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal(1, count[0]);
    }

    // ── API release status (changed) → 1 (criterion 39) ─────────────────────

    [Fact]
    public async Task ApiReleaseStatus_Changed_NotifiesOnce()
    {
        // For Planning → Released we need a ReleaseExecutionService and valid setup.
        // Instead, test a status change that doesn't require execution: Planning → Planning
        // is a no-op. The only non-execution status change path is the generic
        // "existing.Status = newStatus" fallback, which only fires for Planning→Planning
        // (no-op, returns Ok without notify) — so we test Planning→Released which
        // requires the execution service. We'll use a mock repo manager.
        var fake = new ConfigurableFakeRepoManager { CreateTagResult = true };
        var baseFactory = new HiveTestFactory { MockRepoManager = fake };

        // Register config with a repo
        var config = new HiveConfigFile
        {
            Repositories =
            [
                new RepositoryConfig
                {
                    Name = "repo1",
                    Url = "https://github.com/test/repo1",
                    DefaultBranch = "main",
                    Release = new ReleaseRepoConfig { MergeTo = "main", TagBranch = "main" },
                },
            ],
        };

        var notifier = new DashboardNotifier();
        var counter = new int[1];
        notifier.OnStateChanged += () => Interlocked.Increment(ref counter[0]);

        var factory = baseFactory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureServices(services =>
            {
                // Replace DashboardNotifier
                var existingNotifier = services.SingleOrDefault(d => d.ServiceType == typeof(DashboardNotifier));
                if (existingNotifier is not null)
                    services.Remove(existingNotifier);
                services.AddSingleton(notifier);

                // Replace config
                var existingConfig = services.SingleOrDefault(d => d.ServiceType == typeof(HiveConfigFile));
                if (existingConfig is not null)
                    services.Remove(existingConfig);
                services.AddSingleton(config);

                // Register ReleaseExecutionService
                services.AddSingleton(sp => new ReleaseExecutionService(
                    sp.GetRequiredService<IGoalStore>(),
                    config,
                    sp.GetRequiredService<IBrainRepoManager>(),
                    sp.GetRequiredService<ILogger<ReleaseExecutionService>>()));
            });
        });

        using var client = factory.CreateClient();
        var releaseId = UniqueId();

        using (var scope = factory.Services.CreateScope())
        {
            var store = scope.ServiceProvider.GetRequiredService<IGoalStore>();
            await store.CreateReleaseAsync(new Release
            {
                Id = releaseId,
                Tag = "v1.0.0",
                RepositoryNames = ["repo1"],
            }, TestContext.Current.CancellationToken);
            await store.CreateGoalAsync(
                new Goal { Id = UniqueId(), Description = "Test", ReleaseId = releaseId, Status = GoalStatus.Completed },
                TestContext.Current.CancellationToken);
        }
        counter[0] = 0;

        var response = await client.PatchAsync(
            $"/api/releases/{releaseId}/status",
            JsonContent(new { status = "Released" }),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(1, counter[0]);
    }

    // ── API release status (no-op) → 0 (criterion 40) ────────────────────────

    [Fact]
    public async Task ApiReleaseStatus_NoOp_DoesNotNotify()
    {
        var (factory, _, count) = CreateFactoryWithNotifier();
        using var client = factory.CreateClient();
        var releaseId = UniqueId();

        using (var scope = factory.Services.CreateScope())
        {
            var store = scope.ServiceProvider.GetRequiredService<IGoalStore>();
            await store.CreateReleaseAsync(new Release
            {
                Id = releaseId,
                Tag = "v1.0.0",
                RepositoryNames = [],
            }, TestContext.Current.CancellationToken);
        }
        count[0] = 0;

        // Planning → Planning is a no-op (returns Ok without notification)
        var response = await client.PatchAsync(
            $"/api/releases/{releaseId}/status",
            JsonContent(new { status = "Planning" }),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(0, count[0]);
    }

    // ── API release notes → 1 (criterion 41) ───────────────────────────────

    [Fact]
    public async Task ApiReleaseNotes_NotifiesOnce()
    {
        var (factory, _, count) = CreateFactoryWithNotifier();
        using var client = factory.CreateClient();
        var releaseId = UniqueId();

        using (var scope = factory.Services.CreateScope())
        {
            var store = scope.ServiceProvider.GetRequiredService<IGoalStore>();
            await store.CreateReleaseAsync(new Release
            {
                Id = releaseId,
                Tag = "v1.0.0",
                RepositoryNames = [],
            }, TestContext.Current.CancellationToken);
        }
        count[0] = 0;

        var response = await client.PatchAsync(
            $"/api/releases/{releaseId}/notes",
            JsonContent(new { notes = "Updated notes" }),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(1, count[0]);
    }

    // ── API release tag → 1 (criterion 42) ─────────────────────────────────

    [Fact]
    public async Task ApiReleaseTag_NotifiesOnce()
    {
        var (factory, _, count) = CreateFactoryWithNotifier();
        using var client = factory.CreateClient();
        var releaseId = UniqueId();

        using (var scope = factory.Services.CreateScope())
        {
            var store = scope.ServiceProvider.GetRequiredService<IGoalStore>();
            await store.CreateReleaseAsync(new Release
            {
                Id = releaseId,
                Tag = "v1.0.0",
                RepositoryNames = [],
            }, TestContext.Current.CancellationToken);
        }
        count[0] = 0;

        var response = await client.PatchAsync(
            $"/api/releases/{releaseId}/tag",
            JsonContent(new { tag = "v2.0.0" }),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(1, count[0]);
    }

    // ── API release repositories → 1 (criterion 43) ─────────────────────────

    [Fact]
    public async Task ApiReleaseRepositories_NotifiesOnce()
    {
        var (factory, _, count) = CreateFactoryWithNotifier();
        using var client = factory.CreateClient();
        var releaseId = UniqueId();

        using (var scope = factory.Services.CreateScope())
        {
            var store = scope.ServiceProvider.GetRequiredService<IGoalStore>();
            await store.CreateReleaseAsync(new Release
            {
                Id = releaseId,
                Tag = "v1.0.0",
                RepositoryNames = [],
            }, TestContext.Current.CancellationToken);
        }
        count[0] = 0;

        var response = await client.PatchAsync(
            $"/api/releases/{releaseId}/repositories",
            JsonContent(new { repositories = new[] { "repo1", "repo2" } }),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(1, count[0]);
    }

    // ── API not-found → 0 (criterion 44) ────────────────────────────────────

    [Theory]
    [InlineData("GET",    "/api/goals/nonexistent-xyz")]
    [InlineData("DELETE", "/api/goals/nonexistent-xyz")]
    [InlineData("PATCH",  "/api/goals/nonexistent-xyz/status")]
    [InlineData("PATCH",  "/api/goals/nonexistent-xyz/review-status")]
    [InlineData("PATCH",  "/api/goals/nonexistent-xyz/release")]
    [InlineData("PATCH",  "/api/releases/nonexistent-xyz/notes")]
    [InlineData("PATCH",  "/api/releases/nonexistent-xyz/tag")]
    [InlineData("PATCH",  "/api/releases/nonexistent-xyz/repositories")]
    public async Task ApiNotFound_DoesNotNotify(string method, string path)
    {
        var (factory, _, count) = CreateFactoryWithNotifier();
        using var client = factory.CreateClient();
        count[0] = 0;

        HttpResponseMessage response;
        if (method == "GET")
            response = await client.GetAsync(path, TestContext.Current.CancellationToken);
        else if (method == "DELETE")
            response = await client.DeleteAsync(path, TestContext.Current.CancellationToken);
        else
            response = await client.PatchAsync(path,
                JsonContent(new { status = "Draft", reviewStatus = "Approved", releaseId = "x", notes = "n", tag = "v1", repositories = Array.Empty<string>() }),
                TestContext.Current.CancellationToken);

        // Should be 404 (not-found) for all these
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(0, count[0]);
    }

    // ── API release status not-found → 0 (criterion 44) ─────────────────────

    [Fact]
    public async Task ApiReleaseStatus_NotFound_DoesNotNotify()
    {
        var (factory, _, count) = CreateFactoryWithNotifier();
        using var client = factory.CreateClient();
        count[0] = 0;

        var response = await client.PatchAsync(
            "/api/releases/nonexistent-xyz/status",
            JsonContent(new { status = "Planning" }),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(0, count[0]);
    }
}