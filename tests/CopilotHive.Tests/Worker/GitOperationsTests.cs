using CopilotHive.Worker;

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

    [Fact]
    public async Task CreateBranchAsync_OnNonEmptyRepo_WhenBaseBranchMissingLocally_ExistsOnRemote_FetchesAndCreatesTrackingBranch()
    {
        // Arrange — set up a remote bare repo with a distinct commit on a branch that doesn't exist locally.
        var bareDir = Path.Combine(Path.GetTempPath(), $"GitOpsBare_{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(bareDir);
            await RunAsync(bareDir, "init --bare");

            // Create a commit on the local default branch, push to remote as "remote-base".
            await CommitFileAsync("initial.txt", "initial content");
            var (_, symRefOut, _) = await GitOperations.RunGitCommandAsync(
                _repoDir, "symbolic-ref --short HEAD", CancellationToken.None);
            var localDefault = symRefOut.Trim();

            await RunAsync(_repoDir, $"remote add origin {bareDir}");
            await RunAsync(_repoDir, $"push origin {localDefault}:remote-base");

            // Get the commit hash from the remote for later verification.
            var (_, remoteCommit, _) = await GitOperations.RunGitCommandAsync(
                _repoDir, $"rev-parse {localDefault}", CancellationToken.None);
            var expectedCommitHash = remoteCommit.Trim();

            // Create a second commit locally so HEAD diverges from the remote.
            await CommitFileAsync("local-only.txt", "local changes");

            // Verify "remote-base" does NOT exist locally before the operation.
            var (localCheckExit, _, _) = await GitOperations.RunGitCommandAsync(
                _repoDir, "rev-parse --verify remote-base", CancellationToken.None);
            Assert.NotEqual(0, localCheckExit);

            // Act — CreateBranchAsync should fetch "remote-base" from origin, create a local tracking branch,
            //       and then create the feature branch from it.
            await GitOperations.CreateBranchAsync(
                _repoDir, "feature/remote-based", "remote-base", CancellationToken.None);

            // Assert 1 — landed on the feature branch.
            var (_, currentBranch, _) = await GitOperations.RunGitCommandAsync(
                _repoDir, "branch --show-current", CancellationToken.None);
            Assert.Equal("feature/remote-based", currentBranch.Trim());

            // Assert 2 — the base branch "remote-base" now exists locally as a tracking branch.
            var (baseBranchExit, baseBranchList, _) = await GitOperations.RunGitCommandAsync(
                _repoDir, "branch --list remote-base", CancellationToken.None);
            Assert.Equal(0, baseBranchExit);
            Assert.Contains("remote-base", baseBranchList);

            // Assert 3 — "remote-base" points to the commit from the remote (not the local HEAD).
            var (_, baseCommit, _) = await GitOperations.RunGitCommandAsync(
                _repoDir, "rev-parse remote-base", CancellationToken.None);
            Assert.Equal(expectedCommitHash, baseCommit.Trim());

            // Assert 4 — the feature branch points to the same commit as the remote base branch.
            var (_, featureCommit, _) = await GitOperations.RunGitCommandAsync(
                _repoDir, "rev-parse feature/remote-based", CancellationToken.None);
            Assert.Equal(expectedCommitHash, featureCommit.Trim());

            // Assert 5 — git operations from a fresh process see the same state (simulating "fresh manager" pattern).
            // Re-run git commands to verify the repository state is persisted on disk.
            var (_, freshBranchCheck, _) = await GitOperations.RunGitCommandAsync(
                _repoDir, "branch --show-current", CancellationToken.None);
            Assert.Equal("feature/remote-based", freshBranchCheck.Trim());
        }
        finally
        {
            await GitOperations.ForceDeleteDirectoryAsync(bareDir);
        }
    }

    [Fact]
    public async Task CreateBranchAsync_OnNonEmptyRepo_WhenBaseBranchExistsNowhere_CreatesBaseFromHeadAndFeatureBranch()
    {
        // Arrange — create commits on the default branch, no remote configured.
        await CommitFileAsync("first.txt", "first commit content");
        await CommitFileAsync("second.txt", "second commit content");

        var (_, headCommitBefore, _) = await GitOperations.RunGitCommandAsync(
            _repoDir, "rev-parse HEAD", CancellationToken.None);
        var headHash = headCommitBefore.Trim();

        // Verify the base branch "missing-base" does NOT exist.
        var (baseCheckExit, _, _) = await GitOperations.RunGitCommandAsync(
            _repoDir, "rev-parse --verify missing-base", CancellationToken.None);
        Assert.NotEqual(0, baseCheckExit);

        // Act — CreateBranchAsync should create "missing-base" from HEAD, then create feature branch.
        await GitOperations.CreateBranchAsync(
            _repoDir, "feature/fresh-base", "missing-base", CancellationToken.None);

        // Assert 1 — landed on the feature branch.
        var (_, currentBranch, _) = await GitOperations.RunGitCommandAsync(
            _repoDir, "branch --show-current", CancellationToken.None);
        Assert.Equal("feature/fresh-base", currentBranch.Trim());

        // Assert 2 — the base branch "missing-base" was created.
        var (_, baseBranchList, _) = await GitOperations.RunGitCommandAsync(
            _repoDir, "branch --list missing-base", CancellationToken.None);
        Assert.Contains("missing-base", baseBranchList);

        // Assert 3 — the base branch points to the original HEAD commit.
        var (_, baseCommit, _) = await GitOperations.RunGitCommandAsync(
            _repoDir, "rev-parse missing-base", CancellationToken.None);
        Assert.Equal(headHash, baseCommit.Trim());

        // Assert 4 — the feature branch points to the same commit as the base branch.
        var (_, featureCommit, _) = await GitOperations.RunGitCommandAsync(
            _repoDir, "rev-parse feature/fresh-base", CancellationToken.None);
        Assert.Equal(headHash, featureCommit.Trim());

        // Assert 5 — verify repository state persists (simulating "fresh manager" verification).
        // A fresh process reading the repository should see both branches.
        var (_, allBranches, _) = await GitOperations.RunGitCommandAsync(
            _repoDir, "branch --list", CancellationToken.None);
        Assert.Contains("missing-base", allBranches);
        Assert.Contains("feature/fresh-base", allBranches);
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
