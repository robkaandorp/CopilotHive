using CopilotHive.Configuration;
using CopilotHive.Git;
using CopilotHive.Services;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

using Xunit;

namespace CopilotHive.Tests.Services;

/// <summary>
/// Facade tests for the repository + branches operations of <see cref="IConfigFacade"/>
/// (step 3 of the Blazor loopback-HTTP removal). Proves every outcome of the repository
/// failure table — kind, exact message, and exception propagation — is preserved relative
/// to the pre-facade endpoint handlers, including the branches 503-vs-500 distinction
/// (typed as <see cref="FacadeErrorKind.ServiceUnavailable"/>) and the catch-all swallow
/// of <see cref="OperationCanceledException"/>.
/// </summary>
/// <remarks>
/// Each test is removal-proof: it fails if the mapped kind, the exact message, or the
/// catch/propagate decision is removed or changed. All async coordination is deterministic
/// (faulted tasks / TCS gates); there are no timing-based waits.
/// </remarks>
[Collection("HiveIntegration")]
public class ConfigFacadeRepositoryTests
{
    // ── Exact messages the pre-facade handlers produced ────────────────────

    private const string ConfigNotConfiguredMessage = "Config repo not configured.";
    private const string CrudNotConfiguredMessage = "Config service is not configured.";
    private const string RepoManagerUnavailableMessage = "Repository manager is not available.";
    private const string BranchesInternalMessage = "Failed to list branches for this repository.";

    // ── GetRepositories ────────────────────────────────────────────────────

    /// <summary>
    /// Null-HiveConfigFile GetRepositories → NotFound with the pre-facade handler's error body
    /// ("Config repo not configured." → the endpoint's 404 <c>{error}</c> payload).
    /// </summary>
    [Fact]
    public async Task GetRepositories_NullHiveConfigFile_ReturnsNotFound()
    {
        using var factory = new ConfigFacadeTests.FacadeFactory(hiveConfig: null);
        var facade = factory.Services.GetRequiredService<IConfigFacade>();

        var result = facade.GetRepositories();

        Assert.False(result.Success);
        Assert.Equal(FacadeErrorKind.NotFound, result.Kind);
        Assert.Equal(ConfigNotConfiguredMessage, result.Error);
        Assert.Null(result.Value);
    }

    /// <summary>
    /// GetRepositories with a registered config → success with every repository projected
    /// field-for-field onto <see cref="RepositoryDto"/> (including release and publish-nuget
    /// sub-objects). Proves the null check is the ONLY NotFound path and the mapping mirrors
    /// <see cref="RepositoryConfig"/> exactly.
    /// </summary>
    [Fact]
    public async Task GetRepositories_RegisteredConfig_ReturnsMappedDtos()
    {
        var config = new HiveConfigFile
        {
            Orchestrator = new OrchestratorConfig(),
            Repositories =
            [
                new RepositoryConfig
                {
                    Name = "repo-a",
                    Url = "https://github.com/org/repo-a.git",
                    DefaultBranch = "main",
                    MonitorCi = true,
                    CiTimeoutMinutes = 45,
                    Release = new ReleaseRepoConfig { MergeTo = "main", TagBranch = "develop" },
                    PublishNuGet = new NuGetPublishConfig
                    {
                        Packages = [new NuGetPackageEntry { PackageId = "My.Library" }]
                    }
                },
                new RepositoryConfig
                {
                    Name = "repo-b",
                    Url = "https://github.com/org/repo-b.git",
                    DefaultBranch = "develop",
                },
            ],
        };
        using var factory = new ConfigFacadeTests.FacadeFactory(config);
        var facade = factory.Services.GetRequiredService<IConfigFacade>();

        var result = facade.GetRepositories();

        Assert.True(result.Success);
        Assert.Equal(FacadeErrorKind.None, result.Kind);
        var repos = result.Value!;
        Assert.Equal(2, repos.Count);

        var a = repos[0];
        Assert.Equal("repo-a", a.Name);
        Assert.Equal("https://github.com/org/repo-a.git", a.Url);
        Assert.Equal("main", a.DefaultBranch);
        Assert.True(a.MonitorCi);
        Assert.Equal(45, a.CiTimeoutMinutes);
        Assert.NotNull(a.Release);
        Assert.Equal("main", a.Release!.MergeTo);
        Assert.Equal("develop", a.Release.TagBranch);
        Assert.NotNull(a.PublishNuGet);
        Assert.Equal("My.Library", Assert.Single(a.PublishNuGet!.Packages).PackageId);

        var b = repos[1];
        Assert.Equal("repo-b", b.Name);
        Assert.Equal("develop", b.DefaultBranch);
        Assert.False(b.MonitorCi);
        Assert.Equal(30, b.CiTimeoutMinutes);
        Assert.Null(b.Release);
        Assert.Null(b.PublishNuGet);
    }

    // ── Absent-service mutations → NotConfigured ───────────────────────────

    /// <summary>
    /// Every repository mutation with <see cref="ConfigModelService"/> absent → NotConfigured
    /// with the EXACT message the pre-facade handler emitted (rendered as a 500
    /// problem-details body). Removing a guard, changing a message, or swapping the kind
    /// fails this test.
    /// </summary>
    [Fact]
    public async Task RepositoryMutations_ServiceAbsent_ReturnNotConfiguredWithExactMessage()
    {
        using var factory = new ConfigFacadeTests.FacadeFactory(new HiveConfigFile
        {
            Orchestrator = new OrchestratorConfig()
        });
        var facade = factory.Services.GetRequiredService<IConfigFacade>();
        var request = new RepositoryRequest("r", "https://github.com/org/r.git", "main");

        var add = await facade.AddRepositoryAsync(request);
        Assert.False(add.Success);
        Assert.Equal(FacadeErrorKind.NotConfigured, add.Kind);
        Assert.Equal(CrudNotConfiguredMessage, add.Error);

        var update = await facade.UpdateRepositoryAsync("r", request);
        Assert.False(update.Success);
        Assert.Equal(FacadeErrorKind.NotConfigured, update.Kind);
        Assert.Equal(CrudNotConfiguredMessage, update.Error);

        var remove = await facade.RemoveRepositoryAsync("r");
        Assert.False(remove.Success);
        Assert.Equal(FacadeErrorKind.NotConfigured, remove.Kind);
        Assert.Equal(CrudNotConfiguredMessage, remove.Error);
    }

    // ── Absent repo manager → ServiceUnavailable ───────────────────────────

    /// <summary>
    /// GetBranchesAsync with <see cref="IBrainRepoManager"/> absent → ServiceUnavailable with
    /// the EXACT message the pre-facade handler emitted (rendered as a 503 problem-details
    /// body — the typed Kind, NOT inferred from the error message).
    /// </summary>
    [Fact]
    public async Task GetBranchesAsync_NoRepoManager_ReturnsServiceUnavailable()
    {
        using var factory = new ConfigFacadeTests.FacadeFactory(new HiveConfigFile
        {
            Orchestrator = new OrchestratorConfig()
        });
        var facade = factory.Services.GetRequiredService<IConfigFacade>();

        var result = await facade.GetBranchesAsync("repo", TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.Equal(FacadeErrorKind.ServiceUnavailable, result.Kind);
        Assert.Equal(RepoManagerUnavailableMessage, result.Error);
        Assert.Null(result.Value);
    }

    // ── AddRepositoryAsync against the real ConfigModelService ─────────────

    /// <summary>
    /// AddRepositoryAsync with an invalid request (empty URL) → BadRequest carrying the
    /// service's exact ArgumentException message — the pre-facade 400 <c>{error}</c> body.
    /// </summary>
    [Fact]
    public async Task AddRepositoryAsync_InvalidRequest_ReturnsBadRequestWithServiceMessage()
    {
        var (config, service, dir) = ConfigFacadeTests.CreateRealService();
        try
        {
            using var factory = new ConfigFacadeTests.FacadeFactory(config, service);
            var facade = factory.Services.GetRequiredService<IConfigFacade>();

            var result = await facade.AddRepositoryAsync(
                new RepositoryRequest("bad-repo", "", "main"));

            Assert.False(result.Success);
            Assert.Equal(FacadeErrorKind.BadRequest, result.Kind);
            Assert.Equal("Repository URL cannot be null or empty. (Parameter 'url')", result.Error);
            Assert.Null(result.Value);
            // Nothing was persisted.
            Assert.Empty(config.Repositories);
        }
        finally
        {
            ConfigFacadeTests.CleanupDir(dir);
        }
    }

    /// <summary>
    /// AddRepositoryAsync with a case-insensitively duplicated name → Conflict carrying the
    /// service's exact InvalidOperationException message — the pre-facade 409 <c>{error}</c> body.
    /// </summary>
    [Fact]
    public async Task AddRepositoryAsync_DuplicateName_ReturnsConflictWithServiceMessage()
    {
        var (config, service, dir) = ConfigFacadeTests.CreateRealService(cfg =>
            cfg.Repositories =
            [
                new RepositoryConfig { Name = "dup-repo", Url = "https://github.com/org/dup.git", DefaultBranch = "main" }
            ]);
        try
        {
            using var factory = new ConfigFacadeTests.FacadeFactory(config, service);
            var facade = factory.Services.GetRequiredService<IConfigFacade>();

            var result = await facade.AddRepositoryAsync(
                new RepositoryRequest("DUP-REPO", "https://github.com/org/other.git", "develop"));

            Assert.False(result.Success);
            Assert.Equal(FacadeErrorKind.Conflict, result.Kind);
            Assert.Equal("Repository 'DUP-REPO' already exists", result.Error);
            Assert.Null(result.Value);
            // The duplicate was not added.
            Assert.Single(config.Repositories);
        }
        finally
        {
            ConfigFacadeTests.CleanupDir(dir);
        }
    }

    /// <summary>
    /// AddRepositoryAsync success path: the repository is persisted through the real service
    /// and the facade returns a SavedResult.
    /// </summary>
    [Fact]
    public async Task AddRepositoryAsync_ValidRequest_ReturnsSuccessAndPersists()
    {
        var (config, service, dir) = ConfigFacadeTests.CreateRealService();
        try
        {
            using var factory = new ConfigFacadeTests.FacadeFactory(config, service);
            var facade = factory.Services.GetRequiredService<IConfigFacade>();

            var result = await facade.AddRepositoryAsync(
                new RepositoryRequest("new-repo", "https://github.com/org/new.git", "main"));

            Assert.True(result.Success);
            Assert.Equal(FacadeErrorKind.None, result.Kind);
            Assert.True(result.Value!.Saved);
            var repo = Assert.Single(config.Repositories);
            Assert.Equal("new-repo", repo.Name);
            Assert.Equal("https://github.com/org/new.git", repo.Url);
        }
        finally
        {
            ConfigFacadeTests.CleanupDir(dir);
        }
    }

    // ── UpdateRepositoryAsync against the real ConfigModelService ───────────

    /// <summary>
    /// UpdateRepositoryAsync with a missing name → NotFound carrying the service's exact
    /// InvalidOperationException message — the pre-facade 404 <c>{error}</c> body (NOT 409,
    /// different from add).
    /// </summary>
    [Fact]
    public async Task UpdateRepositoryAsync_MissingName_ReturnsNotFoundWithServiceMessage()
    {
        var (config, service, dir) = ConfigFacadeTests.CreateRealService();
        try
        {
            using var factory = new ConfigFacadeTests.FacadeFactory(config, service);
            var facade = factory.Services.GetRequiredService<IConfigFacade>();

            var result = await facade.UpdateRepositoryAsync(
                "missing", new RepositoryRequest("missing", "https://github.com/org/new.git", "main"));

            Assert.False(result.Success);
            Assert.Equal(FacadeErrorKind.NotFound, result.Kind);
            Assert.Equal("Repository 'missing' not found", result.Error);
            Assert.Null(result.Value);
        }
        finally
        {
            ConfigFacadeTests.CleanupDir(dir);
        }
    }

    /// <summary>
    /// UpdateRepositoryAsync success path: the repository is updated through the real service
    /// and the facade returns a SavedResult.
    /// </summary>
    [Fact]
    public async Task UpdateRepositoryAsync_ExistingName_ReturnsSuccessAndPersists()
    {
        var (config, service, dir) = ConfigFacadeTests.CreateRealService(cfg =>
            cfg.Repositories =
            [
                new RepositoryConfig { Name = "upd-repo", Url = "https://github.com/org/old.git", DefaultBranch = "main" }
            ]);
        try
        {
            using var factory = new ConfigFacadeTests.FacadeFactory(config, service);
            var facade = factory.Services.GetRequiredService<IConfigFacade>();

            var result = await facade.UpdateRepositoryAsync(
                "upd-repo", new RepositoryRequest("upd-repo", "https://github.com/org/new.git", "develop"));

            Assert.True(result.Success);
            Assert.Equal(FacadeErrorKind.None, result.Kind);
            Assert.True(result.Value!.Saved);
            var repo = Assert.Single(config.Repositories);
            Assert.Equal("https://github.com/org/new.git", repo.Url);
            Assert.Equal("develop", repo.DefaultBranch);
        }
        finally
        {
            ConfigFacadeTests.CleanupDir(dir);
        }
    }

    // ── RemoveRepositoryAsync against the real ConfigModelService ──────────

    /// <summary>
    /// RemoveRepositoryAsync with a missing name → NotFound with the facade's exact message
    /// ("Repository '{name}' not found.") — the pre-facade 404 <c>{error}</c> body.
    /// </summary>
    [Fact]
    public async Task RemoveRepositoryAsync_MissingName_ReturnsNotFoundWithFacadeMessage()
    {
        var (config, service, dir) = ConfigFacadeTests.CreateRealService();
        try
        {
            using var factory = new ConfigFacadeTests.FacadeFactory(config, service);
            var facade = factory.Services.GetRequiredService<IConfigFacade>();

            var result = await facade.RemoveRepositoryAsync("no-such-repo");

            Assert.False(result.Success);
            Assert.Equal(FacadeErrorKind.NotFound, result.Kind);
            Assert.Equal("Repository 'no-such-repo' not found.", result.Error);
            Assert.Null(result.Value);
        }
        finally
        {
            ConfigFacadeTests.CleanupDir(dir);
        }
    }

    /// <summary>
    /// RemoveRepositoryAsync success path: the repository is removed through the real service
    /// and the facade returns a RemovedResult.
    /// </summary>
    [Fact]
    public async Task RemoveRepositoryAsync_ExistingName_ReturnsSuccessAndRemoves()
    {
        var (config, service, dir) = ConfigFacadeTests.CreateRealService(cfg =>
            cfg.Repositories =
            [
                new RepositoryConfig { Name = "gone-repo", Url = "https://github.com/org/gone.git", DefaultBranch = "main" }
            ]);
        try
        {
            using var factory = new ConfigFacadeTests.FacadeFactory(config, service);
            var facade = factory.Services.GetRequiredService<IConfigFacade>();

            var result = await facade.RemoveRepositoryAsync("gone-repo");

            Assert.True(result.Success);
            Assert.Equal(FacadeErrorKind.None, result.Kind);
            Assert.True(result.Value!.Removed);
            Assert.Empty(config.Repositories);
        }
        finally
        {
            ConfigFacadeTests.CleanupDir(dir);
        }
    }

    // ── GetBranchesAsync against a fake IBrainRepoManager ──────────────────

    /// <summary>
    /// GetBranchesAsync with an "is not cloned" InvalidOperationException → NotFound carrying
    /// the manager's exact message — the pre-facade 404 <c>{error}</c> body.
    /// </summary>
    [Fact]
    public async Task GetBranchesAsync_NotCloned_ReturnsNotFoundWithManagerMessage()
    {
        var fake = new FakeBranchRepoManager(
            _ => Task.FromException<List<string>>(
                new InvalidOperationException("Repository 'test-repo' is not cloned.")));
        using var factory = new ConfigFacadeTests.FacadeFactory(
            new HiveConfigFile { Orchestrator = new OrchestratorConfig() },
            repoManager: fake);
        var facade = factory.Services.GetRequiredService<IConfigFacade>();

        var result = await facade.GetBranchesAsync("test-repo", TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.Equal(FacadeErrorKind.NotFound, result.Kind);
        Assert.Equal("Repository 'test-repo' is not cloned.", result.Error);
        Assert.Null(result.Value);
    }

    /// <summary>
    /// GetBranchesAsync with an ArgumentException (invalid name) → BadRequest carrying the
    /// manager's exact message — the pre-facade 400 <c>{error}</c> body.
    /// </summary>
    [Fact]
    public async Task GetBranchesAsync_InvalidName_ReturnsBadRequestWithManagerMessage()
    {
        var fake = new FakeBranchRepoManager(
            _ => Task.FromException<List<string>>(
                new ArgumentException("Repository name '../evil' is invalid.")));
        using var factory = new ConfigFacadeTests.FacadeFactory(
            new HiveConfigFile { Orchestrator = new OrchestratorConfig() },
            repoManager: fake);
        var facade = factory.Services.GetRequiredService<IConfigFacade>();

        var result = await facade.GetBranchesAsync("../evil", TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.Equal(FacadeErrorKind.BadRequest, result.Kind);
        Assert.Equal("Repository name '../evil' is invalid.", result.Error);
        Assert.Null(result.Value);
    }

    /// <summary>
    /// GetBranchesAsync with an unexpected exception (neither "is not cloned"
    /// InvalidOperationException nor ArgumentException) → Internal with the EXACT catch-all
    /// message the pre-facade handler emitted ("Failed to list branches for this repository."
    /// → the 500 problem-details body). The raw exception message must NOT leak.
    /// </summary>
    [Fact]
    public async Task GetBranchesAsync_UnexpectedException_ReturnsInternalWithCatchAllMessage()
    {
        var fake = new FakeBranchRepoManager(
            _ => Task.FromException<List<string>>(
                new IOException("fatal: https://user:token@github.com/org/repo.git")));
        using var factory = new ConfigFacadeTests.FacadeFactory(
            new HiveConfigFile { Orchestrator = new OrchestratorConfig() },
            repoManager: fake);
        var facade = factory.Services.GetRequiredService<IConfigFacade>();

        var result = await facade.GetBranchesAsync("test-repo", TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.Equal(FacadeErrorKind.Internal, result.Kind);
        Assert.Equal(BranchesInternalMessage, result.Error);
        Assert.DoesNotContain("token", result.Error);
        Assert.Null(result.Value);
    }

    /// <summary>
    /// GetBranchesAsync success path: the manager's branch list is returned as-is.
    /// </summary>
    [Fact]
    public async Task GetBranchesAsync_Success_ReturnsBranches()
    {
        var fake = new FakeBranchRepoManager(
            _ => Task.FromResult(new List<string> { "main", "develop" }));
        using var factory = new ConfigFacadeTests.FacadeFactory(
            new HiveConfigFile { Orchestrator = new OrchestratorConfig() },
            repoManager: fake);
        var facade = factory.Services.GetRequiredService<IConfigFacade>();

        var result = await facade.GetBranchesAsync("test-repo", TestContext.Current.CancellationToken);

        Assert.True(result.Success);
        Assert.Equal(FacadeErrorKind.None, result.Kind);
        Assert.Equal(["main", "develop"], result.Value!);
    }

    /// <summary>
    /// Cancellation-swallow test (deterministic): a fake <see cref="IBrainRepoManager"/> whose
    /// <c>ListRemoteBranchesAsync</c> THROWS <see cref="OperationCanceledException"/> (a faulted
    /// task) → GetBranchesAsync returns an Internal result with the exact failure message and
    /// does NOT throw. The pre-facade handler's catch-all swallowed OperationCanceledException
    /// into a 500 problem-details body — that behavior is preserved.
    /// </summary>
    [Fact]
    public async Task GetBranchesAsync_OperationCanceledException_ReturnsInternalAndDoesNotThrow()
    {
        var fake = new FakeBranchRepoManager(
            _ => Task.FromException<List<string>>(new OperationCanceledException()));
        using var factory = new ConfigFacadeTests.FacadeFactory(
            new HiveConfigFile { Orchestrator = new OrchestratorConfig() },
            repoManager: fake);
        var facade = factory.Services.GetRequiredService<IConfigFacade>();

        var result = await facade.GetBranchesAsync("test-repo", TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.Equal(FacadeErrorKind.Internal, result.Kind);
        Assert.Equal(BranchesInternalMessage, result.Error);
        Assert.Null(result.Value);
    }
}

/// <summary>
/// A minimal <see cref="IBrainRepoManager"/> fake whose <c>ListRemoteBranchesAsync</c> is
/// driven by a delegate — used to deterministically produce faulted tasks (including
/// <see cref="OperationCanceledException"/>) without any timing-based synchronization.
/// </summary>
internal sealed class FakeBranchRepoManager(Func<string, Task<List<string>>> list) : IBrainRepoManager
{
    public string WorkDirectory => "/fake/work";

    public Task<List<string>> ListRemoteBranchesAsync(string repoName, CancellationToken ct = default)
        => list(repoName);

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
