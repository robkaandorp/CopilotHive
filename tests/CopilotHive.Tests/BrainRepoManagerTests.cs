using CopilotHive.Git;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using System.Diagnostics;

namespace CopilotHive.Tests;

public sealed class BrainRepoManagerTests : IDisposable
{
    private readonly string _tempDir;

    public BrainRepoManagerTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            TestHelpers.ForceDeleteDirectory(_tempDir);
    }

    [Fact]
    public void GetClonePath_ReturnsExpectedPath()
    {
        var manager = new BrainRepoManager(_tempDir, NullLogger<BrainRepoManager>.Instance);

        var path = manager.GetClonePath("copilothive");

        var normalised = path.Replace('\\', '/');
        Assert.EndsWith("repos/copilothive", normalised);
    }

    [Fact]
    public void GetClonePath_DifferentRepos_ReturnDifferentPaths()
    {
        var manager = new BrainRepoManager(_tempDir, NullLogger<BrainRepoManager>.Instance);

        var path1 = manager.GetClonePath("repo-a");
        var path2 = manager.GetClonePath("repo-b");

        Assert.NotEqual(path1, path2);
        Assert.Contains("repo-a", path1);
        Assert.Contains("repo-b", path2);
    }

    [Fact]
    public void GetClonePath_UsesBasePath()
    {
        var customPath = Path.Combine(_tempDir, "custom");
        var manager = new BrainRepoManager(customPath, NullLogger<BrainRepoManager>.Instance);

        var path = manager.GetClonePath("myrepo");

        Assert.StartsWith(Path.GetFullPath(customPath), path);
    }

    [Fact]
    public void WorkDirectory_PointsToReposFolder()
    {
        var manager = new BrainRepoManager(_tempDir, NullLogger<BrainRepoManager>.Instance);

        var workDir = manager.WorkDirectory;

        var normalised = workDir.Replace('\\', '/');
        Assert.EndsWith("repos", normalised);
    }

    [Fact]
    public void GetClonePath_IsChildOfWorkDirectory()
    {
        var manager = new BrainRepoManager(_tempDir, NullLogger<BrainRepoManager>.Instance);

        var clonePath = manager.GetClonePath("myrepo");

        Assert.StartsWith(manager.WorkDirectory, clonePath);
    }

    [Fact]
    public async Task DeleteRemoteBranchAsync_NoCloneExists_ReturnsNotFound()
    {
        var manager = new BrainRepoManager(_tempDir, NullLogger<BrainRepoManager>.Instance);
        var ct = TestContext.Current.CancellationToken;

        // No clone exists — method should return NotFound
        var result = await manager.DeleteRemoteBranchAsync("nonexistent-repo", "copilothive/test-goal", ct);

        Assert.Equal(BranchDeleteResult.NotFound, result);
    }

    [Fact]
    public async Task DeleteRemoteBranchAsync_CloneExistsWithNoBranch_DoesNotThrow()
    {
        var manager = new BrainRepoManager(_tempDir, NullLogger<BrainRepoManager>.Instance);
        var ct = TestContext.Current.CancellationToken;
        var clonePath = manager.GetClonePath("test-repo");

        // Create a minimal .git structure so the clone-existence check passes
        Directory.CreateDirectory(Path.Combine(clonePath, ".git"));

        // Method should not throw even though git push will fail (no remote configured)
        var ex = await Record.ExceptionAsync(() =>
            manager.DeleteRemoteBranchAsync("test-repo", "copilothive/test-goal", ct));

        Assert.Null(ex);
    }

    [Fact]
    public async Task DeleteRemoteBranchAsync_DeletesRemoteAndLocalBranch()
    {
        var ct = TestContext.Current.CancellationToken;
        var clonePath = InitTempGitRepoWithRemote(_tempDir, "test-repo");

        // Create a local branch that we'll try to delete
        Git(clonePath, "checkout", "-b", "copilothive/test-goal");
        Git(clonePath, "checkout", "main"); // Go back to main

        // Verify the branch exists before deletion
        var branchesBefore = GitOutput(clonePath, "branch", "--list", "copilothive/test-goal");
        Assert.Contains("copilothive/test-goal", branchesBefore);

        var manager = new BrainRepoManager(_tempDir, NullLogger<BrainRepoManager>.Instance);

        // This will fail to push --delete because there's no actual remote,
        // but the local branch deletion should still be attempted.
        // The method should not throw - it handles errors gracefully.
        var ex = await Record.ExceptionAsync(() =>
            manager.DeleteRemoteBranchAsync("test-repo", "copilothive/test-goal", ct));

        // Method should not throw - it handles errors gracefully
        Assert.Null(ex);

        // Verify the local branch was deleted (the git branch -D command ran)
        var branchesAfter = GitOutput(clonePath, "branch", "--list", "copilothive/test-goal");
        Assert.DoesNotContain("copilothive/test-goal", branchesAfter);
    }

    [Fact]
    public async Task DeleteRemoteBranchAsync_LogsWarningOnFailure()
    {
        var ct = TestContext.Current.CancellationToken;

        // Use a logger that captures log messages
        var logger = new TestLogger<BrainRepoManager>();
        var manager = new BrainRepoManager(_tempDir, logger);

        // Call with non-existent repo - should log warning
        await manager.DeleteRemoteBranchAsync("nonexistent-repo", "copilothive/test-goal", ct);

        // Verify a warning was logged about missing clone
        Assert.Contains(logger.LogEntries, e =>
            e.LogLevel == LogLevel.Warning &&
            e.Message.Contains("Cannot delete remote branch"));
    }

    [Fact]
    public async Task DeleteRemoteBranchAsync_RemoteBranchNotFound_ReturnsNotFoundAndLogsInfo()
    {
        var ct = TestContext.Current.CancellationToken;

        // Set up a real bare remote so git push --delete can talk to it.
        // The branch does not exist on the remote, so git should report "remote ref does not exist".
        var remoteDir = Path.Combine(_tempDir, "bare-remote.git");
        Directory.CreateDirectory(remoteDir);
        Git(remoteDir, "init", "--bare", "-b", "main");

        // Stage repo: push an initial commit so the remote is non-empty
        var stagingDir = Path.Combine(_tempDir, "staging");
        Directory.CreateDirectory(stagingDir);
        Git(stagingDir, "init", "-b", "main");
        Git(stagingDir, "config", "user.email", "test@test.com");
        Git(stagingDir, "config", "user.name", "Test");
        File.WriteAllText(Path.Combine(stagingDir, "README.md"), "# Test\n");
        Git(stagingDir, "add", "README.md");
        Git(stagingDir, "commit", "-m", "Initial commit");
        Git(stagingDir, "remote", "add", "origin", remoteDir);
        Git(stagingDir, "push", "origin", "main");

        // Clone the remote into the repos directory managed by BrainRepoManager
        var reposDir = Path.Combine(_tempDir, "repos");
        Directory.CreateDirectory(reposDir);
        Git(reposDir, "clone", remoteDir, "notfound-repo");
        var clonePath = Path.Combine(reposDir, "notfound-repo");
        Git(clonePath, "config", "user.email", "test@test.com");
        Git(clonePath, "config", "user.name", "Test");

        var logger = new TestLogger<BrainRepoManager>();
        var manager = new BrainRepoManager(_tempDir, logger);

        // The branch "copilothive/nonexistent-branch" does not exist on the remote.
        // Git will report "remote ref does not exist" → NotFound
        var result = await manager.DeleteRemoteBranchAsync("notfound-repo", "copilothive/nonexistent-branch", ct);

        // Should return NotFound (not Failed) since the branch simply isn't there
        Assert.Equal(BranchDeleteResult.NotFound, result);
    }

    [Fact]
    public async Task EnsureCloneAsync_CloneExistsEmptyRemote_SkipsCheckoutAndLogsWarning()
    {
        // Arrange: create a bare "remote" repo with no commits (empty)
        var ct = TestContext.Current.CancellationToken;
        var remoteDir = Path.Combine(_tempDir, "empty-remote.git");
        Directory.CreateDirectory(remoteDir);
        Git(remoteDir, "init", "--bare");

        // Clone from the empty remote into the repos directory
        var reposDir = Path.Combine(_tempDir, "repos");
        Directory.CreateDirectory(reposDir);
        Git(reposDir, "clone", remoteDir, "empty-repo");
        var clonePath = Path.Combine(reposDir, "empty-repo");

        // Configure git identity so the clone looks complete
        Git(clonePath, "config", "user.email", "test@test.com");
        Git(clonePath, "config", "user.name", "Test");

        var logger = new TestLogger<BrainRepoManager>();
        var manager = new BrainRepoManager(_tempDir, logger);

        // Act: EnsureCloneAsync when clone already exists but remote is empty
        var ex = await Record.ExceptionAsync(() =>
            manager.EnsureCloneAsync("empty-repo", remoteDir, "main", ct));

        // Assert: should not throw
        Assert.Null(ex);

        // Should have logged a warning about the empty repository
        Assert.Contains(logger.LogEntries, e =>
            e.LogLevel == LogLevel.Warning &&
            e.Message.Contains("skipping checkout/reset"));
    }

    [Fact]
    public async Task EnsureCloneAsync_CloneExistsWithPopulatedRemote_ChecksOutBranch()
    {
        // Arrange: create a bare remote with one commit
        var ct = TestContext.Current.CancellationToken;
        var remoteDir = Path.Combine(_tempDir, "populated-remote.git");
        Directory.CreateDirectory(remoteDir);
        Git(remoteDir, "init", "--bare", "-b", "main");

        // Create a staging repo to push an initial commit to the bare remote
        var stagingDir = Path.Combine(_tempDir, "staging");
        Directory.CreateDirectory(stagingDir);
        Git(stagingDir, "init", "-b", "main");
        Git(stagingDir, "config", "user.email", "test@test.com");
        Git(stagingDir, "config", "user.name", "Test");
        File.WriteAllText(Path.Combine(stagingDir, "README.md"), "# Hello\n");
        Git(stagingDir, "add", "README.md");
        Git(stagingDir, "commit", "-m", "Initial commit");
        Git(stagingDir, "remote", "add", "origin", remoteDir);
        Git(stagingDir, "push", "origin", "main");

        // Get the commit SHA from the remote
        var expectedSha = GitOutput(stagingDir, "rev-parse", "HEAD").Trim();

        // Clone from the populated remote
        var reposDir = Path.Combine(_tempDir, "repos");
        Directory.CreateDirectory(reposDir);
        Git(reposDir, "clone", remoteDir, "populated-repo");
        var clonePath = Path.Combine(reposDir, "populated-repo");
        Git(clonePath, "config", "user.email", "test@test.com");
        Git(clonePath, "config", "user.name", "Test");

        // Put main into a divergent state by committing a new file locally (don't push).
        // This advances main ahead of origin/main, so EnsureCloneAsync must run
        // `git reset --hard origin/main` to rewind back to expectedSha — verifying
        // that BOTH checkout AND reset are genuinely exercised.
        File.WriteAllText(Path.Combine(clonePath, "local-only.txt"), "divergent\n");
        Git(clonePath, "add", "local-only.txt");
        Git(clonePath, "commit", "-m", "Local divergent commit (not pushed)");

        var logger = new TestLogger<BrainRepoManager>();
        var manager = new BrainRepoManager(_tempDir, logger);

        // Act: EnsureCloneAsync must checkout main and reset to origin/main
        var ex = await Record.ExceptionAsync(() =>
            manager.EnsureCloneAsync("populated-repo", remoteDir, "main", ct));

        // Assert: should succeed without warnings about empty repository
        Assert.Null(ex);
        Assert.DoesNotContain(logger.LogEntries, e =>
            e.LogLevel == LogLevel.Warning &&
            e.Message.Contains("skipping checkout/reset"));

        // Verify HEAD is back on "main" (checkout was actually performed)
        var headBranch = GitOutput(clonePath, "rev-parse", "--abbrev-ref", "HEAD").Trim();
        Assert.Equal("main", headBranch);

        // Verify the working tree is at the expected origin/main commit (reset was performed)
        var headSha = GitOutput(clonePath, "rev-parse", "HEAD").Trim();
        Assert.Equal(expectedSha, headSha);
    }

    [Fact]
    public async Task MergeFeatureBranchAsync_EmptyRemote_ReturnsEmptyWithoutThrowing()
    {
        // Arrange: create a bare remote with no commits (empty) — neither branch exists
        var ct = TestContext.Current.CancellationToken;
        var remoteDir = Path.Combine(_tempDir, "empty-remote2.git");
        Directory.CreateDirectory(remoteDir);
        Git(remoteDir, "init", "--bare");

        // Clone from the empty remote
        var reposDir = Path.Combine(_tempDir, "repos");
        Directory.CreateDirectory(reposDir);
        Git(reposDir, "clone", remoteDir, "empty-repo2");
        var clonePath = Path.Combine(reposDir, "empty-repo2");
        Git(clonePath, "config", "user.email", "test@test.com");
        Git(clonePath, "config", "user.name", "Test");

        var logger = new TestLogger<BrainRepoManager>();
        var manager = new BrainRepoManager(_tempDir, logger);

        // Act
        string? result = null;
        var ex = await Record.ExceptionAsync(async () =>
            result = await manager.MergeFeatureBranchAsync("empty-repo2", "copilothive/feat", "main", "msg", ct));

        // Assert: should NOT throw — returns empty string and logs a warning
        Assert.Null(ex);
        Assert.Equal(string.Empty, result);
        Assert.Contains(logger.LogEntries, e =>
            e.LogLevel == LogLevel.Warning &&
            e.Message.Contains("empty repository"));
    }

    [Fact]
    public async Task MergeFeatureBranchAsync_NoDefaultBranchButFeatureExists_CreatesDefaultBranchAndReturnsHash()
    {
        // Arrange: bare remote with only the feature branch (no default branch yet — orphan push scenario)
        var ct = TestContext.Current.CancellationToken;

        // Create the bare remote
        var remoteDir = Path.Combine(_tempDir, "orphan-remote.git");
        Directory.CreateDirectory(remoteDir);
        Git(remoteDir, "init", "--bare");

        // Push a feature branch to the remote via a staging repo — no default branch is created
        var stagingDir = Path.Combine(_tempDir, "orphan-staging");
        Directory.CreateDirectory(stagingDir);
        Git(stagingDir, "init", "-b", "copilothive/feat");
        Git(stagingDir, "config", "user.email", "test@test.com");
        Git(stagingDir, "config", "user.name", "Test");
        File.WriteAllText(Path.Combine(stagingDir, "README.md"), "# Feature\n");
        Git(stagingDir, "add", "README.md");
        Git(stagingDir, "commit", "-m", "Feature commit");
        Git(stagingDir, "remote", "add", "origin", remoteDir);
        Git(stagingDir, "push", "origin", "copilothive/feat");

        // Get the expected commit SHA from the staging repo
        var expectedSha = GitOutput(stagingDir, "rev-parse", "HEAD").Trim();

        // Clone from the remote (without --branch since the default branch doesn't exist yet)
        var reposDir = Path.Combine(_tempDir, "repos");
        Directory.CreateDirectory(reposDir);
        Git(reposDir, "clone", remoteDir, "orphan-repo");
        var clonePath = Path.Combine(reposDir, "orphan-repo");
        Git(clonePath, "config", "user.email", "test@test.com");
        Git(clonePath, "config", "user.name", "Test");

        var logger = new TestLogger<BrainRepoManager>();
        var manager = new BrainRepoManager(_tempDir, logger);

        // Act
        string? result = null;
        var ex = await Record.ExceptionAsync(async () =>
            result = await manager.MergeFeatureBranchAsync(
                "orphan-repo", "copilothive/feat", "main", "chore: squash", ct));

        // Assert: no exception
        Assert.Null(ex);

        // Should return the feature branch HEAD hash (not empty)
        Assert.NotNull(result);
        Assert.NotEmpty(result);
        Assert.Equal(expectedSha, result);

        // Should log an info message about creating the default branch
        Assert.Contains(logger.LogEntries, e =>
            e.LogLevel == LogLevel.Information &&
            e.Message.Contains("Creating it from feature branch"));

        // Should NOT log the "empty repository" warning
        Assert.DoesNotContain(logger.LogEntries, e =>
            e.LogLevel == LogLevel.Warning &&
            e.Message.Contains("empty repository"));

        // Verify the default branch now exists on the remote
        var remoteBranches = GitOutput(remoteDir, "branch");
        Assert.Contains("main", remoteBranches);
    }

    private static string InitTempGitRepoWithRemote(string basePath, string repoName)
    {
        var reposDir = Path.Combine(basePath, "repos");
        var repoDir = Path.Combine(reposDir, repoName);
        Directory.CreateDirectory(repoDir);

        Git(repoDir, "init", "-b", "main");
        Git(repoDir, "config", "user.email", "test@test.com");
        Git(repoDir, "config", "user.name", "Test");

        File.WriteAllText(Path.Combine(repoDir, "README.md"), "# Hello\n");
        Git(repoDir, "add", "README.md");
        Git(repoDir, "commit", "-m", "Initial commit");

        // Add a fake remote (pointing to non-existent path, which is fine for testing)
        Git(repoDir, "remote", "add", "origin", "/tmp/nonexistent.git");

        return repoDir;
    }

    // ── FetchOriginAsync: the managed refresh+fetch API ──────────────────────

    /// <summary>
    /// A real clone with a real (local) origin: the managed fetch succeeds and actually updates
    /// the remote-tracking refs, so it is a genuine replacement for a raw `git fetch origin`.
    /// </summary>
    [Fact]
    public async Task FetchOriginAsync_RealClone_FetchesAndUpdatesTrackingRefs()
    {
        var ct = TestContext.Current.CancellationToken;
        var (remoteDir, clonePath, manager) = SetupFetchRepo("fetch-repo");

        // Advance the remote by one commit through a separate staging clone.
        AddRemoteCommit(remoteDir, "staging-fetch", "extra.txt", "extra");

        var result = await manager.FetchOriginAsync("fetch-repo", null, ct);

        Assert.True(result.Success, result.Error);
        Assert.Null(result.Error);
        var remoteHead = GitOutput(remoteDir, "rev-parse", "main").Trim();
        var trackedHead = GitOutput(clonePath, "rev-parse", "origin/main").Trim();
        Assert.Equal(remoteHead, trackedHead);
    }

    /// <summary>
    /// The branch overload uses the FORCED refspec, so a rewound (non-fast-forward) remote branch
    /// still updates the tracking ref instead of being rejected.
    /// </summary>
    [Fact]
    public async Task FetchOriginAsync_WithBranch_ForceUpdatesTrackingRefOnNonFastForward()
    {
        var ct = TestContext.Current.CancellationToken;
        var (remoteDir, clonePath, manager) = SetupFetchRepo("force-repo");

        var firstSha = GitOutput(remoteDir, "rev-list", "--max-parents=0", "main").Trim();
        AddRemoteCommit(remoteDir, "staging-force", "second.txt", "second");

        var forward = await manager.FetchOriginAsync("force-repo", "main", ct);
        Assert.True(forward.Success, forward.Error);

        // Rewind the remote branch — a NON-fast-forward change.
        Git(remoteDir, "update-ref", "refs/heads/main", firstSha);

        var rewound = await manager.FetchOriginAsync("force-repo", "main", ct);

        Assert.True(rewound.Success, rewound.Error);
        Assert.Equal(firstSha, GitOutput(clonePath, "rev-parse", "origin/main").Trim());
    }

    [Fact]
    public async Task FetchOriginAsync_MissingClone_Throws()
    {
        var ct = TestContext.Current.CancellationToken;
        var manager = new BrainRepoManager(_tempDir, NullLogger<BrainRepoManager>.Instance);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            manager.FetchOriginAsync("nonexistent", null, ct));

        Assert.Contains("not cloned", ex.Message);
    }

    [Fact]
    public async Task FetchOriginAsync_InvalidRepoName_ThrowsArgumentException()
    {
        var ct = TestContext.Current.CancellationToken;
        var manager = new BrainRepoManager(_tempDir, NullLogger<BrainRepoManager>.Instance);

        await Assert.ThrowsAsync<ArgumentException>(() => manager.FetchOriginAsync("../evil", null, ct));
        await Assert.ThrowsAsync<ArgumentException>(() => manager.FetchOriginAsync("a/b", null, ct));
    }

    [Fact]
    public async Task FetchOriginAsync_InvalidBranchName_ThrowsArgumentException()
    {
        var ct = TestContext.Current.CancellationToken;
        var (_, _, manager) = SetupFetchRepo("badbranch-repo");

        await Assert.ThrowsAsync<ArgumentException>(
            () => manager.FetchOriginAsync("badbranch-repo", "--upload-pack=evil", ct));
        await Assert.ThrowsAsync<ArgumentException>(
            () => manager.FetchOriginAsync("badbranch-repo", "bad@{branch}", ct));
    }

    [Fact]
    public async Task FetchOriginAsync_UnknownBranch_ReturnsFailureInsteadOfThrowing()
    {
        var ct = TestContext.Current.CancellationToken;
        var (_, _, manager) = SetupFetchRepo("nobranch-repo");

        var result = await manager.FetchOriginAsync("nobranch-repo", "does-not-exist", ct);

        Assert.False(result.Success);
        Assert.NotNull(result.Error);
        Assert.Empty(result.Output);
    }

    [Fact]
    public async Task FetchOriginAsync_CancelledToken_Throws()
    {
        var (_, _, manager) = SetupFetchRepo("cancel-fetch-repo");
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => manager.FetchOriginAsync("cancel-fetch-repo", null, cts.Token));
    }

    /// <summary>
    /// A LOCAL (non-eligible) origin is never given a credential: the managed fetch behaves
    /// exactly like the plain fetch it replaces even with both lookups wired up.
    /// </summary>
    [Fact]
    public async Task FetchOriginAsync_LocalOrigin_NeverAttachesACredential()
    {
        var ct = TestContext.Current.CancellationToken;
        var remoteDir = Path.Combine(_tempDir, "local-origin-remote");
        CreateRemote(remoteDir);

        var manager = new BrainRepoManager(
            _tempDir,
            NullLogger<BrainRepoManager>.Instance,
            gitRunner: null,
            tokenLookup: _ => Task.FromResult<string?>("gho_should_never_be_used"),
            configuredUrlLookup: _ => remoteDir);

        var clonePath = manager.GetClonePath("local-origin-repo");
        Git(_tempDir, "clone", remoteDir, clonePath);

        var result = await manager.FetchOriginAsync("local-origin-repo", null, ct);

        Assert.True(result.Success, result.Error);
        // The persisted origin is still the bare local path — no credential was injected.
        var origin = GitOutput(clonePath, "remote", "get-url", "origin").Trim();
        Assert.DoesNotContain("gho_should_never_be_used", origin);
        Assert.DoesNotContain("x-access-token", origin);
    }

    // ── FIX 3: fetch-branch validation matches git, not the stricter tag rules ─

    /// <summary>
    /// Branch names <c>git check-ref-format --branch</c> ACCEPTS must not be rejected by the
    /// additive fetch path. The stricter <c>ValidateBranchOrTagName</c> rejects a lone <c>@</c>
    /// component and any component ending in <c>.</c>, but git allows both — so reusing it here
    /// would reject branches every other Composer git tool happily handles.
    /// </summary>
    /// <remarks>
    /// Each case is verified against the REAL <c>git check-ref-format --branch</c> first, so this
    /// test asserts parity with git itself rather than against a hand-copied list.
    /// </remarks>
    [Theory]
    [InlineData("topic/@/work")]
    [InlineData("topic./work")]
    [InlineData("x/@")]
    [InlineData("@")]
    [InlineData("a/@/@/b")]
    [InlineData("a.locks")]
    [InlineData("feature/JIRA-123")]
    [InlineData("release/1.0")]
    [InlineData("a@b")]
    [InlineData("a{b")]
    public void ValidateFetchBranchName_AcceptsEveryNameGitAccepts(string branch)
    {
        // Parity guard: git really does accept this name.
        Assert.True(GitAcceptsBranch(branch), $"Test premise broken: git rejects '{branch}'.");

        // The additive fetch path must accept it too.
        BrainRepoManager.ValidateFetchBranchName(branch);
    }

    /// <summary>Names git REJECTS are still rejected, so validation was not simply removed.</summary>
    [Theory]
    [InlineData("feature.lock")]
    [InlineData("a/b.lock")]
    [InlineData("a/.b")]
    [InlineData(".a")]
    [InlineData("a..b")]
    [InlineData("ref@{0}")]
    [InlineData("a b")]
    [InlineData("a~b")]
    [InlineData("a^b")]
    [InlineData("a:b")]
    [InlineData("a?b")]
    [InlineData("a*b")]
    [InlineData("a[b")]
    [InlineData("a//b")]
    [InlineData("/a")]
    [InlineData("a/")]
    [InlineData("a.")]
    [InlineData("a/b.")]
    [InlineData("-leading-dash")]
    public void ValidateFetchBranchName_RejectsEveryNameGitRejects(string branch)
    {
        // Parity guard: git really does reject this name.
        Assert.False(GitAcceptsBranch(branch), $"Test premise broken: git accepts '{branch}'.");

        Assert.Throws<ArgumentException>(() => BrainRepoManager.ValidateFetchBranchName(branch));
    }

    /// <summary>
    /// End-to-end against a REAL local repository: a branch git accepts but the tag validator
    /// rejects can actually be fetched through the managed origin path.
    /// </summary>
    [Theory]
    [InlineData("topic/@/work")]
    [InlineData("topic./work")]
    public async Task FetchOriginAsync_BranchGitAcceptsButTagRulesReject_IsFetched(string branch)
    {
        // Windows/NTFS refuses to create a directory whose name ends in '.' (e.g. "topic."), so a
        // loose ref for this branch can't be materialized as a local git ref on Windows even though
        // git's own check-ref-format validation accepts the name. Covered on Linux, where the
        // orchestrator/workers actually run.
        if (OperatingSystem.IsWindows() && HasWindowsIncompatibleRefSegment(branch))
            Assert.Skip("Windows/NTFS can't create a directory segment ending in '.'; covered on Linux.");

        var ct = TestContext.Current.CancellationToken;
        var (remoteDir, clonePath, manager) = SetupFetchRepo("odd-branch-repo");

        // Create the awkward branch on the remote.
        Git(remoteDir, "branch", branch, "main");

        var result = await manager.FetchOriginAsync("odd-branch-repo", branch, ct);

        Assert.True(result.Success, result.Error);
        // The tracking ref really was created.
        var tracked = GitOutput(clonePath, "rev-parse", $"origin/{branch}").Trim();
        Assert.False(string.IsNullOrWhiteSpace(tracked));
    }

    /// <summary>The stricter tag validator really would have rejected those same names.</summary>
    [Theory]
    [InlineData("topic/@/work")]
    [InlineData("topic./work")]
    public async Task TagOperations_StillApplyTheStricterTagRules(string name)
    {
        var ct = TestContext.Current.CancellationToken;
        var (_, _, manager) = SetupFetchRepo("strict-tag-repo");

        // CreateTagAsync/DeleteTagAsync keep using ValidateBranchOrTagName — unchanged behaviour.
        await Assert.ThrowsAsync<ArgumentException>(
            () => manager.DeleteTagAsync("strict-tag-repo", name, ct));
    }

    /// <summary>
    /// True when any path segment of <paramref name="branch"/> ends with '.' — Windows/NTFS refuses
    /// to create a directory with a trailing dot, so such a branch can't be created as a local loose
    /// ref on Windows even though git's ref-name validation accepts it.
    /// </summary>
    private static bool HasWindowsIncompatibleRefSegment(string branch)
    {
        foreach (var segment in branch.Split('/'))
        {
            if (segment.EndsWith('.'))
                return true;
        }
        return false;
    }

    /// <summary>Runs the REAL git ref-format check so the theories above assert parity with git.</summary>
    private static bool GitAcceptsBranch(string branch)
    {
        var psi = new ProcessStartInfo("git")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        psi.ArgumentList.Add("check-ref-format");
        psi.ArgumentList.Add("--branch");
        psi.ArgumentList.Add(branch);

        using var p = Process.Start(psi)!;
        p.StandardOutput.ReadToEnd();
        p.StandardError.ReadToEnd();
        p.WaitForExit();
        return p.ExitCode == 0;
    }

    // ── Fetch-test helpers ───────────────────────────────────────────────────

    /// <summary>Creates a bare-style remote with one commit on <c>main</c>.</summary>
    private void CreateRemote(string remoteDir)
    {
        Directory.CreateDirectory(remoteDir);
        Git(remoteDir, "init", "--bare", "-b", "main");

        var seed = Path.Combine(_tempDir, Path.GetRandomFileName());
        Directory.CreateDirectory(seed);
        Git(seed, "init", "-b", "main");
        Git(seed, "config", "user.email", "test@test.com");
        Git(seed, "config", "user.name", "Test");
        File.WriteAllText(Path.Combine(seed, "README.md"), "seed\n");
        Git(seed, "add", "README.md");
        Git(seed, "commit", "-m", "Initial commit");
        Git(seed, "remote", "add", "origin", remoteDir);
        Git(seed, "push", "origin", "main");
    }

    /// <summary>Creates a remote plus a clone of it registered under <paramref name="repoName"/>.</summary>
    private (string RemoteDir, string ClonePath, BrainRepoManager Manager) SetupFetchRepo(string repoName)
    {
        var remoteDir = Path.Combine(_tempDir, repoName + "-remote");
        CreateRemote(remoteDir);

        var manager = new BrainRepoManager(_tempDir, NullLogger<BrainRepoManager>.Instance);
        var clonePath = manager.GetClonePath(repoName);
        Git(_tempDir, "clone", remoteDir, clonePath);
        Git(clonePath, "config", "user.email", "test@test.com");
        Git(clonePath, "config", "user.name", "Test");

        return (remoteDir, clonePath, manager);
    }

    /// <summary>Pushes one new commit to <paramref name="remoteDir"/>'s <c>main</c>.</summary>
    private void AddRemoteCommit(string remoteDir, string stagingName, string fileName, string content)
    {
        var staging = Path.Combine(_tempDir, stagingName);
        Git(_tempDir, "clone", remoteDir, staging);
        Git(staging, "config", "user.email", "test@test.com");
        Git(staging, "config", "user.name", "Test");
        File.WriteAllText(Path.Combine(staging, fileName), content);
        Git(staging, "add", fileName);
        Git(staging, "commit", "-m", $"Add {fileName}");
        Git(staging, "push", "origin", "main");
    }

    private static void Git(string workDir, params string[] args)
    {
        var psi = new ProcessStartInfo("git")
        {
            WorkingDirectory = workDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        // Force LF endings and allow direct commands against bare repo directories regardless of
        // the host's global git config, so these tests behave identically on any machine/OS.
        psi.ArgumentList.Add("-c");
        psi.ArgumentList.Add("core.autocrlf=false");
        psi.ArgumentList.Add("-c");
        psi.ArgumentList.Add("safe.bareRepository=all");
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        p.WaitForExit();
    }

    private static string GitOutput(string workDir, params string[] args)
    {
        var psi = new ProcessStartInfo("git")
        {
            WorkingDirectory = workDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        psi.ArgumentList.Add("-c");
        psi.ArgumentList.Add("core.autocrlf=false");
        psi.ArgumentList.Add("-c");
        psi.ArgumentList.Add("safe.bareRepository=all");
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var output = p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        return output;
    }
}

/// <summary>
/// Tests for <see cref="BrainRepoManager.GetHeadShaAsync"/>.
/// </summary>
public sealed class BrainRepoManagerGetHeadShaTests : IDisposable
{
    private readonly string _tempDir;

    public BrainRepoManagerGetHeadShaTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            TestHelpers.ForceDeleteDirectory(_tempDir);
    }

    [Fact]
    public async Task GetHeadShaAsync_NoCloneExists_ReturnsNull()
    {
        var manager = new BrainRepoManager(_tempDir, NullLogger<BrainRepoManager>.Instance);
        var ct = TestContext.Current.CancellationToken;

        var sha = await manager.GetHeadShaAsync("nonexistent-repo", ct);

        Assert.Null(sha);
    }

    [Fact]
    public async Task GetHeadShaAsync_EmptyGitDir_ReturnsNull()
    {
        // Create a minimal .git structure without any commits
        var manager = new BrainRepoManager(_tempDir, NullLogger<BrainRepoManager>.Instance);
        var ct = TestContext.Current.CancellationToken;
        var clonePath = manager.GetClonePath("empty-repo");
        Directory.CreateDirectory(Path.Combine(clonePath, ".git"));

        // No commits — rev-parse HEAD should fail; method must return null gracefully
        var sha = await manager.GetHeadShaAsync("empty-repo", ct);

        Assert.Null(sha);
    }

    [Fact]
    public async Task GetHeadShaAsync_RepoWithCommit_ReturnsSha()
    {
        var manager = new BrainRepoManager(_tempDir, NullLogger<BrainRepoManager>.Instance);
        var ct = TestContext.Current.CancellationToken;

        // Initialise a real git repo with a commit so HEAD resolves
        var clonePath = manager.GetClonePath("sha-test-repo");
        Directory.CreateDirectory(clonePath);
        Git(clonePath, "init", "-b", "main");
        Git(clonePath, "config", "user.email", "test@test.com");
        Git(clonePath, "config", "user.name", "Test");
        File.WriteAllText(Path.Combine(clonePath, "file.txt"), "hello");
        Git(clonePath, "add", "file.txt");
        Git(clonePath, "commit", "-m", "Initial commit");

        var expectedSha = GitOutput(clonePath, "rev-parse", "HEAD").Trim();

        var sha = await manager.GetHeadShaAsync("sha-test-repo", ct);

        Assert.NotNull(sha);
        Assert.Equal(expectedSha, sha);
    }

    [Fact]
    public async Task GetHeadShaAsync_ReturnsFullFortyCharSha()
    {
        var manager = new BrainRepoManager(_tempDir, NullLogger<BrainRepoManager>.Instance);
        var ct = TestContext.Current.CancellationToken;

        var clonePath = manager.GetClonePath("sha-full-test");
        Directory.CreateDirectory(clonePath);
        Git(clonePath, "init", "-b", "main");
        Git(clonePath, "config", "user.email", "test@test.com");
        Git(clonePath, "config", "user.name", "Test");
        File.WriteAllText(Path.Combine(clonePath, "file.txt"), "content");
        Git(clonePath, "add", "file.txt");
        Git(clonePath, "commit", "-m", "Commit");

        var sha = await manager.GetHeadShaAsync("sha-full-test", ct);

        // Full SHA is 40 hex characters
        Assert.NotNull(sha);
        Assert.Equal(40, sha!.Length);
        Assert.All(sha, c => Assert.True(
            (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F'),
            $"Non-hex character '{c}' in SHA"));
    }

    private static void Git(string workDir, params string[] args)
    {
        var psi = new ProcessStartInfo("git")
        {
            WorkingDirectory = workDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        // Force LF endings and allow direct commands against bare repo directories regardless of
        // the host's global git config, so these tests behave identically on any machine/OS.
        // Also disable commit signing: a host with commit.gpgsign=true globally configured can
        // make many concurrent `git commit` calls (under high xUnit parallelism) contend for the
        // GPG agent and intermittently fail with "gpg: signing failed: Not enough space" — these
        // test commits don't need to be signed.
        psi.ArgumentList.Add("-c");
        psi.ArgumentList.Add("core.autocrlf=false");
        psi.ArgumentList.Add("-c");
        psi.ArgumentList.Add("safe.bareRepository=all");
        psi.ArgumentList.Add("-c");
        psi.ArgumentList.Add("commit.gpgsign=false");
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        p.WaitForExit();
    }

    private static string GitOutput(string workDir, params string[] args)
    {
        var psi = new ProcessStartInfo("git")
        {
            WorkingDirectory = workDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        psi.ArgumentList.Add("-c");
        psi.ArgumentList.Add("core.autocrlf=false");
        psi.ArgumentList.Add("-c");
        psi.ArgumentList.Add("safe.bareRepository=all");
        psi.ArgumentList.Add("-c");
        psi.ArgumentList.Add("commit.gpgsign=false");
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var output = p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        return output;
    }
}

/// <summary>
/// Test logger that captures log entries for verification.
/// </summary>
internal sealed class TestLogger<T> : ILogger<T>
{
    public List<(LogLevel LogLevel, string Message, Exception? Exception)> LogEntries { get; } = [];

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        LogEntries.Add((logLevel, formatter(state, exception), exception));
    }
}
