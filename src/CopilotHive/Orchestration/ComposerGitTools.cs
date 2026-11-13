using System.ComponentModel;
using CopilotHive.Git;
using CopilotHive.Services;
using SharpCoder;

namespace CopilotHive.Orchestration;

public sealed partial class Composer
{
    private const string GitExecutable = "git";

    /// <summary>
    /// Runs a git command in the clone of <paramref name="repoName"/> and returns the output.
    /// Returns an error string if the repo manager is unavailable or the repo is not found.
    /// Output is truncated to <paramref name="maxLines"/> lines with a notice when truncated.
    /// </summary>
    /// <remarks>
    /// This is the raw-output surface of the git tools, so EVERYTHING it returns is model-visible
    /// and passes through <see cref="RedactGitOutput"/>: the success output (redacted BEFORE it is
    /// split and truncated, so the truncation notice counts the redacted output's lines), the
    /// non-zero-exit failure message (whose embedded stderr AND echoed argument list are both
    /// model-visible), and a failure that escapes the process/filesystem work, which is converted
    /// into a classified, credential-free error instead of propagating. A LOCAL cancellation is
    /// never classified — it propagates unchanged.
    /// </remarks>
    /// <param name="repoName">Short name of the cloned repository.</param>
    /// <param name="args">Git arguments to pass via <see cref="System.Diagnostics.ProcessStartInfo.ArgumentList"/>.</param>
    /// <param name="maxLines">Maximum lines to return before truncating.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Redacted git command output, or a redacted error message string.</returns>
    private async Task<string> RunGitAsync(
        string repoName, string[] args, int maxLines = 500, CancellationToken ct = default)
    {
        try
        {
            return await RunGitCoreAsync(repoName, args, maxLines, ct);
        }
        catch (OperationCanceledException)
        {
            // A LOCAL cancellation belongs to the caller and keeps its identity: it is never
            // converted into tool output.
            throw;
        }
        catch (Exception ex)
        {
            return DescribeGitToolFailure(ex);
        }
    }

    /// <summary>
    /// <see cref="RunGitAsync"/>'s work, with the caller-facing failure boundary kept OUTSIDE it so
    /// every step — path resolution, the repository guards, and the process run — shares one
    /// classified, credential-free failure surface.
    /// </summary>
    private async Task<string> RunGitCoreAsync(
        string repoName, string[] args, int maxLines, CancellationToken ct)
    {
        if (_repoManager is null)
            return RedactGitOutput("Git tools are not available — no repository manager configured.");

        var clonePath = _repoManager.GetClonePath(repoName);

        // SECURITY: Prevent path traversal — validate resolved path is under the expected repos directory.
        var expectedReposDir = Path.GetFullPath(_repoManager.WorkDirectory);
        var resolvedPath = Path.GetFullPath(clonePath);
        if (!resolvedPath.StartsWith(expectedReposDir + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            && !string.Equals(resolvedPath, expectedReposDir, StringComparison.Ordinal))
        {
            return RedactGitOutput($"Repository '{repoName}' is not within the managed repos directory. Access denied.");
        }

        if (!Directory.Exists(Path.Combine(clonePath, ".git")))
        {
            var reposDir = _repoManager.WorkDirectory;
            var available = Directory.Exists(reposDir)
                ? Directory.GetDirectories(reposDir)
                    .Where(d => Directory.Exists(Path.Combine(d, ".git")))
                    .Select(Path.GetFileName)
                    .Where(n => n is not null)
                    .ToList()
                : [];
            var list = available.Count > 0 ? string.Join(", ", available) : "(none)";
            return RedactGitOutput($"Repository '{repoName}' not found. Available: {list}");
        }

        var psi = new System.Diagnostics.ProcessStartInfo(GitExecutable)
        {
            WorkingDirectory = clonePath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };

        foreach (var arg in args)
            psi.ArgumentList.Add(arg);

        using var process = System.Diagnostics.Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start git process");

        var stdoutTask = process.StandardOutput.ReadToEndAsync(ct);
        var stderrTask = process.StandardError.ReadToEndAsync(ct);

        await Task.WhenAll(stdoutTask, stderrTask);
        await process.WaitForExitAsync(ct);

        if (process.ExitCode != 0)
        {
            // The failure text is one string handed to the model, and it embeds BOTH git's stderr
            // and the argument list this tool passed — so the whole message is redacted.
            var stderr = stderrTask.Result;
            return RedactGitOutput(
                $"git {string.Join(' ', args)} failed (exit {process.ExitCode}): {stderr}");
        }

        // Redaction happens immediately after reading and BEFORE truncation: the text that is
        // truncated, and the total line count reported in the truncation notice, therefore both
        // describe the REDACTED output rather than secret-bearing raw output. The transform is
        // documented never to add or remove a line break (asserted directly in
        // GitToolOutputRedactorTests), which is what makes the reported count stable.
        var output = RedactGitOutput(stdoutTask.Result);
        var lines = output.Split('\n');
        if (lines.Length <= maxLines)
            return output;

        var truncated = string.Join('\n', lines.Take(maxLines));
        return truncated + $"\n... (truncated, {lines.Length} lines total)";
    }

    /// <summary>
    /// Runs a git command in <paramref name="workDir"/> and returns the raw result without
    /// throwing for non-zero exit codes. Used for internal validation queries.
    /// </summary>
    /// <remarks>
    /// The result is INTERNAL validation data and is deliberately kept raw here. A caller that
    /// surfaces any part of it to the model (for example the remote-listing stderr that
    /// <see cref="GitFetchAsync"/> returns) is responsible for the redaction, which is why every
    /// such caller runs the text through <see cref="RedactGitOutput"/>.
    /// </remarks>
    private static async Task<(int ExitCode, string Stdout, string Stderr)> TryRunGitAsync(
        string workDir, string[] args, CancellationToken ct)
    {
        var psi = new System.Diagnostics.ProcessStartInfo(GitExecutable)
        {
            WorkingDirectory = workDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };

        foreach (var arg in args)
            psi.ArgumentList.Add(arg);

        using var process = System.Diagnostics.Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start git process");

        var stdoutTask = process.StandardOutput.ReadToEndAsync(ct);
        var stderrTask = process.StandardError.ReadToEndAsync(ct);

        await Task.WhenAll(stdoutTask, stderrTask);
        await process.WaitForExitAsync(ct);

        return (process.ExitCode, stdoutTask.Result, stderrTask.Result);
    }

    /// <summary>
    /// Applies the `git check-ref-format --branch` result to <paramref name="branch"/> and
    /// returns an error message if the branch is invalid or was normalized to a different value
    /// (which would otherwise cause git to silently fetch/act on a different ref than requested).
    /// Returns <c>null</c> when <paramref name="branch"/> is accepted as-is.
    /// </summary>
    /// <remarks>
    /// Extracted as a pure function (no process spawning) so the equality-guard behavior can be
    /// unit-tested directly with synthetic exit codes/output, without needing to fake or spawn a
    /// git executable.
    /// <para>
    /// The returned string is RAW validation data: it quotes the caller's branch verbatim and
    /// embeds git's stderr. It stays internal until <see cref="GitFetchAsync"/> turns it into tool
    /// output, and that boundary is where it is redacted.
    /// </para>
    /// </remarks>
    internal static string? ValidateCheckRefFormatResult(string branch, int exitCode, string stdout, string stderr)
    {
        if (exitCode != 0)
            return $"❌ Invalid branch '{branch}': {stderr.Trim()}";

        var normalizedBranch = stdout.Trim();
        if (!string.Equals(normalizedBranch, branch, StringComparison.Ordinal))
            return $"❌ Invalid branch '{branch}': branch name was normalized to '{normalizedBranch}' — use the exact branch name.";

        return null;
    }

    // ── Central credential-redaction boundary ──

    /// <summary>
    /// The ONE credential-redaction transform for text about to become model-visible git tool
    /// output. Every string these tools return passes through it: raw git stdout, the failure
    /// message's stderr and echoed arguments, a validation rejection that quotes caller input, and
    /// a classified exception summary.
    /// </summary>
    /// <remarks>
    /// The transform itself lives in <see cref="GitToolOutputRedactor"/> so it is composed with —
    /// never reimplemented next to — the shared URL grammar. This method only supplies the
    /// KNOWN-SECRET INVENTORY, which is read FRESH on every operation: a value that changes
    /// between calls must neither keep being redacted from a stale copy nor escape because of one.
    /// </remarks>
    /// <param name="text">The model-visible text. May be null or empty.</param>
    /// <returns>The text with every recognized credential replaced by the shared placeholder.</returns>
    private string RedactGitOutput(string? text) =>
        GitToolOutputRedactor.Redact(text, GitToolSecretInventory());

    /// <summary>
    /// The known-secret inventory this Composer can name: the API key captured at construction
    /// (its value at that moment — rotation is NOT tracked) plus the current value of each
    /// credential environment variable, read fresh by every call.
    /// </summary>
    /// <remarks>
    /// No configuration-file token field is assumed. Values shorter than
    /// <see cref="GitToolOutputRedactor.MinimumInventoryValueLength"/> are ignored by the transform
    /// itself, because replacing a one- or two-character value would rewrite ordinary output.
    /// </remarks>
    private IEnumerable<string?> GitToolSecretInventory()
    {
        yield return _ollamaApiKey;

        foreach (var value in GitToolOutputRedactor.ReadEnvironmentSecrets())
            yield return value;
    }

    /// <summary>
    /// Classifies an exception that escaped git-tool work into credential-free, model-visible text.
    /// </summary>
    /// <remarks>
    /// The exception MESSAGE is never echoed. A clone path, a remote URL, or a process error can
    /// carry a credential the Composer does not know (it holds no inventory of a manager's selected
    /// token), and a structural scanner cannot recognize a BARE token in arbitrary prose, so only
    /// the exception TYPE name is reported: a type name is structurally incapable of carrying a
    /// credential and still tells the model what kind of failure occurred. The redacted message is
    /// written to the orchestrator log instead, where it stays diagnosable.
    /// </remarks>
    /// <param name="ex">The escaping exception.</param>
    /// <returns>A redacted, classified tool error string.</returns>
    private string DescribeGitToolFailure(Exception ex)
    {
        _logger.LogWarning(
            "Git tool failure ({ExceptionType}): {RedactedMessage}",
            ex.GetType().Name,
            LogSanitizer.SanitizeText(RedactGitOutput(ex.Message)));

        return RedactGitOutput(
            $"❌ git tool failed ({ex.GetType().Name}). See the orchestrator logs for details.");
    }

    [Description("View commit history for a repository.")]
    internal async Task<string> GitLogAsync(
        [Description("Repository name")] string repository,
        [Description("Maximum number of commits to show. Default: 20")] int max_count = 20,
        [Description("Branch or ref to show history for. Optional — uses current HEAD if omitted")] string? branch = null,
        [Description("Limit to commits that touch this file or directory path. Optional")] string? path = null,
        [Description("Show only commits after this date (e.g. '2024-01-01'). Optional")] string? since = null,
        [Description("Log format: oneline, short, or full. Default: oneline")] string format = "oneline",
        [Description("Include diffstat summary per commit. Default: false")] bool stat = false,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(repository))
            return RedactGitOutput("❌ repository is required.");

        var validFormats = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "oneline", "short", "full" };
        if (!validFormats.Contains(format))
            return RedactGitOutput($"❌ Invalid format '{format}'. Must be one of: oneline, short, full.");

        var args = new List<string> { "log", $"--max-count={max_count}" };

        // Use correct git format flags: 'oneline' needs --oneline, not --format=oneline
        var formatArg = format.ToLowerInvariant() switch
        {
            "oneline" => "--oneline",
            "short" => "--format=short",
            "full" => "--format=full",
            _ => "--oneline",
        };
        args.Add(formatArg);

        if (stat)
            args.Add("--stat");

        if (!string.IsNullOrWhiteSpace(since))
            args.Add($"--since={since}");

        // SECURITY: Validate branch to prevent git option injection
        if (!string.IsNullOrWhiteSpace(branch))
        {
            if (branch.StartsWith('-'))
                return RedactGitOutput($"❌ Invalid branch '{branch}': branch names cannot start with '-'.");
            args.Add(branch);
        }

        if (!string.IsNullOrWhiteSpace(path))
        {
            args.Add("--");
            args.Add(path);
        }

        return await RunGitAsync(repository, [.. args], ct: cancellationToken);
    }

    [Description("Compare changes between two refs, or between a ref and HEAD.")]
    internal async Task<string> GitDiffAsync(
        [Description("Repository name")] string repository,
        [Description("First ref (commit, branch, or tag) to compare")] string ref1,
        [Description("Second ref to compare against. If omitted, diffs ref1 against HEAD")] string? ref2 = null,
        [Description("Limit the diff to this file or directory path. Optional")] string? path = null,
        [Description("Show only the diffstat summary, not the full diff. Default: false")] bool stat_only = false,
        [Description("Maximum lines of output to return. Default: 500")] int max_lines = 500,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(repository))
            return RedactGitOutput("❌ repository is required.");
        if (string.IsNullOrWhiteSpace(ref1))
            return RedactGitOutput("❌ ref1 is required.");

        // SECURITY: Prevent git option injection for refs
        if (ref1.StartsWith('-'))
            return RedactGitOutput($"❌ Invalid ref '{ref1}': refs cannot start with '-'.");
        if (!string.IsNullOrWhiteSpace(ref2) && ref2.StartsWith('-'))
            return RedactGitOutput($"❌ Invalid ref '{ref2}': refs cannot start with '-'.");

        var args = new List<string> { "diff" };

        if (stat_only)
            args.Add("--stat");

        if (!string.IsNullOrWhiteSpace(ref2))
            args.Add($"{ref1}..{ref2}");
        else
            args.Add(ref1);

        if (!string.IsNullOrWhiteSpace(path))
        {
            args.Add("--");
            args.Add(path);
        }

        return await RunGitAsync(repository, [.. args], maxLines: max_lines, ct: cancellationToken);
    }

    [Description("View the details and diff of a specific commit.")]
    internal async Task<string> GitShowAsync(
        [Description("Repository name")] string repository,
        [Description("Commit ref (SHA, tag, or branch) to show")] string @ref,
        [Description("Show only the diffstat summary, not the full diff. Default: false")] bool stat_only = false,
        [Description("Limit output to this file or directory path. Optional")] string? path = null,
        [Description("Maximum lines of output to return. Default: 500")] int max_lines = 500,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(repository))
            return RedactGitOutput("❌ repository is required.");
        if (string.IsNullOrWhiteSpace(@ref))
            return RedactGitOutput("❌ ref is required.");

        // SECURITY: Prevent git option injection for ref
        if (@ref.StartsWith('-'))
            return RedactGitOutput($"❌ Invalid ref '{@ref}': refs cannot start with '-'.");

        var args = new List<string> { "show" };

        if (stat_only)
            args.Add("--stat");

        args.Add(@ref);

        if (!string.IsNullOrWhiteSpace(path))
        {
            args.Add("--");
            args.Add(path);
        }

        return await RunGitAsync(repository, [.. args], maxLines: max_lines, ct: cancellationToken);
    }

    [Description("List local or remote branches in a repository.")]
    internal async Task<string> GitBranchAsync(
        [Description("Repository name")] string repository,
        [Description("Optional glob pattern to filter branches (e.g. 'feature/*')")] string? pattern = null,
        [Description("List remote tracking branches instead of local branches. Default: false")] bool remote = false,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(repository))
            return RedactGitOutput("❌ repository is required.");

        var args = new List<string> { "branch", "--list" };

        if (remote)
            args.Add("-r");

        // SECURITY: Prevent git option injection for pattern
        if (!string.IsNullOrWhiteSpace(pattern))
        {
            if (pattern.StartsWith('-'))
                return RedactGitOutput($"❌ Invalid pattern '{pattern}': patterns cannot start with '-'.");
            args.Add(pattern);
        }

        return await RunGitAsync(repository, [.. args], ct: cancellationToken);
    }

    [Description("Show line-by-line authorship information for a file.")]
    internal async Task<string> GitBlameAsync(
        [Description("Repository name")] string repository,
        [Description("Relative path to the file within the repository")] string path,
        [Description("First line to show blame for (1-indexed). Optional")] int? start_line = null,
        [Description("Last line to show blame for (1-indexed, inclusive). Optional")] int? end_line = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(repository))
            return RedactGitOutput("❌ repository is required.");
        if (string.IsNullOrWhiteSpace(path))
            return RedactGitOutput("❌ path is required.");

        var args = new List<string> { "blame" };

        if (start_line.HasValue || end_line.HasValue)
        {
            var start = start_line ?? 1;
            var end = end_line ?? start;
            args.Add($"-L{start},{end}");
        }

        // SECURITY: Place path after -- to terminate option parsing
        args.Add("--");
        args.Add(path);

        return await RunGitAsync(repository, [.. args], ct: cancellationToken);
    }

    [Description("Fetch from a remote repository so remote branches and commits are available for inspection.")]
    internal async Task<string> GitFetchAsync(
        [Description("Repository name")] string repository,
        [Description("Remote name. Default: origin")] string? remote = null,
        [Description("Specific branch to fetch. If omitted, fetches all branches.")] string? branch = null,
        CancellationToken cancellationToken = default)
    {
        if (await AuthorizationDenialAsync("git_fetch", cancellationToken) is { } deny_git_fetch)
            return deny_git_fetch;

        if (_repoManager is null)
            return RedactGitOutput("Git tools are not available — no repository manager configured.");

        // SECURITY: Validate repository name to prevent path traversal / option injection.
        if (string.IsNullOrWhiteSpace(repository))
            return RedactGitOutput("❌ repository is required.");
        if (repository.Contains('/') || repository.Contains('\\'))
            return RedactGitOutput($"❌ Invalid repository name '{repository}': repository names cannot contain path separators.");
        if (repository.Contains(".."))
            return RedactGitOutput($"❌ Invalid repository name '{repository}': repository names cannot contain '..'.");
        if (repository.StartsWith('-'))
            return RedactGitOutput($"❌ Invalid repository name '{repository}': repository names cannot start with '-'.");

        // Default the remote to origin when not specified.
        if (string.IsNullOrWhiteSpace(remote))
            remote = "origin";

        // SECURITY: Validate remote to prevent option injection, URL/transport abuse, and refspec injection.
        if (remote.StartsWith('-'))
            return RedactGitOutput($"❌ Invalid remote '{remote}': remote names cannot start with '-'.");
        if (remote.Contains("://") || remote.StartsWith("git@") || remote.StartsWith("ssh://"))
            return RedactGitOutput($"❌ Invalid remote '{remote}': remote must be a configured remote name, not a URL.");
        if (remote.Contains("::"))
            return RedactGitOutput($"❌ Invalid remote '{remote}': remote names cannot contain '::'.");
        if (remote.Contains(':') || remote.Contains('+'))
            return RedactGitOutput($"❌ Invalid remote '{remote}': remote names cannot contain ':' or '+'.");

        // The probe reads git's own output and touches the filesystem, so it runs INSIDE the tool's
        // failure boundary: whatever it surfaces — the remote-listing stderr and the check-ref-format
        // rejection included — is redacted before it can become tool output, and a failure that
        // escapes (a process/filesystem error) is classified instead of thrown. Cancellation is
        // never classified.
        try
        {
            var clonePath = _repoManager.GetClonePath(repository);

            // Verify the remote is actually configured for this repository.
            var (remoteExit, remoteStdout, remoteStderr) = await TryRunGitAsync(clonePath, ["remote"], cancellationToken);
            if (remoteExit != 0)
                return RedactGitOutput($"git remote failed (exit {remoteExit}): {remoteStderr}");

            var configuredRemotes = remoteStdout
                .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Select(r => r.Trim())
                .ToList();
            if (!configuredRemotes.Contains(remote))
                return RedactGitOutput($"❌ Remote '{remote}' is not configured for repository '{repository}'.");

            // Validate branch (if specified) to prevent option injection and refspec abuse.
            if (!string.IsNullOrWhiteSpace(branch))
            {
                if (branch.Contains("@{"))
                    return RedactGitOutput($"❌ Invalid branch '{branch}': branch names cannot contain '@{{'.");

                var (refExit, refStdout, refStderr) = await TryRunGitAsync(
                    clonePath, ["check-ref-format", "--branch", branch], cancellationToken);

                // The pure validator returns RAW validation data; it becomes tool output HERE, so
                // this is the boundary where its rejection is redacted.
                var rejection = ValidateCheckRefFormatResult(branch, refExit, refStdout, refStderr);
                if (rejection is not null)
                    return RedactGitOutput(rejection);
            }
        }
        catch (OperationCanceledException)
        {
            // A LOCAL cancellation keeps its identity and propagates unchanged.
            throw;
        }
        catch (Exception ex)
        {
            return DescribeGitToolFailure(ex);
        }

        // ORIGIN goes through the manager's LOCKED refresh+fetch so the stored OAuth admin
        // credential is applied to the very fetch it authenticates — never refresh-then-unlocked-
        // fetch, which a concurrent operation could slip between. Any OTHER configured remote name
        // stays on the local runner and is NEVER given the admin credential.
        if (string.Equals(remote, "origin", StringComparison.Ordinal))
        {
            BrainFetchResult fetch;
            try
            {
                // The manager already redacts Output/Error with the operation's own credential,
                // and applies its own credential-aware boundary to anything it THROWS.
                fetch = await _repoManager.FetchOriginAsync(repository, branch, cancellationToken);
            }
            catch (ArgumentException ex)
            {
                // The manager applies its own repo/branch validation. Surface a rejection as a
                // tool ERROR STRING — this tool never throws for bad input.
                return RedactGitOutput($"❌ {ex.Message}");
            }
            catch (OperationCanceledException ex)
            {
                // Cancellation must keep propagating AS cancellation (callers and the agent host
                // rely on it), but an arbitrary IBrainRepoManager can throw an OCE whose message or
                // inner chain embeds a credential. Rethrowing it verbatim would let that payload
                // escape into logs and agent traces, so a fresh, credential-free OCE carrying the
                // same token — and NO inner exception — is thrown in its place.
                throw new OperationCanceledException(
                    "git fetch origin was cancelled.", ex.CancellationToken);
            }
            catch (Exception ex)
            {
                // This text becomes tool OUTPUT handed straight to the model, and the Composer
                // does NOT know the manager's selected credential — so it cannot redact a BARE
                // token the way the manager can. A URL scanner only removes userinfo from
                // well-formed URLs, which a bare token is not.
                //
                // The exception MESSAGE is therefore never echoed. Only the exception TYPE name is
                // reported: a type name is structurally incapable of carrying a credential, and it
                // still tells the model what kind of failure occurred. The real
                // BrainRepoManager already sanitizes what it throws; this boundary additionally
                // contains any other IBrainRepoManager implementation.
                return RedactGitOutput(
                    $"git fetch {remote} failed ({ex.GetType().Name}). "
                    + "See the orchestrator logs for details.");
            }

            if (!fetch.Success)
                return RedactGitOutput(fetch.Error ?? $"git fetch {remote} failed.");

            if (string.IsNullOrWhiteSpace(fetch.Output))
                return RedactGitOutput($"Fetched from {remote}. Use git_branch or git_show to inspect fetched refs.");

            return RedactGitOutput(fetch.Output);
        }

        string[] args = !string.IsNullOrWhiteSpace(branch)
            ? ["fetch", remote, $"+refs/heads/{branch}:refs/remotes/{remote}/{branch}"]
            : ["fetch", remote];

        var result = await RunGitAsync(repository, [.. args], ct: cancellationToken);

        // RunGitAsync prefixes error messages with "git " on non-zero exit.
        if (result.StartsWith("git ", StringComparison.Ordinal))
            return result;

        if (string.IsNullOrWhiteSpace(result))
            return RedactGitOutput($"Fetched from {remote}. Use git_branch or git_show to inspect fetched refs.");

        return result;
    }
}
