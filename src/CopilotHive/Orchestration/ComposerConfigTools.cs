using System.ComponentModel;
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

        if (!File.Exists(resolved))
            return $"❌ File '{path}' not found in config repo.";

        var lines = await File.ReadAllLinesAsync(resolved, cancellationToken);
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
    /// Writes full content to <c>agents/{role}.agents.md</c> in the config repo, after validating the role.
    /// </summary>
    [Description("Replace the full content of agents/{role}.agents.md in the config repo. Use edit_agents_md for targeted changes.")]
    internal async Task<string> UpdateAgentsMdAsync(
        [Description("Worker role name (e.g. Coder, Tester, Reviewer, Improver, Orchestrator, DocWriter, MergeWorker).")] string role,
        [Description("New full content for the AGENTS.md file.")] string content,
        CancellationToken cancellationToken = default)
    {
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

        return $"✅ Written {content.Length} characters to '{relPath}'. Use commit_config_changes to persist.";
    }

    /// <summary>
    /// Performs an exact string replacement in <c>agents/{role}.agents.md</c> in the config repo.
    /// </summary>
    [Description("Exact string replacement in agents/{role}.agents.md in the config repo. The old_string must match exactly.")]
    internal async Task<string> EditAgentsMdAsync(
        [Description("Worker role name (e.g. Coder, Tester, Reviewer, Improver, Orchestrator, DocWriter, MergeWorker).")] string role,
        [Description("The exact text to find and replace. Must match the file content exactly.")] string old_string,
        [Description("The replacement text.")] string new_string,
        CancellationToken cancellationToken = default)
    {
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
            return $"❌ File 'agents/{workerRole.ToRoleName()}.agents.md' not found in config repo.";

        var current = await File.ReadAllTextAsync(filePath, cancellationToken);
        if (!current.Contains(old_string, StringComparison.Ordinal))
            return $"❌ old_string not found in 'agents/{workerRole.ToRoleName()}.agents.md'. Verify the exact text (including whitespace).";

        var updated = current.Replace(old_string, new_string, StringComparison.Ordinal);
        await File.WriteAllTextAsync(filePath, updated, cancellationToken);

        var relPath = $"agents/{workerRole.ToRoleName()}.agents.md";
        _logger.LogInformation("Composer edited config repo file '{FilePath}'", relPath);

        return $"✅ Replacement applied to '{relPath}'. Use commit_config_changes to persist.";
    }

    /// <summary>
    /// Stages all changes in the config repo, commits with the given message, and pushes to the remote.
    /// </summary>
    [Description("Stage all changes in the config repo, commit with the given message, and push to the remote.")]
    internal async Task<string> CommitConfigChangesAsync(
        [Description("Commit message describing what was changed and why.")] string message,
        CancellationToken cancellationToken = default)
    {
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
