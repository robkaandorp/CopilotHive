using System.Net;
using System.Text;
using System.Text.Json;
using CopilotHive.Configuration;
using CopilotHive.Git;
using CopilotHive.Goals;
using CopilotHive.Persistence;
using CopilotHive.Services;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CopilotHive.Tests;

/// <summary>
/// Tests for <see cref="ReleaseExecutionService"/> validation, execution, and rollback behaviour.
/// </summary>
public sealed class ReleaseExecutionServiceTests : IDisposable
{
    private readonly CopilotHiveDbContext _dbContext;
    private readonly GoalStore _store;

    public ReleaseExecutionServiceTests()
    {
        _dbContext = CopilotHiveDbContext.CreateInMemory();
        _store = new GoalStore(_dbContext, NullLogger<GoalStore>.Instance);
    }

    public void Dispose() => _dbContext.Dispose();

    private static HiveConfigFile CreateConfig() => new()
    {
        Repositories =
        [
            new RepositoryConfig
            {
                Name = "repo1", Url = "https://github.com/test/repo1", DefaultBranch = "main",
                Release = new ReleaseRepoConfig { MergeTo = "main", TagBranch = "main" },
            },
            new RepositoryConfig
            {
                Name = "repo2", Url = "https://github.com/test/repo2", DefaultBranch = "develop",
                Release = new ReleaseRepoConfig { MergeTo = "develop", TagBranch = "develop" },
            },
        ],
    };

    private ReleaseExecutionService CreateService(HiveConfigFile config, IBrainRepoManager repoManager) =>
        new(_store, config, repoManager, NullLogger<ReleaseExecutionService>.Instance);

    // ── Validation ───────────────────────────────────────────────────────

    [Fact]
    public async Task ValidateReleaseAsync_IncompleteGoals_IsInvalid()
    {
        var ct = TestContext.Current.CancellationToken;
        var release = new Release { Id = "v1.0.0", Tag = "v1.0.0" };
        await _store.CreateReleaseAsync(release, ct);
        await _store.CreateGoalAsync(
            new Goal { Id = "goal-a", Description = "Test", ReleaseId = "v1.0.0", Status = GoalStatus.InProgress }, ct);

        var service = CreateService(CreateConfig(), new ConfigurableFakeRepoManager());
        var result = await service.ValidateReleaseAsync(release, ct);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("goal-a"));
    }

    [Fact]
    public async Task ValidateReleaseAsync_UnconfiguredRepo_IsInvalid()
    {
        var ct = TestContext.Current.CancellationToken;
        var release = new Release { Id = "v1.0.0", Tag = "v1.0.0", RepositoryNames = ["unknown-repo"] };
        await _store.CreateReleaseAsync(release, ct);
        await _store.CreateGoalAsync(
            new Goal { Id = "goal-a", Description = "Test", ReleaseId = "v1.0.0", Status = GoalStatus.Completed }, ct);

        var service = CreateService(CreateConfig(), new ConfigurableFakeRepoManager());
        var result = await service.ValidateReleaseAsync(release, ct);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("unknown-repo"));
    }

    [Fact]
    public async Task ValidateReleaseAsync_NoGoals_IsInvalid()
    {
        var ct = TestContext.Current.CancellationToken;
        var release = new Release { Id = "v1.0.0", Tag = "v1.0.0" };
        await _store.CreateReleaseAsync(release, ct);

        var service = CreateService(CreateConfig(), new ConfigurableFakeRepoManager());
        var result = await service.ValidateReleaseAsync(release, ct);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("no assigned goals"));
    }

    [Fact]
    public async Task ValidateReleaseAsync_CompletedGoalsEmptyRepos_IsValid()
    {
        var ct = TestContext.Current.CancellationToken;
        var release = new Release { Id = "v1.0.0", Tag = "v1.0.0" };
        await _store.CreateReleaseAsync(release, ct);
        await _store.CreateGoalAsync(
            new Goal { Id = "goal-a", Description = "Test", ReleaseId = "v1.0.0", Status = GoalStatus.Completed }, ct);

        var service = CreateService(CreateConfig(), new ConfigurableFakeRepoManager());
        var result = await service.ValidateReleaseAsync(release, ct);

        Assert.True(result.IsValid);
        Assert.Empty(result.Errors);
    }

    // ── Execution ────────────────────────────────────────────────────────

    [Fact]
    public async Task ExecuteReleaseAsync_SetsExecutingBeforeGitOps()
    {
        var ct = TestContext.Current.CancellationToken;
        var release = new Release { Id = "v1.0.0", Tag = "v1.0.0", RepositoryNames = ["repo1"] };
        await _store.CreateReleaseAsync(release, ct);
        await _store.CreateGoalAsync(
            new Goal { Id = "goal-a", Description = "Test", ReleaseId = "v1.0.0", Status = GoalStatus.Completed }, ct);

        // Capture the release's persisted ExecutionState AT THE MOMENT the merge runs by re-reading
        // from the store inside the merge callback. This proves the Executing write happens BEFORE
        // git operations — if that write were removed or moved after git, the observed state would
        // be None/Completed instead of Executing and the assertion below would fail.
        ReleaseExecutionState? stateDuringMerge = null;
        var fake = new ConfigurableFakeRepoManager
        {
            MergeCallback = (_, _, _) =>
            {
                stateDuringMerge = _store.GetReleaseAsync("v1.0.0", CancellationToken.None)
                    .GetAwaiter().GetResult()!.ExecutionState;
                return null;
            },
        };
        var service = CreateService(CreateConfig(), fake);

        await service.ExecuteReleaseAsync(release, ct);

        Assert.True(fake.MergeCalled);
        Assert.Equal(ReleaseExecutionState.Executing, stateDuringMerge);

        // And the final state is Completed since the merge succeeded.
        var stored = await _store.GetReleaseAsync("v1.0.0", ct);
        Assert.Equal(ReleaseExecutionState.Completed, stored!.ExecutionState);
    }

    [Fact]
    public async Task ExecuteReleaseAsync_AlreadyReleased_ReturnsFailure()
    {
        var ct = TestContext.Current.CancellationToken;
        var release = new Release
        {
            Id = "v1.0.0", Tag = "v1.0.0", RepositoryNames = ["repo1"], Status = ReleaseStatus.Released,
        };
        await _store.CreateReleaseAsync(release, ct);

        var service = CreateService(CreateConfig(), new ConfigurableFakeRepoManager());
        var result = await service.ExecuteReleaseAsync(release, ct);

        Assert.False(result.Success);
        Assert.Equal(ReleaseExecutionFailure.AlreadyReleased, result.Failure);
    }

    [Fact]
    public async Task ExecuteReleaseAsync_AlreadyExecuting_ReturnsFailure()
    {
        var ct = TestContext.Current.CancellationToken;
        var release = new Release
        {
            Id = "v1.0.0", Tag = "v1.0.0", RepositoryNames = ["repo1"],
            ExecutionState = ReleaseExecutionState.Executing,
        };
        await _store.CreateReleaseAsync(release, ct);

        var service = CreateService(CreateConfig(), new ConfigurableFakeRepoManager());
        var result = await service.ExecuteReleaseAsync(release, ct);

        Assert.False(result.Success);
        Assert.Equal(ReleaseExecutionFailure.AlreadyExecuting, result.Failure);
    }

    [Fact]
    public async Task ExecuteReleaseAsync_UsesCurrentTagForCreateTag()
    {
        var ct = TestContext.Current.CancellationToken;
        // Persist a release whose stored tag differs from the in-memory input parameter's tag.
        var stored = new Release { Id = "v1.0.0", Tag = "stored-tag", RepositoryNames = ["repo1"] };
        await _store.CreateReleaseAsync(stored, ct);
        await _store.CreateGoalAsync(
            new Goal { Id = "goal-a", Description = "Test", ReleaseId = "v1.0.0", Status = GoalStatus.Completed }, ct);

        var fake = new ConfigurableFakeRepoManager { CreateTagResult = true };
        var service = CreateService(CreateConfig(), fake);

        // Pass an input release with a stale tag; execution must re-read and use the stored tag.
        var input = new Release { Id = "v1.0.0", Tag = "stale-input-tag", RepositoryNames = ["repo1"] };
        var result = await service.ExecuteReleaseAsync(input, ct);

        Assert.True(result.Success);
        Assert.Contains(fake.CreateTagCalls, c => c.Tag == "stored-tag");
        Assert.DoesNotContain(fake.CreateTagCalls, c => c.Tag == "stale-input-tag");
    }

    [Fact]
    public async Task ExecuteReleaseAsync_CreateTagFalse_TreatedAsSuccessNotRolledBack()
    {
        var ct = TestContext.Current.CancellationToken;
        // Two repos: repo1's CreateTagAsync returns false (tag already existed → NOT a new tag,
        // must NOT be tracked for rollback), and repo2's merge fails to trigger a rollback pass.
        // If false-returning tags were incorrectly pushed to the rollback stack, DeleteTagAsync
        // would be called for repo1 and the assertion below would fail.
        var release = new Release { Id = "v1.0.0", Tag = "v1.0.0", RepositoryNames = ["repo1", "repo2"] };
        await _store.CreateReleaseAsync(release, ct);
        await _store.CreateGoalAsync(
            new Goal { Id = "goal-a", Description = "Test", ReleaseId = "v1.0.0", Status = GoalStatus.Completed }, ct);

        var fake = new ConfigurableFakeRepoManager
        {
            // repo1 → CreateTagAsync returns false; repo2 never reaches CreateTag (merge fails first).
            CreateTagCallback = repo => repo != "repo1",
            MergeCallback = (repo, _, _) =>
            {
                if (repo == "repo2")
                    throw new InvalidOperationException("merge failed on repo2");
                return null;
            },
        };
        var service = CreateService(CreateConfig(), fake);

        var result = await service.ExecuteReleaseAsync(release, ct);

        // The overall release fails because of repo2, and a rollback pass runs.
        Assert.False(result.Success);
        Assert.Equal(ReleaseExecutionFailure.Execution, result.Failure);
        // repo1's false-returning tag must NOT have been rolled back — zero DeleteTagAsync calls.
        Assert.Empty(fake.DeleteTagCalls);
    }

    [Fact]
    public async Task ExecuteReleaseAsync_CreateTagFalse_SingleRepo_Succeeds()
    {
        var ct = TestContext.Current.CancellationToken;
        var release = new Release { Id = "v1.0.0", Tag = "v1.0.0", RepositoryNames = ["repo1"] };
        await _store.CreateReleaseAsync(release, ct);
        await _store.CreateGoalAsync(
            new Goal { Id = "goal-a", Description = "Test", ReleaseId = "v1.0.0", Status = GoalStatus.Completed }, ct);

        var fake = new ConfigurableFakeRepoManager { CreateTagResult = false };
        var service = CreateService(CreateConfig(), fake);

        var result = await service.ExecuteReleaseAsync(release, ct);

        Assert.True(result.Success);
        Assert.Empty(fake.DeleteTagCalls);
    }

    [Fact]
    public async Task ExecuteReleaseAsync_FailureRollsBackCreatedTags()
    {
        var ct = TestContext.Current.CancellationToken;
        var release = new Release { Id = "v1.0.0", Tag = "v1.0.0", RepositoryNames = ["repo1", "repo2"] };
        await _store.CreateReleaseAsync(release, ct);
        await _store.CreateGoalAsync(
            new Goal { Id = "goal-a", Description = "Test", ReleaseId = "v1.0.0", Status = GoalStatus.Completed }, ct);

        var fake = new ConfigurableFakeRepoManager
        {
            CreateTagResult = true,
            // Fail the merge for the second repo, after the first repo's tag was created.
            MergeCallback = (repo, _, _) =>
            {
                if (repo == "repo2")
                    throw new InvalidOperationException("merge failed on repo2");
                return null;
            },
        };
        var service = CreateService(CreateConfig(), fake);

        var result = await service.ExecuteReleaseAsync(release, ct);

        Assert.False(result.Success);
        Assert.Equal(ReleaseExecutionFailure.Execution, result.Failure);
        Assert.Contains(fake.DeleteTagCalls, c => c is { Repo: "repo1", Tag: "v1.0.0" });
    }

    [Fact]
    public async Task ExecuteReleaseAsync_RollbackDoesNotRevertMerges()
    {
        var ct = TestContext.Current.CancellationToken;
        var release = new Release { Id = "v1.0.0", Tag = "v1.0.0", RepositoryNames = ["repo1", "repo2"] };
        await _store.CreateReleaseAsync(release, ct);
        await _store.CreateGoalAsync(
            new Goal { Id = "goal-a", Description = "Test", ReleaseId = "v1.0.0", Status = GoalStatus.Completed }, ct);

        var fake = new ConfigurableFakeRepoManager
        {
            CreateTagResult = true,
            MergeCallback = (repo, _, _) =>
            {
                if (repo == "repo2")
                    throw new InvalidOperationException("merge failed on repo2");
                return null;
            },
        };
        var service = CreateService(CreateConfig(), fake);

        await service.ExecuteReleaseAsync(release, ct);

        // The first repo was merged; there is no "unmerge" operation — only tag deletion happens.
        Assert.Contains(fake.MergeCalls, c => c.Repo == "repo1");
        Assert.All(fake.DeleteTagCalls, c => Assert.Equal("v1.0.0", c.Tag));
    }

    [Fact]
    public async Task ExecuteReleaseAsync_SetsCompletedOnSuccess()
    {
        var ct = TestContext.Current.CancellationToken;
        var release = new Release { Id = "v1.0.0", Tag = "v1.0.0", RepositoryNames = ["repo1"] };
        await _store.CreateReleaseAsync(release, ct);
        await _store.CreateGoalAsync(
            new Goal { Id = "goal-a", Description = "Test", ReleaseId = "v1.0.0", Status = GoalStatus.Completed }, ct);

        var service = CreateService(CreateConfig(), new ConfigurableFakeRepoManager { CreateTagResult = true });
        var result = await service.ExecuteReleaseAsync(release, ct);

        Assert.True(result.Success);
        var stored = await _store.GetReleaseAsync("v1.0.0", ct);
        Assert.Equal(ReleaseExecutionState.Completed, stored!.ExecutionState);
    }

    [Fact]
    public async Task ExecuteReleaseAsync_SetsFailedOnFailure()
    {
        var ct = TestContext.Current.CancellationToken;
        var release = new Release { Id = "v1.0.0", Tag = "v1.0.0", RepositoryNames = ["repo1"] };
        await _store.CreateReleaseAsync(release, ct);
        await _store.CreateGoalAsync(
            new Goal { Id = "goal-a", Description = "Test", ReleaseId = "v1.0.0", Status = GoalStatus.Completed }, ct);

        var fake = new ConfigurableFakeRepoManager
        {
            MergeCallback = (_, _, _) => throw new InvalidOperationException("boom"),
        };
        var service = CreateService(CreateConfig(), fake);

        await service.ExecuteReleaseAsync(release, ct);

        var stored = await _store.GetReleaseAsync("v1.0.0", ct);
        Assert.Equal(ReleaseExecutionState.Failed, stored!.ExecutionState);
    }

    [Fact]
    public async Task ExecuteReleaseAsync_SkipsReposWithNoReleaseConfig()
    {
        var ct = TestContext.Current.CancellationToken;
        // repo3 exists in the config but has Release = null.
        var config = new HiveConfigFile
        {
            Repositories =
            [
                new RepositoryConfig
                {
                    Name = "repo3", Url = "https://github.com/test/repo3", DefaultBranch = "main",
                    Release = null, // no release config → should be skipped
                },
            ],
        };
        var release = new Release { Id = "v1.0.0", Tag = "v1.0.0", RepositoryNames = ["repo3"] };
        await _store.CreateReleaseAsync(release, ct);
        await _store.CreateGoalAsync(
            new Goal { Id = "goal-a", Description = "Test", ReleaseId = "v1.0.0", Status = GoalStatus.Completed }, ct);

        var fake = new ConfigurableFakeRepoManager { CreateTagResult = true };
        var service = CreateService(config, fake);

        var result = await service.ExecuteReleaseAsync(release, ct);

        // Execution succeeds overall (the repo is skipped, not a failure).
        Assert.True(result.Success);
        // The result contains a RepoReleaseResult with Skipped = true for repo3.
        var skippedRepo = Assert.Single(result.Results, r => r.RepoName == "repo3");
        Assert.True(skippedRepo.Skipped);
        // No tag or merge operations were performed for the skipped repo.
        Assert.DoesNotContain(fake.CreateTagCalls, c => c.Repo == "repo3");
        Assert.DoesNotContain(fake.MergeCalls, c => c.Repo == "repo3");
    }

    // ── Cancellation fix: Failed state persisted even with a cancelled token ──

    [Fact]
    public async Task ExecuteReleaseAsync_CancelledToken_StillPersistsFailed()
    {
        // This test verifies the CancellationToken.None fix: when the merge operation
        // throws OperationCanceledException (simulating a cancelled git operation), the
        // catch block must persist ExecutionState = Failed using CancellationToken.None
        // — NOT the cancelled caller token. If the service used the cancelled token for
        // the Failed write, UpdateReleaseAsync would throw OperationCanceledException and
        // the release would be stuck at Executing instead of Failed.
        var ct = TestContext.Current.CancellationToken;
        var release = new Release { Id = "v1.0.0", Tag = "v1.0.0", RepositoryNames = ["repo1"] };
        await _store.CreateReleaseAsync(release, ct);
        await _store.CreateGoalAsync(
            new Goal { Id = "goal-a", Description = "Test", ReleaseId = "v1.0.0", Status = GoalStatus.Completed }, ct);

        // Use a CTS that the merge callback cancels before throwing OCE.
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var fake = new ConfigurableFakeRepoManager
        {
            MergeCallback = (_, _, _) =>
            {
                cts.Cancel();
                throw new OperationCanceledException(cts.Token);
            },
        };
        var service = CreateService(CreateConfig(), fake);

        // ExecuteReleaseAsync should catch the OCE and persist Failed with CancellationToken.None.
        // It may or may not re-throw depending on implementation; we only care about the store state.
        try
        {
            await service.ExecuteReleaseAsync(release, cts.Token);
        }
        catch (OperationCanceledException)
        {
            // The service may propagate OCE after persisting Failed; that's acceptable.
            // The important thing is that the store reflects Failed, not Executing.
        }

        var stored = await _store.GetReleaseAsync("v1.0.0", CancellationToken.None);
        Assert.NotNull(stored);
        Assert.Equal(ReleaseExecutionState.Failed, stored!.ExecutionState);
    }

    [Fact]
    public async Task ExecuteReleaseAsync_CancelledTokenOnValidation_StillPersistsFailed()
    {
        // This test verifies the CancellationToken.None fix on the VALIDATION failure path:
        // when the caller's token is cancelled but validation still fails (incomplete goals),
        // the service must persist ExecutionState = Failed using CancellationToken.None.
        var ct = TestContext.Current.CancellationToken;
        var release = new Release { Id = "v1.0.0", Tag = "v1.0.0", RepositoryNames = ["repo1"] };
        await _store.CreateReleaseAsync(release, ct);
        // Goal is InProgress → validation will fail.
        await _store.CreateGoalAsync(
            new Goal { Id = "goal-a", Description = "Test", ReleaseId = "v1.0.0", Status = GoalStatus.InProgress }, ct);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        // Cancel the token so that GetGoalsByReleaseAsync (called with ct) may observe cancellation.
        // But the validation path may still run (in-memory store is synchronous). The key is that
        // the Failed write uses CancellationToken.None.
        cts.Cancel();

        var fake = new ConfigurableFakeRepoManager();
        var service = CreateService(CreateConfig(), fake);

        try
        {
            await service.ExecuteReleaseAsync(release, cts.Token);
        }
        catch (OperationCanceledException)
        {
            // If the store's GetGoalsByReleaseAsync observes the cancelled token and throws,
            // the service may propagate OCE. But the ExecutionState=Executing write happened
            // before that, and if validation ran, the Failed write used CancellationToken.None.
        }

        // If the token was cancelled before WaitAsync, the release stays at None — that's
        // expected behavior (the lock couldn't be acquired). In that case this test just
        // verifies no exception escaped. But if the lock WAS acquired (in-memory, synchronous),
        // the Failed write must have persisted.
        var stored = await _store.GetReleaseAsync("v1.0.0", CancellationToken.None);
        Assert.NotNull(stored);
        // The state should be either Failed (if execution started) or None (if WaitAsync threw).
        // It must NOT be stuck at Executing.
        Assert.True(
            stored!.ExecutionState == ReleaseExecutionState.Failed ||
            stored.ExecutionState == ReleaseExecutionState.None,
            $"Expected Failed or None, but got {stored.ExecutionState} (stuck at Executing would be a bug)");
    }
}

/// <summary>
/// Configurable fake <see cref="IBrainRepoManager"/> that lets tests control merge/tag behaviour
/// and records the calls made to merge, create-tag, and delete-tag.
/// </summary>
internal sealed class ConfigurableFakeRepoManager : IBrainRepoManager
{
    public bool CreateTagResult { get; set; } = true;
    public Func<string, string, string, string?>? MergeCallback { get; set; }
    public Func<string, bool>? CreateTagCallback { get; set; }

    public bool MergeCalled { get; private set; }
    public List<(string Repo, string Source, string Target)> MergeCalls { get; } = [];
    public List<(string Repo, string Tag, string Branch)> CreateTagCalls { get; } = [];
    public List<(string Repo, string Tag)> DeleteTagCalls { get; } = [];

    public string WorkDirectory => "/fake/work";

    public Task<string> EnsureCloneAsync(string repoName, string repoUrl, string defaultBranch, CancellationToken ct = default) =>
        Task.FromResult($"/fake/work/{repoName}");

    public Task<string> MergeFeatureBranchAsync(string repoName, string featureBranch, string defaultBranch, string commitMessage, CancellationToken ct = default) =>
        Task.FromResult("fake-sha");

    public Task<BranchDeleteResult> DeleteRemoteBranchAsync(string repoName, string branchName, CancellationToken ct = default) =>
        Task.FromResult(BranchDeleteResult.Success);

    public string GetClonePath(string repoName) => $"/fake/work/{repoName}";

    public Task<string?> GetHeadShaAsync(string repoName, CancellationToken ct = default) =>
        Task.FromResult<string?>(null);

    public Task<string?> MergeBranchAsync(string repoName, string sourceBranch, string targetBranch, CancellationToken ct = default)
    {
        MergeCalled = true;
        MergeCalls.Add((repoName, sourceBranch, targetBranch));
        var sha = MergeCallback?.Invoke(repoName, sourceBranch, targetBranch);
        return Task.FromResult(sha);
    }

    public Task<bool> CreateTagAsync(string repoName, string tag, string branch, string message, CancellationToken ct = default)
    {
        CreateTagCalls.Add((repoName, tag, branch));
        return Task.FromResult(CreateTagCallback?.Invoke(repoName) ?? CreateTagResult);
    }

    public Task<bool> DeleteTagAsync(string repoName, string tag, CancellationToken ct = default)
    {
        DeleteTagCalls.Add((repoName, tag));
        return Task.FromResult(true);
    }
}

/// <summary>
/// Integration tests for the PATCH /api/releases/{id}/status endpoint, verifying HTTP status
/// codes for release execution transitions. Uses <see cref="HiveTestFactory"/> with
/// <c>WithWebHostBuilder</c> to register a <see cref="ReleaseExecutionService"/> and
/// <see cref="HiveConfigFile"/> with a mock <see cref="IBrainRepoManager"/>.
/// </summary>
[Collection("HiveIntegration")]
public sealed class ReleaseStatusApiEndpointTests
{
    private static readonly JsonSerializerOptions JsonOpts =
        new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private static string UniqueId() => "test-" + Guid.NewGuid().ToString("N")[..16];

    private static HiveConfigFile CreateApiConfig() => new()
    {
        Repositories =
        [
            new RepositoryConfig
            {
                Name = "repo1", Url = "https://github.com/test/repo1", DefaultBranch = "main",
                Release = new ReleaseRepoConfig { MergeTo = "main", TagBranch = "main" },
            },
        ],
    };

    /// <summary>
    /// Creates a test factory that has <see cref="ReleaseExecutionService"/>,
    /// <see cref="HiveConfigFile"/>, and a configurable fake
    /// <see cref="IBrainRepoManager"/> registered.
    /// </summary>
    private static WebApplicationFactory<Program> CreateFactory(ConfigurableFakeRepoManager fake)
    {
        var config = CreateApiConfig();
        var baseFactory = new HiveTestFactory { MockRepoManager = fake };
        return baseFactory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureServices(services =>
            {
                // Remove any existing HiveConfigFile (unlikely but safe).
                var existingConfig = services.SingleOrDefault(d => d.ServiceType == typeof(HiveConfigFile));
                if (existingConfig is not null)
                    services.Remove(existingConfig);
                services.AddSingleton(config);

                // Register ReleaseExecutionService using the same config and the mock repo manager.
                services.AddSingleton(sp => new ReleaseExecutionService(
                    sp.GetRequiredService<IGoalStore>(),
                    config,
                    sp.GetRequiredService<IBrainRepoManager>(),
                    sp.GetRequiredService<ILogger<ReleaseExecutionService>>()));
            });
        });
    }

    private static StringContent StatusJson(string status) =>
        new(JsonSerializer.Serialize(new { status }, JsonOpts), Encoding.UTF8, "application/json");

    // ── API test 15: Planning → Released returns 200 ─────────────────────

    [Fact]
    public async Task PatchReleaseStatus_PlanningToReleased_Returns200()
    {
        var ct = TestContext.Current.CancellationToken;
        var fake = new ConfigurableFakeRepoManager { CreateTagResult = true };
        using var factory = CreateFactory(fake);
        using var client = factory.CreateClient();

        var releaseId = UniqueId();
        // Seed release and a completed goal directly through the store.
        using (var scope = factory.Services.CreateScope())
        {
            var store = scope.ServiceProvider.GetRequiredService<IGoalStore>();
            await store.CreateReleaseAsync(new Release
            {
                Id = releaseId, Tag = "v1.0.0", RepositoryNames = ["repo1"],
            }, ct);
            await store.CreateGoalAsync(
                new Goal { Id = UniqueId(), Description = "Test", ReleaseId = releaseId, Status = GoalStatus.Completed }, ct);
        }

        var response = await client.PatchAsync(
            $"/api/releases/{releaseId}/status", StatusJson("Released"), ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        // The response body reports the release as Released.
        var body = await response.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(body);
        Assert.Equal(ReleaseStatus.Released, ReadStatus(doc.RootElement));

        // The release is now Released with ExecutionState = Completed in the store (persisted).
        using (var scope = factory.Services.CreateScope())
        {
            var store = scope.ServiceProvider.GetRequiredService<IGoalStore>();
            var stored = await store.GetReleaseAsync(releaseId, ct);
            Assert.NotNull(stored);
            Assert.Equal(ReleaseStatus.Released, stored!.Status);
            Assert.Equal(ReleaseExecutionState.Completed, stored.ExecutionState);
        }

        // The fake repo manager received the expected merge and tag calls.
        // repo1 is configured with DefaultBranch=main, MergeTo=main, TagBranch=main.
        Assert.Contains(fake.MergeCalls, c => c is { Repo: "repo1", Source: "main", Target: "main" });
        Assert.Contains(fake.CreateTagCalls, c => c is { Repo: "repo1", Tag: "v1.0.0", Branch: "main" });
    }

    /// <summary>
    /// Reads the <c>status</c> property from a release JSON element, tolerating both numeric
    /// (default enum serialization) and string representations.
    /// </summary>
    private static ReleaseStatus ReadStatus(JsonElement release)
    {
        var status = release.GetProperty("status");
        return status.ValueKind == JsonValueKind.Number
            ? (ReleaseStatus)status.GetInt32()
            : Enum.Parse<ReleaseStatus>(status.GetString()!, ignoreCase: true);
    }

    // ── API test: validation failure returns 400 ─────────────────────────

    [Fact]
    public async Task PatchReleaseStatus_ValidationFailure_Returns400()
    {
        var ct = TestContext.Current.CancellationToken;
        var fake = new ConfigurableFakeRepoManager { CreateTagResult = true };
        using var factory = CreateFactory(fake);
        using var client = factory.CreateClient();

        var releaseId = UniqueId();
        // Seed a release with repos configured but an INCOMPLETE goal → validation fails.
        using (var scope = factory.Services.CreateScope())
        {
            var store = scope.ServiceProvider.GetRequiredService<IGoalStore>();
            await store.CreateReleaseAsync(new Release
            {
                Id = releaseId, Tag = "v1.0.0", RepositoryNames = ["repo1"],
            }, ct);
            await store.CreateGoalAsync(
                new Goal { Id = UniqueId(), Description = "Test", ReleaseId = releaseId, Status = GoalStatus.InProgress }, ct);
        }

        var response = await client.PatchAsync(
            $"/api/releases/{releaseId}/status", StatusJson("Released"), ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        // No git operations should have run for a validation failure.
        Assert.Empty(fake.MergeCalls);
    }

    // ── API test: execution failure returns 500 ──────────────────────────

    [Fact]
    public async Task PatchReleaseStatus_ExecutionFailure_Returns500()
    {
        var ct = TestContext.Current.CancellationToken;
        var fake = new ConfigurableFakeRepoManager
        {
            MergeCallback = (_, _, _) => throw new InvalidOperationException("merge blew up"),
        };
        using var factory = CreateFactory(fake);
        using var client = factory.CreateClient();

        var releaseId = UniqueId();
        // Seed a release with repos configured and a completed goal → validation passes,
        // but the merge throws → execution failure → 500.
        using (var scope = factory.Services.CreateScope())
        {
            var store = scope.ServiceProvider.GetRequiredService<IGoalStore>();
            await store.CreateReleaseAsync(new Release
            {
                Id = releaseId, Tag = "v1.0.0", RepositoryNames = ["repo1"],
            }, ct);
            await store.CreateGoalAsync(
                new Goal { Id = UniqueId(), Description = "Test", ReleaseId = releaseId, Status = GoalStatus.Completed }, ct);
        }

        var response = await client.PatchAsync(
            $"/api/releases/{releaseId}/status", StatusJson("Released"), ct);

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
    }

    // ── API test: comma-combined status rejected with 400 ────────────────

    [Fact]
    public async Task PatchReleaseStatus_CommaCombined_Returns400()
    {
        var ct = TestContext.Current.CancellationToken;
        var fake = new ConfigurableFakeRepoManager { CreateTagResult = true };
        using var factory = CreateFactory(fake);
        using var client = factory.CreateClient();

        var releaseId = UniqueId();
        using (var scope = factory.Services.CreateScope())
        {
            var store = scope.ServiceProvider.GetRequiredService<IGoalStore>();
            await store.CreateReleaseAsync(new Release { Id = releaseId, Tag = "v1.0.0" }, ct);
        }

        var response = await client.PatchAsync(
            $"/api/releases/{releaseId}/status", StatusJson("Released,Planning"), ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        // A comma-combined value must never trigger release git operations.
        Assert.Empty(fake.MergeCalls);
    }

    // ── API test: service not registered returns 503 ─────────────────────

    [Fact]
    public async Task PatchReleaseStatus_ServiceNull_Returns503()
    {
        var ct = TestContext.Current.CancellationToken;
        // A plain factory (booted without --config-repo) does NOT register ReleaseExecutionService,
        // so services.GetService<ReleaseExecutionService>() returns null → the endpoint returns 503.
        using var factory = new HiveTestFactory();
        using var client = factory.CreateClient();

        var releaseId = UniqueId();
        using (var scope = factory.Services.CreateScope())
        {
            var store = scope.ServiceProvider.GetRequiredService<IGoalStore>();
            await store.CreateReleaseAsync(new Release
            {
                Id = releaseId, Tag = "v1.0.0", RepositoryNames = ["repo1"],
            }, ct);
            await store.CreateGoalAsync(
                new Goal { Id = UniqueId(), Description = "Test", ReleaseId = releaseId, Status = GoalStatus.Completed }, ct);
        }

        var response = await client.PatchAsync(
            $"/api/releases/{releaseId}/status", StatusJson("Released"), ct);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
    }

    // ── API test 16: numeric status rejected with 400 ────────────────────

    [Theory]
    [InlineData("0")]
    [InlineData("1")]
    public async Task PatchReleaseStatus_NumericValue_Returns400(string numericStatus)
    {
        var ct = TestContext.Current.CancellationToken;
        var fake = new ConfigurableFakeRepoManager { CreateTagResult = true };
        using var factory = CreateFactory(fake);
        using var client = factory.CreateClient();

        var releaseId = UniqueId();
        using (var scope = factory.Services.CreateScope())
        {
            var store = scope.ServiceProvider.GetRequiredService<IGoalStore>();
            await store.CreateReleaseAsync(new Release { Id = releaseId, Tag = "v1.0.0" }, ct);
        }

        var response = await client.PatchAsync(
            $"/api/releases/{releaseId}/status", StatusJson(numericStatus), ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // ── API test 17: Released → Planning returns 409 ─────────────────────

    [Fact]
    public async Task PatchReleaseStatus_ReleasedToPlanning_Returns409()
    {
        var ct = TestContext.Current.CancellationToken;
        var fake = new ConfigurableFakeRepoManager { CreateTagResult = true };
        using var factory = CreateFactory(fake);
        using var client = factory.CreateClient();

        var releaseId = UniqueId();
        using (var scope = factory.Services.CreateScope())
        {
            var store = scope.ServiceProvider.GetRequiredService<IGoalStore>();
            await store.CreateReleaseAsync(new Release
            {
                Id = releaseId, Tag = "v1.0.0", Status = ReleaseStatus.Released,
            }, ct);
        }

        var response = await client.PatchAsync(
            $"/api/releases/{releaseId}/status", StatusJson("Planning"), ct);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    // ── API test 18: already-Released → Released returns 409 ──────────────

    [Fact]
    public async Task PatchReleaseStatus_ReleasedToReleased_Returns409()
    {
        var ct = TestContext.Current.CancellationToken;
        var fake = new ConfigurableFakeRepoManager { CreateTagResult = true };
        using var factory = CreateFactory(fake);
        using var client = factory.CreateClient();

        var releaseId = UniqueId();
        using (var scope = factory.Services.CreateScope())
        {
            var store = scope.ServiceProvider.GetRequiredService<IGoalStore>();
            await store.CreateReleaseAsync(new Release
            {
                Id = releaseId, Tag = "v1.0.0", Status = ReleaseStatus.Released,
            }, ct);
        }

        var response = await client.PatchAsync(
            $"/api/releases/{releaseId}/status", StatusJson("Released"), ct);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }
}
