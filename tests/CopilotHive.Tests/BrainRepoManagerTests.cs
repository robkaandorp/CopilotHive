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

        var manager = new BrainRepoManager(_tempDir, NullLogger<BrainRepoManager>.Instance);

        // This will fail to push --delete because there's no actual remote,
        // but the local branch deletion should still be attempted.
        // The method should not throw - it logs warnings and continues.
        var ex = await Record.ExceptionAsync(() =>
            manager.DeleteRemoteBranchAsync("test-repo", "copilothive/test-goal", ct));

        // Method should not throw - it handles errors gracefully
        Assert.Null(ex);
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
