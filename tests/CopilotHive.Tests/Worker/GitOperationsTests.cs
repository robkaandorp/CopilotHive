extern alias WorkerAssembly;

using WorkerAssembly::CopilotHive.Worker;

namespace CopilotHive.Tests.Worker;

/// <summary>
/// Integration tests for <see cref="GitOperations"/> that exercise the real git CLI
/// using temporary directories initialised as bare git repositories.
/// </summary>
public sealed class GitOperationsTests : IAsyncLifetime
{
    private string _repoDir = string.Empty;

    /// <inheritdoc/>
    public async ValueTask InitializeAsync()
    {
        _repoDir = Path.Combine(Path.GetTempPath(), $"GitOpsTest_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_repoDir);

        // Initialise a bare git repo in the temp directory.
        await RunAsync(_repoDir, "init");
        await RunAsync(_repoDir, "config user.email \"test@example.com\"");
        await RunAsync(_repoDir, "config user.name \"Test\"");
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        if (Directory.Exists(_repoDir))
            await GitOperations.ForceDeleteDirectoryAsync(_repoDir);
    }

    // ── IsRepoEmptyAsync ─────────────────────────────────────────────────────

    [Fact]
    public async Task IsRepoEmptyAsync_WhenNoCommits_ReturnsTrue()
    {
        var isEmpty = await GitOperations.IsRepoEmptyAsync(_repoDir, CancellationToken.None);

        Assert.True(isEmpty);
    }

    [Fact]
    public async Task IsRepoEmptyAsync_WhenHasCommit_ReturnsFalse()
    {
        await CommitFileAsync("initial.txt", "hello");

        var isEmpty = await GitOperations.IsRepoEmptyAsync(_repoDir, CancellationToken.None);

        Assert.False(isEmpty);
    }

    // ── CreateBranchAsync — empty repository ─────────────────────────────────

    [Fact]
    public async Task CreateBranchAsync_OnEmptyRepo_CreatesOrphanBranch()
    {
        // Act — the repo has no commits so checking out 'main' would fail
        await GitOperations.CreateBranchAsync(_repoDir, "feature", "main", CancellationToken.None);

        // Assert — verify we are now on the new orphan branch
        var (_, stdout, _) = await GitOperations.RunGitCommandAsync(
            _repoDir, "branch --show-current", CancellationToken.None);

        Assert.Equal("feature", stdout.Trim());
    }

    [Fact]
    public async Task CreateBranchAsync_OnEmptyRepo_OrphanBranchDoesNotThrow()
    {
        // Should complete without throwing
        var exception = await Record.ExceptionAsync(() =>
            GitOperations.CreateBranchAsync(_repoDir, "my-branch", "develop", CancellationToken.None));

        Assert.Null(exception);
    }

    // ── CreateBranchAsync — non-empty repository ─────────────────────────────

    [Fact]
    public async Task CreateBranchAsync_OnNonEmptyRepo_CreatesFeatureBranch()
    {
        await CommitFileAsync("readme.md", "# Project");

        await GitOperations.CreateBranchAsync(_repoDir, "feature/new", "master", CancellationToken.None);

        var (_, stdout, _) = await GitOperations.RunGitCommandAsync(
            _repoDir, "branch --show-current", CancellationToken.None);

        Assert.Equal("feature/new", stdout.Trim());
    }

    [Fact]
    public async Task CreateBranchAsync_OnNonEmptyRepo_WhenBaseBranchMissing_Throws()
    {
        await CommitFileAsync("readme.md", "# Project");

        var exception = await Record.ExceptionAsync(() =>
            GitOperations.CreateBranchAsync(
                _repoDir, "feature/x", "nonexistent-base", CancellationToken.None));

        Assert.IsType<GitOperationException>(exception);
        Assert.Contains("nonexistent-base", exception.Message);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private async Task CommitFileAsync(string fileName, string content)
    {
        var filePath = Path.Combine(_repoDir, fileName);
        await File.WriteAllTextAsync(filePath, content);
        await RunAsync(_repoDir, $"add {fileName}");
        await RunAsync(_repoDir, $"commit -m \"Add {fileName}\"");
    }

    private static async Task RunAsync(string workDir, string args)
    {
        var (exitCode, _, stderr) = await GitOperations.RunGitCommandAsync(workDir, args, CancellationToken.None);
        if (exitCode != 0)
            throw new InvalidOperationException($"git {args} failed: {stderr}");
    }
}
