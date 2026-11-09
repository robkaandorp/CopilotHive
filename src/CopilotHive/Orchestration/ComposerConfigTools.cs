using System.ComponentModel;
using CopilotHive.Shared;
using CopilotHive.Workers;
using SharpCoder;

namespace CopilotHive.Orchestration;

public sealed partial class Composer
{
    /// <summary>
    /// Lists files in the config repo root or a subdirectory, returning relative paths.
    /// </summary>
    [Description("List files under the config repo root or a subdirectory. Returns relative paths.")]
    internal Task<string> ListConfigFilesAsync(
        [Description("Subdirectory to list files under. Leave empty for the repo root.")] string? path = null,
        CancellationToken cancellationToken = default)
    {
        if (_configRepo is null)
            return Task.FromResult("❌ Config repo tools are not available — no config repo configured.");

        var baseDir = _configRepo.LocalPath;
        string targetDir;

        if (string.IsNullOrWhiteSpace(path))
        {
            targetDir = baseDir;
        }
        else
        {
            var resolved = Path.GetFullPath(Path.Combine(baseDir, path));
            if (!resolved.StartsWith(baseDir + Path.DirectorySeparatorChar, StringComparison.Ordinal)
                && !string.Equals(resolved, baseDir, StringComparison.Ordinal))
                return Task.FromResult($"❌ Path '{path}' is outside the config repo. Access denied.");
            targetDir = resolved;
        }

        if (!Directory.Exists(targetDir))
            return Task.FromResult($"❌ Directory '{path ?? "(root)"}' not found in config repo.");

        var files = Directory.GetFiles(targetDir, "*", SearchOption.AllDirectories)
            .Select(f => Path.GetRelativePath(baseDir, f).Replace('\\', '/'))
            .OrderBy(f => f)
            .ToList();

        if (files.Count == 0)
            return Task.FromResult("(no files found)");

        return Task.FromResult(string.Join('\n', files));
    }

    /// <summary>
    /// Reads a file from the config repo with line numbers, validating the path stays inside the repo.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why <c>.git</c> is refused.</b> <c>.git/config</c> and its siblings hold remote URLs,
    /// credential helpers and other repository-internal state; on a deployment whose origin still
    /// carries a persisted credential, reading that file would disclose it to the model. This tool
    /// therefore never returns content of a <c>.git</c> directory, a <c>.git</c> file, or anything
    /// nested under either.
    /// </para>
    /// <para>
    /// <b>What is covered.</b> The <c>.git</c> rule is applied to the REQUESTED spelling (split on
    /// both separators, matched case-insensitively, so <c>.GIt</c>, <c>foo\.git\config</c> and
    /// <c>foo/.git/config</c> are all caught) and to the CANONICAL form of whatever already exists on
    /// disk — with the configured root and EVERY existing directory ancestor physically canonicalized,
    /// and every symbolic link or reparse point (file or directory, at any depth, including directory
    /// ancestors inside a link's own target) resolved to its real location under a bounded hop limit.
    /// The fully resolved path is what is checked and what is read. A link whose target cannot be
    /// determined, or a link loop, is refused (fail closed) rather than read through.
    /// </para>
    /// <para>
    /// <b>ACCEPTED LIMITS — not covered.</b> A symbolic link REPLACED between resolution and the read
    /// can still redirect the read (there is no atomic handle-relative open here), and a HARD LINK to
    /// a file outside the config-repo root (or into a <c>.git</c> location) is indistinguishable from
    /// an ordinary file by path alone. Neither is addressed by this guard; it makes no claim beyond
    /// the paths it resolves.
    /// </para>
    /// </remarks>
    [Description("Read a config repo file with line numbers. Validates that the resolved path stays within the config repo root.")]
    internal async Task<string> ReadConfigFileAsync(
        [Description("Relative path to the file within the config repo.")] string path,
        [Description("Line number to start reading from (1-indexed). Default: 1")] int offset = 1,
        [Description("Maximum number of lines to read. Default: 200")] int limit = 200,
        CancellationToken cancellationToken = default)
    {
        if (_configRepo is null)
            return "❌ Config repo tools are not available — no config repo configured.";

        if (string.IsNullOrWhiteSpace(path))
            return "❌ path is required.";

        var baseDir = _configRepo.LocalPath;
        var resolved = Path.GetFullPath(Path.Combine(baseDir, path));

        // SECURITY: Prevent path traversal
        if (!resolved.StartsWith(baseDir + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            && !string.Equals(resolved, baseDir, StringComparison.Ordinal))
            return $"❌ Path '{path}' is outside the config repo. Access denied.";

        // SECURITY: the requested spelling must not name .git anywhere in the path.
        if (NamesGitMetadata(path) || NamesGitMetadata(resolved))
            return $"❌ Path '{path}' refers to git metadata. Access denied.";

        // SECURITY: PHYSICALLY canonicalize the configured root and the requested path — every
        // ancestor directory, and every link (file or directory) at ANY depth, including the
        // ancestors INSIDE a link's own target — then decide on the fully resolved paths. A link
        // target is never trusted as already canonical: `leaf -> <root>/bridge/config` with
        // `bridge -> <root>/.git` resolves through `bridge` here, so the decision sees `.git`.
        var canonicalRoot = TryResolvePhysicalPath(Path.GetFullPath(baseDir));
        if (canonicalRoot is null)
            return $"❌ Path '{path}' could not be resolved safely. Access denied.";

        var canonical = TryResolvePhysicalPath(resolved);
        if (canonical is null)
            return $"❌ Path '{path}' could not be resolved safely. Access denied.";

        if (!canonical.StartsWith(canonicalRoot + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            return $"❌ Path '{path}' is outside the config repo. Access denied.";

        // Only the part BELOW the canonical root is inspected for `.git`.
        if (NamesGitMetadata(Path.GetRelativePath(canonicalRoot, canonical)))
            return $"❌ Path '{path}' refers to git metadata. Access denied.";

        if (!File.Exists(canonical))
            return $"❌ File '{path}' not found in config repo.";

        // Read through the CANONICAL path: an uncanonicalized alias is never opened.
        var lines = await File.ReadAllLinesAsync(canonical, cancellationToken);
        var startIndex = Math.Max(0, offset - 1);
        if (startIndex >= lines.Length)
            return $"❌ offset {offset} is beyond end of file ({lines.Length} lines total).";

        var sb = new System.Text.StringBuilder();
        var end = Math.Min(startIndex + limit, lines.Length);
        for (var i = startIndex; i < end; i++)
            sb.AppendLine($"{i + 1}: {lines[i]}");

        if (end < lines.Length)
            sb.AppendLine($"... ({lines.Length - end} more lines — use offset={end + 1} to continue)");

        return sb.ToString().TrimEnd();
    }

    /// <summary>
    /// Whether <paramref name="path"/> names git metadata anywhere: a path segment equal to
    /// <c>.git</c>, matched case-insensitively and split on BOTH separators so the rule holds for the
    /// caller's spelling (<c>foo\.git\config</c>, <c>foo/.git/config</c>, <c>.GIT</c>) as well as for
    /// a canonical form. A trailing separator is ignored, so <c>agents/.git/</c> is still caught.
    /// </summary>
    private static bool NamesGitMetadata(string path)
    {
        if (string.IsNullOrEmpty(path))
            return false;

        var segments = path.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries);
        foreach (var segment in segments)
        {
            // A Windows drive or volume designator ("C:") is never ".git"; the comparison is exact.
            if (segment.Equals(".git", StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    /// <summary>The bound on link traversals while canonicalizing one path (loop guard).</summary>
    private const int MaxLinkHops = 40;

    /// <summary>
    /// PHYSICALLY canonicalizes <paramref name="absolutePath"/>: walks it component by component from
    /// the filesystem root, and whenever a component is a symbolic link or reparse point — whether it
    /// is the final file, a directory in the chain, or a directory INSIDE a previous link's target —
    /// splices the link's own target into the remaining work and keeps walking. <c>.</c> and
    /// <c>..</c> inside a link target are applied to the PHYSICAL location reached so far, never
    /// lexically against an unresolved one.
    /// </summary>
    /// <returns>
    /// The fully resolved absolute path, or <c>null</c> when it cannot be determined: more than
    /// <see cref="MaxLinkHops"/> link traversals (a loop), a DANGLING link, or any I/O error. A
    /// <c>null</c> result is a FAIL-CLOSED signal — callers deny rather than read.
    /// </returns>
    /// <remarks>
    /// This is not a <c>ResolveLinkTarget(true).FullName</c> shortcut on purpose: that call resolves
    /// the link chain but does NOT canonicalize the directory ancestors inside the returned target, so
    /// it can hand back a path that still aliases <c>.git</c> or an outside location through a
    /// directory link. Only what exists is followed; a non-existent tail is kept lexically since it
    /// cannot alias anything. No cache or other helper state is kept.
    /// </remarks>
    private static string? TryResolvePhysicalPath(string absolutePath)
    {
        try
        {
            var root = Path.GetPathRoot(absolutePath);
            if (string.IsNullOrEmpty(root))
                return null;

            var current = root; // Kept as-is: "C:\" / "/" must stay rooted for Path.Combine.
            var pending = new LinkedList<string>(SplitSegments(absolutePath[root.Length..]));
            var hops = 0;
            var followedLink = false;

            while (pending.First is { } node)
            {
                var segment = node.Value;
                pending.RemoveFirst();

                if (segment == ".")
                    continue;

                if (segment == "..")
                {
                    current = Path.GetDirectoryName(current) ?? current;
                    continue;
                }

                var candidate = Path.Combine(current, segment);
                // FileSystemInfo.LinkTarget reads the link itself (lstat/readlink), so it also works
                // for a dangling link and for a directory link.
                var linkTarget = new FileInfo(candidate).LinkTarget;
                if (linkTarget is null)
                {
                    current = candidate;
                    continue;
                }

                if (++hops > MaxLinkHops)
                    return null; // A loop (or an absurdly deep chain) — fail closed.

                followedLink = true;
                var targetRoot = Path.GetPathRoot(linkTarget);
                if (!string.IsNullOrEmpty(targetRoot))
                {
                    // Absolute target: restart from ITS root.
                    current = targetRoot;
                    linkTarget = linkTarget[targetRoot.Length..];
                }
                // else: a relative target resolves against the PHYSICAL directory reached so far.

                // Splice the target's segments in FRONT of the remaining work, so the directory
                // ancestors inside the target are themselves walked and resolved.
                var spliced = SplitSegments(linkTarget);
                for (var i = spliced.Count - 1; i >= 0; i--)
                    pending.AddFirst(spliced[i]);
            }

            // A link was followed and its destination does not exist: the real location is unknown.
            if (followedLink && !File.Exists(current) && !Directory.Exists(current))
                return null;

            return current;
        }
        catch
        {
            // Any resolution failure (I/O, permissions) is undeterminable — fail closed.
            return null;
        }
    }

    /// <summary>
    /// Splits a path into PHYSICAL components using the PLATFORM's own separators only. On Linux a
    /// backslash is an ordinary filename character, so <c>repo\name</c> stays ONE component; splitting
    /// it on both separators would silently remap the root and the requested path onto a DIFFERENT
    /// directory (<c>repo/name</c>) — moving both sides of the containment check together. The
    /// LEXICAL <c>.git</c> policy (<see cref="NamesGitMetadata"/>) deliberately keeps BOTH separators;
    /// the two splits answer different questions and must not be shared.
    /// </summary>
    private static List<string> SplitSegments(string path) =>
        [.. path.Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries)];

    /// <summary>
    /// Writes full content to <c>agents/{role}.agents.md</c> in the config repo, after validating the role.
    /// </summary>
    [Description("Replace the full content of agents/{role}.agents.md in the config repo, or create the file (and the agents/ folder) when it does not exist. Use edit_agents_md for targeted changes to an existing file.")]
    internal async Task<string> UpdateAgentsMdAsync(
        [Description("Worker role name (e.g. Coder, Tester, Reviewer, Improver, Orchestrator, DocWriter, MergeWorker).")] string role,
        [Description("New full content for the AGENTS.md file.")] string content,
        CancellationToken cancellationToken = default)
    {
        if (await AuthorizationDenialAsync("update_agents_md", cancellationToken) is { } deny_update_agents_md)
            return deny_update_agents_md;

        if (_configRepo is null)
            return "❌ Config repo tools are not available — no config repo configured.";

        if (string.IsNullOrWhiteSpace(role))
            return "❌ role is required.";

        if (!Enum.TryParse<WorkerRole>(role, ignoreCase: true, out var workerRole) || workerRole == WorkerRole.Unspecified)
            return $"❌ Invalid role '{role}'. Valid roles: Coder, Tester, Reviewer, Improver, Orchestrator, DocWriter, MergeWorker.";

        if (content is null)
            return "❌ content is required.";

        var filePath = Path.Combine(_configRepo.LocalPath, "agents", $"{workerRole.ToRoleName()}.agents.md");
        Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
        await File.WriteAllTextAsync(filePath, content, cancellationToken);

        var relPath = Path.GetRelativePath(_configRepo.LocalPath, filePath).Replace('\\', '/');
        _logger.LogInformation("Composer updated config repo file '{FilePath}'", relPath);

        return $"✅ Written {content.Length} characters to '{relPath}'. Use commit_config_changes to persist. "
            + AgentsMdSize.Describe(content.Length);
    }

    /// <summary>
    /// Performs an exact string replacement in <c>agents/{role}.agents.md</c> in the config repo.
    /// </summary>
    [Description("Exact string replacement in an existing agents/{role}.agents.md in the config repo. The file must already exist — use update_agents_md to create a missing file. The old_string must match exactly.")]
    internal async Task<string> EditAgentsMdAsync(
        [Description("Worker role name (e.g. Coder, Tester, Reviewer, Improver, Orchestrator, DocWriter, MergeWorker).")] string role,
        [Description("The exact text to find and replace. Must match the file content exactly.")] string old_string,
        [Description("The replacement text.")] string new_string,
        CancellationToken cancellationToken = default)
    {
        if (await AuthorizationDenialAsync("edit_agents_md", cancellationToken) is { } deny_edit_agents_md)
            return deny_edit_agents_md;

        if (_configRepo is null)
            return "❌ Config repo tools are not available — no config repo configured.";

        if (string.IsNullOrWhiteSpace(role))
            return "❌ role is required.";

        if (!Enum.TryParse<WorkerRole>(role, ignoreCase: true, out var workerRole) || workerRole == WorkerRole.Unspecified)
            return $"❌ Invalid role '{role}'. Valid roles: Coder, Tester, Reviewer, Improver, Orchestrator, DocWriter, MergeWorker.";

        if (string.IsNullOrEmpty(old_string))
            return "❌ old_string is required and must not be empty.";

        new_string ??= "";

        var filePath = Path.Combine(_configRepo.LocalPath, "agents", $"{workerRole.ToRoleName()}.agents.md");
        if (!File.Exists(filePath))
            return $"❌ File 'agents/{workerRole.ToRoleName()}.agents.md' not found in config repo. "
                + "Use update_agents_md to create it.";

        var current = await File.ReadAllTextAsync(filePath, cancellationToken);
        if (!current.Contains(old_string, StringComparison.Ordinal))
            return $"❌ old_string not found in 'agents/{workerRole.ToRoleName()}.agents.md'. Verify the exact text (including whitespace).";

        var updated = current.Replace(old_string, new_string, StringComparison.Ordinal);
        await File.WriteAllTextAsync(filePath, updated, cancellationToken);

        var relPath = $"agents/{workerRole.ToRoleName()}.agents.md";
        _logger.LogInformation("Composer edited config repo file '{FilePath}'", relPath);

        return $"✅ Replacement applied to '{relPath}'. Use commit_config_changes to persist. "
            + AgentsMdSize.Describe(updated.Length);
    }

    /// <summary>
    /// Stages all changes in the config repo, commits with the given message, and pushes to the remote.
    /// </summary>
    [Description("Stage all changes in the config repo, commit with the given message, and push to the remote.")]
    internal async Task<string> CommitConfigChangesAsync(
        [Description("Commit message describing what was changed and why.")] string message,
        CancellationToken cancellationToken = default)
    {
        if (await AuthorizationDenialAsync("commit_config_changes", cancellationToken) is { } deny_commit_config_changes)
            return deny_commit_config_changes;

        if (_configRepo is null)
            return "❌ Config repo tools are not available — no config repo configured.";

        if (string.IsNullOrWhiteSpace(message))
            return "❌ message is required.";

        try
        {
            await _configRepo.CommitAllChangesAsync(message, cancellationToken);
            _logger.LogInformation("Composer committed config repo changes: {Message}", message);
            return $"✅ Config repo changes committed and pushed: \"{message}\"";
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Composer failed to commit config repo changes");
            return $"❌ Failed to commit config repo changes: {ex.Message}";
        }
    }
}
