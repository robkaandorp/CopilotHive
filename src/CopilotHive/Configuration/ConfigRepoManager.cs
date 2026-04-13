using System.Diagnostics;
using CopilotHive.Workers;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace CopilotHive.Configuration;

/// <summary>
/// Manages the configuration repository: clones/pulls the repo, reads hive-config.yaml
/// and per-role AGENTS.md files, and commits AGENTS.md updates back.
/// </summary>
public class ConfigRepoManager
{
    private readonly string _configRepoUrl;
    private readonly string _localPath;
    private HiveConfigFile? _cachedConfig;
    private readonly SemaphoreSlim _gitLock = new(1, 1);

    /// <summary>
    /// Attempts to run <c>git merge --abort</c> on a best-effort basis.
    /// Any failure is silently ignored so the original exception can propagate.
    /// </summary>
    private static async Task TryAbortMergeAsync(string localPath, CancellationToken ct)
    {
        try
        {
            await RunGitAsync(localPath, ["merge", "--abort"], ct);
        }
        catch
        {
            // Best-effort: ignore failures (e.g., no merge in progress).
        }
    }

    private static readonly IDeserializer YamlDeserializer = new DeserializerBuilder()
        .WithNamingConvention(UnderscoredNamingConvention.Instance)
        .IgnoreUnmatchedProperties()
        .Build();

    private static readonly ISerializer YamlSerializer = new SerializerBuilder()
        .WithNamingConvention(UnderscoredNamingConvention.Instance)
        .ConfigureDefaultValuesHandling(DefaultValuesHandling.OmitDefaults | DefaultValuesHandling.OmitNull)
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
        await _gitLock.WaitAsync(ct);
        try
        {
            if (Directory.Exists(Path.Combine(_localPath, ".git")))
            {
                try
                {
                    await RunGitAsync(_localPath, ["pull"], ct);
                }
                catch
                {
                    await TryAbortMergeAsync(_localPath, ct);
                    throw;
                }
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
        finally
        {
            _gitLock.Release();
        }
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
    /// Serializes <paramref name="config"/> to YAML and writes it to <c>hive-config.yaml</c>
    /// in the local config repo path, then updates the in-memory cache.
    /// Call <see cref="CommitFileAsync"/> afterward to commit and push the change.
    /// </summary>
    /// <param name="config">The updated configuration to persist.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task WriteConfigAsync(HiveConfigFile config, CancellationToken ct = default)
    {
        var yaml = YamlSerializer.Serialize(config);
        var configPath = Path.Combine(_localPath, "hive-config.yaml");
        await File.WriteAllTextAsync(configPath, yaml, ct);
        _cachedConfig = config;
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
    public virtual async Task CommitFileAsync(string filePath, string commitMessage, CancellationToken ct = default)
    {
        await _gitLock.WaitAsync(ct);
        try
        {
            await RunGitAsync(_localPath, ["add", filePath], ct);
            await RunGitAsync(_localPath, ["commit", "-m", commitMessage], ct);
            try
            {
                await RunGitAsync(_localPath, ["pull"], ct);
            }
            catch
            {
                await TryAbortMergeAsync(_localPath, ct);
                throw;
            }
            await RunGitAsync(_localPath, ["push"], ct);
        }
        finally
        {
            _gitLock.Release();
        }
    }

    /// <summary>
    /// Stages a file deletion and commits/pushes the removal.
    /// The file should already be deleted from the working tree before calling this method.
    /// Uses <c>git rm --cached</c> to stage the removal from the index without touching
    /// the local file (which the caller has already removed).
    /// </summary>
    public virtual async Task DeleteFileAsync(string filePath, string commitMessage, CancellationToken ct = default)
    {
        await _gitLock.WaitAsync(ct);
        try
        {
            await RunGitAsync(_localPath, ["rm", "--cached", filePath], ct);
            await RunGitAsync(_localPath, ["commit", "-m", commitMessage], ct);
            try
            {
                await RunGitAsync(_localPath, ["pull"], ct);
            }
            catch
            {
                await TryAbortMergeAsync(_localPath, ct);
                throw;
            }
            await RunGitAsync(_localPath, ["push"], ct);
        }
        finally
        {
            _gitLock.Release();
        }
    }

    /// <summary>
    /// Stages all changes, commits with the given message, and pushes to the remote.
    /// Used by the Composer to persist AGENTS.md updates made via config repo tools.
    /// </summary>
    /// <param name="commitMessage">Commit message to use.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task CommitAllChangesAsync(string commitMessage, CancellationToken ct = default)
    {
        await _gitLock.WaitAsync(ct);
        try
        {
            await RunGitAsync(_localPath, ["add", "--all"], ct);
            await RunGitAsync(_localPath, ["commit", "-m", commitMessage], ct);
            try
            {
                await RunGitAsync(_localPath, ["pull"], ct);
            }
            catch
            {
                await TryAbortMergeAsync(_localPath, ct);
                throw;
            }
            await RunGitAsync(_localPath, ["push"], ct);
        }
        finally
        {
            _gitLock.Release();
        }
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
