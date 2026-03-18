using CopilotHive.Workers;

namespace CopilotHive.Agents;

/// <summary>
/// Manages per-role AGENTS.md files on the local filesystem, including versioned history and rollback.
/// </summary>
public sealed class AgentsManager
{
    private readonly string _agentsPath;
    private readonly string _historyPath;

    /// <summary>
    /// Initialises a new <see cref="AgentsManager"/> rooted at the given path.
    /// Creates the history directory if it does not already exist.
    /// </summary>
    /// <param name="agentsPath">Root directory where AGENTS.md files are stored.</param>
    public AgentsManager(string agentsPath)
    {
        _agentsPath = Path.GetFullPath(agentsPath);
        _historyPath = Path.Combine(_agentsPath, "history");
        Directory.CreateDirectory(_historyPath);
    }

    /// <summary>
    /// Returns the full path to the AGENTS.md file for the given role.
    /// </summary>
    /// <param name="role">Worker role.</param>
    /// <returns>Absolute file path for the role's AGENTS.md.</returns>
    public string GetAgentsMdPath(WorkerRole role)
    {
        return Path.Combine(_agentsPath, $"{role.ToRoleName()}.agents.md");
    }

    /// <summary>
    /// Reads and returns the AGENTS.md content for the specified role.
    /// Returns an empty string when the file does not exist.
    /// </summary>
    /// <param name="role">Worker role.</param>
    /// <returns>File contents, or empty string if not found.</returns>
    public string GetAgentsMd(WorkerRole role)
    {
        var path = GetAgentsMdPath(role);
        return File.Exists(path) ? File.ReadAllText(path) : string.Empty;
    }

    /// <summary>
    /// Archives the current AGENTS.md and overwrites it with the new content.
    /// </summary>
    /// <param name="role">Worker role.</param>
    /// <param name="newContent">New AGENTS.md content to write.</param>
    public void UpdateAgentsMd(WorkerRole role, string newContent)
    {
        var currentContent = GetAgentsMd(role);
        if (!string.IsNullOrEmpty(currentContent))
        {
            ArchiveVersion(role, currentContent);
        }

        File.WriteAllText(GetAgentsMdPath(role), newContent);
        Console.WriteLine($"[Agents] Updated AGENTS.md for {role.ToRoleName()}");
    }

    /// <summary>
    /// Rolls back the AGENTS.md for the given role to the most recent archived version.
    /// </summary>
    /// <param name="role">Worker role.</param>
    /// <returns><c>true</c> if a rollback was performed; <c>false</c> if no history exists.</returns>
    public bool RollbackAgentsMd(WorkerRole role)
    {
        var versions = GetVersionFiles(role);
        if (versions.Length == 0)
            return false;

        var latest = versions[^1];
        var content = File.ReadAllText(latest);
        File.WriteAllText(GetAgentsMdPath(role), content);
        File.Delete(latest);

        Console.WriteLine($"[Agents] Rolled back {role.ToRoleName()} to {Path.GetFileName(latest)}");
        return true;
    }

    /// <summary>
    /// Returns the filenames of all archived versions for the given role, in chronological order.
    /// </summary>
    /// <param name="role">Worker role.</param>
    /// <returns>Array of version filenames (e.g. <c>v001.agents.md</c>).</returns>
    public string[] GetHistory(WorkerRole role)
    {
        return GetVersionFiles(role)
            .Select(Path.GetFileName)
            .Where(f => f is not null)
            .ToArray()!;
    }

    private void ArchiveVersion(WorkerRole role, string content)
    {
        var roleName = role.ToRoleName();
        var roleHistoryPath = Path.Combine(_historyPath, roleName);
        Directory.CreateDirectory(roleHistoryPath);

        var version = GetVersionFiles(role).Length + 1;
        var versionFile = Path.Combine(roleHistoryPath, $"v{version:D3}.agents.md");
        File.WriteAllText(versionFile, content);
    }

    private string[] GetVersionFiles(WorkerRole role)
    {
        var roleHistoryPath = Path.Combine(_historyPath, role.ToRoleName());
        if (!Directory.Exists(roleHistoryPath))
            return [];

        return Directory.GetFiles(roleHistoryPath, "v*.agents.md")
            .OrderBy(f => f)
            .ToArray();
    }
}
