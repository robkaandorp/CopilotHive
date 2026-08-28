using System.Diagnostics;
using CopilotHive.Services;

namespace CopilotHive.Worker;

/// <summary>
/// Git CLI operations for cloning, branching, and pushing.
/// All operations shell out to the git CLI via Process.Start.
/// </summary>
public static class GitOperations
{
    /// <summary>
    /// Maximum number of changed-file paths carried in a <see cref="GitChangeSummary"/>.
    /// This is the single cap governing the diagnostic path list length.
    /// </summary>
    public const int ChangedFilesMaxPaths = 50;

    /// <summary>
    /// Builds a <see cref="GitOperationException"/> whose message has every credential-bearing
    /// repository URL redacted.
    /// </summary>
    /// <remarks>
    /// This is the single exception-CONSTRUCTION boundary of this class. A clone URL carries
    /// userinfo credentials (<c>https://x-access-token:&lt;token&gt;@host/org/repo.git</c>) and git
    /// echoes the remote it was given back through stderr, so both the interpolated URL and the
    /// captured stderr can embed a token. Redaction happens HERE and nowhere earlier: the raw
    /// stdout/stderr returned by <see cref="RunGitCommandAsync"/> stays untouched functional data
    /// (SHAs, porcelain status, numstat diffs, filenames) for the parsing code paths.
    /// </remarks>
    private static GitOperationException Fail(string message) =>
        new(GitUrlRedactor.Redact(message));

    /// <summary>
    /// Clones a remote repository into the specified target directory.
    /// Throws <see cref="GitOperationException"/> on failure.
    /// </summary>
    /// <param name="url">Remote URL of the repository to clone.</param>
    /// <param name="targetDir">Local path where the repository will be cloned.</param>
    /// <param name="ct">Cancellation token.</param>
    public static async Task CloneRepositoryAsync(string url, string targetDir, CancellationToken ct)
    {
        var (exitCode, _, stderr) = await RunGitCommandAsync(
            Path.GetDirectoryName(targetDir) ?? ".",
            $"clone {url} {Path.GetFileName(targetDir)}",
            ct);
        if (exitCode != 0)
            throw Fail($"Failed to clone '{url}': {stderr.Trim()}");

        await ConfigureLocalIdentity(targetDir, ct);
    }

    /// <summary>
    /// Configures a local git identity in the specified repository directory.
    /// Throws <see cref="GitOperationException"/> on failure.
    /// </summary>
    /// <param name="repoDir">Path to the local git repository.</param>
    /// <param name="ct">Cancellation token.</param>
    internal static async Task ConfigureLocalIdentity(string repoDir, CancellationToken ct)
    {
        (int ExitCode, string _, string Stderr) emailResult;
        (int ExitCode, string _, string Stderr) nameResult;

        try
        {
            emailResult = await RunGitCommandAsync(
                repoDir, "config user.email \"copilothive@local\"", ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw Fail($"Failed to set local user.email: {ex.Message}");
        }

        if (emailResult.ExitCode != 0)
            throw Fail($"Failed to set local user.email: {emailResult.Stderr.Trim()}");

        try
        {
            nameResult = await RunGitCommandAsync(
                repoDir, "config user.name \"CopilotHive\"", ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw Fail($"Failed to set local user.name: {ex.Message}");
        }

        if (nameResult.ExitCode != 0)
            throw Fail($"Failed to set local user.name: {nameResult.Stderr.Trim()}");
    }

    /// <summary>
    /// Checks out an existing branch in the specified repository directory.
    /// Throws <see cref="GitOperationException"/> if the branch does not exist.
    /// </summary>
    /// <param name="repoDir">Path to the local git repository.</param>
    /// <param name="branch">Name of the branch to check out.</param>
    /// <param name="ct">Cancellation token.</param>
    public static async Task CheckoutBranchAsync(string repoDir, string branch, CancellationToken ct)
    {
        var (exitCode, _, stderr) = await RunGitCommandAsync(repoDir, $"checkout {branch}", ct);
        if (exitCode != 0)
            throw Fail($"Failed to checkout branch '{branch}': {stderr.Trim()}");
    }

    /// <summary>
    /// Creates a new branch from the given base branch.
    /// <list type="bullet">
    ///   <item><description>If the repository is empty (no commits), creates an orphan branch via
    ///   <c>git checkout --orphan</c>.</description></item>
    ///   <item><description>If the base branch is missing on a non-empty repo, tries fetching it from
    ///   <c>origin</c> and sets up a local tracking branch. If the fetch also fails, creates the base
    ///   branch from the current HEAD so work can continue.</description></item>
    /// </list>
    /// Throws <see cref="GitOperationException"/> on unrecoverable failure.
    /// </summary>
    /// <param name="repoDir">Path to the local git repository.</param>
    /// <param name="branchName">Name of the new branch to create.</param>
    /// <param name="baseBranch">The branch to base the new branch on.</param>
    /// <param name="ct">Cancellation token.</param>
    public static async Task CreateBranchAsync(
        string repoDir, string branchName, string baseBranch, CancellationToken ct)
    {
        var (exitCode1, _, _) = await RunGitCommandAsync(repoDir, $"checkout {baseBranch}", ct);
        if (exitCode1 != 0)
        {
            // Empty repo — create an orphan branch (no base commit exists yet).
            if (await IsRepoEmptyAsync(repoDir, ct))
            {
                var (orphanExit, _, orphanStderr) = await RunGitCommandAsync(
                    repoDir, $"checkout --orphan {branchName}", ct);
                if (orphanExit != 0)
                    throw Fail(
                        $"Failed to create orphan branch '{branchName}': {orphanStderr.Trim()}");
                return;
            }

            // Non-empty repo: base branch not available locally — try fetching from origin.
            var (fetchExit, _, _) = await RunGitCommandAsync(
                repoDir, $"fetch origin {baseBranch}", ct);

            if (fetchExit == 0)
            {
                // Fetch succeeded — create a local tracking branch and check it out.
                var (trackExit, _, trackStderr) = await RunGitCommandAsync(
                    repoDir, $"checkout -b {baseBranch} origin/{baseBranch}", ct);
                if (trackExit != 0)
                    throw Fail(
                        $"Failed to create tracking branch '{baseBranch}' from origin: {trackStderr.Trim()}");
            }
            else
            {
                // Fetch failed — create the base branch from the current HEAD so we can continue.
                var (createBaseExit, _, createBaseStderr) = await RunGitCommandAsync(
                    repoDir, $"checkout -b {baseBranch}", ct);
                if (createBaseExit != 0)
                    throw Fail(
                        $"Failed to create base branch '{baseBranch}' from current HEAD: {createBaseStderr.Trim()}");
            }
        }

        var (exitCode2, _, stderr2) = await RunGitCommandAsync(repoDir, $"checkout -b {branchName}", ct);
        if (exitCode2 != 0)
            throw Fail($"Failed to create branch '{branchName}': {stderr2.Trim()}");
    }

    /// <summary>
    /// Returns true if the repository has no commits (is empty).
    /// Uses <c>git rev-parse HEAD</c>; a non-zero exit code indicates no commits exist.
    /// </summary>
    /// <param name="repoDir">Path to the local git repository.</param>
    /// <param name="ct">Cancellation token.</param>
    public static async Task<bool> IsRepoEmptyAsync(string repoDir, CancellationToken ct)
    {
        var (exitCode, stdout, _) = await RunGitCommandAsync(repoDir, "rev-parse HEAD", ct);
        return exitCode != 0 || string.IsNullOrWhiteSpace(stdout);
    }

    /// <summary>
    /// Force-pushes the specified branch to the remote origin.
    /// Throws <see cref="GitOperationException"/> on failure.
    /// </summary>
    /// <param name="repoDir">Path to the local git repository.</param>
    /// <param name="branch">Name of the branch to push.</param>
    /// <param name="ct">Cancellation token.</param>
    public static async Task PushBranchAsync(string repoDir, string branch, CancellationToken ct)
    {
        var (exitCode, _, stderr) = await RunGitCommandAsync(repoDir, $"push origin {branch} --force", ct);
        if (exitCode != 0)
            throw Fail($"Failed to push branch '{branch}': {stderr.Trim()}");
    }

    /// <summary>
    /// Retrieves current git status information for the repository at the given path.
    /// Compares the current branch to the base branch to capture ALL changes on the feature branch.
    /// </summary>
    /// <param name="repoDir">Path to the local git repository.</param>
    /// <param name="baseBranch">The base branch to diff against (e.g. "origin/main"). Falls back to HEAD~1 if null.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A <see cref="GitChangeSummary"/> containing diff statistics.</returns>
    public static async Task<GitChangeSummary> GetGitStatusAsync(string repoDir, string? baseBranch, CancellationToken ct)
    {
        var filesChanged = 0;
        var insertions = 0;
        var deletions = 0;
        List<string> changedFiles = [];

        // Diff stat: compare all changes on the feature branch vs the base branch.
        // Uses three-dot diff (base...HEAD) to capture everything since the branch point.
        // `--numstat -z` yields NUL-delimited records with UNQUOTED paths, so filenames with
        // tabs, newlines, quotes, backslashes or non-ASCII bytes are captured verbatim.
        // `--stat` is deliberately omitted: its human-readable table would corrupt the -z stream.
        var diffRef = !string.IsNullOrEmpty(baseBranch) ? $"origin/{baseBranch}...HEAD" : "HEAD~1";
        var (statExit, statOut, _) = await RunGitCommandAsync(
            repoDir, $"diff --numstat -z {diffRef}", ct);
        if (statExit == 0)
        {
            ParseDiffStat(statOut, ref filesChanged, ref insertions, ref deletions, changedFiles);
        }
        else
        {
            // Fallback for orphan branches that share no common history with the base:
            // diff HEAD against the empty tree so we still capture all files added on this branch.
            const string EmptyTreeSha = "4b825dc642cb6eb9a060e54bf8d69288fbee4904";
            var (fallbackExit, fallbackOut, _) = await RunGitCommandAsync(
                repoDir, $"diff --numstat -z {EmptyTreeSha} HEAD", ct);
            if (fallbackExit == 0)
                ParseDiffStat(fallbackOut, ref filesChanged, ref insertions, ref deletions, changedFiles);
        }

        return new GitChangeSummary
        {
            FilesChanged = filesChanged,
            Insertions = insertions,
            Deletions = deletions,
            ChangedFiles = changedFiles,
        };
    }

    /// <summary>
    /// Returns true if the working directory has uncommitted changes (staged or unstaged).
    /// Uses <c>git status --porcelain</c> which outputs one line per changed file.
    /// </summary>
    /// <param name="repoDir">Path to the local git repository.</param>
    /// <param name="ct">Cancellation token.</param>
    public static async Task<bool> HasUncommittedChangesAsync(string repoDir, CancellationToken ct)
    {
        var (exitCode, stdout, _) = await RunGitCommandAsync(repoDir, "status --porcelain", ct);
        return exitCode == 0 && !string.IsNullOrWhiteSpace(stdout);
    }

    /// <summary>
    /// Computes the merge-base (common ancestor) between a remote branch and HEAD.
    /// Returns the full commit hash, or null if it cannot be determined.
    /// </summary>
    public static async Task<string?> GetMergeBaseAsync(
        string repoDir, string baseBranch, CancellationToken ct)
    {
        var remoteRef = baseBranch.StartsWith("origin/") ? baseBranch : $"origin/{baseBranch}";
        var (exitCode, stdout, _) = await RunGitCommandAsync(
            repoDir, $"merge-base {remoteRef} HEAD", ct);
        return exitCode == 0 ? stdout.Trim() : null;
    }

    /// <summary>
    /// Environment variable names that are ALWAYS stripped from the environment handed to a
    /// child git process. Matching is by name using <see cref="StringComparer.OrdinalIgnoreCase"/>.
    /// </summary>
    /// <remarks>
    /// <c>GIT_TERMINAL_PROMPT</c> is scrubbed together with the credential variables and then
    /// re-set to <c>0</c> by <see cref="CreateProcessStartInfo"/>, so an inherited value can never
    /// re-enable interactive prompting.
    /// </remarks>
    private static readonly string[] ScrubbedChildEnvNames =
    [
        "GH_TOKEN",
        "GITHUB_TOKEN",
        "GIT_ASKPASS",
        "GITHUB_CONFIG_REPO_TOKEN",
        "GIT_TERMINAL_PROMPT",
    ];

    /// <summary>
    /// Test seam replacing the ENTIRE git process launch. When non-null,
    /// <see cref="ExecuteProcessAsync"/> hands the delegate the raw, PRE-factory
    /// <see cref="GitProcessRequest"/> (never a sanitized <see cref="ProcessStartInfo"/>) and
    /// returns the delegate's result. When null, the default real runner starts a git process.
    /// </summary>
    internal static Func<GitProcessRequest, CancellationToken, Task<GitProcessResult>>? ProcessRunner { get; set; }

    /// <summary>
    /// Returns a NEW dictionary copied from <paramref name="env"/> with the credential-bearing and
    /// prompt-controlling variables removed. The input dictionary is never mutated.
    /// </summary>
    /// <remarks>
    /// Removal is unconditional and matches variable NAMES ordinal-ignore-case. A null value for a
    /// NON-scrubbed key is preserved as a null removal-marker (an environment block cannot hold a
    /// null value, so such an entry behaves as an absent variable at repopulation); a null value for
    /// a scrubbed key is simply removed like any other value.
    /// </remarks>
    /// <param name="env">The environment to copy and scrub.</param>
    internal static IReadOnlyDictionary<string, string?> SanitizeChildEnv(IDictionary<string, string?> env)
    {
        ArgumentNullException.ThrowIfNull(env);

        var copy = new Dictionary<string, string?>();
        foreach (var (key, value) in env)
        {
            if (IsScrubbedChildEnvName(key))
                continue;

            copy[key] = value;
        }

        return copy;
    }

    private static bool IsScrubbedChildEnvName(string name)
    {
        foreach (var scrubbed in ScrubbedChildEnvNames)
        {
            if (string.Equals(name, scrubbed, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    /// <summary>
    /// PURE factory building the <see cref="ProcessStartInfo"/> for a git launch. No side effects
    /// and no process is started. This is the SINGLE source of truth for the child-environment
    /// clear-and-repopulate.
    /// </summary>
    /// <param name="request">The request describing the executable, argument, directory and environment.</param>
    /// <exception cref="ArgumentException">
    /// Thrown when <see cref="GitProcessRequest.Args"/> does not contain exactly one element and
    /// <see cref="GitProcessRequest.TokenizedArgs"/> is null.
    /// </exception>
    internal static ProcessStartInfo CreateProcessStartInfo(GitProcessRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var psi = new ProcessStartInfo(request.Executable)
        {
            WorkingDirectory = request.WorkingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };

        if (request.TokenizedArgs is not null)
        {
            // Tokenized form: each element is added in order to the get-only ArgumentList
            // collection (no quoting — the OS receives the tokens verbatim).
            foreach (var arg in request.TokenizedArgs)
                psi.ArgumentList.Add(arg);

            // Tokenized form: environment copied VERBATIM — no re-scrub, no forced additions.
            // Null-valued entries are omitted at repopulation (an environment block cannot
            // hold a null value).
            psi.Environment.Clear();
            foreach (var (key, value) in request.Env)
            {
                if (value is null)
                    continue;

                psi.Environment[key] = value;
            }

            return psi;
        }

        if (request.Args.Count != 1)
        {
            throw new ArgumentException(
                $"Exactly one opaque argument string is required, got {request.Args.Count}.",
                nameof(request));
        }

        // Legacy opaque form: assigned VERBATIM, no splitting and no re-quoting.
        psi.Arguments = request.Args[0];

        var sanitized = SanitizeChildEnv(new Dictionary<string, string?>(request.Env));

        psi.Environment.Clear();
        foreach (var (key, value) in sanitized)
        {
            // A null value is an absent variable: it is not repopulated.
            if (value is null)
                continue;

            psi.Environment[key] = value;
        }

        // Controlled noninteractive replacement — git must never prompt for credentials.
        psi.Environment["GIT_TERMINAL_PROMPT"] = "0";

        return psi;
    }

    /// <summary>
    /// Snapshots the CURRENT process environment without mutating it, narrowing the non-generic
    /// bound collection down to string keys and <c>string?</c> values.
    /// </summary>
    private static IReadOnlyDictionary<string, string?> SnapshotCurrentProcessEnv()
    {
        var snapshot = new Dictionary<string, string?>();
        foreach (System.Collections.DictionaryEntry entry in Environment.GetEnvironmentVariables())
        {
            if (entry.Key is not string key)
                continue;

            snapshot[key] = entry.Value as string;
        }

        return snapshot;
    }

    /// <summary>
    /// Run a git command and return (exitCode, stdout, stderr).
    /// </summary>
    public static async Task<(int ExitCode, string Stdout, string Stderr)> RunGitCommandAsync(
        string workDir, string args, CancellationToken ct)
    {
        var request = new GitProcessRequest("git", [args], workDir, SnapshotCurrentProcessEnv());
        var result = await ExecuteProcessAsync(request, ct);
        return (result.ExitCode, result.Stdout, result.Stderr);
    }

    /// <summary>
    /// The SINGLE launch implementation for git processes. Both the legacy opaque-argument path
    /// (<see cref="RunGitCommandAsync"/>) and the tokenized path route through here.
    /// </summary>
    /// <remarks>
    /// <para>
    /// When the static <see cref="ProcessRunner"/> seam is non-null, the ENTIRE launch is replaced
    /// by the delegate: the request and token are passed through and no real process is started,
    /// killed, drained, or cleaned up.
    /// </para>
    /// <para>
    /// Cancellation semantics: on cancellation the process tree is killed (best-effort; a Kill
    /// throw is swallowed). When the Kill REQUEST succeeds, the root exit is awaited under a
    /// SINGLE absolute 10-second deadline shared by BOTH the exit wait AND the output draining
    /// (one deadline, not two windows), and then <see cref="OperationCanceledException"/> is
    /// thrown with the CALLER'S token attached. When Kill THROWS, neither the root exit nor the
    /// output reads are awaited: the pending reads are ABANDONED and
    /// <see cref="OperationCanceledException"/> propagates promptly.
    /// A COMPLETED process result wins the natural-exit race REGARDLESS of its exit code: if the
    /// process has already exited before cancellation is observed, the result is returned
    /// normally. Descendant termination is deliberately NOT a production guarantee — arbitrary
    /// descendants are not observable here (no handles/PIDs are exposed). If Kill throws or the
    /// deadline expires, the pending output reads are abandoned and
    /// <see cref="OperationCanceledException"/> is thrown even though the process tree may still
    /// be running.
    /// </para>
    /// </remarks>
    internal static async Task<GitProcessResult> ExecuteProcessAsync(
        GitProcessRequest request, CancellationToken ct)
    {
        var runner = ProcessRunner;
        if (runner is not null)
        {
            // Entire launch replaced by the delegate — no real process launch and no
            // kill/drain/cleanup of a delegate-owned launch.
            return await runner(request, ct);
        }

        // A Process.Start exception (e.g. nonexistent executable → Win32Exception) propagates
        // AS-IS, un-wrapped and un-sanitized.
        using var process = Process.Start(CreateProcessStartInfo(request))
            ?? throw new InvalidOperationException("Failed to start git process.");

        // Output reads are started WITHOUT a token: after a kill the streams close and the
        // reads complete naturally.
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();

        try
        {
            await process.WaitForExitAsync(ct);
        }
        catch (OperationCanceledException)
        {
            // Natural-exit race: a COMPLETED process result wins regardless of its exit code.
            // If the process has already exited before cancellation was observed, return the
            // result normally; the kill path applies only when the process is still running.
            if (process.HasExited)
            {
                return new GitProcessResult(process.ExitCode, await stdoutTask, await stderrTask);
            }

            // Best-effort kill of the entire process tree; a Kill throw is swallowed.
            var killRequested = false;
            try
            {
                process.Kill(entireProcessTree: true);
                killRequested = true;
            }
            catch
            {
                // Swallowed — best-effort only. The declared limitation applies: the process tree
                // may still be running.
            }

            if (!killRequested)
            {
                // Kill FAILED: neither the root exit nor the output reads are awaited. The pending
                // reads are ABANDONED and cancellation propagates PROMPTLY, even though the
                // process tree may still be running.
                throw new OperationCanceledException(ct);
            }

            // SINGLE absolute 10-second deadline shared by BOTH the exit wait AND the output
            // draining (one deadline, not two windows).
            using var deadlineCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var deadline = deadlineCts.Token;

            // Await the root exit under the deadline.
            try
            {
                await process.WaitForExitAsync(CancellationToken.None).WaitAsync(deadline);
            }
            catch (OperationCanceledException)
            {
                // Deadline expired — the process tree may still be running. The pending output
                // reads are ABANDONED, not awaited.
                throw new OperationCanceledException(ct);
            }

            // Await the output reads under the SAME deadline. The streams close after the kill
            // so the reads complete naturally; if the deadline expires they are ABANDONED.
            try
            {
                await Task.WhenAll(stdoutTask, stderrTask).WaitAsync(deadline);
            }
            catch (OperationCanceledException)
            {
                // Deadline expired — reads abandoned.
                throw new OperationCanceledException(ct);
            }

            throw new OperationCanceledException(ct);
        }

        // Normal completion: the process exited (any exit code) before cancellation was
        // observed — the completed result wins the race.
        return new GitProcessResult(process.ExitCode, await stdoutTask, await stderrTask);
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

    /// <summary>
    /// Parses the NUL-delimited output of <c>git diff --numstat -z</c>.
    /// </summary>
    /// <remarks>
    /// With <c>-z</c> git disables C-quoting of paths and uses NUL terminators, so filenames
    /// containing tabs, newlines, quotes, backslashes or non-ASCII bytes survive verbatim.
    /// The wire format is a stream of NUL-terminated records:
    /// <list type="bullet">
    /// <item>ordinary record: <c>insertions\tdeletions\tpath\0</c></item>
    /// <item>rename/copy record: <c>insertions\tdeletions\t\0oldPath\0newPath\0</c> — the path
    /// field inside the first token is EMPTY and the old and new paths follow as two separate
    /// NUL-terminated fields. The NEW path is recorded (never <c>old =&gt; new</c> notation).</item>
    /// </list>
    /// Paths are never trimmed: leading and trailing whitespace is legal in a filename.
    /// Binary files report <c>-</c> for the counts, which simply contributes no line totals.
    /// </remarks>
    private static void ParseDiffStat(
        string numstatOutput,
        ref int filesChanged,
        ref int insertions,
        ref int deletions,
        List<string> changedFiles)
    {
        // Do NOT use RemoveEmptyEntries: empty fields are meaningful (rename records) and
        // splitting keeps a trailing empty token after the final NUL terminator.
        var fields = numstatOutput.Split('\0');

        for (var i = 0; i < fields.Length; i++)
        {
            // Split into at most 3 parts so tabs embedded in the path are preserved.
            var parts = fields[i].Split('\t', 3);
            if (parts.Length < 3) continue; // trailing empty token or malformed record

            filesChanged++;
            if (int.TryParse(parts[0], out var added)) insertions += added;
            if (int.TryParse(parts[1], out var removed)) deletions += removed;

            string path;
            if (parts[2].Length == 0 && i + 2 < fields.Length)
            {
                // Rename/copy record: the two following NUL-terminated fields are old, then new.
                path = fields[i + 2];
                i += 2;
            }
            else
            {
                path = parts[2];
            }

            if (path.Length > 0)
                changedFiles.Add(path);
        }
    }
}

/// <summary>
/// A request to launch a git process. All properties are init-only and the collection CONTENTS
/// are not defensively copied — immutability is SHALLOW: the collections are held by reference
/// and the non-mutation obligation belongs to the implementation that consumes the request.
/// </summary>
/// <param name="Executable">The executable to launch (normally <c>git</c>).</param>
/// <param name="Args">
/// The legacy opaque argument list. Exactly one opaque argument string is supported and it is
/// assigned to <see cref="ProcessStartInfo.Arguments"/> VERBATIM. IGNORED when
/// <paramref name="TokenizedArgs"/> is non-null (callers pass <c>Array.Empty&lt;string&gt;()</c>).
/// </param>
/// <param name="WorkingDirectory">The working directory for the child process.</param>
/// <param name="Env">The environment to hand to the child process.</param>
/// <param name="TokenizedArgs">
/// Optional tokenized argument list. When non-null, each element is added in order to
/// <see cref="ProcessStartInfo.ArgumentList"/> (no quoting) and the environment is copied
/// VERBATIM from <see cref="Env"/> — no re-scrub, no forced additions, null-valued entries
/// omitted at repopulation. When null, the legacy path applies: the <c>Args.Count == 1</c>
/// validation, <c>Arguments = Args[0]</c> verbatim, and the internal five-variable scrub.
/// </param>
internal sealed record GitProcessRequest(
    string Executable,
    IReadOnlyList<string> Args,
    string WorkingDirectory,
    IReadOnlyDictionary<string, string?> Env,
    IReadOnlyList<string>? TokenizedArgs = null);

/// <summary>
/// The result of a git process launch.
/// </summary>
/// <param name="ExitCode">The process exit code.</param>
/// <param name="Stdout">The captured standard output.</param>
/// <param name="Stderr">The captured standard error.</param>
internal sealed record GitProcessResult(int ExitCode, string Stdout, string Stderr);

/// <summary>
/// Exception thrown when a git CLI command fails.
/// </summary>
public sealed class GitOperationException(string message) : Exception(message);
