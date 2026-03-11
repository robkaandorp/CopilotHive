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
        await _manager.InitBareRepoAsync();

        Assert.True(Directory.Exists(_manager.BareRepoPath));
        // A bare repo has HEAD directly in the directory
        Assert.True(File.Exists(Path.Combine(_manager.BareRepoPath, "HEAD")));
    }

    [Fact]
    public async Task InitBareRepo_IsIdempotent()
    {
        await _manager.InitBareRepoAsync();
        await _manager.InitBareRepoAsync(); // should not throw

        Assert.True(Directory.Exists(_manager.BareRepoPath));
    }

    [Fact]
    public async Task CreateWorkerClone_ClonesFromBareRepo()
    {
        await _manager.InitBareRepoAsync();

        var clonePath = await _manager.CreateWorkerCloneAsync("test-worker");

        Assert.True(Directory.Exists(clonePath));
        Assert.True(Directory.Exists(Path.Combine(clonePath, ".git")));
    }

    [Fact]
    public async Task CreateBranch_CreatesBranchInClone()
    {
        await _manager.InitBareRepoAsync();
        var clonePath = await _manager.CreateWorkerCloneAsync("brancher");

        await _manager.CreateBranchAsync(clonePath, "feature/test");

        // Verify we're on the new branch by checking HEAD
        var headRef = await File.ReadAllTextAsync(Path.Combine(clonePath, ".git", "HEAD"));
        Assert.Contains("feature/test", headRef);
    }

    [Fact]
    public async Task PushBranch_PushesToBareRepo()
    {
        await _manager.InitBareRepoAsync();
        var clonePath = await _manager.CreateWorkerCloneAsync("pusher");
        await _manager.CreateBranchAsync(clonePath, "coder/task-1");

        // Create a file and commit
        await File.WriteAllTextAsync(Path.Combine(clonePath, "test.txt"), "hello");
        await RunGitInClone(clonePath, "add", ".");
        await RunGitInClone(clonePath, "commit", "-m", "test commit");

        await _manager.PushBranchAsync(clonePath, "coder/task-1");

        // Verify branch exists in bare repo
        var clonePath2 = await _manager.CreateWorkerCloneAsync("verifier");
        await _manager.PullBranchAsync(clonePath2, "coder/task-1");
        Assert.True(File.Exists(Path.Combine(clonePath2, "test.txt")));
    }

    [Fact]
    public async Task MergeToMain_SucceedsForCleanMerge()
    {
        await _manager.InitBareRepoAsync();
        var clonePath = await _manager.CreateWorkerCloneAsync("merger");
        await _manager.CreateBranchAsync(clonePath, "feature/clean");

        await File.WriteAllTextAsync(Path.Combine(clonePath, "feature.txt"), "feature code");
        await RunGitInClone(clonePath, "add", ".");
        await RunGitInClone(clonePath, "commit", "-m", "add feature");
        await _manager.PushBranchAsync(clonePath, "feature/clean");

        var (success, _) = await _manager.MergeToMainAsync(clonePath, "feature/clean");

        Assert.True(success);
    }

    [Fact]
    public async Task GetDiff_ReturnsChanges()
    {
        await _manager.InitBareRepoAsync();
        var clonePath = await _manager.CreateWorkerCloneAsync("differ");
        await _manager.CreateBranchAsync(clonePath, "feature/diff-test");

        await File.WriteAllTextAsync(Path.Combine(clonePath, "new-file.txt"), "new content");
        await RunGitInClone(clonePath, "add", ".");
        await RunGitInClone(clonePath, "commit", "-m", "add file");

        var diff = await _manager.GetDiffAsync(clonePath);

        Assert.Contains("new-file.txt", diff);
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
