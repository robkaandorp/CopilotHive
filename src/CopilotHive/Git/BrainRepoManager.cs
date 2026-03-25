using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace CopilotHive.Git;

/// <summary>
/// Manages persistent clones of target repositories for the Brain.
/// Each repository gets its own clone at <c>{basePath}/repos/{repoName}</c>,
/// checked out to the default branch. The parent <c>repos/</c> directory serves
/// as the Brain's <see cref="WorkDirectory"/> so all repos are visible to file tools.
/// Clones persist across goals and are updated (pulled) before each goal starts.
/// The same clone is reused for merge operations to avoid redundant temp clones.
/// </summary>
public sealed class BrainRepoManager
{
    private readonly string _basePath;
    private readonly ILogger _logger;

    /// <summary>
    /// Initialises a new <see cref="BrainRepoManager"/>.
    /// </summary>
    /// <param name="basePath">
    /// Root directory for Brain state (e.g. <c>/app/state</c>).
    /// Repo clones are created at <c>{basePath}/repos/{repoName}</c>.
    /// </param>
    /// <param name="logger">Logger instance.</param>
    public BrainRepoManager(string basePath, ILogger<BrainRepoManager> logger)
    {
        _basePath = Path.GetFullPath(basePath);
        _logger = logger;
        Directory.CreateDirectory(WorkDirectory);
    }

    /// <summary>
    /// The directory containing all repo clones. Used as the Brain's CodingAgent WorkDirectory
    /// so the Brain can read files across all repositories via relative paths.
    /// </summary>
    public string WorkDirectory => Path.Combine(_basePath, "repos");

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

            Directory.CreateDirectory(WorkDirectory);
            await RunGitAsync(WorkDirectory,
                ["clone", "--branch", defaultBranch, repoUrl, repoName], ct);

            // Configure git identity for merge commits
            await RunGitAsync(clonePath, ["config", "user.email", "copilothive@local"], ct);
            await RunGitAsync(clonePath, ["config", "user.name", "CopilotHive"], ct);
        }

        return clonePath;
    }

    /// <summary>
    /// Merges a feature branch into the default branch and pushes.
    /// Uses the persistent brain clone instead of creating a temporary directory.
    /// On failure, the clone is reset to the remote state.
    /// </summary>
    /// <param name="repoName">Repository name (must have been cloned via <see cref="EnsureCloneAsync"/>).</param>
    /// <param name="featureBranch">The feature branch to merge (e.g. "copilothive/add-logging").</param>
    /// <param name="defaultBranch">The base branch to merge into.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The full SHA-1 hash of the resulting merge commit on the default branch.</returns>
    /// <exception cref="InvalidOperationException">Thrown when the merge fails (after reset).</exception>
    public async Task<string> MergeFeatureBranchAsync(
        string repoName, string featureBranch, string defaultBranch, CancellationToken ct = default)
    {
        var clonePath = GetClonePath(repoName);
        if (!Directory.Exists(Path.Combine(clonePath, ".git")))
            throw new InvalidOperationException(
                $"No brain clone found for '{repoName}'. Call EnsureCloneAsync first.");

        _logger.LogInformation("Merging {Branch} into {Base} for {Repo}",
            featureBranch, defaultBranch, repoName);

        // Ensure we're on the base branch with latest remote state
        await RunGitAsync(clonePath, ["fetch", "origin"], ct);
        await RunGitAsync(clonePath, ["checkout", defaultBranch], ct);
        await RunGitAsync(clonePath, ["reset", "--hard", $"origin/{defaultBranch}"], ct);

        // Fetch the feature branch and attempt merge
        await RunGitAsync(clonePath, ["fetch", "origin", featureBranch], ct);

        try
        {
            await RunGitAsync(clonePath,
                ["merge", $"origin/{featureBranch}", "--no-edit"], ct);
        }
        catch (Exception mergeEx)
        {
            _logger.LogWarning(mergeEx, "Merge failed for {Repo} — resetting clone to clean state", repoName);

            // Abort the merge and reset to clean state
            try { await RunGitAsync(clonePath, ["merge", "--abort"], ct); } catch { }
            await RunGitAsync(clonePath, ["reset", "--hard", $"origin/{defaultBranch}"], ct);
            await RunGitAsync(clonePath, ["clean", "-fd"], ct);

            throw;
        }

        // Push the merge
        await RunGitAsync(clonePath, ["push", "origin", defaultBranch], ct);

        _logger.LogInformation("Successfully merged {Branch} into {Base} for {Repo}",
            featureBranch, defaultBranch, repoName);

        var hashResult = await RunGitWithOutputAsync(clonePath, ["rev-parse", "HEAD"], ct);
        return hashResult.Trim();
    }

    /// <summary>
    /// Runs a git command and returns the standard output as a string.
    /// Does not throw on non-zero exit codes.
    /// </summary>
    /// <param name="workingDir">Working directory for the git process.</param>
    /// <param name="args">Arguments to pass to git.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The standard output of the git command.</returns>
    private static async Task<string> RunGitWithOutputAsync(string workingDir, string[] args, CancellationToken ct)
    {
        var psi = new System.Diagnostics.ProcessStartInfo("git", string.Join(" ", args))
        {
            WorkingDirectory = workingDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        using var process = System.Diagnostics.Process.Start(psi)!;
        await process.WaitForExitAsync(ct);
        if (process.ExitCode != 0)
        {
            var stderr = await process.StandardError.ReadToEndAsync(ct);
            throw new InvalidOperationException($"git rev-parse HEAD failed: {stderr}");
        }
        return await process.StandardOutput.ReadToEndAsync(ct);
    }

    /// <summary>
    /// Returns the clone path for a repository without performing any git operations.
    /// </summary>
    public string GetClonePath(string repoName) =>
        Path.Combine(WorkDirectory, repoName);

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
