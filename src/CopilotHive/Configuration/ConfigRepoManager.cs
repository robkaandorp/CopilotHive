using System.Diagnostics;
using CopilotHive.Workers;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace CopilotHive.Configuration;

/// <summary>
/// Manages the configuration repository: clones/pulls the repo, reads hive-config.yaml
/// and per-role AGENTS.md files, and commits AGENTS.md updates back.
/// </summary>
public sealed class ConfigRepoManager
{
    private readonly string _configRepoUrl;
    private readonly string _localPath;
    private HiveConfigFile? _cachedConfig;

    private static readonly IDeserializer YamlDeserializer = new DeserializerBuilder()
        .WithNamingConvention(UnderscoredNamingConvention.Instance)
        .IgnoreUnmatchedProperties()
        .Build();

    /// <summary>
    /// Initialises a new <see cref="ConfigRepoManager"/>.
    /// </summary>
    /// <param name="configRepoUrl">URL of the remote configuration repository.</param>
    /// <param name="localPath">Local path where the config repo will be cloned.</param>
    public ConfigRepoManager(string configRepoUrl, string localPath)
    {
        _configRepoUrl = configRepoUrl;
        _localPath = Path.GetFullPath(localPath);
    }

    /// <summary>
    /// The local filesystem path where the config repo is cloned.
    /// </summary>
    public string LocalPath => _localPath;

    /// <summary>
    /// Clones the config repo, or pulls latest if already cloned.
    /// </summary>
    public async Task SyncRepoAsync(CancellationToken ct = default)
    {
        if (Directory.Exists(Path.Combine(_localPath, ".git")))
        {
            // Discard any uncommitted changes (e.g. from a failed improver commit)
            // before pulling to prevent merge conflicts with incoming changes.
            await RunGitAsync(_localPath, ["checkout", "--", "."], ct);
            await RunGitAsync(_localPath, ["pull", "--ff-only"], ct);
        }
        else
        {
            var parent = Path.GetDirectoryName(_localPath)!;
            Directory.CreateDirectory(parent);
            var dirName = Path.GetFileName(_localPath);
            var cloneUrl = InjectTokenIntoUrl(_configRepoUrl);
            await RunGitAsync(parent, ["clone", cloneUrl, dirName], ct);
            await RunGitAsync(_localPath, ["config", "user.email", "copilothive@local"], ct);
            await RunGitAsync(_localPath, ["config", "user.name", "CopilotHive"], ct);
        }

        _cachedConfig = null;
    }

    /// <summary>
    /// If GH_TOKEN is set and the URL is HTTPS GitHub, inject the token for auth.
    /// </summary>
    private static string InjectTokenIntoUrl(string url)
    {
        var token = Environment.GetEnvironmentVariable("GH_TOKEN")
                 ?? Environment.GetEnvironmentVariable("GITHUB_TOKEN");
        if (string.IsNullOrEmpty(token))
            return url;

        if (url.StartsWith("https://github.com/", StringComparison.OrdinalIgnoreCase))
            return url.Replace("https://github.com/", $"https://x-access-token:{token}@github.com/");

        return url;
    }

    /// <summary>
    /// Loads and parses hive-config.yaml from the config repo root.
    /// </summary>
    public async Task<HiveConfigFile> LoadConfigAsync(CancellationToken ct = default)
    {
        if (_cachedConfig is not null)
            return _cachedConfig;

        var configPath = Path.Combine(_localPath, "hive-config.yaml");
        if (!File.Exists(configPath))
            throw new FileNotFoundException("Config file not found in config repo.", configPath);

        var yaml = await File.ReadAllTextAsync(configPath, ct);
        _cachedConfig = ParseConfig(yaml);
        return _cachedConfig;
    }

    /// <summary>
    /// Parses a YAML string into a <see cref="HiveConfigFile"/>.
    /// </summary>
    internal static HiveConfigFile ParseConfig(string yaml)
    {
        return YamlDeserializer.Deserialize<HiveConfigFile>(yaml) ?? new HiveConfigFile();
    }

    /// <summary>
    /// Loads a per-role AGENTS.md file from the config repo (agents/{role}.agents.md).
    /// Returns null if the file does not exist.
    /// </summary>
    public async Task<string?> LoadAgentsMdAsync(WorkerRole role, CancellationToken ct = default)
    {
        var agentsPath = Path.Combine(_localPath, "agents", $"{role.ToRoleName()}.agents.md");
        if (!File.Exists(agentsPath))
            return null;

        return await File.ReadAllTextAsync(agentsPath, ct);
    }

    /// <summary>
    /// Checks whether a repository URL is in the allowed list.
    /// Compares normalized URLs (trimmed, case-insensitive, trailing-slash-insensitive).
    /// </summary>
    public bool IsRepositoryAllowed(string repoUrl)
    {
        if (_cachedConfig is null)
            return false;

        var normalized = NormalizeUrl(repoUrl);
        return _cachedConfig.Repositories.Exists(r => NormalizeUrl(r.Url) == normalized);
    }

    /// <summary>
    /// Commits and pushes a single file that has already been written to disk.
    /// Used to persist goals.yaml status updates back to the config repo.
    /// </summary>
    public async Task CommitFileAsync(string filePath, string commitMessage, CancellationToken ct = default)
    {
        await RunGitAsync(_localPath, ["add", filePath], ct);
        await RunGitAsync(_localPath, ["commit", "-m", commitMessage], ct);
        await RunGitAsync(_localPath, ["pull", "--no-rebase"], ct);
        await RunGitAsync(_localPath, ["push"], ct);
    }

    private static string NormalizeUrl(string url)
    {
        return url.Trim().TrimEnd('/').ToLowerInvariant();
    }

    private static async Task<string> RunGitAsync(string workingDir, string[] args, CancellationToken ct)
    {
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

        var stdout = stdoutTask.Result;
        var stderr = stderrTask.Result;

        if (process.ExitCode != 0)
            throw new InvalidOperationException(
                $"git exited with code {process.ExitCode}: {stdout}\n{stderr}".Trim());

        return stdout.Trim();
    }
}
