using CopilotHive.Git;

namespace CopilotHive.Tests;

public class GitWorkspaceManagerTests : IDisposable
{
    private readonly string _tempDir;
    private readonly GitWorkspaceManager _manager;

    public GitWorkspaceManagerTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"copilothive-git-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        _manager = new GitWorkspaceManager(_tempDir);
    }

    public void Dispose()
    {
        // Git leaves locked files on Windows — retry cleanup
        for (var i = 0; i < 5; i++)
        {
            try
            {
                if (!Directory.Exists(_tempDir)) return;
                foreach (var file in Directory.EnumerateFiles(_tempDir, "*", SearchOption.AllDirectories))
                    File.SetAttributes(file, FileAttributes.Normal);
                Directory.Delete(_tempDir, recursive: true);
                return;
            }
            catch (Exception) when (i < 4)
            {
                Thread.Sleep(200 * (i + 1));
            }
        }
    }

    [Fact]
    public async Task InitBareRepo_CreatesBareRepository()
    {
        await _manager.InitBareRepoAsync(ct: TestContext.Current.CancellationToken);

        Assert.True(Directory.Exists(_manager.BareRepoPath));
        // A bare repo has HEAD directly in the directory
        Assert.True(File.Exists(Path.Combine(_manager.BareRepoPath, "HEAD")));
    }

    [Fact]
    public async Task InitBareRepo_IsIdempotent()
    {
        await _manager.InitBareRepoAsync(ct: TestContext.Current.CancellationToken);
        await _manager.InitBareRepoAsync(ct: TestContext.Current.CancellationToken); // should not throw

        Assert.True(Directory.Exists(_manager.BareRepoPath));
    }

    [Fact]
    public async Task CreateWorkerClone_ClonesFromBareRepo()
    {
        await _manager.InitBareRepoAsync(ct: TestContext.Current.CancellationToken);

        var clonePath = await _manager.CreateWorkerCloneAsync("test-worker", TestContext.Current.CancellationToken);

        Assert.True(Directory.Exists(clonePath));
        Assert.True(Directory.Exists(Path.Combine(clonePath, ".git")));
    }

    [Fact]
    public async Task CreateBranch_CreatesBranchInClone()
    {
        await _manager.InitBareRepoAsync(ct: TestContext.Current.CancellationToken);
        var clonePath = await _manager.CreateWorkerCloneAsync("brancher", TestContext.Current.CancellationToken);

        await _manager.CreateBranchAsync(clonePath, "feature/test", TestContext.Current.CancellationToken);

        // Verify we're on the new branch by checking HEAD
        var headRef = await File.ReadAllTextAsync(Path.Combine(clonePath, ".git", "HEAD"), TestContext.Current.CancellationToken);
        Assert.Contains("feature/test", headRef);
    }

    [Fact]
    public async Task PushBranch_PushesToBareRepo()
    {
        await _manager.InitBareRepoAsync(ct: TestContext.Current.CancellationToken);
        var clonePath = await _manager.CreateWorkerCloneAsync("pusher", TestContext.Current.CancellationToken);
        await _manager.CreateBranchAsync(clonePath, "coder/task-1", TestContext.Current.CancellationToken);

        // Create a file and commit
        await File.WriteAllTextAsync(Path.Combine(clonePath, "test.txt"), "hello", TestContext.Current.CancellationToken);
        await RunGitInClone(clonePath, "add", ".");
        await RunGitInClone(clonePath, "commit", "-m", "test commit");

        await _manager.PushBranchAsync(clonePath, "coder/task-1", TestContext.Current.CancellationToken);

        // Verify branch exists in bare repo
        var clonePath2 = await _manager.CreateWorkerCloneAsync("verifier", TestContext.Current.CancellationToken);
        await _manager.PullBranchAsync(clonePath2, "coder/task-1", TestContext.Current.CancellationToken);
        Assert.True(File.Exists(Path.Combine(clonePath2, "test.txt")));
    }

    [Fact]
    public async Task MergeBranch_SucceedsForCleanMerge()
    {
        await _manager.InitBareRepoAsync(ct: TestContext.Current.CancellationToken);
        var clonePath = await _manager.CreateWorkerCloneAsync("merger", TestContext.Current.CancellationToken);
        await _manager.CreateBranchAsync(clonePath, "feature/clean", TestContext.Current.CancellationToken);

        await File.WriteAllTextAsync(Path.Combine(clonePath, "feature.txt"), "feature code", TestContext.Current.CancellationToken);
        await RunGitInClone(clonePath, "add", ".");
        await RunGitInClone(clonePath, "commit", "-m", "add feature");
        await _manager.PushBranchAsync(clonePath, "feature/clean", TestContext.Current.CancellationToken);

        var (success, _) = await _manager.MergeBranchAsync(clonePath, "feature/clean", "main", TestContext.Current.CancellationToken);

        Assert.True(success);
    }

    [Fact]
    public async Task GetDiff_ReturnsChanges()
    {
        await _manager.InitBareRepoAsync(ct: TestContext.Current.CancellationToken);
        var clonePath = await _manager.CreateWorkerCloneAsync("differ", TestContext.Current.CancellationToken);
        await _manager.CreateBranchAsync(clonePath, "feature/diff-test", TestContext.Current.CancellationToken);

        await File.WriteAllTextAsync(Path.Combine(clonePath, "new-file.txt"), "new content", TestContext.Current.CancellationToken);
        await RunGitInClone(clonePath, "add", ".");
        await RunGitInClone(clonePath, "commit", "-m", "add file");

        var diff = await _manager.GetDiffAsync(clonePath, ct: TestContext.Current.CancellationToken);

        Assert.Contains("new-file.txt", diff);
    }

    [Fact]
    public async Task InitBareRepo_WithSourcePath_SeedsFromSource()
    {
        // Arrange: create a fake source project
        var sourceDir = Path.Combine(_tempDir, "_source");
        Directory.CreateDirectory(sourceDir);
        await File.WriteAllTextAsync(Path.Combine(sourceDir, "Program.cs"), "Console.WriteLine(\"Hello\");", TestContext.Current.CancellationToken);
        Directory.CreateDirectory(Path.Combine(sourceDir, "src"));
        await File.WriteAllTextAsync(Path.Combine(sourceDir, "src", "Lib.cs"), "class Lib {}", TestContext.Current.CancellationToken);
        // Also create dirs that should be skipped
        Directory.CreateDirectory(Path.Combine(sourceDir, ".git", "objects"));
        await File.WriteAllTextAsync(Path.Combine(sourceDir, ".git", "HEAD"), "ref: refs/heads/main", TestContext.Current.CancellationToken);
        Directory.CreateDirectory(Path.Combine(sourceDir, "bin", "Debug"));
        await File.WriteAllTextAsync(Path.Combine(sourceDir, "bin", "Debug", "app.dll"), "binary", TestContext.Current.CancellationToken);

        // Act
        await _manager.InitBareRepoAsync(sourceDir, ct: TestContext.Current.CancellationToken);
        var clonePath = await _manager.CreateWorkerCloneAsync("seeded-worker", TestContext.Current.CancellationToken);

        // Assert: source files are present
        Assert.True(File.Exists(Path.Combine(clonePath, "Program.cs")));
        Assert.True(File.Exists(Path.Combine(clonePath, "src", "Lib.cs")));
        // .git internals and bin/ should NOT be in the seeded clone
        Assert.False(Directory.Exists(Path.Combine(clonePath, "bin")));
    }

    [Fact]
    public async Task InitBareRepo_WithNullSourcePath_CreatesEmptyCommit()
    {
        await _manager.InitBareRepoAsync(sourcePath: null, ct: TestContext.Current.CancellationToken);
        var clonePath = await _manager.CreateWorkerCloneAsync("empty-check", TestContext.Current.CancellationToken);

        // Clone should exist but have no files (empty initial commit)
        var files = Directory.GetFiles(clonePath).Where(f => !f.Contains(".git")).ToArray();
        Assert.Empty(files);
    }

    [Fact]
    public async Task RevertLastMerge_UndoesMergeCommit()
    {
        await _manager.InitBareRepoAsync(ct: TestContext.Current.CancellationToken);

        // Create and merge a feature branch
        var clonePath = await _manager.CreateWorkerCloneAsync("revert-merger", TestContext.Current.CancellationToken);
        await _manager.CreateBranchAsync(clonePath, "feature/to-revert", TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(Path.Combine(clonePath, "feature.txt"), "will be reverted", TestContext.Current.CancellationToken);
        await RunGitInClone(clonePath, "add", ".");
        await RunGitInClone(clonePath, "commit", "-m", "add feature to revert");
        await _manager.PushBranchAsync(clonePath, "feature/to-revert", TestContext.Current.CancellationToken);

        var (success, _) = await _manager.MergeBranchAsync(clonePath, "feature/to-revert", "main", TestContext.Current.CancellationToken);
        Assert.True(success);

        // Verify file exists on main after merge
        var verifyClone = await _manager.CreateWorkerCloneAsync("verify-merged", TestContext.Current.CancellationToken);
        Assert.True(File.Exists(Path.Combine(verifyClone, "feature.txt")));

        // Revert the merge
        await _manager.RevertLastMergeAsync(clonePath, "main", TestContext.Current.CancellationToken);

        // Verify file is gone on main after revert
        var postRevertClone = await _manager.CreateWorkerCloneAsync("verify-reverted", TestContext.Current.CancellationToken);
        Assert.False(File.Exists(Path.Combine(postRevertClone, "feature.txt")));
    }

    private static async Task RunGitInClone(string workingDir, params string[] args)
    {
        var psi = new System.Diagnostics.ProcessStartInfo("git")
        {
            WorkingDirectory = workingDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var arg in args) psi.ArgumentList.Add(arg);

        using var process = System.Diagnostics.Process.Start(psi)!;
        await process.WaitForExitAsync();
        if (process.ExitCode != 0)
        {
            var stderr = await process.StandardError.ReadToEndAsync();
            throw new Exception($"git {string.Join(' ', args)} failed: {stderr}");
        }
    }
}
