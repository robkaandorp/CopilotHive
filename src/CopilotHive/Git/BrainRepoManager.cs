using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace CopilotHive.Git;

/// <summary>
/// Result of a remote branch deletion attempt.
/// </summary>
public enum BranchDeleteResult
{
    /// <summary>The remote branch was successfully deleted.</summary>
    Success,

    /// <summary>The remote branch did not exist (already deleted or never pushed).</summary>
    NotFound,

    /// <summary>The deletion attempt failed due to a git or network error.</summary>
    Failed,
}

/// <summary>
/// Manages persistent clones of target repositories for the Brain.
/// </summary>
public interface IBrainRepoManager
{
    /// <summary>
    /// The directory containing all repo clones. Used as the Brain's CodingAgent WorkDirectory
    /// so the Brain can read files across all repositories via relative paths.
    /// </summary>
    string WorkDirectory { get; }

    /// <summary>
    /// Ensures a clone exists for the given repository and returns its path.
    /// If the clone already exists, pulls the latest changes on the default branch.
    /// </summary>
    /// <param name="repoName">Short name of the repository (used in the directory name).</param>
    /// <param name="repoUrl">Remote URL of the repository (with credentials if needed).</param>
    /// <param name="defaultBranch">Default branch to check out (e.g. "main", "develop").</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Absolute path to the clone directory.</returns>
    Task<string> EnsureCloneAsync(string repoName, string repoUrl, string defaultBranch, CancellationToken ct = default);

    /// <summary>
    /// Squash-merges a feature branch into the default branch and pushes.
    /// </summary>
    /// <param name="repoName">Repository name (must have been cloned via <see cref="EnsureCloneAsync"/>).</param>
    /// <param name="featureBranch">The feature branch to merge.</param>
    /// <param name="defaultBranch">The base branch to merge into.</param>
    /// <param name="commitMessage">The commit message for the resulting squash commit.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The full SHA-1 hash of the resulting squash commit on the default branch.</returns>
    Task<string> MergeFeatureBranchAsync(string repoName, string featureBranch, string defaultBranch, string commitMessage, CancellationToken ct = default);

    /// <summary>
    /// Deletes a remote feature branch from the specified repository.
    /// </summary>
    /// <param name="repoName">Short name of the repository.</param>
    /// <param name="branchName">Branch name to delete from the remote.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>
    /// <see cref="BranchDeleteResult.Success"/> if the branch was deleted,
    /// <see cref="BranchDeleteResult.NotFound"/> if the clone or branch did not exist,
    /// <see cref="BranchDeleteResult.Failed"/> if the git operation failed.
    /// </returns>
    Task<BranchDeleteResult> DeleteRemoteBranchAsync(string repoName, string branchName, CancellationToken ct = default);

    /// <summary>
    /// Returns the clone path for a repository without performing any git operations.
    /// </summary>
    /// <param name="repoName">Short name of the repository.</param>
    /// <returns>Absolute path to the clone directory.</returns>
    string GetClonePath(string repoName);

    /// <summary>
    /// Returns the current HEAD SHA from the local clone of the given repository, or <c>null</c>
    /// if the clone does not exist or the repository is empty (no commits yet).
    /// </summary>
    /// <param name="repoName">Short name of the repository.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The full SHA-1 hash of HEAD, or <c>null</c> when it cannot be determined.</returns>
    Task<string?> GetHeadShaAsync(string repoName, CancellationToken ct = default);

    /// <summary>
    /// Merges a source branch into a target branch (non-squash) and pushes the result.
    /// </summary>
    /// <param name="repoName">Short name of the repository (must have been cloned via <see cref="EnsureCloneAsync"/>).</param>
    /// <param name="sourceBranch">The branch to merge from.</param>
    /// <param name="targetBranch">The branch to merge into.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>
    /// The full SHA-1 hash of the resulting merge commit on the target branch, or <c>null</c>
    /// when the merge was a no-op (the target branch already contained the source).
    /// </returns>
    Task<string?> MergeBranchAsync(string repoName, string sourceBranch, string targetBranch, CancellationToken ct = default);

    /// <summary>
    /// Creates an annotated tag pointing at the given branch and pushes it.
    /// </summary>
    /// <param name="repoName">Short name of the repository.</param>
    /// <param name="tag">The tag name to create.</param>
    /// <param name="branch">The branch whose tip the tag should point at.</param>
    /// <param name="message">The annotation message for the tag.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns><c>true</c> if the tag was created and pushed; <c>false</c> otherwise.</returns>
    Task<bool> CreateTagAsync(string repoName, string tag, string branch, string message, CancellationToken ct = default);

    /// <summary>
    /// Deletes a tag from the remote (and local clone).
    /// </summary>
    /// <param name="repoName">Short name of the repository.</param>
    /// <param name="tag">The tag name to delete.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns><c>true</c> if the tag was deleted; <c>false</c> otherwise.</returns>
    Task<bool> DeleteTagAsync(string repoName, string tag, CancellationToken ct = default);
}

/// <summary>
/// Manages persistent clones of target repositories for the Brain.
/// Each repository gets its own clone at <c>{basePath}/repos/{repoName}</c>,
/// checked out to the default branch. The parent <c>repos/</c> directory serves
/// as the Brain's <see cref="WorkDirectory"/> so all repos are visible to file tools.
/// Clones persist across goals and are updated (pulled) before each goal starts.
/// The same clone is reused for merge operations to avoid redundant temp clones.
/// </summary>
public sealed class BrainRepoManager : IBrainRepoManager
{
    private readonly string _basePath;
    private readonly ILogger _logger;

    private readonly ConcurrentDictionary<string, SemaphoreSlim> _repoLocks =
        new(StringComparer.OrdinalIgnoreCase);

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
        ValidateRepoName(repoName);
        var semaphore = await AcquireRepoLockAsync(repoName, ct);
        try
        {
            var clonePath = GetClonePath(repoName);

            if (Directory.Exists(Path.Combine(clonePath, ".git")))
            {
                _logger.LogInformation(
                    "Brain clone exists for {Repo}, pulling latest on {Branch}",
                    repoName, defaultBranch);

                await RunGitAsync(clonePath, ["fetch", "origin"], ct);

                if (!await RemoteBranchExistsAsync(clonePath, defaultBranch, ct))
                {
                    _logger.LogWarning(
                        "Default branch '{Branch}' does not exist on origin for {Repo} — skipping checkout/reset (empty repository)",
                        defaultBranch, repoName);
                }
                else
                {
                    await RunGitAsync(clonePath, ["checkout", defaultBranch], ct);
                    await RunGitAsync(clonePath, ["reset", "--hard", $"origin/{defaultBranch}"], ct);
                }
            }
            else
            {
                _logger.LogInformation(
                    "Creating Brain clone for {Repo} from {Url} (branch: {Branch})",
                    repoName, repoUrl, defaultBranch);

                Directory.CreateDirectory(WorkDirectory);
                try
                {
                    await RunGitAsync(WorkDirectory,
                        ["clone", "--branch", defaultBranch, repoUrl, repoName], ct);
                }
                catch (InvalidOperationException ex) when (ex.Message.Contains("not found in upstream"))
                {
                    _logger.LogWarning(
                        "Branch '{Branch}' not found in upstream for {Repo} — retrying clone without --branch",
                        defaultBranch, repoName);

                    if (Directory.Exists(clonePath))
                        await ForceDeleteDirectoryAsync(clonePath);

                    await RunGitAsync(WorkDirectory, ["clone", repoUrl, repoName], ct);
                }

                // Configure git identity for merge commits
                await RunGitAsync(clonePath, ["config", "user.email", "copilothive@local"], ct);
                await RunGitAsync(clonePath, ["config", "user.name", "CopilotHive"], ct);
            }

            return clonePath;
        }
        finally
        {
            semaphore.Release();
        }
    }

    /// <summary>
    /// Squash-merges a feature branch into the default branch and pushes.
    /// All commits from the feature branch are combined into a single commit on the base branch.
    /// Uses the persistent brain clone instead of creating a temporary directory.
    /// On failure, the clone is reset to the remote state.
    /// <para>
    /// Special case — orphan feature branch: when the default branch does not yet exist on
    /// origin but the feature branch does, the default branch is created from the feature
    /// branch tip (fetch → checkout → push) and its HEAD commit hash is returned directly,
    /// bypassing the squash-merge flow.
    /// </para>
    /// <para>
    /// Truly empty repository: when neither branch exists, the merge is skipped and
    /// <see cref="string.Empty"/> is returned.
    /// </para>
    /// </summary>
    /// <param name="repoName">Repository name (must have been cloned via <see cref="EnsureCloneAsync"/>).</param>
    /// <param name="featureBranch">The feature branch to merge (e.g. "copilothive/add-logging").</param>
    /// <param name="defaultBranch">The base branch to merge into.</param>
    /// <param name="commitMessage">The commit message for the resulting squash commit.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>
    /// The full SHA-1 hash of the resulting commit on the default branch.
    /// Returns <see cref="string.Empty"/> only when both the default branch and the feature
    /// branch are absent on origin (truly empty repository).
    /// </returns>
    /// <exception cref="InvalidOperationException">Thrown when the merge fails (after reset).</exception>
    public async Task<string> MergeFeatureBranchAsync(
        string repoName, string featureBranch, string defaultBranch, string commitMessage,
        CancellationToken ct = default)
    {
        ValidateRepoName(repoName);
        var semaphore = await AcquireRepoLockAsync(repoName, ct);
        try
        {
            var clonePath = GetClonePath(repoName);
            if (!Directory.Exists(Path.Combine(clonePath, ".git")))
                throw new InvalidOperationException(
                    $"No brain clone found for '{repoName}'. Call EnsureCloneAsync first.");

            _logger.LogInformation("Squash-merging {Branch} into {Base} for {Repo}",
                featureBranch, defaultBranch, repoName);

            // Ensure we're on the base branch with latest remote state
            await RunGitAsync(clonePath, ["fetch", "origin"], ct);

            if (!await RemoteBranchExistsAsync(clonePath, defaultBranch, ct))
            {
                // Check whether the feature branch exists on origin
                if (!await RemoteBranchExistsAsync(clonePath, featureBranch, ct))
                {
                    // Truly empty repository — neither branch exists yet
                    _logger.LogWarning(
                        "Cannot merge into '{Branch}' for '{Repo}': neither the default branch nor the feature branch exists on origin (empty repository). Skipping merge.",
                        defaultBranch, repoName);
                    return string.Empty;
                }

                // Default branch is absent but the feature branch is present — create the default
                // branch from the feature branch tip so subsequent goals have a base to build on.
                _logger.LogInformation(
                    "Default branch '{DefaultBranch}' does not exist on origin for '{Repo}'. Creating it from feature branch '{FeatureBranch}'.",
                    defaultBranch, repoName, featureBranch);

                await RunGitAsync(clonePath, ["fetch", "origin", featureBranch], ct);
                await RunGitAsync(clonePath, ["checkout", "-B", defaultBranch, $"origin/{featureBranch}"], ct);
                await RunGitAsync(clonePath, ["push", "origin", defaultBranch], ct);

                var newBranchHash = await RunGitWithOutputAsync(clonePath, ["rev-parse", "HEAD"], ct);
                return newBranchHash.Trim();
            }

            await RunGitAsync(clonePath, ["checkout", defaultBranch], ct);
            await RunGitAsync(clonePath, ["reset", "--hard", $"origin/{defaultBranch}"], ct);

            // Fetch the feature branch and attempt squash merge
            await RunGitAsync(clonePath, ["fetch", "origin", featureBranch], ct);

            try
            {
                await RunGitAsync(clonePath,
                    ["merge", "--squash", $"origin/{featureBranch}"], ct);
            }
            catch (Exception mergeEx)
            {
                _logger.LogWarning(mergeEx, "Squash merge failed for {Repo} — resetting clone to clean state", repoName);

                // Abort the merge and reset to clean state
                try { await RunGitAsync(clonePath, ["merge", "--abort"], ct); } catch { }
                await RunGitAsync(clonePath, ["reset", "--hard", $"origin/{defaultBranch}"], ct);
                await RunGitAsync(clonePath, ["clean", "-fd"], ct);

                throw;
            }

            // Check whether the squash produced any staged changes before committing
            var statusResult = await RunGitWithOutputAsync(clonePath, ["status", "--porcelain"], ct);
            if (string.IsNullOrWhiteSpace(statusResult))
            {
                _logger.LogInformation(
                    "Squash merge of {Branch} into {Base} for {Repo} produced no changes — skipping commit",
                    featureBranch, defaultBranch, repoName);

                // Return the current HEAD since nothing new was committed
                var currentHash = await RunGitWithOutputAsync(clonePath, ["rev-parse", "HEAD"], ct);
                return currentHash.Trim();
            }

            // Commit the squashed changes as a single commit
            await RunGitAsync(clonePath, ["commit", "-m", commitMessage], ct);

            // Push the squash commit
            await RunGitAsync(clonePath, ["push", "origin", defaultBranch], ct);

            _logger.LogInformation("Successfully squash-merged {Branch} into {Base} for {Repo}",
                featureBranch, defaultBranch, repoName);

            var hashResult = await RunGitWithOutputAsync(clonePath, ["rev-parse", "HEAD"], ct);
            return hashResult.Trim();
        }
        finally
        {
            semaphore.Release();
        }
    }

    /// <summary>
    /// Returns <c>true</c> if <c>origin/{branch}</c> exists in the given clone; <c>false</c> otherwise.
    /// Uses <c>git rev-parse --verify</c> so it works correctly on empty repositories where the branch
    /// has not yet been pushed.
    /// </summary>
    /// <param name="clonePath">Absolute path to the local git clone.</param>
    /// <param name="branch">Branch name to check (without the <c>origin/</c> prefix).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns><c>true</c> if the remote-tracking ref exists; <c>false</c> if the repository is empty or the branch was not found.</returns>
    private static async Task<bool> RemoteBranchExistsAsync(string clonePath, string branch, CancellationToken ct)
    {
        var psi = new ProcessStartInfo("git")
        {
            WorkingDirectory = clonePath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        psi.ArgumentList.Add("rev-parse");
        psi.ArgumentList.Add("--verify");
        psi.ArgumentList.Add($"origin/{branch}");

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start git process");

        await process.WaitForExitAsync(ct);
        return process.ExitCode == 0;
    }

    /// <summary>
    /// Runs a git command and returns the standard output as a string.
    /// Throws <see cref="InvalidOperationException"/> with stderr on non-zero exit codes.
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
    /// Deletes a remote feature branch from the specified repository.
    /// Returns <see cref="BranchDeleteResult.NotFound"/> when the clone is absent.
    /// Returns <see cref="BranchDeleteResult.Success"/> on a clean push --delete.
    /// Returns <see cref="BranchDeleteResult.Failed"/> when git reports an error other than
    /// "remote ref not found" (i.e. a genuine failure rather than a missing branch).
    /// Also attempts to delete the local tracking branch; local-branch failure is silently ignored.
    /// </summary>
    /// <param name="repoName">Short name of the repository (must have been cloned via <see cref="EnsureCloneAsync"/>).</param>
    /// <param name="branchName">Branch name to delete from the remote (e.g. "copilothive/my-goal").</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task<BranchDeleteResult> DeleteRemoteBranchAsync(string repoName, string branchName, CancellationToken ct = default)
    {
        ValidateRepoName(repoName);
        var semaphore = await AcquireRepoLockAsync(repoName, ct);
        try
        {
            var clonePath = GetClonePath(repoName);
            if (!Directory.Exists(Path.Combine(clonePath, ".git")))
            {
                _logger.LogWarning(
                    "Cannot delete remote branch {Branch} from {Repo}: no clone found at {Path}",
                    branchName, repoName, clonePath);
                return BranchDeleteResult.NotFound;
            }

            BranchDeleteResult result;
            try
            {
                await RunGitAsync(clonePath, ["push", "origin", "--delete", branchName], ct);
                _logger.LogInformation("Deleted remote branch {Branch} from {Repo}", branchName, repoName);
                result = BranchDeleteResult.Success;
            }
            catch (Exception ex)
            {
                var message = ex.Message;
                // git reports "remote ref does not exist" or "error: unable to delete ... remote ref does not exist"
                // when the branch was already absent — that is not a failure.
                if (message.Contains("remote ref does not exist", StringComparison.OrdinalIgnoreCase) ||
                    message.Contains("did not match any", StringComparison.OrdinalIgnoreCase))
                {
                    _logger.LogInformation(
                        "Remote branch {Branch} does not exist on {Repo} — treating as already deleted",
                        branchName, repoName);
                    result = BranchDeleteResult.NotFound;
                }
                else
                {
                    _logger.LogWarning(ex, "Failed to delete remote branch {Branch} from {Repo}", branchName, repoName);
                    result = BranchDeleteResult.Failed;
                }
            }

            // Best-effort: delete the local tracking branch
            try
            {
                await RunGitAsync(clonePath, ["branch", "-D", branchName], ct);
            }
            catch
            {
                // Ignored — local branch may not exist
            }

            return result;
        }
        finally
        {
            semaphore.Release();
        }
    }

    /// <summary>
    /// Returns the clone path for a repository without performing any git operations.
    /// </summary>
    public string GetClonePath(string repoName) =>
        Path.Combine(WorkDirectory, repoName);

    /// <summary>
    /// Returns the current HEAD SHA from the local clone of the given repository, or <c>null</c>
    /// if the clone does not exist or the repository is empty (no commits yet).
    /// </summary>
    /// <param name="repoName">Short name of the repository.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The full SHA-1 hash of HEAD, or <c>null</c> when it cannot be determined.</returns>
    public async Task<string?> GetHeadShaAsync(string repoName, CancellationToken ct = default)
    {
        var clonePath = GetClonePath(repoName);
        if (!Directory.Exists(Path.Combine(clonePath, ".git")))
        {
            _logger.LogDebug("GetHeadShaAsync: no clone found for '{RepoName}' — returning null", repoName);
            return null;
        }

        try
        {
            var sha = await RunGitWithOutputAsync(clonePath, ["rev-parse", "HEAD"], ct);
            return sha.Trim();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "GetHeadShaAsync: could not read HEAD for '{RepoName}' (empty repo?) — returning null", repoName);
            return null;
        }
    }

    /// <summary>
    /// Deletes a directory with retries to handle transient file locks (e.g. from git processes on Windows).
    /// Clears read-only attributes before deletion so <c>.git</c> pack-files can be removed.
    /// </summary>
    /// <param name="path">The directory to delete.</param>
    /// <param name="maxRetries">Maximum number of attempts before giving up (default: 3).</param>
    private static async Task ForceDeleteDirectoryAsync(string path, int maxRetries = 3)
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

    /// <summary>
    /// Merges a source branch into a target branch (non-squash) and pushes the result.
    /// </summary>
    /// <param name="repoName">Repository name (must have been cloned via <see cref="EnsureCloneAsync"/>).</param>
    /// <param name="sourceBranch">The branch to merge from.</param>
    /// <param name="targetBranch">The branch to merge into.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>
    /// The full SHA-1 hash of the resulting merge commit on the target branch, or <c>null</c>
    /// when the merge was a no-op (the target branch already contained the source).
    /// </returns>
    /// <exception cref="MergeConflictException">Thrown when the merge fails due to conflicts.</exception>
    /// <exception cref="InvalidOperationException">Thrown when the clone is missing, a branch is absent, or the merge fails for another reason.</exception>
    public async Task<string?> MergeBranchAsync(
        string repoName, string sourceBranch, string targetBranch, CancellationToken ct = default)
    {
        ValidateRepoName(repoName);
        ValidateBranchOrTagName(sourceBranch);
        ValidateBranchOrTagName(targetBranch);

        var semaphore = await AcquireRepoLockAsync(repoName, ct);
        try
        {
            var clonePath = GetClonePath(repoName);
            if (!Directory.Exists(Path.Combine(clonePath, ".git")))
                throw new InvalidOperationException($"Repository '{repoName}' is not cloned.");

            _logger.LogInformation("Merging {Source} into {Target} for {Repo}",
                sourceBranch, targetBranch, repoName);

            await RunGitAsync(clonePath, ["fetch", "origin"], ct);

            // Clean worktree so checkout/merge operations are not blocked by leftover state.
            var status = await RunGitWithOutputAsync(clonePath, ["status", "--porcelain"], ct);
            if (!string.IsNullOrWhiteSpace(status))
            {
                await RunGitAsync(clonePath, ["reset", "--hard"], ct);
                await RunGitAsync(clonePath, ["clean", "-fd"], ct);
            }

            if (!await RemoteBranchExistsAsync(clonePath, targetBranch, ct))
                throw new InvalidOperationException(
                    $"Remote branch 'origin/{targetBranch}' does not exist for '{repoName}'.");

            if (!await RemoteBranchExistsAsync(clonePath, sourceBranch, ct))
                throw new InvalidOperationException(
                    $"Remote branch 'origin/{sourceBranch}' does not exist for '{repoName}'.");

            await RunGitAsync(clonePath, ["checkout", "-B", targetBranch, $"origin/{targetBranch}"], ct);

            var preMergeSha = (await RunGitWithOutputAsync(clonePath, ["rev-parse", "HEAD"], ct)).Trim();

            // Track whether the merge command was actually launched. This must NOT rely on the
            // merge exit code: if the caller's token is canceled mid-merge, RunGitCaptureAsync
            // throws before returning an exit code, so a boolean flag is the only reliable signal
            // that a merge may have left the worktree in a MERGE state needing cleanup.
            var mergeStarted = false;
            try
            {
                mergeStarted = true;
                var (exitCode, stdout, stderr) = await RunGitCaptureAsync(
                    clonePath, ["merge", $"origin/{sourceBranch}", "--no-edit"], ct);

                if (exitCode != 0)
                {
                    var combined = stdout + stderr;
                    if (combined.Contains("CONFLICT", StringComparison.OrdinalIgnoreCase))
                    {
                        _logger.LogWarning(
                            "Merge conflict merging {Source} into {Target} for {Repo}",
                            sourceBranch, targetBranch, repoName);
                        throw new MergeConflictException(repoName, sourceBranch, targetBranch);
                    }

                    throw new InvalidOperationException(
                        $"git merge origin/{sourceBranch} failed (exit {exitCode}): {stderr}");
                }

                // Merge succeeded cleanly — no MERGE state remains, so cleanup is not needed.
                mergeStarted = false;

                var postMergeSha = (await RunGitWithOutputAsync(clonePath, ["rev-parse", "HEAD"], ct)).Trim();
                if (postMergeSha == preMergeSha)
                {
                    _logger.LogInformation(
                        "Merge of {Source} into {Target} for {Repo} was a no-op (target already contained source)",
                        sourceBranch, targetBranch, repoName);
                    return null;
                }

                await RunGitAsync(clonePath, ["push", "origin", targetBranch], ct);

                _logger.LogInformation("Successfully merged {Source} into {Target} for {Repo}",
                    sourceBranch, targetBranch, repoName);

                return postMergeSha;
            }
            finally
            {
                // Run cleanup whenever a merge was started but did not complete cleanly (conflict,
                // other error, or caller cancellation). Use a fresh bounded token — NOT the caller's
                // token, which may already be canceled — so abort always runs to completion.
                if (mergeStarted)
                {
                    using var cleanupCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                    try
                    {
                        var (verifyExit, _, _) = await RunGitCaptureAsync(
                            clonePath, ["rev-parse", "--verify", "-q", "MERGE_HEAD"], cleanupCts.Token);
                        if (verifyExit == 0)
                            await RunGitCaptureAsync(clonePath, ["merge", "--abort"], cleanupCts.Token);
                    }
                    catch
                    {
                        // Best-effort cleanup — ignore failures.
                    }
                }
            }
        }
        finally
        {
            semaphore.Release();
        }
    }

    /// <summary>
    /// Acquires the per-repository lock, creating it on first use.
    /// </summary>
    private async Task<SemaphoreSlim> AcquireRepoLockAsync(string repoName, CancellationToken ct)
    {
        var semaphore = _repoLocks.GetOrAdd(repoName, _ => new SemaphoreSlim(1, 1));
        await semaphore.WaitAsync(ct);
        return semaphore;
    }

    /// <summary>
    /// Validates that a repository name is a safe single path segment directly under <see cref="WorkDirectory"/>.
    /// <para>
    /// Containment is enforced in two stages: first both <see cref="WorkDirectory"/> and the clone path
    /// are resolved via <see cref="Path.GetFullPath(string)"/> (which only performs <em>lexical</em>
    /// normalization of <c>.</c> and <c>..</c> segments — it does NOT resolve filesystem symlinks or
    /// junctions), and the canonical parent of the clone path must equal the resolved work directory.
    /// Second, if the clone path exists and is a symlink/junction, its real target is resolved via
    /// <see cref="Directory.ResolveLinkTarget(string, bool)"/> and the target's parent must also equal
    /// the resolved work directory. This second check defeats symlink-based path traversal where a link
    /// inside the work directory points to an external location.
    /// </para>
    /// </summary>
    private void ValidateRepoName(string repoName)
    {
        if (string.IsNullOrWhiteSpace(repoName))
            throw new ArgumentException("Repository name must not be null or whitespace.", nameof(repoName));

        if (repoName.StartsWith('-'))
            throw new ArgumentException($"Repository name '{repoName}' must not start with '-'.", nameof(repoName));

        if (repoName.Contains('/') || repoName.Contains('\\') || repoName.Contains(".."))
            throw new ArgumentException(
                $"Repository name '{repoName}' must be a single path segment (no '/', '\\', or '..').", nameof(repoName));

        // Stage 1 — lexical containment: Path.GetFullPath only resolves '.'/'..' textually, not symlinks.
        var workDirFull = Path.GetFullPath(WorkDirectory);
        var resolved = Path.GetFullPath(Path.Combine(workDirFull, repoName));
        var parent = Path.GetDirectoryName(resolved);
        if (!string.Equals(parent, workDirFull, StringComparison.Ordinal))
            throw new ArgumentException(
                $"Repository name '{repoName}' does not resolve to a direct child of the work directory.", nameof(repoName));

        // Stage 2 — symlink-safe containment: if the clone path exists and is an actual filesystem
        // symlink/junction, resolve its real target and ensure the target also stays directly under
        // the work directory. This prevents a link like {WorkDirectory}/evil -> /etc from escaping.
        // Directory.ResolveLinkTarget throws if the path does not exist, so guard on existence first.
        if (Directory.Exists(resolved) || File.Exists(resolved))
        {
            var linkTarget = Directory.ResolveLinkTarget(resolved, returnFinalTarget: true);
            if (linkTarget is not null)
            {
                var targetFull = Path.GetFullPath(linkTarget.FullName);
                var targetParent = Path.GetDirectoryName(targetFull);
                if (!string.Equals(targetParent, workDirFull, StringComparison.Ordinal))
                    throw new ArgumentException(
                        $"Repository name '{repoName}' resolves through a symlink that escapes the work directory.", nameof(repoName));
            }
        }
    }

    /// <summary>
    /// Validates that a branch or tag name is safe to pass to git as an argument: it must not
    /// enable option injection and must satisfy git's <c>check-ref-format</c> rules.
    /// </summary>
    private static void ValidateBranchOrTagName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("Branch or tag name must not be null or whitespace.", nameof(name));

        if (name.StartsWith('-'))
            throw new ArgumentException($"Branch or tag name '{name}' must not start with '-'.", nameof(name));

        // Reject control characters (anything below 0x20) and DEL.
        foreach (var c in name)
        {
            if (c < 0x20 || c == 0x7f)
                throw new ArgumentException(
                    $"Branch or tag name '{name}' contains control characters.", nameof(name));
        }

        char[] forbidden = [' ', '~', '^', ':', '?', '*', '[', '\\'];
        if (name.IndexOfAny(forbidden) >= 0)
            throw new ArgumentException(
                $"Branch or tag name '{name}' contains forbidden characters.", nameof(name));

        // git check-ref-format rules that the character list above does not cover.
        if (name.Contains(".."))
            throw new ArgumentException($"Branch or tag name '{name}' must not contain '..'.", nameof(name));
        if (name.Contains("@{"))
            throw new ArgumentException($"Branch or tag name '{name}' must not contain '@{{'.", nameof(name));
        if (name.Contains("//"))
            throw new ArgumentException($"Branch or tag name '{name}' must not contain '//'.", nameof(name));
        if (name == "@")
            throw new ArgumentException("Branch or tag name must not be a lone '@'.", nameof(name));
        if (name.StartsWith('.') || name.EndsWith('.'))
            throw new ArgumentException($"Branch or tag name '{name}' must not begin or end with '.'.", nameof(name));
        if (name.StartsWith('/') || name.EndsWith('/'))
            throw new ArgumentException($"Branch or tag name '{name}' must not begin or end with '/'.", nameof(name));
        if (name.EndsWith(".lock", StringComparison.Ordinal))
            throw new ArgumentException($"Branch or tag name '{name}' must not end with '.lock'.", nameof(name));
    }

    /// <summary>
    /// Runs a git command capturing exit code, stdout, and stderr without throwing on non-zero exit.
    /// </summary>
    /// <param name="workingDir">Working directory for the git process.</param>
    /// <param name="args">Arguments to pass to git.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The exit code, standard output, and standard error of the git command.</returns>
    private static async Task<(int exitCode, string stdout, string stderr)> RunGitCaptureAsync(
        string workingDir, string[] args, CancellationToken ct)
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

        try
        {
            await Task.WhenAll(stdoutTask, stderrTask);
            await process.WaitForExitAsync(ct);
        }
        catch (OperationCanceledException)
        {
            // The caller's token was canceled while the git process was still running.
            // WaitForExitAsync/ReadToEndAsync do NOT terminate the child process, so kill the
            // whole process tree and wait (bounded) for it to actually exit before returning.
            // This guarantees the process is dead before any post-cancellation cleanup runs.
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                    process.WaitForExit(TimeSpan.FromSeconds(5));
                }
            }
            catch
            {
                // Best-effort — the process may have already exited.
            }
            throw;
        }

        return (process.ExitCode, stdoutTask.Result, stderrTask.Result);
    }

    /// <summary>
    /// Creates an annotated tag pointing at the tip of the given branch and pushes it to origin.
    /// </summary>
    /// <param name="repoName">Repository name (must have been cloned via <see cref="EnsureCloneAsync"/>).</param>
    /// <param name="tag">The tag name to create.</param>
    /// <param name="branch">The branch whose tip the tag should point at.</param>
    /// <param name="message">The annotation message for the tag.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns><c>true</c> if the tag was created and pushed; <c>false</c> if the tag already exists on origin.</returns>
    /// <exception cref="InvalidOperationException">Thrown when the clone is missing, the branch is absent, or git fails.</exception>
    public async Task<bool> CreateTagAsync(string repoName, string tag, string branch, string message, CancellationToken ct = default)
    {
        ValidateRepoName(repoName);
        ValidateBranchOrTagName(tag);
        ValidateBranchOrTagName(branch);

        if (string.IsNullOrWhiteSpace(message))
            throw new ArgumentException("Tag message must not be null or whitespace.", nameof(message));
        if (message.StartsWith('-'))
            throw new ArgumentException($"Tag message '{message}' must not start with '-'.", nameof(message));

        var semaphore = await AcquireRepoLockAsync(repoName, ct);
        try
        {
            var clonePath = GetClonePath(repoName);
            if (!Directory.Exists(Path.Combine(clonePath, ".git")))
                throw new InvalidOperationException($"Repository '{repoName}' is not cloned.");

            _logger.LogInformation("Creating tag {Tag} on {Branch} for {Repo}", tag, branch, repoName);

            // Check whether the tag already exists on origin (no fetch needed).
            var (lsExit, lsStdout, lsStderr) = await RunGitCaptureAsync(
                clonePath, ["ls-remote", "--tags", "origin", $"refs/tags/{tag}"], ct);
            if (lsExit != 0)
                throw new InvalidOperationException(
                    $"Failed to query remote tags for '{repoName}': {lsStderr}");
            if (!string.IsNullOrWhiteSpace(lsStdout))
            {
                _logger.LogInformation("Tag {Tag} already exists on origin for {Repo} — skipping", tag, repoName);
                return false;
            }

            // Fetch the branch (without tags) so we can point the tag at its tip.
            await RunGitAsync(clonePath,
                ["fetch", "--no-tags", "origin", $"+refs/heads/{branch}:refs/remotes/origin/{branch}"], ct);

            if (!await RemoteBranchExistsAsync(clonePath, branch, ct))
                throw new InvalidOperationException(
                    $"Remote branch 'origin/{branch}' does not exist for '{repoName}'.");

            await RunGitAsync(clonePath, ["checkout", "-B", branch, $"origin/{branch}"], ct);

            // Delete any stale local tag with the same name before recreating it.
            var localTag = await RunGitWithOutputAsync(clonePath, ["tag", "-l", tag], ct);
            if (!string.IsNullOrWhiteSpace(localTag))
                await RunGitAsync(clonePath, ["tag", "-d", tag], ct);

            await RunGitAsync(clonePath, ["tag", "-a", tag, "-m", message], ct);
            await RunGitAsync(clonePath, ["push", "origin", tag], ct);

            _logger.LogInformation("Successfully created and pushed tag {Tag} for {Repo}", tag, repoName);
            return true;
        }
        finally
        {
            semaphore.Release();
        }
    }

    /// <summary>
    /// Deletes a tag from the remote and the local clone.
    /// </summary>
    /// <param name="repoName">Repository name (must have been cloned via <see cref="EnsureCloneAsync"/>).</param>
    /// <param name="tag">The tag name to delete.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns><c>true</c> if the tag was deleted from at least one location; <c>false</c> if it did not exist anywhere.</returns>
    /// <exception cref="InvalidOperationException">Thrown when the clone is missing, existence cannot be determined, or both deletions fail.</exception>
    public async Task<bool> DeleteTagAsync(string repoName, string tag, CancellationToken ct = default)
    {
        ValidateRepoName(repoName);
        ValidateBranchOrTagName(tag);

        var semaphore = await AcquireRepoLockAsync(repoName, ct);
        try
        {
            var clonePath = GetClonePath(repoName);
            if (!Directory.Exists(Path.Combine(clonePath, ".git")))
                throw new InvalidOperationException($"Repository '{repoName}' is not cloned.");

            _logger.LogInformation("Deleting tag {Tag} for {Repo}", tag, repoName);

            var (remoteExit, remoteStdout, remoteStderr) = await RunGitCaptureAsync(
                clonePath, ["ls-remote", "--tags", "origin", $"refs/tags/{tag}"], ct);
            if (remoteExit != 0)
                throw new InvalidOperationException(
                    $"Failed to query remote tags for '{repoName}': {remoteStderr}");

            var (localExit, localStdout, localStderr) = await RunGitCaptureAsync(
                clonePath, ["tag", "-l", tag], ct);
            if (localExit != 0)
                throw new InvalidOperationException(
                    $"Failed to query local tags for '{repoName}': {localStderr}");

            var remoteExists = !string.IsNullOrWhiteSpace(remoteStdout);
            var localExists = !string.IsNullOrWhiteSpace(localStdout);

            if (!remoteExists && !localExists)
            {
                _logger.LogInformation("Tag {Tag} does not exist locally or on origin for {Repo}", tag, repoName);
                return false;
            }

            var anyDeleted = false;
            string? localError = null;
            string? remoteError = null;

            // OperationCanceledException from RunGitCaptureAsync propagates out of this method
            // (it is not caught here), rather than being recorded as a partial deletion error.
            if (localExists)
            {
                var (delExit, _, delStderr) = await RunGitCaptureAsync(clonePath, ["tag", "-d", tag], ct);
                if (delExit == 0)
                    anyDeleted = true;
                else
                    localError = delStderr;
            }

            if (remoteExists)
            {
                var (pushExit, _, pushStderr) = await RunGitCaptureAsync(
                    clonePath, ["push", "origin", $":refs/tags/{tag}"], ct);
                if (pushExit == 0)
                    anyDeleted = true;
                else
                    remoteError = pushStderr;
            }

            if (anyDeleted)
            {
                // At least one side was deleted. Any failure on the other side is logged but
                // does not fail the operation.
                if (localError is not null)
                    _logger.LogWarning("Local tag delete failed for {Tag} in {Repo}: {Error}", tag, repoName, localError);
                if (remoteError is not null)
                    _logger.LogWarning("Remote tag delete failed for {Tag} in {Repo}: {Error}", tag, repoName, remoteError);

                _logger.LogInformation("Successfully deleted tag {Tag} for {Repo}", tag, repoName);
                return true;
            }

            // At least one location had the tag (checked above) but ZERO deletions succeeded —
            // this covers both "existed on both, both failed" and "existed on one side, that
            // sole deletion failed". Surface a genuine failure.
            throw new InvalidOperationException(
                $"Failed to delete tag '{tag}' for '{repoName}'. Local error: {localError ?? "(n/a)"}; Remote error: {remoteError ?? "(n/a)"}");
        }
        finally
        {
            semaphore.Release();
        }
    }
}
