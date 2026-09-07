using System.Collections.Concurrent;
using System.Diagnostics;
using CopilotHive.Services;
using CopilotHive.Shared;
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

    /// <summary>
    /// Lists all remote branches from the repository's origin, excluding the symbolic <c>HEAD</c> ref.
    /// </summary>
    /// <param name="repoName">Short name of the repository (must have been cloned via <see cref="EnsureCloneAsync"/>).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A sorted list of branch names (without the <c>origin/</c> prefix).</returns>
    Task<List<string>> ListRemoteBranchesAsync(string repoName, CancellationToken ct = default);

    /// <summary>
    /// Refreshes the clone's <c>origin</c> credential and fetches from <c>origin</c> in ONE
    /// locked operation, so no unlocked window can ever separate the refresh from the fetch it
    /// authenticates.
    /// </summary>
    /// <remarks>
    /// This is the ONLY supported way for an external caller to run an authenticated
    /// <c>origin</c> fetch against a Brain clone. A caller that refreshes and then fetches by
    /// itself would race a concurrent operation that re-points <c>origin</c> in between.
    /// </remarks>
    /// <param name="repoName">Short name of the repository (must have been cloned via <see cref="EnsureCloneAsync"/>).</param>
    /// <param name="branch">
    /// Optional single branch to fetch. When supplied, the FORCED remote-tracking refspec
    /// <c>+refs/heads/{branch}:refs/remotes/origin/{branch}</c> is used so a non-fast-forward
    /// remote update still refreshes the tracking ref. When <c>null</c>/blank, all branches are
    /// fetched with a plain <c>git fetch origin</c>.
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>
    /// The outcome of the fetch, with all text ALREADY REDACTED — a caller may surface
    /// <see cref="BrainFetchResult.Output"/> and <see cref="BrainFetchResult.Error"/> verbatim.
    /// </returns>
    Task<BrainFetchResult> FetchOriginAsync(string repoName, string? branch = null, CancellationToken ct = default);
}

/// <summary>
/// A single git invocation handed to the optional <see cref="BrainRepoManager"/> runner seam.
/// </summary>
/// <param name="WorkingDirectory">Working directory the git process runs in.</param>
/// <param name="Arguments">The git arguments, already split into individual tokens.</param>
public readonly record struct BrainGitRequest(string WorkingDirectory, IReadOnlyList<string> Arguments);

/// <summary>
/// The RAW result of a git invocation performed through the <see cref="BrainRepoManager"/>
/// runner seam.
/// </summary>
/// <remarks>
/// Deliberately raw: the seam never redacts. <see cref="BrainRepoManager"/> itself remains
/// responsible for constructing — and redacting — every log line and failure message, so a fake
/// runner returning credential-bearing stderr exercises the PRODUCTION redaction path.
/// </remarks>
/// <param name="ExitCode">The git process exit code.</param>
/// <param name="Stdout">Raw standard output.</param>
/// <param name="Stderr">Raw standard error.</param>
public readonly record struct BrainGitResult(int ExitCode, string Stdout, string Stderr);

/// <summary>
/// The outcome of <see cref="IBrainRepoManager.FetchOriginAsync"/>.
/// </summary>
/// <remarks>
/// Both text fields are ALREADY REDACTED by the manager (URL scanner plus a literal pass over
/// the operation's selected credential), so a caller — including a Composer tool that echoes
/// them straight back to the model — can surface them verbatim without leaking a token.
/// </remarks>
/// <param name="Success"><c>true</c> when git exited zero.</param>
/// <param name="Output">Redacted standard output of the fetch. Empty on failure.</param>
/// <param name="Error">
/// The redacted failure description when <paramref name="Success"/> is <c>false</c>;
/// <c>null</c> on success.
/// </param>
public readonly record struct BrainFetchResult(bool Success, string Output, string? Error);

/// <summary>
/// Manages persistent clones of target repositories for the Brain.
/// Each repository gets its own clone at <c>{basePath}/repos/{repoName}</c>,
/// checked out to the default branch. The parent <c>repos/</c> directory serves
/// as the Brain's <see cref="WorkDirectory"/> so all repos are visible to file tools.
/// Clones persist across goals and are updated (pulled) before each goal starts.
/// The same clone is reused for merge operations to avoid redundant temp clones.
/// <para>
/// <b>Credentials.</b> Every NETWORK-BEARING operation resolves ONE credential for its whole
/// duration — the stored OAuth admin token (via the optional live lookup) falling back to the
/// <c>GH_TOKEN</c>/<c>GITHUB_TOKEN</c> environment chain — and refreshes the clone's
/// <c>origin</c> with it immediately before the operation's FIRST network command, under the
/// per-repository lock. Only the repository's currently CONFIGURED, eligible URL (parsed HTTPS,
/// host exactly <c>github.com</c>, effective port 443) is ever credentialed. When no credential
/// resolves, the persisted origin is left completely untouched: a stale credential can survive in
/// <c>.git/config</c> until a later successful resolution replaces it — there is no revocation
/// cleanup and no guarantee of secret-free on-disk origins.
/// </para>
/// </summary>
public sealed class BrainRepoManager : IBrainRepoManager
{
    private readonly string _basePath;
    private readonly ILogger _logger;

    /// <summary>
    /// Optional substitute for the real git process invocation. <c>null</c> in production, where
    /// the private process-based runner is used.
    /// </summary>
    private readonly Func<BrainGitRequest, BrainGitResult>? _gitRunner;

    /// <summary>
    /// Optional LIVE lookup of the stored OAuth admin credential. <c>null</c> when the manager is
    /// constructed without an OAuth bridge (direct construction, tests, unconfigured deployments).
    /// </summary>
    private readonly Func<CancellationToken, Task<string?>>? _tokenLookup;

    /// <summary>
    /// Optional LIVE lookup of the currently configured URL of a named repository. <c>null</c>
    /// when the manager is constructed without configuration access. It is invoked at CALL time
    /// (never captured as a startup snapshot) so a configuration reload is honoured immediately.
    /// </summary>
    private readonly Func<string, string?>? _configuredUrlLookup;

    /// <summary>The placeholder substituted for a raw credential in any constructed message.</summary>
    private const string CredentialPlaceholder = "[redacted]";

    /// <summary>The userinfo user name GitHub accepts alongside a token password.</summary>
    private const string CredentialUserName = "x-access-token";

    /// <summary>The only host whose HTTPS URLs may receive the admin credential.</summary>
    private const string GitHubHost = "github.com";

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
    /// <param name="gitRunner">
    /// Optional substitute for the real git process invocation. When <c>null</c> (the production
    /// default, and what every existing call site gets) the private process-based runner is used.
    /// The seam returns RAW process results — this class stays responsible for constructing and
    /// redacting the resulting log lines and failure messages.
    /// </param>
    /// <param name="tokenLookup">
    /// Optional LIVE lookup of the stored OAuth admin credential (the first candidate of the
    /// credential chain, ahead of <c>GH_TOKEN</c>/<c>GITHUB_TOKEN</c>). Awaited at most ONCE per
    /// high-level operation. <c>null</c> keeps the environment-only chain.
    /// </param>
    /// <param name="configuredUrlLookup">
    /// Optional LIVE lookup mapping a repository name to its currently configured URL. Invoked at
    /// CALL time so a configuration reload is picked up without a restart. <c>null</c> — or a
    /// <c>null</c>/blank result — means "unconfigured": no credential refresh happens at all and
    /// the manager behaves exactly as it did before this seam existed.
    /// </param>
    public BrainRepoManager(
        string basePath,
        ILogger<BrainRepoManager> logger,
        Func<BrainGitRequest, BrainGitResult>? gitRunner = null,
        Func<CancellationToken, Task<string?>>? tokenLookup = null,
        Func<string, string?>? configuredUrlLookup = null)
    {
        _basePath = Path.GetFullPath(basePath);
        _logger = logger;
        _gitRunner = gitRunner;
        _tokenLookup = tokenLookup;
        _configuredUrlLookup = configuredUrlLookup;
        Directory.CreateDirectory(WorkDirectory);
    }

    // ── Credential plumbing ───────────────────────────────────────────────────
    //
    // Every NETWORK-BEARING high-level operation resolves ONE credential for its whole duration
    // and reuses it for each fetch/push/ls-remote/rollback command and for the redaction of the
    // resulting diagnostics. Local-only operations resolve nothing.

    /// <summary>
    /// Resolves the operation credential ONCE: the stored OAuth admin token (when a lookup was
    /// supplied and succeeds) followed by the <c>GH_TOKEN</c>/<c>GITHUB_TOKEN</c> environment
    /// candidates, selected by <see cref="GitCredentialResolver.Resolve"/> and returned UNCHANGED.
    /// </summary>
    /// <remarks>
    /// A CALLER cancellation propagates — it is never converted into an environment fallback.
    /// Any OTHER lookup failure logs a FIXED, credential-free diagnostic and falls through to the
    /// environment-only chain, so a broken OAuth bridge cannot take down an otherwise working
    /// environment-credentialed deployment. The exception itself is never formatted into the
    /// diagnostic: it could embed the token it failed to deliver.
    /// </remarks>
    private async Task<string?> ResolveCredentialAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        string? storedToken = null;
        if (_tokenLookup is not null)
        {
            try
            {
                storedToken = await _tokenLookup(ct);
            }
            catch (Exception ex) when (ex is OperationCanceledException && ct.IsCancellationRequested)
            {
                // Cancellation propagates AS cancellation — but the lookup's OCE payload is its
                // own, arbitrary and untrusted, and at this point NOTHING has been published: the
                // token was never assigned, so neither this catch nor the operation-level boundary
                // knows what value to redact. A BARE token in the payload would therefore be
                // undetectable.
                //
                // The payload is consequently replaced WHOLESALE with fixed, credential-free text
                // rather than being inspected. That is the only provably safe treatment for a
                // value we cannot recognise. Cancellation semantics are preserved: the result is
                // still an OperationCanceledException carrying the same token, with NO inner
                // exception, so callers that catch cancellation are unaffected.
                var oce = (OperationCanceledException)ex;
                throw oce.CancellationToken.CanBeCanceled
                    ? new OperationCanceledException(
                        "The stored OAuth credential lookup was cancelled.", oce.CancellationToken)
                    : new OperationCanceledException(
                        "The stored OAuth credential lookup was cancelled.");
            }
            catch
            {
                // The lookup may have cancelled the caller's token and only THEN failed. Honour the
                // cancellation rather than silently degrading to the environment chain.
                ct.ThrowIfCancellationRequested();

                _logger.LogWarning(
                    "Stored OAuth credential lookup failed — falling back to the environment credential chain.");
                storedToken = null;
            }
        }

        // A lookup that observes the cancellation but RETURNS NORMALLY (rather than throwing an
        // OperationCanceledException) must not be allowed to hand back a credential that then
        // reaches AttachCredential and starts a credential-bearing git process. Cancellation is
        // re-checked here, after the await, so it is observed BEFORE any credential is selected.
        ct.ThrowIfCancellationRequested();

        return GitCredentialResolver.Resolve(
            storedToken,
            Environment.GetEnvironmentVariable("GH_TOKEN"),
            Environment.GetEnvironmentVariable("GITHUB_TOKEN"));
    }

    /// <summary>
    /// Whether <paramref name="url"/> may receive the admin credential: a parsed HTTPS URL whose
    /// host is exactly <c>github.com</c> (case-insensitively) on effective port 443. SSH, local,
    /// plain HTTP, non-GitHub and non-443 targets are NEVER credentialed.
    /// </summary>
    private static bool IsCredentialEligible(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return false;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return false;
        if (!string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            return false;
        if (!string.Equals(uri.Host, GitHubHost, StringComparison.OrdinalIgnoreCase))
            return false;

        return uri.Port == 443;
    }

    /// <summary>
    /// Whether two URLs denote the SAME repository, comparing their credential-FREE identity
    /// (scheme, host, effective port and normalized path — userinfo, a trailing <c>/</c> and a
    /// trailing <c>.git</c> are ignored). A URL that does not parse as an absolute URI never
    /// matches, so an SSH/scp-form or malformed origin is treated as a different repository.
    /// </summary>
    private static bool IsSameRepository(string? left, string? right)
    {
        if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right))
            return false;
        if (!Uri.TryCreate(left.Trim(), UriKind.Absolute, out var a))
            return false;
        if (!Uri.TryCreate(right.Trim(), UriKind.Absolute, out var b))
            return false;

        return string.Equals(a.Scheme, b.Scheme, StringComparison.OrdinalIgnoreCase)
            && string.Equals(a.Host, b.Host, StringComparison.OrdinalIgnoreCase)
            && a.Port == b.Port
            && string.Equals(NormalizeRepoPath(a), NormalizeRepoPath(b), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Returns the repository path of <paramref name="uri"/> without leading/trailing slashes
    /// and without a trailing <c>.git</c> suffix.
    /// </summary>
    private static string NormalizeRepoPath(Uri uri)
    {
        var path = uri.AbsolutePath.Trim('/');
        if (path.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
            path = path[..^4];
        return path;
    }

    /// <summary>
    /// Returns <paramref name="eligibleUrl"/> with its userinfo REPLACED by the URI-escaped
    /// <paramref name="credential"/>. Only ever called with a URL that passed
    /// <see cref="IsCredentialEligible"/>, so the result is always an HTTPS GitHub URL.
    /// </summary>
    private static string AttachCredential(string eligibleUrl, string credential)
    {
        var uri = new Uri(eligibleUrl.Trim(), UriKind.Absolute);
        var authority = uri.IsDefaultPort ? uri.Host : $"{uri.Host}:{uri.Port}";
        return $"{uri.Scheme}://{CredentialUserName}:{Uri.EscapeDataString(credential)}@{authority}{uri.PathAndQuery}{uri.Fragment}";
    }

    /// <summary>
    /// The single per-repository credential-refresh core for an EXISTING clone. It runs under the
    /// caller's already-acquired per-repository semaphore, issues only LOCAL git commands, and
    /// returns the credential the whole operation must reuse (<c>null</c> when none was resolved).
    /// <para>
    /// <b>Unconfigured compatibility.</b> With no configured-URL lookup — or a blank/ineligible
    /// configured URL — this is a complete no-op: no OAuth lookup, no origin inspection, no
    /// rewriting. Direct callers against local or non-GitHub remotes keep their previous behaviour.
    /// </para>
    /// <para>
    /// <b>Existing-origin policy.</b> The persisted <c>origin</c> is read and its credential-free
    /// identity compared against the configured URL. A mismatch, multiple/conflicting fetch
    /// destinations, or an explicit <c>pushurl</c> are rejected with a fixed, credential-free
    /// error BEFORE any credential is attached and before any network command runs — a working
    /// tree is never redirected at a different repository.
    /// </para>
    /// <para>
    /// <b>The stale-origin rule.</b> When NO credential resolves, nothing is written: a transient
    /// lookup failure must never strip a working persisted credential. The declared and accepted
    /// consequence is that a revoked credential can persist in <c>.git/config</c> until a later
    /// successful resolution replaces it; there is no automatic removal.
    /// </para>
    /// </summary>
    private async Task<string?> RefreshOriginCredentialAsync(
        string repoName, string clonePath, CancellationToken ct, CredentialBox? box = null)
    {
        // Caller cancellation is honoured before ANY lookup, mutation or git command.
        ct.ThrowIfCancellationRequested();

        var configuredUrl = _configuredUrlLookup?.Invoke(repoName);
        if (!IsCredentialEligible(configuredUrl))
            return null;

        var credential = await ResolveCredentialAsync(ct);

        // Publish the selection IMMEDIATELY so the operation-level exception boundary can redact
        // with it even if the very next command throws.
        if (box is not null)
            box.Credential = credential;

        // LOCAL read of the persisted fetch destination(s).
        var (originExit, originStdout, _) = await RunGitCaptureAsync(
            clonePath, ["remote", "get-url", "--all", "origin"], ct);

        var origins = originExit == 0
            ? originStdout.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
                .Select(line => line.Trim())
                .Where(line => line.Length > 0)
                .Distinct(StringComparer.Ordinal)
                .ToList()
            : [];

        if (originExit != 0 || origins.Count == 0)
            throw new InvalidOperationException(
                $"Refusing to refresh credentials for '{repoName}': its 'origin' remote could not be read.");

        if (origins.Count > 1)
            throw new InvalidOperationException(
                $"Refusing to refresh credentials for '{repoName}': its 'origin' remote has multiple conflicting fetch URLs.");

        if (!IsSameRepository(origins[0], configuredUrl))
            throw new InvalidOperationException(
                $"Refusing to refresh credentials for '{repoName}': its 'origin' remote points at a different repository than the configured URL.");

        // LOCAL read: an explicit pushurl would silently keep sending pushes elsewhere.
        //
        // This inspection must FAIL CLOSED — the destination policy has to be positively
        // established before any credential is attached:
        //   • exit 1  is git's "key is not set" signal — the ONLY accepted absence.
        //   • exit 0  means the key EXISTS. Any value is rejected, including an explicitly empty
        //             or whitespace one: an empty pushurl is still an explicit override whose
        //             effect we have not established, so it is never treated as absence.
        //   • anything else is an inspection FAILURE (unreadable/locked config, invalid key),
        //             which likewise cannot establish the policy and is rejected.
        var (pushExit, pushStdout, _) = await RunGitCaptureAsync(
            clonePath, ["config", "--get-all", "remote.origin.pushurl"], ct);

        if (pushExit == 0)
            throw new InvalidOperationException(
                $"Refusing to refresh credentials for '{repoName}': its 'origin' remote has an explicit push URL.");

        if (pushExit != 1)
            throw new InvalidOperationException(
                $"Refusing to refresh credentials for '{repoName}': its 'origin' push destination could not be determined.");

        // Referenced so the query result stays part of the inspection contract even though the
        // decision above is driven solely by the exit code.
        _ = pushStdout;

        // No credential resolved — leave the persisted origin completely untouched.
        if (credential is null)
            return null;

        // Final pre-MUTATION cancellation check. Everything above is read-only inspection; this is
        // the last point before the origin is rewritten with a credential-bearing URL.
        ct.ThrowIfCancellationRequested();

        await RunGitAsync(
            clonePath,
            ["remote", "set-url", "origin", AttachCredential(configuredUrl!, credential)],
            ct,
            credential);

        return credential;
    }

    /// <summary>
    /// Credential resolution for a NEW clone: the SUPPLIED URL is authenticated directly, but only
    /// when it is the repository's currently configured, eligible URL. An unconfigured repository —
    /// or a caller-supplied URL that denotes a DIFFERENT repository than the configured one — is
    /// cloned exactly as supplied, with no credential resolution at all.
    /// </summary>
    /// <returns>The URL to clone from and the credential the operation must reuse.</returns>
    private async Task<(string CloneUrl, string? Credential)> ResolveCloneUrlAsync(
        string repoName, string repoUrl, CancellationToken ct, CredentialBox? box = null)
    {
        ct.ThrowIfCancellationRequested();

        var configuredUrl = _configuredUrlLookup?.Invoke(repoName);
        if (!IsCredentialEligible(configuredUrl) || !IsSameRepository(repoUrl, configuredUrl))
            return (repoUrl, null);

        var credential = await ResolveCredentialAsync(ct);
        if (credential is null)
            return (repoUrl, null);

        // Publish the selection BEFORE the credential is ever attached to a URL, so the
        // operation-level boundary can redact a failure in the clone that follows.
        if (box is not null)
            box.Credential = credential;

        // Final pre-mutation cancellation check: nothing credential-bearing is constructed — and
        // therefore nothing can be handed to git — after the caller has cancelled.
        ct.ThrowIfCancellationRequested();

        return (AttachCredential(configuredUrl!, credential), credential);
    }

    /// <summary>
    /// Redacts a message that is about to be surfaced: the URL scanner pass plus, when the
    /// operation's credential is known, an ordinal literal replacement of BOTH its raw and its
    /// URI-escaped form — catching a bare credential no URL scanner would recognise.
    /// </summary>
    private static string Sanitize(string text, string? credential)
    {
        var redacted = GitUrlRedactor.Redact(text) ?? string.Empty;
        if (string.IsNullOrEmpty(credential))
            return redacted;

        redacted = redacted.Replace(credential, CredentialPlaceholder, StringComparison.Ordinal);

        var escaped = Uri.EscapeDataString(credential);
        if (!string.Equals(escaped, credential, StringComparison.Ordinal))
            redacted = redacted.Replace(escaped, CredentialPlaceholder, StringComparison.Ordinal);

        return redacted;
    }

    /// <summary>
    /// Mutable holder for the credential an in-flight operation has selected. It starts empty and
    /// is filled the moment <see cref="RefreshOriginCredentialAsync"/> (or the clone-URL
    /// resolution) picks a credential, so the operation-level exception boundary can redact with
    /// the RIGHT credential even for a failure that happens later in the same call.
    /// </summary>
    private sealed class CredentialBox
    {
        /// <summary>The operation's selected credential, or <c>null</c> when none was resolved.</summary>
        public string? Credential { get; set; }
    }

    /// <summary>
    /// Returns an exception that is safe to propagate or log: <paramref name="ex"/> itself when its
    /// COMPLETE payload is already credential-free, otherwise a fresh
    /// <see cref="InvalidOperationException"/> carrying the sanitized text.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The whole payload is inspected via <see cref="Exception.ToString"/>, which spans the
    /// message, the data of every nested inner exception and the stack trace — a raw token can hide
    /// in any of them. Because a rewrapped exception deliberately drops
    /// <see cref="Exception.InnerException"/>, an unsafe inner exception can never be re-exposed by
    /// a caller that walks the chain or by a logger that formats it.
    /// </para>
    /// <para>
    /// The original exception is returned UNCHANGED whenever it is already safe, so exception TYPES
    /// the callers depend on (<see cref="MergeConflictException"/>, <see cref="ArgumentException"/>,
    /// the fixed-text policy rejections) survive intact. Only a genuinely credential-bearing
    /// exception is replaced.
    /// </para>
    /// </remarks>
    private static Exception SanitizeException(Exception ex, string? credential)
    {
        var payload = ex.ToString();

        // Layer 1 — URL redaction, applied UNCONDITIONALLY. It must NOT be gated on finding the
        // currently selected credential: an exception can echo a DIFFERENT or STALE
        // credential-bearing URL (a persisted origin, another repository's token), and a
        // no-credential operation over a clone with persisted credentials has exactly the same
        // exposure. Both cases have no "selected credential" to match literally.
        var redactedPayload = GitUrlRedactor.Redact(payload) ?? string.Empty;
        var leaks = !string.Equals(redactedPayload, payload, StringComparison.Ordinal);

        // Layer 2 — the literal pass over THIS operation's selected credential, which also catches
        // a BARE token that no URL scanner would recognise.
        if (!leaks && !string.IsNullOrEmpty(credential))
        {
            var escaped = Uri.EscapeDataString(credential);
            leaks = payload.Contains(credential, StringComparison.Ordinal)
                    || (!string.Equals(escaped, credential, StringComparison.Ordinal)
                        && payload.Contains(escaped, StringComparison.Ordinal));
        }

        if (!leaks)
            return ex;

        // The ORIGINAL type name is preserved as text so the diagnostic is not lost, but the
        // exception object itself — and its entire inner chain — is discarded.
        var safeText = Sanitize($"{ex.GetType().Name}: {ex.Message}", credential);

        // Cancellation must still surface AS cancellation, carrying its token, so an unsafe
        // OperationCanceledException is replaced by a credential-free one rather than being
        // converted into a different exception type.
        if (ex is OperationCanceledException oce)
        {
            return oce.CancellationToken.CanBeCanceled
                ? new OperationCanceledException(safeText, oce.CancellationToken)
                : new OperationCanceledException(safeText);
        }

        return new InvalidOperationException(safeText);
    }

    /// <summary>
    /// Exception-filter helper for the per-operation credential boundary. Returns <c>true</c> —
    /// and sets <paramref name="safe"/> to a sanitized replacement — ONLY when
    /// <paramref name="ex"/> would otherwise leak a credential.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Returning <c>false</c> makes the enclosing filter decline the exception, so an
    /// already-safe exception propagates completely untouched, preserving its original type and
    /// stack trace.
    /// </para>
    /// <para>
    /// A caller cancellation is NOT blanket-declined. Cancellation semantics are preserved —
    /// an <see cref="OperationCanceledException"/> is always replaced by another
    /// <see cref="OperationCanceledException"/> carrying the same token, so callers that catch
    /// cancellation still do — but its PAYLOAD is inspected like any other: a runner that cancels
    /// the token and throws an OCE whose message or inner chain embeds a credential would
    /// otherwise escape completely unsanitized.
    /// </para>
    /// </remarks>
    private static bool TryBuildSafeException(
        Exception ex, CredentialBox box, out Exception safe)
    {
        safe = ex;

        var sanitized = SanitizeException(ex, box.Credential);
        if (ReferenceEquals(sanitized, ex))
            return false;

        safe = sanitized;
        return true;
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
        // Holds the credential this operation selects, so the catch below can redact an exception
        // THROWN by the git-runner seam (or a failed process launch) with the right credential.
        var credentialBox = new CredentialBox();
        try
        {
            var clonePath = GetClonePath(repoName);

            if (Directory.Exists(Path.Combine(clonePath, ".git")))
            {
                // Refresh the persisted origin credential BEFORE the fetch (the first network
                // command). The resolved credential is reused for every command below.
                var credential = await RefreshOriginCredentialAsync(repoName, clonePath, ct, credentialBox);

                _logger.LogInformation(
                    "Brain clone exists for {Repo}, pulling latest on {Branch}",
                    repoName, defaultBranch);

                await RunGitAsync(clonePath, ["fetch", "origin"], ct, credential);

                if (!await RemoteBranchExistsAsync(clonePath, defaultBranch, ct))
                {
                    _logger.LogWarning(
                        "Default branch '{Branch}' does not exist on origin for {Repo} — skipping checkout/reset (empty repository)",
                        defaultBranch, repoName);
                }
                else
                {
                    await RunGitAsync(clonePath, ["checkout", defaultBranch], ct, credential);
                    await RunGitAsync(clonePath, ["reset", "--hard", $"origin/{defaultBranch}"], ct, credential);
                }
            }
            else
            {
                // A NEW clone authenticates the SUPPLIED URL directly — but only when it is the
                // repository's currently configured, eligible URL.
                var (cloneUrl, credential) = await ResolveCloneUrlAsync(repoName, repoUrl, ct, credentialBox);

                // The clone URL carries credentials, so the credential-free form is logged.
                // The raw URL still goes to git unchanged below.
                _logger.LogInformation(
                    "Creating Brain clone for {Repo} from {Url} (branch: {Branch})",
                    repoName, Sanitize(cloneUrl, credential), defaultBranch);

                Directory.CreateDirectory(WorkDirectory);
                try
                {
                    await RunGitAsync(WorkDirectory,
                        ["clone", "--branch", defaultBranch, cloneUrl, repoName], ct, credential);
                }
                catch (InvalidOperationException ex) when (ex.Message.Contains("not found in upstream"))
                {
                    _logger.LogWarning(
                        "Branch '{Branch}' not found in upstream for {Repo} — retrying clone without --branch",
                        defaultBranch, repoName);

                    if (Directory.Exists(clonePath))
                        await ForceDeleteDirectoryAsync(clonePath);

                    await RunGitAsync(WorkDirectory, ["clone", cloneUrl, repoName], ct, credential);
                }

                // Configure git identity for merge commits
                await RunGitAsync(clonePath, ["config", "user.email", "copilothive@local"], ct);
                await RunGitAsync(clonePath, ["config", "user.name", "CopilotHive"], ct);
            }

            return clonePath;
        }
        catch (Exception ex) when (TryBuildSafeException(ex, credentialBox, out var safe))
        {
            // The exception carried the operation's credential (a THROWING runner seam, a failed
            // process launch, or any nested inner exception). Replace it with the sanitized form;
            // the unsafe original — and its whole inner chain — is discarded, never logged.
            throw safe;
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
        // Holds the credential this operation selects, so the catch below can redact an exception
        // THROWN by the git-runner seam (or a failed process launch) with the right credential.
        var credentialBox = new CredentialBox();
        try
        {
            var clonePath = GetClonePath(repoName);
            if (!Directory.Exists(Path.Combine(clonePath, ".git")))
                throw new InvalidOperationException(
                    $"No brain clone found for '{repoName}'. Call EnsureCloneAsync first.");

            _logger.LogInformation("Squash-merging {Branch} into {Base} for {Repo}",
                featureBranch, defaultBranch, repoName);

            // Refresh the persisted origin credential BEFORE the fetch (the first network
            // command). The resolved credential is reused for every command below.
            var credential = await RefreshOriginCredentialAsync(repoName, clonePath, ct, credentialBox);

            // Ensure we're on the base branch with latest remote state
            await RunGitAsync(clonePath, ["fetch", "origin"], ct, credential);

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

                await RunGitAsync(clonePath, ["fetch", "origin", featureBranch], ct, credential);
                await RunGitAsync(clonePath, ["checkout", "-B", defaultBranch, $"origin/{featureBranch}"], ct, credential);
                await RunGitAsync(clonePath, ["push", "origin", defaultBranch], ct, credential);

                var newBranchHash = await RunGitWithOutputAsync(clonePath, ["rev-parse", "HEAD"], ct, credential);
                return newBranchHash.Trim();
            }

            await RunGitAsync(clonePath, ["checkout", defaultBranch], ct, credential);
            await RunGitAsync(clonePath, ["reset", "--hard", $"origin/{defaultBranch}"], ct, credential);

            // Fetch the feature branch and attempt squash merge
            await RunGitAsync(clonePath, ["fetch", "origin", featureBranch], ct, credential);

            try
            {
                await RunGitAsync(clonePath,
                    ["merge", "--squash", $"origin/{featureBranch}"], ct, credential);
            }
            catch (Exception mergeEx)
            {
                // Sanitized at the log site: this exception is rethrown below and would ALSO be
                // caught by the operation boundary, but the log entry itself is written here.
                _logger.LogWarning(
                    SanitizeException(mergeEx, credential),
                    "Squash merge failed for {Repo} — resetting clone to clean state", repoName);

                // Abort the merge and reset to clean state
                try { await RunGitAsync(clonePath, ["merge", "--abort"], ct, credential); } catch { }
                await RunGitAsync(clonePath, ["reset", "--hard", $"origin/{defaultBranch}"], ct, credential);
                await RunGitAsync(clonePath, ["clean", "-fd"], ct, credential);

                throw;
            }

            // Check whether the squash produced any staged changes before committing
            var statusResult = await RunGitWithOutputAsync(clonePath, ["status", "--porcelain"], ct, credential);
            if (string.IsNullOrWhiteSpace(statusResult))
            {
                _logger.LogInformation(
                    "Squash merge of {Branch} into {Base} for {Repo} produced no changes — skipping commit",
                    featureBranch, defaultBranch, repoName);

                // Return the current HEAD since nothing new was committed
                var currentHash = await RunGitWithOutputAsync(clonePath, ["rev-parse", "HEAD"], ct, credential);
                return currentHash.Trim();
            }

            // Commit the squashed changes as a single commit
            await RunGitAsync(clonePath, ["commit", "-m", commitMessage], ct, credential);

            // Push the squash commit
            await RunGitAsync(clonePath, ["push", "origin", defaultBranch], ct, credential);

            _logger.LogInformation("Successfully squash-merged {Branch} into {Base} for {Repo}",
                featureBranch, defaultBranch, repoName);

            var hashResult = await RunGitWithOutputAsync(clonePath, ["rev-parse", "HEAD"], ct, credential);
            return hashResult.Trim();
        }
        catch (Exception ex) when (TryBuildSafeException(ex, credentialBox, out var safe))
        {
            // The exception carried the operation's credential (a THROWING runner seam, a failed
            // process launch, or any nested inner exception). Replace it with the sanitized form;
            // the unsafe original — and its whole inner chain — is discarded, never logged.
            throw safe;
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
    /// Routed through the optional runner seam when one was supplied.
    /// </summary>
    /// <param name="workingDir">Working directory for the git process.</param>
    /// <param name="args">Arguments to pass to git.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <param name="credential">
    /// The operation's already-resolved credential, used for the literal redaction pass.
    /// <c>null</c> for local-only commands and for operations that resolved nothing.
    /// </param>
    /// <returns>The standard output of the git command, RAW and unmodified.</returns>
    private async Task<string> RunGitWithOutputAsync(
        string workingDir, string[] args, CancellationToken ct, string? credential = null)
    {
        var (exitCode, stdout, stderr) = await RunGitCoreAsync(workingDir, args, ct);

        if (exitCode != 0)
        {
            // Same construction boundary as RunGitAsync: the argument list and stderr can both
            // embed a credential-bearing remote URL, or the bare credential itself.
            throw new InvalidOperationException(Sanitize(
                $"git {string.Join(' ', args)} failed (exit {exitCode}): {stderr}", credential));
        }

        // Returned VERBATIM: callers parse SHAs, porcelain status and ref listings out of it.
        return stdout;
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
        // Holds the credential this operation selects, so the catch below can redact an exception
        // THROWN by the git-runner seam (or a failed process launch) with the right credential.
        var credentialBox = new CredentialBox();
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

            // Refresh the persisted origin credential BEFORE the delete push (the first — and
            // only — network command in this method).
            var credential = await RefreshOriginCredentialAsync(repoName, clonePath, ct, credentialBox);

            BranchDeleteResult result;
            try
            {
                await RunGitAsync(clonePath, ["push", "origin", "--delete", branchName], ct, credential);
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
                    // The exception object is SWALLOWED here (the method returns Failed), so the
                    // operation-level boundary never sees it — it must be sanitized at this log
                    // site or a throwing runner's credential would land in the log verbatim.
                    _logger.LogWarning(
                        SanitizeException(ex, credential),
                        "Failed to delete remote branch {Branch} from {Repo}", branchName, repoName);
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
        catch (Exception ex) when (TryBuildSafeException(ex, credentialBox, out var safe))
        {
            // The exception carried the operation's credential (a THROWING runner seam, a failed
            // process launch, or any nested inner exception). Replace it with the sanitized form;
            // the unsafe original — and its whole inner chain — is discarded, never logged.
            throw safe;
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

    /// <summary>
    /// Runs a git command, throwing an <see cref="InvalidOperationException"/> on a non-zero
    /// exit code. Routed through the optional runner seam when one was supplied.
    /// </summary>
    /// <remarks>
    /// The failure message embeds the complete git argument list — which for a clone or a
    /// <c>remote set-url</c> contains the credential-bearing remote URL — and git's stderr, which
    /// echoes the remote back. Both are therefore redacted at the point the message is
    /// CONSTRUCTED, using the URL scanner plus a literal pass over the operation's credential.
    /// The raw stdout/stderr are never mutated: they stay functional data for the callers that
    /// parse them.
    /// </remarks>
    private async Task RunGitAsync(
        string workingDir, string[] args, CancellationToken ct, string? credential = null)
    {
        var (exitCode, _, stderr) = await RunGitCoreAsync(workingDir, args, ct);

        if (exitCode != 0)
        {
            throw new InvalidOperationException(Sanitize(
                $"git {string.Join(' ', args)} failed (exit {exitCode}): {stderr}", credential));
        }
    }

    /// <summary>
    /// Runs a git command and returns its RAW exit code, stdout and stderr, using the injected
    /// runner seam when present and the real git process otherwise. Never redacts and never
    /// throws on a non-zero exit code — message construction (and redaction) is the caller's job.
    /// </summary>
    private async Task<(int ExitCode, string Stdout, string Stderr)> RunGitCoreAsync(
        string workingDir, string[] args, CancellationToken ct)
    {
        if (_gitRunner is { } runner)
        {
            ct.ThrowIfCancellationRequested();
            var injected = runner(new BrainGitRequest(workingDir, args));
            return (injected.ExitCode, injected.Stdout, injected.Stderr);
        }

        // The SAME pre-launch cancellation check the fake-runner branch performs. Without it a
        // credential-bearing `clone`/`remote set-url` could still be launched after the caller
        // cancelled, because Process.Start itself observes no token.
        ct.ThrowIfCancellationRequested();

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

        return (process.ExitCode, stdoutTask.Result, stderrTask.Result);
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
        // Holds the credential this operation selects, so the catch below can redact an exception
        // THROWN by the git-runner seam (or a failed process launch) with the right credential.
        var credentialBox = new CredentialBox();
        try
        {
            var clonePath = GetClonePath(repoName);
            if (!Directory.Exists(Path.Combine(clonePath, ".git")))
                throw new InvalidOperationException($"Repository '{repoName}' is not cloned.");

            _logger.LogInformation("Merging {Source} into {Target} for {Repo}",
                sourceBranch, targetBranch, repoName);

            // Refresh the persisted origin credential BEFORE the fetch (the first network command).
            var credential = await RefreshOriginCredentialAsync(repoName, clonePath, ct, credentialBox);

            await RunGitAsync(clonePath, ["fetch", "origin"], ct, credential);

            // Clean worktree so checkout/merge operations are not blocked by leftover state.
            var status = await RunGitWithOutputAsync(clonePath, ["status", "--porcelain"], ct, credential);
            if (!string.IsNullOrWhiteSpace(status))
            {
                await RunGitAsync(clonePath, ["reset", "--hard"], ct, credential);
                await RunGitAsync(clonePath, ["clean", "-fd"], ct, credential);
            }

            if (!await RemoteBranchExistsAsync(clonePath, targetBranch, ct))
                throw new InvalidOperationException(
                    $"Remote branch 'origin/{targetBranch}' does not exist for '{repoName}'.");

            if (!await RemoteBranchExistsAsync(clonePath, sourceBranch, ct))
                throw new InvalidOperationException(
                    $"Remote branch 'origin/{sourceBranch}' does not exist for '{repoName}'.");

            await RunGitAsync(clonePath, ["checkout", "-B", targetBranch, $"origin/{targetBranch}"], ct, credential);

            var preMergeSha = (await RunGitWithOutputAsync(clonePath, ["rev-parse", "HEAD"], ct, credential)).Trim();

            // The merge command is run with a directly-managed Process (NOT RunGitCaptureAsync) so
            // that on cancellation we can Kill(entireProcessTree) and block until the process is
            // confirmed dead BEFORE the finally-block cleanup runs `git merge --abort`. This avoids
            // racing the cleanup against a still-live merge process. `mergeStarted` signals that a
            // MERGE state may exist and must be aborted; it stays false on a clean success.
            Process? mergeProcess = null;
            var mergeStarted = false;
            try
            {
                mergeStarted = true;
                var mergePsi = new ProcessStartInfo("git")
                {
                    WorkingDirectory = clonePath,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                };
                mergePsi.ArgumentList.Add("merge");
                mergePsi.ArgumentList.Add($"origin/{sourceBranch}");
                mergePsi.ArgumentList.Add("--no-edit");

                mergeProcess = Process.Start(mergePsi)
                    ?? throw new InvalidOperationException("Failed to start git merge process");

                var stdoutTask = mergeProcess.StandardOutput.ReadToEndAsync(ct);
                var stderrTask = mergeProcess.StandardError.ReadToEndAsync(ct);

                int exitCode;
                string stdout;
                string stderr;
                try
                {
                    await Task.WhenAll(stdoutTask, stderrTask);
                    await mergeProcess.WaitForExitAsync(ct);
                    exitCode = mergeProcess.ExitCode;
                    stdout = stdoutTask.Result;
                    stderr = stderrTask.Result;
                }
                catch (OperationCanceledException)
                {
                    // Terminate the merge process and BLOCK until it is confirmed dead before
                    // rethrowing, so the finally-block cleanup cannot race a live merge process.
                    try
                    {
                        if (!mergeProcess.HasExited)
                            mergeProcess.Kill(entireProcessTree: true);
                    }
                    catch (InvalidOperationException) when (mergeProcess.HasExited)
                    {
                        // Process already exited between the HasExited check and Kill — safe.
                    }
                    catch (Exception)
                    {
                        // Kill failed for another reason; the WaitForExit below still bounds our wait.
                    }

                    // Block the calling thread until the process exits. A generous 30s timeout —
                    // we MUST confirm process death before allowing cleanup to proceed. If it still
                    // has not exited (extreme edge case: kill failed AND 30s elapsed), there is
                    // nothing more we can do; cleanup will run best-effort.
                    if (!mergeProcess.HasExited)
                        mergeProcess.WaitForExit(30_000);

                    throw; // Rethrow so the finally block runs cleanup.
                }

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

                    throw new InvalidOperationException(Sanitize(
                        $"git merge origin/{sourceBranch} failed (exit {exitCode}): {stderr}", credential));
                }

                // Merge succeeded cleanly — no MERGE state remains, so cleanup is not needed.
                mergeStarted = false;

                var postMergeSha = (await RunGitWithOutputAsync(clonePath, ["rev-parse", "HEAD"], ct, credential)).Trim();
                if (postMergeSha == preMergeSha)
                {
                    _logger.LogInformation(
                        "Merge of {Source} into {Target} for {Repo} was a no-op (target already contained source)",
                        sourceBranch, targetBranch, repoName);
                    return null;
                }

                await RunGitAsync(clonePath, ["push", "origin", targetBranch], ct, credential);

                _logger.LogInformation("Successfully merged {Source} into {Target} for {Repo}",
                    sourceBranch, targetBranch, repoName);

                return postMergeSha;
            }
            finally
            {
                mergeProcess?.Dispose();

                // Run cleanup whenever a merge was started but did not complete cleanly (conflict,
                // other error, or caller cancellation). Use a fresh bounded token — NOT the caller's
                // token, which may already be canceled — so abort always runs to completion. This is
                // now safe because the merge process is confirmed dead (or we waited 30s for it).
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
        catch (Exception ex) when (TryBuildSafeException(ex, credentialBox, out var safe))
        {
            // The exception carried the operation's credential (a THROWING runner seam, a failed
            // process launch, or any nested inner exception). Replace it with the sanitized form;
            // the unsafe original — and its whole inner chain — is discarded, never logged.
            throw safe;
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
    /// <para>
    /// git ref-format rules apply PER slash-separated component, so this validator splits the name
    /// on <c>/</c> and applies component-level checks to each segment (empty, leading <c>.</c>,
    /// trailing <c>.</c>/<c>.lock</c>, lone <c>@</c>, <c>..</c>, <c>@{</c>, forbidden and control
    /// characters), in addition to whole-name checks (null/empty/whitespace, leading <c>-</c>).
    /// This rejects names like <c>foo/.hidden/bar</c> and <c>foo.lock/bar</c> that whole-name-only
    /// checks would miss.
    /// </para>
    /// </summary>
    private static void ValidateBranchOrTagName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("Branch or tag name must not be null or whitespace.", nameof(name));

        // Whole-name option-injection guard.
        if (name.StartsWith('-'))
            throw new ArgumentException($"Branch or tag name '{name}' must not start with '-'.", nameof(name));

        // A trailing slash produces an empty final component; a leading slash an empty first one —
        // both are caught by the per-component empty check below after Split.
        var components = name.Split('/');
        char[] forbidden = [' ', '~', '^', ':', '?', '*', '[', '\\'];

        foreach (var component in components)
        {
            // Empty component catches leading '/', trailing '/', and '//'.
            if (component.Length == 0)
                throw new ArgumentException($"Branch or tag name '{name}' must not contain empty path components ('/', '//').", nameof(name));

            // Control characters (below 0x20) and DEL.
            foreach (var c in component)
            {
                if (c < 0x20 || c == 0x7f)
                    throw new ArgumentException($"Branch or tag name '{name}' contains control characters.", nameof(name));
            }

            if (component.IndexOfAny(forbidden) >= 0)
                throw new ArgumentException($"Branch or tag name '{name}' contains forbidden characters.", nameof(name));

            if (component.StartsWith('.'))
                throw new ArgumentException($"Branch or tag name component '{component}' must not begin with '.'.", nameof(name));
            if (component.EndsWith('.'))
                throw new ArgumentException($"Branch or tag name component '{component}' must not end with '.'.", nameof(name));
            if (component.EndsWith(".lock", StringComparison.Ordinal))
                throw new ArgumentException($"Branch or tag name component '{component}' must not end with '.lock'.", nameof(name));
            if (component == "@")
                throw new ArgumentException($"Branch or tag name component must not be a lone '@' in '{name}'.", nameof(name));
            if (component.Contains(".."))
                throw new ArgumentException($"Branch or tag name '{name}' must not contain '..'.", nameof(name));
            if (component.Contains("@{"))
                throw new ArgumentException($"Branch or tag name '{name}' must not contain '@{{'.", nameof(name));
        }
    }

    /// <summary>
    /// Validates a branch name for the ADDITIVE fetch path using the rules
    /// <c>git check-ref-format --branch</c> actually applies, so
    /// <see cref="FetchOriginAsync"/> never rejects a branch the Composer's own
    /// <c>check-ref-format</c> validation already accepted.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is deliberately NOT <see cref="ValidateBranchOrTagName"/>. That validator is stricter
    /// than git in two places that matter here — it rejects any slash-separated component equal to
    /// a lone <c>@</c> (<c>topic/@/work</c>) and any component ending in <c>.</c>
    /// (<c>topic./work</c>) — yet git accepts both, so reusing it would make the fetch path reject
    /// valid branches that every other Composer git tool happily handles.
    /// </para>
    /// <para>
    /// The rules enforced here mirror git: no leading <c>-</c> (option injection), no empty
    /// component (covers a leading/trailing <c>/</c> and <c>//</c>), no component starting with
    /// <c>.</c>, no component ending in <c>.lock</c>, no <c>..</c>, no <c>@{</c>, no control or DEL
    /// characters, none of <c>space ~ ^ : ? * [ \</c>, and no trailing <c>.</c> on the WHOLE name.
    /// A lone <c>@</c> component and an interior component ending in <c>.</c> are ACCEPTED,
    /// matching git.
    /// </para>
    /// </remarks>
    /// <param name="branch">The branch name to validate.</param>
    /// <exception cref="ArgumentException">Thrown when git itself would reject the name.</exception>
    internal static void ValidateFetchBranchName(string branch)
    {
        if (string.IsNullOrWhiteSpace(branch))
            throw new ArgumentException("Branch name must not be null or whitespace.", nameof(branch));

        if (branch.StartsWith('-'))
            throw new ArgumentException($"Invalid branch '{branch}': branch names cannot start with '-'.", nameof(branch));

        // Whole-name rules: git rejects a name ending in '.' but permits an interior component
        // that ends in '.' (e.g. "topic./work").
        if (branch.EndsWith('.'))
            throw new ArgumentException($"Invalid branch '{branch}': branch names must not end with '.'.", nameof(branch));

        if (branch.Contains("..", StringComparison.Ordinal))
            throw new ArgumentException($"Invalid branch '{branch}': branch names must not contain '..'.", nameof(branch));

        if (branch.Contains("@{", StringComparison.Ordinal))
            throw new ArgumentException($"Invalid branch '{branch}': branch names must not contain '@{{'.", nameof(branch));

        char[] forbidden = [' ', '~', '^', ':', '?', '*', '[', '\\'];

        foreach (var component in branch.Split('/'))
        {
            // Empty component catches a leading '/', a trailing '/', and '//'.
            if (component.Length == 0)
                throw new ArgumentException($"Invalid branch '{branch}': branch names must not contain empty path components.", nameof(branch));

            foreach (var c in component)
            {
                if (c < 0x20 || c == 0x7f)
                    throw new ArgumentException($"Invalid branch '{branch}': branch names must not contain control characters.", nameof(branch));
            }

            if (component.IndexOfAny(forbidden) >= 0)
                throw new ArgumentException($"Invalid branch '{branch}': branch names must not contain forbidden characters.", nameof(branch));

            if (component.StartsWith('.'))
                throw new ArgumentException($"Invalid branch '{branch}': no component may begin with '.'.", nameof(branch));

            if (component.EndsWith(".lock", StringComparison.Ordinal))
                throw new ArgumentException($"Invalid branch '{branch}': no component may end with '.lock'.", nameof(branch));
        }
    }

    /// <summary>
    /// Runs a git command capturing exit code, stdout, and stderr without throwing on non-zero exit.
    /// Routed through the optional runner seam when one was supplied.
    /// </summary>
    /// <remarks>
    /// The returned stdout/stderr are RAW and are never redacted here — they are functional data
    /// (tag listings, ref names) that callers parse. Redaction is applied by each caller at the
    /// point where a log entry or exception message is CONSTRUCTED from them.
    /// </remarks>
    /// <param name="workingDir">Working directory for the git process.</param>
    /// <param name="args">Arguments to pass to git.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The exit code, standard output, and standard error of the git command.</returns>
    private async Task<(int exitCode, string stdout, string stderr)> RunGitCaptureAsync(
        string workingDir, string[] args, CancellationToken ct)
    {
        if (_gitRunner is { } runner)
        {
            ct.ThrowIfCancellationRequested();
            var injected = runner(new BrainGitRequest(workingDir, args));
            return (injected.ExitCode, injected.Stdout, injected.Stderr);
        }

        // The SAME pre-launch cancellation check the fake-runner branch performs — see
        // RunGitCoreAsync. Process.Start observes no token, so the check must be explicit.
        ct.ThrowIfCancellationRequested();

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
            // WaitForExitAsync/ReadToEndAsync do NOT terminate the child process, so kill the whole
            // process tree and wait (bounded) for it to actually exit before rethrowing. We must not
            // silently swallow kill/wait failures: we check process.HasExited before and after each
            // step so post-cancellation cleanup does not race a still-running merge process.
            try
            {
                if (!process.HasExited)
                {
                    try
                    {
                        process.Kill(entireProcessTree: true);
                    }
                    catch (InvalidOperationException) when (process.HasExited)
                    {
                        // Process already exited between the HasExited check and Kill — safe to proceed.
                    }
                    catch (Exception)
                    {
                        // Kill failed for another reason. If the process has since exited we are safe;
                        // otherwise there is nothing more we can do — proceed best-effort. The
                        // MergeBranchAsync finally block runs its cleanup on a fresh bounded token and
                        // tolerates failures, so a still-running process cannot deadlock cleanup.
                    }

                    if (!process.HasExited)
                    {
                        // Best-effort bounded wait. If this returns false the process may still be
                        // alive after 5s; we still rethrow so cancellation propagates, and cleanup
                        // (guarded by a fresh token) degrades gracefully.
                        process.WaitForExit(5000);
                    }
                }
            }
            catch
            {
                // Last-resort guard: never prevent the cancellation from propagating.
            }
            throw; // Always rethrow the OperationCanceledException.
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
        // Holds the credential this operation selects, so the catch below can redact an exception
        // THROWN by the git-runner seam (or a failed process launch) with the right credential.
        var credentialBox = new CredentialBox();
        try
        {
            var clonePath = GetClonePath(repoName);
            if (!Directory.Exists(Path.Combine(clonePath, ".git")))
                throw new InvalidOperationException($"Repository '{repoName}' is not cloned.");

            _logger.LogInformation("Creating tag {Tag} on {Branch} for {Repo}", tag, branch, repoName);

            // Refresh the persisted origin credential BEFORE the ls-remote (the first network
            // command). The resolved credential is reused for every command below.
            var credential = await RefreshOriginCredentialAsync(repoName, clonePath, ct, credentialBox);

            // Check whether the tag already exists on origin (no fetch needed).
            // The clone's `origin` is the credential-bearing URL refreshed above, so a
            // failing REMOTE command echoes it through stderr — redact where the message is built.
            var (lsExit, lsStdout, lsStderr) = await RunGitCaptureAsync(
                clonePath, ["ls-remote", "--tags", "origin", $"refs/tags/{tag}"], ct);
            if (lsExit != 0)
                throw new InvalidOperationException(Sanitize(
                    $"Failed to query remote tags for '{repoName}': {lsStderr}", credential));
            if (!string.IsNullOrWhiteSpace(lsStdout))
            {
                _logger.LogInformation("Tag {Tag} already exists on origin for {Repo} — skipping", tag, repoName);
                return false;
            }

            // Fetch the branch (without tags) so we can point the tag at its tip.
            await RunGitAsync(clonePath,
                ["fetch", "--no-tags", "origin", $"+refs/heads/{branch}:refs/remotes/origin/{branch}"], ct, credential);

            if (!await RemoteBranchExistsAsync(clonePath, branch, ct))
                throw new InvalidOperationException(
                    $"Remote branch 'origin/{branch}' does not exist for '{repoName}'.");

            await RunGitAsync(clonePath, ["checkout", "-B", branch, $"origin/{branch}"], ct, credential);

            // Delete any stale local tag with the same name before recreating it.
            var localTag = await RunGitWithOutputAsync(clonePath, ["tag", "-l", tag], ct, credential);
            if (!string.IsNullOrWhiteSpace(localTag))
                await RunGitAsync(clonePath, ["tag", "-d", tag], ct, credential);

            await RunGitAsync(clonePath, ["tag", "-a", tag, "-m", message], ct, credential);
            await RunGitAsync(clonePath, ["push", "origin", tag], ct, credential);

            _logger.LogInformation("Successfully created and pushed tag {Tag} for {Repo}", tag, repoName);
            return true;
        }
        catch (Exception ex) when (TryBuildSafeException(ex, credentialBox, out var safe))
        {
            // The exception carried the operation's credential (a THROWING runner seam, a failed
            // process launch, or any nested inner exception). Replace it with the sanitized form;
            // the unsafe original — and its whole inner chain — is discarded, never logged.
            throw safe;
        }
        finally
        {
            semaphore.Release();
        }
    }

    /// <summary>
    /// Lists all remote branches from the repository's origin, excluding the symbolic <c>HEAD</c> ref.
    /// </summary>
    /// <param name="repoName">Short name of the repository (must have been cloned via <see cref="EnsureCloneAsync"/>).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A sorted list of branch names (without the <c>origin/</c> prefix).</returns>
    public async Task<List<string>> ListRemoteBranchesAsync(string repoName, CancellationToken ct = default)
    {
        ValidateRepoName(repoName);

        var semaphore = await AcquireRepoLockAsync(repoName, ct);
        // Holds the credential this operation selects, so the catch below can redact an exception
        // THROWN by the git-runner seam (or a failed process launch) with the right credential.
        var credentialBox = new CredentialBox();
        try
        {
            var clonePath = GetClonePath(repoName);
            if (!Directory.Exists(Path.Combine(clonePath, ".git")))
                throw new InvalidOperationException($"Repository '{repoName}' is not cloned.");

            // Refresh the persisted origin credential BEFORE the fetch (the first network command).
            var credential = await RefreshOriginCredentialAsync(repoName, clonePath, ct, credentialBox);

            await RunGitAsync(clonePath, ["fetch", "--prune", "origin"], ct, credential);
            var output = await RunGitWithOutputAsync(clonePath, ["for-each-ref", "--format=%(refname:short)", "refs/remotes/origin/"], ct, credential);

            var branches = new List<string>();
            foreach (var line in output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
            {
                var name = line.Trim();
                if (!name.StartsWith("origin/", StringComparison.Ordinal))
                    continue;

                name = name["origin/".Length..];
                if (string.IsNullOrWhiteSpace(name))
                    continue;
                if (name == "HEAD")
                    continue;

                branches.Add(name);
            }

            branches.Sort(StringComparer.OrdinalIgnoreCase);
            return branches;
        }
        catch (Exception ex) when (TryBuildSafeException(ex, credentialBox, out var safe))
        {
            // The exception carried the operation's credential (a THROWING runner seam, a failed
            // process launch, or any nested inner exception). Replace it with the sanitized form;
            // the unsafe original — and its whole inner chain — is discarded, never logged.
            throw safe;
        }
        finally
        {
            semaphore.Release();
        }
    }

    /// <summary>
    /// Refreshes the clone's <c>origin</c> credential and fetches from <c>origin</c> in ONE
    /// locked operation: both the refresh and the fetch run under the SAME acquisition of the
    /// per-repository semaphore, so a concurrent operation can never re-point <c>origin</c> in
    /// between them.
    /// </summary>
    /// <remarks>
    /// The returned text is redacted at construction with the operation's own credential, so the
    /// caller (a Composer tool that echoes it to the model) never has to redact again. On a clone
    /// whose repository is unconfigured or ineligible the refresh is a no-op and this degenerates
    /// to a plain <c>git fetch origin</c> — exactly the previous behaviour.
    /// </remarks>
    /// <param name="repoName">Short name of the repository.</param>
    /// <param name="branch">Optional single branch; see <see cref="IBrainRepoManager.FetchOriginAsync"/>.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task<BrainFetchResult> FetchOriginAsync(
        string repoName, string? branch = null, CancellationToken ct = default)
    {
        ValidateRepoName(repoName);
        // Git-equivalent validation (NOT ValidateBranchOrTagName, which is stricter than git and
        // would reject branches the Composer's own check-ref-format pass already accepted).
        if (!string.IsNullOrWhiteSpace(branch))
            ValidateFetchBranchName(branch);

        var semaphore = await AcquireRepoLockAsync(repoName, ct);
        // Holds the credential this operation selects, so the catch below can redact an exception
        // THROWN by the git-runner seam (or a failed process launch) with the right credential.
        var credentialBox = new CredentialBox();
        try
        {
            var clonePath = GetClonePath(repoName);
            if (!Directory.Exists(Path.Combine(clonePath, ".git")))
                throw new InvalidOperationException($"Repository '{repoName}' is not cloned.");

            // Refresh under THIS lock acquisition, immediately before the fetch it authenticates.
            var credential = await RefreshOriginCredentialAsync(repoName, clonePath, ct, credentialBox);

            // The leading '+' forces the tracking-ref update so a rewound/non-fast-forward remote
            // branch still refreshes refs/remotes/origin/{branch}.
            string[] args = !string.IsNullOrWhiteSpace(branch)
                ? ["fetch", "origin", $"+refs/heads/{branch}:refs/remotes/origin/{branch}"]
                : ["fetch", "origin"];

            var (exitCode, stdout, stderr) = await RunGitCaptureAsync(clonePath, args, ct);

            if (exitCode != 0)
            {
                // Constructed exactly like RunGitAsync's failure text so callers see the familiar
                // shape, and redacted here because this string is RETURNED rather than thrown.
                return new BrainFetchResult(
                    Success: false,
                    Output: string.Empty,
                    Error: Sanitize(
                        $"git {string.Join(' ', args)} failed (exit {exitCode}): {stderr}", credential));
            }

            return new BrainFetchResult(
                Success: true, Output: Sanitize(stdout, credential), Error: null);
        }
        catch (Exception ex) when (TryBuildSafeException(ex, credentialBox, out var safe))
        {
            // The exception carried the operation's credential (a THROWING runner seam, a failed
            // process launch, or any nested inner exception). Replace it with the sanitized form;
            // the unsafe original — and its whole inner chain — is discarded, never logged.
            throw safe;
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
        // Holds the credential this operation selects, so the catch below can redact an exception
        // THROWN by the git-runner seam (or a failed process launch) with the right credential.
        var credentialBox = new CredentialBox();
        try
        {
            var clonePath = GetClonePath(repoName);
            if (!Directory.Exists(Path.Combine(clonePath, ".git")))
                throw new InvalidOperationException($"Repository '{repoName}' is not cloned.");

            _logger.LogInformation("Deleting tag {Tag} for {Repo}", tag, repoName);

            // Refresh the persisted origin credential BEFORE the ls-remote (the first network
            // command). The resolved credential is reused for every command and diagnostic below.
            var credential = await RefreshOriginCredentialAsync(repoName, clonePath, ct, credentialBox);

            // The REMOTE query talks to the credential-bearing `origin`, so its stderr can echo
            // the full clone URL. Redact where the exception message is constructed.
            var (remoteExit, remoteStdout, remoteStderr) = await RunGitCaptureAsync(
                clonePath, ["ls-remote", "--tags", "origin", $"refs/tags/{tag}"], ct);
            if (remoteExit != 0)
                throw new InvalidOperationException(Sanitize(
                    $"Failed to query remote tags for '{repoName}': {remoteStderr}", credential));

            // The LOCAL query never contacts origin, but its message is constructed the same way
            // so a remote-bearing message can never slip through this boundary either.
            var (localExit, localStdout, localStderr) = await RunGitCaptureAsync(
                clonePath, ["tag", "-l", tag], ct);
            if (localExit != 0)
                throw new InvalidOperationException(Sanitize(
                    $"Failed to query local tags for '{repoName}': {localStderr}", credential));

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
                // does not fail the operation. `remoteError` comes from a `push origin` against
                // the credential-bearing remote, so both warnings redact at construction.
                if (localError is not null)
                    _logger.LogWarning("Local tag delete failed for {Tag} in {Repo}: {Error}",
                        tag, repoName, Sanitize(localError, credential));
                if (remoteError is not null)
                    _logger.LogWarning("Remote tag delete failed for {Tag} in {Repo}: {Error}",
                        tag, repoName, Sanitize(remoteError, credential));

                _logger.LogInformation("Successfully deleted tag {Tag} for {Repo}", tag, repoName);
                return true;
            }

            // At least one location had the tag (checked above) but ZERO deletions succeeded —
            // this covers both "existed on both, both failed" and "existed on one side, that
            // sole deletion failed". Surface a genuine failure, with both stderr copies redacted.
            throw new InvalidOperationException(Sanitize(
                $"Failed to delete tag '{tag}' for '{repoName}'. Local error: {localError ?? "(n/a)"}; Remote error: {remoteError ?? "(n/a)"}", credential));
        }
        catch (Exception ex) when (TryBuildSafeException(ex, credentialBox, out var safe))
        {
            // The exception carried the operation's credential (a THROWING runner seam, a failed
            // process launch, or any nested inner exception). Replace it with the sanitized form;
            // the unsafe original — and its whole inner chain — is discarded, never logged.
            throw safe;
        }
        finally
        {
            semaphore.Release();
        }
    }
}
