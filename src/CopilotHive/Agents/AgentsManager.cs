namespace CopilotHive.Agents;

public sealed class AgentsManager
{
    private readonly string _agentsPath;
    private readonly string _historyPath;

    public AgentsManager(string agentsPath)
    {
        _agentsPath = Path.GetFullPath(agentsPath);
        _historyPath = Path.Combine(_agentsPath, "history");
        Directory.CreateDirectory(_historyPath);
    }

    public string GetAgentsMdPath(string role)
    {
        return Path.Combine(_agentsPath, $"{role}.agents.md");
    }

    public string GetAgentsMd(string role)
    {
        var path = GetAgentsMdPath(role);
        return File.Exists(path) ? File.ReadAllText(path) : string.Empty;
    }

    public void UpdateAgentsMd(string role, string newContent)
    {
        // Archive current version before overwriting
        var currentContent = GetAgentsMd(role);
        if (!string.IsNullOrEmpty(currentContent))
        {
            ArchiveVersion(role, currentContent);
        }

        File.WriteAllText(GetAgentsMdPath(role), newContent);
        Console.WriteLine($"[Agents] Updated AGENTS.md for {role}");
    }

    public bool RollbackAgentsMd(string role)
    {
        var versions = GetVersionFiles(role);
        if (versions.Length == 0)
            return false;

        var latest = versions[^1];
        var content = File.ReadAllText(latest);
        File.WriteAllText(GetAgentsMdPath(role), content);
        File.Delete(latest);

        Console.WriteLine($"[Agents] Rolled back {role} to {Path.GetFileName(latest)}");
        return true;
    }

    public string[] GetHistory(string role)
    {
        return GetVersionFiles(role)
            .Select(Path.GetFileName)
            .Where(f => f is not null)
            .ToArray()!;
    }

    private void ArchiveVersion(string role, string content)
    {
        var roleHistoryPath = Path.Combine(_historyPath, role);
        Directory.CreateDirectory(roleHistoryPath);

        var version = GetVersionFiles(role).Length + 1;
        var versionFile = Path.Combine(roleHistoryPath, $"v{version:D3}.agents.md");
        File.WriteAllText(versionFile, content);
    }

    private string[] GetVersionFiles(string role)
    {
        var roleHistoryPath = Path.Combine(_historyPath, role);
        if (!Directory.Exists(roleHistoryPath))
            return [];

        return Directory.GetFiles(roleHistoryPath, "v*.agents.md")
            .OrderBy(f => f)
            .ToArray();
    }
}
