using CopilotHive.Configuration;

namespace CopilotHive.Tests;

public class ConfigRepoManagerTests : IDisposable
{
    private readonly string _tempDir;

    public ConfigRepoManagerTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"copilothive-cfgtest-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    // ── YAML parsing ─────────────────────────────────────────────────────────

    [Fact]
    public void ParseConfig_FullConfig_ParsesAllFields()
    {
        const string yaml = """
            version: "2.0"
            repositories:
              - name: my-app
                url: https://github.com/org/my-app.git
                default_branch: develop
              - name: my-api
                url: https://github.com/org/my-api.git
            workers:
              coder:
                model: claude-opus-4.6
              tester:
                model: gpt-5-mini
            orchestrator:
              model: gpt-5.4
              max_iterations: 5
              max_retries_per_task: 2
              always_improve: true
            """;

        var config = ConfigRepoManager.ParseConfig(yaml);

        Assert.Equal("2.0", config.Version);
        Assert.Equal(2, config.Repositories.Count);
        Assert.Equal("my-app", config.Repositories[0].Name);
        Assert.Equal("https://github.com/org/my-app.git", config.Repositories[0].Url);
        Assert.Equal("develop", config.Repositories[0].DefaultBranch);
        Assert.Equal("my-api", config.Repositories[1].Name);
        Assert.Equal("main", config.Repositories[1].DefaultBranch); // default

        Assert.Equal(2, config.Workers.Count);
        Assert.Equal("claude-opus-4.6", config.Workers["coder"].Model);
        Assert.Equal("gpt-5-mini", config.Workers["tester"].Model);

        Assert.Equal("gpt-5.4", config.Orchestrator.Model);
        Assert.Equal(5, config.Orchestrator.MaxIterations);
        Assert.Equal(2, config.Orchestrator.MaxRetriesPerTask);
        Assert.True(config.Orchestrator.AlwaysImprove);
    }

    [Fact]
    public void ParseConfig_MinimalConfig_UsesDefaults()
    {
        const string yaml = """
            version: "1.0"
            """;

        var config = ConfigRepoManager.ParseConfig(yaml);

        Assert.Equal("1.0", config.Version);
        Assert.Empty(config.Repositories);
        Assert.Empty(config.Workers);
        Assert.Equal("claude-sonnet-4.6", config.Orchestrator.Model);
        Assert.Equal(10, config.Orchestrator.MaxIterations);
        Assert.Equal(3, config.Orchestrator.MaxRetriesPerTask);
        Assert.False(config.Orchestrator.AlwaysImprove);
    }

    [Fact]
    public void ParseConfig_EmptyYaml_ReturnsDefaults()
    {
        var config = ConfigRepoManager.ParseConfig("");

        Assert.Equal("1.0", config.Version);
        Assert.Empty(config.Repositories);
        Assert.Empty(config.Workers);
    }

    [Fact]
    public void ParseConfig_MissingFields_FallsBackToDefaults()
    {
        const string yaml = """
            repositories:
              - name: only-repo
                url: https://github.com/org/only-repo.git
            orchestrator:
              max_iterations: 20
            """;

        var config = ConfigRepoManager.ParseConfig(yaml);

        Assert.Single(config.Repositories);
        Assert.Equal("main", config.Repositories[0].DefaultBranch);
        Assert.Equal("claude-sonnet-4.6", config.Orchestrator.Model);
        Assert.Equal(20, config.Orchestrator.MaxIterations);
        Assert.Equal(3, config.Orchestrator.MaxRetriesPerTask);
    }

    [Fact]
    public void ParseConfig_UnknownFields_AreIgnored()
    {
        const string yaml = """
            version: "1.0"
            some_future_field: true
            orchestrator:
              model: gpt-5.4
              unknown_setting: 42
            """;

        var config = ConfigRepoManager.ParseConfig(yaml);

        Assert.Equal("gpt-5.4", config.Orchestrator.Model);
    }

    // ── IsRepositoryAllowed ──────────────────────────────────────────────────

    [Fact]
    public async Task IsRepositoryAllowed_AllowedUrl_ReturnsTrue()
    {
        var manager = await CreateManagerWithConfigAsync("""
            repositories:
              - name: my-app
                url: https://github.com/org/my-app.git
            """);

        Assert.True(manager.IsRepositoryAllowed("https://github.com/org/my-app.git"));
    }

    [Fact]
    public async Task IsRepositoryAllowed_DisallowedUrl_ReturnsFalse()
    {
        var manager = await CreateManagerWithConfigAsync("""
            repositories:
              - name: my-app
                url: https://github.com/org/my-app.git
            """);

        Assert.False(manager.IsRepositoryAllowed("https://github.com/other/repo.git"));
    }

    [Fact]
    public async Task IsRepositoryAllowed_TrailingSlashVariation_Matches()
    {
        var manager = await CreateManagerWithConfigAsync("""
            repositories:
              - name: my-app
                url: https://github.com/org/my-app.git
            """);

        Assert.True(manager.IsRepositoryAllowed("https://github.com/org/my-app.git/"));
    }

    [Fact]
    public async Task IsRepositoryAllowed_CaseInsensitive_Matches()
    {
        var manager = await CreateManagerWithConfigAsync("""
            repositories:
              - name: my-app
                url: https://github.com/Org/My-App.git
            """);

        Assert.True(manager.IsRepositoryAllowed("https://github.com/org/my-app.git"));
    }

    [Fact]
    public async Task IsRepositoryAllowed_WhitespaceVariation_Matches()
    {
        var manager = await CreateManagerWithConfigAsync("""
            repositories:
              - name: my-app
                url: https://github.com/org/my-app.git
            """);

        Assert.True(manager.IsRepositoryAllowed("  https://github.com/org/my-app.git  "));
    }

    [Fact]
    public void IsRepositoryAllowed_NoConfigLoaded_ReturnsFalse()
    {
        var manager = new ConfigRepoManager("https://example.com/config.git", _tempDir);

        Assert.False(manager.IsRepositoryAllowed("https://github.com/org/my-app.git"));
    }

    // ── AGENTS.md loading ────────────────────────────────────────────────────

    [Fact]
    public async Task LoadAgentsMdAsync_ExistingRole_ReturnsContent()
    {
        var agentsDir = Path.Combine(_tempDir, "agents");
        Directory.CreateDirectory(agentsDir);
        await File.WriteAllTextAsync(
            Path.Combine(agentsDir, "coder.agents.md"),
            "# Coder\nYou write great code.");

        var manager = new ConfigRepoManager("https://example.com/config.git", _tempDir);

        var content = await manager.LoadAgentsMdAsync("coder");

        Assert.NotNull(content);
        Assert.Contains("You write great code.", content);
    }

    [Fact]
    public async Task LoadAgentsMdAsync_NonexistentRole_ReturnsNull()
    {
        var manager = new ConfigRepoManager("https://example.com/config.git", _tempDir);

        var content = await manager.LoadAgentsMdAsync("nonexistent");

        Assert.Null(content);
    }

    [Fact]
    public async Task LoadAgentsMdAsync_CaseInsensitiveRole()
    {
        var agentsDir = Path.Combine(_tempDir, "agents");
        Directory.CreateDirectory(agentsDir);
        await File.WriteAllTextAsync(
            Path.Combine(agentsDir, "tester.agents.md"),
            "# Tester instructions");

        var manager = new ConfigRepoManager("https://example.com/config.git", _tempDir);

        var content = await manager.LoadAgentsMdAsync("TESTER");

        Assert.NotNull(content);
        Assert.Contains("Tester instructions", content);
    }

    // ── LoadConfigAsync from file ────────────────────────────────────────────

    [Fact]
    public async Task LoadConfigAsync_ReadsFromDisk()
    {
        await File.WriteAllTextAsync(
            Path.Combine(_tempDir, "hive-config.yaml"),
            """
            version: "1.0"
            repositories:
              - name: test-repo
                url: https://github.com/test/repo.git
            """);

        var manager = new ConfigRepoManager("https://example.com/config.git", _tempDir);
        var config = await manager.LoadConfigAsync();

        Assert.Equal("1.0", config.Version);
        Assert.Single(config.Repositories);
        Assert.Equal("test-repo", config.Repositories[0].Name);
    }

    [Fact]
    public async Task LoadConfigAsync_MissingFile_ThrowsFileNotFound()
    {
        var manager = new ConfigRepoManager("https://example.com/config.git", _tempDir);

        await Assert.ThrowsAsync<FileNotFoundException>(() => manager.LoadConfigAsync());
    }

    [Fact]
    public async Task LoadConfigAsync_CachesResult()
    {
        await File.WriteAllTextAsync(
            Path.Combine(_tempDir, "hive-config.yaml"),
            """
            version: "1.0"
            repositories:
              - name: cached-repo
                url: https://github.com/test/cached.git
            """);

        var manager = new ConfigRepoManager("https://example.com/config.git", _tempDir);
        var first = await manager.LoadConfigAsync();
        var second = await manager.LoadConfigAsync();

        Assert.Same(first, second);
    }

    // ── Helper ───────────────────────────────────────────────────────────────

    private async Task<ConfigRepoManager> CreateManagerWithConfigAsync(string yaml)
    {
        await File.WriteAllTextAsync(Path.Combine(_tempDir, "hive-config.yaml"), yaml);
        var manager = new ConfigRepoManager("https://example.com/config.git", _tempDir);
        await manager.LoadConfigAsync();
        return manager;
    }
}
