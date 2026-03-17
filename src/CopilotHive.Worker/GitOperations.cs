using System.Diagnostics;
using CopilotHive.Shared.Grpc;

namespace CopilotHive.Worker;

/// <summary>
/// Git CLI operations for cloning, branching, and pushing.
/// All operations shell out to the git CLI via Process.Start.
/// </summary>
public static class GitOperations
{
    /// <summary>
    /// Clones a remote repository into the specified target directory.
    /// </summary>
    /// <param name="url">Remote URL of the repository to clone.</param>
    /// <param name="targetDir">Local path where the repository will be cloned.</param>
    /// <param name="ct">Cancellation token.</param>
    public static async Task CloneRepositoryAsync(string url, string targetDir, CancellationToken ct)
    {
        await RunGitCommandAsync(
            Path.GetDirectoryName(targetDir) ?? ".",
            $"clone {url} {Path.GetFileName(targetDir)}",
            ct);
    }

    /// <summary>
    /// Checks out an existing branch in the specified repository directory.
    /// </summary>
    /// <param name="repoDir">Path to the local git repository.</param>
    /// <param name="branch">Name of the branch to check out.</param>
    /// <param name="ct">Cancellation token.</param>
    public static async Task CheckoutBranchAsync(string repoDir, string branch, CancellationToken ct)
    {
        await RunGitCommandAsync(repoDir, $"checkout {branch}", ct);
    }

    /// <summary>
    /// Creates a new branch from the given base branch.
    /// </summary>
    /// <param name="repoDir">Path to the local git repository.</param>
    /// <param name="branchName">Name of the new branch to create.</param>
    /// <param name="baseBranch">The branch to base the new branch on.</param>
    /// <param name="ct">Cancellation token.</param>
    public static async Task CreateBranchAsync(
        string repoDir, string branchName, string baseBranch, CancellationToken ct)
    {
        await RunGitCommandAsync(repoDir, $"checkout {baseBranch}", ct);
        await RunGitCommandAsync(repoDir, $"checkout -b {branchName}", ct);
    }

    /// <summary>
    /// Force-pushes the specified branch to the remote origin.
    /// </summary>
    /// <param name="repoDir">Path to the local git repository.</param>
    /// <param name="branch">Name of the branch to push.</param>
    /// <param name="ct">Cancellation token.</param>
    public static async Task PushBranchAsync(string repoDir, string branch, CancellationToken ct)
    {
        await RunGitCommandAsync(repoDir, $"push origin {branch} --force", ct);
    }

    /// <summary>
    /// Retrieves current git status information for the repository at the given path.
    /// </summary>
    /// <param name="repoDir">Path to the local git repository.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A <see cref="GitStatus"/> containing branch, commit, and diff statistics.</returns>
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

        // Diff stat against the previous commit (may fail if no commits)
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
