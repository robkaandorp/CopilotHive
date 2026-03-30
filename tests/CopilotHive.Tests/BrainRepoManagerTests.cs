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
        {
            Directory.Delete(_tempDir, recursive: true);
        }
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
    public async Task DeleteRemoteBranchAsync_NoCloneExists_LogsWarningAndDoesNotThrow()
    {
        var manager = new BrainRepoManager(_tempDir, NullLogger<BrainRepoManager>.Instance);
        var ct = TestContext.Current.CancellationToken;

        // No clone exists — method should not throw, just log a warning
        var ex = await Record.ExceptionAsync(() =>
            manager.DeleteRemoteBranchAsync("nonexistent-repo", "copilothive/test-goal", ct));

        Assert.Null(ex);
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
        // The method should not throw - it logs warnings and continues.
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
    public async Task DeleteRemoteBranchAsync_LogsWarningWhenGitPushFails()
    {
        var ct = TestContext.Current.CancellationToken;
        var clonePath = InitTempGitRepoWithRemote(_tempDir, "test-repo");

        var logger = new TestLogger<BrainRepoManager>();
        var manager = new BrainRepoManager(_tempDir, logger);

        // Try to delete a branch that doesn't exist remotely - git push --delete will fail
        await manager.DeleteRemoteBranchAsync("test-repo", "copilothive/nonexistent-branch", ct);

        // Verify a warning was logged about the failed push
        Assert.Contains(logger.LogEntries, e =>
            e.LogLevel == LogLevel.Warning &&
            e.Message.Contains("Failed to delete remote branch"));
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

    private static void Git(string workDir, params string[] args)
    {
        var psi = new ProcessStartInfo("git")
        {
            WorkingDirectory = workDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
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
            Directory.Delete(_tempDir, recursive: true);
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
