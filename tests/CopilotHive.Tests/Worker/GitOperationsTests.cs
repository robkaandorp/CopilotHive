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
        // Discover the actual default branch name from git config
        var (_, symRefOut, _) = await GitOperations.RunGitCommandAsync(
            _repoDir, "symbolic-ref --short HEAD", CancellationToken.None);
        var defaultBranch = symRefOut.Trim();

        // Act — the repo has no commits so checking out the default branch would fail
        await GitOperations.CreateBranchAsync(_repoDir, "feature", defaultBranch, CancellationToken.None);

        // Assert 1 — verify we are now on the new orphan branch
        var (_, stdout, _) = await GitOperations.RunGitCommandAsync(
            _repoDir, "branch --show-current", CancellationToken.None);
        Assert.Equal("feature", stdout.Trim());

        // Assert 2 — add a commit and verify it has NO parent (orphan branch characteristic)
        await CommitFileAsync("orphan.txt", "orphan content");
        var (_, parentOutput, _) = await GitOperations.RunGitCommandAsync(
            _repoDir, "log --format=%P", CancellationToken.None);
        Assert.Equal(string.Empty, parentOutput.Trim());
    }

    [Fact]
    public async Task CreateBranchAsync_OnEmptyRepo_OrphanBranchIsRealOrphan()
    {
        // Discover the actual default branch name from git config
        var (_, symRefOut, _) = await GitOperations.RunGitCommandAsync(
            _repoDir, "symbolic-ref --short HEAD", CancellationToken.None);
        var defaultBranch = symRefOut.Trim();

        // Arrange — create orphan branch on empty repo
        await GitOperations.CreateBranchAsync(_repoDir, "feature/test", defaultBranch, CancellationToken.None);

        // Add a file and commit so the branch actually appears in git refs
        await CommitFileAsync("orphan.txt", "orphan content");

        // Assert 1 — the branch now shows up in git branch --list
        var (_, branchList, _) = await GitOperations.RunGitCommandAsync(
            _repoDir, "branch --list feature/test", CancellationToken.None);
        Assert.Contains("feature/test", branchList);

        // Assert 2 — the commit has NO parent (key characteristic of an orphan branch)
        var (_, parentOutput, _) = await GitOperations.RunGitCommandAsync(
            _repoDir, "log --format=%P", CancellationToken.None);
        Assert.Equal(string.Empty, parentOutput.Trim());
    }

    [Fact]
    public async Task CreateBranchAsync_OnEmptyRepo_CreatesOrphanBranchWithNoParentCommits()
    {
        // Discover the actual default branch name from git config
        var (_, symRefOut, _) = await GitOperations.RunGitCommandAsync(
            _repoDir, "symbolic-ref --short HEAD", CancellationToken.None);
        var defaultBranch = symRefOut.Trim();

        // Act - create orphan branch on empty repo
        await GitOperations.CreateBranchAsync(_repoDir, "feature/test", defaultBranch, CancellationToken.None);

        // Assert - verify the branch has no parent commits (git log returns empty or error)
        var (_, stdout, _) = await GitOperations.RunGitCommandAsync(
            _repoDir, "log --oneline", CancellationToken.None);

        // On an orphan branch with no commits, git log should return empty output
        // (exit code may be non-zero, but stdout will be empty)
        Assert.Equal(string.Empty, stdout.Trim());
    }

    [Fact]
    public async Task CreateBranchAsync_OnEmptyRepo_CreatesCommitableOrphanBranch()
    {
        // Discover the actual default branch name from git config
        var (_, symRefOut, _) = await GitOperations.RunGitCommandAsync(
            _repoDir, "symbolic-ref --short HEAD", CancellationToken.None);
        var defaultBranch = symRefOut.Trim();

        // Arrange - create orphan branch on empty repo
        await GitOperations.CreateBranchAsync(_repoDir, "feature/test", defaultBranch, CancellationToken.None);

        // Act - add a file and commit on the orphan branch
        await CommitFileAsync("newfile.txt", "content");

        // Assert - verify the commit has no parents (orphan commit)
        var (_, stdout, _) = await GitOperations.RunGitCommandAsync(
            _repoDir, "log --format=%P", CancellationToken.None);

        // %P formats parent hashes; for an orphan commit, this should be empty
        Assert.Equal(string.Empty, stdout.Trim());
    }

    // ── CreateBranchAsync — non-empty repository ─────────────────────────────

    [Fact]
    public async Task CreateBranchAsync_OnNonEmptyRepo_CreatesFeatureBranch()
    {
        await CommitFileAsync("readme.md", "# Project");

        // Discover the actual default branch name set by this environment's git config
        var (_, symRefOut, _) = await GitOperations.RunGitCommandAsync(
            _repoDir, "symbolic-ref --short HEAD", CancellationToken.None);
        var defaultBranch = symRefOut.Trim();

        await GitOperations.CreateBranchAsync(_repoDir, "feature/new", defaultBranch, CancellationToken.None);

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
