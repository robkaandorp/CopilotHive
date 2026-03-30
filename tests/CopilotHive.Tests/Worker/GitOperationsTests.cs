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
    public async Task CreateBranchAsync_OnNonEmptyRepo_WhenBaseBranchMissingAndNoRemote_CreatesFromHead()
    {
        // Arrange — non-empty repo with no remote configured; base branch does not exist locally.
        await CommitFileAsync("readme.md", "# Project");

        // Act — should NOT throw; falls back to creating base branch from current HEAD.
        await GitOperations.CreateBranchAsync(
            _repoDir, "feature/x", "nonexistent-base", CancellationToken.None);

        // Assert — the feature branch was created successfully.
        var (_, stdout, _) = await GitOperations.RunGitCommandAsync(
            _repoDir, "branch --show-current", CancellationToken.None);
        Assert.Equal("feature/x", stdout.Trim());
    }

    [Fact]
    public async Task CreateBranchAsync_OnNonEmptyRepo_WhenBaseBranchMissingAndNoRemote_BaseCreatedFromHead()
    {
        // Arrange — two commits on the default branch.
        await CommitFileAsync("first.txt", "first");
        await CommitFileAsync("second.txt", "second");

        var (_, headBefore, _) = await GitOperations.RunGitCommandAsync(
            _repoDir, "rev-parse HEAD", CancellationToken.None);

        // Act — missing base, no remote; base branch is created from HEAD, then feature is branched off it.
        await GitOperations.CreateBranchAsync(
            _repoDir, "feature/from-head", "missing-base", CancellationToken.None);

        // Assert — feature/from-head points at the same commit as the original HEAD.
        var (_, headAfter, _) = await GitOperations.RunGitCommandAsync(
            _repoDir, "rev-parse HEAD", CancellationToken.None);
        Assert.Equal(headBefore.Trim(), headAfter.Trim());
    }

    [Fact]
    public async Task CreateBranchAsync_OnNonEmptyRepo_WhenBaseBranchFetchedFromOrigin_CreatesFeatureBranch()
    {
        // Arrange — set up a "remote" bare repo that has a commit on "main-remote".
        var bareDir = Path.Combine(Path.GetTempPath(), $"GitOpsBare_{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(bareDir);
            await RunAsync(bareDir, "init --bare");

            // Push a commit from the working repo to the bare remote as "main-remote".
            await CommitFileAsync("readme.md", "# Base");
            var (_, symRefOut, _) = await GitOperations.RunGitCommandAsync(
                _repoDir, "symbolic-ref --short HEAD", CancellationToken.None);
            var localDefault = symRefOut.Trim();

            await RunAsync(_repoDir, $"remote add origin {bareDir}");
            await RunAsync(_repoDir, $"push origin {localDefault}:main-remote");

            // Ensure "main-remote" does NOT exist locally.
            var (localCheckExit, _, _) = await GitOperations.RunGitCommandAsync(
                _repoDir, "rev-parse --verify main-remote", CancellationToken.None);
            Assert.NotEqual(0, localCheckExit);

            // Act — CreateBranchAsync should fetch "main-remote" from origin and then create the feature.
            await GitOperations.CreateBranchAsync(
                _repoDir, "feature/from-origin", "main-remote", CancellationToken.None);

            // Assert — landed on the feature branch.
            var (_, currentBranch, _) = await GitOperations.RunGitCommandAsync(
                _repoDir, "branch --show-current", CancellationToken.None);
            Assert.Equal("feature/from-origin", currentBranch.Trim());
        }
        finally
        {
            await GitOperations.ForceDeleteDirectoryAsync(bareDir);
        }
    }

    // ── GetGitStatusAsync ─────────────────────────────────────────────────────

    [Fact]
    public async Task GetGitStatusAsync_OrphanBranch_FallsBackToEmptyTree()
    {
        // Arrange — create an orphan branch with two files (no shared history with any base branch)
        var (_, symRefOut, _) = await GitOperations.RunGitCommandAsync(
            _repoDir, "symbolic-ref --short HEAD", CancellationToken.None);
        var defaultBranch = symRefOut.Trim();

        await GitOperations.CreateBranchAsync(_repoDir, "orphan-feature", defaultBranch, CancellationToken.None);
        await CommitFileAsync("alpha.txt", "line1\nline2\n");
        await CommitFileAsync("beta.txt", "a\nb\nc\n");

        // Act — use a base branch that shares no history so the three-dot diff fails;
        //        the empty-tree fallback should report both committed files.
        var summary = await GitOperations.GetGitStatusAsync(
            _repoDir, "nonexistent-base", CancellationToken.None);

        // Assert — empty-tree fallback should capture the two committed files
        Assert.Equal(2, summary.FilesChanged);
        Assert.True(summary.Insertions > 0, "Expected insertions > 0 from committed files");
    }

    [Fact]
    public async Task GetGitStatusAsync_NormalBranch_UsesMergeBaseDiff()
    {
        // Arrange — commit on the default branch, then branch off and add another file
        await CommitFileAsync("base.txt", "base content\n");

        var (_, symRefOut, _) = await GitOperations.RunGitCommandAsync(
            _repoDir, "symbolic-ref --short HEAD", CancellationToken.None);
        var defaultBranch = symRefOut.Trim();

        await GitOperations.CreateBranchAsync(_repoDir, "feature-normal", defaultBranch, CancellationToken.None);
        await CommitFileAsync("feature.txt", "feature content\n");

        // Act — pass null baseBranch so it falls back to HEAD~1 which should work for a non-orphan branch
        var summary = await GitOperations.GetGitStatusAsync(
            _repoDir, null, CancellationToken.None);

        // Assert — should see exactly the file added since HEAD~1
        Assert.Equal(1, summary.FilesChanged);
        Assert.True(summary.Insertions > 0);
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
