using System.Diagnostics;
using CopilotHive.Shared.Grpc;

namespace CopilotHive.Worker;

/// <summary>
/// Git CLI operations for cloning, branching, and pushing.
/// All operations shell out to the git CLI via Process.Start.
/// </summary>
public static class GitOperations
{
    public static async Task CloneRepositoryAsync(string url, string targetDir, CancellationToken ct)
    {
        await RunGitCommandAsync(
            Path.GetDirectoryName(targetDir) ?? ".",
            $"clone {url} {Path.GetFileName(targetDir)}",
            ct);
    }

    public static async Task CheckoutBranchAsync(string repoDir, string branch, CancellationToken ct)
    {
        await RunGitCommandAsync(repoDir, $"checkout {branch}", ct);
    }

    public static async Task CreateBranchAsync(
        string repoDir, string branchName, string baseBranch, CancellationToken ct)
    {
        await RunGitCommandAsync(repoDir, $"checkout {baseBranch}", ct);
        await RunGitCommandAsync(repoDir, $"checkout -b {branchName}", ct);
    }

    public static async Task PushBranchAsync(string repoDir, string branch, CancellationToken ct)
    {
        await RunGitCommandAsync(repoDir, $"push origin {branch} --force", ct);
    }

    public static async Task<GitStatus> GetGitStatusAsync(string repoDir, CancellationToken ct)
    {
        var status = new GitStatus();

        // Current branch
        var (branchExit, branchOut, _) = await RunGitCommandAsync(
            repoDir, "rev-parse --abbrev-ref HEAD", ct);
        if (branchExit == 0)
            status.CurrentBranch = branchOut.Trim();

        // Last commit SHA
        var (shaExit, shaOut, _) = await RunGitCommandAsync(
            repoDir, "rev-parse HEAD", ct);
        if (shaExit == 0)
            status.LastCommitSha = shaOut.Trim();

        // Last commit message
        var (msgExit, msgOut, _) = await RunGitCommandAsync(
            repoDir, "log -1 --pretty=%s", ct);
        if (msgExit == 0)
            status.LastCommitMessage = msgOut.Trim();

        // Diff stat against origin/main (may fail if no upstream)
        var (statExit, statOut, _) = await RunGitCommandAsync(
            repoDir, "diff --stat --numstat HEAD~1", ct);
        if (statExit == 0)
            ParseDiffStat(statOut, status);

        return status;
    }

    /// <summary>
    /// Run a git command and return (exitCode, stdout, stderr).
    /// </summary>
    public static async Task<(int ExitCode, string Stdout, string Stderr)> RunGitCommandAsync(
        string workDir, string args, CancellationToken ct)
    {
        var psi = new ProcessStartInfo("git")
        {
            WorkingDirectory = workDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            Arguments = args,
        };

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start git process.");

        // Read stdout and stderr concurrently to avoid deadlock when buffers fill
        var stdoutTask = process.StandardOutput.ReadToEndAsync(ct);
        var stderrTask = process.StandardError.ReadToEndAsync(ct);

        await Task.WhenAll(stdoutTask, stderrTask);
        await process.WaitForExitAsync(ct);

        return (process.ExitCode, stdoutTask.Result, stderrTask.Result);
    }

    /// <summary>
    /// Delete a directory with retries — on Windows, git processes may hold brief file locks.
    /// </summary>
    public static async Task ForceDeleteDirectoryAsync(string path, int maxRetries = 5)
    {
        for (var i = 0; i < maxRetries; i++)
        {
            if (!Directory.Exists(path))
                return;

            try
            {
                foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
                {
                    File.SetAttributes(file, FileAttributes.Normal);
                }
                Directory.Delete(path, recursive: true);
                return;
            }
            catch (UnauthorizedAccessException) when (i < maxRetries - 1)
            {
                await Task.Delay(200 * (i + 1));
            }
            catch (IOException) when (i < maxRetries - 1)
            {
                await Task.Delay(200 * (i + 1));
            }
        }
    }

    private static void ParseDiffStat(string numstatOutput, GitStatus status)
    {
        var lines = numstatOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        var filesChanged = 0;
        var insertions = 0;
        var deletions = 0;

        foreach (var line in lines)
        {
            var parts = line.Split('\t', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 3) continue;

            filesChanged++;
            if (int.TryParse(parts[0], out var added)) insertions += added;
            if (int.TryParse(parts[1], out var removed)) deletions += removed;
        }

        status.FilesChanged = filesChanged;
        status.Insertions = insertions;
        status.Deletions = deletions;
    }
}
