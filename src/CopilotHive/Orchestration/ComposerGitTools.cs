using System.ComponentModel;
using CopilotHive.Git;
using SharpCoder;

namespace CopilotHive.Orchestration;

public sealed partial class Composer
{
    /// <summary>
    /// Runs a git command in the clone of <paramref name="repoName"/> and returns the output.
    /// Returns an error string if the repo manager is unavailable or the repo is not found.
    /// Output is truncated to <paramref name="maxLines"/> lines with a notice when truncated.
    /// </summary>
    /// <param name="repoName">Short name of the cloned repository.</param>
    /// <param name="args">Git arguments to pass via <see cref="System.Diagnostics.ProcessStartInfo.ArgumentList"/>.</param>
    /// <param name="maxLines">Maximum lines to return before truncating.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Git command output, or an error message string.</returns>
    private async Task<string> RunGitAsync(
        string repoName, string[] args, int maxLines = 500, CancellationToken ct = default)
    {
        if (_repoManager is null)
            return "Git tools are not available — no repository manager configured.";

        var clonePath = _repoManager.GetClonePath(repoName);

        // SECURITY: Prevent path traversal — validate resolved path is under the expected repos directory.
        var expectedReposDir = Path.GetFullPath(_repoManager.WorkDirectory);
        var resolvedPath = Path.GetFullPath(clonePath);
        if (!resolvedPath.StartsWith(expectedReposDir + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            && !string.Equals(resolvedPath, expectedReposDir, StringComparison.Ordinal))
        {
            return $"Repository '{repoName}' is not within the managed repos directory. Access denied.";
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
            return $"Repository '{repoName}' not found. Available: {list}";
        }

        var psi = new System.Diagnostics.ProcessStartInfo("git")
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
            var stderr = stderrTask.Result;
            return $"git {string.Join(' ', args)} failed (exit {process.ExitCode}): {stderr}";
        }

        var output = stdoutTask.Result;
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
    private static async Task<(int ExitCode, string Stdout, string Stderr)> TryRunGitAsync(
        string workDir, string[] args, CancellationToken ct)
    {
        var psi = new System.Diagnostics.ProcessStartInfo("git")
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
            return "❌ repository is required.";

        var validFormats = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "oneline", "short", "full" };
        if (!validFormats.Contains(format))
            return $"❌ Invalid format '{format}'. Must be one of: oneline, short, full.";

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
                return $"❌ Invalid branch '{branch}': branch names cannot start with '-'.";
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
            return "❌ repository is required.";
        if (string.IsNullOrWhiteSpace(ref1))
            return "❌ ref1 is required.";

        // SECURITY: Prevent git option injection for refs
        if (ref1.StartsWith('-'))
            return $"❌ Invalid ref '{ref1}': refs cannot start with '-'.";
        if (!string.IsNullOrWhiteSpace(ref2) && ref2.StartsWith('-'))
            return $"❌ Invalid ref '{ref2}': refs cannot start with '-'.";

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
            return "❌ repository is required.";
        if (string.IsNullOrWhiteSpace(@ref))
            return "❌ ref is required.";

        // SECURITY: Prevent git option injection for ref
        if (@ref.StartsWith('-'))
            return $"❌ Invalid ref '{@ref}': refs cannot start with '-'.";

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
            return "❌ repository is required.";

        var args = new List<string> { "branch", "--list" };

        if (remote)
            args.Add("-r");

        // SECURITY: Prevent git option injection for pattern
        if (!string.IsNullOrWhiteSpace(pattern))
        {
            if (pattern.StartsWith('-'))
                return $"❌ Invalid pattern '{pattern}': patterns cannot start with '-'.";
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
            return "❌ repository is required.";
        if (string.IsNullOrWhiteSpace(path))
            return "❌ path is required.";

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
        if (_repoManager is null)
            return "Git tools are not available — no repository manager configured.";

        // SECURITY: Validate repository name to prevent path traversal / option injection.
        if (string.IsNullOrWhiteSpace(repository))
            return "❌ repository is required.";
        if (repository.Contains('/') || repository.Contains('\\'))
            return $"❌ Invalid repository name '{repository}': repository names cannot contain path separators.";
        if (repository.Contains(".."))
            return $"❌ Invalid repository name '{repository}': repository names cannot contain '..'.";
        if (repository.StartsWith('-'))
            return $"❌ Invalid repository name '{repository}': repository names cannot start with '-'.";

        // Default the remote to origin when not specified.
        if (string.IsNullOrWhiteSpace(remote))
            remote = "origin";

        // SECURITY: Validate remote to prevent option injection, URL/transport abuse, and refspec injection.
        if (remote.StartsWith('-'))
            return $"❌ Invalid remote '{remote}': remote names cannot start with '-'.";
        if (remote.Contains("://") || remote.StartsWith("git@") || remote.StartsWith("ssh://"))
            return $"❌ Invalid remote '{remote}': remote must be a configured remote name, not a URL.";
        if (remote.Contains("::"))
            return $"❌ Invalid remote '{remote}': remote names cannot contain '::'.";
        if (remote.Contains(':') || remote.Contains('+'))
            return $"❌ Invalid remote '{remote}': remote names cannot contain ':' or '+'.";

        var clonePath = _repoManager.GetClonePath(repository);

        // Verify the remote is actually configured for this repository.
        var (remoteExit, remoteStdout, remoteStderr) = await TryRunGitAsync(clonePath, ["remote"], cancellationToken);
        if (remoteExit != 0)
            return $"git remote failed (exit {remoteExit}): {remoteStderr}";

        var configuredRemotes = remoteStdout
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(r => r.Trim())
            .ToList();
        if (!configuredRemotes.Contains(remote))
            return $"❌ Remote '{remote}' is not configured for repository '{repository}'.";

        // Validate branch (if specified) to prevent option injection and refspec abuse.
        if (!string.IsNullOrWhiteSpace(branch))
        {
            if (branch.Contains("@{"))
                return $"❌ Invalid branch '{branch}': branch names cannot contain '@{{'.";

            var (refExit, refStdout, refStderr) = await TryRunGitAsync(
                clonePath, ["check-ref-format", "--branch", branch], cancellationToken);
            if (refExit != 0)
                return $"❌ Invalid branch '{branch}': {refStderr.Trim()}";

            var normalizedBranch = refStdout.Trim();
            if (!string.Equals(normalizedBranch, branch, StringComparison.Ordinal))
                return $"❌ Invalid branch '{branch}': branch name was normalized to '{normalizedBranch}' — use the exact branch name.";
        }

        string[] args = !string.IsNullOrWhiteSpace(branch)
            ? ["fetch", remote, $"+refs/heads/{branch}:refs/remotes/{remote}/{branch}"]
            : ["fetch", remote];

        var result = await RunGitAsync(repository, [.. args], ct: cancellationToken);

        // RunGitAsync prefixes error messages with "git " on non-zero exit.
        if (result.StartsWith("git ", StringComparison.Ordinal))
            return result;

        if (string.IsNullOrWhiteSpace(result))
            return $"Fetched from {remote}. Use git_branch or git_show to inspect fetched refs.";

        return result;
    }
}
