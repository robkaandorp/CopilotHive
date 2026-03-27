using CopilotHive.Services;

namespace CopilotHive.Worker;

/// <summary>
/// Abstraction for Git operations, allowing testability of components that need to
/// perform Git operations. The default implementation uses <see cref="GitOperations"/>
/// static methods.
/// </summary>
public interface IGitOperations
{
    /// <summary>
    /// Clones a remote repository into the specified target directory.
    /// </summary>
    Task CloneRepositoryAsync(string url, string targetDir, CancellationToken ct);

    /// <summary>
    /// Checks out an existing branch in the specified repository directory.
    /// </summary>
    Task CheckoutBranchAsync(string repoDir, string branch, CancellationToken ct);

    /// <summary>
    /// Creates a new branch from the given base branch.
    /// </summary>
    Task CreateBranchAsync(string repoDir, string branchName, string baseBranch, CancellationToken ct);

    /// <summary>
    /// Force-pushes the specified branch to the remote origin.
    /// </summary>
    Task PushBranchAsync(string repoDir, string branch, CancellationToken ct);

    /// <summary>
    /// Retrieves current git status information for the repository at the given path.
    /// </summary>
    Task<GitChangeSummary> GetGitStatusAsync(string repoDir, string? baseBranch, CancellationToken ct);

    /// <summary>
    /// Returns true if the working directory has uncommitted changes.
    /// </summary>
    Task<bool> HasUncommittedChangesAsync(string repoDir, CancellationToken ct);

    /// <summary>
    /// Computes the merge-base (common ancestor) between a remote branch and HEAD.
    /// </summary>
    Task<string?> GetMergeBaseAsync(string repoDir, string baseBranch, CancellationToken ct);

    /// <summary>
    /// Run a git command and return (exitCode, stdout, stderr).
    /// </summary>
    Task<(int ExitCode, string Stdout, string Stderr)> RunGitCommandAsync(
        string workDir, string args, CancellationToken ct);

    /// <summary>
    /// Delete a directory with retries — on Windows, git processes may hold brief file locks.
    /// </summary>
    Task ForceDeleteDirectoryAsync(string path, int maxRetries = 5);
}

/// <summary>
/// Default implementation of <see cref="IGitOperations"/> that delegates to the static
/// <see cref="GitOperations"/> class. Used in production.
/// </summary>
public sealed class DefaultGitOperations : IGitOperations
{
    /// <inheritdoc/>
    public Task CloneRepositoryAsync(string url, string targetDir, CancellationToken ct)
        => GitOperations.CloneRepositoryAsync(url, targetDir, ct);

    /// <inheritdoc/>
    public Task CheckoutBranchAsync(string repoDir, string branch, CancellationToken ct)
        => GitOperations.CheckoutBranchAsync(repoDir, branch, ct);

    /// <inheritdoc/>
    public Task CreateBranchAsync(string repoDir, string branchName, string baseBranch, CancellationToken ct)
        => GitOperations.CreateBranchAsync(repoDir, branchName, baseBranch, ct);

    /// <inheritdoc/>
    public Task PushBranchAsync(string repoDir, string branch, CancellationToken ct)
        => GitOperations.PushBranchAsync(repoDir, branch, ct);

    /// <inheritdoc/>
    public Task<GitChangeSummary> GetGitStatusAsync(string repoDir, string? baseBranch, CancellationToken ct)
        => GitOperations.GetGitStatusAsync(repoDir, baseBranch, ct);

    /// <inheritdoc/>
    public Task<bool> HasUncommittedChangesAsync(string repoDir, CancellationToken ct)
        => GitOperations.HasUncommittedChangesAsync(repoDir, ct);

    /// <inheritdoc/>
    public Task<string?> GetMergeBaseAsync(string repoDir, string baseBranch, CancellationToken ct)
        => GitOperations.GetMergeBaseAsync(repoDir, baseBranch, ct);

    /// <inheritdoc/>
    public Task<(int ExitCode, string Stdout, string Stderr)> RunGitCommandAsync(
        string workDir, string args, CancellationToken ct)
        => GitOperations.RunGitCommandAsync(workDir, args, ct);

    /// <inheritdoc/>
    public Task ForceDeleteDirectoryAsync(string path, int maxRetries = 5)
        => GitOperations.ForceDeleteDirectoryAsync(path, maxRetries);
}