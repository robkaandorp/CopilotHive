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

    // ── Fresh state: RepositoryNames re-read from store, not input parameter ──

    [Fact]
    public async Task ExecuteReleaseAsync_UsesCurrentRepositoryNamesFromStore()
    {
        var ct = TestContext.Current.CancellationToken;
        // Persist a release with repo1, then change the store's RepositoryNames to repo2.
        var stored = new Release { Id = "v1.0.0", Tag = "v1.0.0", RepositoryNames = ["repo1"] };
        await _store.CreateReleaseAsync(stored, ct);
        await _store.CreateGoalAsync(
            new Goal { Id = "goal-a", Description = "Test", ReleaseId = "v1.0.0", Status = GoalStatus.Completed }, ct);

        // Update the store so the release now targets repo2 instead of repo1.
        await _store.UpdateReleaseAsync("v1.0.0", new ReleaseUpdateData { Repositories = ["repo2"] }, ct);

        var fake = new ConfigurableFakeRepoManager { CreateTagResult = true };
        var service = CreateService(CreateConfig(), fake);

        // Pass the ORIGINAL release object (still has ["repo1"]); execution must re-read the store
        // and operate on repo2, NOT the stale input parameter's repo1.
        var result = await service.ExecuteReleaseAsync(stored, ct);

        Assert.True(result.Success);
        Assert.Contains(fake.MergeCalls, c => c.Repo == "repo2");
        Assert.DoesNotContain(fake.MergeCalls, c => c.Repo == "repo1");
    }

    // ── Rollback uses an independently bounded 30s token ──

    [Fact]
    public async Task ExecuteReleaseAsync_RollbackUsesBoundedToken()
    {
        var ct = TestContext.Current.CancellationToken;
        var release = new Release { Id = "v1.0.0", Tag = "v1.0.0", RepositoryNames = ["repo1", "repo2"] };
        await _store.CreateReleaseAsync(release, ct);
        await _store.CreateGoalAsync(
            new Goal { Id = "goal-a", Description = "Test", ReleaseId = "v1.0.0", Status = GoalStatus.Completed }, ct);

        var fake = new ConfigurableFakeRepoManager
        {
            CreateTagResult = true,
            // repo1 succeeds (tag created → tracked for rollback); repo2's merge fails.
            MergeCallback = (repo, _, _) =>
            {
                if (repo == "repo2")
                    throw new InvalidOperationException("merge failed on repo2");
                return null;
            },
        };
        var service = CreateService(CreateConfig(), fake);

        // Use a token we control so we can prove the rollback does NOT reuse it.
        using var callerCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var result = await service.ExecuteReleaseAsync(release, callerCts.Token);

        Assert.False(result.Success);
        Assert.Equal(ReleaseExecutionFailure.Execution, result.Failure);

        // Rollback deleted repo1's tag.
        Assert.Contains(fake.DeleteTagCalls, c => c is { Repo: "repo1", Tag: "v1.0.0" });

        // The token handed to DeleteTagAsync is the rollback's own bounded 30s token:
        // it is cancellable and is NOT the caller's token.
        Assert.NotEmpty(fake.DeleteTagTokens);
        var rollbackToken = fake.DeleteTagTokens[0];
        Assert.True(rollbackToken.CanBeCanceled);
        Assert.NotEqual(callerCts.Token, rollbackToken);
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
    public List<CancellationToken> DeleteTagTokens { get; } = [];

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
        DeleteTagTokens.Add(ct);
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
        Assert.Equal(ReleaseStatus.Released, ReadStatus(doc.RootElement.GetProperty("release")));

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

    // ── API test: empty repos with completed goal is a valid no-op → 200 ─────

    [Fact]
    public async Task PatchReleaseStatus_EmptyReposWithCompletedGoal_Returns200()
    {
        var ct = TestContext.Current.CancellationToken;
        var fake = new ConfigurableFakeRepoManager { CreateTagResult = true };
        using var factory = CreateFactory(fake);
        using var client = factory.CreateClient();

        var releaseId = UniqueId();
        // Seed a release with a completed goal and NO repositories → validation passes and the
        // per-repo loop is a no-op, so execution succeeds without any git calls.
        using (var scope = factory.Services.CreateScope())
        {
            var store = scope.ServiceProvider.GetRequiredService<IGoalStore>();
            await store.CreateReleaseAsync(new Release
            {
                Id = releaseId, Tag = "v1.0.0", RepositoryNames = [],
            }, ct);
            await store.CreateGoalAsync(
                new Goal { Id = UniqueId(), Description = "Test", ReleaseId = releaseId, Status = GoalStatus.Completed }, ct);
        }

        var response = await client.PatchAsync(
            $"/api/releases/{releaseId}/status", StatusJson("Released"), ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        // No git operations were performed for an empty repository list.
        Assert.Empty(fake.MergeCalls);
        Assert.Empty(fake.CreateTagCalls);

        // The release is Released with ExecutionState = Completed in the store.
        using (var scope = factory.Services.CreateScope())
        {
            var store = scope.ServiceProvider.GetRequiredService<IGoalStore>();
            var stored = await store.GetReleaseAsync(releaseId, ct);
            Assert.NotNull(stored);
            Assert.Equal(ReleaseStatus.Released, stored!.Status);
            Assert.Equal(ReleaseExecutionState.Completed, stored.ExecutionState);
        }
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

        // The 500 body must carry the execution failure payload — a non-empty error mentioning the
        // failing repo, plus the per-repo results. This ensures an unrelated/unhandled 500 (which
        // would not have these fields) cannot satisfy the test.
        var body = await response.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(body);
        var error = doc.RootElement.GetProperty("error").GetString();
        Assert.False(string.IsNullOrWhiteSpace(error));
        Assert.Contains("repo1", error);

        Assert.True(doc.RootElement.TryGetProperty("results", out var results));
        Assert.Equal(JsonValueKind.Array, results.ValueKind);
        Assert.NotEqual(0, results.GetArrayLength());
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
        // Seed a VALID release: a completed goal and empty RepositoryNames (a valid no-op execution).
        // This makes the test meaningful: WITHOUT the comma guard, "Released,Planning" would parse
        // to Released via bitwise OR, execution would SUCCEED (no repos = no-op), and the endpoint
        // would return 200. WITH the guard it returns 400. If the guard is removed, this test fails.
        using (var scope = factory.Services.CreateScope())
        {
            var store = scope.ServiceProvider.GetRequiredService<IGoalStore>();
            await store.CreateReleaseAsync(new Release { Id = releaseId, Tag = "v1.0.0", RepositoryNames = [] }, ct);
            await store.CreateGoalAsync(
                new Goal { Id = UniqueId(), Description = "Test", ReleaseId = releaseId, Status = GoalStatus.Completed }, ct);
        }

        var response = await client.PatchAsync(
            $"/api/releases/{releaseId}/status", StatusJson("Released,Planning"), ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        // The error message mentions the invalid status value.
        var body = await response.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(body);
        var error = doc.RootElement.GetProperty("error").GetString();
        Assert.False(string.IsNullOrWhiteSpace(error));
        Assert.Contains("Released,Planning", error);

        // A comma-combined value must never trigger release git operations.
        Assert.Empty(fake.MergeCalls);
        Assert.Empty(fake.CreateTagCalls);
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

/// <summary>
/// Integration tests for the GET /api/releases/{id}/validate endpoint, verifying HTTP status
/// codes and response bodies for release validation. Uses <see cref="HiveTestFactory"/> with
/// <c>WithWebHostBuilder</c> to register a <see cref="ReleaseExecutionService"/> and
/// <see cref="HiveConfigFile"/> with a mock <see cref="IBrainRepoManager"/>.
/// </summary>
[Collection("HiveIntegration")]
public sealed class ReleaseValidateApiEndpointTests
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

    // ── Validate: valid release returns 200 with valid=true ───────────────

    [Fact]
    public async Task ValidateRelease_ValidRelease_Returns200WithValidTrue()
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
                Id = releaseId, Tag = "v1.0.0", RepositoryNames = ["repo1"],
            }, ct);
            await store.CreateGoalAsync(
                new Goal { Id = UniqueId(), Description = "Test", ReleaseId = releaseId, Status = GoalStatus.Completed }, ct);
        }

        var response = await client.GetAsync($"/api/releases/{releaseId}/validate", ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(body);
        Assert.True(doc.RootElement.GetProperty("valid").GetBoolean());
        var errors = doc.RootElement.GetProperty("errors");
        Assert.Equal(JsonValueKind.Array, errors.ValueKind);
        Assert.Equal(0, errors.GetArrayLength());
    }

    // ── Validate: incomplete goal returns 200 with valid=false ────────────

    [Fact]
    public async Task ValidateRelease_IncompleteGoal_Returns200WithValidFalse()
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
                Id = releaseId, Tag = "v1.0.0", RepositoryNames = ["repo1"],
            }, ct);
            await store.CreateGoalAsync(
                new Goal { Id = UniqueId(), Description = "Test", ReleaseId = releaseId, Status = GoalStatus.InProgress }, ct);
        }

        var response = await client.GetAsync($"/api/releases/{releaseId}/validate", ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(body);
        Assert.False(doc.RootElement.GetProperty("valid").GetBoolean());
        var errors = doc.RootElement.GetProperty("errors");
        Assert.Equal(JsonValueKind.Array, errors.ValueKind);
        Assert.NotEqual(0, errors.GetArrayLength());
        // The error should mention the goal is not completed.
        var errorText = errors.EnumerateArray().Select(e => e.GetString() ?? "").ToList();
        Assert.Contains(errorText, e => e.Contains("not completed", StringComparison.OrdinalIgnoreCase));
    }

    // ── Validate: nonexistent release returns 404 ──────────────────────────

    [Fact]
    public async Task ValidateRelease_NonexistentRelease_Returns404()
    {
        var ct = TestContext.Current.CancellationToken;
        // A plain factory (no ReleaseExecutionService registered) — not needed for 404.
        using var factory = new HiveTestFactory();
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/api/releases/nonexistent-release/validate", ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);

        var body = await response.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(body);
        var error = doc.RootElement.GetProperty("error").GetString();
        Assert.False(string.IsNullOrWhiteSpace(error));
        Assert.Contains("not found", error, StringComparison.OrdinalIgnoreCase);
    }

    // ── Validate: service not registered returns valid=true ────────────────

    [Fact]
    public async Task ValidateRelease_ServiceNotRegistered_ReturnsValidTrue()
    {
        var ct = TestContext.Current.CancellationToken;
        // A plain factory (no ReleaseExecutionService registered).
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

        var response = await client.GetAsync($"/api/releases/{releaseId}/validate", ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(body);
        Assert.True(doc.RootElement.GetProperty("valid").GetBoolean());
        var errors = doc.RootElement.GetProperty("errors");
        Assert.Equal(JsonValueKind.Array, errors.ValueKind);
        Assert.Equal(0, errors.GetArrayLength());
    }
}
