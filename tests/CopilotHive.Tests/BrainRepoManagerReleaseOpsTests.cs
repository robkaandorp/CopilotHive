using CopilotHive.Git;
using Microsoft.Extensions.Logging.Abstractions;
using System.Diagnostics;

namespace CopilotHive.Tests;

/// <summary>
/// Tests for the release git operations on <see cref="BrainRepoManager"/>:
/// <see cref="BrainRepoManager.MergeBranchAsync"/>, <see cref="BrainRepoManager.CreateTagAsync"/>,
/// and <see cref="BrainRepoManager.DeleteTagAsync"/>.
/// </summary>
public sealed class BrainRepoManagerReleaseOpsTests : IDisposable
{
    private readonly string _tempDir;

    public BrainRepoManagerReleaseOpsTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            TestHelpers.ForceDeleteDirectory(_tempDir);
    }

    // ---------- MergeBranchAsync ----------

    [Fact]
    public async Task MergeBranchAsync_SuccessfulMerge_ReturnsPostMergeSha()
    {
        var ct = TestContext.Current.CancellationToken;
        var (remoteDir, clonePath, manager) = SetupRepo("merge-repo");

        // Create a feature branch on the remote with a new commit.
        CreateBranchWithCommit(remoteDir, "main", "feature", "feature.txt", "feature content");

        var sha = await manager.MergeBranchAsync("merge-repo", "feature", "main", ct);

        Assert.NotNull(sha);
        // The pushed main tip should match the returned SHA.
        var remoteMainSha = GitOutput(clonePath, "rev-parse", "origin/main").Trim();
        // Refresh the clone's view of origin.
        Git(clonePath, "fetch", "origin");
        remoteMainSha = GitOutput(clonePath, "rev-parse", "origin/main").Trim();
        Assert.Equal(sha, remoteMainSha);
    }

    [Fact]
    public async Task MergeBranchAsync_NoOpMerge_ReturnsNull()
    {
        var ct = TestContext.Current.CancellationToken;
        var (remoteDir, _, manager) = SetupRepo("noop-repo");

        // Create a feature branch pointing at the same commit as main (no new commits).
        Git(remoteDir, "branch", "feature", "main");

        var sha = await manager.MergeBranchAsync("noop-repo", "feature", "main", ct);

        Assert.Null(sha);
    }

    [Fact]
    public async Task MergeBranchAsync_MergeConflict_ThrowsMergeConflictException()
    {
        var ct = TestContext.Current.CancellationToken;
        var (remoteDir, _, manager) = SetupRepo("conflict-repo");

        // Create conflicting changes on both branches on the same file.
        var staging = Path.Combine(_tempDir, "staging-conflict");
        Git(_tempDir, "clone", remoteDir, "staging-conflict");
        ConfigureIdentity(staging);

        // Change on main.
        File.WriteAllText(Path.Combine(staging, "conflict.txt"), "main change\n");
        Git(staging, "add", "conflict.txt");
        Git(staging, "commit", "-m", "main change");
        Git(staging, "push", "origin", "main");

        // Create feature from the earlier main and add a conflicting change.
        Git(staging, "checkout", "-b", "feature", "HEAD~1");
        File.WriteAllText(Path.Combine(staging, "conflict.txt"), "feature change\n");
        Git(staging, "add", "conflict.txt");
        Git(staging, "commit", "-m", "feature change");
        Git(staging, "push", "origin", "feature");

        var ex = await Assert.ThrowsAsync<MergeConflictException>(() =>
            manager.MergeBranchAsync("conflict-repo", "feature", "main", ct));

        Assert.Equal("conflict-repo", ex.RepoName);
        Assert.Equal("feature", ex.SourceBranch);
        Assert.Equal("main", ex.TargetBranch);
    }

    [Fact]
    public async Task MergeBranchAsync_MissingClone_ThrowsInvalidOperationException()
    {
        var ct = TestContext.Current.CancellationToken;
        var manager = new BrainRepoManager(_tempDir, NullLogger<BrainRepoManager>.Instance);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            manager.MergeBranchAsync("nonexistent", "feature", "main", ct));
    }

    [Fact]
    public async Task MergeBranchAsync_MissingTargetBranch_ThrowsInvalidOperationException()
    {
        var ct = TestContext.Current.CancellationToken;
        var (remoteDir, _, manager) = SetupRepo("missing-target");

        CreateBranchWithCommit(remoteDir, "main", "feature", "f.txt", "f");

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            manager.MergeBranchAsync("missing-target", "feature", "does-not-exist", ct));
    }

    [Fact]
    public async Task MergeBranchAsync_MissingSourceBranch_ThrowsInvalidOperationException()
    {
        var ct = TestContext.Current.CancellationToken;
        var (_, _, manager) = SetupRepo("missing-source");

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            manager.MergeBranchAsync("missing-source", "does-not-exist", "main", ct));
    }

    [Fact]
    public async Task MergeBranchAsync_DirtyWorktree_CleansBeforeMerge()
    {
        var ct = TestContext.Current.CancellationToken;
        var (remoteDir, clonePath, manager) = SetupRepo("dirty-repo");

        CreateBranchWithCommit(remoteDir, "main", "feature", "feature.txt", "feature content");

        // Introduce uncommitted changes and untracked files in the clone.
        File.WriteAllText(Path.Combine(clonePath, "README.md"), "dirty modification\n");
        File.WriteAllText(Path.Combine(clonePath, "untracked.txt"), "junk\n");

        var sha = await manager.MergeBranchAsync("dirty-repo", "feature", "main", ct);

        Assert.NotNull(sha);
    }

    // ---------- CreateTagAsync ----------

    [Fact]
    public async Task CreateTagAsync_Success_ReturnsTrue()
    {
        var ct = TestContext.Current.CancellationToken;
        var (remoteDir, _, manager) = SetupRepo("tag-repo");

        var result = await manager.CreateTagAsync("tag-repo", "v1.0.0", "main", "Release v1.0.0", ct);

        Assert.True(result);
        var remoteTags = GitOutput(remoteDir, "tag", "-l");
        Assert.Contains("v1.0.0", remoteTags);
    }

    [Fact]
    public async Task CreateTagAsync_PreExistingRemoteTag_ReturnsFalse()
    {
        var ct = TestContext.Current.CancellationToken;
        var (remoteDir, _, manager) = SetupRepo("pretag-repo");

        // Create the tag directly on the remote via a staging clone.
        var staging = Path.Combine(_tempDir, "staging-pretag");
        Git(_tempDir, "clone", remoteDir, "staging-pretag");
        ConfigureIdentity(staging);
        Git(staging, "tag", "v1.0.0");
        Git(staging, "push", "origin", "v1.0.0");

        var result = await manager.CreateTagAsync("pretag-repo", "v1.0.0", "main", "Release v1.0.0", ct);

        Assert.False(result);
    }

    [Fact]
    public async Task CreateTagAsync_MissingClone_ThrowsInvalidOperationException()
    {
        var ct = TestContext.Current.CancellationToken;
        var manager = new BrainRepoManager(_tempDir, NullLogger<BrainRepoManager>.Instance);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            manager.CreateTagAsync("nonexistent", "v1.0.0", "main", "msg", ct));
    }

    [Fact]
    public async Task CreateTagAsync_MissingBranch_ThrowsInvalidOperationException()
    {
        var ct = TestContext.Current.CancellationToken;
        var (_, _, manager) = SetupRepo("tag-missing-branch");

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            manager.CreateTagAsync("tag-missing-branch", "v1.0.0", "does-not-exist", "msg", ct));
    }

    // ---------- DeleteTagAsync ----------

    [Fact]
    public async Task DeleteTagAsync_ExistsBothLocalAndRemote_ReturnsTrue()
    {
        var ct = TestContext.Current.CancellationToken;
        var (remoteDir, clonePath, manager) = SetupRepo("del-both");

        // Push a tag to remote and ensure it's present locally.
        Git(clonePath, "tag", "v1.0.0");
        Git(clonePath, "push", "origin", "v1.0.0");

        var result = await manager.DeleteTagAsync("del-both", "v1.0.0", ct);

        Assert.True(result);
        Assert.DoesNotContain("v1.0.0", GitOutput(remoteDir, "tag", "-l"));
        Assert.DoesNotContain("v1.0.0", GitOutput(clonePath, "tag", "-l"));
    }

    [Fact]
    public async Task DeleteTagAsync_ExistsOnlyRemotely_ReturnsTrue()
    {
        var ct = TestContext.Current.CancellationToken;
        var (remoteDir, clonePath, manager) = SetupRepo("del-remote");

        // Create the tag on remote only (via staging), not in the manager clone.
        var staging = Path.Combine(_tempDir, "staging-del-remote");
        Git(_tempDir, "clone", remoteDir, "staging-del-remote");
        ConfigureIdentity(staging);
        Git(staging, "tag", "v1.0.0");
        Git(staging, "push", "origin", "v1.0.0");

        // Ensure the manager clone does not have the tag locally.
        Assert.DoesNotContain("v1.0.0", GitOutput(clonePath, "tag", "-l"));

        var result = await manager.DeleteTagAsync("del-remote", "v1.0.0", ct);

        Assert.True(result);
        Assert.DoesNotContain("v1.0.0", GitOutput(remoteDir, "tag", "-l"));
    }

    [Fact]
    public async Task DeleteTagAsync_ExistsOnlyLocally_ReturnsTrue()
    {
        var ct = TestContext.Current.CancellationToken;
        var (remoteDir, clonePath, manager) = SetupRepo("del-local");

        // Create a local-only tag (not pushed).
        Git(clonePath, "tag", "v1.0.0");
        Assert.DoesNotContain("v1.0.0", GitOutput(remoteDir, "tag", "-l"));

        var result = await manager.DeleteTagAsync("del-local", "v1.0.0", ct);

        Assert.True(result);
        Assert.DoesNotContain("v1.0.0", GitOutput(clonePath, "tag", "-l"));
    }

    [Fact]
    public async Task DeleteTagAsync_DoesNotExist_ReturnsFalse()
    {
        var ct = TestContext.Current.CancellationToken;
        var (_, _, manager) = SetupRepo("del-none");

        var result = await manager.DeleteTagAsync("del-none", "v9.9.9", ct);

        Assert.False(result);
    }

    [Fact]
    public async Task DeleteTagAsync_MissingClone_ThrowsInvalidOperationException()
    {
        var ct = TestContext.Current.CancellationToken;
        var manager = new BrainRepoManager(_tempDir, NullLogger<BrainRepoManager>.Instance);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            manager.DeleteTagAsync("nonexistent", "v1.0.0", ct));
    }

    // ---------- Input validation ----------

    [Fact]
    public async Task MergeBranchAsync_NullRepoName_ThrowsArgumentException()
    {
        var ct = TestContext.Current.CancellationToken;
        var manager = new BrainRepoManager(_tempDir, NullLogger<BrainRepoManager>.Instance);

        await Assert.ThrowsAsync<ArgumentException>(() =>
            manager.MergeBranchAsync(null!, "feature", "main", ct));
    }

    [Fact]
    public async Task MergeBranchAsync_EmptyRepoName_ThrowsArgumentException()
    {
        var ct = TestContext.Current.CancellationToken;
        var manager = new BrainRepoManager(_tempDir, NullLogger<BrainRepoManager>.Instance);

        await Assert.ThrowsAsync<ArgumentException>(() =>
            manager.MergeBranchAsync("", "feature", "main", ct));
    }

    [Fact]
    public async Task MergeBranchAsync_OptionInjectionRepoName_ThrowsArgumentException()
    {
        var ct = TestContext.Current.CancellationToken;
        var manager = new BrainRepoManager(_tempDir, NullLogger<BrainRepoManager>.Instance);

        await Assert.ThrowsAsync<ArgumentException>(() =>
            manager.MergeBranchAsync("--upload-pack=evil", "feature", "main", ct));
    }

    [Fact]
    public async Task MergeBranchAsync_PathTraversalRepoName_ThrowsArgumentException()
    {
        var ct = TestContext.Current.CancellationToken;
        var manager = new BrainRepoManager(_tempDir, NullLogger<BrainRepoManager>.Instance);

        await Assert.ThrowsAsync<ArgumentException>(() =>
            manager.MergeBranchAsync("../evil", "feature", "main", ct));
    }

    [Fact]
    public async Task MergeBranchAsync_InvalidBranchName_ThrowsArgumentException()
    {
        var ct = TestContext.Current.CancellationToken;
        var manager = new BrainRepoManager(_tempDir, NullLogger<BrainRepoManager>.Instance);

        await Assert.ThrowsAsync<ArgumentException>(() =>
            manager.MergeBranchAsync("repo", "bad branch~name", "main", ct));
    }

    [Fact]
    public async Task CreateTagAsync_NullTag_ThrowsArgumentException()
    {
        var ct = TestContext.Current.CancellationToken;
        var manager = new BrainRepoManager(_tempDir, NullLogger<BrainRepoManager>.Instance);

        await Assert.ThrowsAsync<ArgumentException>(() =>
            manager.CreateTagAsync("repo", null!, "main", "msg", ct));
    }

    [Fact]
    public async Task CreateTagAsync_OptionInjectionTag_ThrowsArgumentException()
    {
        var ct = TestContext.Current.CancellationToken;
        var manager = new BrainRepoManager(_tempDir, NullLogger<BrainRepoManager>.Instance);

        await Assert.ThrowsAsync<ArgumentException>(() =>
            manager.CreateTagAsync("repo", "-evil", "main", "msg", ct));
    }

    [Fact]
    public async Task DeleteTagAsync_EmptyTag_ThrowsArgumentException()
    {
        var ct = TestContext.Current.CancellationToken;
        var manager = new BrainRepoManager(_tempDir, NullLogger<BrainRepoManager>.Instance);

        await Assert.ThrowsAsync<ArgumentException>(() =>
            manager.DeleteTagAsync("repo", "", ct));
    }

    // ---------- Locking ----------

    [Fact]
    public async Task ConcurrentCallsToSameRepo_AreSerialized()
    {
        var ct = TestContext.Current.CancellationToken;
        var (remoteDir, _, manager) = SetupRepo("serial-repo");

        // Two feature branches, each with a distinct commit on top of main.
        CreateBranchWithCommit(remoteDir, "main", "feature-a", "a.txt", "aaa");
        CreateBranchWithCommit(remoteDir, "main", "feature-b", "b.txt", "bbb");

        // Fire both merges concurrently. If they were not serialized, the shared clone
        // worktree would collide and at least one would fail. Serialization guarantees both succeed.
        var task1 = manager.MergeBranchAsync("serial-repo", "feature-a", "main", ct);
        var task2 = manager.MergeBranchAsync("serial-repo", "feature-b", "main", ct);

        var results = await Task.WhenAll(task1, task2);

        // Both merges applied (neither threw). At least one produced a real merge commit.
        Assert.Contains(results, r => r is not null);

        // Verify both files ended up on the remote main branch.
        var check = Path.Combine(_tempDir, "check-serial");
        Git(_tempDir, "clone", remoteDir, "check-serial");
        Assert.True(File.Exists(Path.Combine(check, "a.txt")));
        Assert.True(File.Exists(Path.Combine(check, "b.txt")));
    }

    // ---------- Existing-method repoName validation ----------



    // ---------- DeleteTagAsync single-side deletion failure ----------

    [Fact]
    public async Task DeleteTagAsync_TagExistsOnlyLocally_DeletionFails_ThrowsInvalidOperationException()
    {
        var ct = TestContext.Current.CancellationToken;
        var (remoteDir, clonePath, manager) = SetupRepo("del-local-fail");

        // Create a local-only tag (not pushed).
        Git(clonePath, "tag", "v1.0.0");
        Assert.DoesNotContain("v1.0.0", GitOutput(remoteDir, "tag", "-l"));

        // Pack the tag ref and hold a packed-refs.lock so `git tag -d` cannot rewrite packed-refs.
        Git(clonePath, "pack-refs", "--all");
        File.WriteAllText(Path.Combine(clonePath, ".git", "packed-refs.lock"), "");

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            manager.DeleteTagAsync("del-local-fail", "v1.0.0", ct));
    }

    [Fact]
    public async Task DeleteTagAsync_TagExistsOnlyRemotely_DeletionFails_ThrowsInvalidOperationException()
    {
        var ct = TestContext.Current.CancellationToken;
        var (remoteDir, clonePath, manager) = SetupRepo("del-remote-fail");

        // Create the tag on remote only via a staging clone.
        var staging = Path.Combine(_tempDir, "staging-del-remote-fail");
        Git(_tempDir, "clone", remoteDir, "staging-del-remote-fail");
        ConfigureIdentity(staging);
        Git(staging, "tag", "v1.0.0");
        Git(staging, "push", "origin", "v1.0.0");

        // The manager clone sees the remote tag (via ls-remote) but does not have it locally.
        Assert.DoesNotContain("v1.0.0", GitOutput(clonePath, "tag", "-l"));

        // Make the bare remote reject ref deletion by packing refs and holding a packed-refs.lock,
        // so `git push origin :refs/tags/v1.0.0` fails on the remote side.
        Git(remoteDir, "pack-refs", "--all");
        File.WriteAllText(Path.Combine(remoteDir, "packed-refs.lock"), "");

        try
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                manager.DeleteTagAsync("del-remote-fail", "v1.0.0", ct));
        }
        finally
        {
            // Remove the lock so the remote is usable/cleanable.
            var lockPath = Path.Combine(remoteDir, "packed-refs.lock");
            if (File.Exists(lockPath))
                File.Delete(lockPath);
        }
    }

    // ---------- Existing-method repoName validation ----------

    [Fact]
    public async Task EnsureCloneAsync_InvalidRepoName_ThrowsArgumentException()
    {
        var ct = TestContext.Current.CancellationToken;
        var manager = new BrainRepoManager(_tempDir, NullLogger<BrainRepoManager>.Instance);

        await Assert.ThrowsAsync<ArgumentException>(() =>
            manager.EnsureCloneAsync("../evil", "https://example.com/repo.git", "main", ct));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            manager.EnsureCloneAsync("a/b", "https://example.com/repo.git", "main", ct));
    }

    [Fact]
    public async Task MergeFeatureBranchAsync_InvalidRepoName_ThrowsArgumentException()
    {
        var ct = TestContext.Current.CancellationToken;
        var manager = new BrainRepoManager(_tempDir, NullLogger<BrainRepoManager>.Instance);

        await Assert.ThrowsAsync<ArgumentException>(() =>
            manager.MergeFeatureBranchAsync("../evil", "feature", "main", "msg", ct));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            manager.MergeFeatureBranchAsync("a/b", "feature", "main", "msg", ct));
    }

    [Fact]
    public async Task DeleteRemoteBranchAsync_InvalidRepoName_ThrowsArgumentException()
    {
        var ct = TestContext.Current.CancellationToken;
        var manager = new BrainRepoManager(_tempDir, NullLogger<BrainRepoManager>.Instance);

        await Assert.ThrowsAsync<ArgumentException>(() =>
            manager.DeleteRemoteBranchAsync("../evil", "feature", ct));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            manager.DeleteRemoteBranchAsync("a/b", "feature", ct));
    }

    [Fact]
    public async Task ListRemoteBranchesAsync_InvalidRepoName_ThrowsArgumentException()
    {
        var ct = TestContext.Current.CancellationToken;
        var manager = new BrainRepoManager(_tempDir, NullLogger<BrainRepoManager>.Instance);

        await Assert.ThrowsAsync<ArgumentException>(() =>
            manager.ListRemoteBranchesAsync("../evil", ct));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            manager.ListRemoteBranchesAsync("a/b", ct));
    }

    // ---------- ListRemoteBranchesAsync ----------

    [Fact]
    public async Task ListRemoteBranchesAsync_ReturnsBranchesWithoutOriginPrefix()
    {
        var ct = TestContext.Current.CancellationToken;
        var (remoteDir, _, manager) = SetupRepo("list-branches-repo");

        // Create two feature branches on the remote.
        CreateBranchWithCommit(remoteDir, "main", "alpha", "a.txt", "alpha content");
        CreateBranchWithCommit(remoteDir, "main", "beta", "b.txt", "beta content");

        var branches = await manager.ListRemoteBranchesAsync("list-branches-repo", ct);

        // main + alpha + beta, sorted alphabetically (case-insensitive).
        Assert.Equal(["alpha", "beta", "main"], branches);
    }

    [Fact]
    public async Task ListRemoteBranchesAsync_FiltersSymbolicHeadButKeepsHeadFix()
    {
        var ct = TestContext.Current.CancellationToken;
        var (remoteDir, _, manager) = SetupRepo("head-filter-repo");

        // Create branches named HEAD-fix and zz so we can verify HEAD is filtered but HEAD-fix stays.
        CreateBranchWithCommit(remoteDir, "main", "HEAD-fix", "hf.txt", "head fix content");
        CreateBranchWithCommit(remoteDir, "main", "zz", "z.txt", "zz content");

        var branches = await manager.ListRemoteBranchesAsync("head-filter-repo", ct);

        Assert.DoesNotContain("HEAD", branches);
        Assert.Contains("HEAD-fix", branches);
    }

    [Fact]
    public async Task ListRemoteBranchesAsync_MissingClone_ThrowsInvalidOperationException()
    {
        var ct = TestContext.Current.CancellationToken;
        var manager = new BrainRepoManager(_tempDir, NullLogger<BrainRepoManager>.Instance);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            manager.ListRemoteBranchesAsync("not-cloned-repo", ct));

        Assert.Contains("is not cloned", ex.Message);
    }

    // ---------- Branch/tag ref-format validation ----------

    [Fact]
    public async Task ValidateBranchOrTagName_RejectsDoubleDot()
    {
        var ct = TestContext.Current.CancellationToken;
        var manager = new BrainRepoManager(_tempDir, NullLogger<BrainRepoManager>.Instance);
        await Assert.ThrowsAsync<ArgumentException>(() =>
            manager.MergeBranchAsync("repo", "foo..bar", "main", ct));
    }

    [Fact]
    public async Task ValidateBranchOrTagName_RejectsLeadingDot()
    {
        var ct = TestContext.Current.CancellationToken;
        var manager = new BrainRepoManager(_tempDir, NullLogger<BrainRepoManager>.Instance);
        await Assert.ThrowsAsync<ArgumentException>(() =>
            manager.MergeBranchAsync("repo", ".hidden", "main", ct));
    }

    [Fact]
    public async Task ValidateBranchOrTagName_RejectsTrailingDot()
    {
        var ct = TestContext.Current.CancellationToken;
        var manager = new BrainRepoManager(_tempDir, NullLogger<BrainRepoManager>.Instance);
        await Assert.ThrowsAsync<ArgumentException>(() =>
            manager.MergeBranchAsync("repo", "foo.", "main", ct));
    }

    [Fact]
    public async Task ValidateBranchOrTagName_RejectsAtBrace()
    {
        var ct = TestContext.Current.CancellationToken;
        var manager = new BrainRepoManager(_tempDir, NullLogger<BrainRepoManager>.Instance);
        await Assert.ThrowsAsync<ArgumentException>(() =>
            manager.MergeBranchAsync("repo", "foo@{bar", "main", ct));
    }

    [Fact]
    public async Task ValidateBranchOrTagName_RejectsLoneAt()
    {
        var ct = TestContext.Current.CancellationToken;
        var manager = new BrainRepoManager(_tempDir, NullLogger<BrainRepoManager>.Instance);
        await Assert.ThrowsAsync<ArgumentException>(() =>
            manager.MergeBranchAsync("repo", "@", "main", ct));
    }

    [Fact]
    public async Task ValidateBranchOrTagName_RejectsControlChars()
    {
        var ct = TestContext.Current.CancellationToken;
        var manager = new BrainRepoManager(_tempDir, NullLogger<BrainRepoManager>.Instance);
        await Assert.ThrowsAsync<ArgumentException>(() =>
            manager.MergeBranchAsync("repo", "foo\nbar", "main", ct));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            manager.MergeBranchAsync("repo", "foo\0bar", "main", ct));
    }

    [Fact]
    public async Task ValidateBranchOrTagName_RejectsDoubleSlash()
    {
        var ct = TestContext.Current.CancellationToken;
        var manager = new BrainRepoManager(_tempDir, NullLogger<BrainRepoManager>.Instance);
        await Assert.ThrowsAsync<ArgumentException>(() =>
            manager.MergeBranchAsync("repo", "foo//bar", "main", ct));
    }

    [Fact]
    public async Task ValidateBranchOrTagName_RejectsComponentStartingWithDot()
    {
        var ct = TestContext.Current.CancellationToken;
        var manager = new BrainRepoManager(_tempDir, NullLogger<BrainRepoManager>.Instance);
        await Assert.ThrowsAsync<ArgumentException>(() =>
            manager.MergeBranchAsync("repo", "foo/.bar", "main", ct));
    }

    [Fact]
    public async Task ValidateBranchOrTagName_RejectsComponentEndingWithLock()
    {
        var ct = TestContext.Current.CancellationToken;
        var manager = new BrainRepoManager(_tempDir, NullLogger<BrainRepoManager>.Instance);
        await Assert.ThrowsAsync<ArgumentException>(() =>
            manager.MergeBranchAsync("repo", "foo/bar.lock", "main", ct));
    }

    [Fact]
    public async Task ValidateBranchOrTagName_RejectsLoneDotComponent()
    {
        var ct = TestContext.Current.CancellationToken;
        var manager = new BrainRepoManager(_tempDir, NullLogger<BrainRepoManager>.Instance);
        // "foo/./bar" — the middle component "." starts with '.' and is rejected.
        await Assert.ThrowsAsync<ArgumentException>(() =>
            manager.MergeBranchAsync("repo", "foo/./bar", "main", ct));
    }

    [Fact]
    public async Task ValidateBranchOrTagName_AcceptsValidSlashNames()
    {
        var ct = TestContext.Current.CancellationToken;
        var (_, _, manager) = SetupRepo("valid-slash-repo");

        // Valid slash-separated names must pass validation. They will fail later because the
        // source branch does not exist on origin — an InvalidOperationException, NOT an
        // ArgumentException. Getting past validation is what we assert here.
        foreach (var branch in new[] { "foo/bar", "release/v1.0", "copilothive/feature-branch" })
        {
            var ex = await Record.ExceptionAsync(() =>
                manager.MergeBranchAsync("valid-slash-repo", branch, "main", ct));
            Assert.NotNull(ex);
            Assert.IsNotType<ArgumentException>(ex);
            Assert.IsType<InvalidOperationException>(ex);
        }
    }

    // ---------- Symlink containment ----------

    [Fact]
    public async Task ValidateRepoName_SymlinkEscape_ThrowsArgumentException()
    {
        var ct = TestContext.Current.CancellationToken;
        var manager = new BrainRepoManager(_tempDir, NullLogger<BrainRepoManager>.Instance);

        // Create a directory outside the work directory as the symlink target.
        var outsideDir = Path.Combine(_tempDir, "outside-target");
        Directory.CreateDirectory(outsideDir);
        // Put a fake .git inside so the clone-existence check would otherwise pass.
        Directory.CreateDirectory(Path.Combine(outsideDir, ".git"));

        var reposDir = manager.WorkDirectory;
        Directory.CreateDirectory(reposDir);
        var linkPath = Path.Combine(reposDir, "evil-repo");

        try
        {
            Directory.CreateSymbolicLink(linkPath, outsideDir);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or PlatformNotSupportedException)
        {
            // Symlink creation is not permitted on this platform (e.g., Windows without admin) —
            // skip the assertion. The lexical checks still guard the common cases.
            return;
        }

        await Assert.ThrowsAsync<ArgumentException>(() =>
            manager.MergeBranchAsync("evil-repo", "feature", "main", ct));
    }

    // ---------- Cancellation cleanup ----------

    [Fact]
    public async Task MergeBranchAsync_AlreadyCanceledToken_ThrowsAndLeavesNoMergeState()
    {
        // Deterministic cancellation test: pass an already-canceled token. The first git
        // subprocess (fetch) observes cancellation immediately, RunGitCaptureAsync/RunGitAsync
        // terminate the process tree, and the method throws OperationCanceledException. Crucially,
        // no MERGE_HEAD must be left behind (the merge either never started or was aborted by the
        // finally-block cleanup, which runs on a fresh bounded token).
        var (remoteDir, clonePath, manager) = SetupRepo("cancel-repo");
        CreateBranchWithCommit(remoteDir, "main", "feature", "feature.txt", "content");

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            manager.MergeBranchAsync("cancel-repo", "feature", "main", cts.Token));

        // No leftover MERGE state.
        Assert.False(File.Exists(Path.Combine(clonePath, ".git", "MERGE_HEAD")));
    }

    // Note: reliably racing cancellation against a *mid-flight* merge (as opposed to an
    // already-canceled token) is timing-dependent and flaky, because it requires the merge
    // subprocess to still be writing when the token trips. The production code guards that path via
    // a `mergeStarted` flag, process-tree kill with bounded wait in RunGitCaptureAsync, and a
    // fresh-token MERGE_HEAD abort in the finally block. Fully deterministic coverage would require
    // an injectable process factory to pause the merge; that refactor is out of scope here.

    [Fact]
    public async Task MergeBranchAsync_CanceledDuringMerge_KillsProcessAndCleansUp()
    {
        // Deterministically cancel WHILE the directly-managed merge process is running. A hook
        // creates a marker file as its first action; a background task waits for that marker before
        // canceling, so the token can only trip AFTER the merge process has started (not during
        // fetch/checkout/pre-merge steps). This exercises MergeBranchAsync's directly-managed merge
        // process path: Process.Start, Kill(entireProcessTree), the blocking WaitForExit, and the
        // fresh-token MERGE_HEAD abort in the finally block. (The merge no longer uses RunGitCaptureAsync.)
        var (remoteDir, clonePath, manager) = SetupRepo("cancel-mid-repo");
        // Diverge both branches (non-conflicting, different files) so the merge creates a real
        // merge commit rather than fast-forwarding — a merge commit invokes prepare-commit-msg.
        CreateBranchWithCommit(remoteDir, "main", "feature", "feature.txt", "content");

        // Add a distinct commit directly on main via a throwaway staging clone.
        var mainStaging = Path.Combine(_tempDir, "cancel-mid-main-staging");
        Git(_tempDir, "clone", remoteDir, "cancel-mid-main-staging");
        ConfigureIdentity(mainStaging);
        File.WriteAllText(Path.Combine(mainStaging, "main-extra.txt"), "main content\n");
        Git(mainStaging, "add", "main-extra.txt");
        Git(mainStaging, "commit", "-m", "Add main-extra.txt");
        Git(mainStaging, "push", "origin", "main");

        // Marker file the hook creates when the merge process starts running.
        var markerPath = Path.Combine(clonePath, ".git", "merge_started.marker");
        if (File.Exists(markerPath))
            File.Delete(markerPath);

        // Install a prepare-commit-msg hook that (1) touches the marker, then (2) sleeps long
        // enough to be killed. The hook file MUST be named exactly "prepare-commit-msg" (no
        // extension) — git discovers hooks by this exact name on all platforms, and git on Windows
        // (via Git Bash) executes "#!/bin/sh" scripts without an extension or chmod.
        var hooksDir = Path.Combine(clonePath, ".git", "hooks");
        Directory.CreateDirectory(hooksDir);
        var hookPath = Path.Combine(hooksDir, "prepare-commit-msg");
        File.WriteAllText(hookPath, $"#!/bin/sh\ntouch \"{markerPath}\"\nsleep 30\n");
        if (!OperatingSystem.IsWindows())
        {
            var chmodPsi = new ProcessStartInfo("chmod")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            chmodPsi.ArgumentList.Add("+x");
            chmodPsi.ArgumentList.Add(hookPath);
            using var chmod = Process.Start(chmodPsi)!;
            chmod.WaitForExit();
        }

        // Manually controlled token — cancellation is triggered by the marker poll, not a timer.
        using var cts = new CancellationTokenSource();

        // Background task: poll for the marker (up to 10s), then cancel. This guarantees the token
        // is canceled only after the merge process has started.
        _ = Task.Run(async () =>
        {
            for (var i = 0; i < 100; i++)
            {
                if (File.Exists(markerPath))
                {
                    // Merge process is running inside the hook sleep — give it a moment, then cancel.
                    await Task.Delay(100, CancellationToken.None);
                    cts.Cancel();
                    return;
                }
                await Task.Delay(100, CancellationToken.None);
            }
            // Timeout guard — cancel anyway to avoid hanging the test.
            cts.Cancel();
        }, CancellationToken.None);

        var ex = await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            manager.MergeBranchAsync("cancel-mid-repo", "feature", "main", cts.Token));

        // The merge process actually started (the hook ran and created the marker).
        Assert.True(File.Exists(markerPath),
            "Marker file should exist — the merge process must have started before cancellation");

        // The merge process was killed and the finally-block cleanup aborted the MERGE state.
        Assert.False(File.Exists(Path.Combine(clonePath, ".git", "MERGE_HEAD")),
            "MERGE_HEAD should not exist after cancellation cleanup");
    }

    // ---------- Helpers ----------

    /// <summary>
    /// Creates a bare remote with an initial commit on <c>main</c>, clones it into the manager's
    /// repos directory, configures git identity in the clone, and returns the remote path, clone
    /// path, and a <see cref="BrainRepoManager"/> pointing at the temp dir.
    /// </summary>
    private (string remoteDir, string clonePath, BrainRepoManager manager) SetupRepo(string repoName)
    {
        var remoteDir = Path.Combine(_tempDir, $"{repoName}-remote.git");
        Directory.CreateDirectory(remoteDir);
        Git(remoteDir, "init", "--bare", "-b", "main");

        // Staging repo to seed the remote with an initial commit.
        var stagingDir = Path.Combine(_tempDir, $"{repoName}-staging");
        Directory.CreateDirectory(stagingDir);
        Git(stagingDir, "init", "-b", "main");
        ConfigureIdentity(stagingDir);
        File.WriteAllText(Path.Combine(stagingDir, "README.md"), "# Hello\n");
        Git(stagingDir, "add", "README.md");
        Git(stagingDir, "commit", "-m", "Initial commit");
        Git(stagingDir, "remote", "add", "origin", remoteDir);
        Git(stagingDir, "push", "origin", "main");

        // Clone into the manager's repos directory.
        var reposDir = Path.Combine(_tempDir, "repos");
        Directory.CreateDirectory(reposDir);
        Git(reposDir, "clone", remoteDir, repoName);
        var clonePath = Path.Combine(reposDir, repoName);
        ConfigureIdentity(clonePath);

        var manager = new BrainRepoManager(_tempDir, NullLogger<BrainRepoManager>.Instance);
        return (remoteDir, clonePath, manager);
    }

    /// <summary>
    /// Creates a new branch off <paramref name="baseBranch"/> on the remote with a single new
    /// commit adding <paramref name="fileName"/>, using a throwaway staging clone.
    /// </summary>
    private void CreateBranchWithCommit(string remoteDir, string baseBranch, string newBranch, string fileName, string content)
    {
        var staging = Path.Combine(_tempDir, $"stg-{newBranch}-{Path.GetRandomFileName()}");
        Git(_tempDir, "clone", remoteDir, Path.GetFileName(staging));
        ConfigureIdentity(staging);
        Git(staging, "checkout", "-b", newBranch, $"origin/{baseBranch}");
        File.WriteAllText(Path.Combine(staging, fileName), content + "\n");
        Git(staging, "add", fileName);
        Git(staging, "commit", "-m", $"Add {fileName}");
        Git(staging, "push", "origin", newBranch);
    }

    private static void ConfigureIdentity(string workDir)
    {
        Git(workDir, "config", "user.email", "test@test.com");
        Git(workDir, "config", "user.name", "Test");
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
