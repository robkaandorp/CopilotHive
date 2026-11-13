using CopilotHive.Services;

namespace CopilotHive.Worker;

/// <summary>
/// The operation-scoped credential transport for ONE authenticated worker-side git launch: the
/// credential-free origin the launch must run against, plus the CHILD-ONLY environment that carries
/// the credential to the git process (and to nothing else).
/// </summary>
/// <remarks>
/// <para>
/// It exists so <see cref="IGitOperations"/> can carry credential CONTEXT without ever receiving the
/// secret as an argument: <see cref="GitOperations.CreateProcessStartInfo"/> hands the environment to
/// the child, and the argument list keeps the sanitized URL.
/// </para>
/// <para>
/// <see cref="SanitizedOriginUrl"/> is credential-FREE by construction; it is what a credentialed
/// launch repairs a persisted <c>origin</c> to before any network command runs (the <c>set-url</c>
/// repair precedent of the config seam).
/// </para>
/// </remarks>
public interface IWorkerGitCredentialTransport
{
    /// <summary>
    /// The credential-free origin this launch must run against. A credentialed launch repairs the
    /// persisted <c>origin</c> to this value FIRST, so a host-injected credential origin can never
    /// survive into a network operation.
    /// </summary>
    string SanitizedOriginUrl { get; }

    /// <summary>
    /// Builds the COMPLETE child environment for one launch: every inherited credential-bearing
    /// channel scrubbed, the operation-scoped askpass wiring added, and the credential present ONLY
    /// here.
    /// </summary>
    IReadOnlyDictionary<string, string?> BuildChildEnvironment();
}

/// <summary>
/// Abstraction for Git operations, allowing testability of components that need to
/// perform Git operations. The default implementation uses <see cref="GitOperations"/>
/// static methods.
/// </summary>
/// <remarks>
/// The three NETWORK operations (clone, create-branch and push) accept an optional
/// <see cref="IWorkerGitCredentialTransport"/>. A <c>null</c> transport means the launch is
/// UNCREDENTIALED — no credential exists for it, so nothing is released. Every member here keeps its
/// original three-argument shape available through the interface's own default implementations, so an
/// implementation that never carries credentials (the worker's fakes) stays valid and, when it IS
/// handed a transport, refuses rather than silently dropping the credential.
/// </remarks>
public interface IGitOperations
{
    /// <summary>
    /// Clones a remote repository into the specified target directory.
    /// </summary>
    Task CloneRepositoryAsync(string url, string targetDir, CancellationToken ct);

    /// <summary>
    /// Clones a remote repository into the specified target directory, releasing
    /// <paramref name="credentialTransport"/>'s credential through the git CHILD ENVIRONMENT only.
    /// </summary>
    /// <param name="url">The credential-free clone URL.</param>
    /// <param name="targetDir">Local path where the repository will be cloned.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <param name="credentialTransport">
    /// The credential transport, or <c>null</c> for an uncredentialed clone.
    /// </param>
    /// <remarks>
    /// The default implementation refuses a non-null transport: an implementation that cannot carry a
    /// credential must not silently run an authenticated operation WITHOUT it (which would surface as
    /// a confusing authentication failure) — it fails loudly, and no secret is ever involved.
    /// </remarks>
    Task CloneRepositoryAsync(
        string url, string targetDir, CancellationToken ct,
        IWorkerGitCredentialTransport? credentialTransport)
        => credentialTransport is null
            ? CloneRepositoryAsync(url, targetDir, ct)
            : throw new NotSupportedException(
                $"{GetType().Name} does not support credential-bearing clone operations.");

    /// <summary>
    /// Checks out an existing branch in the specified repository directory.
    /// </summary>
    Task CheckoutBranchAsync(string repoDir, string branch, CancellationToken ct);

    /// <summary>
    /// Creates a new branch from the given base branch.
    /// </summary>
    Task CreateBranchAsync(string repoDir, string branchName, string baseBranch, CancellationToken ct);

    /// <summary>
    /// Creates a new branch from the given base branch, releasing
    /// <paramref name="credentialTransport"/>'s credential for the internal base-branch FETCH that
    /// runs when the base branch is not present locally.
    /// </summary>
    /// <param name="repoDir">Path to the local git repository.</param>
    /// <param name="branchName">Name of the new branch to create.</param>
    /// <param name="baseBranch">The branch to base the new branch on.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <param name="credentialTransport">
    /// The credential transport, or <c>null</c> when the assignment carries no credential.
    /// </param>
    Task CreateBranchAsync(
        string repoDir, string branchName, string baseBranch, CancellationToken ct,
        IWorkerGitCredentialTransport? credentialTransport)
        => credentialTransport is null
            ? CreateBranchAsync(repoDir, branchName, baseBranch, ct)
            : throw new NotSupportedException(
                $"{GetType().Name} does not support credential-bearing branch operations.");

    /// <summary>
    /// Force-pushes the specified branch to the remote origin.
    /// </summary>
    Task PushBranchAsync(string repoDir, string branch, CancellationToken ct);

    /// <summary>
    /// Force-pushes the specified branch to the remote origin, repairing the persisted origin to
    /// <see cref="IWorkerGitCredentialTransport.SanitizedOriginUrl"/> first and releasing the
    /// credential through the git CHILD ENVIRONMENT only.
    /// </summary>
    /// <param name="repoDir">Path to the local git repository.</param>
    /// <param name="branch">Name of the branch to push.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <param name="credentialTransport">
    /// The credential transport, or <c>null</c> for an uncredentialed push.
    /// </param>
    Task PushBranchAsync(
        string repoDir, string branch, CancellationToken ct,
        IWorkerGitCredentialTransport? credentialTransport)
        => credentialTransport is null
            ? PushBranchAsync(repoDir, branch, ct)
            : throw new NotSupportedException(
                $"{GetType().Name} does not support credential-bearing push operations.");

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
        => GitOperations.CloneRepositoryAsync(url, targetDir, ct, credentialTransport: null);

    /// <inheritdoc/>
    public Task CloneRepositoryAsync(
        string url, string targetDir, CancellationToken ct,
        IWorkerGitCredentialTransport? credentialTransport)
        => GitOperations.CloneRepositoryAsync(url, targetDir, ct, credentialTransport);

    /// <inheritdoc/>
    public Task CheckoutBranchAsync(string repoDir, string branch, CancellationToken ct)
        => GitOperations.CheckoutBranchAsync(repoDir, branch, ct);

    /// <inheritdoc/>
    public Task CreateBranchAsync(string repoDir, string branchName, string baseBranch, CancellationToken ct)
        => GitOperations.CreateBranchAsync(repoDir, branchName, baseBranch, ct, credentialTransport: null);

    /// <inheritdoc/>
    public Task CreateBranchAsync(
        string repoDir, string branchName, string baseBranch, CancellationToken ct,
        IWorkerGitCredentialTransport? credentialTransport)
        => GitOperations.CreateBranchAsync(repoDir, branchName, baseBranch, ct, credentialTransport);

    /// <inheritdoc/>
    public Task PushBranchAsync(string repoDir, string branch, CancellationToken ct)
        => GitOperations.PushBranchAsync(repoDir, branch, ct);

    /// <inheritdoc/>
    public Task PushBranchAsync(
        string repoDir, string branch, CancellationToken ct,
        IWorkerGitCredentialTransport? credentialTransport)
        => GitOperations.PushBranchAsync(repoDir, branch, ct, credentialTransport);

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
