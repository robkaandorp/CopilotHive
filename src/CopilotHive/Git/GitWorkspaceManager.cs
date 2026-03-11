using System.Diagnostics;

namespace CopilotHive.Git;

public sealed class GitWorkspaceManager
{
    private readonly string _workspacePath;
    private readonly string _bareRepoPath;

    public GitWorkspaceManager(string workspacePath)
    {
        _workspacePath = Path.GetFullPath(workspacePath);
        _bareRepoPath = Path.Combine(_workspacePath, "origin");
    }

    public string BareRepoPath => _bareRepoPath;

    public async Task InitBareRepoAsync(CancellationToken ct = default)
    {
        Directory.CreateDirectory(_bareRepoPath);

        if (!Directory.Exists(Path.Combine(_bareRepoPath, "HEAD")))
        {
            await RunGitAsync(_bareRepoPath, ["init", "--bare"], ct);
            // Create an initial commit so main branch exists
            var tempClone = Path.Combine(_workspacePath, "_init-temp");
            try
            {
                await RunGitAsync(_workspacePath, ["clone", _bareRepoPath, "_init-temp"], ct);
                await RunGitAsync(tempClone, ["commit", "--allow-empty", "-m", "Initial commit"], ct);
                await RunGitAsync(tempClone, ["push", "origin", "main"], ct);
            }
            finally
            {
                if (Directory.Exists(tempClone))
                    Directory.Delete(tempClone, recursive: true);
            }
        }
    }

    public async Task<string> CreateWorkerCloneAsync(string workerName, CancellationToken ct = default)
    {
        var clonePath = Path.Combine(_workspacePath, workerName);

        if (Directory.Exists(clonePath))
            Directory.Delete(clonePath, recursive: true);

        await RunGitAsync(_workspacePath, ["clone", _bareRepoPath, workerName], ct);
        return clonePath;
    }

    public async Task CreateBranchAsync(string clonePath, string branchName, CancellationToken ct = default)
    {
        await RunGitAsync(clonePath, ["checkout", "-b", branchName], ct);
    }

    public async Task PushBranchAsync(string clonePath, string branchName, CancellationToken ct = default)
    {
        await RunGitAsync(clonePath, ["push", "origin", branchName, "--force"], ct);
    }

    public async Task PullBranchAsync(string clonePath, string branchName, CancellationToken ct = default)
    {
        await RunGitAsync(clonePath, ["fetch", "origin"], ct);
        await RunGitAsync(clonePath, ["checkout", branchName], ct);
        await RunGitAsync(clonePath, ["pull", "origin", branchName], ct);
    }

    public async Task<(bool Success, string Output)> MergeToMainAsync(
        string clonePath,
        string branchName,
        CancellationToken ct = default)
    {
        await RunGitAsync(clonePath, ["checkout", "main"], ct);
        await RunGitAsync(clonePath, ["pull", "origin", "main"], ct);

        try
        {
            var output = await RunGitAsync(clonePath, ["merge", branchName, "--no-ff", "-m", $"Merge {branchName}"], ct);
            await RunGitAsync(clonePath, ["push", "origin", "main"], ct);
            return (true, output);
        }
        catch (GitException ex)
        {
            await RunGitAsync(clonePath, ["merge", "--abort"], ct);
            return (false, ex.Output);
        }
    }

    public async Task<string> GetDiffAsync(string clonePath, string baseBranch = "main", CancellationToken ct = default)
    {
        return await RunGitAsync(clonePath, ["diff", baseBranch], ct);
    }

    private static async Task<string> RunGitAsync(string workingDir, string[] args, CancellationToken ct)
    {
        var psi = new ProcessStartInfo("git")
        {
            WorkingDirectory = workingDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };

        foreach (var arg in args)
            psi.ArgumentList.Add(arg);

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start git process");

        var stdout = await process.StandardOutput.ReadToEndAsync(ct);
        var stderr = await process.StandardError.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct);

        if (process.ExitCode != 0)
            throw new GitException(process.ExitCode, $"{stdout}\n{stderr}".Trim());

        return stdout.Trim();
    }
}

public sealed class GitException(int exitCode, string output)
    : Exception($"git exited with code {exitCode}: {output}")
{
    public int ExitCode => exitCode;
    public string Output => output;
}
