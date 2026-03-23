using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace CopilotHive.Git;

/// <summary>
/// Manages persistent read-only clones of target repositories for the Brain.
/// Each repository gets its own clone at <c>{basePath}/brain-{repoName}</c>,
/// checked out to the default branch. Clones persist across goals and are
/// updated (pulled) before each goal starts.
/// </summary>
public sealed class BrainRepoManager
{
    private readonly string _basePath;
    private readonly ILogger _logger;

    /// <summary>
    /// Initialises a new <see cref="BrainRepoManager"/>.
    /// </summary>
    /// <param name="basePath">
    /// Root directory for Brain clones (e.g. <c>/app/state</c>).
    /// Each repo clone is created at <c>{basePath}/brain-{repoName}</c>.
    /// </param>
    /// <param name="logger">Logger instance.</param>
    public BrainRepoManager(string basePath, ILogger<BrainRepoManager> logger)
    {
        _basePath = Path.GetFullPath(basePath);
        _logger = logger;
    }

    /// <summary>
    /// Ensures a clone exists for the given repository and returns its path.
    /// If the clone already exists, pulls the latest changes on the default branch.
    /// If not, clones from the remote URL.
    /// </summary>
    /// <param name="repoName">Short name of the repository (used in the directory name).</param>
    /// <param name="repoUrl">Remote URL of the repository (with credentials if needed).</param>
    /// <param name="defaultBranch">Default branch to check out (e.g. "main", "develop").</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Absolute path to the clone directory.</returns>
    public async Task<string> EnsureCloneAsync(
        string repoName, string repoUrl, string defaultBranch, CancellationToken ct = default)
    {
        var clonePath = GetClonePath(repoName);

        if (Directory.Exists(Path.Combine(clonePath, ".git")))
        {
            _logger.LogInformation(
                "Brain clone exists for {Repo}, pulling latest on {Branch}",
                repoName, defaultBranch);

            await RunGitAsync(clonePath, ["fetch", "origin"], ct);
            await RunGitAsync(clonePath, ["checkout", defaultBranch], ct);
            await RunGitAsync(clonePath, ["reset", "--hard", $"origin/{defaultBranch}"], ct);
        }
        else
        {
            _logger.LogInformation(
                "Creating Brain clone for {Repo} from {Url} (branch: {Branch})",
                repoName, repoUrl, defaultBranch);

            Directory.CreateDirectory(_basePath);
            await RunGitAsync(_basePath,
                ["clone", "--branch", defaultBranch, repoUrl, $"brain-{repoName}"], ct);
        }

        return clonePath;
    }

    /// <summary>
    /// Returns the clone path for a repository without performing any git operations.
    /// </summary>
    public string GetClonePath(string repoName) =>
        Path.Combine(_basePath, $"brain-{repoName}");

    private static async Task RunGitAsync(string workingDir, string[] args, CancellationToken ct)
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

        var stdoutTask = process.StandardOutput.ReadToEndAsync(ct);
        var stderrTask = process.StandardError.ReadToEndAsync(ct);

        await Task.WhenAll(stdoutTask, stderrTask);
        await process.WaitForExitAsync(ct);

        if (process.ExitCode != 0)
        {
            var stderr = stderrTask.Result;
            throw new InvalidOperationException(
                $"git {string.Join(' ', args)} failed (exit {process.ExitCode}): {stderr}");
        }
    }
}
