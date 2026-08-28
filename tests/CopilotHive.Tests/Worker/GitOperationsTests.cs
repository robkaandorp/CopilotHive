using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
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

    /// <summary>
    /// The changed-file list must contain the real repository-relative paths reported by
    /// <c>git diff --numstat</c> — including nested directories — not just basenames.
    /// </summary>
    [Fact]
    public async Task GetGitStatusAsync_CollectsRepositoryRelativeChangedPaths()
    {
        await CommitFileAsync("base.txt", "base content\n");

        var (_, symRefOut, _) = await GitOperations.RunGitCommandAsync(
            _repoDir, "symbolic-ref --short HEAD", CancellationToken.None);
        var defaultBranch = symRefOut.Trim();

        await GitOperations.CreateBranchAsync(_repoDir, "feature-paths", defaultBranch, CancellationToken.None);

        Directory.CreateDirectory(Path.Combine(_repoDir, "src", "Services"));
        await CommitFileAsync(Path.Combine("src", "Services", "Foo.cs").Replace('\\', '/'), "class Foo {}\n");

        var summary = await GitOperations.GetGitStatusAsync(_repoDir, null, CancellationToken.None);

        Assert.Equal(1, summary.FilesChanged);
        Assert.Contains("src/Services/Foo.cs", summary.ChangedFiles);
        // Path is repository-relative, never a bare basename
        Assert.DoesNotContain("Foo.cs", summary.ChangedFiles);
    }

    /// <summary>
    /// The orphan-branch empty-tree fallback must also collect changed-file paths.
    /// </summary>
    [Fact]
    public async Task GetGitStatusAsync_OrphanBranch_CollectsChangedPathsFromFallback()
    {
        var (_, symRefOut, _) = await GitOperations.RunGitCommandAsync(
            _repoDir, "symbolic-ref --short HEAD", CancellationToken.None);
        var defaultBranch = symRefOut.Trim();

        await GitOperations.CreateBranchAsync(_repoDir, "orphan-paths", defaultBranch, CancellationToken.None);
        await CommitFileAsync("alpha.txt", "line1\nline2\n");

        Directory.CreateDirectory(Path.Combine(_repoDir, "nested"));
        await CommitFileAsync(Path.Combine("nested", "beta.txt").Replace('\\', '/'), "a\nb\nc\n");

        var summary = await GitOperations.GetGitStatusAsync(
            _repoDir, "nonexistent-base", CancellationToken.None);

        Assert.Equal(2, summary.FilesChanged);
        Assert.Contains("alpha.txt", summary.ChangedFiles);
        Assert.Contains("nested/beta.txt", summary.ChangedFiles);
    }

    /// <summary>
    /// A GENUINE zero-diff: an empty commit on top of a real commit means nothing changed
    /// between HEAD~1 and HEAD. Both the count and the path list must be empty.
    /// </summary>
    [Fact]
    public async Task GetGitStatusAsync_NoChanges_ReturnsEmptyChangedFiles()
    {
        await CommitFileAsync("one.txt", "one\n");

        // An empty commit produces a truly empty diff against its parent.
        await RunAsync(_repoDir, "commit --allow-empty -m \"empty commit\"");

        // baseBranch null → diffs HEAD~1...HEAD, which is the empty commit vs its parent.
        var summary = await GitOperations.GetGitStatusAsync(_repoDir, null, CancellationToken.None);

        Assert.Equal(0, summary.FilesChanged);
        Assert.NotNull(summary.ChangedFiles);
        Assert.Empty(summary.ChangedFiles);
        Assert.Equal(0, summary.Insertions);
        Assert.Equal(0, summary.Deletions);
    }

    /// <summary>
    /// Filenames that plain <c>git diff --numstat</c> would C-quote (spaces, double quotes,
    /// backslashes, non-ASCII) must be captured verbatim by the NUL-delimited parser —
    /// with no surrounding quotes and no escape sequences.
    /// </summary>
    [Fact]
    public async Task GetGitStatusAsync_FilenamesWithSpecialCharacters_AreCapturedVerbatim()
    {
        await CommitFileAsync("seed.txt", "seed\n");

        Directory.CreateDirectory(Path.Combine(_repoDir, "dir with space"));

        // '"' and '\' are reserved characters that the Windows filesystem layer rejects
        // outright when creating a file (regardless of NUL-delimited git parsing), so they
        // are exercised only on platforms where they are legal path characters. Space,
        // non-ASCII, and single-quote characters are legal everywhere and still exercise
        // the same NUL-delimited/verbatim-capture parsing path.
        var specialNames = OperatingSystem.IsWindows()
            ? new[]
            {
                "dir with space/plain space.txt",
                "unicode-é-ünï.txt",
                "single'quote.txt",
            }
            : new[]
            {
                "dir with space/plain space.txt",
                "dir with space/qu\"ote.txt",
                "back\\slash.txt",
                "unicode-é-ünï.txt",
                "single'quote.txt",
            };

        foreach (var name in specialNames)
            await File.WriteAllTextAsync(Path.Combine(_repoDir, name), "content\n", TestContext.Current.CancellationToken);

        await RunAsync(_repoDir, "add -A");
        await RunAsync(_repoDir, "commit -m \"add special names\"");

        var summary = await GitOperations.GetGitStatusAsync(_repoDir, null, CancellationToken.None);

        Assert.Equal(specialNames.Length, summary.FilesChanged);
        foreach (var name in specialNames)
            Assert.Contains(name, summary.ChangedFiles);

        // No C-quoting artifacts leaked through: no path is wrapped in double quotes
        // and no backslash-escape sequence was introduced.
        Assert.DoesNotContain(summary.ChangedFiles, p => p.StartsWith('"') && p.EndsWith('"'));
        Assert.DoesNotContain(summary.ChangedFiles, p => p.Contains("\\\""));
        Assert.DoesNotContain(summary.ChangedFiles, p => p.Contains("\\303"));
    }

    /// <summary>
    /// A filename with a leading/trailing space is legal in Git. The parser must NOT trim it,
    /// otherwise the reported path no longer identifies the real file.
    /// </summary>
    [Fact]
    public async Task GetGitStatusAsync_FilenameWithLeadingTrailingWhitespace_IsNotTrimmed()
    {
        // Windows' own file APIs (including the git-for-Windows binary itself, which uses
        // standard Win32 path resolution to open files during `git add`) strip trailing
        // spaces from the final path component. The file can be created on disk via the
        // "\\?\" extended-length-path escape, but native git then fails to open it with
        // "No such file or directory" — a genuine OS/git limitation, not a parsing bug in
        // GitOperations. The scenario is therefore only exercisable on non-Windows platforms.
        if (OperatingSystem.IsWindows())
        {
            Assert.Skip("git-for-Windows cannot open/add a file whose name has a trailing space.");
            return;
        }

        await CommitFileAsync("seed.txt", "seed\n");

        const string paddedName = " padded name ";
        var paddedPath = Path.Combine(_repoDir, paddedName);

        await File.WriteAllTextAsync(paddedPath, "content\n", TestContext.Current.CancellationToken);
        await RunAsync(_repoDir, "add -A");
        await RunAsync(_repoDir, "commit -m \"add padded name\"");

        var summary = await GitOperations.GetGitStatusAsync(_repoDir, null, CancellationToken.None);

        Assert.Contains(paddedName, summary.ChangedFiles);
        Assert.DoesNotContain(paddedName.Trim(), summary.ChangedFiles);
    }

    /// <summary>
    /// A rename record must yield the NEW path, never Git's <c>old =&gt; new</c> display notation
    /// and never the old path.
    /// </summary>
    [Fact]
    public async Task GetGitStatusAsync_RenamedFile_CapturesNewPathNotArrowNotation()
    {
        // A sizeable file so Git's rename detection fires on the identical content.
        var body = string.Join("\n", Enumerable.Range(1, 50).Select(i => $"line {i} content here"));
        await CommitFileAsync("oldname.txt", body + "\n");

        Directory.CreateDirectory(Path.Combine(_repoDir, "sub"));
        await RunAsync(_repoDir, "mv oldname.txt sub/newname.txt");
        await RunAsync(_repoDir, "commit -m \"rename file\"");

        var summary = await GitOperations.GetGitStatusAsync(_repoDir, null, CancellationToken.None);

        Assert.Contains("sub/newname.txt", summary.ChangedFiles);
        Assert.DoesNotContain("oldname.txt", summary.ChangedFiles);
        Assert.DoesNotContain(summary.ChangedFiles, p => p.Contains("=>"));
        // Counts stay consistent with the recorded path list.
        Assert.Equal(1, summary.FilesChanged);
        Assert.Single(summary.ChangedFiles);
    }

    /// <summary>
    /// A rename mixed with ordinary modifications must not desynchronise the NUL-record cursor:
    /// every changed file is still reported exactly once with its correct path.
    /// </summary>
    [Fact]
    public async Task GetGitStatusAsync_RenameMixedWithEdits_ParsesAllRecords()
    {
        var body = string.Join("\n", Enumerable.Range(1, 50).Select(i => $"line {i} content here"));
        await CommitFileAsync("tomove.txt", body + "\n");
        await CommitFileAsync("stable.txt", "stable\n");

        await RunAsync(_repoDir, "mv tomove.txt moved.txt");
        await File.WriteAllTextAsync(Path.Combine(_repoDir, "stable.txt"), "stable edited\n", TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(Path.Combine(_repoDir, "brandnew.txt"), "new\n", TestContext.Current.CancellationToken);
        await RunAsync(_repoDir, "add -A");
        await RunAsync(_repoDir, "commit -m \"rename plus edits\"");

        var summary = await GitOperations.GetGitStatusAsync(_repoDir, null, CancellationToken.None);

        Assert.Contains("moved.txt", summary.ChangedFiles);
        Assert.Contains("stable.txt", summary.ChangedFiles);
        Assert.Contains("brandnew.txt", summary.ChangedFiles);
        Assert.DoesNotContain("tomove.txt", summary.ChangedFiles);
        Assert.DoesNotContain(summary.ChangedFiles, p => p.Contains("=>"));
        Assert.Equal(summary.FilesChanged, summary.ChangedFiles.Count);
    }

    // ── CloneRepositoryAsync ──────────────────────────────────────────────────

    [Fact]
    public async Task CloneRepositoryAsync_SetsLocalIdentityAndAllowsCommit()
    {
        // Arrange — create a bare remote repo with at least one commit.
        var bareDir = Path.Combine(Path.GetTempPath(), $"GitOpsBareClone_{Guid.NewGuid():N}");
        var cloneDir = Path.Combine(Path.GetTempPath(), $"GitOpsClone_{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(bareDir);
            await RunAsync(bareDir, "init --bare");

            // Seed the bare remote by creating a working repo, committing, and pushing.
            var seedDir = Path.Combine(Path.GetTempPath(), $"GitOpsSeed_{Guid.NewGuid():N}");
            try
            {
                Directory.CreateDirectory(seedDir);
                await RunAsync(seedDir, "init");
                await RunAsync(seedDir, "config user.email \"seed@example.com\"");
                await RunAsync(seedDir, "config user.name \"Seed\"");
                await File.WriteAllTextAsync(
                    Path.Combine(seedDir, "seed.txt"), "seed content", TestContext.Current.CancellationToken);
                await RunAsync(seedDir, "add seed.txt");
                await RunAsync(seedDir, "commit -m \"seed\"");
                await RunAsync(seedDir, $"remote add origin {bareDir}");
                await RunAsync(seedDir, "push origin HEAD");
            }
            finally
            {
                await GitOperations.ForceDeleteDirectoryAsync(seedDir);
            }

            // Act — clone the bare repo via the public API.
            await GitOperations.CloneRepositoryAsync(bareDir, cloneDir, CancellationToken.None);

            // Assert — local identity is set as expected.
            var (_, nameOut, _) = await GitOperations.RunGitCommandAsync(
                cloneDir, "config --local user.name", CancellationToken.None);
            Assert.Equal("CopilotHive", nameOut.Trim());

            var (_, emailOut, _) = await GitOperations.RunGitCommandAsync(
                cloneDir, "config --local user.email", CancellationToken.None);
            Assert.Equal("copilothive@local", emailOut.Trim());

            // Assert — we can make a commit in the clone without a global identity.
            await File.WriteAllTextAsync(
                Path.Combine(cloneDir, "file.txt"), "content", TestContext.Current.CancellationToken);
            var (addExit, _, addStderr) = await GitOperations.RunGitCommandAsync(
                cloneDir, "add -A", CancellationToken.None);
            Assert.Equal(0, addExit);

            var (commitExit, _, commitStderr) = await GitOperations.RunGitCommandAsync(
                cloneDir, "commit -m \"test\"", CancellationToken.None);
            Assert.Equal(0, commitExit);
        }
        finally
        {
            await GitOperations.ForceDeleteDirectoryAsync(cloneDir);
            await GitOperations.ForceDeleteDirectoryAsync(bareDir);
        }
    }

    [Fact]
    public async Task ConfigureLocalIdentity_NonexistentRepo_ThrowsGitOperationException()
    {
        var nonexistentDir = Path.Combine(Path.GetTempPath(), $"GitOpsMissing_{Guid.NewGuid():N}");

        var ex = await Assert.ThrowsAsync<GitOperationException>(
            () => GitOperations.ConfigureLocalIdentity(nonexistentDir, CancellationToken.None));

        Assert.Contains("Failed to set local user.email", ex.Message);
    }

    [Fact]
    public async Task ConfigureLocalIdentity_CancelledToken_ThrowsOperationCanceledException()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => GitOperations.ConfigureLocalIdentity(_repoDir, cts.Token));
    }

    // ── SanitizeChildEnv ──────────────────────────────────────────────────────

    [Fact]
    public void SanitizeChildEnv_RemovesTheFiveVariables_IncludingMixedCase()
    {
        var input = new Dictionary<string, string?>
        {
            ["GH_TOKEN"] = "gh-secret",
            ["github_token"] = "github-secret",
            ["Git_AskPass"] = "/usr/bin/askpass",
            ["GITHUB_CONFIG_REPO_TOKEN"] = "config-secret",
            ["giT_termInal_prompt"] = "1",
            ["PATH"] = "/usr/bin",
        };

        var result = GitOperations.SanitizeChildEnv(input);

        Assert.Equal(new[] { "PATH" }, result.Keys.Order().ToArray());
        Assert.Equal("/usr/bin", result["PATH"]);
    }

    [Fact]
    public void SanitizeChildEnv_PreservesEssentialInheritedVariables()
    {
        var input = new Dictionary<string, string?>
        {
            ["PATH"] = "/usr/local/bin:/usr/bin",
            ["HOME"] = "/home/hive",
            ["GH_TOKEN"] = "gh-secret",
            // Differently-cased, unrelated key: it is NOT one of the five and must survive.
            ["gh_token_backup_note"] = "not-a-secret-name",
        };

        var result = GitOperations.SanitizeChildEnv(input);

        Assert.Equal(
            new[] { "HOME", "PATH", "gh_token_backup_note" },
            result.Keys.Order(StringComparer.Ordinal).ToArray());
        Assert.Equal("/usr/local/bin:/usr/bin", result["PATH"]);
        Assert.Equal("/home/hive", result["HOME"]);
        Assert.Equal("not-a-secret-name", result["gh_token_backup_note"]);
    }

    [Fact]
    public void SanitizeChildEnv_ReturnsNewCopy_AndDoesNotMutateInput()
    {
        var input = new Dictionary<string, string?>
        {
            ["GH_TOKEN"] = "gh-secret",
            ["PATH"] = "/usr/bin",
        };

        var result = GitOperations.SanitizeChildEnv(input);

        // The input keeps every entry, including the scrubbed one.
        Assert.Equal(2, input.Count);
        Assert.Equal("gh-secret", input["GH_TOKEN"]);
        Assert.Equal("/usr/bin", input["PATH"]);

        // The result is a distinct dictionary: later input edits do not leak into it.
        Assert.NotSame(input, result);
        input["PATH"] = "/mutated";
        input["EXTRA"] = "added-later";
        Assert.Equal("/usr/bin", result["PATH"]);
        Assert.False(result.ContainsKey("EXTRA"));
    }

    [Fact]
    public void SanitizeChildEnv_NullValuedNonScrubbedEntry_IsPreservedAsNullMarker()
    {
        var input = new Dictionary<string, string?>
        {
            ["REMOVAL_MARKER"] = null,
            ["PATH"] = "/usr/bin",
        };

        var result = GitOperations.SanitizeChildEnv(input);

        Assert.Equal(new[] { "PATH", "REMOVAL_MARKER" }, result.Keys.Order(StringComparer.Ordinal).ToArray());
        Assert.True(result.ContainsKey("REMOVAL_MARKER"));
        Assert.Null(result["REMOVAL_MARKER"]);
    }

    [Fact]
    public void SanitizeChildEnv_NullValuedScrubbedVariables_AreRemoved()
    {
        var input = new Dictionary<string, string?>
        {
            ["GH_TOKEN"] = null,
            ["GITHUB_TOKEN"] = null,
            ["GIT_ASKPASS"] = null,
            ["GITHUB_CONFIG_REPO_TOKEN"] = null,
            ["GIT_TERMINAL_PROMPT"] = null,
            ["KEEP"] = "value",
        };

        var result = GitOperations.SanitizeChildEnv(input);

        Assert.Equal(new[] { "KEEP" }, result.Keys.Order(StringComparer.Ordinal).ToArray());
        Assert.Equal("value", result["KEEP"]);
    }

    // ── CreateProcessStartInfo (pure factory) ────────────────────────────────

    [Fact]
    public void CreateProcessStartInfo_ScrubsCredentialsAndForcesNoninteractivePrompt()
    {
        var request = new GitProcessRequest(
            "git",
            ["status --porcelain"],
            "/work/repo",
            new Dictionary<string, string?>
            {
                ["PATH"] = "/usr/bin",
                ["HOME"] = "/home/hive",
                ["GH_TOKEN"] = "gh-secret",
                ["GITHUB_TOKEN"] = "github-secret",
                ["GIT_ASKPASS"] = "/usr/bin/askpass",
                ["GITHUB_CONFIG_REPO_TOKEN"] = "config-secret",
                ["GIT_TERMINAL_PROMPT"] = "1",
                ["NULL_MARKER"] = null,
            });

        var psi = GitOperations.CreateProcessStartInfo(request);

        // Exact child environment: sanitized copy (null marker omitted) plus GIT_TERMINAL_PROMPT=0.
        Assert.Equal(
            new[] { "GIT_TERMINAL_PROMPT", "HOME", "PATH" },
            psi.Environment.Keys.Order(StringComparer.Ordinal).ToArray());
        Assert.Equal("0", psi.Environment["GIT_TERMINAL_PROMPT"]);
        Assert.Equal("/home/hive", psi.Environment["HOME"]);
        Assert.Equal("/usr/bin", psi.Environment["PATH"]);
    }

    [Fact]
    public void CreateProcessStartInfo_TokenizedArgs_AddsRawTokensIgnoresLegacyArgsAndCopiesExactEnvironment()
    {
        const string UnquotedToken = "message with spaces and a \"quoted\" word";
        string[] tokenizedArgs = ["commit", "-m", UnquotedToken, "--path=dir with spaces"];
        var environment = new Dictionary<string, string?>
        {
            ["GH_TOKEN"] = "gh-secret-must-survive",
            ["GITHUB_TOKEN"] = "github-secret-must-survive",
            ["GIT_ASKPASS"] = "/custom/askpass-must-survive",
            ["GITHUB_CONFIG_REPO_TOKEN"] = "config-secret-must-survive",
            ["GIT_TERMINAL_PROMPT"] = "1",
            ["KEEP_EXACT"] = "unchanged",
            ["NULL_MARKER"] = null,
        };
        var request = new GitProcessRequest(
            "git",
            ["legacy opaque argument must be ignored", "a second legacy argument must also be ignored"],
            "/work/repo",
            environment,
            tokenizedArgs);

        var psi = GitOperations.CreateProcessStartInfo(request);

        // Exact raw elements prove foreach/Add ordering and detect either whole-list or per-token quoting.
        Assert.Equal(tokenizedArgs, psi.ArgumentList.ToArray());
        Assert.Equal(UnquotedToken, psi.ArgumentList[2]);
        Assert.DoesNotContain($"\"{UnquotedToken}\"", psi.ArgumentList);

        // An invalid legacy count is accepted and its opaque values never reach either argument property.
        Assert.Equal(string.Empty, psi.Arguments);
        Assert.DoesNotContain("legacy opaque argument must be ignored", psi.ArgumentList);

        // Exact key/value equality proves no scrub and no forced additions; null is omitted on repopulation.
        Assert.Equal(
            new[]
            {
                "GH_TOKEN",
                "GITHUB_CONFIG_REPO_TOKEN",
                "GITHUB_TOKEN",
                "GIT_ASKPASS",
                "GIT_TERMINAL_PROMPT",
                "KEEP_EXACT",
            },
            psi.Environment.Keys.Order(StringComparer.Ordinal).ToArray());
        Assert.Equal("gh-secret-must-survive", psi.Environment["GH_TOKEN"]);
        Assert.Equal("github-secret-must-survive", psi.Environment["GITHUB_TOKEN"]);
        Assert.Equal("/custom/askpass-must-survive", psi.Environment["GIT_ASKPASS"]);
        Assert.Equal("config-secret-must-survive", psi.Environment["GITHUB_CONFIG_REPO_TOKEN"]);
        Assert.Equal("1", psi.Environment["GIT_TERMINAL_PROMPT"]);
        Assert.Equal("unchanged", psi.Environment["KEEP_EXACT"]);
        Assert.False(psi.Environment.ContainsKey("NULL_MARKER"));

        Assert.Equal("git", psi.FileName);
        Assert.Equal("/work/repo", psi.WorkingDirectory);
        Assert.True(psi.RedirectStandardOutput);
        Assert.True(psi.RedirectStandardError);
        Assert.False(psi.UseShellExecute);
        Assert.Equal(7, environment.Count);
        Assert.True(environment.ContainsKey("NULL_MARKER"));
        Assert.Null(environment["NULL_MARKER"]);
    }

    [Fact]
    public void CreateProcessStartInfo_AssignsArgumentsVerbatimAndSetsProcessProperties()
    {
        const string OpaqueArgs = "commit -m \"a message with spaces\"";
        var request = new GitProcessRequest(
            "git",
            [OpaqueArgs],
            "/work/repo",
            new Dictionary<string, string?> { ["PATH"] = "/usr/bin" });

        var psi = GitOperations.CreateProcessStartInfo(request);

        Assert.Null(request.TokenizedArgs);
        Assert.Equal("git", psi.FileName);
        Assert.Equal(OpaqueArgs, psi.Arguments);
        Assert.Empty(psi.ArgumentList);
        Assert.Equal("/work/repo", psi.WorkingDirectory);
        Assert.True(psi.RedirectStandardOutput);
        Assert.True(psi.RedirectStandardError);
        Assert.False(psi.UseShellExecute);
    }

    [Fact]
    public void CreateProcessStartInfo_DoesNotMutateRequestEnvironment()
    {
        var env = new Dictionary<string, string?>
        {
            ["GH_TOKEN"] = "gh-secret",
            ["PATH"] = "/usr/bin",
        };
        var request = new GitProcessRequest("git", ["status"], "/work/repo", env);

        _ = GitOperations.CreateProcessStartInfo(request);

        Assert.Equal(2, env.Count);
        Assert.Equal("gh-secret", env["GH_TOKEN"]);
        Assert.Equal("/usr/bin", env["PATH"]);
    }

    [Fact]
    public void CreateProcessStartInfo_ZeroArgs_ThrowsArgumentException()
    {
        var request = new GitProcessRequest(
            "git", [], "/work/repo", new Dictionary<string, string?>());

        var exception = Assert.Throws<ArgumentException>(
            () => GitOperations.CreateProcessStartInfo(request));

        Assert.Equal("request", exception.ParamName);
        Assert.Contains("Exactly one opaque argument string is required, got 0.", exception.Message);
    }

    [Fact]
    public void CreateProcessStartInfo_MultipleArgs_ThrowsArgumentException()
    {
        var request = new GitProcessRequest(
            "git", ["status", "--porcelain"], "/work/repo", new Dictionary<string, string?>());

        var exception = Assert.Throws<ArgumentException>(
            () => GitOperations.CreateProcessStartInfo(request));

        Assert.Equal("request", exception.ParamName);
        Assert.Contains("Exactly one opaque argument string is required, got 2.", exception.Message);
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

/// <summary>
/// Tests for the static <see cref="GitOperations.ProcessRunner"/> seam. These mutate static state
/// and process-wide environment variables, so they run in the non-parallel "EnvVarMutation"
/// collection and every assignment is restored in a <c>finally</c> block.
/// </summary>
[Collection("EnvVarMutation")]
public sealed class GitOperationsProcessRunnerSeamTests
{
    /// <summary>A working directory that does not exist — a real launch here would throw.</summary>
    private static readonly string NonexistentWorkDir =
        Path.Combine(Path.GetTempPath(), $"GitOpsNoSuchDir_{Guid.NewGuid():N}");

    [Fact]
    public async Task ExecuteProcessAsync_WithSeam_DelegatesExactRequestAndTokenWithoutOwningCancellation()
    {
        var originalRunner = GitOperations.ProcessRunner;
        using var callerCts = new CancellationTokenSource();
        var request = new GitProcessRequest(
            $"definitely-not-an-executable-{Guid.NewGuid():N}",
            [],
            NonexistentWorkDir,
            new Dictionary<string, string?> { ["RAW"] = "value" },
            ["token with spaces"]);
        var expectedResult = new GitProcessResult(73, "delegate stdout", "delegate stderr");
        var delegateEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var delegateCompletion = new TaskCompletionSource<GitProcessResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        GitProcessRequest? capturedRequest = null;
        CancellationToken capturedToken = default;

        try
        {
            GitOperations.ProcessRunner = (actualRequest, actualToken) =>
            {
                capturedRequest = actualRequest;
                capturedToken = actualToken;
                delegateEntered.TrySetResult();
                return delegateCompletion.Task;
            };

            var execution = GitOperations.ExecuteProcessAsync(request, callerCts.Token);
            await delegateEntered.Task.WaitAsync(
                TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

            Assert.Same(request, capturedRequest);
            Assert.Equal(callerCts.Token, capturedToken);
            Assert.False(Directory.Exists(NonexistentWorkDir));

            // Cancelling the caller does not make ExecuteProcessAsync kill, drain, dispose, or
            // otherwise complete a launch represented and owned entirely by the delegate.
            callerCts.Cancel();
            Assert.False(execution.IsCompleted);

            delegateCompletion.SetResult(expectedResult);
            var actualResult = await execution;
            Assert.Same(expectedResult, actualResult);
        }
        finally
        {
            GitOperations.ProcessRunner = originalRunner;
            delegateCompletion.TrySetResult(expectedResult);
        }
    }

    [Fact]
    public async Task ExecuteProcessAsync_NonexistentExecutable_PropagatesOriginalStartException()
    {
        var originalRunner = GitOperations.ProcessRunner;
        var executable = $"missing-executable-sensitive-marker-{Guid.NewGuid():N}";
        var request = new GitProcessRequest(
            executable,
            [],
            Path.GetTempPath(),
            new Dictionary<string, string?>(),
            []);

        try
        {
            GitOperations.ProcessRunner = null;

            var exception = await Assert.ThrowsAsync<Win32Exception>(
                () => GitOperations.ExecuteProcessAsync(
                    request, TestContext.Current.CancellationToken));

            // The concrete native type and unsanitized executable marker detect wrapping or rewriting.
            Assert.Contains(executable, exception.Message, StringComparison.Ordinal);
            Assert.Null(exception.InnerException);
        }
        finally
        {
            GitOperations.ProcessRunner = originalRunner;
        }
    }

    /// <summary>
    /// Proves the cancellation ORDER: kill REQUEST → root exit awaited → output drained → THEN
    /// <see cref="OperationCanceledException"/> with the caller's token.
    /// </summary>
    /// <remarks>
    /// The launched tree contains two extra processes:
    /// <list type="bullet">
    /// <item>an IN-TREE descendant (<c>sleep</c>) that <c>Kill(entireProcessTree: true)</c> must
    /// terminate — the descendant-kill evidence;</item>
    /// <item>an ESCAPED holder created by a double fork. Its intermediate parent exits
    /// immediately, so the holder is reparented away from the root and is therefore NOT part of
    /// the killed process tree. It inherits the redirected stdout/stderr write handles and blocks
    /// on a FIFO, so the production output reads CANNOT complete until this test releases it.</item>
    /// </list>
    /// The escape is established as a PRECONDITION to cancellation by walking the holder's full
    /// PPID ancestry and requiring that the root PID is no longer an ancestor. A weaker
    /// "PPID != rootPid" check would be vacuous: the holder is created by the INTERMEDIATE, so
    /// its PPID is never the root PID even in the pre-reparenting state root → intermediate →
    /// holder, where <c>Kill(entireProcessTree: true)</c> would still reach and kill it.
    /// That escaped holder is the deterministic ordering gate: after the root process has been
    /// reaped (its <c>/proc</c> entry is gone, which only happens once the production code awaited
    /// the root exit) the execution task MUST still be pending, because the drain is blocked. An
    /// implementation reduced to <c>Kill(entireProcessTree: true); throw new
    /// OperationCanceledException(ct);</c> completes at that point and FAILS the gate. Only after
    /// the FIFO release closes the inherited handles — completing the reads — may the exception
    /// surface. The bounded PID-absence polling afterwards is solely the declared OS-reaping
    /// allowance for the in-tree descendant AFTER propagation.
    /// </remarks>
    [Fact]
    public async Task ExecuteProcessAsync_Cancellation_KillsRootAndKnownDescendantBeforePropagation()
    {
        if (!OperatingSystem.IsLinux())
        {
            Assert.Skip("The known-PID process-tree evidence test requires Linux /proc semantics.");
            return;
        }

        var originalRunner = GitOperations.ProcessRunner;
        var tempDir = Path.Combine(Path.GetTempPath(), $"GitOpsTreeKill_{Guid.NewGuid():N}");
        var rootScriptPath = Path.Combine(tempDir, "spawn-tree.sh");
        var holderScriptPath = Path.Combine(tempDir, "spawn-holder.sh");
        var rootPidPath = Path.Combine(tempDir, "root.pid");
        var descendantPidPath = Path.Combine(tempDir, "descendant.pid");
        var holderPidPath = Path.Combine(tempDir, "holder.pid");
        var releaseFifoPath = Path.Combine(tempDir, "release.fifo");
        var rootPidGate = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var descendantPidGate = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var holderPidGate = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var callerCts = new CancellationTokenSource();
        Task<GitProcessResult>? execution = null;
        var rootPid = 0;
        var descendantPid = 0;
        var holderPid = 0;

        Directory.CreateDirectory(tempDir);
        await CreateFifoAsync(releaseFifoPath, TestContext.Current.CancellationToken);

        // $1 root.pid  $2 descendant.pid  $3 holder.pid  $4 release.fifo  $5 spawn-holder.sh
        await File.WriteAllTextAsync(
            rootScriptPath,
            """
            #!/bin/sh
            printf '%s\n' "$$" > "$1"
            /bin/sh "$5" "$3" "$4" &
            /bin/sh -c 'printf "%s\n" "$$" > "$1"; exec /bin/sleep 300' descendant "$2" &
            descendant_pid=$!
            wait "$descendant_pid"

            """,
            TestContext.Current.CancellationToken);

        // Double fork: this intermediate exits at once so the holder is reparented off the root
        // and escapes Kill(entireProcessTree: true) while still owning the redirected handles.
        // $1 holder.pid  $2 release.fifo
        await File.WriteAllTextAsync(
            holderScriptPath,
            """
            #!/bin/sh
            /bin/sh -c 'exec 3<>"$2"; printf "%s\n" "$$" > "$1"; IFS= read -r _ <&3' holder "$1" "$2" &
            exit 0

            """,
            TestContext.Current.CancellationToken);

        using var watcher = new FileSystemWatcher(tempDir, "*.pid")
        {
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,
            EnableRaisingEvents = true,
        };
        FileSystemEventHandler capturePids = (_, _) =>
        {
            TryCapturePid(rootPidPath, rootPidGate);
            TryCapturePid(descendantPidPath, descendantPidGate);
            TryCapturePid(holderPidPath, holderPidGate);
        };
        watcher.Created += capturePids;
        watcher.Changed += capturePids;

        try
        {
            GitOperations.ProcessRunner = null;
            var request = new GitProcessRequest(
                "/bin/sh",
                ["legacy arguments are ignored"],
                tempDir,
                new Dictionary<string, string?>(),
                [rootScriptPath, rootPidPath, descendantPidPath, holderPidPath, releaseFifoPath, holderScriptPath]);

            execution = GitOperations.ExecuteProcessAsync(request, callerCts.Token);
            capturePids(this, null!);

            rootPid = await rootPidGate.Task.WaitAsync(
                TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            descendantPid = await descendantPidGate.Task.WaitAsync(
                TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            holderPid = await holderPidGate.Task.WaitAsync(
                TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            Assert.NotEqual(rootPid, descendantPid);
            Assert.NotEqual(rootPid, holderPid);
            Assert.NotEqual(descendantPid, holderPid);
            Assert.True(IsLinuxPidPresent(rootPid));
            Assert.True(IsLinuxPidPresent(descendantPid));

            // PRECONDITION to cancellation: the holder must have fully ESCAPED the root's process
            // tree — the intermediate has exited and the holder has been reparented, so rootPid is
            // no longer anywhere in the holder's PPID ancestry. Checking only "PPID != rootPid"
            // would be vacuous, because the holder's parent is the INTERMEDIATE, never the root.
            // Without this gate a scheduler race could let Kill(entireProcessTree: true) reach the
            // still-in-tree holder, destroying the drain gate below.
            Assert.True(
                await WaitForLinuxTreeEscapeAsync(
                    holderPid, rootPid, TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken),
                $"Holder PID {holderPid} had not escaped the process tree of root PID {rootPid}.");
            Assert.True(
                IsLinuxPidPresent(holderPid),
                $"Holder PID {holderPid} exited before it could gate the output drain.");

            callerCts.Cancel();

            // ORDERING GATE. The root's /proc entry only disappears once the production code has
            // awaited (and thereby reaped) the root exit.
            Assert.True(
                await WaitForLinuxPidAbsenceAsync(
                    rootPid, TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken),
                $"Root PID {rootPid} was never awaited to exit after the kill request.");

            // The escaped holder still owns the redirected handles, so the output reads cannot
            // have completed. A production path that propagates before draining fails HERE.
            Assert.False(
                execution.IsCompleted,
                "Cancellation propagated before the redirected output reads were drained.");

            // Release the holder: it closes the inherited handles, the reads complete, and only
            // then may the OperationCanceledException surface.
            await ReleaseFifoAsync(releaseFifoPath, TestContext.Current.CancellationToken);

            var exception = await Assert.ThrowsAsync<OperationCanceledException>(
                () => execution.WaitAsync(
                    TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken));
            Assert.Equal(callerCts.Token, exception.CancellationToken);

            // Bounded polling is ONLY the declared OS-reaping allowance after propagation.
            await WaitForLinuxPidsAbsenceAsync(
                [descendantPid],
                TimeSpan.FromSeconds(5),
                TestContext.Current.CancellationToken);
            Assert.False(
                IsLinuxPidPresent(rootPid),
                $"Root PID {rootPid} remained present after cancellation propagated.");
            Assert.False(
                IsLinuxPidPresent(descendantPid),
                $"Descendant PID {descendantPid} remained present after cancellation propagated.");
        }
        finally
        {
            GitOperations.ProcessRunner = originalRunner;
            callerCts.Cancel();
            capturePids(this, null!);
            TryKillProcess(holderPid != 0 ? holderPid : GetCompletedPid(holderPidGate));
            TryKillProcess(rootPid != 0 ? rootPid : GetCompletedPid(rootPidGate));
            TryKillProcess(descendantPid != 0 ? descendantPid : GetCompletedPid(descendantPidGate));

            if (execution is not null)
            {
                try
                {
                    await execution.WaitAsync(
                        TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken);
                }
                catch (Exception) when (!TestContext.Current.CancellationToken.IsCancellationRequested)
                {
                    // Expected cancellation/start failures are already asserted by the test body.
                }
            }

            await GitOperations.ForceDeleteDirectoryAsync(tempDir);
        }
    }

    [Fact]
    public async Task ExecuteProcessAsync_RootExitedBeforeCancellation_ReturnsAnyExitCodeWithoutKillingDescendant()
    {
        if (!OperatingSystem.IsLinux())
        {
            Assert.Skip("The deterministic natural-exit gate uses Linux FIFOs and /proc.");
            return;
        }

        var originalRunner = GitOperations.ProcessRunner;
        var tempDir = Path.Combine(Path.GetTempPath(), $"GitOpsNaturalExit_{Guid.NewGuid():N}");
        var scriptPath = Path.Combine(tempDir, "exit-with-open-output.sh");
        var rootPidPath = Path.Combine(tempDir, "root.pid");
        var descendantPidPath = Path.Combine(tempDir, "descendant.pid");
        var releaseFifoPath = Path.Combine(tempDir, "release.fifo");
        var rootPidGate = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var descendantPidGate = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var callerCts = new CancellationTokenSource();
        Task<GitProcessResult>? execution = null;
        var descendantPid = 0;

        Directory.CreateDirectory(tempDir);
        await CreateFifoAsync(releaseFifoPath, TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(
            scriptPath,
            "#!/bin/sh\n" +
            "printf '%s\\n' \"$$\" > \"$1\"\n" +
            "/bin/sh -c 'exec 3<>\"$2\"; printf \"%s\\n\" \"$$\" > \"$1\"; IFS= read -r _ <&3' descendant \"$2\" \"$3\" &\n" +
            "exit 23\n",
            TestContext.Current.CancellationToken);

        using var watcher = new FileSystemWatcher(tempDir, "*.pid")
        {
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,
            EnableRaisingEvents = true,
        };
        FileSystemEventHandler capturePids = (_, _) =>
        {
            TryCapturePid(rootPidPath, rootPidGate);
            TryCapturePid(descendantPidPath, descendantPidGate);
        };
        watcher.Created += capturePids;
        watcher.Changed += capturePids;

        try
        {
            GitOperations.ProcessRunner = null;
            var request = new GitProcessRequest(
                "/bin/sh",
                [],
                tempDir,
                new Dictionary<string, string?>(),
                [scriptPath, rootPidPath, descendantPidPath, releaseFifoPath]);

            execution = GitOperations.ExecuteProcessAsync(request, callerCts.Token);
            TryCapturePid(rootPidPath, rootPidGate);
            TryCapturePid(descendantPidPath, descendantPidGate);

            var rootPid = await rootPidGate.Task.WaitAsync(
                TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            descendantPid = await descendantPidGate.Task.WaitAsync(
                TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            Assert.True(await WaitForLinuxPidAbsenceAsync(
                rootPid, TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
            Assert.True(IsLinuxPidPresent(descendantPid));
            Assert.False(execution.IsCompleted); // Descendant still owns the redirected output handles.

            callerCts.Cancel();
            Assert.True(IsLinuxPidPresent(descendantPid));

            await ReleaseFifoAsync(releaseFifoPath, TestContext.Current.CancellationToken);

            var result = await execution.WaitAsync(
                TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            Assert.Equal(23, result.ExitCode);
            Assert.Equal(string.Empty, result.Stdout);
            Assert.Equal(string.Empty, result.Stderr);
        }
        finally
        {
            GitOperations.ProcessRunner = originalRunner;
            callerCts.Cancel();
            TryKillProcess(descendantPid != 0 ? descendantPid : GetCompletedPid(descendantPidGate));

            if (execution is not null)
            {
                try
                {
                    await execution.WaitAsync(
                        TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
                }
                catch (Exception) when (!TestContext.Current.CancellationToken.IsCancellationRequested)
                {
                    // Cleanup is best-effort if an assertion interrupted the FIFO release.
                }
            }

            await GitOperations.ForceDeleteDirectoryAsync(tempDir);
        }
    }

    [Fact]
    public async Task RunGitCommandAsync_WithSeam_ReplacesEntireLaunchAndReceivesRawRequest()
    {
        const string OpaqueArgs = "commit -m \"seam message\"";
        var originalRunner = GitOperations.ProcessRunner;
        GitProcessRequest? captured = null;
        CancellationToken capturedToken = default;
        var gate = new TaskCompletionSource<GitProcessResult>(TaskCreationOptions.RunContinuationsAsynchronously);

        try
        {
            GitOperations.ProcessRunner = (request, token) =>
            {
                captured = request;
                capturedToken = token;
                return gate.Task;
            };

            var callTask = GitOperations.RunGitCommandAsync(
                NonexistentWorkDir, OpaqueArgs, TestContext.Current.CancellationToken);

            gate.SetResult(new GitProcessResult(42, "seam-stdout", "seam-stderr"));
            var (exitCode, stdout, stderr) = await callTask;

            // The delegate's result is returned verbatim — no real process was started.
            Assert.Equal(42, exitCode);
            Assert.Equal("seam-stdout", stdout);
            Assert.Equal("seam-stderr", stderr);
            Assert.False(Directory.Exists(NonexistentWorkDir));

            // The delegate received the PRE-factory request, not a ProcessStartInfo.
            Assert.NotNull(captured);
            Assert.Equal("git", captured!.Executable);
            Assert.Equal(NonexistentWorkDir, captured.WorkingDirectory);
            Assert.Equal(new[] { OpaqueArgs }, captured.Args.ToArray());
            Assert.Null(captured.TokenizedArgs);
            Assert.Equal(TestContext.Current.CancellationToken, capturedToken);
        }
        finally
        {
            GitOperations.ProcessRunner = originalRunner;
            gate.TrySetResult(new GitProcessResult(42, "seam-stdout", "seam-stderr"));
        }
    }

    [Fact]
    public async Task RunGitCommandAsync_WithSeam_ReceivesRawUnsanitizedEnvironmentSnapshot()
    {
        var originalRunner = GitOperations.ProcessRunner;
        var originalGhToken = Environment.GetEnvironmentVariable("GH_TOKEN");
        var originalAskpass = Environment.GetEnvironmentVariable("GIT_ASKPASS");
        GitProcessRequest? captured = null;

        try
        {
            Environment.SetEnvironmentVariable("GH_TOKEN", "raw-gh-secret");
            Environment.SetEnvironmentVariable("GIT_ASKPASS", "/usr/bin/askpass");

            GitOperations.ProcessRunner = (request, _) =>
            {
                captured = request;
                return Task.FromResult(new GitProcessResult(0, string.Empty, string.Empty));
            };

            await GitOperations.RunGitCommandAsync(
                NonexistentWorkDir, "status", TestContext.Current.CancellationToken);

            // Raw (pre-factory) environment: still carries the credential variables.
            Assert.NotNull(captured);
            Assert.Equal("raw-gh-secret", captured!.Env["GH_TOKEN"]);
            Assert.Equal("/usr/bin/askpass", captured.Env["GIT_ASKPASS"]);

            // The factory is what strips them — applying it to the captured request proves the
            // seam is upstream of the scrub.
            var psi = GitOperations.CreateProcessStartInfo(captured);
            Assert.False(psi.Environment.ContainsKey("GH_TOKEN"));
            Assert.False(psi.Environment.ContainsKey("GIT_ASKPASS"));
            Assert.Equal("0", psi.Environment["GIT_TERMINAL_PROMPT"]);

            // The process environment itself was not mutated by the snapshot.
            Assert.Equal("raw-gh-secret", Environment.GetEnvironmentVariable("GH_TOKEN"));
        }
        finally
        {
            GitOperations.ProcessRunner = originalRunner;
            Environment.SetEnvironmentVariable("GH_TOKEN", originalGhToken);
            Environment.SetEnvironmentVariable("GIT_ASKPASS", originalAskpass);
        }
    }

    [Fact]
    public async Task RunGitCommandAsync_WithSeam_ForwardsCallerCancellationToken()
    {
        var originalRunner = GitOperations.ProcessRunner;
        using var cts = new CancellationTokenSource();
        CancellationToken capturedToken = default;

        try
        {
            GitOperations.ProcessRunner = (_, ct) =>
            {
                capturedToken = ct;
                return Task.FromResult(new GitProcessResult(0, string.Empty, string.Empty));
            };

            await GitOperations.RunGitCommandAsync(NonexistentWorkDir, "status", cts.Token);

            Assert.Equal(cts.Token, capturedToken);
            Assert.NotEqual(CancellationToken.None, capturedToken);
        }
        finally
        {
            GitOperations.ProcessRunner = originalRunner;
        }
    }

    [Fact]
    public async Task RunGitCommandAsync_AfterSeamRestored_UsesRealGitProcessAgain()
    {
        var originalRunner = GitOperations.ProcessRunner;
        var invoked = 0;
        var workDir = Path.Combine(Path.GetTempPath(), $"GitOpsSeamRestore_{Guid.NewGuid():N}");
        Directory.CreateDirectory(workDir);

        try
        {
            try
            {
                GitOperations.ProcessRunner = (_, _) =>
                {
                    invoked++;
                    return Task.FromResult(new GitProcessResult(7, "fake", string.Empty));
                };

                var (seamExit, seamOut, _) = await GitOperations.RunGitCommandAsync(
                    workDir, "--version", TestContext.Current.CancellationToken);
                Assert.Equal(7, seamExit);
                Assert.Equal("fake", seamOut);
            }
            finally
            {
                GitOperations.ProcessRunner = null;
            }

            // Restoration is observable: the seam is null and the real git CLI runs.
            Assert.Null(GitOperations.ProcessRunner);

            var (exitCode, stdout, _) = await GitOperations.RunGitCommandAsync(
                workDir, "--version", TestContext.Current.CancellationToken);
            Assert.Equal(0, exitCode);
            Assert.Contains("git version", stdout);
            Assert.Equal(1, invoked);
        }
        finally
        {
            GitOperations.ProcessRunner = originalRunner;
            await GitOperations.ForceDeleteDirectoryAsync(workDir);
        }
    }

    private static void TryCapturePid(string path, TaskCompletionSource<int> gate)
    {
        if (gate.Task.IsCompleted)
            return;

        try
        {
            if (File.Exists(path) &&
                int.TryParse(File.ReadAllText(path).Trim(), out var pid) &&
                pid > 1)
            {
                gate.TrySetResult(pid);
            }
        }
        catch (IOException)
        {
            // A Created notification may run before the process has flushed the short PID file;
            // the Changed notification or explicit post-launch probe retries the file gate.
        }
        catch (UnauthorizedAccessException)
        {
            // Treat a transient file-access race exactly like a not-yet-open gate.
        }
    }

    private static int GetCompletedPid(TaskCompletionSource<int> gate) =>
        gate.Task.IsCompletedSuccessfully ? gate.Task.Result : 0;

    private static bool IsLinuxPidPresent(int pid) =>
        pid > 1 && Directory.Exists($"/proc/{pid}");

    /// <summary>
    /// Reads the parent PID of <paramref name="pid"/> from <c>/proc/{pid}/stat</c>, or null when
    /// the process is absent or the record cannot be parsed.
    /// </summary>
    /// <remarks>
    /// Field 4 of the record is the PPID, but field 2 (<c>comm</c>) is parenthesised and may
    /// itself contain spaces or parentheses, so parsing starts after the LAST <c>')'</c>.
    /// </remarks>
    private static int? TryGetLinuxParentPid(int pid)
    {
        try
        {
            var stat = File.ReadAllText($"/proc/{pid}/stat");
            var commEnd = stat.LastIndexOf(')');
            if (commEnd < 0)
                return null;

            // After "(comm)" the remaining fields are: state ppid ...
            var fields = stat[(commEnd + 1)..]
                .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            return fields.Length >= 2 && int.TryParse(fields[1], out var ppid) ? ppid : null;
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// Returns true when <paramref name="ancestorPid"/> is NOT reachable by walking the PPID chain
    /// upwards from <paramref name="pid"/> — i.e. <paramref name="pid"/> has escaped that process
    /// tree and <c>Kill(entireProcessTree: true)</c> on the ancestor can no longer reach it.
    /// </summary>
    /// <remarks>
    /// Walks to PID 1 (or to a vanished/unreadable ancestor). The hop budget bounds the walk
    /// against a pathological or racing chain; it never loops indefinitely.
    /// </remarks>
    private static bool HasEscapedLinuxProcessTree(int pid, int ancestorPid)
    {
        const int MaxHops = 64;

        var current = pid;
        for (var hop = 0; hop < MaxHops; hop++)
        {
            if (current == ancestorPid)
                return false; // Still inside the ancestor's tree.

            if (TryGetLinuxParentPid(current) is not { } parent || parent <= 1)
                return true; // Reached init/kernel or an unreadable ancestor without meeting it.

            current = parent;
        }

        return true;
    }

    /// <summary>
    /// Polls until <paramref name="pid"/> has escaped the process tree rooted at
    /// <paramref name="ancestorPid"/> (the double-fork reparent completed), returning false if the
    /// bounded deadline expires.
    /// </summary>
    private static async Task<bool> WaitForLinuxTreeEscapeAsync(
        int pid,
        int ancestorPid,
        TimeSpan deadline,
        CancellationToken testToken)
    {
        if (HasEscapedLinuxProcessTree(pid, ancestorPid))
            return true;

        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(100));
        using var deadlineCts = CancellationTokenSource.CreateLinkedTokenSource(testToken);
        deadlineCts.CancelAfter(deadline);

        try
        {
            while (await timer.WaitForNextTickAsync(deadlineCts.Token))
            {
                if (HasEscapedLinuxProcessTree(pid, ancestorPid))
                    return true;
            }
        }
        catch (OperationCanceledException) when (!testToken.IsCancellationRequested)
        {
            // Bounded deadline expired — the caller asserts the failure.
        }

        return false;
    }

    private static async Task<bool> WaitForLinuxPidAbsenceAsync(
        int pid,
        TimeSpan deadline,
        CancellationToken testToken)
    {
        await WaitForLinuxPidsAbsenceAsync([pid], deadline, testToken);
        return !IsLinuxPidPresent(pid);
    }

    private static async Task WaitForLinuxPidsAbsenceAsync(
        IReadOnlyList<int> pids,
        TimeSpan deadline,
        CancellationToken testToken)
    {
        if (pids.All(pid => !IsLinuxPidPresent(pid)))
            return;

        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(100));
        using var deadlineCts = CancellationTokenSource.CreateLinkedTokenSource(testToken);
        deadlineCts.CancelAfter(deadline);

        try
        {
            while (await timer.WaitForNextTickAsync(deadlineCts.Token))
            {
                if (pids.All(pid => !IsLinuxPidPresent(pid)))
                    return;
            }
        }
        catch (OperationCanceledException) when (!testToken.IsCancellationRequested)
        {
            // The single bounded OS-reaping allowance expired.
        }
    }

    private static void TryKillProcess(int pid)
    {
        if (pid <= 1)
            return;

        try
        {
            using var process = Process.GetProcessById(pid);
            process.Kill(entireProcessTree: true);
        }
        catch (ArgumentException)
        {
            // Already absent.
        }
        catch (InvalidOperationException)
        {
            // Already exited.
        }
        catch (Win32Exception)
        {
            // Best-effort cleanup only; the assertions report any contract failure first.
        }
    }

    private static async Task CreateFifoAsync(string path, CancellationToken testToken)
    {
        var startInfo = new ProcessStartInfo("/usr/bin/mkfifo")
        {
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        startInfo.ArgumentList.Add(path);

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Failed to start mkfifo for the natural-exit test gate.");
        var stderrTask = process.StandardError.ReadToEndAsync(testToken);
        await process.WaitForExitAsync(testToken);
        var stderr = await stderrTask;
        Assert.Equal(0, process.ExitCode);
        Assert.Equal(string.Empty, stderr);
    }

    /// <summary>
    /// Writes one line into the FIFO, unblocking the reader that holds the redirected handles.
    /// </summary>
    private static async Task ReleaseFifoAsync(string path, CancellationToken testToken)
    {
        await using var release = new FileStream(
            path,
            new FileStreamOptions
            {
                Mode = FileMode.Open,
                Access = FileAccess.Write,
                Share = FileShare.ReadWrite,
                Options = FileOptions.Asynchronous,
            });
        await using var writer = new StreamWriter(release);
        await writer.WriteLineAsync("release".AsMemory(), testToken);
        await writer.FlushAsync(testToken);
    }
}
